using System.Windows;

namespace StreamlinkVlcStudio.App.Wpf;

internal readonly record struct PictureInPictureWindowInsets(int Left, int Top, int Right, int Bottom)
{
    public int Horizontal => Math.Max(0, Left) + Math.Max(0, Right);
    public int Vertical => Math.Max(0, Top) + Math.Max(0, Bottom);
}

internal static class PictureInPictureWindowSizing
{
    internal const int WmSizing = 0x0214;
    internal const int WmMoving = 0x0216;
    internal const int WmszLeft = 1;
    internal const int WmszRight = 2;
    internal const int WmszTop = 3;
    internal const int WmszTopLeft = 4;
    internal const int WmszTopRight = 5;
    internal const int WmszBottom = 6;
    internal const int WmszBottomLeft = 7;
    internal const int WmszBottomRight = 8;

    internal static Size FitWindowSize(
        Size requestedSize,
        double contentAspectRatio,
        double leftInset,
        double topInset,
        double rightInset,
        double bottomInset,
        double minimumWidth,
        double minimumHeight)
    {
        if (!IsValidAspectRatio(contentAspectRatio) ||
            !IsValidSize(requestedSize))
        {
            return requestedSize;
        }

        var horizontalInset = Math.Max(0, leftInset) + Math.Max(0, rightInset);
        var verticalInset = Math.Max(0, topInset) + Math.Max(0, bottomInset);
        var availableWidth = Math.Max(1, requestedSize.Width - horizontalInset);
        var availableHeight = Math.Max(1, requestedSize.Height - verticalInset);
        var contentWidth = Math.Min(availableWidth, availableHeight * contentAspectRatio);
        var contentHeight = contentWidth / contentAspectRatio;

        var minimumContentWidth = Math.Max(1, minimumWidth - horizontalInset);
        var minimumContentHeight = Math.Max(1, minimumHeight - verticalInset);
        contentWidth = Math.Max(contentWidth, minimumContentWidth);
        contentWidth = Math.Max(contentWidth, minimumContentHeight * contentAspectRatio);
        contentHeight = contentWidth / contentAspectRatio;

        return new Size(
            contentWidth + horizontalInset,
            contentHeight + verticalInset);
    }

    internal static bool TryConstrainRect(
        NativeRectangle proposed,
        int sizingEdge,
        double contentAspectRatio,
        PictureInPictureWindowInsets insets,
        int minimumWidth,
        int minimumHeight,
        out NativeRectangle constrained)
    {
        constrained = proposed;
        if (!IsValidAspectRatio(contentAspectRatio))
        {
            return false;
        }

        var proposedWidth = proposed.Right - (long)proposed.Left;
        var proposedHeight = proposed.Bottom - (long)proposed.Top;
        if (proposedWidth <= 0 || proposedHeight <= 0)
        {
            return false;
        }

        var horizontalEdge = HasHorizontalEdge(sizingEdge);
        var verticalEdge = HasVerticalEdge(sizingEdge);
        if (!horizontalEdge && !verticalEdge)
        {
            return false;
        }

        var horizontalInset = Math.Max(0, insets.Horizontal);
        var verticalInset = Math.Max(0, insets.Vertical);
        var availableContentWidth = Math.Max(1, proposedWidth - horizontalInset);
        var availableContentHeight = Math.Max(1, proposedHeight - verticalInset);
        var contentWidth = horizontalEdge && !verticalEdge
            ? availableContentWidth
            : verticalEdge && !horizontalEdge
                ? availableContentHeight * contentAspectRatio
                : Math.Min(availableContentWidth, availableContentHeight * contentAspectRatio);
        var contentHeight = contentWidth / contentAspectRatio;

        if (verticalEdge && !horizontalEdge)
        {
            contentHeight = availableContentHeight;
            contentWidth = contentHeight * contentAspectRatio;
        }

        var minimumContentWidth = Math.Max(1, minimumWidth - horizontalInset);
        var minimumContentHeight = Math.Max(1, minimumHeight - verticalInset);
        contentWidth = Math.Max(contentWidth, minimumContentWidth);
        contentWidth = Math.Max(contentWidth, minimumContentHeight * contentAspectRatio);
        contentHeight = contentWidth / contentAspectRatio;

        var width = Math.Max(1, ToPixel(contentWidth + horizontalInset));
        var height = Math.Max(1, ToPixel(contentHeight + verticalInset));
        var left = proposed.Left;
        var right = proposed.Right;
        var top = proposed.Top;
        var bottom = proposed.Bottom;

        if (HasLeftEdge(sizingEdge))
        {
            left = right - width;
        }
        else if (HasRightEdge(sizingEdge))
        {
            right = left + width;
        }
        else
        {
            var center = (proposed.Left + (double)proposed.Right) / 2;
            left = ToPixel(center - width / 2.0);
            right = left + width;
        }

        if (HasTopEdge(sizingEdge))
        {
            top = bottom - height;
        }
        else if (HasBottomEdge(sizingEdge))
        {
            bottom = top + height;
        }
        else
        {
            var center = (proposed.Top + (double)proposed.Bottom) / 2;
            top = ToPixel(center - height / 2.0);
            bottom = top + height;
        }

        constrained = new NativeRectangle
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
        return true;
    }

