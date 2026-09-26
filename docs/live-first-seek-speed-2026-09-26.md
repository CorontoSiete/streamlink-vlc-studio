# First seek from a live stream (2026-09-26)

## Reproduction and cause

The application's log at 05:06 recorded first seeks taking 5,380 ms for aceu
and 5,589 ms for mande inside `PlayFromAsync`. URL resolution took 0–1 ms and
pre-playback cleanup took 28–34 ms. The existing URL prefetch was already working.

The first seek replaces Streamlink's live transport with the seekable broadcast
HLS input. That input had to initialize VLC's adaptive demuxer and establish its
timestamp reference on the click path. Later seeks reuse that initialized input.
Native traces also showed the cold input reading a live-edge segment before
restoring the requested position.

The original engine was measured against mande's actual growing broadcast
playlist at 601.217 seconds, with VLC 3.0.23, the production muted-segment gateway,
and native overlay enabled. Its three first-seek confirmations took 4,570, 4,201,
and 3,505 ms; the corresponding checks of advancing displayed video completed at
4,820, 4,990, and 3,757 ms. Subsequent seeks completed their output checks in
1,118–2,203 ms. The raw baseline is `artifacts/live-first-seek/before.log`.

## Change

After resolving the live broadcast's replay URL, the tab prepares a separate,
silent replay input on a worker. It disables video entirely, waits for real
audio decoding and a usable timestamp reference, then pauses that input using
the existing replay pause filter. Preparation leaves the active live player
running. Native tests verify zero decoded video and zero displayed pictures in
the prepared input, while the original live player's pictures continue advancing.

The first seek transfers that prepared input into the playback engine, enables
its video track, and seeks precisely to the requested position. The existing
audio/video output gate stays closed until VLC reports an advancing clock at the
target. The implementation does not change cache settings, seek tolerance, the
output gate, or the normal seek timeout.

VLC's public duration stays frozen while an input is paused, although its
adaptive playlist continues refreshing. Activation therefore does not reject a
newly appended timestamp using that stale duration. A regression grows a paused
40-second playlist to 60 seconds, then verifies that the same prepared player
seeks to 45.25 seconds and produces video with the updated duration.

Preparation has a ten-second budget and owns its source lease and native runtime
reference. Cancellation returns promptly even if a native HTTP read is still
draining; cleanup runs off the UI and active-player lock. Stop, replacement,
disposal, and hidden-tab suspension release the input. Returning to a suspended
tab or recovering live playback prepares again from the cached replay URL.
Moving the video to another HWND keeps preparation alive and binds the prepared
player to the new HWND when it is adopted.

This optimization uses the checked VLC 3.0.23 object ABI, matching the installed
runtime and the existing replay filter. Enabling video uses the exported,
reference-counted input accessor and releases that reference after setting the
input variable. Relevant upstream implementations are
[input ownership](https://github.com/videolan/vlc/blob/3.0.23/lib/media_player.c),
[video track selection](https://github.com/videolan/vlc/blob/3.0.23/lib/video.c),
[decoder selection](https://github.com/videolan/vlc/blob/3.0.23/src/input/es_out.c),
and [adaptive refresh and seeking](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/PlaylistManager.cpp).

If preparation is not ready, lacks usable audio/video, is canceled, or runs on
an unsupported runtime, playback uses the normal opening path. The existing
twelve-hour timestamp rollover guard also retains that path. Preparation does
not make unavailable media or CDN downloads instantaneous and does not delay
seekbar availability to hide a cold open.

## Verification

The new `live first seek` tests exercise real native players and generated HLS
fixtures, including delayed segment responses, first presented pixels, growing
playlists, repeated seeks, pause/resume, cancellation, replacement, disposal,
volume/mute changes during activation, and the real tab-to-engine handoff.
Routing tests cover both direct and resolved URLs, hidden-tab suspension and
resumption, and stop cancellation without starting another URL resolution.

The first presented nonblack frame at 35.25 seconds must be green. Red preroll or
blue live-edge output fails the test. Initial runs presented that frame in
1,052–1,087 ms. The growing-playlist check produced advancing output at the newly
appended position in 1,116–1,913 ms.

Run the test executable after a Release build with
`SVS_TEST_VLC_DIRECTORY=C:\Program Files\VideoLAN\VLC` and
`SVS_TEST_FILTER=live first seek`. Set `SVS_TEST_LIVE_FIRST_SEEK_URI` to an available
broadcast media playlist to repeat three first-seek and subsequent-seek trials
against its CDN. Measurements include confirmation of the requested clock,
retention of the prepared native player, and advancing displayed pictures.

Diagnostic logs and native probes are retained under the ignored directory
`artifacts/live-first-seek`.

The initial focused run passed all nine tests without skips. Three independent
trials against each actual broadcast also passed:

| Broadcast | First-seek confirmations | Advancing video checks |
| --- | --- | --- |
| mande, before | 4,570 / 4,201 / 3,505 ms | 4,820 / 4,990 / 3,757 ms |
| mande, after | 1,926 / 1,064 / 1,772 ms | 2,434 / 1,854 / 2,025 ms |
| aceu, after | 1,873 / 1,065 / 1,116 ms | 2,378 / 1,293 / 1,867 ms |

These are measured runs with changing CDN latency, not a latency guarantee.
The video-health checks poll VLC's periodically published picture counters;
the separate pixel callback test measures first presented content directly.
Raw final logs are `regressions-final.log`, `mande-final.log`, and `aceu-final.log`.

The broader native-enabled suite passed 1,188 tests, skipped 247 checks requiring
interactive desktop input, and reported no failures or timeouts. It covered live
recovery, VOD startup/seeking/resume/completion, mute and volume, pause continuity,
growing playlists, exact first frames, cancellation, and timeout behavior. See
`full-tests.log`.

A final native window-transition regression reproduced preparation being
discarded during output rebinding (`rebind-before.log`). Rebinding now retains
it, while stop/media replacement still cancel it. The final Release solution
build passed with warnings as errors, formatting verification passed, and all
ten focused tests passed without skips (`release-regressions.log`). The added
test verifies the same native player is adopted, its output HWND is the new
window, and video advances at the requested timestamp. In that final run the
three local first seeks confirmed in 1,067, 1,064, and 1,061 ms; the separate
first-pixel check presented the correct green frame in 2,017 ms.

## Installed build

The self-contained Windows x64 publish completed successfully. The executable
was installed atomically at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe` after verifying
that the application was closed and the previous executable had not changed.
The installed file and published file have the same SHA-256:
`A0A7985DBA209FAC9624F17877E0926576D9D3A1E18E2C052FD006AA264E7D93`.

The previous executable is retained at
`artifacts/live-first-seek/installed-before.exe`; its hash matches the original
`D2A4223FD1ED0B33297564424920FB17C75DE7AAA9BA646A8BE161E89C4BC532`.
The existing native replay filter source and binary were preserved byte for byte.
