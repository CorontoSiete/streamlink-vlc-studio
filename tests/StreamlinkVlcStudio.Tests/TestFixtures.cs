
internal sealed class InteractiveDesktopTestSkippedException : Exception
{
    public InteractiveDesktopTestSkippedException()
        : base("interactive desktop unavailable")
    {
    }

    public InteractiveDesktopTestSkippedException(string message)
        : base(message)
    {
    }
}

internal readonly record struct NativeOverlayAlphaBounds(int MinX, int MinY, int MaxX, int MaxY)
{
    public int Width => MaxX >= MinX ? MaxX - MinX + 1 : 0;
    public int Height => MaxY >= MinY ? MaxY - MinY + 1 : 0;
}

internal static class TestSta
{
    private static readonly object ApplicationGate = new();
    private static bool applicationStarted;

    public static Task RunAsync(Action action)
    {
        return RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Loads App.xaml's resources into <see cref="System.Windows.Application.Current"/> once per
    /// process. The studio palette (Themes/Colors/*.xaml) lives in application resources so
    /// <c>ThemeManager</c> can swap it at runtime, which means window XAML cannot even be parsed
    /// without it.
    /// </summary>
    /// <remarks>
    /// This deliberately loads App.xaml onto a plain <see cref="System.Windows.Application"/>
    /// rather than constructing the product's <c>App</c>. WPF's Application constructor queues its
    /// own startup callback, so <c>App.OnStartup</c> would run as soon as this dispatcher starts:
    /// that takes the single-instance mutex, loads the real user settings, and shows a real
    /// MainWindow on the desktop - which races window-creating tests and drifts on top of the
    /// windows the picture-in-picture tests place at fixed screen coordinates.
    /// </remarks>
    private static void EnsureApplication()
    {
        lock (ApplicationGate)
        {
            if (applicationStarted)
            {
                return;
            }

            var ready = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    if (System.Windows.Application.Current is null)
                    {
                        var app = new System.Windows.Application
                        {
                            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                        };
                        // Mirrors App.xaml: the shared control styles plus a palette. ApplyTheme owns
                        // the palette URIs, so the default palette comes from the product code path.
                        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                        {
                            Source = new Uri(
                                "pack://application:,,,/StreamlinkVlcStudio.App.Wpf;component/Themes/StudioTheme.xaml")
                        });
                        StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
                    }

                    ready.TrySetResult();
                    System.Windows.Threading.Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    ready.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "TestApplication"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            applicationStarted = true;
        }
    }

    public static Task RunAsync(Func<Task> action)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("SVS_SKIP_INTERACTIVE_WINDOW_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromException(new InteractiveDesktopTestSkippedException());
        }

        EnsureApplication();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));

                dispatcher.BeginInvoke(
                    new Action(async () =>
                    {
                        try
                        {
                            await action();
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                        finally
                        {
                            dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }),
                    System.Windows.Threading.DispatcherPriority.Send);
                System.Windows.Threading.Dispatcher.Run();

                // Do not let the next STA test start while this dispatcher's shutdown work is
                // still tearing down native WPF windows. Overlapping teardown made z-order tests
                // order-dependent even though each test used its own STA thread.
                if (failure is null)
                {
                    completion.SetResult();
                }
                else
                {
                    completion.SetException(failure);
                }
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        // A test which ignores cancellation must not keep the dependency-free runner alive after
        // it reports the bounded drain failure.
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

internal static class BitmapAssert
{
    public static int CountColoredPixels(ImageSource? source)
    {
        return CountPixels(source, (r, g, b) =>
            Math.Abs(r - g) > 20 ||
            Math.Abs(r - b) > 20 ||
            Math.Abs(g - b) > 20);
    }

    public static int CountPixels(ImageSource? source, Func<byte, byte, byte, bool> predicate)
    {
        if (source is not BitmapSource bitmap)
        {
            return 0;
        }

        var converted = bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var colored = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var a = pixels[i + 3];
            if (a == 0)
            {
                continue;
            }

            if (predicate(r, g, b))
            {
                colored++;
            }
        }

        return colored;
    }

    public static int CountRgbaPixels(
        IReadOnlyList<byte> pixels,
        int offset,
        Func<byte, byte, byte, bool> predicate)
    {
        var matching = 0;
        for (var index = offset; index + 3 < pixels.Count; index += 4)
        {
            var r = pixels[index];
            var g = pixels[index + 1];
            var b = pixels[index + 2];
            var a = pixels[index + 3];
            if (a > 0 && predicate(r, g, b))
            {
                matching++;
            }
        }

        return matching;
    }
}

internal static class BrowserCaptureTestClient
{
    public static Task<HttpResponseMessage> PostCaptureAsync(HttpClient httpClient, int port, string url)
    {
        return httpClient.PostAsync(
            $"http://127.0.0.1:{port}/capture",
            new StringContent($$"""{"url":"{{url}}"}""", Encoding.UTF8, "application/json"));
    }

    public static HttpRequestMessage CreatePostRequest(int port, string url, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/capture")
        {
            Content = new StringContent($$"""{"url":"{{url}}"}""", Encoding.UTF8, "application/json")
        };
        Assert.True(request.Headers.TryAddWithoutValidation("Origin", origin));
        return request;
    }

    public static async Task<string> SendRawRequestAsync(int port, string request)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(1));
        await using var stream = client.GetStream();
        var requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await stream.FlushAsync().WaitAsync(TimeSpan.FromSeconds(1));

        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
}

internal static class TestWait
{
    public static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string? timeoutMessage = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new InvalidOperationException(timeoutMessage is null
                ? "Timed out waiting for condition."
                : $"Timed out waiting for condition: {timeoutMessage}.");
        }
    }
}

internal static class NativeOverlayControllerTest
{
    private const uint OverlayMagic = 0x564C4F56;
    private const uint OverlayVersion = 1;
    private const int EventMessageSize = 16;

    public const uint ChatInputFocusEvent = 4;

    public static async Task SendEventAsync(
        string pipeName,
        uint eventType,
        int value,
        TimeSpan timeout)
    {
        var message = new byte[EventMessageSize];
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(0, 4), OverlayMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4, 4), OverlayVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8, 4), eventType);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(12, 4), value);

        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    $"{pipeName}_events",
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(100);
                await pipe.WriteAsync(message);
                await pipe.FlushAsync();
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                lastException = ex;
                await Task.Delay(25);
            }
        }

        throw new InvalidOperationException(
            $"Could not send native overlay event {eventType} to pipe '{pipeName}'.",
            lastException);
    }
}

internal static class NativeWindowTest
{
    public static IntPtr CreateHiddenParentWindow()
    {
        var handle = CreateWindowEx(
            0,
            "static",
            "",
            0,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create native test parent window.");
        }

        return handle;
    }

    public static IntPtr CreateVisibleChildWindow(IntPtr parentHandle, string className = "static")
    {
        const int wsChild = 0x40000000;
        const int wsVisible = 0x10000000;
        var handle = CreateWindowEx(
            0,
            className,
            "",
            wsChild | wsVisible,
            0,
            0,
            80,
            60,
            parentHandle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create native test child window.");
        }

        return handle;
    }

    public static string GetClassName(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(handle, buffer, buffer.Capacity);
        if (length == 0)
        {
            throw new InvalidOperationException("Failed to read native window class name.");
        }

        return buffer.ToString();
    }

    public static void SetWindowBounds(IntPtr handle, int x, int y, int width, int height)
    {
        if (!SetWindowPos(handle, IntPtr.Zero, x, y, width, height, SwpNoZOrder))
        {
            throw new InvalidOperationException("Failed to set native window bounds.");
        }
    }

