internal static class MultistreamVlcDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, string> Paths = [];
    private static readonly LogCallback Callback = Write;

    internal static void Attach(LibVlcPlaybackEngine engine, string directory)
    {
        if (Environment.GetEnvironmentVariable("SVS_BENCHMARK_VLC_DIAGNOSTICS") != "1") return;
        var instance = (IntPtr)typeof(LibVlcPlaybackEngine).GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        lock (Gate) Paths[instance] = Path.Combine(directory, $"vlc-{engine.NativeOverlayPipeName}.log");
        libvlc_log_set(instance, Callback, instance);
    }

    private static void Write(IntPtr data, int level, IntPtr context, IntPtr format, IntPtr arguments)
    {
        var buffer = new byte[8192];
        _ = vsnprintf(buffer, (nuint)buffer.Length, format, arguments);
        var end = Array.IndexOf(buffer, (byte)0);
        var message = Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        libvlc_log_get_context(context, out var module, out _, out _);
        lock (Gate)
        {
            if (Paths.TryGetValue(data, out var path))
                File.AppendAllText(path, $"{DateTime.UtcNow:O} {level} {Marshal.PtrToStringUTF8(module)}: {message}{Environment.NewLine}");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(IntPtr data, int level, IntPtr context, IntPtr format, IntPtr arguments);
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_log_set(IntPtr instance, LogCallback callback, IntPtr data);
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_log_get_context(IntPtr context, out IntPtr module, out IntPtr file, out uint line);
    [DllImport("msvcrt", EntryPoint = "_vsnprintf", CallingConvention = CallingConvention.Cdecl)]
    private static extern int vsnprintf(byte[] buffer, nuint count, IntPtr format, IntPtr arguments);
}
