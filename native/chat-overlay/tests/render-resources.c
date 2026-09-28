/* Real DirectWrite/GDI rendering, with a deterministic clock and no network. */
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#define INITGUID
#include <winsock2.h>
#include <windows.h>
#include <d2d1.h>
#include <stdbool.h>
#include <string.h>
#include "../protocol.h"

/* Optional shared counters for the application benchmark; regular correctness
 * tests leave this NULL. The controller's real pipe loop and pacing are used. */
typedef struct resource_pipe_metrics_t {
    volatile LONG64 bytes;
    volatile LONG64 frames;
} resource_pipe_metrics_t;
static resource_pipe_metrics_t *resource_pipe_metrics;
static BOOL resource_write_file(HANDLE file, LPCVOID data, DWORD size,
                                 LPDWORD written, LPOVERLAPPED overlapped)
{
    BOOL result = WriteFile(file, data, size, written, overlapped);
    if (result && resource_pipe_metrics) {
        InterlockedAdd64(&resource_pipe_metrics->bytes, *written);
        if (size == sizeof(overlay_msg_v1) && *written == size) {
            overlay_msg_v1 header;
            memcpy(&header, data, sizeof(header));
            if (header.magic == MYO_MAGIC && header.type == MYO_TYPE_FRAME)
                InterlockedIncrement64(&resource_pipe_metrics->frames);
        }
    }
    return result;
}

static ULONGLONG resource_clock_ms = 100000;
static bool resource_use_realtime = false;
static ULONGLONG resource_clock(void)
{
    return resource_use_realtime ? GetTickCount64() : resource_clock_ms;
}
static bool fail_next_surface = false;
static HBITMAP resource_create_dib(HDC dc, const BITMAPINFO *info, UINT usage,
                                   void **pixels, HANDLE section, DWORD offset)
{
    if (fail_next_surface) {
        fail_next_surface = false;
        *pixels = NULL;
        return NULL;
    }
    return CreateDIBSection(dc, info, usage, pixels, section, offset);
}
static bool fail_next_draw = false;
static void (*after_draw)(void) = NULL;
static HRESULT resource_end_draw(ID2D1RenderTarget *target, D2D1_TAG *one, D2D1_TAG *two)
{
    HRESULT result = ID2D1RenderTarget_EndDraw(target, one, two);
    if (after_draw) {
        void (*callback)(void) = after_draw;
        after_draw = NULL;
        callback();
    }
    if (fail_next_draw) {
        fail_next_draw = false;
        return E_FAIL;
    }
    return result;
}
#undef ID2D1RenderTarget_EndDraw
#define ID2D1RenderTarget_EndDraw resource_end_draw
#define CreateDIBSection resource_create_dib
#define GetTickCount64 resource_clock
#define main overlay_controller_main
#define WriteFile resource_write_file
#ifndef OVERLAY_RENDERER_SOURCE
#define OVERLAY_RENDERER_SOURCE "../vlc_chat_overlay.c"
#endif
#include OVERLAY_RENDERER_SOURCE
#undef main
#undef WriteFile
#undef GetTickCount64
#undef CreateDIBSection
#include <psapi.h>
#include <inttypes.h>

#define CHECK(condition) do { if (!(condition)) { \
    fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition); \
    exit(1); \
} } while (0)

static bool prepare_frame(renderer_t *r)
{
#ifdef RESOURCE_BASELINE
    render_chat(r);
    dib_to_rgba(r);
    return true;
#else
    return renderer_prepare_frame(r);
#endif
}

static void initialize(void)
{
    CHECK(SUCCEEDED(CoInitializeEx(NULL, COINIT_MULTITHREADED)));
    InitializeCriticalSection(&g_input_cs);
    queue_init(&g_queue);
    bttv_catalog_init();
    GdiplusStartupInput input = {0};
    input.GdiplusVersion = 1;
    CHECK(GdiplusStartup(&g_gdiplus_token, &input, NULL) == 0);
    g_gdiplus_ready = true;
    set_font_size(15);
    InterlockedExchange(&g_video_height, 1080);
    InterlockedExchange(&g_video_width, 1920);
}

static void cleanup(void)
{
    bttv_catalog_destroy();
    GdiplusShutdown(g_gdiplus_token);
    DeleteCriticalSection(&g_queue.cs);
    DeleteCriticalSection(&g_input_cs);
    CoUninitialize();
}

static void push_message(int sequence, bool animated)
{
    char user[64], text[512];
    snprintf(user, sizeof(user), "Viewer%02d", sequence % 37);
    snprintf(text, sizeof(text), "Message %d: readable live chat, wrapping and Unicode \xF0\x9F\x98\x80%s",
        sequence, animated ? " Pulse" : "");
    queue_push(&g_queue, user, text, false);
}

