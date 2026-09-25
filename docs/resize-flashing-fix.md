# Video flashing during window resize

The continuous capture regression reproduced blank frames while a steady local
video was playing: 17 of 408 captured frames with GDI and 2 of 408 with Direct3D11.
It checks the picture during both repeated native size changes and a physical
mouse drag, rather than waiting for the final layout to settle.

Three parts of the window/rendering interaction needed attention:

- `WindowChrome` with `GlassFrameThickness="0"` disables its DWM frame path and
  rebuilds the window clipping region on every size change. A one-DIP frame keeps
  DWM composition active in both the main and picture-in-picture windows.
- WPF's `HwndHost` adds `SWP_NOCOPYBITS`. The native video host now retains valid
  client pixels while keeping WPF's assigned coordinates, sizing, and normal
  invalidation of newly exposed areas.
- The app resized every descendant inside VLC's renderer, including its video
  output HWND. It now sizes the renderer container and leaves the internal
  video geometry to VLC. This avoids resetting Direct3D output between frames.
  Descendants still receive the window styles needed for input and window sharing.

Frame preservation alone did not eliminate the flashing. The final combination
passed a further 1,219 captured frames across GDI, Direct3D11, and GDI with native
chat, with no blank frames. Playback continued during resizing, and native chat
was visibly composed before and after the resize sequence.

Validation also passed all 97 checks across the resize-flashing, responsive
layout, picture-in-picture resize, window-sharing, tab-switching, video-surface,
and replay-seek-overlay groups, with no skips. These include physical edge and
corner drags with both real VLC renderers. The Release solution build completed
with zero warnings and errors, and the modified C# files passed whitespace
verification. Logs and captured frames are in `artifacts/resize-flash/`.

The self-contained Windows build is
`artifacts/resize-flash/app/StreamStudio.exe`.

Primary implementation references:

- [WPF WindowChromeWorker](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Shell/WindowChromeWorker.cs): `_UpdateFrameState`, `_HandleWindowPosChanged`, and `_SetRoundingRegion`.
- [WPF HwndHost](https://source.dot.net/presentationframework/System/Windows/Interop/HwndHost.cs.html): `WndProc` and `OnWindowPositionChanged`.
- [VLC window geometry and presentation](https://github.com/videolan/vlc/blob/3.0.x/modules/video_output/win32/common.c): `CommonManage`, `UpdateRects`, and `CommonDisplay`.

Reproduce the regression with a steady, bright local video:

```powershell
$env:SVS_TEST_FILTER = 'resize flashing'
$env:SVS_TEST_VLC_DIRECTORY = 'C:\Program Files\VideoLAN\VLC'
$env:SVS_TEST_VLC_MEDIA = 'C:\absolute\path\steady-bright-video.mp4'
$env:SVS_TEST_TIMEOUT_SECONDS = '90'
$env:SVS_EXPECTED_MAX_SKIPS = '0'
dotnet test StreamlinkVlcStudio.sln -c Release --no-build --no-restore
```

Run on an unlocked desktop with room for the 1100 by 760 test window. The capture
session and its Direct3D device are created on the same background COM apartment;
capture buffers follow the window's changing content size. The optional
`SVS_TEST_ARTIFACT_DIR` retains the initial and failing frames.
