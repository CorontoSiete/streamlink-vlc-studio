/*
 * myoverlay.c -- VLC 3.0.x sub source module
 *
 * IPC-driven draggable overlay. A background worker thread hosts a named
 * pipe; an external controller pushes RGBA frames; per-video-frame the
 * Filter() callback turns the current state into a subpicture composited
 * by VLC's video output.
 *
 *   pipe  : \\.\pipe\vlc_overlay  (single instance, byte-stream)
 *   wire  : see protocol.h
 *
 * The overlay is left-click-and-drag repositionable. Drag-released
 * positions are persisted to MYOVERLAY_POSITION_STATE_PATH when set, or to
 * a per-pipe file under %APPDATA%\StreamlinkVlcStudio\vlc-overlays
 * otherwise, so they survive clip loops and VLC restarts.
 */

#ifndef MODULE_STRING
#define MODULE_STRING "myoverlay"
#endif

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdarg.h>

/* compat shims — VLC's headers expect these from autoconf-generated config.h */
struct pollfd;
int poll( struct pollfd *fds, unsigned nfds, int timeout );
#define N_(s)           (s)
#define gettext_noop(s) (s)

#include <vlc_common.h>
#include <vlc_plugin.h>
#include <vlc_filter.h>
#include <vlc_subpicture.h>
#include <vlc_mouse.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>

#include "protocol.h"

/*****************************************************************************
 * Tunables
 *****************************************************************************/
#define PLACEHOLDER_W       240u
#define PLACEHOLDER_H       80u
#define DEFAULT_X           32
#define DEFAULT_Y           32

#define HIDE_BUTTON_W       58u
#define HIDE_BUTTON_H       22u
#define SHOW_BUTTON_W       96u
#define SHOW_BUTTON_H       28u
#define BUTTON_MARGIN       6
#define BUTTON_MOVE_LIMIT   4
#define SCROLLBAR_W         10u
#define SCROLLBAR_MIN_H     26u
#define SCROLLBAR_MARGIN    6
#define RESIZE_HANDLE_SIZE  18u
#define RESIZE_MIN_W        220u
#define RESIZE_MIN_H        120u
#define RESIZE_MAX_W        1920u
#define RESIZE_MAX_H        1080u
#define CHAT_INPUT_MARGIN   8
#define CHAT_INPUT_H        30u
#define UI_REFERENCE_VIDEO_H 1080u

#define POSITION_FILE_REL   "\\vlc-overlay\\position.txt"
#define POSITION_DIR_REL    "\\StreamlinkVlcStudio\\vlc-overlays\\"
#define PIPE_NAME_ENV       "MYOVERLAY_PIPE_NAME"
#define POSITION_STATE_ENV  "MYOVERLAY_POSITION_STATE_PATH"
#define CFG_PREFIX          "myoverlay-"
#define MOUSE_TRACE_ENV     "MYOVERLAY_MOUSE_TRACE"
#define MOUSE_TRACE_PATH_ENV "MYOVERLAY_MOUSE_TRACE_PATH"
#define MOUSE_TRACE_INTERVAL_ENV "MYOVERLAY_MOUSE_TRACE_INTERVAL_MS"
#define MOUSE_TRACE_FILE_REL "\\vlc-overlay\\mouse-trace.log"
#define MOUSE_TRACE_INTERVAL_DEFAULT_MS 40u

/*****************************************************************************
 * Plugin state
 *****************************************************************************/
typedef struct overlay_frame_buffer_t
{
    volatile LONG refs;
    uint32_t      w;
    uint32_t      h;
    uint8_t       alpha;
    picture_t    *picture;
} overlay_frame_buffer_t;

typedef struct overlay_video_metrics_t {
    volatile LONG refs;
    CRITICAL_SECTION lock;
    video_format_t source;
} overlay_video_metrics_t;

struct subpicture_updater_sys_t {
    overlay_video_metrics_t *metrics;
};

/* Only the video thread accesses this cache. Its region pictures are immutable
 * and held separately by every emitted SPU, so replacing the cache cannot change
 * a frame that VLC is still presenting. */
typedef struct overlay_visual_state_t {
    picture_t *picture;
    int32_t x, y, scroll_offset, scroll_max;
    uint32_t w, h, video_h, scroll_visible, scroll_total;
    uint8_t alpha;
    bool blank, hidden, placeholder, button, scrollbar;
} overlay_visual_state_t;

static void ReleaseVideoMetrics(overlay_video_metrics_t *metrics)
{
    if (metrics && InterlockedDecrement(&metrics->refs) == 0) {
        DeleteCriticalSection(&metrics->lock);
        free(metrics);
    }
}

/* A sub-source filter's fmt_out can be empty. The compositor supplies the
 * actual decoded format here, including during playback without mouse input.
 * This shared object can outlive the filter until VLC releases its last SPU. */
static int ObserveVideoFormat(subpicture_t *spu, bool src_changed,
    const video_format_t *src, bool dst_changed, const video_format_t *dst,
    vlc_tick_t date)
{
    (void)src_changed; (void)dst_changed; (void)dst; (void)date;
    overlay_video_metrics_t *metrics = spu->updater.p_sys->metrics;
    EnterCriticalSection(&metrics->lock);
    metrics->source.i_visible_width = src->i_visible_width;
    metrics->source.i_visible_height = src->i_visible_height;
    metrics->source.i_sar_num = src->i_sar_num;
    metrics->source.i_sar_den = src->i_sar_den;
    LeaveCriticalSection(&metrics->lock);
    return VLC_SUCCESS; /* Preserve the already-rendered regions. */
}

static void UpdateObservedVideo(subpicture_t *spu, const video_format_t *src,
    const video_format_t *dst, vlc_tick_t date)
{
    (void)ObserveVideoFormat(spu, true, src, true, dst, date);
}

static void DestroyVideoObserver(subpicture_t *spu)
{
    ReleaseVideoMetrics(spu->updater.p_sys->metrics);
    free(spu->updater.p_sys);
}

struct filter_sys_t
{
    overlay_video_metrics_t *metrics;
    overlay_visual_state_t cached_visual;
    subpicture_region_t *cached_regions;
    bool cached_visual_valid;
    HANDLE       thread;
    HANDLE       stop_event;
    char         pipe_name[MAX_PATH];
    char         event_pipe_name[MAX_PATH];
    char         position_state_path[MAX_PATH];

    CRITICAL_SECTION lock;
    int32_t      x, y;          /* overlay top-left, drag-owned */
    uint32_t     w, h;          /* interactive footprint; frame size may lag */
    uint8_t      alpha;
    overlay_frame_buffer_t *frame; /* immutable w*h*4 RGBA frame */
    bool         have_frame;    /* false => render placeholder unless blanked */
    bool         blank_until_frame;
    bool         show_placeholder;
    bool         hidden;        /* true => render in-plugin "show chat" button */
    bool         mouse_over_chat;
    bool         input_hovered;
    bool         dragging;
    bool         button_pressed;
    bool         button_moved;
    int32_t      button_down_x;
    int32_t      button_down_y;
    bool         scrollbar_pressed;
    int32_t      scrollbar_drag_y;
    int32_t      scrollbar_offset;
    int32_t      scrollbar_max;
    uint32_t     scrollbar_visible;
    uint32_t     scrollbar_total;
    bool         resizing;
    int32_t      resize_down_x;
    int32_t      resize_down_y;
    uint32_t     resize_start_w;
    uint32_t     resize_start_h;
    uint32_t     resize_sent_w;
    uint32_t     resize_sent_h;
    uint32_t     resize_pending_w;
    uint32_t     resize_pending_h;
    bool         resize_frame_pending;
    bool         mouse_trace_enabled;
    char         mouse_trace_path[MAX_PATH];
    uint32_t     mouse_trace_interval_ms;
    ULONGLONG    mouse_trace_next_tick_ms;
    uint32_t     sent_video_w;
    uint32_t     sent_video_h;
    uint32_t     sent_ui_scale_h;
    ULONGLONG    video_size_next_tick_ms;
    ULONGLONG    video_size_attempt_ms;
    bool         logged_first_frame;
    bool         logged_first_clear;
};

/*****************************************************************************
 * Forward decls
 *****************************************************************************/
static int  Open ( vlc_object_t * );
static void Close( vlc_object_t * );
static subpicture_t *Filter  ( filter_t *, vlc_tick_t );
static subpicture_t *Placeholder( filter_t *, vlc_tick_t, int32_t x, int32_t y,
                                  bool show_button, uint32_t video_h );
static subpicture_t *HiddenButton( filter_t *, vlc_tick_t, int32_t x, int32_t y,
                                   uint32_t w, uint32_t h, bool show_button,
                                   uint32_t video_h );
static int  SubMouse( filter_t *,
                      const vlc_mouse_t *p_old,
                      const vlc_mouse_t *p_new,
                      const video_format_t *p_fmt );
static DWORD WINAPI PipeWorker( LPVOID );
static void DrawToggleButtonRect( uint8_t *pixels, int pitch,
                                  uint32_t w, uint32_t h,
                                  int bx, int by,
                                  uint32_t bw, uint32_t bh,
                                  bool hidden, uint32_t video_h );

/*****************************************************************************
 * Module descriptor
 *****************************************************************************/
int StudioGdiOpen(vlc_object_t *);
void StudioGdiClose(vlc_object_t *);

vlc_module_begin()
    set_shortname( N_("myoverlay") )
    set_description( N_("IPC-driven draggable overlay (named pipe)") )
    set_capability( "sub source", 0 )
    set_category( CAT_VIDEO )
    set_subcategory( SUBCAT_VIDEO_SUBPIC )
    set_callbacks( Open, Close )
    add_string( CFG_PREFIX "pipe", "vlc_overlay",
                N_("Overlay pipe name"),
                N_("Win32 named pipe suffix used by the overlay controller."),
                false )
    add_string( CFG_PREFIX "position-state-path", "",
                N_("Overlay state path"),
                N_("Path used to persist overlay position and hidden state."),
                true )
    add_bool( CFG_PREFIX "show-placeholder", false,
              N_("Show placeholder before first overlay frame"),
              N_("Draw the placeholder box while waiting for the overlay controller."),
              true )
    add_shortcut( "myoverlay" )
    add_submodule()
    set_shortname( "Studio GDI" )
    set_description( "Windows GDI output with filtered scaling" )
    set_capability( "vout display", 0 )
    set_category( CAT_VIDEO )
    set_subcategory( SUBCAT_VIDEO_VOUT )
    add_shortcut( "studio_gdi" )
    set_callbacks( StudioGdiOpen, StudioGdiClose )
vlc_module_end()

static const char *const ppsz_filter_options[] = {
    "pipe", "position-state-path", "show-placeholder", NULL
};

