# Live Twitch/Kick resource use, 2026-09-27

Profiling four real streams found two sources of unnecessary UI work. The hidden
docked chat list still built text and emote visuals as live messages arrived.
Unchanged WPF layout notifications also rediscovered native VLC windows and
notified overlay placement listeners.

The docked list now binds its history only while visible. Its scroll controller
avoids hidden layout work, and reopening the panel restores the current history
and follows the latest message. VideoSurface ignores repeated notifications with
the same bounds, while native child creation, repair, movement and resizing keep
their existing synchronization paths.
The replay preview also updates its native window when its image content changes
size. This makes thumbnail layout independent of unrelated video-window updates.

## Live results

Across three live before/after pairs, median process-tree CPU
decreased 10.4% and managed allocation
decreased 82.4%. Median working set changed by
-155.2 MiB (-8.8%).
The measured private-memory reduction is modest. GPU use and I/O are essentially
unchanged; this is primarily a CPU/allocation improvement in the UI.

Values are median (minimum-maximum); all six completed trials are included.

| Metric | Before | Updated | Median change |
| --- | --- | --- | --- |
| CPU cores, complete observed process tree | 0.817 (0.811-0.840) | 0.732 (0.677-0.831) | -10.4% |
| Managed allocations, MiB/s | 13.185 (9.582-13.797) | 2.322 (2.283-2.338) | -82.4% |
| Private memory, MiB | 2239.8 (2230.8-2323.6) | 2105.9 (2101.0-2112.9) | -6.0% |
| Working set, MiB | 1764.9 (1760.8-1768.8) | 1609.7 (1609.6-1624.7) | -8.8% |
| GPU video decode, % of engine | 37.18 (37.18-38.44) | 37.60 (36.20-38.49) | +1.1% |
| GPU 3D, % of engine | 8.78 (8.74-9.20) | 8.42 (7.42-10.63) | -4.1% |
| Process I/O read, MiB per approximately 60 seconds | 1140.6 (1115.6-1151.6) | 1140.2 (1130.3-1144.0) | -0.0% |
| Process I/O write, MiB per approximately 60 seconds | 1140.6 (1115.6-1151.5) | 1140.2 (1130.3-1144.0) | -0.0% |

| Pair | Version | CPU cores | Allocated MiB/s | Gen 0 / 1 / 2 collections | Displayed fps, each stream | Lost pictures, each stream |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | before | 0.811 | 13.797 | 34 / 22 / 17 | 60.01, 60.11, 60.23, 59.96 | 0, 0, 0, 0 |
| 1 | after | 0.732 | 2.283 | 8 / 3 / 1 | 59.92, 60.17, 59.87, 59.88 | 0, 0, 0, 0 |
| 2 | before | 0.817 | 9.582 | 35 / 20 / 17 | 59.98, 60.06, 59.96, 59.96 | 0, 0, 0, 0 |
| 2 | after | 0.677 | 2.322 | 8 / 3 / 1 | 60.08, 59.96, 59.96, 59.96 | 0, 0, 0, 0 |
| 3 | before | 0.840 | 13.185 | 38 / 24 / 19 | 60.01, 59.91, 59.95, 59.96 | 0, 0, 0, 0 |
| 3 | after | 0.831 | 2.338 | 8 / 2 / 1 | 59.90, 60.06, 59.90, 58.08 | 0, 0, 0, 45 |

Streams in the frame columns are eslcs, valorant, cuffem, and deenthegreat.
Both versions decoded 1920x1080 on every stream. The baseline displayed
43,222 pictures and reported 0 lost pictures;
the updated build displayed 43,078 and reported 45 lost.
There were 0 lost audio buffers before and 0 after.
The updated third trial reported 45 lost pictures on deenthegreat (58.08
displayed fps); all other stream/trial combinations were approximately 60 fps
with zero reported loss. Its one-second counters show five-picture bursts at
roughly six-second intervals. The available counters and application logs do
not establish their cause. These measurements do not demonstrate an improvement
in frame drops, and that unfavorable trial remains in all reported aggregates.

