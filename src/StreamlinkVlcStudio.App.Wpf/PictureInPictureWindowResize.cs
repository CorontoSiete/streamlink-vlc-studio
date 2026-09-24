namespace StreamlinkVlcStudio.App.Wpf;

/// <summary>One screen-coordinate resize border for WPF chrome and native video input.</summary>
internal static class PictureInPictureWindowResize
{
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;
    internal const double BorderThickness = 6;
    internal const double BottomBorderThickness = 10;
    internal const double CornerLength = 24;

    internal static bool TryHitTest(
        NativeRectangle bounds,
        int x,
        int y,
        double scaleX,
        double scaleY,
        out int hitTest)
    {
        hitTest = 0;
        if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom ||
            !double.IsFinite(scaleX) || scaleX <= 0 ||
            !double.IsFinite(scaleY) || scaleY <= 0)
        {
            return false;
        }

        // Subtract before comparing so negative monitor coordinates work too. Keep the top
        // band aligned with WindowChrome: title buttons begin just below its six-DIP border.
        var fromLeft = x - (long)bounds.Left;
        var fromRight = bounds.Right - 1L - x;
        var fromTop = y - (long)bounds.Top;
        var fromBottom = bounds.Bottom - 1L - y;
        var left = fromLeft < Math.Ceiling(BorderThickness * scaleX);
        var right = fromRight < Math.Ceiling(BorderThickness * scaleX);
        var top = fromTop < Math.Ceiling(BorderThickness * scaleY);
        var bottom = fromBottom < Math.Ceiling(BottomBorderThickness * scaleY);
        var cornerWidth = Math.Ceiling(CornerLength * scaleX);
        var cornerHeight = Math.Ceiling(CornerLength * scaleY);

        // A corner extends along both adjoining edges, without taking a square out of video.
        hitTest = (top && fromLeft < cornerWidth) || (left && fromTop < cornerHeight)
            ? HtTopLeft
            : (top && fromRight < cornerWidth) || (right && fromTop < cornerHeight)
                ? HtTopRight
                : (bottom && fromLeft < cornerWidth) || (left && fromBottom < cornerHeight)
                    ? HtBottomLeft
                    : (bottom && fromRight < cornerWidth) || (right && fromBottom < cornerHeight)
                        ? HtBottomRight
                        : left ? HtLeft
                        : right ? HtRight
                        : top ? HtTop
                        : bottom ? HtBottom
                        : 0;
        return hitTest != 0;
    }
}
