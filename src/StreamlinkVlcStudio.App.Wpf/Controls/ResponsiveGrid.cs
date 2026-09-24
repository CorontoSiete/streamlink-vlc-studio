using System.Windows;
using System.Windows.Controls;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>Exposes the space offered by the parent, before measuring responsive children.</summary>
public sealed class ResponsiveGrid : Grid
{
    public static readonly DependencyProperty CompactWidthProperty = DependencyProperty.Register(
        nameof(CompactWidth), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(760.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CompactHeightProperty = DependencyProperty.Register(
        nameof(CompactHeight), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly DependencyPropertyKey IsCompactPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCompact), typeof(bool), typeof(ResponsiveGrid), new PropertyMetadata(false));

    public static readonly DependencyProperty IsCompactProperty = IsCompactPropertyKey.DependencyProperty;

    public double CompactWidth
    {
        get => (double)GetValue(CompactWidthProperty);
        set => SetValue(CompactWidthProperty, value);
    }

    public double CompactHeight
    {
        get => (double)GetValue(CompactHeightProperty);
        set => SetValue(CompactHeightProperty, value);
    }

    public bool IsCompact => (bool)GetValue(IsCompactProperty);

    protected override Size MeasureOverride(Size constraint)
    {
        SetValue(IsCompactPropertyKey, constraint.Width < CompactWidth || constraint.Height < CompactHeight);
        return base.MeasureOverride(constraint);
    }
}
