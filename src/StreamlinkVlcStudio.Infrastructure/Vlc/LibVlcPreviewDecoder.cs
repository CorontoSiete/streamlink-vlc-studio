using System.Runtime.InteropServices;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

/// <summary>Decodes one downloaded DVR segment into memory, with no audio or native window.</summary>
internal static class LibVlcPreviewDecoder
{
    internal const int Width = 192;
    internal const int Height = 108;
    private const int Pitch = Width * 4;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static Task<byte[]?> DecodeAsync(byte[] segment, string vlcDirectory, CancellationToken token) =>
        DecodeAsync(segment, null, vlcDirectory, token);

    internal static async Task<byte[]?> DecodeAsync(byte[] segment, byte[]? initialization,
        string vlcDirectory, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        var extension = initialization is null ? ".ts" : ".mp4";
        var path = Path.Combine(Path.GetTempPath(), $"svs-preview-{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                if (initialization is not null) await output.WriteAsync(initialization, token).ConfigureAwait(false);
                await output.WriteAsync(segment, token).ConfigureAwait(false);
            }
            return await Task.Run(() => DecodeFileAsync(path, vlcDirectory, token), token).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Gate.Release();
        }
    }

    private static async Task<byte[]?> DecodeFileAsync(string path, string vlcDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Playback has already loaded libVLC. Do not change its process-wide plugin path.
        using var runtime = LibVlcRuntime.Acquire(vlcDirectory, VideoRendererMode.Automatic,
            ["--intf=dummy", "--ignore-config", "--no-audio", "--no-spu", "--no-osd",
             "--no-video-title-show", "--no-stats", "--avcodec-hw=none",
             Path.GetExtension(path) == ".mp4" ? "--demux=mp4" : "--demux=ts", "--quiet"], share: false);
        var memory = Marshal.AllocHGlobal(Pitch * Height + 31);
        var buffer = new IntPtr((memory.ToInt64() + 31) & ~31L);
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var frameGate = new SemaphoreSlim(1, 1);
        var captured = 0;
        LibVlcNative.PreviewLockCallback lockFrame = (_, planes) =>
        {
            frameGate.Wait();
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        LibVlcNative.PreviewUnlockCallback unlockFrame = (_, _, _) =>
        {
            try
            {
                if (Interlocked.Exchange(ref captured, 1) != 0) return;
                var pixels = new byte[Pitch * Height];
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
                completion.TrySetResult(pixels);
            }
            finally { frameGate.Release(); }
        };
        var media = IntPtr.Zero;
        var player = IntPtr.Zero;
        try
        {
            media = LibVlcNative.libvlc_media_new_location(runtime.Instance, new Uri(path).AbsoluteUri);
            if (media == IntPtr.Zero) return null;
            player = LibVlcNative.libvlc_media_player_new_from_media(media);
            if (player == IntPtr.Zero) return null;
            LibVlcNative.libvlc_video_set_callbacks(player, lockFrame, unlockFrame, IntPtr.Zero, IntPtr.Zero);
            LibVlcNative.libvlc_video_set_format(player, "RV32", Width, Height, Pitch);
            if (LibVlcNative.libvlc_media_player_play(player) != 0) return null;
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false); }
            catch (TimeoutException) { return null; }
        }
        finally
        {
            if (player != IntPtr.Zero)
            {
                LibVlcNative.libvlc_media_player_stop(player);
                LibVlcNative.libvlc_media_player_release(player);
            }
            if (media != IntPtr.Zero) LibVlcNative.libvlc_media_release(media);
            GC.KeepAlive(lockFrame);
            GC.KeepAlive(unlockFrame);
            Marshal.FreeHGlobal(memory);
        }
    }
}
