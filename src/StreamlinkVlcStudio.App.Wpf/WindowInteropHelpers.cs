using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace StreamlinkVlcStudio.App.Wpf;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowPoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MinMaxInfo
{
    public WindowPoint Reserved;
    public WindowPoint MaxSize;
    public WindowPoint MaxPosition;
    public WindowPoint MinTrackSize;
    public WindowPoint MaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRectangle
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int Size;
    public NativeRectangle Monitor;
    public NativeRectangle WorkArea;
    public uint Flags;
}

/// <summary>
/// Win32 window/monitor interop and screen-geometry helpers shared by the main and detached
/// video windows. Consolidates the structs, monitor lookups, hit tests, bounds checks, and DPI
/// transforms that were previously duplicated in both windows.
/// </summary>
internal static partial class WindowInteropHelpers
{
    internal const uint MonitorDefaultToNearest = 0x00000002;

    [LibraryImport("user32")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport("user32")]
    private static partial IntPtr MonitorFromPoint(WindowPoint point, uint dwFlags);

    [LibraryImport("user32")]
    private static partial IntPtr MonitorFromRect(ref NativeRectangle rectangle, uint dwFlags);

    [LibraryImport("user32", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    /// <summary>Reads monitor info for the monitor nearest to <paramref name="hwnd"/>.</summary>
    public static bool TryGetMonitorInfo(IntPtr hwnd, out MonitorInfo monitorInfo)
    {
        monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo);
    }

    /// <summary>Reads monitor info for an explicit monitor handle.</summary>
    public static bool TryGetMonitorInfoForMonitor(IntPtr monitor, out MonitorInfo monitorInfo)
    {
        monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo);
    }

    /// <summary>Reads monitor info for the monitor nearest to a native screen point.</summary>
    public static bool TryGetMonitorInfoForPoint(WindowPoint point, out MonitorInfo monitorInfo)
    {
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        return TryGetMonitorInfoForMonitor(monitor, out monitorInfo);
    }

    /// <summary>Reads monitor info for the monitor nearest to a native screen rectangle.</summary>
    public static bool TryGetMonitorInfoForRect(NativeRectangle rectangle, out MonitorInfo monitorInfo)
    {
        var monitor = MonitorFromRect(ref rectangle, MonitorDefaultToNearest);
        return TryGetMonitorInfoForMonitor(monitor, out monitorInfo);
    }

    /// <summary>
    /// Applies monitor-sized max bounds to a WM_GETMINMAXINFO payload so a borderless window
    /// maximizes to the monitor (full bounds in fullscreen, work area otherwise).
    /// </summary>
    public static void ApplyMonitorMaxInfo(IntPtr hwnd, IntPtr minMaxInfoPointer, bool useFullMonitor)
    {
        if (minMaxInfoPointer == IntPtr.Zero ||
            !TryGetMonitorInfo(hwnd, out var monitorInfo))
        {
            return;
        }

        var targetBounds = useFullMonitor ? monitorInfo.Monitor : monitorInfo.WorkArea;
        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        minMaxInfo.MaxPosition.X = targetBounds.Left - monitorInfo.Monitor.Left;
        minMaxInfo.MaxPosition.Y = targetBounds.Top - monitorInfo.Monitor.Top;
        minMaxInfo.MaxSize.X = targetBounds.Right - targetBounds.Left;
        minMaxInfo.MaxSize.Y = targetBounds.Bottom - targetBounds.Top;
        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
    }

    /// <summary>
    /// True when the visible, sized element's on-screen bounds contain the screen-space point
    /// (half-open: left/top inclusive, right/bottom exclusive).
    /// </summary>
    public static bool IsScreenPointOverElement(FrameworkElement element, double screenX, double screenY)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var topLeft = element.PointToScreen(new Point(0, 0));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        var left = Math.Min(topLeft.X, bottomRight.X);
        var right = Math.Max(topLeft.X, bottomRight.X);
        var top = Math.Min(topLeft.Y, bottomRight.Y);
        var bottom = Math.Max(topLeft.Y, bottomRight.Y);

        return screenX >= left &&
            screenX < right &&
            screenY >= top &&
            screenY < bottom;
    }

    /// <summary>True for finite, positive-size bounds (also rejects <see cref="Rect.Empty"/>).</summary>
    public static bool IsUsableWindowBounds(Rect bounds)
    {
        return double.IsFinite(bounds.Left) &&
            double.IsFinite(bounds.Top) &&
            double.IsFinite(bounds.Width) &&
            double.IsFinite(bounds.Height) &&
            bounds.Width > 0 &&
            bounds.Height > 0;
    }

    /// <summary>Transforms a device-pixel screen point into device-independent units.</summary>
    public static Point ToDeviceIndependentPoint(this Visual visual, Point screenPoint)
    {
        var transform = PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return transform.Transform(screenPoint);
    }

    /// <summary>Transforms a device-independent point into device pixels.</summary>
    public static Point ToDevicePoint(this Visual visual, Point point)
    {
        var transform = PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        return transform.Transform(point);
    }

    /// <summary>Transforms a device-pixel rectangle into device-independent units.</summary>
    public static Rect ToDeviceIndependentRect(this Visual visual, NativeRectangle rectangle)
    {
        var transform = PresentationSource.FromVisual(visual)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(rectangle.Left, rectangle.Top));
        var bottomRight = transform.Transform(new Point(rectangle.Right, rectangle.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
