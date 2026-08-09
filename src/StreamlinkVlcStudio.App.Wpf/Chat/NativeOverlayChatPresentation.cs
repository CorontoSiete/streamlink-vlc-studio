using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed record NativeOverlayChatPresentation(
    int MessageFontSize,
    int SystemFontSize,
    int MessageFontCellHeight,
    int SystemFontCellHeight,
    int LineGap,
    int MessageGap,
    int EmoteHeight,
    int EmoteMaxWidth,
    int ShadowOffset)
{
    internal static NativeOverlayChatPresentation Create(int referenceFontSize, int videoHeight)
    {
        var normalizedReferenceFontSize = Math.Clamp(referenceFontSize, 8, 36);
        var systemReferenceFontSize = Math.Clamp(
            (normalizedReferenceFontSize * 13 + 15 / 2) / 15,
            8,
            36);
        var messageFontSize = NativeOverlaySizing.ScaleReferencePixels(videoHeight, normalizedReferenceFontSize);
        var systemFontSize = NativeOverlaySizing.ScaleReferencePixels(videoHeight, systemReferenceFontSize);

        return new NativeOverlayChatPresentation(
            messageFontSize,
            systemFontSize,
            NativeOverlayFontMetrics.GetCellHeight(messageFontSize, FontWeights.Bold),
            NativeOverlayFontMetrics.GetCellHeight(systemFontSize, FontWeights.Normal),
            NativeOverlaySizing.ScaleReferencePixels(videoHeight, 2),
            NativeOverlaySizing.ScaleReferencePixels(videoHeight, 2),
            NativeOverlaySizing.ScaleReferencePixels(videoHeight, 24),
            NativeOverlaySizing.ScaleReferencePixels(videoHeight, 96),
            NativeOverlaySizing.ScaleReferencePixels(videoHeight, 1));
    }

    internal int GetFontSize(bool isSystem) => isSystem ? SystemFontSize : MessageFontSize;

    internal int GetFontCellHeight(bool isSystem) => isSystem ? SystemFontCellHeight : MessageFontCellHeight;
}

internal static partial class NativeOverlayFontMetrics
{
    private const int DefaultCharset = 1;
    private const int ClearTypeQuality = 5;
    private const int SwissFontFamily = 0x20;

    internal static int GetCellHeight(int pixelHeight, FontWeight fontWeight)
    {
        var fallback = Math.Max(1, (int)Math.Ceiling(pixelHeight * 4d / 3d));
        var deviceContext = CreateCompatibleDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
        {
            return fallback;
        }

        var font = CreateFont(
            -pixelHeight,
            0,
            0,
            0,
            fontWeight.ToOpenTypeWeight(),
            0,
            0,
            0,
            DefaultCharset,
            0,
            0,
            ClearTypeQuality,
            SwissFontFamily,
            "Segoe UI");
        if (font == IntPtr.Zero)
        {
            _ = DeleteDC(deviceContext);
            return fallback;
        }

        var previousFont = SelectObject(deviceContext, font);
        try
        {
            return GetTextMetrics(deviceContext, out var metrics) != 0
                ? Math.Max(1, metrics.Height + metrics.ExternalLeading)
                : fallback;
        }
        finally
        {
            if (previousFont != IntPtr.Zero)
            {
                _ = SelectObject(deviceContext, previousFont);
            }

            _ = DeleteObject(font);
            _ = DeleteDC(deviceContext);
        }
    }

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    private static partial IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint characterSet,
        uint outputPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static partial IntPtr SelectObject(IntPtr deviceContext, IntPtr value);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextMetricsW")]
    private static partial int GetTextMetrics(IntPtr deviceContext, out NativeTextMetric metrics);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    private static partial int DeleteObject(IntPtr value);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    private static partial int DeleteDC(IntPtr deviceContext);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTextMetric
    {
        public int Height;
        public int Ascent;
        public int Descent;
        public int InternalLeading;
        public int ExternalLeading;
        public int AverageCharacterWidth;
        public int MaximumCharacterWidth;
        public int Weight;
        public int Overhang;
        public int DigitizedAspectX;
        public int DigitizedAspectY;
        public ushort FirstCharacter;
        public ushort LastCharacter;
        public ushort DefaultCharacter;
        public ushort BreakCharacter;
        public byte Italic;
        public byte Underlined;
        public byte StruckOut;
        public byte PitchAndFamily;
        public byte CharacterSet;
    }
}
