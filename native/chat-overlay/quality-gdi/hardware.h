/* Display-sized DXVA2 readback for Studio GDI. LGPL-2.1-or-later.
 * GPU decoding keeps its full source resolution. Convert/scale the decoded
 * surface once, then download only the RGB pixels that the window displays.
 * Presentation and separately filtered RGBA chat remain in the GDI canvas.
 */
#ifndef STUDIO_GDI_HARDWARE_H
#define STUDIO_GDI_HARDWARE_H
#include <vlc_picture_pool.h>
#include "dxva2-picture.h"

typedef struct {
    IDirect3D9Ex *api;
    IDirect3DDevice9Ex *device;
    IUnknown *device_identity;
    IDirect3DSurface9 *device_surface, *target, *download;
    IDirectXVideoProcessorService *service;
    IDirectXVideoProcessor *processor;
    DXVA2_VideoDesc description;
    DXVA2_ProcAmpValues adjustment;
    picture_pool_t *pool;
    filter_chain_t *fallback;
    picture_t *fallback_picture;
    int width, height;
    bool failed, fallback_failed;
} studio_hardware_t;

static void HardwareClearTargets(studio_hardware_t *gpu)
{
    if (gpu->target) IDirect3DSurface9_Release(gpu->target);
    if (gpu->download) IDirect3DSurface9_Release(gpu->download);
    gpu->target = gpu->download = NULL;
    gpu->width = gpu->height = 0;
}

static void HardwareClose(studio_hardware_t *gpu)
{
    if (gpu->fallback) filter_chain_Delete(gpu->fallback);
    if (gpu->fallback_picture) picture_Release(gpu->fallback_picture);
    if (gpu->pool) picture_pool_Release(gpu->pool);
    HardwareClearTargets(gpu);
    if (gpu->processor) IDirectXVideoProcessor_Release(gpu->processor);
    if (gpu->service) IDirectXVideoProcessorService_Release(gpu->service);
    if (gpu->device_surface) IDirect3DSurface9_Release(gpu->device_surface);
    if (gpu->device_identity) gpu->device_identity->lpVtbl->Release(gpu->device_identity);
    if (gpu->device) IDirect3DDevice9Ex_Release(gpu->device);
    if (gpu->api) IDirect3D9Ex_Release(gpu->api);
    memset(gpu, 0, sizeof(*gpu));
}

static bool HardwareSupportsFormat(const video_format_t *format)
{
    /* DXVA2's legacy processor cannot represent BT.2020/HDR. Keep those,
     * ten-bit surfaces and rotated sources on VLC's existing converter path. */
    return format->i_chroma == VLC_CODEC_D3D9_OPAQUE &&
        format->orientation == ORIENT_NORMAL &&
        format->i_width && format->i_height &&
        format->i_width <= 16384 && format->i_height <= 16384 &&
        (format->space == COLOR_SPACE_UNDEF || format->space == COLOR_SPACE_BT601 ||
         format->space == COLOR_SPACE_BT709) &&
        (format->primaries == COLOR_PRIMARIES_UNDEF || format->primaries == COLOR_PRIMARIES_BT709 ||
         format->primaries == COLOR_PRIMARIES_BT601_525 || format->primaries == COLOR_PRIMARIES_BT601_625) &&
        (format->transfer == TRANSFER_FUNC_UNDEF || format->transfer == TRANSFER_FUNC_BT709) &&
        (format->chroma_location == CHROMA_LOCATION_UNDEF || format->chroma_location == CHROMA_LOCATION_LEFT ||
         format->chroma_location == CHROMA_LOCATION_CENTER);
}

static DXVA2_ExtendedFormat HardwareColorFormat(const video_format_t *format)
{
    DXVA2_ExtendedFormat color = {0};
    color.SampleFormat = DXVA2_SampleProgressiveFrame;
    color.VideoChromaSubsampling = DXVA2_VideoChromaSubsampling_ProgressiveChroma |
        (format->chroma_location == CHROMA_LOCATION_CENTER ?
         DXVA2_VideoChromaSubsampling_MPEG1 : DXVA2_VideoChromaSubsampling_MPEG2);
    color.NominalRange = format->b_color_range_full ? DXVA2_NominalRange_0_255 : DXVA2_NominalRange_16_235;
    const bool hd = format->space == COLOR_SPACE_BT709 ||
        (format->space == COLOR_SPACE_UNDEF && format->i_visible_height > 576);
    color.VideoTransferMatrix = hd ? DXVA2_VideoTransferMatrix_BT709 : DXVA2_VideoTransferMatrix_BT601;
    color.VideoLighting = DXVA2_VideoLighting_dim;
    switch (format->primaries) {
    case COLOR_PRIMARIES_BT601_525: color.VideoPrimaries = DXVA2_VideoPrimaries_SMPTE170M; break;
    case COLOR_PRIMARIES_BT601_625: color.VideoPrimaries = DXVA2_VideoPrimaries_BT470_2_SysBG; break;
    case COLOR_PRIMARIES_BT709: color.VideoPrimaries = DXVA2_VideoPrimaries_BT709; break;
    default:
        color.VideoPrimaries = hd ? DXVA2_VideoPrimaries_BT709 :
            format->i_visible_height > 525 ? DXVA2_VideoPrimaries_BT470_2_SysBG : DXVA2_VideoPrimaries_SMPTE170M;
        break;
    }
    color.VideoTransferFunction = DXVA2_VideoTransFunc_709;
    return color;
}

