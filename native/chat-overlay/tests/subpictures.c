/* Test the production sub-source against uncached rendering using real VLC
 * allocation/refcount APIs. No network, decoder or desktop is required. */
#include <vlc_common.h>
#include <vlc_subpicture.h>
#include <assert.h>
#include <stdio.h>

static unsigned bitmap_allocations, region_calls, fail_region_call;
static size_t bitmap_bytes;
static subpicture_region_t *CountedRegionNew(const video_format_t *format)
{
    if (++region_calls == fail_region_call) return NULL;
    subpicture_region_t *region = subpicture_region_New(format);
    if (region && region->p_picture) {
        bitmap_allocations++;
        bitmap_bytes += (size_t)region->p_picture->p[0].i_pitch *
                        region->p_picture->p[0].i_lines;
    }
    return region;
}
#define subpicture_region_New CountedRegionNew
#include "../myoverlay.c"
#undef subpicture_region_New

/* The module descriptor references the separately tested GDI output. */
int StudioGdiOpen(vlc_object_t *object) { (void)object; return VLC_EGENERIC; }
void StudioGdiClose(vlc_object_t *object) { (void)object; }

static void compare(const subpicture_t *actual, const subpicture_t *expected)
{
    assert(actual && expected);
    assert(actual->i_start == expected->i_start);
    assert(actual->i_alpha == expected->i_alpha);
    assert(actual->b_ephemer == expected->b_ephemer);
    assert(actual->b_absolute == expected->b_absolute);
    const subpicture_region_t *a = actual->p_region, *b = expected->p_region;
    for (; a && b; a = a->p_next, b = b->p_next) {
        assert(!memcmp(&a->fmt, &b->fmt, sizeof(a->fmt)));
        assert(a->i_x == b->i_x && a->i_y == b->i_y && a->i_alpha == b->i_alpha);
        assert(a->i_align == b->i_align);
        for (unsigned y = 0; y < a->fmt.i_visible_height; y++)
            assert(!memcmp(a->p_picture->p[0].p_pixels + y * a->p_picture->p[0].i_pitch,
                           b->p_picture->p[0].p_pixels + y * b->p_picture->p[0].i_pitch,
                           (size_t)a->fmt.i_visible_width * 4));
    }
    assert(!a && !b);
}

static subpicture_t *uncached(filter_t *filter, vlc_tick_t date)
{
    filter_sys_t *sys = filter->p_sys;
    const uint32_t height = VisibleVideoHeight(&sys->metrics->source);
    const bool button = sys->mouse_over_chat || sys->button_pressed || sys->resizing;
    if (sys->blank_until_frame) {
        uint8_t *pixels; int pitch;
        subpicture_t *spu = NewOverlaySubpicture(filter, date, 1, 1, 0, 0, 0, &pixels, &pitch);
        assert(spu);
        memset(pixels, 0, 4);
        return spu;
    }
    if (sys->hidden)
        return HiddenButton(filter, date, sys->x, sys->y, sys->w, sys->h, button, height);
    if (!sys->have_frame)
        return sys->show_placeholder ? Placeholder(filter, date, sys->x, sys->y, button, height) : NULL;
    return FrameSubpicture(filter, date, sys->frame->picture, sys->frame->w, sys->frame->h,
        sys->w, sys->h, sys->x, sys->y, sys->frame->alpha, button,
        sys->mouse_over_chat || sys->scrollbar_pressed || sys->scrollbar_offset > 0,
        sys->scrollbar_offset, sys->scrollbar_max, sys->scrollbar_visible, sys->scrollbar_total, height);
}

static void check(filter_t *filter)
{
    subpicture_t *first = Filter(filter, 100);
    subpicture_t *expected = uncached(filter, 100);
    compare(first, expected);
    subpicture_Delete(expected);
    const unsigned before = bitmap_allocations;
    for (int i = 0; i < 20; i++) {
        /* A fresh timestamp/observer is still emitted, including after seeks. */
        subpicture_t *next = Filter(filter, i + 1);
        assert(next && next->i_start == i + 1);
        next->i_start = first->i_start;
        compare(next, first);
        for (subpicture_region_t *a = first->p_region, *b = next->p_region; a; a = a->p_next, b = b->p_next)
            assert(a->p_picture == b->p_picture);
        subpicture_Delete(next);
    }
    assert(bitmap_allocations == before);
    subpicture_Delete(first);
}

static void benchmark(filter_t *filter, bool hidden)
{
    filter->p_sys->hidden = hidden;
    subpicture_Delete(Filter(filter, 1));
    LARGE_INTEGER frequency, start, end;
    QueryPerformanceFrequency(&frequency);
    bitmap_allocations = 0; bitmap_bytes = 0;
    QueryPerformanceCounter(&start);
    for (int i = 0; i < 20000; i++) subpicture_Delete(Filter(filter, i + 2));
    QueryPerformanceCounter(&end);
    printf("%s cached: %.3f us/frame, %u bitmap allocations, %zu bitmap bytes\n",
        hidden ? "hidden" : "visible", (end.QuadPart - start.QuadPart) * 1e6 / frequency.QuadPart / 20000,
        bitmap_allocations, bitmap_bytes);
    assert(bitmap_allocations == 0);
}