static void InitRgbaFormat( video_format_t *fmt, uint32_t w, uint32_t h )
{
    video_format_Init( fmt, VLC_CODEC_RGBA );
    fmt->i_width = fmt->i_visible_width  = w;
    fmt->i_height = fmt->i_visible_height = h;
    fmt->i_sar_num = fmt->i_sar_den = 1;
}

static overlay_frame_buffer_t *FrameBufferCreate( const overlay_msg_v1 *hdr,
                                                  uint8_t *rgba )
{
    if( hdr == NULL || rgba == NULL )
        return NULL;

    video_format_t fmt;
    InitRgbaFormat( &fmt, hdr->w, hdr->h );
    picture_t *picture = picture_NewFromFormat( &fmt );
    if( picture == NULL || picture->i_planes <= 0 ||
        picture->p[0].p_pixels == NULL )
    {
        if( picture != NULL )
            picture_Release( picture );
        return NULL;
    }

    const int src_pitch = (int)( hdr->w * 4u );
    for( uint32_t row = 0; row < hdr->h; row++ )
        memcpy( picture->p[0].p_pixels + (size_t)row * picture->p[0].i_pitch,
                rgba + (size_t)row * src_pitch,
                src_pitch );

    overlay_frame_buffer_t *frame = malloc( sizeof(*frame) );
    if( frame == NULL )
    {
        picture_Release( picture );
        return NULL;
    }

    frame->refs = 1;
    frame->w = hdr->w;
    frame->h = hdr->h;
    frame->alpha = hdr->alpha;
    frame->picture = picture;
    return frame;
}

static overlay_frame_buffer_t *FrameBufferAddRef( overlay_frame_buffer_t *frame )
{
    if( frame != NULL )
        InterlockedIncrement( &frame->refs );
    return frame;
}

static void FrameBufferRelease( overlay_frame_buffer_t *frame )
{
    if( frame == NULL )
        return;

    if( InterlockedDecrement( &frame->refs ) == 0 )
    {
        picture_Release( frame->picture );
        free( frame );
    }
}

/*****************************************************************************
 * Persistent drag position — tiny "x y\n" text file in %APPDATA%.
 *****************************************************************************/
static bool BuildDefaultStatePath( char *out, size_t out_size, const char *rel )
{
    const char *appdata = getenv( "APPDATA" );
    if( appdata == NULL )
        return false;
    int n = snprintf( out, out_size, "%s%s", appdata, rel );
    return n > 0 && (size_t)n < out_size;
}

static bool BuildPipeStatePath( char *out, size_t out_size,
                                const char *pipe_name )
{
    const char *appdata = getenv( "APPDATA" );
    if( appdata == NULL )
        return false;

    const char *suffix = strrchr( pipe_name, '\\' );
    suffix = suffix != NULL ? suffix + 1 : pipe_name;
    if( suffix == NULL || suffix[0] == '\0' )
        suffix = "vlc_overlay";

    int n = snprintf( out, out_size, "%s%s%s.txt", appdata,
                      POSITION_DIR_REL, suffix );
    return n > 0 && (size_t)n < out_size;
}

static void InitPositionStatePath( filter_t *p_filter, filter_sys_t *sys )
{
    char *configured = var_InheritString( p_filter,
                                          CFG_PREFIX "position-state-path" );
    if( configured != NULL && configured[0] != '\0' )
    {
        int n = snprintf( sys->position_state_path,
                          sizeof(sys->position_state_path), "%s", configured );
        if( n > 0 && (size_t)n < sizeof(sys->position_state_path) )
            return;
        sys->position_state_path[0] = '\0';
    }

    const char *path = getenv( POSITION_STATE_ENV );
    if( path != NULL && path[0] != '\0' )
    {
        int n = snprintf( sys->position_state_path,
                          sizeof(sys->position_state_path), "%s", path );
        if( n > 0 && (size_t)n < sizeof(sys->position_state_path) )
            return;
        sys->position_state_path[0] = '\0';
    }

    if( BuildPipeStatePath( sys->position_state_path,
                            sizeof(sys->position_state_path),
                            sys->pipe_name ) )
    {
        return;
    }

    if( !BuildDefaultStatePath( sys->position_state_path,
                                sizeof(sys->position_state_path),
                                POSITION_FILE_REL ) )
    {
        sys->position_state_path[0] = '\0';
    }
}

static bool EnsureParentDirectory( const char *path )
{
    char dir[MAX_PATH];
    int n = snprintf( dir, sizeof(dir), "%s", path );
    if( n <= 0 || (size_t)n >= sizeof(dir) )
        return false;

    char *slash = strrchr( dir, '\\' );
    char *fslash = strrchr( dir, '/' );
    if( slash == NULL || ( fslash != NULL && fslash > slash ) )
        slash = fslash;
    if( slash == NULL )
        return true;

    *slash = '\0';
    if( dir[0] == '\0' )
        return true;

    if( CreateDirectoryA( dir, NULL ) )
        return true;
    return GetLastError() == ERROR_ALREADY_EXISTS;
}

static void LoadSavedState( filter_sys_t *sys, int32_t *io_x, int32_t *io_y,
                            bool *io_hidden )
{
    if( sys->position_state_path[0] == '\0' )
        return;
    FILE *f = fopen( sys->position_state_path, "r" );
    if( f == NULL )
        return;
    int x, y, hidden;
    const int fields = fscanf( f, "%d %d %d", &x, &y, &hidden );
    if( fields >= 2 )
    {
        *io_x = x;
        *io_y = y;
        if( fields >= 3 )
            *io_hidden = hidden != 0;
    }
    fclose( f );
}

static void SaveOverlayState( filter_sys_t *sys, int32_t x, int32_t y,
                              bool hidden )
{
    if( sys->position_state_path[0] == '\0' ) return;
    if( !EnsureParentDirectory( sys->position_state_path ) ) return;
    FILE *f = fopen( sys->position_state_path, "w" );
    if( f == NULL )
        return;
    fprintf( f, "%d %d %d\n", x, y, hidden ? 1 : 0 );
    fclose( f );
}

static bool IsEnvironmentFlagEnabled( const char *value )
{
    if( value == NULL )
        return false;

    while( *value == ' ' || *value == '\t' || *value == '\r'
        || *value == '\n' )
    {
        value++;
    }
    if( *value == '\0' )
        return false;

    return _stricmp( value, "1" ) == 0
        || _stricmp( value, "true" ) == 0
        || _stricmp( value, "yes" ) == 0
        || _stricmp( value, "on" ) == 0;
}

static bool BuildMouseTracePath( char *out, size_t out_size )
{
    const char *path = getenv( MOUSE_TRACE_PATH_ENV );
    if( path != NULL && path[0] != '\0' )
    {
        int n = snprintf( out, out_size, "%s", path );
        return n > 0 && (size_t)n < out_size;
    }

    return BuildDefaultStatePath( out, out_size, MOUSE_TRACE_FILE_REL );
}

static uint32_t ReadMouseTraceIntervalMilliseconds( void )
{
    const char *raw = getenv( MOUSE_TRACE_INTERVAL_ENV );
    if( raw == NULL || raw[0] == '\0' )
        return MOUSE_TRACE_INTERVAL_DEFAULT_MS;

    char *end = NULL;
    long value = strtol( raw, &end, 10 );
    if( end == raw || value < 5 || value > 5000 )
        return MOUSE_TRACE_INTERVAL_DEFAULT_MS;
    return (uint32_t)value;
}

static void InitMouseTrace( filter_sys_t *sys )
{
    sys->mouse_trace_enabled = IsEnvironmentFlagEnabled(
        getenv( MOUSE_TRACE_ENV ) );
    if( !sys->mouse_trace_enabled )
        return;

    if( !BuildMouseTracePath( sys->mouse_trace_path,
                              sizeof(sys->mouse_trace_path) ) )
    {
        sys->mouse_trace_enabled = false;
        return;
    }

    sys->mouse_trace_interval_ms = ReadMouseTraceIntervalMilliseconds();
    sys->mouse_trace_next_tick_ms = 0;
}

static void MouseTraceLog( filter_sys_t *sys, const char *fmt, ... )
{
    if( !sys->mouse_trace_enabled )
        return;

    ULONGLONG now = GetTickCount64();
    if( sys->mouse_trace_next_tick_ms != 0
        && now < sys->mouse_trace_next_tick_ms )
    {
        return;
    }
    sys->mouse_trace_next_tick_ms = now + sys->mouse_trace_interval_ms;

    if( !EnsureParentDirectory( sys->mouse_trace_path ) )
        return;

    FILE *f = fopen( sys->mouse_trace_path, "a" );
    if( f == NULL )
        return;

    SYSTEMTIME st;
    GetLocalTime( &st );
    fprintf( f, "%04u-%02u-%02u %02u:%02u:%02u.%03u ",
             (unsigned)st.wYear, (unsigned)st.wMonth, (unsigned)st.wDay,
             (unsigned)st.wHour, (unsigned)st.wMinute, (unsigned)st.wSecond,
             (unsigned)st.wMilliseconds );

    va_list ap;
    va_start( ap, fmt );
    vfprintf( f, fmt, ap );
    va_end( ap );
    fputc( '\n', f );
    fclose( f );
}

static bool PipeSuffixCharIsSafe( char c )
{
    return ( c >= 'a' && c <= 'z' )
        || ( c >= 'A' && c <= 'Z' )
        || ( c >= '0' && c <= '9' )
        || c == '_' || c == '-' || c == '.';
}

static void InitPipeName( filter_t *p_filter, filter_sys_t *sys )
{
    char *configured = var_InheritString( p_filter, CFG_PREFIX "pipe" );
    const char *suffix = configured;
    if( suffix == NULL || suffix[0] == '\0' )
        suffix = getenv( PIPE_NAME_ENV );
    if( suffix == NULL || suffix[0] == '\0' )
        suffix = "vlc_overlay";

    char clean[96];
    size_t out = 0;
    for( size_t i = 0; suffix[i] != '\0' && out + 1 < sizeof(clean); i++ )
    {
        clean[out++] = PipeSuffixCharIsSafe( suffix[i] ) ? suffix[i] : '_';
    }
    clean[out] = '\0';

    if( clean[0] == '\0' )
        strcpy( clean, "vlc_overlay" );

    snprintf( sys->pipe_name, sizeof(sys->pipe_name), "\\\\.\\pipe\\%s", clean );
    snprintf( sys->event_pipe_name, sizeof(sys->event_pipe_name),
              "\\\\.\\pipe\\%s_events", clean );
}