static HRESULT HardwareEnsureTargets(studio_hardware_t *gpu, int width, int height)
{
    if (gpu->target && gpu->download && gpu->width == width && gpu->height == height) return S_OK;
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return E_INVALIDARG;
    HardwareClearTargets(gpu);
    HRESULT hr = IDirect3DDevice9Ex_CreateRenderTarget(gpu->device, width, height,
        D3DFMT_X8R8G8B8, D3DMULTISAMPLE_NONE, 0, FALSE, &gpu->target, NULL);
    if (SUCCEEDED(hr))
        hr = IDirect3DDevice9Ex_CreateOffscreenPlainSurface(gpu->device, width, height,
            D3DFMT_X8R8G8B8, D3DPOOL_SYSTEMMEM, &gpu->download, NULL);
    if (FAILED(hr)) {
        HardwareClearTargets(gpu);
        return hr;
    }
    gpu->width = width;
    gpu->height = height;
    return S_OK;
}

static bool HardwareOpen(vout_display_t *vd, studio_hardware_t *gpu, HWND window)
{
    if (!var_InheritBool(vd, "studio-gdi-gpu-scaling") || !HardwareSupportsFormat(&vd->source))
        return false;
    HRESULT hr = Direct3DCreate9Ex(D3D_SDK_VERSION, &gpu->api);
    if (FAILED(hr)) goto failed;
    D3DPRESENT_PARAMETERS pp = {0};
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.BackBufferWidth = pp.BackBufferHeight = 1;
    pp.BackBufferCount = 1;
    pp.hDeviceWindow = window;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    hr = IDirect3D9Ex_CreateDeviceEx(gpu->api, D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL,
        window, D3DCREATE_MULTITHREADED | D3DCREATE_HARDWARE_VERTEXPROCESSING |
        D3DCREATE_FPU_PRESERVE, &pp, NULL, &gpu->device);
    if (FAILED(hr)) goto failed;
    hr = IDirect3DDevice9Ex_QueryInterface(gpu->device, &IID_IUnknown, (void **)&gpu->device_identity);
    if (FAILED(hr)) goto failed;
    /* This tiny surface communicates the device/format to VLC's DXVA2 decoder.
     * It is never a decoded frame. The decoder allocates and owns its full-size
     * reference surfaces; Display reads the active picture context instead. */
    hr = IDirect3DDevice9Ex_CreateOffscreenPlainSurface(gpu->device, 16, 16,
        MAKEFOURCC('N','V','1','2'), D3DPOOL_DEFAULT, &gpu->device_surface, NULL);
    if (FAILED(hr)) goto failed;
    hr = DXVA2CreateVideoService((IDirect3DDevice9 *)gpu->device,
        &IID_IDirectXVideoProcessorService, (void **)&gpu->service);
    if (FAILED(hr)) goto failed;
    DXVA2_VideoDesc *desc = &gpu->description;
    desc->SampleWidth = vd->source.i_width;
    desc->SampleHeight = vd->source.i_height;
    desc->Format = MAKEFOURCC('N','V','1','2');
    desc->InputSampleFreq.Numerator = vd->source.i_frame_rate ? vd->source.i_frame_rate : 60;
    desc->InputSampleFreq.Denominator = vd->source.i_frame_rate_base ? vd->source.i_frame_rate_base : 1;
    desc->OutputFrameFreq = desc->InputSampleFreq;
    desc->SampleFormat = HardwareColorFormat(&vd->source);
    DXVA2_VideoProcessorCaps caps = {0};
    hr = IDirectXVideoProcessorService_GetVideoProcessorCaps(gpu->service,
        &DXVA2_VideoProcProgressiveDevice, desc, D3DFMT_X8R8G8B8, &caps);
    if (FAILED(hr)) goto failed;
    const UINT operations = DXVA2_VideoProcess_YUV2RGB | DXVA2_VideoProcess_StretchX | DXVA2_VideoProcess_StretchY;
    if (caps.NumForwardRefSamples || caps.NumBackwardRefSamples ||
        (caps.VideoProcessorOperations & operations) != operations) {
        hr = E_NOTIMPL;
        goto failed;
    }
    hr = IDirectXVideoProcessorService_CreateVideoProcessor(gpu->service,
        &DXVA2_VideoProcProgressiveDevice, desc, D3DFMT_X8R8G8B8, 0, &gpu->processor);
    if (FAILED(hr)) goto failed;
    const UINT properties[] = {DXVA2_ProcAmp_Brightness, DXVA2_ProcAmp_Contrast,
                               DXVA2_ProcAmp_Hue, DXVA2_ProcAmp_Saturation};
    DXVA2_Fixed32 *values[] = {&gpu->adjustment.Brightness, &gpu->adjustment.Contrast,
                              &gpu->adjustment.Hue, &gpu->adjustment.Saturation};
    for (unsigned i = 0; i < ARRAY_SIZE(properties); ++i) {
        DXVA2_ValueRange range;
        hr = IDirectXVideoProcessor_GetProcAmpRange(gpu->processor, properties[i], &range);
        if (FAILED(hr)) goto failed;
        *values[i] = range.DefaultValue;
    }
    /* Verify readback allocation while VLC can still negotiate its normal RGB
     * output. Resizing later changes only these two display-sized surfaces. */
    hr = HardwareEnsureTargets(gpu, 16, 16);
    if (FAILED(hr)) goto failed;
    msg_Dbg(vd, "using display-sized DXVA2 video readback");
    return true;
failed:
    msg_Dbg(vd, "display-sized DXVA2 readback unavailable: 0x%08lx", (unsigned long)hr);
    HardwareClose(gpu);
    return false;
}

