# Long Twitch VOD resume failure (2026-09-26)

## Evidence

The affected video is Twitch VOD `2881171681`, xQc's September 22 broadcast,
duration `14:03:13.316`. The user's saved position was `13:43:01.217`.
The application log records the same failure at 01:58:58, 02:00:21, and 02:00:44:
VLC remained `Playing`, its time stayed exactly at the submitted `49381217` ms,
and replay confirmation timed out after fifteen seconds. The black screen was
the intentional output gate, which correctly withheld unconfirmed playback.

The CDN playlist and segments 4925–4931 returned HTTP 200. Their PCR and PTS
timestamps were valid and increasing. Segment 4927 covers media time
49371.816–49381.816; its first PCR is 49433.816. This is not a missing VOD,
authorization failure, muted-section sentinel, or overlay-rendering failure.

Independent libVLC 3.0.23 probes reproduced the frozen clock using start-time
alone, set-time alone, and both. The same source plays from 47000 seconds.
A local synthetic MPEG-TS playlist reproduces the large-seek failure without
Twitch, network latency, authentication, or the app's native overlay.

VLC's [adaptive timestamp continuity code](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/plumbing/FakeESOut.cpp)
adjusts timestamps to the nearest half of a 33-bit, 90 kHz rollover period.
That half-period is approximately 13h 15m 21.859s. A fresh input that establishes
an early reference and then jumps to this bookmark can be interpreted as
crossing the rollover in the wrong direction. The demux timeline fails to
advance to the requested position. The API's immediate set-time echo is not
evidence of decoded or displayed output.

## Change

Completed MPEG-TS HLS replays opened at twelve hours or later receive a local
playlist beginning near the requested segment, with one segment of preroll.
VLC gets a small relative seek. The engine adds the omitted prefix duration
back to its public clock, health samples, and duration. The user's full VOD
timeline, saved bookmark, chat position, mute state, and completion semantics
remain absolute.

Subsequent seeks reopen from the original playlist when needed, including
backward seeks outside the adapted prefix. Window moves retain the adapted
source; re-creating an input after long uninterrupted playback also applies
the adapter. Generation checks prevent a pending seek from replacing newer
media. The temporary playlist and its upstream media lease are released with
the player. Files live inside the product's local data directory.

This adapter preserves segment durations, media sequence, discontinuity
sequence, retained discontinuities, and muted segment filenames. Reads are
bounded; public provider redirects and segment URLs are validated. Growing,
encrypted, byte-range, master, and fMP4 playlists retain their existing playback
path because they require different refresh or initialization semantics.

## Validation

`SVS_TEST_FILTER='long VOD'` with `SVS_TEST_VLC_DIRECTORY` enables the native
regression. Its 28-hour local fixture shifts real MPEG-TS PCR/PTS/DTS fields,
including an actual 33-bit wrap. It verifies late startup, advancing decoded
and displayed output, full duration, paused seeking, backward seeking,
window movement, seeking beyond 26 hours, and playlist cleanup. Pure tests
cover precision, segment boundaries, sequences, unsupported formats, malformed
input, unsafe URLs, and lease ownership.

The optional `SVS_TEST_LONG_VOD_URI` test was run against the actual affected
CDN playlist at the exact bookmark, with the native overlay enabled. Startup
completed in 10.86 seconds, the confirmed clock reached 49381283 ms with 586
displayed pictures, and the full duration remained 50593316 ms. All seven
focused tests passed. Diagnostic probes and logs are in the ignored
`artifacts/xqc-vod-resume` directory.

The broader native-enabled run executed 1,384 tests with no skips: 1,381 passed
on the first run. Two packaging checks selected the older system SDK; both
passed after putting the pinned 10.0.302 SDK on the subprocess PATH. An existing
immediate-reopen fixture raced its asynchronous Home-to-tab playback policy;
the fixture now awaits that controller's idle task before starting its fake
player. All 23 resume checks, including that case and native HLS/MP4 reopening,
then passed. Both packaging checks also passed separately. No production
playback test failed in the broad run.

The self-contained build was installed at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe`, with the old
executable backed up in `artifacts/xqc-vod-resume/installed-before.exe`.
Installed SHA-256:
`DDD972EC1605F21F69F1F6D35CB7FB8BB350791CED8FF03178620E03E6F40F00`.
The installed app reopened the affected VOD from the saved bookmark; its UI
reported `13:43:12 / 14:03:13`, and the history subsequently recorded confirmed
progress at `13:43:42.183`. The desktop screenshot helper was unavailable
(`SetIsBorderRequired: E_NOINTERFACE`); this last check used accessibility and
saved decoder progress, alongside the native decoded/displayed-frame test.

The regression protects this reproducible playback defect. It cannot ensure
that an upstream VOD stays available or that every future VLC/network failure
is impossible.
