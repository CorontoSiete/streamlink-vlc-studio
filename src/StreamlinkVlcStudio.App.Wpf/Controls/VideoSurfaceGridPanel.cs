using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed class VideoSurfaceGridPanel : Panel
{
    private const double AspectRatioTolerance = 0.01;
    private readonly Dictionary<UIElement, List<(DependencyPropertyDescriptor Descriptor, EventHandler Handler)>> childPlacementListeners = [];

    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.RegisterAttached(
        "AspectRatio",
        typeof(double),
        typeof(VideoSurfaceGridPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows),
        typeof(int),
        typeof(VideoSurfaceGridPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns),
        typeof(int),
        typeof(VideoSurfaceGridPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static double GetAspectRatio(DependencyObject element)
    {
        return (double)element.GetValue(AspectRatioProperty);
    }

    public static void SetAspectRatio(DependencyObject element, double value)
    {
        element.SetValue(AspectRatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = Math.Max(1, Rows);
        var columns = Math.Max(1, Columns);
        var childWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / columns;
        var childHeight = double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height / rows;

        foreach (UIElement child in InternalChildren)
        {
            var columnSpan = Math.Clamp(Grid.GetColumnSpan(child), 1, columns);
            var rowSpan = Math.Clamp(Grid.GetRowSpan(child), 1, rows);
            child.Measure(new Size(childWidth * columnSpan, childHeight * rowSpan));
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rows = Math.Max(1, Rows);
        var columns = Math.Max(1, Columns);
        var dpi = VisualTreeHelper.GetDpi(this);
        var widthPixels = Math.Max(0, (int)Math.Round(finalSize.Width * dpi.DpiScaleX));
        var heightPixels = Math.Max(0, (int)Math.Round(finalSize.Height * dpi.DpiScaleY));
        var gridBounds = GetAspectAwareGridBounds(widthPixels, heightPixels, rows, columns);

        foreach (UIElement child in InternalChildren)
        {
            var column = Math.Clamp(Grid.GetColumn(child), 0, columns - 1);
            var row = Math.Clamp(Grid.GetRow(child), 0, rows - 1);
            var columnSpan = Math.Clamp(Grid.GetColumnSpan(child), 1, columns - column);
            var rowSpan = Math.Clamp(Grid.GetRowSpan(child), 1, rows - row);

            var leftPixels = gridBounds.Left + GetPartitionBoundary(gridBounds.Width, column, columns);
            var rightPixels = gridBounds.Left + GetPartitionBoundary(gridBounds.Width, column + columnSpan, columns);
            var topPixels = gridBounds.Top + GetPartitionBoundary(gridBounds.Height, row, rows);
            var bottomPixels = gridBounds.Top + GetPartitionBoundary(gridBounds.Height, row + rowSpan, rows);
            var slot = new Rect(
                leftPixels / dpi.DpiScaleX,
                topPixels / dpi.DpiScaleY,
                (rightPixels - leftPixels) / dpi.DpiScaleX,
                (bottomPixels - topPixels) / dpi.DpiScaleY);
            var rect = FitAspectRatioToSlot(
                slot,
                GetAspectRatio(child),
                row,
                column,
                rowSpan,
                columnSpan,
                rows,
                columns);

            child.Arrange(rect);
            SyncDescendantVideoSurfaces(child);
        }

        return finalSize;
    }

    private PixelRect GetAspectAwareGridBounds(int widthPixels, int heightPixels, int rows, int columns)
    {
        if (widthPixels <= 0 || heightPixels <= 0)
        {
            return new PixelRect(0, 0, widthPixels, heightPixels);
        }

        var cellAspectRatio = GetVisibleCellAspectRatio(rows, columns);
        if (cellAspectRatio <= 0 ||
            double.IsNaN(cellAspectRatio) ||
            double.IsInfinity(cellAspectRatio))
        {
            return new PixelRect(0, 0, widthPixels, heightPixels);
        }

        var gridAspectRatio = cellAspectRatio * columns / rows;
        var viewportAspectRatio = (double)widthPixels / heightPixels;
        if (viewportAspectRatio > gridAspectRatio)
        {
            var contentWidth = RoundToPartitionMultiple(heightPixels * gridAspectRatio, widthPixels, columns);
            return new PixelRect((widthPixels - contentWidth) / 2, 0, contentWidth, heightPixels);
        }

        var contentHeight = RoundToPartitionMultiple(widthPixels / gridAspectRatio, heightPixels, rows);
        return new PixelRect(0, (heightPixels - contentHeight) / 2, widthPixels, contentHeight);
    }

    private double GetVisibleCellAspectRatio(int rows, int columns)
    {
        var ratios = new List<double>();
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Visible)
            {
                continue;
            }

            var aspectRatio = GetAspectRatio(child);
            if (aspectRatio <= 0 ||
                double.IsNaN(aspectRatio) ||
                double.IsInfinity(aspectRatio))
            {
                continue;
            }

            var column = Math.Clamp(Grid.GetColumn(child), 0, columns - 1);
            var row = Math.Clamp(Grid.GetRow(child), 0, rows - 1);
            var columnSpan = Math.Clamp(Grid.GetColumnSpan(child), 1, columns - column);
            var rowSpan = Math.Clamp(Grid.GetRowSpan(child), 1, rows - row);
            ratios.Add(aspectRatio * rowSpan / columnSpan);
        }

        if (ratios.Count == 0)
        {
            return 0;
        }

        ratios.Sort();
        var middle = ratios.Count / 2;
        return ratios.Count % 2 == 1
            ? ratios[middle]
            : (ratios[middle - 1] + ratios[middle]) / 2;
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);

        if (visualRemoved is UIElement removedElement)
        {
            RemoveChildPlacementListeners(removedElement);
        }

        if (visualAdded is UIElement addedElement)
        {
            AddChildPlacementListeners(addedElement);
        }
    }

    private void AddChildPlacementListeners(UIElement child)
    {
        RemoveChildPlacementListeners(child);
        var listeners = new List<(DependencyPropertyDescriptor Descriptor, EventHandler Handler)>();
        foreach (var dependencyProperty in new[]
        {
            Grid.RowProperty,
            Grid.ColumnProperty,
            Grid.RowSpanProperty,
            Grid.ColumnSpanProperty,
            VisibilityProperty,
            AspectRatioProperty
        })
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(dependencyProperty, child.GetType());
            if (descriptor is null)
            {
                continue;
            }

            EventHandler handler = (_, _) =>
            {
                InvalidateMeasure();
                InvalidateArrange();
            };
            descriptor.AddValueChanged(child, handler);
            listeners.Add((descriptor, handler));
        }

        childPlacementListeners[child] = listeners;
    }

    private void RemoveChildPlacementListeners(UIElement child)
    {
        if (!childPlacementListeners.Remove(child, out var listeners))
        {
            return;
        }

        foreach (var (descriptor, handler) in listeners)
        {
            descriptor.RemoveValueChanged(child, handler);
        }
    }

    private static void SyncDescendantVideoSurfaces(DependencyObject root)
    {
        if (root is VideoSurface surface)
        {
            surface.SyncNativeBounds();
            return;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            SyncDescendantVideoSurfaces(VisualTreeHelper.GetChild(root, index));
        }
    }

    private static Rect FitAspectRatioToSlot(
        Rect slot,
        double aspectRatio,
        int row,
        int column,
        int rowSpan,
        int columnSpan,
        int rows,
        int columns)
    {
        if (aspectRatio <= 0 ||
            double.IsNaN(aspectRatio) ||
            double.IsInfinity(aspectRatio) ||
            slot.Width <= 0 ||
            slot.Height <= 0)
        {
            return slot;
        }

        var slotAspectRatio = slot.Width / slot.Height;
        if (Math.Abs(slotAspectRatio - aspectRatio) <= AspectRatioTolerance)
        {
            return slot;
        }

        if (slotAspectRatio > aspectRatio)
        {
            var width = slot.Height * aspectRatio;
            return new Rect(GetHorizontalOffset(slot, width, column, columnSpan, columns), slot.Y, width, slot.Height);
        }

        var height = slot.Width / aspectRatio;
        return new Rect(slot.X, GetVerticalOffset(slot, height, row, rowSpan, rows), slot.Width, height);
    }

    private static double GetHorizontalOffset(Rect slot, double width, int column, int columnSpan, int columns)
    {
        if (columnSpan >= columns)
        {
            return slot.X + (slot.Width - width) / 2;
        }

        return column == 0 ? slot.Right - width : slot.X;
    }

    private static double GetVerticalOffset(Rect slot, double height, int row, int rowSpan, int rows)
    {
        if (rowSpan >= rows)
        {
            return slot.Y + (slot.Height - height) / 2;
        }

        return row == 0 ? slot.Bottom - height : slot.Y;
    }

    private static int GetPartitionBoundary(int totalPixels, int index, int partitions)
    {
        return (int)((long)totalPixels * Math.Clamp(index, 0, partitions) / partitions);
    }

    private static int RoundToPartitionMultiple(double value, int maximum, int partitions)
    {
        var partitionSize = Math.Max(1, partitions);
        var rounded = (int)Math.Round(value / partitionSize) * partitionSize;
        return Math.Min(maximum, Math.Max(1, rounded));
    }

    private readonly record struct PixelRect(int Left, int Top, int Width, int Height);
}
