/* Exercise the production receiver through actual byte-mode Windows pipes.
 * Link to VLC's real picture/region allocation and reference-counting APIs. */
#include <vlc_common.h>
#include <vlc_subpicture.h>
#include <windows.h>
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>

static volatile LONG picture_allocations, receive_allocations, fail_picture, fail_receive, fail_frame;
static size_t picture_bytes;
static picture_t *CountedPicture(const video_format_t *format)
{
    if (InterlockedExchange(&fail_picture, 0)) return NULL;
    picture_t *picture = picture_NewFromFormat(format);
    if (picture) {
        InterlockedIncrement(&picture_allocations);
        picture_bytes += (size_t)picture->p[0].i_pitch * picture->p[0].i_lines;
    }
    return picture;
}
static void *CountedReceive(void *previous, size_t bytes)
{
    if (InterlockedExchange(&fail_receive, 0)) return NULL;
    InterlockedIncrement(&receive_allocations);
    return realloc(previous, bytes);
}
static void *FrameAllocation(size_t bytes)
{
    if (InterlockedExchange(&fail_frame, 0)) return NULL;
#ifdef BASELINE_RECEIVE
    /* The original receiver mallocs this benchmark's fixed payload for every
     * frame. Its only other malloc is the small frame-reference structure. */
    if (bytes == 340u * 292u * 4u) InterlockedIncrement(&receive_allocations);
#endif
    return malloc(bytes);
}
static void TestLog(void *object, const char *format, ...) { (void)object; (void)format; }
#undef msg_Dbg
#undef msg_Warn
#define msg_Dbg(...) TestLog(__VA_ARGS__)
#define msg_Warn(...) TestLog(__VA_ARGS__)
#define picture_NewFromFormat CountedPicture
#define realloc CountedReceive
#define malloc FrameAllocation
#include "../myoverlay.c"
#undef picture_NewFromFormat
#undef realloc
#undef malloc

int StudioGdiOpen(vlc_object_t *object) { (void)object; return VLC_EGENERIC; }
void StudioGdiClose(vlc_object_t *object) { (void)object; }

static void start(filter_t *filter, unsigned index)
{
    filter_sys_t *sys = calloc(1, sizeof(*sys));
    assert(sys);
    filter->p_sys = sys;
    InitializeCriticalSection(&sys->lock);
    sys->metrics = calloc(1, sizeof(*sys->metrics));
    assert(sys->metrics);
    InitializeCriticalSection(&sys->metrics->lock);
    sys->metrics->refs = 1;
    sys->metrics->source.i_visible_width = 1920;
    sys->metrics->source.i_visible_height = 1080;
    sys->video_size_attempt_ms = UINT64_MAX;
    snprintf(sys->pipe_name, sizeof(sys->pipe_name), "\\\\.\\pipe\\svs_receive_%lu_%u", GetCurrentProcessId(), index);
    sys->stop_event = CreateEvent(NULL, TRUE, FALSE, NULL);
    assert(sys->stop_event);
    sys->thread = CreateThread(NULL, 0, PipeWorker, filter, 0, NULL);
    assert(sys->thread);
}

