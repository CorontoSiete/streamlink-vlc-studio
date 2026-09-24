using System.Windows;
using System.Windows.Controls;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>Keeps the docked desktop layout and stacks its blocks when they no longer fit.</summary>
public sealed class ResponsiveDockPanel : DockPanel
{
    public static readonly DependencyProperty CompactWidthProperty = DependencyProperty.Register(
        nameof(CompactWidth), typeof(double), typeof(ResponsiveDockPanel),
        new FrameworkPropertyMetadata(600.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private bool compact;

    public double CompactWidth
    {
        get => (double)GetValue(CompactWidthProperty);
        set => SetValue(CompactWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        compact = constraint.Width < CompactWidth;
        if (!compact)
        {
            return base.MeasureOverride(constraint);
        }

        var width = 0.0;
        var height = 0.0;
        foreach (var child in StackedChildren())
        {
            child.Measure(new Size(constraint.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (!compact)
        {
            return base.ArrangeOverride(arrangeSize);
        }

        var y = 0.0;
        foreach (var child in StackedChildren())
        {
            child.Arrange(new Rect(0, y, arrangeSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }

        return arrangeSize;
    }

    private IEnumerable<UIElement> StackedChildren()
    {
        var children = InternalChildren.Cast<UIElement>().Where(child => child.Visibility != Visibility.Collapsed).ToList();
        if (LastChildFill && children.Count > 1)
        {
            yield return children[^1];
            children.RemoveAt(children.Count - 1);
        }

        foreach (var child in children)
        {
            yield return child;
        }
    }
}