static void clear_messages(void)
{
    EnterCriticalSection(&g_queue.cs);
    g_queue.count = 0;
    g_queue.head = 0;
    LeaveCriticalSection(&g_queue.cs);
    InterlockedExchange(&g_scroll_offset, 0);
    signal_render();
}

/* Three real GIF frames (red/green/blue), with 100/70/90 ms delays. */
static const uint8_t animation_gif[] = {
    0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x02, 0x00, 0x02, 0x00, 0x81, 0x00, 0x00, 0xff, 0x00, 0x00,
    0x00, 0xff, 0x00, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00, 0x21, 0xf9, 0x04, 0x04, 0x0a, 0x00, 0x00,
    0x00, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, 0x00, 0x02, 0x04, 0x04, 0x41, 0x10,
    0x05, 0x00, 0x21, 0xf9, 0x04, 0x04, 0x07, 0x00, 0x00, 0x00, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x02,
    0x00, 0x02, 0x00, 0x00, 0x02, 0x04, 0x0c, 0xc3, 0x30, 0x05, 0x00, 0x21, 0xf9, 0x04, 0x04, 0x09,
    0x00, 0x00, 0x00, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x02, 0x00, 0x00, 0x02, 0x04, 0x14,
    0x45, 0x51, 0x05, 0x00, 0x3b,
};

static int install_emote(const char *code, bool animated)
{
    CHECK(emote_catalog_add_direct(code, "fixture.invalid", "/local.gif", 2, 2));
    int index = bttv_lookup(code);
    CHECK(index >= 0);
    HGLOBAL data = GlobalAlloc(GMEM_MOVEABLE, sizeof(animation_gif));
    CHECK(data != NULL);
    void *pixels = GlobalLock(data);
    CHECK(pixels != NULL);
    memcpy(pixels, animation_gif, sizeof(animation_gif));
    GlobalUnlock(data);
    bttv_emote_t *e = &g_bttv.items[index];
    CHECK(SUCCEEDED(CreateStreamOnHGlobal(data, TRUE, &e->image_stream)));
    CHECK(GdipLoadImageFromStream(e->image_stream, &e->image) == 0);
    e->image_w = 2;
    e->image_h = 2;
    e->tried_image = true;
    emote_init_animation_locked(e);
    CHECK(e->animated && e->frame_count == 3 && e->total_frame_delay_ms == 260);
    e->animated = animated;
    signal_render();
    return index;
}

#ifndef RESOURCE_BASELINE
static void test_obsolete_image_loads(void)
{
    CHECK(image_load_queue_init(&g_image_load_queue));
    const char *code = "ReplacementRace";
    CHECK(emote_catalog_add_direct(code, "fixture.invalid", "/first.png", 2, 2));
    int index = bttv_lookup(code);
    bttv_emote_t *entry = &g_bttv.items[index];
    image_load_request_t obsolete = { index, entry->source_generation };
    entry->loading_image = true;
    CHECK(image_load_queue_push(&g_image_load_queue, obsolete));
    CHECK(emote_catalog_add_direct(code, "fixture.invalid", "/second.png", 2, 2));
    /* Even returning to an identical URL must not admit the old completion. */
    CHECK(emote_catalog_add_direct(code, "fixture.invalid", "/first.png", 2, 2));
    entry->loading_image = true;
    image_load_request_t queued;
    CHECK(image_load_queue_pop(&g_image_load_queue, &queued, 0));
    CHECK(queued.generation == obsolete.generation);
    CHECK(!bttv_load_image_sync(queued));
    CHECK(entry->loading_image && !entry->tried_image);
    bttv_finish_image_load(obsolete, true);
    CHECK(entry->loading_image && !entry->tried_image);
    CHECK(!bttv_apply_static_fallback(obsolete, "/obsolete/static.png"));
    CHECK(strcmp(entry->path, "/first.png") == 0);

    HGLOBAL data = GlobalAlloc(GMEM_MOVEABLE, sizeof(animation_gif));
    CHECK(data != NULL);
    void *bytes = GlobalLock(data);
    CHECK(bytes != NULL);
    memcpy(bytes, animation_gif, sizeof(animation_gif));
    GlobalUnlock(data);
    IStream *stream = NULL;
    GpImage *image = NULL;
    CHECK(SUCCEEDED(CreateStreamOnHGlobal(data, TRUE, &stream)));
    CHECK(GdipLoadImageFromStream(stream, &image) == 0);
    stream->lpVtbl->AddRef(stream); /* Inspect the last reference after rejection. */
    CHECK(!bttv_commit_image_load(obsolete, image, stream, 2, 2));
    CHECK(stream->lpVtbl->Release(stream) == 0);
    CHECK(entry->image == NULL && entry->loading_image && !entry->tried_image);

    for (int i = 0; i < IMAGE_LOAD_QUEUE_CAP; i++)
        CHECK(image_load_queue_push(&g_image_load_queue, obsolete));
    CHECK(!image_load_queue_push(&g_image_load_queue, obsolete));
    bttv_finish_image_load(obsolete, false);
    CHECK(entry->loading_image && !entry->tried_image);
    entry->loading_image = false;
    CHECK(!bttv_ensure_image(index));
    CHECK(!entry->loading_image && !entry->tried_image); /* A full queue is retryable. */

    renderer_t renderer = {0};
    const uint8_t pixel[4] = { 0, 0, 255, 255 };
    const GpImage *reused_address = (GpImage *)(uintptr_t)1;
    renderer_store_emote_cache(&renderer, index, 1, 1, -1, reused_address, obsolete.generation, pixel, 4);
    CHECK(renderer_find_emote_cache(&renderer, index, 1, 1, -1, reused_address, obsolete.generation) != NULL);
    CHECK(renderer_find_emote_cache(&renderer, index, 1, 1, -1, reused_address, entry->source_generation) == NULL);
    for (int i = 0; i < EMOTE_RENDER_CACHE_CAP; i++) free(renderer.emote_render_cache[i].pixels);
    image_load_queue_destroy(&g_image_load_queue);
    puts("PASS obsolete emote queue, success, failure, fallback, rejection and rendered cache generations");
}
#endif

