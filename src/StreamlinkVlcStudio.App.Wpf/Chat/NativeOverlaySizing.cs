using System.Globalization;
using System.IO;
using StreamlinkVlcStudio.Core.Settings;
using StreamStudio.Io;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal readonly record struct NativeOverlaySourceSize(int Width, int Height);

internal static class NativeOverlaySizing
{
    private const int MaximumStateFileBytes = 4096;
    internal const int ReferenceVideoHeight = 1080;
    internal const int MinWidth = (int)ChatSettings.MinimumDockWidth;
    internal const int MinHeight = 120;
    internal const int MaxWidth = (int)ChatSettings.MaximumDockWidth;
    internal const int MaxHeight = 1080;

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
            (int)Math.Clamp(Math.Round((double)width * ReferenceVideoHeight / sourceHeight), MinWidth, MaxWidth),
            (int)Math.Clamp(Math.Round((double)height * ReferenceVideoHeight / sourceHeight), MinHeight, MaxHeight));
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

    /// <summary>
    /// Parses the integers out of a small overlay state file. Values are always read with the
    /// invariant culture so the renderer and the shell agree on every machine.
    /// </summary>
    internal static int[] ParseInts(string text)
    {
        return (text ?? "")
            .Split([' ', '\t', '\r', '\n', ':', ',', '{', '}'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : (int?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
    }

    /// <summary>
    /// Reads a persisted overlay size. <paramref name="referenceSize"/> reports whether the file
    /// stores reference (1080p-normalized) pixels rather than absolute ones.
    /// </summary>
    internal static bool TryReadSizeFile(string path, out int width, out int height, out bool referenceSize)
    {
        width = 0;
        height = 0;
        referenceSize = false;
        if (!TryReadStateFile(path, out var text))
        {
            return false;
        }

        var values = ParseInts(text);
        if (values.Length < 2)
        {
            return false;
        }

        width = values[0];
        height = values[1];
        referenceSize =
            text.Contains("reference", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("normalized", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    internal static bool TryReadIntFile(string path, out int[] values)
    {
        values = [];
        if (!TryReadStateFile(path, out var text))
        {
            return false;
        }

        values = ParseInts(text);
        return values.Length > 0;
    }

    private static bool TryReadStateFile(string path, out string text)
    {
        text = "";
        try
        {
            text = BoundedFile.ReadAllText(path, MaximumStateFileBytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