int main(void)
{
    filter_t filter = {0};
    filter_sys_t sys = {0};
    filter.p_sys = &sys;
    sys.metrics = calloc(1, sizeof(*sys.metrics));
    assert(sys.metrics);
    sys.metrics->refs = 1;
    InitializeCriticalSection(&sys.lock);
    InitializeCriticalSection(&sys.metrics->lock);
    sys.metrics->source.i_visible_width = 3840;
    sys.metrics->source.i_visible_height = 2160;
    sys.video_size_attempt_ms = UINT64_MAX;
    sys.x = 32; sys.y = 48; sys.w = 340; sys.h = 292;
    sys.have_frame = true;
    overlay_msg_v1 header = {0};
    header.w = sys.w; header.h = sys.h; header.alpha = 173;
    uint8_t *pixels = malloc((size_t)header.w * header.h * 4);
    assert(pixels);
    for (size_t i = 0; i < (size_t)header.w * header.h * 4; i++) pixels[i] = (uint8_t)(i * 73);
    sys.frame = FrameBufferCreate(&header, pixels);
    assert(sys.frame);

    /* Match the original RGBA constructor's format and retain the same pixels. */
    video_format_t format;
    InitRgbaFormat(&format, header.w, header.h);
    subpicture_region_t *old = subpicture_region_New(&format);
    subpicture_region_t *reference = NewPictureRegion(sys.frame->picture, 0, 0, 255);
    assert(old && reference && !memcmp(&old->fmt, &reference->fmt, sizeof(format)));
    assert(reference->p_picture == sys.frame->picture);
    subpicture_region_Delete(old);
    subpicture_region_Delete(reference);
    check(&filter);
    sys.mouse_over_chat = true; check(&filter);
    sys.scrollbar_max = 200; sys.scrollbar_visible = 10; sys.scrollbar_total = 210; check(&filter);
    sys.scrollbar_offset = 80; check(&filter);
    sys.scrollbar_pressed = true; sys.mouse_over_chat = false; check(&filter);
    sys.button_pressed = true; check(&filter);
    sys.resizing = true; sys.w = 511; sys.h = 397; check(&filter);
    sys.x = 135; sys.y = 167; check(&filter);
    sys.metrics->source.i_visible_height = 1080; check(&filter);
    sys.metrics->source.i_visible_width = 320; check(&filter); /* clamps the drag position */
    sys.metrics->source.i_visible_width = 1920;

    /* Old SPUs must survive a new image, a cache reset and filter teardown. */
    subpicture_t *held = Filter(&filter, 123);
    subpicture_t *held_expected = uncached(&filter, 123);
    pixels[3] ^= 255; header.alpha = 220;
    overlay_frame_buffer_t *previous = sys.frame;
    sys.frame = FrameBufferCreate(&header, pixels);
    FrameBufferRelease(previous);
    check(&filter);
    compare(held, held_expected);
    sys.blank_until_frame = true; check(&filter);
    sys.blank_until_frame = false; check(&filter);
    sys.hidden = true; check(&filter);
    sys.mouse_over_chat = sys.button_pressed = sys.resizing = false; check(&filter);
    sys.hidden = false; sys.have_frame = false; sys.show_placeholder = true; check(&filter);
    sys.mouse_over_chat = true; check(&filter);
    sys.show_placeholder = false;
    assert(!Filter(&filter, 1) && !sys.cached_visual_valid && !sys.cached_regions);
    sys.have_frame = true; check(&filter);

    /* Allocation failure while cloning or rebuilding must allow a later retry. */
    fail_region_call = region_calls + 2;
    assert(!Filter(&filter, 1));
    fail_region_call = 0; check(&filter);
    sys.x++;
    fail_region_call = region_calls + 2;
    assert(!Filter(&filter, 1) && !sys.cached_visual_valid);
    fail_region_call = 0; check(&filter);
    benchmark(&filter, false);
    benchmark(&filter, true);

    FrameBufferRelease(sys.frame);
    subpicture_region_ChainDelete(sys.cached_regions);
    DeleteCriticalSection(&sys.lock);
    assert(sys.metrics->refs == 3); /* owner plus the two retained observers */
    ReleaseVideoMetrics(sys.metrics);
    compare(held, held_expected);
    subpicture_Delete(held);
    subpicture_Delete(held_expected);
    free(pixels);
    puts("Subpicture pixels, metadata, invalidation, allocation and ownership checks passed.");
    return 0;
}