static void seed_catalog(int entries)
{
    CHECK(entries >= 0 && entries <= BTTV_MAX_EMOTES - 1);
    for (int i = 0; i < entries; i++) {
        char code[64];
        snprintf(code, sizeof(code), "CatalogEmote%05d", i);
        CHECK(emote_catalog_add_direct(code, "fixture.invalid", "/unused.png", 28, 28));
    }
}

static ULONG reference_count(IDWriteTextLayout *layout)
{
    ULONG count = IDWriteTextLayout_AddRef(layout);
    IDWriteTextLayout_Release(layout);
    return count - 1;
}

static void test_text_reference_lifetime(void)
{
    renderer_t r;
    CHECK(renderer_init(&r, 340, 292));
    CHECK(r.dwrite_ready);
    const wchar_t text[] = L"A cached width measurement";
    int width = measure_directwrite_text_width(&r, r.font_msg, text, 26);
    CHECK(width > 0);
    IDWriteTextLayout *layout = renderer_find_text_layout(&r, r.font_msg, text, 26, NULL);
    CHECK(layout != NULL);
    ULONG before = reference_count(layout);
    for (int i = 0; i < 1000; i++) {
        CHECK(measure_directwrite_text_width(&r, r.font_msg, text, 26) == width);
    }
    ULONG after = reference_count(layout);
    printf("Cached text references before=%lu after=%lu\n", (unsigned long)before, (unsigned long)after);
    CHECK(after == before);
    for (int i = 0; i < TEXT_LAYOUT_CACHE_CAP + 10; i++) {
        wchar_t different[64];
        swprintf(different, 64, L"Eviction test %d", i);
        CHECK(measure_directwrite_text_width(&r, r.font_msg, different, (int)wcslen(different)) > 0);
    }
    CHECK(reference_count(layout) == before - 1);
    renderer_destroy(&r);
    CHECK(reference_count(layout) == before - 1);
    IDWriteTextLayout_Release(layout);
    puts("PASS cached text measurements release their references, including eviction and teardown");
}

static void test_surface_capacity(void)
{
    renderer_t r;
    CHECK(renderer_init(&r, 340, 292));
    uint64_t visible_bytes = (uint64_t)r.width * r.height * 4;
    uint64_t capacity = (uint64_t)r.surface_width * r.surface_height * 4 + r.rgba_cap;
    printf("Chat surfaces visible=%" PRIu64 " capacity=%" PRIu64 "\n", visible_bytes, capacity);
    CHECK(capacity <= visible_bytes * 4);
    HFONT font = r.font_msg;
    CHECK(renderer_resize(&r, 341, 293));
    CHECK(r.font_msg == font);
    HBITMAP bitmap = r.dib_bitmap;
    for (int width = 342; width < 370; width++) {
        CHECK(renderer_resize(&r, width, 293));
        CHECK(r.dib_bitmap == bitmap);
    }
    CHECK(renderer_resize(&r, 810, 620));
    CHECK(r.font_msg == font);
    CHECK(r.surface_width >= r.width && r.surface_height >= r.height);
    CHECK(r.rgba_cap >= r.width * r.height * 4);
    CHECK(!renderer_resize(&r, INT_MAX, INT_MAX));
    CHECK(r.width == 810 && r.height == 620);
    renderer_destroy(&r);
    puts("PASS chat buffers start near the panel size and grow without recreating fonts");
}

static void compare_fresh(renderer_t *cached)
{
    renderer_t fresh;
    CHECK(renderer_init(&fresh, cached->width, cached->height));
    render_chat(&fresh);
    dib_to_rgba(&fresh);
    CHECK(memcmp(cached->rgba_out, fresh.rgba_out,
        (size_t)cached->width * cached->height * 4) == 0);
    renderer_destroy(&fresh);
}

