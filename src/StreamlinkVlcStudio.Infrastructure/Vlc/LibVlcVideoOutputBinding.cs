using System.Runtime.InteropServices;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static class LibVlcVideoOutputBinding
{
    internal static void Bind(IntPtr player, IntPtr window, VideoRendererMode renderer, Version? version,
        bool usesNativeOverlay, bool hardwareOverlayComposition = false)
    {
        // This adapter uses VLC 3's exported plugin ABI. Do not apply it to a different
        // major version: libvlc_media_player_t must embed vlc_object_t as its first field.
        if (version is not { Major: 3 })
        {
            throw new NotSupportedException("Embedded video output selection requires VLC 3.x.");
        }

        LibVlcNative.libvlc_media_player_set_hwnd(player, window);

        // set_hwnd resets vout and avcodec-hw to "", overriding instance options. Media options
        // cannot fix this: the vout is parented to the player, not the media input.
        // Restore the variable using the same checked setter as VLC's var_SetString.
        SetString(player, "vout", LibVlcRendererSelection.GetVoutOption(renderer, usesNativeOverlay, hardwareOverlayComposition));
        SetString(player, "avcodec-hw", LibVlcRendererSelection.GetHardwareDecodingOption(renderer, usesNativeOverlay, hardwareOverlayComposition));
    }

    private static void SetString(IntPtr player, string name, string text)
    {
        var value = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            const int vlcVarString = 0x0040;
            if (SetChecked(player, name, vlcVarString, new VlcValue { String = value }) != 0)
            {
                throw new InvalidOperationException($"VLC could not apply the embedded video setting '{name}'.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    internal static void SetReplayOutputReadyEvent(IntPtr player, string eventName)
    {
        // Called only for the verified bundled Studio GDI output on the VLC 3 ABI.
        // Vouts inherit from the player, not the media input. Each new player owns
        // one variable and one event; prepared inputs set it before enabling video.
        const string name = "studio-replay-output-ready";
        if (CreateVariable(player, name, 0x0040) != 0)
            throw new InvalidOperationException("VLC could not create the replay output gate.");
        SetString(player, name, eventName);
    }

    internal static void EnablePreparedVideo(IntPtr player)
    {
        // The prepared VLC 3 input deliberately has no video decoder/output. Its
        // video variable must be enabled before the public track-selection API can
        // create that decoder. Use the exported VLC 3 object API, as Bind does.
        var input = GetInputThread(player);
        if (input == IntPtr.Zero) throw new InvalidOperationException("The prepared replay input has ended.");
        try
        {
            const int vlcVarBool = 0x0020;
            if (SetChecked(input, "video", vlcVarBool, new VlcValue { Boolean = 1 }) != 0)
                throw new InvalidOperationException("VLC could not enable the prepared replay video.");
        }
        finally { ReleaseObject(input); }
    }

    // vlc_value_t is an eight-byte union on the supported Windows x64 VLC 3 ABI.
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct VlcValue
    {
        [FieldOffset(0)] public IntPtr String;
        [FieldOffset(0)] public byte Boolean;
    }

    [DllImport("libvlccore", EntryPoint = "var_SetChecked", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetChecked(IntPtr obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type, VlcValue value);

    [DllImport("libvlccore", EntryPoint = "var_Create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CreateVariable(IntPtr obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type);

    [DllImport("libvlc", EntryPoint = "libvlc_get_input_thread", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetInputThread(IntPtr player);

    [DllImport("libvlccore", EntryPoint = "vlc_object_release", CallingConvention = CallingConvention.Cdecl)]
    private static extern void ReleaseObject(IntPtr obj);
}
