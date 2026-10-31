using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWindowPosition
{
    public IntPtr Window;
    public IntPtr InsertAfter;
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public uint Flags;
}

internal static partial class WindowInteropHelpers
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRectangle rectangle);

    /// <summary>
    /// Constrains the final WM_WINDOWPOSCHANGING placement of a maximized borderless window.
    /// WM_GETMINMAXINFO alone is insufficient: Windows can adjust its primary-monitor-based
    /// maximize dimensions again when placing the window on a larger secondary monitor.
    /// </summary>
    public static bool ConstrainMaximizedWindowPosition(IntPtr hwnd, IntPtr positionPointer)
    {
        // Native state is authoritative here; WPF's WindowState can still describe the
        // preceding state while a maximize, restore, or minimize transition is in progress.
        if (positionPointer == IntPtr.Zero || !IsZoomed(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        var position = Marshal.PtrToStructure<NativeWindowPosition>(positionPointer);
        if ((position.Flags & (SwpNoMove | SwpNoSize)) == (SwpNoMove | SwpNoSize))
        {
            return false;
        }

        MonitorInfo monitorInfo;
        if ((position.Flags & SwpNoMove) != 0)
        {
            if (!TryGetMonitorInfo(hwnd, out monitorInfo))
            {
                return false;
            }
        }
        else
        {
            var width = position.Width;
            var height = position.Height;
            if ((position.Flags & SwpNoSize) != 0)
            {
                if (!GetWindowRect(hwnd, out var currentBounds))
                {
                    return false;
                }

                width = currentBounds.Right - currentBounds.Left;
                height = currentBounds.Bottom - currentBounds.Top;
            }

            // Use the destination rectangle, not the old HWND monitor or cursor. This
            // also handles Win+Shift+Arrow and moves between monitors with different DPI.
            var proposedBounds = new NativeRectangle
            {
                Left = position.X,
                Top = position.Y,
                Right = position.X + width,
                Bottom = position.Y + height
            };
            if (!TryGetMonitorInfoForRect(proposedBounds, out monitorInfo))
            {
                return false;
            }
        }

        if (!ConstrainMaximizedWindowPosition(ref position, monitorInfo.WorkArea))
        {
            return false;
        }

        Marshal.StructureToPtr(position, positionPointer, false);
        return true;
    }

    internal static bool ConstrainMaximizedWindowPosition(
        ref NativeWindowPosition position, NativeRectangle workArea)
    {
        var width = workArea.Right - workArea.Left;
        var height = workArea.Bottom - workArea.Top;
        if (width <= 0 || height <= 0 ||
            (position.Flags & (SwpNoMove | SwpNoSize)) == (SwpNoMove | SwpNoSize))
        {
            return false;
        }

        position.X = workArea.Left;
        position.Y = workArea.Top;
        position.Width = width;
        position.Height = height;
        position.Flags &= ~(SwpNoMove | SwpNoSize);
        return true;
    }
}