static bool SendOverlayEvents( const filter_sys_t *sys,
                               uint32_t type1, int32_t value1,
                               uint32_t type2, int32_t value2,
                               uint32_t type3, int32_t value3 )
{
    if( type1 == 0 && type2 == 0 && type3 == 0 )
        return false;

    HANDLE pipe = CreateFileA( sys->event_pipe_name, GENERIC_WRITE, 0, NULL,
                               OPEN_EXISTING, 0, NULL );
    if( pipe == INVALID_HANDLE_VALUE )
        return false;

    overlay_event_v1 event = {0};
    event.magic   = MYO_MAGIC;
    event.version = MYO_VERSION;

    const uint32_t types[3] = { type1, type2, type3 };
    const int32_t values[3] = { value1, value2, value3 };
    DWORD written = 0;
    bool sent = false;
    bool ok = true;
    for( size_t i = 0; i < 3; i++ )
    {
        if( types[i] == 0 )
            continue;

        event.type = types[i];
        event.value = values[i];
        if( !WriteFile( pipe, &event, (DWORD)sizeof(event), &written, NULL )
            || written != (DWORD)sizeof(event) )
        {
            ok = false;
            break;
        }
        sent = true;
    }
    CloseHandle( pipe );
    return sent && ok;
}

static bool PointInRect( int32_t px, int32_t py, int32_t x, int32_t y,
                         uint32_t w, uint32_t h )
{
    return px >= x && py >= y
        && px < x + (int32_t)w && py < y + (int32_t)h;
}

static uint32_t VisibleVideoHeight( const video_format_t *p_fmt )
{
    /* Keep the user's chat layout in source-video coordinates. App window
     * resizing/DPI must not resize the chat bitmap or reflow its messages. */
    return p_fmt != NULL && p_fmt->i_visible_height > 0
        ? p_fmt->i_visible_height
        : UI_REFERENCE_VIDEO_H;
}

static uint32_t ScaleUiU( uint32_t video_h, uint32_t value )
{
    if( video_h == 0 )
        video_h = UI_REFERENCE_VIDEO_H;

    uint64_t scaled = (uint64_t)value * video_h + UI_REFERENCE_VIDEO_H / 2u;
    scaled /= UI_REFERENCE_VIDEO_H;
    if( scaled < 1u )
        scaled = 1u;
    if( scaled > UINT32_MAX )
        scaled = UINT32_MAX;
    return (uint32_t)scaled;
}

static int ScaleUiI( uint32_t video_h, int value )
{
    if( value <= 0 )
        return (int)ScaleUiU( video_h, 1u );
    return (int)ScaleUiU( video_h, (uint32_t)value );
}

static void MaybeSendVideoSizeEvent( filter_t *filter,
                                     const video_format_t *p_fmt )
{
    filter_sys_t *sys = filter->p_sys;
    if( sys == NULL || p_fmt == NULL || p_fmt->i_visible_width == 0
        || p_fmt->i_visible_height == 0 )
        return;

    const uint32_t video_w = p_fmt->i_visible_width;
    const uint32_t video_h = p_fmt->i_visible_height;
    const uint32_t ui_h = VisibleVideoHeight( p_fmt );
    const ULONGLONG now = GetTickCount64();
    if (now < sys->video_size_attempt_ms) return;
    if( sys->sent_video_w == video_w && sys->sent_video_h == video_h &&
        sys->sent_ui_scale_h == ui_h && now < sys->video_size_next_tick_ms )
        return;

    sys->video_size_attempt_ms = now + 50u;
    if( SendOverlayEvents( sys, MYO_EVENT_VIDEO_SIZE,
                           MYO_PACK_SIZE_EVENT( video_w, video_h ),
                           MYO_EVENT_UI_SCALE, (int32_t)ui_h, 0, 0 ) )
    {
        sys->sent_ui_scale_h = ui_h;
        sys->sent_video_w = video_w;
        sys->sent_video_h = video_h;
        sys->video_size_next_tick_ms = now + 500u;
    }
    else
    {
        sys->video_size_next_tick_ms = now + 250u;
    }
}

static void ToggleButtonRect( bool hidden, int32_t x, int32_t y,
                              uint32_t w, uint32_t h,
                              int32_t *out_x, int32_t *out_y,
                              uint32_t *out_w, uint32_t *out_h,
                              uint32_t video_h )
{
    (void)h;

    if( hidden )
    {
        *out_x = x;
        *out_y = y;
        *out_w = ScaleUiU( video_h, SHOW_BUTTON_W );
        *out_h = ScaleUiU( video_h, SHOW_BUTTON_H );
        return;
    }

    const uint32_t margin = ScaleUiU( video_h, BUTTON_MARGIN );
    *out_w = ScaleUiU( video_h, HIDE_BUTTON_W );
    *out_h = ScaleUiU( video_h, HIDE_BUTTON_H );
    *out_x = x + (int32_t)w - (int32_t)*out_w - (int32_t)margin;
    *out_y = y + (int32_t)margin;
}

static bool ScrollbarTrackRect( int32_t x, int32_t y, uint32_t w, uint32_t h,
                                int32_t *out_x, int32_t *out_y,
                                uint32_t *out_w, uint32_t *out_h,
                                uint32_t video_h )
{
    const uint32_t margin = ScaleUiU( video_h, BUTTON_MARGIN );
    const uint32_t scrollbar_margin = ScaleUiU( video_h, SCROLLBAR_MARGIN );
    const uint32_t scrollbar_w = ScaleUiU( video_h, SCROLLBAR_W );
    const uint32_t scrollbar_min_h = ScaleUiU( video_h, SCROLLBAR_MIN_H );
    const uint32_t top_inset = margin + ScaleUiU( video_h, HIDE_BUTTON_H )
                             + scrollbar_margin;
    const uint32_t bottom_inset = ScaleUiU( video_h, CHAT_INPUT_H )
                                + ScaleUiU( video_h, CHAT_INPUT_MARGIN )
                                + scrollbar_margin;
    if( w < scrollbar_w + scrollbar_margin * 2
        || h < top_inset + bottom_inset + scrollbar_min_h )
        return false;

    *out_w = scrollbar_w;
    *out_x = x + (int32_t)w - (int32_t)scrollbar_w
           - (int32_t)scrollbar_margin;
    *out_y = y + (int32_t)top_inset;
    *out_h = h - top_inset - bottom_inset;
    return *out_h >= scrollbar_min_h;
}

static uint32_t ScrollbarThumbHeight( uint32_t track_h,
                                      uint32_t visible, uint32_t total,
                                      uint32_t video_h )
{
    if( total == 0 || visible == 0 || visible >= total )
        return track_h;

    const uint32_t scrollbar_min_h = ScaleUiU( video_h, SCROLLBAR_MIN_H );
    uint32_t thumb_h = (uint32_t)( (uint64_t)track_h * visible / total );
    if( thumb_h < scrollbar_min_h )
        thumb_h = scrollbar_min_h;
    if( thumb_h > track_h )
        thumb_h = track_h;
    return thumb_h;
}

static int32_t ScrollbarThumbY( int32_t track_y, uint32_t track_h,
                                uint32_t thumb_h,
                                int32_t offset, int32_t max_offset )
{
    const int32_t range = (int32_t)track_h - (int32_t)thumb_h;
    if( max_offset <= 0 || range <= 0 )
        return track_y;

    offset = VLC_CLIP( offset, 0, max_offset );
    return track_y + range - (int32_t)( (int64_t)offset * range / max_offset );
}

static int32_t ScrollbarOffsetFromThumbY( int32_t thumb_y,
                                          int32_t track_y, uint32_t track_h,
                                          uint32_t thumb_h,
                                          int32_t max_offset )
{
    const int32_t range = (int32_t)track_h - (int32_t)thumb_h;
    if( max_offset <= 0 || range <= 0 )
        return 0;

    thumb_y = VLC_CLIP( thumb_y, track_y, track_y + range );
    return (int32_t)( (int64_t)( track_y + range - thumb_y ) * max_offset
                    / range );
}

static bool ScrollbarThumbRect( int32_t x, int32_t y, uint32_t w, uint32_t h,
                                int32_t offset, int32_t max_offset,
                                uint32_t visible, uint32_t total,
                                int32_t *out_x, int32_t *out_y,
                                uint32_t *out_w, uint32_t *out_h,
                                uint32_t video_h )
{
    int32_t tx, ty;
    uint32_t tw, th;
    if( !ScrollbarTrackRect( x, y, w, h, &tx, &ty, &tw, &th, video_h ) )
        return false;

    uint32_t thumb_h = ScrollbarThumbHeight( th, visible, total, video_h );
    *out_x = tx;
    *out_y = ScrollbarThumbY( ty, th, thumb_h, offset, max_offset );
    *out_w = tw;
    *out_h = thumb_h;
    return true;
}

static bool ResizeHandleRect( int32_t x, int32_t y, uint32_t w, uint32_t h,
                              int32_t *out_x, int32_t *out_y,
                              uint32_t *out_w, uint32_t *out_h,
                              uint32_t video_h )
{
    const uint32_t handle_size = ScaleUiU( video_h, RESIZE_HANDLE_SIZE );
    if( w < handle_size || h < handle_size )
        return false;

    *out_w = handle_size;
    *out_h = handle_size;
    *out_x = x + (int32_t)w - (int32_t)*out_w;
    *out_y = y + (int32_t)h - (int32_t)*out_h;
    return true;
}

static bool ChatInputRect( int32_t x, int32_t y, uint32_t w, uint32_t h,
                           int32_t *out_x, int32_t *out_y,
                           uint32_t *out_w, uint32_t *out_h,
                           uint32_t video_h )
{
    const uint32_t margin = ScaleUiU( video_h, CHAT_INPUT_MARGIN );
    const uint32_t input_h = ScaleUiU( video_h, CHAT_INPUT_H );
    if( w <= margin * 2 || h <= input_h + margin * 2 )
        return false;

    *out_x = x + (int32_t)margin;
    *out_y = y + (int32_t)h - (int32_t)input_h - (int32_t)margin;
    *out_w = w - margin * 2;
    *out_h = input_h;
    return true;
}

static uint32_t ClampResizeDimension( int64_t value,
                                      uint32_t min_value,
                                      uint32_t max_value )
{
    if( max_value < min_value )
        max_value = min_value;
    if( value < (int64_t)min_value )
        return min_value;
    if( value > (int64_t)max_value )
        return max_value;
    return (uint32_t)value;
}

