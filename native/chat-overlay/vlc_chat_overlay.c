/*
 * vlc_chat_overlay.c -- native C controller for the myoverlay VLC plugin.
 *
 * Connects anonymously to Twitch IRC or Kick's Pusher WebSocket, parses chat
 * messages into a small message ring, renders the last N messages to a 32-bit
 * RGBA bitmap with GDI, and pushes that bitmap to the VLC plugin through a
 * named pipe (see protocol.h). Replaces the PowerShell version used by the
 * orchestrator.
 *
 * Threads (all daemonic; main returns when g_stop is set):
 *   CHAT     -- blocks on tls_recv; reconnects with backoff on failure;
 *               pushes chat_msg_t into g_queue and signals g_render_signal.
 *   ASSETS   -- loads optional Twitch emote/badge catalogs without delaying
 *               pipe, render, or IRC startup.
 *   RENDER   -- waits on g_render_signal (or 80 ms heartbeat); composes
 *               a fresh frame from g_queue's tail; sends it through the
 *               pipe; reconnects pipe on EOF.
 *   WATCH    -- polls g_owner_pid every ~250 ms; exits when parent dies.
 *
 * CLI (matches the orchestrator's PowerShell overlay so it's a drop-in):
 *   --channel <name>        channel to join (required)
 *   --provider <name>       twitch or kick (default twitch)
 *   --kick-chatroom-id N    Kick chatroom id when provider is kick
 *   --kick-broadcaster-user-id N
 *                           Kick broadcaster user id for user-mode sends
 *   --pipe-name <name>      named-pipe name (default vlc_overlay)
 *   --width N --height N
 *   --x N --y N             initial overlay position (plugin owns the real x/y)
 *   --max-messages N
 *   --font-size N           message text and badge size in 1080p reference pixels
 *   --owner-process-id N    parent PID; we exit when it does
 *   --vlc-process-id N      VLC PID; reserved for future use (ignored today)
 *   --position-state-path P plugin/controller state path used to avoid
 *                           clobbering a saved per-channel drag position
 *   --twitch-badge-manifest PATH
 *                           app-bundled Twitch badge manifest fallback
 *   --twitch-room-id N      Twitch room/user id for early channel badge preload
 *   --kick-badge-manifest PATH
 *                           app-bundled Kick badge manifest fallback
 */

#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#define INITGUID
#include <winsock2.h>
#include <windows.h>
#include <wincrypt.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <stdbool.h>
#include <time.h>
#include <ctype.h>
#include <limits.h>
#include <wchar.h>
#include <objidl.h>
#include <winhttp.h>
#include <d2d1.h>
#include <dwrite.h>
#include <gdiplus/gdiplus.h>

#include "protocol.h"
#include "tls.h"

/* ===== Configuration ==================================================== */

#define MAX_MSG_TEXT      512
#define MAX_USERNAME      64
#define MAX_CHANNEL_DISPLAY 64
#define MAX_OAUTH_TOKEN   2048
#define MAX_MSG_EMOTES    64
#define MAX_MSG_BADGES    8
#define MAX_BADGE_ID      128
#define MAX_BADGE_VERSION 32
#define MAX_BADGE_LABEL   16
#define QUEUE_CAP         256
#define IRC_LINE_BUF      8192
#define HEARTBEAT_MS      80
#define RENDER_MIN_INTERVAL_MS 16
#define RECONNECT_MS      2500
#define WATCH_POLL_MS     250
#define SCROLL_LINES_PER_NOTCH 3
#define FONT_SIZE_PX      15
#define SYSTEM_FONT_SIZE  13
#define MIN_FONT_SIZE_PX  8
#define MAX_FONT_SIZE_PX  36
#define PLUGIN_POSITION_FILE_REL "\\vlc-overlay\\position.txt"

#define BTTV_MAX_EMOTES   32768
#define BTTV_CODE_MAX     192
#define BTTV_ID_MAX       80
#define BTTV_TYPE_MAX     8
#define IMAGE_PATH_MAX    520
#define HTTP_MAX_JSON     (4u * 1024u * 1024u)
#define HTTP_MAX_IMAGE    (2u * 1024u * 1024u)
#define EMOTE_RENDER_H    24
#define EMOTE_MAX_W       96
#define MAX_LAYOUT_RUNS   192
#define LINE_GAP_PX       2
#define CHAT_INPUT_H      30
#define CHAT_INPUT_GAP    6
#define CHAT_INPUT_MARGIN 8
#define CHAT_MIN_W        220
#define CHAT_MIN_H        120
#define CHAT_MAX_W        1920
#define CHAT_MAX_H        1080
#define REFERENCE_VIDEO_H 1080
#define WS_MAX_PAYLOAD    65536
#define KICK_BADGE_VERSION_CAP 64
#define TWITCH_BADGE_VERSION_CAP 64
#define TEXT_BATCH_BRUSH_CAP 16
#define TEXT_LAYOUT_CACHE_CAP 256
#define EMOTE_RENDER_CACHE_CAP 128

enum {
    CHAT_PROVIDER_TWITCH = 0,
    CHAT_PROVIDER_KICK = 1
};

/* ===== Globals ========================================================== */

static volatile LONG g_stop = 0;
static volatile LONG g_scroll_offset = 0;
static volatile LONG g_scroll_max = 0;
static volatile LONG g_scroll_visible = 0;
static volatile LONG g_scroll_total = 0;
static volatile LONG g_target_size = 0;
static volatile LONG g_render_generation = 1;
static volatile LONG g_video_width = 1920;
static volatile LONG g_video_height = REFERENCE_VIDEO_H;
static volatile LONG g_ui_scale_height = 0;
/* Only the render thread reads this snapshot; event callbacks publish atomically. */
static int render_scale_height = 0;
static volatile LONG render_thread_id = 0;
static volatile LONG g_twitch_assets_async = 0;
static HANDLE        g_render_signal = NULL;
static int           g_font_size_px = FONT_SIZE_PX;
static int           g_system_font_size_px = SYSTEM_FONT_SIZE;

/* A render request is a state change, not a frame queue.  The generation
 * lets the render thread collapse a burst of scroll/resize/input events to
 * the newest state while still noticing an event that arrived mid-render. */
static void signal_render(void)
{
    InterlockedIncrement(&g_render_generation);
    if (g_render_signal) SetEvent(g_render_signal);
}

static CRITICAL_SECTION g_input_cs;
static char             g_input_text[MAX_MSG_TEXT] = {0};
static bool             g_input_focused = false;
static volatile LONG    g_input_hovered = 0;
static char             g_input_notice[160] = {0};
static ULONGLONG        g_input_notice_until_ms = 0;
static HWND             g_input_focus_hwnd = NULL;
static DWORD            g_input_focus_pid = 0;
static DWORD            g_keyboard_thread_id = 0;
static HHOOK            g_keyboard_hook = NULL;

static CRITICAL_SECTION g_irc_send_cs;
static tls_conn_t      *g_irc_send_conn = NULL;
static bool             g_irc_send_ready = false;
static CRITICAL_SECTION g_sent_echo_cs;
static char             g_last_sent_text[MAX_MSG_TEXT] = {0};
static ULONGLONG        g_last_sent_ms = 0;

typedef struct {
    int  start_byte;
    int  end_byte;   /* exclusive */
    char code[BTTV_CODE_MAX];
} chat_emote_t;

typedef struct {
    char id[MAX_BADGE_ID];
    char version[MAX_BADGE_VERSION];
    char label[MAX_BADGE_LABEL];
    char image_code[BTTV_CODE_MAX];
    bool image_only;
} chat_badge_t;

typedef struct {
    char user[MAX_USERNAME];
    char text[MAX_MSG_TEXT];
    bool is_system;
    int  badge_count;
    chat_badge_t badges[MAX_MSG_BADGES];
    int  emote_count;
    chat_emote_t emotes[MAX_MSG_EMOTES];
    ULONGLONG created_ms;
} chat_msg_t;

typedef struct {
    CRITICAL_SECTION cs;
    chat_msg_t       buf[QUEUE_CAP];
    int              head;        /* index of oldest entry */
    int              count;
} msg_queue_t;

static msg_queue_t   g_queue;

static char          g_channel[64]              = {0};
static char          g_channel_display_name[MAX_CHANNEL_DISPLAY] = {0};
static bool          g_channel_display_name_explicit = false;
static int           g_chat_provider            = CHAT_PROVIDER_TWITCH;
static char          g_kick_chatroom_id[64]     = {0};
static char          g_kick_broadcaster_user_id[64] = {0};
static char          g_kick_chat_token[MAX_OAUTH_TOKEN] = {0};
static char          g_kick_chat_token_file[MAX_PATH] = {0};
static bool          g_kick_chat_auth_ready      = false;
static bool          g_kick_send_as_bot          = false;
static int           g_kick_subscriber_badge_versions[KICK_BADGE_VERSION_CAP] = {0};
static int           g_kick_subscriber_badge_version_count = 0;
static int           g_twitch_subscriber_badge_versions[TWITCH_BADGE_VERSION_CAP] = {0};
static int           g_twitch_subscriber_badge_version_count = 0;
static char          g_twitch_client_id[128]     = {0};
static char          g_twitch_room_id[64]        = {0};
static char          g_twitch_chat_login[MAX_USERNAME] = {0};
static char          g_twitch_chat_oauth[MAX_OAUTH_TOKEN] = {0};
static char          g_twitch_chat_token_file[MAX_PATH] = {0};
static char          g_twitch_badge_manifest_path[IMAGE_PATH_MAX] = {0};
static char          g_kick_badge_manifest_path[IMAGE_PATH_MAX] = {0};
static bool          g_twitch_chat_auth_ready    = false;
static char          g_pipe_name[128]           = "vlc_overlay";
static int           g_width                    = 420;
static int           g_height                   = 292;
static int           g_init_x                   = 0;
static int           g_init_y                   = 302;
static int           g_max_messages             = 18;
static DWORD         g_owner_pid                = 0;
static DWORD         g_vlc_pid                  = 0;
static char          g_position_state_path[MAX_PATH] = {0};
static char          g_size_state_path[MAX_PATH] = {0};
static bool          g_size_state_loaded          = false;
static bool          g_size_state_reference       = false;

typedef struct {
    char     code[BTTV_CODE_MAX];
    char     id[BTTV_ID_MAX];
    char     image_type[BTTV_TYPE_MAX];
    char     host[80];
    char     path[IMAGE_PATH_MAX];
    bool     local_file;
    int      api_w;
    int      api_h;
    bool     tried_image;
    bool     loading_image;
    GpImage *image;
    IStream *image_stream;
    UINT     image_w;
    UINT     image_h;
    bool     animated;
    GUID     frame_dimension;
    UINT     frame_count;
    UINT     current_frame;
    DWORD   *frame_delays_ms;
    DWORD    total_frame_delay_ms;
    ULONGLONG animation_started_ms;
} bttv_emote_t;

typedef struct {
    CRITICAL_SECTION cs;
    bttv_emote_t     items[BTTV_MAX_EMOTES];
    int              count;
    bool             global_loaded;
    bool             channel_loaded;
    bool             ffz_global_loaded;
    bool             ffz_channel_loaded;
    bool             seventv_global_loaded;
    bool             seventv_channel_loaded;
    bool             twitch_global_loaded;
    bool             twitch_channel_loaded;
    bool             twitch_badges_global_loaded;
    bool             twitch_badges_channel_loaded;
    char             channel_room_id[64];
    char             ffz_channel_room_id[64];
    char             seventv_channel_room_id[64];
    char             twitch_channel_login[64];
    char             twitch_badges_channel_room_id[64];
} bttv_catalog_t;

#define IMAGE_LOAD_QUEUE_CAP 1024

typedef struct {
    CRITICAL_SECTION cs;
    HANDLE           signal;
    int              items[IMAGE_LOAD_QUEUE_CAP];
    int              head;
    int              count;
} image_load_queue_t;

static bttv_catalog_t g_bttv;
static image_load_queue_t g_image_load_queue;
static ULONG_PTR      g_gdiplus_token = 0;
static bool           g_gdiplus_ready = false;
static bool           g_com_ready = false;
static bool           g_debug_emotes = false;

/* ===== Logging ========================================================== */

static void log_msg(const char *fmt, ...) {
    char prefix[32];
    time_t now = time(NULL);
    struct tm *tm = localtime(&now);
    strftime(prefix, sizeof(prefix), "%H:%M:%S", tm);
    fprintf(stderr, "[%s] ", prefix);
    va_list ap;
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
    fprintf(stderr, "\n");
    fflush(stderr);
}

static void log_emote_debug(const char *fmt, ...) {
    if (!g_debug_emotes) return;

    char prefix[32];
    time_t now = time(NULL);
    struct tm *tm = localtime(&now);
    strftime(prefix, sizeof(prefix), "%H:%M:%S", tm);
    fprintf(stderr, "[%s] emote-debug ", prefix);

    va_list ap;
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
    fprintf(stderr, "\n");
    fflush(stderr);
}

/* ===== Message queue ==================================================== */

static int utf8_valid_sequence_len(const unsigned char *s, size_t remain)
{
    if (!s || remain == 0 || s[0] == '\0') return 0;

    if (s[0] < 0x80u) return 1;

    int need = 0;
    if (s[0] >= 0xC2u && s[0] <= 0xDFu) {
        need = 2;
    } else if (s[0] >= 0xE0u && s[0] <= 0xEFu) {
        need = 3;
    } else if (s[0] >= 0xF0u && s[0] <= 0xF4u) {
        need = 4;
    } else {
        return 0;
    }

    if ((size_t)need > remain) return 0;
    for (int i = 1; i < need; i++) {
        if ((s[i] & 0xC0u) != 0x80u) return 0;
    }

    if (need == 3) {
        if (s[0] == 0xE0u && s[1] < 0xA0u) return 0;
        if (s[0] == 0xEDu && s[1] >= 0xA0u) return 0;
    } else if (need == 4) {
        if (s[0] == 0xF0u && s[1] < 0x90u) return 0;
        if (s[0] == 0xF4u && s[1] > 0x8Fu) return 0;
    }

    return need;
}

static size_t utf8_valid_prefix_bytes(const char *src, size_t max_bytes)
{
    if (!src || max_bytes == 0) return 0;

    size_t i = 0;
    while (i < max_bytes && src[i]) {
        int step = utf8_valid_sequence_len(
            (const unsigned char *)src + i,
            max_bytes - i);
        if (step <= 0) break;
        i += (size_t)step;
    }
    return i;
}

static void copy_utf8_truncated(char *dst, size_t dst_cap, const char *src)
{
    if (!dst || dst_cap == 0) return;
    dst[0] = '\0';
    if (!src || !src[0]) return;

    size_t n = utf8_valid_prefix_bytes(src, dst_cap - 1);
    memcpy(dst, src, n);
    dst[n] = '\0';
}

static void queue_init(msg_queue_t *q) {
    InitializeCriticalSection(&q->cs);
    q->head = 0;
    q->count = 0;
}

static void keep_scrolled_view_anchored_locked(const msg_queue_t *q) {
    LONG offset = InterlockedCompareExchange(&g_scroll_offset, 0, 0);
    if (offset <= 0) return;

    int visible = q->count < g_max_messages ? q->count : g_max_messages;
    int max_offset = q->count > visible ? q->count - visible : 0;
    LONG next = offset + 1;
    if (next > max_offset) next = max_offset;
    if (next < 0) next = 0;
    InterlockedExchange(&g_scroll_offset, next);
}

static void queue_push_ex(msg_queue_t *q, const char *user, const char *text,
                          bool is_system, const chat_badge_t *badges,
                          int badge_count, const chat_emote_t *emotes,
                          int emote_count)
{
    EnterCriticalSection(&q->cs);
    int idx;
    if (q->count < QUEUE_CAP) {
        idx = (q->head + q->count) % QUEUE_CAP;
        q->count++;
    } else {
        idx = q->head;
        q->head = (q->head + 1) % QUEUE_CAP;
    }
    chat_msg_t *m = &q->buf[idx];
    copy_utf8_truncated(m->user, MAX_USERNAME, user ? user : "chat");
    copy_utf8_truncated(m->text, MAX_MSG_TEXT, text ? text : "");
    m->is_system = is_system;
    m->badge_count = 0;
    if (!is_system && badges && badge_count > 0) {
        if (badge_count > MAX_MSG_BADGES) badge_count = MAX_MSG_BADGES;
        memcpy(m->badges, badges, (size_t)badge_count * sizeof(badges[0]));
        m->badge_count = badge_count;
    }
    m->emote_count = 0;
    if (!is_system && emotes && emote_count > 0) {
        if (emote_count > MAX_MSG_EMOTES) emote_count = MAX_MSG_EMOTES;
        memcpy(m->emotes, emotes, (size_t)emote_count * sizeof(emotes[0]));
        m->emote_count = emote_count;
    }
    m->created_ms = GetTickCount64();
    keep_scrolled_view_anchored_locked(q);
    LeaveCriticalSection(&q->cs);
    signal_render();
}

static void queue_push(msg_queue_t *q, const char *user, const char *text, bool is_system) {
    queue_push_ex(q, user, text, is_system, NULL, 0, NULL, 0);
}

/* Copy the last `max` messages out into `dst`, returning the number copied
 * (oldest first). Holding the lock for the duration is fine — render is
 * the only consumer and the snapshot is tiny. */
static int queue_snapshot_scrolled(msg_queue_t *q, chat_msg_t *dst, int max,
                                   int requested_offset,
                                   int *out_offset, int *out_max_offset)
{
    EnterCriticalSection(&q->cs);
    int n = q->count < max ? q->count : max;
    int max_offset = q->count > n ? q->count - n : 0;
    int offset = requested_offset;
    if (offset < 0) offset = 0;
    if (offset > max_offset) offset = max_offset;
    int start = (q->head + q->count - n - offset + QUEUE_CAP) % QUEUE_CAP;
    for (int i = 0; i < n; i++) {
        dst[i] = q->buf[(start + i) % QUEUE_CAP];
    }
    LeaveCriticalSection(&q->cs);
    if (out_offset) *out_offset = offset;
    if (out_max_offset) *out_max_offset = max_offset;
    return n;
}

static void adjust_scroll_offset(int notches) {
    int delta = notches * SCROLL_LINES_PER_NOTCH;
    if (delta == 0) return;

    for (;;) {
        LONG old_value = InterlockedCompareExchange(&g_scroll_offset, 0, 0);
        LONG next = old_value + delta;
        LONG max_offset = InterlockedCompareExchange(&g_scroll_max, 0, 0);
        if (next < 0) next = 0;
        if (next > max_offset) next = max_offset;
        if (InterlockedCompareExchange(&g_scroll_offset, next, old_value)
            == old_value) {
            break;
        }
    }

    signal_render();
}

static void set_scroll_offset(int offset) {
    LONG max_offset = InterlockedCompareExchange(&g_scroll_max, 0, 0);
    if (offset < 0) offset = 0;
    if (offset > max_offset) offset = max_offset;
    InterlockedExchange(&g_scroll_offset, offset);
    signal_render();
}

static void clamp_chat_size(int *width, int *height) {
    if (*width < CHAT_MIN_W) *width = CHAT_MIN_W;
    if (*height < CHAT_MIN_H) *height = CHAT_MIN_H;
    if (*width > CHAT_MAX_W) *width = CHAT_MAX_W;
    if (*height > CHAT_MAX_H) *height = CHAT_MAX_H;
    if ((uint64_t)(*width) * (uint64_t)(*height) * 4u > MYO_MAX_PAYLOAD) {
        *width = 420;
        *height = 292;
    }
}

static int clamp_int(int value, int min_value, int max_value) {
    if (value < min_value) return min_value;
    if (value > max_value) return max_value;
    return value;
}

static void set_font_size(int font_size_px) {
    g_font_size_px = clamp_int(font_size_px, MIN_FONT_SIZE_PX, MAX_FONT_SIZE_PX);
    g_system_font_size_px = clamp_int(
        (g_font_size_px * SYSTEM_FONT_SIZE + FONT_SIZE_PX / 2) / FONT_SIZE_PX,
        MIN_FONT_SIZE_PX,
        MAX_FONT_SIZE_PX);
}

static int current_video_height(void) {
    if ((LONG)GetCurrentThreadId() == InterlockedCompareExchange(&render_thread_id, 0, 0) && render_scale_height > 0) return render_scale_height;
    LONG height = InterlockedCompareExchange(&g_ui_scale_height, 0, 0);
    if (height <= 0) height = InterlockedCompareExchange(&g_video_height, 0, 0);
    return height > 0 ? (int)height : REFERENCE_VIDEO_H;
}

static int scale_reference_px(int value) {
    if (value <= 0) return 1;
    int video_h = current_video_height();
    int64_t scaled = (int64_t)value * video_h + REFERENCE_VIDEO_H / 2;
    scaled /= REFERENCE_VIDEO_H;
    if (scaled < 1) scaled = 1;
    if (scaled > INT_MAX) scaled = INT_MAX;
    return (int)scaled;
}

static int unscale_source_px(int value) {
    if (value <= 0) return 1;
    int video_h = current_video_height();
    int64_t unscaled = (int64_t)value * REFERENCE_VIDEO_H + video_h / 2;
    unscaled /= video_h;
    if (unscaled < 1) unscaled = 1;
    if (unscaled > INT_MAX) unscaled = INT_MAX;
    return (int)unscaled;
}

static void clamp_source_chat_size(int *width, int *height) {
    int min_w = scale_reference_px(CHAT_MIN_W);
    int min_h = scale_reference_px(CHAT_MIN_H);
    int max_w = scale_reference_px(CHAT_MAX_W);
    int max_h = scale_reference_px(CHAT_MAX_H);

    if (*width < min_w) *width = min_w;
    if (*height < min_h) *height = min_h;
    if (*width > max_w) *width = max_w;
    if (*height > max_h) *height = max_h;
    if (InterlockedCompareExchange(&g_ui_scale_height, 0, 0) > 0) {
        int video_w = InterlockedCompareExchange(&g_video_width, 0, 0);
        int video_h = InterlockedCompareExchange(&g_video_height, 0, 0);
        if (video_w > 0 && *width > video_w) *width = video_w;
        if (video_h > 0 && *height > video_h) *height = video_h;
    }

    while ((uint64_t)(*width) * (uint64_t)(*height) * 4u > MYO_MAX_PAYLOAD) {
        if (*width >= *height && *width > min_w) {
            (*width)--;
        } else if (*height > min_h) {
            (*height)--;
        } else {
            break;
        }
    }
}

static void source_chat_size_from_reference(int ref_width, int ref_height,
                                            int *out_width, int *out_height)
{
    clamp_chat_size(&ref_width, &ref_height);
    int width = scale_reference_px(ref_width);
    int height = scale_reference_px(ref_height);
    clamp_source_chat_size(&width, &height);
    *out_width = width;
    *out_height = height;
}

static void set_target_chat_size(int width, int height) {
    clamp_chat_size(&width, &height);
    int source_width = width;
    int source_height = height;
    source_chat_size_from_reference(width, height, &source_width, &source_height);
    InterlockedExchange(&g_target_size, MYO_PACK_SIZE_EVENT(source_width, source_height));
}

static bool ensure_parent_directory(const char *path) {
    if (!path || !path[0]) return false;

    char dir[MAX_PATH];
    strncpy(dir, path, sizeof(dir) - 1);
    dir[sizeof(dir) - 1] = '\0';

    char *slash = strrchr(dir, '\\');
    char *fslash = strrchr(dir, '/');
    if (!slash || (fslash && fslash > slash)) slash = fslash;
    if (!slash) return true;
    *slash = '\0';
    if (!dir[0]) return true;

    if (CreateDirectoryA(dir, NULL)) return true;
    return GetLastError() == ERROR_ALREADY_EXISTS;
}

static void init_size_state_path(void) {
    if (g_position_state_path[0]) {
        int n = snprintf(g_size_state_path, sizeof(g_size_state_path),
                         "%s.size", g_position_state_path);
        if (n > 0 && n < (int)sizeof(g_size_state_path)) return;
        g_size_state_path[0] = '\0';
    }

    const char *appdata = getenv("APPDATA");
    if (!appdata || !appdata[0]) return;
    int n = snprintf(g_size_state_path, sizeof(g_size_state_path),
                     "%s\\vlc-overlay\\chat-size.txt", appdata);
    if (n <= 0 || n >= (int)sizeof(g_size_state_path)) {
        g_size_state_path[0] = '\0';
    }
}

static bool parse_size_state_text(const char *text, int *width, int *height,
                                  bool *reference_size) {
    int w = 0, h = 0;
    if (reference_size) *reference_size = false;

    if (sscanf(text, " reference %d %d", &w, &h) == 2 ||
        sscanf(text, " normalized %d %d", &w, &h) == 2) {
        *width = w;
        *height = h;
        if (reference_size) *reference_size = true;
        return true;
    }

    if (sscanf(text, " %d %d", &w, &h) == 2) {
        *width = w;
        *height = h;
        return true;
    }

    const char *wp = strstr(text, "width");
    const char *hp = strstr(text, "height");
    if (!wp || !hp) return false;
    wp = strchr(wp, ':');
    hp = strchr(hp, ':');
    if (!wp || !hp) return false;
    *width = (int)strtol(wp + 1, NULL, 10);
    *height = (int)strtol(hp + 1, NULL, 10);
    if (reference_size &&
        (strstr(text, "reference") != NULL || strstr(text, "normalized") != NULL)) {
        *reference_size = true;
    }
    return *width > 0 && *height > 0;
}

static void load_saved_chat_size(void) {
    if (!g_size_state_path[0]) return;

    FILE *f = fopen(g_size_state_path, "r");
    if (!f) return;
    char text[256];
    size_t n = fread(text, 1, sizeof(text) - 1, f);
    fclose(f);
    text[n] = '\0';

    int width = 0, height = 0;
    bool reference_size = false;
    if (!parse_size_state_text(text, &width, &height, &reference_size)) return;
    clamp_chat_size(&width, &height);
    g_width = width;
    g_height = height;
    g_size_state_loaded = true;
    g_size_state_reference = reference_size;
}

static void save_chat_size(int width, int height) {
    if (!g_size_state_path[0]) return;
    clamp_chat_size(&width, &height);
    if (!ensure_parent_directory(g_size_state_path)) return;

    FILE *f = fopen(g_size_state_path, "w");
    if (!f) return;
    fprintf(f, "reference %d %d\n", width, height);
    fclose(f);
    g_size_state_loaded = true;
    g_size_state_reference = true;
}

/* ===== BetterTTV catalog + image cache ================================= */

typedef struct {
    uint8_t *data;
    size_t   size;
} byte_buf_t;

static void byte_buf_free(byte_buf_t *b) {
    if (!b) return;
    free(b->data);
    b->data = NULL;
    b->size = 0;
}

static bool file_read_bytes(const char *path, size_t max_bytes, byte_buf_t *out)
{
    memset(out, 0, sizeof(*out));
    if (!path || !path[0]) return false;

    FILE *f = fopen(path, "rb");
    if (!f) return false;
    if (fseek(f, 0, SEEK_END) != 0) {
        fclose(f);
        return false;
    }
    long size_long = ftell(f);
    if (size_long < 0 || (size_t)size_long > max_bytes) {
        fclose(f);
        return false;
    }
    if (fseek(f, 0, SEEK_SET) != 0) {
        fclose(f);
        return false;
    }

    size_t size = (size_t)size_long;
    uint8_t *data = (uint8_t *)malloc(size + 1);
    if (!data) {
        fclose(f);
        return false;
    }
    size_t read = size ? fread(data, 1, size, f) : 0;
    fclose(f);
    if (read != size) {
        free(data);
        return false;
    }
    data[size] = 0;
    out->data = data;
    out->size = size;
    return true;
}

static bool image_load_queue_init(image_load_queue_t *q) {
    memset(q, 0, sizeof(*q));
    InitializeCriticalSection(&q->cs);
    q->signal = CreateEventA(NULL, TRUE, FALSE, NULL);
    if (!q->signal) {
        DeleteCriticalSection(&q->cs);
        return false;
    }
    return true;
}

static void image_load_queue_destroy(image_load_queue_t *q) {
    if (q->signal) CloseHandle(q->signal);
    q->signal = NULL;
    DeleteCriticalSection(&q->cs);
}

static bool image_load_queue_push(image_load_queue_t *q, int index) {
    bool ok = false;
    EnterCriticalSection(&q->cs);
    if (q->count < IMAGE_LOAD_QUEUE_CAP) {
        int tail = (q->head + q->count) % IMAGE_LOAD_QUEUE_CAP;
        q->items[tail] = index;
        q->count++;
        SetEvent(q->signal);
        ok = true;
    }
    LeaveCriticalSection(&q->cs);
    return ok;
}

static bool image_load_queue_pop(image_load_queue_t *q, int *out_index,
                                 DWORD timeout_ms)
{
    if (WaitForSingleObject(q->signal, timeout_ms) != WAIT_OBJECT_0) {
        return false;
    }

    bool ok = false;
    EnterCriticalSection(&q->cs);
    if (q->count > 0) {
        *out_index = q->items[q->head];
        q->head = (q->head + 1) % IMAGE_LOAD_QUEUE_CAP;
        q->count--;
        ok = true;
    }
    if (q->count == 0) {
        ResetEvent(q->signal);
    }
    LeaveCriticalSection(&q->cs);
    return ok;
}

static bool utf8_to_wide_path(const char *src, wchar_t *dst, int dst_cap) {
    int n = MultiByteToWideChar(CP_UTF8, 0, src, -1, dst, dst_cap);
    if (n <= 0) {
        if (dst_cap > 0) dst[0] = L'\0';
        return false;
    }
    return true;
}

static bool split_https_url(const char *url, char *host, int host_cap,
                            char *path, int path_cap)
{
    const char *p = url;
    if (_strnicmp(p, "https://", 8) == 0) {
        p += 8;
    } else if (strncmp(p, "//", 2) == 0) {
        p += 2;
    } else {
        return false;
    }

    const char *slash = strchr(p, '/');
    if (!slash) return false;

    int host_len = (int)(slash - p);
    if (host_len <= 0 || host_len >= host_cap) return false;
    memcpy(host, p, (size_t)host_len);
    host[host_len] = '\0';

    int path_len = (int)strlen(slash);
    if (path_len <= 0 || path_len >= path_cap) return false;
    memcpy(path, slash, (size_t)path_len + 1);
    return true;
}

static bool is_kick_asset_host(const char *host);

static bool https_get_bytes_ex(const wchar_t *host, const wchar_t *path,
                               const wchar_t *accept,
                               const wchar_t *extra_headers,
                               size_t max_bytes, byte_buf_t *out)
{
    memset(out, 0, sizeof(*out));

    HINTERNET session = WinHttpOpen(L"vlc-chat-overlay/1.0",
                                    WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                                    WINHTTP_NO_PROXY_NAME,
                                    WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return false;
    WinHttpSetTimeouts(session, 3000, 5000, 5000, 8000);

    HINTERNET connect = WinHttpConnect(session, host, (INTERNET_PORT)443, 0);
    if (!connect) {
        WinHttpCloseHandle(session);
        return false;
    }

    HINTERNET request = WinHttpOpenRequest(connect, L"GET", path, NULL,
                                           WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES,
                                           WINHTTP_FLAG_SECURE);
    if (!request) {
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
        return false;
    }

    wchar_t headers[2048];
    headers[0] = L'\0';
    if (accept && accept[0]) {
        swprintf(headers, 2048, L"Accept: %s\r\n", accept);
    }
    if (extra_headers && extra_headers[0]) {
        size_t used = wcslen(headers);
        if (used + 1 < 2048) {
            wcsncpy(headers + used, extra_headers, 2048 - used - 1);
            headers[2047] = L'\0';
        }
    }

    BOOL ok = WinHttpSendRequest(
                  request,
                  headers[0] ? headers : WINHTTP_NO_ADDITIONAL_HEADERS,
                  headers[0] ? (DWORD)-1L : 0,
                  WINHTTP_NO_REQUEST_DATA, 0, 0, 0)
           && WinHttpReceiveResponse(request, NULL);
    if (!ok) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
        return false;
    }

    DWORD status = 0;
    DWORD status_len = sizeof(status);
    if (!WinHttpQueryHeaders(request,
                             WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                             WINHTTP_HEADER_NAME_BY_INDEX,
                             &status, &status_len, WINHTTP_NO_HEADER_INDEX)
        || status < 200 || status >= 300) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
        return false;
    }

    uint8_t *data = NULL;
    size_t size = 0;
    size_t cap = 0;
    while (true) {
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(request, &avail)) {
            free(data);
            data = NULL;
            break;
        }
        if (avail == 0) {
            break;
        }
        if (size + avail > max_bytes) {
            free(data);
            data = NULL;
            break;
        }
        if (size + avail + 1 > cap) {
            size_t new_cap = cap ? cap * 2 : 8192;
            while (new_cap < size + avail + 1) new_cap *= 2;
            uint8_t *next = (uint8_t *)realloc(data, new_cap);
            if (!next) {
                free(data);
                data = NULL;
                break;
            }
            data = next;
            cap = new_cap;
        }
        DWORD read = 0;
        if (!WinHttpReadData(request, data + size, avail, &read) || read == 0) {
            free(data);
            data = NULL;
            break;
        }
        size += read;
    }

    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connect);
    WinHttpCloseHandle(session);

    if (!data) return false;
    data[size] = 0;
    out->data = data;
    out->size = size;
    return true;
}

static bool https_get_bytes(const wchar_t *host, const wchar_t *path,
                            const wchar_t *accept, size_t max_bytes,
                            byte_buf_t *out)
{
    return https_get_bytes_ex(host, path, accept, NULL, max_bytes, out);
}

static const wchar_t *image_extra_headers_for_host(const char *host)
{
    if (is_kick_asset_host(host)) {
        return L"Referer: https://kick.com/\r\n"
               L"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36\r\n"
               L"Accept-Language: *\r\n";
    }

    return NULL;
}

static bool https_get_image_bytes(const wchar_t *host, const wchar_t *path,
                                  const char *host_utf8, size_t max_bytes,
                                  byte_buf_t *out)
{
    return https_get_bytes_ex(host, path,
                              L"image/png,image/gif,image/jpeg,image/*",
                              image_extra_headers_for_host(host_utf8),
                              max_bytes, out);
}

static bool https_post_json_bytes(const wchar_t *host, const wchar_t *path,
                                  const wchar_t *extra_headers,
                                  const char *body, size_t body_len,
                                  size_t max_bytes, DWORD *out_status,
                                  byte_buf_t *out)
{
    memset(out, 0, sizeof(*out));
    if (out_status) *out_status = 0;

    HINTERNET session = WinHttpOpen(L"vlc-chat-overlay/1.0",
                                    WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                                    WINHTTP_NO_PROXY_NAME,
                                    WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return false;
    WinHttpSetTimeouts(session, 3000, 5000, 5000, 8000);

    HINTERNET connect = WinHttpConnect(session, host, (INTERNET_PORT)443, 0);
    if (!connect) {
        WinHttpCloseHandle(session);
        return false;
    }

    HINTERNET request = WinHttpOpenRequest(connect, L"POST", path, NULL,
                                           WINHTTP_NO_REFERER,
                                           WINHTTP_DEFAULT_ACCEPT_TYPES,
                                           WINHTTP_FLAG_SECURE);
    if (!request) {
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
        return false;
    }

    wchar_t headers[3072];
    swprintf(headers, 3072,
             L"Accept: application/json\r\n"
             L"Content-Type: application/json\r\n");
    if (extra_headers && extra_headers[0]) {
        size_t used = wcslen(headers);
        if (used + 1 < 3072) {
            wcsncpy(headers + used, extra_headers, 3072 - used - 1);
            headers[3071] = L'\0';
        }
    }

    BOOL ok = WinHttpSendRequest(
                  request,
                  headers,
                  (DWORD)-1L,
                  (LPVOID)body, (DWORD)body_len, (DWORD)body_len, 0)
           && WinHttpReceiveResponse(request, NULL);
    if (!ok) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connect);
        WinHttpCloseHandle(session);
        return false;
    }

    DWORD status = 0;
    DWORD status_len = sizeof(status);
    if (WinHttpQueryHeaders(request,
                            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                            WINHTTP_HEADER_NAME_BY_INDEX,
                            &status, &status_len, WINHTTP_NO_HEADER_INDEX)) {
        if (out_status) *out_status = status;
    }

    uint8_t *data = (uint8_t *)malloc(1);
    size_t size = 0;
    size_t cap = data ? 1 : 0;
    bool read_ok = data != NULL;
    while (read_ok) {
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(request, &avail)) {
            read_ok = false;
            break;
        }
        if (avail == 0) {
            break;
        }
        if (size + avail > max_bytes) {
            read_ok = false;
            break;
        }
        if (size + avail + 1 > cap) {
            size_t new_cap = cap ? cap * 2 : 8192;
            while (new_cap < size + avail + 1) new_cap *= 2;
            uint8_t *next = (uint8_t *)realloc(data, new_cap);
            if (!next) {
                read_ok = false;
                break;
            }
            data = next;
            cap = new_cap;
        }
        DWORD read = 0;
        if (!WinHttpReadData(request, data + size, avail, &read)) {
            read_ok = false;
            break;
        }
        if (read == 0) break;
        size += read;
    }

    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connect);
    WinHttpCloseHandle(session);

    if (!read_ok) {
        free(data);
        return false;
    }
    data[size] = 0;
    out->data = data;
    out->size = size;
    return true;
}

static bool string_replace_once(char *text, size_t text_cap,
                                const char *from, const char *to)
{
    char *hit = strstr(text, from);
    if (!hit) return false;

    size_t prefix = (size_t)(hit - text);
    size_t from_len = strlen(from);
    size_t to_len = strlen(to);
    size_t suffix_len = strlen(hit + from_len);
    if (prefix + to_len + suffix_len + 1 > text_cap) return false;

    memmove(hit + to_len, hit + from_len, suffix_len + 1);
    memcpy(hit, to, to_len);
    return true;
}

static const char *bounded_strstr(const char *start, const char *end,
                                  const char *needle)
{
    size_t n = strlen(needle);
    if (n == 0) return start;
    for (const char *p = start; p + n <= end; p++) {
        if (memcmp(p, needle, n) == 0) return p;
    }
    return NULL;
}

static const char *json_object_end(const char *obj, const char *end,
                                   bool *has_nested)
{
    bool in_string = false;
    bool escaped = false;
    int depth = 0;
    *has_nested = false;

    for (const char *p = obj; p < end; p++) {
        char c = *p;
        if (in_string) {
            if (escaped) {
                escaped = false;
            } else if (c == '\\') {
                escaped = true;
            } else if (c == '"') {
                in_string = false;
            }
            continue;
        }
        if (c == '"') {
            in_string = true;
        } else if (c == '{') {
            depth++;
            if (depth > 1) *has_nested = true;
        } else if (c == '}') {
            depth--;
            if (depth == 0) return p + 1;
            if (depth < 0) return NULL;
        }
    }
    return NULL;
}

static const char *json_array_end(const char *arr, const char *end)
{
    bool in_string = false;
    bool escaped = false;
    int depth = 0;

    for (const char *p = arr; p < end; p++) {
        char c = *p;
        if (in_string) {
            if (escaped) {
                escaped = false;
            } else if (c == '\\') {
                escaped = true;
            } else if (c == '"') {
                in_string = false;
            }
            continue;
        }
        if (c == '"') {
            in_string = true;
        } else if (c == '[') {
            depth++;
        } else if (c == ']') {
            depth--;
            if (depth == 0) return p + 1;
            if (depth < 0) return NULL;
        } else if (c == '{') {
            depth++;
        } else if (c == '}') {
            depth--;
            if (depth < 0) return NULL;
        }
    }
    return NULL;
}

static int json_hex_digit(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static bool json_parse_hex4(const char *p, const char *end, unsigned *out)
{
    if (!p || !end || !out || p + 4 > end) return false;

    unsigned value = 0;
    for (int i = 0; i < 4; i++) {
        int digit = json_hex_digit(p[i]);
        if (digit < 0) return false;
        value = (value << 4) | (unsigned)digit;
    }
    *out = value;
    return true;
}

static bool json_append_utf8_codepoint(char *out, int out_size, int *o,
                                       unsigned codepoint)
{
    if (!out || !o || out_size <= 0) return false;

    if (codepoint > 0x10FFFFu ||
        (codepoint >= 0xD800u && codepoint <= 0xDFFFu)) {
        codepoint = 0xFFFDu;
    }

    if (codepoint < 0x80u) {
        if (*o + 1 >= out_size) return false;
        out[(*o)++] = (char)codepoint;
    } else if (codepoint < 0x800u) {
        if (*o + 2 >= out_size) return false;
        out[(*o)++] = (char)(0xC0u | (codepoint >> 6));
        out[(*o)++] = (char)(0x80u | (codepoint & 0x3Fu));
    } else if (codepoint < 0x10000u) {
        if (*o + 3 >= out_size) return false;
        out[(*o)++] = (char)(0xE0u | (codepoint >> 12));
        out[(*o)++] = (char)(0x80u | ((codepoint >> 6) & 0x3Fu));
        out[(*o)++] = (char)(0x80u | (codepoint & 0x3Fu));
    } else {
        if (*o + 4 >= out_size) return false;
        out[(*o)++] = (char)(0xF0u | (codepoint >> 18));
        out[(*o)++] = (char)(0x80u | ((codepoint >> 12) & 0x3Fu));
        out[(*o)++] = (char)(0x80u | ((codepoint >> 6) & 0x3Fu));
        out[(*o)++] = (char)(0x80u | (codepoint & 0x3Fu));
    }

    return true;
}

static bool json_unescape_string_from_value(const char *p, const char *obj_end,
                                            char *out, int out_size,
                                            const char **after_string)
{
    if (!p || p >= obj_end || *p != '"' || !out || out_size <= 0) return false;
    p++;

    int o = 0;
    while (p < obj_end) {
        unsigned char c = (unsigned char)*p++;
        if (c == '"') {
            out[o] = '\0';
            if (after_string) *after_string = p;
            return true;
        }

        if (c != '\\') {
            if (c < 0x20u) continue;
            if (o + 1 >= out_size) break;
            out[o++] = (char)c;
            continue;
        }

        if (p >= obj_end) break;
        char escaped = *p++;
        switch (escaped) {
            case '"':
            case '\\':
            case '/':
                if (o + 1 >= out_size) goto full;
                out[o++] = escaped;
                break;
            case 'b':
                if (o + 1 >= out_size) goto full;
                out[o++] = '\b';
                break;
            case 'f':
                if (o + 1 >= out_size) goto full;
                out[o++] = '\f';
                break;
            case 'n':
                if (o + 1 >= out_size) goto full;
                out[o++] = '\n';
                break;
            case 'r':
                if (o + 1 >= out_size) goto full;
                out[o++] = '\r';
                break;
            case 't':
                if (o + 1 >= out_size) goto full;
                out[o++] = '\t';
                break;
            case 'u': {
                unsigned cp = 0;
                if (!json_parse_hex4(p, obj_end, &cp)) {
                    if (o + 1 >= out_size) goto full;
                    out[o++] = 'u';
                    break;
                }
                p += 4;

                if (cp >= 0xD800u && cp <= 0xDBFFu) {
                    unsigned low = 0;
                    if (p + 6 <= obj_end &&
                        p[0] == '\\' &&
                        p[1] == 'u' &&
                        json_parse_hex4(p + 2, obj_end, &low) &&
                        low >= 0xDC00u &&
                        low <= 0xDFFFu) {
                        cp = 0x10000u + (((cp - 0xD800u) << 10) | (low - 0xDC00u));
                        p += 6;
                    } else {
                        cp = 0xFFFDu;
                    }
                } else if (cp >= 0xDC00u && cp <= 0xDFFFu) {
                    cp = 0xFFFDu;
                }

                if (!json_append_utf8_codepoint(out, out_size, &o, cp)) goto full;
                break;
            }
            default:
                if (o + 1 >= out_size) goto full;
                out[o++] = escaped;
                break;
        }
    }

full:
    out[o] = '\0';
    if (after_string) *after_string = p;
    return false;
}

static bool json_get_string(const char *obj, const char *obj_end,
                            const char *key, char *out, int out_size)
{
    char needle[80];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = bounded_strstr(obj, obj_end, needle);
    if (!p) return false;
    p += strlen(needle);
    while (p < obj_end && isspace((unsigned char)*p)) p++;

    return json_unescape_string_from_value(p, obj_end, out, out_size, NULL);
}

static const char *json_find_direct_value(const char *obj, const char *obj_end,
                                          const char *key)
{
    if (!obj || !obj_end || obj >= obj_end || *obj != '{') return NULL;

    const size_t key_len = strlen(key);
    int depth = 1;
    const char *p = obj + 1;

    while (p < obj_end && depth > 0) {
        char c = *p;

        if (c == '"') {
            const char *name_start = p + 1;
            const char *q = name_start;
            bool escaped = false;
            while (q < obj_end) {
                char qc = *q;
                if (escaped) {
                    escaped = false;
                } else if (qc == '\\') {
                    escaped = true;
                } else if (qc == '"') {
                    break;
                }
                q++;
            }
            if (q >= obj_end) return NULL;

            const char *after = q + 1;
            while (after < obj_end && isspace((unsigned char)*after)) after++;
            if (depth == 1 && after < obj_end && *after == ':') {
                size_t name_len = (size_t)(q - name_start);
                if (name_len == key_len && memcmp(name_start, key, key_len) == 0) {
                    const char *value = after + 1;
                    while (value < obj_end && isspace((unsigned char)*value)) value++;
                    return value;
                }
            }
            p = q + 1;
            continue;
        }

        if (c == '{' || c == '[') {
            depth++;
        } else if (c == '}' || c == ']') {
            depth--;
        }
        p++;
    }

    return NULL;
}

static bool json_get_string_from_value(const char *p, const char *obj_end,
                                       char *out, int out_size)
{
    return json_unescape_string_from_value(p, obj_end, out, out_size, NULL);
}

static bool json_get_string_direct(const char *obj, const char *obj_end,
                                   const char *key, char *out, int out_size)
{
    return json_get_string_from_value(json_find_direct_value(obj, obj_end, key),
                                      obj_end, out, out_size);
}

static bool json_get_scalar_string_direct(const char *obj, const char *obj_end,
                                          const char *key, char *out,
                                          int out_size)
{
    const char *p = json_find_direct_value(obj, obj_end, key);
    if (!p || p >= obj_end || !out || out_size <= 0) return false;

    if (*p == '"') {
        return json_get_string_from_value(p, obj_end, out, out_size);
    }

    while (p < obj_end && isspace((unsigned char)*p)) p++;
    if (p + 4 <= obj_end && memcmp(p, "null", 4) == 0) {
        out[0] = '\0';
        return false;
    }

    const char *q = p;
    while (q < obj_end && *q != ',' && *q != '}' && *q != ']'
           && !isspace((unsigned char)*q)) {
        q++;
    }
    while (q > p && isspace((unsigned char)q[-1])) q--;

    int len = (int)(q - p);
    if (len <= 0) {
        out[0] = '\0';
        return false;
    }
    if (len >= out_size) len = out_size - 1;
    memcpy(out, p, (size_t)len);
    out[len] = '\0';
    return true;
}

static bool json_get_object_direct(const char *obj, const char *obj_end,
                                   const char *key,
                                   const char **out_obj,
                                   const char **out_obj_end)
{
    const char *p = json_find_direct_value(obj, obj_end, key);
    if (!p || p >= obj_end || *p != '{') return false;

    bool nested = false;
    const char *end = json_object_end(p, obj_end, &nested);
    if (!end) return false;

    *out_obj = p;
    *out_obj_end = end;
    return true;
}

static bool json_get_array_direct(const char *obj, const char *obj_end,
                                  const char *key,
                                  const char **out_arr,
                                  const char **out_arr_end)
{
    const char *p = json_find_direct_value(obj, obj_end, key);
    if (!p || p >= obj_end || *p != '[') return false;

    const char *end = json_array_end(p, obj_end);
    if (!end) return false;

    *out_arr = p;
    *out_arr_end = end;
    return true;
}

static int json_get_int_default(const char *obj, const char *obj_end,
                                const char *key, int fallback)
{
    char needle[80];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = bounded_strstr(obj, obj_end, needle);
    if (!p) return fallback;
    p += strlen(needle);
    while (p < obj_end && isspace((unsigned char)*p)) p++;
    return (int)strtol(p, NULL, 10);
}

static int json_get_int_default_direct(const char *obj, const char *obj_end,
                                       const char *key, int fallback)
{
    const char *p = json_find_direct_value(obj, obj_end, key);
    if (!p) return fallback;
    return (int)strtol(p, NULL, 10);
}

static bool json_get_bool_default(const char *obj, const char *obj_end,
                                  const char *key, bool fallback)
{
    char needle[80];
    snprintf(needle, sizeof(needle), "\"%s\":", key);
    const char *p = bounded_strstr(obj, obj_end, needle);
    if (!p) return fallback;
    p += strlen(needle);
    while (p < obj_end && isspace((unsigned char)*p)) p++;
    if (p + 4 <= obj_end && memcmp(p, "true", 4) == 0) return true;
    if (p + 5 <= obj_end && memcmp(p, "false", 5) == 0) return false;
    return fallback;
}

static bool json_get_bool_default_direct(const char *obj, const char *obj_end,
                                         const char *key, bool fallback)
{
    const char *p = json_find_direct_value(obj, obj_end, key);
    if (!p) return fallback;
    if (p + 4 <= obj_end && memcmp(p, "true", 4) == 0) return true;
    if (p + 5 <= obj_end && memcmp(p, "false", 5) == 0) return false;
    return fallback;
}

static const char *json_document_root(const char *json, const char *end)
{
    if (!json || !end || json >= end) return json;
    const unsigned char *p = (const unsigned char *)json;
    const unsigned char *limit = (const unsigned char *)end;
    if (limit - p >= 3 && p[0] == 0xEFu && p[1] == 0xBBu && p[2] == 0xBFu) {
        p += 3;
    }
    while (p < limit && isspace(*p)) {
        p++;
    }
    return (const char *)p;
}

static const char *json_string_end(const char *str, const char *end) {
    if (!str || str >= end || *str != '"') return NULL;
    bool escaped = false;
    for (const char *p = str + 1; p < end; p++) {
        char c = *p;
        if (escaped) {
            escaped = false;
        } else if (c == '\\') {
            escaped = true;
        } else if (c == '"') {
            return p + 1;
        }
    }
    return NULL;
}

static const char *json_value_end(const char *value, const char *end) {
    if (!value || value >= end) return NULL;
    if (*value == '{') {
        bool nested = false;
        return json_object_end(value, end, &nested);
    }
    if (*value == '[') {
        return json_array_end(value, end);
    }
    if (*value == '"') {
        return json_string_end(value, end);
    }

    const char *p = value;
    while (p < end && *p != ',' && *p != '}' && *p != ']') p++;
    return p;
}

static bool json_next_direct_property(const char **cursor, const char *obj_end,
                                      char *name, int name_cap,
                                      const char **out_value,
                                      const char **out_value_end)
{
    if (!cursor || !*cursor || !obj_end || !name || name_cap <= 0
        || !out_value || !out_value_end) {
        return false;
    }

    const char *p = *cursor;
    if (p < obj_end && *p == '{') p++;

    while (p < obj_end) {
        while (p < obj_end && (isspace((unsigned char)*p) || *p == ',')) p++;
        if (p >= obj_end || *p == '}') {
            *cursor = p;
            return false;
        }
        if (*p != '"') {
            *cursor = p + 1;
            continue;
        }

        const char *key_start = p + 1;
        const char *key_end = json_string_end(p, obj_end);
        if (!key_end) return false;

        int o = 0;
        bool escaped = false;
        for (const char *q = key_start; q < key_end - 1 && o + 1 < name_cap; q++) {
            char c = *q;
            if (escaped) {
                name[o++] = c;
                escaped = false;
            } else if (c == '\\') {
                escaped = true;
            } else {
                name[o++] = c;
            }
        }
        name[o] = '\0';

        p = key_end;
        while (p < obj_end && isspace((unsigned char)*p)) p++;
        if (p >= obj_end || *p != ':') return false;
        p++;
        while (p < obj_end && isspace((unsigned char)*p)) p++;

        const char *value_end = json_value_end(p, obj_end);
        if (!value_end) return false;

        *out_value = p;
        *out_value_end = value_end;
        *cursor = value_end;
        return true;
    }

    *cursor = p;
    return false;
}

static bool json_next_array_value(const char **cursor, const char *arr_end,
                                  const char **out_value,
                                  const char **out_value_end)
{
    if (!cursor || !*cursor || !arr_end || !out_value || !out_value_end) {
        return false;
    }

    const char *p = *cursor;
    if (p < arr_end && *p == '[') p++;

    while (p < arr_end && (isspace((unsigned char)*p) || *p == ',')) p++;
    if (p >= arr_end || *p == ']') {
        *cursor = p;
        return false;
    }

    const char *value_end = json_value_end(p, arr_end);
    if (!value_end) return false;

    *out_value = p;
    *out_value_end = value_end;
    *cursor = value_end;
    return true;
}

/* ===== Twitch chat send auth + input state ============================= */

static void trim_ascii_in_place(char *s) {
    if (!s) return;
    char *start = s;
    while (*start && isspace((unsigned char)*start)) start++;
    if (start != s) memmove(s, start, strlen(start) + 1);

    size_t len = strlen(s);
    while (len > 0 && isspace((unsigned char)s[len - 1])) {
        s[--len] = '\0';
    }
}

static void strip_oauth_prefix(char *token) {
    trim_ascii_in_place(token);
    if (_strnicmp(token, "oauth:", 6) == 0) {
        memmove(token, token + 6, strlen(token + 6) + 1);
    } else if (_strnicmp(token, "oauth ", 6) == 0) {
        memmove(token, token + 6, strlen(token + 6) + 1);
    } else if (_strnicmp(token, "bearer ", 7) == 0) {
        memmove(token, token + 7, strlen(token + 7) + 1);
    }
    trim_ascii_in_place(token);
}

static void sanitize_twitch_login(char *login) {
    if (!login) return;
    size_t out = 0;
    for (size_t i = 0; login[i] && out + 1 < MAX_USERNAME; i++) {
        char c = login[i];
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
        if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_') {
            login[out++] = c;
        }
    }
    login[out] = '\0';
}

static void sanitize_channel_display_name(char *name) {
    if (!name) return;
    trim_ascii_in_place(name);
    size_t out = 0;
    for (size_t i = 0; name[i] && out + 1 < MAX_CHANNEL_DISPLAY; i++) {
        char c = name[i];
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9') || c == '_' || c == '-') {
            name[out++] = c;
        }
    }
    name[out] = '\0';
}

static void set_channel_display_name(const char *name) {
    if (!name || !name[0]) return;
    strncpy(g_channel_display_name, name, sizeof(g_channel_display_name) - 1);
    g_channel_display_name[sizeof(g_channel_display_name) - 1] = '\0';
    sanitize_channel_display_name(g_channel_display_name);
}

static const char *channel_display_name(void) {
    return g_channel_display_name[0] ? g_channel_display_name : g_channel;
}

static bool read_text_file_trimmed(const char *path, char *out, int out_cap) {
    if (!path || !path[0] || !out || out_cap <= 0) return false;
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    size_t n = fread(out, 1, (size_t)out_cap - 1, f);
    fclose(f);
    out[n] = '\0';
    trim_ascii_in_place(out);
    return out[0] != '\0';
}

static void default_twitch_token_file(char *out, int out_cap) {
    if (!out || out_cap <= 0) return;
    out[0] = '\0';
    const char *appdata = getenv("APPDATA");
    if (!appdata || !appdata[0]) return;
    int n = snprintf(out, (size_t)out_cap,
                     "%s\\streamlink\\twitch-oauth-token.txt", appdata);
    if (n <= 0 || n >= out_cap) out[0] = '\0';
}

static void default_twitch_client_id_file(char *out, int out_cap) {
    if (!out || out_cap <= 0) return;
    out[0] = '\0';
    const char *appdata = getenv("APPDATA");
    if (!appdata || !appdata[0]) return;
    int n = snprintf(out, (size_t)out_cap,
                     "%s\\streamlink\\twitch-chat-client-id.txt", appdata);
    if (n <= 0 || n >= out_cap) out[0] = '\0';
}

static void default_kick_token_file(char *out, int out_cap) {
    if (!out || out_cap <= 0) return;
    out[0] = '\0';
    const char *appdata = getenv("APPDATA");
    if (!appdata || !appdata[0]) return;
    int n = snprintf(out, (size_t)out_cap,
                     "%s\\streamlink\\kick-oauth-token.txt", appdata);
    if (n <= 0 || n >= out_cap) out[0] = '\0';
}

static void input_set_notice_ms(DWORD ms, const char *fmt, ...) {
    EnterCriticalSection(&g_input_cs);
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(g_input_notice, sizeof(g_input_notice), fmt, ap);
    va_end(ap);
    g_input_notice[sizeof(g_input_notice) - 1] = '\0';
    g_input_notice_until_ms = GetTickCount64() + ms;
    LeaveCriticalSection(&g_input_cs);
    signal_render();
}

static void input_set_focused(bool focused) {
    HWND focus_hwnd = NULL;
    DWORD focus_pid = 0;
    if (focused) {
        focus_hwnd = GetForegroundWindow();
        if (focus_hwnd) {
            GetWindowThreadProcessId(focus_hwnd, &focus_pid);
        }
    }

    EnterCriticalSection(&g_input_cs);
    g_input_focused = focused;
    g_input_focus_hwnd = focused ? focus_hwnd : NULL;
    g_input_focus_pid = focused ? focus_pid : 0;
    if (focused) {
        g_input_notice[0] = '\0';
        g_input_notice_until_ms = 0;
    }
    LeaveCriticalSection(&g_input_cs);
    signal_render();
}

static bool input_is_focused(void) {
    bool focused;
    EnterCriticalSection(&g_input_cs);
    focused = g_input_focused;
    LeaveCriticalSection(&g_input_cs);
    return focused;
}

static void input_backspace(void) {
    EnterCriticalSection(&g_input_cs);
    size_t len = strlen(g_input_text);
    if (len > 0) {
        len--;
        while (len > 0 && ((unsigned char)g_input_text[len] & 0xc0u) == 0x80u) {
            len--;
        }
        g_input_text[len] = '\0';
    }
    LeaveCriticalSection(&g_input_cs);
    signal_render();
}

static void input_append_utf8_sanitized(const char *text) {
    if (!text || !text[0]) return;
    EnterCriticalSection(&g_input_cs);
    size_t len = strlen(g_input_text);
    for (const unsigned char *p = (const unsigned char *)text;
         *p && len + 1 < sizeof(g_input_text);
         p++) {
        unsigned char c = *p;
        if (c == '\r' || c == '\n' || c == '\t') {
            g_input_text[len++] = ' ';
            continue;
        }
        if (c < 0x20u) continue;

        int seq_len = 1;
        if (c >= 0xF0u) seq_len = 4;
        else if (c >= 0xE0u) seq_len = 3;
        else if (c >= 0xC2u) seq_len = 2;
        else if (c >= 0x80u) continue;

        if (len + (size_t)seq_len >= sizeof(g_input_text)) break;
        bool valid = true;
        for (int i = 1; i < seq_len; i++) {
            if ((p[i] & 0xC0u) != 0x80u) {
                valid = false;
                break;
            }
        }
        if (!valid) continue;
        for (int i = 0; i < seq_len; i++) {
            g_input_text[len++] = (char)p[i];
        }
        p += seq_len - 1;
    }
    g_input_text[len] = '\0';
    LeaveCriticalSection(&g_input_cs);
    signal_render();
}

static void input_append_utf16_sanitized(const wchar_t *text, int wchar_count) {
    if (!text || wchar_count <= 0) return;
    char utf8[512];
    int n = WideCharToMultiByte(CP_UTF8, 0, text, wchar_count,
                                utf8, (int)sizeof(utf8) - 1,
                                NULL, NULL);
    if (n <= 0) return;
    utf8[n] = '\0';
    input_append_utf8_sanitized(utf8);
}

static bool input_take_submit_text(char *out, int out_cap) {
    if (!out || out_cap <= 0) return false;
    EnterCriticalSection(&g_input_cs);
    snprintf(out, (size_t)out_cap, "%s", g_input_text);
    g_input_text[0] = '\0';
    LeaveCriticalSection(&g_input_cs);
    trim_ascii_in_place(out);
    signal_render();
    return out[0] != '\0';
}

static void input_snapshot(char *text, int text_cap, bool *focused,
                           char *notice, int notice_cap) {
    EnterCriticalSection(&g_input_cs);
    if (text && text_cap > 0) {
        snprintf(text, (size_t)text_cap, "%s", g_input_text);
    }
    if (focused) *focused = g_input_focused;
    if (notice && notice_cap > 0) {
        if (g_input_notice[0] && GetTickCount64() <= g_input_notice_until_ms) {
            snprintf(notice, (size_t)notice_cap, "%s", g_input_notice);
        } else {
            notice[0] = '\0';
            g_input_notice[0] = '\0';
            g_input_notice_until_ms = 0;
        }
    }
    LeaveCriticalSection(&g_input_cs);
}

static bool validate_twitch_token_login(const char *token,
                                        char *login, int login_cap,
                                        bool *out_can_send) {
    if (!token || !token[0] || !login || login_cap <= 0) return false;
    login[0] = '\0';
    if (out_can_send) *out_can_send = false;

    char header_utf8[MAX_OAUTH_TOKEN + 64];
    int h = snprintf(header_utf8, sizeof(header_utf8),
                     "Authorization: OAuth %s\r\n", token);
    if (h <= 0 || h >= (int)sizeof(header_utf8)) return false;

    wchar_t header_w[2300];
    if (MultiByteToWideChar(CP_UTF8, 0, header_utf8, -1,
                            header_w, (int)(sizeof(header_w) / sizeof(header_w[0]))) <= 0) {
        return false;
    }

    byte_buf_t body;
    if (!https_get_bytes_ex(L"id.twitch.tv", L"/oauth2/validate",
                            L"application/json", header_w,
                            HTTP_MAX_JSON, &body)) {
        return false;
    }

    const char *json = (const char *)body.data;
    const char *end = json + body.size;
    bool ok = json_get_string_direct(json, end, "login", login, login_cap);
    if (ok) {
        sanitize_twitch_login(login);
        ok = login[0] != '\0';
    }
    if (out_can_send) {
        *out_can_send = bounded_strstr(json, end, "\"chat:edit\"") != NULL
                     || bounded_strstr(json, end, "\"chat:write\"") != NULL
                     || bounded_strstr(json, end, "\"user:write:chat\"") != NULL;
    }
    byte_buf_free(&body);
    return ok;
}

static void load_twitch_client_id(void) {
    if (g_twitch_client_id[0]) return;

    const char *env_id = getenv("TWITCH_CHAT_CLIENT_ID");
    if (!env_id || !env_id[0]) {
        env_id = getenv("STREAMLINK_TWITCH_CHAT_CLIENT_ID");
    }
    if (env_id && env_id[0]) {
        strncpy(g_twitch_client_id, env_id, sizeof(g_twitch_client_id) - 1);
        trim_ascii_in_place(g_twitch_client_id);
        return;
    }

    char path[MAX_PATH];
    default_twitch_client_id_file(path, sizeof(path));
    if (path[0]) {
        read_text_file_trimmed(path, g_twitch_client_id,
                               sizeof(g_twitch_client_id));
    }
}

static void resolve_twitch_channel_display_name(void) {
    if (g_chat_provider != CHAT_PROVIDER_TWITCH || !g_channel[0]
        || !g_twitch_chat_oauth[0] || g_channel_display_name_explicit) {
        return;
    }

    load_twitch_client_id();
    if (!g_twitch_client_id[0]) return;

    char path_utf8[160];
    int p = snprintf(path_utf8, sizeof(path_utf8),
                     "/helix/users?login=%s", g_channel);
    if (p <= 0 || p >= (int)sizeof(path_utf8)) return;

    wchar_t path_w[180];
    if (MultiByteToWideChar(CP_UTF8, 0, path_utf8, -1,
                            path_w, (int)(sizeof(path_w) / sizeof(path_w[0]))) <= 0) {
        return;
    }

    char header_utf8[MAX_OAUTH_TOKEN + 192];
    int h = snprintf(header_utf8, sizeof(header_utf8),
                     "Authorization: Bearer %s\r\nClient-Id: %s\r\n",
                     g_twitch_chat_oauth, g_twitch_client_id);
    if (h <= 0 || h >= (int)sizeof(header_utf8)) return;

    wchar_t header_w[2400];
    if (MultiByteToWideChar(CP_UTF8, 0, header_utf8, -1,
                            header_w, (int)(sizeof(header_w) / sizeof(header_w[0]))) <= 0) {
        return;
    }

    byte_buf_t body;
    if (!https_get_bytes_ex(L"api.twitch.tv", path_w,
                            L"application/json", header_w,
                            HTTP_MAX_JSON, &body)) {
        return;
    }

    char display[MAX_CHANNEL_DISPLAY] = {0};
    const char *json = (const char *)body.data;
    const char *end = json + body.size;
    if (json_get_string(json, end, "display_name",
                        display, sizeof(display))) {
        sanitize_channel_display_name(display);
        if (display[0]) {
            set_channel_display_name(display);
        }
    }
    byte_buf_free(&body);
}

static void load_twitch_chat_auth(void) {
    if (g_chat_provider != CHAT_PROVIDER_TWITCH) return;

    const char *env_login = getenv("TWITCH_CHAT_USERNAME");
    if (!env_login || !env_login[0]) {
        env_login = getenv("STREAMLINK_TWITCH_CHAT_USERNAME");
    }
    if (env_login && env_login[0]) {
        strncpy(g_twitch_chat_login, env_login, sizeof(g_twitch_chat_login) - 1);
        sanitize_twitch_login(g_twitch_chat_login);
    }

    const char *env_token = getenv("TWITCH_CHAT_OAUTH_TOKEN");
    if (!env_token || !env_token[0]) {
        env_token = getenv("STREAMLINK_TWITCH_CHAT_OAUTH_TOKEN");
    }
    if (env_token && env_token[0]) {
        strncpy(g_twitch_chat_oauth, env_token, sizeof(g_twitch_chat_oauth) - 1);
        strip_oauth_prefix(g_twitch_chat_oauth);
    }

    if (!g_twitch_chat_oauth[0]) {
        const char *env_token_file = getenv("TWITCH_CHAT_TOKEN_FILE");
        if (!env_token_file || !env_token_file[0]) {
            env_token_file = getenv("STREAMLINK_TWITCH_CHAT_TOKEN_FILE");
        }
        if (env_token_file && env_token_file[0]) {
            strncpy(g_twitch_chat_token_file, env_token_file,
                    sizeof(g_twitch_chat_token_file) - 1);
        }
        if (!g_twitch_chat_token_file[0]) {
            default_twitch_token_file(g_twitch_chat_token_file,
                                      sizeof(g_twitch_chat_token_file));
        }
        if (g_twitch_chat_token_file[0]) {
            read_text_file_trimmed(g_twitch_chat_token_file,
                                   g_twitch_chat_oauth,
                                   sizeof(g_twitch_chat_oauth));
            strip_oauth_prefix(g_twitch_chat_oauth);
        }
    }

    bool token_valid = false;
    bool token_can_send = false;
    if (g_twitch_chat_oauth[0]) {
        char validated_login[MAX_USERNAME] = {0};
        token_valid = validate_twitch_token_login(g_twitch_chat_oauth,
                                                  validated_login,
                                                  sizeof(validated_login),
                                                  &token_can_send);
        if (token_valid && validated_login[0]) {
            strncpy(g_twitch_chat_login, validated_login,
                    sizeof(g_twitch_chat_login) - 1);
            g_twitch_chat_login[sizeof(g_twitch_chat_login) - 1] = '\0';
        }
    }

    g_twitch_chat_auth_ready =
        token_valid && token_can_send
        && g_twitch_chat_oauth[0] && g_twitch_chat_login[0];
    if (g_twitch_chat_auth_ready) {
        char text[128];
        snprintf(text, sizeof(text), "chat input ready as %s", g_twitch_chat_login);
        queue_push(&g_queue, "chat", text, true);
    } else if (token_valid && !token_can_send) {
        queue_push(&g_queue, "chat",
                   "chat input token needs chat:edit scope", true);
    } else if (g_twitch_chat_oauth[0]) {
        queue_push(&g_queue, "chat",
                   "chat input token could not be validated", true);
    } else {
        queue_push(&g_queue, "chat",
                   "chat input needs a Twitch OAuth token", true);
    }
}

static void load_kick_chat_auth(void) {
    if (g_chat_provider != CHAT_PROVIDER_KICK) return;

    const char *env_token = getenv("KICK_CHAT_BEARER_TOKEN");
    if (!env_token || !env_token[0]) {
        env_token = getenv("STREAMLINK_KICK_CHAT_BEARER_TOKEN");
    }
    if (!env_token || !env_token[0]) {
        env_token = getenv("KICK_CHAT_OAUTH_TOKEN");
    }
    if (!env_token || !env_token[0]) {
        env_token = getenv("STREAMLINK_KICK_CHAT_OAUTH_TOKEN");
    }
    if (env_token && env_token[0]) {
        strncpy(g_kick_chat_token, env_token, sizeof(g_kick_chat_token) - 1);
        strip_oauth_prefix(g_kick_chat_token);
    }

    if (!g_kick_chat_token[0]) {
        const char *env_token_file = getenv("KICK_CHAT_TOKEN_FILE");
        if (!env_token_file || !env_token_file[0]) {
            env_token_file = getenv("STREAMLINK_KICK_CHAT_TOKEN_FILE");
        }
        if (env_token_file && env_token_file[0]) {
            strncpy(g_kick_chat_token_file, env_token_file,
                    sizeof(g_kick_chat_token_file) - 1);
        }
        if (!g_kick_chat_token_file[0]) {
            default_kick_token_file(g_kick_chat_token_file,
                                    sizeof(g_kick_chat_token_file));
        }
        if (g_kick_chat_token_file[0]) {
            read_text_file_trimmed(g_kick_chat_token_file,
                                   g_kick_chat_token,
                                   sizeof(g_kick_chat_token));
            strip_oauth_prefix(g_kick_chat_token);
        }
    }

    g_kick_chat_auth_ready =
        g_kick_chat_token[0] && ( g_kick_send_as_bot || g_kick_broadcaster_user_id[0] );

    if (g_kick_chat_auth_ready) {
        queue_push(&g_queue, "chat", "Kick chat input ready", true);
    } else if (!g_kick_chat_token[0]) {
        queue_push(&g_queue, "chat",
                   "Kick chat input needs a chat:write token", true);
    } else {
        queue_push(&g_queue, "chat",
                   "Kick chat input needs the broadcaster user id", true);
    }
}

static bool json_escape_utf8_string(const char *src, char *out, int out_cap) {
    if (!src || !out || out_cap <= 0) return false;
    int o = 0;
    for (const unsigned char *p = (const unsigned char *)src;
         *p;
         p++) {
        unsigned char c = *p;
        const char *esc = NULL;
        char uni[8];
        if (c == '"') esc = "\\\"";
        else if (c == '\\') esc = "\\\\";
        else if (c == '\b') esc = "\\b";
        else if (c == '\f') esc = "\\f";
        else if (c == '\n') esc = "\\n";
        else if (c == '\r') esc = "\\r";
        else if (c == '\t') esc = "\\t";
        else if (c < 0x20u) {
            snprintf(uni, sizeof(uni), "\\u%04x", c);
            esc = uni;
        }

        if (esc) {
            size_t n = strlen(esc);
            if (o + (int)n >= out_cap) return false;
            memcpy(out + o, esc, n);
            o += (int)n;
        } else {
            if (o + 1 >= out_cap) return false;
            out[o++] = (char)c;
        }
    }
    if (o + 1 > out_cap) return false;
    out[o] = '\0';
    return true;
}

static void irc_publish_send_connection(tls_conn_t *tls, bool can_send) {
    EnterCriticalSection(&g_irc_send_cs);
    g_irc_send_conn = tls;
    g_irc_send_ready = can_send;
    LeaveCriticalSection(&g_irc_send_cs);
}

static void irc_clear_send_connection(tls_conn_t *tls) {
    EnterCriticalSection(&g_irc_send_cs);
    if (g_irc_send_conn == tls) {
        g_irc_send_conn = NULL;
        g_irc_send_ready = false;
    }
    LeaveCriticalSection(&g_irc_send_cs);
}

static int irc_send_locked(tls_conn_t *tls, const char *data, int len) {
    int rc;
    EnterCriticalSection(&g_irc_send_cs);
    rc = tls_send(tls, data, len);
    LeaveCriticalSection(&g_irc_send_cs);
    return rc;
}

static bool should_suppress_local_echo(const char *user, const char *text) {
    if (!user || !text || !g_twitch_chat_login[0]) return false;
    if (_stricmp(user, g_twitch_chat_login) != 0) return false;

    bool suppress = false;
    EnterCriticalSection(&g_sent_echo_cs);
    if (g_last_sent_text[0]
        && strcmp(g_last_sent_text, text) == 0
        && GetTickCount64() - g_last_sent_ms < 10000) {
        g_last_sent_text[0] = '\0';
        suppress = true;
    }
    LeaveCriticalSection(&g_sent_echo_cs);
    return suppress;
}

static bool send_twitch_chat_message(const char *text) {
    if (!text || !text[0]) return false;
    if (g_chat_provider != CHAT_PROVIDER_TWITCH) {
        input_set_notice_ms(3500, "Twitch chat input only");
        return false;
    }
    if (!g_twitch_chat_auth_ready) {
        input_set_notice_ms(4500, "Twitch OAuth token needed");
        queue_push(&g_queue, "chat",
                   "cannot send: Twitch OAuth token needs chat:edit", true);
        return false;
    }

    char line[MAX_MSG_TEXT + 96];
    int n = snprintf(line, sizeof(line), "PRIVMSG #%s :%s\r\n",
                     g_channel, text);
    if (n <= 0 || n >= (int)sizeof(line)) {
        input_set_notice_ms(3000, "message too long");
        return false;
    }

    bool ok = false;
    EnterCriticalSection(&g_irc_send_cs);
    if (g_irc_send_conn && g_irc_send_ready) {
        ok = tls_send(g_irc_send_conn, line, n) == n;
    }
    LeaveCriticalSection(&g_irc_send_cs);

    if (!ok) {
        input_set_notice_ms(3000, "chat reconnecting");
        return false;
    }

    EnterCriticalSection(&g_sent_echo_cs);
    copy_utf8_truncated(g_last_sent_text, sizeof(g_last_sent_text), text);
    g_last_sent_ms = GetTickCount64();
    LeaveCriticalSection(&g_sent_echo_cs);

    queue_push_ex(&g_queue, g_twitch_chat_login, text, false,
                  NULL, 0, NULL, 0);
    return true;
}

static bool kick_send_chat_message_http(const char *text) {
    char escaped[MAX_MSG_TEXT * 6 + 1];
    if (!json_escape_utf8_string(text, escaped, sizeof(escaped))) {
        input_set_notice_ms(3000, "message could not be encoded");
        return false;
    }

    char body[4096];
    int body_len = snprintf(body, sizeof(body),
                            g_kick_send_as_bot
                                ? "{\"type\":\"bot\",\"content\":\"%s\"}"
                                : "{\"type\":\"user\",\"content\":\"%s\","
                                  "\"broadcaster_user_id\":%s}",
                            escaped, g_kick_broadcaster_user_id);
    if (body_len <= 0 || body_len >= (int)sizeof(body)) {
        input_set_notice_ms(3000, "message too long");
        return false;
    }

    char header_utf8[MAX_OAUTH_TOKEN + 64];
    int h = snprintf(header_utf8, sizeof(header_utf8),
                     "Authorization: Bearer %s\r\n", g_kick_chat_token);
    if (h <= 0 || h >= (int)sizeof(header_utf8)) {
        input_set_notice_ms(3000, "Kick token too long");
        return false;
    }

    wchar_t header_w[2300];
    if (MultiByteToWideChar(CP_UTF8, 0, header_utf8, -1,
                            header_w, (int)(sizeof(header_w) / sizeof(header_w[0]))) <= 0) {
        input_set_notice_ms(3000, "Kick token could not be encoded");
        return false;
    }

    byte_buf_t response;
    DWORD status = 0;
    if (!https_post_json_bytes(L"api.kick.com", L"/public/v1/chat",
                               header_w, body, (size_t)body_len,
                               HTTP_MAX_JSON, &status, &response)) {
        input_set_notice_ms(3500, "Kick send failed");
        return false;
    }

    const char *json = (const char *)response.data;
    const char *end = json + response.size;
    const char *data_obj = NULL;
    const char *data_end = NULL;
    bool sent = false;
    if (json_get_object_direct(json, end, "data", &data_obj, &data_end)) {
        sent = json_get_bool_default_direct(data_obj, data_end,
                                           "is_sent", false);
    }

    if (status >= 200 && status < 300 && sent) {
        byte_buf_free(&response);
        input_set_notice_ms(1600, "sent");
        return true;
    }

    char message[96] = {0};
    (void)json_get_string_direct(json, end, "message", message, sizeof(message));
    fprintf(stderr,
            "[%lu] Kick chat send failed: HTTP %lu body=%.*s\n",
            (unsigned long)(GetTickCount64() / 1000),
            (unsigned long)status,
            response.size > 512 ? 512 : (int)response.size,
            response.data ? (const char *)response.data : "");
    byte_buf_free(&response);
    if (message[0]) {
        input_set_notice_ms(4500, "%s", message);
    } else if (status == 401 || status == 403) {
        input_set_notice_ms(4500, "Kick token lacks chat:write");
    } else if (status) {
        input_set_notice_ms(4500, "Kick send failed (%lu)", (unsigned long)status);
    } else {
        input_set_notice_ms(3500, "Kick send failed");
    }
    return false;
}

static DWORD WINAPI kick_send_worker(LPVOID arg) {
    char *text = (char *)arg;
    if (text) {
        (void)kick_send_chat_message_http(text);
        free(text);
    }
    return 0;
}

static bool send_kick_chat_message(const char *text) {
    if (!text || !text[0]) return false;
    if (g_chat_provider != CHAT_PROVIDER_KICK) {
        input_set_notice_ms(3500, "Kick chat input only");
        return false;
    }
    if (!g_kick_chat_token[0]) {
        input_set_notice_ms(4500, "Kick token needed");
        queue_push(&g_queue, "chat",
                   "cannot send: Kick token needs chat:write", true);
        return false;
    }
    if (!g_kick_send_as_bot && !g_kick_broadcaster_user_id[0]) {
        input_set_notice_ms(4500, "Kick channel id needed");
        queue_push(&g_queue, "chat",
                   "cannot send: missing Kick broadcaster user id", true);
        return false;
    }
    if (strlen(text) > 500) {
        input_set_notice_ms(3000, "message too long");
        return false;
    }

    size_t len = strlen(text);
    char *copy = (char *)malloc(len + 1);
    if (!copy) {
        input_set_notice_ms(3000, "out of memory");
        return false;
    }
    memcpy(copy, text, len + 1);

    input_set_notice_ms(2000, "sending...");
    HANDLE thread = CreateThread(NULL, 0, kick_send_worker, copy, 0, NULL);
    if (!thread) {
        free(copy);
        input_set_notice_ms(3000, "send thread failed");
        return false;
    }
    CloseHandle(thread);
    return true;
}

static void submit_current_input(void) {
    char text[MAX_MSG_TEXT];
    if (!input_take_submit_text(text, sizeof(text))) return;
    if (g_chat_provider == CHAT_PROVIDER_KICK)
        (void)send_kick_chat_message(text);
    else
        (void)send_twitch_chat_message(text);
}

static void bttv_catalog_init(void) {
    InitializeCriticalSection(&g_bttv.cs);
    g_bttv.count = 0;
    g_bttv.global_loaded = false;
    g_bttv.channel_loaded = false;
    g_bttv.ffz_global_loaded = false;
    g_bttv.ffz_channel_loaded = false;
    g_bttv.seventv_global_loaded = false;
    g_bttv.seventv_channel_loaded = false;
    g_bttv.twitch_global_loaded = false;
    g_bttv.twitch_channel_loaded = false;
    g_bttv.twitch_badges_global_loaded = false;
    g_bttv.twitch_badges_channel_loaded = false;
    g_bttv.channel_room_id[0] = '\0';
    g_bttv.ffz_channel_room_id[0] = '\0';
    g_bttv.seventv_channel_room_id[0] = '\0';
    g_bttv.twitch_channel_login[0] = '\0';
    g_bttv.twitch_badges_channel_room_id[0] = '\0';
    g_twitch_subscriber_badge_version_count = 0;
}

static void emote_reset_image_locked(bttv_emote_t *e);

static void bttv_catalog_destroy(void) {
    EnterCriticalSection(&g_bttv.cs);
    for (int i = 0; i < g_bttv.count; i++) {
        emote_reset_image_locked(&g_bttv.items[i]);
    }
    LeaveCriticalSection(&g_bttv.cs);
    DeleteCriticalSection(&g_bttv.cs);
}

static int bttv_find_locked(const char *code) {
    for (int i = 0; i < g_bttv.count; i++) {
        if (strcmp(g_bttv.items[i].code, code) == 0) return i;
    }
    return -1;
}

static void emote_reset_image_locked(bttv_emote_t *e) {
    if (e->image) {
        GdipDisposeImage(e->image);
        e->image = NULL;
    }
    if (e->image_stream) {
        e->image_stream->lpVtbl->Release(e->image_stream);
        e->image_stream = NULL;
    }
    free(e->frame_delays_ms);
    e->frame_delays_ms = NULL;
    e->animated = false;
    e->frame_count = 0;
    e->current_frame = 0;
    e->total_frame_delay_ms = 0;
    e->animation_started_ms = 0;
    memset(&e->frame_dimension, 0, sizeof(e->frame_dimension));
    e->image_w = 0;
    e->image_h = 0;
    e->loading_image = false;
}

static bool emote_catalog_add_direct(const char *code, const char *host,
                                     const char *path, int api_w, int api_h)
{
    if (!code || !code[0] || !host || !host[0] || !path || !path[0]) return false;
    if ((int)strlen(code) >= BTTV_CODE_MAX
        || (int)strlen(host) >= (int)sizeof(g_bttv.items[0].host)
        || (int)strlen(path) >= (int)sizeof(g_bttv.items[0].path)) {
        return false;
    }

    EnterCriticalSection(&g_bttv.cs);
    int idx = bttv_find_locked(code);
    if (idx < 0) {
        if (g_bttv.count >= BTTV_MAX_EMOTES) {
            LeaveCriticalSection(&g_bttv.cs);
            return false;
        }
        idx = g_bttv.count++;
    }

    bttv_emote_t *e = &g_bttv.items[idx];
    bool same_image = !e->local_file
                   && e->host[0]
                   && strcmp(e->host, host) == 0
                   && strcmp(e->path, path) == 0;
    if (!same_image)
        emote_reset_image_locked(e);
    size_t code_len = strlen(code);
    memcpy(e->code, code, code_len + 1);
    e->id[0] = '\0';
    e->image_type[0] = '\0';
    strcpy(e->host, host);
    strcpy(e->path, path);
    e->local_file = false;
    e->api_w = api_w;
    e->api_h = api_h;
    if (!same_image) {
        e->tried_image = false;
    }
    LeaveCriticalSection(&g_bttv.cs);
    return true;
}

static bool emote_catalog_add_file(const char *code, const char *path,
                                   int api_w, int api_h)
{
    if (!code || !code[0] || !path || !path[0]) return false;
    if ((int)strlen(code) >= BTTV_CODE_MAX
        || (int)strlen(path) >= (int)sizeof(g_bttv.items[0].path)) {
        return false;
    }

    DWORD attrs = GetFileAttributesA(path);
    if (attrs == INVALID_FILE_ATTRIBUTES || (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0) {
        return false;
    }

    EnterCriticalSection(&g_bttv.cs);
    int idx = bttv_find_locked(code);
    if (idx < 0) {
        if (g_bttv.count >= BTTV_MAX_EMOTES) {
            LeaveCriticalSection(&g_bttv.cs);
            return false;
        }
        idx = g_bttv.count++;
    }

    bttv_emote_t *e = &g_bttv.items[idx];
    bool same_image = e->local_file && strcmp(e->path, path) == 0;
    if (!same_image) {
        emote_reset_image_locked(e);
    }

    size_t code_len = strlen(code);
    memcpy(e->code, code, code_len + 1);
    e->id[0] = '\0';
    e->image_type[0] = '\0';
    e->host[0] = '\0';
    strcpy(e->path, path);
    e->local_file = true;
    e->api_w = api_w;
    e->api_h = api_h;
    if (!same_image) {
        e->tried_image = false;
    }
    LeaveCriticalSection(&g_bttv.cs);
    return true;
}

static bool emote_catalog_contains(const char *code)
{
    if (!code || !code[0]) return false;

    EnterCriticalSection(&g_bttv.cs);
    bool found = bttv_find_locked(code) >= 0;
    LeaveCriticalSection(&g_bttv.cs);
    return found;
}

static bool bttv_catalog_add(const char *code, const char *id,
                             const char *image_type, int api_w, int api_h)
{
    if (!code || !code[0] || !id || !id[0]) return false;
    if ((int)strlen(code) >= BTTV_CODE_MAX || (int)strlen(id) >= BTTV_ID_MAX) return false;

    const char *type = (image_type && image_type[0]) ? image_type : "png";
    char path[220];
    snprintf(path, sizeof(path), "/emote/%s/1x.%s", id, type);
    return emote_catalog_add_direct(code, "cdn.betterttv.net", path, api_w, api_h);
}

static void add_builtin_emote_fallbacks(void) {
    /* 7TV personal/global search can expose emotes that are not in the
     * broadcaster set endpoint. Keep a tiny exact-code fallback for known
     * high-traffic misses instead of showing the raw text. */
    (void)emote_catalog_add_direct("RespectfullyNo", "cdn.7tv.app",
                                   "/emote/01K65KFQ64QEPPWVW055JMNBWY/1x.gif",
                                   32, 32);
    (void)emote_catalog_add_direct("RespectfullyNO", "cdn.7tv.app",
                                   "/emote/01K7SRC7DHWSB0AGB81DM10T26/1x.gif",
                                   32, 32);
    (void)emote_catalog_add_direct("respectfullyno", "cdn.7tv.app",
                                   "/emote/01K6N6FWRAEPB4BBNNFYV1G5MJ/1x.gif",
                                   32, 32);
    (void)emote_catalog_add_direct("Uppies", "cdn.betterttv.net",
                                   "/emote/61818ec01f8ff7628e6c1e0b/1x.gif",
                                   28, 28);
    (void)emote_catalog_add_direct("Yo", "cdn.7tv.app",
                                   "/emote/01GKFRT59000047SF1NR3YD3WA/1x.gif",
                                   32, 32);
    (void)emote_catalog_add_direct("nickmercsW", "static-cdn.jtvnw.net",
                                   "/emoticons/v2/emotesv2_61905b27c9b649e8af5c92e1a5c3cd64/default/light/1.0",
                                   28, 28);
}

static int bttv_parse_emotes_json(const char *json, size_t len) {
    const char *end = json + len;
    const char *p = json;
    int added = 0;

    while (p < end) {
        const char *obj = memchr(p, '{', (size_t)(end - p));
        if (!obj) break;

        bool nested = false;
        const char *obj_end = json_object_end(obj, end, &nested);
        if (!obj_end) break;

        char id[BTTV_ID_MAX] = {0};
        char code[BTTV_CODE_MAX] = {0};
        char type[BTTV_TYPE_MAX] = {0};
        bool modifier = json_get_bool_default_direct(obj, obj_end,
                                                     "modifier", false);
        if (!modifier
            && json_get_string_direct(obj, obj_end, "id", id, sizeof(id))
            && json_get_string_direct(obj, obj_end, "code", code, sizeof(code))) {
            (void)json_get_string_direct(obj, obj_end, "imageType",
                                         type, sizeof(type));
            int w = json_get_int_default_direct(obj, obj_end, "width", 0);
            int h = json_get_int_default_direct(obj, obj_end, "height", 0);
            if (bttv_catalog_add(code, id, type, w, h))
                added++;
        }
        p = obj + 1;
    }
    return added;
}

static bool emote_catalog_add_url(const char *code, const char *url,
                                  int api_w, int api_h)
{
    char host[80];
    char path[IMAGE_PATH_MAX];
    if (split_https_url(url, host, sizeof(host), path, sizeof(path))) {
        return emote_catalog_add_direct(code, host, path, api_w, api_h);
    }
    return false;
}

static bool is_kick_asset_host(const char *host)
{
    if (!host || !host[0]) return false;
    if (_stricmp(host, "kick.com") == 0) return true;

    const char *suffix = ".kick.com";
    size_t host_len = strlen(host);
    size_t suffix_len = strlen(suffix);
    return host_len > suffix_len
        && _stricmp(host + host_len - suffix_len, suffix) == 0;
}

static bool normalize_kick_asset_url(const char *url, char *out, int out_size)
{
    if (!url || !out || out_size <= 0) return false;

    char trimmed[IMAGE_PATH_MAX];
    snprintf(trimmed, sizeof(trimmed), "%s", url);
    trim_ascii_in_place(trimmed);
    if (!trimmed[0]) return false;

    char normalized[IMAGE_PATH_MAX];
    if (strncmp(trimmed, "//", 2) == 0) {
        int n = snprintf(normalized, sizeof(normalized), "https:%s", trimmed);
        if (n <= 0 || n >= (int)sizeof(normalized)) return false;
    } else if (trimmed[0] == '/' && trimmed[1] != '/') {
        int n = snprintf(normalized, sizeof(normalized), "https://kick.com%s",
                         trimmed);
        if (n <= 0 || n >= (int)sizeof(normalized)) return false;
    } else if (_strnicmp(trimmed, "https://", 8) == 0) {
        snprintf(normalized, sizeof(normalized), "%s", trimmed);
    } else {
        return false;
    }

    char host[80];
    char path[IMAGE_PATH_MAX];
    if (!split_https_url(normalized, host, sizeof(host), path, sizeof(path))
        || !is_kick_asset_host(host)) {
        return false;
    }

    int n = snprintf(out, (size_t)out_size, "%s", normalized);
    return n > 0 && n < out_size;
}

static uint32_t fnv1a_32(const char *text)
{
    uint32_t hash = 2166136261u;
    if (!text) return hash;
    for (const unsigned char *p = (const unsigned char *)text; *p; p++) {
        hash ^= (uint32_t)(*p);
        hash *= 16777619u;
    }
    return hash;
}

static bool make_kick_badge_code_from_url(const char *image_url,
                                          char *out, int out_size)
{
    if (!image_url || !image_url[0] || !out || out_size <= 0) return false;
    uint32_t hash = fnv1a_32(image_url);
    int n = snprintf(out, (size_t)out_size, "badge:kick:url:%08x", hash);
    return n > 0 && n < out_size;
}

static void normalize_badge_id(const char *id, char *out, int out_size);
static bool ascii_equals_ci(const char *a, const char *b);
static bool is_kick_gift_badge_id(const char *id);

static void normalize_badge_version(const char *version,
                                    char *out, int out_size)
{
    if (!out || out_size <= 0) return;
    int o = 0;
    if (version) {
        while (*version && isspace((unsigned char)*version)) version++;
        for (; *version && o + 1 < out_size; version++) {
            unsigned char c = (unsigned char)*version;
            if (isspace(c) || c == ',') break;
            out[o++] = (char)c;
        }
    }
    while (o > 0 && isspace((unsigned char)out[o - 1])) o--;
    out[o] = '\0';
}

static bool make_twitch_badge_code_raw(const char *scope,
                                       const char *room_id,
                                       const char *id,
                                       const char *version,
                                       char *out, int out_size)
{
    char normalized_id[MAX_BADGE_ID] = {0};
    char normalized_version[MAX_BADGE_VERSION] = {0};
    char normalized_room[64] = {0};
    normalize_badge_id(id, normalized_id, sizeof(normalized_id));
    normalize_badge_version(version, normalized_version,
                            sizeof(normalized_version));
    normalize_badge_version(room_id, normalized_room,
                            sizeof(normalized_room));
    if (!normalized_id[0] || !normalized_version[0]
        || !out || out_size <= 0) {
        if (out && out_size > 0) out[0] = '\0';
        return false;
    }

    int n = 0;
    if (scope && ascii_equals_ci(scope, "channel")) {
        if (!normalized_room[0]) {
            out[0] = '\0';
            return false;
        }
        n = snprintf(out, (size_t)out_size, "badge:twitch:channel:%s:%s:%s",
                     normalized_room, normalized_id, normalized_version);
    } else {
        n = snprintf(out, (size_t)out_size, "badge:twitch:global:%s:%s",
                     normalized_id, normalized_version);
    }
    return n > 0 && n < out_size;
}

static bool make_twitch_global_badge_code(const char *id, const char *version,
                                          char *out, int out_size)
{
    return make_twitch_badge_code_raw("global", NULL, id, version,
                                      out, out_size);
}

static bool make_twitch_channel_badge_code(const char *room_id,
                                           const char *id,
                                           const char *version,
                                           char *out, int out_size)
{
    return make_twitch_badge_code_raw("channel", room_id, id, version,
                                      out, out_size);
}

static bool make_twitch_badge_code(const char *id, const char *version,
                                   char *out, int out_size)
{
    return make_twitch_global_badge_code(id, version, out, out_size);
}

static bool append_badge_candidate(char candidates[][MAX_BADGE_ID],
                                   int *count, int cap,
                                   const char *candidate)
{
    if (!candidates || !count || *count >= cap
        || !candidate || !candidate[0]) {
        return false;
    }

    for (int i = 0; i < *count; i++) {
        if (ascii_equals_ci(candidates[i], candidate)) {
            return false;
        }
    }

    snprintf(candidates[*count], MAX_BADGE_ID, "%s", candidate);
    (*count)++;
    return true;
}

static void copy_replace_char(const char *src, char from, char to,
                              char *out, int out_size)
{
    if (!out || out_size <= 0) return;
    int o = 0;
    if (src) {
        for (; *src && o + 1 < out_size; src++) {
            out[o++] = *src == from ? to : *src;
        }
    }
    out[o] = '\0';
}

static void normalize_twitch_badge_alias(const char *id,
                                         char *out, int out_size)
{
    if (!out || out_size <= 0) return;

    char hyphenated[MAX_BADGE_ID] = {0};
    copy_replace_char(id, '_', '-', hyphenated, sizeof(hyphenated));

    const char *alias = hyphenated;
    if (ascii_equals_ci(hyphenated, "sub-gift")
        || ascii_equals_ci(hyphenated, "sub-gifter")
        || ascii_equals_ci(hyphenated, "sub-gifter-badge")
        || ascii_equals_ci(hyphenated, "subgifter")) {
        alias = "sub-gifter";
    } else if (ascii_equals_ci(hyphenated, "sub-gift-leader")) {
        alias = "sub-gift-leader";
    }

    snprintf(out, (size_t)out_size, "%s", alias);
}

static int candidate_twitch_badge_ids(const char *id,
                                      char candidates[][MAX_BADGE_ID],
                                      int cap)
{
    int count = 0;
    char normalized[MAX_BADGE_ID] = {0};
    normalize_badge_id(id, normalized, sizeof(normalized));
    if (!normalized[0]) return 0;

    append_badge_candidate(candidates, &count, cap, normalized);

    char swapped[MAX_BADGE_ID] = {0};
    copy_replace_char(normalized, '_', '-', swapped, sizeof(swapped));
    append_badge_candidate(candidates, &count, cap, swapped);

    copy_replace_char(normalized, '-', '_', swapped, sizeof(swapped));
    append_badge_candidate(candidates, &count, cap, swapped);

    char alias[MAX_BADGE_ID] = {0};
    normalize_twitch_badge_alias(normalized, alias, sizeof(alias));
    append_badge_candidate(candidates, &count, cap, alias);

    return count;
}

static int candidate_badge_versions(const char *version,
                                    char candidates[][MAX_BADGE_VERSION],
                                    int cap,
                                    bool fallback_when_version_present,
                                    bool include_zero_fallback)
{
    int count = 0;
    char normalized[MAX_BADGE_VERSION] = {0};
    normalize_badge_version(version, normalized, sizeof(normalized));

    if (normalized[0]) {
        for (int i = 0; i < count; i++) {
            if (ascii_equals_ci(candidates[i], normalized)) {
                normalized[0] = '\0';
                break;
            }
        }
        if (normalized[0] && count < cap) {
            snprintf(candidates[count], MAX_BADGE_VERSION, "%s", normalized);
            count++;
        }
    }

    if (count > 0 && !fallback_when_version_present) {
        return count;
    }

    const char *fallbacks[] = { "1", "0" };
    int fallback_count = include_zero_fallback ? 2 : 1;
    for (int f = 0; f < fallback_count && count < cap; f++) {
        bool seen = false;
        for (int i = 0; i < count; i++) {
            if (ascii_equals_ci(candidates[i], fallbacks[f])) {
                seen = true;
                break;
            }
        }
        if (!seen) {
            snprintf(candidates[count], MAX_BADGE_VERSION, "%s", fallbacks[f]);
            count++;
        }
    }

    return count;
}

static bool append_badge_version_candidate(
    char candidates[][MAX_BADGE_VERSION],
    int *count, int cap, const char *candidate)
{
    if (!candidates || !count || *count >= cap
        || !candidate || !candidate[0]) {
        return false;
    }

    for (int i = 0; i < *count; i++) {
        if (ascii_equals_ci(candidates[i], candidate)) {
            return false;
        }
    }

    snprintf(candidates[*count], MAX_BADGE_VERSION, "%s", candidate);
    (*count)++;
    return true;
}

static bool is_twitch_subscriber_badge_id(const char *id)
{
    char normalized[MAX_BADGE_ID] = {0};
    normalize_badge_id(id, normalized, sizeof(normalized));
    return ascii_equals_ci(normalized, "subscriber")
        || ascii_equals_ci(normalized, "sub");
}

static bool twitch_badges_current_channel_room_id(char *out, int out_size)
{
    if (!out || out_size <= 0) return false;
    out[0] = '\0';

    EnterCriticalSection(&g_bttv.cs);
    bool loaded = g_bttv.twitch_badges_channel_loaded
               && g_bttv.twitch_badges_channel_room_id[0];
    if (loaded) {
        snprintf(out, (size_t)out_size, "%s",
                 g_bttv.twitch_badges_channel_room_id);
    }
    LeaveCriticalSection(&g_bttv.cs);
    return loaded;
}

static void twitch_register_subscriber_badge_version(int version)
{
    if (version < 0) return;

    EnterCriticalSection(&g_bttv.cs);
    for (int i = 0; i < g_twitch_subscriber_badge_version_count; i++) {
        if (g_twitch_subscriber_badge_versions[i] == version) {
            LeaveCriticalSection(&g_bttv.cs);
            return;
        }
    }
    if (g_twitch_subscriber_badge_version_count >= TWITCH_BADGE_VERSION_CAP) {
        LeaveCriticalSection(&g_bttv.cs);
        return;
    }

    int index = g_twitch_subscriber_badge_version_count++;
    g_twitch_subscriber_badge_versions[index] = version;
    while (index > 0
           && g_twitch_subscriber_badge_versions[index - 1]
              > g_twitch_subscriber_badge_versions[index]) {
        int tmp = g_twitch_subscriber_badge_versions[index - 1];
        g_twitch_subscriber_badge_versions[index - 1] =
            g_twitch_subscriber_badge_versions[index];
        g_twitch_subscriber_badge_versions[index] = tmp;
        index--;
    }
    LeaveCriticalSection(&g_bttv.cs);
}

static bool parse_nonnegative_int_string(const char *value, int *out)
{
    if (!value || !value[0] || !out) return false;
    char *end = NULL;
    long parsed = strtol(value, &end, 10);
    if (end == value) return false;
    while (end && *end && isspace((unsigned char)*end)) end++;
    if (end && *end) return false;
    if (parsed < 0 || parsed > INT_MAX) return false;
    *out = (int)parsed;
    return true;
}

static bool twitch_best_subscriber_badge_version(const char *requested_version,
                                                 char *out, int out_size)
{
    int requested = 0;
    if (!parse_nonnegative_int_string(requested_version, &requested)) {
        return false;
    }

    EnterCriticalSection(&g_bttv.cs);
    int selected = -1;
    for (int i = 0; i < g_twitch_subscriber_badge_version_count; i++) {
        int candidate = g_twitch_subscriber_badge_versions[i];
        if (candidate > requested) break;
        selected = candidate;
    }
    LeaveCriticalSection(&g_bttv.cs);

    if (selected < 0) return false;
    int n = snprintf(out, (size_t)out_size, "%d", selected);
    return n > 0 && n < out_size;
}

static bool make_twitch_badge_code_for_request(const char *id,
                                               const char *version,
                                               char *out, int out_size,
                                               int *cursor)
{
    if (!out || out_size <= 0 || !cursor) return false;

    char ids[4][MAX_BADGE_ID] = {{0}};
    char versions[4][MAX_BADGE_VERSION] = {{0}};
    int id_count = candidate_twitch_badge_ids(id, ids, 4);
    int version_count = 0;
    char normalized_version[MAX_BADGE_VERSION] = {0};
    normalize_badge_version(version, normalized_version,
                            sizeof(normalized_version));
    append_badge_version_candidate(versions, &version_count, 4,
                                   normalized_version);

    char channel_room_id[64] = {0};
    bool has_channel_badges =
        twitch_badges_current_channel_room_id(channel_room_id,
                                             sizeof(channel_room_id));
    if (has_channel_badges && is_twitch_subscriber_badge_id(id)) {
        char selected_version[MAX_BADGE_VERSION] = {0};
        if (twitch_best_subscriber_badge_version(
                normalized_version, selected_version,
                sizeof(selected_version))) {
            append_badge_version_candidate(versions, &version_count, 4,
                                           selected_version);
        }
    }

    append_badge_version_candidate(versions, &version_count, 4, "1");
    append_badge_version_candidate(versions, &version_count, 4, "0");

    int scope_count = (has_channel_badges ? 1 : 0) + 1;
    int total = id_count * version_count * scope_count;
    if (total <= 0 || id_count <= 0 || version_count <= 0) {
        out[0] = '\0';
        return false;
    }

    for (int i = *cursor; i < total; i++) {
        int per_scope = id_count * version_count;
        int scope_index = i / per_scope;
        int remainder = i % per_scope;
        int id_index = remainder / version_count;
        int version_index = remainder % version_count;
        *cursor = i + 1;

        bool use_channel_scope = has_channel_badges && scope_index == 0;
        if (use_channel_scope) {
            if (make_twitch_channel_badge_code(channel_room_id,
                                               ids[id_index],
                                               versions[version_index],
                                               out, out_size)) {
                return true;
            }
        } else if (make_twitch_global_badge_code(ids[id_index],
                                                 versions[version_index],
                                                 out, out_size)) {
            return true;
        }
    }

    out[0] = '\0';
    return false;
}

static bool parse_positive_int_string(const char *value, int *out)
{
    if (!value || !value[0] || !out) return false;
    char *end = NULL;
    long parsed = strtol(value, &end, 10);
    if (end == value) return false;
    while (end && *end && isspace((unsigned char)*end)) end++;
    if (end && *end) return false;
    if (parsed <= 0 || parsed > INT_MAX) return false;
    *out = (int)parsed;
    return true;
}

static bool make_kick_badge_code(const char *id, const char *version,
                                 char *out, int out_size)
{
    char normalized_id[MAX_BADGE_ID] = {0};
    char normalized_version[MAX_BADGE_VERSION] = {0};
    normalize_badge_id(id, normalized_id, sizeof(normalized_id));
    normalize_badge_version(version, normalized_version,
                            sizeof(normalized_version));
    if (!normalized_id[0] || !out || out_size <= 0) {
        if (out && out_size > 0) out[0] = '\0';
        return false;
    }

    int n = normalized_version[0]
          ? snprintf(out, (size_t)out_size, "badge:kick:%s:%s",
                     normalized_id, normalized_version)
          : snprintf(out, (size_t)out_size, "badge:kick:%s",
                     normalized_id);
    return n > 0 && n < out_size;
}

static void kick_register_subscriber_badge_version(int version)
{
    if (version <= 0) return;
    for (int i = 0; i < g_kick_subscriber_badge_version_count; i++) {
        if (g_kick_subscriber_badge_versions[i] == version) return;
    }
    if (g_kick_subscriber_badge_version_count >= KICK_BADGE_VERSION_CAP) {
        return;
    }

    int index = g_kick_subscriber_badge_version_count++;
    g_kick_subscriber_badge_versions[index] = version;
    while (index > 0
           && g_kick_subscriber_badge_versions[index - 1]
              > g_kick_subscriber_badge_versions[index]) {
        int tmp = g_kick_subscriber_badge_versions[index - 1];
        g_kick_subscriber_badge_versions[index - 1] =
            g_kick_subscriber_badge_versions[index];
        g_kick_subscriber_badge_versions[index] = tmp;
        index--;
    }
}

static bool kick_best_subscriber_badge_version(const char *requested_version,
                                               char *out, int out_size)
{
    int requested = 0;
    if (!parse_positive_int_string(requested_version, &requested)) {
        return false;
    }

    int selected = 0;
    for (int i = 0; i < g_kick_subscriber_badge_version_count; i++) {
        int candidate = g_kick_subscriber_badge_versions[i];
        if (candidate > requested) break;
        selected = candidate;
    }
    if (selected <= 0) return false;

    int n = snprintf(out, (size_t)out_size, "%d", selected);
    return n > 0 && n < out_size;
}

static int candidate_kick_badge_ids(const char *id,
                                    char candidates[][MAX_BADGE_ID],
                                    int cap)
{
    char normalized_id[MAX_BADGE_ID] = {0};
    normalize_badge_id(id, normalized_id, sizeof(normalized_id));
    if (!normalized_id[0]) return 0;

    int count = 0;
    append_badge_candidate(candidates, &count, cap, normalized_id);

    char swapped[MAX_BADGE_ID] = {0};
    copy_replace_char(normalized_id, '_', '-', swapped, sizeof(swapped));
    append_badge_candidate(candidates, &count, cap, swapped);

    copy_replace_char(normalized_id, '-', '_', swapped, sizeof(swapped));
    append_badge_candidate(candidates, &count, cap, swapped);

    char alias[MAX_BADGE_ID] = {0};
    normalize_badge_id(normalized_id, alias, sizeof(alias));
    append_badge_candidate(candidates, &count, cap, alias);

    return count;
}

static bool make_kick_badge_code_for_request(const char *id,
                                             const char *version,
                                             char *out, int out_size,
                                             int *cursor)
{
    if (!out || out_size <= 0 || !cursor) return false;

    char ids[4][MAX_BADGE_ID] = {{0}};
    char versions[3][MAX_BADGE_VERSION] = {{0}};
    int id_count = candidate_kick_badge_ids(id, ids, 4);
    char normalized_id[MAX_BADGE_ID] = {0};
    normalize_badge_id(id, normalized_id, sizeof(normalized_id));
    int version_count = candidate_badge_versions(
        version, versions, 3, is_kick_gift_badge_id(normalized_id), true);
    if (id_count <= 0) {
        out[0] = '\0';
        return false;
    }

    char selected_version[MAX_BADGE_VERSION] = {0};
    if (ascii_equals_ci(normalized_id, "subscriber")
        && kick_best_subscriber_badge_version(version,
                                              selected_version,
                                              sizeof(selected_version))) {
        bool seen = false;
        for (int i = 0; i < version_count; i++) {
            if (ascii_equals_ci(versions[i], selected_version)) {
                seen = true;
                break;
            }
        }
        if (!seen) {
            if (version_count < 3) version_count++;
            for (int i = version_count - 1; i > 0; i--) {
                strncpy(versions[i], versions[i - 1], MAX_BADGE_VERSION - 1);
                versions[i][MAX_BADGE_VERSION - 1] = '\0';
            }
            strncpy(versions[0], selected_version, MAX_BADGE_VERSION - 1);
            versions[0][MAX_BADGE_VERSION - 1] = '\0';
        }
    }

    int total = id_count * version_count;
    for (int i = *cursor; i < total; i++) {
        int id_index = i / version_count;
        int version_index = i % version_count;
        *cursor = i + 1;
        if (make_kick_badge_code(ids[id_index], versions[version_index],
                                 out, out_size)) {
            return true;
        }
    }

    out[0] = '\0';
    return false;
}

static int ffz_parse_emotes_json(const char *json, size_t len) {
    const char *end = json + len;
    const char *p = json;
    int added = 0;

    while (p < end) {
        const char *obj = memchr(p, '{', (size_t)(end - p));
        if (!obj) break;

        bool nested = false;
        const char *obj_end = json_object_end(obj, end, &nested);
        if (!obj_end) break;

        char code[BTTV_CODE_MAX] = {0};
        char url[220] = {0};
        if (bounded_strstr(obj, obj_end, "\"urls\"")
            && json_get_string(obj, obj_end, "name", code, sizeof(code))
            && json_get_string(obj, obj_end, "1", url, sizeof(url))) {
            bool modifier = json_get_bool_default(obj, obj_end, "modifier", false);
            int w = json_get_int_default(obj, obj_end, "width", 0);
            int h = json_get_int_default(obj, obj_end, "height", 0);
            if (!modifier) {
                if (emote_catalog_add_url(code, url, w, h))
                    added++;
            }
        }

        p = obj + 1;
    }
    return added;
}

static bool json_contains_string(const char *obj, const char *obj_end,
                                 const char *needle)
{
    char quoted[96];
    snprintf(quoted, sizeof(quoted), "\"%s\"", needle);
    return bounded_strstr(obj, obj_end, quoted) != NULL;
}

static bool seventv_catalog_add_from_host(const char *code,
                                          const char *host_obj,
                                          const char *host_end)
{
    char base_url[180] = {0};
    if (!json_get_string_direct(host_obj, host_end, "url",
                                base_url, sizeof(base_url))) {
        return false;
    }

    const char *file = json_contains_string(host_obj, host_end, "1x.gif")
                     ? "1x.gif"
                     : (json_contains_string(host_obj, host_end, "1x.png")
                        ? "1x.png" : NULL);
    if (!file) return false;

    char url[220];
    int n = snprintf(url, sizeof(url), "%s/%s", base_url, file);
    if (n <= 0 || n >= (int)sizeof(url)) return false;

    return emote_catalog_add_url(code, url, 0, 0);
}

static bool seventv_parse_emote_object(const char *obj, const char *obj_end)
{
    char code[BTTV_CODE_MAX] = {0};
    if (!json_get_string_direct(obj, obj_end, "name", code, sizeof(code))) {
        return false;
    }

    const char *data_obj = NULL;
    const char *data_end = NULL;
    if (json_get_object_direct(obj, obj_end, "data", &data_obj, &data_end)) {
        const char *host_obj = NULL;
        const char *host_end = NULL;
        if (json_get_object_direct(data_obj, data_end,
                                   "host", &host_obj, &host_end)) {
            return seventv_catalog_add_from_host(code, host_obj, host_end);
        }
        return false;
    }

    const char *host_obj = NULL;
    const char *host_end = NULL;
    if (json_get_object_direct(obj, obj_end, "host", &host_obj, &host_end)) {
        return seventv_catalog_add_from_host(code, host_obj, host_end);
    }

    return false;
}

static int seventv_parse_emotes_json(const char *json, size_t len) {
    const char *end = json + len;
    const char *p = json;
    int added = 0;

    while (p < end) {
        const char *obj = memchr(p, '{', (size_t)(end - p));
        if (!obj) break;

        bool nested = false;
        const char *obj_end = json_object_end(obj, end, &nested);
        if (!obj_end) break;

        if (seventv_parse_emote_object(obj, obj_end)) {
            added++;
        }

        p = obj + 1;
    }
    return added;
}

static int twitch_api_parse_emotes_json(const char *json, size_t len) {
    const char *end = json + len;
    const char *p = json;
    int added = 0;

    while (p < end) {
        const char *obj = memchr(p, '{', (size_t)(end - p));
        if (!obj) break;

        bool nested = false;
        const char *obj_end = json_object_end(obj, end, &nested);
        if (!obj_end) break;

        char code[BTTV_CODE_MAX] = {0};
        char url[220] = {0};
        int provider = json_get_int_default_direct(obj, obj_end,
                                                   "provider", -1);
        bool zero_width = json_get_bool_default_direct(obj, obj_end,
                                                       "zero_width", false);
        if (provider == 0 && !zero_width
            && json_get_string_direct(obj, obj_end, "code",
                                      code, sizeof(code))
            && json_get_string(obj, obj_end, "url", url, sizeof(url))) {
            if (emote_catalog_add_url(code, url, 28, 28))
                added++;
        }

        p = obj + 1;
    }
    return added;
}

static bool twitch_badge_version_image_url(const char *version_obj,
                                           const char *version_end,
                                           char *url, int url_cap)
{
    return json_get_string_direct(version_obj, version_end, "image_url_2x",
                                  url, url_cap)
        || json_get_string_direct(version_obj, version_end, "image_url_4x",
                                  url, url_cap)
        || json_get_string_direct(version_obj, version_end, "image_url_1x",
                                  url, url_cap);
}

static bool bttv_preload_image_by_code(const char *code);

static int twitch_badges_parse_json(const char *json, size_t len,
                                    const char *room_id) {
    const char *end = json + len;
    const char *sets = NULL;
    const char *sets_end = NULL;
    int added = 0;
    bool channel_scope = room_id && room_id[0];

    if (!json_get_object_direct(json, end, "badge_sets",
                                &sets, &sets_end)) {
        return 0;
    }

    const char *set_cursor = sets + 1;
    while (set_cursor < sets_end) {
        char badge_id[MAX_BADGE_ID] = {0};
        const char *set_obj = NULL;
        const char *set_obj_end = NULL;
        if (!json_next_direct_property(&set_cursor, sets_end,
                                       badge_id, sizeof(badge_id),
                                       &set_obj, &set_obj_end)) {
            break;
        }
        if (!set_obj || set_obj >= set_obj_end || *set_obj != '{') {
            continue;
        }

        const char *versions = NULL;
        const char *versions_end = NULL;
        if (!json_get_object_direct(set_obj, set_obj_end, "versions",
                                    &versions, &versions_end)) {
            continue;
        }

        const char *version_cursor = versions + 1;
        while (version_cursor < versions_end) {
            char version[MAX_BADGE_VERSION] = {0};
            const char *version_obj = NULL;
            const char *version_obj_end = NULL;
            if (!json_next_direct_property(&version_cursor, versions_end,
                                           version, sizeof(version),
                                           &version_obj, &version_obj_end)) {
                break;
            }
            if (!version_obj || version_obj >= version_obj_end
                || *version_obj != '{') {
                continue;
            }

            char url[220] = {0};
            char code[BTTV_CODE_MAX] = {0};
            if (twitch_badge_version_image_url(version_obj, version_obj_end,
                                               url, sizeof(url))
                && (channel_scope
                    ? make_twitch_channel_badge_code(room_id, badge_id,
                                                     version, code,
                                                     sizeof(code))
                    : make_twitch_global_badge_code(badge_id, version,
                                                    code, sizeof(code)))
                && emote_catalog_add_url(code, url, 18, 18)) {
                added++;
                if (channel_scope) {
                    (void)bttv_preload_image_by_code(code);
                }
            }
            if (channel_scope
                && is_twitch_subscriber_badge_id(badge_id)) {
                int version_number = 0;
                if (parse_nonnegative_int_string(version, &version_number)) {
                    twitch_register_subscriber_badge_version(version_number);
                }
            }
        }
    }

    return added;
}

static int twitch_badges_parse_helix_json(const char *json, size_t len,
                                          const char *room_id) {
    const char *end = json + len;
    const char *root = json_document_root(json, end);
    const char *data = NULL;
    const char *data_end = NULL;
    int added = 0;
    bool channel_scope = room_id && room_id[0];

    if (!json_get_array_direct(root, end, "data", &data, &data_end)) {
        return 0;
    }

    const char *set_cursor = data + 1;
    while (set_cursor < data_end) {
        const char *set_obj = NULL;
        const char *set_obj_end = NULL;
        if (!json_next_array_value(&set_cursor, data_end,
                                   &set_obj, &set_obj_end)) {
            break;
        }
        if (!set_obj || set_obj >= set_obj_end || *set_obj != '{') {
            continue;
        }

        char badge_id[MAX_BADGE_ID] = {0};
        const char *versions = NULL;
        const char *versions_end = NULL;
        if (!json_get_string_direct(set_obj, set_obj_end, "set_id",
                                    badge_id, sizeof(badge_id))
            || !json_get_array_direct(set_obj, set_obj_end, "versions",
                                      &versions, &versions_end)) {
            continue;
        }

        const char *version_cursor = versions + 1;
        while (version_cursor < versions_end) {
            const char *version_obj = NULL;
            const char *version_obj_end = NULL;
            if (!json_next_array_value(&version_cursor, versions_end,
                                       &version_obj, &version_obj_end)) {
                break;
            }
            if (!version_obj || version_obj >= version_obj_end
                || *version_obj != '{') {
                continue;
            }

            char version[MAX_BADGE_VERSION] = {0};
            char url[220] = {0};
            char code[BTTV_CODE_MAX] = {0};
            if (json_get_string_direct(version_obj, version_obj_end, "id",
                                       version, sizeof(version))
                && twitch_badge_version_image_url(version_obj,
                                                  version_obj_end,
                                                  url, sizeof(url))
                && (channel_scope
                    ? make_twitch_channel_badge_code(room_id, badge_id,
                                                     version, code,
                                                     sizeof(code))
                    : make_twitch_global_badge_code(badge_id, version,
                                                    code, sizeof(code)))) {
                if (!channel_scope && emote_catalog_contains(code)) {
                    added++;
                } else if (emote_catalog_add_url(code, url, 18, 18)) {
                    added++;
                    (void)bttv_preload_image_by_code(code);
                }
            }
            if (channel_scope && is_twitch_subscriber_badge_id(badge_id)) {
                int version_number = 0;
                if (parse_nonnegative_int_string(version, &version_number)) {
                    twitch_register_subscriber_badge_version(version_number);
                }
            }
        }
    }

    return added;
}

static bool full_path_from_utf8(const char *path, char *out, int out_cap)
{
    if (!path || !path[0] || !out || out_cap <= 0) return false;
    DWORD n = GetFullPathNameA(path, (DWORD)out_cap, out, NULL);
    return n > 0 && n < (DWORD)out_cap;
}

static bool directory_from_path(const char *path, char *out, int out_cap)
{
    if (!path || !path[0] || !out || out_cap <= 0) return false;
    size_t len = strlen(path);
    if (len >= (size_t)out_cap) return false;
    memcpy(out, path, len + 1);

    char *slash = strrchr(out, '\\');
    char *alt = strrchr(out, '/');
    if (!slash || (alt && alt > slash)) slash = alt;
    if (!slash) return false;
    slash[1] = '\0';
    return true;
}

static bool path_is_absolute_win32(const char *path)
{
    if (!path || !path[0]) return false;
    if ((path[0] == '\\' && path[1] == '\\')
        || (path[0] == '/' && path[1] == '/')) {
        return true;
    }
    return isalpha((unsigned char)path[0])
        && path[1] == ':'
        && (path[2] == '\\' || path[2] == '/');
}

static void normalize_path_separators(char *path)
{
    if (!path) return;
    for (char *p = path; *p; p++) {
        if (*p == '/') *p = '\\';
    }
}

static bool build_manifest_image_path(const char *root_dir,
                                      const char *relative,
                                      char *out, int out_cap)
{
    if (!root_dir || !root_dir[0] || !relative || !relative[0]
        || !out || out_cap <= 0) {
        return false;
    }

    char combined[IMAGE_PATH_MAX * 2];
    if (path_is_absolute_win32(relative)) {
        int n = snprintf(combined, sizeof(combined), "%s", relative);
        if (n <= 0 || n >= (int)sizeof(combined)) return false;
    } else {
        int n = snprintf(combined, sizeof(combined), "%s%s", root_dir, relative);
        if (n <= 0 || n >= (int)sizeof(combined)) return false;
    }
    normalize_path_separators(combined);

    if (!full_path_from_utf8(combined, out, out_cap)) {
        return false;
    }
    normalize_path_separators(out);

    size_t root_len = strlen(root_dir);
    return _strnicmp(out, root_dir, root_len) == 0;
}

static bool bttv_preload_image_by_code(const char *code);

static int twitch_badges_load_bundled_manifest(void)
{
    static int cached_added = -1;
    if (cached_added >= 0) return cached_added;
    cached_added = 0;

    if (!g_twitch_badge_manifest_path[0]) return 0;

    char manifest_path[IMAGE_PATH_MAX];
    if (!full_path_from_utf8(g_twitch_badge_manifest_path,
                             manifest_path, sizeof(manifest_path))) {
        return 0;
    }
    normalize_path_separators(manifest_path);

    char root_dir[IMAGE_PATH_MAX];
    if (!directory_from_path(manifest_path, root_dir, sizeof(root_dir))) {
        return 0;
    }
    normalize_path_separators(root_dir);

    byte_buf_t json;
    if (!file_read_bytes(manifest_path, HTTP_MAX_JSON, &json)) {
        log_msg("Bundled Twitch badge manifest unavailable: %s", manifest_path);
        return 0;
    }

    const char *end = (const char *)json.data + json.size;
    const char *root = json_document_root((const char *)json.data, end);
    const char *entries = NULL;
    const char *entries_end = NULL;
    int added = 0;
    int preloaded = 0;
    if (json_get_array_direct(root, end, "entries", &entries, &entries_end)) {
        const char *cursor = entries + 1;
        while (cursor < entries_end) {
            const char *entry = NULL;
            const char *entry_end = NULL;
            if (!json_next_array_value(&cursor, entries_end,
                                       &entry, &entry_end)) {
                break;
            }
            if (!entry || entry >= entry_end || *entry != '{') {
                continue;
            }

            char id[MAX_BADGE_ID] = {0};
            char version[MAX_BADGE_VERSION] = {0};
            char image[IMAGE_PATH_MAX] = {0};
            char image_path[IMAGE_PATH_MAX] = {0};
            char code[BTTV_CODE_MAX] = {0};
            if (json_get_string_direct(entry, entry_end, "id", id, sizeof(id))
                && json_get_string_direct(entry, entry_end, "version",
                                          version, sizeof(version))
                && json_get_string_direct(entry, entry_end, "image",
                                          image, sizeof(image))
                && make_twitch_badge_code(id, version, code, sizeof(code))
                && build_manifest_image_path(root_dir, image,
                                             image_path, sizeof(image_path))
                && emote_catalog_add_file(code, image_path, 18, 18)) {
                if (bttv_preload_image_by_code(code)) {
                    preloaded++;
                }
                added++;
            }
        }
    } else {
        log_msg("Bundled Twitch badge manifest has no entries array: %s",
                manifest_path);
    }

    byte_buf_free(&json);
    if (added > 0) {
        log_msg("Bundled Twitch badges loaded: %d, preloaded: %d",
                added, preloaded);
        signal_render();
    } else {
        log_msg("Bundled Twitch badge manifest loaded no badges: %s",
                manifest_path);
    }
    cached_added = added;
    return cached_added;
}

static void bttv_set_global_loaded(bool loaded) {
    EnterCriticalSection(&g_bttv.cs);
    g_bttv.global_loaded = loaded;
    LeaveCriticalSection(&g_bttv.cs);
}

static bool bttv_global_loaded(void) {
    EnterCriticalSection(&g_bttv.cs);
    bool loaded = g_bttv.global_loaded;
    LeaveCriticalSection(&g_bttv.cs);
    return loaded;
}

static void bttv_load_global(void) {
    if (bttv_global_loaded()) return;

    byte_buf_t json;
    if (!https_get_bytes(L"api.betterttv.net",
                         L"/3/cached/emotes/global",
                         L"application/json",
                         HTTP_MAX_JSON, &json)) {
        log_msg("BTTV global emote load failed");
        return;
    }
    int added = bttv_parse_emotes_json((const char *)json.data, json.size);
    byte_buf_free(&json);
    bttv_set_global_loaded(true);
    log_msg("BTTV global emotes loaded: %d", added);
}

static bool bttv_channel_loaded_for(const char *room_id) {
    EnterCriticalSection(&g_bttv.cs);
    bool loaded = g_bttv.channel_loaded
               && strcmp(g_bttv.channel_room_id, room_id) == 0;
    LeaveCriticalSection(&g_bttv.cs);
    return loaded;
}

static void bttv_mark_channel_loaded(const char *room_id) {
    EnterCriticalSection(&g_bttv.cs);
    g_bttv.channel_loaded = true;
    strncpy(g_bttv.channel_room_id, room_id, sizeof(g_bttv.channel_room_id) - 1);
    g_bttv.channel_room_id[sizeof(g_bttv.channel_room_id) - 1] = '\0';
    LeaveCriticalSection(&g_bttv.cs);
}

static void bttv_load_channel_for_room(const char *room_id) {
    if (!room_id || !room_id[0] || bttv_channel_loaded_for(room_id)) return;

    char path[160];
    wchar_t wpath[160];
    snprintf(path, sizeof(path), "/3/cached/users/twitch/%s", room_id);
    if (!utf8_to_wide_path(path, wpath, 160)) {
        bttv_mark_channel_loaded(room_id);
        return;
    }

    byte_buf_t json;
    if (!https_get_bytes(L"api.betterttv.net", wpath, L"application/json",
                         HTTP_MAX_JSON, &json)) {
        bttv_mark_channel_loaded(room_id);
        log_msg("BTTV channel emote load failed for room %s", room_id);
        return;
    }
    int added = bttv_parse_emotes_json((const char *)json.data, json.size);
    byte_buf_free(&json);
    bttv_mark_channel_loaded(room_id);
    log_msg("BTTV channel emotes loaded: %d", added);
}

static bool catalog_flag_get(bool *flag) {
    EnterCriticalSection(&g_bttv.cs);
    bool loaded = *flag;
    LeaveCriticalSection(&g_bttv.cs);
    return loaded;
}

static void catalog_flag_set(bool *flag, bool loaded) {
    EnterCriticalSection(&g_bttv.cs);
    *flag = loaded;
    LeaveCriticalSection(&g_bttv.cs);
}

static void twitch_api_load_global(void) {
    if (catalog_flag_get(&g_bttv.twitch_global_loaded)) return;

    byte_buf_t json;
    if (!https_get_bytes(L"emotes.crippled.dev",
                         L"/v1/global/twitch",
                         L"application/json",
                         HTTP_MAX_JSON, &json)) {
        log_msg("Twitch global emote fallback load failed");
        return;
    }
    int added = twitch_api_parse_emotes_json((const char *)json.data,
                                             json.size);
    byte_buf_free(&json);
    catalog_flag_set(&g_bttv.twitch_global_loaded, true);
    log_msg("Twitch global emote fallbacks loaded: %d", added);
}

static bool twitch_api_channel_loaded_for(const char *login) {
    EnterCriticalSection(&g_bttv.cs);
    bool loaded = g_bttv.twitch_channel_loaded
               && strcmp(g_bttv.twitch_channel_login, login) == 0;
    LeaveCriticalSection(&g_bttv.cs);
    return loaded;
}

static void twitch_api_mark_channel_loaded(const char *login) {
    EnterCriticalSection(&g_bttv.cs);
    g_bttv.twitch_channel_loaded = true;
    strncpy(g_bttv.twitch_channel_login, login,
            sizeof(g_bttv.twitch_channel_login) - 1);
    g_bttv.twitch_channel_login[sizeof(g_bttv.twitch_channel_login) - 1] = '\0';
    LeaveCriticalSection(&g_bttv.cs);
}

static void twitch_api_load_channel_for_login(const char *login) {
    if (!login || !login[0] || twitch_api_channel_loaded_for(login)) return;

    char path[160];
    wchar_t wpath[160];
    snprintf(path, sizeof(path), "/v1/channel/%s/twitch", login);
    if (!utf8_to_wide_path(path, wpath, 160)) {
        twitch_api_mark_channel_loaded(login);
        return;
    }

    byte_buf_t json;
    if (!https_get_bytes(L"emotes.crippled.dev", wpath, L"application/json",
                         HTTP_MAX_JSON, &json)) {
        twitch_api_mark_channel_loaded(login);
        log_msg("Twitch channel emote fallback load failed for %s", login);
        return;
    }
    int added = twitch_api_parse_emotes_json((const char *)json.data,
                                             json.size);
    byte_buf_free(&json);
    twitch_api_mark_channel_loaded(login);
    log_msg("Twitch channel emote fallbacks loaded: %d", added);
}

static int twitch_badges_load_global_helix(void) {
    if (!g_twitch_chat_oauth[0]) return 0;

    load_twitch_client_id();
    if (!g_twitch_client_id[0]) {
        log_msg("Twitch Helix global badge load skipped; missing Client ID");
        return 0;
    }

    char header_utf8[MAX_OAUTH_TOKEN + 192];
    int h = snprintf(header_utf8, sizeof(header_utf8),
                     "Authorization: Bearer %s\r\nClient-Id: %s\r\n",
                     g_twitch_chat_oauth, g_twitch_client_id);
    if (h <= 0 || h >= (int)sizeof(header_utf8)) return 0;

    wchar_t header_w[2400];
    if (MultiByteToWideChar(CP_UTF8, 0, header_utf8, -1,
                            header_w, (int)(sizeof(header_w) / sizeof(header_w[0]))) <= 0) {
        return 0;
    }

    byte_buf_t json;
    if (!https_get_bytes_ex(L"api.twitch.tv", L"/helix/chat/badges/global",
                            L"application/json", header_w,
                            HTTP_MAX_JSON, &json)) {
        log_msg("Twitch Helix global badge load failed");
        return 0;
    }

    int added = twitch_badges_parse_helix_json((const char *)json.data,
                                               json.size, NULL);
    byte_buf_free(&json);
    if (added > 0) {
        log_msg("Twitch Helix global badges loaded: %d", added);
    }
    return added;
}

static void twitch_badges_load_global(void) {
    if (catalog_flag_get(&g_bttv.twitch_badges_global_loaded)) return;

    int added = twitch_badges_load_global_helix();
    if (added > 0) {
        catalog_flag_set(&g_bttv.twitch_badges_global_loaded, true);
        signal_render();
        return;
    }

    byte_buf_t json;
    if (!https_get_bytes(L"badges.twitch.tv",
                         L"/v1/badges/global/display",
                         L"application/json",
                         HTTP_MAX_JSON, &json)) {
        log_msg("Twitch global badge load failed");
        return;
    }
    added = twitch_badges_parse_json((const char *)json.data, json.size, NULL);
    byte_buf_free(&json);
    catalog_flag_set(&g_bttv.twitch_badges_global_loaded, true);
    log_msg("Twitch global badges loaded: %d", added);
    if (added > 0) signal_render();
}

static bool twitch_badges_channel_loaded_for(const char *room_id) {
    EnterCriticalSection(&g_bttv.cs);
    bool loaded = g_bttv.twitch_badges_channel_loaded
               && strcmp(g_bttv.twitch_badges_channel_room_id, room_id) == 0;
    LeaveCriticalSection(&g_bttv.cs);
    return loaded;
}

static void twitch_badges_mark_channel_loaded(const char *room_id) {
    EnterCriticalSection(&g_bttv.cs);
    g_bttv.twitch_badges_channel_loaded = true;
    strncpy(g_bttv.twitch_badges_channel_room_id, room_id,
            sizeof(g_bttv.twitch_badges_channel_room_id) - 1);
    g_bttv.twitch_badges_channel_room_id[
        sizeof(g_bttv.twitch_badges_channel_room_id) - 1] = '\0';
    LeaveCriticalSection(&g_bttv.cs);
}

static int twitch_badges_load_channel_helix_for_room(const char *room_id) {
    if (!room_id || !room_id[0] || !g_twitch_chat_oauth[0]) {
        return 0;
    }

    load_twitch_client_id();
    if (!g_twitch_client_id[0]) {
        log_msg("Twitch Helix channel badge fallback skipped; missing Client ID");
        return 0;
    }

    char path[160];
    wchar_t wpath[180];
    snprintf(path, sizeof(path), "/helix/chat/badges?broadcaster_id=%s",
             room_id);
    if (!utf8_to_wide_path(path, wpath, 180)) {
        return 0;
    }

    char header_utf8[MAX_OAUTH_TOKEN + 192];
    int h = snprintf(header_utf8, sizeof(header_utf8),
                     "Authorization: Bearer %s\r\nClient-Id: %s\r\n",
                     g_twitch_chat_oauth, g_twitch_client_id);
    if (h <= 0 || h >= (int)sizeof(header_utf8)) return 0;

    wchar_t header_w[2400];
    if (MultiByteToWideChar(CP_UTF8, 0, header_utf8, -1,
                            header_w, (int)(sizeof(header_w) / sizeof(header_w[0]))) <= 0) {
        return 0;
    }

    byte_buf_t json;
    if (!https_get_bytes_ex(L"api.twitch.tv", wpath,
                            L"application/json", header_w,
                            HTTP_MAX_JSON, &json)) {
        log_msg("Twitch Helix channel badge fallback failed for room %s",
                room_id);
        return 0;
    }

    int added = twitch_badges_parse_helix_json((const char *)json.data,
                                               json.size, room_id);
    byte_buf_free(&json);
    if (added > 0) {
        log_msg("Twitch Helix channel badges loaded: %d", added);
    }
    return added;
}

static void twitch_badges_load_channel_for_room(const char *room_id) {
    if (!room_id || !room_id[0]
        || twitch_badges_channel_loaded_for(room_id)) {
        return;
    }

    int added = 0;
    char path[160];
    wchar_t wpath[160];
    snprintf(path, sizeof(path), "/v1/badges/channels/%s/display", room_id);
    if (!utf8_to_wide_path(path, wpath, 160)) {
        log_msg("Twitch channel badge path build failed for room %s", room_id);
    } else {
        byte_buf_t json;
        if (https_get_bytes(L"badges.twitch.tv", wpath, L"application/json",
                            HTTP_MAX_JSON, &json)) {
            added = twitch_badges_parse_json((const char *)json.data, json.size,
                                             room_id);
            byte_buf_free(&json);
        } else {
            log_msg("Twitch channel badge load failed for room %s", room_id);
        }
    }

    if (g_twitch_subscriber_badge_version_count <= 0) {
        added += twitch_badges_load_channel_helix_for_room(room_id);
    }

    twitch_badges_mark_channel_loaded(room_id);
    log_msg("Twitch channel badges loaded: %d", added);
    if (added > 0) signal_render();
}

static void ffz_load_global(void) {
    if (catalog_flag_get(&g_bttv.ffz_global_loaded)) return;

    byte_buf_t json;
    if (!https_get_bytes(L"api.frankerfacez.com",
                         L"/v1/set/global",
                         L"application/json",
                         HTTP_MAX_JSON, &json)) {
        log_msg("FFZ global emote load failed");
        return;
    }
    int added = ffz_parse_emotes_json((const char *)json.data, json.size);
    byte_buf_free(&json);
    catalog_flag_set(&g_bttv.ffz_global_loaded, true);
    log_msg("FFZ global emotes loaded: %d", added);
}

static bool ffz_channel_loaded_for(const char *room_id) {
    EnterCriticalSection(&g_bttv.cs);
    bool loaded = g_bttv.ffz_channel_loaded
               && strcmp(g_bttv.ffz_channel_room_id, room_id) == 0;
    LeaveCriticalSection(&g_bttv.cs);
    return loaded;
}

static void ffz_mark_channel_loaded(const char *room_id) {
    EnterCriticalSection(&g_bttv.cs);
    g_bttv.ffz_channel_loaded = true;
    strncpy(g_bttv.ffz_channel_room_id, room_id,
            sizeof(g_bttv.ffz_channel_room_id) - 1);
    g_bttv.ffz_channel_room_id[sizeof(g_bttv.ffz_channel_room_id) - 1] = '\0';
    LeaveCriticalSection(&g_bttv.cs);
}

static void ffz_load_channel_for_room(const char *room_id) {
    if (!room_id || !room_id[0] || ffz_channel_loaded_for(room_id)) return;

    char path[160];
    wchar_t wpath[160];
    snprintf(path, sizeof(path), "/v1/room/id/%s", room_id);
    if (!utf8_to_wide_path(path, wpath, 160)) {
        ffz_mark_channel_loaded(room_id);
        return;
    }

    byte_buf_t json;
    if (!https_get_bytes(L"api.frankerfacez.com", wpath, L"application/json",
                         HTTP_MAX_JSON, &json)) {
        ffz_mark_channel_loaded(room_id);
        log_msg("FFZ channel emote load failed for room %s", room_id);
        return;
    }
    int added = ffz_parse_emotes_json((const char *)json.data, json.size);
    byte_buf_free(&json);
    ffz_mark_channel_loaded(room_id);
    log_msg("FFZ channel emotes loaded: %d", added);
}

static void seventv_load_global(void) {
    if (catalog_flag_get(&g_bttv.seventv_global_loaded)) return;

    byte_buf_t json;
    if (!https_get_bytes(L"7tv.io",
                         L"/v3/emote-sets/global",
                         L"application/json",
                         HTTP_MAX_JSON, &json)) {
        log_msg("7TV global emote load failed");
        return;
    }
    int added = seventv_parse_emotes_json((const char *)json.data, json.size);
    byte_buf_free(&json);
    catalog_flag_set(&g_bttv.seventv_global_loaded, true);
    log_msg("7TV global emotes loaded: %d", added);
}

static bool seventv_channel_loaded_for(const char *room_id) {
    EnterCriticalSection(&g_bttv.cs);
    bool loaded = g_bttv.seventv_channel_loaded
               && strcmp(g_bttv.seventv_channel_room_id, room_id) == 0;
    LeaveCriticalSection(&g_bttv.cs);
    return loaded;
}

static void seventv_mark_channel_loaded(const char *room_id) {
    EnterCriticalSection(&g_bttv.cs);
    g_bttv.seventv_channel_loaded = true;
    strncpy(g_bttv.seventv_channel_room_id, room_id,
            sizeof(g_bttv.seventv_channel_room_id) - 1);
    g_bttv.seventv_channel_room_id[sizeof(g_bttv.seventv_channel_room_id) - 1] = '\0';
    LeaveCriticalSection(&g_bttv.cs);
}

static void seventv_load_channel_for_room(const char *room_id) {
    if (!room_id || !room_id[0] || seventv_channel_loaded_for(room_id)) return;

    char path[160];
    wchar_t wpath[160];
    snprintf(path, sizeof(path), "/v3/users/twitch/%s", room_id);
    if (!utf8_to_wide_path(path, wpath, 160)) {
        seventv_mark_channel_loaded(room_id);
        return;
    }

    byte_buf_t json;
    if (!https_get_bytes(L"7tv.io", wpath, L"application/json",
                         HTTP_MAX_JSON, &json)) {
        seventv_mark_channel_loaded(room_id);
        log_msg("7TV channel emote load failed for room %s", room_id);
        return;
    }
    int added = seventv_parse_emotes_json((const char *)json.data, json.size);
    byte_buf_free(&json);
    seventv_mark_channel_loaded(room_id);
    log_msg("7TV channel emotes loaded: %d", added);
}

static void load_channel_emotes_for_room(const char *room_id) {
    bttv_load_channel_for_room(room_id);
    ffz_load_channel_for_room(room_id);
    seventv_load_channel_for_room(room_id);
    twitch_api_load_channel_for_login(g_channel);
    twitch_badges_load_channel_for_room(room_id);
}

static void preload_known_twitch_room_assets(void) {
    if (g_chat_provider != CHAT_PROVIDER_TWITCH || !g_twitch_room_id[0]) {
        return;
    }

    log_msg("Preloading Twitch channel assets for room %s", g_twitch_room_id);
    load_channel_emotes_for_room(g_twitch_room_id);
}

static bool twitch_room_assets_are_async_for(const char *room_id) {
    if (!room_id || !room_id[0] || !g_twitch_room_id[0]) {
        return false;
    }
    if (InterlockedCompareExchange(&g_twitch_assets_async, 0, 0) == 0) {
        return false;
    }
    return strcmp(g_twitch_room_id, room_id) == 0;
}

static int bttv_lookup(const char *code) {
    int idx;
    EnterCriticalSection(&g_bttv.cs);
    idx = bttv_find_locked(code);
    LeaveCriticalSection(&g_bttv.cs);
    return idx;
}

static void bttv_get_base_size(int index, int *out_w, int *out_h) {
    int w = 28;
    int h = 28;
    EnterCriticalSection(&g_bttv.cs);
    if (index >= 0 && index < g_bttv.count) {
        bttv_emote_t *e = &g_bttv.items[index];
        if (e->image_w > 0 && e->image_h > 0) {
            w = (int)e->image_w;
            h = (int)e->image_h;
        } else if (e->api_w > 0 && e->api_h > 0) {
            w = e->api_w;
            h = e->api_h;
        }
    }
    LeaveCriticalSection(&g_bttv.cs);
    *out_w = w;
    *out_h = h;
}

static void bttv_scaled_size_for_height(int index, int render_h, int max_w,
                                        int *out_w, int *out_h)
{
    int base_w, base_h;
    bttv_get_base_size(index, &base_w, &base_h);
    if (base_w <= 0) base_w = 28;
    if (base_h <= 0) base_h = 28;

    int h = render_h > 0 ? render_h : EMOTE_RENDER_H;
    int w = (base_w * h + base_h / 2) / base_h;
    if (w < 4) w = 4;
    if (w > max_w) w = max_w;
    *out_w = w;
    *out_h = h;
}

static void bttv_scaled_size(int index, int *out_w, int *out_h) {
    bttv_scaled_size_for_height(index, scale_reference_px(EMOTE_RENDER_H),
                                scale_reference_px(EMOTE_MAX_W),
                                out_w, out_h);
}

static DWORD normalize_frame_delay_ms(DWORD hundredths) {
    DWORD ms = hundredths * 10u;
    if (ms == 0) ms = 100;
    if (ms < 20) ms = 20;
    return ms;
}

static void emote_init_animation_locked(bttv_emote_t *e) {
    if (!e || !e->image) return;

    UINT dim_count = 0;
    if (GdipImageGetFrameDimensionsCount(e->image, &dim_count) != 0 || dim_count == 0) {
        return;
    }

    GUID *dims = (GUID *)calloc(dim_count, sizeof(GUID));
    if (!dims) return;
    if (GdipImageGetFrameDimensionsList(e->image, dims, dim_count) != 0) {
        free(dims);
        return;
    }

    GUID chosen = dims[0];
    UINT chosen_count = 1;
    for (UINT i = 0; i < dim_count; i++) {
        UINT count = 0;
        if (GdipImageGetFrameCount(e->image, &dims[i], &count) != 0 || count <= 1) {
            continue;
        }
        chosen = dims[i];
        chosen_count = count;
        if (memcmp(&dims[i], &FrameDimensionTime, sizeof(GUID)) == 0) {
            break;
        }
    }
    free(dims);

    if (chosen_count <= 1) return;

    DWORD *delays = (DWORD *)calloc(chosen_count, sizeof(DWORD));
    if (!delays) return;
    for (UINT i = 0; i < chosen_count; i++) {
        delays[i] = 100;
    }

    UINT prop_size = 0;
    if (GdipGetPropertyItemSize(e->image, PropertyTagFrameDelay, &prop_size) == 0
        && prop_size > 0) {
        PropertyItem *item = (PropertyItem *)malloc(prop_size);
        if (item
            && GdipGetPropertyItem(e->image, PropertyTagFrameDelay,
                                   prop_size, item) == 0
            && item->value) {
            DWORD *raw = (DWORD *)item->value;
            UINT raw_count = item->length / sizeof(DWORD);
            if (raw_count > chosen_count) raw_count = chosen_count;
            for (UINT i = 0; i < raw_count; i++) {
                delays[i] = normalize_frame_delay_ms(raw[i]);
            }
        }
        free(item);
    }

    DWORD total = 0;
    for (UINT i = 0; i < chosen_count; i++) {
        total += delays[i] ? delays[i] : 100;
    }
    if (total == 0) {
        free(delays);
        return;
    }

    e->animated = true;
    e->frame_dimension = chosen;
    e->frame_count = chosen_count;
    e->current_frame = 0;
    e->frame_delays_ms = delays;
    e->total_frame_delay_ms = total;
    e->animation_started_ms = GetTickCount64();
    GdipImageSelectActiveFrame(e->image, &e->frame_dimension, 0);
}

static void emote_select_animation_frame_locked(bttv_emote_t *e,
                                                ULONGLONG occurrence_started_ms) {
    if (!e || !e->image || !e->animated || e->frame_count <= 1
        || !e->frame_delays_ms || e->total_frame_delay_ms == 0) {
        return;
    }

    ULONGLONG now = GetTickCount64();
    ULONGLONG started = occurrence_started_ms ? occurrence_started_ms
                                              : e->animation_started_ms;
    DWORD elapsed = (DWORD)((now - started)
                            % e->total_frame_delay_ms);
    DWORD cursor = 0;
    UINT frame = 0;
    for (UINT i = 0; i < e->frame_count; i++) {
        DWORD delay = e->frame_delays_ms[i] ? e->frame_delays_ms[i] : 100;
        if (elapsed < cursor + delay) {
            frame = i;
            break;
        }
        cursor += delay;
    }

    if (frame != e->current_frame) {
        if (GdipImageSelectActiveFrame(e->image, &e->frame_dimension, frame) == 0) {
            e->current_frame = frame;
        }
    }
}

static void bttv_finish_image_load(int index, bool tried) {
    EnterCriticalSection(&g_bttv.cs);
    if (index >= 0 && index < g_bttv.count) {
        g_bttv.items[index].loading_image = false;
        if (tried) g_bttv.items[index].tried_image = true;
    }
    LeaveCriticalSection(&g_bttv.cs);
}

static bool bttv_load_image_sync(int index) {
    if (!g_gdiplus_ready || index < 0) return false;

    char host[80];
    char path[IMAGE_PATH_MAX];
    bool local_file = false;
    EnterCriticalSection(&g_bttv.cs);
    if (index >= g_bttv.count) {
        LeaveCriticalSection(&g_bttv.cs);
        return false;
    }
    bttv_emote_t *e = &g_bttv.items[index];
    if (e->image) {
        e->loading_image = false;
        LeaveCriticalSection(&g_bttv.cs);
        return true;
    }
    if (e->tried_image) {
        e->loading_image = false;
        LeaveCriticalSection(&g_bttv.cs);
        return false;
    }
    local_file = e->local_file;
    strncpy(host, e->host, sizeof(host) - 1);
    host[sizeof(host) - 1] = '\0';
    strncpy(path, e->path, sizeof(path) - 1);
    path[sizeof(path) - 1] = '\0';
    LeaveCriticalSection(&g_bttv.cs);

    byte_buf_t bytes;
    if (local_file) {
        if (!file_read_bytes(path, HTTP_MAX_IMAGE, &bytes)) {
            log_emote_debug("local image read failed for %s", path);
            bttv_finish_image_load(index, true);
            return false;
        }
    } else {
        wchar_t whost[80];
        wchar_t wpath[IMAGE_PATH_MAX];
        if (!utf8_to_wide_path(host, whost, 80)
            || !utf8_to_wide_path(path, wpath, IMAGE_PATH_MAX)) {
            bttv_finish_image_load(index, true);
            return false;
        }

        if (!https_get_image_bytes(whost, wpath, host,
                                   HTTP_MAX_IMAGE, &bytes)) {
        bool retried_static = false;
        if (strcmp(host, "static-cdn.jtvnw.net") == 0
            && string_replace_once(path, sizeof(path), "/animated/", "/static/")
            && utf8_to_wide_path(path, wpath, IMAGE_PATH_MAX)) {
            retried_static = true;
            if (https_get_image_bytes(whost, wpath, host,
                                      HTTP_MAX_IMAGE, &bytes)) {
                EnterCriticalSection(&g_bttv.cs);
                if (index < g_bttv.count
                    && strcmp(g_bttv.items[index].host, host) == 0) {
                    size_t path_len = strlen(path);
                    if (path_len >= sizeof(g_bttv.items[index].path))
                        path_len = sizeof(g_bttv.items[index].path) - 1;
                    memcpy(g_bttv.items[index].path, path, path_len);
                    g_bttv.items[index].path[path_len] = '\0';
                }
                LeaveCriticalSection(&g_bttv.cs);
            }
        }
        if (!bytes.data) {
            log_emote_debug("image fetch failed for %s%s%s", host, path,
                            retried_static ? " (after static fallback)" : "");
            bttv_finish_image_load(index, true);
            return false;
        }
        }
    }

    HGLOBAL hmem = GlobalAlloc(GMEM_MOVEABLE, bytes.size);
    if (!hmem) {
        byte_buf_free(&bytes);
        bttv_finish_image_load(index, true);
        return false;
    }
    void *dst = GlobalLock(hmem);
    if (!dst) {
        GlobalFree(hmem);
        byte_buf_free(&bytes);
        bttv_finish_image_load(index, true);
        return false;
    }
    memcpy(dst, bytes.data, bytes.size);
    GlobalUnlock(hmem);
    byte_buf_free(&bytes);

    IStream *stream = NULL;
    HRESULT hr = CreateStreamOnHGlobal(hmem, TRUE, &stream);
    if (FAILED(hr) || !stream) {
        GlobalFree(hmem);
        bttv_finish_image_load(index, true);
        return false;
    }

    GpImage *image = NULL;
    GpStatus st = GdipLoadImageFromStream(stream, &image);
    if (st != 0 || !image) {
        log_emote_debug("image decode failed status=%d for %s%s", (int)st, host, path);
        stream->lpVtbl->Release(stream);
        bttv_finish_image_load(index, true);
        return false;
    }

    UINT iw = 0, ih = 0;
    GdipGetImageWidth(image, &iw);
    GdipGetImageHeight(image, &ih);

    bool ok = false;
    EnterCriticalSection(&g_bttv.cs);
    if (index < g_bttv.count
        && g_bttv.items[index].local_file == local_file
        && (local_file || strcmp(g_bttv.items[index].host, host) == 0)
        && strcmp(g_bttv.items[index].path, path) == 0) {
        if (!g_bttv.items[index].image) {
            g_bttv.items[index].image = image;
            g_bttv.items[index].image_stream = stream;
            g_bttv.items[index].image_w = iw;
            g_bttv.items[index].image_h = ih;
            emote_init_animation_locked(&g_bttv.items[index]);
            ok = true;
        }
        g_bttv.items[index].tried_image = true;
    }
    if (index < g_bttv.count) {
        g_bttv.items[index].loading_image = false;
    }
    LeaveCriticalSection(&g_bttv.cs);

    if (!ok) {
        GdipDisposeImage(image);
        stream->lpVtbl->Release(stream);
    } else if (local_file) {
        log_emote_debug("image loaded: %s", path);
    } else {
        log_emote_debug("image loaded: %s%s", host, path);
    }
    return ok;
}

static bool bttv_preload_image_by_code(const char *code)
{
    int index = bttv_lookup(code);
    if (index < 0) return false;

    bool ready = false;
    bool should_load = false;
    EnterCriticalSection(&g_bttv.cs);
    if (index < g_bttv.count) {
        bttv_emote_t *e = &g_bttv.items[index];
        if (e->image) {
            ready = true;
            e->loading_image = false;
        } else if (!e->tried_image && !e->loading_image) {
            e->loading_image = true;
            should_load = true;
        }
    }
    LeaveCriticalSection(&g_bttv.cs);

    if (ready) return true;
    return should_load && bttv_load_image_sync(index);
}

static bool bttv_ensure_image(int index) {
    if (!g_gdiplus_ready || index < 0) return false;

    bool ready = false;
    bool should_queue = false;
    EnterCriticalSection(&g_bttv.cs);
    if (index < g_bttv.count) {
        bttv_emote_t *e = &g_bttv.items[index];
        if (e->image) {
            ready = true;
        } else if (!e->tried_image && !e->loading_image) {
            e->loading_image = true;
            should_queue = true;
        }
    }
    LeaveCriticalSection(&g_bttv.cs);

    if (ready) return true;
    if (should_queue && !image_load_queue_push(&g_image_load_queue, index)) {
        bttv_finish_image_load(index, true);
    }
    return false;
}

static DWORD WINAPI image_loader_thread(LPVOID arg) {
    (void)arg;
    HRESULT co_hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    bool co_ready = SUCCEEDED(co_hr);

    while (!g_stop) {
        int index = -1;
        if (!image_load_queue_pop(&g_image_load_queue, &index, 250)) {
            continue;
        }
        (void)bttv_load_image_sync(index);
        signal_render();
    }

    if (co_ready) CoUninitialize();
    return 0;
}

/* ===== IRC client ======================================================= */

/* Parse and de-escape an IRCv3 tag value into out (caller-provided). */
static void unescape_tag(const char *in, int in_len, char *out, int out_size) {
    int o = 0;
    for (int i = 0; i < in_len && o + 1 < out_size; i++) {
        if (in[i] != '\\' || i + 1 >= in_len) {
            out[o++] = in[i];
            continue;
        }
        char next = in[i + 1];
        char repl;
        switch (next) {
            case ':':  repl = ';';  break;
            case 's':  repl = ' ';  break;
            case '\\': repl = '\\'; break;
            case 'r':  repl = '\r'; break;
            case 'n':  repl = '\n'; break;
            default:   repl = next; break;
        }
        out[o++] = repl;
        i++;
    }
    out[o] = '\0';
}

/* Look up a tag by key in the @key=val;key=val tag block (without the @). */
static bool find_tag(const char *tags, const char *key, char *out, int out_size) {
    int key_len = (int)strlen(key);
    const char *p = tags;
    while (*p) {
        const char *eq = strchr(p, '=');
        const char *semi = strchr(p, ';');
        if (!eq || (semi && eq > semi)) {
            if (!semi) break;
            p = semi + 1;
            continue;
        }
        int this_key_len = (int)(eq - p);
        if (this_key_len == key_len && strncmp(p, key, key_len) == 0) {
            const char *vstart = eq + 1;
            const char *vend = semi ? semi : tags + strlen(tags);
            unescape_tag(vstart, (int)(vend - vstart), out, out_size);
            return true;
        }
        if (!semi) break;
        p = semi + 1;
    }
    return false;
}

/* Extract user from "user!user@host" prefix; returns empty string on failure. */
static void prefix_user(const char *prefix, char *out, int out_size) {
    const char *bang = strchr(prefix, '!');
    if (!bang) { out[0] = '\0'; return; }
    int n = (int)(bang - prefix);
    if (n >= out_size) n = out_size - 1;
    memcpy(out, prefix, n);
    out[n] = '\0';
}

static bool ascii_equals_ci(const char *a, const char *b) {
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) {
            return false;
        }
        a++;
        b++;
    }
    return *a == '\0' && *b == '\0';
}

static void normalize_badge_id(const char *id, char *out, int out_size) {
    if (!out || out_size <= 0) return;
    int o = 0;
    if (id) {
        while (*id && isspace((unsigned char)*id)) id++;
        for (; *id && o + 1 < out_size; id++) {
            unsigned char c = (unsigned char)*id;
            if (isspace(c)) break;
            out[o++] = (char)tolower(c);
        }
    }
    while (o > 0 && isspace((unsigned char)out[o - 1])) o--;
    out[o] = '\0';

    if (ascii_equals_ci(out, "subgifter")
        || ascii_equals_ci(out, "sub-gifter")
        || ascii_equals_ci(out, "gift-sub")
        || ascii_equals_ci(out, "gift-subs")
        || ascii_equals_ci(out, "gift-subscriber")
        || ascii_equals_ci(out, "gift-subscription")
        || ascii_equals_ci(out, "gifted-sub")
        || ascii_equals_ci(out, "gifted-subs")
        || ascii_equals_ci(out, "gifted-subscriber")
        || ascii_equals_ci(out, "gifted-subscription")
        || ascii_equals_ci(out, "gifter")
        || ascii_equals_ci(out, "gift_sub")
        || ascii_equals_ci(out, "gift_subs")
        || ascii_equals_ci(out, "gift_subscriber")
        || ascii_equals_ci(out, "gift_subscription")
        || ascii_equals_ci(out, "gifted_sub")
        || ascii_equals_ci(out, "gifted_subs")
        || ascii_equals_ci(out, "gifted_subscriber")
        || ascii_equals_ci(out, "gifted_subscription")
        || ascii_equals_ci(out, "sub_gift")
        || ascii_equals_ci(out, "sub-gift")
        || ascii_equals_ci(out, "subgift")
        || ascii_equals_ci(out, "sub_gifts")
        || ascii_equals_ci(out, "sub-gifts")
        || ascii_equals_ci(out, "sub_gifter_badge")
        || ascii_equals_ci(out, "subscriber_gifter")
        || ascii_equals_ci(out, "subscriber-gifter")
        || ascii_equals_ci(out, "subscription_gift")
        || ascii_equals_ci(out, "subscription-gift")
        || ascii_equals_ci(out, "subscription_gifts")
        || ascii_equals_ci(out, "subscription-gifts")) {
        snprintf(out, (size_t)out_size, "%s", "sub_gifter");
    } else if (ascii_equals_ci(out, "channel_host")
               || ascii_equals_ci(out, "channel-host")
               || ascii_equals_ci(out, "creator")) {
        snprintf(out, (size_t)out_size, "%s", "broadcaster");
    } else if (ascii_equals_ci(out, "subscription")
               || ascii_equals_ci(out, "subscriptions")
               || ascii_equals_ci(out, "sub")) {
        snprintf(out, (size_t)out_size, "%s", "subscriber");
    }
}

static const char *known_badge_label(const char *id) {
    if (ascii_equals_ci(id, "admin")) return "ADMIN";
    if (ascii_equals_ci(id, "artist-badge")) return "ART";
    if (ascii_equals_ci(id, "bits")) return "BITS";
    if (ascii_equals_ci(id, "broadcaster")) return "BC";
    if (ascii_equals_ci(id, "founder")) return "FND";
    if (ascii_equals_ci(id, "moderator")) return "MOD";
    if (ascii_equals_ci(id, "og")) return "OG";
    if (ascii_equals_ci(id, "partner")) return "PART";
    if (ascii_equals_ci(id, "premium")) return "PRIME";
    if (ascii_equals_ci(id, "staff")) return "STAFF";
    if (ascii_equals_ci(id, "sub_gifter")) return "GIFT";
    if (ascii_equals_ci(id, "subscriber")) return "SUB";
    if (ascii_equals_ci(id, "turbo")) return "TURBO";
    if (ascii_equals_ci(id, "verified")) return "VER";
    if (ascii_equals_ci(id, "vip")) return "VIP";
    return NULL;
}

static void make_badge_label(const char *id, const char *text,
                             char *out, int out_size)
{
    if (!out || out_size <= 0) return;
    out[0] = '\0';

    const char *known = known_badge_label(id);
    if (known) {
        strncpy(out, known, out_size - 1);
        out[out_size - 1] = '\0';
        return;
    }

    const char *source = (text && text[0]) ? text : id;
    int o = 0;
    if (source) {
        while (*source && isspace((unsigned char)*source)) source++;
        for (; *source && o + 1 < out_size; source++) {
            unsigned char c = (unsigned char)*source;
            if (isspace(c) || c == '-' || c == '_' || c == '/') break;
            if (isalnum(c)) {
                out[o++] = (char)toupper(c);
            }
        }
    }

    if (o == 0 && out_size > 1) {
        strncpy(out, "BADGE", out_size - 1);
        out[out_size - 1] = '\0';
        return;
    }
    out[o] = '\0';
}

static int chat_badge_index(const chat_badge_t *badges, int count,
                            const char *id)
{
    for (int i = 0; i < count; i++) {
        if (ascii_equals_ci(badges[i].id, id)) return i;
    }
    return -1;
}

static bool is_kick_gift_badge_id(const char *id)
{
    return ascii_equals_ci(id, "sub_gifter")
        || ascii_equals_ci(id, "sub_gift_leader");
}

static void maybe_set_kick_badge_image_code(chat_badge_t *badge,
                                            const char *image_url)
{
    if (!badge) return;
    if (image_url && image_url[0]) {
        char normalized_url[IMAGE_PATH_MAX] = {0};
        if (!normalize_kick_asset_url(image_url, normalized_url,
                                      sizeof(normalized_url))) {
            return;
        }

        char image_code[BTTV_CODE_MAX] = {0};
        if (make_kick_badge_code(badge->id, badge->version,
                                 image_code, sizeof(image_code))
            && emote_catalog_add_url(image_code, normalized_url, 36, 36)) {
            strncpy(badge->image_code, image_code,
                    sizeof(badge->image_code) - 1);
            badge->image_code[sizeof(badge->image_code) - 1] = '\0';
        } else if (make_kick_badge_code_from_url(normalized_url,
                                                 image_code,
                                                 sizeof(image_code))) {
            if (emote_catalog_add_url(image_code, normalized_url, 36, 36)) {
                strncpy(badge->image_code, image_code,
                        sizeof(badge->image_code) - 1);
                badge->image_code[sizeof(badge->image_code) - 1] = '\0';
            }
        }
    } else if (ascii_equals_ci(badge->id, "subscriber")) {
        int cursor = 0;
        if (make_kick_badge_code_for_request(badge->id, badge->version,
                                             badge->image_code,
                                             sizeof(badge->image_code),
                                             &cursor)) {
            badge->image_code[sizeof(badge->image_code) - 1] = '\0';
        }
    }
}

static bool add_chat_badge(chat_badge_t *out, int cap, int *count,
                           const char *id, const char *version,
                           const char *text, const char *image_url,
                           bool image_only)
{
    if (!out || !count || *count >= cap) return false;

    char normalized[MAX_BADGE_ID];
    normalize_badge_id(id, normalized, sizeof(normalized));
    if (!normalized[0]) {
        return false;
    }
    if (g_chat_provider == CHAT_PROVIDER_KICK
        && ascii_equals_ci(normalized, "level")) {
        return false;
    }
    bool is_kick_gift_badge =
        g_chat_provider == CHAT_PROVIDER_KICK
        && is_kick_gift_badge_id(normalized);
    const char *badge_version = is_kick_gift_badge ? NULL : version;
    const char *badge_image_url = is_kick_gift_badge ? NULL : image_url;

    int existing_index = chat_badge_index(out, *count, normalized);
    if (existing_index >= 0) {
        if (!out[existing_index].version[0]
            && badge_version && badge_version[0]) {
            normalize_badge_version(badge_version, out[existing_index].version,
                                    sizeof(out[existing_index].version));
        }
        if (g_chat_provider == CHAT_PROVIDER_KICK
            && badge_image_url && badge_image_url[0]) {
            maybe_set_kick_badge_image_code(&out[existing_index],
                                            badge_image_url);
        }
        return false;
    }

    chat_badge_t *badge = &out[*count];
    memset(badge, 0, sizeof(*badge));
    size_t id_len = strlen(normalized);
    if (id_len >= sizeof(badge->id)) id_len = sizeof(badge->id) - 1;
    memcpy(badge->id, normalized, id_len);
    badge->id[id_len] = '\0';
    normalize_badge_version(badge_version, badge->version,
                            sizeof(badge->version));
    make_badge_label(normalized, text, badge->label, sizeof(badge->label));
    if (g_chat_provider == CHAT_PROVIDER_KICK) {
        maybe_set_kick_badge_image_code(badge, badge_image_url);
    }
    badge->image_only = image_only;
    (*count)++;
    return true;
}

static int twitch_collect_badges_from_tags(const char *tags,
                                           chat_badge_t *out, int cap)
{
    int count = 0;
    char badges_tag[2048] = {0};
    if (find_tag(tags, "badges", badges_tag, sizeof(badges_tag))
        && badges_tag[0]) {
        const char *p = badges_tag;
        while (*p && count < cap) {
            while (*p == ',' || isspace((unsigned char)*p)) p++;
            const char *start = p;
            while (*p && *p != ',' && *p != '/') p++;
            int id_len = (int)(p - start);
            if (id_len > 0) {
                char id[MAX_BADGE_ID] = {0};
                char version[MAX_BADGE_VERSION] = {0};
                if (id_len >= (int)sizeof(id)) id_len = (int)sizeof(id) - 1;
                memcpy(id, start, (size_t)id_len);
                id[id_len] = '\0';
                if (*p == '/') {
                    p++;
                    const char *version_start = p;
                    while (*p && *p != ',') p++;
                    int version_len = (int)(p - version_start);
                    if (version_len >= (int)sizeof(version)) {
                        version_len = (int)sizeof(version) - 1;
                    }
                    if (version_len > 0) {
                        memcpy(version, version_start, (size_t)version_len);
                        version[version_len] = '\0';
                    }
                }
                (void)add_chat_badge(out, cap, &count, id, version,
                                     NULL, NULL, true);
            }
            while (*p && *p != ',') p++;
            if (*p == ',') p++;
        }
    }

    char mod_tag[16] = {0};
    char user_type[32] = {0};
    if ((find_tag(tags, "mod", mod_tag, sizeof(mod_tag))
         && strcmp(mod_tag, "1") == 0)
        || (find_tag(tags, "user-type", user_type, sizeof(user_type))
            && ascii_equals_ci(user_type, "mod"))) {
        (void)add_chat_badge(out, cap, &count, "moderator", "1",
                             "Moderator", NULL, true);
    }

    return count;
}

static void normalize_action_text(char *text) {
    static const char prefix[] = "\001ACTION ";
    const size_t prefix_len = sizeof(prefix) - 1;
    if (!text || strncmp(text, prefix, prefix_len) != 0) return;

    size_t len = strlen(text);
    if (len > prefix_len && text[len - 1] == '\001') {
        text[len - 1] = '\0';
    }
    memmove(text, text + prefix_len, strlen(text + prefix_len) + 1);
}

static int utf8_codepoint_len(const char *p, const char *end) {
    if (!p || p >= end) return 0;

    const unsigned char *s = (const unsigned char *)p;
    int need = 1;
    if (s[0] < 0x80) {
        return 1;
    } else if ((s[0] & 0xE0) == 0xC0) {
        need = 2;
    } else if ((s[0] & 0xF0) == 0xE0) {
        need = 3;
    } else if ((s[0] & 0xF8) == 0xF0) {
        need = 4;
    } else {
        return 1;
    }

    if (p + need > end) return 1;
    for (int i = 1; i < need; i++) {
        if ((((const unsigned char *)p)[i] & 0xC0) != 0x80) {
            return 1;
        }
    }
    return need;
}

static bool utf8_codepoint_range_to_bytes(const char *message,
                                          long start_cp, long end_cp,
                                          int *out_start, int *out_end)
{
    if (!message || start_cp < 0 || end_cp < start_cp) return false;

    const char *begin = message;
    const char *end = message + strlen(message);
    const char *p = begin;
    const char *range_start = NULL;
    const char *range_end = NULL;
    long cp = 0;

    while (p < end) {
        if (cp == start_cp) {
            range_start = p;
        }

        int step = utf8_codepoint_len(p, end);
        if (step <= 0) break;
        const char *next = p + step;

        if (cp == end_cp) {
            range_end = next;
            break;
        }

        p = next;
        cp++;
    }

    if (!range_start || !range_end || range_end <= range_start) {
        return false;
    }

    *out_start = (int)(range_start - begin);
    *out_end = (int)(range_end - begin);
    return true;
}

static bool emote_code_range_is_plausible(const char *code, int len) {
    if (!code || len <= 0 || len >= BTTV_CODE_MAX) return false;

    for (int i = 0; i < len; i++) {
        unsigned char c = (unsigned char)code[i];
        if (c == '\0' || c <= 0x20 || c == 0x7F) {
            return false;
        }
    }
    return true;
}

static void sort_chat_emotes(chat_emote_t *emotes, int count);

static bool append_twitch_emote(chat_emote_t *out, int cap, int *count,
                                const char *message, int start_byte,
                                int end_byte, const char *id)
{
    int msg_len = (int)strlen(message);
    if (!out || !count || *count >= cap || !id || !id[0]) return false;
    if (start_byte < 0 || end_byte <= start_byte || end_byte > msg_len) return false;

    int code_len = end_byte - start_byte;
    const char *code_start = message + start_byte;
    if (!emote_code_range_is_plausible(code_start, code_len)) return false;

    char code[BTTV_CODE_MAX];
    memcpy(code, code_start, (size_t)code_len);
    code[code_len] = '\0';

    char path[220];
    snprintf(path, sizeof(path), "/emoticons/v2/%s/animated/light/1.0", id);
    (void)emote_catalog_add_direct(code, "static-cdn.jtvnw.net",
                                   path, 28, 28);

    chat_emote_t *item = &out[*count];
    memset(item, 0, sizeof(*item));
    item->start_byte = start_byte;
    item->end_byte = end_byte;
    memcpy(item->code, code, (size_t)code_len + 1);
    (*count)++;
    return true;
}

static bool kick_emote_name_is_plausible(const char *name, int len) {
    if (!name || len <= 0 || len >= BTTV_CODE_MAX) return false;

    for (int i = 0; i < len; i++) {
        unsigned char c = (unsigned char)name[i];
        if (c <= 0x20 || c == 0x7F || c == '[' || c == ']' || c == ':') {
            return false;
        }
    }
    return true;
}

static bool append_kick_emote(chat_emote_t *out, int cap, int *count,
                              int start_byte, int end_byte,
                              const char *id, int id_len,
                              const char *name, int name_len)
{
    if (!out || !count || *count >= cap || !id || !name) return false;
    if (start_byte < 0 || end_byte <= start_byte) return false;
    if (id_len <= 0 || id_len >= BTTV_ID_MAX) return false;
    if (!kick_emote_name_is_plausible(name, name_len)) return false;

    char id_buf[BTTV_ID_MAX];
    for (int i = 0; i < id_len; i++) {
        if (!isdigit((unsigned char)id[i])) return false;
        id_buf[i] = id[i];
    }
    id_buf[id_len] = '\0';

    char code[BTTV_CODE_MAX];
    memcpy(code, name, (size_t)name_len);
    code[name_len] = '\0';

    char path[220];
    snprintf(path, sizeof(path), "/emotes/%s/fullsize", id_buf);
    (void)emote_catalog_add_direct(code, "files.kick.com", path, 28, 28);

    chat_emote_t *item = &out[*count];
    memset(item, 0, sizeof(*item));
    item->start_byte = start_byte;
    item->end_byte = end_byte;
    memcpy(item->code, code, (size_t)name_len + 1);
    (*count)++;
    return true;
}

static int kick_collect_emotes_from_text(const char *message,
                                         chat_emote_t *out, int cap)
{
    if (!message || !message[0] || !out || cap <= 0) return 0;

    int count = 0;
    const char *p = message;
    while ((p = strstr(p, "[emote:")) != NULL && count < cap) {
        const char *marker_start = p;
        const char *id_start = p + 7;
        const char *id_end = id_start;
        while (*id_end && isdigit((unsigned char)*id_end)) id_end++;
        if (id_end == id_start || *id_end != ':') {
            p = marker_start + 1;
            continue;
        }

        const char *name_start = id_end + 1;
        const char *name_end = strchr(name_start, ']');
        if (!name_end) break;

        int start_byte = (int)(marker_start - message);
        int end_byte = (int)(name_end + 1 - message);
        (void)append_kick_emote(out, cap, &count, start_byte, end_byte,
                                id_start, (int)(id_end - id_start),
                                name_start, (int)(name_end - name_start));
        p = name_end + 1;
    }

    sort_chat_emotes(out, count);
    return count;
}

static void sort_chat_emotes(chat_emote_t *emotes, int count) {
    for (int i = 1; i < count; i++) {
        chat_emote_t item = emotes[i];
        int j = i - 1;
        while (j >= 0
               && (emotes[j].start_byte > item.start_byte
                   || (emotes[j].start_byte == item.start_byte
                       && emotes[j].end_byte > item.end_byte))) {
            emotes[j + 1] = emotes[j];
            j--;
        }
        emotes[j + 1] = item;
    }
}

static int twitch_collect_emotes_from_tag(const char *message,
                                          const char *emotes_tag,
                                          chat_emote_t *out, int cap)
{
    if (!message || !message[0] || !emotes_tag || !emotes_tag[0]
        || !out || cap <= 0) {
        return 0;
    }

    const int msg_len = (int)strlen(message);
    int count = 0;
    const char *p = emotes_tag;
    while (*p) {
        char id[BTTV_ID_MAX] = {0};
        int id_len = 0;
        while (*p && *p != ':' && *p != '/' && id_len + 1 < (int)sizeof(id)) {
            id[id_len++] = *p++;
        }
        id[id_len] = '\0';
        if (*p != ':' || !id[0]) {
            while (*p && *p != '/') p++;
            if (*p == '/') p++;
            continue;
        }
        p++;

        while (*p && *p != '/') {
            char *endptr = NULL;
            long start = strtol(p, &endptr, 10);
            if (endptr == p || *endptr != '-') break;
            p = endptr + 1;
            long end = strtol(p, &endptr, 10);
            if (endptr == p) break;
            p = endptr;

            if (start >= 0 && end >= start && end < msg_len) {
                int start_byte = -1;
                int end_byte = -1;
                if (!utf8_codepoint_range_to_bytes(message, start, end,
                                                   &start_byte, &end_byte)
                    || !append_twitch_emote(out, cap, &count, message,
                                            start_byte, end_byte, id)) {
                    (void)append_twitch_emote(out, cap, &count, message,
                                              (int)start, (int)end + 1, id);
                }
            }

            if (*p == ',') p++;
        }
        if (*p == '/') p++;
    }
    sort_chat_emotes(out, count);
    return count;
}

/* Parse one complete IRC line (without trailing \r\n). On PRIVMSG, pushes
 * to the queue. On PING, writes a PONG reply through `tls`. Returns false
 * only on fatal protocol error (caller can ignore — never happens today). */
static void handle_irc_line(tls_conn_t *tls, char *line) {
    /* PING :tmi.twitch.tv  -> reply PONG :tmi.twitch.tv */
    if (strncmp(line, "PING", 4) == 0) {
        const char *arg = strchr(line, ':');
        char reply[256];
        if (arg) {
            snprintf(reply, sizeof(reply), "PONG %s\r\n", arg);
        } else {
            strcpy(reply, "PONG :tmi.twitch.tv\r\n");
        }
        irc_send_locked(tls, reply, (int)strlen(reply));
        return;
    }

    /* @tags :prefix CMD args :trailing
     * We need tags (for display-name) and the trailing message. */
    char tags[2048] = {0};
    char *cursor = line;
    if (cursor[0] == '@') {
        char *sp = strchr(cursor, ' ');
        if (!sp) return;
        int tag_len = (int)(sp - cursor - 1);
        if (tag_len > (int)sizeof(tags) - 1) tag_len = (int)sizeof(tags) - 1;
        memcpy(tags, cursor + 1, tag_len);
        tags[tag_len] = '\0';
        cursor = sp + 1;
    }

    char prefix[256] = {0};
    if (cursor[0] == ':') {
        char *sp = strchr(cursor, ' ');
        if (!sp) return;
        int n = (int)(sp - cursor - 1);
        if (n > (int)sizeof(prefix) - 1) n = (int)sizeof(prefix) - 1;
        memcpy(prefix, cursor + 1, n);
        prefix[n] = '\0';
        cursor = sp + 1;
    }

    char room_id[64] = {0};
    if (find_tag(tags, "room-id", room_id, sizeof(room_id)) && room_id[0]
        && !twitch_room_assets_are_async_for(room_id)) {
        load_channel_emotes_for_room(room_id);
    }

    /* Numeric replies: 001 = welcome, 366 = end-of-names. Surface as system. */
    if (strncmp(cursor, "001 ", 4) == 0) {
        queue_push(&g_queue, "chat", "connected", true);
        return;
    }
    if (strncmp(cursor, "366 ", 4) == 0) {
        char joined[80];
        snprintf(joined, sizeof(joined), "joined #%s", g_channel);
        queue_push(&g_queue, "chat", joined, true);
        return;
    }
    if (strncmp(cursor, "NOTICE", 6) == 0) {
        const char *trailing = strstr(cursor, " :");
        if (trailing) {
            char text[MAX_MSG_TEXT];
            snprintf(text, sizeof(text), "notice: %s", trailing + 2);
            queue_push(&g_queue, "chat", text, true);
            if (strstr(trailing + 2, "Login authentication failed")
                || strstr(trailing + 2, "Improperly formatted auth")) {
                EnterCriticalSection(&g_irc_send_cs);
                g_irc_send_ready = false;
                LeaveCriticalSection(&g_irc_send_cs);
                input_set_notice_ms(4500, "Twitch login failed");
            }
        }
        return;
    }
    if (strncmp(cursor, "PRIVMSG ", 8) != 0) {
        return;
    }

    const char *colon = strstr(cursor, " :");
    if (!colon) return;
    const char *text_start = colon + 2;

    char user[MAX_USERNAME] = {0};
    if (!find_tag(tags, "display-name", user, sizeof(user)) || !user[0]) {
        prefix_user(prefix, user, sizeof(user));
    }
    if (!user[0]) return;

    char text[MAX_MSG_TEXT] = {0};
    copy_utf8_truncated(text, sizeof(text), text_start);
    normalize_action_text(text);

    chat_badge_t badges[MAX_MSG_BADGES];
    int badge_count = twitch_collect_badges_from_tags(tags, badges,
                                                      MAX_MSG_BADGES);

    chat_emote_t emotes[MAX_MSG_EMOTES];
    int emote_count = 0;
    char emotes_tag[2048] = {0};
    if (find_tag(tags, "emotes", emotes_tag, sizeof(emotes_tag)) && emotes_tag[0]) {
        emote_count = twitch_collect_emotes_from_tag(text, emotes_tag,
                                                     emotes, MAX_MSG_EMOTES);
    }
    if (should_suppress_local_echo(user, text)) {
        return;
    }
    queue_push_ex(&g_queue, user, text, false,
                  badges, badge_count, emotes, emote_count);
}

/* TLS loop: connect, login, read lines, dispatch. Returns on disconnect.
 * Caller calls again with backoff to reconnect. */
static void irc_session(void) {
    tls_conn_t *tls = tls_connect("irc.chat.twitch.tv", "6697");
    if (!tls) {
        log_msg("TLS connect failed: %s", tls_last_error());
        queue_push(&g_queue, "chat",
                   "reconnecting (tls failed)", true);
        return;
    }
    log_msg("TLS connected to irc.chat.twitch.tv");

    const bool auth_ready = g_twitch_chat_auth_ready
                         && g_twitch_chat_login[0]
                         && g_twitch_chat_oauth[0];

    char hello[4096];
    int hello_len;
    if (auth_ready) {
        hello_len = snprintf(hello, sizeof(hello),
            "CAP REQ :twitch.tv/tags twitch.tv/commands\r\n"
            "PASS oauth:%s\r\n"
            "NICK %s\r\n"
            "JOIN #%s\r\n",
            g_twitch_chat_oauth, g_twitch_chat_login, g_channel);
    } else {
        /* Anonymous login: good for reading, not for sending. */
        srand((unsigned)GetTickCount());
        unsigned suffix = 10000u + (unsigned)(rand() % 90000);
        char nick[64];
        snprintf(nick, sizeof(nick), "justinfan%u", suffix);
        hello_len = snprintf(hello, sizeof(hello),
            "CAP REQ :twitch.tv/tags twitch.tv/commands\r\n"
            "NICK %s\r\n"
            "JOIN #%s\r\n", nick, g_channel);
    }
    if (hello_len <= 0 || hello_len >= (int)sizeof(hello)) {
        log_msg("IRC hello too large");
        tls_close(tls);
        return;
    }
    if (tls_send(tls, hello, hello_len) < 0) {
        log_msg("TLS send (hello) failed: %s", tls_last_error());
        tls_close(tls);
        return;
    }
    irc_publish_send_connection(tls, auth_ready);

    if (auth_ready) {
        char status[128];
        snprintf(status, sizeof(status), "connecting to Twitch chat as %s...",
                 g_twitch_chat_login);
        queue_push(&g_queue, "chat", status, true);
    } else {
        queue_push(&g_queue, "chat", "connecting to Twitch chat...", true);
    }

    /* Line-buffered receive. */
    char buf[IRC_LINE_BUF];
    int  buf_len = 0;

    while (!g_stop) {
        int n = tls_recv(tls, buf + buf_len, (int)sizeof(buf) - 1 - buf_len);
        if (n < 0) {
            log_msg("TLS recv error: %s", tls_last_error());
            break;
        }
        if (n == 0) {
            log_msg("TLS peer closed");
            break;
        }
        buf_len += n;
        buf[buf_len] = '\0';

        /* Drain whole lines (\r\n delimited). */
        char *line_start = buf;
        while (true) {
            char *lf = memchr(line_start, '\n', (size_t)(buf + buf_len - line_start));
            if (!lf) break;
            *lf = '\0';
            /* Strip trailing CR if present. */
            if (lf > line_start && *(lf - 1) == '\r') {
                *(lf - 1) = '\0';
            }
            handle_irc_line(tls, line_start);
            line_start = lf + 1;
        }
        /* Keep leftover partial line. */
        int leftover = (int)(buf + buf_len - line_start);
        if (leftover > 0 && line_start != buf) {
            memmove(buf, line_start, leftover);
        }
        buf_len = leftover;
        if (buf_len >= (int)sizeof(buf) - 1) {
            /* A suffix of this oversized line must never become a new IRC command. */
            log_msg("IRC line overflow; reconnecting");
            break;
        }
    }

    irc_clear_send_connection(tls);
    tls_close(tls);
    queue_push(&g_queue, "chat", "chat reconnecting...", true);
}

static DWORD WINAPI twitch_asset_thread(LPVOID arg) {
    (void)arg;
    twitch_badges_load_global();
    bttv_load_global();
    ffz_load_global();
    seventv_load_global();
    twitch_api_load_global();
    preload_known_twitch_room_assets();
    return 0;
}

static DWORD WINAPI irc_thread(LPVOID arg) {
    (void)arg;
    add_builtin_emote_fallbacks();
    while (!g_stop) {
        irc_session();
        if (g_stop) break;
        Sleep(RECONNECT_MS);
    }
    return 0;
}

/* ===== Kick Pusher WebSocket =========================================== */

static bool ws_read_exact(tls_conn_t *tls, void *buf, int len) {
    char *p = (char *)buf;
    int got_total = 0;
    while (got_total < len && !g_stop) {
        int n = tls_recv(tls, p + got_total, len - got_total);
        if (n <= 0) return false;
        got_total += n;
    }
    return got_total == len;
}

static void base64_encode_small(const uint8_t *src, int len,
                                char *out, int out_cap)
{
    static const char table[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    int o = 0;
    for (int i = 0; i < len && o + 4 < out_cap; i += 3) {
        int rem = len - i;
        uint32_t v = ((uint32_t)src[i]) << 16;
        if (rem > 1) v |= ((uint32_t)src[i + 1]) << 8;
        if (rem > 2) v |= src[i + 2];

        out[o++] = table[(v >> 18) & 63];
        out[o++] = table[(v >> 12) & 63];
        out[o++] = rem > 1 ? table[(v >> 6) & 63] : '=';
        out[o++] = rem > 2 ? table[v & 63] : '=';
    }
    out[o] = '\0';
}

static bool secure_random_bytes(uint8_t *bytes, DWORD count) {
    HCRYPTPROV provider = 0;
    if (!CryptAcquireContextA(&provider, NULL, NULL, PROV_RSA_FULL,
                              CRYPT_VERIFYCONTEXT | CRYPT_SILENT)) return false;
    bool ok = CryptGenRandom(provider, count, bytes) != FALSE;
    CryptReleaseContext(provider, 0);
    return ok;
}

static bool make_websocket_key(char *out, int out_cap) {
    uint8_t bytes[16];
    if (out_cap < 25 || !secure_random_bytes(bytes, sizeof(bytes))) return false;
    base64_encode_small(bytes, 16, out, out_cap);
    return true;
}

static bool make_websocket_accept(const char *key, char *out, int out_cap) {
    static const char guid[] = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    HCRYPTPROV provider = 0;
    HCRYPTHASH hash = 0;
    uint8_t digest[20];
    DWORD size = sizeof(digest);
    if (out_cap < 29 || !CryptAcquireContextA(&provider, NULL, NULL, PROV_RSA_FULL,
                                             CRYPT_VERIFYCONTEXT | CRYPT_SILENT)) return false;
    bool ok = CryptCreateHash(provider, CALG_SHA1, 0, 0, &hash) &&
        CryptHashData(hash, (const BYTE *)key, (DWORD)strlen(key), 0) &&
        CryptHashData(hash, (const BYTE *)guid, sizeof(guid) - 1, 0) &&
        CryptGetHashParam(hash, HP_HASHVAL, digest, &size, 0) && size == sizeof(digest);
    if (ok) base64_encode_small(digest, sizeof(digest), out, out_cap);
    if (hash) CryptDestroyHash(hash);
    CryptReleaseContext(provider, 0);
    return ok;
}

static bool http_header_has_token(const char *value, const char *token) {
    size_t length = strlen(token);
    while (*value) {
        const char *end = strchr(value, ',');
        if (!end) end = value + strlen(value);
        const char *next = *end ? end + 1 : end;
        while (value < end && (*value == ' ' || *value == '\t')) value++;
        while (end > value && (end[-1] == ' ' || end[-1] == '\t')) end--;
        if ((size_t)(end - value) == length && _strnicmp(value, token, length) == 0) return true;
        value = next;
    }
    return false;
}

static bool websocket_validate_upgrade(char *response, const char *key) {
    if (strncmp(response, "HTTP/1.1 101 ", 13) != 0) return false;
    char expected[29];
    if (!make_websocket_accept(key, expected, sizeof(expected))) return false;
    bool upgrade = false, connection = false, accept = false;
    char *line = strstr(response, "\r\n");
    if (!line) return false;
    line += 2;
    while (*line) {
        char *end = strstr(line, "\r\n");
        if (!end) return false;
        if (end == line) return upgrade && connection && accept && end[2] == '\0';
        *end = '\0';
        char *colon = strchr(line, ':');
        if (!colon) return false;
        *colon = '\0';
        char *value = colon + 1;
        trim_ascii_in_place(value);
        if (_stricmp(line, "Upgrade") == 0) {
            upgrade = upgrade || http_header_has_token(value, "websocket");
        } else if (_stricmp(line, "Connection") == 0) {
            connection = connection || http_header_has_token(value, "upgrade");
        } else if (_stricmp(line, "Sec-WebSocket-Accept") == 0) {
            if (accept || strcmp(value, expected) != 0) return false;
            accept = true;
        } else if (_stricmp(line, "Sec-WebSocket-Extensions") == 0 ||
                   _stricmp(line, "Sec-WebSocket-Protocol") == 0) {
            return false; /* Neither was offered in our request. */
        }
        line = end + 2;
    }
    return false;
}

static bool websocket_handshake(tls_conn_t *tls) {
    char key[32];
    if (g_stop || !make_websocket_key(key, sizeof(key))) return false;

    char request[1024];
    int request_len = snprintf(request, sizeof(request),
        "GET /app/32cbd69e4b950bf97679?protocol=7&client=js&version=8.4.0-rc2&flash=false HTTP/1.1\r\n"
        "Host: ws-us2.pusher.com\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        "Sec-WebSocket-Key: %s\r\n"
        "Sec-WebSocket-Version: 13\r\n"
        "Origin: https://kick.com\r\n"
        "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36\r\n"
        "\r\n",
        key);
    if (request_len <= 0 || request_len >= (int)sizeof(request)) return false;
    if (tls_send(tls, request, request_len) < 0) return false;

    char response[4096] = {0};
    int len = 0;
    while (len + 1 < (int)sizeof(response) && !g_stop) {
        char c;
        int n = tls_recv(tls, &c, 1);
        if (n <= 0 || c == '\0') return false;
        response[len++] = c;
        response[len] = '\0';
        if (len >= 4 && memcmp(response + len - 4, "\r\n\r\n", 4) == 0) {
            return !g_stop && websocket_validate_upgrade(response, key);
        }
    }
    return false; /* Canceled, oversized or incomplete headers never establish a connection. */
}

static bool websocket_send_frame(tls_conn_t *tls, uint8_t opcode,
                                 const char *payload, size_t payload_len)
{
    if (payload_len > 65535u || ((opcode & 0x8u) && payload_len > 125u)) return false;

    uint8_t header[8];
    int h = 0;
    header[h++] = (uint8_t)(0x80u | (opcode & 0x0f));
    if (payload_len < 126u) {
        header[h++] = (uint8_t)(0x80u | payload_len);
    } else {
        header[h++] = 0x80u | 126u;
        header[h++] = (uint8_t)((payload_len >> 8) & 0xff);
        header[h++] = (uint8_t)(payload_len & 0xff);
    }

    uint8_t mask[4];
    if (!secure_random_bytes(mask, sizeof(mask))) return false;
    for (int i = 0; i < 4; i++) {
        header[h++] = mask[i];
    }

    size_t total = (size_t)h + payload_len;
    uint8_t *frame = (uint8_t *)malloc(total);
    if (!frame) return false;
    memcpy(frame, header, (size_t)h);
    for (size_t i = 0; i < payload_len; i++) {
        frame[h + i] = ((const uint8_t *)payload)[i] ^ mask[i & 3u];
    }
    bool ok = tls_send(tls, frame, (int)total) == (int)total;
    free(frame);
    return ok;
}

static bool websocket_send_text(tls_conn_t *tls, const char *text) {
    return websocket_send_frame(tls, 0x1, text, strlen(text));
}

static bool websocket_read_frame_header(tls_conn_t *tls, uint64_t *payload_len,
                                        uint8_t *opcode, bool *final)
{
    uint8_t hdr[2];
    if (!ws_read_exact(tls, hdr, 2)) return false;

    *opcode = hdr[0] & 0x0f;
    *final = (hdr[0] & 0x80u) != 0;
    /* Server frames are unmasked; no RSV extension was negotiated. */
    if ((hdr[0] & 0x70u) || (hdr[1] & 0x80u) ||
        (*opcode != 0 && *opcode != 1 && *opcode != 8 && *opcode != 9 && *opcode != 10)) return false;
    uint64_t len = hdr[1] & 0x7fu;
    if ((*opcode & 0x8u) && (!*final || len > 125u)) return false;
    if (len == 126u) {
        uint8_t ext[2];
        if (!ws_read_exact(tls, ext, 2)) return false;
        len = (((uint64_t)ext[0]) << 8) | ext[1];
        if (len < 126u) return false;
    } else if (len == 127u) {
        uint8_t ext[8];
        if (!ws_read_exact(tls, ext, 8)) return false;
        if (ext[0] & 0x80u) return false;
        len = 0;
        for (int i = 0; i < 8; i++) {
            len = (len << 8) | ext[i];
        }
        if (len < 65536u) return false;
    }
    *payload_len = len;
    return true;
}

/* Assemble one bounded text message; control frames can interrupt any fragment. */
static bool websocket_read_text(tls_conn_t *tls, char *payload, size_t capacity) {
    if (capacity == 0 || capacity > INT_MAX) return false;
    size_t used = 0;
    bool fragmented = false;
    payload[0] = '\0';
    while (!g_stop) {
        uint64_t length;
        uint8_t opcode;
        bool final;
        if (!websocket_read_frame_header(tls, &length, &opcode, &final)) return false;
        if (opcode & 0x8u) {
            char control[125];
            if (!ws_read_exact(tls, control, (int)length)) return false;
            if (opcode == 8) return false;
            if (opcode == 9 && !websocket_send_frame(tls, 10, control, (size_t)length)) return false;
            continue;
        }
        if ((opcode == 0) != fragmented || length >= capacity - used) return false;
        if (!ws_read_exact(tls, payload + used, (int)length)) return false;
        used += (size_t)length;
        payload[used] = '\0';
        if (final) return utf8_valid_prefix_bytes(payload, used) == used;
        fragmented = true;
    }
    return false;
}

static void append_utf8_codepoint(char *out, int out_cap, int *o,
                                  unsigned codepoint)
{
    if (codepoint < 0x80u) {
        if (*o + 1 < out_cap) out[(*o)++] = (char)codepoint;
    } else if (codepoint < 0x800u) {
        if (*o + 2 < out_cap) {
            out[(*o)++] = (char)(0xc0u | (codepoint >> 6));
            out[(*o)++] = (char)(0x80u | (codepoint & 0x3fu));
        }
    } else if (codepoint < 0x10000u) {
        if (*o + 3 < out_cap) {
            out[(*o)++] = (char)(0xe0u | (codepoint >> 12));
            out[(*o)++] = (char)(0x80u | ((codepoint >> 6) & 0x3fu));
            out[(*o)++] = (char)(0x80u | (codepoint & 0x3fu));
        }
    } else if (codepoint <= 0x10ffffu) {
        if (*o + 4 < out_cap) {
            out[(*o)++] = (char)(0xf0u | (codepoint >> 18));
            out[(*o)++] = (char)(0x80u | ((codepoint >> 12) & 0x3fu));
            out[(*o)++] = (char)(0x80u | ((codepoint >> 6) & 0x3fu));
            out[(*o)++] = (char)(0x80u | (codepoint & 0x3fu));
        }
    }
}

/* ===== Keyboard input =================================================== */

static void paste_clipboard_text(void) {
    if (!OpenClipboard(NULL)) return;
    HANDLE h = GetClipboardData(CF_UNICODETEXT);
    if (h) {
        const wchar_t *w = (const wchar_t *)GlobalLock(h);
        if (w) {
            size_t len = wcslen(w);
            if (len > 512) len = 512;
            input_append_utf16_sanitized(w, (int)len);
            GlobalUnlock(h);
        }
    }
    CloseClipboard();
}

static bool input_foreground_context_is_active(void) {
    HWND foreground = GetForegroundWindow();
    if (!foreground) return false;

    DWORD foreground_pid = 0;
    GetWindowThreadProcessId(foreground, &foreground_pid);

    HWND focus_hwnd = NULL;
    DWORD focus_pid = 0;
    EnterCriticalSection(&g_input_cs);
    focus_hwnd = g_input_focus_hwnd;
    focus_pid = g_input_focus_pid;
    LeaveCriticalSection(&g_input_cs);

    if (g_vlc_pid != 0) {
        return foreground_pid == g_vlc_pid;
    }
    if (focus_pid != 0) {
        return foreground_pid == focus_pid;
    }
    if (focus_hwnd != NULL) {
        return foreground == focus_hwnd;
    }
    return true;
}

static bool input_handle_virtual_key(DWORD vk, DWORD scan_code, DWORD flags) {
    if (!input_is_focused()) return false;

    if (!input_foreground_context_is_active()) {
        input_set_focused(false);
        return false;
    }

    const bool alt_down = (flags & LLKHF_ALTDOWN) != 0
                       || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
    if (alt_down) {
        input_set_focused(false);
        return false;
    }

    /*
     * Do not consume Shift itself.  WH_KEYBOARD_LL runs before Windows updates
     * the asynchronous keyboard state; swallowing Shift here means ToUnicode
     * sees the following shifted key (for example OEM_2) as unshifted, so '?'
     * is inserted as '/'.  The character key is still consumed below, keeping
     * overlay input isolated while allowing Windows to track the modifier.
     */
    if (vk == VK_SHIFT || vk == VK_LSHIFT || vk == VK_RSHIFT) {
        return false;
    }

    const bool ctrl_down = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                        || (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0
                        || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;

    if (vk == VK_ESCAPE) {
        input_set_focused(false);
        return true;
    }
    if (vk == VK_RETURN) {
        submit_current_input();
        return true;
    }
    if (vk == VK_BACK) {
        input_backspace();
        return true;
    }
    if (vk == VK_DELETE) {
        return true;
    }
    if (ctrl_down && (vk == 'V')) {
        paste_clipboard_text();
        return true;
    }
    if (ctrl_down) {
        return true;
    }
    if (vk == VK_TAB) {
        input_append_utf8_sanitized(" ");
        return true;
    }
    if (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL
        || vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU
        || vk == VK_CAPITAL) {
        return true;
    }

    BYTE state[256];
    if (!GetKeyboardState(state)) {
        memset(state, 0, sizeof(state));
    }
    state[VK_SHIFT] = (GetAsyncKeyState(VK_SHIFT) & 0x8000) ? 0x80 : 0;
    state[VK_CONTROL] = (GetAsyncKeyState(VK_CONTROL) & 0x8000) ? 0x80 : 0;
    state[VK_MENU] = (GetAsyncKeyState(VK_MENU) & 0x8000) ? 0x80 : 0;
    if (GetKeyState(VK_CAPITAL) & 1) state[VK_CAPITAL] = 1;

    wchar_t chars[8];
    int n = ToUnicode(vk, scan_code, state, chars,
                      (int)(sizeof(chars) / sizeof(chars[0])), 0);
    if (n > 0) {
        input_append_utf16_sanitized(chars, n);
    }
    return true;
}

static LRESULT CALLBACK keyboard_hook_proc(int code, WPARAM wparam, LPARAM lparam) {
    if (code == HC_ACTION
        && (wparam == WM_KEYDOWN || wparam == WM_SYSKEYDOWN)) {
        KBDLLHOOKSTRUCT *kb = (KBDLLHOOKSTRUCT *)lparam;
        if (input_handle_virtual_key(kb->vkCode, kb->scanCode, kb->flags)) {
            return 1;
        }
    }
    return CallNextHookEx(g_keyboard_hook, code, wparam, lparam);
}

static DWORD WINAPI keyboard_thread(LPVOID arg) {
    (void)arg;
    g_keyboard_thread_id = GetCurrentThreadId();
    g_keyboard_hook = SetWindowsHookExA(WH_KEYBOARD_LL,
                                        keyboard_hook_proc, NULL, 0);
    if (!g_keyboard_hook) {
        log_msg("keyboard hook unavailable (gle=%lu)", GetLastError());
        return 0;
    }

    MSG msg;
    while (!g_stop && GetMessageA(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageA(&msg);
    }

    UnhookWindowsHookEx(g_keyboard_hook);
    g_keyboard_hook = NULL;
    return 0;
}

static void html_to_plain_text(const char *in, char *out, int out_cap) {
    int o = 0;
    bool in_tag = false;
    int in_len = in ? (int)strlen(in) : 0;
    for (int i = 0; i < in_len && o + 1 < out_cap; i++) {
        char c = in[i];
        if (in_tag) {
            if (c == '>') in_tag = false;
            continue;
        }
        if (c == '<') {
            in_tag = true;
            continue;
        }
        if (c == '&') {
            const char *semi = strchr(in + i, ';');
            if (semi && semi - (in + i) <= 12) {
                size_t n = (size_t)(semi - (in + i) + 1);
                if (strncmp(in + i, "&amp;", n) == 0) { out[o++] = '&'; i += (int)n - 1; continue; }
                if (strncmp(in + i, "&lt;", n) == 0) { out[o++] = '<'; i += (int)n - 1; continue; }
                if (strncmp(in + i, "&gt;", n) == 0) { out[o++] = '>'; i += (int)n - 1; continue; }
                if (strncmp(in + i, "&quot;", n) == 0) { out[o++] = '"'; i += (int)n - 1; continue; }
                if (strncmp(in + i, "&#39;", n) == 0 || strncmp(in + i, "&apos;", n) == 0) {
                    out[o++] = '\'';
                    i += (int)n - 1;
                    continue;
                }
                if (i + 3 < in_len && in[i + 1] == '#') {
                    char *endptr = NULL;
                    unsigned long cp;
                    if (in[i + 2] == 'x' || in[i + 2] == 'X') {
                        cp = strtoul(in + i + 3, &endptr, 16);
                    } else {
                        cp = strtoul(in + i + 2, &endptr, 10);
                    }
                    if (endptr == semi && cp > 0) {
                        append_utf8_codepoint(out, out_cap, &o, (unsigned)cp);
                        i += (int)n - 1;
                        continue;
                    }
                }
            }
        }
        int step = utf8_valid_sequence_len(
            (const unsigned char *)in + i,
            (size_t)(in_len - i));
        if (step > 1) {
            if (o + step >= out_cap) break;
            memcpy(out + o, in + i, (size_t)step);
            o += step;
            i += step - 1;
        } else if ((unsigned char)c < 0x80u) {
            out[o++] = c;
        }
    }
    out[o] = '\0';
}

static bool string_contains_ci(const char *text, const char *needle)
{
    if (!text || !needle || !needle[0]) return false;

    size_t needle_len = strlen(needle);
    for (const char *p = text; *p; p++) {
        size_t i = 0;
        while (i < needle_len && p[i]
               && tolower((unsigned char)p[i])
                  == tolower((unsigned char)needle[i])) {
            i++;
        }
        if (i == needle_len) return true;
    }
    return false;
}

static bool looks_like_image_url(const char *value)
{
    if (!value) return false;
    while (*value && isspace((unsigned char)*value)) value++;
    return _strnicmp(value, "https://", 8) == 0
        || strncmp(value, "//", 2) == 0
        || value[0] == '/';
}

static bool is_image_like_property_name(const char *name)
{
    return string_contains_ci(name, "image")
        || string_contains_ci(name, "icon")
        || ascii_equals_ci(name, "url")
        || ascii_equals_ci(name, "src");
}

static bool json_find_image_like_string(const char *value,
                                        const char *value_end,
                                        char *out, int out_size)
{
    if (!value || !value_end || value >= value_end || !out || out_size <= 0) {
        return false;
    }

    if (*value == '{') {
        const char *cursor = value + 1;
        while (cursor < value_end) {
            char name[80] = {0};
            const char *child = NULL;
            const char *child_end = NULL;
            if (!json_next_direct_property(&cursor, value_end,
                                           name, sizeof(name),
                                           &child, &child_end)) {
                break;
            }

            if (child && child < child_end && *child == '"'
                && is_image_like_property_name(name)) {
                char candidate[220] = {0};
                if (json_get_string_from_value(child, child_end,
                                               candidate, sizeof(candidate))
                    && looks_like_image_url(candidate)) {
                    snprintf(out, (size_t)out_size, "%s", candidate);
                    return true;
                }
            }

            if (child && child < child_end && (*child == '{' || *child == '[')
                && json_find_image_like_string(child, child_end,
                                               out, out_size)) {
                return true;
            }
        }
    } else if (*value == '[') {
        const char *cursor = value + 1;
        while (cursor < value_end) {
            const char *child = NULL;
            const char *child_end = NULL;
            if (!json_next_array_value(&cursor, value_end,
                                       &child, &child_end)) {
                break;
            }
            if (child && child < child_end && (*child == '{' || *child == '[')
                && json_find_image_like_string(child, child_end,
                                               out, out_size)) {
                return true;
            }
        }
    }

    return false;
}

static bool kick_badge_image_url(const char *obj, const char *obj_end,
                                 char *out, int out_size)
{
    static const char *direct_names[] = {
        "image_url",
        "imageUrl",
        "image",
        "iconUrl",
        "icon_url",
        "icon",
        "url",
        "src"
    };

    for (int i = 0; i < (int)(sizeof(direct_names) / sizeof(direct_names[0])); i++) {
        if (json_get_string_direct(obj, obj_end,
                                   direct_names[i], out, out_size)
            && looks_like_image_url(out)) {
            return true;
        }
    }

    return json_find_image_like_string(obj, obj_end, out, out_size);
}

static bool kick_add_channel_badge(const char *id, const char *version,
                                   const char *image_url)
{
    if (!id || !id[0] || !version || !version[0]
        || !image_url || !image_url[0]) {
        return false;
    }

    char code[BTTV_CODE_MAX] = {0};
    if (!make_kick_badge_code(id, version, code, sizeof(code))) {
        return false;
    }

    char normalized_url[IMAGE_PATH_MAX] = {0};
    if (!normalize_kick_asset_url(image_url, normalized_url,
                                  sizeof(normalized_url))) {
        return false;
    }

    bool added = emote_catalog_add_url(code, normalized_url, 36, 36);
    if (added) {
        int image_index = bttv_lookup(code);
        if (image_index >= 0) {
            (void)bttv_ensure_image(image_index);
        }
    }
    int version_number = 0;
    char normalized_id[MAX_BADGE_ID] = {0};
    normalize_badge_id(id, normalized_id, sizeof(normalized_id));
    if (added
        && ascii_equals_ci(normalized_id, "subscriber")
        && parse_positive_int_string(version, &version_number)) {
        kick_register_subscriber_badge_version(version_number);
    }
    return added;
}

static int kick_badges_load_bundled_manifest(void)
{
    static int cached_added = -1;
    if (cached_added >= 0) return cached_added;
    cached_added = 0;

    if (!g_kick_badge_manifest_path[0]) return 0;

    char manifest_path[IMAGE_PATH_MAX];
    if (!full_path_from_utf8(g_kick_badge_manifest_path,
                             manifest_path, sizeof(manifest_path))) {
        return 0;
    }
    normalize_path_separators(manifest_path);

    char root_dir[IMAGE_PATH_MAX];
    if (!directory_from_path(manifest_path, root_dir, sizeof(root_dir))) {
        return 0;
    }
    normalize_path_separators(root_dir);

    byte_buf_t json;
    if (!file_read_bytes(manifest_path, HTTP_MAX_JSON, &json)) {
        log_msg("Bundled Kick badge manifest unavailable: %s", manifest_path);
        return 0;
    }

    const char *end = (const char *)json.data + json.size;
    const char *root = json_document_root((const char *)json.data, end);
    const char *entries = NULL;
    const char *entries_end = NULL;
    int added = 0;
    int preloaded = 0;
    if (json_get_array_direct(root, end, "entries", &entries, &entries_end)) {
        const char *cursor = entries + 1;
        while (cursor < entries_end) {
            const char *entry = NULL;
            const char *entry_end = NULL;
            if (!json_next_array_value(&cursor, entries_end,
                                       &entry, &entry_end)) {
                break;
            }
            if (!entry || entry >= entry_end || *entry != '{') {
                continue;
            }

            char id[MAX_BADGE_ID] = {0};
            char version[MAX_BADGE_VERSION] = {0};
            char image[IMAGE_PATH_MAX] = {0};
            char image_path[IMAGE_PATH_MAX] = {0};
            char code[BTTV_CODE_MAX] = {0};
            if (json_get_string_direct(entry, entry_end, "id", id, sizeof(id))
                && json_get_string_direct(entry, entry_end, "version",
                                          version, sizeof(version))
                && json_get_string_direct(entry, entry_end, "image",
                                          image, sizeof(image))
                && make_kick_badge_code(id, version, code, sizeof(code))
                && build_manifest_image_path(root_dir, image,
                                             image_path, sizeof(image_path))
                && emote_catalog_add_file(code, image_path, 36, 36)) {
                if (bttv_preload_image_by_code(code)) {
                    preloaded++;
                }

                int version_number = 0;
                char normalized_id[MAX_BADGE_ID] = {0};
                normalize_badge_id(id, normalized_id, sizeof(normalized_id));
                if (ascii_equals_ci(normalized_id, "subscriber")
                    && parse_positive_int_string(version, &version_number)) {
                    kick_register_subscriber_badge_version(version_number);
                }
                added++;
            }
        }
    } else {
        log_msg("Bundled Kick badge manifest has no entries array: %s",
                manifest_path);
    }

    byte_buf_free(&json);
    if (added > 0) {
        log_msg("Bundled Kick badges loaded: %d, preloaded: %d",
                added, preloaded);
        signal_render();
    } else {
        log_msg("Bundled Kick badge manifest loaded no badges: %s",
                manifest_path);
    }
    cached_added = added;
    return cached_added;
}

typedef struct {
    const char *id;
    const char *file;
} kick_builtin_badge_asset_t;

static bool kick_builtin_badge_path(const char *file, char *out, int out_cap)
{
    if (!file || !file[0] || !out || out_cap <= 0) return false;

    char module_path[MAX_PATH];
    DWORD n = GetModuleFileNameA(NULL, module_path, (DWORD)sizeof(module_path));
    if (n == 0 || n >= sizeof(module_path)) return false;
    normalize_path_separators(module_path);

    char module_dir[MAX_PATH];
    if (!directory_from_path(module_path, module_dir, sizeof(module_dir))) {
        return false;
    }

    n = (DWORD)snprintf(out, (size_t)out_cap, "%skick-badges\\%s",
                        module_dir, file);
    if (n == 0 || n >= (DWORD)out_cap) return false;
    normalize_path_separators(out);
    return true;
}

static int kick_add_builtin_badge_file(const char *id, const char *file)
{
    char path[IMAGE_PATH_MAX];
    if (!kick_builtin_badge_path(file, path, sizeof(path))) return 0;

    const char *versions[] = { "1", "0", "" };
    int added = 0;
    for (int i = 0; i < (int)(sizeof(versions) / sizeof(versions[0])); i++) {
        char code[BTTV_CODE_MAX] = {0};
        if (make_kick_badge_code(id, versions[i], code, sizeof(code))
            && emote_catalog_add_file(code, path, 32, 32)) {
            (void)bttv_preload_image_by_code(code);
            added++;
        }
    }
    return added;
}

static int kick_badges_load_builtin(void)
{
    static bool loaded = false;
    if (loaded) return 0;
    loaded = true;

    static const kick_builtin_badge_asset_t assets[] = {
        {"broadcaster",     "broadcaster.png"},
        {"channel_host",    "broadcaster.png"},
        {"creator",         "broadcaster.png"},
        {"founder",         "founder.png"},
        {"global_mod",      "moderator.png"},
        {"mod",             "moderator.png"},
        {"moderator",       "moderator.png"},
        {"og",              "og.png"},
        {"sidekick",        "sidekick.png"},
        {"staff",           "staff.png"},
        {"sub_gift_leader", "sub_gifter.png"},
        {"sub_gifter",      "sub_gifter.png"},
        {"subscriber",      "subscriber.png"},
        {"verified",        "verified.png"},
        {"vip",             "vip.png"},
    };

    int added = 0;
    for (int i = 0; i < (int)(sizeof(assets) / sizeof(assets[0])); i++) {
        added += kick_add_builtin_badge_file(assets[i].id, assets[i].file);
    }

    if (added > 0) {
        log_msg("Kick built-in badges loaded: %d", added);
        signal_render();
    }
    return added;
}

static int kick_parse_channel_subscriber_badges(const char *json, size_t len)
{
    const char *end = json + len;
    const char *root = json_document_root(json, end);
    const char *arr = NULL;
    const char *arr_end = NULL;
    if (!json_get_array_direct(root, end, "subscriber_badges",
                               &arr, &arr_end)) {
        return 0;
    }

    int added = 0;
    const char *cursor = arr + 1;
    while (cursor < arr_end) {
        const char *value = NULL;
        const char *value_end = NULL;
        if (!json_next_array_value(&cursor, arr_end, &value, &value_end)) {
            break;
        }
        if (!value || value >= value_end || *value != '{') {
            continue;
        }

        char months[MAX_BADGE_VERSION] = {0};
        char image_url[IMAGE_PATH_MAX] = {0};
        if (json_get_scalar_string_direct(value, value_end,
                                          "months", months, sizeof(months))
            && parse_positive_int_string(months, &(int){0})
            && kick_badge_image_url(value, value_end,
                                    image_url, sizeof(image_url))
            && kick_add_channel_badge("subscriber", months, image_url)) {
            added++;
        }
    }
    return added;
}

static void kick_badges_load_channel(void)
{
    if (g_chat_provider != CHAT_PROVIDER_KICK || !g_channel[0]) return;

    char path[160];
    wchar_t wpath[160];
    snprintf(path, sizeof(path), "/api/v2/channels/%s", g_channel);
    if (!utf8_to_wide_path(path, wpath, 160)) {
        return;
    }

    wchar_t wchannel[128];
    wchar_t extra_headers[512];
    if (!utf8_to_wide_path(g_channel, wchannel, 128)) {
        extra_headers[0] = L'\0';
    } else {
        swprintf(extra_headers, 512,
                 L"Referer: https://kick.com/%ls\r\n"
                 L"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36\r\n"
                 L"Accept-Language: *\r\n",
                 wchannel);
    }

    byte_buf_t json;
    if (!https_get_bytes_ex(L"kick.com", wpath,
                            L"application/json,text/plain,*/*",
                            extra_headers,
                            HTTP_MAX_JSON, &json)) {
        log_msg("Kick channel badge load failed for %s", g_channel);
        return;
    }

    int added = kick_parse_channel_subscriber_badges((const char *)json.data,
                                                     json.size);
    byte_buf_free(&json);
    log_msg("Kick channel subscriber badges loaded: %d", added);
    if (added > 0) signal_render();
}

static int kick_collect_badges_from_array(const char *arr, const char *arr_end,
                                          chat_badge_t *out, int cap,
                                          int count)
{
    const char *cursor = arr + 1;
    while (cursor < arr_end && count < cap) {
        const char *value = NULL;
        const char *value_end = NULL;
        if (!json_next_array_value(&cursor, arr_end, &value, &value_end)) {
            break;
        }
        if (!value || value >= value_end) {
            continue;
        }

        if (*value == '"') {
            char id[MAX_BADGE_ID] = {0};
            if (json_get_string_from_value(value, value_end,
                                           id, sizeof(id))) {
                (void)add_chat_badge(out, cap, &count, id, NULL,
                                     NULL, NULL, false);
            }
            continue;
        }

        if (*value != '{') {
            continue;
        }

        const char *obj = value;
        const char *obj_end = value_end;

        char id[MAX_BADGE_ID] = {0};
        char version[MAX_BADGE_VERSION] = {0};
        char text[64] = {0};
        if (!json_get_string_direct(obj, obj_end, "type", id, sizeof(id))
            && !json_get_string_direct(obj, obj_end, "id", id, sizeof(id))
            && !json_get_string_direct(obj, obj_end, "name", id, sizeof(id))
            && !json_get_string_direct(obj, obj_end, "slug", id, sizeof(id))
            && !json_get_string_direct(obj, obj_end, "badge_type", id, sizeof(id))
            && !json_get_string_direct(obj, obj_end, "text", id, sizeof(id))) {
            continue;
        }

        if (!json_get_scalar_string_direct(obj, obj_end, "count", version, sizeof(version))
            && !json_get_scalar_string_direct(obj, obj_end, "months", version, sizeof(version))
            && !json_get_scalar_string_direct(obj, obj_end, "tier", version, sizeof(version))) {
            const char *metadata = NULL;
            const char *metadata_end = NULL;
            if (json_get_object_direct(obj, obj_end, "metadata",
                                       &metadata, &metadata_end)) {
                if (!json_get_scalar_string_direct(metadata, metadata_end,
                                                   "level", version, sizeof(version))
                    && !json_get_scalar_string_direct(metadata, metadata_end,
                                                      "count", version, sizeof(version))
                    && !json_get_scalar_string_direct(metadata, metadata_end,
                                                      "months", version, sizeof(version))
                    && !json_get_scalar_string_direct(metadata, metadata_end,
                                                      "tier", version, sizeof(version))) {
                    (void)json_get_scalar_string_direct(metadata, metadata_end,
                                                        "info", version, sizeof(version));
                }
            }
            if (!version[0]) {
                (void)json_get_scalar_string_direct(obj, obj_end, "info",
                                                    version, sizeof(version));
            }
        }

        if (!json_get_string_direct(obj, obj_end, "text", text, sizeof(text))) {
            if (!json_get_string_direct(obj, obj_end, "title", text, sizeof(text))) {
                if (!json_get_string_direct(obj, obj_end, "label", text, sizeof(text))) {
                    const char *metadata = NULL;
                    const char *metadata_end = NULL;
                    if (json_get_object_direct(obj, obj_end, "metadata",
                                               &metadata, &metadata_end)) {
                        if (!json_get_string_direct(metadata, metadata_end,
                                                    "title", text, sizeof(text))
                            && !json_get_string_direct(metadata, metadata_end,
                                                       "label", text, sizeof(text))) {
                            (void)json_get_string_direct(metadata, metadata_end,
                                                         "info", text, sizeof(text));
                        }
                    }
                    if (!text[0]
                        && !json_get_string_direct(obj, obj_end, "info",
                                                   text, sizeof(text))) {
                        (void)json_get_string_direct(obj, obj_end, "name",
                                                     text, sizeof(text));
                    }
                }
            }
        }

        char image_url[220] = {0};
        (void)kick_badge_image_url(obj, obj_end,
                                   image_url, sizeof(image_url));

        (void)add_chat_badge(out, cap, &count, id,
                             version[0] ? version : NULL, text,
                             image_url[0] ? image_url : NULL, false);
    }
    return count;
}

static int kick_collect_badges_from_object(const char *obj, const char *obj_end,
                                           chat_badge_t *out, int cap,
                                           int count)
{
    const char *identity = NULL;
    const char *identity_end = NULL;
    if (json_get_object_direct(obj, obj_end, "identity",
                               &identity, &identity_end)) {
        const char *arr = NULL;
        const char *arr_end = NULL;
        if (json_get_array_direct(identity, identity_end, "badges_v2",
                                  &arr, &arr_end)) {
            count = kick_collect_badges_from_array(arr, arr_end,
                                                   out, cap, count);
        }
        if (json_get_array_direct(identity, identity_end, "badges",
                                  &arr, &arr_end)) {
            count = kick_collect_badges_from_array(arr, arr_end,
                                                   out, cap, count);
        }
    }

    const char *arr = NULL;
    const char *arr_end = NULL;
    if (json_get_array_direct(obj, obj_end, "badges_v2", &arr, &arr_end)) {
        count = kick_collect_badges_from_array(arr, arr_end,
                                               out, cap, count);
    }
    if (json_get_array_direct(obj, obj_end, "badges", &arr, &arr_end)) {
        count = kick_collect_badges_from_array(arr, arr_end,
                                               out, cap, count);
    }
    return count;
}

static void kick_handle_pusher_text(tls_conn_t *tls, const char *text) {
    const char *end = text + strlen(text);
    char event[128] = {0};
    if (!json_get_string_direct(text, end, "event", event, sizeof(event))) {
        return;
    }

    if (strcmp(event, "pusher:ping") == 0) {
        websocket_send_text(tls, "{\"event\":\"pusher:pong\",\"data\":{}}");
        return;
    }
    if (strcmp(event, "pusher:connection_established") == 0) {
        queue_push(&g_queue, "chat", "connected to Kick chat", true);
        return;
    }
    if (strcmp(event, "pusher_internal:subscription_succeeded") == 0) {
        char joined[96];
        snprintf(joined, sizeof(joined), "joined #%s", g_channel);
        queue_push(&g_queue, "chat", joined, true);
        return;
    }
    if (strcmp(event, "App\\Events\\ChatMessageEvent") != 0) {
        return;
    }

    char data[WS_MAX_PAYLOAD];
    if (!json_get_string_direct(text, end, "data", data, sizeof(data))) {
        return;
    }

    const char *data_start = data;
    while (*data_start && isspace((unsigned char)*data_start)) data_start++;
    const char *data_end = data + strlen(data);
    bool nested = false;
    const char *obj_end = json_object_end(data_start, data_end, &nested);
    if (!obj_end) return;

    char content[2048] = {0};
    if (!json_get_string_direct(data_start, obj_end,
                                "content", content, sizeof(content))) {
        return;
    }

    char user[MAX_USERNAME] = {0};
    chat_badge_t badges[MAX_MSG_BADGES];
    int badge_count = 0;
    const char *sender = NULL;
    const char *sender_end = NULL;
    if (json_get_object_direct(data_start, obj_end, "sender",
                               &sender, &sender_end)) {
        if (!json_get_string_direct(sender, sender_end,
                                    "username", user, sizeof(user))) {
            (void)json_get_string_direct(sender, sender_end,
                                         "slug", user, sizeof(user));
        }
        badge_count = kick_collect_badges_from_object(sender, sender_end,
                                                      badges, MAX_MSG_BADGES,
                                                      badge_count);
    }
    if (!user[0]) {
        (void)json_get_string_direct(data_start, obj_end,
                                     "sender_username", user, sizeof(user));
    }
    if (badge_count == 0) {
        badge_count = kick_collect_badges_from_object(data_start, obj_end,
                                                      badges, MAX_MSG_BADGES,
                                                      badge_count);
    }
    if (!user[0]) return;

    char plain[MAX_MSG_TEXT] = {0};
    html_to_plain_text(content, plain, sizeof(plain));
    if (!plain[0]) return;

    chat_emote_t emotes[MAX_MSG_EMOTES];
    int emote_count = kick_collect_emotes_from_text(plain, emotes,
                                                    MAX_MSG_EMOTES);
    queue_push_ex(&g_queue, user, plain, false,
                  badges, badge_count, emotes, emote_count);
}

static void kick_session(void) {
    tls_conn_t *tls = tls_connect("ws-us2.pusher.com", "443");
    if (!tls) {
        log_msg("Kick TLS connect failed: %s", tls_last_error());
        queue_push(&g_queue, "chat", "Kick chat reconnecting (tls failed)", true);
        return;
    }

    if (!websocket_handshake(tls)) {
        log_msg("Kick websocket handshake failed: %s", tls_last_error());
        tls_close(tls);
        queue_push(&g_queue, "chat", "Kick chat reconnecting (websocket failed)", true);
        return;
    }
    log_msg("Kick websocket connected");

    char subscribe[256];
    snprintf(subscribe, sizeof(subscribe),
             "{\"event\":\"pusher:subscribe\",\"data\":{\"auth\":\"\",\"channel\":\"chatrooms.%s.v2\"}}",
             g_kick_chatroom_id);
    if (!websocket_send_text(tls, subscribe)) {
        log_msg("Kick websocket subscribe send failed: %s", tls_last_error());
        tls_close(tls);
        return;
    }
    queue_push(&g_queue, "chat", "connecting to Kick chat...", true);

    char payload[WS_MAX_PAYLOAD];
    while (!g_stop) {
        if (!websocket_read_text(tls, payload, sizeof(payload))) {
            log_msg("Kick websocket closed or returned an invalid message: %s", tls_last_error());
            break;
        }
        kick_handle_pusher_text(tls, payload);
    }

    tls_close(tls);
    queue_push(&g_queue, "chat", "Kick chat reconnecting...", true);
}

static DWORD WINAPI kick_thread(LPVOID arg) {
    (void)arg;
    if (kick_badges_load_bundled_manifest() <= 0) {
        kick_badges_load_builtin();
    }
    kick_badges_load_channel();
    add_builtin_emote_fallbacks();
    while (!g_stop) {
        kick_session();
        if (g_stop) break;
        Sleep(RECONNECT_MS);
    }
    return 0;
}

/* ===== Rendering ======================================================== */

/* The user-color palette. Same 8 colors as the PowerShell version so the
 * visual identity stays consistent. */
typedef enum {
    BADGE_ICON_GENERIC,
    BADGE_ICON_CAMERA,
    BADGE_ICON_CHECK,
    BADGE_ICON_CROWN,
    BADGE_ICON_DIAMOND,
    BADGE_ICON_GIFT,
    BADGE_ICON_SHIELD,
    BADGE_ICON_STAR
} badge_icon_t;

typedef struct {
    const char  *id;
    COLORREF     fill;
    badge_icon_t icon;
} badge_visual_t;

static const COLORREF kPalette[] = {
    RGB(0x7D, 0xD3, 0xFC),  /* sky */
    RGB(0x86, 0xEF, 0xAC),  /* mint */
    RGB(0xFD, 0xE6, 0x8A),  /* amber */
    RGB(0xFC, 0xA5, 0xA5),  /* rose */
    RGB(0xC4, 0xB5, 0xFD),  /* violet */
    RGB(0xF9, 0xA8, 0xD4),  /* pink */
    RGB(0x67, 0xE8, 0xF9),  /* cyan */
    RGB(0xFD, 0xBA, 0x74),  /* orange */
};
static const COLORREF kSystemColor = RGB(0x93, 0xC5, 0xFD);  /* light blue */
static const COLORREF kShadowColor = RGB(0x00, 0x00, 0x00);
static const COLORREF kWhiteColor  = RGB(0xFF, 0xFF, 0xFF);

static const badge_visual_t kBadgeVisuals[] = {
    {"admin",           RGB(0xE0, 0x8C, 0x1A), BADGE_ICON_SHIELD},
    {"ambassador",      RGB(0x2E, 0x7B, 0xEA), BADGE_ICON_CHECK},
    {"artist_badge",    RGB(0xD4, 0x3D, 0x8E), BADGE_ICON_STAR},
    {"bits",            RGB(0x7C, 0x4D, 0xFF), BADGE_ICON_DIAMOND},
    {"bits_charity",    RGB(0x7C, 0x4D, 0xFF), BADGE_ICON_DIAMOND},
    {"bits_leader",     RGB(0x7C, 0x4D, 0xFF), BADGE_ICON_DIAMOND},
    {"bot_badge",       RGB(0x2E, 0x7B, 0xEA), BADGE_ICON_CHECK},
    {"broadcaster",     RGB(0xD1, 0x32, 0x32), BADGE_ICON_CAMERA},
    {"clip_champ",      RGB(0xE0, 0x8C, 0x1A), BADGE_ICON_STAR},
    {"clips_leader",    RGB(0xE0, 0x8C, 0x1A), BADGE_ICON_STAR},
    {"founder",         RGB(0x0D, 0x7F, 0x8C), BADGE_ICON_STAR},
    {"game_developer",  RGB(0xE0, 0x8C, 0x1A), BADGE_ICON_STAR},
    {"global_mod",      RGB(0x16, 0x8A, 0x4A), BADGE_ICON_SHIELD},
    {"hype_train",      RGB(0x7C, 0x4D, 0xFF), BADGE_ICON_STAR},
    {"moderator",       RGB(0x16, 0x8A, 0x4A), BADGE_ICON_SHIELD},
    {"mod",             RGB(0x16, 0x8A, 0x4A), BADGE_ICON_SHIELD},
    {"no_audio",        RGB(0x2B, 0x34, 0x42), BADGE_ICON_GENERIC},
    {"no_video",        RGB(0x2B, 0x34, 0x42), BADGE_ICON_GENERIC},
    {"og",              RGB(0xB7, 0x79, 0x1F), BADGE_ICON_STAR},
    {"partner",         RGB(0x2E, 0x7B, 0xEA), BADGE_ICON_CHECK},
    {"premium",         RGB(0x7C, 0x4D, 0xFF), BADGE_ICON_CROWN},
    {"prime",           RGB(0x7C, 0x4D, 0xFF), BADGE_ICON_CROWN},
    {"raider",          RGB(0x16, 0x8A, 0x4A), BADGE_ICON_SHIELD},
    {"sidekick",        RGB(0x2E, 0x7B, 0xEA), BADGE_ICON_CHECK},
    {"staff",           RGB(0xE0, 0x8C, 0x1A), BADGE_ICON_SHIELD},
    {"sub_gift_leader", RGB(0x0D, 0x7F, 0x8C), BADGE_ICON_GIFT},
    {"sub_gifter",      RGB(0x0D, 0x7F, 0x8C), BADGE_ICON_GIFT},
    {"subscriber",      RGB(0x0D, 0x7F, 0x8C), BADGE_ICON_STAR},
    {"turbo",           RGB(0x7C, 0x4D, 0xFF), BADGE_ICON_DIAMOND},
    {"twitch_dj",       RGB(0x7C, 0x4D, 0xFF), BADGE_ICON_DIAMOND},
    {"verified",        RGB(0x2E, 0x7B, 0xEA), BADGE_ICON_CHECK},
    {"vip",             RGB(0xD4, 0x3D, 0x8E), BADGE_ICON_CHECK},
};

static void normalize_badge_style_id(const char *id, char *out, int out_size)
{
    if (!out || out_size <= 0) return;
    int o = 0;
    if (id) {
        while (*id && isspace((unsigned char)*id)) id++;
        for (; *id && o + 1 < out_size; id++) {
            unsigned char c = (unsigned char)*id;
            if (isspace(c) || c == ',' || c == '/') break;
            out[o++] = (char)(c == '-' ? '_' : tolower(c));
        }
    }
    while (o > 0 && isspace((unsigned char)out[o - 1])) o--;
    out[o] = '\0';

    if (ascii_equals_ci(out, "subgifter")
        || ascii_equals_ci(out, "gift_sub")
        || ascii_equals_ci(out, "gift_subs")
        || ascii_equals_ci(out, "gift_subscriber")
        || ascii_equals_ci(out, "gift_subscription")
        || ascii_equals_ci(out, "gifted_sub")
        || ascii_equals_ci(out, "gifted_subs")
        || ascii_equals_ci(out, "gifted_subscriber")
        || ascii_equals_ci(out, "gifted_subscription")
        || ascii_equals_ci(out, "gifter")
        || ascii_equals_ci(out, "sub_gift")
        || ascii_equals_ci(out, "subgift")
        || ascii_equals_ci(out, "sub_gifts")
        || ascii_equals_ci(out, "sub_gifter_badge")
        || ascii_equals_ci(out, "subscriber_gifter")
        || ascii_equals_ci(out, "subscription_gift")
        || ascii_equals_ci(out, "subscription_gifts")) {
        snprintf(out, (size_t)out_size, "%s", "sub_gifter");
    } else if (ascii_equals_ci(out, "channel_host")
               || ascii_equals_ci(out, "creator")) {
        snprintf(out, (size_t)out_size, "%s", "broadcaster");
    } else if (ascii_equals_ci(out, "subscription")
               || ascii_equals_ci(out, "subscriptions")
               || ascii_equals_ci(out, "sub")) {
        snprintf(out, (size_t)out_size, "%s", "subscriber");
    }
}

static bool badge_visual_for_id_ex(const char *id, bool allow_default,
                                   badge_visual_t *visual)
{
    if (!visual) return false;
    visual->id = NULL;
    visual->fill = RGB(0x2B, 0x34, 0x42);
    visual->icon = BADGE_ICON_GENERIC;

    char normalized[MAX_BADGE_ID] = {0};
    normalize_badge_style_id(id, normalized, sizeof(normalized));
    if (!normalized[0]) return false;

    for (size_t i = 0; i < sizeof(kBadgeVisuals) / sizeof(kBadgeVisuals[0]); i++) {
        if (ascii_equals_ci(kBadgeVisuals[i].id, normalized)) {
            *visual = kBadgeVisuals[i];
            return true;
        }
    }

    return allow_default;
}

static bool badge_visual_for_id(const char *id, badge_visual_t *visual)
{
    return badge_visual_for_id_ex(id, true, visual);
}

static COLORREF badge_fill_color(const char *id) {
    badge_visual_t visual;
    (void)badge_visual_for_id(id, &visual);
    return visual.fill;
}

static COLORREF user_color(const char *user) {
    uint32_t hash = 0;
    for (const char *p = user; *p; p++) {
        hash = (hash * 31u + (uint32_t)(unsigned char)*p) & 0x7fffffffu;
    }
    return kPalette[hash % (sizeof(kPalette) / sizeof(kPalette[0]))];
}

typedef struct {
    HFONT               font;
    wchar_t             text[384];
    int                 text_len;
    int                 width;
    IDWriteTextLayout  *layout;
    uint64_t            last_used;
} text_layout_cache_entry_t;

typedef struct {
    int                 index;
    int                 width;
    int                 height;
    int                 frame;
    const GpImage      *image;
    uint8_t            *pixels;
    size_t              pixel_bytes;
    uint64_t            last_used;
} emote_render_cache_entry_t;

typedef struct {
    HDC      mem_dc;
    HBITMAP  dib_bitmap;
    HBITMAP  prev_bitmap;
    void    *pixels;       /* 32-bit BGRA (Windows-native), top-down */
    int      width;
    int      height;
    int      scale_height;
    int      surface_width;
    int      surface_height;
    HFONT    font_msg;
    HFONT    font_sys;
    HFONT    prev_font;
    /* Direct2D/DirectWrite is used for Unicode text so Segoe UI Emoji's
     * color glyphs are preserved. GDI's DrawText/ExtTextOut APIs flatten
     * color fonts to the monochrome outline shown by the old overlay. */
    ID2D1Factory          *d2d_factory;
    ID2D1DCRenderTarget   *d2d_target;
    IDWriteFactory        *dwrite_factory;
    IDWriteTextFormat     *dwrite_msg;
    IDWriteTextFormat     *dwrite_sys;
    bool                   dwrite_ready;
    bool                   d2d_batch_active;
    ID2D1SolidColorBrush  *d2d_batch_brushes[TEXT_BATCH_BRUSH_CAP];
    COLORREF               d2d_batch_colors[TEXT_BATCH_BRUSH_CAP];
    int                    d2d_batch_brush_count;
    text_layout_cache_entry_t text_layout_cache[TEXT_LAYOUT_CACHE_CAP];
    uint64_t               text_layout_cache_clock;
    emote_render_cache_entry_t emote_render_cache[EMOTE_RENDER_CACHE_CAP];
    uint64_t               emote_render_cache_clock;
    /* Sticky scratch buffer for the RGBA conversion sent to the pipe. */
    uint8_t *rgba_out;
    int      rgba_cap;
    chat_msg_t *snapshot;
    bool     empty_frame_ready;
} renderer_t;

static void renderer_release_directwrite(renderer_t *r)
{
    if (!r) return;
    if (r->dwrite_msg) {
        IDWriteTextFormat_Release(r->dwrite_msg);
        r->dwrite_msg = NULL;
    }
    if (r->dwrite_sys) {
        IDWriteTextFormat_Release(r->dwrite_sys);
        r->dwrite_sys = NULL;
    }
    if (r->dwrite_factory) {
        IDWriteFactory_Release(r->dwrite_factory);
        r->dwrite_factory = NULL;
    }
    if (r->d2d_target) {
        ID2D1DCRenderTarget_Release(r->d2d_target);
        r->d2d_target = NULL;
    }
    if (r->d2d_factory) {
        ID2D1Factory_Release(r->d2d_factory);
        r->d2d_factory = NULL;
    }
    r->dwrite_ready = false;
}

static bool renderer_init_directwrite(renderer_t *r,
                                      int font_msg_px,
                                      int font_sys_px)
{
    if (!r || !r->mem_dc) return false;

    HRESULT hr = D2D1CreateFactory(
        D2D1_FACTORY_TYPE_SINGLE_THREADED,
        &IID_ID2D1Factory,
        NULL,
        (void **)&r->d2d_factory);
    if (FAILED(hr)) goto failed;

    D2D1_RENDER_TARGET_PROPERTIES properties;
    memset(&properties, 0, sizeof(properties));
    properties.type = D2D1_RENDER_TARGET_TYPE_DEFAULT;
    properties.pixelFormat.format = DXGI_FORMAT_B8G8R8A8_UNORM;
    properties.pixelFormat.alphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED;
    properties.dpiX = 96.0f;
    properties.dpiY = 96.0f;
    properties.usage = D2D1_RENDER_TARGET_USAGE_NONE;
    properties.minLevel = D2D1_FEATURE_LEVEL_DEFAULT;
    hr = ID2D1Factory_CreateDCRenderTarget(
        r->d2d_factory, &properties, &r->d2d_target);
    if (FAILED(hr)) goto failed;

    hr = DWriteCreateFactory(
        DWRITE_FACTORY_TYPE_SHARED,
        &IID_IDWriteFactory,
        (IUnknown **)&r->dwrite_factory);
    if (FAILED(hr)) goto failed;

    hr = IDWriteFactory_CreateTextFormat(
        r->dwrite_factory,
        L"Segoe UI",
        NULL,
        DWRITE_FONT_WEIGHT_BOLD,
        DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL,
        (FLOAT)font_msg_px,
        L"en-us",
        &r->dwrite_msg);
    if (FAILED(hr)) goto failed;

    hr = IDWriteFactory_CreateTextFormat(
        r->dwrite_factory,
        L"Segoe UI",
        NULL,
        DWRITE_FONT_WEIGHT_NORMAL,
        DWRITE_FONT_STYLE_NORMAL,
        DWRITE_FONT_STRETCH_NORMAL,
        (FLOAT)font_sys_px,
        L"en-us",
        &r->dwrite_sys);
    if (FAILED(hr)) goto failed;

    IDWriteTextFormat_SetWordWrapping(
        r->dwrite_msg, DWRITE_WORD_WRAPPING_NO_WRAP);
    IDWriteTextFormat_SetWordWrapping(
        r->dwrite_sys, DWRITE_WORD_WRAPPING_NO_WRAP);
    IDWriteTextFormat_SetParagraphAlignment(
        r->dwrite_msg, DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
    IDWriteTextFormat_SetParagraphAlignment(
        r->dwrite_sys, DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
    r->dwrite_ready = true;
    return true;

failed:
    renderer_release_directwrite(r);
    return false;
}

static bool renderer_init(renderer_t *r, int width, int height) {
    memset(r, 0, sizeof(*r));
    if (width <= 0 || height <= 0) return false;
    uint64_t rgba_bytes = (uint64_t)width * (uint64_t)height * 4u;
    if (rgba_bytes == 0 || rgba_bytes > MYO_MAX_PAYLOAD || rgba_bytes > INT_MAX) {
        return false;
    }

    r->scale_height = current_video_height();
    r->width = width;
    r->height = height;
    r->surface_width = scale_reference_px(CHAT_MAX_W);
    r->surface_height = scale_reference_px(CHAT_MAX_H);
    if (r->surface_width < width) r->surface_width = width;
    if (r->surface_height < height) r->surface_height = height;
    uint64_t surface_bytes = (uint64_t)r->surface_width
                           * (uint64_t)r->surface_height * 4u;
    if (surface_bytes == 0 || surface_bytes > MYO_MAX_PAYLOAD
        || surface_bytes > INT_MAX) {
        r->surface_width = width;
        r->surface_height = height;
        surface_bytes = rgba_bytes;
    }

    HDC screen_dc = GetDC(NULL);
    r->mem_dc = CreateCompatibleDC(screen_dc);
    ReleaseDC(NULL, screen_dc);
    if (!r->mem_dc) return false;

    BITMAPINFO bmi = {0};
    bmi.bmiHeader.biSize        = sizeof(bmi.bmiHeader);
    bmi.bmiHeader.biWidth       = r->surface_width;
    bmi.bmiHeader.biHeight      = -r->surface_height; /* top-down */
    bmi.bmiHeader.biPlanes      = 1;
    bmi.bmiHeader.biBitCount    = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    r->dib_bitmap = CreateDIBSection(r->mem_dc, &bmi, DIB_RGB_COLORS, &r->pixels, NULL, 0);
    if (!r->dib_bitmap || !r->pixels) {
        DeleteDC(r->mem_dc);
        return false;
    }
    r->prev_bitmap = (HBITMAP)SelectObject(r->mem_dc, r->dib_bitmap);
    SetBkMode(r->mem_dc, TRANSPARENT);

    const int font_msg_px = scale_reference_px(g_font_size_px);
    const int font_sys_px = scale_reference_px(g_system_font_size_px);
    r->font_msg = CreateFontW(
        -font_msg_px, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    r->font_sys = CreateFontW(
        -font_sys_px, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
        ANTIALIASED_QUALITY, DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
    r->prev_font = (HFONT)SelectObject(r->mem_dc, r->font_msg);
    if (!renderer_init_directwrite(r, font_msg_px, font_sys_px)) {
        log_msg("DirectWrite color-font renderer unavailable; falling back to GDI text");
    }

    r->rgba_cap = (int)surface_bytes;
    r->rgba_out = (uint8_t *)malloc((size_t)r->rgba_cap);
    if (!r->rgba_out) {
        renderer_release_directwrite(r);
        SelectObject(r->mem_dc, r->prev_bitmap);
        SelectObject(r->mem_dc, r->prev_font);
        DeleteObject(r->dib_bitmap);
        DeleteObject(r->font_msg);
        DeleteObject(r->font_sys);
        DeleteDC(r->mem_dc);
        return false;
    }
    r->snapshot = (chat_msg_t *)calloc(QUEUE_CAP, sizeof(chat_msg_t));
    if (!r->snapshot) {
        renderer_release_directwrite(r);
        free(r->rgba_out);
        SelectObject(r->mem_dc, r->prev_bitmap);
        SelectObject(r->mem_dc, r->prev_font);
        DeleteObject(r->dib_bitmap);
        DeleteObject(r->font_msg);
        DeleteObject(r->font_sys);
        DeleteDC(r->mem_dc);
        return false;
    }
    return true;
}

/* Resize only the pixel surfaces.  Fonts, DirectWrite factories, and the
 * persistent message snapshot do not depend on the bitmap dimensions, so
 * rebuilding them for every mouse-move resize event needlessly stalls the
 * render thread. */
static bool renderer_resize(renderer_t *r, int width, int height)
{
    if (!r || !r->mem_dc || width <= 0 || height <= 0) return false;
    if (width == r->width && height == r->height) return true;

    uint64_t rgba_bytes = (uint64_t)width * (uint64_t)height * 4u;
    if (rgba_bytes == 0 || rgba_bytes > MYO_MAX_PAYLOAD
        || rgba_bytes > INT_MAX) {
        return false;
    }

    if (width <= r->surface_width && height <= r->surface_height) {
        r->width = width;
        r->height = height;
        r->empty_frame_ready = false;
        return true;
    }

    int new_surface_width = r->surface_width > width
                          ? r->surface_width : width;
    int new_surface_height = r->surface_height > height
                           ? r->surface_height : height;
    uint64_t surface_bytes = (uint64_t)new_surface_width
                           * (uint64_t)new_surface_height * 4u;
    if (surface_bytes == 0 || surface_bytes > MYO_MAX_PAYLOAD
        || surface_bytes > INT_MAX) {
        return false;
    }

    uint8_t *new_rgba = (uint8_t *)malloc((size_t)surface_bytes);
    if (!new_rgba) return false;

    BITMAPINFO bmi = {0};
    bmi.bmiHeader.biSize        = sizeof(bmi.bmiHeader);
    bmi.bmiHeader.biWidth       = new_surface_width;
    bmi.bmiHeader.biHeight      = -new_surface_height;
    bmi.bmiHeader.biPlanes      = 1;
    bmi.bmiHeader.biBitCount    = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    void *new_pixels = NULL;
    HBITMAP new_bitmap = CreateDIBSection(
        r->mem_dc, &bmi, DIB_RGB_COLORS, &new_pixels, NULL, 0);
    if (!new_bitmap || !new_pixels) {
        if (new_bitmap) DeleteObject(new_bitmap);
        free(new_rgba);
        return false;
    }

    HBITMAP old_bitmap = (HBITMAP)SelectObject(r->mem_dc, new_bitmap);
    if (!old_bitmap || old_bitmap == (HBITMAP)HGDI_ERROR) {
        SelectObject(r->mem_dc, r->dib_bitmap);
        DeleteObject(new_bitmap);
        free(new_rgba);
        return false;
    }

    DeleteObject(old_bitmap);
    free(r->rgba_out);
    r->dib_bitmap = new_bitmap;
    r->pixels = new_pixels;
    r->width = width;
    r->height = height;
    r->surface_width = new_surface_width;
    r->surface_height = new_surface_height;
    r->rgba_out = new_rgba;
    r->rgba_cap = (int)surface_bytes;
    r->empty_frame_ready = false;
    SetBkMode(r->mem_dc, TRANSPARENT);
    return true;
}

static void renderer_end_text_batch(renderer_t *r)
{
    if (!r || !r->d2d_batch_active) return;

    (void)ID2D1RenderTarget_EndDraw(
        (ID2D1RenderTarget *)r->d2d_target, NULL, NULL);
    for (int i = 0; i < r->d2d_batch_brush_count; i++) {
        if (r->d2d_batch_brushes[i]) {
            ID2D1SolidColorBrush_Release(r->d2d_batch_brushes[i]);
            r->d2d_batch_brushes[i] = NULL;
        }
    }
    r->d2d_batch_brush_count = 0;
    r->d2d_batch_active = false;
}

static bool renderer_begin_text_batch(renderer_t *r)
{
    if (!r || !r->dwrite_ready || !r->d2d_target) return false;
    if (r->d2d_batch_active) return true;

    RECT target_rect = {0, 0, r->width, r->height};
    HRESULT hr = ID2D1DCRenderTarget_BindDC(
        r->d2d_target, r->mem_dc, &target_rect);
    if (FAILED(hr)) return false;

    ID2D1RenderTarget_SetTextAntialiasMode(
        (ID2D1RenderTarget *)r->d2d_target,
        D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    ID2D1RenderTarget_BeginDraw((ID2D1RenderTarget *)r->d2d_target);
    r->d2d_batch_brush_count = 0;
    r->d2d_batch_active = true;
    return true;
}

static ID2D1SolidColorBrush *renderer_text_batch_brush(renderer_t *r,
                                                       COLORREF color)
{
    if (!r || !r->d2d_batch_active) return NULL;
    for (int i = 0; i < r->d2d_batch_brush_count; i++) {
        if (r->d2d_batch_colors[i] == color) {
            return r->d2d_batch_brushes[i];
        }
    }
    if (r->d2d_batch_brush_count >= TEXT_BATCH_BRUSH_CAP) return NULL;

    D2D1_COLOR_F brush_color = {
        (FLOAT)GetRValue(color) / 255.0f,
        (FLOAT)GetGValue(color) / 255.0f,
        (FLOAT)GetBValue(color) / 255.0f,
        1.0f
    };
    ID2D1SolidColorBrush *brush = NULL;
    HRESULT hr = ID2D1RenderTarget_CreateSolidColorBrush(
        (ID2D1RenderTarget *)r->d2d_target,
        &brush_color,
        NULL,
        &brush);
    if (FAILED(hr) || !brush) return NULL;

    int index = r->d2d_batch_brush_count++;
    r->d2d_batch_colors[index] = color;
    r->d2d_batch_brushes[index] = brush;
    return brush;
}

static void renderer_destroy(renderer_t *r) {
    if (!r->mem_dc) return;
    renderer_end_text_batch(r);
    for (int i = 0; i < TEXT_LAYOUT_CACHE_CAP; i++) {
        if (r->text_layout_cache[i].layout) {
            IDWriteTextLayout_Release(r->text_layout_cache[i].layout);
            r->text_layout_cache[i].layout = NULL;
        }
    }
    for (int i = 0; i < EMOTE_RENDER_CACHE_CAP; i++) {
        free(r->emote_render_cache[i].pixels);
        r->emote_render_cache[i].pixels = NULL;
    }
    renderer_release_directwrite(r);
    SelectObject(r->mem_dc, r->prev_bitmap);
    SelectObject(r->mem_dc, r->prev_font);
    DeleteObject(r->dib_bitmap);
    if (r->font_msg) DeleteObject(r->font_msg);
    if (r->font_sys) DeleteObject(r->font_sys);
    DeleteDC(r->mem_dc);
    free(r->rgba_out);
    free(r->snapshot);
    memset(r, 0, sizeof(*r));
}

static void clear_frame(renderer_t *r) {
    /* DIB is 32 bpp; zero = fully transparent. */
    uint8_t *base = (uint8_t *)r->pixels;
    const size_t row_bytes = (size_t)r->width * 4u;
    const size_t pitch = (size_t)r->surface_width * 4u;
    for (int py = 0; py < r->height; py++) {
        memset(base + (size_t)py * pitch, 0, row_bytes);
    }
}

static void fill_bgra_rect(renderer_t *r, int x, int y, int w, int h,
                           COLORREF color, uint8_t alpha)
{
    if (w <= 0 || h <= 0) return;
    int x0 = x < 0 ? 0 : x;
    int y0 = y < 0 ? 0 : y;
    int x1 = x + w > r->width ? r->width : x + w;
    int y1 = y + h > r->height ? r->height : y + h;
    if (x0 >= x1 || y0 >= y1) return;

    /* Direct2D and cached emotes share a premultiplied BGRA surface. Keep
     * manually painted input backgrounds/borders in that same representation. */
    uint8_t b = (uint8_t)((GetBValue(color) * alpha + 127u) / 255u);
    uint8_t g = (uint8_t)((GetGValue(color) * alpha + 127u) / 255u);
    uint8_t red = (uint8_t)((GetRValue(color) * alpha + 127u) / 255u);
    uint8_t *base = (uint8_t *)r->pixels;
    for (int py = y0; py < y1; py++) {
        uint8_t *row = base + (size_t)py * (size_t)r->surface_width * 4u
                     + (size_t)x0 * 4u;
        for (int px = x0; px < x1; px++) {
            row[0] = b;
            row[1] = g;
            row[2] = red;
            row[3] = alpha;
            row += 4;
        }
    }
}

static void stroke_bgra_rect(renderer_t *r, int x, int y, int w, int h,
                             COLORREF color, uint8_t alpha)
{
    fill_bgra_rect(r, x, y, w, 1, color, alpha);
    fill_bgra_rect(r, x, y + h - 1, w, 1, color, alpha);
    fill_bgra_rect(r, x, y, 1, h, color, alpha);
    fill_bgra_rect(r, x + w - 1, y, 1, h, color, alpha);
}

static IDWriteTextFormat *renderer_text_format(renderer_t *r, HFONT font)
{
    if (!r || !r->dwrite_ready) return NULL;
    return font == r->font_sys ? r->dwrite_sys : r->dwrite_msg;
}

static IDWriteTextLayout *create_directwrite_layout(renderer_t *r, HFONT font,
                                                    const wchar_t *text,
                                                    int text_len,
                                                    FLOAT max_width,
                                                    FLOAT max_height)
{
    if (!r || !r->dwrite_ready || !text || text_len <= 0
        || max_width <= 0.0f || max_height <= 0.0f) {
        return NULL;
    }

    IDWriteTextFormat *format = renderer_text_format(r, font);
    if (!format) return NULL;

    IDWriteTextLayout *layout = NULL;
    HRESULT hr = IDWriteFactory_CreateTextLayout(
        r->dwrite_factory,
        text,
        (UINT32)text_len,
        format,
        max_width,
        max_height,
        &layout);
    if (FAILED(hr) || !layout) return NULL;
    return layout;
}

static IDWriteTextLayout *renderer_find_text_layout(renderer_t *r, HFONT font,
                                                     const wchar_t *text,
                                                     int text_len,
                                                     int *out_width)
{
    if (!r || !font || !text || text_len <= 0
        || text_len >= (int)(sizeof(r->text_layout_cache[0].text)
                             / sizeof(r->text_layout_cache[0].text[0]))) {
        return NULL;
    }

    for (int i = 0; i < TEXT_LAYOUT_CACHE_CAP; i++) {
        text_layout_cache_entry_t *entry = &r->text_layout_cache[i];
        if (!entry->layout || entry->font != font
            || entry->text_len != text_len) {
            continue;
        }
        if (memcmp(entry->text, text, (size_t)text_len * sizeof(wchar_t)) != 0) {
            continue;
        }

        entry->last_used = ++r->text_layout_cache_clock;
        if (out_width) *out_width = entry->width;
        IDWriteTextLayout_AddRef(entry->layout);
        return entry->layout;
    }
    return NULL;
}

static void renderer_store_text_layout(renderer_t *r, HFONT font,
                                        const wchar_t *text, int text_len,
                                        int width,
                                        IDWriteTextLayout *layout)
{
    if (!r || !font || !text || text_len <= 0 || !layout
        || text_len >= (int)(sizeof(r->text_layout_cache[0].text)
                             / sizeof(r->text_layout_cache[0].text[0]))) {
        return;
    }

    int slot = -1;
    uint64_t oldest = UINT64_MAX;
    for (int i = 0; i < TEXT_LAYOUT_CACHE_CAP; i++) {
        text_layout_cache_entry_t *entry = &r->text_layout_cache[i];
        if (entry->layout && entry->font == font
            && entry->text_len == text_len
            && memcmp(entry->text, text,
                      (size_t)text_len * sizeof(wchar_t)) == 0) {
            return;
        }
        if (!entry->layout) {
            slot = i;
            break;
        }
        if (entry->last_used < oldest) {
            oldest = entry->last_used;
            slot = i;
        }
    }
    if (slot < 0) return;

    text_layout_cache_entry_t *entry = &r->text_layout_cache[slot];
    if (entry->layout) IDWriteTextLayout_Release(entry->layout);
    memset(entry, 0, sizeof(*entry));
    entry->font = font;
    entry->text_len = text_len;
    memcpy(entry->text, text, (size_t)text_len * sizeof(wchar_t));
    entry->width = width;
    entry->layout = layout;
    IDWriteTextLayout_AddRef(entry->layout);
    entry->last_used = ++r->text_layout_cache_clock;
}

static bool draw_directwrite_layout(renderer_t *r,
                                    IDWriteTextLayout *layout,
                                    int x, int y, int max_width,
                                    int max_height, COLORREF color)
{
    if (!r || !r->dwrite_ready || !layout || max_width <= 0
        || max_height <= 0) {
        return false;
    }

    /* Runs are measured once at a wide width, but the final draw must retain
     * the old per-run clipping/wrapping behavior for an unusually long word. */
    (void)IDWriteTextLayout_SetMaxWidth(layout, (FLOAT)max_width);
    (void)IDWriteTextLayout_SetMaxHeight(layout, (FLOAT)max_height);

    ID2D1SolidColorBrush *brush = NULL;
    bool own_draw = !r->d2d_batch_active;
    HRESULT hr = S_OK;
    if (own_draw) {
        RECT target_rect = {0, 0, r->width, r->height};
        hr = ID2D1DCRenderTarget_BindDC(
            r->d2d_target, r->mem_dc, &target_rect);
        if (FAILED(hr)) return false;

        D2D1_COLOR_F brush_color = {
            (FLOAT)GetRValue(color) / 255.0f,
            (FLOAT)GetGValue(color) / 255.0f,
            (FLOAT)GetBValue(color) / 255.0f,
            1.0f
        };
        hr = ID2D1RenderTarget_CreateSolidColorBrush(
            (ID2D1RenderTarget *)r->d2d_target,
            &brush_color,
            NULL,
            &brush);
        if (FAILED(hr) || !brush) return false;

        ID2D1RenderTarget_SetTextAntialiasMode(
            (ID2D1RenderTarget *)r->d2d_target,
            D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        ID2D1RenderTarget_BeginDraw((ID2D1RenderTarget *)r->d2d_target);
    } else {
        brush = renderer_text_batch_brush(r, color);
        if (!brush) return false;
    }

    D2D1_RECT_F clip_rect = {
        (FLOAT)x,
        (FLOAT)y,
        (FLOAT)(x + max_width),
        (FLOAT)(y + max_height)
    };
    ID2D1RenderTarget_PushAxisAlignedClip(
        (ID2D1RenderTarget *)r->d2d_target,
        &clip_rect,
        D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    D2D1_POINT_2F origin = {(FLOAT)x, (FLOAT)y};
    ID2D1RenderTarget_DrawTextLayout(
        (ID2D1RenderTarget *)r->d2d_target,
        origin,
        layout,
        (ID2D1Brush *)brush,
        D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT |
            D2D1_DRAW_TEXT_OPTIONS_CLIP);
    ID2D1RenderTarget_PopAxisAlignedClip(
        (ID2D1RenderTarget *)r->d2d_target);
    if (own_draw) {
        hr = ID2D1RenderTarget_EndDraw(
            (ID2D1RenderTarget *)r->d2d_target, NULL, NULL);
        ID2D1SolidColorBrush_Release(brush);
        return SUCCEEDED(hr);
    }
    return true;
}

static bool draw_directwrite_text(renderer_t *r, HFONT font,
                                  const wchar_t *text, int text_len,
                                  int x, int y, int max_width, int max_height,
                                  COLORREF color)
{
    if (!r || !r->dwrite_ready || !text || text_len <= 0 ||
        max_width <= 0 || max_height <= 0) {
        return false;
    }

    IDWriteTextLayout *layout = create_directwrite_layout(
        r, font, text, text_len, (FLOAT)max_width, (FLOAT)max_height);
    if (!layout) return false;

    bool ok = draw_directwrite_layout(
        r, layout, x, y, max_width, max_height, color);
    IDWriteTextLayout_Release(layout);
    return ok;
}

static int measure_directwrite_text_width(renderer_t *r, HFONT font,
                                          const wchar_t *text, int text_len)
{
    if (!r || !r->dwrite_ready || !text || text_len <= 0) return 0;
    int cached_width = 0;
    IDWriteTextLayout *layout = renderer_find_text_layout(
        r, font, text, text_len, &cached_width);
    if (layout) return cached_width;

    layout = create_directwrite_layout(r, font, text, text_len,
                                       32768.0f, 4096.0f);
    if (!layout) return 0;

    DWRITE_TEXT_METRICS metrics;
    memset(&metrics, 0, sizeof(metrics));
    HRESULT hr = IDWriteTextLayout_GetMetrics(layout, &metrics);
    if (FAILED(hr)) {
        IDWriteTextLayout_Release(layout);
        return 0;
    }
    int width = (int)(metrics.widthIncludingTrailingWhitespace + 0.5f);
    renderer_store_text_layout(r, font, text, text_len, width, layout);
    IDWriteTextLayout_Release(layout);
    return width;
}

static bool draw_directwrite_text_shadowed(renderer_t *r, HFONT font,
                                           const wchar_t *text, int text_len,
                                           int x, int y, int max_width,
                                           int max_height, COLORREF color)
{
    IDWriteTextLayout *layout = create_directwrite_layout(
        r, font, text, text_len, (FLOAT)max_width, (FLOAT)max_height);
    if (!layout) return false;

    bool owns_batch = !r->d2d_batch_active;
    if (!renderer_begin_text_batch(r)) {
        IDWriteTextLayout_Release(layout);
        return false;
    }

    bool shadow_drawn = draw_directwrite_layout(
        r, layout, x + scale_reference_px(1), y + scale_reference_px(1), max_width, max_height, kShadowColor);
    bool text_drawn = draw_directwrite_layout(
        r, layout, x, y, max_width, max_height, color);
    if (owns_batch) renderer_end_text_batch(r);
    IDWriteTextLayout_Release(layout);
    return shadow_drawn && text_drawn;
}

static void draw_text_shadowed(renderer_t *r, HFONT font, const wchar_t *wtext,
                                int x, int y, int max_width, COLORREF color)
{
    const int text_len = wtext ? (int)wcslen(wtext) : 0;
    if (draw_directwrite_text_shadowed(r, font, wtext, text_len,
                                       x, y, max_width, 1000, color) ||
        (draw_directwrite_text(r, font, wtext, text_len,
                               x + scale_reference_px(1), y + scale_reference_px(1), max_width, 1000,
                               kShadowColor) &&
         draw_directwrite_text(r, font, wtext, text_len,
                               x, y, max_width, 1000, color))) {
        return;
    }

    HFONT prev = (HFONT)SelectObject(r->mem_dc, font);

    RECT rc;
    /* Shadow first, offset by (+1, +1). */
    rc.left = x + scale_reference_px(1); rc.top = y + scale_reference_px(1); rc.right = x + scale_reference_px(1) + max_width; rc.bottom = y + scale_reference_px(1) + 1000;
    SetTextColor(r->mem_dc, kShadowColor);
    DrawTextW(r->mem_dc, wtext, -1, &rc, DT_WORDBREAK | DT_NOPREFIX);

    rc.left = x; rc.top = y; rc.right = x + max_width; rc.bottom = y + 1000;
    SetTextColor(r->mem_dc, color);
    DrawTextW(r->mem_dc, wtext, -1, &rc, DT_WORDBREAK | DT_NOPREFIX);

    SelectObject(r->mem_dc, prev);
}

static void draw_text_single_line_shadowed(renderer_t *r, HFONT font,
                                           const wchar_t *wtext,
                                           int x, int y, int max_width,
                                           COLORREF color)
{
    const int text_len = wtext ? (int)wcslen(wtext) : 0;
    if (draw_directwrite_text_shadowed(r, font, wtext, text_len,
                                       x, y, max_width, scale_reference_px(64), color) ||
        (draw_directwrite_text(r, font, wtext, text_len,
                               x + scale_reference_px(1), y + scale_reference_px(1), max_width, scale_reference_px(64),
                               kShadowColor) &&
         draw_directwrite_text(r, font, wtext, text_len,
                               x, y, max_width, scale_reference_px(64), color))) {
        return;
    }

    HFONT prev = (HFONT)SelectObject(r->mem_dc, font);
    RECT clip = {x, y - scale_reference_px(2), x + max_width, y + scale_reference_px(64)};
    SetTextColor(r->mem_dc, kShadowColor);
    ExtTextOutW(r->mem_dc, x + scale_reference_px(1), y + scale_reference_px(1), ETO_CLIPPED, &clip,
                wtext, (UINT)wcslen(wtext), NULL);
    SetTextColor(r->mem_dc, color);
    ExtTextOutW(r->mem_dc, x, y, ETO_CLIPPED, &clip,
                wtext, (UINT)wcslen(wtext), NULL);
    SelectObject(r->mem_dc, prev);
}

/* Convert UTF-8 -> UTF-16 into a stack buffer; returns wcslen written. */
static int utf8_to_utf16(const char *src, wchar_t *dst, int dst_cap) {
    int n = MultiByteToWideChar(CP_UTF8, 0, src, -1, dst, dst_cap);
    if (n <= 0) {
        dst[0] = L'\0';
        return 0;
    }
    return n - 1;
}

static void format_channel_placeholder(wchar_t *dst, int dst_cap) {
    if (!dst || dst_cap <= 0) return;
    wchar_t channel_w[96];
    utf8_to_utf16(channel_display_name(), channel_w,
                  (int)(sizeof(channel_w) / sizeof(channel_w[0])));
    if (!channel_w[0]) {
        wcsncpy(channel_w, L"chat",
                sizeof(channel_w) / sizeof(channel_w[0]) - 1);
        channel_w[sizeof(channel_w) / sizeof(channel_w[0]) - 1] = L'\0';
    }
    swprintf(dst, (size_t)dst_cap, L"Message %ls", channel_w);
}

typedef enum {
    RUN_TEXT,
    RUN_EMOTE,
    RUN_BADGE
} run_type_t;

typedef struct {
    run_type_t type;
    wchar_t    text[384];
    int        text_len;
    int        emote_index;
    badge_icon_t badge_icon;
    int        w;
    int        h;
    HFONT      font;
    IDWriteTextLayout *dwrite_layout;
    COLORREF   color;
    COLORREF   fill;
} layout_run_t;

typedef struct {
    int  x;
    int  draw_y;
    bool visible;
} layout_run_position_t;

typedef enum {
    LAYOUT_DRAW_FULL,
    LAYOUT_DRAW_TEXT_ONLY,
    LAYOUT_DRAW_ASSETS_ONLY
} layout_draw_mode_t;

typedef struct {
    int snapshot_index;
    int y;
    int line_h;
} message_render_info_t;

typedef enum {
    BADGE_IMAGE_MISSING,
    BADGE_IMAGE_PLACEHOLDER,
    BADGE_IMAGE_RENDERED
} badge_image_run_result_t;

static int text_height_for_font(renderer_t *r, HFONT font) {
    HFONT prev = (HFONT)SelectObject(r->mem_dc, font);
    TEXTMETRICW tm;
    GetTextMetricsW(r->mem_dc, &tm);
    SelectObject(r->mem_dc, prev);
    return tm.tmHeight + tm.tmExternalLeading;
}

static int measure_text_width(renderer_t *r, HFONT font,
                              const wchar_t *text, int text_len)
{
    if (text_len <= 0) return 0;
    int directwrite_width = measure_directwrite_text_width(
        r, font, text, text_len);
    if (directwrite_width > 0) return directwrite_width;
    HFONT prev = (HFONT)SelectObject(r->mem_dc, font);
    SIZE sz = {0, 0};
    GetTextExtentPoint32W(r->mem_dc, text, text_len, &sz);
    SelectObject(r->mem_dc, prev);
    return sz.cx;
}

static int badge_height_for_font(renderer_t *r, HFONT font)
{
    int h = text_height_for_font(r, font);
    if (h <= 0) {
        h = scale_reference_px(FONT_SIZE_PX + 3);
    }
    return clamp_int(h, 1, scale_reference_px(MAX_FONT_SIZE_PX * 2));
}

static void draw_input_box(renderer_t *r)
{
    const int margin = scale_reference_px(CHAT_INPUT_MARGIN);
    const int input_h = scale_reference_px(CHAT_INPUT_H);
    const int x = margin;
    const int y = r->height - margin - input_h;
    const int w = r->width - margin * 2;
    const int h = input_h;
    if (w <= 24 || y < 0) return;

    char text[MAX_MSG_TEXT];
    char notice[160];
    bool focused = false;
    input_snapshot(text, sizeof(text), &focused, notice, sizeof(notice));
    bool hovered = InterlockedCompareExchange(&g_input_hovered, 0, 0) != 0;
    if (!hovered && !focused && !text[0] && !notice[0]) return;

    COLORREF bg = focused ? RGB(0x10, 0x14, 0x1A) : RGB(0x09, 0x0C, 0x10);
    COLORREF border = focused ? RGB(0x8B, 0xD3, 0xFF) : RGB(0x55, 0x66, 0x72);
    fill_bgra_rect(r, x, y, w, h, bg, focused ? 222 : 176);
    stroke_bgra_rect(r, x, y, w, h, border, focused ? 235 : 150);

    wchar_t display[384];
    display[0] = L'\0';
    COLORREF color = focused ? kWhiteColor : RGB(0xB7, 0xC3, 0xCF);
    bool showing_notice = notice[0] != '\0';
    bool showing_placeholder = false;

    if (showing_notice) {
        utf8_to_utf16(notice, display,
                      (int)(sizeof(display) / sizeof(display[0])));
        color = RGB(0xF7, 0xBA, 0xBA);
    } else if (text[0]) {
        utf8_to_utf16(text, display,
                      (int)(sizeof(display) / sizeof(display[0])));
    } else if (!focused) {
        if (g_chat_provider == CHAT_PROVIDER_KICK) {
            if (!g_kick_chat_token[0]) {
                swprintf(display, sizeof(display) / sizeof(display[0]), L"Kick token needed");
            } else if (!g_kick_send_as_bot && !g_kick_broadcaster_user_id[0]) {
                swprintf(display, sizeof(display) / sizeof(display[0]), L"Kick channel id needed");
            } else {
                format_channel_placeholder(display,
                                           (int)(sizeof(display) / sizeof(display[0])));
            }
        } else if (!g_twitch_chat_auth_ready) {
            swprintf(display, sizeof(display) / sizeof(display[0]), L"OAuth token needed");
        } else {
            format_channel_placeholder(display,
                                       (int)(sizeof(display) / sizeof(display[0])));
        }
        color = RGB(0x9A, 0xA6, 0xB2);
        showing_placeholder = true;
    }

    int text_x = x + scale_reference_px(10);
    int text_w = w - scale_reference_px(20);
    int text_h = text_height_for_font(r, r->font_msg);
    int text_y = y + (h - text_h) / 2 - scale_reference_px(1);
    if (text_y < y + scale_reference_px(2)) text_y = y + scale_reference_px(2);

    wchar_t *visible = display;
    int visible_len = (int)wcslen(visible);
    while (visible_len > 0
           && measure_text_width(r, r->font_msg, visible, visible_len) > text_w) {
        visible++;
        visible_len--;
    }

    if (visible_len > 0) {
        draw_text_single_line_shadowed(r, r->font_msg, visible,
                                       text_x, text_y, text_w, color);
    }

    if (focused && !showing_notice && !showing_placeholder) {
        int cursor_x = text_x;
        if (visible_len > 0) {
            cursor_x += measure_text_width(r, r->font_msg, visible, visible_len);
        }
        const int cursor_margin = scale_reference_px(2);
        if (cursor_x > text_x + text_w - cursor_margin) cursor_x = text_x + text_w - cursor_margin;
        if ((GetTickCount64() / 500u) % 2u == 0) {
            fill_bgra_rect(r, cursor_x + cursor_margin, y + scale_reference_px(7),
                           scale_reference_px(2), h - scale_reference_px(14),
                           RGB(0xE8, 0xF6, 0xFF), 235);
        }
    }
}

static bool input_is_idle(void)
{
    char text[MAX_MSG_TEXT];
    char notice[160];
    bool focused = false;
    input_snapshot(text, sizeof(text), &focused, notice, sizeof(notice));
    return !focused
        && InterlockedCompareExchange(&g_input_hovered, 0, 0) == 0
        && !text[0]
        && !notice[0];
}

static void draw_text_span_shadowed(renderer_t *r, HFONT font,
                                    const wchar_t *text, int text_len,
                                    int x, int y, int clip_right,
                                    COLORREF color,
                                    IDWriteTextLayout *layout)
{
    if (text_len <= 0) return;
    const int clip_width = clip_right - x;
    bool shadow_drawn = layout
        ? draw_directwrite_layout(r, layout, x + scale_reference_px(1), y + scale_reference_px(1),
                                  clip_width, scale_reference_px(64), kShadowColor)
        : draw_directwrite_text(r, font, text, text_len,
                                x + scale_reference_px(1), y + scale_reference_px(1), clip_width, scale_reference_px(64),
                                kShadowColor);
    bool text_drawn = layout
        ? draw_directwrite_layout(r, layout, x, y,
                                  clip_width, scale_reference_px(64), color)
        : draw_directwrite_text(r, font, text, text_len,
                                x, y, clip_width, scale_reference_px(64), color);
    if (shadow_drawn && text_drawn) {
        return;
    }
    if (r->d2d_batch_active) renderer_end_text_batch(r);

    HFONT prev = (HFONT)SelectObject(r->mem_dc, font);
    RECT clip = {x, y - scale_reference_px(2), clip_right, y + scale_reference_px(64)};

    SetTextColor(r->mem_dc, kShadowColor);
    ExtTextOutW(r->mem_dc, x + scale_reference_px(1), y + scale_reference_px(1), ETO_CLIPPED, &clip,
                text, (UINT)text_len, NULL);
    SetTextColor(r->mem_dc, color);
    ExtTextOutW(r->mem_dc, x, y, ETO_CLIPPED, &clip,
                text, (UINT)text_len, NULL);

    SelectObject(r->mem_dc, prev);
}

static void draw_badge_polygon(renderer_t *r, const POINT *points, int count)
{
    if (!r || !points || count <= 0) return;

    HBRUSH brush = CreateSolidBrush(kWhiteColor);
    HPEN pen = CreatePen(PS_SOLID, 1, kWhiteColor);
    if (!brush || !pen) {
        if (brush) DeleteObject(brush);
        if (pen) DeleteObject(pen);
        return;
    }

    HBRUSH prev_brush = (HBRUSH)SelectObject(r->mem_dc, brush);
    HPEN prev_pen = (HPEN)SelectObject(r->mem_dc, pen);
    Polygon(r->mem_dc, points, count);
    SelectObject(r->mem_dc, prev_pen);
    SelectObject(r->mem_dc, prev_brush);
    DeleteObject(pen);
    DeleteObject(brush);
}

static void draw_badge_line(renderer_t *r, int x1, int y1, int x2, int y2,
                            int width)
{
    if (!r) return;

    HPEN pen = CreatePen(PS_SOLID, width, kWhiteColor);
    if (!pen) return;

    HPEN prev_pen = (HPEN)SelectObject(r->mem_dc, pen);
    MoveToEx(r->mem_dc, x1, y1, NULL);
    LineTo(r->mem_dc, x2, y2);
    SelectObject(r->mem_dc, prev_pen);
    DeleteObject(pen);
}

static void draw_badge_icon(renderer_t *r, badge_icon_t icon,
                            int x, int y, int w, int h)
{
    int cx = x + w / 2;
    int cy = y + h / 2;
    int badge_unit = h > 0 ? h : 18;
    int s1 = clamp_int((1 * badge_unit + 9) / 18, 1, badge_unit);
    int s2 = clamp_int((2 * badge_unit + 9) / 18, 1, badge_unit);
    int s3 = clamp_int((3 * badge_unit + 9) / 18, 1, badge_unit);
    int s4 = clamp_int((4 * badge_unit + 9) / 18, 1, badge_unit);
    int s5 = clamp_int((5 * badge_unit + 9) / 18, 1, badge_unit);
    int s6 = clamp_int((6 * badge_unit + 9) / 18, 1, badge_unit);
    int left = x + s4;
    int right = x + w - s4;
    int top = y + s4;
    int bottom = y + h - s4;
    int stroke = s3;

    if (right <= left || bottom <= top) {
        return;
    }

    switch (icon) {
        case BADGE_ICON_CAMERA: {
            fill_bgra_rect(r, left, top + s2, right - left - s3,
                           bottom - top - s3, kWhiteColor, 245);
            POINT lens[] = {
                {right - s3, cy - s4},
                {right, cy - s2},
                {right, cy + s4},
                {right - s3, cy + s6}
            };
            draw_badge_polygon(r, lens, 4);
            break;
        }
        case BADGE_ICON_CHECK:
            draw_badge_line(r, left, cy, cx - s1, bottom - s1, stroke);
            draw_badge_line(r, cx - s1, bottom - s1, right, top, stroke);
            break;
        case BADGE_ICON_CROWN: {
            POINT crown[] = {
                {left, bottom},
                {right, bottom},
                {right - s1, top + s5},
                {cx + s3, cy + s2},
                {cx, top},
                {cx - s3, cy + s2},
                {left + s1, top + s5}
            };
            draw_badge_polygon(r, crown, 7);
            break;
        }
        case BADGE_ICON_DIAMOND: {
            POINT diamond[] = {
                {cx, top},
                {right, cy},
                {cx, bottom},
                {left, cy}
            };
            draw_badge_polygon(r, diamond, 4);
            break;
        }
        case BADGE_ICON_GIFT:
            fill_bgra_rect(r, left, cy - s2, right - left, bottom - cy + s2,
                           kWhiteColor, 245);
            fill_bgra_rect(r, left - s1, top + s4, right - left + s2, s4,
                           kWhiteColor, 245);
            fill_bgra_rect(r, cx - s1, top + s4, s2, bottom - top,
                           badge_fill_color("sub_gifter"), 245);
            break;
        case BADGE_ICON_SHIELD: {
            POINT shield[] = {
                {cx, top},
                {right, top + s3},
                {right - s1, cy + s2},
                {cx, bottom},
                {left + s1, cy + s2},
                {left, top + s3}
            };
            draw_badge_polygon(r, shield, 6);
            break;
        }
        case BADGE_ICON_STAR: {
            POINT star[] = {
                {cx, top},
                {cx + s2, cy - s2},
                {right, cy - s2},
                {cx + s3, cy + s1},
                {right - s1, bottom},
                {cx, cy + s3},
                {left + s1, bottom},
                {cx - s3, cy + s1},
                {left, cy - s2},
                {cx - s2, cy - s2}
            };
            draw_badge_polygon(r, star, 10);
            break;
        }
        case BADGE_ICON_GENERIC:
        default: {
            HBRUSH brush = CreateSolidBrush(kWhiteColor);
            HPEN pen = CreatePen(PS_SOLID, 1, kWhiteColor);
            if (brush && pen) {
                HBRUSH prev_brush = (HBRUSH)SelectObject(r->mem_dc, brush);
                HPEN prev_pen = (HPEN)SelectObject(r->mem_dc, pen);
                Ellipse(r->mem_dc, left, top, right + 1, bottom + 1);
                SelectObject(r->mem_dc, prev_pen);
                SelectObject(r->mem_dc, prev_brush);
            }
            if (pen) DeleteObject(pen);
            if (brush) DeleteObject(brush);
            break;
        }
    }
}

static void draw_badge_run(renderer_t *r, const layout_run_t *run,
                           int x, int y, int clip_right)
{
    if (!run || run->w <= 0 || run->h <= 0) return;
    if (run->badge_icon == BADGE_ICON_GENERIC && run->text_len <= 0
        && run->fill == 0) return;
    if (x >= clip_right) return;

    int w = run->w;
    if (x + w > clip_right) w = clip_right - x;
    if (w <= 0) return;

    fill_bgra_rect(r, x, y, w, run->h, run->fill, 226);
    stroke_bgra_rect(r, x, y, w, run->h, RGB(0x0B, 0x0D, 0x12), 180);

    if (run->text_len <= 0) {
        draw_badge_icon(r, run->badge_icon, x, y, w, run->h);
        return;
    }

    int text_h = text_height_for_font(r, run->font);
    int text_y = y + (run->h - text_h) / 2 - 1;
    if (text_y < y - 1) text_y = y - 1;
    draw_text_span_shadowed(r, run->font, run->text, run->text_len,
                            x + 3, text_y, clip_right, run->color,
                            NULL);
}

static emote_render_cache_entry_t *renderer_find_emote_cache(
    renderer_t *r, int index, int w, int h, int frame, const GpImage *image)
{
    if (!r || !image || w <= 0 || h <= 0) return NULL;
    for (int i = 0; i < EMOTE_RENDER_CACHE_CAP; i++) {
        emote_render_cache_entry_t *entry = &r->emote_render_cache[i];
        if (!entry->pixels || entry->index != index
            || entry->width != w || entry->height != h
            || entry->frame != frame || entry->image != image) {
            continue;
        }
        entry->last_used = ++r->emote_render_cache_clock;
        return entry;
    }
    return NULL;
}

static void renderer_store_emote_cache(renderer_t *r, int index,
                                        int w, int h, int frame,
                                        const GpImage *image,
                                        const uint8_t *pixels,
                                        size_t pixel_bytes)
{
    if (!r || !image || !pixels || pixel_bytes == 0) return;

    uint8_t *copy = (uint8_t *)malloc(pixel_bytes);
    if (!copy) return;
    memcpy(copy, pixels, pixel_bytes);

    int slot = -1;
    uint64_t oldest = UINT64_MAX;
    for (int i = 0; i < EMOTE_RENDER_CACHE_CAP; i++) {
        emote_render_cache_entry_t *entry = &r->emote_render_cache[i];
        if (entry->pixels && entry->index == index
            && entry->width == w && entry->height == h
            && entry->frame == frame && entry->image == image) {
            slot = i;
            break;
        }
        if (!entry->pixels) {
            slot = i;
            break;
        }
        if (entry->last_used < oldest) {
            oldest = entry->last_used;
            slot = i;
        }
    }
    if (slot < 0) {
        free(copy);
        return;
    }

    emote_render_cache_entry_t *entry = &r->emote_render_cache[slot];
    free(entry->pixels);
    entry->index = index;
    entry->width = w;
    entry->height = h;
    entry->frame = frame;
    entry->image = image;
    entry->pixels = copy;
    entry->pixel_bytes = pixel_bytes;
    entry->last_used = ++r->emote_render_cache_clock;
}

static void blend_bttv_emote_pixels(renderer_t *r, const uint8_t *pixels,
                                    int x, int y, int w, int h)
{
    if (!r || !pixels || w <= 0 || h <= 0) return;

    uint8_t *dst_base = (uint8_t *)r->pixels;
    for (int py = 0; py < h; py++) {
        int dy = y + py;
        if (dy < 0 || dy >= r->height) continue;
        for (int px = 0; px < w; px++) {
            int dx = x + px;
            if (dx < 0 || dx >= r->width) continue;

            const uint8_t *s = pixels + ((size_t)py * (size_t)w
                                        + (size_t)px) * 4u;
            uint8_t a = s[3];
            if (a == 0) continue;

            uint8_t *d = dst_base + (size_t)dy * (size_t)r->surface_width * 4u
                       + (size_t)dx * 4u;
            if (a == 255 || d[3] == 0) {
                d[0] = s[0];
                d[1] = s[1];
                d[2] = s[2];
                d[3] = a;
                continue;
            }

            unsigned inv = 255u - a;
            unsigned b = (unsigned)d[0] * inv + 128u;
            unsigned g = (unsigned)d[1] * inv + 128u;
            unsigned red = (unsigned)d[2] * inv + 128u;
            unsigned alpha = (unsigned)d[3] * inv + 128u;
            d[0] = (uint8_t)(s[0] + ((b + (b >> 8)) >> 8));
            d[1] = (uint8_t)(s[1] + ((g + (g >> 8)) >> 8));
            d[2] = (uint8_t)(s[2] + ((red + (red >> 8)) >> 8));
            d[3] = (uint8_t)(a + ((alpha + (alpha >> 8)) >> 8));
        }
    }
}

static void draw_bttv_emote(renderer_t *r, int index, int x, int y, int w, int h,
                            ULONGLONG occurrence_started_ms) {
    if (index < 0 || w <= 0 || h <= 0) return;

    const GpImage *image = NULL;
    int frame = -1;
    EnterCriticalSection(&g_bttv.cs);
    if (index < g_bttv.count && g_bttv.items[index].image) {
        bttv_emote_t *e = &g_bttv.items[index];
        emote_select_animation_frame_locked(e, occurrence_started_ms);
        image = e->image;
        frame = e->animated ? (int)e->current_frame : -1;
    }
    LeaveCriticalSection(&g_bttv.cs);
    if (!image) return;

    emote_render_cache_entry_t *cached = renderer_find_emote_cache(
        r, index, w, h, frame, image);
    if (cached) {
        blend_bttv_emote_pixels(r, cached->pixels, x, y, w, h);
        return;
    }

    size_t pixel_bytes = (size_t)w * (size_t)h * 4u;
    if (pixel_bytes == 0 || pixel_bytes > SIZE_MAX / 2u) return;

    uint8_t *tmp = (uint8_t *)calloc(pixel_bytes, 1);
    if (!tmp) return;

    GpBitmap *bmp = NULL;
    GpStatus st = GdipCreateBitmapFromScan0(w, h, w * 4,
                                            PixelFormat32bppPARGB,
                                            tmp, &bmp);
    if (st != 0 || !bmp) {
        free(tmp);
        return;
    }

    GpGraphics *g = NULL;
    st = GdipGetImageGraphicsContext((GpImage *)bmp, &g);
    if (st != 0 || !g) {
        GdipDisposeImage((GpImage *)bmp);
        free(tmp);
        return;
    }

    GdipSetCompositingMode(g, CompositingModeSourceCopy);
    GdipSetInterpolationMode(g, InterpolationModeHighQualityBicubic);
    GdipSetPixelOffsetMode(g, PixelOffsetModeHalf);

    const uint8_t *cached_pixels = NULL;
    bool rendered = false;
    EnterCriticalSection(&g_bttv.cs);
    if (index < g_bttv.count && g_bttv.items[index].image) {
        bttv_emote_t *e = &g_bttv.items[index];
        emote_select_animation_frame_locked(e, occurrence_started_ms);
        image = e->image;
        frame = e->animated ? (int)e->current_frame : -1;
        cached = renderer_find_emote_cache(r, index, w, h, frame, image);
        if (cached) {
            cached_pixels = cached->pixels;
        } else {
            GdipDrawImageRectI(g, (GpImage *)image, 0, 0, w, h);
            rendered = true;
        }
    }
    LeaveCriticalSection(&g_bttv.cs);
    GdipDeleteGraphics(g);
    GdipDisposeImage((GpImage *)bmp);

    if (cached_pixels) {
        blend_bttv_emote_pixels(r, cached_pixels, x, y, w, h);
    } else if (rendered) {
        renderer_store_emote_cache(r, index, w, h, frame, image,
                                    tmp, pixel_bytes);
        blend_bttv_emote_pixels(r, tmp, x, y, w, h);
    }

    free(tmp);
}

static int add_text_run(renderer_t *r, layout_run_t *runs, int count, int cap,
                        const char *text, int text_len,
                        HFONT font, COLORREF color)
{
    if (count >= cap || text_len <= 0) return count;
    layout_run_t *run = &runs[count];
    memset(run, 0, sizeof(*run));
    run->type = RUN_TEXT;
    run->font = font;
    run->color = color;

    char tmp[512];
    if (text_len >= (int)sizeof(tmp)) text_len = (int)sizeof(tmp) - 1;
    memcpy(tmp, text, (size_t)text_len);
    tmp[text_len] = '\0';

    run->text_len = utf8_to_utf16(tmp, run->text,
                                  (int)(sizeof(run->text) / sizeof(run->text[0])));
    if (run->text_len <= 0) return count;
    if (r->dwrite_ready) {
        int cached_width = 0;
        IDWriteTextLayout *layout = renderer_find_text_layout(
            r, font, run->text, run->text_len, &cached_width);
        if (layout) {
            run->dwrite_layout = layout;
            run->w = cached_width;
        } else {
            layout = create_directwrite_layout(
                r, font, run->text, run->text_len, 32768.0f, 4096.0f);
            if (!layout) {
                goto measure_fallback;
            }

            DWRITE_TEXT_METRICS metrics;
            memset(&metrics, 0, sizeof(metrics));
            if (FAILED(IDWriteTextLayout_GetMetrics(layout, &metrics))) {
                IDWriteTextLayout_Release(layout);
                goto measure_fallback;
            }
            run->dwrite_layout = layout;
            run->w = (int)(metrics.widthIncludingTrailingWhitespace + 0.5f);
            renderer_store_text_layout(r, font, run->text, run->text_len,
                                        run->w, layout);
        }
    }
measure_fallback:
    if (run->w <= 0) {
        run->w = measure_text_width(r, font, run->text, run->text_len);
    }
    run->h = text_height_for_font(r, font);
    return count + 1;
}

static badge_image_run_result_t add_badge_image_run(layout_run_t *runs,
                                                    int *count, int cap,
                                                    const char *image_code,
                                                    int render_h,
                                                    bool allow_placeholder,
                                                    bool *has_emote)
{
    if (!count || *count >= cap || !image_code || !image_code[0]) {
        return BADGE_IMAGE_MISSING;
    }

    int emote_index = bttv_lookup(image_code);
    if (emote_index >= 0) {
        if (bttv_ensure_image(emote_index)) {
            layout_run_t *run = &runs[*count];
            memset(run, 0, sizeof(*run));
            run->type = RUN_EMOTE;
            run->emote_index = emote_index;
            bttv_scaled_size_for_height(emote_index, render_h, render_h * 3,
                                        &run->w, &run->h);
            if (has_emote) *has_emote = true;
            (*count)++;
            return BADGE_IMAGE_RENDERED;
        }

        if (!allow_placeholder) {
            log_emote_debug("badge image cataloged but not ready: %s",
                            image_code);
            return BADGE_IMAGE_MISSING;
        }

        layout_run_t *run = &runs[*count];
        memset(run, 0, sizeof(*run));
        run->type = RUN_BADGE;
        run->badge_icon = BADGE_ICON_GENERIC;
        bttv_scaled_size_for_height(emote_index, render_h, render_h * 3,
                                    &run->w, &run->h);
        if (has_emote) *has_emote = true;
        (*count)++;
        log_emote_debug("badge image cataloged but not ready: %s", image_code);
        return BADGE_IMAGE_PLACEHOLDER;
    }

    return BADGE_IMAGE_MISSING;
}

static bool is_kick_gift_badge(const chat_badge_t *badge)
{
    if (!badge) return false;

    char normalized[MAX_BADGE_ID] = {0};
    normalize_badge_style_id(badge->id, normalized, sizeof(normalized));
    return ascii_equals_ci(normalized, "sub_gifter")
        || ascii_equals_ci(normalized, "sub_gift_leader");
}

static int add_badge_glyph_run(layout_run_t *runs, int count, int cap,
                               const chat_badge_t *badge,
                               HFONT font,
                               int badge_h,
                               bool allow_default_visual)
{
    if (count >= cap || !badge) return count;

    char style_id[MAX_BADGE_ID] = {0};
    normalize_badge_style_id(badge->id, style_id, sizeof(style_id));
    if (!style_id[0]) return count;
    if (is_kick_gift_badge(badge)) {
        strncpy(style_id, "sub_gifter", sizeof(style_id) - 1);
        style_id[sizeof(style_id) - 1] = '\0';
    }

    badge_visual_t visual;
    if (!badge_visual_for_id_ex(style_id, allow_default_visual, &visual)) {
        return count;
    }

    layout_run_t *run = &runs[count];
    memset(run, 0, sizeof(*run));
    run->type = RUN_BADGE;
    run->font = font;
    run->color = kWhiteColor;
    run->fill = visual.fill;
    run->badge_icon = visual.icon;
    run->w = badge_h;
    run->h = badge_h;
    return count + 1;
}

static int add_badge_run(renderer_t *r, layout_run_t *runs, int count, int cap,
                         const chat_badge_t *badge, HFONT font,
                         bool *has_emote)
{
    if (count >= cap || !badge) return count;

    int badge_h = badge_height_for_font(r, font);
    badge_image_run_result_t image_result =
        add_badge_image_run(runs, &count, cap,
                            badge->image_code, badge_h, true, has_emote);
    if (image_result != BADGE_IMAGE_MISSING) return count;

    if (g_chat_provider == CHAT_PROVIDER_KICK) {
        char resolved_code[BTTV_CODE_MAX] = {0};
        int cursor = 0;
        while (make_kick_badge_code_for_request(badge->id, badge->version,
                                                resolved_code,
                                                sizeof(resolved_code),
                                                &cursor)) {
            image_result = add_badge_image_run(runs, &count, cap,
                                               resolved_code, badge_h, true,
                                               has_emote);
            if (image_result != BADGE_IMAGE_MISSING) return count;
        }

        return add_badge_glyph_run(runs, count, cap, badge, font, badge_h,
                                   false);
    }

    char fallback_code[BTTV_CODE_MAX] = {0};
    int cursor = 0;
    while (make_twitch_badge_code_for_request(badge->id, badge->version,
                                              fallback_code,
                                              sizeof(fallback_code),
                                              &cursor)) {
        image_result = add_badge_image_run(runs, &count, cap,
                                           fallback_code, badge_h, false,
                                           has_emote);
        if (image_result != BADGE_IMAGE_MISSING) return count;
    }

    return add_badge_glyph_run(runs, count, cap, badge, font, badge_h, true);
}

static int add_emote_or_text_run(renderer_t *r, layout_run_t *runs,
                                 int count, int cap,
                                 const char *token, int token_len,
                                 HFONT font, COLORREF color,
                                 bool allow_emotes, bool *has_emote)
{
    if (count >= cap || token_len <= 0) return count;

    if (allow_emotes && token_len < BTTV_CODE_MAX) {
        char code[BTTV_CODE_MAX];
        memcpy(code, token, (size_t)token_len);
        code[token_len] = '\0';
        int emote_index = bttv_lookup(code);
        if (emote_index >= 0 && bttv_ensure_image(emote_index)) {
            layout_run_t *run = &runs[count];
            memset(run, 0, sizeof(*run));
            run->type = RUN_EMOTE;
            run->emote_index = emote_index;
            bttv_scaled_size(emote_index, &run->w, &run->h);
            *has_emote = true;
            return count + 1;
        }
        if (emote_index >= 0) {
            log_emote_debug("cataloged but unavailable: %s", code);
        }
    }

    return add_text_run(r, runs, count, cap, token, token_len, font, color);
}

static int add_text_segment_runs(renderer_t *r, layout_run_t *runs,
                                 int count, int cap,
                                 const char *text, int text_len,
                                 HFONT font, COLORREF color,
                                 bool allow_emotes, bool *has_emote,
                                 bool *need_space)
{
    const char *p = text;
    const char *end = text + text_len;
    while (p < end && count < cap) {
        int spaces = 0;
        while (p + spaces < end
               && isspace((unsigned char)p[spaces])) {
            spaces++;
        }
        if (spaces > 0) {
            *need_space = true;
            p += spaces;
        }
        if (p >= end) break;

        if (*need_space) {
            count = add_text_run(r, runs, count, cap, " ", 1, font, color);
            *need_space = false;
        }

        const char *start = p;
        while (p < end && !isspace((unsigned char)*p)) p++;
        count = add_emote_or_text_run(r, runs, count, cap,
                                      start, (int)(p - start),
                                      font, color,
                                      allow_emotes, has_emote);
    }

    return count;
}

static int add_explicit_emote_run(renderer_t *r, layout_run_t *runs,
                                  int count, int cap,
                                  const chat_emote_t *emote,
                                  HFONT font, COLORREF color,
                                  bool *has_emote)
{
    if (count >= cap || !emote || !emote->code[0]) return count;

    int emote_index = bttv_lookup(emote->code);
    if (emote_index >= 0 && bttv_ensure_image(emote_index)) {
        layout_run_t *run = &runs[count];
        memset(run, 0, sizeof(*run));
        run->type = RUN_EMOTE;
        run->emote_index = emote_index;
        bttv_scaled_size(emote_index, &run->w, &run->h);
        *has_emote = true;
        return count + 1;
    }

    if (emote_index >= 0) {
        log_emote_debug("tagged but unavailable: %s", emote->code);
    }
    return add_text_run(r, runs, count, cap,
                        emote->code, (int)strlen(emote->code),
                        font, color);
}

static int build_message_runs(renderer_t *r, const chat_msg_t *m,
                              layout_run_t *runs, int cap,
                              bool *has_emote)
{
    *has_emote = false;
    HFONT font = m->is_system ? r->font_sys : r->font_msg;
    COLORREF prefix_color = m->is_system ? kSystemColor : user_color(m->user);
    COLORREF body_color = m->is_system ? kSystemColor : kWhiteColor;
    int count = 0;

    if (!m->is_system && m->badge_count > 0) {
        for (int i = 0; i < m->badge_count && count < cap; i++) {
            int before = count;
            count = add_badge_run(r, runs, count, cap, &m->badges[i], font,
                                  has_emote);
            if (count > before && count < cap) {
                count = add_text_run(r, runs, count, cap, " ", 1,
                                     font, body_color);
            }
        }
    }

    char prefix[MAX_USERNAME + 3];
    snprintf(prefix, sizeof(prefix), "%s: ", m->user);
    count = add_text_run(r, runs, count, cap, prefix, (int)strlen(prefix),
                         font, prefix_color);

    if (!m->is_system && m->emote_count > 0) {
        const int text_len = (int)strlen(m->text);
        int cursor = 0;
        bool need_space = false;

        for (int i = 0; i < m->emote_count && count < cap; i++) {
            const chat_emote_t *emote = &m->emotes[i];
            int start = emote->start_byte;
            int end = emote->end_byte;
            if (start < cursor || end <= start || start >= text_len) {
                continue;
            }
            if (end > text_len) end = text_len;

            count = add_text_segment_runs(r, runs, count, cap,
                                          m->text + cursor, start - cursor,
                                          font, body_color, true, has_emote,
                                          &need_space);
            if (need_space && count < cap) {
                count = add_text_run(r, runs, count, cap, " ", 1,
                                     font, body_color);
                need_space = false;
            }
            count = add_explicit_emote_run(r, runs, count, cap, emote,
                                           font, body_color, has_emote);
            cursor = end;
            need_space = false;
        }

        if (cursor < text_len && count < cap) {
            count = add_text_segment_runs(r, runs, count, cap,
                                          m->text + cursor, text_len - cursor,
                                          font, body_color, true, has_emote,
                                          &need_space);
        }
        return count;
    }

    const char *p = m->text;
    bool need_space = false;
    count = add_text_segment_runs(r, runs, count, cap, p, (int)strlen(p),
                                  font, body_color,
                                  !m->is_system, has_emote, &need_space);

    return count;
}

static int layout_line_count(const layout_run_t *runs, int n, int max_width) {
    int lines = 1;
    int x = 0;
    for (int i = 0; i < n; i++) {
        const layout_run_t *run = &runs[i];
        bool is_space = run->type == RUN_TEXT
                     && run->text_len == 1
                     && run->text[0] == L' ';
        if (is_space && x == 0) {
            continue;
        }
        if (x > 0 && x + run->w > max_width) {
            lines++;
            x = 0;
            if (is_space) continue;
        }
        x += run->w;
    }
    return lines;
}

static void draw_text_run_group(renderer_t *r, const layout_run_t *runs,
                                const layout_run_position_t *positions,
                                int first, int last, int clip_right)
{
    if (!r || !runs || !positions || first < 0 || last < first) return;

    wchar_t text[MAX_MSG_TEXT * 2];
    int text_len = 0;
    for (int i = first; i <= last; i++) {
        const layout_run_t *run = &runs[i];
        if (!positions[i].visible || run->type != RUN_TEXT) continue;
        int copy_len = run->text_len;
        if (copy_len > (int)(sizeof(text) / sizeof(text[0])) - text_len) {
            copy_len = (int)(sizeof(text) / sizeof(text[0])) - text_len;
        }
        if (copy_len <= 0) break;
        memcpy(text + text_len, run->text, (size_t)copy_len * sizeof(wchar_t));
        text_len += copy_len;
    }
    if (text_len <= 0) return;

    IDWriteTextLayout *layout = NULL;
    if (r->dwrite_ready) {
        layout = renderer_find_text_layout(
            r, runs[first].font, text, text_len, NULL);
        if (!layout) {
            layout = create_directwrite_layout(
                r, runs[first].font, text, text_len, 32768.0f, 4096.0f);
            if (layout) {
                DWRITE_TEXT_METRICS metrics;
                memset(&metrics, 0, sizeof(metrics));
                int width = 0;
                if (SUCCEEDED(IDWriteTextLayout_GetMetrics(layout, &metrics))) {
                    width = (int)(metrics.widthIncludingTrailingWhitespace
                                  + 0.5f);
                }
                renderer_store_text_layout(r, runs[first].font, text,
                                            text_len, width, layout);
            }
        }
    }

    draw_text_span_shadowed(r, runs[first].font, text, text_len,
                            positions[first].x, positions[first].draw_y,
                            clip_right, runs[first].color, layout);
    if (layout) IDWriteTextLayout_Release(layout);
}

static void draw_layout_runs(renderer_t *r, const layout_run_t *runs, int n,
                             int x0, int y0, int max_width, int line_h,
                             ULONGLONG occurrence_started_ms,
                             layout_draw_mode_t mode)
{
    if (!runs || n <= 0) return;

    layout_run_position_t positions[MAX_LAYOUT_RUNS];
    memset(positions, 0, sizeof(positions));
    int x = x0;
    int y = y0;
    int content_h = line_h - scale_reference_px(LINE_GAP_PX);
    int clip_right = x0 + max_width;

    for (int i = 0; i < n; i++) {
        const layout_run_t *run = &runs[i];
        bool is_space = run->type == RUN_TEXT
                     && run->text_len == 1
                     && run->text[0] == L' ';
        if (is_space && x == x0) {
            continue;
        }
        if (x > x0 && x + run->w > clip_right) {
            y += line_h;
            x = x0;
            if (is_space) continue;
        }

        int draw_y = y + (content_h - run->h) / 2;
        if (draw_y < y) draw_y = y;

        positions[i].x = x;
        positions[i].draw_y = draw_y;
        positions[i].visible = true;
        x += run->w;
    }

    if (mode != LAYOUT_DRAW_ASSETS_ONLY) {
        /* Text drawing is expensive to begin/end on a DCRenderTarget. Draw
         * all text runs in one batch, then composite bitmap/vector runs
         * afterward. Their layout slots do not overlap. */
        renderer_begin_text_batch(r);
        for (int i = 0; i < n; ) {
            const layout_run_t *run = &runs[i];
            if (!positions[i].visible || run->type != RUN_TEXT) {
                i++;
                continue;
            }

            int last = i;
            while (last + 1 < n) {
                const layout_run_t *next = &runs[last + 1];
                if (!positions[last + 1].visible || next->type != RUN_TEXT
                    || next->font != run->font
                    || next->color != run->color
                    || positions[last + 1].draw_y != positions[i].draw_y
                    || positions[last + 1].x != positions[last].x + runs[last].w) {
                    break;
                }
                last++;
            }
            draw_text_run_group(r, runs, positions, i, last, clip_right);
            i = last + 1;
        }
        if (mode == LAYOUT_DRAW_FULL) {
            renderer_end_text_batch(r);
        }
    } else {
        renderer_end_text_batch(r);
    }

    if (mode == LAYOUT_DRAW_TEXT_ONLY) return;

    for (int i = 0; i < n; i++) {
        const layout_run_t *run = &runs[i];
        if (!positions[i].visible) continue;
        if (run->type == RUN_EMOTE) {
            draw_bttv_emote(r, run->emote_index, positions[i].x,
                            positions[i].draw_y, run->w, run->h,
                            occurrence_started_ms);
        } else if (run->type == RUN_BADGE) {
            draw_badge_run(r, run, positions[i].x, positions[i].draw_y,
                           clip_right);
        }
    }
}

static void release_layout_runs(layout_run_t *runs, int n)
{
    if (!runs || n <= 0) return;
    for (int i = 0; i < n; i++) {
        if (runs[i].dwrite_layout) {
            IDWriteTextLayout_Release(runs[i].dwrite_layout);
            runs[i].dwrite_layout = NULL;
        }
    }
}

static int visible_message_limit(renderer_t *r)
{
    const int padding = scale_reference_px(8);
    const int line_gap = scale_reference_px(LINE_GAP_PX);
    const int input_h = scale_reference_px(CHAT_INPUT_H);
    const int input_gap = scale_reference_px(CHAT_INPUT_GAP);
    int line_h = text_height_for_font(r, r->font_msg) + line_gap + scale_reference_px(2);
    if (line_h < 1) line_h = scale_reference_px(g_font_size_px) + line_gap + scale_reference_px(2);

    int available_h = r->height - padding * 2 - input_h - input_gap;
    int limit = available_h > 0 ? (available_h + line_h - 1) / line_h : 1;

    if (limit < g_max_messages) limit = g_max_messages;
    if (limit < 1) limit = 1;
    if (limit > QUEUE_CAP) limit = QUEUE_CAP;
    return limit;
}

/* Renders the current message tail into r's DIB. */
static void render_chat(renderer_t *r) {
    chat_msg_t *snap = r->snapshot;
    if (!snap) return;
    int visible_limit = visible_message_limit(r);
    int requested_offset = (int)InterlockedCompareExchange(&g_scroll_offset, 0, 0);
    int scroll_offset = 0;
    int max_offset = 0;
    int snap_n = queue_snapshot_scrolled(&g_queue, snap, visible_limit,
                                         requested_offset,
                                         &scroll_offset, &max_offset);
    if (scroll_offset != requested_offset) {
        InterlockedExchange(&g_scroll_offset, scroll_offset);
    }
    InterlockedExchange(&g_scroll_max, max_offset);
    InterlockedExchange(&g_scroll_visible, snap_n);
    InterlockedExchange(&g_scroll_total, snap_n + max_offset);

    if (snap_n == 0) {
        bool idle_input = input_is_idle();
        if (r->empty_frame_ready && idle_input) return;
        clear_frame(r);

        /* Tiny "waiting for messages" hint. */
        wchar_t hint[160];
        wchar_t channel_w[96];
        utf8_to_utf16(channel_display_name(), channel_w,
                      (int)(sizeof(channel_w) / sizeof(channel_w[0])));
        if (!channel_w[0]) {
            wcscpy(channel_w, L"chat");
        }
        swprintf(hint, 160, L"waiting for %ls messages...", channel_w);
        draw_text_shadowed(r, r->font_sys, hint,
                           scale_reference_px(8), scale_reference_px(8),
                           r->width - scale_reference_px(16), kSystemColor);
        draw_input_box(r);
        r->empty_frame_ready = idle_input;
        return;
    }

    r->empty_frame_ready = false;
    clear_frame(r);
    const int padding = scale_reference_px(8);
    const int input_h = scale_reference_px(CHAT_INPUT_H);
    const int input_gap = scale_reference_px(CHAT_INPUT_GAP);
    const int emote_render_h = scale_reference_px(EMOTE_RENDER_H);
    const int line_gap = scale_reference_px(LINE_GAP_PX);
    const int max_text_width = r->width - padding * 2;
    int y = r->height - padding - input_h - input_gap;
    message_render_info_t infos[QUEUE_CAP];
    int info_n = 0;

    for (int i = snap_n - 1; i >= 0; i--) {
        chat_msg_t *m = &snap[i];
        layout_run_t runs[MAX_LAYOUT_RUNS];
        bool has_emote = false;
        int run_n = build_message_runs(r, m, runs, MAX_LAYOUT_RUNS, &has_emote);
        if (run_n <= 0) continue;

        int text_h = text_height_for_font(r, m->is_system ? r->font_sys : r->font_msg);
        int content_h = has_emote && text_h < emote_render_h ? emote_render_h : text_h;
        int line_h = content_h + line_gap;
        int lines = layout_line_count(runs, run_n, max_text_width);
        int block_h = lines * line_h;

        y -= block_h;
        if (y < 0) {
            release_layout_runs(runs, run_n);
            break;
        }
        if (info_n < QUEUE_CAP) {
            infos[info_n].snapshot_index = i;
            infos[info_n].y = y;
            infos[info_n].line_h = line_h;
            info_n++;
        }
        release_layout_runs(runs, run_n);
        y -= scale_reference_px(2);
    }

    /* Build the text portion of the entire frame in one Direct2D batch. The
     * second pass composites emotes/badges after that batch is closed, so the
     * cached bitmap path never forces Direct2D to restart for each message. */
    renderer_begin_text_batch(r);
    for (int i = 0; i < info_n; i++) {
        message_render_info_t *info = &infos[i];
        chat_msg_t *m = &snap[info->snapshot_index];
        layout_run_t runs[MAX_LAYOUT_RUNS];
        bool has_emote = false;
        int run_n = build_message_runs(r, m, runs, MAX_LAYOUT_RUNS, &has_emote);
        if (run_n <= 0) continue;

        draw_layout_runs(r, runs, run_n, padding, info->y, max_text_width,
                         info->line_h, m->created_ms,
                         LAYOUT_DRAW_TEXT_ONLY);
        release_layout_runs(runs, run_n);
    }
    renderer_end_text_batch(r);

    for (int i = 0; i < info_n; i++) {
        message_render_info_t *info = &infos[i];
        chat_msg_t *m = &snap[info->snapshot_index];
        layout_run_t runs[MAX_LAYOUT_RUNS];
        bool has_emote = false;
        int run_n = build_message_runs(r, m, runs, MAX_LAYOUT_RUNS, &has_emote);
        if (run_n <= 0) continue;

        draw_layout_runs(r, runs, run_n, padding, info->y, max_text_width,
                         info->line_h, m->created_ms,
                         LAYOUT_DRAW_ASSETS_ONLY);
        release_layout_runs(runs, run_n);
    }

    draw_input_box(r);
}

static uint8_t straight_alpha_channel(uint8_t value, uint8_t alpha) {
    unsigned straight = ((unsigned)value * 255u + alpha / 2u) / alpha;
    return (uint8_t)(straight > 255u ? 255u : straight);
}

/* Convert premultiplied BGRA to the protocol's straight RGBA. VLC applies
 * coverage while blending: sending premultiplied colors applies alpha twice,
 * darkening thin strokes, antialiased edges and translucent emotes. */
static void dib_to_rgba(renderer_t *r) {
    GdiFlush(); /* Finish any fallback GDI drawing before reading DIB memory. */
    const uint8_t *src = (const uint8_t *)r->pixels;
    uint8_t *dst = r->rgba_out;
    const size_t src_pitch = (size_t)r->surface_width * 4u;
    for (int py = 0; py < r->height; py++) {
        const uint8_t *src_row = src + (size_t)py * src_pitch;
        for (int px = 0; px < r->width; px++) {
            const uint8_t b = src_row[0];
            const uint8_t g = src_row[1];
            const uint8_t red = src_row[2];
            uint8_t alpha = src_row[3];

            /* GDI text drawn into a BI_RGB 32-bpp DIB updates color channels
             * but commonly leaves the high byte at 0. VLC treats that byte as
             * alpha, so synthesize coverage from visible RGB pixels. */
            if (alpha == 0 && (red != 0 || g != 0 || b != 0)) {
                alpha = red;
                if (g > alpha) alpha = g;
                if (b > alpha) alpha = b;
            }

            const bool partial = alpha > 0 && alpha < 255;
            dst[0] = partial ? straight_alpha_channel(red, alpha) : red;
            dst[1] = partial ? straight_alpha_channel(g, alpha) : g;
            dst[2] = partial ? straight_alpha_channel(b, alpha) : b;
            dst[3] = alpha;
            src_row += 4;
            dst += 4;
        }
    }
}

/* ===== Pipe writer ====================================================== */

typedef struct {
    HANDLE pipe;
    char   path[160];
    bool   positioned;
} pipe_writer_t;

static bool position_state_file_exists(const char *path) {
    if (!path || !path[0]) return false;
    DWORD attrs = GetFileAttributesA(path);
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

static bool plugin_position_state_exists(void) {
    if (position_state_file_exists(g_position_state_path)) return true;

    const char *appdata = getenv("APPDATA");
    if (!appdata || !appdata[0]) return false;

    char path[MAX_PATH];
    int n = snprintf(path, sizeof(path), "%s%s", appdata, PLUGIN_POSITION_FILE_REL);
    if (n <= 0 || n >= (int)sizeof(path)) return false;

    return position_state_file_exists(path);
}

static bool pipe_writer_open(pipe_writer_t *p) {
    if (p->pipe && p->pipe != INVALID_HANDLE_VALUE) return true;
    /* Block briefly for a server; if not present we just return false and
     * try again on the next render tick. */
    if (!WaitNamedPipeA(p->path, 200)) {
        return false;
    }
    HANDLE h = CreateFileA(p->path, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    p->pipe = h;
    log_msg("pipe connected: %s", p->path);
    return true;
}

static void pipe_writer_close(pipe_writer_t *p) {
    if (p->pipe && p->pipe != INVALID_HANDLE_VALUE) {
        CloseHandle(p->pipe);
        p->pipe = INVALID_HANDLE_VALUE;
    }
    p->positioned = false;
}

static bool pipe_write_all(HANDLE pipe, const void *buf, DWORD bytes) {
    const uint8_t *cursor = (const uint8_t *)buf;
    while (bytes > 0) {
        DWORD written = 0;
        if (!WriteFile(pipe, cursor, bytes, &written, NULL) || written == 0) {
            return false;
        }
        cursor += written;
        bytes -= written;
    }
    return true;
}

/* MYO_TYPE_POSITION carries no payload. Used only when the plugin has no saved
 * position yet. The plugin ignores x/y on MYO_TYPE_FRAME, so this seeds the
 * first-run placement without fighting later drag persistence. */
static bool pipe_send_position(pipe_writer_t *p, int x, int y, uint8_t alpha) {
    if (!pipe_writer_open(p)) return false;
    overlay_msg_v1 hdr = {0};
    hdr.magic        = MYO_MAGIC;
    hdr.version      = MYO_VERSION;
    hdr.payload_size = 0;
    hdr.type         = MYO_TYPE_POSITION;
    hdr.x = x; hdr.y = y;
    hdr.alpha = alpha;
    if (!pipe_write_all(p->pipe, &hdr, (DWORD)sizeof(hdr))) {
        pipe_writer_close(p);
        return false;
    }
    p->positioned = true;
    return true;
}

static bool pipe_send_frame(pipe_writer_t *p, int x, int y, int w, int h,
                              uint8_t alpha, const uint8_t *rgba)
{
    if (!pipe_writer_open(p)) return false;
    if (w <= 0 || h <= 0 || !rgba) return false;

    uint64_t payload_size = (uint64_t)w * (uint64_t)h * 4u;
    if (payload_size == 0 || payload_size > MYO_MAX_PAYLOAD) return false;

    overlay_msg_v1 hdr = {0};
    hdr.magic        = MYO_MAGIC;
    hdr.version      = MYO_VERSION;
    hdr.payload_size = (uint32_t)payload_size;
    hdr.type         = MYO_TYPE_FRAME;
    hdr.x = x; hdr.y = y;
    hdr.w = (uint32_t)w; hdr.h = (uint32_t)h;
    hdr.alpha = alpha;

    if (!pipe_write_all(p->pipe, &hdr, (DWORD)sizeof(hdr))) {
        pipe_writer_close(p);
        return false;
    }
    if (!pipe_write_all(p->pipe, rgba, hdr.payload_size)) {
        pipe_writer_close(p);
        return false;
    }
    return true;
}

static bool pipe_send_clear(pipe_writer_t *p)
{
    if (!pipe_writer_open(p)) return false;

    overlay_msg_v1 hdr = {0};
    hdr.magic        = MYO_MAGIC;
    hdr.version      = MYO_VERSION;
    hdr.payload_size = 0;
    hdr.type         = MYO_TYPE_CLEAR;

    if (!pipe_write_all(p->pipe, &hdr, (DWORD)sizeof(hdr))) {
        pipe_writer_close(p);
        return false;
    }
    return true;
}

static bool pipe_send_scroll_state(pipe_writer_t *p, int offset, int max_offset,
                                   int visible, int total)
{
    if (!pipe_writer_open(p)) return false;

    overlay_msg_v1 hdr = {0};
    hdr.magic        = MYO_MAGIC;
    hdr.version      = MYO_VERSION;
    hdr.payload_size = 0;
    hdr.type         = MYO_TYPE_SCROLL_STATE;
    hdr.x = offset;
    hdr.y = max_offset;
    hdr.w = visible < 0 ? 0u : (uint32_t)visible;
    hdr.h = total < 0 ? 0u : (uint32_t)total;
    hdr.alpha = 255;

    if (!pipe_write_all(p->pipe, &hdr, (DWORD)sizeof(hdr))) {
        pipe_writer_close(p);
        return false;
    }
    return true;
}

/* ===== Render thread ==================================================== */

/* Font metrics and bitmap dimensions must come from one scale snapshot. */
static bool renderer_update_scale_and_size(renderer_t *r, int width, int height)
{
    const int old_message_px = (int)(((int64_t)g_font_size_px * r->scale_height + REFERENCE_VIDEO_H / 2) / REFERENCE_VIDEO_H);
    const int old_system_px = (int)(((int64_t)g_system_font_size_px * r->scale_height + REFERENCE_VIDEO_H / 2) / REFERENCE_VIDEO_H);
    if (old_message_px == scale_reference_px(g_font_size_px) &&
        old_system_px == scale_reference_px(g_system_font_size_px)) {
        r->scale_height = current_video_height();
        return renderer_resize(r, width, height);
    }
    renderer_t next;
    if (!renderer_init(&next, width, height)) return false;
    renderer_destroy(r);
    *r = next;
    return true;
}

static DWORD WINAPI render_thread(LPVOID arg) {
    InterlockedExchange(&render_thread_id, (LONG)GetCurrentThreadId());
    (void)arg;

    renderer_t rend;
    LONG packed_size = InterlockedCompareExchange(&g_target_size, 0, 0);
    int render_width = MYO_UNPACK_SIZE_W(packed_size);
    int render_height = MYO_UNPACK_SIZE_H(packed_size);
    if (render_width <= 0 || render_height <= 0) {
        source_chat_size_from_reference(g_width, g_height,
                                        &render_width, &render_height);
        set_target_chat_size(g_width, g_height);
    }
    if (!renderer_init(&rend, render_width, render_height)) {
        log_msg("renderer_init failed");
        InterlockedExchange(&g_stop, 1);
        return 1;
    }

    pipe_writer_t pipe = {0};
    snprintf(pipe.path, sizeof(pipe.path), "\\\\.\\pipe\\%s", g_pipe_name);

    LONG rendered_generation = 0;
    ULONGLONG last_render_ms = 0;
    bool have_rendered = false;

    while (!g_stop) {
        LONG current_generation =
            InterlockedCompareExchange(&g_render_generation, 0, 0);
        DWORD wait_ms = 0;
        if (have_rendered) {
            if (current_generation == rendered_generation) {
                wait_ms = HEARTBEAT_MS;
            } else {
                ULONGLONG elapsed = GetTickCount64() - last_render_ms;
                wait_ms = elapsed >= RENDER_MIN_INTERVAL_MS
                    ? 0
                    : (DWORD)(RENDER_MIN_INTERVAL_MS - elapsed);
            }
        }

        if (wait_ms > 0) {
            if (WaitForSingleObject(g_render_signal, wait_ms) == WAIT_OBJECT_0) {
                /* Consume the wake and render the latest state below. */
                continue;
            }
        } else {
            /* Do not leave an already-consumed generation wake behind. */
            (void)WaitForSingleObject(g_render_signal, 0);
        }
        if (g_stop) break;

        LONG render_generation =
            InterlockedCompareExchange(&g_render_generation, 0, 0);
        render_scale_height = 0;
        render_scale_height = current_video_height();
        packed_size = InterlockedCompareExchange(&g_target_size, 0, 0);
        int target_width = MYO_UNPACK_SIZE_W(packed_size);
        int target_height = MYO_UNPACK_SIZE_H(packed_size);
        clamp_source_chat_size(&target_width, &target_height);
        if (!renderer_update_scale_and_size(&rend, target_width, target_height)) {
            log_msg("renderer update failed for %dx%d", target_width, target_height);
            break;
        }

        render_chat(&rend);
        dib_to_rgba(&rend);

        /* Seed the plugin's initial position only while no persisted plugin
         * position exists. That gives first-run launches the orchestrator's
         * default placement without clobbering a user's saved drag position
         * on later launches or VLC input rebuilds. */
        if (!pipe.positioned) {
            if (plugin_position_state_exists()) {
                pipe.positioned = true;
            } else {
                (void)pipe_send_position(&pipe, g_init_x, g_init_y, 255);
            }
        }

        bool frame_sent = pipe_send_frame(&pipe, g_init_x, g_init_y,
                                          rend.width, rend.height, 255,
                                          rend.rgba_out);
        if (!frame_sent) {
            /* If the plugin is not up yet, slow the retry cadence a bit. */
            Sleep(250);
        } else {
            int offset = (int)InterlockedCompareExchange(&g_scroll_offset, 0, 0);
            int max_offset = (int)InterlockedCompareExchange(&g_scroll_max, 0, 0);
            int visible = (int)InterlockedCompareExchange(&g_scroll_visible, 0, 0);
            int total = (int)InterlockedCompareExchange(&g_scroll_total, 0, 0);
            (void)pipe_send_scroll_state(&pipe, offset, max_offset, visible, total);
        }

        rendered_generation = render_generation;
        last_render_ms = GetTickCount64();
        have_rendered = true;
    }

    (void)pipe_send_clear(&pipe);
    pipe_writer_close(&pipe);
    renderer_destroy(&rend);
    return 0;
}

/* ===== Parent-watch thread ============================================== */

static DWORD WINAPI watch_thread(LPVOID arg) {
    (void)arg;
    if (g_owner_pid == 0) return 0;
    HANDLE h = OpenProcess(SYNCHRONIZE, FALSE, g_owner_pid);
    if (!h) {
        /* Can't open the parent — fall back to a polling sentinel. */
        while (!g_stop) {
            HANDLE p = OpenProcess(SYNCHRONIZE, FALSE, g_owner_pid);
            if (!p) {
                log_msg("parent process %u gone (open failed); exiting",
                        (unsigned)g_owner_pid);
                InterlockedExchange(&g_stop, 1);
                signal_render();
                return 0;
            }
            CloseHandle(p);
            Sleep(WATCH_POLL_MS);
        }
        return 0;
    }
    /* Block until the parent exits. */
    DWORD wait = WaitForSingleObject(h, INFINITE);
    (void)wait;
    log_msg("parent process %u exited; shutting down", (unsigned)g_owner_pid);
    InterlockedExchange(&g_stop, 1);
    signal_render();
    CloseHandle(h);
    return 0;
}

static bool event_read_all(HANDLE pipe, void *buf, DWORD bytes) {
    uint8_t *cursor = (uint8_t *)buf;
    while (bytes > 0) {
        DWORD got = 0;
        if (!ReadFile(pipe, cursor, bytes, &got, NULL) || got == 0) {
            return false;
        }
        cursor += got;
        bytes -= got;
    }
    return true;
}

static bool overlay_event_is_valid(const overlay_event_v1 *event) {
    return event->magic == MYO_MAGIC
        && event->version == MYO_VERSION
        && (event->type == MYO_EVENT_SCROLL
            || event->type == MYO_EVENT_SCROLL_TO
            || event->type == MYO_EVENT_RESIZE
            || event->type == MYO_EVENT_CHAT_INPUT_FOCUS
            || event->type == MYO_EVENT_CHAT_INPUT_HOVER
            || event->type == MYO_EVENT_SHUTDOWN
            || event->type == MYO_EVENT_UI_SCALE
            || event->type == MYO_EVENT_VIDEO_SIZE);
}

static void handle_overlay_event(const overlay_event_v1 *event) {
    if (event->type == MYO_EVENT_SCROLL) {
        adjust_scroll_offset(event->value);
    } else if (event->type == MYO_EVENT_SCROLL_TO) {
        set_scroll_offset(event->value);
    } else if (event->type == MYO_EVENT_RESIZE) {
        int width = unscale_source_px(MYO_UNPACK_SIZE_W(event->value));
        int height = unscale_source_px(MYO_UNPACK_SIZE_H(event->value));
        clamp_chat_size(&width, &height);
        g_width = width;
        g_height = height;
        set_target_chat_size(width, height);
        save_chat_size(width, height);
        signal_render();
    } else if (event->type == MYO_EVENT_UI_SCALE) {
        if (event->value > 0 && event->value <= 65535 &&
            InterlockedExchange(&g_ui_scale_height, event->value) != event->value) {
            set_target_chat_size(g_width, g_height);
            signal_render();
        }
    } else if (event->type == MYO_EVENT_VIDEO_SIZE) {
        int video_width = MYO_UNPACK_SIZE_W(event->value);
        int video_height = MYO_UNPACK_SIZE_H(event->value);
        if (video_width > 0 && video_height > 0) {
            LONG previous_width = InterlockedCompareExchange(&g_video_width, 0, 0);
            LONG previous_height = InterlockedCompareExchange(&g_video_height, 0, 0);
            bool needs_size_migration = g_size_state_loaded && !g_size_state_reference;
            if (previous_width == video_width &&
                previous_height == video_height &&
                !needs_size_migration) {
                return;
            }
            InterlockedExchange(&g_video_width, video_width);
            InterlockedExchange(&g_video_height, video_height);
            if (g_size_state_loaded && !g_size_state_reference) {
                g_width = unscale_source_px(g_width);
                g_height = unscale_source_px(g_height);
                clamp_chat_size(&g_width, &g_height);
                save_chat_size(g_width, g_height);
            }
            set_target_chat_size(g_width, g_height);
            signal_render();
        }
    } else if (event->type == MYO_EVENT_CHAT_INPUT_FOCUS) {
        input_set_focused(event->value != 0);
        signal_render();
    } else if (event->type == MYO_EVENT_CHAT_INPUT_HOVER) {
        InterlockedExchange(&g_input_hovered, event->value ? 1 : 0);
        signal_render();
    } else if (event->type == MYO_EVENT_SHUTDOWN) {
        log_msg("shutdown requested; clearing overlay");
        InterlockedExchange(&g_stop, 1);
        signal_render();
    }
}

static void wake_event_thread(void) {
    char path[160];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\%s_events", g_pipe_name);

    HANDLE pipe = CreateFileA(path, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (pipe != INVALID_HANDLE_VALUE) {
        CloseHandle(pipe);
    }
}

static DWORD WINAPI event_thread(LPVOID arg) {
    (void)arg;

    char path[160];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\%s_events", g_pipe_name);

    while (!g_stop) {
        HANDLE pipe = CreateNamedPipeA(
            path,
            PIPE_ACCESS_INBOUND,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 0, 4096, 0, NULL);
        if (pipe == INVALID_HANDLE_VALUE) {
            Sleep(250);
            continue;
        }

        BOOL connected = ConnectNamedPipe(pipe, NULL)
                      || GetLastError() == ERROR_PIPE_CONNECTED;
        if (connected) {
            while (!g_stop) {
                overlay_event_v1 event;
                if (!event_read_all(pipe, &event, (DWORD)sizeof(event))) {
                    break;
                }
                if (!overlay_event_is_valid(&event)) {
                    break;
                }
                handle_overlay_event(&event);
            }
        }

        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }

    return 0;
}

/* ===== CLI parsing ====================================================== */

static bool arg_matches(const char *got, const char *long_name, const char *short_alias) {
    if (long_name && _stricmp(got, long_name) == 0) return true;
    if (short_alias && _stricmp(got, short_alias) == 0) return true;
    return false;
}

static bool pipe_name_char_is_safe(char c) {
    return (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c == '_' || c == '-' || c == '.';
}

static void sanitize_pipe_name(char *name, size_t name_cap) {
    if (!name || name_cap == 0) return;
    size_t out = 0;
    for (size_t i = 0; name[i] && out + 1 < name_cap; i++) {
        name[out++] = pipe_name_char_is_safe(name[i]) ? name[i] : '_';
    }
    name[out] = '\0';
    if (!name[0]) {
        strncpy(name, "vlc_overlay", name_cap - 1);
        name[name_cap - 1] = '\0';
    }
}

static void usage(void) {
    fprintf(stderr,
        "vlc_chat_overlay --channel <name> [options]\n"
        "  --channel <name>            channel (required)\n"
        "  --channel-display-name NAME display name for chat placeholder\n"
        "  --provider <name>           twitch or kick (default twitch)\n"
        "  --kick-chatroom-id N        Kick chatroom id when provider is kick\n"
        "  --kick-broadcaster-user-id N Kick broadcaster user id for user-mode sends\n"
        "  --kick-send-as-bot          send Kick chat as the token's bot account\n"
        "  --pipe-name <name>          VLC overlay pipe name (default vlc_overlay)\n"
        "  --width N (default 420, normalized to a 1080p source)\n"
        "  --height N (default 292, normalized to a 1080p source)\n"
        "                              Drag the VLC overlay corner to resize; saved per state path\n"
        "  --x N --y N\n"
        "  --max-messages N (default 18)\n"
        "  --font-size N (default 15, scales text/badges, clamped 8-36, normalized to a 1080p source)\n"
        "  --owner-process-id PID      exit when this process exits\n"
        "  --vlc-process-id PID        (currently ignored)\n"
        "  --position-state-path PATH  plugin/controller state path\n"
        "  --chat-token-file PATH      Twitch OAuth token file for sending chat\n"
        "  --chat-username NAME        Twitch username for the token (optional)\n"
        "  --twitch-client-id ID       Twitch Client ID for Helix badge fallback\n"
        "  --twitch-room-id N          Twitch room/user id for early channel badge preload\n"
        "  --twitch-badge-manifest PATH bundled Twitch badge manifest\n"
        "  --kick-badge-manifest PATH bundled Kick badge manifest\n"
        "  --kick-chat-token-file PATH Kick chat:write bearer token file\n");
}

static bool parse_args(int argc, char **argv) {
    /* Accept both Unix-style (--name) and PowerShell-style (-Name) so the
     * orchestrator can pass the same arg list it sends to the PS overlay. */
    for (int i = 1; i < argc; i++) {
        const char *a = argv[i];
        const char *next = (i + 1 < argc) ? argv[i + 1] : NULL;
        if (arg_matches(a, "--channel", "-Channel")) {
            if (!next) return false;
            strncpy(g_channel, next, sizeof(g_channel) - 1);
            i++;
        } else if (arg_matches(a, "--channel-display-name", "-ChannelDisplayName")) {
            if (!next) return false;
            set_channel_display_name(next);
            g_channel_display_name_explicit = true;
            i++;
        } else if (arg_matches(a, "--provider", "-Provider")) {
            if (!next) return false;
            if (_stricmp(next, "twitch") == 0) {
                g_chat_provider = CHAT_PROVIDER_TWITCH;
            } else if (_stricmp(next, "kick") == 0) {
                g_chat_provider = CHAT_PROVIDER_KICK;
            } else {
                fprintf(stderr, "unknown provider: %s\n", next);
                return false;
            }
            i++;
        } else if (arg_matches(a, "--kick-chatroom-id", "-KickChatroomId")) {
            if (!next) return false;
            strncpy(g_kick_chatroom_id, next, sizeof(g_kick_chatroom_id) - 1);
            i++;
        } else if (arg_matches(a, "--kick-broadcaster-user-id", "-KickBroadcasterUserId")) {
            if (!next) return false;
            strncpy(g_kick_broadcaster_user_id, next,
                    sizeof(g_kick_broadcaster_user_id) - 1);
            i++;
        } else if (arg_matches(a, "--kick-send-as-bot", "-KickSendAsBot")) {
            g_kick_send_as_bot = true;
        } else if (arg_matches(a, "--pipe-name", "-PipeName")) {
            if (!next) return false;
            strncpy(g_pipe_name, next, sizeof(g_pipe_name) - 1);
            i++;
        } else if (arg_matches(a, "--width", "-Width")) {
            if (!next) return false;
            g_width = atoi(next); i++;
        } else if (arg_matches(a, "--height", "-Height")) {
            if (!next) return false;
            g_height = atoi(next); i++;
        } else if (arg_matches(a, "--x", "-X")) {
            if (!next) return false;
            g_init_x = atoi(next); i++;
        } else if (arg_matches(a, "--y", "-Y")) {
            if (!next) return false;
            g_init_y = atoi(next); i++;
        } else if (arg_matches(a, "--max-messages", "-MaxMessages")) {
            if (!next) return false;
            g_max_messages = atoi(next); i++;
        } else if (arg_matches(a, "--font-size", "-FontSize")) {
            if (!next) return false;
            g_font_size_px = atoi(next); i++;
        } else if (arg_matches(a, "--owner-process-id", "-OwnerProcessId")) {
            if (!next) return false;
            g_owner_pid = (DWORD)strtoul(next, NULL, 10); i++;
        } else if (arg_matches(a, "--vlc-process-id", "-VlcProcessId")) {
            if (!next) return false;
            g_vlc_pid = (DWORD)strtoul(next, NULL, 10);
            i++;
        } else if (arg_matches(a, "--position-state-path", "-PositionStatePath")) {
            if (!next) return false;
            strncpy(g_position_state_path, next, sizeof(g_position_state_path) - 1);
            i++;
        } else if (arg_matches(a, "--chat-token-file", "-ChatTokenFile")) {
            if (!next) return false;
            strncpy(g_twitch_chat_token_file, next,
                    sizeof(g_twitch_chat_token_file) - 1);
            i++;
        } else if (arg_matches(a, "--chat-username", "-ChatUsername")) {
            if (!next) return false;
            strncpy(g_twitch_chat_login, next, sizeof(g_twitch_chat_login) - 1);
            sanitize_twitch_login(g_twitch_chat_login);
            i++;
        } else if (arg_matches(a, "--twitch-client-id", "-TwitchClientId")) {
            if (!next) return false;
            strncpy(g_twitch_client_id, next, sizeof(g_twitch_client_id) - 1);
            trim_ascii_in_place(g_twitch_client_id);
            i++;
        } else if (arg_matches(a, "--twitch-room-id", "-TwitchRoomId")) {
            if (!next) return false;
            strncpy(g_twitch_room_id, next, sizeof(g_twitch_room_id) - 1);
            trim_ascii_in_place(g_twitch_room_id);
            i++;
        } else if (arg_matches(a, "--twitch-badge-manifest", "-TwitchBadgeManifest")) {
            if (!next) return false;
            strncpy(g_twitch_badge_manifest_path, next,
                    sizeof(g_twitch_badge_manifest_path) - 1);
            i++;
        } else if (arg_matches(a, "--kick-badge-manifest", "-KickBadgeManifest")) {
            if (!next) return false;
            strncpy(g_kick_badge_manifest_path, next,
                    sizeof(g_kick_badge_manifest_path) - 1);
            i++;
        } else if (arg_matches(a, "--kick-chat-token-file", "-KickChatTokenFile")) {
            if (!next) return false;
            strncpy(g_kick_chat_token_file, next,
                    sizeof(g_kick_chat_token_file) - 1);
            i++;
        } else if (arg_matches(a, "--help", "-h")) {
            usage();
            return false;
        } else {
            fprintf(stderr, "unknown arg: %s\n", a);
            return false;
        }
    }
    if (!g_channel[0]) {
        fprintf(stderr, "missing --channel\n");
        return false;
    }
    /* Sanitize. */
    for (char *p = g_channel; *p; p++) {
        if ((*p >= 'A' && *p <= 'Z')) *p = (char)(*p - 'A' + 'a');
        else if ((*p >= 'a' && *p <= 'z') || (*p >= '0' && *p <= '9') || *p == '_' || *p == '-') {
            /* keep */
        } else {
            *p = '_';
        }
    }
    if (!g_channel_display_name[0]) {
        set_channel_display_name(g_channel);
    }
    if (g_chat_provider == CHAT_PROVIDER_KICK) {
        for (char *p = g_kick_chatroom_id; *p; p++) {
            if (!isdigit((unsigned char)*p)) {
                fprintf(stderr, "invalid --kick-chatroom-id: %s\n", g_kick_chatroom_id);
                return false;
            }
        }
        if (!g_kick_chatroom_id[0]) {
            fprintf(stderr, "missing --kick-chatroom-id for Kick provider\n");
            return false;
        }
        for (char *p = g_kick_broadcaster_user_id; *p; p++) {
            if (!isdigit((unsigned char)*p)) {
                fprintf(stderr, "invalid --kick-broadcaster-user-id: %s\n",
                        g_kick_broadcaster_user_id);
                return false;
            }
        }
    } else {
        for (char *p = g_twitch_room_id; *p; p++) {
            if (!isdigit((unsigned char)*p)) {
                fprintf(stderr, "invalid --twitch-room-id: %s\n",
                        g_twitch_room_id);
                return false;
            }
        }
    }
    clamp_chat_size(&g_width, &g_height);
    if (g_max_messages < 1) g_max_messages = 1;
    if (g_max_messages > QUEUE_CAP) g_max_messages = QUEUE_CAP;
    set_font_size(g_font_size_px);
    sanitize_pipe_name(g_pipe_name, sizeof(g_pipe_name));
    init_size_state_path();
    load_saved_chat_size();
    set_target_chat_size(g_width, g_height);
    return true;
}

/* ===== Main ============================================================ */

/* Join the workers before freeing any shared renderer/chat state. A blocked
 * network worker must never be killed in isolation: it may own a heap or
 * catalog lock that cleanup needs. This standalone controller is exiting, so
 * let Windows reclaim the whole process if cooperative shutdown times out. */
static void finish_workers(const HANDLE *workers, DWORD count, DWORD timeout) {
    HANDLE active[MAXIMUM_WAIT_OBJECTS];
    DWORD active_count = 0;
    for (DWORD i = 0; i < count; i++) {
        if (workers[i]) active[active_count++] = workers[i];
    }
    if (active_count > 0 &&
        WaitForMultipleObjects(active_count, active, TRUE, timeout) != WAIT_OBJECT_0) {
        TerminateProcess(GetCurrentProcess(), 0);
        /* Do not touch shared state even if process termination fails. */
        ExitProcess(1);
    }
    for (DWORD i = 0; i < active_count; i++) CloseHandle(active[i]);
}

int main(int argc, char **argv) {
    for (int i = 1; i < argc; i++) {
        if (arg_matches(argv[i], "--help", "-h")) {
            usage();
            return 0;
        }
    }

    if (!parse_args(argc, argv)) {
        usage();
        return 2;
    }
    {
        const char *debug = getenv("VLC_CHAT_OVERLAY_DEBUG_EMOTES");
        g_debug_emotes = debug && debug[0] && strcmp(debug, "0") != 0;
    }

    /* Hide our console window on the off chance one shows up. We're launched
     * with -WindowStyle Hidden by the orchestrator's New-ProcessStartInfo,
     * but belt-and-suspenders. */
    HWND console = GetConsoleWindow();
    if (console) ShowWindow(console, SW_HIDE);

    if (!tls_global_init()) {
        log_msg("tls_global_init failed: %s", tls_last_error());
        return 3;
    }
    InitializeCriticalSection(&g_input_cs);
    InitializeCriticalSection(&g_irc_send_cs);
    InitializeCriticalSection(&g_sent_echo_cs);

    HRESULT co_hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    g_com_ready = SUCCEEDED(co_hr);

    GdiplusStartupInput gdip = {0};
    gdip.GdiplusVersion = 1;
    if (GdiplusStartup(&g_gdiplus_token, &gdip, NULL) == 0) {
        g_gdiplus_ready = true;
    } else {
        log_msg("GDI+ startup failed; BTTV emotes will render as text");
    }

    queue_init(&g_queue);
    if (!image_load_queue_init(&g_image_load_queue)) {
        log_msg("CreateEvent failed for image loader");
        DeleteCriticalSection(&g_queue.cs);
        DeleteCriticalSection(&g_sent_echo_cs);
        DeleteCriticalSection(&g_irc_send_cs);
        DeleteCriticalSection(&g_input_cs);
        if (g_gdiplus_ready) GdiplusShutdown(g_gdiplus_token);
        if (g_com_ready) CoUninitialize();
        tls_global_cleanup();
        return 4;
    }
    g_render_signal = CreateEventA(NULL, FALSE, FALSE, NULL);
    if (!g_render_signal) {
        log_msg("CreateEvent failed");
        image_load_queue_destroy(&g_image_load_queue);
        DeleteCriticalSection(&g_queue.cs);
        DeleteCriticalSection(&g_sent_echo_cs);
        DeleteCriticalSection(&g_irc_send_cs);
        DeleteCriticalSection(&g_input_cs);
        if (g_gdiplus_ready) GdiplusShutdown(g_gdiplus_token);
        if (g_com_ready) CoUninitialize();
        tls_global_cleanup();
        return 4;
    }
    bttv_catalog_init();
    if (g_chat_provider == CHAT_PROVIDER_KICK) {
        if (kick_badges_load_bundled_manifest() <= 0) {
            kick_badges_load_builtin();
        }
    } else {
        twitch_badges_load_bundled_manifest();
    }
    load_twitch_chat_auth();
    resolve_twitch_channel_display_name();
    load_kick_chat_auth();

    HANDLE t_img = CreateThread(NULL, 0, image_loader_thread, NULL, 0, NULL);
    HANDLE t_evt = CreateThread(NULL, 0, event_thread, NULL, 0, NULL);
    HANDLE t_key = CreateThread(NULL, 0, keyboard_thread, NULL, 0, NULL);
    HANDLE t_ren = CreateThread(NULL, 0, render_thread, NULL, 0, NULL);
    HANDLE t_twitch_assets = NULL;
    if (g_chat_provider == CHAT_PROVIDER_TWITCH) {
        t_twitch_assets = CreateThread(NULL, 0, twitch_asset_thread, NULL, 0, NULL);
        if (t_twitch_assets) {
            InterlockedExchange(&g_twitch_assets_async, 1);
        }
    }
    HANDLE t_chat = CreateThread(NULL, 0,
                                 g_chat_provider == CHAT_PROVIDER_KICK
                                     ? kick_thread
                                     : irc_thread,
                                 NULL, 0, NULL);
    HANDLE t_wch = (g_owner_pid != 0)
                   ? CreateThread(NULL, 0, watch_thread, NULL, 0, NULL)
                   : NULL;

    if (!t_img) {
        log_msg("CreateThread failed for image loader; emotes will render as text");
    }
    if (!t_evt) {
        log_msg("CreateThread failed for scroll events; overlay scrollbar disabled");
    }
    if (!t_key) {
        log_msg("CreateThread failed for keyboard input");
    }
    if (g_chat_provider == CHAT_PROVIDER_TWITCH && !t_twitch_assets) {
        log_msg("CreateThread failed for Twitch assets; emotes and badges will load on first chat line");
    }
    if (!t_chat || !t_ren) {
        log_msg("CreateThread failed");
        InterlockedExchange(&g_stop, 1);
    }

    /* Block until render returns (render is the one that watches g_stop and
     * always observes the heartbeat). */
    if (t_ren) WaitForSingleObject(t_ren, INFINITE);

    InterlockedExchange(&g_stop, 1);
    SetEvent(g_image_load_queue.signal);
    wake_event_thread();
    if (g_keyboard_thread_id) {
        PostThreadMessageA(g_keyboard_thread_id, WM_QUIT, 0, 0);
    }
    const HANDLE workers[] = {t_img, t_evt, t_key, t_chat, t_twitch_assets, t_wch, t_ren};
    finish_workers(workers, (DWORD)(sizeof(workers) / sizeof(workers[0])), 12000);

    bttv_catalog_destroy();
    image_load_queue_destroy(&g_image_load_queue);
    DeleteCriticalSection(&g_queue.cs);
    DeleteCriticalSection(&g_sent_echo_cs);
    DeleteCriticalSection(&g_irc_send_cs);
    DeleteCriticalSection(&g_input_cs);
    CloseHandle(g_render_signal);
    if (g_gdiplus_ready) {
        GdiplusShutdown(g_gdiplus_token);
    }
    if (g_com_ready) {
        CoUninitialize();
    }
    tls_global_cleanup();
    return 0;
}
