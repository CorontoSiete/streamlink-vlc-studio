using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf;

/// <summary>Small native hit-testing seam used by the two video windows.</summary>
internal interface IWindowHitTester
{
    IntPtr WindowFromPoint(int screenX, int screenY);
    IntPtr GetRootWindow(IntPtr hwnd);
    IntPtr GetRootOwnerWindow(IntPtr hwnd);
    bool IsChild(IntPtr parent, IntPtr child);
}

internal sealed partial class NativeWindowHitTester : IWindowHitTester
{
    public static NativeWindowHitTester Instance { get; } = new();

    private NativeWindowHitTester()
    {
    }

    public IntPtr WindowFromPoint(int screenX, int screenY) =>
        NativeMethods.WindowFromPoint(new WindowPoint { X = screenX, Y = screenY });

    public IntPtr GetRootWindow(IntPtr hwnd) => NativeMethods.GetAncestor(hwnd, 2);

    public IntPtr GetRootOwnerWindow(IntPtr hwnd) => NativeMethods.GetAncestor(hwnd, 3);

    public bool IsChild(IntPtr parent, IntPtr child) => NativeMethods.IsChild(parent, child);

    private static partial class NativeMethods
    {
        [LibraryImport("user32")]
        internal static partial IntPtr WindowFromPoint(WindowPoint point);

        [LibraryImport("user32")]
        internal static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [LibraryImport("user32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsChild(IntPtr parent, IntPtr child);
    }
}

internal static class WindowHitTestPolicy
{
    internal static bool IsPointInWindow(
        IWindowHitTester hitTester,
        IntPtr hostWindow,
        int screenX,
        int screenY,
        bool includeOwnedPopups)
    {
        ArgumentNullException.ThrowIfNull(hitTester);
        if (hostWindow == IntPtr.Zero)
        {
            return false;
        }

        var pointWindow = hitTester.WindowFromPoint(screenX, screenY);
        return IsWindowOwnedBy(hitTester, hostWindow, pointWindow, includeOwnedPopups);
    }

    internal static bool IsWindowOwnedBy(
        IWindowHitTester hitTester,
        IntPtr hostWindow,
        IntPtr pointWindow,
        bool includeOwnedPopups)
    {
        if (hostWindow == IntPtr.Zero || pointWindow == IntPtr.Zero)
        {
            return false;
        }

        return pointWindow == hostWindow ||
            hitTester.IsChild(hostWindow, pointWindow) ||
            hitTester.GetRootWindow(pointWindow) == hostWindow ||
            (includeOwnedPopups && hitTester.GetRootOwnerWindow(pointWindow) == hostWindow);
    }
}