static void ResizeTargetFromMouse( const filter_sys_t *sys,
                                   const vlc_mouse_t *p_new,
                                   const video_format_t *p_fmt, uint32_t video_h,
                                   uint32_t *out_w, uint32_t *out_h )
{
    const int dx = p_new->i_x - sys->resize_down_x;
    const int dy = p_new->i_y - sys->resize_down_y;
    const uint32_t min_w = ScaleUiU( video_h, RESIZE_MIN_W );
    const uint32_t min_h = ScaleUiU( video_h, RESIZE_MIN_H );
    uint32_t max_w = ScaleUiU( video_h, RESIZE_MAX_W );
    uint32_t max_h = ScaleUiU( video_h, RESIZE_MAX_H );

    if( p_fmt->i_visible_width > 0 )
    {
        if( sys->x >= (int32_t)p_fmt->i_visible_width )
            max_w = min_w;
        else
        {
            uint32_t room_w = p_fmt->i_visible_width - (uint32_t)sys->x;
            if( room_w < max_w )
                max_w = room_w;
        }
    }
    if( p_fmt->i_visible_height > 0 )
    {
        if( sys->y >= (int32_t)p_fmt->i_visible_height )
            max_h = min_h;
        else
        {
            uint32_t room_h = p_fmt->i_visible_height - (uint32_t)sys->y;
            if( room_h < max_h )
                max_h = room_h;
        }
    }

    uint32_t target_w = ClampResizeDimension( (int64_t)sys->resize_start_w + dx,
                                              min_w, max_w );
    uint32_t target_h = ClampResizeDimension( (int64_t)sys->resize_start_h + dy,
                                              min_h, max_h );

    while( (uint64_t)target_w * target_h * 4u > MYO_MAX_PAYLOAD )
    {
        if( target_w >= target_h && target_w > min_w )
            target_w--;
        else if( target_h > min_h )
            target_h--;
        else
            break;
    }

    *out_w = target_w;
    *out_h = target_h;
}

static void QueueResizeEventIfChanged( filter_sys_t *sys,
                                       uint32_t target_w, uint32_t target_h,
                                       uint32_t *event_type,
                                       int32_t *event_value )
{
    if( target_w == sys->resize_sent_w && target_h == sys->resize_sent_h )
        return;

    sys->resize_sent_w = target_w;
    sys->resize_sent_h = target_h;
    *event_type = MYO_EVENT_RESIZE;
    *event_value = MYO_PACK_SIZE_EVENT( target_w, target_h );
}

static void SetResizeFootprintTarget( filter_sys_t *sys,
                                      uint32_t target_w,
                                      uint32_t target_h,
                                      bool wait_for_frame )
{
    /* Mouse resize owns the hit-test footprint while rendered frames catch up. */
    sys->w = target_w;
    sys->h = target_h;
    sys->resize_pending_w = target_w;
    sys->resize_pending_h = target_h;
    sys->resize_frame_pending = wait_for_frame
        || sys->frame == NULL
        || sys->frame->w != target_w
        || sys->frame->h != target_h;
}

static void PutPixel( uint8_t *pixels, int pitch, uint32_t w, uint32_t h,
                      int x, int y, uint8_t r, uint8_t g, uint8_t b, uint8_t a )
{
    if( x < 0 || y < 0 || x >= (int)w || y >= (int)h )
        return;

    uint8_t *p = pixels + (size_t)y * pitch + (size_t)x * 4u;
    p[0] = r; p[1] = g; p[2] = b; p[3] = a;
}

static void MyFillRect( uint8_t *pixels, int pitch, uint32_t w, uint32_t h,
                        int x, int y, int rw, int rh,
                        uint8_t r, uint8_t g, uint8_t b, uint8_t a )
{
    int x0 = VLC_CLIP( x, 0, (int)w );
    int y0 = VLC_CLIP( y, 0, (int)h );
    int x1 = VLC_CLIP( x + rw, 0, (int)w );
    int y1 = VLC_CLIP( y + rh, 0, (int)h );

    for( int py = y0; py < y1; py++ )
    {
        uint8_t *row = pixels + (size_t)py * pitch;
        for( int px = x0; px < x1; px++ )
        {
            uint8_t *p = row + (size_t)px * 4u;
            p[0] = r; p[1] = g; p[2] = b; p[3] = a;
        }
    }
}

static void MyStrokeRect( uint8_t *pixels, int pitch, uint32_t w, uint32_t h,
                          int x, int y, int rw, int rh,
                          uint8_t r, uint8_t g, uint8_t b, uint8_t a )
{
    MyFillRect( pixels, pitch, w, h, x, y, rw, 1, r, g, b, a );
    MyFillRect( pixels, pitch, w, h, x, y + rh - 1, rw, 1, r, g, b, a );
    MyFillRect( pixels, pitch, w, h, x, y, 1, rh, r, g, b, a );
    MyFillRect( pixels, pitch, w, h, x + rw - 1, y, 1, rh, r, g, b, a );
}

static const uint8_t *Glyph5x7( char c )
{
    static const uint8_t A[7] = { 0x0E,0x11,0x11,0x1F,0x11,0x11,0x11 };
    static const uint8_t C[7] = { 0x0E,0x11,0x10,0x10,0x10,0x11,0x0E };
    static const uint8_t D[7] = { 0x1E,0x11,0x11,0x11,0x11,0x11,0x1E };
    static const uint8_t E[7] = { 0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F };
    static const uint8_t H[7] = { 0x11,0x11,0x11,0x1F,0x11,0x11,0x11 };
    static const uint8_t I[7] = { 0x1F,0x04,0x04,0x04,0x04,0x04,0x1F };
    static const uint8_t O[7] = { 0x0E,0x11,0x11,0x11,0x11,0x11,0x0E };
    static const uint8_t S[7] = { 0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E };
    static const uint8_t T[7] = { 0x1F,0x04,0x04,0x04,0x04,0x04,0x04 };
    static const uint8_t W[7] = { 0x11,0x11,0x11,0x15,0x15,0x15,0x0A };
    static const uint8_t BLANK[7] = { 0,0,0,0,0,0,0 };

    switch( c )
    {
    case 'A': return A;
    case 'C': return C;
    case 'D': return D;
    case 'E': return E;
    case 'H': return H;
    case 'I': return I;
    case 'O': return O;
    case 'S': return S;
    case 'T': return T;
    case 'W': return W;
    default:  return BLANK;
    }
}

static int Text5x7Width( const char *text, int scale )
{
    int chars = (int)strlen( text );
    if( chars <= 0 )
        return 0;
    return chars * 5 * scale + (chars - 1) * scale;
}

static void DrawText5x7( uint8_t *pixels, int pitch, uint32_t w, uint32_t h,
                         int x, int y, const char *text, int scale,
                         uint8_t r, uint8_t g, uint8_t b, uint8_t a )
{
    int cursor = x;
    for( const char *ch = text; *ch; ch++ )
    {
        const uint8_t *rows = Glyph5x7( *ch );
        for( int gy = 0; gy < 7; gy++ )
        {
            for( int gx = 0; gx < 5; gx++ )
            {
                if( ( rows[gy] & ( 1 << ( 4 - gx ) ) ) == 0 )
                    continue;

                for( int sy = 0; sy < scale; sy++ )
                    for( int sx = 0; sx < scale; sx++ )
                        PutPixel( pixels, pitch, w, h,
                                  cursor + gx * scale + sx,
                                  y + gy * scale + sy,
                                  r, g, b, a );
            }
        }
        cursor += 6 * scale;
    }
}

static void DrawToggleButton( uint8_t *pixels, int pitch, uint32_t w, uint32_t h,
                              bool hidden, uint32_t video_h )
{
    const uint32_t bw = ScaleUiU( video_h, hidden ? SHOW_BUTTON_W : HIDE_BUTTON_W );
    const uint32_t bh = ScaleUiU( video_h, hidden ? SHOW_BUTTON_H : HIDE_BUTTON_H );
    const int margin = ScaleUiI( video_h, BUTTON_MARGIN );
    const int bx = hidden ? 0 : (int)w - (int)bw - margin;
    const int by = hidden ? 0 : margin;
    DrawToggleButtonRect( pixels, pitch, w, h, bx, by, bw, bh, hidden,
                          video_h );
}

static void DrawToggleButtonRect( uint8_t *pixels, int pitch,
                                  uint32_t w, uint32_t h,
                                  int bx, int by,
                                  uint32_t bw, uint32_t bh,
                                  bool hidden, uint32_t video_h )
{
    const char *label = hidden ? "SHOW" : "HIDE";
    const int scale = ScaleUiI( video_h, 2 );
    const int text_w = Text5x7Width( label, scale );
    const int text_h = 7 * scale;
    const int tx = bx + ( (int)bw - text_w ) / 2;
    const int ty = by + ( (int)bh - text_h ) / 2;

    MyFillRect( pixels, pitch, w, h, bx, by, (int)bw, (int)bh,
                hidden ? 159 : 30, hidden ? 39 : 125, hidden ? 59 : 58, 235 );
    MyStrokeRect( pixels, pitch, w, h, bx, by, (int)bw, (int)bh,
                  255, 255, 255, 120 );
    DrawText5x7( pixels, pitch, w, h, tx + 1, ty + 1, label, scale,
                 0, 0, 0, 170 );
    DrawText5x7( pixels, pitch, w, h, tx, ty, label, scale,
                 255, 255, 255, 255 );
}

static void DrawResizeHandle( uint8_t *pixels, int pitch, uint32_t w, uint32_t h,
                              uint32_t video_h )
{
    const int size = ScaleUiI( video_h, RESIZE_HANDLE_SIZE );
    const int x0 = (int)w - size;
    const int y0 = (int)h - size;

    if( x0 < 0 || y0 < 0 )
        return;

    MyFillRect( pixels, pitch, w, h, x0, y0, size, size, 0, 0, 0, 70 );
    for( int i = 0; i < 3; i++ )
    {
        const int inset = ScaleUiI( video_h, 4 + i * 5 );
        const int x1 = (int)w - inset;
        const int y1 = (int)h - ScaleUiI( video_h, 3 );
        const int x2 = (int)w - ScaleUiI( video_h, 3 );
        const int steps = x2 - x1;
        for( int step = 0; step <= steps; step++ )
        {
            PutPixel( pixels, pitch, w, h,
                      x1 + step, y1 - step,
                      255, 255, 255, 175 );
            PutPixel( pixels, pitch, w, h,
                      x1 + step + 1, y1 - step,
                      0, 0, 0, 90 );
        }
    }
}

/*****************************************************************************
 * NewOverlaySubpicture: allocate a subpicture_t with one RGBA region at
 * (x, y) of size w*h, primed for direct pixel writes by the caller.
 *
 * On success, returns the subpicture and writes the region's pixel buffer
 * address + row pitch through out_pixels / out_pitch. Caller fills the
 * buffer however it likes and returns the subpicture from pf_sub_source;
 * VLC takes ownership and frees everything.
 *
 * On any allocation failure returns NULL; nothing to free.
 *****************************************************************************/
static subpicture_t *NewObservedSubpicture(filter_t *filter)
{
    subpicture_updater_sys_t *observer = calloc(1, sizeof(*observer));
    if (!observer) return NULL;
    observer->metrics = filter->p_sys->metrics;
    InterlockedIncrement(&observer->metrics->refs);
    subpicture_updater_t updater = { ObserveVideoFormat, UpdateObservedVideo,
        DestroyVideoObserver, observer };
    subpicture_t *spu = subpicture_New(&updater);
    if (!spu) { ReleaseVideoMetrics(observer->metrics); free(observer); }
    return spu;
}