static void test_static_frames(void)
{
    renderer_t r;
    CHECK(renderer_init(&r, 340, 292));
    clear_messages();
    for (int i = 0; i < 30; i++) push_message(i, false);
    CHECK(prepare_frame(&r));
    compare_fresh(&r);
    for (int i = 0; i < 25; i++) {
        resource_clock_ms += HEARTBEAT_MS;
        CHECK(!prepare_frame(&r));
    }
    push_message(31, false);
    CHECK(prepare_frame(&r));
    compare_fresh(&r);
    adjust_scroll_offset(1);
    CHECK(prepare_frame(&r));
    compare_fresh(&r);
    CHECK(!prepare_frame(&r));
    CHECK(renderer_resize(&r, 501, 399));
    CHECK(prepare_frame(&r));
    compare_fresh(&r);
    CHECK(!prepare_frame(&r));
    uint8_t *old_pixels = r.rgba_out;
    HBITMAP old_bitmap = r.dib_bitmap;
    fail_next_surface = true;
    CHECK(!renderer_resize(&r, 1800, 1000));
    CHECK(!fail_next_surface);
    CHECK(r.rgba_out == old_pixels && r.dib_bitmap == old_bitmap);
    CHECK(r.width == 501 && r.height == 399);
    CHECK(!prepare_frame(&r));
    compare_fresh(&r);
    renderer_destroy(&r);
    puts("PASS unchanged frames reuse their pixels; messages, scrolling and resize match fresh rendering");
}

static void message_during_draw(void) { push_message(9988, false); }

static void test_pending_events_and_retry(void)
{
    renderer_t r;
    CHECK(renderer_init(&r, 340, 292));
    clear_messages();
    push_message(1, false);
    CHECK(prepare_frame(&r));
    CHECK(!prepare_frame(&r));
    after_draw = message_during_draw;
    signal_render();
    CHECK(prepare_frame(&r));
    CHECK(after_draw == NULL);
    CHECK(prepare_frame(&r));
    compare_fresh(&r);
    CHECK(!prepare_frame(&r));
    fail_next_draw = true;
    signal_render();
    CHECK(prepare_frame(&r));
    CHECK(!fail_next_draw);
    CHECK(prepare_frame(&r));
    compare_fresh(&r);
    CHECK(!prepare_frame(&r));
    /* An empty frame must also retry a failed text draw. */
    clear_messages();
    fail_next_draw = true;
    CHECK(prepare_frame(&r));
    CHECK(!fail_next_draw);
    CHECK(prepare_frame(&r));
    compare_fresh(&r);
    CHECK(!prepare_frame(&r));
    renderer_destroy(&r);
    puts("PASS changes arriving during a draw remain pending and failed drawing retries on the next heartbeat");
}

static void test_animation_input_and_assets(void)
{
    clear_messages();
    int emote = install_emote("Pulse", true);
    queue_push(&g_queue, "Viewer", "Pulse", false);
    renderer_t r;
    CHECK(renderer_init(&r, 340, 292));
    CHECK(prepare_frame(&r));
    uint8_t *first = malloc((size_t)r.width * r.height * 4);
    CHECK(first != NULL);
    memcpy(first, r.rgba_out, (size_t)r.width * r.height * 4);
    resource_clock_ms += 100;
    CHECK(prepare_frame(&r));
    CHECK(g_bttv.items[emote].current_frame == 1);
    CHECK(memcmp(first, r.rgba_out, (size_t)r.width * r.height * 4) != 0);
    compare_fresh(&r);
    resource_clock_ms += 70;
    CHECK(prepare_frame(&r));
    CHECK(g_bttv.items[emote].current_frame == 2);
    compare_fresh(&r);
    resource_clock_ms += 90;
    CHECK(prepare_frame(&r));
    CHECK(g_bttv.items[emote].current_frame == 0);
    CHECK(memcmp(first, r.rgba_out, (size_t)r.width * r.height * 4) == 0);

    /* Animated history outside the displayed message tail must not keep a
     * static viewport repainting. Returning to it resumes at the right frame. */
    for (int i = 0; i < 40; i++) push_message(i, false);
    CHECK(prepare_frame(&r));
    CHECK(!prepare_frame(&r));
    set_scroll_offset(1000);
    CHECK(prepare_frame(&r));
    compare_fresh(&r);
    set_scroll_offset(0);

    clear_messages();
    queue_push(&g_queue, "Viewer", "Later", false);
    CHECK(prepare_frame(&r));
    memcpy(first, r.rgba_out, (size_t)r.width * r.height * 4);
    (void)install_emote("Later", false);
    CHECK(prepare_frame(&r));
    CHECK(memcmp(first, r.rgba_out, (size_t)r.width * r.height * 4) != 0);
    compare_fresh(&r);
    CHECK(!prepare_frame(&r));

    for (int provider = CHAT_PROVIDER_TWITCH; provider <= CHAT_PROVIDER_KICK; provider++) {
        g_chat_provider = provider;
        input_set_focused(true);
        input_append_utf8_sanitized("Text \xF0\x9F\x98\x80");
        resource_clock_ms = (resource_clock_ms / 1000 + 1) * 1000;
        CHECK(prepare_frame(&r));
        compare_fresh(&r);
        memcpy(first, r.rgba_out, (size_t)r.width * r.height * 4);
        resource_clock_ms += 500;
        CHECK(prepare_frame(&r));
        CHECK(memcmp(first, r.rgba_out, (size_t)r.width * r.height * 4) != 0);
        compare_fresh(&r);
        input_backspace();
        CHECK(prepare_frame(&r));
        compare_fresh(&r);
        char unsent[MAX_MSG_TEXT];
        CHECK(input_take_submit_text(unsent, sizeof(unsent)));
        input_set_focused(false);
        input_set_notice_ms(100, "A local test notice");
        CHECK(prepare_frame(&r));
        memcpy(first, r.rgba_out, (size_t)r.width * r.height * 4);
        resource_clock_ms += 101;
        CHECK(prepare_frame(&r));
        CHECK(memcmp(first, r.rgba_out, (size_t)r.width * r.height * 4) != 0);
        compare_fresh(&r);
        CHECK(!prepare_frame(&r));
        overlay_event_v1 hover = {MYO_MAGIC, MYO_VERSION, MYO_EVENT_CHAT_INPUT_HOVER, 1};
        handle_overlay_event(&hover);
        CHECK(prepare_frame(&r));
        compare_fresh(&r);
        hover.value = 0;
        handle_overlay_event(&hover);
        CHECK(prepare_frame(&r));
        CHECK(!prepare_frame(&r));
    }
    free(first);
    renderer_destroy(&r);
    puts("PASS real GIF timing, late assets, Twitch/Kick input, caret, notice expiry and hover preserve pixels");
}

