/* Display-resolution chat composition for Studio GDI. LGPL-2.1-or-later. */
#ifndef STUDIO_GDI_COMPOSITOR_H
#define STUDIO_GDI_COMPOSITOR_H
#include "scaler.h"
#include <vlc_subpicture.h>

typedef struct studio_gdi_layer_t {
    studio_gdi_scaler_t scale;
    picture_t *premultiplied;
    picture_t *source;
    bool source_valid, scaled_valid;
    struct studio_gdi_layer_t *next;
} studio_gdi_layer_t;

typedef struct {
    HDC dc;
    HBITMAP bitmap;
    HGDIOBJ previous;
    int width, height;
    studio_gdi_layer_t *layers;
} studio_gdi_compositor_t;

static void CleanLayers(studio_gdi_layer_t *layer)
{
    while (layer) {
        studio_gdi_layer_t *next = layer->next;
        CleanScaler(&layer->scale);
        if (layer->premultiplied) picture_Release(layer->premultiplied);
        if (layer->source) picture_Release(layer->source);
        free(layer);
        layer = next;
    }
}

static void CleanCompositor(studio_gdi_compositor_t *compositor)
{
    CleanLayers(compositor->layers);
    if (compositor->previous) SelectObject(compositor->dc, compositor->previous);
    if (compositor->bitmap) DeleteObject(compositor->bitmap);
    if (compositor->dc) DeleteDC(compositor->dc);
    memset(compositor, 0, sizeof(*compositor));
}

static bool EnsureCanvas(studio_gdi_compositor_t *compositor, HDC dc, int width, int height)
{
    if (compositor->dc && compositor->width == width && compositor->height == height)
        return true;
    CleanCompositor(compositor);
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return false;
    BITMAPINFO format = {0};
    format.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    format.bmiHeader.biWidth = width;
    format.bmiHeader.biHeight = -height;
    format.bmiHeader.biPlanes = 1;
    format.bmiHeader.biBitCount = 32;
    format.bmiHeader.biCompression = BI_RGB;
    void *pixels;
    compositor->dc = CreateCompatibleDC(dc);
    compositor->bitmap = CreateDIBSection(dc, &format, DIB_RGB_COLORS, &pixels, NULL, 0);
    if (!compositor->dc || !compositor->bitmap) goto failed;
    compositor->previous = SelectObject(compositor->dc, compositor->bitmap);
    if (!compositor->previous || compositor->previous == HGDI_ERROR) {
        compositor->previous = NULL;
        goto failed;
    }
    compositor->width = width;
    compositor->height = height;
    return true;
failed:
    CleanCompositor(compositor);
    return false;
}

