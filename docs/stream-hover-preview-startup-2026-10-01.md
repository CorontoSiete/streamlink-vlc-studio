# Uneven hover preview startup, 2026-10-01

`summit1g`'s visible preview now starts in a median **1,275 ms**, down from
**3,350 ms** in three alternating before/after trials, a **61.94%** reduction.
The fix supports the live format observed on that channel and selects its small
preview quality correctly.

## Verified cause

The running app logged `InvalidDataException` followed by Streamlink startup for
`summit1g`, while other previews resolved directly in about 300–500 ms. A fresh
authorization and playlist inspection established two differences:

- The stream publishes `360p30` and `480p30`, while the preview requested only
  `360p,480p,best`. Both the direct selector and Streamlink therefore selected
  `1080p60` through `best`.
- Its selected media playlist contains `#EXT-X-MAP:URI="…mp4"` and two-second MP4
  fragments. The direct preview supported only MPEG-TS, rejected the initialization
  tag, and launched a separate Streamlink transport. No encryption or ad markers
  appeared in this inspected playlist.

The original .NET resolver reproduced the rejection specifically in
`LivePreviewPlaylist.Rewrite`. The original player selected 1080p, failed direct
lookup at 700 ms, opened Streamlink, and delivered its first decoded frame at
3,438 ms. [The rejection log](measurements/hover-preview-startup-2026-10-01/summit1g-rejection.log)
and [the original playback trace](measurements/hover-preview-startup-2026-10-01/before-summit1g-probe.log)
record those stages without playback tokens or signed URLs.

## Change

The preview request now uses
`360p,360p30,360p60,480p,480p30,480p60,best`. The direct selector reads this same
request, and fallback receives it unchanged. Exact qualities and their frame-rate
variants keep their order; each selected stream requires only one media-playlist
lookup.

The playlist validator now accepts unencrypted fragmented MP4 with a whole MP4
initialization file. It validates and resolves the initialization URI, rewrites
it to an absolute approved provider URL, and requires an initialization section
before MP4/M4S fragments. Unsupported tags and unsafe URLs still require fallback.
Each refresh retains the initial initialization URI and container. Changes stop
direct playback before handing the new playlist to VLC.

This follows the initialization rules in
[RFC 8216](https://datatracker.ietf.org/doc/html/rfc8216#section-4.3.2.5).
[VLC 3.0.23's HLS parser](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/hls/playlist/Parser.cpp#L382)
loads `EXT-X-MAP` but retains one initialization segment in a representation,
so changing maps retain Streamlink handling. Ranged initialization sections,
encryption, initial/later Twitch ads, custom transport configuration, cancellation,
and bounded startup also retain their existing handling.

## Measurements

Each trial launches a fresh process, physically hovers the production WPF card,
observes the first bitmap at 100 ms intervals, verifies changing video, then
disables previews and verifies image clearing and cleanup. Order alternates to
limit ordering effects. No builds or other tests ran during these measurements.

| Pair | Order | Original visible frame | Updated visible frame |
| --- | --- | ---: | ---: |
| 1 | Before, after | 3,461 ms | 1,275 ms |
| 2 | After, before | 3,244 ms | 1,275 ms |
| 3 | Before, after | 3,350 ms | 1,282 ms |
| Median | | **3,350 ms** | **1,275 ms** |

[Raw paired results](measurements/hover-preview-startup-2026-10-01/paired-results.json)
and all six `card-*.log` files are saved beside this report. These are local
measurements of the currently live Twitch broadcast, subject to network,
provider, and broadcast changes.

The updated player also ran for **40 seconds**: first decoded frame at 1,418 ms,
634 changing frames, longest presentation gap 158 ms, zero fallback starts, and
76 ms cancellation/cleanup. Its selected quality remained `360p30` across the
playlist reloads. [The continuity trace](measurements/hover-preview-startup-2026-10-01/after-summit1g-probe.log)
records these requests and results.

Both builds used the incoming working tree at Git revision
`933bb5f24c0aafaa336d7227defc6fddc4394ba1`, including its existing local changes,
with .NET SDK 10.0.302, Streamlink 8.4.0, and installed VLC 3.0.23. The baseline
was built and preserved before production edits. DLL hashes are included in the
paired-results JSON. Settings were loaded in memory for tests and were not saved.

## Verification

- Release build with warnings treated as errors: zero warnings/errors.
- Formatting and whitespace checks passed for the changed source files.
- **34 focused tests passed, zero skipped**, including physical card interaction,
  existing TS playback, real native fragmented MP4 decoding, quality names, map
  validation and refreshes, map/container changes, later ads, startup deadlines,
  cancellation and cleanup.
- The two new quality/MP4 regressions fail with the preserved original
  infrastructure DLL and pass with the updated DLL. Their original failures are
  saved as `regression-*-before.log` beside the paired measurements.
- All six live production-card trials passed, verifying changing video and
  cleanup. The updated 40-second continuity run also passed.
- The rebuilt application and infrastructure DLLs have the same SHA-256 hashes
  as the updated build used for the live measurements. The rebuilt app was
  relaunched and is responding. The final automated screenshot capture timed out;
  the six physical card trials above provide the visual playback verification.

Both runs of the standard broader suite completed with **1,481 passed, 1 failed,
269 skipped, zero timed out**. The sole failure is `resuming after pausing while behind live
holds the rewound position`, at its initial `IsReplayMode` assertion before
pause/resume. The preserved original build's full suite passed **1,476 tests,
269 skipped**. Three isolated runs of the rewind case on each build also passed.
To check whether the failure preceded this change, a diagnostic runner repeatedly
invoked the exact test delegate from the preserved original test DLL, using the
original app and infrastructure DLLs. It reproduced the same assertion failure
on iteration 53. [The original-build repetition trace](measurements/hover-preview-startup-2026-10-01/rewind-stress-before.log)
records that result. This confirms an existing intermittent failure; the broader
suite is not claimed to be fully green. Replay behavior and that test were not
changed for this preview fix.
The [broader validation excerpts](measurements/hover-preview-startup-2026-10-01/broader-validation.log)
retain both updated-suite results and the original-build controls.

Run the focused suite using the repository's pinned SDK and writable temp path:

```powershell
.\scripts\dev.ps1 Test -Filter 'stream hover preview:' -Interactive
```

To verify a currently live channel:

```powershell
$env:SVS_TEST_HOVER_CHANNEL = 'https://www.twitch.tv/summit1g'
$env:SVS_TEST_TIMEOUT_SECONDS = '75'
$env:SVS_TEST_HOVER_DURATION_SECONDS = '40'
.\scripts\dev.ps1 Test -Filter 'stream hover preview:' -Interactive
```

Local baseline/new builds, fixture images, and working logs are in
`.tmp/hover-preview-diagnosis-2026-10-01/`.
