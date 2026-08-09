using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed partial class VideoSurface : HwndHost
{
    private const string VideoSurfaceWindowClassName = "StreamlinkVlcStudioVideoSurface";
    private const int CsDoubleClicks = 0x0008;
    private const int ErrorClassAlreadyExists = 1410;
    private const int BlackBrush = 4;
    private const int WmEraseBackground = 0x0014;
    private const int WmSetCursor = 0x0020;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int SwHide = 0;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private static readonly object WindowClassGate = new();
    private static readonly NativeWindowProc RegisteredWindowProc = DefWindowProcCallback;
    private static bool windowClassRegistered;
    private IntPtr handle;
    private long lastLeftButtonDownAt = long.MinValue;
    private int lastLeftButtonDownX;
    private int lastLeftButtonDownY;
    private int lastNativeWidth = -1;
    private int lastNativeHeight = -1;
    private bool lastNativeVisible;

    public new IntPtr Handle => handle;
    public event EventHandler<VideoSurfaceMouseWheelEventArgs>? MouseWheelScrolled;
    public event EventHandler? SurfaceMouseLeftButtonPressed;
    public event EventHandler? MouseLeftButtonDoubleClicked;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeSetCursorRequested;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeMouseLeftButtonDown;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeMouseMoved;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeMouseLeftButtonUp;
    public event EventHandler<VideoSurfaceNativeMouseEventArgs>? NativeMouseRightButtonDown;

    public VideoSurface()
    {
        IsVisibleChanged += (_, _) => SyncNativeBounds();
        SizeChanged += (_, _) => SyncNativeBounds();
        LayoutUpdated += (_, _) => SyncNativeBounds();
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClassRegistered();

        handle = CreateWindowEx(
            0,
            VideoSurfaceWindowClassName,
            "",
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to create the video surface window.");
        }

        SyncNativeBounds();
        return new HandleRef(this, handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
            handle = IntPtr.Zero;
        }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEraseBackground)
        {
            FillClientArea(hwnd, wParam);
            handled = true;
            return new IntPtr(1);
        }

        if (msg == WmSetCursor && TryRaiseNativeSetCursorRequested(out var cursorResult))
        {
            handled = true;
            return cursorResult;
        }

        if (msg == WmLeftButtonDoubleClick)
        {
            ResetLastLeftButtonDown();
            MouseLeftButtonDoubleClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (msg == WmLeftButtonDown)
        {
            if (TryRaiseNativeMouseLeftButtonDown(hwnd, lParam, out var mouseDownResult))
            {
                handled = true;
                return mouseDownResult;
            }

            _ = SetCapture(hwnd);
            SurfaceMouseLeftButtonPressed?.Invoke(this, EventArgs.Empty);
            if (IsLeftButtonDoubleClick(lParam))
            {
                ResetLastLeftButtonDown();
                MouseLeftButtonDoubleClicked?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                CaptureLastLeftButtonDown(lParam);
            }
        }
        else if (msg == WmMouseMove)
        {
            if (TryRaiseNativeMouseEvent(hwnd, lParam, NativeMouseMoved, out var mouseMoveResult))
            {
                handled = true;
                return mouseMoveResult;
            }
        }
        else if (msg == WmLeftButtonUp)
        {
            var mouseUpHandled = TryRaiseNativeMouseEvent(hwnd, lParam, NativeMouseLeftButtonUp, out var mouseUpResult);
            if (GetCapture() == hwnd)
            {
                ReleaseCapture();
            }

            if (mouseUpHandled)
            {
                handled = true;
                return mouseUpResult;
            }
        }
        else if (msg == WmRightButtonDown &&
                 TryRaiseNativeMouseEvent(hwnd, lParam, NativeMouseRightButtonDown, out var rightButtonResult))
        {
            handled = true;
            return rightButtonResult;
        }

        if (msg == WmMouseWheel)
        {
            var delta = GetWheelDelta(wParam);
            if (delta != 0)
            {
                MouseWheelScrolled?.Invoke(this, new VideoSurfaceMouseWheelEventArgs(delta));
                handled = true;
                return IntPtr.Zero;
            }
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private bool TryRaiseNativeMouseEvent(
        IntPtr hwnd,
        IntPtr lParam,
        EventHandler<VideoSurfaceNativeMouseEventArgs>? handler,
        out IntPtr result)
    {
        result = IntPtr.Zero;
        if (handler is null)
        {
            return false;
        }

        var screenPoint = new NativePoint
        {
            X = GetLParamX(lParam),
            Y = GetLParamY(lParam)
        };
        if (!ClientToScreen(hwnd, ref screenPoint))
        {
            return false;
        }

        var args = new VideoSurfaceNativeMouseEventArgs(screenPoint.X, screenPoint.Y);
        handler(this, args);
        if (!args.Handled)
        {
            return false;
        }

        result = args.Result;
        return true;
    }

    private bool TryRaiseNativeSetCursorRequested(out IntPtr result)
    {
        result = IntPtr.Zero;
        if (NativeSetCursorRequested is not { } handler ||
            !GetCursorPos(out var screenPoint))
        {
            return false;
        }

        var args = new VideoSurfaceNativeMouseEventArgs(screenPoint.X, screenPoint.Y);
        handler(this, args);
        if (!args.Handled)
        {
            return false;
        }

        result = args.Result;
        return true;
    }

    private bool TryRaiseNativeMouseLeftButtonDown(IntPtr hwnd, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (NativeMouseLeftButtonDown is not { } handler)
        {
            return false;
        }

        var screenPoint = new NativePoint
        {
            X = GetLParamX(lParam),
            Y = GetLParamY(lParam)
        };
        if (!ClientToScreen(hwnd, ref screenPoint))
        {
            return false;
        }

        var args = new VideoSurfaceNativeMouseEventArgs(screenPoint.X, screenPoint.Y);
        handler(this, args);
        if (!args.Handled)
        {
            return false;
        }

        result = args.Result;
        return true;
    }

    public void SyncNativeBounds()
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var source = PresentationSource.FromVisual(this);
        var visible = IsVisible &&
            ActualWidth > 0 &&
            ActualHeight > 0;
        if (!visible)
        {
            _ = ShowWindow(handle, SwHide);
            lastNativeVisible = false;
            return;
        }

        if (source is null)
        {
            return;
        }

        var transformToDevice = source.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var width = Math.Max(1, (int)Math.Round(ActualWidth * transformToDevice.M11));
        var height = Math.Max(1, (int)Math.Round(ActualHeight * transformToDevice.M22));

        if (!lastNativeVisible ||
            width != lastNativeWidth ||
            height != lastNativeHeight)
        {
            _ = SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                width,
                height,
                SwpNoZOrder | SwpNoActivate | SwpShowWindow);
            lastNativeWidth = width;
            lastNativeHeight = height;
            lastNativeVisible = true;
        }

        ResizeDirectChildWindows(width, height);
    }

    private void ResizeDirectChildWindows(int width, int height)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        EnumChildWindows(
            handle,
            (childHandle, lParam) =>
            {
                if (GetParent(childHandle) == handle)
                {
                    _ = SetWindowPos(
                        childHandle,
                        IntPtr.Zero,
                        0,
                        0,
                        width,
                        height,
                        SwpNoZOrder | SwpNoActivate | SwpShowWindow);
                }

                return true;
            },
            IntPtr.Zero);
    }

    private static void EnsureWindowClassRegistered()
    {
        lock (WindowClassGate)
        {
            if (windowClassRegistered)
            {
                return;
            }

            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                Style = CsDoubleClicks,
                WindowProc = Marshal.GetFunctionPointerForDelegate(RegisteredWindowProc),
                Instance = GetModuleHandle(null),
                BackgroundBrush = GetStockObject(BlackBrush),
                ClassName = VideoSurfaceWindowClassName
            };

            var atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorClassAlreadyExists)
                {
                    throw new Win32Exception(error, "Failed to register the video surface window class.");
                }
            }

            windowClassRegistered = true;
        }
    }

    private static void FillClientArea(IntPtr hwnd, IntPtr deviceContext)
    {
        if (deviceContext == IntPtr.Zero ||
            !GetClientRect(hwnd, out var clientRect))
        {
            return;
        }

        _ = FillRect(deviceContext, ref clientRect, GetStockObject(BlackBrush));
    }

    private static IntPtr DefWindowProcCallback(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) =>
        DefWindowProc(hwnd, msg, wParam, lParam);

    private bool IsLeftButtonDoubleClick(IntPtr lParam)
    {
        if (lastLeftButtonDownAt == long.MinValue)
        {
            return false;
        }

        var now = Environment.TickCount64;
        var elapsed = now - lastLeftButtonDownAt;
        var x = GetLParamX(lParam);
        var y = GetLParamY(lParam);

        return elapsed >= 0 &&
            elapsed <= GetDoubleClickTime() &&
            Math.Abs(x - lastLeftButtonDownX) <= GetSystemMetrics(SmCxDoubleClick) &&
            Math.Abs(y - lastLeftButtonDownY) <= GetSystemMetrics(SmCyDoubleClick);
    }

    private void CaptureLastLeftButtonDown(IntPtr lParam)
    {
        lastLeftButtonDownAt = Environment.TickCount64;
        lastLeftButtonDownX = GetLParamX(lParam);
        lastLeftButtonDownY = GetLParamY(lParam);
    }

    private void ResetLastLeftButtonDown()
    {
        lastLeftButtonDownAt = long.MinValue;
        lastLeftButtonDownX = 0;
        lastLeftButtonDownY = 0;
    }

    private static int GetWheelDelta(IntPtr wParam)
    {
        var value = unchecked((long)wParam);
        return unchecked((short)((value >> 16) & 0xFFFF));
    }

    private static int GetLParamX(IntPtr lParam)
    {
        var value = unchecked((long)lParam);
        return unchecked((short)(value & 0xFFFF));
    }

    private static int GetLParamY(IntPtr lParam)
    {
        var value = unchecked((long)lParam);
        return unchecked((short)((value >> 16) & 0xFFFF));
    }

    private delegate IntPtr NativeWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumChildWindowProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [LibraryImport("user32", EntryPoint = "DefWindowProcW")]
    private static partial IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [LibraryImport("user32")]
    private static partial IntPtr SetCapture(IntPtr hwnd);

    [LibraryImport("user32")]
    private static partial IntPtr GetCapture();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(IntPtr parentHandle, EnumChildWindowProc callback, IntPtr lParam);

    [LibraryImport("user32")]
    private static partial IntPtr GetParent(IntPtr hwnd);

    [LibraryImport("user32")]
    private static partial int FillRect(IntPtr hdc, ref NativeRect rect, IntPtr brush);

    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr GetModuleHandle(string? moduleName);

    [LibraryImport("gdi32")]
    private static partial IntPtr GetStockObject(int objectType);

    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(
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

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [LibraryImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [LibraryImport("user32")]
    private static partial uint GetDoubleClickTime();

    [LibraryImport("user32")]
    private static partial int GetSystemMetrics(int nIndex);
}

public sealed class VideoSurfaceMouseWheelEventArgs : EventArgs
{
    public VideoSurfaceMouseWheelEventArgs(int delta)
    {
        Delta = delta;
    }

    public int Delta { get; }
}

public sealed class VideoSurfaceNativeMouseEventArgs : EventArgs
{
    public VideoSurfaceNativeMouseEventArgs(int screenX, int screenY)
    {
        ScreenX = screenX;
        ScreenY = screenY;
    }

    public int ScreenX { get; }
    public int ScreenY { get; }
    public bool Handled { get; set; }
    public IntPtr Result { get; set; }
}
