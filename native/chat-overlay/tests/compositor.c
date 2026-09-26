/* Exercise the production chat compositor with VLC's real swscale module. */
#include <vlc_common.h>
#include <vlc/vlc.h>
#include "../quality-gdi/compositor.h"
#include <assert.h>
#include <stdio.h>
const char vlc_module_name[] = "chat-compositor-test";

static unsigned scale_calls;
static picture_t *(*original_scale)(filter_t *, picture_t *);
static picture_t *count_scale(filter_t *filter, picture_t *picture)
{
    scale_calls++;
    return original_scale(filter, picture);
}

/* Compare every visible byte with a new compositor (no cached content). */
static void check_fresh(vlc_object_t *owner, studio_gdi_compositor_t *compositor,
                        subpicture_t *spu, const RECT *video, const RECT *clip)
{
    studio_gdi_compositor_t fresh = {0};
    assert(EnsureCanvas(&fresh, compositor->dc, compositor->width, compositor->height));
    PatBlt(compositor->dc, 0, 0, compositor->width, compositor->height, BLACKNESS);
    PatBlt(fresh.dc, 0, 0, fresh.width, fresh.height, BLACKNESS);
    ComposeChat(owner, compositor, spu, video, clip);
    ComposeChat(owner, &fresh, spu, video, clip);
    GdiFlush();
    DIBSECTION actual, expected;
    assert(GetObject(compositor->bitmap, sizeof(actual), &actual));
    assert(GetObject(fresh.bitmap, sizeof(expected), &expected));
    for (int y = 0; y < fresh.height; y++)
        assert(!memcmp((uint8_t *)actual.dsBm.bmBits + y * actual.dsBm.bmWidthBytes,
                       (uint8_t *)expected.dsBm.bmBits + y * expected.dsBm.bmWidthBytes,
                       (size_t)fresh.width * 4));
    CleanCompositor(&fresh);
}

static subpicture_t *fixture(int width, int height)
{
    subpicture_t *spu = subpicture_New(NULL);
    assert(spu);
    video_format_t format;
    video_format_Init(&format, VLC_CODEC_RGBA);
    video_format_Setup(&format, VLC_CODEC_RGBA, width, height, width, height, 1, 1);
    spu->p_region = subpicture_region_New(&format);
    assert(spu->p_region);
    spu->i_original_picture_width = 1920;
    spu->i_original_picture_height = 1080;
    spu->i_alpha = spu->p_region->i_alpha = 255;
    for (int y = 0; y < height; y++) {
        uint8_t *row = spu->p_region->p_picture->p[0].p_pixels +
            y * spu->p_region->p_picture->p[0].i_pitch;
        for (int x = 0; x < width; x++) {
            row[x*4] = row[x*4+1] = row[x*4+2] = (x % 2) * 255;
            row[x*4+3] = 255;
        }
    }
    return spu;
}

static void check_color(HDC dc, int x, int y, int r, int g, int b)
{
    GdiFlush();
    COLORREF color = GetPixel(dc, x, y);
    assert(abs(GetRValue(color)-r) <= 3);
    assert(abs(GetGValue(color)-g) <= 3);
    assert(abs(GetBValue(color)-b) <= 3);
}

