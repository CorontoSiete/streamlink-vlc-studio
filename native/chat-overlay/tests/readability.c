/* Deterministic native renderer coverage: no chat connection or keyboard hook. */
#define main overlay_controller_main
#include "../vlc_chat_overlay.c"
#undef main
#include <assert.h>

/* Exercise the real reader/validation path. Calling handle_overlay_event or
 * changing g_ui_scale_height directly misses rejected wire events. */
static void send_scale_event(int scale)
{
    char path[160];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\%s_events", g_pipe_name);
    HANDLE pipe = INVALID_HANDLE_VALUE;
    const ULONGLONG deadline = GetTickCount64() + 2000;
    while (pipe == INVALID_HANDLE_VALUE && GetTickCount64() < deadline) {
        pipe = CreateFileA(path, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (pipe == INVALID_HANDLE_VALUE) Sleep(10);
    }
    assert(pipe != INVALID_HANDLE_VALUE);
    overlay_event_v1 events[] = {
        {MYO_MAGIC, MYO_VERSION, MYO_EVENT_VIDEO_SIZE, MYO_PACK_SIZE_EVENT(scale * 16 / 9, scale)},
        {MYO_MAGIC, MYO_VERSION, MYO_EVENT_UI_SCALE, scale}
    };
    DWORD written = 0;
    assert(WriteFile(pipe, events, sizeof(events), &written, NULL));
    assert(written == sizeof(events));
    CloseHandle(pipe);
    while (InterlockedCompareExchange(&g_ui_scale_height, 0, 0) != scale &&
           GetTickCount64() < deadline) Sleep(10);
    if (InterlockedCompareExchange(&g_ui_scale_height, 0, 0) != scale) {
        fprintf(stderr, "FAIL scale event %d was not applied by the controller pipe reader\n", scale);
        exit(1);
    }
}

static void test_text_alpha(renderer_t *r)
{
    assert(r->dwrite_ready);
    clear_frame(r);
    assert(draw_directwrite_text(r, r->font_msg, L"Readable edges", 14,
                                10, 10, r->width - 20, r->height - 20, RGB(255,255,255)));
    dib_to_rgba(r);
    int antialiased = 0;
    for (int y = 0; y < r->height; y++) {
        for (int x = 0; x < r->width; x++) {
            const uint8_t *p = r->rgba_out + ((size_t)y * r->width + x) * 4;
            if (p[3] > 0 && p[3] < 255) {
                antialiased++;
                if (p[0] != 255 || p[1] != 255 || p[2] != 255) {
                    fprintf(stderr, "FAIL white glyph edge is premultiplied twice: RGBA=%u,%u,%u,%u\n",
                            p[0], p[1], p[2], p[3]);
                    exit(1);
                }
            }
        }
    }
    assert(antialiased > 20);

    /* The shared DIB also contains translucent UI and premultiplied emotes.
     * Check the outgoing straight colors and their composition on both dark
     * and bright video, including source pitch after a width-only resize. */
    clear_frame(r);
    fill_bgra_rect(r, 0, 0, 1, 1, RGB(200,100,50), 128);
    const uint8_t emote[] = {25, 50, 100, 128};
    blend_bttv_emote_pixels(r, emote, 1, 0, 1, 1);
    fill_bgra_rect(r, 0, 1, 1, 1, RGB(255,255,255), 255);
    dib_to_rgba(r);
    for (int x = 0; x < 2; x++) {
        const uint8_t *p = r->rgba_out + x * 4;
        assert(p[0] == 199 && p[1] == 100 && p[2] == 50 && p[3] == 128);
        for (int background = 0; background <= 255; background += 255) {
            const int composed = (p[0] * p[3] + background * (255 - p[3]) + 127) / 255;
            const int expected = 100 + (background * 127 + 127) / 255;
            assert(abs(composed - expected) <= 1);
        }
    }
    assert(r->rgba_out[8] == 0 && r->rgba_out[11] == 0);
    assert(r->rgba_out[r->width * 4] == 255);

    /* The fallback must use grayscale coverage too. LCD subpixel colors do not
     * survive video scaling and are not valid against a transparent surface. */
    clear_frame(r);
    HFONT previous = (HFONT)SelectObject(r->mem_dc, r->font_msg);
    SetTextColor(r->mem_dc, RGB(255,255,255));
    TextOutW(r->mem_dc, 10, 10, L"Readable", 8);
    SelectObject(r->mem_dc, previous);
    dib_to_rgba(r);
    int fallback_pixels = 0;
    for (int i = 0; i < r->width * r->height; i++) {
        const uint8_t *p = r->rgba_out + i * 4;
        if (!p[3]) continue;
        fallback_pixels++;
        assert(p[0] == 255 && p[1] == 255 && p[2] == 255);
    }
    assert(fallback_pixels > 20);
}

static HANDLE shutdown_worker_started;
static HANDLE shutdown_worker_release;

static DWORD WINAPI shutdown_test_worker(LPVOID ignored)
{
    (void)ignored;
    EnterCriticalSection(&g_input_cs);
    SetEvent(shutdown_worker_started);
    WaitForSingleObject(shutdown_worker_release, INFINITE);
    LeaveCriticalSection(&g_input_cs);
    return 0;
}

static int shutdown_test_child(bool blocked)
{
    InitializeCriticalSection(&g_input_cs);
    shutdown_worker_started = CreateEventA(NULL, TRUE, FALSE, NULL);
    shutdown_worker_release = CreateEventA(NULL, TRUE, FALSE, NULL);
    HANDLE workers[] = {NULL, CreateThread(NULL, 0, shutdown_test_worker, NULL, 0, NULL), NULL};
    assert(workers[1]);
    assert(WaitForSingleObject(shutdown_worker_started, 2000) == WAIT_OBJECT_0);
    if (!blocked) SetEvent(shutdown_worker_release);
    finish_workers(workers, 3, 1000);
    /* A timed-out worker still owns this lock. Only the completed case may
     * reach cleanup; the blocked child must exit inside finish_workers. */
    assert(!blocked);
    EnterCriticalSection(&g_input_cs);
    LeaveCriticalSection(&g_input_cs);
    DeleteCriticalSection(&g_input_cs);
    CloseHandle(shutdown_worker_started);
    CloseHandle(shutdown_worker_release);
    return 7;
}

static void test_worker_shutdown(void)
{
    char executable[MAX_PATH];
    assert(GetModuleFileNameA(NULL, executable, sizeof(executable)) > 0);
    for (int blocked = 0; blocked <= 1; blocked++) {
        char command[MAX_PATH + 64];
        snprintf(command, sizeof(command), "\"%s\" --shutdown-%s", executable, blocked ? "blocked" : "complete");
        STARTUPINFOA startup = {0};
        PROCESS_INFORMATION process = {0};
        startup.cb = sizeof(startup);
        assert(CreateProcessA(executable, command, NULL, NULL, FALSE, CREATE_NO_WINDOW,
            NULL, NULL, &startup, &process));
        DWORD wait = WaitForSingleObject(process.hProcess, 5000);
        if (wait != WAIT_OBJECT_0) TerminateProcess(process.hProcess, 99);
        assert(wait == WAIT_OBJECT_0);
        DWORD code;
        assert(GetExitCodeProcess(process.hProcess, &code));
        assert(code == (blocked ? 0u : 7u));
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    }
    puts("PASS native overlay joins completed workers and exits safely with a blocked lock owner");
}

int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--shutdown-blocked") == 0) return shutdown_test_child(true);
    if (argc == 2 && strcmp(argv[1], "--shutdown-complete") == 0) return shutdown_test_child(false);
    test_worker_shutdown();
    CoInitializeEx(NULL, COINIT_MULTITHREADED);
    InitializeCriticalSection(&g_input_cs);
    queue_init(&g_queue);
    bttv_catalog_init();
    g_render_signal = CreateEventA(NULL, FALSE, FALSE, NULL);
    snprintf(g_pipe_name, sizeof(g_pipe_name), "readability-%lu", (unsigned long)GetCurrentProcessId());
    HANDLE events = CreateThread(NULL, 0, event_thread, NULL, 0, NULL);
    assert(events);
    set_font_size(15);
    InterlockedExchange(&g_video_width, 1920);
    InterlockedExchange(&g_video_height, 1080);
    queue_push(&g_queue, "Viewer", "Small text should stay readable", false);
    queue_push(&g_queue, "Viewer", "Wrapping stays aligned when resized", false);

    renderer_t renderer;
    assert(renderer_init(&renderer, 340, 292));
    test_text_alpha(&renderer);
    /* Only decoded-source changes change the render scale. Repeated source
     * announcements after window resizing must preserve the cached fonts. */
    const int scales[] = {1080, 1080, 720, 2160, 2160, 1080};
    for (size_t i = 0; i < sizeof(scales)/sizeof(scales[0]); i++) {
        send_scale_event(scales[i]);
        int width, height;
        source_chat_size_from_reference(340, 292, &width, &height);
        HFONT previous_font = renderer.font_msg;
        assert(renderer_update_scale_and_size(&renderer, width, height));
        if (i > 0 && scales[i] == scales[i - 1]) assert(renderer.font_msg == previous_font);
        LOGFONTW font;
        assert(GetObjectW(renderer.font_msg, sizeof(font), &font));
        assert(-font.lfHeight == scale_reference_px(15));
        assert(renderer.width <= scales[i] * 16 / 9 && renderer.height <= scales[i]);
        render_chat(&renderer);
        dib_to_rgba(&renderer);
        if (argc > 1) {
            char path[MAX_PATH];
            snprintf(path, sizeof(path), "%s/native-%d.rgba", argv[1], scales[i]);
            FILE *out = fopen(path, "wb");
            assert(out);
            overlay_msg_v1 header = {0};
            header.magic = MYO_MAGIC; header.version = MYO_VERSION;
            header.type = MYO_TYPE_FRAME; header.alpha = 255;
            header.w = renderer.width; header.h = renderer.height;
            header.payload_size = header.w * header.h * 4;
            assert(fwrite(&header, sizeof(header), 1, out) == 1);
            assert(fwrite(renderer.rgba_out, header.payload_size, 1, out) == 1);
            fclose(out);
        }
    }
    HFONT same_font = renderer.font_msg;
    assert(renderer_update_scale_and_size(&renderer, 800, 600));
    assert(renderer.font_msg == same_font); /* panel resize preserves font caches */
    test_text_alpha(&renderer);
    renderer_destroy(&renderer);

    /* A large selected font on an 8K source needs >64 source-pixel glyphs.
     * The old fixed-height clip cut these in half even after font recreation. */
    set_font_size(36);
    InterlockedExchange(&g_ui_scale_height, 4320);
    assert(renderer_init(&renderer, 1500, 900));
    clear_frame(&renderer);
    draw_text_span_shadowed(&renderer, renderer.font_msg, L"Agjp", 4,
        50, 30, 1400, kWhiteColor, NULL);
    dib_to_rgba(&renderer);
    int glyph_rows = 0;
    for (int y = 0; y < renderer.height; y++) {
        int white = 0;
        for (int x = 0; x < renderer.width; x++) {
            const uint8_t *pixel = renderer.rgba_out + (y * renderer.width + x) * 4;
            if (pixel[0] > 205 && pixel[1] > 205 && pixel[2] > 205) white++;
        }
        if (white >= 3) glyph_rows++;
    }
    assert(glyph_rows > 90);
    renderer_destroy(&renderer);
    InterlockedExchange(&g_stop, 1);
    wake_event_thread();
    assert(WaitForSingleObject(events, 2000) == WAIT_OBJECT_0);
    CloseHandle(events);
    CloseHandle(g_render_signal);
    DeleteCriticalSection(&g_queue.cs);
    DeleteCriticalSection(&g_input_cs);
    DeleteCriticalSection(&g_bttv.cs);
    CoUninitialize();
    puts("PASS native overlay source scale through the pipe, stable cached fonts, source resolution changes, clipping, straight-alpha text/emotes/UI, and grayscale fallback");
    return 0;
}