    public static System.Drawing.Rectangle GetWindowBounds(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rect))
        {
            throw new InvalidOperationException("Failed to read native window bounds.");
        }

        return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static bool IsTopmost(IntPtr handle)
    {
        const int gwlExStyle = -20;
        const int wsExTopmost = 0x00000008;
        return (GetWindowLong(handle, gwlExStyle) & wsExTopmost) != 0;
    }

    public static IntPtr MakeMouseLParam(int x, int y)
    {
        return new IntPtr(unchecked((short)x & 0xFFFF | ((short)y << 16)));
    }

    public static IntPtr MakeMouseLParamFromScreenPoint(IntPtr handle, System.Windows.Point screenPoint)
    {
        var point = new NativePoint
        {
            X = (int)Math.Round(screenPoint.X),
            Y = (int)Math.Round(screenPoint.Y)
        };
        if (!ScreenToClient(handle, ref point))
        {
            throw new InvalidOperationException("Failed to convert screen point to native client coordinates.");
        }

        return MakeMouseLParam(point.X, point.Y);
    }

    public static IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam)
    {
        return SendMessageNative(handle, message, wParam, lParam);
    }

    public static IntPtr GetCapture()
    {
        return GetCaptureNative();
    }

    public static bool ReleaseCapture()
    {
        return ReleaseCaptureNative();
    }

    public static bool TryGetCursorPosition(out System.Drawing.Point point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = new System.Drawing.Point(nativePoint.X, nativePoint.Y);
            return true;
        }

        point = default;
        return false;
    }

    public static void SetCursorPosition(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            throw new InvalidOperationException("Failed to set native cursor position.");
        }
    }

    public static IntPtr GetForegroundWindow()
    {
        return GetForegroundWindowNative();
    }

    /// <summary>
    /// Brings <paramref name="handle"/> to the foreground. A bare SetForegroundWindow is refused
    /// whenever another process owns the foreground (Windows only flashes the taskbar button), so
    /// attach to the current foreground thread's input queue first - otherwise every test that
    /// asserts on foreground or z-order fails on any desktop that has an active app.
    /// </summary>
    public static void ActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var foregroundWindow = GetForegroundWindowNative();
        if (foregroundWindow == handle)
        {
            return;
        }

        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
        var attached = foregroundThreadId != 0 &&
            foregroundThreadId != currentThreadId &&
            AttachThreadInput(currentThreadId, foregroundThreadId, true);

        try
        {
            _ = BringWindowToTop(handle);
            _ = SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                _ = AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    public static async Task RequireForegroundAsync(
        IntPtr handle,
        TimeSpan timeout,
        string precondition)
    {
        if (handle == IntPtr.Zero)
        {
            throw new InteractiveDesktopTestSkippedException(
                $"{precondition}: the test window has no native handle");
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            ActivateWindow(handle);
            if (GetForegroundWindowNative() == handle)
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new InteractiveDesktopTestSkippedException(
            $"{precondition}: Windows did not grant foreground activation");
    }

    public static string DescribeWindowAtPoint(int screenX, int screenY)
    {
        var point = new NativePoint { X = screenX, Y = screenY };
        var hwnd = WindowFromPoint(point);
        var root = GetAncestor(hwnd, 2);
        var title = new StringBuilder(256);
        _ = GetWindowText(root, title, title.Capacity);
        var className = new StringBuilder(256);
        _ = GetClassName(root, className, className.Capacity);
        return $"[point=({screenX},{screenY}) hwnd={hwnd} root={root} class='{className}' title='{title}' topmost={IsTopmost(root)}]";
    }

    public static bool IsRootWindowAtPoint(IntPtr expectedRoot, int screenX, int screenY)
    {
        if (expectedRoot == IntPtr.Zero)
        {
            return false;
        }

        var point = new NativePoint { X = screenX, Y = screenY };
        var hwnd = WindowFromPoint(point);
        return hwnd == expectedRoot ||
            (hwnd != IntPtr.Zero && GetAncestor(hwnd, 2) == expectedRoot);
    }

    [DllImport("user32")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    public static void SendLeftDoubleClick(int screenX, int screenY)
    {
        const uint leftDown = 0x0002;
        const uint leftUp = 0x0004;
        SetCursorPosition(screenX, screenY);
        mouse_event(leftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(leftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(50);
        mouse_event(leftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(leftUp, 0, 0, 0, UIntPtr.Zero);
    }

    public static void SendLeftClick(int screenX, int screenY)
    {
        const uint leftDown = 0x0002;
        const uint leftUp = 0x0004;
        SetCursorPosition(screenX, screenY);
        mouse_event(leftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(leftUp, 0, 0, 0, UIntPtr.Zero);
    }

    public static void SendUnicodeText(string text)
    {
        const uint inputKeyboard = 1;
        const uint keyEventKeyUp = 0x0002;
        const uint keyEventUnicode = 0x0004;
        var inputs = text
            .SelectMany(character => new[]
            {
                new NativeInput
                {
                    Type = inputKeyboard,
                    Union = new NativeInputUnion
                    {
                        Keyboard = new NativeKeyboardInput
                        {
                            ScanCode = character,
                            Flags = keyEventUnicode
                        }
                    }
                },
                new NativeInput
                {
                    Type = inputKeyboard,
                    Union = new NativeInputUnion
                    {
                        Keyboard = new NativeKeyboardInput
                        {
                            ScanCode = character,
                            Flags = keyEventUnicode | keyEventKeyUp
                        }
                    }
                }
            })
            .ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"Failed to send Unicode keyboard input ({sent}/{inputs.Length}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeMouseInput Mouse;

        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    private const int SwpNoZOrder = 0x0004;

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        int uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32", EntryPoint = "SendMessageW", SetLastError = true)]
    private static extern IntPtr SendMessageNative(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32", EntryPoint = "GetCapture")]
    private static extern IntPtr GetCaptureNative();

    [DllImport("user32", EntryPoint = "ReleaseCapture")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCaptureNative();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);
}

internal static class WpfVisualTest
{
    /// <summary>
    /// Resolves a studio palette colour (e.g. <c>StudioAccentPressedColor</c>) through the element
    /// tree. The palette lives in application resources so <c>ThemeManager</c> can swap it, which
    /// means <c>window.Resources[key]</c> does not see it - only a tree walk does.
    /// </summary>
    public static string PaletteColor(System.Windows.FrameworkElement element, string colorKey)
    {
        var value = element.TryFindResource(colorKey);
        if (value is not System.Windows.Media.Color color)
        {
            throw new InvalidOperationException($"Palette colour '{colorKey}' was not found in the resource tree.");
        }

        return color.ToString();
    }

    /// <summary>Resolves a studio palette brush (e.g. <c>StudioAccentBrush</c>) through the element tree.</summary>
    public static System.Windows.Media.Brush PaletteBrush(System.Windows.FrameworkElement element, string brushKey)
    {
        var value = element.TryFindResource(brushKey);
        if (value is not System.Windows.Media.Brush brush)
        {
            throw new InvalidOperationException($"Palette brush '{brushKey}' was not found in the resource tree.");
        }

        return brush;
    }

    public static void AssertSolidBrushColor(string expected, System.Windows.Media.Brush brush)
    {
        AssertBrushColor(expected, brush);
    }

    public static void AssertTemplateBorderColor(
        System.Windows.Controls.Button button,
        string expectedBackground,
        string expectedBorder)
    {
        var border = button.Template.FindName("ButtonBorder", button) as System.Windows.Controls.Border;
        Assert.NotNull(border);
        AssertBrushColor(expectedBackground, border!.Background);
        AssertBrushColor(expectedBorder, border.BorderBrush);
    }

    public static void AssertBorderColor(
        System.Windows.Controls.Border border,
        string expectedBackground,
        string expectedBorder)
    {
        AssertBrushColor(expectedBackground, border.Background);
        AssertBrushColor(expectedBorder, border.BorderBrush);
    }

    public static RenderTargetBitmap Render(FrameworkElement element)
    {
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(element.RenderSize.Width));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(element.RenderSize.Height));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    public static byte PixelAlpha(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    public static string PixelColor(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return System.Windows.Media.Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]).ToString();
    }

    private static void AssertBrushColor(string expected, System.Windows.Media.Brush brush)
    {
        if (brush is not System.Windows.Media.SolidColorBrush solidBrush)
        {
            throw new InvalidOperationException($"Expected a solid brush, got {brush.GetType().Name}.");
        }

        Assert.Equal(expected, solidBrush.Color.ToString());
    }
}

public sealed class TopControlToggleState
{
    public bool IsReplaySeekBarUiVisible { get; init; }

    public bool IsSelectedChatShowing { get; init; }

    public bool IsChatLayoutHidden { get; init; }

    public bool IsMultiStreamEnabled { get; init; }

    public bool IsSettingsOpen { get; init; }

    public TopControlToggleTabState? SelectedTab { get; init; }
}

public sealed class TopControlToggleTabState
{
    public bool IsMuted { get; init; }

    public PlaybackStatus Status { get; init; }
}

public sealed class HomeNavigationVisualState
{
    public bool IsFollowedHomePageSelected { get; init; }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException($"Expected [{string.Join(", ", expectedArray.Select(FormatAssertValue))}], got [{string.Join(", ", actualArray.Select(FormatAssertValue))}].");
        }
    }

    private static string FormatAssertValue<T>(T value)
    {
        return value switch
        {
            StreamTabViewModel tab => tab.Target.Channel,
            null => "",
            _ => value.ToString() ?? ""
        };
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    public static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    public static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expectedSubstring}'.");
        }
    }

    public static void DoesNotContain(string unexpectedSubstring, string actual)
    {
        if (actual.Contains(unexpectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{actual}' not to contain '{unexpectedSubstring}'.");
        }
    }
}

internal static class KickWebhookTestSignature
{
    public static void AddKickHeaders(
        HttpRequestMessage request,
        RSA rsa,
        string eventType,
        string messageId,
        string timestamp,
        byte[] bodyBytes)
    {
        var signedPrefix = Encoding.UTF8.GetBytes($"{messageId}.{timestamp}.");
        var signedBytes = new byte[signedPrefix.Length + bodyBytes.Length];
        Buffer.BlockCopy(signedPrefix, 0, signedBytes, 0, signedPrefix.Length);
        Buffer.BlockCopy(bodyBytes, 0, signedBytes, signedPrefix.Length, bodyBytes.Length);
        var signature = Convert.ToBase64String(rsa.SignData(
            signedBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        request.Headers.TryAddWithoutValidation("Kick-Event-Type", eventType);
        request.Headers.TryAddWithoutValidation("Kick-Event-Version", "1");
        request.Headers.TryAddWithoutValidation("Kick-Event-Message-Id", messageId);
        request.Headers.TryAddWithoutValidation("Kick-Event-Message-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("Kick-Event-Signature", signature);
    }
}

internal sealed class MemoryLogger : IAppLogger
{
    private readonly object gate = new();
    private readonly List<LogEntry> entries = [];

    public event EventHandler<LogEntry>? EntryWritten;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToArray();
            }
        }
    }

    public void Write(AppLogLevel level, string source, string message, Exception? exception = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, source, message, exception);
        lock (gate)
        {
            entries.Add(entry);
        }

        EntryWritten?.Invoke(this, entry);
    }
}

internal sealed class FakeTaskbarFullscreenController : ITaskbarFullscreenController
{
    public List<(IntPtr WindowHandle, bool Fullscreen)> Requests { get; } = [];

    public Queue<bool> ReturnValues { get; } = [];

    public bool ReturnValue { get; set; } = true;

    public bool TrySetFullscreen(IntPtr windowHandle, bool fullscreen)
    {
        Requests.Add((windowHandle, fullscreen));
        return ReturnValues.TryDequeue(out var returnValue) ? returnValue : ReturnValue;
    }
}

internal sealed class FakeSettingsService : ISettingsService
{
    private AppSettings settings;

    public FakeSettingsService(AppSettings settings)
    {
        this.settings = settings;
    }

    public string SettingsPath => "memory";

    public int SaveCount { get; private set; }

    public Exception? SaveException { get; set; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        if (SaveException is not null)
        {
            throw SaveException;
        }

        this.settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        this.respond = respond;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(respond(request));
    }
}

internal sealed class AsyncHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync;

    public AsyncHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync)
    {
        this.respondAsync = respondAsync;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return respondAsync(request, cancellationToken);
    }
}