static subpicture_t *NewOverlaySubpicture( filter_t *p_filter, vlc_tick_t date,
                                           uint32_t w, uint32_t h,
                                           int32_t x, int32_t y, uint8_t alpha,
                                           uint8_t **out_pixels, int *out_pitch )
{
    video_format_t fmt;
    InitRgbaFormat( &fmt, w, h );

    subpicture_region_t *region = subpicture_region_New( &fmt );
    if( region == NULL )
        return NULL;

    region->i_x     = x;
    region->i_y     = y;
    region->i_align = SUBPICTURE_ALIGN_LEFT | SUBPICTURE_ALIGN_TOP;
    region->i_alpha = alpha;

    subpicture_t *spu = NewObservedSubpicture( p_filter );
    if( spu == NULL )
    {
        subpicture_region_ChainDelete( region );
        return NULL;
    }

    spu->p_region   = region;
    spu->i_start    = date;
    spu->i_stop     = 0;
    spu->b_ephemer  = true;
    spu->b_absolute = true;

    *out_pixels = region->p_picture->p[0].p_pixels;
    *out_pitch  = region->p_picture->p[0].i_pitch;
    return spu;
}

static subpicture_t *NewSubpictureFromRegion( filter_t *p_filter,
                                              vlc_tick_t date,
                                              subpicture_region_t *region )
{
    subpicture_t *spu = NewObservedSubpicture( p_filter );
    if( spu == NULL )
    {
        subpicture_region_ChainDelete( region );
        return NULL;
    }

    spu->p_region   = region;
    spu->i_start    = date;
    spu->i_stop     = 0;
    spu->b_ephemer  = true;
    spu->b_absolute = true;
    return spu;
}

static subpicture_region_t *NewPictureRegion( picture_t *picture,
                                              int32_t x, int32_t y,
                                              uint8_t alpha )
{
    video_format_t fmt;
    InitRgbaFormat( &fmt, picture->format.i_visible_width,
                    picture->format.i_visible_height );

    /* VLC 3's text-region constructor allocates the region without a bitmap.
     * We supply an existing RGBA picture instead. Preserve the same sRGB/full
     * range region metadata that its RGBA constructor normally initializes. */
    fmt.i_chroma = VLC_CODEC_TEXT;
    subpicture_region_t *region = subpicture_region_New( &fmt );
    if( region == NULL )
        return NULL;

    region->fmt.i_chroma = VLC_CODEC_RGBA;
    region->fmt.transfer = TRANSFER_FUNC_SRGB;
    region->fmt.primaries = COLOR_PRIMARIES_SRGB;
    region->fmt.space = COLOR_SPACE_SRGB;
    region->fmt.b_color_range_full = true;
    region->p_picture = picture_Hold( picture );

    region->i_x     = x;
    region->i_y     = y;
    region->i_align = SUBPICTURE_ALIGN_LEFT | SUBPICTURE_ALIGN_TOP;
    region->i_alpha = alpha;
    return region;
}

static subpicture_t *CopyCachedOverlay( filter_t *filter, vlc_tick_t date )
{
    subpicture_region_t *head = NULL;
    subpicture_region_t **next = &head;
    for( const subpicture_region_t *source = filter->p_sys->cached_regions;
         source != NULL; source = source->p_next )
    {
        *next = NewPictureRegion( source->p_picture, source->i_x, source->i_y,
                                  source->i_alpha );
        if( *next == NULL )
        {
            subpicture_region_ChainDelete( head );
            return NULL;
        }
        next = &(*next)->p_next;
    }
    return NewSubpictureFromRegion( filter, date, head );
}

static subpicture_t *CacheOverlay( filter_t *filter, vlc_tick_t date,
                                   const overlay_visual_state_t *visual,
                                   subpicture_t *spu )
{
    filter_sys_t *sys = filter->p_sys;
    subpicture_region_ChainDelete( sys->cached_regions );
    sys->cached_regions = NULL;
    sys->cached_visual_valid = false;
    if( spu == NULL ) return NULL;
    sys->cached_regions = spu->p_region;
    spu->p_region = NULL;
    subpicture_Delete( spu );
    memcpy( &sys->cached_visual, visual, sizeof(*visual) );
    sys->cached_visual_valid = true;
    return CopyCachedOverlay( filter, date );
}

static subpicture_region_t *NewWritableRegion( uint32_t w, uint32_t h,
                                               int32_t x, int32_t y,
                                               uint8_t **out_pixels,
                                               int *out_pitch )
{
    video_format_t fmt;
    InitRgbaFormat( &fmt, w, h );

    subpicture_region_t *region = subpicture_region_New( &fmt );
    if( region == NULL )
        return NULL;

    region->i_x     = x;
    region->i_y     = y;
    region->i_align = SUBPICTURE_ALIGN_LEFT | SUBPICTURE_ALIGN_TOP;
    region->i_alpha = 0xFF;

    uint8_t *pixels = region->p_picture->p[0].p_pixels;
    const int pitch = region->p_picture->p[0].i_pitch;
    for( uint32_t row = 0; row < h; row++ )
        memset( pixels + (size_t)row * pitch, 0, (size_t)w * 4u );

    *out_pixels = pixels;
    *out_pitch = pitch;
    return region;
}

static void AppendRegion( subpicture_region_t **tail,
                          subpicture_region_t *region )
{
    if( region == NULL )
        return;
    (*tail)->p_next = region;
    *tail = region;
}

static subpicture_t *FrameSubpicture( filter_t *p_filter, vlc_tick_t date,
                                       picture_t *picture,
                                       uint32_t render_w, uint32_t render_h,
                                       uint32_t footprint_w,
                                       uint32_t footprint_h,
                                       int32_t x, int32_t y,
                                       uint8_t alpha,
                                       bool show_button,
                                       bool show_scrollbar,
                                       int32_t scroll_offset,
                                       int32_t scroll_max,
                                       uint32_t scroll_visible,
                                       uint32_t scroll_total,
                                       uint32_t video_h )
{
    if( render_w == 0 || render_h == 0
        || footprint_w == 0 || footprint_h == 0 )
        return NULL;

    subpicture_region_t *head = NewPictureRegion( picture, x, y, alpha );
    if( head == NULL )
        return NULL;
    subpicture_region_t *tail = head;

    if( show_scrollbar )
    {
        int32_t track_x = 0, track_y = 0;
        uint32_t track_w = 0, track_h = 0;
        if( ScrollbarTrackRect( x, y, footprint_w, footprint_h,
                                &track_x, &track_y,
                                &track_w, &track_h, video_h ) )
        {
            uint8_t *pixels = NULL;
            int pitch = 0;
            subpicture_region_t *region =
                NewWritableRegion( track_w, track_h, track_x, track_y,
                                   &pixels, &pitch );
            if( region == NULL ) goto failed;
            if( region != NULL )
            {
                MyFillRect( pixels, pitch, track_w, track_h, 0, 0,
                            (int)track_w, (int)track_h, 0, 0, 0, 82 );
                MyStrokeRect( pixels, pitch, track_w, track_h, 0, 0,
                              (int)track_w, (int)track_h,
                              255, 255, 255, 70 );

                int32_t thumb_x = 0, thumb_y = 0;
                uint32_t thumb_w = 0, thumb_h = 0;
                if( ScrollbarThumbRect( x, y, footprint_w, footprint_h,
                                        scroll_offset,
                                        scroll_max, scroll_visible,
                                        scroll_total, &thumb_x, &thumb_y,
                                        &thumb_w, &thumb_h, video_h ) )
                {
                    const int inset = ScaleUiI( video_h, 1 );
                    MyFillRect( pixels, pitch, track_w, track_h,
                                thumb_x - track_x + inset,
                                thumb_y - track_y + inset,
                                (int)thumb_w - inset * 2,
                                (int)thumb_h - inset * 2,
                                230, 238, 255,
                                scroll_max > 0 ? 210 : 110 );
                }
                AppendRegion( &tail, region );
            }
        }
    }

    if( show_button )
    {
        int32_t bx = 0, by = 0;
        uint32_t bw = 0, bh = 0;
        ToggleButtonRect( false, x, y, footprint_w, footprint_h,
                          &bx, &by, &bw, &bh, video_h );
        uint8_t *pixels = NULL;
        int pitch = 0;
        subpicture_region_t *button =
            NewWritableRegion( bw, bh, bx, by, &pixels, &pitch );
        if( button == NULL ) goto failed;
        if( button != NULL )
        {
            DrawToggleButtonRect( pixels, pitch, bw, bh, 0, 0, bw, bh,
                                  false, video_h );
            AppendRegion( &tail, button );
        }

        int32_t rx = 0, ry = 0;
        uint32_t rw = 0, rh = 0;
        if( ResizeHandleRect( x, y, footprint_w, footprint_h,
                              &rx, &ry, &rw, &rh, video_h ) )
        {
            subpicture_region_t *resize =
                NewWritableRegion( rw, rh, rx, ry, &pixels, &pitch );
            if( resize == NULL ) goto failed;
            if( resize != NULL )
            {
                DrawResizeHandle( pixels, pitch, rw, rh, video_h );
                AppendRegion( &tail, resize );
            }
        }
    }

    return NewSubpictureFromRegion( p_filter, date, head );
failed:
    subpicture_region_ChainDelete( head );
    return NULL;
}

/*****************************************************************************
 * Lifecycle
 *****************************************************************************/
static int Open( vlc_object_t *p_this )
{
    filter_t *p_filter = (filter_t *)p_this;

    filter_sys_t *sys = calloc( 1, sizeof(*sys) );
    if( sys == NULL )
        return VLC_ENOMEM;

    sys->metrics = calloc(1, sizeof(*sys->metrics));
    if (!sys->metrics) { free(sys); return VLC_ENOMEM; }
    sys->metrics->refs = 1;
    InitializeCriticalSection(&sys->metrics->lock);
    InitializeCriticalSection( &sys->lock );
    config_ChainParse( p_filter, CFG_PREFIX, ppsz_filter_options,
                       p_filter->p_cfg );
    InitPipeName( p_filter, sys );
    InitPositionStatePath( p_filter, sys );
    sys->show_placeholder = var_InheritBool( p_filter,
                                             CFG_PREFIX "show-placeholder" );
    sys->blank_until_frame = !sys->show_placeholder;

    sys->stop_event = CreateEventA( NULL, TRUE, FALSE, NULL );
    if( sys->stop_event == NULL )
        goto err_after_cs;

    /* Initial position + placeholder size. Drag-updatable from the moment
     * Open() returns. Position is restored from disk if a previous drag
     * saved one. */
    sys->x     = DEFAULT_X;
    sys->y     = DEFAULT_Y;
    LoadSavedState( sys, &sys->x, &sys->y, &sys->hidden );
    sys->w     = PLACEHOLDER_W;
    sys->h     = PLACEHOLDER_H;
    sys->alpha = 0xFF;
    InitMouseTrace( sys );

    p_filter->p_sys         = sys;
    p_filter->pf_sub_source = Filter;
    p_filter->pf_sub_mouse  = SubMouse;

    MouseTraceLog( sys,
                   "open x=%d y=%d w=%u h=%u hidden=%d showPlaceholder=%d blankUntilFrame=%d pipe=%s eventPipe=%s state=%s",
                   sys->x, sys->y, sys->w, sys->h, sys->hidden ? 1 : 0,
                   sys->show_placeholder ? 1 : 0,
                   sys->blank_until_frame ? 1 : 0,
                   sys->pipe_name, sys->event_pipe_name,
                   sys->position_state_path );
    msg_Dbg( p_filter,
             "myoverlay open show_placeholder=%d blank_until_frame=%d pipe=%s event_pipe=%s state=%s",
             sys->show_placeholder ? 1 : 0,
             sys->blank_until_frame ? 1 : 0,
             sys->pipe_name,
             sys->event_pipe_name,
             sys->position_state_path );

    sys->thread = CreateThread( NULL, 0, PipeWorker, p_filter, 0, NULL );
    if( sys->thread == NULL )
        goto err_after_event;

    return VLC_SUCCESS;

err_after_event:
    CloseHandle( sys->stop_event );
err_after_cs:
    DeleteCriticalSection( &sys->lock );
    ReleaseVideoMetrics(sys->metrics);
    free( sys );
    return VLC_EGENERIC;
}

