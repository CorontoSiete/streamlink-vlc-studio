using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

internal static class MultistreamResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("multistream resources: timeline matches the original through wraparound late arrivals and seeks", TimelineEquivalence),
        ("multistream resources: evicted and cleared chat references are released", EvictedReferences),
        ("multistream resources: tab diagnostics retain 300 lines with one queued delivery", TabLogBurstAsync),
        ("multistream resources: diagnostic batches yield and stop on disposal", LogBatchYield),
        ("multistream resources: rejected diagnostic delivery can retry", LogDispatchRetry),
        .. Environment.GetEnvironmentVariable("SVS_BENCHMARK_TIMELINE") == "1"
            ? new (string, Func<Task>)[] { ("multistream resources: four full history benchmark", BenchmarkTimeline) }
            : []
    ];

    private static VodChatMessage Message(int id, long tick, bool fallbackKey = false) => new(TimeSpan.FromTicks(tick),
        new ChatMessage(PlatformKind.Twitch, "fixture", "viewer" + id % 11, "message " + id,
            DateTimeOffset.UnixEpoch, MessageId: fallbackKey ? null : " " + id.ToString(CultureInfo.InvariantCulture) + " "));

    private static Task TimelineEquivalence()
    {
        foreach (var maximum in new[] { 17, 127, 40_000 })
        {
            var random = new Random(781 + maximum);
            var original = new OriginalVodChatTimeline();
            var actual = new VodChatTimeline();
            var pool = new List<VodChatMessage>();
            long clock = 0;
            var id = 0;
            for (var i = 0; i < maximum + 100; i++)
            {
                var message = Message(id++, clock++);
                pool.Add(message);
                Assert.Equal(original.Add(message, maximum), actual.Add(message, maximum));
            }
            for (var operation = 0; operation < 6_000; operation++)
            {
                var offset = TimeSpan.FromTicks(random.NextInt64(-10, clock + 10));
                switch (random.Next(10))
                {
                    case 0:
                        original.MoveCursorTo(offset); actual.MoveCursorTo(offset);
                        break;
                    case 1:
                    case 2:
                        var take = random.Next(-1, 40);
                        Assert.True(original.TakeMessagesDueAt(offset, take).SequenceEqual(actual.TakeMessagesDueAt(offset, take)),
                            $"Cursor mismatch at cap {maximum}, operation {operation}.");
                        break;
                    case 3:
                        var duplicate = pool[random.Next(pool.Count)];
                        Assert.Equal(original.Add(duplicate, maximum), actual.Add(duplicate, maximum));
                        break;
                    case 4:
                        var incoming = Enumerable.Range(0, random.Next(1, 20))
                            .Select(_ => random.Next(4) == 0 ? pool[random.Next(pool.Count)] : Message(id++, random.NextInt64(-5, clock + 3), id % 3 == 0))
                            .ToArray();
                        Assert.Equal(original.AddRange(incoming, maximum), actual.AddRange(incoming, maximum));
                        pool.AddRange(incoming);
                        break;
                    default:
                        // Include equal timestamps and frequent head/middle insertion.
                        var timestamp = random.Next(4) == 0 ? random.NextInt64(-5, clock + 1) : clock += random.Next(2);
                        var message = Message(id++, timestamp, id % 3 == 0);
                        pool.Add(message);
                        Assert.Equal(original.Add(message, maximum), actual.Add(message, maximum));
                        break;
                }
                Assert.Equal(original.Count, actual.Count);
                Assert.Equal(original.HasMessagesAtOrBefore(offset), actual.HasMessagesAtOrBefore(offset));
                Assert.Equal(original.HasDiscardedMessagesFrom(offset), actual.HasDiscardedMessagesFrom(offset));
                if (operation == 3_333)
                {
                    original.Clear(); actual.Clear();
                    original.MoveCursorTo(TimeSpan.FromTicks(clock + 10));
                    actual.MoveCursorTo(TimeSpan.FromTicks(clock + 10));
                }
            }
            original.MoveCursorTo(TimeSpan.MinValue); actual.MoveCursorTo(TimeSpan.MinValue);
            Assert.True(original.TakeMessagesDueAt(TimeSpan.MaxValue, int.MaxValue)
                .SequenceEqual(actual.TakeMessagesDueAt(TimeSpan.MaxValue, int.MaxValue)));
        }
        return Task.CompletedTask;
    }

    private static Task EvictedReferences()
    {
        var timeline = new VodChatTimeline();
        var evicted = PopulateAndEvict(timeline);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Equal(false, evicted.IsAlive);
        var cleared = PopulateAndClear(timeline);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Equal(false, cleared.IsAlive);
        GC.KeepAlive(timeline);
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PopulateAndEvict(VodChatTimeline timeline)
    {
        var first = Message(1, 1);
        var weak = new WeakReference(first);
        timeline.Add(first, 31);
        for (var index = 2; index < 250; index++) timeline.Add(Message(index, index), 31);
        Assert.Equal(31, timeline.Count);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PopulateAndClear(VodChatTimeline timeline)
    {
        var last = Message(999, 999);
        timeline.Add(last, 31);
        var weak = new WeakReference(last);
        timeline.Clear();
        return weak;
    }

    private static Task TabLogBurstAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var queue = new Queue<Action>();
        await using var tab = TestViewModels.CreateTab(StreamInputParser.FromChannel(PlatformKind.Kick, "fixture"), "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), queue.Enqueue);
        while (queue.TryDequeue(out var startup)) startup();
        var receive = (Action<object?, string>)typeof(StreamTabViewModel)
            .GetMethod("StreamSessionOnLogLineReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate(typeof(Action<object?, string>), tab);
        for (var index = 0; index < 10_000; index++) receive(null, "line " + index);
        Assert.Equal(1, queue.Count);
        queue.Dequeue()();
        Assert.Equal(300, tab.Logs.Count);
        Assert.Equal("line 9700", tab.Logs[0]);
        Assert.Equal("line 9999", tab.Logs[^1]);
        Assert.Equal(0, queue.Count);
        receive(null, "after disposal");
        await tab.DisposeAsync();
        while (queue.TryDequeue(out var callback)) callback();
        Assert.Equal("line 9999", tab.Logs[^1]);
    });

    private static Task LogBatchYield()
    {
        var lines = new ObservableCollection<string>();
        var queue = new Queue<Action>();
        using var buffer = new BoundedUiLogBuffer<string>(lines, queue.Enqueue, null, 300, static line => line);
        var injected = false;
        lines.CollectionChanged += (_, _) =>
        {
            if (injected) return;
            injected = true;
            for (var index = 0; index < 600; index++) buffer.Enqueue("new " + index);
        };
        for (var index = 0; index < 20; index++) buffer.Enqueue("initial " + index);
        var inputRan = false;
        queue.Enqueue(() => inputRan = true);
        queue.Dequeue()();
        Assert.Equal(false, inputRan);
        Assert.Equal(2, queue.Count);
        queue.Dequeue()();
        Assert.True(inputRan);
        queue.Dequeue()();
        Assert.Equal(300, lines.Count);
        Assert.Equal("new 300", lines[0]);
        Assert.Equal("new 599", lines[^1]);
        buffer.Enqueue("never delivered");
        buffer.Dispose();
        queue.Dequeue()();
        Assert.Equal("new 599", lines[^1]);
        return Task.CompletedTask;
    }

    private static Task LogDispatchRetry()
    {
        var lines = new ObservableCollection<string>();
        var queue = new Queue<Action>();
        var available = false;
        using var buffer = new BoundedUiLogBuffer<string>(lines, queue.Enqueue, action =>
        {
            if (!available) return false;
            queue.Enqueue(action); return true;
        }, 300, static line => line);
        buffer.Enqueue("retained");
        available = true;
        buffer.Enqueue("retry");
        Assert.Equal(1, queue.Count);
        queue.Dequeue()();
        Assert.True(lines.SequenceEqual(["retained", "retry"]));
        return Task.CompletedTask;
    }

    private static Task BenchmarkTimeline()
    {
        const int capacity = 40_000, count = 4, additions = 20_000;
        var initial = Enumerable.Range(0, capacity).Select(index => Message(index, index)).ToArray();
        var incoming = Enumerable.Range(capacity, additions).Select(index => Message(index, index)).ToArray();
        var results = new List<object>();
        for (var trial = 0; trial < 3; trial++)
        {
            foreach (var optimized in trial % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                Action<VodChatMessage>[] append = new Action<VodChatMessage>[count];
                for (var index = 0; index < count; index++)
                {
                    if (optimized)
                    {
                        var timeline = new VodChatTimeline();
                        timeline.AddRange(initial, capacity);
                        append[index] = message => timeline.Add(message, capacity);
                    }
                    else
                    {
                        var timeline = new OriginalVodChatTimeline();
                        timeline.AddRange(initial, capacity);
                        append[index] = message => timeline.Add(message, capacity);
                    }
                }
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                foreach (var message in incoming) foreach (var add in append) add(message);
                watch.Stop();
                results.Add(new
                {
                    trial,
                    version = optimized ? "after" : "before",
                    histories = count,
                    capacity,
                    additions,
                    elapsedSeconds = watch.Elapsed.TotalSeconds,
                    cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated
                });
            }
        }
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        var path = Environment.GetEnvironmentVariable("SVS_BENCHMARK_TIMELINE_OUTPUT");
        if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, json);
        return Task.CompletedTask;
    }
}
