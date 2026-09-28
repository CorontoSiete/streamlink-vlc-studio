/* Exercise the production DXVA2 scaler/readback and its real VLC fallback.
 * Requires the supported Windows x64 VLC 3.0.23 runtime and a DXVA2 GPU.
 * Fail rather than silently skip if the requested hardware path is unavailable.
 */
#include <vlc_common.h>
#include <vlc/vlc.h>
#include <vlc_vout_display.h>
#include <vlc_picture_pool.h>
#include "../quality-gdi/compositor.h"
#include "../quality-gdi/hardware.h"
#include <assert.h>
#include <stdio.h>
const char vlc_module_name[] = "gdi-hardware-test";

static void check_color(HDC dc, int x, int y, int r, int g, int b)
{
    GdiFlush();
    COLORREF actual = GetPixel(dc, x, y);
    if (abs(GetRValue(actual)-r) > 4 || abs(GetGValue(actual)-g) > 4 || abs(GetBValue(actual)-b) > 4) {
        fprintf(stderr, "pixel (%d,%d): expected %d,%d,%d, got %d,%d,%d\n",
                x, y, r, g, b, GetRValue(actual), GetGValue(actual), GetBValue(actual));
        abort();
    }
}

static void destroy_context(picture_context_t *context)
{
    struct studio_va_picture_context *ctx = (void *)context;
    IDirect3DSurface9_Release(ctx->picture.surface);
    free(ctx);
}

static picture_context_t *copy_context(picture_context_t *context)
{
    struct studio_va_picture_context *copy = malloc(sizeof(*copy));
    assert(copy);
    *copy = *(struct studio_va_picture_context *)context;
    IDirect3DSurface9_AddRef(copy->picture.surface);
    return &copy->context;
}

/* Four constant quadrants: red, blue, black and white. Values are the
 * independently rounded BT.601/709 full/limited-range YCbCr color equations. */
static void fill_surface(IDirect3DSurface9 *surface, bool hd, bool full, bool swapped)
{
    const uint8_t red[] = {full ? (hd ? 54 : 76) : (hd ? 63 : 81),
                           full ? (hd ? 99 : 85) : (hd ? 102 : 90), full ? 255 : 240};
    const uint8_t blue[] = {full ? (hd ? 18 : 29) : (hd ? 32 : 41),
                            full ? 255 : 240, full ? (hd ? 116 : 107) : (hd ? 118 : 110)};
    const uint8_t black[] = {full ? 0 : 16, 128, 128};
    const uint8_t white[] = {full ? 255 : 235, 128, 128};
    const uint8_t *colors[] = {swapped ? blue : red, swapped ? red : blue, black, white};
    D3DLOCKED_RECT lock;
    assert(SUCCEEDED(IDirect3DSurface9_LockRect(surface, &lock, NULL, 0)));
    for (int y = 0; y < 96; ++y) for (int x = 0; x < 128; ++x)
        ((uint8_t *)lock.pBits)[y * lock.Pitch + x] = colors[(y >= 48) * 2 + (x >= 64)][0];
    for (int y = 0; y < 48; ++y) for (int x = 0; x < 128; x += 2) {
        uint8_t *uv = (uint8_t *)lock.pBits + (96 + y) * lock.Pitch + x;
        const uint8_t *color = colors[(y >= 24) * 2 + (x >= 64)];
        uv[0] = color[1]; uv[1] = color[2];
    }
    assert(SUCCEEDED(IDirect3DSurface9_UnlockRect(surface)));
}

static int clamp_rgb(double value)
{
    return value < 0 ? 0 : value > 255 ? 255 : (int)(value + .5);
}

static void check_fallback_color(HDC dc, int x, bool hd, bool full, bool red)
{
    /* VLC 3.0.23's ordinary I420 -> RGB converter uses the limited BT.601
     * matrix even for tagged BT.709/full-range input (swscale.c does not set
     * colorspace details). The emergency path must match that existing output;
     * the primary DXVA2 path above is checked against the actual source colors. */
    const int y = red ? (full ? (hd ? 54 : 76) : (hd ? 63 : 81)) :
                        (full ? (hd ? 18 : 29) : (hd ? 32 : 41));
    const int u = red ? (full ? (hd ? 99 : 85) : (hd ? 102 : 90)) : (full ? 255 : 240);
    const int v = red ? (full ? 255 : 240) : (full ? (hd ? 116 : 107) : (hd ? 118 : 110));
    const double luma = 1.164383 * (y - 16);
    check_color(dc, x, 24, clamp_rgb(luma + 1.596027 * (v - 128)),
        clamp_rgb(luma - .391762 * (u - 128) - .812968 * (v - 128)),
        clamp_rgb(luma + 2.017232 * (u - 128)));
}

