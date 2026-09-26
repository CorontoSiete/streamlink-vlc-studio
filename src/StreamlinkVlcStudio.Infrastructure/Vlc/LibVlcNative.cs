using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static partial class LibVlcNative
{
    [LibraryImport("kernel32", EntryPoint = "SetDllDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDllDirectory(string? lpPathName);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_new(
        int argc,
        IntPtr argv);

    internal static IntPtr CreateInstance(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var argumentPointers = new IntPtr[arguments.Count];
        var nativeArguments = IntPtr.Zero;
        try
        {
            nativeArguments = Marshal.AllocHGlobal(checked((arguments.Count + 1) * IntPtr.Size));
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index]
                    ?? throw new ArgumentException("A libVLC argument cannot be null.", nameof(arguments));
                var argumentPointer = Marshal.StringToCoTaskMemUTF8(argument);
                argumentPointers[index] = argumentPointer;
                Marshal.WriteIntPtr(nativeArguments, checked(index * IntPtr.Size), argumentPointer);
            }

            Marshal.WriteIntPtr(nativeArguments, checked(arguments.Count * IntPtr.Size), IntPtr.Zero);
            return libvlc_new(arguments.Count, nativeArguments);
        }
        finally
        {
            foreach (var argumentPointer in argumentPointers)
            {
                if (argumentPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(argumentPointer);
                }
            }

            if (nativeArguments != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(nativeArguments);
            }
        }
    }

    [DllImport("msvcrt", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int putenv_s(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    // Returns a pointer to a static string owned by libVLC, for example "3.0.12 Vetinari".
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_get_version();

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_release(IntPtr instance);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_retain(IntPtr instance);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_video_get_track_description(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_video_set_track(IntPtr player, int track);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_media_new_location(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mediaLocation);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_media_release(IntPtr media);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_media_add_option(IntPtr media,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string option);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_video_set_adjust_int(IntPtr player, uint option, int value);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_video_set_adjust_float(IntPtr player, uint option, float value);

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr PreviewLockCallback(IntPtr opaque, IntPtr planes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void PreviewUnlockCallback(IntPtr opaque, IntPtr picture, IntPtr planes);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_video_set_callbacks(IntPtr player, PreviewLockCallback lockCallback,
        PreviewUnlockCallback unlockCallback, IntPtr displayCallback, IntPtr opaque);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void libvlc_video_set_format(IntPtr player,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string chroma, uint width, uint height, uint pitch);

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
    internal static extern MediaPlayerState libvlc_media_player_get_state(IntPtr player);

    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int libvlc_media_get_stats(IntPtr media, out MediaStatistics statistics);

    // libvlc_media_stats_t, VLC 3.x: all fields are 32-bit, including the bitrates.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MediaStatistics
    {
        public int ReadBytes;
        public float InputBitrate;
        public int DemuxReadBytes;
        public float DemuxBitrate;
        public int DemuxCorrupted;
        public int DemuxDiscontinuity;
        public int DecodedVideo;
        public int DecodedAudio;
        public int DisplayedPictures;
        public int LostPictures;
        public int PlayedAudioBuffers;
        public int LostAudioBuffers;
        public int SentPackets;
        public int SentBytes;
        public float SendBitrate;
    }

    internal enum MediaPlayerState
    {
        NothingSpecial,
        Opening,
        Buffering,
        Playing,
        Paused,
        Stopped,
        Ended,
        Error
    }

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

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct TrackDescription
    {
        public readonly int Id;
        public readonly IntPtr Name;
        public readonly IntPtr Next;
    }
}
