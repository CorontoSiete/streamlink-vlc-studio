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
            var vodChatProvider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("Kick replay chat should not be requested."));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                chatFactory,
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: vodChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(settings);
            // In overlay layout the capture client is connected in the background, so wait for it
            // before feeding chat through it.
            await TestWait.UntilAsync(
                () => chatFactory.Client.Connected,
                TimeSpan.FromSeconds(4));
            chatFactory.Client.Receive(new ChatMessage(
                PlatformKind.Kick,
                "streamer",
                "viewer",
                "initial kick native overlay chat",
                startedAt.AddMinutes(10),
                MessageId: "initial-kick-native-overlay-chat"));

            var initialFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(8));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            await TestWait.UntilAsync(
                () => tab.ChatMessages.Any(message => message.Message == "initial kick native overlay chat"),
                TimeSpan.FromSeconds(2));
            Assert.True(tab.ChatMessages.Any(message => message.Message == "initial kick native overlay chat"));
            AssertNativeOverlayChatFrame(await initialFrameTask);

            var emptyFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayTransparentFrame,
                TimeSpan.FromSeconds(4));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(30));

            AssertNativeOverlayTransparentFrame(await emptyFrameTask);
            Assert.Equal(false, tab.ChatMessages.Any(message => message.Message == "initial kick native overlay chat"));

            chatFactory.Client.Receive(new ChatMessage(
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
                () => tab.ChatMessages.Any(message => message.Message == "later kick timestamp overlay chat"),
                TimeSpan.FromSeconds(1));
            Assert.True(tab.ChatMessages.Any(message => message.Message == "later kick timestamp overlay chat"));

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
            "StreamStudioTests",
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
            var vodChatProvider = new FakeVodChatProvider(FakeVodChatProvider.Once([
                new VodChatMessage(
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
                vodChatProvider: vodChatProvider);
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
            var vodChatProvider = new FakeVodChatProvider(FakeVodChatProvider.Once([
                new VodChatMessage(
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
                vodChatProvider: vodChatProvider);
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
            var vodChatProvider = new FakeVodChatProvider(FakeVodChatProvider.Once([
                new VodChatMessage(
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
                vodChatProvider: vodChatProvider);
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
        var vodChatProvider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("VOD comments ID should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            vodChatProvider: vodChatProvider);
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
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "captured hello"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("VOD comments ID", StringComparison.Ordinal)));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message.Contains("unexpected provider call", StringComparison.Ordinal)));

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
        var vodChatProvider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("VOD comments ID should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            vodChatProvider: vodChatProvider);
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
            var vodChatProvider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("VOD comments ID should not be requested."));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                chatFactory,
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: vodChatProvider);
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
            var vodChatProvider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("VOD comments ID should not be requested."));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                chatFactory,
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                vodChatProvider: vodChatProvider);
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
            vodChatProvider: new FakeVodChatProvider(VodChatFetchResult.Unsupported("unexpected provider call")));
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
            vodChatProvider: new FakeVodChatProvider(VodChatFetchResult.Unsupported("unexpected provider call")));
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
        var vodChatProvider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("Kick replay chat should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: replayResolver,
            vodChatProvider: vodChatProvider);
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
            vodChatProvider: new FakeVodChatProvider(VodChatFetchResult.Unsupported("Kick replay chat should not be requested.")));
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
        var vodChatProvider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("Kick replay chat should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            vodChatProvider: vodChatProvider);
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
            vodChatProvider: new FakeVodChatProvider(VodChatFetchResult.Unsupported("Kick replay chat should not be requested.")));
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

        // Chat the provider has already fetched must only become visible once playback reaches it.
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "later-viewer",
            "later kick timestamp chat",
            startedAt.AddMinutes(10).AddSeconds(50),
            MessageId: "later-kick-timestamp-chat"));
        Assert.Equal(false, DockedChatMessagesContain(tab, "later kick timestamp chat"));

        MarkReplayClockSeekConfirmed(tab, TimeSpan.FromSeconds(50));
        forcedClockPosition = TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(50));
        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "later kick timestamp chat"),
            TimeSpan.FromSeconds(2));

        Assert.True(DockedChatMessagesContain(tab, "initial kick captured chat"));
        Assert.True(DockedChatMessagesContain(tab, "later kick timestamp chat"));
        Assert.Equal(false, DockedChatMessagesContainText(tab, "Kick replay chat should not be requested"));

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
            vodChatProvider: new FakeVodChatProvider(VodChatFetchResult.Unsupported("Kick replay chat should not be requested.")));
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
            PlatformKind.Kick,
            "streamer",
            "later-viewer",
            "anchor progressed kick chat",
            startedAt.AddMinutes(10).AddSeconds(8),
            MessageId: "anchor-progressed-kick-chat"));
        Assert.Equal(false, DockedChatMessagesContain(tab, "anchor progressed kick chat"));

        // The engine clock is stuck, so only the dead-reckoned anchor can move chat forward.
        MarkReplayClockSeekConfirmed(tab, TimeSpan.FromSeconds(8));
        InvokeReplayClockUpdate(tab);

        await TestWait.UntilAsync(
            () => DockedChatMessagesContain(tab, "anchor progressed kick chat"),
            TimeSpan.FromSeconds(2));

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
            vodChatProvider: new FakeVodChatProvider(VodChatFetchResult.Unsupported("unexpected provider call")));
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
        var vodChatProvider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("Kick replay chat should not be requested."));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            vodChatProvider: vodChatProvider);
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

        // Seeking back on a live stream stays quiet; captured chat simply appears as playback
        // reaches it, so neither a coverage notice nor the provider reason belongs on screen.
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
        var vodChatProvider = new FakeVodChatProvider(FakeVodChatProvider.Once([
            new VodChatMessage(
                TimeSpan.FromMinutes(10),
                new ChatMessage(PlatformKind.Twitch, "streamer", "vod-viewer", "vod chat after promotion", startedAt.AddMinutes(10)))
        ]));
        await using var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: replayResolver,
            vodChatProvider: vodChatProvider,
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
        await TestWait.UntilAsync(
            () => vodChatProvider.RequestedReplays.Any(replay => replay.ReplayId == "123"),
            TimeSpan.FromSeconds(2));
        // A recorded request precedes timeline insertion and the next UI clock update.
        await WaitForDockedChatMessageAsync(tab, "vod chat after promotion");

        Assert.True(vodChatProvider.RequestedReplays.Any(replay => replay.ReplayId == "123"));
        Assert.Contains("123", tab.ReplaySeekToolTip);
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "vod chat after promotion"));

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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
        tab.ReplaySeekSliderValue = TimeSpan.FromMinutes(5).TotalSeconds;
        tab.ReplaySeekSliderValue = TimeSpan.FromMinutes(15).TotalSeconds;
        tab.ReplaySeekSliderValue = TimeSpan.FromMinutes(25).TotalSeconds;

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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
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
            entry.Message.Contains("LogLineReceived subscriber threw", StringComparison.Ordinal) &&
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
    ("VOD chat timeline only releases messages playback has reached", () =>
    {
        var timeline = new VodChatTimeline();
        timeline.AddRange(
            [
                VodChatTestMessage(TimeSpan.FromSeconds(5), "a"),
                VodChatTestMessage(TimeSpan.FromSeconds(10), "b"),
                VodChatTestMessage(TimeSpan.FromSeconds(30), "c")
            ],
            100);

        Assert.Equal(3, timeline.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), timeline.LastOffset);

        var first = timeline.TakeMessagesDueAt(TimeSpan.FromSeconds(10), 100);
        Assert.Equal(2, first.Count);
        Assert.Equal("a", first[0].Message);
        Assert.Equal("b", first[1].Message);

        // Nothing new is due, so nothing is handed out a second time.
        Assert.Equal(0, timeline.TakeMessagesDueAt(TimeSpan.FromSeconds(10), 100).Count);

        var second = timeline.TakeMessagesDueAt(TimeSpan.FromSeconds(40), 100);
        Assert.Equal(1, second.Count);
        Assert.Equal("c", second[0].Message);
        return Task.CompletedTask;
    }),
    ("VOD chat timeline rewinds its cursor so a backward seek replays chat", () =>
    {
        var timeline = new VodChatTimeline();
        timeline.AddRange(
            [
                VodChatTestMessage(TimeSpan.FromSeconds(5), "a"),
                VodChatTestMessage(TimeSpan.FromSeconds(10), "b"),
                VodChatTestMessage(TimeSpan.FromSeconds(30), "c")
            ],
            100);
        timeline.TakeMessagesDueAt(TimeSpan.FromSeconds(40), 100);

        timeline.MoveCursorTo(TimeSpan.FromSeconds(8));
        var replayed = timeline.TakeMessagesDueAt(TimeSpan.FromSeconds(30), 100);

        Assert.Equal(2, replayed.Count);
        Assert.Equal("b", replayed[0].Message);
        Assert.Equal("c", replayed[1].Message);
        return Task.CompletedTask;
    }),
    ("VOD chat timeline ignores duplicates and messages already behind the cursor", () =>
    {
        var timeline = new VodChatTimeline();
        Assert.True(timeline.Add(VodChatTestMessage(TimeSpan.FromSeconds(10), "b"), 100));
        Assert.Equal(false, timeline.Add(VodChatTestMessage(TimeSpan.FromSeconds(10), "b"), 100));

        timeline.TakeMessagesDueAt(TimeSpan.FromSeconds(10), 100);

        // Arrives late but belongs to chat the viewer has already passed.
        Assert.True(timeline.Add(VodChatTestMessage(TimeSpan.FromSeconds(4), "late"), 100));
        Assert.Equal(0, timeline.TakeMessagesDueAt(TimeSpan.FromSeconds(10), 100).Count);
        return Task.CompletedTask;
    }),
    ("VOD chat timeline drops the oldest surplus instead of letting chat lag the video", () =>
    {
        var timeline = new VodChatTimeline();
        var backlog = new List<VodChatMessage>();
        for (var index = 0; index < 250; index++)
        {
            backlog.Add(VodChatTestMessage(
                TimeSpan.FromMilliseconds(index),
                "m" + index.ToString(CultureInfo.InvariantCulture)));
        }

        timeline.AddRange(backlog, 1_000);
        var shown = timeline.TakeMessagesDueAt(TimeSpan.FromSeconds(1), 100);

        Assert.Equal(100, shown.Count);
        Assert.Equal("m150", shown[0].Message);
        Assert.Equal("m249", shown[99].Message);
        return Task.CompletedTask;
    }),
    ("VOD chat timeline evicts the oldest messages once capped", () =>
    {
        var timeline = new VodChatTimeline();
        for (var index = 0; index < 20; index++)
        {
            timeline.Add(
                VodChatTestMessage(
                    TimeSpan.FromSeconds(index),
                    "m" + index.ToString(CultureInfo.InvariantCulture)),
                5);
        }

        Assert.Equal(5, timeline.Count);
        timeline.MoveCursorTo(TimeSpan.Zero);
        var remaining = timeline.TakeMessagesDueAt(TimeSpan.FromMinutes(1), 100);
        Assert.Equal(5, remaining.Count);
        Assert.Equal("m15", remaining[0].Message);
        Assert.Equal("m19", remaining[4].Message);
        return Task.CompletedTask;
    }),
    ("VOD chat pump walks forward and stops once the look-ahead is covered", async () =>
    {
        var replay = TwitchVodChatReplay();
        var provider = new FakeVodChatProvider();
        provider.Enqueue(FakeVodChatProvider.Chunk(replay, TimeSpan.Zero, 3, "first"));
        provider.Enqueue(FakeVodChatProvider.Chunk(replay, TimeSpan.FromSeconds(3), 3, "second"));
        provider.TrailingResult = VodChatFetchResult.Completed([], TimeSpan.FromMinutes(5));
        await using var controller = new VodChatController(provider, new MemoryLogger());

        controller.Start(replay, new AppSettings(), TimeSpan.Zero, () => replay.Duration);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Each request continues from the previous frontier, so paging always advances.
        Assert.Equal(TimeSpan.Zero, provider.RequestedOffsets[0]);
        Assert.Equal(TimeSpan.FromSeconds(3), provider.RequestedOffsets[1]);
        Assert.True(provider.CallCount <= 3);

        var due = controller.TakeMessagesDueAt(TimeSpan.FromSeconds(10), 100);
        Assert.Equal(6, due.Count);
        Assert.Equal("first 0", due[0].Message);
        Assert.Equal("second 2", due[5].Message);
    }),
    ("VOD chat pump stops asking once a session reports it cannot serve chat", async () =>
    {
        var replay = TwitchVodChatReplay();
        var provider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("no comments id yet"));
        await using var controller = new VodChatController(provider, new MemoryLogger());

        controller.Start(replay, new AppSettings(), TimeSpan.Zero, () => replay.Duration);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(120);

        Assert.Equal(1, provider.CallCount);
        Assert.True(controller.TryTakeNotice(out var notice));
        Assert.Equal("no comments id yet", notice);
        // Taken once, then offered again only after a seek wipes the visible chat.
        Assert.Equal(false, controller.TryTakeNotice(out _));

        controller.Start(replay, new AppSettings(), TimeSpan.FromMinutes(10), () => replay.Duration);
        Assert.True(controller.TryTakeNotice(out var repeated));
        Assert.Equal("no comments id yet", repeated);
    }),
    ("VOD chat seeking inside fetched chat republishes it without refetching", async () =>
    {
        var replay = TwitchVodChatReplay();
        var provider = new FakeVodChatProvider();
        provider.Enqueue(FakeVodChatProvider.Chunk(replay, TimeSpan.Zero, 40, "chunk"));
        provider.TrailingResult = VodChatFetchResult.Completed([], TimeSpan.FromMinutes(5));
        await using var controller = new VodChatController(provider, new MemoryLogger());

        controller.Start(replay, new AppSettings(), TimeSpan.Zero, () => replay.Duration);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TimeSpan.FromSeconds(5));
        controller.TakeMessagesDueAt(TimeSpan.FromSeconds(40), 100);
        var callsBeforeSeek = provider.CallCount;

        controller.Start(replay, new AppSettings(), TimeSpan.FromSeconds(20), () => replay.Duration);
        var republished = controller.TakeMessagesDueAt(TimeSpan.FromSeconds(20), 100);

        Assert.Equal(callsBeforeSeek, provider.CallCount);
        Assert.Equal(21, republished.Count);
        Assert.Equal("chunk 0", republished[0].Message);
        Assert.Equal("chunk 20", republished[20].Message);
    }),
    ("VOD chat keeps captured chat when the session is promoted to a published VOD", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var dvr = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://example.invalid/index-dvr.m3u8",
            "live-dvr-99",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "",
            MediaKind: ReplayMediaKind.CurrentLiveDvr);
        var provider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("no comments id yet"));
        provider.TrailingResult = VodChatFetchResult.Completed([], TimeSpan.FromHours(1));
        await using var controller = new VodChatController(provider, new MemoryLogger());

        controller.Start(dvr, new AppSettings(), TimeSpan.Zero, () => dvr.Duration);
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TimeSpan.FromSeconds(5));
        controller.CaptureLiveMessage(new ChatMessage(
            PlatformKind.Twitch,
            "streamer",
            "viewer",
            "captured while live",
            startedAt.AddMinutes(5),
            MessageId: "captured-while-live"));

        controller.Promote(dvr with { ReplayId = "2877743217", MediaKind = ReplayMediaKind.Archive });
        await controller.WaitUntilCaughtUpAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(provider.RequestedReplays.Any(requested => requested.ReplayId == "2877743217"));
        var due = controller.TakeMessagesDueAt(TimeSpan.FromMinutes(10), 100);
        Assert.Equal(1, due.Count);
        Assert.Equal("captured while live", due[0].Message);
    }),
    ("VOD chat captures live messages received before the replay session is known", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var provider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("no comments id yet"));
        await using var controller = new VodChatController(provider, new MemoryLogger());

        // Arrives while the replay lookup is still in flight, so no offset can be computed yet.
        Assert.Equal(false, controller.CaptureLiveMessage(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "buffered",
            startedAt.AddMinutes(3),
            MessageId: "buffered")));
        Assert.Equal(false, controller.HasMessages);

        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-1",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "");
        controller.Start(replay, new AppSettings(), TimeSpan.FromMinutes(5), () => replay.Duration);

        Assert.True(controller.HasMessages);
        var due = controller.TakeMessagesDueAt(TimeSpan.FromMinutes(5), 100);
        Assert.Equal(1, due.Count);
        Assert.Equal("buffered", due[0].Message);
    }),
    ("VOD chat reports a captured message as due only once playback reaches it", async () =>
    {
        var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            "https://kick.example/replay/index.m3u8",
            "kick-1",
            startedAt,
            TimeSpan.FromHours(1),
            true,
            "");
        var provider = new FakeVodChatProvider(VodChatFetchResult.Unsupported("not needed"));
        await using var controller = new VodChatController(provider, new MemoryLogger());
        controller.Start(replay, new AppSettings(), TimeSpan.FromMinutes(10), () => replay.Duration);

        Assert.Equal(false, controller.CaptureLiveMessage(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "from the future",
            startedAt.AddMinutes(50),
            MessageId: "future")));
        Assert.True(controller.CaptureLiveMessage(new ChatMessage(
            PlatformKind.Kick,
            "streamer",
            "viewer",
            "already passed",
            startedAt.AddMinutes(9).AddSeconds(50),
            MessageId: "passed")));

        var due = controller.TakeMessagesDueAt(TimeSpan.FromMinutes(10), 100);
        Assert.Equal(1, due.Count);
        Assert.Equal("already passed", due[0].Message);
    }),
    ("Twitch VOD chat frontier always advances past the offset it asked for", () =>
    {
        // Twitch answers an offset request with the page around it, so a page can end before the
        // requested offset. The frontier still has to move, or polling would stall forever.
        Assert.Equal(
            TimeSpan.FromSeconds(3602),
            TwitchVodChatFetcher.ResolveCoveredThroughOffset(
                TimeSpan.FromSeconds(3600),
                TimeSpan.FromSeconds(3602.9)));
        Assert.Equal(
            TimeSpan.FromSeconds(3624),
            TwitchVodChatFetcher.ResolveCoveredThroughOffset(
                TimeSpan.FromSeconds(3623),
                TimeSpan.FromSeconds(3622)));
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            TwitchVodChatFetcher.ResolveCoveredThroughOffset(TimeSpan.Zero, TimeSpan.Zero));
        return Task.CompletedTask;
    }),
    ("Kick VOD chat cursors are Unix microsecond timestamps", () =>
    {
        // Kick page cursors are timestamps, so one can be synthesized for any instant and used as
        // an exclusive upper bound while paging backwards.
        Assert.Equal(
            "1789768800000000",
            KickVodChatFetcher.ToCursor(new DateTimeOffset(2026, 9, 18, 22, 0, 0, TimeSpan.Zero)));
        Assert.Equal(
            "1789768800000000",
            KickVodChatFetcher.ToCursor(new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("0", KickVodChatFetcher.ToCursor(DateTimeOffset.UnixEpoch));
        return Task.CompletedTask;
    }),
    ];
}
