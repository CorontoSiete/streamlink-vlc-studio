using System.Windows;
using System.Windows.Controls;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

/// <summary>Measures media from the available width, so card rows know their height before arranging.</summary>
public sealed class AspectRatioDecorator : Decorator
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(AspectRatioDecorator),
        new FrameworkPropertyMetadata(16.0 / 9.0, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double ratio && double.IsFinite(ratio) && ratio > 0);

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var naturalWidth = 0.0;
        if (!double.IsFinite(constraint.Width) && !double.IsFinite(constraint.Height))
        {
            Child?.Measure(constraint);
            naturalWidth = Child?.DesiredSize.Width ?? 0;
        }

        var size = Fit(constraint, naturalWidth);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = Fit(arrangeSize, 0);
        Child?.Arrange(new Rect(
            (arrangeSize.Width - size.Width) / 2,
            (arrangeSize.Height - size.Height) / 2,
            size.Width, size.Height));
        return arrangeSize;
    }

    private Size Fit(Size available, double naturalWidth)
    {
        var width = double.IsFinite(available.Width) ? available.Width
            : double.IsFinite(available.Height) ? available.Height * AspectRatio : naturalWidth;
        var height = width / AspectRatio;
        if (height > available.Height)
        {
            height = available.Height;
            width = height * AspectRatio;
        }
        return new Size(width, height);
    }
}
