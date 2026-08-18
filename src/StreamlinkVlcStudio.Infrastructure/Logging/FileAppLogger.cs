using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.Infrastructure.Logging;

public sealed partial class FileAppLogger : IAppLogger, IDisposable, IAsyncDisposable
{
    private const int DefaultCapacity = 4096;
    private const long DefaultMaximumFileBytes = 10 * 1024 * 1024;
    private const int DefaultMaximumFileCount = 5;
    private const int MaximumSourceCharacters = 256;
    private const int MaximumMessageCharacters = 16 * 1024;
    private const int MaximumExceptionCharacters = 8 * 1024;
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding LogEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object shutdownGate = new();
    private readonly string logDirectory;
    private readonly string currentLogPath;
    private readonly Channel<LogWorkItem> channel;
    private readonly CancellationTokenSource writerCancellation = new();
    private readonly Task writerTask;
    private readonly long maximumFileBytes;
    private readonly int maximumFileCount;
    private readonly TimeSpan shutdownTimeout;
    private readonly Func<CancellationToken, Task>? beforeWriteAsync;
    private Task? shutdownTask;
    private int pendingEntryCount;
    private long droppedEntryCount;
    private int stopping;

    public FileAppLogger(string? baseDirectory = null)
        : this(
            baseDirectory,
            DefaultCapacity,
            DefaultMaximumFileBytes,
            DefaultMaximumFileCount,
            DefaultShutdownTimeout,
            beforeWriteAsync: null)
    {
    }

    internal FileAppLogger(
        string? baseDirectory,
        int capacity,
        long maximumFileBytes,
        int maximumFileCount,
        TimeSpan shutdownTimeout,
        Func<CancellationToken, Task>? beforeWriteAsync = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileCount, 1);
        if (shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        }

        var root = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamlinkVlcStudio");