internal sealed class FakeStreamMetadataService : IStreamMetadataService
{
    private readonly object gate = new();
    private readonly Queue<StreamMetadataResult> results = new();
    private readonly List<StreamTarget> requests = [];
    private StreamMetadataResult currentResult;

    public FakeStreamMetadataService(params StreamMetadataResult[] results)
    {
        if (results.Length == 0)
        {
            throw new ArgumentException("At least one metadata result is required.", nameof(results));
        }

        foreach (var result in results)
        {
            this.results.Enqueue(result);
        }

        currentResult = results[^1];
    }

    public int CallCount { get; private set; }

    public IReadOnlyList<StreamTarget> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<StreamMetadataResult> GetLiveStreamMetadataAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        StreamMetadataResult result;
        lock (gate)
        {
            CallCount++;
            requests.Add(target);
            if (results.Count > 0)
            {
                currentResult = results.Dequeue();
            }

            result = currentResult;
        }

        return Task.FromResult(result);
    }
}

internal sealed class FakeFollowedStreamsService : IFollowedStreamsService
{
    private readonly object gate = new();
    private readonly Queue<Func<CancellationToken, Task<FollowedLiveStreamsResult>>> results = new();
    private readonly List<CancellationToken> cancellationTokens = [];
    private readonly FollowedLiveStreamsResult defaultResult;
    private int activeCalls;
    private int callCount;
    private int maxConcurrentCalls;

    public FakeFollowedStreamsService(params FollowedLiveStream[] streams)
    {
        defaultResult = new FollowedLiveStreamsResult(streams, []);
    }

    public int CallCount
    {
        get
        {
            lock (gate)
            {
                return callCount;
            }
        }
    }

    public int MaxConcurrentCalls
    {
        get
        {
            lock (gate)
            {
                return maxConcurrentCalls;
            }
        }
    }

    public IReadOnlyList<CancellationToken> CancellationTokens
    {
        get
        {
            lock (gate)
            {
                return cancellationTokens.ToArray();
            }
        }
    }

    public void EnqueueResult(params FollowedLiveStream[] streams)
    {
        var result = new FollowedLiveStreamsResult(streams, []);
        EnqueueResult(_ => Task.FromResult(result));
    }

    public void EnqueueResult(Func<CancellationToken, Task<FollowedLiveStreamsResult>> loadAsync)
    {
        lock (gate)
        {
            results.Enqueue(loadAsync);
        }
    }

    public async Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task<FollowedLiveStreamsResult>> loadAsync;
        lock (gate)
        {
            callCount++;
            activeCalls++;
            maxConcurrentCalls = Math.Max(maxConcurrentCalls, activeCalls);
            cancellationTokens.Add(cancellationToken);
            loadAsync = results.Count > 0
                ? results.Dequeue()
                : _ => Task.FromResult(defaultResult);
        }

        try
        {
            return await loadAsync(cancellationToken);
        }
        finally
        {
            lock (gate)
            {
                activeCalls--;
            }
        }
    }
}

internal sealed class FakeLiveNotificationService : ILiveNotificationService
{
    private readonly object gate = new();
    private readonly List<LiveChannelNotification> notifications = [];
    private bool isEnabled = true;

    public event Action<NotificationActivation>? Activated;

    public bool IsEnabled
    {
        get
        {
            lock (gate)
            {
                return isEnabled;
            }
        }
        set
        {
            lock (gate)
            {
                isEnabled = value;
            }
        }
    }

    public IReadOnlyList<LiveChannelNotification> Notifications
    {
        get
        {
            lock (gate)
            {
                return notifications.ToArray();
            }
        }
    }

    public void NotifyChannelLive(LiveChannelNotification notification)
    {
        lock (gate)
        {
            if (!isEnabled)
            {
                return;
            }

            notifications.Add(notification);
        }
    }

    public void RaiseActivated(NotificationActivation activation)
    {
        Activated?.Invoke(activation);
    }
}

internal sealed class FakeTwitchVodService : ITwitchVodService
{
    private readonly object gate = new();
    private readonly Queue<TwitchVodSearchResult> results = new();
    private readonly List<TwitchVodSearchRequest> requests = [];
    private TwitchVodSearchResult currentResult;

    public FakeTwitchVodService(params TwitchVodSearchResult[] results)
    {
        if (results.Length == 0)
        {
            throw new ArgumentException("At least one VOD result is required.", nameof(results));
        }

        foreach (var result in results)
        {
            this.results.Enqueue(result);
        }

        currentResult = results[^1];
    }

    public int CallCount
    {
        get
        {
            lock (gate)
            {
                return requests.Count;
            }
        }
    }

    public IReadOnlyList<TwitchVodSearchRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<TwitchVodSearchResult> SearchAsync(
        TwitchVodSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        TwitchVodSearchResult result;
        lock (gate)
        {
            requests.Add(request);
            if (results.Count > 0)
            {
                currentResult = results.Dequeue();
            }

            result = currentResult;
        }

        return Task.FromResult(result);
    }
}

internal sealed class FakeKickVodService : IKickVodService
{
    private readonly object gate = new();
    private readonly Queue<KickVodSearchResult> results = new();
    private readonly List<KickVodSearchRequest> requests = [];
    private KickVodSearchResult currentResult;

