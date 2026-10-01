# Volume indicator and seekbar placement

The volume popup now follows the measured space occupied by the seek controls on the same video target. The original lookup inspected the video's immediate parent for a sibling seekbar. The reusable video surface is nested inside a `VideoSurfacePresenter`, so that lookup missed the controls. It also failed to reposition an existing popup when the seekbar opened or closed.

The original desktop regression reproduced the overlap before changes: the volume popup occupied screen Y=499–534 while the seek controls occupied Y=470–544. The seekbar now publishes its measured bottom reservation on its placement target. The volume popup observes that value until it closes. Hiding, unloading, retargeting, or destroying the native target releases the reservation. Finished VODs use the same per-tab WPF screen for both controls in main and detached windows.

Validation used the pinned .NET SDK 10.0.302 on Windows:

- Release solution build with warnings treated as errors: no warnings or errors.
- Seven focused volume-popup desktop tests passed without skips. They cover an already-open popup when controls appear, normal and compact sizing, target changes, unloading, tiny video bounds, main fullscreen, and completed VODs in both windows.
- Fifty-five distinct related checks passed across focused runs. These include volume wheel routing, keyboard volume changes, theme updates, popup cleanup, physical seek input, hover previews, and native child-window placement.
- Actual VLC Direct3D11 and GDI composition/input tests passed using the documented steady blue video fixture. Each verified 60 composed hover frames with physical input and polling enabled.
- Whitespace verification of the changed code and `git diff --check` passed.

The wider run exposed an existing compact-layout assertion expecting a two-column span despite the current three-column XAML. The same failure was reproduced with a separately compiled copy of the original controls. The assertion now verifies that the action buttons span the complete transport row. Five physical-input checks that Windows skipped for foreground activation in a shared run were rerun individually; each passed without skips.

The layout checks compare actual desktop popup bounds against the seekbar's rendered bounds and keep the same popup open through visibility and size changes. A 640×360 video retains a 26-pixel gap; a 196×126 compact video retains a 6-pixel gap. The popup scales to fit the remaining space on short videos.

Rendered screenshots were inspected and are retained here:

- [Normal video](measurements/volume-seek-overlap-2026-10-01/volume-seek-presented-reveal.png)
- [Smallest compact layout](measurements/volume-seek-overlap-2026-10-01/volume-seek-presented-196x126.png)
- [Main fullscreen](measurements/volume-seek-overlap-2026-10-01/volume-seek-main-fullscreen.png)
- [Finished VOD in picture-in-picture](measurements/volume-seek-overlap-2026-10-01/volume-seek-finished-detached.png)
- [Verified test names](measurements/volume-seek-overlap-2026-10-01/passed-tests.txt)

Reproduce the focused layout checks with:

```powershell
$env:SVS_TEST_ARTIFACT_DIR = Join-Path $PWD '.tmp\volume-seek-overlap\after'
.\scripts\dev.ps1 Test -Filter 'volume popup' -Interactive
```

The optional native VLC fixture setup is documented in the README. Local build, regression, baseline, and native-renderer logs are retained in `.tmp/volume-seek-overlap`.
