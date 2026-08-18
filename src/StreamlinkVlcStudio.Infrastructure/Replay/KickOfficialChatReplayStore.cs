using System.Globalization;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Text;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

public sealed class KickOfficialChatReplayStore
{
    private const int MaximumRecordBytes = 1024 * 1024;
    private const long DefaultMaximumCacheBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan DefaultPruneInterval = TimeSpan.FromMinutes(15);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string rootDirectory;
    private readonly SemaphoreSlim appendGate = new(1, 1);
    private readonly IAppLogger? logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan retention;
    private readonly TimeSpan pruneInterval;
    private readonly long maximumCacheBytes;
    private DateTimeOffset lastPruneUtc = DateTimeOffset.MinValue;

    public KickOfficialChatReplayStore()
        : this(GetDefaultDirectory(), logger: null)
    {
    }

    public KickOfficialChatReplayStore(string rootDirectory)
        : this(rootDirectory, logger: null)
    {
    }

    public KickOfficialChatReplayStore(IAppLogger logger)
        : this(GetDefaultDirectory(), logger)
    {
    }

    public KickOfficialChatReplayStore(string rootDirectory, IAppLogger? logger)
        : this(
            rootDirectory,
            logger,
            TimeProvider.System,
            DefaultRetention,
            DefaultMaximumCacheBytes,
            DefaultPruneInterval)
    {
    }

    internal KickOfficialChatReplayStore(
        string rootDirectory,
        IAppLogger? logger,
        TimeProvider timeProvider,
        TimeSpan retention,
        long maximumCacheBytes,
        TimeSpan pruneInterval)
    {
        this.rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? GetDefaultDirectory()
            : Path.GetFullPath(rootDirectory);
        this.logger = logger;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.retention = retention > TimeSpan.Zero ? retention : DefaultRetention;
        this.maximumCacheBytes = maximumCacheBytes > 0 ? maximumCacheBytes : DefaultMaximumCacheBytes;
        this.pruneInterval = pruneInterval >= TimeSpan.Zero ? pruneInterval : DefaultPruneInterval;
        TryPrune(activeFilePath: null, force: true);
    }