    public FakeKickVodService(params KickVodSearchResult[] results)
    {
        if (results.Length == 0)
        {
            throw new ArgumentException("At least one Kick VOD result is required.", nameof(results));
        }

        foreach (var result in results)
        {
            this.results.Enqueue(result);
        }

        currentResult = results[^1];
    }

    public int CallCount
    {
        get
        {
            lock (gate)
            {
                return requests.Count;
            }
        }
    }

    public IReadOnlyList<KickVodSearchRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<KickVodSearchResult> SearchAsync(
        KickVodSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        KickVodSearchResult result;
        lock (gate)
        {
            requests.Add(request);
            if (results.Count > 0)
            {
                currentResult = results.Dequeue();
            }

            result = currentResult;
        }

        return Task.FromResult(result);
    }
}

internal sealed class FakeStreamSearchService : IStreamSearchService
{
    private readonly object gate = new();
    private readonly Queue<StreamSearchResult> results = new();
    private readonly List<StreamSearchRequest> requests = [];
    private StreamSearchResult currentResult;

    public Func<StreamSearchRequest, CancellationToken, Task<StreamSearchResult>>? ResponderAsync { get; init; }

    public FakeStreamSearchService(params StreamSearchResult[] results)
    {
        if (results.Length == 0)
        {
            throw new ArgumentException("At least one stream search result is required.", nameof(results));
        }

        foreach (var result in results)
        {
            this.results.Enqueue(result);
        }

        currentResult = results[^1];
    }

    public int CallCount
    {
        get
        {
            lock (gate)
            {
                return requests.Count;
            }
        }
    }

    public IReadOnlyList<StreamSearchRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<StreamSearchResult> SearchAsync(
        StreamSearchRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        StreamSearchResult result;
        Func<StreamSearchRequest, CancellationToken, Task<StreamSearchResult>>? responder;
        lock (gate)
        {
            requests.Add(request);
            responder = ResponderAsync;
            if (responder is null && results.Count > 0)
            {
                currentResult = results.Dequeue();
            }

            result = currentResult;
        }

        return responder?.Invoke(request, cancellationToken) ?? Task.FromResult(result);
    }
}

internal sealed class FakeBrowseService : IBrowseService
{
    private readonly object gate = new();
    private readonly Queue<BrowseResult<BrowseCategory>> categoryResults = new();
    private readonly Queue<BrowseResult<BrowseCategoryViewerCount>> categoryViewerCountResults = new();
    private readonly Queue<BrowseResult<BrowseLiveStream>> streamResults = new();
    private readonly List<BrowseCategoryRequest> categoryRequests = [];
    private readonly List<BrowseCategoryViewerCountRequest> categoryViewerCountRequests = [];
    private readonly List<BrowseStreamRequest> streamRequests = [];

    public Func<BrowseCategoryRequest, BrowseResult<BrowseCategory>>? CategoryResponder { get; set; }

    public Func<BrowseCategoryViewerCountRequest, Task<BrowseResult<BrowseCategoryViewerCount>>>? CategoryViewerCountResponder { get; set; }

    public Func<BrowseCategoryViewerCountRequest, CancellationToken, Task<BrowseResult<BrowseCategoryViewerCount>>>? CategoryViewerCountResponderWithCancellation { get; set; }

    public Func<BrowseStreamRequest, BrowseResult<BrowseLiveStream>>? StreamResponder { get; set; }

    public IReadOnlyList<BrowseCategoryRequest> CategoryRequests
    {
        get
        {
            lock (gate)
            {
                return categoryRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<BrowseCategoryViewerCountRequest> CategoryViewerCountRequests
    {
        get
        {
            lock (gate)
            {
                return categoryViewerCountRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<BrowseStreamRequest> StreamRequests
    {
        get
        {
            lock (gate)
            {
                return streamRequests.ToArray();
            }
        }
    }

    public void EnqueueCategories(BrowseResult<BrowseCategory> result)
    {
        lock (gate)
        {
            categoryResults.Enqueue(result);
        }
    }

    public void EnqueueCategoryViewerCounts(BrowseResult<BrowseCategoryViewerCount> result)
    {
        lock (gate)
        {
            categoryViewerCountResults.Enqueue(result);
        }
    }

    public void EnqueueStreams(BrowseResult<BrowseLiveStream> result)
    {
        lock (gate)
        {
            streamResults.Enqueue(result);
        }
    }

    public void ClearRequests()
    {
        lock (gate)
        {
            categoryRequests.Clear();
            categoryViewerCountRequests.Clear();
            streamRequests.Clear();
        }
    }

    public Task<BrowseResult<BrowseCategory>> GetCategoriesAsync(
        BrowseCategoryRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            categoryRequests.Add(request);
            if (CategoryResponder is not null)
            {
                return Task.FromResult(CategoryResponder(request));
            }

            if (categoryResults.Count > 0)
            {
                return Task.FromResult(categoryResults.Dequeue());
            }
        }

        return Task.FromResult(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [],
            "",
            "No categories."));
    }

    public Task<BrowseResult<BrowseCategoryViewerCount>> GetCategoryViewerCountsAsync(
        BrowseCategoryViewerCountRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            categoryViewerCountRequests.Add(request);
            if (CategoryViewerCountResponderWithCancellation is not null)
            {
                return CategoryViewerCountResponderWithCancellation(request, cancellationToken);
            }

            if (CategoryViewerCountResponder is not null)
            {
                return CategoryViewerCountResponder(request);
            }

            if (categoryViewerCountResults.Count > 0)
            {
                return Task.FromResult(categoryViewerCountResults.Dequeue());
            }
        }

        return Task.FromResult(new BrowseResult<BrowseCategoryViewerCount>(
            BrowseResultStatus.Available,
            [],
            "",
            "No category viewer counts."));
    }

    public Task<BrowseResult<BrowseLiveStream>> GetStreamsAsync(
        BrowseStreamRequest request,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            streamRequests.Add(request);
            if (StreamResponder is not null)
            {
                return Task.FromResult(StreamResponder(request));
            }

            if (streamResults.Count > 0)
            {
                return Task.FromResult(streamResults.Dequeue());
            }
        }

        return Task.FromResult(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [],
            "",
            "No streams."));
    }
}

internal sealed class FakeViewerCountService : IViewerCountService
{
    private readonly object gate = new();
    private readonly List<StreamTarget> requests = [];
    private int callCount;

    public Func<StreamTarget, ViewerCountResult>? Responder { get; init; }

    public Func<StreamTarget, CancellationToken, Task<ViewerCountResult>>? ResponderAsync { get; init; }

    public int CallCount => Volatile.Read(ref callCount);

    public IReadOnlyList<StreamTarget> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<ViewerCountResult> GetViewerCountAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref callCount);
        lock (gate)
        {
            requests.Add(target);
        }

        return ResponderAsync?.Invoke(target, cancellationToken) ??
            Task.FromResult(Responder?.Invoke(target) ??
                new ViewerCountResult(ViewerCountState.Available, 1234, "viewer count updated"));
    }
}

internal sealed class FakeStreamlinkService : IStreamlinkService
{
    private readonly object gate = new();
    private readonly List<StreamTransportRequest> probeRequests = [];
    private readonly List<StreamTransportRequest> resolveStreamUrlRequests = [];
    private readonly List<StreamTransportRequest> startExternalHttpRequests = [];
    public bool Started { get; private set; }
    public int StartCount { get; private set; }
    public int ResolveStreamUrlCount { get; private set; }
    public Func<StreamTransportRequest, CancellationToken, Task<StreamlinkProbeResult>>? ProbeStreamsOverride { get; set; }
    public Func<StreamTransportRequest, CancellationToken, Task<StreamlinkResolvedUrl>>? ResolveStreamUrlOverride { get; set; }
    public Func<StreamTransportRequest, CancellationToken, Task<IStreamTransportSession>>? StartExternalHttpOverride { get; set; }