static void Close( vlc_object_t *p_this )
{
    filter_t *p_filter = (filter_t *)p_this;
    filter_sys_t *sys = p_filter->p_sys;

    SetEvent( sys->stop_event );
    CancelSynchronousIo( sys->thread );  /* wake any pending ConnectNamedPipe / ReadFile */

    if( WaitForSingleObject( sys->thread, 2000 ) == WAIT_TIMEOUT )
        TerminateThread( sys->thread, 1 );
    CloseHandle( sys->thread );
    CloseHandle( sys->stop_event );

    EnterCriticalSection( &sys->lock );
    overlay_frame_buffer_t *frame = sys->frame;
    sys->frame = NULL;
    LeaveCriticalSection( &sys->lock );
    FrameBufferRelease( frame );
    subpicture_region_ChainDelete( sys->cached_regions );
    DeleteCriticalSection( &sys->lock );
    ReleaseVideoMetrics(sys->metrics);
    free( sys );
}

/*****************************************************************************
 * SubMouse: drag-to-position. Sub source modules use pf_sub_mouse
 * (different signature than pf_video_mouse — they share an anonymous
 * union slot in filter_t). See src/misc/filter_chain.c::filter_chain_MouseEvent.
 *
 *   return VLC_EGENERIC => event consumed
 *   return VLC_SUCCESS  => let it propagate (default click-to-pause etc.)
 *****************************************************************************/
static int SubMouse( filter_t *p_filter,
                     const vlc_mouse_t *p_old,
                     const vlc_mouse_t *p_new,
                     const video_format_t *p_fmt )
{
    filter_sys_t *sys = p_filter->p_sys;
    const uint32_t video_h = VisibleVideoHeight( p_fmt );
    MaybeSendVideoSizeEvent( p_filter, p_fmt );
    bool    save_now = false;
    bool    toggled = false;
    bool    ui_release = false;
    uint32_t event_type = 0;
    int32_t event_value = 0;
    uint32_t hover_event_type = 0;
    int32_t hover_event_value = 0;
    uint32_t focus_event_type = 0;
    int32_t focus_event_value = 0;
    int32_t save_x = 0, save_y = 0;
    bool    save_hidden = false;

    EnterCriticalSection( &sys->lock );
    if( sys->blank_until_frame )
    {
        sys->mouse_over_chat = false;
        sys->dragging = false;
        sys->button_pressed = false;
        sys->button_moved = false;
        sys->scrollbar_pressed = false;
        sys->resizing = false;
        if( sys->input_hovered )
        {
            sys->input_hovered = false;
            hover_event_type = MYO_EVENT_CHAT_INPUT_HOVER;
            hover_event_value = 0;
        }
        LeaveCriticalSection( &sys->lock );
        SendOverlayEvents( sys, 0, 0,
                           hover_event_type, hover_event_value,
                           0, 0 );
        return VLC_SUCCESS;
    }

    const bool hidden = sys->hidden;
    uint32_t hover_w = sys->w;
    uint32_t hover_h = sys->h;
    if( hidden )
    {
        const uint32_t show_w = ScaleUiU( video_h, SHOW_BUTTON_W );
        const uint32_t show_h = ScaleUiU( video_h, SHOW_BUTTON_H );
        if( hover_w < show_w ) hover_w = show_w;
        if( hover_h < show_h ) hover_h = show_h;
    }
    int32_t bx, by;
    uint32_t bw, bh;
    ToggleButtonRect( hidden, sys->x, sys->y, sys->w, sys->h,
                      &bx, &by, &bw, &bh, video_h );

    const bool b_over_chat = PointInRect( p_new->i_x, p_new->i_y,
                                          sys->x, sys->y, hover_w, hover_h );
    const bool b_over_button = b_over_chat
                            && PointInRect( p_new->i_x, p_new->i_y,
                                            bx, by, bw, bh );
    int32_t track_x = 0, track_y = 0;
    uint32_t track_w = 0, track_h = 0;
    const bool have_scrollbar = !hidden
        && ScrollbarTrackRect( sys->x, sys->y, sys->w, sys->h,
                               &track_x, &track_y, &track_w, &track_h,
                               video_h );
    int32_t thumb_x = 0, thumb_y = 0;
    uint32_t thumb_w = 0, thumb_h = 0;
    const bool have_thumb = have_scrollbar
        && ScrollbarThumbRect( sys->x, sys->y, sys->w, sys->h,
                               sys->scrollbar_offset, sys->scrollbar_max,
                               sys->scrollbar_visible, sys->scrollbar_total,
                               &thumb_x, &thumb_y, &thumb_w, &thumb_h,
                               video_h );
    const bool b_over_scrollbar = have_scrollbar
        && PointInRect( p_new->i_x, p_new->i_y,
                        track_x, track_y, track_w, track_h );
    int32_t resize_x = 0, resize_y = 0;
    uint32_t resize_w = 0, resize_h = 0;
    const bool have_resize = !hidden
        && ResizeHandleRect( sys->x, sys->y, sys->w, sys->h,
                             &resize_x, &resize_y, &resize_w, &resize_h,
                             video_h );
    const bool b_over_resize = have_resize
        && PointInRect( p_new->i_x, p_new->i_y,
                        resize_x, resize_y, resize_w, resize_h );
    int32_t input_x = 0, input_y = 0;
    uint32_t input_w = 0, input_h = 0;
    const bool have_input = !hidden
        && ChatInputRect( sys->x, sys->y, sys->w, sys->h,
                          &input_x, &input_y, &input_w, &input_h,
                          video_h );
    const bool b_over_input = have_input
        && PointInRect( p_new->i_x, p_new->i_y,
                        input_x, input_y, input_w, input_h );
    const bool left_pressed =
        vlc_mouse_HasPressed( p_old, p_new, MOUSE_BUTTON_LEFT );

    if( left_pressed )
    {
        focus_event_type = MYO_EVENT_CHAT_INPUT_FOCUS;
        focus_event_value = b_over_input ? 1 : 0;
    }

    if( left_pressed && b_over_button )
    {
        sys->button_pressed = true;
        sys->button_moved = false;
        sys->button_down_x = p_new->i_x;
        sys->button_down_y = p_new->i_y;
    }
    else if( left_pressed && b_over_resize )
    {
        sys->resizing = true;
        sys->resize_down_x = p_new->i_x;
        sys->resize_down_y = p_new->i_y;
        sys->resize_start_w = sys->w;
        sys->resize_start_h = sys->h;
        sys->resize_sent_w = sys->w;
        sys->resize_sent_h = sys->h;
    }
    else if( left_pressed && b_over_scrollbar )
    {
        sys->scrollbar_pressed = true;
        if( have_thumb && PointInRect( p_new->i_x, p_new->i_y,
                                       thumb_x, thumb_y, thumb_w, thumb_h ) )
            sys->scrollbar_drag_y = p_new->i_y - thumb_y;
        else
            sys->scrollbar_drag_y = (int32_t)( ( have_thumb ? thumb_h
                                                            : ScaleUiU( video_h, SCROLLBAR_MIN_H ) )
                                             / 2u );

        uint32_t local_thumb_h = have_thumb ? thumb_h
                                : ScrollbarThumbHeight( track_h,
                                                        sys->scrollbar_visible,
                                                        sys->scrollbar_total,
                                                        video_h );
        int32_t target = ScrollbarOffsetFromThumbY(
            p_new->i_y - sys->scrollbar_drag_y,
            track_y, track_h, local_thumb_h, sys->scrollbar_max );
        sys->scrollbar_offset = target;
        event_type = MYO_EVENT_SCROLL_TO;
        event_value = target;
    }
    else if( left_pressed && b_over_input )
    {
        /* Focus event is queued above; this branch exists to consume the click. */
    }
    else if( left_pressed && !hidden && b_over_chat )
    {
        sys->dragging = true;
    }
    else if( vlc_mouse_HasReleased( p_old, p_new, MOUSE_BUTTON_LEFT ) )
    {
        ui_release = sys->button_pressed || sys->scrollbar_pressed
                  || sys->resizing;
        if( sys->button_pressed && b_over_button && !sys->button_moved )
        {
            sys->hidden = !sys->hidden;
            toggled = true;
            save_now = true;
            save_x = sys->x;
            save_y = sys->y;
            save_hidden = sys->hidden;
        }
        sys->button_pressed = false;
        sys->button_moved = false;
        sys->scrollbar_pressed = false;

        if( sys->resizing )
        {
            uint32_t target_w, target_h;
            ResizeTargetFromMouse( sys, p_new, p_fmt, video_h, &target_w, &target_h );
            SetResizeFootprintTarget( sys, target_w, target_h, true );
            sys->resize_sent_w = 0;
            sys->resize_sent_h = 0;
            QueueResizeEventIfChanged( sys, target_w, target_h,
                                       &event_type, &event_value );
        }
        sys->resizing = false;

        if( sys->dragging )
        {
            save_now = true;
            save_x = sys->x;
            save_y = sys->y;
            save_hidden = sys->hidden;
        }
        sys->dragging = false;
    }

    if( sys->button_pressed )
    {
        const int dx = p_new->i_x - sys->button_down_x;
        const int dy = p_new->i_y - sys->button_down_y;
        const int move_limit = ScaleUiI( video_h, BUTTON_MOVE_LIMIT );
        if( dx * dx + dy * dy > move_limit * move_limit )
            sys->button_moved = true;
    }

    if( sys->scrollbar_pressed && have_scrollbar )
    {
        uint32_t local_thumb_h = have_thumb ? thumb_h
                                : ScrollbarThumbHeight( track_h,
                                                        sys->scrollbar_visible,
                                                        sys->scrollbar_total,
                                                        video_h );
        int32_t target = ScrollbarOffsetFromThumbY(
            p_new->i_y - sys->scrollbar_drag_y,
            track_y, track_h, local_thumb_h, sys->scrollbar_max );
        if( target != sys->scrollbar_offset )
        {
            sys->scrollbar_offset = target;
            event_type = MYO_EVENT_SCROLL_TO;
            event_value = target;
        }
    }

    if( sys->resizing )
    {
        uint32_t target_w, target_h;
        ResizeTargetFromMouse( sys, p_new, p_fmt, video_h, &target_w, &target_h );
        SetResizeFootprintTarget( sys, target_w, target_h, true );
        QueueResizeEventIfChanged( sys, target_w, target_h,
                                   &event_type, &event_value );
    }

    if( sys->dragging )
    {
        int dx, dy;
        vlc_mouse_GetMotion( &dx, &dy, p_old, p_new );

        const int vid_w = (int)p_fmt->i_visible_width;
        const int vid_h = (int)p_fmt->i_visible_height;
        int max_x = vid_w - (int)sys->w;  if( max_x < 0 ) max_x = 0;
        int max_y = vid_h - (int)sys->h;  if( max_y < 0 ) max_y = 0;

        sys->x = VLC_CLIP( sys->x + dx, 0, max_x );
        sys->y = VLC_CLIP( sys->y + dy, 0, max_y );
    }

    sys->mouse_over_chat = b_over_chat || sys->dragging || sys->button_pressed
                         || sys->scrollbar_pressed || sys->resizing
                         || b_over_input;
    const bool input_hovered_now = !sys->hidden && b_over_input;
    if( input_hovered_now != sys->input_hovered )
    {
        sys->input_hovered = input_hovered_now;
        hover_event_type = MYO_EVENT_CHAT_INPUT_HOVER;
        hover_event_value = input_hovered_now ? 1 : 0;
    }

    const bool consume = sys->dragging || sys->button_pressed
                       || sys->scrollbar_pressed || sys->resizing || toggled
                       || ui_release || event_type != 0
                       || ( focus_event_type != 0 && focus_event_value != 0 );
    const int32_t trace_sys_x = sys->x;
    const int32_t trace_sys_y = sys->y;
    const uint32_t trace_sys_w = sys->w;
    const uint32_t trace_sys_h = sys->h;
    const bool trace_hidden = sys->hidden;
    const bool trace_dragging = sys->dragging;
    const bool trace_resizing = sys->resizing;
    const bool trace_scrollbar_pressed = sys->scrollbar_pressed;
    const bool trace_button_pressed = sys->button_pressed;
    const bool trace_mouse_over_chat = sys->mouse_over_chat;
    LeaveCriticalSection( &sys->lock );

    MouseTraceLog(
        sys,
        "submouse p=%d,%d fmt=%u,%u sys=%d,%d,%u,%u hidden=%d button=%d,%d,%u,%u "
        "overChat=%d overButton=%d overInput=%d overScrollbar=%d overResize=%d "
        "dragging=%d resizing=%d scrollbarPressed=%d buttonPressed=%d mouseOver=%d consume=%d",
        p_new->i_x, p_new->i_y,
        p_fmt->i_visible_width, p_fmt->i_visible_height,
        trace_sys_x, trace_sys_y, trace_sys_w, trace_sys_h,
        trace_hidden ? 1 : 0,
        bx, by, bw, bh,
        b_over_chat ? 1 : 0,
        b_over_button ? 1 : 0,
        b_over_input ? 1 : 0,
        b_over_scrollbar ? 1 : 0,
        b_over_resize ? 1 : 0,
        trace_dragging ? 1 : 0,
        trace_resizing ? 1 : 0,
        trace_scrollbar_pressed ? 1 : 0,
        trace_button_pressed ? 1 : 0,
        trace_mouse_over_chat ? 1 : 0,
        consume ? 1 : 0 );

    SendOverlayEvents( sys, event_type, event_value,
                       hover_event_type, hover_event_value,
                       focus_event_type, focus_event_value );

    if( save_now )
        SaveOverlayState( sys, save_x, save_y, save_hidden );

    return consume ? VLC_EGENERIC : VLC_SUCCESS;
}

