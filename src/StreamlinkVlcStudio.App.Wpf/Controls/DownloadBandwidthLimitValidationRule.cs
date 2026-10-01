using System.Globalization;
using System.Windows.Controls;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public sealed class DownloadBandwidthLimitValidationRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) =>
        double.TryParse(value as string, NumberStyles.Float, cultureInfo, out var limit) && double.IsFinite(limit) && limit >= 0
            ? ValidationResult.ValidResult : new ValidationResult(false, "Enter a non-negative number, or 0 for unlimited.");
}