    public IReadOnlyList<StreamTransportRequest> ProbeRequests
    {
        get
        {
            lock (gate)
            {
                return probeRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<StreamTransportRequest> ResolveStreamUrlRequests
    {
        get
        {
            lock (gate)
            {
                return resolveStreamUrlRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<StreamTransportRequest> StartExternalHttpRequests
    {
        get
        {
            lock (gate)
            {
                return startExternalHttpRequests.ToArray();
            }
        }
    }

    public Task<StreamlinkProbeResult> ProbeStreamsAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            probeRequests.Add(request);
        }

        if (ProbeStreamsOverride is not null)
        {
            return ProbeStreamsOverride(request, cancellationToken);
        }

        return Task.FromResult(new StreamlinkProbeResult(true, "Playable stream found."));
    }

    public Task<StreamlinkResolvedUrl> ResolveStreamUrlAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ResolveStreamUrlCount++;
            resolveStreamUrlRequests.Add(request);
        }

        if (ResolveStreamUrlOverride is not null)
        {
            return ResolveStreamUrlOverride(request, cancellationToken);
        }

        return Task.FromResult(new StreamlinkResolvedUrl(new Uri("https://example.com/replay.m3u8"), "Resolved."));
    }

    public Task<IStreamTransportSession> StartExternalHttpAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            startExternalHttpRequests.Add(request);
            Started = true;
            StartCount++;
        }

        if (StartExternalHttpOverride is not null)
        {
            return StartExternalHttpOverride(request, cancellationToken);
        }

        return Task.FromResult<IStreamTransportSession>(new FakeTransportSession());
    }
}

internal sealed class FakeTwitchSubOnlyVodResolver : ITwitchSubOnlyVodResolver
{
    public List<TwitchSubOnlyVodRequest> Requests { get; } = [];
    public Func<TwitchSubOnlyVodRequest, CancellationToken, Task<TwitchSubOnlyVodResolution>>? Override { get; set; }

    public Task<TwitchSubOnlyVodResolution> ResolveAsync(
        TwitchSubOnlyVodRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Override?.Invoke(request, cancellationToken) ??
            Task.FromResult(new TwitchSubOnlyVodResolution(new Uri(@"C:\fake\sub-only.m3u8"), "chunked", "Resolved."));
    }
}

internal sealed class FakeTwitchClipService : ITwitchClipService
{
    public List<StreamTarget> Targets { get; } = [];

    public Uri ClipUri { get; } = new("https://clips.twitch.tv/test-clip");

    public Task<TwitchClipResult> CreateLiveClipAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        Targets.Add(target);
        return Task.FromResult(new TwitchClipResult("test-clip", ClipUri));
    }
}

internal sealed class FakeTransportSession : IStreamTransportSession
{
    private int disposeCount;

    public Uri PlaybackUri { get; } = new("http://127.0.0.1:5000/");
    public int DisposeCount => Volatile.Read(ref disposeCount);
    public event EventHandler<string>? LogLineReceived { add { } remove { } }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref disposeCount);
        return ValueTask.CompletedTask;
    }
}

internal sealed class BlockingTransportSession : IStreamTransportSession
{
    private readonly Task releaseDispose;
    private readonly TaskCompletionSource disposeStarted;

    public BlockingTransportSession(Task releaseDispose, TaskCompletionSource disposeStarted)
    {
        this.releaseDispose = releaseDispose;
        this.disposeStarted = disposeStarted;
    }

    public Uri PlaybackUri { get; } = new("http://127.0.0.1:5000/");
    public event EventHandler<string>? LogLineReceived { add { } remove { } }

    public async ValueTask DisposeAsync()
    {
        disposeStarted.TrySetResult();
        await releaseDispose;
    }
}

internal static class TestViewModels
{
    public static MainViewModel CreateMain(
        AppSettings settings,
        ISettingsService settingsService,
        IStreamlinkService streamlinkService,
        IPlaybackEngineFactory playbackFactory,
        IChatClientFactory chatFactory,
        IAppLogger logger,
        Action<Action> dispatch,
        IViewerCountService? viewerCountService = null,
        IFollowedStreamsService? followedStreamsService = null,
        IStreamMetadataService? streamMetadataService = null,
        IReplayResolver? replayResolver = null,
        IReplayChatProvider? replayChatProvider = null,
        TimeSpan? recentThumbnailRefreshInterval = null,
        TimeSpan? streamSearchDebounceInterval = null,
        ITwitchVodService? twitchVodService = null,
        TimeSpan? twitchVodSearchDebounceInterval = null,
        IBrowseService? browseService = null,
        TimeSpan? browseCategorySearchDebounceInterval = null,
        IKickChatHistoryProvider? kickChatHistoryProvider = null,
        TimeSpan? followedChannelsRefreshInterval = null,
        IStreamSearchService? streamSearchService = null,
        IKickVodService? kickVodService = null,
        IKickEventSubscriptionService? kickEventSubscriptionService = null,
        ILiveNotificationService? liveNotificationService = null,
        ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver = null,
        ITwitchClipService? twitchClipService = null,
        IAppUpdateService? appUpdateService = null,
        Action<Uri>? openBrowser = null,
        Action? requestShutdown = null,
        Func<Action, bool>? tryDispatch = null) =>
        new(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = settingsService,
            StreamlinkService = streamlinkService,
            PlaybackFactory = playbackFactory,
            ChatFactory = chatFactory,
            Logger = logger,
            Dispatch = dispatch,
            ViewerCountService = viewerCountService,
            FollowedStreamsService = followedStreamsService,
            StreamMetadataService = streamMetadataService,
            ReplayResolver = replayResolver,
            ReplayChatProvider = replayChatProvider,
            RecentThumbnailRefreshInterval = recentThumbnailRefreshInterval,
            StreamSearchDebounceInterval = streamSearchDebounceInterval,
            TwitchVodService = twitchVodService,
            TwitchVodSearchDebounceInterval = twitchVodSearchDebounceInterval,
            BrowseService = browseService,
            BrowseCategorySearchDebounceInterval = browseCategorySearchDebounceInterval,
            KickChatHistoryProvider = kickChatHistoryProvider,
            FollowedChannelsRefreshInterval = followedChannelsRefreshInterval,
            StreamSearchService = streamSearchService,
            KickVodService = kickVodService,
            KickEventSubscriptionService = kickEventSubscriptionService,
            LiveNotificationService = liveNotificationService,
            TwitchSubOnlyVodResolver = twitchSubOnlyVodResolver,
            TwitchClipService = twitchClipService,
            AppUpdateService = appUpdateService,
            OpenBrowser = openBrowser,
            RequestShutdown = requestShutdown,
            TryDispatch = tryDispatch
        });

    public static StreamTabViewModel CreateTab(
        StreamTarget target,
        string quality,
        IStreamlinkService streamlinkService,
        IPlaybackEngineFactory playbackFactory,
        IChatClientFactory chatFactory,
        IAppLogger logger,
        Action<Action> dispatch,
        int initialVolume = StreamTabViewModel.DefaultVolume,
        IViewerCountService? viewerCountService = null,
        IReplayResolver? replayResolver = null,
        IReplayChatProvider? replayChatProvider = null,
        TimeSpan? twitchLiveDvrPromotionPollInterval = null,
        IKickChatHistoryProvider? kickChatHistoryProvider = null,
        IKickEventSubscriptionService? kickEventSubscriptionService = null,
        ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver = null) =>
        new(new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = quality,
            StreamlinkService = streamlinkService,
            PlaybackFactory = playbackFactory,
            ChatFactory = chatFactory,
            Logger = logger,
            Dispatch = dispatch,
            InitialVolume = initialVolume,
            ViewerCountService = viewerCountService,
            ReplayResolver = replayResolver,
            ReplayChatProvider = replayChatProvider,
            TwitchLiveDvrPromotionPollInterval = twitchLiveDvrPromotionPollInterval,
            KickChatHistoryProvider = kickChatHistoryProvider,
            KickEventSubscriptionService = kickEventSubscriptionService,
            TwitchSubOnlyVodResolver = twitchSubOnlyVodResolver
        });
}

