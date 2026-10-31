namespace StreamlinkVlcStudio.Core.Settings;

// Null dimensions retain the responsive default; X/Y are fractions of available travel.
public sealed record WebChatOverlayLayout(double? Width = null, double? Height = null, double X = 1, double Y = 0)
{
    public static bool IsValid(WebChatOverlayLayout? layout) => layout is not null &&
        ValidDimension(layout.Width) && ValidDimension(layout.Height) &&
        double.IsFinite(layout.X) && layout.X is >= 0 and <= 1 &&
        double.IsFinite(layout.Y) && layout.Y is >= 0 and <= 1;

    private static bool ValidDimension(double? value) => value is null ||
        double.IsFinite(value.Value) && value.Value is > 0 and <= 16_384;
}
