# Replay resource usage — September 24, 2026

## Measured result

The replay chat renderer uses **68.4% less process CPU time** and allocates **69.7%
fewer managed bytes** in the controlled four-context animation benchmark below.
These are renderer measurements, not a prediction of total app CPU savings.
The benefit depends on how many replay overlays and animated emotes are visible.

| Measurement, median of three Release runs | Original | Updated |
| --- | ---: | ---: |
| Process CPU time | 17.609 seconds | 5.563 seconds |
| Wall time | 15.301 seconds | 4.310 seconds |
| Managed allocation volume | 2,359,186,912 bytes | 714,781,952 bytes |
| Encoded output | 700,843,200 bytes | 700,843,200 bytes |

The fixture uses four independent render contexts, 100 cached messages with
animated emotes, a 500×292 overlay at the 1080p video scale, 80 warmup frames and
1,200 measured frames. Each context advances its animation clock by 20 ms per
frame. All images are local cached fixtures; no network requests are needed.
Measurements were taken on this Windows machine using Release builds and the
repository's pinned SDK. The original renderer was rebuilt in an isolated copy
under `.tmp/cpu-optimization/baseline`; production sources were not swapped during
measurement. Allocation volume measures all bytes allocated during the run, not
resident memory.

Sample output frame hashes match across all six original/final runs, including
both animation frames:

- `166039A7FD19A1E25ED2C19A1D5BB0B2694B32F1B2C7EEE633B600FF88A5A632`
- `29AE728A7B05AACB032D3CCC8E920C8C4C35757EA39E1D0B90CF16F37E350348`

The original CPU times were 17.641, 17.469 and 17.609 seconds; final CPU times were
5.609, 5.516 and 5.563 seconds. Raw logs and the JSON comparison are retained in
`.tmp/cpu-optimization/`, with final measurements named `optimized-final-trial-*`.
Earlier experiments in that directory are not the final results.

## Changes

- Keep unchanged WPF chat rows attached to their existing parent between frames.
  Previously every emote tick removed and reinserted all visible rows.
- Reuse measured row heights only while WPF measurement remains valid and width,
  presentation and margins still match. Content invalidation and the existing
  bounded row cache also invalidate the measurements. Accounting for margins is
  essential: before/after frame hashing caught a spacing difference in an initial
  implementation, which was corrected before delivery.
- Reuse each renderer's bitmap at the same dimensions, clearing it before every
  render so removed messages and transparent emote regions cannot leave trails.
- Copy pixels directly into the final protocol message, eliminating the separate
  temporary pixel buffer. Convert channels in place, using vector operations for
  opaque/transparent runs and a shared 64 KiB exact integer lookup table for
  partially transparent pixels. The scalar fallback preserves the same rounding.
- Stop automatic WPF emote timers when an image or its parent is hidden. Restart
  when shown. Explicit offscreen replay animation remains driven by its existing
  animation clock. The new regression failed on the original control because
  hidden images kept changing frames.

Stream quality selection, resolution, bitrate, frame rate, decoding options,
audio, animation delays, high-quality scaling and text rendering settings are
unchanged. No stream is paused or downscaled to obtain these savings.

## Verification

The resource regressions cover every alpha/channel value, vector boundaries,
unaligned protocol offsets, the scalar fallback, animation, scrolling, changing
message lists, font/size changes, content invalidation, transparent clearing, and
hiding/showing/unloading animated controls. They compare the cached layout with
an independently remeasured layout and compare conversion with the original
integer formula.

The native window-sharing integration test now waits, with its existing bounded
timeout, for WinGDI's actual renderer title after a rebind. VLC can notify the
application before replacing its temporary `VLC Video Output` title. The test
still requires WinGDI and visible overlay pixels after detach and reattach.

Final verification:

- Release solution build with warnings treated as errors: passed, zero warnings.
- Full non-desktop run: 746 passed, 208 desktop tests skipped, zero failures.
- Focused native replay-overlay tests on the desktop: 40 passed, zero skips.
- Focused emote tests, including actual GIF/WebP pixel fixtures and hidden-image
  animation: 32 passed, zero skips.
- Actual VLC/window-sharing tests: 5 passed, zero skips, including overlay pixels
  after detach/reattach and whole-window capture through navigation and resizing.
- New resource regressions: 3 passed. Exhaustive pixel conversion also passed in
  a separate process with hardware intrinsics disabled.
- Self-contained, single-file application publish with warnings treated as
  errors: passed. The executable is `artifacts/resource-usage/StreamStudio.exe`.

These focused counts overlap the full suite. An earlier full run had one failure
in an existing fake-clock backward-seek test; its isolated rerun and the complete
final run both passed. The first native rebind checks sampled VLC's temporary
window title; the bounded readiness check above fixed that test race without
changing playback behavior.

## Reproduce the renderer benchmark

Build the test project in Release, then run its executable with:

```powershell
$env:SVS_RESOURCE_BENCHMARK = '1'
$env:SVS_TEST_FILTER = 'resource benchmark'
$env:SVS_TEST_TIMEOUT_SECONDS = '120'
dotnet tests/StreamlinkVlcStudio.Tests/bin/Release/net10.0-windows10.0.19041.0/StreamlinkVlcStudio.Tests.dll
```

Run in separate processes without other builds or benchmarks competing for CPU.
Timing is diagnostic, not a hard CI pass/fail threshold. Use
`SVS_TEST_FILTER='resource usage'` for the correctness regressions. Clear these
environment variables before running the complete suite.

## Playback investigation

Four local 1920×1080 H.264 streams at 60 fps played through VLC's existing GDI
path without reported lost pictures in the initial 12-second sample. Limiting
decoder threads did not give a reliable improvement, so decoder threading was
left unchanged. This playback-only probe is not included in the renderer savings.

Native-overlay GDI playback still uses software decoding because VLC 3's early
subpicture blending cannot draw chat into opaque hardware decoder surfaces. The
existing [window-sharing diagnosis](window-sharing-fix.md) explains the source
evidence and compatibility tests. Working chat and window capture retain their
existing decoder policy.
