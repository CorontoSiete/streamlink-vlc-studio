/* Cached premultiplied BGRA scaler for the Studio GDI output. LGPL-2.1-or-later.
 * Uses VLC's public filter/module ABI and its installed swscale converter. */
#ifndef STUDIO_GDI_SCALER_H
#define STUDIO_GDI_SCALER_H
#include <vlc_filter.h>
#include <vlc_modules.h>
#include <vlc_picture.h>
#include <windows.h>
typedef struct {
    filter_t   *scaler;
    HDC         scale_dc;
    HBITMAP     scale_bitmap;
    HGDIOBJ     scale_previous;
    picture_t  *scale_picture;
    RECT        scale_source;
    int         scale_width, scale_height;
} studio_gdi_scaler_t;

static void CleanScaler(studio_gdi_scaler_t *sys)
{
    if (sys->scaler) {
        if (sys->scaler->p_module)
            module_unneed(sys->scaler, sys->scaler->p_module);
        es_format_Clean(&sys->scaler->fmt_in);
        es_format_Clean(&sys->scaler->fmt_out);
        vlc_object_release(sys->scaler);
        sys->scaler = NULL;
    }
    if (sys->scale_picture) picture_Release(sys->scale_picture);
    sys->scale_picture = NULL;
    if (sys->scale_previous) SelectObject(sys->scale_dc, sys->scale_previous);
    sys->scale_previous = NULL;
    if (sys->scale_bitmap) DeleteObject(sys->scale_bitmap);
    sys->scale_bitmap = NULL;
    if (sys->scale_dc) DeleteDC(sys->scale_dc);
    sys->scale_dc = NULL;
}

static picture_t *ScaleBuffer(filter_t *filter)
{
    studio_gdi_scaler_t *sys = filter->owner.sys;
    /* swscale consumes and returns this buffer synchronously in Display. */
    return picture_Hold(sys->scale_picture);
}

static bool EnsureScaler(vlc_object_t *owner, studio_gdi_scaler_t *sys,
                         const video_format_t *format, HDC dc, const RECT *source,
                         int width, int height)
{
    if (width == sys->scale_width && height == sys->scale_height &&
        EqualRect(source, &sys->scale_source))
        return sys->scaler != NULL;

    CleanScaler(sys);
    sys->scale_source = *source;
    sys->scale_width = width;
    sys->scale_height = height;
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384 ||
        source->left < 0 || source->top < 0 ||
        source->right > (LONG)format->i_width || source->bottom > (LONG)format->i_height ||
        source->right <= source->left || source->bottom <= source->top)
        return false;

    filter_t *filter = vlc_object_create(owner, sizeof(*filter));
    if (!filter) return false;
    sys->scaler = filter;
    es_format_Init(&filter->fmt_in, VIDEO_ES, format->i_chroma);
    video_format_Copy(&filter->fmt_in.video, format);
    filter->fmt_in.video.i_x_offset = source->left;
    filter->fmt_in.video.i_y_offset = source->top;
    filter->fmt_in.video.i_visible_width = source->right - source->left;
    filter->fmt_in.video.i_visible_height = source->bottom - source->top;
    es_format_Init(&filter->fmt_out, VIDEO_ES, VLC_CODEC_BGRA);
    /* Keep scanlines SIMD-aligned even for fractional window sizes. Only the
     * visible width is blitted, so the padding never reaches the window. */
    const int bitmap_width = (width + 15) & ~15;
    video_format_Setup(&filter->fmt_out.video, VLC_CODEC_BGRA, bitmap_width, height, width, height, 1, 1);
    filter->fmt_out.video.i_rmask = 0x00ff0000;
    filter->fmt_out.video.i_gmask = 0x0000ff00;
    filter->fmt_out.video.i_bmask = 0x000000ff;
    filter->owner.sys = sys;
    filter->owner.video.buffer_new = ScaleBuffer;

    BITMAPINFO bitmap = {0};
    bitmap.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bitmap.bmiHeader.biWidth = bitmap_width;
    bitmap.bmiHeader.biHeight = -height;
    bitmap.bmiHeader.biPlanes = 1;
    bitmap.bmiHeader.biBitCount = 32;
    bitmap.bmiHeader.biCompression = BI_RGB;
    void *pixels = NULL;
    sys->scale_dc = CreateCompatibleDC(dc);
    sys->scale_bitmap = CreateDIBSection(dc, &bitmap, DIB_RGB_COLORS, &pixels, NULL, 0);
    if (!sys->scale_dc || !sys->scale_bitmap) goto failed;
    sys->scale_previous = SelectObject(sys->scale_dc, sys->scale_bitmap);
    if (!sys->scale_previous || sys->scale_previous == HGDI_ERROR) {
        sys->scale_previous = NULL;
        goto failed;
    }
    picture_resource_t resource = {0};
    resource.p[0].p_pixels = pixels;
    resource.p[0].i_pitch = bitmap_width * 4;
    resource.p[0].i_lines = height;
    sys->scale_picture = picture_NewFromResource(&filter->fmt_out.video, &resource);
    if (!sys->scale_picture) goto failed;

    /* Area filtering preserves subpixel coverage when reducing the image.
     * Bilinear interpolation avoids blocks when the window is larger. */
    var_Create(filter, "swscale-mode", VLC_VAR_INTEGER);
    var_SetInteger(filter, "swscale-mode",
        width <= source->right - source->left && height <= source->bottom - source->top ? 5 : 1);
    filter->p_module = module_need(filter, "video converter", "swscale", true);
    if (!filter->p_module) goto failed;
    return true;

failed:
    msg_Warn(owner, "could not allocate the GDI chat scaler");
    CleanScaler(sys);
    return false;
}

#endif
