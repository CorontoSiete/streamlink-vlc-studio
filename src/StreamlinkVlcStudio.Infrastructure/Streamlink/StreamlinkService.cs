using System.Diagnostics;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Text;
using static StreamlinkVlcStudio.Infrastructure.Processes.ProcessExtensions;

namespace StreamlinkVlcStudio.Infrastructure.Streamlink;

public sealed partial class StreamlinkService : IStreamlinkService
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    // This is a per-Streamlink-process buffer. Multi-stream tiles use a smaller
    // buffer so a 16-tile grid does not reserve hundreds of megabytes before
    // libVLC starts decoding. It does not change the requested Streamlink quality
    // or the selected media variant.
    private const string DefaultExternalHttpRingBufferSize = "32M";
    private const string MultiStreamExternalHttpRingBufferSize = "16M";
    private readonly IAppLogger logger;

    public StreamlinkService(IAppLogger logger)
    {
        this.logger = logger;
    }

    public async Task<StreamlinkProbeResult> ProbeStreamsAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.StreamlinkPath))
        {
            return new StreamlinkProbeResult(false, "Streamlink executable was not found.");
        }

        var psi = CreateRedirectedStartInfo(request.StreamlinkPath, BuildProbeArguments(request));
        logger.Write(AppLogLevel.Info, "Streamlink", $"Probing {request.Target.Url} ({request.Quality})");

        var result = await RunRedirectedProcessAsync(psi, ProbeTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return new StreamlinkProbeResult(false, "Timed out while checking this platform.");
        }

        if (result.OutputWasTruncated)
        {
            return new StreamlinkProbeResult(false, "Streamlink returned more diagnostic output than the safety limit allows.");
        }

        var message = BuildProbeMessage(result.StandardOutput, result.StandardError);
        if (result.ExitCode == 0 && TryReadFirstAbsoluteUri(result.StandardOutput, out _))
        {
            return new StreamlinkProbeResult(true, "Playable stream found.");
        }

        return new StreamlinkProbeResult(
            false,
            string.IsNullOrWhiteSpace(message) ? $"Streamlink exited with code {result.ExitCode}." : message);
    }

    public async Task<StreamlinkResolvedUrl> ResolveStreamUrlAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.StreamlinkPath))
        {
            throw new FileNotFoundException("Streamlink executable was not found.", request.StreamlinkPath);
        }

        var psi = CreateRedirectedStartInfo(request.StreamlinkPath, BuildStreamUrlArguments(request));
        logger.Write(AppLogLevel.Info, "Streamlink", $"Resolving direct stream URL for {request.Target.Url} ({request.Quality})");

        var result = await RunRedirectedProcessAsync(psi, StartupTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new TimeoutException("Timed out while resolving the direct Streamlink URL.");
        }

        if (result.OutputWasTruncated)
        {
            throw new InvalidDataException("Streamlink returned more output than the safety limit allows.");
        }

        if (result.ExitCode == 0 && TryReadFirstAbsoluteUri(result.StandardOutput, out var streamUri))
        {
            return new StreamlinkResolvedUrl(streamUri, "Resolved direct Streamlink URL.");
        }

        var message = BuildProbeMessage(result.StandardOutput, result.StandardError);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"Streamlink exited with code {result.ExitCode} while resolving the direct stream URL."
            : message);
    }

    public async Task<IStreamTransportSession> StartExternalHttpAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.StreamlinkPath))
        {
            throw new FileNotFoundException("Streamlink executable was not found.", request.StreamlinkPath);
        }

        var psi = CreateRedirectedStartInfo(request.StreamlinkPath, BuildArguments(request));

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var session = new StreamlinkExternalHttpSession(process, logger);
        var uriCompletion = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleLine(string? data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }

            session.AddLogLine(data);
            logger.Write(AppLogLevel.Info, "Streamlink", data);

            if (TryReadLocalHttpUri(data, out var uri))
            {
                session.SetPlaybackUri(uri);
                uriCompletion.TrySetResult(uri);
            }
        }

        process.Exited += (_, _) => exitCompletion.TrySetResult();

        logger.Write(AppLogLevel.Info, "Streamlink", $"Starting Streamlink for {request.Target.Url} ({request.Quality})");
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Streamlink process could not be started.");
            }
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var outputPump = PumpOutputAsync(
            process.StandardOutput.BaseStream,
            process.StandardOutput.CurrentEncoding,
            HandleLine);
        var errorPump = PumpOutputAsync(
            process.StandardError.BaseStream,
            process.StandardError.CurrentEncoding,
            HandleLine);
        session.AttachOutputPumps(outputPump, errorPump);

        var sessionTransferred = false;
        try
        {
            using var timeout = new CancellationTokenSource(StartupTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

            var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            var completed = await Task.WhenAny(uriCompletion.Task, exitCompletion.Task, delayTask).ConfigureAwait(false);
            // The timeout task is only a race sentinel. Cancel it as soon as one of the real
            // completion paths wins so a successful session does not leave a pending task behind.
            linked.Cancel();
            var exitedBeforeReady = completed == exitCompletion.Task || exitCompletion.Task.IsCompleted || process.HasExited;
            if (completed == uriCompletion.Task && !exitedBeforeReady)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.Write(AppLogLevel.Info, "Streamlink", $"Streamlink HTTP transport ready at {session.PlaybackUri}");
                sessionTransferred = true;
                return session;
            }

            var recent = string.Join(Environment.NewLine, session.RecentLogLines.TakeLast(12));
            if (exitedBeforeReady)
            {
                throw new InvalidOperationException($"Streamlink exited before providing an HTTP transport URL. Recent output:{Environment.NewLine}{recent}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new TimeoutException($"Timed out waiting for Streamlink HTTP transport. Recent output:{Environment.NewLine}{recent}");
        }
        finally
        {
            if (!sessionTransferred)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task PumpOutputAsync(
        Stream stream,
        System.Text.Encoding encoding,
        Action<string?> handleLine)
    {
        try
        {
            using var reader = new BoundedStreamLineReader(
                stream,
                encoding,
                PayloadLimits.ProcessLineBytes);
            while (true)
            {
                BoundedTextLine? line;
                try
                {
                    line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (System.Text.DecoderFallbackException)
                {
                    logger.Write(AppLogLevel.Warning, "Streamlink", "Streamlink emitted a line with invalid text encoding.");
                    continue;
                }

                if (line is null)
                {
                    return;
                }

                handleLine(line.Value.WasTruncated
                    ? $"{line.Value.Text} ...[line truncated]"
                    : line.Value.Text);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logger.Write(AppLogLevel.Warning, "Streamlink", "A Streamlink output pipe closed unexpectedly.", ex);
        }
    }

    private static IEnumerable<string> BuildArguments(StreamTransportRequest request)
    {
        yield return "--loglevel";
        yield return "info";
        yield return "--webbrowser";
        yield return "no";
        yield return "--player-external-http";
        yield return "--player-external-http-interface";
        yield return "127.0.0.1";
        yield return "--player-external-http-port";
        yield return "0";
        yield return "--player-external-http-continuous";
        yield return "yes";
        yield return "--retry-open";
        yield return "3";
        yield return "--stream-types";
        yield return "hls";
        yield return "--ringbuffer-size";
        yield return request.IsMultiStream
            ? MultiStreamExternalHttpRingBufferSize
            : DefaultExternalHttpRingBufferSize;

        if (request.LowLatency && request.Target.Platform == PlatformKind.Twitch)
        {
            // Twitch low-latency is tuned end-to-end; ride close to the live edge.
            yield return "--hls-live-edge";
            yield return "2";
            yield return "--hls-segment-stream-data";
            yield return "--stream-segment-threads";
            yield return "2";
            yield return "--twitch-low-latency";
            yield return "--twitch-supported-codecs";
            yield return "h264";
        }
        else if (request.LowLatency)
        {
            // Non-Twitch (Kick/Amazon IVS) has no tuned low-latency path: riding 2 segments
            // from the live edge with partial-segment streaming constantly rebuffers. Keep a
            // larger buffer and download full segments with extra throughput instead.
            yield return "--hls-live-edge";
            yield return "4";
            yield return "--stream-segment-threads";
            yield return "2";
        }

        foreach (var argument in request.CustomArguments)
        {
            yield return argument;
        }

        yield return request.Target.Url;
        yield return request.Quality;
    }

    private static IEnumerable<string> BuildProbeArguments(StreamTransportRequest request)
    {
        foreach (var argument in BuildStreamUrlBaseArguments(request))
        {
            yield return argument;
        }

        yield return "--retry-streams";
        yield return "1";
        yield return request.Target.Url;
        yield return request.Quality;
    }

    private static IEnumerable<string> BuildStreamUrlArguments(StreamTransportRequest request)
    {
        foreach (var argument in BuildStreamUrlBaseArguments(request))
        {
            yield return argument;
        }

        yield return request.Target.Url;
        yield return request.Quality;
    }

    private static IEnumerable<string> BuildStreamUrlBaseArguments(StreamTransportRequest request)
    {
        yield return "--loglevel";
        yield return "info";
        yield return "--webbrowser";
        yield return "no";
        yield return "--stream-url";
        yield return "--retry-open";
        yield return "1";
        yield return "--stream-types";
        yield return "hls";

        foreach (var argument in request.CustomArguments)
        {
            yield return argument;
        }
    }

    private static bool TryReadFirstAbsoluteUri(string output, out Uri uri)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(line, UriKind.Absolute, out uri!) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return true;
            }
        }

        uri = null!;
        return false;
    }

    private static string BuildProbeMessage(string output, string error)
    {
        var combined = string.Join(
            Environment.NewLine,
            new[] { error, output }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();

        if (combined.Length <= 360)
        {
            return combined;
        }

        return combined[..360].TrimEnd() + "...";
    }

    private static bool TryReadLocalHttpUri(string output, out Uri uri)
    {
        var match = LocalHttpUrlPattern().Match(output);
        if (!match.Success ||
            !Uri.TryCreate(
                match.Value.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal),
                UriKind.Absolute,
                out uri!))
        {
            uri = null!;
            return false;
        }

        return true;
    }

    [GeneratedRegex(@"http://(?:127\.0\.0\.1|0\.0\.0\.0|localhost):\d+/?(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LocalHttpUrlPattern();
}