static bool PrepareLayer(vlc_object_t *owner, studio_gdi_layer_t *layer,
                         HDC dc, const subpicture_region_t *region, int width, int height)
{
    const video_format_t *format = &region->fmt;
    if (!region->p_picture || format->i_chroma != VLC_CODEC_RGBA ||
        !format->i_visible_width || !format->i_visible_height ||
        format->i_visible_width > 16384 || format->i_visible_height > 16384 ||
        (uint64_t)format->i_x_offset + format->i_visible_width > region->p_picture->format.i_width ||
        (uint64_t)format->i_y_offset + format->i_visible_height > region->p_picture->format.i_height)
        return false;
    if (!layer->premultiplied || !layer->source ||
        layer->premultiplied->format.i_visible_width != format->i_visible_width ||
        layer->premultiplied->format.i_visible_height != format->i_visible_height) {
        CleanScaler(&layer->scale);
        memset(&layer->scale, 0, sizeof(layer->scale));
        if (layer->premultiplied) picture_Release(layer->premultiplied);
        if (layer->source) picture_Release(layer->source);
        layer->source_valid = layer->scaled_valid = false;
        video_format_t input;
        video_format_Init(&input, VLC_CODEC_BGRA);
        video_format_Setup(&input, VLC_CODEC_BGRA, format->i_visible_width, format->i_visible_height,
                          format->i_visible_width, format->i_visible_height, 1, 1);
        layer->premultiplied = picture_NewFromFormat(&input);
        input.i_chroma = VLC_CODEC_RGBA;
        layer->source = picture_NewFromFormat(&input);
        if (!layer->premultiplied || !layer->source) return false;
    }
    picture_t *input = layer->premultiplied;
    /* A video frame need not be a new chat frame. Compare the visible bytes,
     * not picture addresses: VLC can reuse pictures or update them in place.
     * A bounded snapshot also handles fresh pictures with identical content. */
    bool changed = !layer->source_valid;
    const size_t row_bytes = (size_t)format->i_visible_width * 4;
    for (unsigned y = 0; !changed && y < format->i_visible_height; y++) {
        const uint8_t *src = region->p_picture->p[0].p_pixels +
            (y + format->i_y_offset) * region->p_picture->p[0].i_pitch + format->i_x_offset * 4;
        changed = memcmp(src, layer->source->p[0].p_pixels + y * layer->source->p[0].i_pitch,
                         row_bytes) != 0;
    }
    /* Filter premultiplied coverage, so transparent texels cannot darken thin
     * white strokes or bleed invisible RGB into colored glyphs and emotes. */
    for (unsigned y = 0; changed && y < format->i_visible_height; y++) {
        const uint8_t *src = region->p_picture->p[0].p_pixels +
            (y + format->i_y_offset) * region->p_picture->p[0].i_pitch + format->i_x_offset * 4;
        uint8_t *dst = input->p[0].p_pixels + y * input->p[0].i_pitch;
        memcpy(layer->source->p[0].p_pixels + y * layer->source->p[0].i_pitch, src, row_bytes);
        for (unsigned x = 0; x < format->i_visible_width; x++, src += 4, dst += 4) {
            dst[0] = (src[2] * src[3] + 127) / 255;
            dst[1] = (src[1] * src[3] + 127) / 255;
            dst[2] = (src[0] * src[3] + 127) / 255;
            dst[3] = src[3];
        }
    }
    layer->source_valid = true;
    if (changed || layer->scale.scale_width != width || layer->scale.scale_height != height)
        layer->scaled_valid = false;
    if (layer->scaled_valid) return true;
    RECT source = {0, 0, format->i_visible_width, format->i_visible_height};
    if (!EnsureScaler(owner, &layer->scale, &input->format, dc, &source, width, height)) return false;
    picture_t *scaled = layer->scale.scaler->pf_video_filter(layer->scale.scaler, picture_Hold(input));
    if (!scaled) return false;
    picture_Release(scaled);
    layer->scaled_valid = true;
    return true;
}

static void ComposeChat(vlc_object_t *owner, studio_gdi_compositor_t *compositor,
                        subpicture_t *subpicture, const RECT *video, const RECT *clipped)
{
    studio_gdi_layer_t **slot = &compositor->layers;
    if (subpicture && subpicture->i_original_picture_width > 0 && subpicture->i_original_picture_height > 0) {
        /* Same coordinate mapping as VLC's Direct3D output: SPU positions are
         * relative to its original canvas, then clipped to the video window. */
        const double sx = (video->right - video->left) / (double)subpicture->i_original_picture_width;
        const double sy = (video->bottom - video->top) / (double)subpicture->i_original_picture_height;
        for (const subpicture_region_t *region = subpicture->p_region; region; region = region->p_next) {
            const int left = (int)(region->i_x * sx);
            const int top = (int)(region->i_y * sy);
            const int width = (int)((region->i_x + (double)region->fmt.i_visible_width) * sx) - left;
            const int height = (int)((region->i_y + (double)region->fmt.i_visible_height) * sy) - top;
            if (width <= 0 || height <= 0) continue;
            if (!*slot) *slot = calloc(1, sizeof(**slot));
            if (!*slot) break;
            studio_gdi_layer_t *layer = *slot;
            if (PrepareLayer(owner, layer, compositor->dc, region, width, height)) {
                BLENDFUNCTION blend = {AC_SRC_OVER, 0, (BYTE)(subpicture->i_alpha * region->i_alpha / 255), AC_SRC_ALPHA};
                AlphaBlend(compositor->dc, video->left - clipped->left + left,
                           video->top - clipped->top + top, width, height,
                           layer->scale.scale_dc, 0, 0, width, height, blend);
                /* Finish GDI reads before a later content change rewrites the DIB. */
                GdiFlush();
            }
            slot = &layer->next;
        }
    }
    CleanLayers(*slot);
    *slot = NULL;
}
#endif
