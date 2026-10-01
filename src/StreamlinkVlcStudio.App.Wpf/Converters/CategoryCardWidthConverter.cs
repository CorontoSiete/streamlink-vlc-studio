using System.Globalization;
using System.Windows.Data;

namespace StreamlinkVlcStudio.App.Wpf.Converters;

public sealed class CategoryCardWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width > 0 && width < 440 ? 130.0 : 168.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
