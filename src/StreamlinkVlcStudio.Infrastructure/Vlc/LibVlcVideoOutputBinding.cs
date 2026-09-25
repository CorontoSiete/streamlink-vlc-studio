using System.Runtime.InteropServices;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static class LibVlcVideoOutputBinding
{
    internal static void Bind(IntPtr player, IntPtr window, VideoRendererMode renderer, Version? version,
        bool usesNativeOverlay)
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
        SetString(player, "vout", LibVlcRendererSelection.GetVoutOption(renderer));
        SetString(player, "avcodec-hw", LibVlcRendererSelection.GetHardwareDecodingOption(renderer, usesNativeOverlay));
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

    // vlc_value_t is an eight-byte union on the supported Windows x64 VLC 3 ABI.
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct VlcValue
    {
        [FieldOffset(0)] public IntPtr String;
    }

    [DllImport("libvlccore", EntryPoint = "var_SetChecked", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetChecked(IntPtr obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type, VlcValue value);
}
