using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class ReplayChatWindowSelector
{
    private const int CompactionHeadThreshold = 1024;
    private readonly object gate = new();
    private readonly List<ReplayChatMessage> messages = [];
    private readonly HashSet<string> messageKeys = new(StringComparer.Ordinal);
    private int headIndex;

    public int Count
    {
        get
        {
            lock (gate)
            {
                return LogicalCount;
            }
        }
    }

    public TimeSpan? FirstOffset
    {
        get
        {
            lock (gate)
            {
                return LogicalCount == 0 ? null : messages[headIndex].Offset;
            }
        }
    }

    public TimeSpan? LastOffset
    {
        get
        {
            lock (gate)
            {
                return LogicalCount == 0 ? null : messages[^1].Offset;
            }
        }
    }

    private int LogicalCount => messages.Count - headIndex;

    public void Clear()
    {
        lock (gate)
        {
            messages.Clear();
            messageKeys.Clear();
            headIndex = 0;
        }
    }

    public void Replace(IReadOnlyList<ReplayChatMessage> source)
    {
        lock (gate)
        {
            messages.Clear();
            messageKeys.Clear();
            headIndex = 0;
            AddRangeCore(source);
        }
    }

    public int AddRange(IReadOnlyList<ReplayChatMessage> source)
    {
        lock (gate)
        {
            return AddRangeCore(source);
        }
    }

    public bool Add(ReplayChatMessage message, int maxCount, out bool evicted)
    {
        lock (gate)
        {
            evicted = false;
            if (!AddCore(message))
            {
                return false;
            }

            while (LogicalCount > maxCount)
            {
                RemoveHeadCore();
                evicted = true;
            }

            CompactIfNeeded();
            return true;
        }
    }

    public IReadOnlyList<ReplayChatMessage> Snapshot()
    {
        lock (gate)
        {
            return LogicalCount == 0
                ? []
                : messages.Skip(headIndex).ToArray();
        }
    }

    public int CopyTo(ReplayChatWindowSelector target, bool replaceExisting)
    {
        if (ReferenceEquals(this, target))
        {
            return 0;
        }

        lock (gate)
        {
            return target.ReplaceOrAddOrderedCore(messages, headIndex, LogicalCount, replaceExisting);
        }
    }

    public ReplayChatWindowSelection SelectWindow(TimeSpan offset, TimeSpan window, int maxMessages)
    {
        lock (gate)
        {
            var start = offset - window;
            if (start < TimeSpan.Zero)
            {
                start = TimeSpan.Zero;
            }

            return SelectRangeCore(start, offset, maxMessages);
        }
    }

    public ReplayChatWindowSelection SelectRange(TimeSpan startOffset, TimeSpan endOffset, int maxMessages)
    {
        lock (gate)
        {
            if (startOffset < TimeSpan.Zero)
            {
                startOffset = TimeSpan.Zero;
            }

            return SelectRangeCore(startOffset, endOffset, maxMessages);
        }
    }

    private int AddRangeCore(IReadOnlyList<ReplayChatMessage> source)
    {
        if (source.Count == 0)
        {
            return 0;
        }

        var added = 0;
        foreach (var message in source.OrderBy(message => message.Offset))
        {
            if (AddCore(message))
            {
                added++;
            }
        }

        return added;
    }

    private ReplayChatWindowSelection SelectRangeCore(TimeSpan startOffset, TimeSpan endOffset, int maxMessages)
    {
        if (LogicalCount == 0 || endOffset < startOffset)
        {
            return new ReplayChatWindowSelection([], ReplayChatWindowKey.Empty);
        }

        var firstIndex = LowerBound(startOffset);
        var endIndex = UpperBound(endOffset);
        if (endIndex <= firstIndex)
        {
            return new ReplayChatWindowSelection([], ReplayChatWindowKey.Empty);
        }

        firstIndex = Math.Max(firstIndex, endIndex - Math.Max(1, maxMessages));
        var count = endIndex - firstIndex;
        var visibleMessages = new ChatMessage[count];
        var hash = ReplayChatWindowKey.HashOffsetBasis;
        for (var index = 0; index < count; index++)
        {
            var replayMessage = messages[firstIndex + index];
            visibleMessages[index] = replayMessage.Message;
            hash = HashReplayChatMessage(hash, replayMessage);
        }

        var key = new ReplayChatWindowKey(
            count,
            messages[firstIndex].Offset.Ticks,
            messages[endIndex - 1].Offset.Ticks,
            hash);
        return new ReplayChatWindowSelection(visibleMessages, key);
    }

    private bool AddCore(ReplayChatMessage message)
    {
        if (!messageKeys.Add(GetReplayChatMessageKey(message)))
        {
            return false;
        }

        var index = LogicalCount == 0 || message.Offset >= messages[^1].Offset
            ? messages.Count
            : UpperBound(message.Offset);
        messages.Insert(index, message);
        return true;
    }

    private int ReplaceOrAddOrderedCore(
        List<ReplayChatMessage> source,
        int sourceHeadIndex,
        int sourceCount,
        bool replaceExisting)
    {
        lock (gate)
        {
            if (replaceExisting)
            {
                messages.Clear();
                messageKeys.Clear();
                headIndex = 0;
            }

            var added = 0;
            var endIndex = sourceHeadIndex + sourceCount;
            for (var index = sourceHeadIndex; index < endIndex; index++)
            {
                if (AddCore(source[index]))
                {
                    added++;
                }
            }

            CompactIfNeeded();
            return added;
        }
    }

    private void RemoveHeadCore()
    {
        if (LogicalCount == 0)
        {
            return;
        }

        messageKeys.Remove(GetReplayChatMessageKey(messages[headIndex]));
        headIndex++;
    }

    private void CompactIfNeeded()
    {
        if (headIndex < CompactionHeadThreshold ||
            headIndex < messages.Count / 2)
        {
            return;
        }

        messages.RemoveRange(0, headIndex);
        headIndex = 0;
    }

    private int LowerBound(TimeSpan value)
    {
        var low = headIndex;
        var high = messages.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (messages[mid].Offset < value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int UpperBound(TimeSpan value)
    {
        var low = headIndex;
        var high = messages.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (messages[mid].Offset <= value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    internal static string GetReplayChatMessageKey(ReplayChatMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.Message.MessageId)
            ? message.Message.MessageId
            : $"{message.Offset.TotalMilliseconds:0}:{message.Message.Username}:{message.Message.Message}";
    }

    private static ulong HashReplayChatMessage(ulong hash, ReplayChatMessage replayMessage)
    {
        hash = HashInt64(hash, replayMessage.Offset.Ticks);
        hash = HashString(hash, replayMessage.Message.MessageId);
        hash = HashString(hash, replayMessage.Message.Username);
        hash = HashInt64(hash, replayMessage.Message.Timestamp.UtcTicks);
        hash = HashString(hash, replayMessage.Message.Message);
        return hash;
    }

    private static ulong HashInt64(ulong hash, long value)
    {
        unchecked
        {
            for (var index = 0; index < sizeof(long); index++)
            {
                hash ^= (byte)(value >> (index * 8));
                hash *= ReplayChatWindowKey.HashPrime;
            }
        }

        return hash;
    }

    private static ulong HashString(ulong hash, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return HashInt64(hash, 0);
        }

        unchecked
        {
            foreach (var character in value)
            {
                hash ^= character;
                hash *= ReplayChatWindowKey.HashPrime;
            }
        }

        return hash;
    }
}

internal readonly record struct ReplayChatWindowSelection(
    IReadOnlyList<ChatMessage> Messages,
    ReplayChatWindowKey Key);

internal readonly record struct ReplayChatWindowKey(
    int Count,
    long FirstOffsetTicks,
    long LastOffsetTicks,
    ulong ContentHash)
{
    internal const ulong HashOffsetBasis = 14695981039346656037UL;
    internal const ulong HashPrime = 1099511628211UL;
    public static ReplayChatWindowKey Empty { get; } = new(0, 0, 0, 0);
}
