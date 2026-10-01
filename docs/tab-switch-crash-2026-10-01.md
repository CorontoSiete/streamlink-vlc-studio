# Streamer tab click crash — 2026-10-01

Windows recorded two StreamStudio crashes at 02:23:36 and 02:24:01 EDT. Both
`.NET Runtime` events (ID 1026) report the same unhandled exception on .NET 10.0.7:

```text
System.InvalidOperationException: 'System.Windows.Documents.Run' is not a Visual or Visual3D.
  System.Windows.Media.VisualTreeHelper.GetParent(DependencyObject reference)
  MainWindow.FindVisualParent<T>(DependencyObject child)       [original line 2862]
  MainWindow.TabContent_PreviewMouseLeftButtonDown(...)        [original line 1378]
```

The crashed executable was the Release output in this workspace. The corresponding
Windows dumps are `StreamStudio.exe.21532.dmp` and `StreamStudio.exe.28484.dmp` in
`%LOCALAPPDATA%\CrashDumps`. The event evidence is preserved in
`.artifacts/tab-switch-crash/windows-events.json`.

`TabTitleTextBox` is an `EmojiTextBlock`. Its renderer creates `Run` content for
ordinary text as well as mixed text/emoji titles. A click on the text therefore
can have a `Run` as `MouseButtonEventArgs.OriginalSource`. Before starting tab
selection or dragging, the tab handler checks for an enclosing close button.
Its old parent finder unconditionally passed that content element to
`VisualTreeHelper.GetParent`, which throws for nonvisual input. The exception
escaped the routed input handler and terminated the application.

The regression test raised the underlying `Mouse.PreviewMouseDown` event from a
`Run` in the actual tab template against the unchanged production source. It
reproduced the same exception, helper, tab handler and WPF reraising stack.
See `.artifacts/tab-switch-crash/reproduced-tests.log`.

The shared helper is now `FindInputAncestor`. It traverses content parents with
`ContentOperations.GetParent` and `FrameworkContentElement.Parent`, then resumes
visual traversal only for `Visual` or `Visual3D`. Logical parents also bridge
content that has not been mounted into a visual template. The tab handler, Home
middle-click resolver, scrollbar guard and text-input focus guard use this
helper. This also preserves close-button detection through nested inline text.

Microsoft documents the applicable contracts in
[VisualTreeHelper.GetParent](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.visualtreehelper.getparent?view=windowsdesktop-10.0)
and [ContentOperations.GetParent](https://learn.microsoft.com/en-us/dotnet/api/system.windows.contentoperations.getparent?view=windowsdesktop-10.0).
The regression uses the tunneling
[Mouse.PreviewMouseDown event](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.mouse.previewmousedown?view=windowsdesktop-10.0),
which WPF reraises as the button-specific event at each element along the route.

Focused reproduction and verification command:

```powershell
.\scripts\dev.ps1 Test -Interactive -Filter 'tab input:'
```

Four content/focus regressions run in headless CI. One regression uses a native
window and physical mouse input; the documented CI/development skip ceiling
increased by one to account for that desktop-only check.

The physical-input test runs in a fresh process, following the runner's existing
keyboard/input test isolation policy. It verifies the native hit target and
`OriginalSource`, selected streamer and tab-strip item, drag capture/release,
returning from Home, grouped tabs, and close-button behavior.

Verification completed:

| Check | Result | Evidence under `.artifacts/tab-switch-crash/` |
| --- | --- | --- |
| Release solution build, warnings treated as errors | 0 warnings, 0 errors | `fixed-tests.log` |
| Final five regressions against the pre-fix app | 5 failed with the expected original behavior | `baseline-crash-tests.log` |
| Final five regressions against the fixed app | 5 passed, 0 skipped | `fixed-tests.log` |
| Same regressions on the installed .NET 10.0.7 runtime | 5 passed, 0 skipped | `installed-runtime-tests.log` |
| Complete headless suite | 1,470 passed, 269 skipped, 0 failed | `full-headless-tests.log` |
| Broader desktop filter `tab` | 139 passed, 2 failed, 0 skipped | `tab-tests-final.log` |
| Real VLC GDI tab switching | Passed, 0 skipped | `vlc-gdi-tests.log` |
| Real VLC Direct3D11 tab switching | Passed, 0 skipped | `vlc-d3d11-tests.log` |
| Changed-source formatting and development-script contracts | Passed | `format-verify.log`, `development-tests.log` |

The native VLC checks used the verified 30-second, 1920×1080, 60 fps uniform-blue
fixture `vlc-blue-1080p60.mp4`, with captured video frames under `vlc-frames/`.
They exercise repeated switching, pause/resume and three native window sizes.

The two broader desktop failures are `responsive compact tabs select and close
the same tab after shrink and grow` (empty reported selector title) and `replay
seek overlay owns native input while volume popup remains routable` (volume OSD
placement). Both fail identically against a separately rebuilt copy of the
working tree using the original `MainWindow.xaml.cs`. That source matches the
saved pre-edit file byte-for-byte, recorded in `baseline-summary.json`; the
corresponding failure logs are `baseline-compact-tests.log` and
`baseline-overlay-tests.log`. These failures predate the crash fix. The broader
desktop run is therefore not reported as entirely passing.