static void test_catalog_index(void)
{
    char collisions[3][64];
    int found = 0;
    for (int i = 0; i < 2000000 && found < 3; i++) {
        char code[64];
        snprintf(code, sizeof(code), "collision-%d", i);
        if ((fnv1a_32(code) & (BTTV_MAX_EMOTES * 2 - 1)) == 17) {
            strcpy(collisions[found++], code);
        }
    }
    CHECK(found == 3);
    int expected[3];
    for (int i = 0; i < 3; i++) {
        expected[i] = g_bttv.count;
        CHECK(emote_catalog_add_direct(collisions[i], "fixture.invalid", "/original.png", 21, 22));
    }
    for (int i = 0; i < 3; i++) CHECK(bttv_lookup(collisions[i]) == expected[i]);
    int count = g_bttv.count;
    LONG generation = InterlockedCompareExchange(&g_render_generation, 0, 0);
    CHECK(emote_catalog_add_direct(collisions[1], "fixture.invalid", "/replacement.png", 31, 32));
    CHECK(InterlockedCompareExchange(&g_render_generation, 0, 0) != generation);
    generation = InterlockedCompareExchange(&g_render_generation, 0, 0);
    CHECK(emote_catalog_add_direct(collisions[1], "fixture.invalid", "/replacement.png", 31, 32));
    CHECK(InterlockedCompareExchange(&g_render_generation, 0, 0) == generation);
    CHECK(g_bttv.count == count && bttv_lookup(collisions[1]) == expected[1]);
    CHECK(g_bttv.items[expected[1]].api_w == 31);
    char local_file[IMAGE_PATH_MAX];
    CHECK(GetModuleFileNameA(NULL, local_file, sizeof(local_file)) > 0);
    CHECK(emote_catalog_add_file(collisions[1], local_file, 41, 42));
    CHECK(InterlockedCompareExchange(&g_render_generation, 0, 0) != generation);
    CHECK(g_bttv.count == count && bttv_lookup(collisions[1]) == expected[1]);
    CHECK(g_bttv.items[expected[1]].local_file);
    CHECK(emote_catalog_add_direct("CaseSensitive", "fixture.invalid", "/one.png", 1, 1));
    CHECK(emote_catalog_add_direct("casesensitive", "fixture.invalid", "/two.png", 2, 2));
    CHECK(bttv_lookup("CaseSensitive") != bttv_lookup("casesensitive"));
    CHECK(bttv_lookup("CASESENSITIVE") == -1);
    CHECK(bttv_lookup("") == -1);
    while (g_bttv.count < BTTV_MAX_EMOTES) {
        char code[64];
        snprintf(code, sizeof(code), "capacity-%d", g_bttv.count);
        int expected_index = g_bttv.count;
        CHECK(emote_catalog_add_direct(code, "fixture.invalid", "/unused.png", 1, 1));
        CHECK(bttv_lookup(code) == expected_index);
    }
    CHECK(!emote_catalog_add_direct("one-too-many", "fixture.invalid", "/unused.png", 1, 1));
    CHECK(bttv_lookup("one-too-many") == -1);
    CHECK(emote_catalog_add_direct(collisions[0], "fixture.invalid", "/at-capacity.png", 51, 52));
    for (int i = 0; i < 3; i++) CHECK(bttv_lookup(collisions[i]) == expected[i]);
    CHECK(g_bttv.items[expected[0]].api_w == 51);
    puts("PASS catalog collisions, case sensitivity, replacement, local files, stable indices and full capacity");
}

