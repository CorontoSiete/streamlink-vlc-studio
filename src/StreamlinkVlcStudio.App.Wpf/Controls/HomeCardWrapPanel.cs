using System.Windows;
using System.Windows.Controls;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed class HomeCardWrapPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(HomeCardWrapPanel),
        new FrameworkPropertyMetadata(316.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty HorizontalGapProperty = DependencyProperty.Register(
        nameof(HorizontalGap),
        typeof(double),
        typeof(HomeCardWrapPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty VerticalGapProperty = DependencyProperty.Register(
        nameof(VerticalGap),
        typeof(double),
        typeof(HomeCardWrapPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty KeepRightGapProperty = DependencyProperty.Register(
        nameof(KeepRightGap),
        typeof(bool),
        typeof(HomeCardWrapPanel),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double HorizontalGap
    {
        get => (double)GetValue(HorizontalGapProperty);
        set => SetValue(HorizontalGapProperty, value);
    }

    public double VerticalGap
    {
        get => (double)GetValue(VerticalGapProperty);
        set => SetValue(VerticalGapProperty, value);
    }

    public bool KeepRightGap
    {
        get => (bool)GetValue(KeepRightGapProperty);
        set => SetValue(KeepRightGapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visibleChildren = GetVisibleChildren();
        if (visibleChildren.Count == 0)
        {
            return new Size(GetDesiredWidth(availableSize.Width, 0), 0);
        }

        var horizontalGap = NormalizeGap(HorizontalGap);
        var verticalGap = NormalizeGap(VerticalGap);
        var columns = GetColumnCount(availableSize.Width, visibleChildren.Count, horizontalGap);
        var itemWidth = GetArrangeItemWidth(availableSize.Width, columns, horizontalGap);
        var desiredWidth = 0.0;
        var desiredHeight = 0.0;
        var rowWidth = 0.0;
        var rowHeight = 0.0;
        var column = 0;

        foreach (var child in visibleChildren)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            rowWidth += GetVisualSlotWidth(itemWidth, column, horizontalGap);
            column++;

            if (column == columns)
            {
                desiredWidth = Math.Max(desiredWidth, rowWidth);
                desiredHeight += rowHeight + verticalGap;
                rowWidth = 0;
                rowHeight = 0;
                column = 0;
            }
        }

        if (column > 0)
        {
            desiredWidth = Math.Max(desiredWidth, rowWidth);
            desiredHeight += rowHeight + verticalGap;
        }

        return new Size(GetDesiredWidth(availableSize.Width, desiredWidth), desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visibleChildren = GetVisibleChildren();
        if (visibleChildren.Count == 0)
        {
            return finalSize;
        }

        var horizontalGap = NormalizeGap(HorizontalGap);
        var verticalGap = NormalizeGap(VerticalGap);
        var columns = GetColumnCount(finalSize.Width, visibleChildren.Count, horizontalGap);
        var itemWidth = GetArrangeItemWidth(finalSize.Width, columns, horizontalGap);
        var rowStart = 0;
        var y = 0.0;

        while (rowStart < visibleChildren.Count)
        {
            var rowCount = Math.Min(columns, visibleChildren.Count - rowStart);
            var rowHeight = 0.0;
            for (var offset = 0; offset < rowCount; offset++)
            {
                rowHeight = Math.Max(rowHeight, visibleChildren[rowStart + offset].DesiredSize.Height);
            }

            var x = 0.0;
            for (var offset = 0; offset < rowCount; offset++)
            {
                visibleChildren[rowStart + offset].Arrange(new Rect(x, y, itemWidth, rowHeight));
                x += itemWidth + horizontalGap;
            }

            y += rowHeight + verticalGap;
            rowStart += rowCount;
        }

        return finalSize;
    }

    private List<UIElement> GetVisibleChildren()
    {
        var children = new List<UIElement>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Collapsed)
            {
                children.Add(child);
            }
        }

        return children;
    }

    private int GetColumnCount(double availableWidth, int visibleChildCount, double horizontalGap)
    {
        if (visibleChildCount <= 0)
        {
            return 1;
        }

        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return Math.Max(1, visibleChildCount);
        }

        var baseItemWidth = GetBaseItemWidth();
        var slotWidth = baseItemWidth + horizontalGap;
        if (slotWidth <= 0)
        {
            return 1;
        }

        var columns = KeepRightGap
            ? (int)Math.Floor(availableWidth / slotWidth)
            : (int)Math.Floor((availableWidth + horizontalGap) / slotWidth);
        return Math.Clamp(columns, 1, visibleChildCount);
    }

    private double GetArrangeItemWidth(double availableWidth, int columns, double horizontalGap)
    {
        var baseItemWidth = GetBaseItemWidth();
        if (KeepRightGap || !double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return baseItemWidth;
        }

        var filledWidth = (availableWidth - horizontalGap * Math.Max(0, columns - 1)) / Math.Max(1, columns);
        return Math.Max(baseItemWidth, filledWidth);
    }

    private double GetVisualSlotWidth(double itemWidth, int column, double horizontalGap)
    {
        if (KeepRightGap)
        {
            return itemWidth + horizontalGap;
        }

        return itemWidth + (column == 0 ? 0 : horizontalGap);
    }

    private static double GetDesiredWidth(double availableWidth, double measuredWidth)
    {
        return double.IsFinite(availableWidth) && availableWidth >= 0
            ? availableWidth
            : measuredWidth;
    }

    private double GetBaseItemWidth()
    {
        return double.IsFinite(ItemWidth) && ItemWidth > 0 ? ItemWidth : 316.0;
    }

    private static double NormalizeGap(double value)
    {
        return double.IsFinite(value) && value > 0 ? value : 0;
    }
}
