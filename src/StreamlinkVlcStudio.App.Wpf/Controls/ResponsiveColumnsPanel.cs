using System.Windows;
using System.Windows.Controls;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>Reflows equal-width columns into rows when the available width decreases.</summary>
public sealed class ResponsiveColumnsPanel : Panel
{
    public static readonly DependencyProperty MinimumColumnWidthProperty = DependencyProperty.Register(
        nameof(MinimumColumnWidth), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(320.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinimumColumnWidth
    {
        get => (double)GetValue(MinimumColumnWidthProperty);
        set => SetValue(MinimumColumnWidthProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = VisibleChildren();
        var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : MinimumColumnWidth * 2 + Gap(ColumnGap);
        var columns = ColumnCount(width, children.Count);
        var itemWidth = Math.Max(0, (width - Gap(ColumnGap) * (columns - 1)) / columns);
        foreach (var child in children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
        }

        return new Size(width, LayoutRows(children, columns, itemWidth, arrange: false));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = VisibleChildren();
        var columns = ColumnCount(finalSize.Width, children.Count);
        var itemWidth = Math.Max(0, (finalSize.Width - Gap(ColumnGap) * (columns - 1)) / columns);
        LayoutRows(children, columns, itemWidth, arrange: true);
        return finalSize;
    }

    private double LayoutRows(List<UIElement> children, int columns, double itemWidth, bool arrange)
    {
        var y = 0.0;
        for (var start = 0; start < children.Count; start += columns)
        {
            var count = Math.Min(columns, children.Count - start);
            var height = 0.0;
            for (var offset = 0; offset < count; offset++)
            {
                height = Math.Max(height, children[start + offset].DesiredSize.Height);
            }

            if (arrange)
            {
                for (var offset = 0; offset < count; offset++)
                {
                    children[start + offset].Arrange(new Rect(offset * (itemWidth + Gap(ColumnGap)), y, itemWidth, height));
                }
            }

            y += height;
            if (start + count < children.Count)
            {
                y += Gap(RowGap);
            }
        }

        return y;
    }

    private int ColumnCount(double width, int childCount)
    {
        var minimum = double.IsFinite(MinimumColumnWidth) && MinimumColumnWidth > 0 ? MinimumColumnWidth : 320;
        return childCount > 1 && width >= minimum * 2 + Gap(ColumnGap) ? 2 : 1;
    }

    private List<UIElement> VisibleChildren() => InternalChildren.Cast<UIElement>()
        .Where(child => child.Visibility != Visibility.Collapsed).ToList();

    private static double Gap(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
