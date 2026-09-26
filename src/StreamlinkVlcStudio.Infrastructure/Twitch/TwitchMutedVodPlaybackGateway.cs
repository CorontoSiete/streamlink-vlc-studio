using System.Net.Sockets;
using System.Text;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Twitch;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>
/// Repairs invalid timestamps in Twitch's muted VOD segments. It decides, per media URL,
/// whether libVLC can open it as is: Twitch VOD playlists that
/// list muted segments are played through <see cref="TwitchMutedVodRepairProxy"/>.
/// Live transports, other providers and VODs
/// without muted segments ordinarily get their URL back untouched. A timestamped open can
/// also request the validated local transport for a completed TS replay on supported VLC.
/// Inspection failures fall back to the original URL.
/// </summary>
internal sealed class TwitchMutedVodPlaybackGateway : IPlaybackMediaSourceGateway, IAsyncDisposable
{
    private const string PlaylistExtension = ".m3u8";
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UpstreamRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly ReplayUrlSecurityValidator replayUrlValidator;
    private readonly TimeSpan probeTimeout;
    private readonly TwitchMutedVodRepairProxy proxy;
    private readonly TwitchVodPlaylistHandoff playlistHandoff;
    private int disposed;

    internal TwitchMutedVodPlaybackGateway(IAppLogger logger, TwitchVodPlaylistHandoff? playlistHandoff = null)
        : this(
            logger,
            HttpClientFactory.Create(UpstreamRequestTimeout, allowAutoRedirect: false),
            ReplayUrlSecurityValidator.Shared,
            DefaultProbeTimeout,
            ownsHttpClient: true, playlistHandoff ?? TwitchVodPlaylistHandoff.Shared)
    {
    }

    internal TwitchMutedVodPlaybackGateway(
        IAppLogger logger,
        HttpClient httpClient,
        ReplayUrlSecurityValidator replayUrlValidator,
        TimeSpan? probeTimeout = null, TwitchVodPlaylistHandoff? playlistHandoff = null)
        : this(logger, httpClient, replayUrlValidator, probeTimeout ?? DefaultProbeTimeout, ownsHttpClient: false,
            playlistHandoff ?? new TwitchVodPlaylistHandoff())
    {
    }