/*****************************************************************************
 * Filter: per-frame subpicture builder.
 *****************************************************************************/
static subpicture_t *Filter( filter_t *p_filter, vlc_tick_t date )
{
    filter_sys_t *sys = p_filter->p_sys;
    video_format_t source;
    EnterCriticalSection(&sys->metrics->lock);
    source = sys->metrics->source;
    LeaveCriticalSection(&sys->metrics->lock);
    if (!source.i_visible_height) source = p_filter->fmt_out.video;
    const uint32_t video_h = VisibleVideoHeight( &source );
    MaybeSendVideoSizeEvent( p_filter, &source );

    int32_t  x, y;
    uint32_t footprint_w, footprint_h;
    uint32_t render_w = 0, render_h = 0;
    uint8_t  alpha = 0xFF;
    overlay_frame_buffer_t *frame = NULL;
    bool     have;
    bool     hidden;
    bool     blank_until_frame;
    bool     show_placeholder;
    bool     show_button;
    bool     show_scrollbar;
    int32_t  scroll_offset;
    int32_t  scroll_max;
    uint32_t scroll_visible;
    uint32_t scroll_total;

    EnterCriticalSection( &sys->lock );
    footprint_w = sys->w; footprint_h = sys->h;
    x = sys->x; y = sys->y;
    const int32_t max_x = (int32_t)source.i_visible_width - (int32_t)footprint_w;
    const int32_t max_y = (int32_t)source.i_visible_height - (int32_t)footprint_h;
    if( source.i_visible_width && x > max_x ) x = max_x > 0 ? max_x : 0;
    if( source.i_visible_height && y > max_y ) y = max_y > 0 ? max_y : 0;
    sys->x = x; sys->y = y;
    hidden = sys->hidden;
    blank_until_frame = sys->blank_until_frame;
    show_placeholder = sys->show_placeholder;
    show_button = sys->mouse_over_chat || sys->button_pressed || sys->resizing;
    show_scrollbar = !hidden
                  && ( sys->mouse_over_chat || sys->scrollbar_pressed
                       || sys->scrollbar_offset > 0 );
    scroll_offset = sys->scrollbar_offset;
    scroll_max = sys->scrollbar_max;
    scroll_visible = sys->scrollbar_visible;
    scroll_total = sys->scrollbar_total;
    have = !hidden && sys->have_frame && sys->frame != NULL
        && footprint_w > 0 && footprint_h > 0;
    if( have )
    {
        frame = FrameBufferAddRef( sys->frame );
        if( frame != NULL )
        {
            render_w = frame->w;
            render_h = frame->h;
            alpha = frame->alpha;
        }
        else
        {
            have = false;
        }
    }
    LeaveCriticalSection( &sys->lock );

    overlay_visual_state_t visual;
    /* Clear padding as well, since equality below compares the complete key. */
    memset( &visual, 0, sizeof(visual) );
    visual.picture = frame != NULL ? frame->picture : NULL;
    visual.x = x; visual.y = y;
    visual.w = footprint_w; visual.h = footprint_h;
    visual.video_h = video_h; visual.alpha = alpha;
    visual.blank = blank_until_frame; visual.hidden = hidden;
    visual.placeholder = show_placeholder; visual.button = show_button;
    visual.scrollbar = show_scrollbar; visual.scroll_offset = scroll_offset;
    visual.scroll_max = scroll_max; visual.scroll_visible = scroll_visible;
    visual.scroll_total = scroll_total;
    if( sys->cached_visual_valid &&
        !memcmp( &sys->cached_visual, &visual, sizeof(visual) ) )
    {
        FrameBufferRelease( frame );
        return CopyCachedOverlay( p_filter, date );
    }

    if( blank_until_frame )
    {
        FrameBufferRelease( frame );
        uint8_t *pixels; int pitch;
        subpicture_t *blank = NewOverlaySubpicture(p_filter, date, 1, 1, 0, 0, 0, &pixels, &pitch);
        if (blank) memset(pixels, 0, 4);
        return CacheOverlay( p_filter, date, &visual, blank );
    }

    if( hidden )
    {
        FrameBufferRelease( frame );
        return CacheOverlay( p_filter, date, &visual, HiddenButton( p_filter, date, x, y,
                             footprint_w, footprint_h, show_button,
                             video_h ) );
    }

    if( !have )
    {
        if( !show_placeholder )
            return CacheOverlay( p_filter, date, &visual, NULL );
        return CacheOverlay( p_filter, date, &visual,
            Placeholder( p_filter, date, x, y, show_button, video_h ) );
    }

    subpicture_t *spu = FrameSubpicture( p_filter, date,
                                          frame->picture,
                                          render_w, render_h,
                                          footprint_w, footprint_h,
                                          x, y, alpha,
                                          show_button, show_scrollbar,
                                          scroll_offset, scroll_max,
                                          scroll_visible, scroll_total,
                                          video_h );
    FrameBufferRelease( frame );
    return CacheOverlay( p_filter, date, &visual, spu );
}

/* Placeholder: translucent red box with a black border. Drawn whenever no
 * controller has pushed a frame yet. Position tracks the same sys->x,sys->y
 * as live frames, so dragging works in either state. */
static subpicture_t *Placeholder( filter_t *p_filter, vlc_tick_t date,
                                  int32_t x, int32_t y, bool show_button,
                                  uint32_t video_h )
{
    const int W = ScaleUiI( video_h, PLACEHOLDER_W );
    const int H = ScaleUiI( video_h, PLACEHOLDER_H );
    const int BORDER = ScaleUiI( video_h, 3 );

    uint8_t *pixels;
    int pitch;
    subpicture_t *spu = NewOverlaySubpicture( p_filter, date, W, H, x, y, 0xFF,
                                              &pixels, &pitch );
    if( spu == NULL )
        return NULL;

    for( int py = 0; py < H; py++ )
    {
        uint8_t *row = pixels + py * pitch;
        for( int px = 0; px < W; px++ )
        {
            const int border = (px < BORDER) || (px >= W - BORDER)
                            || (py < BORDER) || (py >= H - BORDER);
            row[px*4 + 0] = border ?   0 : 220;
            row[px*4 + 1] = border ?   0 :  30;
            row[px*4 + 2] = border ?   0 :  30;
            row[px*4 + 3] = border ? 255 : 200;
        }
    }
    if( show_button )
    {
        DrawToggleButton( pixels, pitch, W, H, false, video_h );
        DrawResizeHandle( pixels, pitch, W, H, video_h );
    }
    return spu;
}

