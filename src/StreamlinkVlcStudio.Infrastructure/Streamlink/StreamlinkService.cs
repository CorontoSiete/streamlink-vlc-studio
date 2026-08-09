using System.Diagnostics;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using static StreamlinkVlcStudio.Infrastructure.Processes.ProcessExtensions;

namespace StreamlinkVlcStudio.Infrastructure.Streamlink;

public sealed partial class StreamlinkService : IStreamlinkService
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
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

        var psi = new ProcessStartInfo
        {
            FileName = request.StreamlinkPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildProbeArguments(request))
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = psi };
        logger.Write(AppLogLevel.Info, "Streamlink", $"Probing {request.Target.Url} ({request.Quality})");

        if (!process.Start())
        {
            return new StreamlinkProbeResult(false, "Streamlink process could not be started.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

        string output;
        string error;

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            output = await outputTask.ConfigureAwait(false);
            error = await errorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            await ObserveOutputReadsAsync(outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new StreamlinkProbeResult(false, "Timed out while checking this platform.");
        }

        var message = BuildProbeMessage(output, error);

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
        {
            return new StreamlinkProbeResult(true, "Playable stream found.");
        }

        return new StreamlinkProbeResult(false, string.IsNullOrWhiteSpace(message) ? $"Streamlink exited with code {process.ExitCode}." : message);
    }

    public async Task<StreamlinkResolvedUrl> ResolveStreamUrlAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.StreamlinkPath))
        {
            throw new FileNotFoundException("Streamlink executable was not found.", request.StreamlinkPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = request.StreamlinkPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildStreamUrlArguments(request))
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = psi };
        logger.Write(AppLogLevel.Info, "Streamlink", $"Resolving direct stream URL for {request.Target.Url} ({request.Quality})");

        if (!process.Start())
        {
            throw new InvalidOperationException("Streamlink process could not be started.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartupTimeout);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);

        string output;
        string error;

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            output = await outputTask.ConfigureAwait(false);
            error = await errorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            await ObserveOutputReadsAsync(outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new TimeoutException("Timed out while resolving the direct Streamlink URL.");
        }

        if (process.ExitCode == 0 && TryReadFirstAbsoluteUri(output, out var streamUri))
        {
            return new StreamlinkResolvedUrl(streamUri, "Resolved direct Streamlink URL.");
        }

        var message = BuildProbeMessage(output, error);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"Streamlink exited with code {process.ExitCode} while resolving the direct stream URL."
            : message);
    }

    public async Task<IStreamTransportSession> StartExternalHttpAsync(StreamTransportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.StreamlinkPath))
        {
            throw new FileNotFoundException("Streamlink executable was not found.", request.StreamlinkPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = request.StreamlinkPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildArguments(request))
        {
            psi.ArgumentList.Add(argument);
        }

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

            var match = LocalHttpUrlPattern().Match(data);
            if (match.Success && Uri.TryCreate(match.Value.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal), UriKind.Absolute, out var uri))
            {
                session.SetPlaybackUri(uri);
                uriCompletion.TrySetResult(uri);
            }
        }

        process.OutputDataReceived += (_, args) => HandleLine(args.Data);
        process.ErrorDataReceived += (_, args) => HandleLine(args.Data);
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

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(StartupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        var delayTask = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        var completed = await Task.WhenAny(uriCompletion.Task, exitCompletion.Task, delayTask).ConfigureAwait(false);
        var exitedBeforeReady = completed == exitCompletion.Task || exitCompletion.Task.IsCompleted || process.HasExited;
        if (completed == uriCompletion.Task && !exitedBeforeReady)
        {
            logger.Write(AppLogLevel.Info, "Streamlink", $"Streamlink HTTP transport ready at {session.PlaybackUri}");
            return session;
        }

        var recent = string.Join(Environment.NewLine, session.RecentLogLines.TakeLast(12));
        await session.DisposeAsync().ConfigureAwait(false);

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
        yield return "32M";

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
            if (Uri.TryCreate(line, UriKind.Absolute, out uri!))
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

    [GeneratedRegex(@"http://(?:(?:127\.0\.0\.1)|(?:0\.0\.0\.0)|localhost):\d+/", RegexOptions.CultureInvariant)]
    private static partial Regex LocalHttpUrlPattern();
}