static HANDLE create_frame_pipe(void)
{
    char path[160];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\%s", g_pipe_name);
    HANDLE pipe = CreateNamedPipeA(path, PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_WAIT, 1, 0, 1024 * 1024, 0, NULL);
    CHECK(pipe != INVALID_HANDLE_VALUE);
    return pipe;
}

static void connect_frame_pipe(HANDLE pipe)
{
    OVERLAPPED io = {0};
    io.hEvent = CreateEventA(NULL, TRUE, FALSE, NULL);
    CHECK(io.hEvent != NULL);
    if (!ConnectNamedPipe(pipe, &io)) {
        DWORD error = GetLastError();
        CHECK(error == ERROR_PIPE_CONNECTED || error == ERROR_IO_PENDING);
        if (error == ERROR_IO_PENDING) {
            CHECK(WaitForSingleObject(io.hEvent, 5000) == WAIT_OBJECT_0);
            DWORD ignored;
            CHECK(GetOverlappedResult(pipe, &io, &ignored, FALSE));
        }
    }
    CloseHandle(io.hEvent);
}

static void read_frame_bytes(HANDLE pipe, void *buffer, DWORD bytes)
{
    uint8_t *next = buffer;
    OVERLAPPED io = {0};
    io.hEvent = CreateEventA(NULL, TRUE, FALSE, NULL);
    CHECK(io.hEvent != NULL);
    while (bytes > 0) {
        ResetEvent(io.hEvent);
        DWORD read = 0;
        if (!ReadFile(pipe, next, bytes, &read, &io)) {
            CHECK(GetLastError() == ERROR_IO_PENDING);
            CHECK(WaitForSingleObject(io.hEvent, 5000) == WAIT_OBJECT_0);
            CHECK(GetOverlappedResult(pipe, &io, &read, FALSE));
        }
        CHECK(read > 0 && read <= bytes);
        next += read;
        bytes -= read;
    }
    CloseHandle(io.hEvent);
}

static uint8_t *receive_frame(HANDLE pipe, int width, int height)
{
    for (int message = 0; message < 8; message++) {
        overlay_msg_v1 header;
        read_frame_bytes(pipe, &header, sizeof(header));
        CHECK(header.magic == MYO_MAGIC && header.version == MYO_VERSION);
        if (header.type != MYO_TYPE_FRAME) {
            CHECK(header.payload_size == 0);
            continue;
        }
        CHECK(header.w == (uint32_t)width && header.h == (uint32_t)height);
        CHECK(header.payload_size == (uint32_t)(width * height * 4));
        uint8_t *pixels = malloc(header.payload_size);
        CHECK(pixels != NULL);
        read_frame_bytes(pipe, pixels, header.payload_size);
        return pixels;
    }
    CHECK(false);
    return NULL;
}

static void test_pipe_reconnection(void)
{
    clear_messages();
    queue_push(&g_queue, "Viewer", "A persistent static frame", false);
    g_width = 340;
    g_height = 292;
    set_target_chat_size(g_width, g_height);
    snprintf(g_pipe_name, sizeof(g_pipe_name), "resource-pipe-%lu", (unsigned long)GetCurrentProcessId());
    g_render_signal = CreateEventA(NULL, FALSE, FALSE, NULL);
    CHECK(g_render_signal != NULL);
    HANDLE pipe = create_frame_pipe();
    resource_use_realtime = true;
    HANDLE worker = CreateThread(NULL, 0, render_thread, NULL, 0, NULL);
    CHECK(worker != NULL);
    connect_frame_pipe(pipe);
    uint8_t *first = receive_frame(pipe, g_width, g_height);
    uint8_t *heartbeat = receive_frame(pipe, g_width, g_height);
    const size_t bytes = (size_t)g_width * g_height * 4;
    CHECK(memcmp(first, heartbeat, bytes) == 0);
    free(heartbeat);
    CloseHandle(pipe);
    pipe = create_frame_pipe();
    connect_frame_pipe(pipe);
    uint8_t *reconnected = receive_frame(pipe, g_width, g_height);
    CHECK(memcmp(first, reconnected, bytes) == 0);
    free(reconnected);
    queue_push(&g_queue, "Viewer", "An update after reconnect", false);
    renderer_t expected;
    CHECK(renderer_init(&expected, g_width, g_height));
    render_chat(&expected);
    dib_to_rgba(&expected);
    bool received_update = false;
    for (int frame = 0; frame < 8 && !received_update; frame++) {
        uint8_t *actual = receive_frame(pipe, g_width, g_height);
        received_update = memcmp(actual, expected.rgba_out, bytes) == 0;
        free(actual);
    }
    CHECK(received_update);
    renderer_destroy(&expected);
    free(first);
    InterlockedExchange(&g_stop, 1);
    signal_render();
    CloseHandle(pipe);
    CHECK(WaitForSingleObject(worker, 5000) == WAIT_OBJECT_0);
    DWORD exit_code;
    CHECK(GetExitCodeThread(worker, &exit_code) && exit_code == 0);
    CloseHandle(worker);
    CloseHandle(g_render_signal);
    g_render_signal = NULL;
    resource_use_realtime = false;
    InterlockedExchange(&g_stop, 0);
    puts("PASS the production render thread keeps heartbeat delivery, reconnects with cached pixels and publishes new chat");
}

