using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PictureInPictureContextMenuForeignForegroundTests =>
    [
        ("picture-in-picture context menu activates from a foreign foreground process and physically toggles the top bar",
            PictureInPictureContextMenuForeignForegroundAsync)
    ];

    private static Task PictureInPictureContextMenuForeignForegroundAsync() =>
        WithPictureInPictureContextMenuSurfaceAsync(showTopBar: false, async (_, _, window, surface) =>
        {
            var owner = new WindowInteropHelper(window).Handle;
            Assert.True(WindowInteropHelpers.TryGetMonitorInfo(owner, out var monitor));
            await using var foreign = await PictureInPictureForeignForegroundWindow.CreateAsync(
                monitor.WorkArea.Right - 190, monitor.WorkArea.Top + 60);
            Assert.True(foreign.ProcessId != Environment.ProcessId);
            var overlay = FindVisualDescendants<ReplaySeekOverlay>(window).Single();
            overlay.IsOverlayEnabled = false;
            StopReplayOverlayPointerSampling(overlay);
            var child = NativeWindowTest.CreateVisibleChildWindow(surface.Handle, "button");
            PictureInPictureContextMenuChildProcedure childProcedure = (hwnd, message, wParam, lParam, _, _) =>
                message is 0x0204 or 0x0205 or 0x007B
                    ? IntPtr.Zero
                    : PictureInPictureContextMenuDefSubclassProc(hwnd, message, wParam, lParam);
            var menu = (ContextMenu)window.FindName("VideoContextMenu");
            var item = (MenuItem)window.FindName("ShowTopBarMenuItem");
            var opened = 0;
            var closed = 0;
            var changes = new List<bool>();
            menu.Opened += (_, _) => opened++;
            menu.Closed += (_, _) => closed++;
            window.TopBarVisibilityChanged += (_, shown) => changes.Add(shown);
            try
            {
                Assert.True(PictureInPictureContextMenuSetWindowSubclass(child, childProcedure, new UIntPtr(1), UIntPtr.Zero));
                foreach (var busy in new[] { false, true })
                {
                    Assert.Equal(false, window.IsTopBarShown);
                    foreach (var showTopBar in new[] { true, false })
                    {
                        PumpPictureInPictureResize(window);
                        surface.SyncNativeBounds();
                        var bounds = NativeWindowTest.GetWindowBounds(surface.Handle);
                        NativeWindowTest.SetWindowBounds(child, 0, 0, bounds.Width, bounds.Height);
                        // Background PiP through normal physical activation. Forcing
                        // foreground with attached input queues is not this user gesture.
                        var foreignBounds = NativeWindowTest.GetWindowBounds(foreign.Handle);
                        var foreignX = foreignBounds.Left + foreignBounds.Width / 2;
                        var foreignY = foreignBounds.Top + foreignBounds.Height / 2;
                        Assert.Equal(foreign.Handle, NativeWindowHitTester.Instance.GetRootWindow(
                            NativeWindowHitTester.Instance.WindowFromPoint(foreignX, foreignY)));
                        NativeWindowTest.SetCursorPosition(foreignX, foreignY);
                        await Task.Run(() =>
                        {
                            try
                            {
                                PictureInPictureContextMenuMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                                Thread.Sleep(45);
                            }
                            finally
                            {
                                PictureInPictureContextMenuMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                            }
                        });
                        await TestWait.UntilAsync(() => NativeWindowTest.GetForegroundWindow() == foreign.Handle && !window.IsActive,
                            TimeSpan.FromSeconds(2), "Physical input must activate the foreign process and deactivate PiP before its right click.");
                        var foreground = NativeWindowTest.GetForegroundWindow();
                        Assert.Equal(foreign.Handle, foreground);
                        PictureInPictureContextMenuGetWindowThreadProcessId(foreground, out var foregroundProcess);
                        Assert.Equal((uint)foreign.ProcessId, foregroundProcess);
                        Assert.Equal(false, window.IsActive);
                        var point = VolumeWheelPoint(surface);
                        Assert.Equal(child, NativeWindowHitTester.Instance.WindowFromPoint(point.X, point.Y));
                        NativeWindowTest.SetCursorPosition(point.X, point.Y);
                        await Task.Delay(80);
                        var opensBefore = opened;
                        var closesBefore = closed;
                        var changesBefore = changes.Count;
                        var outside = window.PointToScreen(new Point(-12, -12));
                        await SendPictureInPictureContextMenuRightClickAsync(busy,
                            busy ? () => NativeWindowTest.SetCursorPosition((int)Math.Round(outside.X), (int)Math.Round(outside.Y)) : null);
                        await TestWait.UntilAsync(() => menu.IsOpen, TimeSpan.FromSeconds(2),
                            $"PiP must open its menu over a native child while another process owns the foreground (busy={busy}).");
                        await Task.Delay(200);
                        Assert.True(menu.IsOpen);
                        Assert.Equal(opensBefore + 1, opened);
                        Assert.Equal(closesBefore, closed);
                        Assert.Equal(owner, NativeWindowHitTester.Instance.GetRootOwnerWindow(NativeWindowTest.GetForegroundWindow()));
                        Assert.True(window.IsActive,
                            "The physical right click must activate PiP without the test forcing foreground after injection.");
                        Assert.Equal(!showTopBar, item.IsChecked);
                        // Do not ActivateWindow/RequireForeground here: that would hide
                        // a foreground-lock failure caused by swallowing the native down.
                        await ClickPictureInPictureTopBarMenuItemAsync(menu, item);
                        await TestWait.UntilAsync(() => window.IsTopBarShown == showTopBar && !menu.IsOpen,
                            TimeSpan.FromSeconds(2));
                        await Task.Delay(150);
                        Assert.Equal(opensBefore + 1, opened);
                        Assert.Equal(closesBefore + 1, closed);
                        Assert.Equal(changesBefore + 1, changes.Count);
                        Assert.Equal(showTopBar, changes[^1]);
                    }
                }
            }
            finally
            {
                menu.IsOpen = false;
                PictureInPictureContextMenuRemoveWindowSubclass(child, childProcedure, new UIntPtr(1));
                NativeWindowTest.DestroyWindow(child);
                GC.KeepAlive(childProcedure);
            }
        });

    private sealed class PictureInPictureForeignForegroundWindow(Process process, Task<string> errors) : IAsyncDisposable
    {
        internal IntPtr Handle { get; private set; }
        internal int ProcessId => process.Id;

        internal static async Task<PictureInPictureForeignForegroundWindow> CreateAsync(int left, int top)
        {
            // Windows PowerShell and Windows Forms ship with the Windows desktop.
            // This creates a disposable window in another process without depending
            // on the user's browser, an installed editor, or a separately built exe.
            var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.True(File.Exists(powerShell), "The built-in Windows PowerShell host is missing.");
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.Application]::EnableVisualStyles()
                $window = New-Object System.Windows.Forms.Form
                $window.Text = 'PiP foreign foreground regression'
                $window.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
                $window.Location = New-Object System.Drawing.Point({{left}}, {{top}})
                $window.Size = New-Object System.Drawing.Size(170, 120)
                $window.ShowInTaskbar = $false
                $window.TopMost = $true
                $window.Show()
                [Console]::WriteLine($window.Handle.ToInt64())
                [Console]::Out.Flush()
                [System.Windows.Forms.Application]::Run($window)
                $window.Dispose()
                """;
            var startInfo = new ProcessStartInfo(powerShell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                startInfo.ArgumentList.Add(argument);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not launch the isolated foreign window.");
            var fixture = new PictureInPictureForeignForegroundWindow(process, process.StandardError.ReadToEndAsync());
            try
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value != 0,
                    "The foreign window did not report a valid HWND.");
                fixture.Handle = new IntPtr(value);
                await TestWait.UntilAsync(() => NativeWindowTest.IsWindowVisible(fixture.Handle), TimeSpan.FromSeconds(2));
                PictureInPictureContextMenuGetWindowThreadProcessId(fixture.Handle, out var actualProcess);
                Assert.Equal((uint)process.Id, actualProcess);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    if (Handle != IntPtr.Zero)
                        PictureInPictureContextMenuPostMessage(Handle, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
                    try
                    {
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    catch (TimeoutException)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                }
                var diagnostic = await errors.WaitAsync(TimeSpan.FromSeconds(1));
                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(diagnostic))
                    Console.WriteLine($"Foreign PiP test helper: {diagnostic.Trim()}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    [DllImport("user32", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint PictureInPictureContextMenuGetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PictureInPictureContextMenuPostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
