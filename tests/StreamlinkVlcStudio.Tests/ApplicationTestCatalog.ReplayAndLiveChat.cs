internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ReplayAndLiveChat { get; } =
    [
    ("Kick native replay overlay recovers after empty seekback window", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_recovery_{Guid.NewGuid():N}";
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var chatFactory = new FakeChatClientFactory();
            chatFactory.Client.BackfillCoveredRequestedRange = true;
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "streamer",
                "https://kick.example/replay/index.m3u8",
                "kick-replay-native-overlay-recovery",
                startedAt,
                TimeSpan.FromHours(1),
                true,
                "",
                "best");
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested."));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                chatFactory,
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(settings);
            chatFactory.Client.BackfillMessages.Add(new ChatMessage(
                PlatformKind.Kick,
                "streamer",
                "viewer",
                "initial kick native overlay chat",
                startedAt.AddMinutes(10),
                MessageId: "initial-kick-native-overlay-chat"));

            var initialFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(3));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            AssertNativeOverlayChatFrame(await initialFrameTask);
            await TestWait.UntilAsync(
                () => tab.ChatMessages.Any(message => message.Message == "initial kick native overlay chat"),
                TimeSpan.FromSeconds(1));
            Assert.True(tab.ChatMessages.Any(message => message.Message == "initial kick native overlay chat"));

            var requestCountBeforeEmpty = chatFactory.Client.BackfillUntilRequests.Count;
            var emptyFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayTransparentFrame,
                TimeSpan.FromSeconds(4));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(30));

            AssertNativeOverlayTransparentFrame(await emptyFrameTask);
            await TestWait.UntilAsync(
                () => chatFactory.Client.BackfillUntilRequests.Count > requestCountBeforeEmpty,
                TimeSpan.FromSeconds(1));
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message == "initial kick native overlay chat"));

            var requestCountBeforeRecovery = chatFactory.Client.BackfillUntilRequests.Count;
            chatFactory.Client.BackfillMessages.Add(new ChatMessage(
                PlatformKind.Kick,
                "streamer",
                "later-viewer",
                "later kick timestamp overlay chat",
                startedAt.AddMinutes(31),
                MessageId: "later-kick-timestamp-overlay-chat"));
            var recoveredFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(4));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(31));

            AssertNativeOverlayChatFrame(await recoveredFrameTask);
            await TestWait.UntilAsync(
                () => chatFactory.Client.BackfillUntilRequests.Count > requestCountBeforeRecovery,
                TimeSpan.FromSeconds(1));
            await TestWait.UntilAsync(
                () => tab.ChatMessages.Any(message => message.Message == "later kick timestamp overlay chat"),
                TimeSpan.FromSeconds(1));
            Assert.True(tab.ChatMessages.Any(message => message.Message == "later kick timestamp overlay chat"));
            Assert.Equal(0, replayChatProvider.CallCount);

            await tab.DisposeAsync();
        });
    }),
    ("native VLC replay overlay event host decodes wheel and thumb scroll events", async () =>
    {
        var pipeName = $"svs_replay_scroll_events_{Guid.NewGuid():N}";
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
        var receivedNotches = new List<int>();
        var receivedGate = new object();
        var receivedBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedPosition = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new NativeOverlayReplayEventHost(
            new MemoryLogger(),
            action => action(),
            () => { },
            () => 1080,
            replayScrolled: notches =>
            {
                lock (receivedGate)
                {
                    receivedNotches.Add(notches);
                    if (receivedNotches.Count == 2)
                    {
                        receivedBoth.TrySetResult();
                    }
                }
            },
            replayScrollPositionChanged: position => receivedPosition.TrySetResult(position));

        var badMagic = BuildNativeOverlayEventMessage(1, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(badMagic.AsSpan(0, 4), 0);
        var badVersion = BuildNativeOverlayEventMessage(1, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(badVersion.AsSpan(4, 4), 2);
        host.Start(pipeName, positionStatePath);
        await WriteNativeOverlayEventPipeMessagesAsync(
            $"{pipeName}_events",
            [
                BuildNativeOverlayEventMessage(1, 2),
                BuildNativeOverlayEventMessage(1, -3),
                BuildNativeOverlayEventMessage(1, 0),
                BuildNativeOverlayEventMessage(1, 274),
                BuildNativeOverlayEventMessage(2, 7),
                BuildNativeOverlayEventMessage(2, -1),
                BuildNativeOverlayEventMessage(999, 1),
                badMagic,
                badVersion
            ],
            TimeSpan.FromSeconds(2));
        await receivedBoth.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(7, await receivedPosition.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Delay(100);

        lock (receivedGate)
        {
            Assert.SequenceEqual(new[] { 2, -3 }, receivedNotches);
        }
    }),
    ("native VLC replay overlay resize event saves normalized reference size above old cap", async () =>
    {
        var pipeName = $"svs_replay_resize_{Guid.NewGuid():N}";
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new NativeOverlayReplayEventHost(
            new MemoryLogger(),
            action => action(),
            () => invalidated.TrySetResult(),
            () => 720);

        host.Start(pipeName, positionStatePath);
        host.ResumeResizePersistence();

        await WriteNativeOverlayEventPipeMessageAsync(
            $"{pipeName}_events",
            BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(800, 320)),
            TimeSpan.FromSeconds(2));
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("reference 1200 480", File.ReadAllText($"{positionStatePath}.size"));
    }),
    ("native VLC replay overlay resize event waits until replay frame is established", async () =>
    {
        var pipeName = $"svs_replay_resize_suspended_{Guid.NewGuid():N}";
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidationCount = 0;
        await using var host = new NativeOverlayReplayEventHost(
            new MemoryLogger(),
            action => action(),
            () =>
            {
                Interlocked.Increment(ref invalidationCount);
                invalidated.TrySetResult();
            },
            () => 720,
            TimeSpan.FromMilliseconds(50));

        host.Start(pipeName, positionStatePath);

        await WriteNativeOverlayEventPipeMessageAsync(
            $"{pipeName}_events",
            BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(800, 320)),
            TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        Assert.Equal(0, Volatile.Read(ref invalidationCount));
        Assert.Equal(false, File.Exists($"{positionStatePath}.size"));

        host.ResumeResizePersistence();
        await WriteNativeOverlayEventPipeMessageAsync(
            $"{pipeName}_events",
            BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(800, 320)),
            TimeSpan.FromSeconds(2));
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref invalidationCount));
        Assert.Equal("reference 1200 480", File.ReadAllText($"{positionStatePath}.size"));
    }),
    ("native VLC replay overlay resize event clamps to live-equivalent reference size", async () =>
    {
        var pipeName = $"svs_replay_resize_max_{Guid.NewGuid():N}";
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new NativeOverlayReplayEventHost(
            new MemoryLogger(),
            action => action(),
            () => invalidated.TrySetResult(),
            () => 720);

        host.Start(pipeName, positionStatePath);
        host.ResumeResizePersistence();

        await WriteNativeOverlayEventPipeMessageAsync(
            $"{pipeName}_events",
            BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(2000, 900)),
            TimeSpan.FromSeconds(2));
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("reference 1920 1080", File.ReadAllText($"{positionStatePath}.size"));
    }),
    ("native VLC replay overlay ignores undersized resize event without overwriting saved size", async () =>
    {
        var pipeName = $"svs_replay_resize_ignore_small_{Guid.NewGuid():N}";
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
        File.WriteAllText($"{positionStatePath}.size", "reference 900 500");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidationCount = 0;
        await using var host = new NativeOverlayReplayEventHost(
            new MemoryLogger(),
            action => action(),
            () =>
            {
                Interlocked.Increment(ref invalidationCount);
                invalidated.TrySetResult();
            },
            () => 720,
            TimeSpan.FromMilliseconds(50));

        host.Start(pipeName, positionStatePath);
        host.ResumeResizePersistence();

        await WriteNativeOverlayEventPipeMessageAsync(
            $"{pipeName}_events",
            BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(1, 1)),
            TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        Assert.Equal(0, Volatile.Read(ref invalidationCount));
        Assert.Equal("reference 900 500", File.ReadAllText($"{positionStatePath}.size"));

        await WriteNativeOverlayEventPipeMessageAsync(
            $"{pipeName}_events",
            BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(800, 320)),
            TimeSpan.FromSeconds(2));
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref invalidationCount));
        Assert.Equal("reference 1200 480", File.ReadAllText($"{positionStatePath}.size"));
    }),
    ("native VLC replay overlay resize events coalesce burst to final size", async () =>
    {
        var pipeName = $"svs_replay_resize_burst_{Guid.NewGuid():N}";
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidationCount = 0;
        await using var host = new NativeOverlayReplayEventHost(
            new MemoryLogger(),
            action => action(),
            () =>
            {
                if (Interlocked.Increment(ref invalidationCount) == 1)
                {
                    invalidated.TrySetResult();
                }
            },
            () => 720,
            TimeSpan.FromMilliseconds(50));

        host.Start(pipeName, positionStatePath);
        host.ResumeResizePersistence();

        await WriteNativeOverlayEventPipeMessagesAsync(
            $"{pipeName}_events",
            [
                BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(640, 360)),
                BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(800, 450)),
                BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(960, 540))
            ],
            TimeSpan.FromSeconds(2));
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        Assert.Equal(1, Volatile.Read(ref invalidationCount));
        Assert.Equal("reference 1440 810", File.ReadAllText($"{positionStatePath}.size"));

        await WriteNativeOverlayEventPipeMessageAsync(
            $"{pipeName}_events",
            BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(960, 540)),
            TimeSpan.FromSeconds(2));
        await Task.Delay(150);

        Assert.Equal(1, Volatile.Read(ref invalidationCount));
        Assert.Equal("reference 1440 810", File.ReadAllText($"{positionStatePath}.size"));
    }),
    ("native VLC replay overlay contains resize timer callback failures", async () =>
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "StreamlinkVlcStudioTests",
            $"resize-callback-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var positionStatePath = Path.Combine(root, "overlay-position");
        var callbackReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new MemoryLogger();
        var host = new NativeOverlayReplayEventHost(
            logger,
            action => action(),
            () => { },
            () => 720,
            resizeDebounceDelay: TimeSpan.FromMilliseconds(1),
            resizeTempWritten: (_, _) =>
            {
                callbackReached.TrySetResult();
                throw new InvalidOperationException("Expected resize callback failure.");
            });

        try
        {
            host.Start($"resize-callback-failure-{Guid.NewGuid():N}", positionStatePath);
            host.ResumeResizePersistence();
            host.QueueResizeFlushForTest(800, 450);

            await callbackReached.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await TestWait.UntilAsync(
                () => logger.Entries.Any(entry =>
                    entry.Message.Contains("Could not flush native VLC replay overlay size", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(1));

            Assert.True(host.IsRunning);
            Assert.Equal(false, Directory.EnumerateFiles(root, "*.tmp").Any());
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }),
    ("native VLC replay overlay event host retries when event pipe instance is busy", async () =>
    {
        var pipeName = $"svs_replay_resize_busy_{Guid.NewGuid():N}";
        var eventPipeName = $"{pipeName}_events";
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var busyPipe = new NamedPipeServerStream(
            eventPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await using var host = new NativeOverlayReplayEventHost(
            new MemoryLogger(),
            action => action(),
            () => invalidated.TrySetResult(),
            () => 720);

        host.Start(pipeName, positionStatePath);
        host.ResumeResizePersistence();
        await Task.Delay(150);
        Assert.True(host.IsRunning);

        await busyPipe.DisposeAsync();
        await WriteNativeOverlayEventPipeMessageAsync(
            eventPipeName,
            BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(800, 320)),
            TimeSpan.FromSeconds(2));
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(host.IsRunning);
        Assert.Equal("reference 1200 480", File.ReadAllText($"{positionStatePath}.size"));
    }),
    ("native VLC replay overlay resize burst after seekback writes latest frame and keeps host alive", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_resize_frame_{Guid.NewGuid():N}";
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName,
                NativeOverlayPositionStatePathOverride = positionStatePath
            });
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            void Dispatch(Action action)
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.Invoke(action);
                }
            }

            var replay = new ReplaySessionInfo(
                PlatformKind.Twitch,
                "streamer",
                "https://www.twitch.tv/videos/123",
                "123",
                DateTimeOffset.UtcNow.AddHours(-1),
                TimeSpan.FromHours(1),
                true,
                "",
                "best");
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available([
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(10),
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        "viewer",
                        "native replay resize hello",
                        DateTimeOffset.UtcNow,
                        "#8AB4F8",
                        MessageId: "replay-native-resize-1"))
            ]));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                Dispatch,
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;
            settings.Chat.VlcOverlayFontSize = 18;

            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(settings);

            var initialFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(3));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            var initialFrame = await initialFrameTask;
            AssertNativeOverlayChatFrame(initialFrame);
            var initialBounds = GetNativeOverlayAlphaBounds(initialFrame);
            Assert.True(initialBounds.Height > 0);
            await TestWait.UntilAsync(
                () => tab.IsNativeReplayOverlayEventHostRunning &&
                    string.Equals(tab.NativeReplayOverlayEventHostPipeName, pipeName, StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var resizedFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                message =>
                    BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4)) == 960 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)) == 540,
                TimeSpan.FromSeconds(3));
            await WriteNativeOverlayEventPipeMessagesAsync(
                $"{pipeName}_events",
                [
                    BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(760, 420)),
                    BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(880, 500)),
                    BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(960, 540))
                ],
                TimeSpan.FromSeconds(2));

            var resizedFrame = await resizedFrameTask;
            AssertNativeOverlayChatFrame(resizedFrame);
            var resizedBounds = GetNativeOverlayAlphaBounds(resizedFrame);
            Assert.True(
                resizedBounds.Height <= initialBounds.Height + 3,
                $"Expected resized replay overlay text to stay within 3px of {initialBounds.Height}px, got {resizedBounds.Height}px.");
            Assert.Equal("reference 960 540", File.ReadAllText($"{positionStatePath}.size"));
            Assert.True(tab.IsNativeReplayOverlayEventHostRunning);
            Assert.Equal(pipeName, tab.NativeReplayOverlayEventHostPipeName);
            Assert.True(tab.IsBehindLive);
            Assert.True(tab.DockedChatMessages.Any(message => message.Message == "native replay resize hello"));

            await tab.DisposeAsync();
        });
    }),
    ("native VLC replay overlay refreshes when replay badge catalog resolves", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_badge_refresh_{Guid.NewGuid():N}";
            var roomId = $"room-{Guid.NewGuid():N}";
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            void Dispatch(Action action)
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.Invoke(action);
                }
            }

            var replay = new ReplaySessionInfo(
                PlatformKind.Twitch,
                "streamer",
                "https://www.twitch.tv/videos/123",
                "123",
                DateTimeOffset.UtcNow.AddHours(-1),
                TimeSpan.FromHours(1),
                true,
                "",
                "best",
                ChatRoomId: roomId);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available([
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(10),
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        "viewer",
                        "native replay badge refresh",
                        DateTimeOffset.UtcNow,
                        "#8AB4F8",
                        [new ChatBadge("subscriber", "1", "Subscriber")],
                        RoomId: roomId,
                        MessageId: "replay-native-badge-refresh-1"))
            ]));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                Dispatch,
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;
            settings.Chat.VlcOverlayFontSize = 18;

            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(settings);

            var initialFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(3));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            AssertNativeOverlayChatFrame(await initialFrameTask);

            var catalogType = typeof(StreamlinkVlcStudio.App.Wpf.Controls.DockedChatMessageTextBlock).Assembly.GetType(
                "StreamlinkVlcStudio.App.Wpf.Chat.DockedChatBadgeCatalog");
            Assert.NotNull(catalogType);
            var sharedCatalog = catalogType!.GetProperty("Shared", BindingFlags.Static | BindingFlags.Public)!.GetValue(null);
            var addTwitchBadge = catalogType.GetMethod("AddTwitchBadge", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(addTwitchBadge);
            Assert.True((bool)addTwitchBadge!.Invoke(
                sharedCatalog,
                [roomId, "subscriber", "1", "Channel Subscriber", "https://static-cdn.jtvnw.net/badges/v1/channel-subscriber/3"])!);

            var refreshedFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                message => BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4)) > 4,
                TimeSpan.FromSeconds(3));
            var catalogChanged = typeof(StreamTabViewModel).GetMethod(
                "OnChatRenderCatalogChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(catalogChanged);
            catalogChanged!.Invoke(tab, [sharedCatalog, EventArgs.Empty]);

            AssertNativeOverlayChatFrame(await refreshedFrameTask);
            await tab.DisposeAsync();
        });
    }),
    ("native VLC replay overlay event host stops when returning live", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_lifetime_{Guid.NewGuid():N}";
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-position-{Guid.NewGuid():N}.txt");
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName,
                NativeOverlayPositionStatePathOverride = positionStatePath
            });
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            void Dispatch(Action action)
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.Invoke(action);
                }
            }

            var replay = new ReplaySessionInfo(
                PlatformKind.Twitch,
                "streamer",
                "https://www.twitch.tv/videos/123",
                "123",
                DateTimeOffset.UtcNow.AddHours(-1),
                TimeSpan.FromHours(1),
                true,
                "",
                "best");
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available([
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(10),
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        "viewer",
                        "native replay lifetime hello",
                        DateTimeOffset.UtcNow,
                        "#8AB4F8",
                        MessageId: "replay-native-lifetime-1"))
            ]));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                Dispatch,
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(settings);

            var initialFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(3));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            AssertNativeOverlayChatFrame(await initialFrameTask);
            await TestWait.UntilAsync(
                () => tab.IsNativeReplayOverlayEventHostRunning,
                TimeSpan.FromSeconds(2));

            await tab.ReturnToLiveAsync();

            Assert.Equal(false, tab.IsReplayMode);
            Assert.Equal(false, tab.IsBehindLive);
            Assert.Equal(false, tab.IsNativeReplayOverlayEventHostRunning);

            await tab.DisposeAsync();
        });
    }),
    ("current-live DVR seek displays captured Twitch chat without VOD comments warning", async () =>
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8",
            "live-dvr-123456789",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best",
            ReplayMediaKind.CurrentLiveDvr);
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("VOD comments ID should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Twitch,
            "streamer",
            "viewer",
            "captured hello",
            startedAt.AddMinutes(10),
            MessageId: "live-captured-1"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.True(chatFactory.Client.Connected);
        Assert.Equal(0, replayChatProvider.CallCount);
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "captured hello"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("VOD comments ID", StringComparison.Ordinal)));

        tab.OutgoingChatText = "should not send";
        await tab.SendChatMessageAsync();
        Assert.Equal(0, chatFactory.Client.SentMessages.Count);

        await tab.DisposeAsync();
    }),
    ("current-live DVR seek before first captured Twitch chat stays quiet", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8",
            "live-dvr-123456789",
            startedAt,
            TimeSpan.FromHours(8),
            true,
            "",
            "best",
            ReplayMediaKind.CurrentLiveDvr);
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("VOD comments ID should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Twitch,
            "streamer",
            "viewer",
            "first captured Twitch DVR chat",
            startedAt.Add(new TimeSpan(7, 17, 18)),
            MessageId: "first-live-dvr-captured-chat"));

        await tab.SeekReplayAsync(new TimeSpan(7, 16, 0));

        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.Equal(0, replayChatProvider.CallCount);
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "first captured Twitch DVR chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("Current-live DVR chat", StringComparison.Ordinal)));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("was not captured by this tab", StringComparison.Ordinal)));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("VOD comments ID", StringComparison.Ordinal)));

        await tab.DisposeAsync();
    }),
    ("current-live DVR native overlay blanks when seeking before first captured Twitch chat", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_live_dvr_empty_{Guid.NewGuid():N}";
            var startedAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var chatFactory = new FakeChatClientFactory();
            var replay = new ReplaySessionInfo(
                PlatformKind.Twitch,
                "streamer",
                "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8",
                "live-dvr-123456789",
                startedAt,
                TimeSpan.FromHours(8),
                true,
                "",
                "best",
                ReplayMediaKind.CurrentLiveDvr);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("VOD comments ID should not be requested."));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                chatFactory,
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;
            tab.SetVideoHandle(new IntPtr(42));

            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.IsReplaySeekEnabled, TimeSpan.FromSeconds(1));
            await TestWait.UntilAsync(() => chatFactory.Client.Connected, TimeSpan.FromSeconds(1));
            chatFactory.Client.Receive(new ChatMessage(
                PlatformKind.Twitch,
                "streamer",
                "viewer",
                "first captured Twitch DVR chat",
                startedAt.Add(new TimeSpan(7, 17, 18)),
                MessageId: "first-live-dvr-native-overlay-captured-chat"));

            var renderedFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(4));
            await tab.SeekReplayAsync(new TimeSpan(7, 17, 30));

            AssertNativeOverlayChatFrame(await renderedFrameTask);
            Assert.True(tab.ChatMessages.Any(message => message.Message == "first captured Twitch DVR chat"));

            var blankFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayTransparentFrame,
                TimeSpan.FromSeconds(4));
            await tab.SeekReplayAsync(new TimeSpan(7, 16, 0));

            AssertNativeOverlayTransparentFrame(await blankFrameTask);
            Assert.Equal(0, replayChatProvider.CallCount);
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message == "first captured Twitch DVR chat"));
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message.Contains("Current-live DVR chat", StringComparison.Ordinal)));
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message.Contains("was not captured by this tab", StringComparison.Ordinal)));

            await tab.DisposeAsync();
        });
    }),
    ("current-live DVR native overlay blanks first empty seek before replay seek completes", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_live_dvr_first_empty_{Guid.NewGuid():N}";
            var seekRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName,
                SeekCompletion = seekRelease.Task
            });
            var chatFactory = new FakeChatClientFactory();
            var replay = new ReplaySessionInfo(
                PlatformKind.Twitch,
                "streamer",
                "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8",
                "live-dvr-123456789",
                startedAt,
                TimeSpan.FromHours(8),
                true,
                "",
                "best",
                ReplayMediaKind.CurrentLiveDvr);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("VOD comments ID should not be requested."));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                chatFactory,
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;
            tab.SetVideoHandle(new IntPtr(42));

            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.IsReplaySeekEnabled, TimeSpan.FromSeconds(1));
            await TestWait.UntilAsync(() => chatFactory.Client.Connected, TimeSpan.FromSeconds(1));
            chatFactory.Client.Receive(new ChatMessage(
                PlatformKind.Twitch,
                "streamer",
                "viewer",
                "first captured Twitch DVR chat",
                startedAt.Add(new TimeSpan(7, 17, 18)),
                MessageId: "first-live-dvr-native-overlay-captured-chat"));

            var blankFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayTransparentFrame,
                TimeSpan.FromSeconds(4));
            var seekTask = tab.SeekReplayAsync(new TimeSpan(7, 16, 0));

            AssertNativeOverlayTransparentFrame(await blankFrameTask);
            Assert.Equal(false, seekTask.IsCompleted);
            seekRelease.SetResult();
            await seekTask;

            Assert.Equal(0, replayChatProvider.CallCount);
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message == "first captured Twitch DVR chat"));
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message.Contains("Current-live DVR chat", StringComparison.Ordinal)));
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message.Contains("was not captured by this tab", StringComparison.Ordinal)));

            await tab.DisposeAsync();
        });
    }),
    ("current-live DVR captures behind-live messages without appending outside replay window", async () =>
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8",
            "live-dvr-123456789",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best",
            ReplayMediaKind.CurrentLiveDvr);
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("unexpected provider call")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Twitch,
            "streamer",
            "viewer",
            "future captured",
            startedAt.AddMinutes(50),
            MessageId: "future-captured"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "future captured"));

        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Twitch,
            "streamer",
            "viewer",
            "window captured",
            startedAt.AddMinutes(9).AddSeconds(50),
            MessageId: "window-captured"));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "window captured"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "future captured"));

        await tab.DisposeAsync();
    }),
    ("Kick live startup does not wait for replay availability lookup", async () =>
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var releaseReplayLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-delayed",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var replayResolver = new BlockingReplayResolver(replay, releaseReplayLookup.Task);
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: replayResolver,
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("unexpected provider call")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings).WaitAsync(TimeSpan.FromSeconds(1));
        await replayResolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(false, tab.IsBusy);
        Assert.Equal(false, tab.IsReplaySeekEnabled);

        releaseReplayLookup.SetResult();
        await TestWait.UntilAsync(
            () => tab.IsReplaySeekEnabled,
            TimeSpan.FromSeconds(1));

        await tab.DisposeAsync();
    }),
    ("Kick seekback uses chat received before replay lookup completed", async () =>
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var releaseReplayLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-delayed",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var replayResolver = new BlockingReplayResolver(replay, releaseReplayLookup.Task);
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: replayResolver,
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings).WaitAsync(TimeSpan.FromSeconds(1));
        await replayResolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(
            () => chatFactory.Client.Connected,
            TimeSpan.FromSeconds(1));

        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "buffered kick captured chat",
            startedAt.AddMinutes(10),
            MessageId: "buffered-kick-captured-chat"));

        releaseReplayLookup.SetResult();
        await TestWait.UntilAsync(
            () => tab.IsReplaySeekEnabled,
            TimeSpan.FromSeconds(1));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.True(chatFactory.Client.Connected);
        Assert.Equal(false, tab.CanSendChatMessages);
        Assert.Equal(0, replayChatProvider.CallCount);
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "buffered kick captured chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("Kick seekback chat", StringComparison.Ordinal)));

        await tab.DisposeAsync();
    }),
    ("Kick seekback waits for in-flight replay availability lookup", async () =>
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var releaseReplayLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-inflight-seek",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var replayResolver = new BlockingReplayResolver(replay, releaseReplayLookup.Task);
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: replayResolver,
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings).WaitAsync(TimeSpan.FromSeconds(1));
        await replayResolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "inflight captured kick chat",
            startedAt.AddMinutes(10),
            MessageId: "inflight-captured-kick-chat"));

        var seekTask = tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await Task.Delay(50);
        Assert.Equal(false, seekTask.IsCompleted);

        releaseReplayLookup.SetResult();
        await seekTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.True(DockedChatMessagesContain(tab, "inflight captured kick chat"));

        await tab.DisposeAsync();
    }),
    ("Kick seekbar seekback backfills older recent chat before captured range warning", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-123",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "first captured after target",
            startedAt.AddMinutes(45).AddSeconds(14),
            MessageId: "first-captured-after-target"));
        var expectedFromTimestamp = startedAt.AddMinutes(44).AddSeconds(15);
        var expectedThroughTimestamp = startedAt.AddMinutes(45);
        var visibleBackfillMessage = new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "older-viewer",
            "older kick backfill for seekbar",
            startedAt.AddMinutes(44).AddSeconds(50),
            MessageId: "older-kick-backfill-for-seekbar");
        chatFactory.Client.BackfillHandler = (client, fromTimestampUtc, throughTimestampUtc, cancellationToken) =>
        {
            if (fromTimestampUtc == expectedFromTimestamp &&
                throughTimestampUtc == expectedThroughTimestamp)
            {
                client.Receive(visibleBackfillMessage);
                return Task.FromResult(new ChatHistoryBackfillResult(
                    Attempted: true,
                    LoadedMessageCount: 1,
                    CoveredRequestedRange: true,
                    CoveredFromTimestampUtc: fromTimestampUtc,
                    CoveredThroughTimestampUtc: throughTimestampUtc,
                    Messages: [visibleBackfillMessage]));
            }

            return Task.FromResult(new ChatHistoryBackfillResult(
                Attempted: true,
                LoadedMessageCount: 0,
                CoveredRequestedRange: false,
                CoveredFromTimestampUtc: null,
                CoveredThroughTimestampUtc: null));
        };

        tab.BeginReplaySeekPreview();
        tab.ReplaySeekSliderValue = TimeSpan.FromMinutes(45).TotalSeconds;
        await tab.CommitReplaySeekPreviewAsync(tab.ReplaySeekSliderValue);

        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "older kick backfill for seekbar"),
            TimeSpan.FromSeconds(1));

        Assert.True(chatFactory.Client.BackfillUntilRequests.Any(request =>
            request == expectedFromTimestamp));
        Assert.True(chatFactory.Client.BackfillRangeRequests.Any(request =>
            request.FromTimestampUtc == expectedFromTimestamp &&
            request.ThroughTimestampUtc == expectedThroughTimestamp));
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick seekback chat before"));
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

        await tab.DisposeAsync();
    }),
    ("Kick seekback does not warn when timestamp backfill verifies empty chat", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var chatFactory = new FakeChatClientFactory();
        chatFactory.Client.BackfillCoveredRequestedRange = true;
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-empty-chat",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        await TestWait.UntilAsync(
            () => chatFactory.Client.BackfillUntilRequests.Any(request =>
                request == startedAt.AddMinutes(9).AddSeconds(15)),
            TimeSpan.FromSeconds(1));
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.Equal(false, tab.DockedChatMessages.Any(message =>
            message.Message.Contains("Kick seekback chat", StringComparison.Ordinal)));
        Assert.Equal(false, tab.DockedChatMessages.Any(message =>
            message.Message.Contains("Kick replay chat should not be requested", StringComparison.Ordinal)));

        await tab.DisposeAsync();
    }),
    ("Kick seekback empty captured window clears stale chat before uncovered backfill notice", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-empty-window",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "stale future captured chat",
            startedAt.AddMinutes(50),
            MessageId: "stale-future-captured-chat"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        Assert.True(DockedChatMessagesContain(tab, "stale future captured chat"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(false, DockedChatMessagesContain(tab, "stale future captured chat"));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContainText(tab, "Kick seekback chat before"),
            TimeSpan.FromSeconds(1));
        Assert.True(DockedChatMessagesContainText(tab, "Kick seekback chat before"));
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

        await tab.DisposeAsync();
    }),
    ("Kick seekbar seekback uses standalone history when chat auto-connect is disabled", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var chatFactory = new FakeChatClientFactory();
        var kickHistoryProvider = new FakeKickChatHistoryProvider();
        kickHistoryProvider.BackfillMessages.Add(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "history-viewer",
            "standalone kick history chat",
            startedAt.AddMinutes(9).AddSeconds(50),
            MessageId: "standalone-kick-history-chat"));
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-standalone-no-auto-chat",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")),
            kickChatHistoryProvider: kickHistoryProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "standalone kick history chat"),
            TimeSpan.FromSeconds(1));
        Assert.Equal(0, chatFactory.Client.ConnectCount);
        Assert.True(kickHistoryProvider.Requests.Any(request =>
            request.FromTimestampUtc == startedAt.AddMinutes(9).AddSeconds(15) &&
            request.ThroughTimestampUtc == startedAt.AddMinutes(10)));
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

        await tab.DisposeAsync();
    }),
    ("Kick seekbar seekback uses standalone history when chat is hidden and stopped", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var chatFactory = new FakeChatClientFactory();
        var kickHistoryProvider = new FakeKickChatHistoryProvider();
        kickHistoryProvider.BackfillMessages.Add(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "history-viewer",
            "hidden chat standalone kick history",
            startedAt.AddMinutes(10),
            MessageId: "hidden-chat-standalone-kick-history"));
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-standalone-hidden-chat",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")),
            kickChatHistoryProvider: kickHistoryProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => chatFactory.Client.Connected,
            TimeSpan.FromSeconds(1));

        tab.IsChatVisible = false;
        await tab.RestartChatAsync(settings);
        await chatFactory.Client.DisconnectAsync();
        Assert.Equal(false, chatFactory.Client.Connected);

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "hidden chat standalone kick history"),
            TimeSpan.FromSeconds(1));

        Assert.True(kickHistoryProvider.Requests.Count > 0);
        Assert.Equal(0, chatFactory.Client.BackfillRangeRequests.Count);
        Assert.Equal(false, chatFactory.Client.Connected);
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

        await tab.DisposeAsync();
    }),
    ("Kick seekback provider future messages do not remain visible after seeking backward", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var kickHistoryProvider = new FakeKickChatHistoryProvider
        {
            FilterMessagesToRequest = false
        };
        kickHistoryProvider.BackfillMessages.Add(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "future-viewer",
            "future provider kick history",
            startedAt.AddMinutes(50),
            MessageId: "future-provider-kick-history"));
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-provider-window-reset",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")),
            kickChatHistoryProvider: kickHistoryProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "future provider kick history"),
            TimeSpan.FromSeconds(1));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await TestWait.UntilAsync(
            () => kickHistoryProvider.Requests.Count >= 2,
            TimeSpan.FromSeconds(1));

        Assert.Equal(false, DockedChatMessagesContain(tab, "future provider kick history"));
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

        await tab.DisposeAsync();
    }),
    ("replay duration ignores impossible stream start timestamp", () =>
    {
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-impossible-start",
            DateTimeOffset.MinValue,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var method = typeof(StreamTabViewModel).GetMethod(
            "GetCurrentReplayDuration",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var duration = (TimeSpan)method!.Invoke(tab, [replay])!;

        Assert.Equal(TimeSpan.FromHours(1), duration);
        return Task.CompletedTask;
    }),
    ("Kick seekback displays captured chat and keeps chat connected read-only", async () =>
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-123",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "captured kick hello",
            startedAt.AddMinutes(10),
            MessageId: "kick-captured-1"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.True(chatFactory.Client.Connected);
        Assert.Equal(false, tab.CanSendChatMessages);
        Assert.Equal(0, replayChatProvider.CallCount);
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "captured kick hello"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("Kick replay chat should not be requested", StringComparison.Ordinal)));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("Kick seekback chat only includes", StringComparison.Ordinal)));

        tab.OutgoingChatText = "should not send";
        await tab.SendChatMessageAsync();
        Assert.Equal(0, chatFactory.Client.SentMessages.Count);

        await tab.DisposeAsync();
    }),
    ("Kick seekback clock advance retains reached chat and loads timestamp chat", async () =>
    {
        TimeSpan? forcedClockPosition = null;
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackClockOverride = engine =>
                (true, new PlaybackClock(forcedClockPosition ?? engine.Position, engine.Duration, engine.Seekable))
        });
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-123",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "initial kick captured chat",
            startedAt.AddMinutes(10),
            MessageId: "initial-kick-captured-chat"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "initial kick captured chat"));
        await TestWait.UntilAsync(
            () => chatFactory.Client.BackfillUntilRequests.Count > 0,
            TimeSpan.FromSeconds(1));

        var initialRequestCount = chatFactory.Client.BackfillUntilRequests.Count;
        chatFactory.Client.BackfillMessages.Add(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "later-viewer",
            "later kick timestamp chat",
            startedAt.AddMinutes(10).AddSeconds(50),
            MessageId: "later-kick-timestamp-chat"));

        MarkReplayClockSeekConfirmed(tab, TimeSpan.FromSeconds(50));
        forcedClockPosition = TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(50));
        await TestWait.UntilAsync(
            () => chatFactory.Client.BackfillUntilRequests.Count > initialRequestCount,
            TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "later kick timestamp chat"),
            TimeSpan.FromSeconds(1));

        Assert.True(DockedChatMessagesContain(tab, "initial kick captured chat"));
        Assert.True(DockedChatMessagesContain(tab, "later kick timestamp chat"));
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

        await tab.DisposeAsync();
    }),
    ("Kick seekback rechecks after partial timestamp backfill and retains reached chat", async () =>
    {
        TimeSpan? forcedClockPosition = null;
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackClockOverride = engine =>
                (true, new PlaybackClock(forcedClockPosition ?? engine.Position, engine.Duration, engine.Seekable))
        });
        var chatFactory = new FakeChatClientFactory();
        chatFactory.Client.BackfillMessages.Add(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "early-viewer",
            "early partial kick timestamp chat",
            startedAt.AddMinutes(9).AddSeconds(16),
            MessageId: "early-partial-kick-timestamp-chat"));
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-partial-backfill",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "early partial kick timestamp chat"),
            TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(
            () => chatFactory.Client.BackfillRangeRequests.Count > 0,
            TimeSpan.FromSeconds(1));

        var initialRequestCount = chatFactory.Client.BackfillRangeRequests.Count;
        chatFactory.Client.BackfillMessages.Add(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "later-viewer",
            "later cursor kick timestamp chat",
            startedAt.AddMinutes(10).AddSeconds(10),
            MessageId: "later-cursor-kick-timestamp-chat"));

        MarkReplayClockSeekConfirmed(tab, TimeSpan.FromSeconds(10));
        forcedClockPosition = TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(10));
        await TestWait.UntilAsync(
            () => chatFactory.Client.BackfillRangeRequests.Count > initialRequestCount,
            TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "later cursor kick timestamp chat"),
            TimeSpan.FromSeconds(1));

        Assert.True(DockedChatMessagesContain(tab, "later cursor kick timestamp chat"));
        Assert.True(DockedChatMessagesContain(tab, "early partial kick timestamp chat"));
        Assert.Equal(0, replayChatProvider.CallCount);
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

        await tab.DisposeAsync();
    }),
    ("Kick seekback supersedes stalled captured backfill with latest offset", async () =>
    {
        TimeSpan? forcedClockPosition = null;
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackClockOverride = engine =>
                (true, new PlaybackClock(forcedClockPosition ?? engine.Position, engine.Duration, engine.Seekable))
        });
        var chatFactory = new FakeChatClientFactory();
        var handlerGate = new object();
        var callCount = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chatFactory.Client.BackfillHandler = async (client, fromTimestampUtc, throughTimestampUtc, cancellationToken) =>
        {
            int currentCall;
            lock (handlerGate)
            {
                callCount++;
                currentCall = callCount;
            }

            if (currentCall == 1)
            {
                firstStarted.SetResult();
                using var registration = cancellationToken.Register(
                    () => firstCancellationRequested.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (currentCall == 2)
            {
                secondStarted.SetResult();
                client.Receive(new ChatMessage(
                    PlatformKind.Kick,
                    "streamer",
                    "newer-viewer",
                    "newer superseded kick seekback chat",
                    startedAt.AddMinutes(12).AddSeconds(30),
                    MessageId: "newer-superseded-kick-seekback-chat"));
                return new ChatHistoryBackfillResult(
                    Attempted: true,
                    LoadedMessageCount: 1,
                    CoveredRequestedRange: true,
                    CoveredFromTimestampUtc: fromTimestampUtc,
                    CoveredThroughTimestampUtc: throughTimestampUtc);
            }

            return new ChatHistoryBackfillResult(
                Attempted: true,
                LoadedMessageCount: 0,
                CoveredRequestedRange: true,
                CoveredFromTimestampUtc: fromTimestampUtc,
                CoveredThroughTimestampUtc: throughTimestampUtc);
        };
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-supersede-stalled-backfill",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        MarkReplayClockSeekConfirmed(tab, TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(30)));
        forcedClockPosition = TimeSpan.FromMinutes(12).Add(TimeSpan.FromSeconds(30));
        await firstCancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "newer superseded kick seekback chat"),
            TimeSpan.FromSeconds(1));

        Assert.True(DockedChatMessagesContain(tab, "newer superseded kick seekback chat"));
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));
        Assert.True(chatFactory.Client.BackfillRangeRequests.Count >= 2);
        Assert.Equal(0, replayChatProvider.CallCount);

        await tab.DisposeAsync();
    }),
    ("Kick seekback stagnant playback clock keeps chat progressing from anchor", async () =>
    {
        TimeSpan? forcedClockPosition = TimeSpan.FromMinutes(10);
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackClockOverride = engine =>
                (true, new PlaybackClock(forcedClockPosition ?? engine.Position, engine.Duration, engine.Seekable))
        });
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-stagnant-clock",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await TestWait.UntilAsync(
            () => chatFactory.Client.BackfillRangeRequests.Count > 0,
            TimeSpan.FromSeconds(1));

        var initialRequestCount = chatFactory.Client.BackfillRangeRequests.Count;
        chatFactory.Client.BackfillMessages.Add(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "later-viewer",
            "anchor progressed kick chat",
            startedAt.AddMinutes(10).AddSeconds(8),
            MessageId: "anchor-progressed-kick-chat"));

        MarkReplayClockSeekConfirmed(tab, TimeSpan.FromSeconds(8));
        InvokeReplayClockUpdate(tab);

        await TestWait.UntilAsync(
            () => chatFactory.Client.BackfillRangeRequests.Count > initialRequestCount,
            TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "anchor progressed kick chat"),
            TimeSpan.FromSeconds(1));

        Assert.True(tab.ReplaySeekValue >= TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(7)).TotalSeconds);
        Assert.True(DockedChatMessagesContain(tab, "anchor progressed kick chat"));
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

        forcedClockPosition = null;
        await tab.DisposeAsync();
    }),
    ("Kick seekback captured chat ignores stale old clock after backward seek", async () =>
    {
        TimeSpan? forcedClockPosition = null;
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackClockOverride = engine =>
                (true, new PlaybackClock(forcedClockPosition ?? engine.Position, engine.Duration, engine.Seekable))
        });
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-123",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("unexpected provider call")));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "early-viewer",
            "early kick captured chat",
            startedAt.AddMinutes(10),
            MessageId: "early-kick-captured-chat"));
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "late-viewer",
            "late kick captured chat",
            startedAt.AddMinutes(50),
            MessageId: "late-kick-captured-chat"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        Assert.True(DockedChatMessagesContain(tab, "late kick captured chat"));
        Assert.Equal(false, DockedChatMessagesContain(tab, "early kick captured chat"));

        forcedClockPosition = TimeSpan.FromMinutes(50);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        Assert.True(DockedChatMessagesContain(tab, "early kick captured chat"));
        Assert.Equal(false, DockedChatMessagesContain(tab, "late kick captured chat"));

        await TestWait.UntilAsync(
            () => tab.ReplaySeekValue > TimeSpan.FromMinutes(10).TotalSeconds,
            TimeSpan.FromSeconds(2));

        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "early kick captured chat") &&
                !DockedChatMessagesContain(tab, "late kick captured chat"),
            TimeSpan.FromSeconds(1));
        Assert.True(DockedChatMessagesContain(tab, "early kick captured chat"));
        Assert.Equal(false, DockedChatMessagesContain(tab, "late kick captured chat"));

        forcedClockPosition = null;
        await tab.DisposeAsync();
    }),
    ("Kick seekback captures behind-live messages without appending outside replay window", async () =>
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-replay-123",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(0, replayChatProvider.CallCount);
        await TestWait.UntilAsync(
            () => tab.DockedChatMessages.Any(message => message.Message.Contains("Kick seekback chat only includes", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(1));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message.Contains("Kick seekback chat only includes", StringComparison.Ordinal)));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("Kick replay chat should not be requested", StringComparison.Ordinal)));

        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "future kick captured",
            startedAt.AddMinutes(50),
            MessageId: "future-kick-captured"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "future kick captured"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "future kick captured"));

        await tab.DisposeAsync();
    }),
    ("current-live DVR promotion polling swaps to real VOD replay chat", async () =>
    {
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var liveReplay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8",
            "live-dvr-123456789",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best",
            ReplayMediaKind.CurrentLiveDvr);
        var promotedReplay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var replayResolver = new FakeReplayResolver(liveReplay, promotedReplay);
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available([
            new ReplayChatMessage(
                TimeSpan.FromMinutes(10),
                new ChatMessage(PlatformKind.Twitch, "streamer", "vod-viewer", "vod chat after promotion", startedAt.AddMinutes(10)))
        ]));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: replayResolver,
            replayChatProvider: replayChatProvider,
            twitchLiveDvrPromotionPollInterval: TimeSpan.FromMilliseconds(20));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        await TestWait.UntilAsync(() => replayResolver.CallCount >= 2, TimeSpan.FromSeconds(1));
        await TestWait.UntilAsync(() => replayChatProvider.CallCount > 0, TimeSpan.FromSeconds(1));

        Assert.Equal("123", replayChatProvider.Requests.Last().ReplayId);
        Assert.Contains("123", tab.ReplaySeekToolTip);
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "vod chat after promotion"));

        await tab.DisposeAsync();
    }),
    ("replay step buttons seek thirty seconds and return to live at the edge", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var replayDuration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            replayDuration,
            true,
            "");
        var replayResolver = new FakeReplayResolver(replay);
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: replayResolver,
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        Assert.Equal(false, tab.RewindReplay30SecondsCommand.CanExecute(null));
        Assert.Equal(false, tab.FastForwardReplay30SecondsCommand.CanExecute(null));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => tab.CanSeekReplay,
            TimeSpan.FromSeconds(1));

        Assert.True(tab.RewindReplay30SecondsCommand.CanExecute(null));
        Assert.True(tab.FastForwardReplay30SecondsCommand.CanExecute(null));
        Assert.Equal(replayDuration.TotalSeconds, tab.ReplaySeekValue);
        await TestWait.UntilAsync(
            () => streamlink.ResolveStreamUrlCount == 1,
            TimeSpan.FromSeconds(1));

        await tab.RewindReplay30SecondsCommand.ExecuteAsync();

        var thirtySecondsBehindLive = replayDuration - TimeSpan.FromSeconds(30);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.Equal(thirtySecondsBehindLive, playbackFactory.Engine!.Position);

        var liveStartCountBeforeFastForward = streamlink.StartCount;

        await tab.FastForwardReplay30SecondsCommand.ExecuteAsync();

        Assert.Equal(liveStartCountBeforeFastForward + 1, streamlink.StartCount);
        Assert.Equal(false, tab.IsReplayMode);
        Assert.Equal(false, tab.IsBehindLive);
        Assert.Equal("Live", tab.ReplayLiveStateText);
        Assert.Equal(replayDuration.TotalSeconds, tab.ReplaySeekValue);
    }),
    ("resuming after pausing while behind live holds the rewound position", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var replayDuration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            replayDuration,
            true,
            "");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => streamlink.ResolveStreamUrlCount == 1,
            TimeSpan.FromSeconds(1));

        tab.ReplaySeekSliderValue = TimeSpan.FromMinutes(25).TotalSeconds;
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(tab.ReplaySeekSliderValue));
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.Equal(TimeSpan.FromMinutes(25), playbackFactory.Engine!.Position);

        // Simulate libVLC snapping a live HLS stream forward to the live edge when it is unpaused.
        playbackFactory.Engine!.ResumeJumpsToPosition = replayDuration;

        await tab.PauseOrResumeAsync();
        Assert.True(playbackFactory.Engine!.Paused);

        var playCountBeforeResume = playbackFactory.Engine!.PlayCount;
        await tab.PauseOrResumeAsync();

        Assert.Equal(false, playbackFactory.Engine!.Paused);
        // Resume must reload the replay media (a fresh player) and seek back, not rely on an in-place
        // seek that libVLC would override with the live edge.
        Assert.True(playbackFactory.Engine!.PlayCount > playCountBeforeResume);
        Assert.Equal(TimeSpan.FromMinutes(25), playbackFactory.Engine!.Position);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.True(Math.Abs(tab.ReplaySeekValue - TimeSpan.FromMinutes(25).TotalSeconds) < 2);

        await tab.DisposeAsync();
    }),
    ("inactive live suspension stops only the player connection and resumes at the live edge", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var transport = new FakeTransportSession();
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            TimeSpan.FromHours(1),
            true,
            "");
        streamlink.StartExternalHttpOverride = (_, _) =>
            Task.FromResult<IStreamTransportSession>(transport);
        var playbackFactory = new FakePlaybackEngineFactory();
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Replay.Enabled = true;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => streamlink.ResolveStreamUrlCount == 1,
            TimeSpan.FromSeconds(1));
        var initialPlayCount = playbackFactory.Engine!.PlayCount;
        var initialStartCount = streamlink.StartCount;
        var initialReplayUrlResolutionCount = streamlink.ResolveStreamUrlCount;

        await tab.PauseForTabSwitchAsync();

        Assert.Equal(PlaybackStatus.Paused, tab.Status);
        Assert.True(tab.PausedByTabSwitch);
        Assert.True(tab.IsLivePlaybackConnectionSuspended);
        Assert.Equal(1, playbackFactory.Engine.StopCount);
        Assert.Equal(0, transport.DisposeCount);

        await tab.ResumeFromTabSwitchAsync();

        Assert.Equal(initialStartCount, streamlink.StartCount);
        Assert.Equal(initialPlayCount + 1, playbackFactory.Engine.PlayCount);
        Assert.Equal(0, playbackFactory.Engine.SeekCount);
        Assert.Equal(transport.PlaybackUri, playbackFactory.Engine.LastPlayedUri);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.Equal(false, tab.PausedByTabSwitch);
        Assert.Equal(false, tab.IsLivePlaybackConnectionSuspended);
        Assert.Equal(false, tab.IsReplayMode);
        Assert.Equal(false, tab.IsBehindLive);
        Assert.Equal(0, transport.DisposeCount);
        Assert.Equal(initialReplayUrlResolutionCount, streamlink.ResolveStreamUrlCount);

        await tab.DisposeAsync();
    }),
    ("inactive live resume does not resolve replay media and reports reconnect failures", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var transport = new FakeTransportSession();
        streamlink.StartExternalHttpOverride = (_, _) =>
            Task.FromResult<IStreamTransportSession>(transport);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlayCompletionOverride = playNumber => playNumber == 2
                ? Task.FromException(new InvalidOperationException("simulated reconnect failure"))
                : Task.CompletedTask
        });
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        settings.Replay.Enabled = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.PauseForTabSwitchAsync();
        await tab.ResumeFromTabSwitchAsync();

        Assert.Equal(PlaybackStatus.Error, tab.Status);
        Assert.Contains("Automatic live resume failed", tab.ErrorMessage);
        Assert.Contains("simulated reconnect failure", tab.ErrorMessage);
        Assert.Equal(1, streamlink.StartCount);
        Assert.Equal(1, playbackFactory.Engine!.PlayCount);
        Assert.Equal(0, streamlink.ResolveStreamUrlCount);

        await tab.DisposeAsync();
    }),
    ("inactive behind-live replay suspension keeps the deliberate replay position", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var replayDuration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            replayDuration,
            true,
            "");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => streamlink.ResolveStreamUrlCount == 1,
            TimeSpan.FromSeconds(1));
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(25));

        Assert.True(tab.IsBehindLive);
        Assert.Equal(TimeSpan.FromMinutes(25), playbackFactory.Engine!.Position);
        var playCountBeforeResume = playbackFactory.Engine.PlayCount;

        await tab.PauseForTabSwitchAsync();

        Assert.Equal(0, playbackFactory.Engine.StopCount);
        Assert.True(playbackFactory.Engine.Paused);
        Assert.Equal(false, tab.IsLivePlaybackConnectionSuspended);

        await tab.ResumeFromTabSwitchAsync();

        Assert.True(playbackFactory.Engine.PlayCount > playCountBeforeResume);
        Assert.True(playbackFactory.Engine.SeekCount > 0);
        Assert.Equal(TimeSpan.FromMinutes(25), playbackFactory.Engine.Position);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        Assert.Equal(1, streamlink.StartCount);

        await tab.DisposeAsync();
    }),
    ("resuming an instant live-edge pause stays at the live edge", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var replayDuration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            replayDuration,
            true,
            "");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => streamlink.ResolveStreamUrlCount == 1,
            TimeSpan.FromSeconds(1));
        Assert.Equal(false, tab.IsReplayMode);

        var playCountBeforePause = playbackFactory.Engine!.PlayCount;
        await tab.PauseOrResumeAsync();
        Assert.True(playbackFactory.Engine!.Paused);

        await tab.PauseOrResumeAsync();

        Assert.Equal(false, playbackFactory.Engine!.Paused);
        Assert.Equal(playCountBeforePause, playbackFactory.Engine.PlayCount);
        Assert.Equal(0, playbackFactory.Engine.StopCount);
        Assert.Equal(false, tab.IsReplayMode);
        Assert.Equal(false, tab.IsBehindLive);

        await tab.DisposeAsync();
    }),
    ("paused replay clock stays frozen while the engine clock keeps advancing", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var replayDuration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            replayDuration,
            true,
            "");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => streamlink.ResolveStreamUrlCount == 1,
            TimeSpan.FromSeconds(1));

        tab.ReplaySeekSliderValue = TimeSpan.FromMinutes(25).TotalSeconds;
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(tab.ReplaySeekSliderValue));
        Assert.True(tab.IsBehindLive);
        Assert.Equal("25:00", tab.ReplayElapsedText);

        await tab.PauseOrResumeAsync();
        Assert.True(playbackFactory.Engine!.Paused);

        // Simulate a live stream whose engine clock keeps ticking toward the live edge while paused.
        var pausedAtUtc = DateTimeOffset.UtcNow;
        playbackFactory.Engine!.PlaybackClockOverride = engine =>
            (true, new PlaybackClock(
                TimeSpan.FromMinutes(25) + (DateTimeOffset.UtcNow - pausedAtUtc),
                engine.Duration,
                true));

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.Equal("25:00", tab.ReplayElapsedText);
        Assert.True(
            tab.ReplaySeekValue <= TimeSpan.FromMinutes(25).TotalSeconds + 1,
            $"Paused replay clock advanced to {tab.ReplaySeekValue}.");

        await tab.DisposeAsync();
    }),
    ("replay seek preview movement does not mutate committed clock until commit", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var replayDuration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            replayDuration,
            true,
            "");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => tab.CanSeekReplay,
            TimeSpan.FromSeconds(1));
        Assert.Equal(replayDuration.TotalSeconds, tab.ReplaySeekValue);

        tab.BeginReplaySeekPreview(tab.ReplaySeekSliderValue);
        tab.PreviewReplaySeek(TimeSpan.FromMinutes(5).TotalSeconds);
        tab.PreviewReplaySeek(TimeSpan.FromMinutes(15).TotalSeconds);
        tab.PreviewReplaySeek(TimeSpan.FromMinutes(25).TotalSeconds);

        Assert.True(tab.IsReplaySeekPreviewActive);
        Assert.Equal(replayDuration.TotalSeconds, tab.ReplaySeekValue);
        Assert.Equal(TimeSpan.FromMinutes(25).TotalSeconds, tab.ReplaySeekSliderValue);
        Assert.Equal("25:00", tab.ReplayElapsedText);

        await tab.CommitReplaySeekPreviewAsync(tab.ReplaySeekSliderValue);

        Assert.Equal(false, tab.IsReplaySeekPreviewActive);
        Assert.Equal(TimeSpan.FromMinutes(25), playbackFactory.Engine!.Position);
        Assert.Equal(TimeSpan.FromMinutes(25).TotalSeconds, tab.ReplaySeekValue);
        Assert.Equal(TimeSpan.FromMinutes(25).TotalSeconds, tab.ReplaySeekSliderValue);

        await tab.DisposeAsync();
    }),
    ("replay keyboard slider commit seeks from final slider value", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var replayDuration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            replayDuration,
            true,
            "");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        tab.ReplaySeekSliderValue = TimeSpan.FromMinutes(40).TotalSeconds;
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(tab.ReplaySeekSliderValue));

        Assert.Equal(TimeSpan.FromMinutes(40), playbackFactory.Engine!.Position);
        Assert.Equal(TimeSpan.FromMinutes(40).TotalSeconds, tab.ReplaySeekValue);
        Assert.Equal(TimeSpan.FromMinutes(40).TotalSeconds, tab.ReplaySeekSliderValue);

        await tab.DisposeAsync();
    }),
    ("replay seek preview is not overwritten by replay clock polling", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var replayDuration = TimeSpan.FromHours(1);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            null,
            replayDuration,
            true,
            "");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => tab.CanSeekReplay,
            TimeSpan.FromSeconds(1));
        Assert.Equal(replayDuration.TotalSeconds, tab.ReplaySeekValue);

        tab.BeginReplaySeekPreview();
        tab.ReplaySeekSliderValue = TimeSpan.FromMinutes(10).TotalSeconds;

        var updateClock = typeof(StreamTabViewModel).GetMethod(
            "UpdateReplayClock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(updateClock);
        updateClock!.Invoke(tab, []);

        Assert.Equal(replayDuration.TotalSeconds, tab.ReplaySeekValue);
        Assert.Equal(TimeSpan.FromMinutes(10).TotalSeconds, tab.ReplaySeekSliderValue);
        Assert.Equal("10:00", tab.ReplayElapsedText);

        tab.CancelReplaySeekPreview();
        updateClock.Invoke(tab, []);

        Assert.Equal(replayDuration.TotalSeconds, tab.ReplaySeekValue);
        Assert.Equal(replayDuration.TotalSeconds, tab.ReplaySeekSliderValue);

        await tab.DisposeAsync();
    }),
    ("does not load Twitch replay chat for current live DVR ids", async () =>
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[]""", Encoding.UTF8, "application/json")
            };
        }));
        var provider = new ReplayChatProvider(httpClient);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8",
            "live-dvr-123456789",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            true,
            "",
            "best");

        var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromMinutes(10));

        Assert.Equal(false, result.IsAvailable);
        Assert.Contains("VOD comments ID", result.UnavailableReason);
        Assert.Equal(0, requestCount);
    }),
    ("parses Twitch IRC PRIVMSG", () =>
    {
        var raw = "@badge-info=;badges=broadcaster/1;color=#1E90FF;display-name=Streamer;id=twitch-message-1 :streamer!streamer@streamer.tmi.twitch.tv PRIVMSG #streamer :hello chat";
        var message = TwitchIrcParser.TryParsePrivMsg(raw, "streamer");
        Assert.NotNull(message);
        Assert.Equal("Streamer", message!.Username);
        Assert.Equal("hello chat", message.Message);
        Assert.Equal("twitch-message-1", message.MessageId);
        Assert.Equal(PlatformKind.Twitch, message.Platform);
        Assert.Equal(TimeSpan.Zero, message.Timestamp.Offset);
        Assert.NotNull(message.Badges);
        Assert.Equal("broadcaster", message.Badges![0].Id);
        Assert.Equal("Broadcaster", message.Badges[0].Title);

        var lowercaseCommand = TwitchIrcParser.TryParsePrivMsg(
            raw.Replace(" PRIVMSG ", " privmsg ", StringComparison.Ordinal),
            "streamer");
        Assert.NotNull(lowercaseCommand);
        Assert.Equal("hello chat", lowercaseCommand!.Message);
        return Task.CompletedTask;
    }),
    ("parses Twitch IRC tmi sent timestamp", () =>
    {
        var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(1780344600123);
        var raw = "@badge-info=;badges=;color=#1E90FF;display-name=Viewer;id=twitch-message-1;tmi-sent-ts=1780344600123 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #streamer :timestamped";
        var message = TwitchIrcParser.TryParsePrivMsg(raw, "streamer");

        Assert.NotNull(message);
        Assert.Equal(sentAt, message!.Timestamp);
        return Task.CompletedTask;
    }),
    ("parses Twitch moderator and Prime badges", () =>
    {
        var raw = "@badge-info=;badges=moderator/1,premium/1;color=#1E90FF;display-name=ModPrime;mod=1;user-type=mod :modprime!modprime@modprime.tmi.twitch.tv PRIVMSG #streamer :hello chat";
        var message = TwitchIrcParser.TryParsePrivMsg(raw, "streamer");
        Assert.NotNull(message);
        Assert.NotNull(message!.Badges);
        Assert.Equal(2, message.Badges!.Count);
        Assert.Equal("moderator", message.Badges[0].Id);
        Assert.Equal("Moderator", message.Badges[0].Title);
        Assert.Equal("premium", message.Badges[1].Id);
        Assert.Equal("Prime Gaming", message.Badges[1].Title);
        return Task.CompletedTask;
    }),
    ("parses Twitch IRC emotes tag", () =>
    {
        var raw = "@badge-info=;badges=;color=#1E90FF;display-name=Streamer;emotes=25:0-4;room-id=12345 :streamer!streamer@streamer.tmi.twitch.tv PRIVMSG #streamer :Kappa hello";
        var message = TwitchIrcParser.TryParsePrivMsg(raw, "streamer");
        Assert.NotNull(message);
        Assert.Equal("12345", message!.RoomId);
        Assert.NotNull(message.Emotes);
        Assert.Equal(1, message.Emotes!.Count);
        Assert.Equal("Kappa", message.Emotes[0].Code);
        Assert.Equal(0, message.Emotes[0].StartIndex);
        Assert.Equal(5, message.Emotes[0].EndIndex);
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/25/static/light/2.0", message.Emotes[0].ImageUrl);
        return Task.CompletedTask;
    }),
    ("parses Twitch IRC static subscriber emote with canonical CDN URL", () =>
    {
        var raw = "@badge-info=;badges=subscriber/1;color=#1E90FF;display-name=Viewer;emotes=emotesv2_4691b27f1e1742c892ea1d3267dc5ea0:0-16;room-id=412132764 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #playapex :apxlgndsHifriends";
        var message = TwitchIrcParser.TryParsePrivMsg(raw, "playapex");
        Assert.NotNull(message);
        Assert.Equal("playapex", message!.Channel);
        Assert.Equal("412132764", message.RoomId);
        Assert.NotNull(message.Emotes);
        Assert.Equal(1, message.Emotes!.Count);
        Assert.Equal("apxlgndsHifriends", message.Emotes[0].Code);
        Assert.Equal(0, message.Emotes[0].StartIndex);
        Assert.Equal(17, message.Emotes[0].EndIndex);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_4691b27f1e1742c892ea1d3267dc5ea0/static/light/2.0",
            message.Emotes[0].ImageUrl);
        return Task.CompletedTask;
    }),
    ("emote candidates prefer high-resolution animated CDN representations", () =>
    {
        var method = typeof(AnimatedEmoteImage).GetMethod(
            "GetImageUrlCandidates",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var staticUrl = new Uri(
            "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_91417441b2f24a5299ed7b2a1ce8e7b9/static/light/2.0");
        var candidates = ((IEnumerable<Uri>)method!.Invoke(null, [staticUrl])!).ToArray();
        Assert.Equal(
            "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_91417441b2f24a5299ed7b2a1ce8e7b9/animated/light/3.0",
            candidates[0].ToString());
        Assert.Equal(
            "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_91417441b2f24a5299ed7b2a1ce8e7b9/static/light/3.0",
            candidates[1].ToString());
        Assert.Equal(
            "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_91417441b2f24a5299ed7b2a1ce8e7b9/animated/light/2.0",
            candidates[2].ToString());
        Assert.Equal(staticUrl.ToString(), candidates[3].ToString());
        return Task.CompletedTask;
    }),
    ("7TV emote candidates prefer the 3x CDN representation", () =>
    {
        var method = typeof(AnimatedEmoteImage).GetMethod(
            "GetImageUrlCandidates",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var catalogUrl = new Uri(
            "https://cdn.7tv.app/emote/01GKFRT59000047SF1NR3YD3WA/2x.gif");
        var candidates = ((IEnumerable<Uri>)method!.Invoke(null, [catalogUrl])!).ToArray();
        Assert.Equal(
            "https://cdn.7tv.app/emote/01GKFRT59000047SF1NR3YD3WA/3x.gif",
            candidates[0].ToString());
        Assert.Equal(catalogUrl.ToString(), candidates[1].ToString());
        return Task.CompletedTask;
    }),
    ("parses Twitch IRC multilingual message and Unicode emote offsets", () =>
    {
        var emoji = char.ConvertFromUtf32(0x1F602);
        var body = "\u65E5\u672C\u8A9E " + emoji + " Kappa \u0645\u0631\u062D\u0628\u0627";
        var raw = $"@badge-info=;badges=;color=#1E90FF;display-name=\u8996\u8074\u8005;emotes=25:6-10;room-id=12345 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #streamer :{body}";
        var message = TwitchIrcParser.TryParsePrivMsg(raw, "streamer");

        Assert.NotNull(message);
        Assert.Equal("\u8996\u8074\u8005", message!.Username);
        Assert.Equal(body, message.Message);
        Assert.NotNull(message.Emotes);
        Assert.Equal(1, message.Emotes!.Count);
        Assert.Equal("Kappa", message.Emotes[0].Code);
        Assert.Equal(body.IndexOf("Kappa", StringComparison.Ordinal), message.Emotes[0].StartIndex);
        Assert.Equal(body.IndexOf("Kappa", StringComparison.Ordinal) + "Kappa".Length, message.Emotes[0].EndIndex);
        return Task.CompletedTask;
    }),
    ("unescapes Twitch IRC literal backslashes in tags", () =>
    {
        var raw = "@badge-info=;badges=;color=;display-name=Name\\\\sTag :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #streamer :hello chat";
        var message = TwitchIrcParser.TryParsePrivMsg(raw, "streamer");
        Assert.NotNull(message);
        Assert.Equal(@"Name\sTag", message!.Username);
        return Task.CompletedTask;
    }),
    ("drops invalid and trailing Twitch IRC tag escape backslashes", () =>
    {
        var invalidEscape = TwitchIrcParser.TryParsePrivMsg(
            "@display-name=Name\\qTag :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #streamer :hello chat",
            "streamer");
        var trailingBackslash = TwitchIrcParser.TryParsePrivMsg(
            "@display-name=Name\\ :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #streamer :hello chat",
            "streamer");

        Assert.NotNull(invalidEscape);
        Assert.Equal("NameqTag", invalidEscape!.Username);
        Assert.NotNull(trailingBackslash);
        Assert.Equal("Name", trailingBackslash!.Username);
        return Task.CompletedTask;
    }),
    ("rejects lines that only mention Twitch PRIVMSG outside the command", () =>
    {
        Assert.Equal<ChatMessage?>(null, TwitchIrcParser.TryParsePrivMsg(null, "streamer"));
        Assert.Equal<ChatMessage?>(null, TwitchIrcParser.TryParsePrivMsg("   ", "streamer"));
        Assert.Equal<ChatMessage?>(
            null,
            TwitchIrcParser.TryParsePrivMsg(
                ":tmi.twitch.tv NOTICE * :body mentions PRIVMSG #streamer :fake chat",
                "streamer"));
        Assert.Equal<ChatMessage?>(
            null,
            TwitchIrcParser.TryParsePrivMsg(
                ":viewer!viewer@viewer.tmi.twitch.tv PRIVMSGX #streamer :fake chat",
                "streamer"));
        return Task.CompletedTask;
    }),
    ("parses Kick Pusher chat message", () =>
    {
        var payload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"id\":\"kick-message-1\",\"content\":\"hello kick\",\"sender\":{\"username\":\"viewer\",\"identity\":{\"color\":\"#55AAFF\",\"badges\":[{\"type\":\"moderator\",\"text\":\"Moderator\",\"count\":0},{\"type\":\"og\",\"text\":\"OG\",\"count\":1}]}}}"
        }
        """;
        var message = KickPusherParser.TryParse(payload, "channel");
        Assert.NotNull(message);
        Assert.Equal("viewer", message!.Username);
        Assert.Equal("hello kick", message.Message);
        Assert.Equal("kick-message-1", message.MessageId);
        Assert.Equal(PlatformKind.Kick, message.Platform);
        Assert.Equal(TimeSpan.Zero, message.Timestamp.Offset);
        Assert.NotNull(message.Badges);
        Assert.Equal(2, message.Badges!.Count);
        Assert.Equal("moderator", message.Badges[0].Id);
        Assert.Equal("Moderator", message.Badges[0].Title);
        Assert.Equal("og", message.Badges[1].Id);
        Assert.Equal("OG", message.Badges[1].Title);
        return Task.CompletedTask;
    }),
    ("parses Kick Pusher server timestamps", () =>
    {
        var createdAt = new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero);
        var createdAtPayload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"id\":\"kick-created-at\",\"content\":\"timestamped\",\"created_at\":\"2026-06-01T20:04:05Z\",\"sender\":{\"username\":\"viewer\"}}"
        }
        """;
        var createdAtMessage = KickPusherParser.TryParse(createdAtPayload, "channel");
        Assert.NotNull(createdAtMessage);
        Assert.Equal(createdAt, createdAtMessage!.Timestamp);

        var unixTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1780344600123);
        var unixPayload = """
        {
          "event": "App\\Events\\ChatMessageEvent",
          "data": "{\"id\":\"kick-created-at-unix\",\"content\":\"timestamped unix\",\"createdAt\":1780344600123,\"sender\":{\"username\":\"viewer\"}}"
        }
        """;
        var unixMessage = KickPusherParser.TryParse(unixPayload, "channel");
        Assert.NotNull(unixMessage);
        Assert.Equal(unixTimestamp, unixMessage!.Timestamp);
        return Task.CompletedTask;
    }),
    ("parses official Kick chat webhook payload", () =>
    {
        var body = """
        {
          "message_id": "official-kick-message-1",
          "broadcaster": {
            "user_id": 123456789,
            "username": "Broadcaster",
            "channel_slug": "streamer"
          },
          "sender": {
            "user_id": 987654321,
            "username": "viewer",
            "channel_slug": "viewer",
            "identity": {
              "username_color": "#FF5733",
              "badges": [
                { "text": "Moderator", "type": "moderator" },
                { "text": "Subscriber", "type": "subscriber", "count": 3 }
              ]
            }
          },
          "content": "official hello [emote:4148074:HYPERCLAP]",
          "created_at": "2026-06-01T20:04:05Z"
        }
        """;

        var parsed = KickOfficialChatWebhookParser.TryParseChatMessage(body, out var message, out var error);

        Assert.True(parsed, error);
        Assert.Equal(PlatformKind.Kick, message.Platform);
        Assert.Equal("streamer", message.Channel);
        Assert.Equal("viewer", message.Username);
        Assert.Equal("official hello [emote:4148074:HYPERCLAP]", message.Message);
        Assert.Equal("official-kick-message-1", message.MessageId);
        Assert.Equal("#FF5733", message.Color);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 4, 5, TimeSpan.Zero), message.Timestamp);
        Assert.Equal("moderator", message.Badges![0].Id);
        Assert.Equal("subscriber", message.Badges[1].Id);
        Assert.Equal("3", message.Badges[1].Version);
        Assert.Equal("HYPERCLAP", message.Emotes![0].Code);
        return Task.CompletedTask;
    }),
    ("loads Kick replay chat from official webhook cache", async () =>
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-official-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var store = new KickOfficialChatReplayStore(cacheDirectory);
            await store.AppendAsync(new ChatMessage(
                PlatformKind.Kick,
                "streamer",
                "viewer",
                "official cached replay chat",
                startedAt.AddMinutes(2),
                MessageId: "official-cached-replay-chat"));
            await store.AppendAsync(new ChatMessage(
                PlatformKind.Kick,
                "streamer",
                "viewer",
                "outside requested window",
                startedAt.AddMinutes(20),
                MessageId: "outside-requested-window"));

            var provider = new ReplayChatProvider(
                new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Kick official cache should not call HTTP."))),
                store);
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "streamer",
                "https://vod.kick.example/index.m3u8",
                "kick-vod-1",
                startedAt,
                TimeSpan.FromHours(1),
                true,
                "");

            var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromMinutes(2));

            Assert.True(result.IsAvailable, result.UnavailableReason);
            Assert.Equal(TimeSpan.FromMinutes(1), result.LoadedFromOffset);
            Assert.Equal(TimeSpan.FromMinutes(6), result.LoadedThroughOffset);
            var message = result.Messages.Single();
            Assert.Equal(TimeSpan.FromMinutes(2), message.Offset);
            Assert.Equal("official cached replay chat", message.Message.Message);
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }),
    ("keeps official Kick replay cache paths inside the configured root", async () =>
    {
        var parentDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-cache-path-" + Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(parentDirectory, "cache");
        try
        {
            var timestamp = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var store = new KickOfficialChatReplayStore(cacheDirectory);
            await store.AppendAsync(new ChatMessage(
                PlatformKind.Kick,
                "..",
                "viewer",
                "contained message",
                timestamp,
                MessageId: "contained-message"));

            var files = Directory.GetFiles(cacheDirectory, "*.jsonl", SearchOption.AllDirectories);
            Assert.Equal(1, files.Length);
            var rootPrefix = Path.GetFullPath(cacheDirectory) + Path.DirectorySeparatorChar;
            Assert.True(Path.GetFullPath(files[0]).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(false, File.Exists(Path.Combine(parentDirectory, "20260601.jsonl")));
        }
        finally
        {
            if (Directory.Exists(parentDirectory))
            {
                Directory.Delete(parentDirectory, recursive: true);
            }
        }
    }),
    ("serializes concurrent official Kick replay cache appends", async () =>
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-cache-concurrent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var store = new KickOfficialChatReplayStore(cacheDirectory);
            var writes = Enumerable.Range(0, 200)
                .Select(index => store.AppendAsync(new ChatMessage(
                    PlatformKind.Kick,
                    "streamer",
                    $"viewer-{index}",
                    $"message-{index}",
                    timestamp,
                    MessageId: $"concurrent-{index}")));

            await Task.WhenAll(writes);

            var result = await store.ReadMessagesAsync(
                "streamer",
                timestamp.Subtract(TimeSpan.FromSeconds(1)),
                timestamp.AddSeconds(1));
            Assert.Equal(200, result.Messages.Count);
            Assert.Equal(200, result.Messages.Select(message => message.MessageId).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }),
    ("loads Kick replay chat from timestamp messages endpoint", async () =>
    {
        var requests = new List<Uri>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            Assert.Equal("kick.com", request.RequestUri!.Host);
            Assert.Equal(new Uri("https://kick.com/streamer"), request.Headers.Referrer);

            if (request.RequestUri.AbsolutePath == "/api/v2/channels/streamer")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":668,"chatroom":{"id":668}}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/api/v2/channels/668/messages", request.RequestUri.AbsolutePath);

            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var body = query.Contains("2026-06-01T20:00:00.000Z", StringComparison.Ordinal)
                ? """
                {
                  "status": {"error": false, "code": 200, "message": "SUCCESS"},
                  "data": {
                    "messages": [
                      {
                        "id": "kick-replay-5",
                        "content": "timestamp replay chat [emote:4148074:HYPERCLAP]",
                        "created_at": "2026-06-01T20:00:05Z",
                        "sender": {
                          "username": "ViewerOne",
                          "identity": {
                            "color": "#55AAFF",
                            "badges": [{"type": "subscriber", "text": "Subscriber", "count": 3}]
                          }
                        }
                      },
                      {
                        "id": "kick-replay-10",
                        "content": "second replay chat",
                        "created_at": "2026-06-01T20:00:10Z",
                        "sender": {"username": "ViewerTwo"}
                      }
                    ],
                    "pinned_message": null
                  }
                }
                """
                : """{"data":{"messages":[],"pinned_message":null}}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));
        var provider = new ReplayChatProvider(httpClient);
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://vod.kick.example/index.m3u8",
            "kick-vod-1",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(20),
            true,
            "",
            ChatRoomId: "668");

        var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromSeconds(5));

        Assert.True(result.IsAvailable, result.UnavailableReason);
        Assert.Equal(TimeSpan.Zero, result.LoadedFromOffset);
        Assert.Equal(TimeSpan.FromSeconds(10), result.LoadedThroughOffset);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Messages[0].Offset);
        Assert.Equal("ViewerOne", result.Messages[0].Message.Username);
        Assert.Equal("timestamp replay chat [emote:4148074:HYPERCLAP]", result.Messages[0].Message.Message);
        Assert.Equal("668", result.Messages[0].Message.RoomId);
        Assert.Equal("kick-replay-5", result.Messages[0].Message.MessageId);
        Assert.Equal("subscriber", result.Messages[0].Message.Badges![0].Id);
        Assert.Equal("HYPERCLAP", result.Messages[0].Message.Emotes![0].Code);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Messages[1].Offset);
        Assert.Equal("second replay chat", result.Messages[1].Message.Message);
        Assert.Equal(2, requests.Count);
        Assert.True(requests.Any(uri => Uri.UnescapeDataString(uri.Query).Contains("2026-06-01T20:00:00.000Z", StringComparison.Ordinal)));
    }),
    ("Kick VOD timestamp replay chat starts at visible window and preserves partial coverage", async () =>
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-visible-partial-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var requests = new List<Uri>();
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                requests.Add(request.RequestUri!);
                if (request.RequestUri!.AbsolutePath == "/api/v2/channels/streamer")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"id":123,"chatroom":{"id":668}}""", Encoding.UTF8, "application/json")
                    };
                }

                Assert.Equal("/api/v2/channels/123/messages", request.RequestUri.AbsolutePath);
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var body = query.Contains("2026-06-01T20:00:15.000Z", StringComparison.Ordinal)
                    ? """
                    {
                      "data": {
                        "messages": [
                          {
                            "id": "kick-visible-partial",
                            "content": "visible partial replay chat",
                            "created_at": "2026-06-01T20:00:25Z",
                            "sender": { "username": "VisibleViewer" }
                          }
                        ]
                      }
                    }
                    """
                    : """{"data":{"messages":[]}}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }));
            var provider = new ReplayChatProvider(httpClient, new KickOfficialChatReplayStore(cacheDirectory));
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "streamer",
                "https://vod.kick.example/index.m3u8",
                "kick-vod-1",
                startedAt,
                TimeSpan.FromMinutes(10),
                true,
                "",
                ChatRoomId: "123");

            var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromSeconds(60));

            Assert.True(result.IsAvailable, result.UnavailableReason);
            Assert.Equal(TimeSpan.FromSeconds(15), result.LoadedFromOffset);
            Assert.Equal(TimeSpan.FromSeconds(25), result.LoadedThroughOffset);
            var message = result.Messages.Single();
            Assert.Equal(TimeSpan.FromSeconds(25), message.Offset);
            Assert.Equal("visible partial replay chat", message.Message.Message);
            Assert.SequenceEqual(
                new[] { "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A00%3A15.000Z" },
                requests
                    .Where(uri => uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                    .Select(uri => uri.AbsoluteUri)
                    .ToArray());
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }),
    ("loads Kick replay chat through curl fallback when timestamp HttpClient is forbidden", async () =>
    {
        var previousCurl = Environment.GetEnvironmentVariable("STREAMLINK_KICK_CURL");
        var curlDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-replay-curl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(curlDirectory);
            var curlPath = Path.Combine(curlDirectory, "curl.cmd");
            await File.WriteAllTextAsync(
                curlPath,
                """
                @echo off
                echo {"data":{"messages":[{"id":"kick-curl-replay","content":"curl fallback replay chat","created_at":"2026-06-01T20:00:05Z","sender":{"username":"CurlViewer"}}]}}
                """);
            Environment.SetEnvironmentVariable("STREAMLINK_KICK_CURL", curlPath);

            var requests = new List<Uri>();
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                requests.Add(request.RequestUri!);
                if (request.RequestUri!.AbsolutePath == "/api/v2/channels/streamer")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"id":123,"chatroom":{"id":668}}""", Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"message":"blocked"}""", Encoding.UTF8, "application/json")
                };
            }));
            var provider = new ReplayChatProvider(httpClient, new MemoryLogger());
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "streamer",
                "https://vod.kick.example/index.m3u8",
                "kick-vod-1",
                new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
                TimeSpan.FromSeconds(20),
                true,
                "",
                ChatRoomId: "123");

            var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromSeconds(5));

            Assert.True(result.IsAvailable, result.UnavailableReason);
            var message = result.Messages.Single();
            Assert.Equal(TimeSpan.FromSeconds(5), message.Offset);
            Assert.Equal("CurlViewer", message.Message.Username);
            Assert.Equal("curl fallback replay chat", message.Message.Message);
            Assert.Equal("kick-curl-replay", message.Message.MessageId);
            Assert.Equal("668", message.Message.RoomId);
            Assert.True(requests.Any(uri =>
                uri.AbsolutePath == "/api/v2/channels/123/messages" &&
                Uri.UnescapeDataString(uri.Query).Contains("2026-06-01T20:00:00.000Z", StringComparison.Ordinal)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("STREAMLINK_KICK_CURL", previousCurl);
            if (Directory.Exists(curlDirectory))
            {
                Directory.Delete(curlDirectory, recursive: true);
            }
        }
    }),
    ("loads Kick replay chat from chatroom id after empty channel id page", async () =>
    {
        var requests = new List<Uri>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath == "/api/v2/channels/streamer")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":123,"chatroom":{"id":668}}""", Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath == "/api/v2/channels/123/messages")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"messages":[]}}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/api/v2/channels/668/messages", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "messages": [
                      {
                        "id": "kick-chatroom-replay",
                        "content": "chatroom id replay chat",
                        "created_at": "2026-06-01T20:00:10Z",
                        "sender": { "username": "ChatroomViewer" }
                      }
                    ]
                  }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var provider = new ReplayChatProvider(httpClient);
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://vod.kick.example/index.m3u8",
            "kick-vod-1",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(20),
            true,
            "",
            ChatRoomId: "123");

        var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromSeconds(5));

        Assert.True(result.IsAvailable, result.UnavailableReason);
        var message = result.Messages.Single();
        Assert.Equal(TimeSpan.FromSeconds(10), message.Offset);
        Assert.Equal("ChatroomViewer", message.Message.Username);
        Assert.Equal("chatroom id replay chat", message.Message.Message);
        Assert.Equal("668", message.Message.RoomId);
        Assert.SequenceEqual(
            new[]
            {
                "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A00%3A00.000Z",
                "https://kick.com/api/v2/channels/668/messages?start_time=2026-06-01T20%3A00%3A00.000Z"
            },
            requests
                .Where(uri => uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                .Select(uri => uri.AbsoluteUri)
                .ToArray());
    }),
    ("pages explicit Kick VOD replay chat through timestamp cursors", async () =>
    {
        var requests = new List<Uri>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath == "/api/v2/channels/streamer")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":123,"chatroom":{"id":668}}""", Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Query.Contains("cursor=cursor-1", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": {
                        "messages": [
                          {
                            "id": "kick-replay-cursor-2",
                            "content": "cursor replay page two",
                            "created_at": "2026-06-01T20:00:20Z",
                            "sender": { "username": "CursorTwo" }
                          }
                        ]
                      }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "messages": [
                      {
                        "id": "kick-replay-cursor-1",
                        "content": "cursor replay page one",
                        "created_at": "2026-06-01T20:00:05Z",
                        "sender": { "username": "CursorOne" }
                      }
                    ],
                    "cursor": "cursor-1"
                  }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var provider = new ReplayChatProvider(httpClient);
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://vod.kick.example/index.m3u8",
            "kick-vod-1",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(20),
            true,
            "",
            ChatRoomId: "123");

        var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromSeconds(5));

        Assert.True(result.IsAvailable, result.UnavailableReason);
        Assert.SequenceEqual(
            new[] { "cursor replay page one", "cursor replay page two" },
            result.Messages.Select(message => message.Message.Message).ToArray());
        Assert.SequenceEqual(
            new[]
            {
                "https://kick.com/api/v2/channels/123/messages?start_time=2026-06-01T20%3A00%3A00.000Z",
                "https://kick.com/api/v2/channels/123/messages?cursor=cursor-1"
            },
            requests
                .Where(uri => uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                .Select(uri => uri.AbsoluteUri)
                .ToArray());
    }),
    ("uses Kick webhook cache when timestamp messages endpoint is empty", async () =>
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-empty-direct-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var store = new KickOfficialChatReplayStore(cacheDirectory);
            await store.AppendAsync(new ChatMessage(
                PlatformKind.Kick,
                "streamer",
                "cached",
                "webhook fallback replay chat",
                startedAt.AddSeconds(5),
                MessageId: "webhook-fallback-replay-chat"));
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                var body = request.RequestUri!.AbsolutePath == "/api/v2/channels/streamer"
                    ? """{"id":668,"chatroom":{"id":668}}"""
                    : """{"data":{"messages":[]}}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }));
            var provider = new ReplayChatProvider(httpClient, store);
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "streamer",
                "https://vod.kick.example/index.m3u8",
                "kick-vod-1",
                startedAt,
                TimeSpan.FromSeconds(20),
                true,
                "",
                ChatRoomId: "668");

            var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromSeconds(5));

            Assert.True(result.IsAvailable, result.UnavailableReason);
            Assert.Equal("webhook fallback replay chat", result.Messages.Single().Message.Message);
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }),
    ("reports missing Kick official webhook cache", async () =>
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "svs-empty-kick-official-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new ReplayChatProvider(
                new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Kick official cache should not call HTTP."))),
                new KickOfficialChatReplayStore(cacheDirectory));
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "streamer",
                "https://vod.kick.example/index.m3u8",
                "kick-vod-1",
                new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
                TimeSpan.FromHours(1),
                true,
                "");

            var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.Zero);

            Assert.Equal(false, result.IsAvailable);
            Assert.Contains("No official Kick webhook chat cache", result.UnavailableReason);
            Assert.Contains("streamer", result.UnavailableReason);
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }),
    ("reports missing Kick VOD start time for official replay chat", async () =>
    {
        var provider = new ReplayChatProvider(
            new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("Kick official cache should not call HTTP."))),
            new KickOfficialChatReplayStore(Path.Combine(Path.GetTempPath(), "svs-unused-kick-official-chat-" + Guid.NewGuid().ToString("N"))));
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://vod.kick.example/index.m3u8",
            "kick-vod-1",
            null,
            TimeSpan.FromHours(1),
            true,
            "");

        var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.Zero);

        Assert.Equal(false, result.IsAvailable);
        Assert.Contains("VOD start time", result.UnavailableReason);
    }),
    ("official Kick webhook server stores only signed chat messages", async () =>
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-webhook-server-" + Guid.NewGuid().ToString("N"));
        using var rsa = RSA.Create(2048);
        try
        {
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
            using var publicKeyHttpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { data = new { public_key = publicKey } }),
                        Encoding.UTF8,
                        "application/json")
                }));
            var store = new KickOfficialChatReplayStore(cacheDirectory);
            await using var server = new KickWebhookChatServer(
                store,
                new MemoryLogger(),
                port: 0,
                httpClient: publicKeyHttpClient,
                timeProvider: new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 20, 2, 1, TimeSpan.Zero)));
            Assert.True(server.Start());

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var body = """
            {
              "message_id": "signed-webhook-message",
              "broadcaster": { "channel_slug": "streamer" },
              "sender": { "username": "viewer" },
              "content": "signed webhook chat",
              "created_at": "2026-06-01T20:02:00Z"
            }
            """;
            using var request = new HttpRequestMessage(HttpMethod.Post, server.LocalWebhookUrl);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            KickWebhookTestSignature.AddKickHeaders(
                request,
                rsa,
                "chat.message.sent",
                "message-id-1",
                "2026-06-01T20:02:01Z",
                Encoding.UTF8.GetBytes(body));

            using var response = await httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await store.ReadMessagesAsync(
                "streamer",
                startedAt,
                startedAt.AddMinutes(5));
            Assert.Equal(1, result.Messages.Count);
            Assert.Equal("signed webhook chat", result.Messages[0].Message);
            Assert.Equal("signed-webhook-message", result.Messages[0].MessageId);
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }),
    ("official Kick webhook server rejects invalid signatures and ignores non-chat events", async () =>
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "svs-kick-webhook-reject-" + Guid.NewGuid().ToString("N"));
        using var rsa = RSA.Create(2048);
        try
        {
            var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
            using var publicKeyHttpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { data = new { public_key = publicKey } }),
                        Encoding.UTF8,
                        "application/json")
                }));
            var store = new KickOfficialChatReplayStore(cacheDirectory);
            await using var server = new KickWebhookChatServer(
                store,
                new MemoryLogger(),
                port: 0,
                httpClient: publicKeyHttpClient,
                timeProvider: new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 20, 3, 1, TimeSpan.Zero)));
            Assert.True(server.Start());
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

            var chatBody = """
            {
              "message_id": "invalid-signature-message",
              "broadcaster": { "channel_slug": "streamer" },
              "sender": { "username": "viewer" },
              "content": "should not store",
              "created_at": "2026-06-01T20:02:00Z"
            }
            """;
            using (var invalidRequest = new HttpRequestMessage(HttpMethod.Post, server.LocalWebhookUrl))
            {
                invalidRequest.Content = new StringContent(chatBody, Encoding.UTF8, "application/json");
                KickWebhookTestSignature.AddKickHeaders(
                    invalidRequest,
                    rsa,
                    "chat.message.sent",
                    "message-id-2",
                    "2026-06-01T20:02:02Z",
                    Encoding.UTF8.GetBytes(chatBody + "tampered"));
                using var invalidResponse = await httpClient.SendAsync(invalidRequest);
                Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
            }

            var nonChatBody = """{"broadcaster":{"channel_slug":"streamer"},"created_at":"2026-06-01T20:03:00Z"}""";
            using (var nonChatRequest = new HttpRequestMessage(HttpMethod.Post, server.LocalWebhookUrl))
            {
                nonChatRequest.Content = new StringContent(nonChatBody, Encoding.UTF8, "application/json");
                KickWebhookTestSignature.AddKickHeaders(
                    nonChatRequest,
                    rsa,
                    "livestream.status.updated",
                    "message-id-3",
                    "2026-06-01T20:03:01Z",
                    Encoding.UTF8.GetBytes(nonChatBody));
                using var nonChatResponse = await httpClient.SendAsync(nonChatRequest);
                Assert.Equal(HttpStatusCode.Accepted, nonChatResponse.StatusCode);
            }

            var result = await store.ReadMessagesAsync(
                "streamer",
                new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 1, 20, 5, 0, TimeSpan.Zero));
            Assert.Equal(0, result.Messages.Count);
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }),
    ("official Kick webhook server shares concurrent disposal", async () =>
    {
        var server = new KickWebhookChatServer(
            new KickOfficialChatReplayStore(Path.Combine(Path.GetTempPath(), "svs-unused-kick-webhook-" + Guid.NewGuid().ToString("N"))),
            new MemoryLogger(),
            port: 0);
        Assert.True(server.Start());

        var firstDisposal = server.DisposeAsync().AsTask();
        var secondDisposal = server.DisposeAsync().AsTask();
        Assert.True(ReferenceEquals(firstDisposal, secondDisposal));
        await Task.WhenAll(firstDisposal, secondDisposal);
        await server.DisposeAsync();
        Assert.Equal(false, server.Start());
    }),
    ("normalizes out-of-range Kick webhook ports", async () =>
    {
        await using var server = new KickWebhookChatServer(
            new KickOfficialChatReplayStore(Path.Combine(Path.GetTempPath(), "svs-unused-kick-webhook-port-" + Guid.NewGuid().ToString("N"))),
            new MemoryLogger(),
            port: 65_536);
        var requestedPortField = typeof(KickWebhookChatServer).GetField(
            "requestedPort",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(requestedPortField);
        Assert.Equal(KickWebhookChatServer.DefaultPort, (int)requestedPortField!.GetValue(server)!);
        Assert.Equal(
            $"http://127.0.0.1:{KickWebhookChatServer.DefaultPort}{KickWebhookChatServer.WebhookPath}",
            server.LocalWebhookUrl);
    }),
    ("isolates throwing Streamlink log subscribers", async () =>
    {
        var logger = new MemoryLogger();
        using var process = new Process();
        var sessionType = typeof(StreamlinkService).Assembly.GetType(
            "StreamlinkVlcStudio.Infrastructure.Streamlink.StreamlinkExternalHttpSession",
            throwOnError: true)!;
        await using var session = (IStreamTransportSession)Activator.CreateInstance(
            sessionType,
            process,
            logger)!;
        var logLineReceived = sessionType.GetEvent("LogLineReceived");
        var addLogLine = sessionType.GetMethod(
            "AddLogLine",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(logLineReceived);
        Assert.NotNull(addLogLine);
        var observedLine = "";
        EventHandler<string> throwingHandler = (_, _) => throw new InvalidOperationException("subscriber failure");
        EventHandler<string> observingHandler = (_, line) => observedLine = line;
        logLineReceived!.AddEventHandler(session, throwingHandler);
        logLineReceived.AddEventHandler(session, observingHandler);

        addLogLine!.Invoke(session, ["test log line"]);

        Assert.Equal("test log line", observedLine);
        Assert.True(logger.Entries.Any(entry =>
            entry.Level == AppLogLevel.Warning &&
            entry.Message == "A Streamlink log subscriber failed." &&
            entry.Exception is InvalidOperationException));
    }),
    ("Kick chat client disposal is idempotent", async () =>
    {
        var client = new KickChatClient(new ChatSettings(), new MemoryLogger());

        var firstDisposal = client.DisposeAsync().AsTask();
        var secondDisposal = client.DisposeAsync().AsTask();
        Assert.True(ReferenceEquals(firstDisposal, secondDisposal));
        await Task.WhenAll(firstDisposal, secondDisposal);
        await client.DisposeAsync();
    }),
    ("Twitch chat client disposal is idempotent", async () =>
    {
        var client = new TwitchChatClient(new ChatSettings(), new MemoryLogger());

        var firstDisposal = client.DisposeAsync().AsTask();
        var secondDisposal = client.DisposeAsync().AsTask();
        Assert.True(ReferenceEquals(firstDisposal, secondDisposal));
        await Task.WhenAll(firstDisposal, secondDisposal);
        await client.DisposeAsync();
    }),
    ("creates official Kick chat message event subscription", async () =>
    {
        var requests = new List<(HttpMethod Method, Uri Uri, string Body, string? Authorization)>();
        var persistCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var body = request.Content is null
                ? ""
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString()));

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "name": "chat.message.sent",
                      "version": 1,
                      "subscription_id": "sub-123"
                    }
                  ]
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        await using var service = new KickEventSubscriptionService(
            new MemoryLogger(),
            httpClient,
            (_, _, _) => Task.FromResult<string?>("app-token"),
            (channel, _, _, _) =>
            {
                Assert.Equal("streamer", channel);
                return Task.FromResult<long?>(456);
            },
            (_, _) =>
            {
                persistCount++;
                return Task.CompletedTask;
            });
        var settings = new AppSettings();
        var target = new StreamTarget(PlatformKind.Kick, "streamer", "https://kick.com/streamer");

        var result = await service.EnsureChatMessageSentSubscriptionAsync(target, settings.Chat);

        Assert.Equal(KickEventSubscriptionEnsureStatus.Subscribed, result.Status);
        Assert.Equal("sub-123", result.SubscriptionId);
        Assert.Equal(456L, result.BroadcasterUserId);
        Assert.Equal("456", settings.Chat.KickBroadcasterUserIds["streamer"]);
        Assert.Equal(1, persistCount);
        Assert.Equal(2, requests.Count);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("Bearer app-token", requests[0].Authorization);
        Assert.Contains("broadcaster_user_id=456", requests[0].Uri.Query);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal("Bearer app-token", requests[1].Authorization);

        using var postBody = JsonDocument.Parse(requests[1].Body);
        var root = postBody.RootElement;
        Assert.Equal(456, root.GetProperty("broadcaster_user_id").GetInt32());
        Assert.Equal("webhook", root.GetProperty("method").GetString());
        var evt = root.GetProperty("events").EnumerateArray().Single();
        Assert.Equal("chat.message.sent", evt.GetProperty("name").GetString());
        Assert.Equal(1, evt.GetProperty("version").GetInt32());
    }),
    ("skips official Kick chat subscription create when it already exists", async () =>
    {
        var requestCount = 0;
        var persistCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestCount++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "id": "existing-sub",
                      "event": "chat.message.sent",
                      "version": 1,
                      "method": "webhook"
                    }
                  ]
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        await using var service = new KickEventSubscriptionService(
            new MemoryLogger(),
            httpClient,
            (_, _, _) => Task.FromResult<string?>("app-token"),
            (_, _, _, _) => throw new InvalidOperationException("Broadcaster resolver should not be called."),
            (_, _) =>
            {
                persistCount++;
                return Task.CompletedTask;
            });
        var settings = new AppSettings();
        var target = new StreamTarget(
            PlatformKind.Kick,
            "streamer",
            "https://kick.com/streamer",
            BroadcasterId: "456");

        var result = await service.EnsureChatMessageSentSubscriptionAsync(target, settings.Chat);

        Assert.Equal(KickEventSubscriptionEnsureStatus.AlreadySubscribed, result.Status);
        Assert.Equal("existing-sub", result.SubscriptionId);
        Assert.Equal(456L, result.BroadcasterUserId);
        Assert.Equal("456", settings.Chat.KickBroadcasterUserIds["streamer"]);
        Assert.Equal(1, persistCount);
        Assert.Equal(1, requestCount);
    }),
    ("maps Kick recent chat backfill messages", () =>
    {
        using var document = JsonDocument.Parse("""
        {
          "status": {"error": false, "code": 200, "message": "SUCCESS"},
          "data": {
            "messages": [
              {
                "id": "newer-kick-message",
                "content": "newer recent",
                "created_at": "2026-06-01T20:04:05Z",
                "sender": {
                  "username": "newer",
                  "identity": {
                    "color": "#55AAFF",
                    "badges": [{"type": "vip", "text": "VIP"}]
                  }
                }
              },
              {
                "id": "older-kick-message",
                "content": "older recent",
                "created_at": "2026-06-01T20:03:55Z",
                "sender": {"username": "older"}
              }
            ],
            "cursor": "1777086806667581",
            "pinned_message": null
          }
        }
        """);
        var page = KickChatTransport.ReadPage(document.RootElement, "channel");
        var messages = page.Messages;

        Assert.Equal(2, messages.Count);
        Assert.Equal("older-kick-message", messages[0].MessageId);
        Assert.Equal("older recent", messages[0].Message);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 3, 55, TimeSpan.Zero), messages[0].Timestamp);
        Assert.Equal("newer-kick-message", messages[1].MessageId);
        Assert.Equal("newer recent", messages[1].Message);
        Assert.Equal("vip", messages[1].Badges![0].Id);

        Assert.Equal("1777086806667581", page.Cursor);
        return Task.CompletedTask;
    }),
    ];
}