static subpicture_t *HiddenButton( filter_t *p_filter, vlc_tick_t date,
                                   int32_t x, int32_t y,
                                   uint32_t w, uint32_t h, bool show_button,
                                   uint32_t video_h )
{
    const uint32_t show_w = ScaleUiU( video_h, SHOW_BUTTON_W );
    const uint32_t show_h = ScaleUiU( video_h, SHOW_BUTTON_H );
    if( w < show_w ) w = show_w;
    if( h < show_h ) h = show_h;
    const int W = (int)w;
    const int H = (int)h;

    uint8_t *pixels;
    int pitch;
    subpicture_t *spu = NewOverlaySubpicture( p_filter, date, W, H, x, y, 0xFF,
                                              &pixels, &pitch );
    if( spu == NULL )
        return NULL;

    MyFillRect( pixels, pitch, W, H, 0, 0, W, H, 0, 0, 0, 0 );
    if( show_button )
        DrawToggleButton( pixels, pitch, W, H, true, video_h );
    return spu;
}

/*****************************************************************************
 * Pipe worker thread
 *****************************************************************************/
static bool ReadAll( HANDLE pipe, void *buf, DWORD bytes )
{
    uint8_t *cursor = buf;
    while( bytes )
    {
        DWORD got = 0;
        if( !ReadFile( pipe, cursor, bytes, &got, NULL ) || got == 0 )
            return false;
        cursor += got;
        bytes  -= got;
    }
    return true;
}

/* Apply one validated message to plugin state. The frame payload is copied
 * into a VLC picture before the state lock is taken. */
static void ApplyMessage( filter_t *p_filter, filter_sys_t *sys,
                          const overlay_msg_v1 *hdr, uint8_t *payload )
{
    bool save_position = false;
    int32_t save_x = 0;
    int32_t save_y = 0;
    bool save_hidden = false;
    overlay_frame_buffer_t *new_frame = NULL;
    overlay_frame_buffer_t *old_frame = NULL;
    bool log_first_frame = false;
    bool log_first_clear = false;

    if( hdr->type == MYO_TYPE_FRAME )
    {
        new_frame = FrameBufferCreate( hdr, payload );
        if( new_frame == NULL )
        {
            free( payload );
            return;
        }
    }

    EnterCriticalSection( &sys->lock );
    switch( hdr->type )
    {
    case MYO_TYPE_FRAME:
        if( !sys->logged_first_frame )
        {
            sys->logged_first_frame = true;
            log_first_frame = true;
        }
        old_frame       = sys->frame;
        sys->frame      = new_frame;     /* ownership moves */
        if( sys->resize_frame_pending
            && hdr->w == sys->resize_pending_w
            && hdr->h == sys->resize_pending_h )
        {
            if( !sys->resizing )
                sys->resize_frame_pending = false;
            sys->w = hdr->w;
            sys->h = hdr->h;
        }
        else if( !sys->resizing && !sys->resize_frame_pending )
        {
            sys->w = hdr->w;
            sys->h = hdr->h;
        }
        sys->alpha      = hdr->alpha;
        sys->have_frame = true;
        sys->blank_until_frame = false;
        new_frame = NULL;
        /* x, y in header are intentionally ignored — see protocol.h */
        break;

    case MYO_TYPE_CLEAR:
        if( !sys->logged_first_clear )
        {
            sys->logged_first_clear = true;
            log_first_clear = true;
        }
        old_frame       = sys->frame;
        sys->frame      = NULL;
        sys->have_frame = false;
        sys->blank_until_frame = true;
        sys->w          = PLACEHOLDER_W;  /* restore hit-test bounds */
        sys->h          = PLACEHOLDER_H;
        sys->resize_frame_pending = false;
        break;

    case MYO_TYPE_POSITION:
        sys->x     = hdr->x;
        sys->y     = hdr->y;
        sys->alpha = hdr->alpha;
        save_position = true;
        save_x = hdr->x;
        save_y = hdr->y;
        save_hidden = sys->hidden;
        break;

    case MYO_TYPE_SCROLL_STATE:
        sys->scrollbar_offset = hdr->x < 0 ? 0 : hdr->x;
        sys->scrollbar_max = hdr->y < 0 ? 0 : hdr->y;
        if( sys->scrollbar_offset > sys->scrollbar_max )
            sys->scrollbar_offset = sys->scrollbar_max;
        sys->scrollbar_visible = hdr->w;
        sys->scrollbar_total = hdr->h;
        break;
    }
    LeaveCriticalSection( &sys->lock );
    FrameBufferRelease( old_frame );
    FrameBufferRelease( new_frame );
    free( payload );  /* NULL if FRAME moved ownership; harmless */
    if( log_first_frame )
    {
        msg_Dbg( p_filter,
                 "myoverlay first FRAME w=%u h=%u alpha=%u payload=%u pipe=%s",
                 hdr->w, hdr->h, (unsigned)hdr->alpha, hdr->payload_size,
                 sys->pipe_name );
        MouseTraceLog( sys,
                       "first FRAME w=%u h=%u alpha=%u payload=%u pipe=%s",
                       hdr->w, hdr->h, (unsigned)hdr->alpha,
                       hdr->payload_size,
                       sys->pipe_name );
    }
    if( log_first_clear )
    {
        msg_Dbg( p_filter, "myoverlay first CLEAR pipe=%s",
                 sys->pipe_name );
        MouseTraceLog( sys, "first CLEAR pipe=%s", sys->pipe_name );
    }
    if( save_position )
        SaveOverlayState( sys, save_x, save_y, save_hidden );
}

/* Validate a header for a known + sane message. */
static bool HeaderIsValid( const overlay_msg_v1 *hdr )
{
    if( hdr->magic != MYO_MAGIC || hdr->version != MYO_VERSION )
        return false;
    if( hdr->payload_size > MYO_MAX_PAYLOAD )
        return false;
    if( hdr->type == MYO_TYPE_FRAME )
    {
        const uint64_t expect = (uint64_t)hdr->w * hdr->h * 4u;
        return expect != 0 && expect == hdr->payload_size;
    }
    if( hdr->type == MYO_TYPE_CLEAR || hdr->type == MYO_TYPE_POSITION
        || hdr->type == MYO_TYPE_SCROLL_STATE )
        return hdr->payload_size == 0;
    return false;
}

/* Service one connected client until the pipe breaks or stop is signalled. */
static void ServeClient( filter_t *p_filter, HANDLE pipe )
{
    filter_sys_t *sys = p_filter->p_sys;

    while( WaitForSingleObject( sys->stop_event, 0 ) == WAIT_TIMEOUT )
    {
        overlay_msg_v1 hdr;
        if( !ReadAll( pipe, &hdr, sizeof(hdr) ) )
        {
            DWORD gle = GetLastError();
            msg_Dbg( p_filter,
                     "myoverlay pipe disconnected while reading header pipe=%s gle=%lu",
                     sys->pipe_name, (unsigned long)gle );
            MouseTraceLog( sys,
                           "pipe disconnected while reading header pipe=%s gle=%lu",
                           sys->pipe_name, (unsigned long)gle );
            break;
        }
        if( !HeaderIsValid( &hdr ) )
        {
            msg_Warn( p_filter,
                      "myoverlay invalid header magic=0x%08x version=%u type=%u payload=%u w=%u h=%u pipe=%s",
                      hdr.magic, hdr.version, (unsigned)hdr.type,
                      hdr.payload_size,
                      hdr.w, hdr.h, sys->pipe_name );
            MouseTraceLog( sys,
                           "invalid header magic=0x%08x version=%u type=%u payload=%u w=%u h=%u pipe=%s",
                           hdr.magic, hdr.version, (unsigned)hdr.type,
                           hdr.payload_size, hdr.w, hdr.h,
                           sys->pipe_name );
            break;
        }

        uint8_t *payload = NULL;
        if( hdr.payload_size > 0 )
        {
            payload = malloc( hdr.payload_size );
            if( payload == NULL )
            {
                msg_Warn( p_filter,
                          "myoverlay payload allocation failed payload=%u pipe=%s",
                          hdr.payload_size, sys->pipe_name );
                break;
            }
            if( !ReadAll( pipe, payload, hdr.payload_size ) )
            {
                DWORD gle = GetLastError();
                msg_Dbg( p_filter,
                         "myoverlay pipe disconnected while reading payload type=%u payload=%u pipe=%s gle=%lu",
                         (unsigned)hdr.type, hdr.payload_size,
                         sys->pipe_name,
                         (unsigned long)gle );
                MouseTraceLog( sys,
                               "pipe disconnected while reading payload type=%u payload=%u pipe=%s gle=%lu",
                               (unsigned)hdr.type, hdr.payload_size,
                               sys->pipe_name,
                               (unsigned long)gle );
                free( payload );
                break;
            }
        }

        ApplyMessage( p_filter, sys, &hdr, payload );
    }
}

static DWORD WINAPI PipeWorker( LPVOID arg )
{
    filter_t *p_filter = arg;
    filter_sys_t *sys = p_filter->p_sys;

    while( WaitForSingleObject( sys->stop_event, 0 ) == WAIT_TIMEOUT )
    {
        HANDLE pipe = CreateNamedPipeA(
            sys->pipe_name,
            PIPE_ACCESS_INBOUND,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 0, 64 * 1024, 0, NULL );
        if( pipe == INVALID_HANDLE_VALUE )
        {
            DWORD gle = GetLastError();
            msg_Warn( p_filter,
                      "myoverlay CreateNamedPipe failed pipe=%s gle=%lu",
                      sys->pipe_name, (unsigned long)gle );
            MouseTraceLog( sys,
                           "CreateNamedPipe failed pipe=%s gle=%lu",
                           sys->pipe_name, (unsigned long)gle );
            Sleep( 500 );
            continue;
        }

        BOOL connected = ConnectNamedPipe( pipe, NULL )
                      || GetLastError() == ERROR_PIPE_CONNECTED;
        if( !connected )
        {
            DWORD gle = GetLastError();
            CloseHandle( pipe );
            if( gle == ERROR_OPERATION_ABORTED )
                break;  /* Close() told us to stop */
            msg_Dbg( p_filter,
                     "myoverlay ConnectNamedPipe failed pipe=%s gle=%lu",
                     sys->pipe_name, (unsigned long)gle );
            MouseTraceLog( sys,
                           "ConnectNamedPipe failed pipe=%s gle=%lu",
                           sys->pipe_name, (unsigned long)gle );
            continue;
        }

        msg_Dbg( p_filter, "myoverlay pipe client connected pipe=%s",
                 sys->pipe_name );
        MouseTraceLog( sys, "pipe client connected pipe=%s",
                       sys->pipe_name );
        ServeClient( p_filter, pipe );
        msg_Dbg( p_filter, "myoverlay pipe client disconnected pipe=%s",
                 sys->pipe_name );
        MouseTraceLog( sys, "pipe client disconnected pipe=%s",
                       sys->pipe_name );

        FlushFileBuffers( pipe );
        DisconnectNamedPipe( pipe );
        CloseHandle( pipe );
    }
    return 0;
}