static void HardwarePictureDestroy(picture_t *picture)
{
    IDirect3DSurface9_Release(picture->p_sys->surface);
    free(picture->p_sys);
    free(picture);
}

static picture_pool_t *HardwarePool(studio_hardware_t *gpu, const video_format_t *format, unsigned count)
{
    if (gpu->pool) return gpu->pool;
    if (!count || count > 128) return NULL;
    picture_t **pictures = calloc(count, sizeof(*pictures));
    if (!pictures) return NULL;
    unsigned made = 0;
    for (; made < count; ++made) {
        picture_sys_t *system = calloc(1, sizeof(*system));
        if (!system) break;
        system->surface = gpu->device_surface;
        IDirect3DSurface9_AddRef(system->surface);
        picture_resource_t resource = { .p_sys = system, .pf_destroy = HardwarePictureDestroy };
        pictures[made] = picture_NewFromResource(format, &resource);
        if (!pictures[made]) { IDirect3DSurface9_Release(system->surface); free(system); break; }
    }
    if (made == count) gpu->pool = picture_pool_New(count, pictures);
    if (!gpu->pool) for (unsigned i = 0; i < made; ++i) picture_Release(pictures[i]);
    free(pictures);
    return gpu->pool;
}

static bool HardwareDraw(vout_display_t *vd, studio_hardware_t *gpu,
                         picture_t *picture, const RECT *source,
                         studio_gdi_compositor_t *canvas)
{
    if (gpu->failed || !canvas->pixels) return false;
    /* Do not reinterpret interlaced fields as progressive. VLC's established
     * conversion path remains available for frames the processor cannot handle. */
    if (!picture->b_progressive) return false;
    HRESULT hr = E_INVALIDARG;
    picture_sys_t *system = HardwarePictureSystem(picture);
    if (!system || !system->surface || system->surface == gpu->device_surface ||
        source->left < 0 || source->top < 0 || source->right <= source->left || source->bottom <= source->top ||
        source->right > (LONG)picture->format.i_width || source->bottom > (LONG)picture->format.i_height)
        goto failed;
    IDirect3DSurface9 *surface = system->surface;
    IDirect3DDevice9 *device = NULL;
    hr = IDirect3DSurface9_GetDevice(surface, &device);
    if (FAILED(hr)) goto failed;
    /* COM identity is defined by IUnknown, not equality of two different
     * interface pointers (IDirect3DDevice9 versus IDirect3DDevice9Ex). */
    IUnknown *identity = NULL;
    hr = IDirect3DDevice9_QueryInterface(device, &IID_IUnknown, (void **)&identity);
    IDirect3DDevice9_Release(device);
    if (FAILED(hr)) goto failed;
    const bool same_device = identity == gpu->device_identity;
    identity->lpVtbl->Release(identity);
    if (!same_device) { hr = D3DERR_INVALIDCALL; goto failed; }
    hr = HardwareEnsureTargets(gpu, canvas->width, canvas->height);
    if (FAILED(hr)) goto failed;
    DXVA2_VideoProcessBltParams blt = {0};
    blt.TargetRect = (RECT){0, 0, canvas->width, canvas->height};
    blt.ConstrictionSize = (SIZE){canvas->width, canvas->height};
    blt.BackgroundColor.Cr = blt.BackgroundColor.Cb = 0x8000;
    blt.BackgroundColor.Y = 0x1000;
    blt.BackgroundColor.Alpha = 0xffff;
    blt.DestFormat = gpu->description.SampleFormat;
    blt.DestFormat.NominalRange = DXVA2_NominalRange_0_255;
    blt.ProcAmpValues = gpu->adjustment;
    blt.Alpha.Value = 1;
    DXVA2_VideoSample sample = {0};
    sample.Start = 0;
    sample.End = __MAX(1, 10000000LL * gpu->description.InputSampleFreq.Denominator /
                         gpu->description.InputSampleFreq.Numerator);
    sample.SampleFormat = gpu->description.SampleFormat;
    sample.SrcSurface = surface;
    sample.SrcRect = *source;
    sample.DstRect = blt.TargetRect;
    sample.PlanarAlpha.Value = 1;
    hr = IDirectXVideoProcessor_VideoProcessBlt(gpu->processor, gpu->target, &blt, &sample, 1, NULL);
    if (FAILED(hr)) goto failed;
    hr = IDirect3DDevice9Ex_GetRenderTargetData(gpu->device, gpu->target, gpu->download);
    if (FAILED(hr)) goto failed;
    D3DLOCKED_RECT lock;
    hr = IDirect3DSurface9_LockRect(gpu->download, &lock, NULL, D3DLOCK_READONLY);
    if (FAILED(hr)) goto failed;
    const size_t row_bytes = (size_t)canvas->width * 4;
    if (!lock.pBits || lock.Pitch < (int)row_bytes) {
        IDirect3DSurface9_UnlockRect(gpu->download);
        hr = E_UNEXPECTED;
        goto failed;
    }
    for (int y = 0; y < canvas->height; ++y)
        memcpy((uint8_t *)canvas->pixels + (size_t)y * row_bytes,
               (uint8_t *)lock.pBits + (size_t)y * lock.Pitch, row_bytes);
    hr = IDirect3DSurface9_UnlockRect(gpu->download);
    if (FAILED(hr)) goto failed;
    return true;
failed:
    msg_Warn(vd, "display-sized DXVA2 readback failed (0x%08lx); using VLC's download converter", (unsigned long)hr);
    gpu->failed = true;
    HardwareClearTargets(gpu);
    return false;
}

