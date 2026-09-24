using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.App.Wpf;

/// <summary>
/// Tracks a left-button-down position and time so a following button-down can be classified as
/// a double click using the user's configured Windows double-click time and slop rectangle.
/// The video surface and both windows need the same rule, so it lives in one place.
/// </summary>
internal sealed class DoubleClickTracker
{
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;

    private long lastDownAt = long.MinValue;
    private int lastDownX;
    private int lastDownY;

    /// <summary>True when a button-down at this time and position completes a double click.</summary>
    internal bool IsDoubleClick(long now, int x, int y)
    {
        if (lastDownAt == long.MinValue)
        {
            return false;
        }

        var elapsed = now - lastDownAt;
        return elapsed >= 0 &&
            elapsed <= GetDoubleClickTime() &&
            Math.Abs(x - lastDownX) <= GetSystemMetrics(SmCxDoubleClick) &&
            Math.Abs(y - lastDownY) <= GetSystemMetrics(SmCyDoubleClick);
    }

    internal bool IsDoubleClick(int x, int y) => IsDoubleClick(Environment.TickCount64, x, y);

    internal void Capture(long now, int x, int y)
    {
        lastDownAt = now;
        lastDownX = x;
        lastDownY = y;
    }

    internal void Capture(int x, int y) => Capture(Environment.TickCount64, x, y);

    internal void Reset()
    {
        lastDownAt = long.MinValue;
        lastDownX = 0;
        lastDownY = 0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
