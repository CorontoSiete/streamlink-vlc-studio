using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>
/// A border that clips its complete visual subtree to its rounded outline.
/// WPF's regular <see cref="Border"/> only applies <see cref="Border.CornerRadius"/>
/// to its own rendering; <see cref="UIElement.ClipToBounds"/> remains rectangular.
/// </summary>
public sealed class RoundedClipBorder : Border
{
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClipGeometry();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == CornerRadiusProperty)
        {
            UpdateClipGeometry();
        }
    }

    internal static Geometry CreateClipGeometry(Size size, CornerRadius cornerRadius)
    {
        var width = NormalizeLength(size.Width);
        var height = NormalizeLength(size.Height);
        var radii = NormalizeCornerRadius(new Size(width, height), cornerRadius);
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(radii.TopLeft, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(width - radii.TopRight, 0), isStroked: true, isSmoothJoin: false);
            AddCorner(context, new Point(width, radii.TopRight), radii.TopRight);
            context.LineTo(new Point(width, height - radii.BottomRight), isStroked: true, isSmoothJoin: false);
            AddCorner(context, new Point(width - radii.BottomRight, height), radii.BottomRight);
            context.LineTo(new Point(radii.BottomLeft, height), isStroked: true, isSmoothJoin: false);
            AddCorner(context, new Point(0, height - radii.BottomLeft), radii.BottomLeft);
            context.LineTo(new Point(0, radii.TopLeft), isStroked: true, isSmoothJoin: false);
            AddCorner(context, new Point(radii.TopLeft, 0), radii.TopLeft);
        }

        geometry.Freeze();
        return geometry;
    }

    private void UpdateClipGeometry()
    {
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            ClearValue(ClipProperty);
            return;
        }

        Clip = CreateClipGeometry(RenderSize, CornerRadius);
    }

    private static void AddCorner(StreamGeometryContext context, Point endPoint, double radius)
    {
        if (radius <= 0)
        {
            context.LineTo(endPoint, isStroked: true, isSmoothJoin: false);
            return;
        }

        context.ArcTo(
            endPoint,
            new Size(radius, radius),
            rotationAngle: 0,
            isLargeArc: false,
            SweepDirection.Clockwise,
            isStroked: true,
            isSmoothJoin: false);
    }

    private static CornerRadius NormalizeCornerRadius(Size size, CornerRadius cornerRadius)
    {
        var topLeft = NormalizeRadius(cornerRadius.TopLeft);
        var topRight = NormalizeRadius(cornerRadius.TopRight);
        var bottomRight = NormalizeRadius(cornerRadius.BottomRight);
        var bottomLeft = NormalizeRadius(cornerRadius.BottomLeft);
        var scale = Math.Min(
            1,
            Math.Min(
                GetRadiusScale(size.Width, topLeft + topRight, bottomLeft + bottomRight),
                GetRadiusScale(size.Height, topLeft + bottomLeft, topRight + bottomRight)));

        return new CornerRadius(
            topLeft * scale,
            topRight * scale,
            bottomRight * scale,
            bottomLeft * scale);
    }

    private static double GetRadiusScale(double availableLength, double firstPair, double secondPair)
    {
        var largestPair = Math.Max(firstPair, secondPair);
        return largestPair > availableLength && largestPair > 0
            ? availableLength / largestPair
            : 1;
    }

    private static double NormalizeLength(double value)
    {
        return double.IsFinite(value) && value > 0 ? value : 0;
    }

    private static double NormalizeRadius(double value)
    {
        return double.IsFinite(value) && value > 0 ? value : 0;
    }
}
