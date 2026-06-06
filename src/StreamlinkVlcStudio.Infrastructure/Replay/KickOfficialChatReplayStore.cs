using System.Globalization;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Replay;

public sealed class KickOfficialChatReplayStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string rootDirectory;

    public KickOfficialChatReplayStore()
        : this(GetDefaultDirectory())
    {
    }

    public KickOfficialChatReplayStore(string rootDirectory)
    {
        this.rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? GetDefaultDirectory()
            : rootDirectory;
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

        var filePath = GetMessageFilePath(message.Channel, message.Timestamp);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await using var stream = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            4096,
            useAsync: true);
        var bytes = Encoding.UTF8.GetBytes(json + Environment.NewLine);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
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

    private async Task ReadFileMessagesAsync(
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
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

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
        while (day <= throughDay)
        {
            yield return GetMessageFilePath(channel, new DateTimeOffset(day, TimeSpan.Zero));
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
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}

public sealed record KickOfficialReplayChatReadResult(
    IReadOnlyList<ChatMessage> Messages,
    int CacheFileCount);
