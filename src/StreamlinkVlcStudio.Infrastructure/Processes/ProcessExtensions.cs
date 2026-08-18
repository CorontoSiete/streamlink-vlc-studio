using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using StreamlinkVlcStudio.Infrastructure.Limits;

namespace StreamlinkVlcStudio.Infrastructure.Processes;

/// <summary>
/// Shared <see cref="Process"/> helpers. Consolidates the process-tree termination logic that was
/// previously duplicated across the streamlink, replay, and viewer service integrations.
/// </summary>
internal static class ProcessExtensions
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates the common redirected, non-shell process configuration used by the small command
    /// line integrations in the infrastructure layer.
    /// </summary>
    internal static ProcessStartInfo CreateRedirectedStartInfo(
        string fileName,
        IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    /// <summary>
    /// Runs a redirected process, drains both output streams, and terminates the full child tree
    /// when the operation is canceled or exceeds its timeout. A timeout is returned as data so
    /// callers can provide a domain-specific diagnostic; caller cancellation is rethrown.
    /// </summary>
    internal static async Task<ProcessExecutionResult> RunRedirectedProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Process '{startInfo.FileName}' could not be started.");
        }

        var standardOutputEncoding = process.StandardOutput.CurrentEncoding;
        var standardErrorEncoding = process.StandardError.CurrentEncoding;
        var standardOutputCollector = new BoundedProcessOutputCollector(PayloadLimits.ProcessOutputBytes);
        var standardErrorCollector = new BoundedProcessOutputCollector(PayloadLimits.ProcessOutputBytes);
        var standardOutputTask = ReadOutputAsync(process.StandardOutput.BaseStream, standardOutputCollector);
        var standardErrorTask = ReadOutputAsync(process.StandardError.BaseStream, standardErrorCollector);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var outputDrained = await DrainOutputAfterExitAsync(
                process,
                standardOutputTask,
                standardErrorTask).ConfigureAwait(false);
            var standardOutput = standardOutputCollector.ToOutput(standardOutputEncoding, !outputDrained);
            var standardError = standardErrorCollector.ToOutput(standardErrorEncoding, !outputDrained);
            return new ProcessExecutionResult(
                process.ExitCode,
                standardOutput.Text,
                standardError.Text,
                TimedOut: false,
                standardOutput.Truncated,
                standardError.Truncated);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            await ObserveOutputReadsAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var standardOutput = standardOutputCollector.ToOutput(
                standardOutputEncoding,
                standardOutputTask.Status != TaskStatus.RanToCompletion);
            var standardError = standardErrorCollector.ToOutput(
                standardErrorEncoding,
                standardErrorTask.Status != TaskStatus.RanToCompletion);
            return new ProcessExecutionResult(
                -1,
                standardOutput.Text,
                standardError.Text,
                TimedOut: true,
                standardOutput.Truncated,
                standardError.Truncated);
        }
        catch
        {
            // A redirected pipe can fail independently of WaitForExitAsync. Ensure the child
            // cannot survive an output-read failure before propagating the original exception.
            await KillProcessTreeAsync(process).ConfigureAwait(false);
            await ObserveOutputReadsAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Kills the process and its entire child tree (when still running) and waits for exit, swallowing
    /// the benign races that occur when the process has already exited.
    /// </summary>
    internal static async Task KillProcessTreeAsync(Process process, TimeSpan? cleanupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        var timeoutDuration = cleanupTimeout ?? CleanupTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeoutDuration, TimeSpan.Zero);

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(timeoutDuration);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception or OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Observes redirected output reads after a process is terminated. Cancellation and stream
    /// teardown races are expected during forced process shutdown.
    /// </summary>
    internal static async Task ObserveOutputReadsAsync(Task standardOutputTask, Task standardErrorTask)
    {
        var readsTask = Task.WhenAll(standardOutputTask, standardErrorTask);
        try
        {
            await readsTask.WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = readsTask.ContinueWith(
                static completedTask => _ = completedTask.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task<bool> DrainOutputAfterExitAsync(
        Process process,
        Task standardOutputTask,
        Task standardErrorTask)
    {
        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask)
                .WaitAsync(CleanupTimeout)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // A descendant can inherit redirected handles and keep the pipes open after the
            // process we launched has exited. Close our readers so completion stays bounded.
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            await ObserveOutputReadsAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task ReadOutputAsync(
        Stream stream,
        BoundedProcessOutputCollector collector)
    {
        var buffer = new byte[81_920];
        while (true)
        {
            var bytesRead = await stream
                .ReadAsync(buffer.AsMemory(), CancellationToken.None)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return;
            }

            collector.Append(buffer.AsSpan(0, bytesRead));
        }
    }

}

internal readonly record struct BoundedProcessOutput(string Text, bool Truncated);

/// <summary>
/// Retains the beginning and end of a process stream while continuing to drain all bytes.
/// </summary>
internal sealed class BoundedProcessOutputCollector
{
    private readonly object gate = new();
    private readonly int maximumBytes;
    private readonly byte[] head;
    private readonly byte[] tail;
    private int headLength;
    private int tailLength;
    private int tailStart;
    private long totalBytes;

    internal BoundedProcessOutputCollector(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 1);
        this.maximumBytes = maximumBytes;
        head = new byte[maximumBytes / 2];
        tail = new byte[maximumBytes - head.Length];
    }

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            AppendCore(bytes);
        }
    }

    private void AppendCore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        totalBytes = totalBytes > long.MaxValue - bytes.Length
            ? long.MaxValue
            : totalBytes + bytes.Length;

        if (headLength < head.Length)
        {
            var headBytes = Math.Min(bytes.Length, head.Length - headLength);
            bytes[..headBytes].CopyTo(head.AsSpan(headLength));
            headLength += headBytes;
            bytes = bytes[headBytes..];
        }

        if (bytes.IsEmpty)
        {
            return;
        }

        if (bytes.Length >= tail.Length)
        {
            bytes[^tail.Length..].CopyTo(tail);
            tailLength = tail.Length;
            tailStart = 0;
            return;
        }

        if (tailLength < tail.Length)
        {
            var appendBytes = Math.Min(bytes.Length, tail.Length - tailLength);
            bytes[..appendBytes].CopyTo(tail.AsSpan(tailLength));
            tailLength += appendBytes;
            bytes = bytes[appendBytes..];
            if (bytes.IsEmpty)
            {
                return;
            }
        }

        var first = Math.Min(bytes.Length, tail.Length - tailStart);
        bytes[..first].CopyTo(tail.AsSpan(tailStart));
        bytes[first..].CopyTo(tail);
        tailStart = (tailStart + bytes.Length) % tail.Length;
    }

    internal BoundedProcessOutput ToOutput(Encoding encoding, bool forceTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        lock (gate)
        {
            return ToOutputCore(encoding, forceTruncated);
        }
    }

    private BoundedProcessOutput ToOutputCore(Encoding encoding, bool forceTruncated)
    {
        var captured = new byte[headLength + tailLength];
        head.AsSpan(0, headLength).CopyTo(captured);
        var destination = captured.AsSpan(headLength);
        if (tailLength < tail.Length || tailStart == 0)
        {
            tail.AsSpan(0, tailLength).CopyTo(destination);
        }
        else
        {
            var firstLength = tail.Length - tailStart;
            tail.AsSpan(tailStart, firstLength).CopyTo(destination);
            tail.AsSpan(0, tailStart).CopyTo(destination[firstLength..]);
        }

        return new BoundedProcessOutput(
            encoding.GetString(captured),
            forceTruncated || totalBytes > maximumBytes);
    }
}

/// <summary>Captured result from a bounded redirected process invocation.</summary>
public readonly record struct ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool StandardOutputTruncated = false,
    bool StandardErrorTruncated = false)
{
    public bool OutputWasTruncated => StandardOutputTruncated || StandardErrorTruncated;
}

/// <summary>
/// Public composition boundary for short-lived command-line integrations. The implementation is
/// deliberately backed by <see cref="ProcessExtensions"/> so Streamlink, curl, replay helpers,
/// overlay preparation, and UI capability probes share the same output-draining and process-tree
/// cleanup behavior.
/// </summary>
public sealed class BoundedProcessRunner
{
    public static ProcessStartInfo CreateRedirectedStartInfo(
        string fileName,
        IEnumerable<string> arguments) =>
        ProcessExtensions.CreateRedirectedStartInfo(fileName, arguments);

    public Task<ProcessExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ProcessExtensions.RunRedirectedProcessAsync(startInfo, timeout, cancellationToken);

}
