# Kick speed changes covering the seekbar (2026-09-30)

## Observed reproduction

On September 29, 2026, the production Streamlink service, Kick replay resolver,
and installed libVLC played the live `korekore_ch` broadcast. The native WPF
seekbar was exercised with physical mouse input after seeking approximately
three minutes behind live, in both normal and topmost windows. Only chat was
stubbed; the stream, replay lookup, player, renderer, and controls were real.

After a speed selection, the owner was still active, the controls HWND was
visible, the overlay was open at opacity 1, and both video and overlay bounds
were unchanged. Nevertheless, `VideoSurface.IsOverlayAboveRenderer` returned
false and the native hit at the speed selector belonged to `VLC video main`,
not the controls. After another 2.5 seconds with the pointer stationary, the
overlay closed. The decisive trace is
`artifacts/kick-speed-seekbar/kick-live-native-trace-before.log`.

That broadcast was offline when work resumed on September 30. Subsequent live
checks used the officially confirmed live `classybeef` broadcast, ID
`129844276`, rather than treating yesterday's stream as still live.

## Cause

VLC can raise an existing renderer child above the layered controls when the
HLS input resynchronizes after a rate change. This is a sibling z-order change,
not a resize or newly created renderer. The bounded startup repair stops after
eight seconds, and unchanged native bounds do not invalidate overlay placement.
`Reveal` also previously returned immediately for an already-open overlay.

The pointer sampler then sees the covering video HWND instead of the controls.
It treats the stationary pointer as idle over video and eventually dismisses
the seekbar, even though the pointer has not left the speed selector.

Repairing placement on the existing 100 ms pointer timer prevents that idle
misclassification, but is not sufficient for input: a real live trace caught
the renderer rising between the hit check and mouse-down. The click reached
VLC before the next timer tick. This is recorded in
`artifacts/kick-speed-seekbar/kick-live-native-input-probe-2026-09-30.log`.

## Correction

- `VideoOverlayHost` listens for native `EVENT_OBJECT_REORDER` notifications
  while its controls HWND is open. The hook is restricted to this process and
  filters for its video parent's HWND; delivery occurs on the owning UI thread.
- A reorder invokes the existing cached `ApplyBounds` path. That path raises
  the controls only when an unregistered renderer sibling is actually above
  them, leaving other registered overlays and unchanged bounds alone.
- Closing, disabling, unloading, retargeting, or destroying the host removes
  its native hook before releasing the child source. No closed controls are
  resurrected by a pending notification.
- Pointer polling repairs open placement before native hit classification,
  and repeated reveal activity also validates an already-open overlay.
- Playback-rate behavior, media sources, audio resynchronization, normal idle
  fading, owner deactivation, and minimize handling are unchanged. The fix
  does not force controls permanently visible or make the application topmost.

## Regression coverage

`ApplicationTestCatalog.ReplaySeekOverlayZOrder.cs` creates a real native
renderer child, waits for the eight-second startup repair to finish, opens
the controls, and changes only that existing child's z-order. It verifies
restoration by stationary pointer polling, repeated reveal activity, and
native reorder notification with polling stopped and no subsequent activity.
All cases retain the same controls HWND and identical renderer/overlay bounds.
The notification case also checks hook release when controls are disabled.

`ApplicationTestCatalog.ReplaySeekOverlayKickLive.cs` is explicitly opt-in.
It resolves and decodes a currently live Kick broadcast, seeks behind live,
and physically opens/clicks all seven speed choices five times in normal and
topmost windows. It checks native popup/control hit targets, owner activation,
opacity, the unchanged controls HWND, the actually applied rate index, and
continuing decoded video. It parks the pointer over the speed selector beyond
the idle interval and saves the final desktop image for visual inspection.

