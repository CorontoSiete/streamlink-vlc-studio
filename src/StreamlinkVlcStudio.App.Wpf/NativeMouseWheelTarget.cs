using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf;

/// <summary>Captures the native recipient before wheel input leaves the hook thread.</summary>
internal static partial class NativeMouseWheelTarget
{
    private static readonly ConcurrentDictionary<IntPtr, bool> VideoWindows = new();

    internal static void RegisterWindow(IntPtr hwnd, bool acceptsInactiveWheel)
    {
        if (hwnd != IntPtr.Zero)
        {
            VideoWindows[hwnd] = acceptsInactiveWheel;
        }
    }

    internal static void UnregisterWindow(IntPtr hwnd) => VideoWindows.TryRemove(hwnd, out _);

    internal static Action? CaptureFallback(LowLevelMouseHookEvent hookEvent)
    {
        var target = WindowFromPoint(new WindowPoint { X = hookEvent.ScreenX, Y = hookEvent.ScreenY });
        var threadId = GetWindowThreadProcessId(target, out var processId);
        if (target == IntPtr.Zero || threadId == 0 || processId != (uint)Environment.ProcessId ||
            !CanRouteTarget(target))
        {
            return null;
        }

        // Only native video and the volume OSD need interception. Ordinary WPF
        // controls keep Windows' focus/hover routing and their existing wheel handlers.

        // The low-level hook's MouseData contains only the wheel delta. Preserve
        // button/modifier state now, rather than sampling it after a dispatcher stall.
        var keyState = 0;
        if (IsKeyDown(0x01)) keyState |= 0x0001; // MK_LBUTTON
        if (IsKeyDown(0x02)) keyState |= 0x0002; // MK_RBUTTON
        if (IsKeyDown(0x10)) keyState |= 0x0004; // MK_SHIFT
        if (IsKeyDown(0x11)) keyState |= 0x0008; // MK_CONTROL
        if (IsKeyDown(0x04)) keyState |= 0x0010; // MK_MBUTTON
        if (IsKeyDown(0x05)) keyState |= 0x0020; // MK_XBUTTON1
        if (IsKeyDown(0x06)) keyState |= 0x0040; // MK_XBUTTON2
        var wParam = new IntPtr(unchecked((hookEvent.WheelDelta << 16) | keyState));
        var lParam = new IntPtr(unchecked((hookEvent.ScreenY << 16) | (hookEvent.ScreenX & 0xFFFF)));

        return () =>
        {
            // A closed/recreated window must not redirect retained input to another
            // process. Posting bypasses WH_MOUSE_LL, so fallback cannot be recaptured.
            if (GetWindowThreadProcessId(target, out var currentProcessId) == threadId &&
                currentProcessId == processId)
            {
                _ = PostMessage(target, LowLevelMouseHookEvent.WmMouseWheel, wParam, lParam);
            }
        };
    }

    private static bool CanRouteTarget(IntPtr target)
    {
        foreach (var (videoWindow, acceptsInactiveWheel) in VideoWindows)
        {
            if (target == videoWindow || IsChild(videoWindow, target))
            {
                // Detached video has always accepted hovered input without activation.
                // For inactive main windows leave routing to Windows, including the
                // user's setting for whether inactive windows receive wheel input.
                return acceptsInactiveWheel || GetAncestor(videoWindow, 3) == GetForegroundWindow();
            }
        }

        return false;
    }

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [LibraryImport("user32")]
    private static partial IntPtr WindowFromPoint(WindowPoint point);

    [LibraryImport("user32")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsChild(IntPtr parent, IntPtr child);

    [LibraryImport("user32")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32")]
    private static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