    internal static bool TryConstrainMoveRect(
        NativeRectangle proposed,
        NativeRectangle workArea,
        out NativeRectangle constrained)
    {
        constrained = proposed;
        var proposedWidth = proposed.Right - (long)proposed.Left;
        var proposedHeight = proposed.Bottom - (long)proposed.Top;
        var availableWidth = workArea.Right - (long)workArea.Left;
        var availableHeight = workArea.Bottom - (long)workArea.Top;
        if (proposedWidth <= 0 ||
            proposedHeight <= 0 ||
            availableWidth <= 0 ||
            availableHeight <= 0)
        {
            return false;
        }

        var width = proposedWidth;
        var height = proposedHeight;
        if (width > availableWidth || height > availableHeight)
        {
            var scale = Math.Min(
                availableWidth / (double)width,
                availableHeight / (double)height);
            width = Math.Max(1, (long)Math.Floor(width * scale));
            height = Math.Max(1, (long)Math.Floor(height * scale));
        }

        var left = Math.Clamp(
            (long)proposed.Left,
            workArea.Left,
            workArea.Right - width);
        var top = Math.Clamp(
            (long)proposed.Top,
            workArea.Top,
            workArea.Bottom - height);
        var right = left + width;
        var bottom = top + height;
        if (left < int.MinValue ||
            top < int.MinValue ||
            right > int.MaxValue ||
            bottom > int.MaxValue)
        {
            return false;
        }

        constrained = new NativeRectangle
        {
            Left = (int)left,
            Top = (int)top,
            Right = (int)right,
            Bottom = (int)bottom
        };
        return true;
    }

    private static bool IsValidAspectRatio(double value) =>
        double.IsFinite(value) && value > 0.2;

    private static bool IsValidSize(Size size) =>
        double.IsFinite(size.Width) &&
        double.IsFinite(size.Height) &&
        size.Width > 0 &&
        size.Height > 0;

    private static bool HasHorizontalEdge(int sizingEdge) =>
        sizingEdge is WmszLeft or WmszRight or WmszTopLeft or WmszTopRight or WmszBottomLeft or WmszBottomRight;

    private static bool HasVerticalEdge(int sizingEdge) =>
        sizingEdge is WmszTop or WmszTopLeft or WmszTopRight or WmszBottom or WmszBottomLeft or WmszBottomRight;

    private static bool HasLeftEdge(int sizingEdge) =>
        sizingEdge is WmszLeft or WmszTopLeft or WmszBottomLeft;

    private static bool HasRightEdge(int sizingEdge) =>
        sizingEdge is WmszRight or WmszTopRight or WmszBottomRight;

    private static bool HasTopEdge(int sizingEdge) =>
        sizingEdge is WmszTop or WmszTopLeft or WmszTopRight;

    private static bool HasBottomEdge(int sizingEdge) =>
        sizingEdge is WmszBottom or WmszBottomLeft or WmszBottomRight;

    private static int ToPixel(double value)
    {
        if (value <= int.MinValue)
        {
            return int.MinValue;
        }

        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Round(value);
    }
}
