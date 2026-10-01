using System.Globalization;
using System.Windows.Data;

namespace StreamlinkVlcStudio.App.Wpf.Converters;

public sealed class StudioMetadataConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text ? text.Replace(" | ", " · ", StringComparison.Ordinal) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
