using System.Windows.Input;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class HomeAutoScrollController
{
    private const double DeadZonePixels = 8;
    private const double PixelsPerSecondPerPixel = 18;
    private const double MaximumPixelsPerSecond = 2600;

    public static bool ShouldContinue(MouseButtonState middleButtonState)
        => middleButtonState == MouseButtonState.Pressed;

    public static double GetVelocity(double anchorY, double currentY)
    {
        if (!double.IsFinite(anchorY) || !double.IsFinite(currentY))
        {
            return 0;
        }

        var distance = currentY - anchorY;
        var magnitude = Math.Abs(distance);
        if (magnitude <= DeadZonePixels)
        {
            return 0;
        }

        var velocity = (magnitude - DeadZonePixels) * PixelsPerSecondPerPixel;
        return Math.Sign(distance) * Math.Min(velocity, MaximumPixelsPerSecond);
    }

    public static double GetVerticalOffset(
        double currentVerticalOffset,
        double anchorY,
        double currentY,
        double scrollableHeight,
        double elapsedSeconds)
    {
        var maxOffset = double.IsFinite(scrollableHeight)
            ? Math.Max(0, scrollableHeight)
            : 0;

        if (!double.IsFinite(currentVerticalOffset) || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return double.IsFinite(currentVerticalOffset)
                ? Math.Clamp(currentVerticalOffset, 0, maxOffset)
                : 0;
        }

        var targetOffset = currentVerticalOffset + GetVelocity(anchorY, currentY) * elapsedSeconds;
        return Math.Clamp(targetOffset, 0, maxOffset);
    }

    public static bool IsNearBottom(double verticalOffset, double scrollableHeight, double bottomThreshold)
    {
        if (!double.IsFinite(verticalOffset) ||
            !double.IsFinite(scrollableHeight) ||
            !double.IsFinite(bottomThreshold) ||
            bottomThreshold < 0)
        {
            return false;
        }

        if (scrollableHeight <= 0)
        {
            return true;
        }

        return Math.Max(0, verticalOffset) >= Math.Max(0, scrollableHeight - bottomThreshold);
    }
}