    public static string GetDefaultDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamlinkVlcStudio",
            "replay-chat",
            "kick-official");
    }

    public async Task AppendAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Platform != PlatformKind.Kick ||
            string.IsNullOrWhiteSpace(message.Channel) ||
            string.IsNullOrWhiteSpace(message.Message))
        {
            return;
        }

        await appendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = GetMessageFilePath(message.Channel, message.Timestamp);
            EnsurePathHasNoReparsePoints(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            EnsurePathHasNoReparsePoints(filePath);
            TryPrune(filePath, force: false);
            var json = JsonSerializer.Serialize(message, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            if (bytes.Length > MaximumRecordBytes)
            {
                throw new InvalidOperationException($"Kick replay chat record exceeds {MaximumRecordBytes} bytes.");
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                4096,
                useAsync: true);
            await RepairIncompleteTailAsync(stream).ConfigureAwait(false);
            stream.Position = stream.Length;
            // After a record begins mutating the file it is deliberately non-cancellable. A
            // canceled caller therefore produces either no record or one complete JSONL record.
            await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            appendGate.Release();
        }
    }

    internal KickReplayCachePruneResult PruneForTest(string? activeFilePath = null) =>
        TryPrune(activeFilePath, force: true);

    private KickReplayCachePruneResult TryPrune(string? activeFilePath, bool force)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        if (!force && now - lastPruneUtc < pruneInterval)
        {
            return KickReplayCachePruneResult.NotRun;
        }

        lastPruneUtc = now;
        try
        {
            return PruneCore(activeFilePath, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SafeLog(AppLogLevel.Warning, "Kick replay chat cache retention failed.", ex);
            return new KickReplayCachePruneResult(true, 0, 0, 0, false);
        }
    }

    private KickReplayCachePruneResult PruneCore(string? activeFilePath, DateTimeOffset now)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return new KickReplayCachePruneResult(true, 0, 0, 0, false);
        }

        if (IsReparsePoint(rootDirectory))
        {
            SafeLog(AppLogLevel.Warning, "Kick replay chat cache root is a reparse point; retention was skipped.");
            return new KickReplayCachePruneResult(true, 0, 0, 0, false);
        }

        var normalizedActivePath = string.IsNullOrWhiteSpace(activeFilePath)
            ? null
            : Path.GetFullPath(activeFilePath);
        var currentDayName = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl";
        var files = EnumerateRetentionFiles(normalizedActivePath, currentDayName).ToList();
        var cutoff = now - retention;
        var deletedFiles = 0;
        long deletedBytes = 0;

        foreach (var file in files
                     .Where(file => !file.Protected && file.LastWriteTimeUtc < cutoff)
                     .OrderBy(file => file.LastWriteTimeUtc))
        {
            if (TryDeleteRetentionFile(file))
            {
                file.Deleted = true;
                deletedFiles++;
                deletedBytes += file.Length;
            }
        }

        long retainedBytes = files.Where(file => !file.Deleted).Sum(file => file.Length);
        foreach (var file in files
                     .Where(file => !file.Deleted && !file.Protected)
                     .OrderBy(file => file.LastWriteTimeUtc)
                     .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (retainedBytes <= maximumCacheBytes)
            {
                break;
            }

            if (TryDeleteRetentionFile(file))
            {
                file.Deleted = true;
                retainedBytes -= file.Length;
                deletedFiles++;
                deletedBytes += file.Length;
            }
        }

        var protectedBytes = files
            .Where(file => !file.Deleted && file.Protected)
            .Sum(file => file.Length);
        var protectedExceedsLimit = retainedBytes > maximumCacheBytes && protectedBytes > maximumCacheBytes;
        if (retainedBytes > maximumCacheBytes)
        {
            SafeLog(
                AppLogLevel.Warning,
                $"Protected Kick replay chat cache data uses {protectedBytes:N0} bytes and prevents the {maximumCacheBytes:N0}-byte retention limit from being met.");
        }

        return new KickReplayCachePruneResult(
            true,
            deletedFiles,
            deletedBytes,
            retainedBytes,
            protectedExceedsLimit);
    }

    private IEnumerable<RetentionFile> EnumerateRetentionFiles(
        string? activeFilePath,
        string currentDayName)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SafeLog(AppLogLevel.Warning, $"Could not inspect Kick replay cache directory '{directory}'.", ex);
                continue;
            }

            foreach (var child in children)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SafeLog(AppLogLevel.Warning, $"Could not inspect Kick replay cache entry '{child}'.", ex);
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    SafeLog(AppLogLevel.Warning, $"Ignoring reparse point in Kick replay chat cache: '{child}'.");
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                    continue;
                }

                if (!child.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(child);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    SafeLog(AppLogLevel.Warning, $"Could not inspect Kick replay cache file '{child}'.", ex);
                    continue;
                }

                var fullPath = info.FullName;
                var isProtected = string.Equals(fullPath, activeFilePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(info.Name, currentDayName, StringComparison.OrdinalIgnoreCase);
                yield return new RetentionFile(fullPath, info.Length, info.LastWriteTimeUtc, isProtected);
            }
        }
    }

    private static bool TryDeleteRetentionFile(RetentionFile file)
    {
        try
        {
            if (IsReparsePoint(file.Path))
            {
                return false;
            }

            File.Delete(file.Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private void EnsurePathHasNoReparsePoints(string filePath)
    {
        if (Directory.Exists(rootDirectory) && IsReparsePoint(rootDirectory))
        {
            throw new IOException("Kick replay chat cache root cannot be a reparse point.");
        }

        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrWhiteSpace(directory) &&
               directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(directory) && IsReparsePoint(directory))
            {
                throw new IOException($"Kick replay chat cache path cannot contain a reparse point: '{directory}'.");
            }

            if (string.Equals(
                    directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        if (File.Exists(filePath) && IsReparsePoint(filePath))
        {
            throw new IOException($"Kick replay chat cache file cannot be a reparse point: '{filePath}'.");
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private void SafeLog(AppLogLevel level, string message, Exception? exception = null)
    {
        try
        {
            logger?.Write(level, "KickReplayChat", message, exception);
        }
        catch (Exception)
        {
        }
    }

    private sealed class RetentionFile(
        string path,
        long length,
        DateTime lastWriteTimeUtc,
        bool isProtected)
    {
        internal string Path { get; } = path;
        internal long Length { get; } = Math.Max(0, length);
        internal DateTimeOffset LastWriteTimeUtc { get; } = new(lastWriteTimeUtc, TimeSpan.Zero);
        internal bool Protected { get; } = isProtected;
        internal bool Deleted { get; set; }
    }

    private static async Task RepairIncompleteTailAsync(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return;
        }

        var singleByte = new byte[1];
        stream.Position = stream.Length - 1;
        if (await stream.ReadAsync(singleByte, CancellationToken.None).ConfigureAwait(false) == 1 &&
            singleByte[0] == (byte)'\n')
        {
            return;
        }

        var tailStart = await FindTailStartAsync(stream).ConfigureAwait(false);
        var tailLength = stream.Length - tailStart;
        if (tailLength <= 0 || tailLength > MaximumRecordBytes)
        {
            stream.SetLength(tailStart);
            return;
        }

        var tail = new byte[checked((int)tailLength)];
        stream.Position = tailStart;
        await stream.ReadExactlyAsync(tail, CancellationToken.None).ConfigureAwait(false);
        try
        {
            _ = StrictUtf8.GetString(tail);
            using var document = JsonDocument.Parse(tail);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                stream.SetLength(tailStart);
                return;
            }

            stream.Position = stream.Length;
            await stream.WriteAsync(new byte[] { (byte)'\n' }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            stream.SetLength(tailStart);
        }
    }

    private static async Task<long> FindTailStartAsync(FileStream stream)
    {
        var buffer = new byte[4096];
        var searchEnd = stream.Length;
        while (searchEnd > 0)
        {
            var count = (int)Math.Min(buffer.Length, searchEnd);
            var start = searchEnd - count;
            stream.Position = start;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), CancellationToken.None).ConfigureAwait(false);
            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return start + index + 1;
                }
            }

            searchEnd = start;
        }

        return 0;
    }

    public async Task<KickOfficialReplayChatReadResult> ReadMessagesAsync(
        string channel,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return new KickOfficialReplayChatReadResult([], 0);
        }

        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        if (throughTimestampUtc < fromTimestampUtc)
        {
            throughTimestampUtc = fromTimestampUtc;
        }

        var messages = new List<ChatMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cacheFileCount = 0;
        foreach (var filePath in EnumerateCandidateFiles(channel, fromTimestampUtc, throughTimestampUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePathHasNoReparsePoints(filePath);
            if (!File.Exists(filePath))
            {
                continue;
            }

            cacheFileCount++;
            await ReadFileMessagesAsync(filePath, channel, fromTimestampUtc, throughTimestampUtc, messages, seen, cancellationToken)
                .ConfigureAwait(false);
        }

        return new KickOfficialReplayChatReadResult(
            messages
                .OrderBy(message => message.Timestamp)
                .ThenBy(message => message.MessageId, StringComparer.Ordinal)
                .ToArray(),
            cacheFileCount);
    }

    private static async Task ReadFileMessagesAsync(
        string filePath,
        string channel,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc,
        List<ChatMessage> messages,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            useAsync: true);
        using var reader = new BoundedStreamLineReader(
            stream,
            StrictUtf8,
            MaximumRecordBytes);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BoundedTextLine? record;
            try
            {
                record = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            if (record is null)
            {
                break;
            }

            if (record.Value.WasTruncated)
            {
                continue;
            }

            var line = record.Value.Text;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ChatMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ChatMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (message is null ||
                message.Platform != PlatformKind.Kick ||
                !string.Equals(message.Channel, channel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var timestampUtc = message.Timestamp.ToUniversalTime();
            if (timestampUtc < fromTimestampUtc || timestampUtc > throughTimestampUtc)
            {
                continue;
            }

            var key = string.IsNullOrWhiteSpace(message.MessageId)
                ? $"{timestampUtc.UtcTicks}:{message.Username}:{message.Message}"
                : message.MessageId;
            if (seen.Add(key))
            {
                messages.Add(message);
            }
        }
    }

    private IEnumerable<string> EnumerateCandidateFiles(
        string channel,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc)
    {
        var day = fromTimestampUtc.Date;
        var throughDay = throughTimestampUtc.Date;
        while (true)
        {
            yield return GetMessageFilePath(channel, new DateTimeOffset(day, TimeSpan.Zero));
            if (day == throughDay)
            {
                yield break;
            }

            day = day.AddDays(1);
        }
    }

    private string GetMessageFilePath(string channel, DateTimeOffset timestampUtc)
    {
        return Path.Combine(
            rootDirectory,
            SanitizePathPart(channel.Trim().ToLowerInvariant()),
            timestampUtc.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
    }

    private static string SanitizePathPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        if (builder.Length == 0)
        {
            return "unknown";
        }

        var result = builder.ToString();
        return IsReservedWindowsFileName(result) ? $"_{result}" : result;
    }

    private static bool IsReservedWindowsFileName(string value)
    {
        return value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            IsNumberedWindowsDeviceName(value, "COM") ||
            IsNumberedWindowsDeviceName(value, "LPT");
    }

    private static bool IsNumberedWindowsDeviceName(string value, string prefix)
    {
        return value.Length == 4 &&
            value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            value[3] is >= '1' and <= '9';
    }
}

public sealed record KickOfficialReplayChatReadResult(
    IReadOnlyList<ChatMessage> Messages,
    int CacheFileCount);

internal readonly record struct KickReplayCachePruneResult(
    bool Ran,
    int DeletedFileCount,
    long DeletedBytes,
    long RetainedBytes,
    bool ProtectedDataExceedsLimit)
{
    internal static KickReplayCachePruneResult NotRun { get; } = new(false, 0, 0, 0, false);
}
