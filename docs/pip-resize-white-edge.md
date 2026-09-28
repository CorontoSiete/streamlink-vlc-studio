# PiP resize white edge

The original window reproduced a pure-white outer pixel column at every tested
width from 630 through 646 pixels, with the top bar both shown and hidden. The
native video surface already reached the window's right edge exactly. A black
surface without VLC also reproduced it, identifying the extended Windows glass
frame as the source rather than video size rounding. The before captures and
bounds are in `.tmp/pip-edge/before/` and `.tmp/pip-edge/baseline.log`.

## Change

PiP now supplies its full client rectangle through `WM_NCCALCSIZE` instead of
using `WindowChrome` to extend a glass frame over native video. This also avoids
the per-resize clipping regions used by `WindowChrome` with zero glass margins.
The existing native resize styles, edge and corner hit tests, caption dragging,
and client input for title-bar buttons remain in use.

The new handler retains WPF's `WVR_REDRAW` resize behavior. Preserving the whole
parent bitmap instead produced three blank GDI frames in a 269-frame capture;
the native video host already preserves its own pixels separately. Activation
also uses `DefWindowProc` with `WM_NCACTIVATE`'s no-repaint parameter, preventing
Windows from drawing a legacy border when PiP loses focus.

References: [WM_NCCALCSIZE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-nccalcsize),
[WM_NCACTIVATE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-ncactivate),
and [WPF's WindowChromeWorker](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Shell/WindowChromeWorker.cs).

## Regression coverage

- Actual pixels on all four edges at consecutive window widths, growing and
  shrinking, with both top-bar settings. A white window underneath exposes
  transparent gaps; alternating focus and changing the title exercises native
  frame repaints.
- Continuous GDI and Direct3D11 video captures while resizing, checking both
  visible video and unexpected white edge pixels.
- Physical drags from all eight edges/corners over actual VLC child windows,
  with an additional capture check after each drag.
- Native resize behavior across hidden chrome, fixed-size, maximized, and
  fullscreen states. Existing tests now inspect actual hit-test results rather
  than relying on `WindowChrome` property values.

Run the focused checks on an unlocked Windows desktop:

```powershell
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_TEST_VLC_MEDIA = 'C:\absolute\path\steady-bright-video.mp4'
$env:SVS_TEST_ARTIFACT_DIR = Join-Path $PWD '.tmp\pip-edge\captures'
$env:SVS_TEST_TIMEOUT_SECONDS = '90'
.\scripts\dev.ps1 Test -Filter 'picture-in-picture resize' -Interactive -ExpectedMaxSkips 0
```

## Local verification

On Windows 10 build 19044 with the installed VLC GDI and Direct3D11 renderers:

- 68 edge captures passed with no bright borders or transparent gaps, including
  title changes after focus switches (`.tmp/pip-edge/title-focus-edges.log`).
- 1,217 continuous playback frames had zero blank frames and zero white edges
  (`.tmp/pip-edge/verified-resize-regression.log`). All 12 resize cases passed
  across the focused runs. The batch's bare-window physical drag case was
  initially skipped because Windows denied foreground activation; its isolated
  rerun passed with zero skips (`.tmp/pip-edge/physical-isolated.log`).
- All 73 related PiP, detached-window, input, overlay, fullscreen, and window
  sharing checks passed with zero skips (`.tmp/pip-edge/related-verified.log`).
  That run used the same Windows compatibility manifest as the application;
  an earlier diagnostic host omitted it and could not create transparent child
  overlays.
- The Release solution build and whitespace verification completed with zero
  warnings or errors. Captures are in `.tmp/pip-edge/verified/`.
