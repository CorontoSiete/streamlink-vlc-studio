using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Twitch;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

/// <summary>Where a Twitch VOD media playlist lives and how to (re)read its current text.</summary>
internal sealed record TwitchVodPlaylistSource(Uri PlaylistUri, Func<CancellationToken, Task<string>> ReadAsync);

/// <summary>
/// A playlist published by <see cref="TwitchMutedVodRepairProxy"/>. Disposing it revokes the
/// playlist and segment URLs.
/// </summary>
internal sealed class TwitchMutedVodRepairSession : IDisposable
{
    private Action? release;

    internal TwitchMutedVodRepairSession(Uri playlistUri, Action release)
    {
        PlaylistUri = playlistUri;
        this.release = release;
    }

    internal Uri PlaylistUri { get; }

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}

/// <summary>
/// Loopback HTTP server that lets an affected libVLC play Twitch VODs containing muted segments.
/// It serves each session's playlist with the muted segments pointed back at itself and streams
/// those segments from Twitch through <see cref="TwitchMutedSegmentSanitizer"/>. Ordinarily
/// other segments keep their Twitch URL. Completed replays can opt into transporting all
/// segments for VLC's FFmpeg demuxer, whose bundled HTTP client does not support HTTPS.
/// <para>
/// The listener binds to 127.0.0.1 on an ephemeral port, every URL carries an unguessable
/// per-session token, and only segment URIs taken from a validated playlist can be fetched, through
/// the same public-host validation as all other replay requests.
/// </para>
/// </summary>
internal sealed class TwitchMutedVodRepairProxy : IAsyncDisposable
{
    private const string PlaylistFileName = "playlist.m3u8";
    private const string SegmentDirectory = "s";
    private const string SegmentExtension = ".ts";
    private const int TokenByteLength = 16;
    private const int MaxSegmentIndexDigits = 9;
    private const int MaxRequestBytes = 8 * 1024;
    // Reading a request costs no upstream work, so many connections may be open at once; only a
    // request that names a live session is admitted to the much smaller pool that fetches from
    // Twitch. A caller without a token can therefore never starve the player of transfers.
    private const int MaxOpenConnections = 256;
    private const int MaxConcurrentTransfers = 64;
    private const int MaxSegmentsPerSession = 200_000;
    private const long MaxSegmentBytes = 512L * 1024 * 1024;
    private const int MaxConsecutiveAcceptFailures = 8;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ControlResponseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpstreamResponseTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TransferIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RejectionLogInterval = TimeSpan.FromSeconds(30);

    private readonly IAppLogger logger;
    private readonly HttpClient httpClient;
    private readonly ReplayUrlSecurityValidator replayUrlValidator;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim connectionSlots = new(MaxOpenConnections, MaxOpenConnections);
    private readonly SemaphoreSlim transferSlots = new(MaxConcurrentTransfers, MaxConcurrentTransfers);
    private readonly object lifecycleGate = new();
    private readonly HashSet<Task> backgroundTasks = [];
    private TcpListener? listener;
    private Task? acceptLoop;
    private Task? disposalTask;
    private int port;
    private long lastRejectionLogTicks = long.MinValue / 2;