static HANDLE connect_client(filter_t *filter)
{
    const ULONGLONG end = GetTickCount64() + 5000;
    do {
        HANDLE pipe = CreateFileA(filter->p_sys->pipe_name, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;
        Sleep(1);
    } while (GetTickCount64() < end);
    assert(!"pipe connection timed out");
    return INVALID_HANDLE_VALUE;
}

static void write_all(HANDLE pipe, const void *data, DWORD bytes)
{
    const uint8_t *next = data;
    while (bytes) {
        DWORD written = 0;
        assert(WriteFile(pipe, next, bytes, &written, NULL) && written);
        next += written;
        bytes -= written;
    }
}

static overlay_msg_v1 header(uint32_t w, uint32_t h)
{
    overlay_msg_v1 result = {0};
    result.magic = MYO_MAGIC; result.version = MYO_VERSION; result.type = MYO_TYPE_FRAME;
    result.w = w; result.h = h; result.alpha = 193; result.payload_size = w * h * 4;
    return result;
}

static void send_frame(HANDLE pipe, const overlay_msg_v1 *hdr, const uint8_t *pixels)
{
    write_all(pipe, hdr, sizeof(*hdr));
    if (hdr->payload_size) write_all(pipe, pixels, hdr->payload_size);
}

/* A scroll-state message is a protocol-preserving barrier: the receiver applies
 * it only after the complete preceding frame. No arbitrary timing sleeps. */
static void wait_marker(filter_t *filter, uint32_t expected)
{
    const ULONGLONG end = GetTickCount64() + 5000;
    do {
        EnterCriticalSection(&filter->p_sys->lock);
        bool done = filter->p_sys->scrollbar_visible == expected;
        LeaveCriticalSection(&filter->p_sys->lock);
        if (done) return;
        Sleep(1);
    } while (GetTickCount64() < end);
    assert(!"receiver barrier timed out");
}

static void barrier(filter_t *filter, HANDLE pipe)
{
    static uint32_t sequence;
    overlay_msg_v1 marker = header(0, 0);
    marker.type = MYO_TYPE_SCROLL_STATE; marker.w = ++sequence;
    send_frame(pipe, &marker, NULL); wait_marker(filter, marker.w);
    /* Restore the visual state before Filter observes it, so the barrier itself
     * cannot invalidate the unchanged-frame region cache being measured. */
    marker.w = 0;
    send_frame(pipe, &marker, NULL); wait_marker(filter, 0);
}

static uint64_t picture_hash(picture_t *picture, uint32_t w, uint32_t h)
{
    uint64_t hash = UINT64_C(14695981039346656037);
    for (uint32_t y = 0; y < h; y++)
        for (uint32_t x = 0; x < w * 4; x++) {
            hash ^= picture->p[0].p_pixels[(size_t)y * picture->p[0].i_pitch + x];
            hash *= UINT64_C(1099511628211);
        }
    return hash;
}

#ifndef BASELINE_RECEIVE
static void regression(void)
{
    filter_t filter = {0}; start(&filter, 0);
    filter_sys_t *sys = filter.p_sys;
    HANDLE pipe = connect_client(&filter);
    overlay_msg_v1 hdr = header(341, 293); /* visible rows differ from VLC's pitch */
    uint8_t *pixels = malloc(512 * 400 * 4);
    assert(pixels);
    for (unsigned i = 0; i < 512 * 400 * 4; i++) pixels[i] = (uint8_t)(i * 73);
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    picture_t *first = sys->frame->picture;
    const LONG before = picture_allocations, scratch_before = receive_allocations;
    subpicture_t *held = Filter(&filter, 1);
    assert(held && held->p_region->p_picture == first);
    subpicture_region_t *cached = sys->cached_regions;
    const uint64_t old_hash = picture_hash(first, hdr.w, hdr.h);
    for (unsigned i = 0; i < 50; i++) {
        send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
        assert(sys->frame->picture == first);
        subpicture_t *spu = Filter(&filter, i + 2);
        assert(spu && spu->p_region->p_picture == held->p_region->p_picture);
        assert(sys->cached_regions == cached);
        subpicture_Delete(spu);
    }
    assert(picture_allocations == before && receive_allocations == scratch_before);
    /* An unchanged frame must still acknowledge the requested dimensions. */
    EnterCriticalSection(&sys->lock);
    sys->resize_frame_pending = true; sys->resize_pending_w = hdr.w; sys->resize_pending_h = hdr.h;
    sys->w = 500; sys->h = 400;
    LeaveCriticalSection(&sys->lock);
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    assert(!sys->resize_frame_pending && sys->w == hdr.w && sys->h == hdr.h);
    assert(sys->frame->picture == first);
    CloseHandle(pipe); pipe = connect_client(&filter);
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    assert(sys->frame->picture == first && receive_allocations == scratch_before);

    pixels[hdr.payload_size - 1] ^= 255;
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    assert(sys->frame->picture != first && picture_allocations == before + 1);
    assert(FrameBufferMatches(sys->frame, &hdr, pixels));
    assert(picture_hash(first, hdr.w, hdr.h) == old_hash);
    hdr.alpha--;
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    assert(picture_allocations == before + 2 && sys->frame->alpha == hdr.alpha);

    picture_t *previous = sys->frame->picture;
    pixels[0] ^= 255;
    InterlockedExchange(&fail_picture, 1);
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    assert(sys->frame->picture == previous);
    InterlockedExchange(&fail_frame, 1);
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    assert(sys->frame->picture == previous);
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    assert(FrameBufferMatches(sys->frame, &hdr, pixels));

    const size_t old_capacity = sys->receive_capacity;
    assert(old_capacity <= MYO_MAX_PAYLOAD);
    assert(!ReserveReceiveBuffer(sys, (size_t)MYO_MAX_PAYLOAD + 1));
    overlay_msg_v1 larger = header(512, 400);
    InterlockedExchange(&fail_receive, 1);
    write_all(pipe, &larger, sizeof(larger));
    CloseHandle(pipe); pipe = connect_client(&filter);
    assert(sys->receive_capacity == old_capacity);
    send_frame(pipe, &larger, pixels); barrier(&filter, pipe);
    assert(FrameBufferMatches(sys->frame, &larger, pixels));
    assert(sys->w == larger.w && sys->h == larger.h);
    previous = sys->frame->picture;

    /* Rejected and incomplete payloads never replace a valid current image. */
    overlay_msg_v1 bad = header(2470483914u, 3733427279u);
    bad.payload_size = 14056792u;
    write_all(pipe, &bad, sizeof(bad)); CloseHandle(pipe); pipe = connect_client(&filter);
    assert(sys->frame->picture == previous);
    write_all(pipe, &hdr, sizeof(hdr)); write_all(pipe, pixels, 13);
    CloseHandle(pipe); pipe = connect_client(&filter);
    assert(sys->frame->picture == previous);
    overlay_msg_v1 clear = header(0, 0); clear.type = MYO_TYPE_CLEAR;
    send_frame(pipe, &clear, NULL); barrier(&filter, pipe);
    assert(!sys->frame && !sys->have_frame && sys->blank_until_frame);
    send_frame(pipe, &hdr, pixels); barrier(&filter, pipe);
    assert(sys->have_frame && !sys->blank_until_frame && FrameBufferMatches(sys->frame, &hdr, pixels));
    CloseHandle(pipe);
    Close((vlc_object_t *)&filter);
    assert(picture_hash(held->p_region->p_picture, hdr.w, hdr.h) == old_hash);
    subpicture_Delete(held);
    free(pixels);
    puts("Real pipe receipt: identity, pixels, alpha, resize ACK, reconnect, bounds, failed allocations, clear and outstanding SPUs passed.");
}
#endif

static double cpu_seconds(void)
{
    FILETIME created, exited, kernel, user;
    assert(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user));
    ULARGE_INTEGER k = {.LowPart = kernel.dwLowDateTime, .HighPart = kernel.dwHighDateTime};
    ULARGE_INTEGER u = {.LowPart = user.dwLowDateTime, .HighPart = user.dwHighDateTime};
    return (k.QuadPart + u.QuadPart) / 1e7;
}