internal sealed class FakePlaybackEngineFactory : IPlaybackEngineFactory
{
    private readonly object gate = new();
    private readonly Func<FakePlaybackEngine> createEngine;
    private readonly List<bool> enableNativeOverlayRequests = [];
    private readonly List<FakePlaybackEngine> engines = [];
    public FakePlaybackEngine? Engine { get; private set; }
    public int CreateCount { get; private set; }
    public bool? LastEnableNativeOverlay { get; private set; }
    public string? LastNativeOverlayPositionStatePath { get; private set; }
    public VideoRendererMode LastRendererMode { get; private set; } = VideoRendererMode.Automatic;
    public IReadOnlyList<bool> EnableNativeOverlayRequests
    {
        get
        {
            lock (gate)
            {
                return enableNativeOverlayRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<FakePlaybackEngine> Engines
    {
        get
        {
            lock (gate)
            {
                return engines.ToArray();
            }
        }
    }

    public FakePlaybackEngineFactory(Func<FakePlaybackEngine>? createEngine = null)
    {
        this.createEngine = createEngine ?? (() => new FakePlaybackEngine());
    }

    public Task<IPlaybackEngine> CreateAsync(
        string vlcDirectory,
        bool enableNativeOverlay = true,
        string? nativeOverlayPositionStatePath = null,
        CancellationToken cancellationToken = default,
        VideoRendererMode rendererMode = VideoRendererMode.Automatic)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            CreateCount++;
            LastEnableNativeOverlay = enableNativeOverlay;
            enableNativeOverlayRequests.Add(enableNativeOverlay);
            LastNativeOverlayPositionStatePath = nativeOverlayPositionStatePath;
            LastRendererMode = rendererMode;
            Engine = createEngine();
            engines.Add(Engine);
            Engine.NativeOverlayPositionStatePathOverride ??= nativeOverlayPositionStatePath;
            return Task.FromResult<IPlaybackEngine>(Engine);
        }
    }
}

internal sealed class FakeSharedAudioState
{
    public int Volume { get; set; }
    public bool Muted { get; set; }
    public PlaybackAudioState AudioState { get; set; }
}

internal sealed record FakePlaybackAudioCall(FakePlaybackEngine Engine, int Volume, PlaybackAudioState AudioState);

internal sealed class FakePlaybackEngine : IPlaybackEngine
{
    private readonly List<IntPtr> videoHandleHistory = [];
    private readonly List<Uri> playedUris = [];
    public event EventHandler? VideoOutputRebound;
    public event EventHandler? AudioStateReapplied;
    public bool Played { get; private set; }
    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }
    public int SeekCount { get; private set; }
    public bool Stopped { get; private set; }
    public Uri? LastPlayedUri { get; private set; }
    public int RequestedVolume { get; private set; }
    public int Volume { get; private set; }
    public bool Muted { get; private set; }
    public bool Paused { get; private set; }
    public PlaybackAudioState AudioState { get; private set; } = PlaybackAudioState.Audible;
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromHours(2);
    public bool Seekable { get; set; } = true;
    public TimeSpan? ResumeJumpsToPosition { get; set; }
    public bool AudioTrackEnabled { get; private set; } = true;
    public bool IgnoreSetMutedUntilPlayed { get; init; }
    public bool IgnoreAudibleWhilePaused { get; init; }
    public int FailingSeekCount { get; set; }
    public Task PlayCompletion { get; init; } = Task.CompletedTask;
    public Func<int, Task>? PlayCompletionOverride { get; init; }
    public TaskCompletionSource PlayStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task SeekCompletion { get; init; } = Task.CompletedTask;
    public TaskCompletionSource SeekStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task StopCompletion { get; init; } = Task.CompletedTask;
    public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FakeSharedAudioState? SharedAudioState { get; init; }
    public ConcurrentQueue<FakePlaybackAudioCall>? AudioCallLog { get; init; }
    public bool UsesNativeOverlayOverride { get; init; }
    public string? NativeOverlayPipeNameOverride { get; init; }
    public string? NativeOverlayPositionStatePathOverride { get; set; }
    public string? NativeOverlayDirectoryOverride { get; init; }
    public Func<FakePlaybackEngine, (bool IsAvailable, PlaybackClock Clock)>? PlaybackClockOverride { get; set; }
    public int VideoWidth { get; set; } = 1920;
    public int VideoHeight { get; set; } = 1080;
    public bool UsesNativeOverlay => UsesNativeOverlayOverride;
    public string? NativeOverlayPipeName => NativeOverlayPipeNameOverride;
    public string? NativeOverlayPositionStatePath => NativeOverlayPositionStatePathOverride;
    public string? NativeOverlayDirectory => NativeOverlayDirectoryOverride;
    public IntPtr VideoHandle { get; private set; }
    public IReadOnlyList<IntPtr> VideoHandleHistory => videoHandleHistory.ToArray();
    public IReadOnlyList<Uri> PlayedUris => playedUris.ToArray();

    public void SetVideoHandle(IntPtr handle)
    {
        VideoHandle = handle;
        videoHandleHistory.Add(handle);
    }