int main(void)
{
    const char *args[] = {"--quiet", "--no-audio"};
    libvlc_instance_t *vlc = libvlc_new(2, args);
    assert(vlc);
    libvlc_media_player_t *player = libvlc_media_player_new(vlc);
    assert(player);
    vlc_object_t *owner = (vlc_object_t *)player;
    HDC screen = GetDC(NULL);
    const DWORD baseline_handles = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    studio_gdi_compositor_t compositor = {0};
    assert(EnsureCanvas(&compositor, screen, 960, 540));
    RECT video = {0, 0, 960, 540};
    subpicture_t *spu = fixture(96, 48);
    PatBlt(compositor.dc, 0, 0, 960, 540, BLACKNESS);
    ComposeChat(owner, &compositor, spu, &video, &video);
    check_color(compositor.dc, 10, 10, 128, 128, 128);
    picture_t *cached = compositor.layers->premultiplied;
    filter_t *filter = compositor.layers->scale.scaler;
    original_scale = filter->pf_video_filter;
    filter->pf_video_filter = count_scale;
    for (int i = 0; i < 20; i++) ComposeChat(owner, &compositor, spu, &video, &video);
    assert(compositor.layers->premultiplied == cached && compositor.layers->scale.scaler == filter);
    assert(scale_calls == 0);
    check_fresh(owner, &compositor, spu, &video, &video);
    assert(scale_calls == 0);

    /* Alternating opaque white and invisible saturated blue must become half
     * covered white, not dark blue fringes or double-applied coverage. */
    picture_t *picture = spu->p_region->p_picture;
    for (int y = 0; y < 48; y++) for (int x = 0; x < 96; x++) {
        uint8_t *p = picture->p[0].p_pixels + y*picture->p[0].i_pitch + x*4;
        p[0] = p[1] = (x%2)*255; p[2] = 255; p[3] = (x%2)*255;
    }
    PatBlt(compositor.dc, 0, 0, 960, 540, BLACKNESS);
    ComposeChat(owner, &compositor, spu, &video, &video);
    assert(scale_calls == 1); /* Mutating the same picture invalidates its cache. */
    check_fresh(owner, &compositor, spu, &video, &video);
    check_color(compositor.dc, 10, 10, 128, 128, 128);
    spu->p_region->i_alpha = 128;
    PatBlt(compositor.dc, 0, 0, 960, 540, BLACKNESS);
    ComposeChat(owner, &compositor, spu, &video, &video);
    check_color(compositor.dc, 10, 10, 64, 64, 64);
    assert(scale_calls == 1); /* Opacity is applied during blending, not scaling. */
    PatBlt(compositor.dc, 0, 0, 960, 540, WHITENESS);
    ComposeChat(owner, &compositor, spu, &video, &video);
    check_color(compositor.dc, 10, 10, 255, 255, 255);
    spu->p_region->i_alpha = 255;

    /* Picture replacement alone is not a content change. A one-byte change
     * at the very end must invalidate, including alpha-only animation. */
    picture_t *replacement = picture_NewFromFormat(&picture->format);
    assert(replacement);
    picture_CopyPixels(replacement, picture);
    spu->p_region->p_picture = replacement;
    picture_Release(picture);
    check_fresh(owner, &compositor, spu, &video, &video);
    assert(scale_calls == 1);
    replacement->p[0].p_pixels[47 * replacement->p[0].i_pitch + 95 * 4 + 3] = 128;
    check_fresh(owner, &compositor, spu, &video, &video);
    assert(scale_calls == 2);
    filter->pf_video_filter = original_scale;
    /* Resize the output without reallocating the canvas: unchanged pixels
     * still need rescaling. Exercise 1:1 and enlargement as well as reduction. */
    for (int i = 0; i < 3; i++) {
        RECT resized = {0, 0, 1920 + i * 137, 1080 + i * 73};
        check_fresh(owner, &compositor, spu, &resized, &video);
    }

    /* Source cropping, off-window positioning and fractional output widths. */
    spu->p_region->fmt.i_x_offset = 8;
    spu->p_region->fmt.i_visible_width = 80;
    spu->p_region->i_x = 32;
    spu->p_region->i_y = 32;
    RECT clip = {10, 10, 960, 540};
    PatBlt(compositor.dc, 0, 0, 960, 540, BLACKNESS);
    ComposeChat(owner, &compositor, spu, &video, &clip);
    check_color(compositor.dc, 8, 8, 128, 128, 128);
    check_color(compositor.dc, 4, 4, 0, 0, 0);
    check_fresh(owner, &compositor, spu, &video, &clip);
    /* Same dimensions with a different crop, then move without resizing. */
    spu->p_region->fmt.i_x_offset = 9;
    check_fresh(owner, &compositor, spu, &video, &clip);
    spu->p_region->i_x = -10;
    check_fresh(owner, &compositor, spu, &video, &clip);
    for (int i = 0; i < 30; i++) {
        video.right = 853 + i % 5; video.bottom = 480 + i % 3;
        assert(EnsureCanvas(&compositor, screen, video.right, video.bottom));
        ComposeChat(owner, &compositor, spu, &video, &video);
        check_fresh(owner, &compositor, spu, &video, &video);
        assert(compositor.layers->scale.scale_picture->p[0].i_pitch % 64 == 0);
    }
    ComposeChat(owner, &compositor, NULL, &video, &video);
    assert(!compositor.layers);
    check_fresh(owner, &compositor, spu, &video, &video); /* Hide/show. */
    subpicture_Delete(spu);
    CleanCompositor(&compositor);
    assert(GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS) == baseline_handles);

    LARGE_INTEGER frequency, start, end;
    QueryPerformanceFrequency(&frequency);
    for (int scale = 1; scale <= 2; scale++) {
        spu = fixture(340*scale, 292*scale);
        spu->i_original_picture_width = 1920*scale;
        spu->i_original_picture_height = 1080*scale;
        video = (RECT){0, 0, 1280, 720};
        assert(EnsureCanvas(&compositor, screen, 1280, 720));
        ComposeChat(owner, &compositor, spu, &video, &video);
        QueryPerformanceCounter(&start);
        for (int frame = 0; frame < 200; frame++)
            ComposeChat(owner, &compositor, spu, &video, &video);
        QueryPerformanceCounter(&end);
        printf("%dp source chat -> 720p window: %.3f ms/frame (200 frames, premultiply + scale + blend)\n",
            1080*scale, (end.QuadPart-start.QuadPart)*1000.0/frequency.QuadPart/200);
        subpicture_Delete(spu);
        CleanCompositor(&compositor);
    }
    assert(GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS) == baseline_handles);
    ReleaseDC(NULL, screen);
    libvlc_media_player_release(player);
    libvlc_release(vlc);
    puts("PASS chat coverage, colored transparent edges, opacity, cropping, clipping, cache reuse, resize alignment and GDI cleanup");
    return 0;
}