static picture_t *frame(studio_hardware_t *gpu, const video_format_t *format)
{
    picture_pool_t *pool = HardwarePool(gpu, format, 4);
    assert(pool);
    picture_t *picture = picture_pool_Get(pool);
    assert(picture);
    struct studio_va_picture_context *context = calloc(1, sizeof(*context));
    assert(context);
    context->context.destroy = destroy_context;
    context->context.copy = copy_context;
    assert(SUCCEEDED(IDirect3DDevice9Ex_CreateOffscreenPlainSurface(gpu->device, 128, 96,
        MAKEFOURCC('N','V','1','2'), D3DPOOL_DEFAULT, &context->picture.surface, NULL)));
    picture->context = &context->context;
    picture->b_progressive = true;
    return picture;
}

static void check_format_guards(video_format_t format)
{
    assert(HardwareSupportsFormat(&format));
    video_format_t other = format;
    other.i_chroma = VLC_CODEC_D3D9_OPAQUE_10B; assert(!HardwareSupportsFormat(&other));
    other = format; other.i_chroma = VLC_CODEC_I420; assert(!HardwareSupportsFormat(&other));
    other = format; other.space = COLOR_SPACE_BT2020; assert(!HardwareSupportsFormat(&other));
    other = format; other.primaries = COLOR_PRIMARIES_BT2020; assert(!HardwareSupportsFormat(&other));
    other = format; other.transfer = TRANSFER_FUNC_SMPTE_ST2084; assert(!HardwareSupportsFormat(&other));
    other = format; other.transfer = TRANSFER_FUNC_HLG; assert(!HardwareSupportsFormat(&other));
    other = format; other.orientation = ORIENT_ROTATED_90; assert(!HardwareSupportsFormat(&other));
    other = format; other.i_width = 0; assert(!HardwareSupportsFormat(&other));
    other = format; other.i_height = 16385; assert(!HardwareSupportsFormat(&other));
}