Observed process counts ranged from 21
to 21 during measurement (host plus transport/chat descendants).
All six runs passed child-process cleanup. Native chat was
current on all four tabs, and post-trial captures were visually inspected. Source
content changes during testing, including break screens and a dark camera scene;
displayed-frame counters do not imply every frame is visually distinct. Hidden
docked chat realized 14-17
rows before and zero after in the live runs.

## Measurement method

The baseline is the existing dirty working tree captured at
`2026-09-27T09:02:45.062076+00:00`, at Git revision
`12e0adbb8476aa3ede6353d1be7701f47761a358`. Its 1,187 original files were preserved
before editing. Existing optimizations and unrelated changes are part of both
versions. This comparison is separate from the earlier synthetic multistream
measurements on the same date.

Three pairs of fresh Release processes run in this order: before/after,
after/before, before/after. Each has 20 seconds of warmup followed by 60 seconds
of measurements. Builds, other playback tests and profilers are stopped during
these trials. Screenshots are captured after the timed interval.

The four public broadcasts were live at the time of testing:

- Twitch: `eslcs`, `valorant`.
- Kick: `cuffem`, `deenthegreat`.

The test opens the production WPF window at 1360x900, with the multistream grid,
native chat overlays, production Streamlink/libVLC, replay resolution, VOD chat
provider, muted-VOD gateway, viewer counts and stream metadata services. Test
settings are in memory; ordinary account, updater and library startup is excluded
from both versions. No chat messages are sent. Both versions request `best`,
resolve 1920x1080 video at approximately 60 displayed fps, retain default latency
and buffering, and select the fourth stream under the automatic audio policy,
automatically muting the other three. There is no
quality, frame-rate, history, chat or background-playback reduction.
The recorded audio-selection field describes automatic muting; manual mute
state was not logged. Audio decoding and lost-buffer counters were checked,
but these runs do not verify audible speaker output.

Both versions use the identical test assembly and infrastructure assembly. The
only production changes are MainWindow.xaml, its docked chat scroll controller,
VideoSurface.cs, and ReplaySeekOverlay.Preview.cs. Every observed child process must exit when its test window
closes. Unrelated preexisting Streamlink processes are left untouched and excluded
from the counters.

CPU is process-tree CPU seconds divided by measured wall seconds, expressed in
CPU cores. Private bytes and working sets are per-trial means of one-second
process-tree samples; tables compare the medians of those trial means. Managed
allocation is the host's GC allocation delta divided by elapsed time. GPU values
sum this tree's PDH instances by engine type. Process I/O includes named pipes
and other device operations; it is not physical disk traffic. The counter sampler
itself allocates memory in both versions, including enumerating GPU instances.

The machine is an i9-13900K with 64 GiB RAM and an RTX 4080, Windows 10 IoT
Enterprise LTSC build 19044, NVIDIA driver 32.0.16.1656, .NET SDK 10.0.302,
VLC 3.0.23, and Streamlink 8.5.0. The host inherits affinity `FFFF00` and reports
16 available CPUs; WMI reports 24 machine logical CPUs. Neither build changes
affinity. CPU results are not normalized to a potentially misleading machine
percentage.

Live content and chat differ between trials, and three short pairs do not
establish a guaranteed CPU or RAM saving for other workloads. The removed work
is also checked with deterministic UI regressions. Four distinct streams were
tested; this report makes no claim about eight or sixteen simultaneous streams.

## Profiling and regressions

A separate EventPipe allocation trace found WPF inline/text formatting and emote
decoding below DockedChatMessageTextBlock, and StringBuilder/character arrays
below VideoSurface.IsLikelyVlcRendererWindow during unchanged layout. Native
thread sampling showed video-output work in swscale, DXVA2 readback and GDI
presentation. Those diagnostic runs are excluded from performance comparisons.

The first candidate exposed an existing dependency in thumbnail layout: its
native window only reached the image's new height after an unrelated video
layout notification. The baseline passed the existing thumbnail stability test;
the first candidate failed it and both real-VLC preview composition tests. The
final implementation handles the preview image frame's own SizeChanged event.
All 36 replay-control tests then passed, including 60 continuously captured hover
frames over each of the real GDI and Direct3D11 renderers. The final performance
matrix was collected only after that fix. The first candidate's measurements and
failure logs are retained separately and are not used for the final percentages.