    private TwitchMutedVodPlaybackGateway(
        IAppLogger logger,
        HttpClient httpClient,
        ReplayUrlSecurityValidator replayUrlValidator,
        TimeSpan probeTimeout,
        bool ownsHttpClient, TwitchVodPlaylistHandoff playlistHandoff)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probeTimeout, TimeSpan.Zero);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.replayUrlValidator = replayUrlValidator ?? throw new ArgumentNullException(nameof(replayUrlValidator));
        this.probeTimeout = probeTimeout;
        this.ownsHttpClient = ownsHttpClient;
        this.playlistHandoff = playlistHandoff;
        proxy = new TwitchMutedVodRepairProxy(logger, httpClient, replayUrlValidator);
    }

    public async Task<PlaybackMediaSource> PrepareAsync(
        Uri mediaUri,
        Version? libVlcVersion,
        CancellationToken cancellationToken, bool preferFastReplay = false)
    {
        ArgumentNullException.ThrowIfNull(mediaUri);
        if (Volatile.Read(ref disposed) != 0 ||
            !IsTwitchVodPlaylist(mediaUri))
        {
            return PlaybackMediaSource.Direct(mediaUri);
        }

        var player = DescribePlayer(libVlcVersion);
        var media = TwitchMutedVodRepairLog.Describe(mediaUri);
        var source = new TwitchVodPlaylistSource(
            mediaUri,
            mediaUri.IsFile
                ? token => ReadLocalPlaylistAsync(mediaUri.LocalPath, token)
                : token => ReadRemotePlaylistAsync(mediaUri, token));
        try
        {
            string playlist;
            using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                probe.CancelAfter(probeTimeout);
                probe.Token.ThrowIfCancellationRequested();
                playlist = playlistHandoff.Take(mediaUri) ?? await source.ReadAsync(probe.Token).ConfigureAwait(false);
            }

            var inspection = TwitchMutedVodPlaylist.Inspect(playlist);
            var preroll = preferFastReplay ? TwitchVodReplayPolicy.GetPreroll(playlist, libVlcVersion) : TimeSpan.Zero;
            var fastReplay = preroll > TimeSpan.Zero;
            if (inspection.MutedSegments == 0 && !fastReplay)
            {
                LogDirectPlayback(media, player, inspection);
                return PlaybackMediaSource.Direct(mediaUri);
            }

            // Reject a playlist the proxy could not serve now, while falling back is still possible.
            _ = TwitchMutedVodPlaylist.RewriteForRepair(playlist, mediaUri, static uri => uri.AbsoluteUri);
            var session = proxy.OpenSession(source, playlist, proxyAllSegments: fastReplay);
            if (inspection.MutedSegments > 0) logger.Write(
                AppLogLevel.Info,
                TwitchMutedVodRepairLog.Source,
                $"{media} lists {inspection.MutedSegments} muted segment(s); removing invalid timestamps " +
                $"through the local repair proxy for {player}. These can prevent end-of-media even in VLC 3.0.23.");
            if (fastReplay)
                logger.Write(AppLogLevel.Info, "VOD resume", "Using VLC's FFmpeg demuxer for a completed MPEG-TS replay through the validated local transport.");
            return new PlaybackMediaSource(session.PlaylistUri, session, useAvformatDemuxer: fastReplay, replaySeekPreroll: preroll);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsInspectionFailure(ex))
        {
            logger.Write(
                AppLogLevel.Warning,
                TwitchMutedVodRepairLog.Source,
                $"Could not inspect {media} for muted segments within {probeTimeout.TotalSeconds:0.#} seconds; playing it directly. " +
                $"If it has muted segments, their invalid timestamps may interrupt playback or prevent end-of-media in {player}.",
                ex);
            return PlaybackMediaSource.Direct(mediaUri);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await proxy.DisposeAsync().ConfigureAwait(false);
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private static string DescribePlayer(Version? libVlcVersion) =>
        libVlcVersion is null ? "this libVLC release" : $"libVLC {libVlcVersion}";

    /// <summary>
    /// A media playlist on an approved Twitch HTTPS host, or the local playlist file the
    /// subscriber-only VOD fallback writes. Streamlink's loopback transports, other providers and
    /// non-playlist media never match.
    /// </summary>
    private static bool IsTwitchVodPlaylist(Uri mediaUri) =>
        TwitchMutedVodPlaylist.IsSupportedPlaylistLocation(mediaUri) &&
        mediaUri.AbsolutePath.EndsWith(PlaylistExtension, StringComparison.OrdinalIgnoreCase);

    private static bool IsInspectionFailure(Exception exception) => exception is
        HttpRequestException or
        IOException or
        InvalidDataException or
        OperationCanceledException or
        TimeoutException or
        DecoderFallbackException or
        UnauthorizedAccessException or
        SocketException or
        ObjectDisposedException or
        InvalidOperationException;

    /// <summary>
    /// Leaves a breadcrumb for the cases this shim cannot protect: a playlist that is still growing
    /// can gain muted segments after playback started directly, and muted segments in another
    /// container are not repaired at all.
    /// </summary>
    private void LogDirectPlayback(string media, string player, TwitchMutedVodPlaylistInspection inspection)
    {
        if (inspection.UnsupportedMutedSegments > 0)
        {
            logger.Write(
                AppLogLevel.Warning,
                TwitchMutedVodRepairLog.Source,
                $"{media} lists {inspection.UnsupportedMutedSegments} muted segment(s) in a container the repair does not handle; " +
                $"playing it directly on {player}.");
            return;
        }

        var state = !inspection.IsMediaPlaylist
            ? "is not a media playlist"
            : inspection.IsComplete
                ? "is complete and lists no muted segments"
                : "lists no muted segments yet but is still growing, so it may gain some that are not repaired";
        logger.Write(
            AppLogLevel.Debug,
            TwitchMutedVodRepairLog.Source,
            $"{media} {state}; playing it directly on {player}.");
    }

    private async Task<string> ReadRemotePlaylistAsync(Uri playlistUri, CancellationToken cancellationToken)
    {
        var playlist = await ValidatedReplayHttpClient.ReadPlaylistAsync(
            httpClient, replayUrlValidator, playlistUri, PlatformKind.Twitch, cancellationToken).ConfigureAwait(false);
        // Each refresh may redirect to another location. Resolve relative entries now,
        // before the proxy associates the text with the session's original playlist URL.
        return TwitchMutedVodPlaylist.RewriteForRepair(playlist.Content, playlist.Uri, static uri => uri.AbsoluteUri);
    }

    private static async Task<string> ReadLocalPlaylistAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await BoundedByteReader
            .ReadFileAsync(path, PayloadLimits.PlaylistBytes, cancellationToken)
            .ConfigureAwait(false);
        return bytes is null
            ? throw new FileNotFoundException("The local playlist is missing, empty, or larger than the playlist limit.", path)
            : StrictUtf8.GetString(bytes);
    }
}
