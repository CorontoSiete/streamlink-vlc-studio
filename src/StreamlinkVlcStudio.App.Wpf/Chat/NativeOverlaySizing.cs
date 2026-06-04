namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal static class NativeOverlaySizing
{
    internal const int ReferenceVideoHeight = 1080;
    internal const int MinWidth = 220;
    internal const int MinHeight = 120;
    internal const int MaxWidth = 1920;
    internal const int MaxHeight = 1080;
    internal const int MaxFramePayloadBytes = 32 * 1024 * 1024;

    internal static int ClampReferenceWidth(int width)
    {
        return Math.Clamp(width, MinWidth, MaxWidth);
    }

    internal static int ClampReferenceHeight(int height)
    {
        return Math.Clamp(height, MinHeight, MaxHeight);
    }

    internal static (int Width, int Height) NormalizeToReferenceSize(int width, int height, int videoHeight)
    {
        var sourceHeight = videoHeight > 0 ? videoHeight : ReferenceVideoHeight;
        return (
            ClampReferenceWidth((int)Math.Round(width * ReferenceVideoHeight / (double)sourceHeight)),
            ClampReferenceHeight((int)Math.Round(height * ReferenceVideoHeight / (double)sourceHeight)));
    }

    internal static double GetVideoScale(int videoHeight)
    {
        var sourceHeight = videoHeight > 0 ? videoHeight : ReferenceVideoHeight;
        return sourceHeight / (double)ReferenceVideoHeight;
    }

    internal static int ScaleReferencePixels(double scale, double value)
    {
        return (int)Math.Clamp(Math.Round(value * scale), 1, int.MaxValue);
    }

    internal static int ScaleReferencePixels(int videoHeight, int value)
    {
        var sourceHeight = videoHeight > 0 ? videoHeight : ReferenceVideoHeight;
        var scaled = ((long)value * sourceHeight + ReferenceVideoHeight / 2) / ReferenceVideoHeight;
        return (int)Math.Clamp(scaled, 1, int.MaxValue);
    }
}
