using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static partial class LibVlcNative
{
    internal const int MarqueeEnable = 0;
    internal const int MarqueeText = 1;
    internal const int MarqueeColor = 2;
    internal const int MarqueeOpacity = 3;
    internal const int MarqueePosition = 4;
    internal const int MarqueeRefresh = 5;
    internal const int MarqueeSize = 6;
    internal const int MarqueeTimeout = 7;
    internal const int MarqueeX = 8;
    internal const int MarqueeY = 9;
    internal const int PositionTopRight = 6;

    [LibraryImport("kernel32", EntryPoint = "SetDllDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDllDirectory(string? lpPathName);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_new(
        int argc,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv);

    [DllImport("msvcrt", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int putenv_s(string name, string value);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_release(IntPtr instance);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_media_new_location(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mediaLocation);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_media_release(IntPtr media);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_media_player_new_from_media(IntPtr media);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_media_player_release(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_media_player_set_hwnd(IntPtr player, IntPtr drawable);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_video_get_size(IntPtr player, uint num, out uint width, out uint height);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_video_get_cursor(IntPtr player, uint num, out int x, out int y);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_media_player_play(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_media_player_set_pause(IntPtr player, int pause);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long libvlc_media_player_get_time(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_media_player_set_time(IntPtr player, long time);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long libvlc_media_player_get_length(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_media_player_is_seekable(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_media_player_stop(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_audio_set_volume(IntPtr player, int volume);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_audio_set_mute(IntPtr player, int mute);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_audio_get_track(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_audio_set_track(IntPtr player, int track);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_audio_get_track_description(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_track_description_list_release(IntPtr trackDescription);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_video_set_marquee_int(IntPtr player, int option, int value);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_video_set_marquee_string(
        IntPtr player,
        int option,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct TrackDescription
    {
        public readonly int Id;
        public readonly IntPtr Name;
        public readonly IntPtr Next;
    }
}
