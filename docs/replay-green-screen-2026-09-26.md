# Green HLS replay diagnosis and fix

Reproduced on this machine with VLC 3.0.23 and the NVIDIA RTX 4080, using the
production playback engine, bundled chat renderer, and local HLS fixtures.
No account credentials or external stream availability are required to repeat it.

## Evidence

- Before the fix, opening HLS at 45.25 seconds and adopting a prepared live replay
  both displayed solid green (`R=0 G=135 B=0`) instead of the fixture's blue.
  MP4 resume and seeking passed. Playback clocks and displayed-picture counters
  continued advancing in the failing cases.
- Verbose native logs show HLS restarting the decoder and reusing its video
  output. D3D11 hardware decoding starts again, followed by repeated
  `d3d11_filters: Failed to map source surface. (hr=0x887a0005)` errors.
  Microsoft identifies this code as
  [DXGI_ERROR_DEVICE_REMOVED](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-error).
- VLC 3.0.23's [D3D11 surface converter](https://github.com/videolan/vlc/blob/3.0.23/modules/hw/d3d11/d3d11_surface.c)
  caches a staging texture in `assert_staging` without checking whether the
  incoming decoder device changed. The retained converter/new decoder sequence
  in the logs is consistent with that resource-lifetime defect.
- A controlled run removed only the seek-time adjust filter. Initial HLS output
  became blue, but the next backward seek still produced green and the same
  surface-read errors. Removing the filter alone was insufficient.
- With the original gate restored, selecting DXVA2 instead of D3D11VA produced
  the correct blue/red/blue frames in all three routes. Verbose logs confirmed
  **DXVA2 hardware decoding on the RTX 4080** after each HLS decoder restart.
- A separate test held replay output gated after decoded pictures were available.
  The old adjust-based gate showed blue instead of black: VLC could not apply
  that CPU filter to the opaque hardware pictures. Its converter errors explain
  why changing only the decoder would leave preroll visible.

Raw diagnostic logs and screenshots are in `artifacts/replay-green/` (ignored
local artifacts): `app-verbose.log`, `control-no-adjust.log`,
`control-dxva2.log`, `gate-before.log`, and the before/after capture directories.
Verbose VLC error messages also cause MSBuild's log parser to fail the diagnostic
command even when its color assertions pass; final runs restore quiet logging.

## Changes

GDI output selects `dxva2` explicitly. This retains hardware decoding without
selecting VLC's affected D3D11-to-system-memory path. Direct3D11 presentation
retains its existing automatic decoder choice. Older/custom chat plugins retain
their software-decoding policy.

The verified bundled Studio GDI renderer gates final presentation using a unique
event owned by each replay player. It presents black until seek confirmation,
while decoding and the existing audio gate continue normally. Prepared replay
adoption installs the gate before enabling the video track. Stop, failed startup,
replacement, and output recreation keep event ownership tied to the correct
player. The native DLL is rebuilt from source and its manifest hash is updated.

The renderer's event gate is enabled only for the bundled plugin hash whose
capabilities the engine verifies. Legacy/software paths retain their existing
adjust gate.

## Regression checks

`ApplicationTestCatalog.ReplayColor.cs` checks actual presented pixels at nine
points inside the video, using blue and red rather than green as expectations.
It covers MP4, growing HLS, prepared live replay, completed VOD, muted VOD,
backward/forward seeks, paused seeks, and stop/reopen, with both the bundled
overlay and stock GDI. The muted case verifies that the repaired segment was
actually fetched and that its red frames are presented. The gate test separately
checks black output while decoded frames are available, then blue after release.

```powershell
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_TEST_ARTIFACT_DIR = "$PWD/artifacts/replay-green/final"
./scripts/dev.ps1 Test -Filter 'replay color:' -Interactive
```

The original HLS color checks failed before the decoder change, and the gate
check failed before the renderer change. Neither relies on memory video callbacks,
which force a different software-output path and missed the regression.

Validation results:

- Release builds completed with zero warnings/errors.
- All eight new real-output regression cases passed. Their pre-fix failures,
  final color captures, and held/released gate captures are retained locally.
- Full headless suite: 1,181 passed, 246 desktop-dependent skips, zero failures.
- Existing fast-resume suite: 13 passed. Live-first-seek suite: 10 passed.
  Window-sharing suite: 5 passed, including chat after detach/reattach, actual
  GDI/Direct3D11 output selection, and window capture after resize/rebind.
- Native compositor and subpicture pixel, allocation, and ownership checks passed.
  The bundled native dependency hashes verified successfully.
- Four local 1920x1080 H.264/60 fps videos, with chat updated 20 times/second:
  all continued playing, with zero VLC-reported lost pictures over 12 seconds.
  Process CPU time was 8.328 seconds summed across threads. This is a single
  playback-only verification run, not a new comparative CPU benchmark.
  Raw measurements: `artifacts/replay-green/dxva2-playback.json`.

Self-contained local build: `artifacts/replay-green/fixed-app/StreamStudio.exe`.