static void test_resize_scale_and_cleanup(void)
{
    clear_messages();
    for (int i = 0; i < 30; i++) push_message(i, false);
    DWORD handles_before = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    renderer_t r;
    CHECK(renderer_init(&r, 340, 292));
    const int cases[][3] = {
        {1080, 341, 293}, {1080, 820, 601}, {1080, 221, 121},
        {720, 225, 195}, {2160, 1500, 800}, {2160, 680, 584}, {1080, 340, 292}
    };
    for (int iteration = 0; iteration < 3; iteration++) {
        for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); i++) {
            render_scale_height = 0;
            overlay_event_v1 size = {MYO_MAGIC, MYO_VERSION, MYO_EVENT_VIDEO_SIZE,
                MYO_PACK_SIZE_EVENT(cases[i][0] * 16 / 9, cases[i][0])};
            overlay_event_v1 scale = {MYO_MAGIC, MYO_VERSION, MYO_EVENT_UI_SCALE, cases[i][0]};
            handle_overlay_event(&size);
            handle_overlay_event(&scale);
            CHECK(renderer_update_scale_and_size(&r, cases[i][1], cases[i][2]));
            CHECK(prepare_frame(&r));
            compare_fresh(&r);
            CHECK(!prepare_frame(&r));
        }
    }
    renderer_destroy(&r);
    GdiFlush();
    DWORD handles_after = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    printf("GDI handles before=%lu after=%lu\n", (unsigned long)handles_before, (unsigned long)handles_after);
    CHECK(handles_after == handles_before);
    puts("PASS growth, shrink, odd pitches and source-scale changes match fresh pixels and release every GDI handle");
}

static double process_cpu_seconds(void)
{
    FILETIME created, exited, kernel, user;
    CHECK(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user));
    ULARGE_INTEGER k = {.LowPart = kernel.dwLowDateTime, .HighPart = kernel.dwHighDateTime};
    ULARGE_INTEGER u = {.LowPart = user.dwLowDateTime, .HighPart = user.dwHighDateTime};
    return (double)(k.QuadPart + u.QuadPart) / 10000000.0;
}

static PROCESS_MEMORY_COUNTERS_EX memory_counters(void)
{
    PROCESS_MEMORY_COUNTERS_EX counters = {0};
    counters.cb = sizeof(counters);
    CHECK(GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS *)&counters, sizeof(counters)));
    return counters;
}

static uint64_t frame_hash(const renderer_t *r, uint64_t hash)
{
    size_t bytes = (size_t)r->width * r->height * 4;
    for (size_t i = 0; i < bytes; i++) hash = (hash ^ r->rgba_out[i]) * UINT64_C(1099511628211);
    return hash;
}

