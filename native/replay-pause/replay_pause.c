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

struct demux_sys_t { HANDLE ready; };

static int Demux(demux_t *demux)
{
    return demux_Demux(demux->p_next);
}

static int Control(demux_t *demux, int query, va_list args)
{
    // Input's ControlPause/ControlUnpause still freeze/rebase es_out and its
    // decoders. Only the adaptive demuxer's destructive live reset is bypassed.
    if (query == DEMUX_SET_PAUSE_STATE)
        return VLC_SUCCESS;
    return demux_vaControl(demux->p_next, query, args);
}

static int Open(vlc_object_t *object)
{
    demux_t *demux = (demux_t *)object;
    if (!demux->p_next->p_module ||
        strcmp(module_get_object(demux->p_next->p_module), "adaptive") != 0)
        return VLC_EGENERIC;

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
vlc_module_end()