    public async Task PlayAsync(Uri mediaUri, int volume, PlaybackAudioState audioState, CancellationToken cancellationToken = default)
    {
        if (VideoHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Playback started without a video handle.");
        }

        PlayStarted.TrySetResult();
        var playNumber = PlayCount + 1;
        var completion = PlayCompletionOverride?.Invoke(playNumber) ?? PlayCompletion;
        await completion.WaitAsync(cancellationToken);
        PlayCount++;
        LastPlayedUri = mediaUri;
        playedUris.Add(mediaUri);
        Played = true;
        Stopped = false;
        Paused = false;
        ApplyAudioState(volume, audioState);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        Paused = false;
        if (ResumeJumpsToPosition is { } jump)
        {
            Position = jump;
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        SeekCount++;
        if (FailingSeekCount > 0)
        {
            FailingSeekCount--;
            throw new InvalidOperationException("Simulated seek failure.");
        }

        SeekStarted.TrySetResult();
        return SeekCoreAsync(position, cancellationToken);
    }

    private async Task SeekCoreAsync(TimeSpan position, CancellationToken cancellationToken)
    {
        await SeekCompletion.WaitAsync(cancellationToken);
        Position = position;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopStarted.TrySetResult();
        await StopCompletion.WaitAsync(cancellationToken);
        StopCount++;
        Stopped = true;
        Paused = true;
    }

    public bool TryGetPlaybackClock(out PlaybackClock clock)
    {
        if (PlaybackClockOverride is not null)
        {
            var result = PlaybackClockOverride(this);
            clock = result.Clock;
            return result.IsAvailable;
        }

        clock = new PlaybackClock(Position, Duration, Seekable);
        return Played;
    }

    public bool TryGetVideoSize(out int width, out int height)
    {
        width = VideoWidth;
        height = VideoHeight;
        return Played && width > 0 && height > 0;
    }

    public bool TryGetVideoCursor(out int x, out int y)
    {
        x = 960;
        y = 540;
        return Played;
    }

    public void SetAudioState(int volume, PlaybackAudioState audioState)
    {
        ApplyAudioState(volume, audioState);
    }

    public void SimulateAudioStateReapplied()
    {
        ApplyAudioState(RequestedVolume, AudioState);
        AudioStateReapplied?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAudioState(int volume, PlaybackAudioState audioState)
    {
        RequestedVolume = volume;
        AudioState = audioState;
        if (IgnoreAudibleWhilePaused && Paused && audioState == PlaybackAudioState.Audible)
        {
            Volume = 0;
            if (!IgnoreSetMutedUntilPlayed || Played)
            {
                Muted = true;
            }

            AudioTrackEnabled = false;
            LogAudioCall(volume, audioState);
            ApplySharedAudioState();
            return;
        }

        Volume = audioState == PlaybackAudioState.Audible ? volume : 0;
        if (!IgnoreSetMutedUntilPlayed || Played)
        {
            Muted = audioState != PlaybackAudioState.Audible;
        }

        AudioTrackEnabled = audioState == PlaybackAudioState.Audible;
        LogAudioCall(volume, audioState);
        ApplySharedAudioState();
    }

    private void LogAudioCall(int volume, PlaybackAudioState audioState)
    {
        AudioCallLog?.Enqueue(new FakePlaybackAudioCall(this, volume, audioState));
    }

    private void ApplySharedAudioState()
    {
        if (SharedAudioState is null)
        {
            return;
        }

        SharedAudioState.Volume = Volume;
        SharedAudioState.Muted = Muted;
        SharedAudioState.AudioState = AudioState;
    }

    public void RaiseVideoOutputRebound()
    {
        VideoOutputRebound?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeChatClientFactory : IChatClientFactory
{
    public FakeChatClient Client { get; } = new();

    public IChatClient Create(PlatformKind platform) => Client;
}

internal sealed class FakeChatClient : IChatClient, IChatHistoryBackfillClient, ITwitchPredictionClient
{
    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<string>? StatusChanged { add { } remove { } }
    public event EventHandler<TwitchPrediction>? PredictionReceived;
    public event EventHandler<TwitchPredictionAccessState>? PredictionAccessChanged;
    public string? CurrentUsername => "tester";
    public List<string> SentMessages { get; } = [];
    public List<TwitchPredictionCreateRequest> PredictionCreateRequests { get; } = [];
    public List<string> PredictionLockRequests { get; } = [];
    public List<string> PredictionCancelRequests { get; } = [];
    public List<(string PredictionId, string WinningOutcomeId)> PredictionResolveRequests { get; } = [];
    public List<DateTimeOffset> BackfillUntilRequests { get; } = [];
    public List<ChatBackfillRangeRequest> BackfillRangeRequests { get; } = [];
    public List<ChatMessage> BackfillMessages { get; } = [];
    public bool? BackfillCoveredRequestedRange { get; set; }
    public DateTimeOffset? BackfillCoveredFromTimestampUtc { get; set; }
    public DateTimeOffset? BackfillCoveredThroughTimestampUtc { get; set; }
    public Func<FakeChatClient, DateTimeOffset, DateTimeOffset, CancellationToken, Task<ChatHistoryBackfillResult>>? BackfillHandler { get; set; }
    public bool EchoSentMessages { get; set; }
    public Func<FakeChatClient, StreamTarget, CancellationToken, Task>? ConnectHandler { get; set; }
    public bool Connected { get; private set; }
    public int ConnectCount { get; private set; }
    public int DisposeCount { get; private set; }
    public TwitchPredictionAccessState PredictionAccess { get; private set; } = TwitchPredictionAccessState.Pending;
    private StreamTarget? connectedTarget;

    public async Task ConnectAsync(StreamTarget target, CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        connectedTarget = target;
        if (ConnectHandler is { } handler)
        {
            await handler(this, target, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Connected = true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Connected = false;
        connectedTarget = null;
        return Task.CompletedTask;
    }

    public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!Connected)
        {
            throw new InvalidOperationException("Fake chat is not connected.");
        }

        SentMessages.Add(message);
        if (EchoSentMessages && connectedTarget is not null)
        {
            Receive(new ChatMessage(
                connectedTarget.Platform,
                connectedTarget.Channel,
                CurrentUsername!,
                message,
                DateTimeOffset.Now,
                "#48C7B5"));
        }

        return Task.CompletedTask;
    }

    public void Receive(ChatMessage message)
    {
        MessageReceived?.Invoke(this, message);
    }

    public void SetPredictionAccess(TwitchPredictionAccessState access)
    {
        PredictionAccess = access;
        PredictionAccessChanged?.Invoke(this, access);
    }

    public void ReceivePrediction(TwitchPrediction prediction)
    {
        PredictionReceived?.Invoke(this, prediction);
    }

    public Task<TwitchPrediction> CreatePredictionAsync(
        TwitchPredictionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        PredictionCreateRequests.Add(request);
        var prediction = CreateFakePrediction("fake-prediction", request.Title, request.Outcomes);
        ReceivePrediction(prediction);
        return Task.FromResult(prediction);
    }

    public Task<TwitchPrediction> LockPredictionAsync(string predictionId, CancellationToken cancellationToken = default)
    {
        PredictionLockRequests.Add(predictionId);
        var prediction = CreateFakePrediction(predictionId, "Locked prediction", ["Yes", "No"]) with
        {
            Status = TwitchPredictionStatus.Locked,
            LocksAtUtc = DateTimeOffset.UtcNow
        };
        ReceivePrediction(prediction);
        return Task.FromResult(prediction);
    }

    public Task<TwitchPrediction> CancelPredictionAsync(string predictionId, CancellationToken cancellationToken = default)
    {
        PredictionCancelRequests.Add(predictionId);
        var prediction = CreateFakePrediction(predictionId, "Canceled prediction", ["Yes", "No"]) with
        {
            Status = TwitchPredictionStatus.Canceled,
            EndedAtUtc = DateTimeOffset.UtcNow
        };
        ReceivePrediction(prediction);
        return Task.FromResult(prediction);
    }

    public Task<TwitchPrediction> ResolvePredictionAsync(
        string predictionId,
        string winningOutcomeId,
        CancellationToken cancellationToken = default)
    {
        PredictionResolveRequests.Add((predictionId, winningOutcomeId));
        var prediction = CreateFakePrediction(predictionId, "Resolved prediction", ["Yes", "No"]) with
        {
            WinningOutcomeId = winningOutcomeId,
            Status = TwitchPredictionStatus.Resolved,
            EndedAtUtc = DateTimeOffset.UtcNow
        };
        ReceivePrediction(prediction);
        return Task.FromResult(prediction);
    }

    public Task<ChatHistoryBackfillResult> BackfillRecentChatRangeAsync(
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default)
    {
        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        if (throughTimestampUtc < fromTimestampUtc)
        {
            throughTimestampUtc = fromTimestampUtc;
        }

        BackfillUntilRequests.Add(fromTimestampUtc);
        BackfillRangeRequests.Add(new ChatBackfillRangeRequest(fromTimestampUtc, throughTimestampUtc));
        if (BackfillHandler is { } handler)
        {
            return handler(this, fromTimestampUtc, throughTimestampUtc, cancellationToken);
        }

        var loadedMessages = new List<ChatMessage>();
        DateTimeOffset? loadedThroughTimestampUtc = null;
        var backfillMessages = BackfillMessages.ToArray();
        foreach (var message in backfillMessages
            .OrderBy(message => message.Timestamp)
            .Where(message =>
            {
                var timestampUtc = message.Timestamp.ToUniversalTime();
                return timestampUtc >= fromTimestampUtc && timestampUtc <= throughTimestampUtc;
            }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Receive(message);
            loadedMessages.Add(message);
            var timestampUtc = message.Timestamp.ToUniversalTime();
            loadedThroughTimestampUtc = loadedThroughTimestampUtc is { } loadedThrough &&
                loadedThrough >= timestampUtc
                    ? loadedThrough
                    : timestampUtc;
        }

        DateTimeOffset? coveredFromTimestampUtc = null;
        DateTimeOffset? coveredThroughTimestampUtc = null;
        if (BackfillCoveredFromTimestampUtc is { } configuredFrom &&
            BackfillCoveredThroughTimestampUtc is { } configuredThrough)
        {
            coveredFromTimestampUtc = configuredFrom.ToUniversalTime();
            coveredThroughTimestampUtc = configuredThrough.ToUniversalTime();
            if (coveredThroughTimestampUtc < coveredFromTimestampUtc)
            {
                coveredThroughTimestampUtc = coveredFromTimestampUtc;
            }
        }
        else if (BackfillCoveredRequestedRange == true)
        {
            coveredFromTimestampUtc = fromTimestampUtc;
            coveredThroughTimestampUtc = throughTimestampUtc;
        }
        else if (loadedThroughTimestampUtc is { } loadedThrough)
        {
            coveredFromTimestampUtc = fromTimestampUtc;
            coveredThroughTimestampUtc = loadedThrough < fromTimestampUtc ? fromTimestampUtc : loadedThrough;
        }

        var coveredRequestedRange = BackfillCoveredRequestedRange ??
            (coveredFromTimestampUtc <= fromTimestampUtc && coveredThroughTimestampUtc >= throughTimestampUtc);
        return Task.FromResult(new ChatHistoryBackfillResult(
            Attempted: true,
            LoadedMessageCount: loadedMessages.Count,
            CoveredRequestedRange: coveredRequestedRange,
            CoveredFromTimestampUtc: coveredFromTimestampUtc,
            CoveredThroughTimestampUtc: coveredThroughTimestampUtc,
            Messages: loadedMessages));
    }

    private static TwitchPrediction CreateFakePrediction(
        string id,
        string title,
        IReadOnlyList<string> outcomeTitles)
    {
        var outcomes = outcomeTitles
            .Select((outcomeTitle, index) => new TwitchPredictionOutcome(
                $"outcome-{index + 1}",
                outcomeTitle,
                index % 2 == 0 ? "blue" : "pink",
                index + 1,
                (index + 1) * 100,
                []))
            .ToArray();

        return new TwitchPrediction(
            id,
            "broadcaster-1",
            "streamer",
            "Streamer",
            title,
            null,
            outcomes,
            120,
            TwitchPredictionStatus.Active,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(2),
            null);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        Connected = false;
        connectedTarget = null;
        return ValueTask.CompletedTask;
    }
}

internal readonly record struct ChatBackfillRangeRequest(
    DateTimeOffset FromTimestampUtc,
    DateTimeOffset ThroughTimestampUtc);

internal sealed class FakeKickChatHistoryProvider : IKickChatHistoryProvider
{
    private readonly object gate = new();
    private readonly List<KickHistoryBackfillRequest> requests = [];
    public List<ChatMessage> BackfillMessages { get; } = [];
    public bool FilterMessagesToRequest { get; set; } = true;
    public bool? BackfillCoveredRequestedRange { get; set; }
    public DateTimeOffset? BackfillCoveredFromTimestampUtc { get; set; }
    public DateTimeOffset? BackfillCoveredThroughTimestampUtc { get; set; }
    public Func<FakeKickChatHistoryProvider, StreamTarget, ChatSettings, DateTimeOffset, DateTimeOffset, CancellationToken, Task<ChatHistoryBackfillResult>>? BackfillHandler { get; set; }

    public IReadOnlyList<KickHistoryBackfillRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<ChatHistoryBackfillResult> BackfillRecentChatRangeAsync(
        StreamTarget target,
        ChatSettings settings,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default)
    {
        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        if (throughTimestampUtc < fromTimestampUtc)
        {
            throughTimestampUtc = fromTimestampUtc;
        }

        lock (gate)
        {
            requests.Add(new KickHistoryBackfillRequest(target, fromTimestampUtc, throughTimestampUtc));
        }

        if (BackfillHandler is { } handler)
        {
            return handler(this, target, settings, fromTimestampUtc, throughTimestampUtc, cancellationToken);
        }

        var messages = BackfillMessages
            .OrderBy(message => message.Timestamp)
            .Where(message =>
            {
                if (!FilterMessagesToRequest)
                {
                    return true;
                }

                var timestampUtc = message.Timestamp.ToUniversalTime();
                return timestampUtc >= fromTimestampUtc && timestampUtc <= throughTimestampUtc;
            })
            .ToArray();

        DateTimeOffset? coveredFromTimestampUtc = null;
        DateTimeOffset? coveredThroughTimestampUtc = null;
        if (BackfillCoveredFromTimestampUtc is { } configuredFrom &&
            BackfillCoveredThroughTimestampUtc is { } configuredThrough)
        {
            coveredFromTimestampUtc = configuredFrom.ToUniversalTime();
            coveredThroughTimestampUtc = configuredThrough.ToUniversalTime();
            if (coveredThroughTimestampUtc < coveredFromTimestampUtc)
            {
                coveredThroughTimestampUtc = coveredFromTimestampUtc;
            }
        }
        else if (BackfillCoveredRequestedRange == true)
        {
            coveredFromTimestampUtc = fromTimestampUtc;
            coveredThroughTimestampUtc = throughTimestampUtc;
        }
        else if (messages.Length > 0)
        {
            coveredFromTimestampUtc = fromTimestampUtc;
            coveredThroughTimestampUtc = messages.Max(message => message.Timestamp).ToUniversalTime();
            if (coveredThroughTimestampUtc < fromTimestampUtc)
            {
                coveredThroughTimestampUtc = fromTimestampUtc;
            }
        }

        var coveredRequestedRange = BackfillCoveredRequestedRange ??
            (coveredFromTimestampUtc <= fromTimestampUtc && coveredThroughTimestampUtc >= throughTimestampUtc);
        return Task.FromResult(new ChatHistoryBackfillResult(
            Attempted: true,
            LoadedMessageCount: messages.Length,
            CoveredRequestedRange: coveredRequestedRange,
            CoveredFromTimestampUtc: coveredFromTimestampUtc,
            CoveredThroughTimestampUtc: coveredThroughTimestampUtc,
            Messages: messages));
    }
}

internal readonly record struct KickHistoryBackfillRequest(
    StreamTarget Target,
    DateTimeOffset FromTimestampUtc,
    DateTimeOffset ThroughTimestampUtc);

internal sealed class FakeReplayResolver : IReplayResolver
{
    private readonly object gate = new();
    private readonly Queue<ReplaySessionInfo> results = new();
    private ReplaySessionInfo currentResult;

    public FakeReplayResolver(params ReplaySessionInfo[] results)
    {
        if (results.Length == 0)
        {
            throw new ArgumentException("At least one replay result is required.", nameof(results));
        }

        foreach (var result in results)
        {
            this.results.Enqueue(result);
        }

        currentResult = results[^1];
    }

    public int CallCount { get; private set; }

    public Task<ReplaySessionInfo> ResolveCurrentReplayAsync(
        StreamTarget target,
        string quality,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ReplaySessionInfo result;
        lock (gate)
        {
            CallCount++;
            if (results.Count > 0)
            {
                currentResult = results.Dequeue();
            }

            result = currentResult;
        }

        return Task.FromResult(result);
    }
}

internal sealed class BlockingReplayResolver : IReplayResolver
{
    private readonly ReplaySessionInfo result;
    private readonly Task releaseTask;
    private int callCount;

    public BlockingReplayResolver(ReplaySessionInfo result, Task releaseTask)
    {
        this.result = result;
        this.releaseTask = releaseTask;
    }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int CallCount => Volatile.Read(ref callCount);

    public async Task<ReplaySessionInfo> ResolveCurrentReplayAsync(
        StreamTarget target,
        string quality,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref callCount);
        Started.TrySetResult();
        await releaseTask.WaitAsync(cancellationToken);
        return result;
    }
}

internal sealed class FakeReplayChatProvider : IReplayChatProvider
{
    private readonly ReplayChatLoadResult result;
    private readonly object gate = new();
    private readonly List<ReplaySessionInfo> requests = [];
    private readonly List<TimeSpan> offsets = [];

    public FakeReplayChatProvider(ReplayChatLoadResult result)
    {
        this.result = result;
    }

    public int CallCount
    {
        get
        {
            lock (gate)
            {
                return requests.Count;
            }
        }
    }

    public IReadOnlyList<ReplaySessionInfo> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public IReadOnlyList<TimeSpan> Offsets
    {
        get
        {
            lock (gate)
            {
                return offsets.ToArray();
            }
        }
    }

    public Task<ReplayChatLoadResult> LoadChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            requests.Add(replay);
            offsets.Add(offset);
        }

        return Task.FromResult(result);
    }
}

internal sealed class FakeKickEventSubscriptionService : IKickEventSubscriptionService
{
    private readonly KickEventSubscriptionEnsureResult result;
    private readonly object gate = new();
    private readonly List<StreamTarget> requests = [];

    public FakeKickEventSubscriptionService(KickEventSubscriptionEnsureResult result)
    {
        this.result = result;
    }

    public int CallCount
    {
        get
        {
            lock (gate)
            {
                return requests.Count;
            }
        }
    }

    public IReadOnlyList<StreamTarget> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<KickEventSubscriptionEnsureResult> EnsureChatMessageSentSubscriptionAsync(
        StreamTarget target,
        ChatSettings settings,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            requests.Add(target);
        }

        return Task.FromResult(result);
    }
}

internal sealed class BlockingReplayChatProvider : IReplayChatProvider
{
    private readonly object gate = new();
    private readonly TaskCompletionSource firstLoadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource secondLoadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int callCount;

