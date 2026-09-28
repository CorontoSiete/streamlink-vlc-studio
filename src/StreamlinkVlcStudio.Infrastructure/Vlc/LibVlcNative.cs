using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal static partial class LibVlcNative
{
    // VLC 3's localization startup and the MSVCRT environment share process-wide
    // state. Concurrent initialization/environment writes can crash inside gettext.
    private static readonly object InitializationGate = new();
    private static string? configuredVlcDirectory;
    private static string? configuredLibVlcSha256;
    private static string? configuredCoreSha256;
    private static string? configuredCorePath;
    private static string? loadedCoreSha256;
    private static IntPtr libVlcHandle;
    private static IntPtr libVlcCoreHandle;
    private static string? coreSelectionDescription;

    static LibVlcNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibVlcNative).Assembly, ResolveVlcLibrary);
    }

    [LibraryImport("kernel32", EntryPoint = "SetDllDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryNative(string? lpPathName);

    internal static bool SetDllDirectory(string? lpPathName)
    {
        if (lpPathName is not null) ConfigureVlcDirectory(lpPathName);
        return SetDllDirectoryNative(lpPathName);
    }

    internal static string CoreSelectionDescription
    {
        get
        {
            lock (InitializationGate)
                return coreSelectionDescription ?? "VLC native libraries are not configured.";
        }
    }

    internal static string? ActiveCorePath
    {
        get
        {
            lock (InitializationGate)
                return configuredCorePath;
        }
    }

    internal static string? ActiveCoreSha256
    {
        get
        {
            lock (InitializationGate)
                return loadedCoreSha256;
        }
    }

    internal static void ConfigureVlcDirectory(string vlcDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vlcDirectory);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vlcDirectory));
        lock (InitializationGate)
        {
            if (configuredVlcDirectory is not null)
            {
                if (configuredVlcDirectory.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)) return;

                var requestedLibVlcHash = HashFile(Path.Combine(fullDirectory, "libvlc.dll"));
                var requestedCoreHash = HashFile(Path.Combine(fullDirectory, "libvlccore.dll"));
                if (configuredLibVlcSha256!.Equals(requestedLibVlcHash, StringComparison.OrdinalIgnoreCase) &&
                    configuredCoreSha256!.Equals(requestedCoreHash, StringComparison.OrdinalIgnoreCase)) return;

                throw new InvalidOperationException(
                    "A different VLC build is already loaded in this process. Restart Stream Studio before changing VLC installations.");
            }

            var libVlcPath = Path.Combine(fullDirectory, "libvlc.dll");
            var selection = BundledVlcCoreRuntime.Select(fullDirectory);
            if (!SetDllDirectoryNative(fullDirectory))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not set the selected VLC library directory.");

            if (selection.IsBundledAddressWaitBuild)
            {
                EnsureVlcPluginPath(fullDirectory);
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VLC_DATA_PATH")) &&
                    SetEnvironmentVariable("VLC_DATA_PATH", fullDirectory) != 0)
                    throw new InvalidOperationException("The VLC data directory could not be configured for its bundled core.");
            }

            IntPtr coreHandle = IntPtr.Zero;
            IntPtr vlcHandle = IntPtr.Zero;
            try
            {
                coreHandle = NativeLibrary.Load(selection.Path);
                vlcHandle = NativeLibrary.Load(libVlcPath);
                libVlcCoreHandle = coreHandle;
                libVlcHandle = vlcHandle;
                configuredVlcDirectory = fullDirectory;
                configuredCorePath = selection.Path;
                loadedCoreSha256 = selection.Sha256;
                configuredLibVlcSha256 = HashFile(libVlcPath);
                configuredCoreSha256 = HashFile(Path.Combine(fullDirectory, "libvlccore.dll"));
                coreSelectionDescription = selection.IsBundledAddressWaitBuild
                    ? $"Loaded verified VLC 3.0.23 address-wait core SHA-256 {selection.Sha256}."
                    : $"Loaded the selected VLC core SHA-256 {selection.Sha256}; its DLL pair did not match the bundled 3.0.23 build.";
            }
            catch
            {
                if (vlcHandle != IntPtr.Zero) NativeLibrary.Free(vlcHandle);
                if (coreHandle != IntPtr.Zero) NativeLibrary.Free(coreHandle);
                throw;
            }
        }
    }

    private static IntPtr ResolveVlcLibrary(
        string libraryName,
        System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (libraryName.Equals("libvlc", StringComparison.OrdinalIgnoreCase))
        {
            if (libVlcHandle != IntPtr.Zero) return libVlcHandle;
            throw new DllNotFoundException("Configure the VLC directory before calling libVLC.");
        }
        if (libraryName.Equals("libvlccore", StringComparison.OrdinalIgnoreCase))
        {
            if (libVlcCoreHandle != IntPtr.Zero) return libVlcCoreHandle;
            throw new DllNotFoundException("Configure the VLC directory before calling libVLC core APIs.");
        }
        return IntPtr.Zero;
    }

    private static void EnsureVlcPluginPath(string vlcDirectory)
    {
        var pluginDirectory = Path.Combine(vlcDirectory, "plugins");
        var existing = Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH") ?? "";
        var paths = existing.Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!paths.Contains(pluginDirectory, StringComparer.OrdinalIgnoreCase)) paths.Add(pluginDirectory);
        if (paths.Count > 0 && SetEnvironmentVariable("VLC_PLUGIN_PATH", string.Join(Path.PathSeparator, paths)) != 0)
            throw new InvalidOperationException("The VLC plugin path could not be configured for its bundled core.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

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
            lock (InitializationGate)
            {
                return libvlc_new(arguments.Count, nativeArguments);
            }
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

    internal static int SetEnvironmentVariable(string name, string value)
    {
        lock (InitializationGate)
        {
            if (string.Equals(Environment.GetEnvironmentVariable(name), value, StringComparison.Ordinal) &&
                string.Equals(Marshal.PtrToStringUTF8(getenv(name)), value, StringComparison.Ordinal)) return 0;
            Environment.SetEnvironmentVariable(name, value);
            return putenv_s(name, value);
        }
    }

    [DllImport("msvcrt", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr getenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("msvcrt", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern int putenv_s(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    // Returns a pointer to a static string owned by libVLC, for example "3.0.12 Vetinari".
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr libvlc_get_version();

    [DllImport("libvlccore", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool module_exists([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void PreviewDisplayCallback(IntPtr opaque, IntPtr picture);

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
    internal static extern int libvlc_media_player_set_rate(IntPtr player, float rate);

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
