using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StreamlinkVlcStudio.App.Wpf.Converters;

public sealed class HomeContentPaddingConverter : IValueConverter
{
    private static readonly Thickness GappedPadding = new(24, 18, 24, 24);
    private static readonly Thickness CompactPadding = new(16, 18, 16, 24);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool keepRightGap && !keepRightGap
            ? CompactPadding
            : GappedPadding;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
