# VOD seek latency (2026-09-26)

## Reproduction

The native engine was measured against Twitch archive `2881171681`, with
libVLC 3.0.23, the production muted-segment gateway, and the native chat overlay.
Every measured seek was followed by assertions on the absolute playback clock,
full duration, and advancing displayed pictures. Diagnostic output is in the
ignored `artifacts/vod-seek-speed` directory.

The original implementation reopened the player for every seek whose absolute
position exceeded twelve hours, even when that input already used the long-VOD
timeline adapter. Seeking to 49395.217, 49505.217, and 49385.217 seconds took
4843, 4614, and 5054 ms. The player generation changed for every seek.

With input reuse alone, those same seeks took 706, 1010, and 1007 ms and retained
the player generation. Local, generated MPEG-TS timelines reproduce the
unnecessary replacement independently of Twitch. Their regression failed before
the correction.

A second problem affected opening at a timestamp, including seeks that genuinely
need to reopen. `Playing` and media duration become available before the HLS
demuxer has its initial timestamp reference. The immediate second seek could not
discard preroll precisely, so VLC played it through while the output gate stayed
closed. A standalone native probe using downloaded segments from this archive
reproduced this without network traffic: immediate seeking took 10.743 seconds;
seeking after the first real clock sample took 1.700 seconds. Start-time alone
took 9.937 seconds. See `probe_startup.py` and `start-*.log`.

## Correction

The twelve-hour guard now uses the requested position relative to the current
input's timeline offset. Seeks within its safe range retain the player, source
lease, and connections. Seeking before the retained playlist or beyond the safe
relative range still reopens from the original source. The existing guard for
inputs that have themselves played for twelve hours remains active.

Opening still supplies `:start-time` to select the segment. It now waits for a
nonzero playback clock sample before submitting a precise corrective seek. If
playback has already reached the requested position, the extra seek is omitted.
The output gate, exact seek behavior, cancellation and generation checks,
timeout, pause state, muted-segment repair, and absolute VOD/chat timeline remain
in use.

With both corrections, opening the actual archive at 49381.217 seconds took
2902 ms, compared with 11790 ms before. Its subsequent three seeks took 755,
1007, and 759 ms. These are measured runs, not a guarantee about CDN latency.

Upstream behavior was checked against VideoLAN's
[adaptive seek implementation](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/PlaylistManager.cpp),
[input start-time handling](https://github.com/videolan/vlc/blob/3.0.23/src/input/input.c),
and [libVLC time API](https://github.com/videolan/vlc/blob/3.0.23/lib/media_player.c).

## Regression checks

Set `SVS_TEST_VLC_DIRECTORY` to an installed VLC 3 directory, build the test
project, and run its executable with `SVS_TEST_FILTER='VOD seek latency'`.
These checks exercise ordinary and rebased timelines, forward and backward
seeks, retained player generations, absent playlist reloads, real output, and
paused seeks. A delayed HTTP fixture also checks the first presented frame and
bounded startup time. Set `SVS_TEST_LONG_VOD_URI` to the available archive's media
playlist to include the same checks against its CDN, on both original and rebased
timelines with the native overlay enabled.

The existing `pause`, `long VOD`, and other VOD tests cover first presented
pixels, silence while restoring, cancellation, timeout, growing EVENT playlists,
seeking outside a rebased prefix, 33-bit timestamp rollover, timeline lease
cleanup, exact-end seeking, and muted endings.

The native-enabled full run executed 1,423 tests, with 1,422 passing initially,
one fake-slider fixture failure, and no skips or timeouts. The fixture set a
slider value before asynchronous replay metadata was ready and omitted the
keyboard preview/commit lifecycle. It now waits for the same readiness as the
real disabled slider and follows that lifecycle; its exact position assertions
are unchanged. This test does not use libVLC. The original failure remains in
`full-tests.log`. The separate 53-test pause/playback run passed without failures
or skips. All new native seek checks, including both actual-CDN timelines and
paused seeks, passed in the full run.

The corrected slider fixture passed ten consecutive runs; both neighboring seek
preview tests also passed. The final Release solution build (warnings as errors),
format verification, self-contained publish, and `git diff --check` passed.

The tested executable was installed at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe`.
Its SHA-256 matches the published executable:
`B16D9EA5CFE23A7D6CD9832A169E9D4044A52172F72EC4372D8D9CA5AF728751`.
The previous executable is backed up at
`artifacts/vod-seek-speed/installed-before.exe`, with its original hash verified.
