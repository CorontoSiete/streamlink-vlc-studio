# Saved VOD opening latency (2026-09-26)

The delay was reproduced on Twitch archive `2875113048` at the saved position
`05:36:16.717`, using the installed VLC 3.0.23, production native chat overlay,
direct provider resolver, playlist handoff and muted-segment gateway.

## Change and evidence

Completed Twitch MPEG-TS playlists can now open through VLC's FFmpeg demuxer.
The existing loopback gateway transports every validated segment for these
inputs, because this VLC build's bundled FFmpeg cannot fetch HTTPS itself.
Ordinary segments retain their bytes; muted segments retain their existing
timestamp repair. Growing playlists, other formats, other VLC versions and
ordinary live playback retain their existing selection.

A direct demuxer switch was insufficient: FFmpeg's HLS reader can skip to the
next keyframe after a seek. The native replay filter now seeks earlier by the
longest inspected segment duration and tells VLC to decode and discard up to
the requested timestamp. This lets the opening seek perform the necessary
preroll once. The engine still requires real playback-clock progress and a
per-input filter acknowledgement before releasing its black video/audio gate.
It does not accept the immediate seek-time echo as proof of playback.

The filter uses the public VLC 3.0.23 demux and output interfaces. Relevant
upstream implementations are [VLC's FFmpeg demuxer](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/avformat/demux.c),
[FFmpeg's HLS reader](https://github.com/FFmpeg/FFmpeg/blob/n4.4.6/libavformat/hls.c)
and [VLC's adaptive input](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/PlaylistManager.cpp).

Inspection rejects incomplete, encrypted, discontinuous, fragmented-MP4,
byte-range, master and unrecognized playlists. Segment durations must be
positive and at most 30 seconds, with a valid target duration. A failed precise
open retries through the adaptive demuxer. Cancellation and replacement inputs
cannot trigger a stale retry. Releasing a source closes its player connections
and cancels its upstream requests before joining FFmpeg's input thread. Moving
the video surface reopens the corrected input with a fresh transport lease.
Long VODs retain the existing absolute timeline and timestamp-rollover adapter.

## Measurement method

The benchmark includes fresh provider authorization/resolution, playlist
preparation, player creation, confirmed restore and a subsequent displayed
picture. It excludes UI navigation and closing an existing tab. Each trial
also checks absolute position, full duration, five seconds of advancing video
and clock, and bounded stop time. All playback measurements run sequentially.

Initial three-trial measurements (milliseconds):

| Path | Trial 1 | Trial 2 | Trial 3 | Mean |
| --- | ---: | ---: | ---: | ---: |
| Original adaptive | 3817 | 3193 | 3054 | 3355 |
| Corrected FFmpeg | 3391 | 2770 | 2678 | 2946 |

These runs saved about 0.41 seconds (12%). Startup still takes seconds and
depends on the CDN, segment/keyframe position and decoding cost. Earlier faster
experimental numbers that lacked the precise preroll correction are excluded.
Neither the network buffer nor the output-confirmation tolerance was reduced.
All-position timeline rebasing, speculative segment caching and earlier output
gate release were investigated and rejected when they failed performance or
correctness checks.

The final same-build comparison used two groups of three opens per path in
adaptive / updated / updated / adaptive order. All twelve opens passed the
position, duration, sustained output and stop assertions:

| Path | Six measured opens (ms) | Mean |
| --- | --- | ---: |
| Original adaptive | 4060, 3429, 3602, 4104, 3341, 3413 | 3658 ms |
| Corrected FFmpeg | 3224, 2668, 2680, 3208, 3689, 2688 | 3026 ms |

This comparison saved **632 ms, or 17.3%**, on average. The updated path's
3689 ms trial includes a 1344 ms provider-resolution response; it is retained
in the average. Stop took at most 39 ms across these twelve trials.

Logs and comparison scripts are in the ignored `artifacts/vod-resume-latency`
directory. Build the Release test executable and set:

```powershell
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_TEST_STREAMLINK_PATH = 'C:\Program Files\Streamlink\bin\streamlink.exe'
$env:SVS_TEST_STARTUP_VOD_ID = '2875113048'
$env:SVS_TEST_STARTUP_POSITION = '05:36:16.717'
$env:SVS_TEST_FILTER = 'VOD startup: provider resolution to presented output at bookmark'
$env:SVS_TEST_TIMEOUT_SECONDS = '120'
```

`SVS_TEST_STARTUP_ADAPTIVE_ONLY=1` selects the original demuxer in the test
gateway; `0` uses production selection. Each invocation makes three opens.
This switch is confined to the test harness.

## Regression coverage

The `VOD fast resume` group covers policy and version limits, unchanged ordinary
bytes, muted repairs, unsafe URLs, opt-in behavior, source revocation, upstream
cancellation, timeline ownership, first presented pixels, long GOPs, buffered
pause, seeking, window moves, completion, missing-filter fallback, stalled
native reads and a generated 28-hour timeline crossing the actual 33-bit wrap.
The long-GOP fixture resumes in green at 35.25 seconds; the next keyframe at
40 seconds is blue, so the uncorrected FFmpeg seek cannot pass that pixel check.

The checked-in native DLL reproduces byte for byte using the documented build
toolchain and the same output filename. Its SHA-256 is
`EEA562FD4E3FCF41B8B47B5B962C95092780778993396AF71C0AE35F75F511E2`.

The Release solution build passed with warnings treated as errors. The complete
native-enabled run executed 1426 tests: 1424 passed, two failed, none timed out
and none were skipped. All 13 new fast-resume regressions passed. The two failures
were `Twitch behind-live native replay overlay scrolls anchors and resets after
seek` and `resuming after pausing while behind live holds the rewound position`.
Both use simulated playback and both passed in separate fresh-process reruns;
their assertions were not changed for this task.

The repository-wide formatting check also reports existing whitespace and line
ending issues in other working-tree files, including the overlay-rendering
catalog. Those existing edits are preserved. Formatting issues introduced by
this change were corrected; scoped whitespace verification for every changed
C# file passed, as did `git diff --check`.

## Installed build

The self-contained Windows x64 publish completed with warnings treated as
errors. The executable was installed atomically at
`C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe`, using the
standard administrator elevation required by that directory. Its hash matches
the published executable:

`5D5C802B73CD465A0696AC4B27CE26B586D616449CDED6A9C1E0E6F669867927`.

The verified previous executable is preserved at
`artifacts/vod-resume-latency/installed-before.exe`, with SHA-256
`05BD2A79F886A24B61D1879E6407411903839B628D1E03C95255F46216D8841E`.
The benchmark and installation do not write settings or VOD playback history.