static void benchmark(const char *mode, int count, int frames)
{
    assert(count >= 1 && count <= 8 && frames > 0);
    filter_t filters[8]; HANDLE pipes[8];
    memset(filters, 0, sizeof(filters));
    overlay_msg_v1 hdr = header(340, 292);
    uint8_t *pixels = malloc(hdr.payload_size);
    assert(pixels); memset(pixels, 193, hdr.payload_size);
    for (int i = 0; i < count; i++) {
        start(&filters[i], (unsigned)i); pipes[i] = connect_client(&filters[i]);
        send_frame(pipes[i], &hdr, pixels); barrier(&filters[i], pipes[i]);
        subpicture_Delete(Filter(&filters[i], 1));
    }
    picture_allocations = receive_allocations = 0; picture_bytes = 0;
    const double begin = cpu_seconds();
    uint64_t hash = 0;
    for (int tick = 0; tick < frames; tick++) {
        if (strcmp(mode, "quiet")) pixels[0] = (uint8_t)tick;
        for (int i = 0; i < count; i++) {
            send_frame(pipes[i], &hdr, pixels); barrier(&filters[i], pipes[i]);
            subpicture_Delete(Filter(&filters[i], tick + 2));
            if (tick % 31 == 0) hash = (hash ^ picture_hash(filters[i].p_sys->frame->picture, hdr.w, hdr.h) ^ (unsigned)i ^ (unsigned)tick) * UINT64_C(1099511628211);
        }
    }
    printf("{\"mode\":\"%s\",\"streams\":%d,\"frames_per_stream\":%d,\"cpu_seconds\":%.7f,"
           "\"picture_allocations\":%ld,\"picture_bytes\":%zu,\"receive_allocations\":%ld,\"sample_hash\":\"%016llx\"}\n",
           mode, count, frames, cpu_seconds() - begin, picture_allocations, picture_bytes, receive_allocations, (unsigned long long)hash);
    for (int i = 0; i < count; i++) { CloseHandle(pipes[i]); Close((vlc_object_t *)&filters[i]); }
    free(pixels);
}

int main(int argc, char **argv)
{
    if (argc == 5 && !strcmp(argv[1], "--benchmark")) { benchmark(argv[2], atoi(argv[3]), atoi(argv[4])); return 0; }
#ifndef BASELINE_RECEIVE
    regression();
#else
    (void)CountedReceive;
#endif
    return 0;
}
