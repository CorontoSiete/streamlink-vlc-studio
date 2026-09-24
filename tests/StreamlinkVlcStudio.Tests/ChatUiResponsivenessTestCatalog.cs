using System.Windows.Threading;

internal static class ChatUiResponsivenessTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("chat UI catalog notification bursts coalesce before entering the dispatcher", CatalogBurstCoalescesAsync),
        ("chat UI catalog refresh yields to pending input", CatalogRefreshYieldsToInputAsync),
        ("chat UI unloaded rows ignore pending catalog refreshes", UnloadedRowIgnoresPendingRefreshAsync),
        ("chat UI historical messages with conflicting emotes do not create a catalog feedback loop", ConflictingHistoricalEmotesSettleAsync),
        ("chat UI old messages cannot replace newer learned emotes when views are recreated", RecreatedViewsPreserveNewerLearnedEmoteAsync),
        ("chat UI duplicate emote codes in one message notify only on first admission", DuplicateEmoteCodesNotifyOnceAsync),
        ("chat UI concurrent views of old messages preserve the newer learned emote", ConcurrentHistoricalMessagesPreserveNewerEmoteAsync)
    ];

    private static Task CatalogBurstCoalescesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var block = CreateSubscribedEmptyRow();
        var posted = 0;
        void OnOperationPosted(object? sender, DispatcherHookEventArgs args) => Interlocked.Increment(ref posted);
        try
        {
            dispatcher.Hooks.OperationPosted += OnOperationPosted;
            // Keep the STA occupied until the producer has delivered the complete burst.
            // The real event handlers may post, but must not synchronously invoke this STA.
            DeliverCatalogNotificationsFromWorker(128);
            dispatcher.Hooks.OperationPosted -= OnOperationPosted;
            var burstOperations = Volatile.Read(ref posted);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(burstOperations <= 1,
                $"128 catalog notifications posted {burstOperations} dispatcher operations for one row; expected at most one pending refresh.");
        }
        finally
        {
            dispatcher.Hooks.OperationPosted -= OnOperationPosted;
            Unload(block);
        }
    });

    private static Task CatalogRefreshYieldsToInputAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var block = CreateSubscribedEmptyRow();
        var sentinel = new Run("waiting for refresh");
        block.Inlines.Add(sentinel);
        try
        {
            DeliverCatalogNotificationsFromWorker(32);
            var inputRanBeforeRefresh = await dispatcher.InvokeAsync(
                () => ReferenceEquals(block.Inlines.FirstInline, sentinel), DispatcherPriority.Input);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(inputRanBeforeRefresh,
                "Queued chat catalog refreshes ran ahead of pending input and can starve the UI during a sustained notification stream.");
            Assert.Equal(0, block.Inlines.Count);
        }
        finally
        {
            Unload(block);
        }
    });

    private static Task UnloadedRowIgnoresPendingRefreshAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var block = CreateSubscribedEmptyRow();
        var sentinel = new Run("unloaded row must stay untouched");
        block.Inlines.Add(sentinel);
        try
        {
            DeliverCatalogNotificationsFromWorker(8);
            Unload(block);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(ReferenceEquals(block.Inlines.FirstInline, sentinel),
                "A queued catalog callback rebuilt an unloaded chat row.");
        }
        finally
        {
            Unload(block);
        }
    });

    private static Task ConflictingHistoricalEmotesSettleAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var code = $"historical{Guid.NewGuid():N}";
        var firstUrl = $"https://example.com/{code}/old.png";
        var secondUrl = $"https://example.com/{code}/new.png";
        // Use real emote inlines while keeping this regression independent of the network.
        foreach (var url in new[] { firstUrl, secondUrl })
        {
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes,
                [Colors.Red], [TimeSpan.FromMilliseconds(100)]);
        }

        DockedChatMessageTextBlock CreateRow(string imageUrl) => new()
        {
            Message = new ChatMessage(PlatformKind.Kick, "", "viewer", code, DateTimeOffset.UnixEpoch,
                Emotes: [new ChatEmote(0, code.Length, code, imageUrl)])
        };

        var first = CreateRow(firstUrl);
        var second = CreateRow(secondUrl);
        var notifications = 0;
        EventHandler changed = (_, _) => Interlocked.Increment(ref notifications);
        try
        {
            // Neither row is subscribed yet. Drain the initial catalog admission notifications.
            await Task.Delay(100);
            DockedChatEmoteCatalog.Shared.CatalogChanged += changed;
            first.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            second.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            // A Normal-priority flood must never prevent fixture cleanup. The monitor runs
            // independently and tears down at Send priority once the feedback is proven.
            await Task.Run(async () =>
            {
                var deadline = Stopwatch.StartNew();
                while (Volatile.Read(ref notifications) < 16 && deadline.Elapsed < TimeSpan.FromMilliseconds(400))
                {
                    await Task.Delay(5).ConfigureAwait(false);
                }

                await dispatcher.InvokeAsync(() =>
                {
                    Unload(first);
                    Unload(second);
                }, DispatcherPriority.Send);
            });
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(Volatile.Read(ref notifications) <= 2,
                $"Two unchanged historical messages generated {Volatile.Read(ref notifications)} further catalog notifications by repeatedly replacing the same emote code.");
            Assert.Equal(firstUrl, first.AnimatedEmoteImages.Single().ImageUrl);
            Assert.Equal(secondUrl, second.AnimatedEmoteImages.Single().ImageUrl);
        }
        finally
        {
            Unload(first);
            Unload(second);
            DockedChatEmoteCatalog.Shared.CatalogChanged -= changed;
            AnimatedEmoteImage.RemoveCachedImageForTest(firstUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
            AnimatedEmoteImage.RemoveCachedImageForTest(secondUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
        }
    });

    private static Task RecreatedViewsPreserveNewerLearnedEmoteAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var code = $"recreated{Guid.NewGuid():N}";
        var firstUrl = $"https://example.com/{code}/old.png";
        var secondUrl = $"https://example.com/{code}/new.png";
        foreach (var url in new[] { firstUrl, secondUrl })
        {
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes,
                [Colors.Blue], [TimeSpan.FromMilliseconds(100)]);
        }

        ChatMessage Message(string url) => new(PlatformKind.Kick, "", "viewer", code, DateTimeOffset.UnixEpoch,
            Emotes: [new ChatEmote(0, code.Length, code, url)]);
        var first = Message(firstUrl);
        var second = Message(secondUrl);
        try
        {
            _ = new DockedChatMessageTextBlock { Message = first };
            _ = new DockedChatMessageTextBlock { Message = second };
            for (var index = 0; index < 8; index++)
            {
                var olderView = new DockedChatMessageTextBlock { Message = first };
                // The old row retains its own direct image URL, while plain-code fallback
                // lookup retains the latest admitted message's URL across view recreation.
                Assert.Equal(firstUrl, olderView.AnimatedEmoteImages.Single().ImageUrl);
                Assert.True(DockedChatEmoteCatalog.Shared.TryGet(first, code, out var learned));
                Assert.Equal(secondUrl, learned.ImageUrl);
            }

            var fallbackMessage = new ChatMessage(PlatformKind.Kick, "", "viewer", code, DateTimeOffset.UnixEpoch);
            var fallbackView = new DockedChatMessageTextBlock { Message = fallbackMessage };
            Assert.Equal(secondUrl, fallbackView.AnimatedEmoteImages.Single().ImageUrl);
        }
        finally
        {
            AnimatedEmoteImage.RemoveCachedImageForTest(firstUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
            AnimatedEmoteImage.RemoveCachedImageForTest(secondUrl, AnimatedEmoteImage.DefaultMaxImageBytes);
        }

        return Task.CompletedTask;
    });

    private static async Task DuplicateEmoteCodesNotifyOnceAsync()
    {
        var catalog = new DockedChatEmoteCatalog();
        var message = new ChatMessage(PlatformKind.Kick, "channel", "viewer", "Wave Wave", DateTimeOffset.UnixEpoch,
            Emotes:
            [
                new ChatEmote(0, 4, "Wave", "https://example.com/old-wave.png"),
                new ChatEmote(5, 9, "Wave", "https://example.com/new-wave.png")
            ]);
        var notifications = 0;
        catalog.CatalogChanged += (_, _) => Interlocked.Increment(ref notifications);
        catalog.EnsureForMessage(message);
        await TestWait.UntilAsync(() => Volatile.Read(ref notifications) == 1, TimeSpan.FromSeconds(1));
        for (var index = 0; index < 64; index++)
        {
            catalog.EnsureForMessage(message);
        }

        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref notifications));
        Assert.True(catalog.TryGet(message, "Wave", out var learned));
        Assert.Equal("https://example.com/new-wave.png", learned.ImageUrl);
    }

    private static Task ConcurrentHistoricalMessagesPreserveNewerEmoteAsync()
    {
        var catalog = new DockedChatEmoteCatalog();
        ChatMessage Message(string suffix) => new(PlatformKind.Kick, "channel", "viewer", "Wave", DateTimeOffset.UnixEpoch,
            Emotes: [new ChatEmote(0, 4, "Wave", $"https://example.com/{suffix}.png")]);
        var older = Message("older");
        var newer = Message("newer");
        catalog.EnsureForMessage(older);
        catalog.EnsureForMessage(newer);
        Parallel.For(0, 64, _ => catalog.EnsureForMessage(older));
        Assert.True(catalog.TryGet(older, "Wave", out var learned));
        Assert.Equal("https://example.com/newer.png", learned.ImageUrl);
        return Task.CompletedTask;
    }

    private static DockedChatMessageTextBlock CreateSubscribedEmptyRow()
    {
        var block = new DockedChatMessageTextBlock();
        block.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return block;
    }

    private static void Unload(DockedChatMessageTextBlock block) =>
        block.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

    private static void DeliverCatalogNotificationsFromWorker(int count)
    {
        // Invoke the actual subscribed handlers without depending on notifier timing or HTTP.
        // Reflection is confined to delivering the event; rendering and dispatch stay real.
        var eventField = typeof(DockedChatEmoteCatalog).GetField("CatalogChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(eventField);
        var handlers = (EventHandler?)eventField!.GetValue(DockedChatEmoteCatalog.Shared);
        Assert.NotNull(handlers);
        var delivered = Task.Run(() =>
        {
            for (var index = 0; index < count; index++)
            {
                handlers!(DockedChatEmoteCatalog.Shared, EventArgs.Empty);
            }
        });
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)), "Catalog notification producer blocked waiting for the UI thread.");
        delivered.GetAwaiter().GetResult();
    }
}