The live check waits for the actual decoder clock to pass its recorded HLS
audio-resynchronization target before the next accepted rate selection. A
released UI rate semaphore alone does not establish native clock recovery.
An earlier rapid run hit the existing five-second native rate-recovery guard
after switching to 0.5x; the rate rolled back, but the repaired seekbar remained
visible and input-capable. That separate backend behavior was not changed or
silently counted as an accepted rate change. Its trace is
`artifacts/kick-speed-seekbar/kick-live-event-driven-after-2026-09-30.log`.

## Recorded verification on September 30, 2026

- Native reorder/polling/reveal and hook-cleanup checks: all three passed,
  with zero skips.
- Real live Kick input: both normal/topmost checks passed, with zero skips.
  Each physically selected all seven rates five times: 70 accepted changes
  total, with the same controls HWND throughout each run.
- Normal-window video advanced from 12,325.732 to 12,393.507 seconds and
  displayed pictures increased from 41 to 2,229. Topmost-window video advanced
  from 12,422.481 to 12,486.507 seconds, with pictures increasing from 44 to
  2,478. Both retained visible, input-capable controls beyond the idle interval.
- Both final desktop images were visually inspected: the actual stream and
  seekbar were visible. The log and images are
  `artifacts/kick-speed-seekbar/kick-live-settled-clock-after-2026-09-30.log`
  and `artifacts/kick-speed-seekbar/settled-clock-after-2026-09-30/`.
- Scoped formatter verification passed without changes.
- Final Release solution build passed with warnings treated as errors:
  zero warnings and zero errors. Its trace is
  `artifacts/kick-speed-seekbar/final-release-build-2026-09-30.log`.

The wider suites are not entirely green. The headless run passed 1,310 tests
and intentionally skipped 263 interactive tests, exceeding the initially
configured 256-skip ceiling. Its other failure is the VOD-completion template
test expecting a direct `VideoSurface` child after the existing presenter
refactor. The additional seek-overlay UI run passed 32 tests; compact-grid
column-span and volume-OSD placement assertions failed, and four foreground
input tests could not acquire focus.

To avoid guessing whether these failures were introduced here, a separate
output directory was built using the saved pre-fix overlay sources, without
reverting or changing the working source files. All three failing assertions
reproduced against that baseline with the same causes. The comparison build
and individual traces are `baseline-comparison-build-2026-09-30.log`,
`baseline-pointer-layout-2026-09-30.log`,
`baseline-volume-routing-2026-09-30.log`, and
`baseline-vod-template-2026-09-30.log` under
`artifacts/kick-speed-seekbar/`. Those unrelated failures were left untouched;
none of the focused live/native successes is a skipped test counted as a pass.

## Run the live check

From the repository root, use the project's pinned .NET SDK and installed
Streamlink/VLC, with a channel confirmed live at execution time:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.dotnet-sdk'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:SVS_TEST_FILTER = 'kick live seekbar:'
$env:SVS_TEST_KICK_LIVE_CHANNEL = 'classybeef'
$env:SVS_TEST_STREAMLINK_PATH = 'C:\Users\ComputerGuy\AppData\Local\Programs\Streamlink\bin\streamlink.exe'
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_SKIP_INTERACTIVE_WINDOW_TESTS = 'false'
$env:SVS_EXPECTED_MAX_SKIPS = '0'
$env:SVS_TEST_TIMEOUT_SECONDS = '480'
$env:SVS_TEST_ARTIFACT_DIR = Join-Path $PWD 'artifacts\kick-speed-seekbar\live-check'
& '.\.dotnet-sdk\dotnet.exe' build tests\StreamlinkVlcStudio.Tests\StreamlinkVlcStudio.Tests.csproj -c Debug --no-restore -warnaserror
& '.\.dotnet-sdk\dotnet.exe' tests\StreamlinkVlcStudio.Tests\bin\Debug\net10.0-windows10.0.19041.0\StreamlinkVlcStudio.Tests.dll
```

These tests require an unlocked interactive desktop; an inability to obtain
real foreground input is a skip and fails the explicit zero-skip ceiling.
For the native reorder checks alone, set `SVS_TEST_FILTER` to
`replay seek overlay repairs`.
