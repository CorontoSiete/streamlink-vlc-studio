using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class ReplayChatWindowSelector
{
    private readonly object gate = new();
    private readonly List<ReplayChatMessage> messages = [];
    private readonly HashSet<string> messageKeys = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (gate)
            {
                return messages.Count;
            }
        }
    }

    public TimeSpan? FirstOffset
    {
        get
        {
            lock (gate)
            {
                return messages.Count == 0 ? null : messages[0].Offset;
            }
        }
    }

    public TimeSpan? LastOffset
    {
        get
        {
            lock (gate)
            {
                return messages.Count == 0 ? null : messages[^1].Offset;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            messages.Clear();
            messageKeys.Clear();
        }
    }

    public void Replace(IReadOnlyList<ReplayChatMessage> source)
    {
        lock (gate)
        {
            messages.Clear();
            messageKeys.Clear();
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

            while (messages.Count > maxCount)
            {
                messageKeys.Remove(GetReplayChatMessageKey(messages[0]));
                messages.RemoveAt(0);
                evicted = true;
            }

            return true;
        }
    }

    public IReadOnlyList<ReplayChatMessage> Snapshot()
    {
        lock (gate)
        {
            return messages.ToArray();
        }
    }

    public ReplayChatWindowSelection SelectWindow(TimeSpan offset, TimeSpan window, int maxMessages)
    {
        lock (gate)
        {
            return SelectWindowCore(offset, window, maxMessages);
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

    private ReplayChatWindowSelection SelectWindowCore(TimeSpan offset, TimeSpan window, int maxMessages)
    {
        var start = offset - window;
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        var firstIndex = LowerBound(start);
        var endIndex = UpperBound(offset);
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

        var index = messages.Count == 0 || message.Offset >= messages[^1].Offset
            ? messages.Count
            : UpperBound(message.Offset);
        messages.Insert(index, message);
        return true;
    }

    private int LowerBound(TimeSpan value)
    {
        var low = 0;
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
        var low = 0;
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
