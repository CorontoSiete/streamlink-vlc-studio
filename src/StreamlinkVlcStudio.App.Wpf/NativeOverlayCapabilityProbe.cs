using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using StreamlinkVlcStudio.Infrastructure.Processes;

namespace StreamlinkVlcStudio.App.Wpf;

/// <summary>Bounded, single-flight capability probing keyed by executable file identity.</summary>
internal sealed class NativeOverlayCapabilityProbe
{
    private const string FontSizeArgument = "--font-size";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransientFailureCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, ProbeCacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessExecutionResult>> runProcessAsync;
    private readonly TimeSpan timeout;
    private readonly TimeProvider timeProvider;

    public NativeOverlayCapabilityProbe(
        BoundedProcessRunner? processRunner = null,
        TimeSpan? timeout = null)
        : this(
            (startInfo, boundedTimeout, cancellationToken) =>
                (processRunner ?? new BoundedProcessRunner()).RunAsync(startInfo, boundedTimeout, cancellationToken),
            timeout)
    {
    }

    internal NativeOverlayCapabilityProbe(
        Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessExecutionResult>> runProcessAsync,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
    {
        this.runProcessAsync = runProcessAsync ?? throw new ArgumentNullException(nameof(runProcessAsync));
        this.timeout = timeout is { } value && value > TimeSpan.Zero
            ? value
            : DefaultTimeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> SupportsFontSizeAsync(
        string controllerPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerPath);
        cancellationToken.ThrowIfCancellationRequested();

        var key = BuildCacheKey(controllerPath);
        Task<bool> probeTask;
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(key, out var entry))
            {
                entry = new ProbeCacheEntry();
                Cache[key] = entry;
            }

            if (entry.DefinitiveResult is { } definitive)
            {
                return definitive;
            }

            if (entry.TransientFailureUntilUtc > timeProvider.GetUtcNow())
            {
                return false;
            }

            entry.InFlight ??= ProbeAndPublishAsync(key, entry, controllerPath);
            probeTask = entry.InFlight;
        }

        return await probeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void ClearCache()
    {
        lock (CacheGate)
        {
            Cache.Clear();
        }
    }

    private async Task<bool> ProbeAndPublishAsync(
        string key,
        ProbeCacheEntry expectedEntry,
        string controllerPath)
    {
        // Ensure the entry's InFlight field is assigned before even a synchronous test transport
        // can complete and publish its result.
        await Task.Yield();
        var outcome = await ProbeCoreAsync(controllerPath).ConfigureAwait(false);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var current) && ReferenceEquals(current, expectedEntry))
            {
                if (outcome.Definitive)
                {
                    current.DefinitiveResult = outcome.Supported;
                    current.TransientFailureUntilUtc = default;
                }
                else
                {
                    current.TransientFailureUntilUtc = timeProvider.GetUtcNow().Add(TransientFailureCacheDuration);
                }

                current.InFlight = null;
            }
        }

        return outcome.Supported;
    }

    private async Task<ProbeOutcome> ProbeCoreAsync(string controllerPath)
    {
        try
        {
            var startInfo = BoundedProcessRunner.CreateRedirectedStartInfo(
                controllerPath,
                ["--help"]);
            var result = await runProcessAsync(startInfo, timeout, CancellationToken.None)
                .ConfigureAwait(false);
            if (result.TimedOut || result.OutputWasTruncated || result.ExitCode != 0)
            {
                return ProbeOutcome.TransientFailure;
            }

            var supported = result.StandardOutput.Contains(FontSizeArgument, StringComparison.OrdinalIgnoreCase) ||
                result.StandardError.Contains(FontSizeArgument, StringComparison.OrdinalIgnoreCase);
            return new ProbeOutcome(supported, Definitive: true);
        }
        catch (Exception ex) when (ex is
            IOException or
            InvalidOperationException or
            UnauthorizedAccessException or
            Win32Exception or
            OperationCanceledException)
        {
            return ProbeOutcome.TransientFailure;
        }
    }

    private static string BuildCacheKey(string controllerPath)
    {
        try
        {
            var info = new FileInfo(controllerPath);
            return string.Join(
                '|',
                info.FullName,
                info.Length.ToString(CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Path.GetFullPath(controllerPath);
        }
    }

    private sealed class ProbeCacheEntry
    {
        internal bool? DefinitiveResult { get; set; }
        internal DateTimeOffset TransientFailureUntilUtc { get; set; }
        internal Task<bool>? InFlight { get; set; }
    }

    private readonly record struct ProbeOutcome(bool Supported, bool Definitive)
    {
        internal static ProbeOutcome TransientFailure { get; } = new(false, false);
    }
}
