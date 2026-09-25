internal static class GrowingVodChatTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("growing VOD chat: Twitch seekback continues after the current last page", SeekbackContinuesAsync),
        ("growing VOD chat: empty last pages retry without skipping unpublished messages", DelayedEmptyTailAsync),
        ("growing VOD chat: finished archives stop polling and retain backward seeking", FinishedArchiveAsync),
        ("growing VOD chat: seeking outside coverage cancels the tail cooldown", SeekDuringCooldownAsync),
        ("growing VOD chat: a stale last page cannot delay a newer seek", StaleTailAsync),
        ("growing VOD chat: live capture continues during the tail cooldown", CaptureDuringCooldownAsync)
    ];

    private static async Task SeekbackContinuesAsync()
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123", "123",
            startedAt, TimeSpan.FromHours(1), true, "");
        var publishMore = 0;
        var offsets = new ConcurrentQueue<int>();
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var offset = body.RootElement[0].GetProperty("variables").GetProperty("contentOffsetSeconds").GetInt32();
            offsets.Enqueue(offset);
            return Page(startedAt, hasNextPage: false, offset > 650
                ? []
                : Volatile.Read(ref publishMore) == 0 ? [590, 600] : [590, 600, 601]);
        }));
        var playback = new FakePlaybackEngineFactory();
        await using var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch), "source",
            new FakeStreamlinkService(), playback, new FakeChatClientFactory(), new MemoryLogger(),
            action => action(), replayResolver: new FakeReplayResolver(replay),
            vodChatProvider: new VodChatProvider(client));
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => tab.IsReplaySeekEnabled, TimeSpan.FromSeconds(3));
        await tab.VodChatIdleTask.WaitAsync(TimeSpan.FromSeconds(3));
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(600));
        await TestWait.UntilAsync(() => HasMessage(tab, "chat-600"),
            TimeSpan.FromSeconds(3));

        // Twitch's current final page has arrived and was displayed. Publish another message
        // without another seek, a reconnect, or a live IRC message to restart the chat pump.
        Volatile.Write(ref publishMore, 1);
        await TestWait.UntilAsync(() => HasMessage(tab, "chat-601"),
            TimeSpan.FromSeconds(8));
        Assert.Equal(1, tab.DockedChatMessages.Count(message => message.MessageId == "chat-600"));
        Assert.True(offsets.Contains(600));
    }

    private static bool HasMessage(StreamTabViewModel tab, string id)
    {
        try
        {
            return tab.DockedChatMessages.ToArray().Any(message => message.MessageId == id);
        }
        catch (InvalidOperationException)
        {
            // The fake dispatcher applies clock updates on the background worker.
            return false;
        }
    }

    private static async Task DelayedEmptyTailAsync()
    {
        var replay = Replay();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var publishMore = 0;
        var offsets = new ConcurrentQueue<int>();
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            offsets.Enqueue(body.RootElement[0].GetProperty("variables").GetProperty("contentOffsetSeconds").GetInt32());
            return Page(replay.StreamStartedAtUtc!.Value, false,
                Volatile.Read(ref publishMore) == 0 ? [] : [580, 599, 600]);
        }));
        await using var controller = new VodChatController(new VodChatProvider(client), new MemoryLogger(), clock);
        Start(controller, replay, 600);
        await Idle(controller);
        Assert.Equal(1, offsets.Count);

        clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(600);
        Assert.Equal(1, offsets.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => offsets.Count == 2, TimeSpan.FromSeconds(3));
        await Idle(controller);
        Assert.SequenceEqual(new[] { 570, 570 }, offsets);

        Volatile.Write(ref publishMore, 1);
        clock.Advance(TimeSpan.FromSeconds(5));
        await TestWait.UntilAsync(() => controller.TimelineCount == 3, TimeSpan.FromSeconds(3));
        await Idle(controller);
        Assert.SequenceEqual(new[] { 570, 570, 570 }, offsets);
        Assert.SequenceEqual(new[] { "chat-580", "chat-599", "chat-600" },
            controller.TakeMessagesDueAt(TimeSpan.FromSeconds(600), 100).Select(message => message.MessageId));
    }

    private static async Task FinishedArchiveAsync()
    {
        var replay = Replay();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var calls = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Page(replay.StreamStartedAtUtc!.Value, false, [10, 20]);
        }));
        await using var controller = new VodChatController(new VodChatProvider(client), new MemoryLogger(), clock);
        Start(controller, replay, 30, isGrowing: false);
        await Idle(controller);
        Assert.Equal(2, controller.TakeMessagesDueAt(TimeSpan.FromSeconds(30), 100).Count);
        clock.Advance(TimeSpan.FromMinutes(1));
        controller.UpdatePosition(TimeSpan.FromSeconds(100));
        await Task.Delay(600);
        Assert.Equal(1, calls);
        Start(controller, replay, 20, isGrowing: false);
        Assert.Equal(2, controller.TakeMessagesDueAt(TimeSpan.FromSeconds(20), 100).Count);
        Assert.Equal(1, calls);
    }

    private static async Task SeekDuringCooldownAsync()
    {
        var replay = Replay();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var provider = new CallbackProvider((_, offset, _) => Task.FromResult(
            VodChatFetchResult.Completed([Message(offset.TotalSeconds + 10)], offset + TimeSpan.FromSeconds(10))));
        await using var controller = new VodChatController(provider, new MemoryLogger(), clock);
        Start(controller, replay, 600);
        await Idle(controller);
        Start(controller, replay, 120);
        await Idle(controller);
        Assert.SequenceEqual(new[] { 570d, 90d }, provider.Offsets.Select(offset => offset.TotalSeconds));
        Assert.Equal("captured-100", controller.TakeMessagesDueAt(TimeSpan.FromSeconds(120), 100).Single().MessageId);
    }

    private static async Task StaleTailAsync()
    {
        var replay = Replay();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<VodChatFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new CallbackProvider((_, offset, token) =>
        {
            if (offset == TimeSpan.FromSeconds(570))
            {
                firstStarted.TrySetResult();
                return release.Task.WaitAsync(token);
            }
            return Task.FromResult(VodChatFetchResult.Completed([Message(100)], TimeSpan.FromSeconds(100)));
        });
        await using var controller = new VodChatController(provider, new MemoryLogger(), clock);
        Start(controller, replay, 600);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Start(controller, replay, 120);
        release.SetResult(VodChatFetchResult.Completed([], TimeSpan.FromSeconds(600)));
        await Idle(controller);
        Assert.SequenceEqual(new[] { 570d, 90d }, provider.Offsets.Select(offset => offset.TotalSeconds));
        Assert.Equal("captured-100", controller.TakeMessagesDueAt(TimeSpan.FromSeconds(120), 100).Single().MessageId);
    }

    private static async Task CaptureDuringCooldownAsync()
    {
        var replay = Replay();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var provider = new CallbackProvider((_, offset, _) => Task.FromResult(
            VodChatFetchResult.Completed([], offset + TimeSpan.FromSeconds(30))));
        await using var controller = new VodChatController(provider, new MemoryLogger(), clock);
        Start(controller, replay, 600);
        await Idle(controller);
        controller.CaptureLiveMessage(Message(601).Message);
        Assert.Equal(0, controller.TakeMessagesDueAt(TimeSpan.FromSeconds(600), 100).Count);
        Assert.Equal("captured-601", controller.TakeMessagesDueAt(TimeSpan.FromSeconds(601), 100).Single().MessageId);
        Assert.Equal(1, provider.Offsets.Count);
    }

    private static ReplaySessionInfo Replay() => new(
        PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123", "123",
        DateTimeOffset.UnixEpoch, TimeSpan.FromHours(1), true, "");

    private static VodChatMessage Message(double seconds) => new(TimeSpan.FromSeconds(seconds),
        new ChatMessage(PlatformKind.Twitch, "streamer", "viewer", $"at {seconds}",
            DateTimeOffset.UnixEpoch.AddSeconds(seconds), MessageId: $"captured-{seconds}"));

    private static void Start(VodChatController controller, ReplaySessionInfo replay, double seconds, bool isGrowing = true) =>
        controller.Start(replay, new AppSettings(), TimeSpan.FromSeconds(seconds), () => replay.Duration, isGrowing);

    private static Task Idle(VodChatController controller) =>
        controller.WaitUntilCaughtUpAsync().WaitAsync(TimeSpan.FromSeconds(3));

    private sealed class CallbackProvider(
        Func<ReplaySessionInfo, TimeSpan, CancellationToken, Task<VodChatFetchResult>> fetch) : IVodChatProvider
    {
        internal ConcurrentQueue<TimeSpan> Offsets { get; } = new();

        public Task<VodChatFetchResult> FetchAsync(ReplaySessionInfo replay, AppSettings settings, TimeSpan fromOffset,
            CancellationToken cancellationToken = default)
        {
            Offsets.Enqueue(fromOffset);
            return fetch(replay, fromOffset, cancellationToken);
        }
    }

    private static HttpResponseMessage Page(DateTimeOffset startedAt, bool hasNextPage, int[] offsets) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new[]
            {
                new
                {
                    data = new
                    {
                        video = new
                        {
                            comments = new
                            {
                                edges = offsets.Select(offset => new
                                {
                                    node = new
                                    {
                                        id = $"chat-{offset}", contentOffsetSeconds = offset,
                                        createdAt = startedAt.AddSeconds(offset),
                                        commenter = new { displayName = "viewer" },
                                        message = new { body = $"message at {offset}" }
                                    }
                                }),
                                pageInfo = new { hasNextPage }
                            }
                        }
                    }
                }
            }))
        };
}
