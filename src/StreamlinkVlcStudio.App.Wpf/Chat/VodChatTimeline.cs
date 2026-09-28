using System.Globalization;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

/// <summary>
/// An offset-ordered store of a VOD's chat plus a read cursor that tracks how far playback has
/// consumed it.
/// </summary>
/// <remarks>
/// <para>
/// The cursor is what makes VOD chat behave like live chat: <see cref="TakeMessagesDueAt"/> hands
/// back only the messages playback has newly reached, so the view model appends them to the same
/// collections the live feed appends to instead of rebuilding a window snapshot on every tick.
/// </para>
/// <para>
/// Messages inserted before the cursor are treated as already past and are never handed out, which
/// is what keeps live capture (whose messages arrive ahead of a behind-live playback position) and
/// network fetches from replaying chat the viewer has already seen.
/// </para>
/// </remarks>
internal sealed class VodChatTimeline
{
    private readonly object gate = new();
    private readonly MessageRing messages = new();
    private readonly HashSet<string> messageKeys = new(StringComparer.Ordinal);
    private int cursor;
    private TimeSpan seekBoundary = TimeSpan.MinValue;
    private TimeSpan? discardedThrough;

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

    public bool HasMessages => Count > 0;

    public bool HasMessagesAtOrBefore(TimeSpan position)
    {
        lock (gate)
        {
            return messages.Count > 0 && messages[0].Offset <= position;
        }
    }

    public bool HasDiscardedMessagesFrom(TimeSpan offset)
    {
        lock (gate)
        {
            return discardedThrough is { } boundary && offset <= boundary;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            messages.Clear();
            messageKeys.Clear();
            cursor = 0;
            seekBoundary = TimeSpan.MinValue;
            discardedThrough = null;
        }
    }

    /// <summary>Adds one message, keeping the store sorted by offset. Returns false for duplicates.</summary>
    public bool Add(VodChatMessage message, int maximumMessages)
    {
        lock (gate)
        {
            if (!AddCore(message))
            {
                return false;
            }

            TrimCore(maximumMessages);
            return true;
        }
    }

    /// <summary>Adds a batch and returns how many were new.</summary>
    public int AddRange(IReadOnlyList<VodChatMessage> incoming, int maximumMessages)
    {
        if (incoming.Count == 0)
        {
            return 0;
        }

        lock (gate)
        {
            var added = 0;
            foreach (var message in incoming)
            {
                if (AddCore(message))
                {
                    added++;
                }
            }

            TrimCore(maximumMessages);
            return added;
        }
    }

    /// <summary>
    /// Moves the read cursor to <paramref name="offset"/>. Messages at or after it become pending
    /// again, so a backward seek re-shows the chat around the new position without a refetch.
    /// </summary>
    public void MoveCursorTo(TimeSpan offset)
    {
        lock (gate)
        {
            seekBoundary = offset;
            cursor = LowerBoundCore(offset);
        }
    }

    /// <summary>
    /// Returns the messages playback has reached but not yet shown, oldest first, and advances the
    /// cursor past them.
    /// </summary>
    /// <param name="position">The current playback offset.</param>
    /// <param name="maximumMessages">
    /// The most to return in one call. When more are due than this — after a seek, or after the UI
    /// thread has been busy — the oldest surplus is skipped rather than queued, because the chat
    /// view only keeps this many anyway and dribbling a backlog out over later calls would leave
    /// chat running behind the video.
    /// </param>
    public IReadOnlyList<ChatMessage> TakeMessagesDueAt(TimeSpan position, int maximumMessages)
    {
        if (maximumMessages <= 0)
        {
            return [];
        }

        lock (gate)
        {
            var dueEnd = UpperBoundCore(position);
            if (dueEnd <= cursor)
            {
                return [];
            }

            if (dueEnd - cursor > maximumMessages)
            {
                cursor = dueEnd - maximumMessages;
            }

            var due = new ChatMessage[dueEnd - cursor];
            for (var index = 0; index < due.Length; index++)
            {
                due[index] = messages[cursor + index].Message;
            }

            cursor = dueEnd;
            return due;
        }
    }

