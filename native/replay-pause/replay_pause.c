// SPDX-License-Identifier: LGPL-2.1-or-later
// VLC 3.0 adaptive HLS resets its segment tracker on DEMUX_SET_PAUSE_STATE.
// Let the input/decoder clock pause normally, retaining the adaptive demuxer's
// bounded buffer. Playback then continues from the same frame without a reload.
#define MODULE_STRING "studio_replay_pause"

// VLC source headers normally receive this declaration from generated config.h.
struct pollfd;
int poll(struct pollfd *, unsigned, int);
#include <vlc_common.h>
#include <vlc_plugin.h>
#include <vlc_demux.h>
#include <vlc_modules.h>

struct demux_sys_t { HANDLE ready; int64_t preroll; };

static int Demux(demux_t *demux)
{
    return demux_Demux(demux->p_next);
}

static int Control(demux_t *demux, int query, va_list args)
{
    // Input's ControlPause/ControlUnpause still freeze/rebase es_out and its
    // decoders. Only the adaptive demuxer's destructive live reset is bypassed.
    if (query == DEMUX_SET_PAUSE_STATE && demux->p_sys->preroll == 0)
        return VLC_SUCCESS;
    if (query == DEMUX_SET_TIME && demux->p_sys->preroll > 0)
    {
        int64_t target = va_arg(args, int64_t);
        bool accurate = va_arg(args, int);
        // FFmpeg HLS discards packets up to the next keyframe at/after its seek
        // timestamp, even for AVSEEK_FLAG_BACKWARD. Begin at least one complete
        // segment earlier and let VLC discard decoded preroll at the exact target.
        int64_t start = target > demux->p_sys->preroll ? target - demux->p_sys->preroll : 0;
        int result = demux_Control(demux->p_next, query, start, accurate);
        if (result == VLC_SUCCESS && accurate)
            result = es_out_Control(demux->p_next->out, ES_OUT_SET_NEXT_DISPLAY_TIME, target);
        return result;
    }
    return demux_vaControl(demux->p_next, query, args);
}

static int Open(vlc_object_t *object)
{
    demux_t *demux = (demux_t *)object;
    if (!demux->p_next->p_module) return VLC_EGENERIC;
    const char *module = module_get_object(demux->p_next->p_module);
    int64_t preroll = var_InheritInteger(demux, "studio-replay-seek-preroll");
    if (strcmp(module, "adaptive") == 0) preroll = 0;
    else if (strcmp(module, "avcodec") != 0 || !demux->p_next->out ||
             preroll <= 0 || preroll > 30 * CLOCK_FREQ) return VLC_EGENERIC;

    char *name = var_InheritString(demux, "studio-replay-pause-ready");
    if (!name)
        return VLC_EGENERIC;
    HANDLE ready = OpenEventA(EVENT_MODIFY_STATE, FALSE, name);
    free(name);
    if (!ready)
        return VLC_EGENERIC;

    demux_sys_t *sys = malloc(sizeof(*sys));
    if (!sys)
    {
        CloseHandle(ready);
        return VLC_ENOMEM;
    }
    sys->ready = ready;
    sys->preroll = preroll;
    demux->p_sys = sys;
    demux->pf_demux = Demux;
    demux->pf_control = Control;
    // A per-input handshake. The application uses in-place resume only after
    // this module has actually attached, including on replacement/rebound inputs.
    if (!SetEvent(ready))
    {
        CloseHandle(ready);
        free(sys);
        return VLC_EGENERIC;
    }
    return VLC_SUCCESS;
}

static void Close(vlc_object_t *object)
{
    demux_t *demux = (demux_t *)object;
    ResetEvent(demux->p_sys->ready);
    CloseHandle(demux->p_sys->ready);
    free(demux->p_sys);
}

vlc_module_begin()
    set_description("Stream Studio replay pause continuity")
    set_capability("demux_filter", 0)
    set_callbacks(Open, Close)
    add_string("studio-replay-pause-ready", NULL, "Replay pause readiness event", NULL, true)
    add_integer("studio-replay-seek-preroll", 0, "Completed HLS seek preroll (microseconds)", NULL, true)
vlc_module_end()
