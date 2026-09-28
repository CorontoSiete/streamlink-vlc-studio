using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

/// <summary>Decodes one downloaded DVR segment into memory, with no audio or native window.</summary>
internal static class LibVlcPreviewDecoder
{
    internal const int Width = 192;
    internal const int Height = 108;
    private static readonly SemaphoreSlim Gate = new(1, 1);

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
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = 0;
        using var player = new LibVlcPreviewPlayer(runtime.Instance, new Uri(path), Width, Height,
            () => Interlocked.Exchange(ref captured, 1) == 0, pixels => completion.TrySetResult(pixels), captureOnDecode: true);
        if (!player.Start()) return null;
        try
        {
            await Task.WhenAny(completion.Task, player.CallbackFailure).WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
            if (player.CallbackFailure.IsCompleted) await player.CallbackFailure.ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
    }
}