        Directory.CreateDirectory(root);
        logDirectory = root;
        currentLogPath = Path.Combine(logDirectory, "studio.log");
        this.maximumFileBytes = maximumFileBytes;
        this.maximumFileCount = maximumFileCount;
        this.shutdownTimeout = shutdownTimeout;
        this.beforeWriteAsync = beforeWriteAsync;
        channel = Channel.CreateBounded<LogWorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        writerTask = Task.Run(WriteLoopAsync);
    }

    public event EventHandler<LogEntry>? EntryWritten;

    internal int PendingEntryCount => Volatile.Read(ref pendingEntryCount);
    internal long DroppedEntryCount => Interlocked.Read(ref droppedEntryCount);

    public void Write(AppLogLevel level, string source, string message, Exception? exception = null)
    {
        if (Volatile.Read(ref stopping) != 0)
        {
            return;
        }

        var sanitizedSource = Sanitize(source, MaximumSourceCharacters);
        var sanitizedMessage = Sanitize(message, MaximumMessageCharacters);
        var entry = new LogEntry(DateTimeOffset.Now, level, sanitizedSource, sanitizedMessage, exception);
        var line = $"{entry.Timestamp:O} [{entry.Level}] {entry.Source}: {entry.Message}";
        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {Sanitize(exception.Message, MaximumExceptionCharacters)}";
        }

        Interlocked.Increment(ref pendingEntryCount);
        if (!channel.Writer.TryWrite(new LogWorkItem(line, null)))
        {
            Interlocked.Decrement(ref pendingEntryCount);
            Interlocked.Increment(ref droppedEntryCount);
        }

        NotifySubscribers(entry);
    }

    public void Dispose()
    {
        ShutdownAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (writerTask.IsCompleted)
        {
            await writerTask.ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await channel.Writer
                .WriteAsync(new LogWorkItem(null, completion), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            await writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var completedTask = await Task.WhenAny(completion.Task, writerTask)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (ReferenceEquals(completedTask, writerTask))
        {
            await writerTask.ConfigureAwait(false);
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task ShutdownAsync()
    {
        lock (shutdownGate)
        {
            return shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        channel.Writer.TryComplete();
        try
        {
            await writerTask.WaitAsync(shutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            writerCancellation.Cancel();
            try
            {
                await writerTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
        catch (OperationCanceledException) when (writerCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (writerTask.IsCompleted)
            {
                writerCancellation.Dispose();
            }
            else
            {
                _ = writerTask.ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    writerCancellation,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        FileStream? stream = null;
        Exception? writerFailure = null;
        try
        {
            PruneLegacyLogs();
            await foreach (var item in channel.Reader.ReadAllAsync(writerCancellation.Token).ConfigureAwait(false))
            {
                if (item.FlushCompletion is not null)
                {
                    try
                    {
                        if (stream is not null)
                        {
                            await stream.FlushAsync(writerCancellation.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (stream is not null)
                        {
                            await DisposeStreamIgnoringErrorsAsync(stream).ConfigureAwait(false);
                            stream = null;
                        }
                    }

                    item.FlushCompletion.TrySetResult();
                    continue;
                }

                try
                {
                    if (beforeWriteAsync is not null)
                    {
                        await beforeWriteAsync(writerCancellation.Token).ConfigureAwait(false);
                    }

                    var bytes = LogEncoding.GetBytes(item.Line + Environment.NewLine);
                    stream = await EnsureWritableStreamAsync(stream, bytes.Length, writerCancellation.Token)
                        .ConfigureAwait(false);
                    await stream.WriteAsync(bytes, writerCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (stream is not null)
                    {
                        await DisposeStreamIgnoringErrorsAsync(stream).ConfigureAwait(false);
                        stream = null;
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref pendingEntryCount);
                }

            }

            if (stream is not null)
            {
                try
                {
                    await stream.FlushAsync(writerCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (writerCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            writerFailure = ex;
            throw;
        }
        finally
        {
            while (channel.Reader.TryRead(out var abandoned))
            {
                if (abandoned.Line is not null)
                {
                    Interlocked.Decrement(ref pendingEntryCount);
                }

                if (abandoned.FlushCompletion is not { } flushCompletion)
                {
                    continue;
                }

                if (writerFailure is not null)
                {
                    flushCompletion.TrySetException(writerFailure);
                }
                else if (writerCancellation.IsCancellationRequested)
                {
                    flushCompletion.TrySetCanceled(writerCancellation.Token);
                }
                else
                {
                    flushCompletion.TrySetException(
                        new IOException("The log writer stopped before the flush completed."));
                }
            }

            if (stream is not null)
            {
                await DisposeStreamIgnoringErrorsAsync(stream).ConfigureAwait(false);
            }
        }
    }

    private async Task<FileStream> EnsureWritableStreamAsync(
        FileStream? stream,
        int incomingBytes,
        CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            stream = OpenCurrentLog();
        }

        if (stream.Length > 0 && stream.Length + incomingBytes > maximumFileBytes)
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            RotateLogs();
            stream = OpenCurrentLog();
        }

        return stream;
    }

    private FileStream OpenCurrentLog() => new(
        currentLogPath,
        new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

    private void RotateLogs()
    {
        if (maximumFileCount == 1)
        {
            File.Delete(currentLogPath);
            return;
        }

        var oldestPath = GetRotatedLogPath(maximumFileCount - 1);
        File.Delete(oldestPath);
        for (var index = maximumFileCount - 2; index >= 1; index--)
        {
            var sourcePath = GetRotatedLogPath(index);
            if (File.Exists(sourcePath))
            {
                File.Move(sourcePath, GetRotatedLogPath(index + 1), overwrite: true);
            }
        }

        if (File.Exists(currentLogPath))
        {
            File.Move(currentLogPath, GetRotatedLogPath(1), overwrite: true);
        }
    }

    private string GetRotatedLogPath(int index) => Path.Combine(logDirectory, $"studio.{index}.log");

    private void PruneLegacyLogs()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         logDirectory,
                         "studio-*.log",
                         SearchOption.TopDirectoryOnly))
            {
                File.Delete(path);
            }

            foreach (var path in Directory.EnumerateFiles(
                         logDirectory,
                         "studio.*.log",
                         SearchOption.TopDirectoryOnly))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var suffix = stem.AsSpan("studio.".Length);
                if (int.TryParse(suffix, out var index) && index >= maximumFileCount)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void NotifySubscribers(LogEntry entry)
    {
        var handlers = EntryWritten;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<LogEntry> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, entry);
            }
            catch (Exception)
            {
                // A diagnostic subscriber must not make logging fail or block other subscribers.
            }
        }
    }

    private static string Sanitize(string? value, int maximumCharacters)
    {
        var normalized = (value ?? "")
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        normalized = UrlUserInfoPattern().Replace(normalized, "$1[REDACTED]@");
        normalized = SensitiveQueryPattern().Replace(normalized, "$1[REDACTED]");
        normalized = BearerTokenPattern().Replace(normalized, "$1 [REDACTED]");
        normalized = OAuthPrefixPattern().Replace(normalized, "oauth:[REDACTED]");
        normalized = SensitiveAssignmentPattern().Replace(normalized, "$1[REDACTED]");
        normalized = JwtPattern().Replace(normalized, "[REDACTED-JWT]");
        return normalized.Length <= maximumCharacters
            ? normalized
            : string.Concat(normalized.AsSpan(0, maximumCharacters), "...[truncated]");
    }

    [GeneratedRegex(@"(?i)(https?://)[^/@\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoPattern();

    [GeneratedRegex(@"(?i)([?&](?:access_token|refresh_token|client_secret|oauth_token|token|authorization|auth|code)=)[^&#\s\""']+", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryPattern();

    [GeneratedRegex(@"(?i)((?:\""|')?(?:access_token|refresh_token|client_secret|oauth_token|token|authorization|auth|code)(?:\""|')?\s*[:=]\s*(?:\""|')?)[^,\s&}\""']+", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentPattern();

    [GeneratedRegex(@"(?i)\b(Bearer|OAuth)\s+[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"(?i)\boauth:[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex OAuthPrefixPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    private static async ValueTask DisposeStreamIgnoringErrorsAsync(FileStream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record LogWorkItem(string? Line, TaskCompletionSource? FlushCompletion);
}