static void exercise(vlc_object_t *owner, bool hd, bool full)
{
    printf("Testing BT.%s %s\n", hd ? "709" : "601", full ? "full" : "limited");
    vout_display_t *vd = vlc_object_create(owner, sizeof(*vd));
    assert(vd);
    video_format_Setup(&vd->source, VLC_CODEC_D3D9_OPAQUE, 128, 96, 128, 96, 1, 1);
    vd->source.space = hd ? COLOR_SPACE_BT709 : COLOR_SPACE_BT601;
    vd->source.primaries = hd ? COLOR_PRIMARIES_BT709 : COLOR_PRIMARIES_BT601_525;
    vd->source.transfer = TRANSFER_FUNC_BT709;
    vd->source.b_color_range_full = full;
    vd->source.i_frame_rate = 60000;
    vd->source.i_frame_rate_base = 1001;
    check_format_guards(vd->source);
    studio_hardware_t gpu = {0};
    var_Create(vd, "studio-gdi-gpu-scaling", VLC_VAR_BOOL);
    assert(!HardwareOpen(vd, &gpu, GetDesktopWindow()));
    var_SetBool(vd, "studio-gdi-gpu-scaling", true);
    assert(HardwareOpen(vd, &gpu, GetDesktopWindow()));
    assert(!HardwarePool(&gpu, &vd->source, 0));
    assert(!HardwarePool(&gpu, &vd->source, 129));
    picture_t *picture = frame(&gpu, &vd->source);
    IDirect3DSurface9 *surface = HardwarePictureSystem(picture)->surface;
    fill_surface(surface, hd, full, false);
    studio_gdi_compositor_t canvas = {0};
    HDC screen = GetDC(NULL);
    const SIZE sizes[] = {{64,48}, {127,65}, {256,192}, {32,24}, {128,96}};
    RECT source = {0, 0, 128, 96};
    for (unsigned repeat = 0; repeat < 6; ++repeat) for (unsigned i = 0; i < ARRAY_SIZE(sizes); ++i) {
        const int width = sizes[i].cx, height = sizes[i].cy;
        assert(EnsureCanvas(&canvas, screen, width, height));
        assert(HardwareDraw(vd, &gpu, picture, &source, &canvas));
        check_color(canvas.dc, width/4, height/4, 255, 0, 0);
        check_color(canvas.dc, width*3/4, height/4, 0, 0, 255);
        check_color(canvas.dc, width/4, height*3/4, 0, 0, 0);
        check_color(canvas.dc, width*3/4, height*3/4, 255, 255, 255);
    }
    source = (RECT){64, 0, 128, 48};
    assert(EnsureCanvas(&canvas, screen, 64, 48));
    assert(HardwareDraw(vd, &gpu, picture, &source, &canvas));
    check_color(canvas.dc, 32, 24, 0, 0, 255);
    video_format_t rgba;
    video_format_Init(&rgba, VLC_CODEC_RGBA);
    video_format_Setup(&rgba, VLC_CODEC_RGBA, 8, 8, 8, 8, 1, 1);
    subpicture_t *chat = subpicture_New(NULL);
    assert(chat);
    chat->p_region = subpicture_region_New(&rgba);
    assert(chat->p_region);
    chat->i_original_picture_width = 128;
    chat->i_original_picture_height = 96;
    chat->i_alpha = 128;
    chat->p_region->i_alpha = 255;
    for (int y = 0; y < 8; ++y)
        memset(chat->p_region->p_picture->p[0].p_pixels + y * chat->p_region->p_picture->p[0].i_pitch, 255, 32);
    RECT displayed = {0, 0, 64, 48};
    ComposeChat(VLC_OBJECT(vd), &canvas, chat, &displayed, &displayed);
    check_color(canvas.dc, 1, 1, 128, 128, 255);
    subpicture_Delete(chat);

    /* Reject interlaced input without poisoning subsequent progressive frames. */
    picture->b_progressive = false;
    assert(!HardwareDraw(vd, &gpu, picture, &source, &canvas) && !gpu.failed);
    picture->b_progressive = true;
    assert(HardwareDraw(vd, &gpu, picture, &source, &canvas));

    /* A decoder restart may supply a different device. Never read that surface
     * through stale targets; the CPU download converter must still render it. */
    studio_hardware_t restarted = {0};
    assert(HardwareOpen(vd, &restarted, GetDesktopWindow()));
    picture_t *fresh = frame(&restarted, &vd->source);
    fill_surface(HardwarePictureSystem(fresh)->surface, hd, full, false);
    assert(!HardwareDraw(vd, &gpu, fresh, &source, &canvas));
    assert(gpu.failed && !gpu.target && !gpu.download);
    studio_gdi_compositor_t fallback = {0};
    assert(EnsureCanvas(&fallback, screen, 128, 96));
    video_format_t rgb = vd->source;
    rgb.i_chroma = VLC_CODEC_RGB32;
    rgb.i_rmask = 0x00ff0000; rgb.i_gmask = 0x0000ff00; rgb.i_bmask = 0x000000ff;
    picture_resource_t resource = {0};
    resource.p[0].p_pixels = fallback.pixels;
    resource.p[0].i_pitch = 128 * 4;
    resource.p[0].i_lines = 96;
    picture_t *rgb_picture = picture_NewFromResource(&rgb, &resource);
    assert(rgb_picture);
    picture_pool_t *rgb_pool = picture_pool_New(1, &rgb_picture);
    assert(rgb_pool);
    puts("Testing fallback pixels");
    assert(HardwareDownload(vd, &gpu, fresh, rgb_pool));
    check_fallback_color(fallback.dc, 32, hd, full, true);
    check_fallback_color(fallback.dc, 96, hd, full, false);
    fill_surface(HardwarePictureSystem(fresh)->surface, hd, full, true);
    assert(HardwareDownload(vd, &gpu, fresh, rgb_pool));
    check_fallback_color(fallback.dc, 32, hd, full, false);
    check_fallback_color(fallback.dc, 96, hd, full, true);
    picture_Release(fresh);
    HardwareClose(&restarted);
    /* Outstanding pictures own their surface/device independently of the vout. */
    HardwareClose(&gpu);
    D3DLOCKED_RECT lock;
    assert(SUCCEEDED(IDirect3DSurface9_LockRect(surface, &lock, NULL, D3DLOCK_READONLY)));
    IDirect3DSurface9_UnlockRect(surface);
    picture_Release(picture);
    picture_pool_Release(rgb_pool);
    CleanCompositor(&canvas);
    CleanCompositor(&fallback);
    ReleaseDC(NULL, screen);
    video_format_Clean(&vd->source);
    vlc_object_release(vd);
    printf("PASS BT.%s %s: colors, fractional resize, crop, chat, interlace guard, device restart, fallback and lifetime\n",
           hd ? "709" : "601", full ? "full" : "limited");
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    assert(!strncmp(libvlc_get_version(), "3.0.23 ", 7));
    const char *args[] = {"--quiet", "--no-audio"};
    libvlc_instance_t *vlc = libvlc_new(2, args);
    assert(vlc);
    libvlc_media_player_t *player = libvlc_media_player_new(vlc);
    assert(player);
    const DWORD gdi = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    for (int hd = 0; hd < 2; ++hd) for (int full = 0; full < 2; ++full)
        exercise((vlc_object_t *)player, hd, full);
    assert(GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS) == gdi);
    libvlc_media_player_release(player);
    libvlc_release(vlc);
    puts("All hardware GDI checks passed; no GDI handles retained.");
    return 0;
}
