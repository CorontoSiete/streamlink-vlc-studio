# First backward skip near the live replay edge

## Diagnosis

The production log recorded a prepared first seek taking **11,658 ms**. Another
hit the existing 15-second seek timeout, reopened the input, and took **18,174 ms**
overall. Replay URL resolution took 0–1 ms and cleanup took 33–36 ms. The replay
was already prepared; moving more initialization ahead of the click did not
address this failure.

Twitch's growing broadcast recording can lag behind the wall-clock live edge.
Consequently, a 30-second backward skip can land inside the newest published
HLS segment. VLC 3.0.23's adaptive demuxer asks whether any *later* segment is
available before decoding, even when the current chunk or source buffer still
contains the requested video. It suspends until the playlist grows. A paused
prepared input can also have an older playlist and select the wrong last segment
unless it refreshes before seeking into appended media.

A local 60-second MPEG-TS EVENT fixture reproduced both the prepared and cold
open timing out at 53.75 seconds, despite all required bytes already being on
the server. Upstream code confirming the cause:

- [Adaptive buffering guard](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/Streams.cpp)
- [Segment selection and availability](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/SegmentTracker.cpp)
- [Ahead-time calculation](https://github.com/videolan/vlc/blob/3.0.23/modules/demux/adaptive/playlist/SegmentList.cpp)

## Correction

The bundled `studio_adaptive` module makes two changes: consume the current
chunk/source buffer before waiting for a later segment, and refresh the live
playlist before choosing a seek position past its known range. The pause filter
recognizes this module, so the prepared input and in-place pause/resume continue
to work. Both prepared and cold inputs select the same corrected demuxer.

Selection is explicit and limited to inspected, unencrypted MPEG-TS EVENT
replays on VLC 3.0.23. Unsupported playlists and VLC versions keep their existing
modules. The source gateway retains direct URLs and muted-segment proxy leases.
The module has zero automatic-selection priority and does not modify installed
VLC files. Seek targets, output gates, cache settings and timeouts are unchanged.

The complete build inputs and readable upstream patch ship under
`native/adaptive-replay`. The source archive is pinned by SHA-256, and the build
checks that the module needs only VLC and Windows runtime DLLs.

## Measurements and regression coverage

The original native probe against the same growing broadcast took 12,739 and
11,622 ms. After the fix, three probes, each holding preparation paused for
12 seconds before seeking into the newest available segment, confirmed in
**1,599 / 931 / 906 ms**. These are measured CDN runs, not latency guarantees.
The integrated application engine, including the production source gateway,
confirmed an actual near-edge broadcast seek in **1,624 ms**.

The integrated application engine confirmed the local first skip in **1,269 ms**
and the cold open in **1,999 ms**. A presentation callback verified the first
nonblack frame at 53.75 seconds was the requested blue frame in **1,022 ms**,
without showing the red or green preroll. A paused 40-second playlist was grown
to 60 seconds immediately before activation; the same input reached 53.75
seconds and produced advancing video in **1,893 ms**.
The real skip command/tab handoff completed in **879 ms**.

Native regressions additionally exercise the real skip command/tab handoff,
playback continuing after waiting at the available edge, subsequent seeks,
pause/resume, volume and mute changes while output is gated, surface rebinding,
cancellation, replacement and stop. Source-policy tests cover supported and
unsupported formats, completed playlists, failed inspection and proxy disposal.

Build the test project in Release, then run its executable with
`SVS_TEST_VLC_DIRECTORY=C:\Program Files\VideoLAN\VLC`,
`SVS_TEST_TIMEOUT_SECONDS=90`, and `SVS_TEST_FILTER=live first seek:`.
`SVS_TEST_FILTER=live replay demuxer:` selects the source-policy tests.
Set `SVS_TEST_LIVE_FIRST_SEEK_URI` to a growing broadcast media playlist to
exercise the actual CDN through the production source gateway.

Diagnostic evidence and test logs are retained under the ignored directory
`artifacts/live-skip-first`.

## Final validation and installed build

The Release build with warnings treated as errors completed with zero warnings
and errors. Formatting validation and `git diff --check` passed. Two independent
native source/object builds produced the same module SHA-256:
`1A7F6B52D07B8F3AB71BA0A1779E96FA16C615A11D83988D93B405995C1FA0DD`.

The full native-enabled headless suite completed successfully: **1,307 passed,
263 skipped**, with no failed or timed-out tests. Run it with the VLC and timeout
environment variables above, without `SVS_TEST_FILTER`, using
`scripts/dev.ps1 Test -NoBuild -ExpectedMaxSkips 263`. The initial full run passed
the same 1,307 individual tests but exceeded the default skip ceiling of 252;
the final run explicitly allowed the observed 263 skips and returned exit code
zero. Its output is in `artifacts/live-skip-first/full-tests-final.log`.

With the user's administrator approval, the self-contained Windows x64 build
replaced `C:\Program Files\Streamlink VLC Studio\StreamlinkVlcStudio.exe` at
2026-09-26 22:48 EDT. The published and installed executable hashes both equal
`21D6BFCE97925CD4A18B0878795429EF43E2A7D2F65755476A9E9BB7524EB4B2`.
All 16 installed source, build, and notice files also match their repository
copies.

The original executable is preserved at
`artifacts/live-skip-first/installed-backup/StreamlinkVlcStudio.exe`, with verified
SHA-256 `74348454E4DDFCEF4865FCC8FD8A09801C3ACFD14C8B9851805BBED46382EFBC`.
The installation receipt is `artifacts/live-skip-first/installed-fix.json`.