    public TaskCompletionSource FirstLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondLoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstLoadCancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstLoadReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount
    {
        get
        {
            lock (gate)
            {
                return callCount;
            }
        }
    }

    public void ReleaseFirstLoad()
    {
        firstLoadRelease.SetResult();
    }

    public void ReleaseSecondLoad()
    {
        secondLoadRelease.SetResult();
    }

    public async Task<ReplayChatLoadResult> LoadChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        CancellationToken cancellationToken = default)
    {
        int currentCall;
        lock (gate)
        {
            callCount++;
            currentCall = callCount;
        }

        if (currentCall == 1)
        {
            FirstLoadStarted.SetResult();
            using var cancellationRegistration = cancellationToken.Register(
                () => FirstLoadCancellationRequested.TrySetResult());
            await firstLoadRelease.Task;
            var result = CreateResult(replay, offset, "seek A chat", "seek-a-chat");
            FirstLoadReturned.SetResult();
            return result;
        }

        if (currentCall == 2)
        {
            SecondLoadStarted.SetResult();
            await secondLoadRelease.Task.WaitAsync(cancellationToken);
            return CreateResult(replay, offset, "seek B chat", "seek-b-chat");
        }

        return ReplayChatLoadResult.Available([], TimeSpan.Zero, replay.Duration);
    }

    private static ReplayChatLoadResult CreateResult(
        ReplaySessionInfo replay,
        TimeSpan offset,
        string message,
        string messageId)
    {
        return ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    offset,
                    new ChatMessage(
                        replay.Platform,
                        replay.Channel,
                        "viewer",
                        message,
                        DateTimeOffset.UtcNow,
                        MessageId: messageId))
            ],
            offset - TimeSpan.FromMinutes(1),
            offset + TimeSpan.FromMinutes(1));
    }
}
