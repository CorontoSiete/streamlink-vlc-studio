using System.Diagnostics;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Previews;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

public sealed record LivePreviewFrame(int Width, int Height, byte[] Pixels);

/// <summary>A silent video-only player that never creates a native window or a playback tab.</summary>
public sealed class LibVlcLivePreview
{
    internal const int Width = 320;
    internal const int Height = 180;
    private readonly IStreamlinkService streamlink;
    private readonly IAppLogger? logger;
    private readonly Func<StreamTransportRequest, CancellationToken, Task<LivePreviewPlaylistSession?>> open;
    private readonly Func<Uri, string, Action<LivePreviewFrame>, CancellationToken, LivePreviewPlaybackOptions?, Task> decode;
    private readonly TimeSpan directStartupTimeout;

    public LibVlcLivePreview(IStreamlinkService streamlink, IAppLogger? logger = null)
        : this(streamlink, logger, new LivePreviewSourceResolver().OpenAsync, DecodeAsync) { }

    internal LibVlcLivePreview(IStreamlinkService streamlink, IAppLogger? logger,
        Func<StreamTransportRequest, CancellationToken, Task<LivePreviewPlaylistSession?>> open,
        Func<Uri, string, Action<LivePreviewFrame>, CancellationToken, LivePreviewPlaybackOptions?, Task> decode,
        TimeSpan? directStartupTimeout = null)
    {
        this.streamlink = streamlink;
        this.logger = logger;
        this.open = open;
        this.decode = decode;
        this.directStartupTimeout = directStartupTimeout ?? TimeSpan.FromSeconds(4);
    }

    public async Task RunAsync(StreamTransportRequest request, string vlcDirectory,
        Action<LivePreviewFrame> present, CancellationToken token)
    {
        if (await TryDirectAsync(request, vlcDirectory, present, token).ConfigureAwait(false)) return;
        // The transport owns and kills its process tree, including canceled startup.
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(20));
        await using var transport = await streamlink.StartExternalHttpAsync(request, startup.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await decode(transport.PlaybackUri, vlcDirectory, present, token, null).ConfigureAwait(false);
    }

    private async Task<bool> TryDirectAsync(StreamTransportRequest request, string vlcDirectory,
        Action<LivePreviewFrame> present, CancellationToken token)
    {
        LivePreviewPlaylistSession? source = null;
        try
        {
            var elapsed = Stopwatch.StartNew();
            using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                lookup.CancelAfter(TimeSpan.FromSeconds(2));
                source = await open(request, lookup.Token).ConfigureAwait(false);
                lookup.Token.ThrowIfCancellationRequested();
            }
            if (source is null) return false;
            logger?.Write(AppLogLevel.Debug, "Hover preview", $"Direct preview source ready in {elapsed.ElapsedMilliseconds} ms ({request.Target.Platform}).");
            using var playback = CancellationTokenSource.CreateLinkedTokenSource(token, source.FallbackToken);
            // The deadline applies only to first video; a successful preview can run
            // as long as it is hovered. A failed direct path retains Streamlink fallback.
            playback.CancelAfter(directStartupTimeout);
            using var stopSource = playback.Token.Register(source.Stop);
            var firstFrame = 0;
            await decode(source.PlaybackUri, vlcDirectory, frame =>
            {
                if (Interlocked.Exchange(ref firstFrame, 1) == 0) playback.CancelAfter(Timeout.InfiniteTimeSpan);
                present(frame);
            }, playback.Token, source.PlaybackOptions).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (LivePreviewPlaylistSession.IsSourceFailure(ex))
        {
            // Token payloads, signed URLs and provider diagnostics must not enter logs.
            logger?.Write(AppLogLevel.Debug, "Hover preview", $"Using Streamlink preview transport ({ex.GetType().Name}).");
            return false;
        }
        finally
        {
            // Includes a resolver that returns a session after its cancellation deadline.
            if (source is not null) await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static async Task DecodeAsync(Uri uri, string vlcDirectory,
        Action<LivePreviewFrame> present, CancellationToken token, LivePreviewPlaybackOptions? playbackOptions = null)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")))
            throw new FileNotFoundException("Configure the VLC installation directory to play previews.");

        // Previews can be the first playback in this process. Keep the existing plugin path.
        LibVlcNative.SetDllDirectory(vlcDirectory);
        List<string> options =
            ["--intf=dummy", "--ignore-config", "--no-audio", "--no-spu", "--no-osd",
             "--no-video-title-show", "--no-stats", "--avcodec-hw=none", "--network-caching=500", "--quiet"];
        if (playbackOptions is not null)
        {
            options.Add(playbackOptions.LowLatency ? "--adaptive-lowlatency=1" : "--adaptive-lowlatency=0");
            options.Add("--adaptive-livedelay=" + playbackOptions.LiveDelayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        using var runtime = LibVlcRuntime.Acquire(vlcDirectory, VideoRendererMode.Automatic, options, share: false);
        var lastFrameAt = Environment.TickCount64;
        long lastPresentation = 0;
        using var player = new LibVlcPreviewPlayer(runtime.Instance, uri, Width, Height, () =>
        {
            var now = Environment.TickCount64;
            Interlocked.Exchange(ref lastFrameAt, now);
            if (token.IsCancellationRequested || now - lastPresentation < 33) return false;
            lastPresentation = now;
            return true;
        }, pixels => present(new LivePreviewFrame(Width, Height, pixels)));
        if (!player.Start()) throw new InvalidOperationException("VLC could not play the preview.");
        while (true)
        {
            await Task.Delay(100, token).ConfigureAwait(false);
            if (player.CallbackFailure.IsCompleted) await player.CallbackFailure.ConfigureAwait(false);
            if (player.State is LibVlcNative.MediaPlayerState.Ended or LibVlcNative.MediaPlayerState.Error or LibVlcNative.MediaPlayerState.Stopped)
                throw new InvalidOperationException("The preview stream ended or is unavailable.");
            if (Environment.TickCount64 - Interlocked.Read(ref lastFrameAt) > 20_000)
                throw new TimeoutException("The preview stream stopped delivering video.");
        }
    }
}