static void benchmark(const char *mode, int frames, int catalog_entries)
{
    CHECK(frames >= 1 && frames <= 100000);
    bool busy = strcmp(mode, "busy") == 0;
    bool animated = strcmp(mode, "animated") == 0;
    CHECK(busy || animated || strcmp(mode, "quiet") == 0);
    seed_catalog(catalog_entries);
    if (animated) (void)install_emote("Pulse", true);
    const int count = 4;
    renderer_t *renderers = calloc(count, sizeof(*renderers));
    CHECK(renderers != NULL);
    for (int i = 0; i < 100; i++) push_message(i, animated);
    uint64_t capacity = 0;
    for (int i = 0; i < count; i++) {
        CHECK(renderer_init(&renderers[i], 340, 292));
        capacity += (uint64_t)renderers[i].surface_width * renderers[i].surface_height * 4 + renderers[i].rgba_cap;
        for (int warmup = 0; warmup < 10; warmup++) (void)prepare_frame(&renderers[i]);
    }
    PROCESS_MEMORY_COUNTERS_EX before = memory_counters();
    LARGE_INTEGER frequency, start, finish;
    CHECK(QueryPerformanceFrequency(&frequency));
    CHECK(QueryPerformanceCounter(&start));
    double cpu_start = process_cpu_seconds();
    int rendered = 0;
    uint64_t hash = UINT64_C(14695981039346656037);
    for (int frame = 0; frame < frames; frame++) {
        resource_clock_ms += HEARTBEAT_MS;
        if (busy) push_message(100 + frame, false);
        for (int i = 0; i < count; i++) {
            if (prepare_frame(&renderers[i])) rendered++;
            if (frame % 80 == 0 || frame == frames - 1) hash = frame_hash(&renderers[i], hash);
        }
    }
    double cpu = process_cpu_seconds() - cpu_start;
    CHECK(QueryPerformanceCounter(&finish));
    PROCESS_MEMORY_COUNTERS_EX after = memory_counters();
    printf("{\"mode\":\"%s\",\"catalog_entries\":%d,\"contexts\":%d,\"frames\":%d,\"rendered\":%d,"
        "\"cpu_seconds\":%.6f,\"wall_seconds\":%.6f,\"surface_capacity_bytes\":%" PRIu64 ","
        "\"private_before\":%zu,\"private_after\":%zu,\"working_set_after\":%zu,\"hash\":\"%016" PRIx64 "\"}\n",
        mode, catalog_entries, count, frames * count, rendered, cpu, (double)(finish.QuadPart - start.QuadPart) / frequency.QuadPart,
        capacity, (size_t)before.PrivateUsage, (size_t)after.PrivateUsage, (size_t)after.WorkingSetSize, hash);
    for (int i = 0; i < count; i++) renderer_destroy(&renderers[i]);
    free(renderers);
}

static void stream_pipe(const char *mode, const char *pipe_name,
                         const char *stop_name, const char *metrics_name)
{
    bool busy = strcmp(mode, "busy") == 0;
    bool animated = strcmp(mode, "animated") == 0;
    CHECK(busy || animated || strcmp(mode, "quiet") == 0);
    HANDLE stop = OpenEventA(SYNCHRONIZE, FALSE, stop_name);
    HANDLE mapping = OpenFileMappingA(FILE_MAP_WRITE, FALSE, metrics_name);
    CHECK(stop && mapping);
    resource_pipe_metrics = MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, sizeof(*resource_pipe_metrics));
    CHECK(resource_pipe_metrics);
    seed_catalog(4000);
    if (animated) (void)install_emote("Pulse", true);
    for (int i = 0; i < 100; i++) push_message(i, animated);
    g_width = 340; g_height = 292;
    g_init_x = 32; g_init_y = 32;
    set_target_chat_size(g_width, g_height);
    snprintf(g_pipe_name, sizeof(g_pipe_name), "%s", pipe_name);
    g_render_signal = CreateEventA(NULL, FALSE, FALSE, NULL);
    CHECK(g_render_signal);
    resource_use_realtime = true;
    HANDLE worker = CreateThread(NULL, 0, render_thread, NULL, 0, NULL);
    CHECK(worker);
    int sequence = 100;
    while (WaitForSingleObject(stop, HEARTBEAT_MS) == WAIT_TIMEOUT) {
        if (busy) push_message(sequence++, false);
        CHECK(WaitForSingleObject(worker, 0) == WAIT_TIMEOUT);
    }
    InterlockedExchange(&g_stop, 1);
    signal_render();
    CHECK(WaitForSingleObject(worker, 5000) == WAIT_OBJECT_0);
    CloseHandle(worker); CloseHandle(g_render_signal); CloseHandle(stop);
    UnmapViewOfFile(resource_pipe_metrics); CloseHandle(mapping);
    resource_pipe_metrics = NULL;
}

int main(int argc, char **argv)
{
    initialize();
    if (argc == 6 && strcmp(argv[1], "--stream-pipe") == 0) {
        stream_pipe(argv[2], argv[3], argv[4], argv[5]);
    } else if ((argc == 4 || argc == 5) && strcmp(argv[1], "--benchmark") == 0) {
        benchmark(argv[2], atoi(argv[3]), argc == 5 ? atoi(argv[4]) : 4000);
    } else if (argc == 2 && strcmp(argv[1], "--references") == 0) {
        test_text_reference_lifetime();
    } else if (argc == 2 && strcmp(argv[1], "--capacity") == 0) {
        test_surface_capacity();
    } else if (argc == 2 && strcmp(argv[1], "--pipe") == 0) {
        test_pipe_reconnection();
    } else {
        test_text_reference_lifetime();
        test_surface_capacity();
        test_static_frames();
        test_pending_events_and_retry();
        test_animation_input_and_assets();
        test_pipe_reconnection();
        test_resize_scale_and_cleanup();
#ifndef RESOURCE_BASELINE
        test_obsolete_image_loads();
#endif
        test_catalog_index();
    }
    cleanup();
    return 0;
}