    internal TwitchMutedVodRepairProxy(
        IAppLogger logger,
        HttpClient httpClient,
        ReplayUrlSecurityValidator replayUrlValidator)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.replayUrlValidator = replayUrlValidator ?? throw new ArgumentNullException(nameof(replayUrlValidator));
    }

    /// <summary>
    /// Publishes a playlist and returns the loopback URL libVLC should open instead of it.
    /// </summary>
    /// <param name="initialPlaylist">
    /// Playlist text the caller already fetched; it answers the first playlist request so
    /// starting playback does not read the playlist from Twitch twice.
    /// </param>
    /// <exception cref="SocketException">The loopback listener could not be started.</exception>
    internal TwitchMutedVodRepairSession OpenSession(TwitchVodPlaylistSource source, string? initialPlaylist = null,
        bool proxyAllSegments = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposalTask is not null, this);
            EnsureListeningCore();
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenByteLength));
            var session = new Session(token, source, initialPlaylist, proxyAllSegments);
            sessions[token] = session;
            return new TwitchMutedVodRepairSession(
                new Uri($"http://127.0.0.1:{port}/{token}/{PlaylistFileName}"),
                () => ReleaseSession(session));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate)
        {
            disposalTask ??= DisposeCoreAsync();
            return new ValueTask(disposalTask);
        }
    }

    private void EnsureListeningCore()
    {
        if (listener is not null && acceptLoop is { IsCompleted: false })
        {
            return;
        }

        // Either the first session, or the accept loop ended although the proxy is still in use.
        // Reusing the previous port makes the URLs of sessions that are still playing work again.
        var previousPort = port;
        listener?.Dispose();
        listener = null;
        acceptLoop = null;

        var nextListener = StartListener(previousPort);
        // Written under lifecycleGate, but connection handlers read it while building segment URLs.
        Volatile.Write(ref port, ((IPEndPoint)nextListener.LocalEndpoint).Port);
        listener = nextListener;
        acceptLoop = AcceptLoopAsync(nextListener, cancellation.Token);
        logger.Write(
            AppLogLevel.Info,
            TwitchMutedVodRepairLog.Source,
            previousPort == 0
                ? $"Muted VOD repair proxy listening on 127.0.0.1:{port}."
                : $"Muted VOD repair proxy restarted on 127.0.0.1:{port} (previously port {previousPort}).");
    }

    private static TcpListener StartListener(int preferredPort)
    {
        if (preferredPort != 0)
        {
            var preferred = new TcpListener(IPAddress.Loopback, preferredPort);
            try
            {
                preferred.Start();
                return preferred;
            }
            catch (SocketException)
            {
                preferred.Dispose();
            }
        }

        var ephemeral = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            ephemeral.Start();
            return ephemeral;
        }
        catch
        {
            ephemeral.Dispose();
            throw;
        }
    }

    private void ReleaseSession(Session session)
    {
        if (!sessions.TryRemove(new KeyValuePair<string, Session>(session.Token, session)))
        {
            return;
        }
        session.Revoke();

        var statistics = session.Statistics;
        var unrepaired = statistics.UnrepairedSegments == 0
            ? ""
            : $" ({statistics.UnrepairedSegments} of them had nothing to repair)";
        logger.Write(
            AppLogLevel.Info,
            TwitchMutedVodRepairLog.Source,
            $"Released the muted VOD repair session for {TwitchMutedVodRepairLog.Describe(session.Source.PlaylistUri)} " +
            $"after {statistics.ServedSegments} muted segment(s){unrepaired} and {statistics.RemovedTimestamps} removed timestamp(s).");
    }

    private async Task DisposeCoreAsync()
    {
        // Runs once disposalTask is set, after which OpenSession refuses to touch listener and
        // acceptLoop; that is what makes reading them here without lifecycleGate safe.
        foreach (var session in sessions.Values) session.Revoke();
        await cancellation.CancelAsync().ConfigureAwait(false);
        listener?.Dispose();

        var loop = acceptLoop;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        await DrainBackgroundTasksAsync().ConfigureAwait(false);
        sessions.Clear();
        cancellation.Dispose();
        connectionSlots.Dispose();
        transferSlots.Dispose();

        lock (lifecycleGate)
        {
            listener = null;
            acceptLoop = null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        try
        {
            await AcceptConnectionsAsync(activeListener, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing awaits this task once a restart replaced it, so a failure that the loop did
            // not anticipate has to be reported here or it would strand muted VODs without a trace.
            logger.Write(
                AppLogLevel.Error,
                TwitchMutedVodRepairLog.Source,
                "The muted VOD repair proxy listener failed unexpectedly; muted VODs that are playing will stall. It restarts with the next muted VOD.",
                ex);
        }
    }

    private async Task AcceptConnectionsAsync(TcpListener activeListener, CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await activeListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // A connection that is reset before it is accepted surfaces as a SocketException
                // while the listener is still healthy, so that alone must not end the loop. A
                // disposed or stopped listener (InvalidOperationException) cannot recover.
                consecutiveFailures++;
                if (ex is not SocketException || consecutiveFailures >= MaxConsecutiveAcceptFailures)
                {
                    // OpenSession notices the finished loop and starts a new listener.
                    logger.Write(
                        AppLogLevel.Error,
                        TwitchMutedVodRepairLog.Source,
                        "The muted VOD repair proxy listener stopped unexpectedly; muted VODs that are playing will stall. It restarts with the next muted VOD.",
                        ex);
                    return;
                }

                logger.Write(
                    AppLogLevel.Debug,
                    TwitchMutedVodRepairLog.Source,
                    "The muted VOD repair proxy could not accept a connection; retrying.",
                    ex);
                try
                {
                    await Task.Delay(AcceptRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (!connectionSlots.Wait(0, CancellationToken.None))
            {
                client.Dispose();
                LogRejection($"more than {MaxOpenConnections} connections are open");
                continue;
            }

            TrackBackgroundTask(HandleClientAsync(client, cancellationToken));
        }
    }

    private void LogRejection(string reason)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastRejectionLogTicks);
        if (now - last < RejectionLogInterval.TotalMilliseconds ||
            Interlocked.CompareExchange(ref lastRejectionLogTicks, now, last) != last)
        {
            return;
        }

        logger.Write(
            AppLogLevel.Warning,
            TwitchMutedVodRepairLog.Source,
            $"The muted VOD repair proxy refused a request because {reason}; playback of muted VODs may stall.");
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (lifecycleGate)
        {
            backgroundTasks.Add(task);
        }

        _ = RemoveBackgroundTaskWhenCompleteAsync(task);
    }

    private async Task RemoveBackgroundTaskWhenCompleteAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // HandleClientAsync handles its own failures; this only keeps a future regression
            // there from becoming an unobserved task fault.
            logger.Write(
                AppLogLevel.Error,
                TwitchMutedVodRepairLog.Source,
                "A muted VOD repair connection handler faulted unexpectedly.",
                ex);
        }
        finally
        {
            lock (lifecycleGate)
            {
                backgroundTasks.Remove(task);
            }
        }
    }

    private async Task DrainBackgroundTasksAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (lifecycleGate)
            {
                tasks = [.. backgroundTasks];
            }

            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            LocalHttpRequestReadResult readResult;
            using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readTimeout.CancelAfter(RequestReadTimeout);
                readResult = await LocalHttpRequestReader
                    .ReadWithStatusAsync(stream, MaxRequestBytes, readTimeout.Token)
                    .ConfigureAwait(false);
            }

            if (!readResult.IsSuccess)
            {
                await WriteTextResponseAsync(
                        stream,
                        readResult.StatusCode,
                        readResult.ReasonPhrase,
                        readResult.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await RouteAsync(client, stream, readResult.Request!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The player closed the connection (seek, stop, tab closed), a request never arrived,
            // or the proxy is shutting down. Upstream failures are logged where they happen.
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, TwitchMutedVodRepairLog.Source, "Muted VOD repair request failed.", ex);
        }
        finally
        {
            connectionSlots.Release();
        }
    }

    private async Task RouteAsync(TcpClient client, Stream stream, LocalHttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseRoute(request.Path, out var token, out var segmentIndex) ||
            !sessions.TryGetValue(token, out var session))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Not found.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!request.Method.Equals("GET", StringComparison.Ordinal))
        {
            await WriteTextResponseAsync(
                    stream,
                    405,
                    "Method Not Allowed",
                    "Only GET is supported.",
                    cancellationToken,
                    allow: "GET")
                .ConfigureAwait(false);
            return;
        }

        if (!transferSlots.Wait(0, CancellationToken.None))
        {
            LogRejection($"{MaxConcurrentTransfers} transfers are already running");
            await WriteTextResponseAsync(stream, 503, "Service Unavailable", "Too many transfers.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!session.Attach(client, out var sessionToken))
        {
            transferSlots.Release();
            return;
        }
        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionToken);
        try
        {
            // A Range header is deliberately ignored: the whole representation is a valid answer
            // to any range request (RFC 9110, 14.2), and libVLC only ever asks for "bytes=0-".
            await (segmentIndex is { } index
                    ? ServeSegmentAsync(stream, session, index, transferCancellation.Token)
                    : ServePlaylistAsync(stream, session, transferCancellation.Token))
                .ConfigureAwait(false);
        }
        finally
        {
            session.Detach(client);
            transferSlots.Release();
        }
    }

    private async Task ServePlaylistAsync(Stream stream, Session session, CancellationToken cancellationToken)
    {
        byte[] body;
        try
        {
            var content = session.TakeInitialPlaylist();
            if (content is null)
            {
                using var upstreamTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                upstreamTimeout.CancelAfter(UpstreamResponseTimeout);
                content = await session.Source.ReadAsync(upstreamTimeout.Token).ConfigureAwait(false);
            }

            var rewritten = TwitchMutedVodPlaylist.RewriteForRepair(
                content,
                session.Source.PlaylistUri,
                segmentUri => BuildSegmentUrl(session, segmentUri), session.ProxyAllSegments);
            body = Encoding.UTF8.GetBytes(rewritten);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Write(
                AppLogLevel.Warning,
                TwitchMutedVodRepairLog.Source,
                $"Could not load the playlist {TwitchMutedVodRepairLog.Describe(session.Source.PlaylistUri)} for the muted VOD repair proxy.",
                ex);
            await WriteTextResponseAsync(stream, 502, "Bad Gateway", "The playlist is unavailable.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(ControlResponseTimeout);
        await WriteHeadersAsync(stream, 200, "OK", "application/vnd.apple.mpegurl", body.Length, responseTimeout.Token)
            .ConfigureAwait(false);
        await stream.WriteAsync(body, responseTimeout.Token).ConfigureAwait(false);
    }

    private async Task ServeSegmentAsync(Stream stream, Session session, int index, CancellationToken cancellationToken)
    {
        if (!session.TryGetSegment(index, out var segmentUri))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Not found.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var segment = TwitchMutedVodRepairLog.Describe(segmentUri);
        HttpResponseMessage response;
        Stream upstream;
        try
        {
            using var upstreamTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            upstreamTimeout.CancelAfter(UpstreamResponseTimeout);
            response = await ValidatedReplayHttpClient.SendGetAsync(
                    httpClient,
                    replayUrlValidator,
                    segmentUri,
                    PlatformKind.Twitch,
                    static requestUri => new HttpRequestMessage(HttpMethod.Get, requestUri),
                    upstreamTimeout.Token)
                .ConfigureAwait(false);
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Twitch returned {(int)response.StatusCode}.");
                }

                upstream = await response.Content.ReadAsStreamAsync(upstreamTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Write(AppLogLevel.Warning, TwitchMutedVodRepairLog.Source, $"Could not fetch the muted segment {segment}.", ex);
            await WriteTextResponseAsync(stream, 502, "Bad Gateway", "The segment is unavailable.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using (response)
        await using (upstream.ConfigureAwait(false))
        {
            using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headerTimeout.CancelAfter(ControlResponseTimeout);
                // The repair never changes a segment's length, so the upstream length still holds.
                await WriteHeadersAsync(stream, 200, "OK", "video/mp2t", response.Content.Headers.ContentLength, headerTimeout.Token)
                    .ConfigureAwait(false);
            }

            try
            {
                var muted = TwitchMutedVodPlaylist.IsMutedSegment(segmentUri);
                var result = await TwitchMutedSegmentRepairCopier
                    .CopyAsync(upstream, stream, MaxSegmentBytes, TransferIdleTimeout, cancellationToken, repairTimestamps: muted)
                    .ConfigureAwait(false);
                if (muted && session.RecordServedSegment(result.Repairs))
                {
                    // Every muted segment seen so far carried the invalid timestamps. One without
                    // them is either harmless or uses a layout the repair does not recognize.
                    logger.Write(
                        AppLogLevel.Warning,
                        TwitchMutedVodRepairLog.Source,
                        $"Found nothing to repair in the muted segment {segment}. If playback freezes there, Twitch changed how muted segments are written.");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or TimeoutException or TwitchMutedSegmentSourceException)
            {
                // The response has started, so the only remaining signal is closing the connection.
                // A player that simply went away surfaces as a plain IOException and is not logged.
                logger.Write(AppLogLevel.Warning, TwitchMutedVodRepairLog.Source, $"The muted segment {segment} was interrupted.", ex);
            }
        }
    }

    private string BuildSegmentUrl(Session session, Uri segmentUri) => string.Create(
        CultureInfo.InvariantCulture,
        $"http://127.0.0.1:{Volatile.Read(ref port)}/{session.Token}/{SegmentDirectory}/{session.GetOrAddSegment(segmentUri)}{SegmentExtension}");

    private static bool TryParseRoute(string path, out string token, out int? segmentIndex)
    {
        token = "";
        segmentIndex = null;
        if (path.Length < 2 || path[0] != '/')
        {
            return false;
        }

        var parts = path[1..].Split('/');
        if (parts.Length is < 2 or > 3 || !IsToken(parts[0]))
        {
            return false;
        }

        token = parts[0];
        if (parts.Length == 2)
        {
            return parts[1].Equals(PlaylistFileName, StringComparison.Ordinal);
        }

        if (!parts[1].Equals(SegmentDirectory, StringComparison.Ordinal) ||
            !parts[2].EndsWith(SegmentExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = parts[2].AsSpan(0, parts[2].Length - SegmentExtension.Length);
        var isCanonical = digits.Length is > 0 and <= MaxSegmentIndexDigits &&
            (digits.Length == 1 || digits[0] != '0') &&
            digits.IndexOfAnyExceptInRange('0', '9') < 0;
        if (!isCanonical)
        {
            return false;
        }

        segmentIndex = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool IsToken(string value) =>
        value.Length == TokenByteLength * 2 &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private static async Task WriteTextResponseAsync(
        Stream stream,
        int statusCode,
        string reasonPhrase,
        string message,
        CancellationToken cancellationToken,
        string? allow = null)
    {
        var body = Encoding.UTF8.GetBytes(message);
        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(ControlResponseTimeout);
        await WriteHeadersAsync(
                stream,
                statusCode,
                reasonPhrase,
                "text/plain; charset=utf-8",
                body.Length,
                responseTimeout.Token,
                allow)
            .ConfigureAwait(false);
        await stream.WriteAsync(body, responseTimeout.Token).ConfigureAwait(false);
    }

    private static async Task WriteHeadersAsync(
        Stream stream,
        int statusCode,
        string reasonPhrase,
        string contentType,
        long? contentLength,
        CancellationToken cancellationToken,
        string? allow = null)
    {
        var builder = new StringBuilder(256)
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Type: {contentType}\r\n");
        if (contentLength is { } length)
        {
            builder.Append(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n");
        }

        if (allow is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"Allow: {allow}\r\n");
        }

        // One request per connection keeps the server trivial; without a Content-Length the
        // closing connection is also what delimits the body.
        builder.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct SessionStatistics(int ServedSegments, int UnrepairedSegments, int RemovedTimestamps);

    private sealed class Session(string token, TwitchVodPlaylistSource source, string? initialPlaylist, bool proxyAllSegments)
    {
        private readonly object gate = new();
        private readonly List<Uri> segments = [];
        private readonly Dictionary<string, int> segmentIndexes = new(StringComparer.Ordinal);
        private string? initialPlaylist = initialPlaylist;
        private int servedSegments;
        private int unrepairedSegments;
        private int removedTimestamps;
        private readonly HashSet<TcpClient> clients = [];
        private readonly CancellationTokenSource requestsCancellation = new();
        private bool revoked;
        private bool revoking;

        internal bool ProxyAllSegments { get; } = proxyAllSegments;

        internal bool Attach(TcpClient client, out CancellationToken token)
        {
            lock (gate)
            {
                token = default;
                if (revoked) return false;
                clients.Add(client);
                token = requestsCancellation.Token;
                return true;
            }
        }

        internal void Detach(TcpClient client)
        {
            lock (gate)
            {
                clients.Remove(client);
                if (revoked && !revoking && clients.Count == 0) requestsCancellation.Dispose();
            }
        }

        internal void Revoke()
        {
            TcpClient[] active;
            lock (gate)
            {
                if (revoked) return;
                revoked = true;
                revoking = true;
                active = [.. clients];
            }
            // FFmpeg owns its nested HTTP reads; VLC cannot interrupt those reads.
            // Closing this session's sockets lets input teardown finish even if an
            // upstream request is still waiting on its bounded timeout.
            requestsCancellation.Cancel();
            foreach (var client in active) client.Dispose();
            lock (gate)
            {
                revoking = false;
                if (clients.Count == 0) requestsCancellation.Dispose();
            }
        }

        internal string Token { get; } = token;

        internal TwitchVodPlaylistSource Source { get; } = source;

        internal SessionStatistics Statistics
        {
            get
            {
                lock (gate)
                {
                    return new SessionStatistics(servedSegments, unrepairedSegments, removedTimestamps);
                }
            }
        }

        internal string? TakeInitialPlaylist() => Interlocked.Exchange(ref initialPlaylist, null);

        /// <summary>
        /// Segment URLs are indexes into this table, so only URIs that came out of a validated
        /// playlist are ever fetched and a growing playlist keeps the URLs it already handed out.
        /// </summary>
        internal int GetOrAddSegment(Uri segmentUri)
        {
            lock (gate)
            {
                if (segmentIndexes.TryGetValue(segmentUri.AbsoluteUri, out var existing))
                {
                    return existing;
                }

                if (segments.Count >= MaxSegmentsPerSession)
                {
                    throw new InvalidDataException("The playlist contains too many muted segments to repair.");
                }

                segments.Add(segmentUri);
                segmentIndexes[segmentUri.AbsoluteUri] = segments.Count - 1;
                return segments.Count - 1;
            }
        }

        internal bool TryGetSegment(int index, out Uri segmentUri)
        {
            lock (gate)
            {
                if (index >= 0 && index < segments.Count)
                {
                    segmentUri = segments[index];
                    return true;
                }
            }

            segmentUri = null!;
            return false;
        }

        /// <returns>
        /// <see langword="true"/> the first time a segment of this session needed no repair, so
        /// the caller reports that once instead of for every such segment.
        /// </returns>
        internal bool RecordServedSegment(int repairs)
        {
            lock (gate)
            {
                servedSegments++;
                removedTimestamps += repairs;
                if (repairs > 0)
                {
                    return false;
                }

                unrepairedSegments++;
                return unrepairedSegments == 1;
            }
        }
    }
}