    private bool AddCore(VodChatMessage message)
    {
        if (!messageKeys.Add(GetMessageKey(message)))
        {
            return false;
        }

        var index = messages.Count == 0 || message.Offset >= messages[^1].Offset
            ? messages.Count
            : UpperBoundCore(message.Offset);
        messages.Insert(index, message);
        if (index < cursor || message.Offset < seekBoundary)
        {
            // Preserve the absolute seek boundary even when the list was empty (or ended
            // before the target) when seeking. Late pages still belong to their original time.
            cursor++;
        }

        return true;
    }

    /// <summary>Drops the oldest messages once the store outgrows its cap.</summary>
    private void TrimCore(int maximumMessages)
    {
        if (maximumMessages <= 0 || messages.Count <= maximumMessages)
        {
            return;
        }

        var removeCount = messages.Count - maximumMessages;
        var removedThrough = messages[removeCount - 1].Offset;
        if (discardedThrough is null || removedThrough > discardedThrough)
        {
            discardedThrough = removedThrough;
        }
        for (var index = 0; index < removeCount; index++)
        {
            messageKeys.Remove(GetMessageKey(messages[index]));
        }

        messages.RemoveFirst(removeCount);
        cursor = Math.Max(0, cursor - removeCount);
    }

    private int LowerBoundCore(TimeSpan offset)
    {
        var low = 0;
        var high = messages.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (messages[mid].Offset < offset)
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

    private int UpperBoundCore(TimeSpan offset)
    {
        var low = 0;
        var high = messages.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (messages[mid].Offset <= offset)
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

    internal static string GetMessageKey(VodChatMessage message)
    {
        var chatMessage = message.Message;
        if (!string.IsNullOrWhiteSpace(chatMessage.MessageId))
        {
            return chatMessage.MessageId.Trim();
        }

        return string.Concat(
            message.Offset.Ticks.ToString(CultureInfo.InvariantCulture),
            ":",
            chatMessage.Username,
            ":",
            chatMessage.Message);
    }

    /// <summary>Logical indices stay offset-ordered while eviction advances the head without shifting the history.</summary>
    private sealed class MessageRing
    {
        private VodChatMessage?[] items = [];
        private int head;

        internal int Count { get; private set; }
        internal VodChatMessage this[int index] => items[PhysicalIndex(index)]!;

        internal void Insert(int index, VodChatMessage message)
        {
            if (Count == items.Length)
            {
                var grown = new VodChatMessage?[items.Length == 0 ? 16 : checked(items.Length * 2)];
                for (var offset = 0; offset < Count; offset++) grown[offset] = this[offset];
                items = grown;
                head = 0;
            }

            // Appends are constant-time. Late arrivals retain the original upper-bound
            // insertion/equal-time ordering; move the shorter side of the ring.
            if (index < Count / 2)
            {
                head = head == 0 ? items.Length - 1 : head - 1;
                for (var offset = 0; offset < index; offset++)
                    items[PhysicalIndex(offset)] = items[PhysicalIndex(offset + 1)];
            }
            else
            {
                for (var offset = Count; offset > index; offset--)
                    items[PhysicalIndex(offset)] = items[PhysicalIndex(offset - 1)];
            }
            items[PhysicalIndex(index)] = message;
            Count++;
        }

        internal void RemoveFirst(int count)
        {
            var first = Math.Min(count, items.Length - head);
            Array.Clear(items, head, first);
            Array.Clear(items, 0, count - first);
            head = PhysicalIndex(count);
            Count -= count;
            if (Count == 0) head = 0;
        }

        internal void Clear()
        {
            if (Count > 0) RemoveFirst(Count);
        }

        private int PhysicalIndex(int index)
        {
            var physical = head + index;
            return physical >= items.Length ? physical - items.Length : physical;
        }
    }
}