In a separate 20-second allocation trace of the first candidate, no sampled stacks included
DockedChatMessageTextBlock, AnimatedEmoteImage, or
VideoSurface.IsLikelyVlcRendererWindow. All three paths appeared in the baseline
trace. The initial trace baseline predates adding VOD-chat and muted-VOD services
to the common harness. Trace totals locate call paths and are not used for the
formal percentage comparisons. The final performance comparison uses fresh
trials of the corrected build.

The new UI regressions fail on the baseline and pass after the change:

| Check | Baseline | Updated |
| --- | --- | --- |
| Hidden docked chat with 100 retained messages | 100 bound items, 21 realized rows | 0 bound items, 0 realized rows |
| 100 unchanged native host layouts | 100 bounds notifications, 503,200 allocated bytes | 0 notifications, 45,600 allocated bytes |

Reopening chat after adding and evicting messages while hidden restores the
latest 100 messages and the bottom scroll position. Actual host resizing and a
newly created native child still synchronize their bounds. The allocation counts
above are diagnostic observations, not brittle test thresholds.

## Verification and remaining limitation

The final `scripts/dev.ps1 Check` completed successfully: **1,293 tests passed,
254 expected interactive tests skipped**. The Release build had zero warnings
and zero errors. Interactive checks were run separately, as detailed below.

The Release build treats warnings as errors. The repository Check includes a
locked restore, C# formatting, PowerShell syntax/tooling contracts, native input
hash verification, build and the full headless test suite. Its desktop skip
ceiling increases from 252 to 254 solely for the two new interactive regressions.
Desktop runs use a zero-skip ceiling.

The two new resource regressions passed, as did the 30-test docked-chat group,
five window-sharing tests, 36 replay-control tests, the isolated GDI resize
check and hidden-emote visibility check. Both GDI and Direct3D11 were exercised
with real libVLC playback. The application's final Release DLL, Core,
Infrastructure and test DLL hashes exactly match the final measured build.

Focused coverage includes hidden chat/history restoration, emote visibility,
docked chat toggles and input, real VLC renderer selection, composed window
capture, Home/stream transitions, detaching and reattaching video, resizing,
replay previews and physical seek input, and PiP edges/cursors. The corrected
build also completes two real four-stream open/play/chat/close cycles within
one process, with all observed children exiting after each cycle.

The initial docked-chat run had two cursor-positioning failures, and a combined
resize run was refused foreground activation for its second case. Fresh
baseline/updated cursor checks, the complete 30-test docked group, and the
isolated GDI resize check passed without changing code or weakening assertions.
Those initial logs remain in the archive; their underlying cause is unconfirmed.

One existing GDI PiP resize test remains intermittent. An initial updated run
captured one blank frame out of 322. Separate isolated baseline and updated runs
passed. A subsequent alternating three-pair comparison reproduced one affected
run in each version: one blank frame in 1,632 sampled baseline frames and one in
1,657 updated frames, with no white edges. Both versions passed the other two
runs. These captures are retained. The change does not establish a fix for that
existing rapid-resize issue, and this report does not claim every desktop test
passed on every attempt.

The thumbnail regression was reproduced against the first candidate, fixed in
production code, and all 36 replay-control tests passed afterwards. Its failures
are distinct from the cursor/foreground limitations and the GDI resize issue.

## Evidence and reproduction

- [Per-trial summary and all counter ranges](measurements/live-multistream-2026-09-27/summary.json)
- [Raw results, screenshots, profiles, failure logs and verification logs](measurements/live-multistream-2026-09-27/raw-results.zip)
- [Exact original versions of the four changed production files](measurements/live-multistream-2026-09-27/baseline-production.zip)
- [Reproduction instructions](measurements/live-multistream-2026-09-27/REPRODUCE.md)
- [Artifact SHA-256 manifest](measurements/live-multistream-2026-09-27/archives.json)

The full original working tree and EventPipe traces remain under
`.tmp/live-resource-2026-09-27`. The archived evidence omits provider playlist
URLs and application logs. The baseline manifest and source delta confirm that
all original production files outside the four listed changes remain intact.
