# Streamer tab switch video resize

## Reproduction and cause

`MainViewModel.ApplyVideoLayout` uses a 2-by-2 grid for single-stream playback. The
selected stream spans both rows and columns. Inactive streams retain their WPF
and native VLC windows, but were reset to a 1-by-1 placement while hidden.

A regression test observes synchronous Win32 visibility and position messages
on the actual `VideoSurface` HWNDs in `MainWindow`. Before the fix, switching back
to an existing tab exposed a **550-by-309** window before resizing it to
**1100-by-618**, at the same screen origin. Checking only the size after WPF's
layout completes would miss this intermediate state.

## Change

Inactive tabs now span the full current grid while hidden. Their video surfaces
keep the single-stream allocation needed when selected, eliminating the
quarter-area intermediate surface. Visible multi-stream placements still come
from `VideoGridLayoutCalculator`.

## Regression coverage

- Synchronous native bounds during repeated tab switches at three window sizes.
- Switching between streams with different aspect ratios.
- Returning to a mounted stream from Home.
- Actual VLC GDI and Direct3D11 playback, including pause/resume, unchanged HWNDs,
  visible video pixels, and captured frames after switching.

Run the focused tests with `SVS_TEST_FILTER=tab switching`. The real VLC cases
also require `SVS_TEST_VLC_DIRECTORY` and `SVS_TEST_VLC_MEDIA` (a bright, steady
video fixture). Set `SVS_TEST_ARTIFACT_DIR` to save the captured frames.

Local verification logs are in `.tmp/tab-switch-before.log`,
`.tmp/tab-switch-vlc.log`, `.tmp/tab-switch-build.log`, and
`.tmp/tab-switch-full-tests.log`. Video captures are in `artifacts/tab-switch`.

The Release solution build completed with zero warnings or errors. All nine
focused tab-switch tests passed, including the two real VLC renderer cases.
The full suite completed with 1,131 passed, one failed, zero timed out, and zero
skipped. The failure was `replay keyboard slider commit seeks from final slider
value`, which uses a standalone `StreamTabViewModel` and does not exercise the
changed `MainViewModel` layout path. It passed in a separate fresh-process run
(`.tmp/tab-switch-replay-check.log`). The full run is not reported as all green.
