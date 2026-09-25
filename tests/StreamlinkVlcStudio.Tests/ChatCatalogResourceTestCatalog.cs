using System.Net.Http;
using System.Windows.Threading;

internal static class ChatCatalogResourceTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("chat catalog resources: learning an emote preserves unrelated rows", UnrelatedRowsStayAttachedAsync),
        ("chat catalog resources: concurrent changes retain every affected channel", ConcurrentScopesAreMergedAsync),
        ("chat catalog resources: global updates stay within their platform", GlobalScopeIsPlatformSpecificAsync),
        ("chat catalog resources: scope overflow refreshes all decorations", ScopeOverflowRefreshesAllAsync),
        ("chat catalog resources: changes during delivery get another notification", ReentrantChangesAreDeliveredAsync),
        ("chat catalog resources: failing subscribers do not suppress changes", FailingSubscriberIsIsolatedAsync),
        ("chat catalog resources: Twitch badges follow room identity", TwitchBadgeRoomIdentityAsync),
        ("chat catalog resources: reused rows follow their current channel", ReusedRowsFollowCurrentChannelAsync),
        ("chat catalog resources: unrelated overlays retain their render version", UnrelatedOverlaysRetainVersionAsync),
        ("chat catalog resources: learned emote eviction refreshes its original channel", LearnedEmoteEvictionAsync),
        ("chat catalog resources: channel emote eviction remains scoped", ChannelEmoteEvictionAsync),
        ("chat catalog resources: channel badge eviction remains scoped", ChannelBadgeEvictionAsync)
    ];

    private static Task UnrelatedRowsStayAttachedAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var channel = $"catalog{Guid.NewGuid():N}";
        var code = $"Wave{Guid.NewGuid():N}";
        var url = $"https://example.com/{code}.png";
        var dispatcher = Dispatcher.CurrentDispatcher;
        var rows = new List<DockedChatMessageTextBlock>();
        AnimatedEmoteImage.SetCachedSolidColorImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes,
            [Colors.Red, Colors.Blue], [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)]);
        PrimeScope(DockedChatBadgeCatalog.Shared, "badges:kick:bundled");
        foreach (var name in new[] { channel, channel + "other" })
            PrimeScope(DockedChatBadgeCatalog.Shared, $"badges:kick:channel:{name}");

        DockedChatMessageTextBlock CreateRow(string name)
        {
            var row = new DockedChatMessageTextBlock
            {
                Message = new ChatMessage(PlatformKind.Kick, name, "viewer", code, DateTimeOffset.UnixEpoch)
            };
            row.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            rows.Add(row);
            return row;
        }

        var relevant = CreateRow(channel);
        var unrelated = Enumerable.Range(0, 100).Select(_ => CreateRow(channel + "other")).ToArray();
        var inlines = unrelated.Select(row => row.Inlines.FirstInline).ToArray();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler changed = (_, _) => completed.TrySetResult();
        var posted = 0;
        void OnPosted(object? sender, DispatcherHookEventArgs e) => Interlocked.Increment(ref posted);
        try
        {
            DockedChatEmoteCatalog.Shared.CatalogChanged += changed;
            dispatcher.Hooks.OperationPosted += OnPosted;
            DockedChatEmoteCatalog.Shared.EnsureForMessage(new ChatMessage(
                PlatformKind.Kick, channel, "viewer", code, DateTimeOffset.UnixEpoch,
                Emotes: [new ChatEmote(0, code.Length, code, url)]));
            Assert.True(completed.Task.Wait(TimeSpan.FromSeconds(3)), "Catalog delivery must not block on the UI.");
            dispatcher.Hooks.OperationPosted -= OnPosted;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(url, relevant.AnimatedEmoteImages.Single().ImageUrl);
            Assert.True(relevant.AnimatedEmoteImages.Single().ApplyAnimationClock(TimeSpan.FromMilliseconds(150), out var delay));
            Assert.Equal(TimeSpan.FromMilliseconds(150), delay);
            var pixel = new byte[4];
            ((BitmapSource)relevant.AnimatedEmoteImages.Single().Source).CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.Equal("FF0000FF", Convert.ToHexString(pixel));
            var retained = unrelated.Where((row, index) => ReferenceEquals(inlines[index], row.Inlines.FirstInline)).Count();
            Console.WriteLine($"Catalog fixture: {posted} UI callbacks; {retained}/100 unrelated rows retained.");
            Assert.Equal(100, retained);
            Assert.Equal(1, posted);
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= OnPosted;
            DockedChatEmoteCatalog.Shared.CatalogChanged -= changed;
            foreach (var row in rows) row.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes);
        }
    });

    private static Task ConcurrentScopesAreMergedAsync()
    {
        var scheduled = new ConcurrentQueue<Action>();
        var notifier = new CatalogChangeNotifier(new object(), scheduled.Enqueue);
        CatalogChangedEventArgs? delivered = null;
        EventHandler handler = (_, e) => delivered = (CatalogChangedEventArgs)e;
        Parallel.For(0, 128, index =>
        {
            var scope = CatalogChangeScope.ForChannel(PlatformKind.Kick, $" Stream{index} ");
            notifier.Queue(() => handler, scope);
            notifier.Queue(() => handler, CatalogChangeScope.ForChannel(PlatformKind.Kick, $"stream{index}"));
        });
        Assert.Equal(1, scheduled.Count);
        Assert.True(scheduled.TryDequeue(out var deliver));
        deliver!();
        Assert.NotNull(delivered);
        for (var index = 0; index < 128; index++) Assert.True(delivered!.Affects(Message(PlatformKind.Kick, $"STREAM{index}")));
        Assert.True(!delivered!.Affects(Message(PlatformKind.Kick, "unrelated")));
        Assert.True(!delivered.Affects(Message(PlatformKind.Twitch, "stream0")));
        return Task.CompletedTask;
    }

    private static Task GlobalScopeIsPlatformSpecificAsync()
    {
        var scheduled = new Queue<Action>();
        var notifier = new CatalogChangeNotifier(new object(), scheduled.Enqueue);
        EventArgs? delivered = null;
        EventHandler handler = (_, e) => delivered = e;
        notifier.Queue(() => handler, CatalogChangeScope.ForChannel(PlatformKind.Twitch));
        notifier.Queue(() => handler, CatalogChangeScope.ForChannel(PlatformKind.Kick, "specific"));
        scheduled.Dequeue()();
        var changes = (CatalogChangedEventArgs)delivered!;
        Assert.True(changes.Affects(Message(PlatformKind.Twitch, "anything")));
        Assert.True(changes.Affects(Message(PlatformKind.Kick, "specific")));
        Assert.True(!changes.Affects(Message(PlatformKind.Kick, "other")));
        notifier.Queue(() => handler, CatalogChangeScope.ForChannel(PlatformKind.Kick, "specific"));
        notifier.Queue(() => handler);
        scheduled.Dequeue()();
        Assert.True(ReferenceEquals(EventArgs.Empty, delivered), "Legacy complete refreshes must remain complete.");
        return Task.CompletedTask;
    }

    private static Task ScopeOverflowRefreshesAllAsync()
    {
        var scheduled = new Queue<Action>();
        var notifier = new CatalogChangeNotifier(new object(), scheduled.Enqueue);
        EventArgs? delivered = null;
        EventHandler handler = (_, e) => delivered = e;
        for (var index = 0; index < 10_000; index++)
            notifier.Queue(() => handler, CatalogChangeScope.ForChannel(PlatformKind.Kick, $"stream{index}"));
        Assert.Equal(1, scheduled.Count);
        scheduled.Dequeue()();
        Assert.True(ReferenceEquals(EventArgs.Empty, delivered));
        notifier.Queue(() => handler, CatalogChangeScope.ForChannel(PlatformKind.Kick, "next"));
        scheduled.Dequeue()();
        var changes = (CatalogChangedEventArgs)delivered!;
        Assert.True(changes.Affects(Message(PlatformKind.Kick, "next")));
        Assert.True(!changes.Affects(Message(PlatformKind.Kick, "stream0")));
        return Task.CompletedTask;
    }

    private static Task ReentrantChangesAreDeliveredAsync()
    {
        var scheduled = new Queue<Action>();
        var notifier = new CatalogChangeNotifier(new object(), scheduled.Enqueue);
        var deliveries = new List<CatalogChangedEventArgs>();
        EventHandler handler = null!;
        handler = (_, e) =>
        {
            deliveries.Add((CatalogChangedEventArgs)e);
            if (deliveries.Count == 1)
                notifier.Queue(() => handler, CatalogChangeScope.ForChannel(PlatformKind.Kick, "second"));
        };
        notifier.Queue(() => handler, CatalogChangeScope.ForChannel(PlatformKind.Kick, "first"));
        scheduled.Dequeue()();
        Assert.Equal(1, scheduled.Count);
        scheduled.Dequeue()();
        Assert.Equal(2, deliveries.Count);
        Assert.True(deliveries[0].Affects(Message(PlatformKind.Kick, "first")));
        Assert.True(!deliveries[0].Affects(Message(PlatformKind.Kick, "second")));
        Assert.True(deliveries[1].Affects(Message(PlatformKind.Kick, "second")));
        return Task.CompletedTask;
    }

    private static Task FailingSubscriberIsIsolatedAsync()
    {
        var scheduled = new Queue<Action>();
        var notifier = new CatalogChangeNotifier(new object(), scheduled.Enqueue);
        var received = false;
        EventHandler handlers = (_, _) => throw new InvalidOperationException("Test subscriber failure");
        handlers += (_, e) => received = ((CatalogChangedEventArgs)e).Affects(Message(PlatformKind.Kick, "streamer"));
        notifier.Queue(() => handlers, CatalogChangeScope.ForChannel(PlatformKind.Kick, "streamer"));
        scheduled.Dequeue()();
        Assert.True(received);
        return Task.CompletedTask;
    }

    private static async Task TwitchBadgeRoomIdentityAsync()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"set_id":"subscriber","versions":[{"id":"1","title":"Subscriber","image_url_4x":"https://example.com/badge.png"}]}]}""")
        }));
        var catalog = new DockedChatBadgeCatalog(http);
        catalog.ConfigureTwitchCredentials("client", "token");
        PrimeScope(catalog, "badges:twitch:global");
        // Credential invalidation precedes priming so the fixture never loads global data.
        PrimeScope(catalog, "badges:twitch:bundled");
        var delivered = new TaskCompletionSource<CatalogChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.CatalogChanged += (_, e) => delivered.TrySetResult((CatalogChangedEventArgs)e);
        var message = Message(PlatformKind.Twitch, "streamer") with { RoomId = "123" };
        catalog.EnsureForMessage(message);
        var changes = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(catalog.TryGet(message, new ChatBadge("subscriber", "1"), out var image));
        Assert.Equal("https://example.com/badge.png", image.ImageUrl);
        Assert.True(changes.Affects(message with { Channel = "renamed", RoomId = " 123 " }));
        Assert.True(!changes.Affects(message with { RoomId = "456" }));
        Assert.True(!changes.Affects(message with { RoomId = null }));
        Assert.True(!changes.Affects(message with { Platform = PlatformKind.Kick }));
    }

    private static Task ReusedRowsFollowCurrentChannelAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var prefix = $"reused{Guid.NewGuid():N}";
        PrimeScope(DockedChatBadgeCatalog.Shared, "badges:kick:bundled");
        PrimeScope(DockedChatBadgeCatalog.Shared, $"badges:kick:channel:{prefix}first");
        PrimeScope(DockedChatBadgeCatalog.Shared, $"badges:kick:channel:{prefix}second");
        var row = new DockedChatMessageTextBlock { Message = Message(PlatformKind.Kick, prefix + "first") };
        row.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        try
        {
            row.Message = Message(PlatformKind.Kick, prefix + "second");
            var current = row.Inlines.FirstInline;
            DeliverToRows(new CatalogChangedEventArgs([CatalogChangeScope.ForChannel(PlatformKind.Kick, prefix + "first")]));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(ReferenceEquals(current, row.Inlines.FirstInline));
            DeliverToRows(new CatalogChangedEventArgs([CatalogChangeScope.ForChannel(PlatformKind.Kick, prefix + "second")]));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(!ReferenceEquals(current, row.Inlines.FirstInline));
            row.Message = null;
            DeliverToRows(new CatalogChangedEventArgs([CatalogChangeScope.ForChannel(PlatformKind.Kick)]));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, row.Inlines.Count);
        }
        finally
        {
            row.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
    });

    private static async Task UnrelatedOverlaysRetainVersionAsync()
    {
        var pending = new Queue<Action>();
        await using var tab = TestViewModels.CreateTab(
            StreamInputParser.FromChannel(PlatformKind.Twitch, "streamer") with { BroadcasterId = "123" },
            "best", new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), pending.Enqueue);
        var method = typeof(StreamTabViewModel).GetMethod("OnChatRenderCatalogChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var version = typeof(StreamTabViewModel).GetField("nativeReplayOverlayRenderContentVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var initial = (long)version.GetValue(tab)!;
        pending.Clear();
        void Notify(EventArgs changes) => method.Invoke(tab, [null, changes]);
        Notify(new CatalogChangedEventArgs([CatalogChangeScope.ForChannel(PlatformKind.Kick, "streamer")]));
        Notify(new CatalogChangedEventArgs([CatalogChangeScope.ForChannel(PlatformKind.Twitch, "other")]));
        Notify(new CatalogChangedEventArgs([CatalogChangeScope.ForTwitchRoom("456")]));
        Assert.Equal(initial, (long)version.GetValue(tab)!);
        Assert.Equal(0, pending.Count);
        Notify(new CatalogChangedEventArgs([CatalogChangeScope.ForChannel(PlatformKind.Twitch, " STREAMER ")]));
        Notify(new CatalogChangedEventArgs([CatalogChangeScope.ForTwitchRoom("123")]));
        Notify(new CatalogChangedEventArgs([CatalogChangeScope.ForChannel(PlatformKind.Twitch)]));
        Notify(EventArgs.Empty);
        Assert.Equal(initial + 4, (long)version.GetValue(tab)!);
        Assert.Equal(4, pending.Count);
        var roomChange = new CatalogChangedEventArgs([CatalogChangeScope.ForTwitchRoom("123")]);
        Assert.True(roomChange.MayAffect(tab.Target with { BroadcasterId = "" }), "Unresolved live targets must still refresh.");
    }

    private static async Task LearnedEmoteEvictionAsync()
    {
        var catalog = new DockedChatEmoteCatalog();
        var add = typeof(DockedChatEmoteCatalog).GetMethod("AddMessageEmote", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var index = 0; index < 4096; index++)
            add.Invoke(catalog, [PlatformKind.Kick, "old", $"E{index}", $"https://example.com/e{index}.png", 28, 28]);
        var evicted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = new ConcurrentQueue<CatalogChangedEventArgs>();
        catalog.CatalogChanged += (_, e) =>
        {
            var changes = (CatalogChangedEventArgs)e;
            deliveries.Enqueue(changes);
            if (changes.Affects(Message(PlatformKind.Kick, "old"))) evicted.TrySetResult();
            if (changes.Affects(Message(PlatformKind.Kick, "new"))) added.TrySetResult();
        };
        catalog.EnsureForMessage(Message(PlatformKind.Kick, "new") with
        {
            Emotes = [new ChatEmote(0, 4, "Wave", "https://example.com/wave.png")]
        });
        await Task.WhenAll(evicted.Task, added.Task).WaitAsync(TimeSpan.FromSeconds(3));
        foreach (var changes in deliveries)
        {
            Assert.True(!changes.Affects(Message(PlatformKind.Twitch, "old")));
            Assert.True(!changes.Affects(Message(PlatformKind.Kick, "unrelated")));
        }
        Assert.True(!catalog.TryGet(Message(PlatformKind.Kick, "old"), "E0", out _));
        Assert.Equal(4096, catalog.MessageSuppliedEmoteCountForTest);
    }

    private static async Task ChannelEmoteEvictionAsync()
    {
        var catalog = new DockedChatEmoteCatalog();
        Assert.True((bool)typeof(DockedChatEmoteCatalog).GetMethod("AddCatalogEmote", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(catalog, [PlatformKind.Twitch, "streamer", "Wave", "https://example.com/wave.png", 28, 28, "twitch:123:streamer"])!);
        var changes = await EvictAsync(catalog, "twitch:123:streamer");
        Assert.True(changes.Affects(Message(PlatformKind.Twitch, "STREAMER")));
        Assert.True(!changes.Affects(Message(PlatformKind.Twitch, "other")));
        Assert.True(!changes.Affects(Message(PlatformKind.Kick, "streamer")));
        Assert.True(!catalog.TryGet(Message(PlatformKind.Twitch, "streamer"), "Wave", out _));
    }

    private static async Task ChannelBadgeEvictionAsync()
    {
        var catalog = new DockedChatBadgeCatalog();
        Assert.True((bool)typeof(DockedChatBadgeCatalog).GetMethod("AddKickBadge", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(catalog, ["streamer", "subscriber", "1", "Subscriber", "https://files.kick.com/badge.png"])!);
        var changes = await EvictAsync(catalog, "badges:kick:channel:streamer");
        Assert.True(changes.Affects(Message(PlatformKind.Kick, "STREAMER")));
        Assert.True(!changes.Affects(Message(PlatformKind.Kick, "other")));
        Assert.True(!changes.Affects(Message(PlatformKind.Twitch, "streamer")));
        Assert.True(!catalog.TryGet(Message(PlatformKind.Kick, "streamer"), new ChatBadge("subscriber", "1"), out _));
    }

    private static async Task<CatalogChangedEventArgs> EvictAsync(object catalog, string scope)
    {
        var completed = new TaskCompletionSource<CatalogChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler changed = (_, e) => completed.TrySetResult((CatalogChangedEventArgs)e);
        var eventInfo = catalog.GetType().GetEvent("CatalogChanged")!;
        eventInfo.AddEventHandler(catalog, changed);
        try
        {
            catalog.GetType().GetMethod("EvictCatalogScope", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(catalog, [scope]);
            return await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            eventInfo.RemoveEventHandler(catalog, changed);
        }
    }

    private static void DeliverToRows(EventArgs changes)
    {
        var field = typeof(DockedChatEmoteCatalog).GetField("CatalogChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handlers = (EventHandler?)field.GetValue(DockedChatEmoteCatalog.Shared);
        Task.Run(() => handlers?.Invoke(DockedChatEmoteCatalog.Shared, changes)).GetAwaiter().GetResult();
    }

    private static ChatMessage Message(PlatformKind platform, string channel) =>
        new(platform, channel, "viewer", "hello", DateTimeOffset.UnixEpoch);

    private static void PrimeScope(object catalog, string scope)
    {
        var field = catalog.GetType().GetField("loadCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var coordinator = (CatalogLoadCoordinator)field.GetValue(catalog)!;
        coordinator.Ensure(scope, () => Task.FromResult(CatalogLoadResult.Successful()), preserveFromEviction: true);
    }
}