static picture_t *HardwareFallbackBuffer(filter_t *filter)
{
    studio_hardware_t *gpu = filter->owner.sys;
    return picture_Hold(gpu->fallback_picture);
}

static bool HardwareDownload(vout_display_t *vd, studio_hardware_t *gpu,
                             picture_t *picture, picture_pool_t *rgb_pool)
{
    /* A driver/capability failure must not turn a working stream into black
     * frames. Reuse VLC's ordinary DXVA2 -> YUV -> RGB converter chain, writing
     * into the original GDI bitmap. This is allocated only when needed. */
    if (!picture->context || gpu->fallback_failed) return false;
    if (!gpu->fallback) {
        gpu->fallback_picture = picture_pool_Get(rgb_pool);
        if (!gpu->fallback_picture) goto failed;
        filter_owner_t owner = { .sys = gpu, .video = { .buffer_new = HardwareFallbackBuffer } };
        gpu->fallback = filter_chain_NewVideo(vd, false, &owner);
        if (!gpu->fallback) goto failed;
        es_format_t input, output;
        es_format_InitFromVideo(&input, &picture->format);
        es_format_InitFromVideo(&output, &gpu->fallback_picture->format);
        filter_chain_Reset(gpu->fallback, &input, &output);
        const int status = filter_chain_AppendConverter(gpu->fallback, &input, &output);
        es_format_Clean(&input);
        es_format_Clean(&output);
        if (status != 0) goto failed;
        msg_Dbg(vd, "using VLC's full-resolution DXVA2 download fallback");
    }
    picture_t *converted = filter_chain_VideoFilter(gpu->fallback, picture_Hold(picture));
    if (!converted) return false;
    picture_Release(converted);
    return true;
failed:
    msg_Err(vd, "could not create the DXVA2 download fallback");
    if (gpu->fallback) filter_chain_Delete(gpu->fallback);
    gpu->fallback = NULL;
    if (gpu->fallback_picture) picture_Release(gpu->fallback_picture);
    gpu->fallback_picture = NULL;
    gpu->fallback_failed = true;
    return false;
}
#endif
