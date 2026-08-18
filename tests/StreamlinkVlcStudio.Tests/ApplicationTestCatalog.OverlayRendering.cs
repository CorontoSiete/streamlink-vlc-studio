internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> OverlayRendering { get; } =
    [
    ("animated emote image cache evicts by count and decoded memory", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int maxImageBytes = 2 * 1024 * 1024;
            AnimatedEmoteImage.ClearCacheForTest();
            var pendingUrl = $"https://example.invalid/pending-cache-{Guid.NewGuid():N}.png";
            var pending = AnimatedEmoteImage.SetPendingImageLoadForTest(pendingUrl, maxImageBytes);

            for (var index = 0; index < 257; index++)
            {
                AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                    $"https://example.invalid/count-cache-{index}.png",
                    maxImageBytes,
                    [Colors.Red],
                    [TimeSpan.FromMilliseconds(100)]);
            }

            var countStats = AnimatedEmoteImage.GetCacheStatsForTest();
            Assert.Equal(257, countStats.TotalEntries);
            Assert.Equal(256, countStats.CompletedEntries);
            Assert.Equal(1, countStats.InFlightEntries);
            Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest(pendingUrl, maxImageBytes));
            Assert.Equal(false, AnimatedEmoteImage.ContainsCachedImageForTest("https://example.invalid/count-cache-0.png", maxImageBytes));
            Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest("https://example.invalid/count-cache-256.png", maxImageBytes));

            AnimatedEmoteImage.ClearCacheForTest();
            for (var index = 0; index < 25; index++)
            {
                AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                    $"https://example.invalid/memory-cache-{index}.png",
                    maxImageBytes,
                    [Colors.Lime],
                    [TimeSpan.FromMilliseconds(100)],
                    1024,
                    1024);
            }

            var memoryStats = AnimatedEmoteImage.GetCacheStatsForTest();
            Assert.True(memoryStats.CompletedEntries <= 24);
            Assert.True(memoryStats.EstimatedDecodedBytes <= 96L * 1024 * 1024);
            Assert.Equal(false, AnimatedEmoteImage.ContainsCachedImageForTest("https://example.invalid/memory-cache-0.png", maxImageBytes));
            Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest("https://example.invalid/memory-cache-24.png", maxImageBytes));

            pending.SetResult(null);
            AnimatedEmoteImage.ClearCacheForTest();
        });
    }),
    ("animated emote image cache version reloads the same URL independently", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            const long firstCacheVersion = 101;
            const long secondCacheVersion = 102;
            var imageUrl = $"https://example.invalid/versioned-thumbnail-{Guid.NewGuid():N}.png";
            AnimatedEmoteImage.ClearCacheForTest();
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                imageUrl,
                maxImageBytes,
                [Colors.Red],
                [TimeSpan.FromSeconds(1)],
                width: 8,
                height: 8,
                cacheVersion: firstCacheVersion);
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                imageUrl,
                maxImageBytes,
                [Colors.Lime],
                [TimeSpan.FromSeconds(1)],
                width: 16,
                height: 9,
                cacheVersion: secondCacheVersion);

            try
            {
                var image = new AnimatedEmoteImage
                {
                    MaxImageBytes = maxImageBytes,
                    ImageRequest = new AnimatedImageRequest(imageUrl, firstCacheVersion)
                };
                await TestWait.UntilAsync(
                    () => !image.IsImageLoadPending && image.Source is BitmapSource,
                    TimeSpan.FromSeconds(1));

                Assert.True(image.CurrentImageCacheKey.HasValue);
                var firstKey = image.CurrentImageCacheKey.GetValueOrDefault();
                Assert.Equal(imageUrl, firstKey.Url);
                Assert.Equal(firstCacheVersion, firstKey.CacheVersion);
                Assert.Equal(8, ((BitmapSource)image.Source).PixelWidth);

                image.ImageRequest = new AnimatedImageRequest(imageUrl, secondCacheVersion);
                await TestWait.UntilAsync(
                    () => !image.IsImageLoadPending &&
                        image.CurrentImageCacheKey is { CacheVersion: secondCacheVersion },
                    TimeSpan.FromSeconds(1));

                Assert.True(image.CurrentImageCacheKey.HasValue);
                var secondKey = image.CurrentImageCacheKey.GetValueOrDefault();
                Assert.Equal(imageUrl, secondKey.Url);
                Assert.Equal(secondCacheVersion, secondKey.CacheVersion);
                Assert.Equal(16, ((BitmapSource)image.Source).PixelWidth);
                Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest(
                    imageUrl,
                    maxImageBytes,
                    firstCacheVersion));
                Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest(
                    imageUrl,
                    maxImageBytes,
                    secondCacheVersion));
            }
            finally
            {
                AnimatedEmoteImage.ClearCacheForTest();
            }
        });
    }),
    ("animated emote image cache versions isolate an older in-flight load", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            const long oldCacheVersion = 201;
            const long currentCacheVersion = 202;
            var imageUrl = $"https://example.invalid/version-race-{Guid.NewGuid():N}.png";
            var oldLoadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completionWasVisible = 0;
            EventHandler<AnimatedEmoteImageCacheCompletedEventArgs> cacheCompleted = (_, e) =>
            {
                if (e.Key.Url == imageUrl && e.Key.CacheVersion == oldCacheVersion)
                {
                    if (AnimatedEmoteImage.IsCacheEntryCompleted(e.Key))
                    {
                        Interlocked.Exchange(ref completionWasVisible, 1);
                    }

                    oldLoadCompleted.TrySetResult();
                }
            };
            AnimatedEmoteImage.ClearCacheForTest();
            var releaseOldLoad = AnimatedEmoteImage.SetPendingImageLoadForTest(
                imageUrl,
                maxImageBytes,
                oldCacheVersion);
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                imageUrl,
                maxImageBytes,
                [Colors.Lime],
                [TimeSpan.FromSeconds(1)],
                width: 16,
                height: 9,
                cacheVersion: currentCacheVersion);
            AnimatedEmoteImage.ImageCacheEntryCompleted += cacheCompleted;

            try
            {
                var image = new AnimatedEmoteImage
                {
                    MaxImageBytes = maxImageBytes,
                    ImageRequest = new AnimatedImageRequest(imageUrl, oldCacheVersion)
                };
                Assert.True(image.IsImageLoadPending);

                image.ImageRequest = new AnimatedImageRequest(imageUrl, currentCacheVersion);
                await TestWait.UntilAsync(
                    () => !image.IsImageLoadPending && image.Source is BitmapSource,
                    TimeSpan.FromSeconds(1));
                Assert.Equal(16, ((BitmapSource)image.Source).PixelWidth);

                releaseOldLoad.SetResult(null);
                await oldLoadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(1, Volatile.Read(ref completionWasVisible));
                await image.Dispatcher.InvokeAsync(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.True(image.CurrentImageCacheKey.HasValue);
                Assert.Equal(
                    currentCacheVersion,
                    image.CurrentImageCacheKey.GetValueOrDefault().CacheVersion);
                Assert.Equal(16, ((BitmapSource)image.Source).PixelWidth);
            }
            finally
            {
                AnimatedEmoteImage.ImageCacheEntryCompleted -= cacheCompleted;
                releaseOldLoad.TrySetResult(null);
                AnimatedEmoteImage.ClearCacheForTest();
            }
        });
    }),
    ("failed stale image load does not evict its cache replacement", () =>
    {
        return TestSta.RunAsync(async () =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            var imageUrl = $"https://example.invalid/failed-cache-race-{Guid.NewGuid():N}.png";
            AnimatedEmoteImage.ClearCacheForTest();
            var failOldLoad = AnimatedEmoteImage.SetPendingImageLoadForTest(imageUrl, maxImageBytes);

            try
            {
                var staleImage = new AnimatedEmoteImage
                {
                    MaxImageBytes = maxImageBytes,
                    ImageUrl = imageUrl
                };
                Assert.True(staleImage.IsImageLoadPending);

                AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                    imageUrl,
                    maxImageBytes,
                    [Colors.Lime],
                    [TimeSpan.FromSeconds(1)],
                    width: 17,
                    height: 9);
                failOldLoad.SetException(new InvalidDataException("Expected stale image load failure."));
                await TestWait.UntilAsync(
                    () => !staleImage.IsImageLoadPending,
                    TimeSpan.FromSeconds(1));

                Assert.Equal<ImageSource?>(null, staleImage.Source);
                Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest(imageUrl, maxImageBytes));

                var currentImage = new AnimatedEmoteImage
                {
                    MaxImageBytes = maxImageBytes,
                    ImageUrl = imageUrl
                };
                await TestWait.UntilAsync(
                    () => !currentImage.IsImageLoadPending && currentImage.Source is BitmapSource,
                    TimeSpan.FromSeconds(1));
                Assert.Equal(17, ((BitmapSource)currentImage.Source).PixelWidth);
            }
            finally
            {
                failOldLoad.TrySetResult(null);
                AnimatedEmoteImage.ClearCacheForTest();
            }
        });
    }),
    ("failed image cache expires and reloads the same thumbnail URL", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            const string webpBase64 = "UklGRkAAAABXRUJQVlA4IDQAAADwAQCdASoBAAEAAQAcJaACdLoB+AAETAAA/vW4f/6aR40jxpHxcP/ugT90CfugT/3NoAAA";
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "svs-thumbnail-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var thumbnailPath = Path.Combine(tempDirectory, "eventually-available.webp");
            var thumbnailUrl = new Uri(thumbnailPath).AbsoluteUri;
            AnimatedEmoteImage.ClearCacheForTest();

            try
            {
                var unavailableImage = new AnimatedEmoteImage
                {
                    MaxImageBytes = maxImageBytes,
                    ImageUrl = thumbnailUrl
                };
                await TestWait.UntilAsync(
                    () => !unavailableImage.IsImageLoadPending,
                    TimeSpan.FromSeconds(1));

                Assert.Equal<ImageSource?>(null, unavailableImage.Source);
                Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest(thumbnailUrl, maxImageBytes));

                File.WriteAllBytes(thumbnailPath, Convert.FromBase64String(webpBase64));
                var negativelyCachedImage = new AnimatedEmoteImage
                {
                    MaxImageBytes = maxImageBytes,
                    ImageUrl = thumbnailUrl
                };
                await TestWait.UntilAsync(
                    () => !negativelyCachedImage.IsImageLoadPending,
                    TimeSpan.FromSeconds(1));

                Assert.Equal<ImageSource?>(null, negativelyCachedImage.Source);
                Assert.True(AnimatedEmoteImage.ExpireFailedImageLoadForTest(thumbnailUrl, maxImageBytes));

                var recoveredImage = new AnimatedEmoteImage
                {
                    MaxImageBytes = maxImageBytes,
                    ImageUrl = thumbnailUrl
                };
                await TestWait.UntilAsync(
                    () => !recoveredImage.IsImageLoadPending,
                    TimeSpan.FromSeconds(1));

                Assert.NotNull(recoveredImage.Source);
                Assert.Equal(Visibility.Visible, recoveredImage.Visibility);
                Assert.True(AnimatedEmoteImage.FailedLoadCacheDuration < TimeSpan.FromMinutes(1));
            }
            finally
            {
                AnimatedEmoteImage.ClearCacheForTest();
                Directory.Delete(tempDirectory, recursive: true);
            }
        });
    }),
    ("native replay overlay pins visible emotes until the frame is replaced", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            var pinnedUrl = $"https://example.invalid/pinned-replay-{Guid.NewGuid():N}.gif";
            var pinOwner = new object();
            AnimatedEmoteImage.ClearCacheForTest();
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                pinnedUrl,
                maxImageBytes,
                [Colors.Red, Colors.Lime],
                [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)]);
            try
            {
                var message = new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    "Spin",
                    DateTimeOffset.UtcNow,
                    Emotes: [new ChatEmote(0, 4, "Spin", pinnedUrl)],
                    MessageId: "pinned-native-replay-emote");
                var frame = NativeOverlayChatFrameRenderer.TryBuildFrame(
                    [message],
                    new ChatSettings { DockWidth = 340 },
                    18,
                    1080,
                    null,
                    TimeSpan.Zero,
                    out _,
                    out _,
                    imageCachePinOwner: pinOwner);
                Assert.NotNull(frame);
                Assert.True(frame!.HasAnimatedContent);

                for (var index = 0; index < 256; index++)
                {
                    AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                        $"https://example.invalid/pin-pressure-{index}.png",
                        maxImageBytes,
                        [Colors.DeepSkyBlue],
                        [TimeSpan.FromMilliseconds(100)]);
                }

                Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest(pinnedUrl, maxImageBytes));
                Assert.Equal(
                    false,
                    AnimatedEmoteImage.ContainsCachedImageForTest(
                        "https://example.invalid/pin-pressure-0.png",
                        maxImageBytes));
                AnimatedEmoteImage.ClearCachePins(pinOwner);
                AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                    "https://example.invalid/pin-pressure-after-release.png",
                    maxImageBytes,
                    [Colors.DeepSkyBlue],
                    [TimeSpan.FromMilliseconds(100)]);
                Assert.Equal(false, AnimatedEmoteImage.ContainsCachedImageForTest(pinnedUrl, maxImageBytes));
            }
            finally
            {
                AnimatedEmoteImage.ClearCachePins(pinOwner);
                AnimatedEmoteImage.ClearCacheForTest();
            }
        });
    }),
    ("seeking replay switches tab to VOD playback and disables chat sending", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
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
        var replayResolver = new FakeReplayResolver(replay);
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available([
            new ReplayChatMessage(
                TimeSpan.FromMinutes(10),
                new ChatMessage(PlatformKind.Twitch, "streamer", "viewer", "replay hello", DateTimeOffset.UtcNow))
        ]));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action(),
            replayResolver: replayResolver,
            replayChatProvider: replayChatProvider);
        try
        {
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            tab.SetVideoHandle(new IntPtr(42));

            await tab.StartAsync(settings);
            Assert.True(tab.IsReplaySeekEnabled);
            Assert.Equal(1, replayResolver.CallCount);
            await TestWait.UntilAsync(
                () => streamlink.ResolveStreamUrlCount == 1,
                TimeSpan.FromSeconds(1));

            tab.ReplaySeekValue = TimeSpan.FromMinutes(10).TotalSeconds;
            await tab.SeekReplayAsync(TimeSpan.FromSeconds(tab.ReplaySeekSliderValue));
            await TestWait.UntilAsync(
                () => tab.DockedChatMessages.Any(message => message.Message == "replay hello"),
                TimeSpan.FromSeconds(1));

            Assert.Equal(1, streamlink.ResolveStreamUrlCount);
            Assert.Equal("best", streamlink.ResolveStreamUrlRequests.Single().Quality);
            Assert.True(tab.IsReplayMode);
            Assert.True(tab.IsBehindLive);
            Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine!.Position);
            Assert.True(tab.DockedChatMessages.Any(message => message.Message == "replay hello"));
            Assert.True(replayChatProvider.CallCount > 0);
            Assert.Equal(false, replayChatProvider.Requests.Any(request => request.ReplayId.StartsWith("live-dvr-", StringComparison.Ordinal)));
            Assert.True(replayChatProvider.Requests.Any(request => request.ReplayId == "123"));

            tab.OutgoingChatText = "should not send";
            await tab.SendChatMessageAsync();
            Assert.Equal(0, chatFactory.Client.SentMessages.Count);
        }
        finally
        {
            await tab.DisposeAsync();
        }
    }),
    ("sub-only live replay falls back to the direct VOD playlist", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException(
                "This video is only available to subscribers")
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var bypassUri = new Uri(@"C:\fake\sub-only-live-123.m3u8");
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver
        {
            Override = (request, _) =>
            {
                Assert.Equal("123", request.VodId);
                Assert.Equal("best", request.Quality);
                return Task.FromResult(new TwitchSubOnlyVodResolution(bypassUri, "chunked", "Resolved."));
            }
        };
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
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => tab.CanSeekReplay && subOnlyResolver.Requests.Count == 1,
            TimeSpan.FromSeconds(1));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(1, subOnlyResolver.Requests.Count);
        Assert.Equal(bypassUri, playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);
        await tab.DisposeAsync();
    }),
    ("sub-only live replay normalizes a direct CloudFront DVR URL", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException(
                "Direct CloudFront replay should use the sub-only resolver")
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var bypassUri = new Uri(@"C:\fake\sub-only-direct-123.m3u8");
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver
        {
            Override = (_, _) => Task.FromResult(
                new TwitchSubOnlyVodResolution(bypassUri, "720p60", "Resolved."))
        };
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://d1g1f25tn8m2e6.cloudfront.net/replay/chunked/index-dvr.m3u8",
            "123",
            DateTimeOffset.UtcNow.AddHours(-1),
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => tab.CanSeekReplay && subOnlyResolver.Requests.Count == 1,
            TimeSpan.FromSeconds(1));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(0, streamlink.ResolveStreamUrlCount);
        Assert.Equal(1, subOnlyResolver.Requests.Count);
        Assert.Equal(bypassUri, playbackFactory.Engine!.LastPlayedUri);
        Assert.True(tab.IsReplayMode);
        await tab.DisposeAsync();
    }),
    ("replay seekbar waits for required pre-resolved playback URL", async () =>
    {
        var releaseResolve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamlink = new FakeStreamlinkService();
        streamlink.ResolveStreamUrlOverride = async (_, cancellationToken) =>
        {
            await releaseResolve.Task.WaitAsync(cancellationToken);
            return new StreamlinkResolvedUrl(new Uri("https://example.com/pre-resolved-replay.m3u8"), "Resolved.");
        };
        var playbackFactory = new FakePlaybackEngineFactory();
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
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
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

        Assert.Equal(false, tab.CanSeekReplay);
        Assert.Equal(true, tab.IsReplaySeekEnabled);
        Assert.Contains("Preparing replay stream URL", tab.ReplaySeekToolTip);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);

        releaseResolve.SetResult();
        await TestWait.UntilAsync(
            () => tab.CanSeekReplay,
            TimeSpan.FromSeconds(1));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(new Uri("https://example.com/pre-resolved-replay.m3u8"), playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);

        await tab.DisposeAsync();
    }),
    ("Twitch direct HLS replay URL skips Streamlink URL resolution", async () =>
    {
        var directReplayUri = new Uri("https://cdn.example.com/replays/123/index.m3u8?token=abc");
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("Direct replay HLS should not call Streamlink URL resolution.")
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            directReplayUri.ToString(),
            "123",
            DateTimeOffset.UtcNow.AddHours(-1),
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
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

        Assert.True(tab.CanSeekReplay);
        Assert.Equal(0, streamlink.ResolveStreamUrlCount);

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(0, streamlink.ResolveStreamUrlCount);
        Assert.Equal(directReplayUri, playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);

        await tab.DisposeAsync();
    }),
    ("Kick direct HLS replay URL resolves through Streamlink for seek playback", async () =>
    {
        var directReplayUri = new Uri("https://stream.kick.com/replay/abc-123/index.m3u8");
        var resolvedReplayUri = new Uri("https://stream.kick.com/replay/abc-123/720p/index.m3u8");
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(resolvedReplayUri, "Resolved Kick replay."))
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var replay = new ReplaySessionInfo(
            PlatformKind.Kick,
            "streamer",
            directReplayUri.ToString(),
            "abc-123",
            DateTimeOffset.UtcNow.AddHours(-1),
            TimeSpan.FromHours(1),
            true,
            "",
            "720p");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
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

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(PlatformKind.Kick, streamlink.ResolveStreamUrlRequests.Single().Target.Platform);
        Assert.Equal(directReplayUri.ToString(), streamlink.ResolveStreamUrlRequests.Single().Target.Url);
        Assert.Equal("720p", streamlink.ResolveStreamUrlRequests.Single().Quality);

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(resolvedReplayUri, playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);

        await tab.DisposeAsync();
    }),
    ("failed replay URL prefetch allows on-demand retry during first seek", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var resolveAttempts = 0;
        streamlink.ResolveStreamUrlOverride = (_, _) =>
        {
            resolveAttempts++;
            if (resolveAttempts == 1)
            {
                throw new InvalidOperationException("Simulated prefetch failure.");
            }

            return Task.FromResult(new StreamlinkResolvedUrl(new Uri("https://example.com/retry-replay.m3u8"), "Resolved on retry."));
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
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
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
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
            () => logger.Entries.Any(entry => entry.Message.Contains("Replay stream URL prefetch failed", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(1));

        Assert.True(tab.CanSeekReplay);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(2, streamlink.ResolveStreamUrlCount);
        Assert.Equal(new Uri("https://example.com/retry-replay.m3u8"), playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);

        await tab.DisposeAsync();
    }),
    ("stale replay URL prefetch cannot enable current replay seek", async () =>
    {
        var releaseOldResolve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResolveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewResolve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResolveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamlink = new FakeStreamlinkService();
        streamlink.ResolveStreamUrlOverride = async (request, cancellationToken) =>
        {
            if (request.Target.Url.Contains("/old", StringComparison.Ordinal))
            {
                oldResolveStarted.TrySetResult();
                await releaseOldResolve.Task.WaitAsync(cancellationToken);
                return new StreamlinkResolvedUrl(new Uri("https://example.com/old-replay.m3u8"), "Old resolved.");
            }

            if (request.Target.Url.Contains("/new", StringComparison.Ordinal))
            {
                newResolveStarted.TrySetResult();
                await releaseNewResolve.Task.WaitAsync(cancellationToken);
                return new StreamlinkResolvedUrl(new Uri("https://example.com/new-replay.m3u8"), "New resolved.");
            }

            throw new InvalidOperationException($"Unexpected replay URL {request.Target.Url}.");
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var oldReplay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/old",
            "old",
            DateTimeOffset.UtcNow.AddHours(-1),
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var newReplay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/new",
            "new",
            DateTimeOffset.UtcNow.AddHours(-1),
            TimeSpan.FromHours(1),
            true,
            "",
            "best");
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(oldReplay, newReplay),
            replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Available([])));
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await oldResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(false, tab.CanSeekReplay);

        var refreshReplayAvailability = typeof(StreamTabViewModel).GetMethod(
            "RefreshReplayAvailabilityAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refreshReplayAvailability);
        await ((Task)refreshReplayAvailability!.Invoke(tab, [settings, CancellationToken.None, 0L])!)
            .WaitAsync(TimeSpan.FromSeconds(1));
        await newResolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(false, tab.CanSeekReplay);
        releaseOldResolve.SetResult();
        await Task.Delay(50);
        Assert.Equal(false, tab.CanSeekReplay);

        releaseNewResolve.SetResult();
        await TestWait.UntilAsync(
            () => tab.CanSeekReplay,
            TimeSpan.FromSeconds(1));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(2, streamlink.ResolveStreamUrlCount);
        Assert.Equal(new Uri("https://example.com/new-replay.m3u8"), playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);

        await tab.DisposeAsync();
    }),
    ("stopping playback cancels pending replay URL prefetch", async () =>
    {
        var resolveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolveCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamlink = new FakeStreamlinkService();
        streamlink.ResolveStreamUrlOverride = async (_, cancellationToken) =>
        {
            resolveStarted.TrySetResult();
            using var registration = cancellationToken.Register(() => resolveCancelled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new StreamlinkResolvedUrl(new Uri("https://example.com/unreachable-replay.m3u8"), "Unexpected.");
        };
        var playbackFactory = new FakePlaybackEngineFactory();
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
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
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
        await resolveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(false, tab.CanSeekReplay);

        await tab.StopAsync();

        await resolveCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(false, tab.CanSeekReplay);
        Assert.Equal(false, tab.IsReplaySeekEnabled);
        Assert.Equal("Replay is stopped.", tab.ReplaySeekToolTip);

        await tab.DisposeAsync();
    }),
    ("subsequent live replay seek reuses already-playing replay media", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
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
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
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

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(2, playbackFactory.Engine!.PlayCount);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(20));

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(2, playbackFactory.Engine.PlayCount);
        Assert.Equal(TimeSpan.FromMinutes(20), playbackFactory.Engine.Position);
        Assert.Equal(new Uri("https://example.com/replay.m3u8"), playbackFactory.Engine.LastPlayedUri);
        Assert.True(tab.IsBehindLive);

        await tab.DisposeAsync();
    }),
    ("first live replay seek does not wait for old Streamlink transport disposal", async () =>
    {
        var releaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamlink = new FakeStreamlinkService
        {
            StartExternalHttpOverride = (_, _) => Task.FromResult<IStreamTransportSession>(
                new BlockingTransportSession(releaseDispose.Task, disposeStarted))
        };
        var playbackFactory = new FakePlaybackEngineFactory();
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
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
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

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10)).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, playbackFactory.Engine!.PlayCount);
        Assert.Equal(TimeSpan.FromMinutes(10), playbackFactory.Engine.Position);
        await TestWait.UntilAsync(
            () => disposeStarted.Task.IsCompleted,
            TimeSpan.FromSeconds(1));
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);

        releaseDispose.SetResult();
        await tab.DisposeAsync();
    }),
    ("live replay in-place seek failure reloads replay media", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
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
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
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

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        playbackFactory.Engine!.FailingSeekCount = 1;

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(20));

        Assert.Equal(2, streamlink.ResolveStreamUrlCount);
        Assert.Equal(3, playbackFactory.Engine.PlayCount);
        Assert.Equal(TimeSpan.FromMinutes(20), playbackFactory.Engine.Position);
        Assert.Equal(new Uri("https://example.com/replay.m3u8"), playbackFactory.Engine.LastPlayedUri);
        Assert.True(tab.IsReplayMode);
        Assert.True(tab.IsBehindLive);

        await tab.DisposeAsync();
    }),
    ("stale Twitch replay chat load cannot overwrite later seek chat", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
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
        var replayChatProvider = new BlockingReplayChatProvider();
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
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => tab.CanSeekReplay,
            TimeSpan.FromSeconds(1));
        Assert.True(tab.CanSeekReplay);

        var firstSeek = tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await replayChatProvider.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await firstSeek.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(false, tab.IsReplaySeekInProgress);
        Assert.Equal(false, tab.IsBusy);
        Assert.True(tab.CanSeekReplay);
        Assert.True(tab.RewindReplay30SecondsCommand.CanExecute(null));
        Assert.True(tab.FastForwardReplay30SecondsCommand.CanExecute(null));
        Assert.True(tab.CanReturnToLive);
        Assert.True(tab.ReturnToLiveCommand.CanExecute(null));

        var secondSeek = tab.SeekReplayAsync(TimeSpan.FromMinutes(20));
        await replayChatProvider.FirstLoadCancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await replayChatProvider.SecondLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await secondSeek.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(false, tab.IsReplaySeekInProgress);
        Assert.Equal(false, tab.IsBusy);
        Assert.True(tab.CanSeekReplay);
        Assert.True(tab.RewindReplay30SecondsCommand.CanExecute(null));
        Assert.True(tab.FastForwardReplay30SecondsCommand.CanExecute(null));
        Assert.True(tab.CanReturnToLive);
        Assert.True(tab.ReturnToLiveCommand.CanExecute(null));

        replayChatProvider.ReleaseSecondLoad();
        await TestWait.UntilAsync(
            () => tab.DockedChatMessages.Any(message => message.Message == "seek B chat"),
            TimeSpan.FromSeconds(1));

        Assert.Equal(false, tab.IsReplaySeekInProgress);
        Assert.True(tab.CanSeekReplay);
        Assert.True(tab.RewindReplay30SecondsCommand.CanExecute(null));
        Assert.True(tab.FastForwardReplay30SecondsCommand.CanExecute(null));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "seek B chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "seek A chat"));

        replayChatProvider.ReleaseFirstLoad();
        await replayChatProvider.FirstLoadReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "seek B chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "seek A chat"));

        await tab.DisposeAsync();
    }),
    ("seeking Twitch replay backward renders the earlier chat window", async () =>
    {
        var playbackFactory = new FakePlaybackEngineFactory();
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
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(10),
                    new ChatMessage(PlatformKind.Twitch, "streamer", "early-viewer", "early replay chat", DateTimeOffset.UtcNow)),
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(50),
                    new ChatMessage(PlatformKind.Twitch, "streamer", "late-viewer", "late replay chat", DateTimeOffset.UtcNow))
            ],
            TimeSpan.Zero,
            TimeSpan.FromHours(1)));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        await TestWait.UntilAsync(
            () => tab.DockedChatMessages.Any(message => message.Message == "late replay chat"),
            TimeSpan.FromSeconds(1));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "late replay chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "early replay chat"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await TestWait.UntilAsync(
            () => tab.DockedChatMessages.Any(message => message.Message == "early replay chat"),
            TimeSpan.FromSeconds(1));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "early replay chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "late replay chat"));

        await tab.DisposeAsync();
    }),
    ("repeated replay chat ticks with same visible messages do not rebuild chat collections", async () =>
    {
        var playbackFactory = new FakePlaybackEngineFactory();
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
        var timestamp = DateTimeOffset.UtcNow;
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(9).Add(TimeSpan.FromSeconds(30)),
                    new ChatMessage(PlatformKind.Twitch, "streamer", "viewer-a", "stable replay chat A", timestamp, MessageId: "stable-a")),
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(10),
                    new ChatMessage(PlatformKind.Twitch, "streamer", "viewer-b", "stable replay chat B", timestamp, MessageId: "stable-b"))
            ],
            TimeSpan.Zero,
            TimeSpan.FromHours(1)));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await TestWait.UntilAsync(
            () => tab.DockedChatMessages.Any(message => message.Message == "stable replay chat B"),
            TimeSpan.FromSeconds(1));
        Assert.SequenceEqual(
            new[] { "stable replay chat A", "stable replay chat B" },
            tab.DockedChatMessages.Select(message => message.Message).ToArray());

        await StopReplayClockPollingAsync(tab);

        var collectionChanges = 0;
        tab.DockedChatMessages.CollectionChanged += (_, _) => collectionChanges++;
        var updateWindow = typeof(StreamTabViewModel).GetMethod(
            "UpdateReplayChatWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(updateWindow);
        updateWindow!.Invoke(
            tab,
            [TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(2)), false, null]);

        Assert.Equal(0, collectionChanges);
        Assert.SequenceEqual(
            new[] { "stable replay chat A", "stable replay chat B" },
            tab.DockedChatMessages.Select(message => message.Message).ToArray());

        await tab.DisposeAsync();
    }),
    ("replay chat stays at seek target when VLC clock is unavailable or invalid after seek", async () =>
    {
        await RunClockFallbackCaseAsync(engine => (false, new PlaybackClock(TimeSpan.Zero, null, true)));
        await RunClockFallbackCaseAsync(_ => (true, new PlaybackClock(TimeSpan.MaxValue, TimeSpan.MaxValue, true)));

        static async Task RunClockFallbackCaseAsync(Func<FakePlaybackEngine, (bool IsAvailable, PlaybackClock Clock)> clockOverride)
        {
            var targetOffset = TimeSpan.FromMinutes(10);
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                PlaybackClockOverride = clockOverride
            });
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
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
                [
                    new ReplayChatMessage(
                        targetOffset,
                        new ChatMessage(PlatformKind.Twitch, "streamer", "target-viewer", "target replay chat", DateTimeOffset.UtcNow)),
                    new ReplayChatMessage(
                        TimeSpan.FromMinutes(59).Add(TimeSpan.FromSeconds(45)),
                        new ChatMessage(PlatformKind.Twitch, "streamer", "edge-viewer", "live edge replay chat", DateTimeOffset.UtcNow))
                ],
                TimeSpan.Zero,
                TimeSpan.FromHours(1)));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            tab.SetVideoHandle(new IntPtr(42));

            await tab.StartAsync(settings);
            await tab.SeekReplayAsync(targetOffset);
            await TestWait.UntilAsync(
                () => tab.ReplaySeekValue > targetOffset.TotalSeconds,
                TimeSpan.FromSeconds(2));

            Assert.True(tab.DockedChatMessages.Any(message => message.Message == "target replay chat"));
            Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "live edge replay chat"));

            await tab.DisposeAsync();
        }
    }),
    ("stale old VLC clock after backward replay seek cannot overwrite new chat window", async () =>
    {
        TimeSpan? forcedClockPosition = null;
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackClockOverride = engine =>
                (true, new PlaybackClock(forcedClockPosition ?? engine.Position, engine.Duration, engine.Seekable))
        });
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
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(10),
                    new ChatMessage(PlatformKind.Twitch, "streamer", "early-viewer", "early replay chat", DateTimeOffset.UtcNow)),
                new ReplayChatMessage(
                    TimeSpan.FromMinutes(50),
                    new ChatMessage(PlatformKind.Twitch, "streamer", "late-viewer", "late replay chat", DateTimeOffset.UtcNow))
            ],
            TimeSpan.Zero,
            TimeSpan.FromHours(1)));
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            new FakeStreamlinkService(),
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayResolver: new FakeReplayResolver(replay),
            replayChatProvider: replayChatProvider);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        await TestWait.UntilAsync(
            () => tab.DockedChatMessages.Any(message => message.Message == "late replay chat"),
            TimeSpan.FromSeconds(1));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "late replay chat"));

        forcedClockPosition = TimeSpan.FromMinutes(50);
        await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
        await TestWait.UntilAsync(
            () => tab.ReplaySeekValue > TimeSpan.FromMinutes(10).TotalSeconds,
            TimeSpan.FromSeconds(2));
        await TestWait.UntilAsync(
            () => tab.DockedChatMessages.Any(message => message.Message == "early replay chat"),
            TimeSpan.FromSeconds(1));

        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "early replay chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "late replay chat"));

        forcedClockPosition = null;
        await tab.DisposeAsync();
    }),
    ("current-live DVR captured chat ignores stale old clock after backward seek", async () =>
    {
        TimeSpan? forcedClockPosition = null;
        var blockNextClockRead = 0;
        using var clockReadStarted = new ManualResetEventSlim();
        using var releaseClockRead = new ManualResetEventSlim();
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            PlaybackClockOverride = engine =>
            {
                var result = (true, new PlaybackClock(
                    forcedClockPosition ?? engine.Position,
                    engine.Duration,
                    engine.Seekable));
                if (Interlocked.Exchange(ref blockNextClockRead, 0) == 1)
                {
                    clockReadStarted.Set();
                    if (!releaseClockRead.Wait(TimeSpan.FromSeconds(2)))
                    {
                        throw new TimeoutException("Timed out waiting to release the stale replay clock sample.");
                    }
                }

                return result;
            }
        });
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
            PlatformKind.Twitch,
            "streamer",
            "early-viewer",
            "early captured chat",
            startedAt.AddMinutes(10),
            MessageId: "early-captured-chat"));
        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Twitch,
            "streamer",
            "late-viewer",
            "late captured chat",
            startedAt.AddMinutes(50),
            MessageId: "late-captured-chat"));

        await tab.SeekReplayAsync(TimeSpan.FromMinutes(50));
        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "late captured chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "early captured chat"));

        await StopReplayClockPollingAsync(tab);
        forcedClockPosition = TimeSpan.FromMinutes(50);
        Volatile.Write(ref blockNextClockRead, 1);
        var staleClockUpdate = Task.CompletedTask;
        try
        {
            staleClockUpdate = Task.Run(() => InvokeReplayClockUpdate(tab));
            Assert.True(clockReadStarted.Wait(TimeSpan.FromSeconds(1)));

            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            // SeekReplayAsync starts a fresh poller. Stop it so the deliberately stale update is
            // the final clock-driven chat refresh before the assertions below.
            await StopReplayClockPollingAsync(tab);
        }
        finally
        {
            releaseClockRead.Set();
        }

        await staleClockUpdate.WaitAsync(TimeSpan.FromSeconds(1));
        AssertNear(TimeSpan.FromMinutes(10).TotalSeconds, tab.ReplaySeekValue, tolerance: 0.1);

        Assert.True(tab.DockedChatMessages.Any(message => message.Message == "early captured chat"));
        Assert.Equal(false, tab.DockedChatMessages.Any(message => message.Message == "late captured chat"));

        forcedClockPosition = null;
        await tab.DisposeAsync();
    }),
    ("toast thumbnail reads enforce the streaming byte limit", async () =>
    {
        var readThumbnailBytes = typeof(ToastLiveNotificationService).GetMethod(
            "ReadThumbnailBytesAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(readThumbnailBytes);

        using var smallContent = new ByteArrayContent([1, 2, 3]);
        var smallResult = await (Task<byte[]?>)readThumbnailBytes!.Invoke(
            null,
            [smallContent, CancellationToken.None])!;
        Assert.SequenceEqual(new byte[] { 1, 2, 3 }, smallResult ?? []);

        using var oversizedContent = new ByteArrayContent(new byte[(8 * 1024 * 1024) + 1]);
        var oversizedResult = await (Task<byte[]?>)readThumbnailBytes.Invoke(
            null,
            [oversizedContent, CancellationToken.None])!;
        Assert.Equal<byte[]?>(null, oversizedResult);
    }),
    ("live notification delivery gate invalidates queued work when disabled", () =>
    {
        using var gate = new LiveNotificationDeliveryGate();
        Assert.True(gate.IsEnabled);
        Assert.True(gate.TryBegin(out var initialGeneration));

        var deliveries = 0;
        gate.IsEnabled = false;
        Assert.Equal(false, gate.IsEnabled);
        Assert.Equal(false, gate.TryRunIfCurrent(initialGeneration, () => deliveries++));

        gate.IsEnabled = true;
        Assert.True(gate.IsEnabled);
        Assert.Equal(false, gate.TryRunIfCurrent(initialGeneration, () => deliveries++));
        Assert.True(gate.TryBegin(out var currentGeneration));
        Assert.True(gate.TryRunIfCurrent(currentGeneration, () => deliveries++));
        Assert.Equal(1, deliveries);

        gate.Dispose();
        Assert.Equal(false, gate.IsEnabled);
        Assert.Equal(false, gate.TryBegin(out _));
        Assert.Equal(false, gate.TryRunIfCurrent(currentGeneration, () => deliveries++));
        Assert.Equal(1, deliveries);
        return Task.CompletedTask;
    }),
    ("native replay overlay renderer emits transparent blank frame for empty messages", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var frame = NativeOverlayChatFrameRenderer.TryBuildFrame(
                Array.Empty<ChatMessage>(),
                new ChatSettings { DockWidth = 340 },
                18,
                1080,
                null,
                TimeSpan.Zero,
                out var width,
                out var height)?.Frame;

            Assert.NotNull(frame);
            Assert.True(width >= NativeOverlaySizing.MinWidth);
            Assert.True(height >= NativeOverlaySizing.MinHeight);
            AssertNativeOverlayTransparentFrame(frame!);
        });
    }),
    ("native replay overlay renderer honors saved reference size above old cap", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-renderer-size-{Guid.NewGuid():N}.txt");
            File.WriteAllText($"{positionStatePath}.size", "reference 1440 900");
            try
            {
                var message = new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    "sized native replay frame",
                    DateTimeOffset.UtcNow,
                    "#8AB4F8");
                var frame = NativeOverlayChatFrameRenderer.TryBuildFrame(
                    [message],
                    new ChatSettings { DockWidth = 340 },
                    18,
                    1080,
                    positionStatePath,
                    TimeSpan.Zero,
                    out var width,
                    out var height)?.Frame;

                Assert.NotNull(frame);
                Assert.Equal(1440, width);
                Assert.Equal(900, height);
                var renderedFrame = frame!;
                Assert.Equal(1440, (int)BinaryPrimitives.ReadUInt32LittleEndian(renderedFrame.AsSpan(24, 4)));
                Assert.Equal(900, (int)BinaryPrimitives.ReadUInt32LittleEndian(renderedFrame.AsSpan(28, 4)));
                AssertNativeOverlayChatFrame(renderedFrame);
            }
            finally
            {
                try
                {
                    File.Delete($"{positionStatePath}.size");
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        });
    }),
    ("native replay overlay renderer keeps live content metrics for width-only resize", () =>
    {
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-renderer-width-layout-{Guid.NewGuid():N}.txt");
        File.WriteAllText($"{positionStatePath}.size", "reference 680 292");
        try
        {
            var settings = new ChatSettings { DockWidth = 340 };
            var defaultLayout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                settings,
                18,
                1080,
                null);
            var resizedLayout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                settings,
                18,
                1080,
                positionStatePath);

            Assert.Equal(340, defaultLayout.FrameWidth);
            Assert.Equal(292, defaultLayout.FrameHeight);
            Assert.Equal(18d, defaultLayout.EffectiveReferenceFontSize);
            Assert.Equal(680, resizedLayout.FrameWidth);
            Assert.Equal(292, resizedLayout.FrameHeight);
            Assert.Equal(18d, resizedLayout.EffectiveReferenceFontSize);
            Assert.Equal(defaultLayout.Presentation, resizedLayout.Presentation);
        }
        finally
        {
            try
            {
                File.Delete($"{positionStatePath}.size");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Task.CompletedTask;
    }),
    ("native replay overlay renderer keeps live content metrics when width and height grow", () =>
    {
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-renderer-layout-{Guid.NewGuid():N}.txt");
        File.WriteAllText($"{positionStatePath}.size", "reference 680 584");
        try
        {
            var settings = new ChatSettings { DockWidth = 340 };
            var defaultLayout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                settings,
                18,
                1080,
                null);
            var resizedLayout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                settings,
                18,
                1080,
                positionStatePath);

            Assert.Equal(340, defaultLayout.FrameWidth);
            Assert.Equal(292, defaultLayout.FrameHeight);
            Assert.Equal(18d, defaultLayout.EffectiveReferenceFontSize);
            Assert.Equal(680, resizedLayout.FrameWidth);
            Assert.Equal(584, resizedLayout.FrameHeight);
            Assert.Equal(18d, resizedLayout.EffectiveReferenceFontSize);
            Assert.Equal(defaultLayout.Presentation, resizedLayout.Presentation);
        }
        finally
        {
            try
            {
                File.Delete($"{positionStatePath}.size");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Task.CompletedTask;
    }),
    ("native replay overlay renderer keeps live content metrics for height-only resize", () =>
    {
        var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-renderer-height-layout-{Guid.NewGuid():N}.txt");
        File.WriteAllText($"{positionStatePath}.size", "reference 340 584");
        try
        {
            var settings = new ChatSettings { DockWidth = 340 };
            var defaultLayout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                settings,
                15,
                1080,
                null);
            var resizedLayout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                settings,
                15,
                1080,
                positionStatePath);

            Assert.Equal(340, resizedLayout.FrameWidth);
            Assert.Equal(584, resizedLayout.FrameHeight);
            Assert.Equal(defaultLayout.Presentation, resizedLayout.Presentation);
        }
        finally
        {
            File.Delete($"{positionStatePath}.size");
        }

        return Task.CompletedTask;
    }),
    ("native replay overlay renderer uses live typography colors and spacing", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var layout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                new ChatSettings { DockWidth = 340 },
                15,
                1080,
                null);
            var presentation = layout.Presentation;

            Assert.Equal(15, presentation.MessageFontSize);
            Assert.Equal(13, presentation.SystemFontSize);
            Assert.Equal(20, presentation.MessageFontCellHeight);
            Assert.Equal(17, presentation.SystemFontCellHeight);
            Assert.Equal(2, presentation.LineGap);
            Assert.Equal(2, presentation.MessageGap);
            Assert.Equal(24, presentation.EmoteHeight);
            Assert.Equal(96, presentation.EmoteMaxWidth);
            Assert.Equal(1, presentation.ShadowOffset);

            var timestamp = new DateTimeOffset(2026, 8, 3, 14, 37, 0, TimeSpan.Zero);
            var block = NativeOverlayChatFrameRenderer.CreateMessageBlock(
                new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    "hello chat",
                    timestamp,
                    "#010203"),
                presentation);
            var runs = block.Inlines.OfType<Run>().ToArray();
            var renderedText = string.Concat(runs.Select(run => run.Text));

            Assert.Equal("viewer: hello chat", renderedText);
            Assert.DoesNotContain("14:37", renderedText);
            Assert.Equal("Segoe UI", block.FontFamily.Source);
            Assert.Equal(FontWeights.Bold, block.FontWeight);
            Assert.Equal(TextWrapping.WrapWithOverflow, block.TextWrapping);
            Assert.Equal(LineStackingStrategy.BlockLineHeight, block.LineStackingStrategy);
            Assert.Equal(22d, block.LineHeight);
            Assert.Equal("#FFFDE68A", ((SolidColorBrush)runs[0].Foreground).Color.ToString());
            Assert.True(runs.Skip(1).All(run =>
                ((SolidColorBrush)run.Foreground).Color == Colors.White));
            Assert.True(block.Effect is System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius: 0,
                ShadowDepth: 1,
                Opacity: 1,
                Color: var shadowColor
            } && shadowColor == Colors.Black);

            var systemBlock = NativeOverlayChatFrameRenderer.CreateMessageBlock(
                new ChatMessage(
                    PlatformKind.Kick,
                    "streamer",
                    "system",
                    "replay chat unavailable",
                    timestamp),
                presentation);
            var systemRuns = systemBlock.Inlines.OfType<Run>().ToArray();
            Assert.Equal("system: replay chat unavailable", string.Concat(systemRuns.Select(run => run.Text)));
            Assert.Equal(FontWeights.Normal, systemBlock.FontWeight);
            Assert.Equal(13d, systemBlock.FontSize);
            Assert.Equal(19d, systemBlock.LineHeight);
            Assert.True(systemRuns.All(run =>
                ((SolidColorBrush)run.Foreground).Color == Color.FromRgb(0x93, 0xC5, 0xFD)));

            var systemEmoteBlock = NativeOverlayChatFrameRenderer.CreateMessageBlock(
                new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "system",
                    "Spin",
                    timestamp,
                    Emotes:
                    [
                        new ChatEmote(0, 4, "Spin", "https://example.invalid/system-emote.png")
                    ]),
                presentation);
            Assert.Equal(
                "system: Spin",
                string.Concat(systemEmoteBlock.Inlines.OfType<Run>().Select(run => run.Text)));
            Assert.Equal(0, systemEmoteBlock.Inlines.OfType<InlineUIContainer>().Count());
        });
    }),
    ("native replay overlay renders Unicode emoji as colored images", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var apple = char.ConvertFromUtf32(0x1F34E);
            var presentation = NativeOverlayChatPresentation.Create(15, 1080);
            var block = NativeOverlayChatFrameRenderer.CreateMessageBlock(
                new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    apple,
                    DateTimeOffset.UtcNow),
                presentation);

            var emojiImages = block.Inlines
                .OfType<InlineUIContainer>()
                .Select(container => container.Child)
                .OfType<Image>()
                .Where(image => image is not AnimatedEmoteImage)
                .ToArray();

            Assert.Equal(1, emojiImages.Length);
            Assert.Equal(apple, emojiImages[0].ToolTip as string);
            Assert.Equal(presentation.EmoteHeight, emojiImages[0].Height);
            Assert.True(
                BitmapAssert.CountColoredPixels(emojiImages[0].Source) > 0,
                "Expected the native overlay emoji image to retain its color.");

            var frame = NativeOverlayChatFrameRenderer.TryBuildFrame(
                [
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        "viewer",
                        apple,
                        DateTimeOffset.UtcNow)
                ],
                new ChatSettings
                {
                    DockWidth = 340,
                    VlcOverlayFontSize = 15
                },
                15,
                1080,
                null,
                TimeSpan.Zero,
                out _,
                out _);
            Assert.NotNull(frame);
            var frameRedPixels = BitmapAssert.CountRgbaPixels(
                frame!.Frame,
                36,
                (r, g, b) => r > 150 && g < 120 && b < 120);
            Assert.True(
                frameRedPixels > 20,
                $"The final native overlay frame must retain the apple's red pixels (red={frameRedPixels}).");
        });
    }),
    ("native replay overlay renderer uses live badge and emote dimensions", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            var emoteUrl = $"https://example.invalid/wide-native-overlay-{Guid.NewGuid():N}.png";
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                emoteUrl,
                maxImageBytes,
                [Colors.Lime],
                [TimeSpan.FromMilliseconds(100)],
                width: 200,
                height: 40);
            try
            {
                var presentation = NativeOverlayChatPresentation.Create(15, 1080);
                var block = NativeOverlayChatFrameRenderer.CreateMessageBlock(
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        "viewer",
                        "WideEmote",
                        DateTimeOffset.UtcNow,
                        Badges:
                        [
                            new ChatBadge(
                                "future",
                                "1",
                                "Future",
                                "https://example.invalid/badge.png")
                        ],
                        Emotes:
                        [
                            new ChatEmote(
                                0,
                                9,
                                "WideEmote",
                                emoteUrl)
                        ]),
                    presentation);
                var images = block.Inlines
                    .OfType<InlineUIContainer>()
                    .Select(container => container.Child)
                    .OfType<AnimatedEmoteImage>()
                    .ToArray();
                var badge = images.Single(image => Equals(image.ToolTip, "Future (1)"));
                var emote = images.Single(image => Equals(image.ToolTip, "WideEmote"));

                Assert.Equal(20d, badge.Height);
                Assert.Equal(20d, badge.Width);
                Assert.Equal(24d, emote.Height);
                Assert.Equal(96d, emote.Width);
                Assert.Equal(96d, emote.MaxWidth);
                Assert.Equal(26d, block.LineHeight);
                Assert.Equal(new Thickness(0), badge.Margin);
                Assert.Equal(new Thickness(0), emote.Margin);
            }
            finally
            {
                AnimatedEmoteImage.RemoveCachedImageForTest(emoteUrl, maxImageBytes);
            }
        });
    }),
    ("native replay overlay renderer scales live metrics only with video resolution", () =>
    {
        var settings = new ChatSettings { DockWidth = 340 };
        var at720p = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(settings, 15, 720, null);
        var at1080p = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(settings, 15, 1080, null);
        var at2160p = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(settings, 15, 2160, null);

        Assert.Equal(227, at720p.FrameWidth);
        Assert.Equal(195, at720p.FrameHeight);
        Assert.Equal(10, at720p.Presentation.MessageFontSize);
        Assert.Equal(16, at720p.Presentation.EmoteHeight);
        Assert.Equal(64, at720p.Presentation.EmoteMaxWidth);
        Assert.Equal(1, at720p.Presentation.MessageGap);

        Assert.Equal(340, at1080p.FrameWidth);
        Assert.Equal(292, at1080p.FrameHeight);
        Assert.Equal(15, at1080p.Presentation.MessageFontSize);

        Assert.Equal(680, at2160p.FrameWidth);
        Assert.Equal(584, at2160p.FrameHeight);
        Assert.Equal(30, at2160p.Presentation.MessageFontSize);
        Assert.Equal(48, at2160p.Presentation.EmoteHeight);
        Assert.Equal(192, at2160p.Presentation.EmoteMaxWidth);
        Assert.Equal(4, at2160p.Presentation.MessageGap);
        Assert.Equal(2, at2160p.Presentation.ShadowOffset);
        return Task.CompletedTask;
    }),
    ("native replay overlay renderer supports every live font size", () =>
    {
        return TestSta.RunAsync(() =>
        {
            for (var referenceFontSize = 8; referenceFontSize <= 36; referenceFontSize++)
            {
                var presentation = NativeOverlayChatPresentation.Create(referenceFontSize, 1080);
                var block = NativeOverlayChatFrameRenderer.CreateMessageBlock(
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        "viewer",
                        "font metrics",
                        DateTimeOffset.UtcNow),
                    presentation);

                Assert.Equal(referenceFontSize, presentation.MessageFontSize);
                Assert.Equal(referenceFontSize, (int)block.FontSize);
                Assert.Equal(
                    presentation.MessageFontCellHeight + presentation.LineGap,
                    (int)block.LineHeight);
            }
        });
    }),
    ("native replay overlay renderer wraps whole tokens and excludes a clipped oldest message", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var presentation = NativeOverlayChatPresentation.Create(15, 1080);
            var oversizedTokenBlock = NativeOverlayChatFrameRenderer.CreateMessageBlock(
                new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "u",
                    new string('W', 80),
                    DateTimeOffset.UtcNow),
                presentation);
            oversizedTokenBlock.Measure(new System.Windows.Size(90, double.PositiveInfinity));
            Assert.Equal(TextWrapping.WrapWithOverflow, oversizedTokenBlock.TextWrapping);
            Assert.Equal(44d, oversizedTokenBlock.DesiredSize.Height);

            var layout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                new ChatSettings { DockWidth = 220 },
                15,
                1080,
                null) with
            {
                FrameHeight = 120,
                ReferenceHeight = 120
            };
            var messages = Enumerable.Range(0, 30)
                .Select(index => new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    $"viewer{index}",
                    "short",
                    DateTimeOffset.UtcNow,
                    MessageId: index.ToString(CultureInfo.InvariantCulture)))
                .ToArray();
            var selection = NativeOverlayChatFrameRenderer.MeasureVisibleMessages(messages, layout);

            Assert.Equal(18, selection.CandidateLimit);
            Assert.Equal(2, selection.MessageBlocks.Count);
            Assert.SequenceEqual(
                new[] { "28", "29" },
                selection.MessageBlocks.Select(block => block.Message!.MessageId));
            Assert.Equal(46, selection.UsedHeight);
            Assert.Equal(68, selection.AvailableHeight);
        });
    }),
    ("native replay overlay renderer caps newest-first measurement at 256 messages", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var layout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                new ChatSettings { DockWidth = 340 },
                8,
                1080,
                null) with
            {
                FrameHeight = 100_000,
                ReferenceHeight = 100_000
            };
            var messages = Enumerable.Range(0, 300)
                .Select(index => new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "v",
                    index.ToString(CultureInfo.InvariantCulture),
                    DateTimeOffset.UtcNow,
                    MessageId: index.ToString(CultureInfo.InvariantCulture)))
                .ToArray();
            var selection = NativeOverlayChatFrameRenderer.MeasureVisibleMessages(messages, layout);

            Assert.Equal(256, selection.CandidateLimit);
            Assert.Equal(256, selection.MessageBlocks.Count);
            Assert.Equal("44", selection.MessageBlocks[0].Message!.MessageId);
            Assert.Equal("299", selection.MessageBlocks[^1].Message!.MessageId);
        });
    }),
    ("native replay overlay renderer selects newest intermediate and oldest full pages", () =>
    {
        return TestSta.RunAsync(() =>
        {
            var layout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                new ChatSettings { DockWidth = 220 },
                15,
                1080,
                null) with
            {
                FrameHeight = 250,
                ReferenceHeight = 250
            };
            var messages = Enumerable.Range(0, 12)
                .Select(index => new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "u",
                    index == 0 ? new string('W', 60) : "short",
                    DateTimeOffset.UtcNow,
                    MessageId: index.ToString(CultureInfo.InvariantCulture)))
                .ToArray();

            var newest = NativeOverlayChatFrameRenderer.MeasureVisibleMessages(messages, layout);
            var intermediate = NativeOverlayChatFrameRenderer.MeasureVisibleMessages(messages, layout, 3);
            var oldest = NativeOverlayChatFrameRenderer.MeasureVisibleMessages(messages, layout, int.MaxValue);
            var negative = NativeOverlayChatFrameRenderer.MeasureVisibleMessages(messages, layout, -100);

            Assert.Equal(0, newest.RenderedSelection.MessageOffset);
            Assert.Equal(11, newest.RenderedSelection.NewestMessageIndex);
            Assert.Equal(3, intermediate.RenderedSelection.MessageOffset);
            Assert.Equal(8, intermediate.RenderedSelection.NewestMessageIndex);
            Assert.Equal(0, negative.RenderedSelection.MessageOffset);
            Assert.Equal(11, negative.RenderedSelection.NewestMessageIndex);

            Assert.True(oldest.RenderedSelection.MaximumMessageOffset > 0);
            Assert.Equal(
                oldest.RenderedSelection.MaximumMessageOffset,
                oldest.RenderedSelection.MessageOffset);
            Assert.Equal(0, oldest.RenderedSelection.OldestMessageIndex);
            Assert.Equal("0", oldest.MessageBlocks[0].Message!.MessageId);
            Assert.Equal(
                messages.Length - 1,
                oldest.RenderedSelection.NewestMessageIndex + oldest.RenderedSelection.MessageOffset);
            Assert.True(oldest.UsedHeight <= oldest.AvailableHeight);
            Assert.True(
                oldest.MessageBlocks[0].Height > layout.Presentation.MessageFontCellHeight,
                "Expected the oldest page to include a wrapped message.");
        });
    }),
    ("native replay overlay scrollbar state matches the VLC live-overlay protocol", () =>
    {
        var message = NativeOverlayChatFrameRenderer.BuildScrollbarStateFrameMessage(
            new NativeReplayOverlayRenderedSelection(
                MessageOffset: 3,
                MaximumMessageOffset: 9,
                OldestMessageIndex: 2,
                NewestMessageIndex: 6),
            totalMessageCount: 12);

        AssertNativeOverlayScrollbarStateFrame(
            message,
            expectedMessageOffset: 3,
            expectedMaximumMessageOffset: 9,
            expectedVisibleMessageCount: 5,
            expectedTotalMessageCount: 12);
        return Task.CompletedTask;
    }),
    ("native replay overlay renderer advances cached animated emote frames by animation clock", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            var imageUrl = $"https://example.invalid/replay-animated-{Guid.NewGuid():N}.gif";
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                imageUrl,
                maxImageBytes,
                [Colors.Red, Colors.Lime],
                [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)],
                16,
                16);
            try
            {
                var message = new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    "Spin",
                    DateTimeOffset.UtcNow,
                    "#8AB4F8",
                    Emotes: [new ChatEmote(0, 4, "Spin", imageUrl)],
                    MessageId: "native-replay-animated-renderer");

                var first = NativeOverlayChatFrameRenderer.TryBuildFrame(
                    [message],
                    new ChatSettings { DockWidth = 340 },
                    18,
                    1080,
                    null,
                    TimeSpan.Zero,
                    out _,
                    out _);
                var second = NativeOverlayChatFrameRenderer.TryBuildFrame(
                    [message],
                    new ChatSettings { DockWidth = 340 },
                    18,
                    1080,
                    null,
                    TimeSpan.FromMilliseconds(150),
                    out _,
                    out _);

                Assert.NotNull(first);
                Assert.NotNull(second);
                Assert.True(first!.HasAnimatedContent);
                Assert.True(second!.HasAnimatedContent);
                Assert.Equal(false, first.Frame.SequenceEqual(second.Frame));
                Assert.Equal(false, first.HasPendingImageLoads);
                Assert.Equal(false, second.HasPendingImageLoads);
            }
            finally
            {
                AnimatedEmoteImage.RemoveCachedImageForTest(imageUrl, maxImageBytes);
            }
        });
    }),
    ("native replay overlay animates Twitch VOD emotes parsed from replay metadata", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            const string staticImageUrl = "https://static-cdn.jtvnw.net/emoticons/v2/25/static/light/2.0";
            const string animatedImageUrl = "https://static-cdn.jtvnw.net/emoticons/v2/25/animated/light/2.0";
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                staticImageUrl,
                maxImageBytes,
                [Colors.Red, Colors.Blue],
                [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)],
                16,
                16);
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                animatedImageUrl,
                maxImageBytes,
                [Colors.Red, Colors.Blue],
                [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)],
                16,
                16);
            try
            {
                using var document = JsonDocument.Parse("""
                {
                  "comments": [
                    {
                      "_id": "vod-animated-emote",
                      "content_offset_seconds": 12,
                      "message": {
                        "body": "Kappa",
                        "emoticons": [{ "_id": "25", "begin": 0, "end": 4 }]
                      }
                    }
                  ]
                }
                """);
                var replay = new ReplaySessionInfo(
                    PlatformKind.Twitch,
                    "streamer",
                    "https://www.twitch.tv/videos/123",
                    "123",
                    new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
                    TimeSpan.FromHours(1),
                    true,
                    "");
                var message = ReplayChatProvider
                    .ReadTwitchDownloaderMessages(document.RootElement, replay)
                    .Single()
                    .Message;

                Assert.Equal(staticImageUrl, message.Emotes!.Single().ImageUrl);
                var first = NativeOverlayChatFrameRenderer.TryBuildFrame(
                    [message],
                    new ChatSettings { DockWidth = 340 },
                    18,
                    1080,
                    null,
                    TimeSpan.Zero,
                    out _,
                    out _);
                var second = NativeOverlayChatFrameRenderer.TryBuildFrame(
                    [message],
                    new ChatSettings { DockWidth = 340 },
                    18,
                    1080,
                    null,
                    TimeSpan.FromMilliseconds(150),
                    out _,
                    out _);

                Assert.NotNull(first);
                Assert.NotNull(second);
                Assert.True(first!.HasAnimatedContent);
                Assert.True(second!.HasAnimatedContent);
                Assert.Equal(false, first.Frame.SequenceEqual(second.Frame));
                Assert.Equal(false, first.HasPendingImageLoads);
                Assert.Equal(false, second.HasPendingImageLoads);
            }
            finally
            {
                AnimatedEmoteImage.RemoveCachedImageForTest(staticImageUrl, maxImageBytes);
                AnimatedEmoteImage.RemoveCachedImageForTest(animatedImageUrl, maxImageBytes);
            }
        });
    }),
    ("native replay overlay renderer does not request animation loop for static emotes", () =>
    {
        return TestSta.RunAsync(() =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            var imageUrl = $"https://example.invalid/replay-static-{Guid.NewGuid():N}.png";
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                imageUrl,
                maxImageBytes,
                [Colors.DeepSkyBlue],
                [TimeSpan.FromMilliseconds(100)],
                16,
                16);
            try
            {
                var message = new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    "Still",
                    DateTimeOffset.UtcNow,
                    "#8AB4F8",
                    Emotes: [new ChatEmote(0, 5, "Still", imageUrl)],
                    MessageId: "native-replay-static-renderer");

                var frame = NativeOverlayChatFrameRenderer.TryBuildFrame(
                    [message],
                    new ChatSettings { DockWidth = 340 },
                    18,
                    1080,
                    null,
                    TimeSpan.FromMilliseconds(150),
                    out _,
                    out _);

                Assert.NotNull(frame);
                Assert.Equal(false, frame!.HasAnimatedContent);
                Assert.Equal<TimeSpan?>(null, frame.NextAnimationFrameDelay);
                Assert.Equal(false, frame.HasPendingImageLoads);
            }
            finally
            {
                AnimatedEmoteImage.RemoveCachedImageForTest(imageUrl, maxImageBytes);
            }
        });
    }),
    ("native replay overlay animation schedules from the frame deadline", () =>
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(20),
            StreamTabViewModel.CalculateNativeReplayOverlayAnimationDelay(
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.Zero));
        Assert.Equal(
            TimeSpan.FromMilliseconds(5),
            StreamTabViewModel.CalculateNativeReplayOverlayAnimationDelay(
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(15)));
        Assert.Equal(
            TimeSpan.Zero,
            StreamTabViewModel.CalculateNativeReplayOverlayAnimationDelay(
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(25)));
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            StreamTabViewModel.CalculateNativeReplayOverlayAnimationDelay(
                TimeSpan.Zero,
                null,
                TimeSpan.Zero));
        return Task.CompletedTask;
    }),
    ("native replay overlay refreshes animation time across chat frame invalidations", () =>
    {
        var state = new NativeReplayOverlayRenderState();

        var initial = state.BeginRender(
            "frame-a",
            forceAnimationRepaint: false,
            repaintAnimationClock: TimeSpan.Zero);
        Assert.NotNull(initial);
        Assert.Equal(TimeSpan.Zero, initial!.Value.AnimationClock);

        var animated = state.BeginRender(
            "frame-a",
            forceAnimationRepaint: true,
            repaintAnimationClock: TimeSpan.FromMilliseconds(350));
        Assert.NotNull(animated);
        Assert.Equal(TimeSpan.FromMilliseconds(350), animated!.Value.AnimationClock);

        state.InvalidateFrameKey();
        var invalidated = state.BeginRender(
            "frame-a",
            forceAnimationRepaint: false,
            repaintAnimationClock: TimeSpan.FromMilliseconds(725));
        Assert.NotNull(invalidated);
        Assert.Equal(TimeSpan.FromMilliseconds(725), invalidated!.Value.AnimationClock);

        var changedChat = state.BeginRender(
            "frame-b",
            forceAnimationRepaint: false,
            repaintAnimationClock: TimeSpan.FromMilliseconds(1100));
        Assert.NotNull(changedChat);
        Assert.Equal(TimeSpan.FromMilliseconds(1100), changedChat!.Value.AnimationClock);

        state.Reset();
        var nextSession = state.BeginRender(
            "frame-c",
            forceAnimationRepaint: false,
            repaintAnimationClock: TimeSpan.Zero);
        Assert.NotNull(nextSession);
        Assert.Equal(TimeSpan.Zero, nextSession!.Value.AnimationClock);
        return Task.CompletedTask;
    }),
    ("native replay overlay scheduler renders on background STA thread", async () =>
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var rendered = new TaskCompletionSource<NativeReplayOverlayFrameResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = await NativeReplayOverlayFrameScheduler.CreateAsync(
            new MemoryLogger(),
            result => rendered.TrySetResult(result));

        scheduler.QueueRender(new NativeReplayOverlayFrameRequest(
            1,
            "unused-pipe",
            [
                new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    "background STA render",
                    DateTimeOffset.UtcNow,
                    MessageId: "background-sta-render")
            ],
            new ChatSettings { DockWidth = 340 },
            18,
            1080,
            null,
            "background-sta-render"));

        var result = await rendered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Succeeded);
        Assert.Equal(ApartmentState.STA, result.RenderThreadApartmentState);
        Assert.Equal(false, callerThreadId == result.RenderThreadId);
        AssertNativeOverlayChatFrame(result.Frame!);
    }),
    ("native replay overlay scheduler coalesces rapid updates to latest frame", async () =>
    {
        var results = new List<NativeReplayOverlayFrameResult>();
        var resultsGate = new object();
        var latestRendered = new TaskCompletionSource<NativeReplayOverlayFrameResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = await NativeReplayOverlayFrameScheduler.CreateAsync(
            new MemoryLogger(),
            result =>
            {
                lock (resultsGate)
                {
                    results.Add(result);
                }

                if (result.Request.Version == 25)
                {
                    latestRendered.TrySetResult(result);
                }
            });

        for (var version = 1; version <= 25; version++)
        {
            scheduler.QueueRender(new NativeReplayOverlayFrameRequest(
                version,
                "unused-pipe",
                [
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        "viewer",
                        $"rapid render {version}",
                        DateTimeOffset.UtcNow,
                        MessageId: $"rapid-render-{version}")
                ],
                new ChatSettings { DockWidth = 340 },
                18,
                1080,
                null,
                $"rapid-render-{version}"));
        }

        var latest = await latestRendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        NativeReplayOverlayFrameResult[] renderedResults;
        lock (resultsGate)
        {
            renderedResults = results.ToArray();
        }

        Assert.True(latest.Succeeded);
        Assert.Equal(25, latest.Request.Version);
        Assert.Equal(ApartmentState.STA, latest.RenderThreadApartmentState);
        Assert.True(renderedResults.Length < 25);
        Assert.Equal(25, renderedResults[^1].Request.Version);
    }),
    ("native replay overlay write gate keeps only latest pending frame", async () =>
    {
        var currentVersion = 1L;
        var writes = new List<long>();
        var writesGate = new object();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            async (request, _) =>
            {
                lock (writesGate)
                {
                    writes.Add(request.Version);
                }

                if (request.Version == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }

                if (request.Version == 3)
                {
                    latestWritten.TrySetResult();
                }

                return new NativeReplayOverlayFrameWriteResult(true, null);
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50));

        writeGate.QueueWrite("unused-pipe", [1], 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref currentVersion, 2);
        writeGate.QueueWrite("unused-pipe", [2], 2);
        Volatile.Write(ref currentVersion, 3);
        writeGate.QueueWrite("unused-pipe", [3], 3);

        releaseFirst.TrySetResult();
        await latestWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        long[] observedWrites;
        lock (writesGate)
        {
            observedWrites = writes.ToArray();
        }

        Assert.SequenceEqual(new[] { 1L, 3L }, observedWrites);
    }),
    ("native replay overlay write gate discards pending frame after invalidation", async () =>
    {
        var currentVersion = 1L;
        var writes = new List<long>();
        var writesGate = new object();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedWrite = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            async (request, _) =>
            {
                lock (writesGate)
                {
                    writes.Add(request.Version);
                }

                if (request.Version == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                else
                {
                    unexpectedWrite.TrySetResult(request.Version);
                }

                return new NativeReplayOverlayFrameWriteResult(true, null);
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50));

        writeGate.QueueWrite("unused-pipe", [1], 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref currentVersion, 2);
        writeGate.QueueWrite("unused-pipe", [2], 2);
        Volatile.Write(ref currentVersion, 3);
        writeGate.Invalidate();

        releaseFirst.TrySetResult();
        var timeout = Task.Delay(300);
        var completed = await Task.WhenAny(unexpectedWrite.Task, timeout);
        Assert.Equal(timeout, completed);

        long[] observedWrites;
        lock (writesGate)
        {
            observedWrites = writes.ToArray();
        }

        Assert.SequenceEqual(new[] { 1L }, observedWrites);
    }),
    ("native replay overlay write gate cancels an active transient frame after invalidation", async () =>
    {
        var currentVersion = 1L;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            async (request, cancellationToken) =>
            {
                if (request.Version == 1)
                {
                    firstStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.TrySetResult();
                        throw;
                    }
                }

                replacementWritten.TrySetResult();
                return new NativeReplayOverlayFrameWriteResult(true, null);
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50));

        writeGate.QueueWrite("transient-frame-pipe", [1], 1, writeKind: "blank-frame");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref currentVersion, 2);
        writeGate.Invalidate();
        writeGate.QueueWrite("transient-frame-pipe", [2], 2, writeKind: "chat-frame");

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await replacementWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }),
    ("native replay overlay write gate ignores its own cancellation as a write failure", async () =>
    {
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = 0;
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            async (_, cancellationToken) =>
            {
                writeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    return new NativeReplayOverlayFrameWriteResult(
                        false,
                        new OperationCanceledException("Write was canceled by invalidation."));
                }

                return new NativeReplayOverlayFrameWriteResult(false, null);
            },
            () => 1,
            _ => Interlocked.Increment(ref failures),
            TimeSpan.FromMilliseconds(50));

        writeGate.QueueWrite("critical-cancel-pipe", [1], 1, isCritical: true, writeKind: "blank-frame");
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        writeGate.Invalidate();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(0, failures);
    }),
    ("native replay overlay write gate keeps critical clear through normal invalidation", async () =>
    {
        var currentVersion = 1L;
        var currentPipeName = "critical-pipe";
        var writes = new List<byte>();
        var writesGate = new object();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalAfterCriticalWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            async (request, _) =>
            {
                lock (writesGate)
                {
                    writes.Add(request.Frame[0]);
                }

                if (request.Frame[0] == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }

                if (request.Frame[0] == 3)
                {
                    normalAfterCriticalWritten.TrySetResult();
                }

                return new NativeReplayOverlayFrameWriteResult(true, null);
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => currentPipeName);

        writeGate.QueueWrite(currentPipeName, [1], 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        writeGate.QueueWrite(
            currentPipeName,
            [2],
            1,
            isCritical: true,
            writeKind: "critical-clear");
        Volatile.Write(ref currentVersion, 2);
        writeGate.Invalidate();
        writeGate.QueueWrite(currentPipeName, [3], 2);

        releaseFirst.TrySetResult();
        await normalAfterCriticalWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        byte[] observedWrites;
        lock (writesGate)
        {
            observedWrites = writes.ToArray();
        }

        Assert.SequenceEqual(new byte[] { 1, 2, 3 }, observedWrites);
    }),
    ("native replay overlay write gate lets loaded chat supersede a blocked critical clear", async () =>
    {
        var currentVersion = 1L;
        var currentPipeName = "critical-clear-chat-replacement-pipe";
        var writes = new List<byte>();
        var writesGate = new object();
        var criticalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            async (request, cancellationToken) =>
            {
                lock (writesGate)
                {
                    writes.Add(request.Frame[0]);
                }

                if (request.IsCritical)
                {
                    criticalStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                chatWritten.TrySetResult();
                return new NativeReplayOverlayFrameWriteResult(true, null);
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => currentPipeName);

        writeGate.QueueWrite(
            currentPipeName,
            [1],
            1,
            isCritical: true,
            writeKind: "critical-clear");
        await criticalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref currentVersion, 2);
        writeGate.Invalidate();
        writeGate.SupersedePersistentCriticalClears();
        writeGate.QueueWrite(
            currentPipeName,
            [2],
            2,
            writeKind: "chat-frame");

        await chatWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        byte[] observedWrites;
        lock (writesGate)
        {
            observedWrites = writes.ToArray();
        }

        Assert.SequenceEqual(new byte[] { 1, 2 }, observedWrites);
    }),
    ("native replay overlay write gate lets loaded chat supersede a blocked blank frame", async () =>
    {
        var currentVersion = 1L;
        var currentPipeName = "blank-frame-chat-replacement-pipe";
        var writes = new List<byte>();
        var writesGate = new object();
        var blankStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            async (request, cancellationToken) =>
            {
                lock (writesGate)
                {
                    writes.Add(request.Frame[0]);
                }

                if (request.IsCritical)
                {
                    blankStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                chatWritten.TrySetResult();
                return new NativeReplayOverlayFrameWriteResult(true, null);
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => currentPipeName);

        writeGate.QueueWrite(
            currentPipeName,
            [1],
            1,
            isCritical: true,
            writeKind: "blank-frame");
        await blankStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref currentVersion, 2);
        writeGate.Invalidate();
        writeGate.SupersedePersistentCriticalClears();
        writeGate.QueueWrite(
            currentPipeName,
            [2],
            2,
            writeKind: "chat-frame");

        await chatWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        byte[] observedWrites;
        lock (writesGate)
        {
            observedWrites = writes.ToArray();
        }

        Assert.SequenceEqual(new byte[] { 1, 2 }, observedWrites);
    }),
    ("native replay overlay write gate supersedes a persistent clear during retry delay", async () =>
    {
        var currentVersion = 1L;
        var clearAttempts = 0;
        var firstAttemptFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            (request, _) =>
            {
                if (request.IsCritical)
                {
                    if (Interlocked.Increment(ref clearAttempts) == 1)
                    {
                        firstAttemptFailed.TrySetResult();
                        return Task.FromResult(new NativeReplayOverlayFrameWriteResult(
                            false,
                            new IOException("connect failed")));
                    }

                    throw new InvalidOperationException("A superseded persistent clear was retried.");
                }

                chatWritten.TrySetResult();
                return Task.FromResult(new NativeReplayOverlayFrameWriteResult(true, null));
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => "critical-clear-retry-delay-pipe",
            retryDelay: TimeSpan.FromMilliseconds(500));

        writeGate.QueueWrite(
            "critical-clear-retry-delay-pipe",
            [1],
            1,
            isCritical: true,
            writeKind: "critical-clear");
        await firstAttemptFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref currentVersion, 2);
        writeGate.Invalidate();
        await Task.Delay(50);
        writeGate.SupersedePersistentCriticalClears();
        writeGate.QueueWrite(
            "critical-clear-retry-delay-pipe",
            [2],
            2,
            writeKind: "chat-frame");

        await chatWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(700);
        Assert.Equal(1, Volatile.Read(ref clearAttempts));
    }),
    ("native replay overlay write gate drops stale critical blank retry after newer frame", async () =>
    {
        var currentVersion = 1L;
        var currentPipeName = "seek-blank-race-pipe";
        var writes = new List<byte>();
        var writesGate = new object();
        var blankAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            (request, _) =>
            {
                lock (writesGate)
                {
                    writes.Add(request.Frame[0]);
                }

                if (request.Frame[0] == 1)
                {
                    blankAttempted.TrySetResult();
                    return Task.FromResult(new NativeReplayOverlayFrameWriteResult(
                        false,
                        new IOException("All pipe instances are busy.")));
                }

                chatWritten.TrySetResult();
                return Task.FromResult(new NativeReplayOverlayFrameWriteResult(true, null));
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => currentPipeName,
            retryDelay: TimeSpan.FromMilliseconds(100));

        writeGate.QueueWrite(
            currentPipeName,
            [1],
            1,
            isCritical: true,
            writeKind: "blank-frame");
        await blankAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref currentVersion, 2);
        writeGate.Invalidate();
        writeGate.QueueWrite(
            currentPipeName,
            [2],
            2,
            writeKind: "chat-frame");

        await chatWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(250);

        byte[] observedWrites;
        lock (writesGate)
        {
            observedWrites = writes.ToArray();
        }

        Assert.SequenceEqual(new byte[] { 1, 2 }, observedWrites);
    }),
    ("native replay overlay write gate retries critical frame after pipe failure", async () =>
    {
        var attempts = 0;
        var failures = 0;
        var criticalWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            (request, _) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    return Task.FromResult(new NativeReplayOverlayFrameWriteResult(
                        false,
                        new IOException("All pipe instances are busy.")));
                }

                criticalWritten.TrySetResult();
                return Task.FromResult(new NativeReplayOverlayFrameWriteResult(true, null));
            },
            () => 1,
            _ => Interlocked.Increment(ref failures),
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => "critical-retry-pipe");

        writeGate.QueueWrite("critical-retry-pipe", [1], 1, isCritical: true);

        await criticalWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(0, Volatile.Read(ref failures));
    }),
    ("native replay overlay write gate retries latest current frame after timeout", async () =>
    {
        var attempts = 0;
        var failures = 0;
        var successes = 0;
        var latestWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new MemoryLogger();
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            logger,
            (request, _) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    return Task.FromResult(new NativeReplayOverlayFrameWriteResult(
                        false,
                        new TimeoutException("connect timed out")));
                }

                return Task.FromResult(new NativeReplayOverlayFrameWriteResult(true, null));
            },
            () => 1,
            _ => Interlocked.Increment(ref failures),
            TimeSpan.FromMilliseconds(50),
            currentWriteSucceeded: _ =>
            {
                Interlocked.Increment(ref successes);
                latestWritten.TrySetResult();
            },
            getCurrentPipeName: () => "latest-retry-pipe",
            writeTimeout: TimeSpan.FromSeconds(2),
            maxCurrentFrameRetries: 3,
            retryDelay: TimeSpan.Zero);

        writeGate.QueueWrite(
            "latest-retry-pipe",
            [1],
            1,
            writeKind: "chat-frame",
            replaySessionKey: "Twitch:streamer:123:replay");

        await latestWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(0, Volatile.Read(ref failures));
        Assert.Equal(1, Volatile.Read(ref successes));
        Assert.True(
            logger.Entries.Any(entry =>
                entry.Message.Contains("kind=chat-frame", StringComparison.Ordinal) &&
                entry.Message.Contains("session=Twitch:streamer:123:replay", StringComparison.Ordinal) &&
                entry.Message.Contains("timeoutMs=2000", StringComparison.Ordinal) &&
                entry.Message.Contains("retry=0/3", StringComparison.Ordinal)),
            "Expected retry diagnostics to include write kind, replay session, timeout, and retry count.");
    }),
    ("native replay overlay write gate cancels obsolete latest-frame retry after session change", async () =>
    {
        var currentVersion = 1L;
        var writes = new List<byte>();
        var writesGate = new object();
        var firstAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            (request, _) =>
            {
                lock (writesGate)
                {
                    writes.Add(request.Frame[0]);
                }

                if (request.Frame[0] == 1)
                {
                    firstAttempted.TrySetResult();
                    return Task.FromResult(new NativeReplayOverlayFrameWriteResult(
                        false,
                        new TimeoutException("connect timed out")));
                }

                replacementWritten.TrySetResult();
                return Task.FromResult(new NativeReplayOverlayFrameWriteResult(true, null));
            },
            () => Volatile.Read(ref currentVersion),
            _ => { },
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => "session-change-pipe",
            writeTimeout: TimeSpan.FromSeconds(2),
            maxCurrentFrameRetries: 3,
            retryDelay: TimeSpan.FromMilliseconds(200));

        writeGate.QueueWrite("session-change-pipe", [1], 1);
        await firstAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Volatile.Write(ref currentVersion, 2);
        writeGate.QueueWrite("session-change-pipe", [2], 2);

        await replacementWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(300);

        byte[] observedWrites;
        lock (writesGate)
        {
            observedWrites = writes.ToArray();
        }

        Assert.SequenceEqual(new byte[] { 1, 2 }, observedWrites);
    }),
    ("native replay overlay write gate parks bounded retries and replays latest state on reconnect", async () =>
    {
        var attempts = 0;
        var failures = 0;
        var connected = false;
        var reconnectedFrame = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            (request, _) =>
            {
                Interlocked.Increment(ref attempts);
                if (Volatile.Read(ref connected))
                {
                    reconnectedFrame.TrySetResult(request.Frame[0]);
                    return Task.FromResult(new NativeReplayOverlayFrameWriteResult(true, null));
                }

                return Task.FromResult(new NativeReplayOverlayFrameWriteResult(
                    false,
                    new TimeoutException("connect timed out")));
            },
            () => 1,
            _ => Interlocked.Increment(ref failures),
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => "bounded-retry-pipe",
            writeTimeout: TimeSpan.FromSeconds(2),
            maxCurrentFrameRetries: 2,
            retryDelay: TimeSpan.Zero);

        writeGate.QueueWrite("bounded-retry-pipe", [1], 1);

        await TestWait.UntilAsync(() => writeGate.ParkedWriteCount == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(3, Volatile.Read(ref attempts));
        Assert.Equal(0, Volatile.Read(ref failures));

        writeGate.QueueWrite("bounded-retry-pipe", [2], 1);
        await Task.Delay(50);
        Assert.Equal(3, Volatile.Read(ref attempts));
        Assert.Equal(1, writeGate.ParkedWriteCount);

        Volatile.Write(ref connected, true);
        writeGate.NotifyReconnected("bounded-retry-pipe");
        Assert.Equal((byte)2, await reconnectedFrame.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(4, Volatile.Read(ref attempts));
        Assert.Equal(0, writeGate.ParkedWriteCount);
    }),
    ("native replay overlay critical queue coalesces semantic state and caps bytes", async () =>
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            new MemoryLogger(),
            async (request, cancellationToken) =>
            {
                if (request.Frame[0] == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return new NativeReplayOverlayFrameWriteResult(true, null);
            },
            () => 1,
            _ => { },
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => "bounded-critical-pipe",
            maximumQueuedBytes: 64);

        try
        {
            writeGate.QueueWrite("bounded-critical-pipe", [1], 1);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            writeGate.QueueWrite(
                "bounded-critical-pipe",
                new byte[8],
                1,
                isCritical: true,
                writeKind: "status-frame",
                replaySessionKey: "session-a");
            writeGate.QueueWrite(
                "bounded-critical-pipe",
                new byte[9],
                1,
                isCritical: true,
                writeKind: "status-frame",
                replaySessionKey: "session-a");
            Assert.Equal(1, writeGate.PendingCriticalWriteCount);
            Assert.Equal(9L, writeGate.QueuedByteCount);

            writeGate.QueueWrite(
                "bounded-critical-pipe",
                new byte[5],
                1,
                isCritical: true,
                writeKind: "critical-clear",
                replaySessionKey: "session-a");
            writeGate.QueueWrite(
                "bounded-critical-pipe",
                new byte[6],
                1,
                isCritical: true,
                writeKind: "status-frame",
                replaySessionKey: "session-b");
            Assert.Equal(3, writeGate.PendingCriticalWriteCount);
            Assert.Equal(20L, writeGate.QueuedByteCount);

            writeGate.QueueWrite(
                "bounded-critical-pipe",
                new byte[60],
                1,
                isCritical: true,
                writeKind: "status-frame",
                replaySessionKey: "session-c");
            Assert.True(writeGate.QueuedByteCount <= 64);
            Assert.Equal(1, writeGate.PendingCriticalWriteCount);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
    }),
    ("native replay overlay write gate uses bounded exponential retry delays", async () =>
    {
        var logger = new MemoryLogger();
        using var writeGate = new NativeReplayOverlayFrameWriteGate(
            logger,
            (_, _) => Task.FromResult(new NativeReplayOverlayFrameWriteResult(
                false,
                new TimeoutException("connect timed out"))),
            () => 1,
            _ => { },
            TimeSpan.FromMilliseconds(50),
            getCurrentPipeName: () => "exponential-retry-pipe",
            maxCurrentFrameRetries: 2,
            retryDelay: TimeSpan.FromMilliseconds(10));

        writeGate.QueueWrite("exponential-retry-pipe", [1], 1);

        await TestWait.UntilAsync(() => writeGate.ParkedWriteCount == 1, TimeSpan.FromSeconds(2));
        Assert.True(logger.Entries.Any(entry =>
            entry.Message.Contains("retrying in 10 ms", StringComparison.Ordinal)));
        Assert.True(logger.Entries.Any(entry =>
            entry.Message.Contains("retrying in 20 ms", StringComparison.Ordinal)));
        Assert.True(logger.Entries.Any(entry =>
            entry.Message.Contains("parked the latest state until reconnect", StringComparison.Ordinal)));
    }),
    ("native VLC replay overlay sends app-rendered frame", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_{Guid.NewGuid():N}";
            var streamlink = new FakeStreamlinkService();
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
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
                        "native replay hello",
                        DateTimeOffset.UtcNow,
                        "#8AB4F8",
                        MessageId: "replay-native-1"))
            ]));
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
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

            var frameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                message => BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8, 4)) > 4,
                TimeSpan.FromSeconds(3));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

            var frame = await frameTask;
            AssertNativeOverlayChatFrame(frame);
            Assert.True(tab.DockedChatMessages.Any(message => message.Message == "native replay hello"));

            await tab.DisposeAsync();
        });
    }),
    ("native replay transition stops the live overlay controller before replay playback", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_transition_controller_{Guid.NewGuid():N}";
            var replayPlayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReplayPlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName,
                PlayCompletionOverride = playNumber =>
                {
                    if (playNumber == 2)
                    {
                        replayPlayStarted.TrySetResult();
                        return releaseReplayPlay.Task;
                    }

                    return Task.CompletedTask;
                }
            });
            var replay = new ReplaySessionInfo(
                PlatformKind.Twitch,
                "streamer",
                "https://d1g1f25tn8m2e6.cloudfront.net/live/index-dvr.m3u8",
                "live-dvr-123456789",
                DateTimeOffset.UtcNow.AddHours(-1),
                TimeSpan.FromHours(1),
                true,
                "",
                "best",
                ReplayMediaKind.CurrentLiveDvr);
            var tab = TestViewModels.CreateTab(
                StreamInputParser.Parse("streamer", PlatformKind.Twitch),
                "source",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: new FakeReplayChatProvider(
                    ReplayChatLoadResult.Unavailable("Current-live DVR uses captured chat.")));
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = false;
            settings.Chat.Layout = ChatLayout.Overlay;

            var controllerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controller = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping 127.0.0.1 -n 60 > nul",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(controller);
            controller!.EnableRaisingEvents = true;
            controller.Exited += (_, _) => controllerExited.TrySetResult();

            try
            {
                tab.SetVideoHandle(new IntPtr(42));
                await tab.StartAsync(settings);

                typeof(StreamTabViewModel)
                    .GetField("nativeOverlayProcess", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(tab, controller);
                typeof(StreamTabViewModel)
                    .GetField("nativeOverlayPipeName", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(tab, pipeName);

                var seekTask = tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
                await replayPlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await controllerExited.Task.WaitAsync(TimeSpan.FromSeconds(2));

                releaseReplayPlay.TrySetResult();
                await seekTask;
            }
            finally
            {
                releaseReplayPlay.TrySetResult();
                await tab.DisposeAsync();
                controller.Dispose();
            }
        });
    }),
    ("sub-only Twitch VOD native overlay replaces a blocked startup clear with loaded chat", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_sub_only_replay_vod_blocked_clear_{Guid.NewGuid():N}";
            await using var blockedPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 1,
                outBufferSize: 1);
            var blockedConnection = blockedPipe.WaitForConnectionAsync();
            var replayChatProvider = new BlockingReplayChatProvider();
            var subOnlyResolver = new FakeTwitchSubOnlyVodResolver
            {
                Override = (_, _) => Task.FromResult(
                    new TwitchSubOnlyVodResolution(
                        new Uri(@"C:\fake\sub-only-overlay.m3u8"),
                        "chunked",
                        "Resolved.",
                        MediaDuration: TimeSpan.FromHours(1),
                        OwnerLogin: "streamer",
                        CreatedAtUtc: DateTimeOffset.UtcNow.AddHours(-1)))
            };
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var target = new StreamTarget(
                PlatformKind.Twitch,
                "123456",
                "https://www.twitch.tv/videos/123456",
                StreamTargetKind.TwitchVod,
                "123456",
                "Sub-only VOD");
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService
                {
                    ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException(
                        "This video is only available to subscribers")
                },
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
                replayChatProvider: replayChatProvider,
                twitchSubOnlyVodResolver: subOnlyResolver);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            try
            {
                tab.SetVideoHandle(new IntPtr(42));
                await tab.StartAsync(settings);
                await replayChatProvider.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await blockedConnection.WaitAsync(TimeSpan.FromSeconds(1));

                replayChatProvider.ReleaseFirstLoad();
                await replayChatProvider.FirstLoadReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
                await TestWait.UntilAsync(
                    () => DockedChatMessagesContain(tab, "seek A chat"),
                    TimeSpan.FromSeconds(2));

                await blockedPipe.DisposeAsync();
                var renderedFrame = await ReadNativeOverlayPipeMatchingMessageAsync(
                    pipeName,
                    IsNativeOverlayRenderedChatFrame,
                    TimeSpan.FromSeconds(5));

                AssertNativeOverlayChatFrame(renderedFrame);
                Assert.Equal(1, subOnlyResolver.Requests.Count);
                Assert.True(DockedChatMessagesContain(tab, "seek A chat"));
            }
            finally
            {
                await tab.DisposeAsync();
            }
        });
    }),
    ("Twitch behind-live native replay overlay scrolls anchors and resets after seek", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_twitch_scroll_{Guid.NewGuid():N}";
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-twitch-scroll-{Guid.NewGuid():N}.txt");
            var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName,
                NativeOverlayPositionStatePathOverride = positionStatePath
            });
            var replay = new ReplaySessionInfo(
                PlatformKind.Twitch,
                "streamer",
                "https://www.twitch.tv/videos/123",
                "123",
                startedAt,
                TimeSpan.FromHours(1),
                true,
                "",
                "best");
            var replayMessages = Enumerable.Range(0, 20)
                .Select(index => new ReplayChatMessage(
                    TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(19 - index),
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        $"viewer{index}",
                        $"scroll message {index}",
                        startedAt.AddMinutes(10).AddSeconds(index),
                        MessageId: $"twitch-scroll-{index}")))
                .ToArray();
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
                replayMessages,
                TimeSpan.FromMinutes(9),
                TimeSpan.FromMinutes(11)));
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

            try
            {
                tab.SetVideoHandle(new IntPtr(42));
                await tab.StartAsync(settings);
                var initialFrameTask = ReadNativeOverlayPipeMessagePairMatchingAsync(
                    pipeName,
                    IsNativeOverlayRenderedChatFrame,
                    TimeSpan.FromSeconds(4));
                await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
                var initialFrame = await initialFrameTask;
                AssertNativeOverlayChatFrame(initialFrame.Primary);
                Assert.True(IsNativeOverlayScrollbarStateFrame(initialFrame.Followup));
                Assert.Equal(0, ReadNativeOverlayScrollbarMessageOffset(initialFrame.Followup));
                Assert.Equal(replayMessages.Length, ReadNativeOverlayScrollbarTotalMessageCount(initialFrame.Followup));
                await TestWait.UntilAsync(
                    () => tab.IsNativeReplayOverlayEventHostRunning &&
                        tab.NativeReplayOverlayMaximumMessageOffset > 0,
                    TimeSpan.FromSeconds(2));

                var scrollFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                    pipeName,
                    IsNativeOverlayRenderedChatFrame,
                    TimeSpan.FromSeconds(4));
                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(1, 1),
                    TimeSpan.FromSeconds(2));
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMessageOffset == 3,
                    TimeSpan.FromSeconds(2));
                AssertNativeOverlayChatFrame(await scrollFrameTask);

                tab.ChatMessages.Add(new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "newer1",
                    "newer message one",
                    startedAt.AddMinutes(10).AddMinutes(1),
                    MessageId: "twitch-scroll-newer-1"));
                tab.ChatMessages.Add(new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "newer2",
                    "newer message two",
                    startedAt.AddMinutes(10).AddMinutes(1).AddSeconds(1),
                    MessageId: "twitch-scroll-newer-2"));
                var invalidateFrame = typeof(StreamTabViewModel).GetMethod(
                    "InvalidateNativeReplayOverlayFrame",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(invalidateFrame);
                invalidateFrame!.Invoke(tab, []);
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMessageOffset == 5,
                    TimeSpan.FromSeconds(2));

                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(1, -1),
                    TimeSpan.FromSeconds(2));
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMessageOffset == 2,
                    TimeSpan.FromSeconds(2));
                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(1, -1),
                    TimeSpan.FromSeconds(2));
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMessageOffset == 0,
                    TimeSpan.FromSeconds(2));

                var maximumMessageOffset = tab.NativeReplayOverlayMaximumMessageOffset;
                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(2, maximumMessageOffset),
                    TimeSpan.FromSeconds(2));
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMaximumMessageOffset > 0 &&
                        tab.NativeReplayOverlayMessageOffset == tab.NativeReplayOverlayMaximumMessageOffset,
                    TimeSpan.FromSeconds(2));

                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(2, 0),
                    TimeSpan.FromSeconds(2));
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMessageOffset == 0,
                    TimeSpan.FromSeconds(2));

                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(1, 1),
                    TimeSpan.FromSeconds(2));
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMessageOffset == 3,
                    TimeSpan.FromSeconds(2));
                await tab.SeekReplayAsync(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(5)));
                Assert.Equal(0, tab.NativeReplayOverlayMessageOffset);
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMaximumMessageOffset > 0,
                    TimeSpan.FromSeconds(2));
                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(1, 1),
                    TimeSpan.FromSeconds(2));
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMessageOffset == 3,
                    TimeSpan.FromSeconds(2));

                await tab.ReturnToLiveAsync();
                Assert.Equal(0, tab.NativeReplayOverlayMessageOffset);
                Assert.Equal(false, tab.IsNativeReplayOverlayEventHostRunning);
            }
            finally
            {
                await tab.DisposeAsync();
                File.Delete(positionStatePath);
                File.Delete($"{positionStatePath}.size");
            }
        });
    }),
    ("Kick explicit VOD native replay overlay scrolls and resets after seek", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_scroll_{Guid.NewGuid():N}";
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-kick-vod-scroll-{Guid.NewGuid():N}.txt");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                "https://vod.kick.com/xqc/index.m3u8",
                StreamTargetKind.KickVod,
                "uuid-scroll",
                "Kick VOD scrolling",
                "",
                TimeSpan.FromMinutes(30),
                startedAt,
                ChatRoomId: "668");
            var replayMessages = Enumerable.Range(0, 20)
                .Select(index => new ReplayChatMessage(
                    TimeSpan.Zero,
                    new ChatMessage(
                        PlatformKind.Kick,
                        "xqc",
                        $"viewer{index}",
                        $"Kick VOD scroll message {index}",
                        startedAt.AddSeconds(index),
                        MessageId: $"kick-vod-scroll-{index}")))
                .ToArray();
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
                replayMessages,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(5)));
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

            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                Dispatch,
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            try
            {
                tab.SetVideoHandle(new IntPtr(42));
                var initialFrameTask = ReadNativeOverlayPipeMessagePairMatchingAsync(
                    pipeName,
                    IsNativeOverlayRenderedChatFrame,
                    TimeSpan.FromSeconds(4));
                await tab.StartAsync(settings);
                var initialFrame = await initialFrameTask;
                AssertNativeOverlayChatFrame(initialFrame.Primary);
                Assert.True(IsNativeOverlayScrollbarStateFrame(initialFrame.Followup));
                Assert.Equal(0, ReadNativeOverlayScrollbarMessageOffset(initialFrame.Followup));
                Assert.Equal(replayMessages.Length, ReadNativeOverlayScrollbarTotalMessageCount(initialFrame.Followup));
                await TestWait.UntilAsync(
                    () => tab.IsNativeReplayOverlayEventHostRunning &&
                        tab.NativeReplayOverlayMaximumMessageOffset > 0,
                    TimeSpan.FromSeconds(2));

                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(1, 1),
                    TimeSpan.FromSeconds(2));
                await TestWait.UntilAsync(
                    () => tab.NativeReplayOverlayMessageOffset == 3,
                    TimeSpan.FromSeconds(2));

                await tab.SeekReplayAsync(TimeSpan.FromMinutes(1));
                Assert.Equal(0, tab.NativeReplayOverlayMessageOffset);
            }
            finally
            {
                await tab.DisposeAsync();
                File.Delete(positionStatePath);
                File.Delete($"{positionStatePath}.size");
            }
        });
    }),
    ("explicit Twitch VOD native overlay blanks when replay chat is unavailable", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_vod_no_chat_{Guid.NewGuid():N}";
            var streamlink = new FakeStreamlinkService();
            streamlink.ResolveStreamUrlOverride = (_, _) => Task.FromResult(
                new StreamlinkResolvedUrl(new Uri("https://example.com/vod/no-chat.m3u8"), "Resolved VOD."));
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            const string unavailableReason = "Twitch replay chat is unavailable for this VOD.";
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable(unavailableReason));
            var target = new StreamTarget(
                PlatformKind.Twitch,
                "streamer",
                "https://www.twitch.tv/videos/123",
                StreamTargetKind.TwitchVod,
                "123",
                "VOD without replay chat",
                "456",
                TimeSpan.FromHours(1));
            var queuedDispatchCount = 0;
            var queuedDispatches = new Queue<Action>();
            void DeferredDispatch(Action action)
            {
                lock (queuedDispatches)
                {
                    queuedDispatches.Enqueue(action);
                }

                Interlocked.Increment(ref queuedDispatchCount);
            }

            void DrainDispatches()
            {
                while (true)
                {
                    Action action;
                    lock (queuedDispatches)
                    {
                        if (queuedDispatches.Count == 0)
                        {
                            return;
                        }

                        action = queuedDispatches.Dequeue();
                    }

                    action();
                }
            }

            var tab = TestViewModels.CreateTab(
                target,
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                DeferredDispatch,
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            var messagesTask = ReadNativeOverlayPipeMessagesUntilAsync(
                pipeName,
                messages => messages.Any(IsNativeOverlayTransparentFrame),
                TimeSpan.FromSeconds(4));
            await tab.StartAsync(settings);

            var messages = await messagesTask;
            await TestWait.UntilAsync(() => replayChatProvider.CallCount > 0, TimeSpan.FromSeconds(1));
            DrainDispatches();

            Assert.True(messages.Any(IsNativeOverlayTransparentFrame));
            Assert.Equal(false, messages.Any(IsNativeOverlayRenderedChatFrame));
            Assert.Equal("123", replayChatProvider.Requests[0].ReplayId);
            Assert.True(Volatile.Read(ref queuedDispatchCount) > 0);

            await tab.DisposeAsync();
        });
    }),
    ("explicit Kick VOD native overlay renders replay chat unavailable status", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_no_cache_{Guid.NewGuid():N}";
            var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            const string unavailableReason = "No official Kick webhook chat cache was found for xqc. Enable the Kick webhook listener, subscribe to chat.message.sent, and capture chat before opening the VOD.";
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var chatFactory = new FakeChatClientFactory();
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable(unavailableReason));
            var queuedDispatchCount = 0;
            var queuedDispatches = new Queue<Action>();
            void DeferredDispatch(Action action)
            {
                lock (queuedDispatches)
                {
                    queuedDispatches.Enqueue(action);
                }

                Interlocked.Increment(ref queuedDispatchCount);
            }

            void DrainDispatches()
            {
                while (true)
                {
                    Action action;
                    lock (queuedDispatches)
                    {
                        if (queuedDispatches.Count == 0)
                        {
                            return;
                        }

                        action = queuedDispatches.Dequeue();
                    }

                    action();
                }
            }

            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD without cached chat",
                "",
                TimeSpan.FromMinutes(30),
                startedAt);
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                chatFactory,
                new MemoryLogger(),
                DeferredDispatch,
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
            await TestWait.UntilAsync(
                () =>
                {
                    DrainDispatches();
                    return replayChatProvider.CallCount >= 1 &&
                        DockedChatMessagesContainText(tab, "webhook chat cache");
                },
                TimeSpan.FromSeconds(2));

            var renderedFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(4));
            var invalidateFrame = typeof(StreamTabViewModel).GetMethod(
                "InvalidateNativeReplayOverlayFrame",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(invalidateFrame);
            invalidateFrame!.Invoke(tab, []);

            AssertNativeOverlayChatFrame(await renderedFrameTask);
            Assert.Equal(0, chatFactory.Client.ConnectCount);
            Assert.Equal(false, tab.CanSendChatMessages);
            Assert.Equal("uuid-123", replayChatProvider.Requests[0].ReplayId);
            Assert.Equal(startedAt, replayChatProvider.Requests[0].StreamStartedAtUtc);

            await tab.DisposeAsync();
        });
    }),
    ("explicit Kick VOD native overlay renders loaded replay chat without manual invalidation", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_chat_{Guid.NewGuid():N}";
            var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.Zero,
                    new ChatMessage(
                        PlatformKind.Kick,
                        "xqc",
                        "viewer",
                        "native Kick VOD replay chat",
                        startedAt,
                        MessageId: "native-kick-vod-replay-chat"))
            ], TimeSpan.Zero, TimeSpan.FromMinutes(4)));
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD with chat",
                "",
                TimeSpan.FromMinutes(30),
                startedAt,
                ChatRoomId: "668");
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            var messagesTask = ReadNativeOverlayPipeMessagesUntilAsync(
                pipeName,
                messages => messages.Any(IsNativeOverlayRenderedChatFrame),
                TimeSpan.FromSeconds(4));

            await tab.StartAsync(settings);
            var messages = await messagesTask;

            Assert.True(messages.Any(IsNativeOverlayRenderedChatFrame));
            Assert.True(DockedChatMessagesContain(tab, "native Kick VOD replay chat"));
            Assert.Equal("uuid-123", replayChatProvider.Requests[0].ReplayId);
            Assert.Equal(startedAt, replayChatProvider.Requests[0].StreamStartedAtUtc);
            Assert.Equal("668", replayChatProvider.Requests[0].ChatRoomId);
            Assert.Equal(TimeSpan.Zero, replayChatProvider.Offsets[0]);

            await tab.DisposeAsync();
        });
    }),
    ("explicit Kick VOD native overlay resize event keeps replay chat text stable for width-only resize", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_width_resize_{Guid.NewGuid():N}";
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-kick-width-resize-{Guid.NewGuid():N}.txt");
            var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.Zero,
                    new ChatMessage(
                        PlatformKind.Kick,
                        "xqc",
                        "viewer",
                        "width resized Kick VOD replay chat",
                        startedAt,
                        MessageId: "width-resized-kick-vod-replay-chat"))
            ], TimeSpan.Zero, TimeSpan.FromMinutes(4)));
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

            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD with chat",
                "",
                TimeSpan.FromMinutes(30),
                startedAt,
                ChatRoomId: "668");
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                Dispatch,
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;
            settings.Chat.VlcOverlayFontSize = 18;

            try
            {
                tab.SetVideoHandle(new IntPtr(42));
                var initialFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                    pipeName,
                    message =>
                        IsNativeOverlayRenderedChatFrame(message) &&
                        BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4)) == 340 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)) == 292,
                    TimeSpan.FromSeconds(4));

                await tab.StartAsync(settings);
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
                        IsNativeOverlayRenderedChatFrame(message) &&
                        BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4)) == 680 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)) == 292,
                    TimeSpan.FromSeconds(4));
                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(680, 292)),
                    TimeSpan.FromSeconds(2));

                var resizedFrame = await resizedFrameTask;
                AssertNativeOverlayChatFrame(resizedFrame);
                var resizedBounds = GetNativeOverlayAlphaBounds(resizedFrame);
                Assert.True(
                    resizedBounds.Height <= initialBounds.Height + 3,
                    $"Expected width-resized replay overlay text to stay within 3px of {initialBounds.Height}px, got {resizedBounds.Height}px.");
                Assert.Equal("reference 680 292", File.ReadAllText($"{positionStatePath}.size"));
                Assert.True(DockedChatMessagesContain(tab, "width resized Kick VOD replay chat"));
            }
            finally
            {
                await tab.DisposeAsync();
                File.Delete(positionStatePath);
                File.Delete($"{positionStatePath}.size");
            }
        });
    }),
    ("explicit Kick VOD native overlay ignores stale seekbar handoff resize before lower-height replay frame", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_seekbar_resize_{Guid.NewGuid():N}";
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-kick-seekbar-resize-{Guid.NewGuid():N}.txt");
            var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var engine = new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName,
                NativeOverlayPositionStatePathOverride = positionStatePath,
                VideoWidth = 1280,
                VideoHeight = 720
            };
            var playbackFactory = new FakePlaybackEngineFactory(() => engine);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.Zero,
                    new ChatMessage(
                        PlatformKind.Kick,
                        "xqc",
                        "viewer",
                        "seekbar handoff Kick VOD replay chat",
                        startedAt,
                        MessageId: "seekbar-handoff-kick-vod-replay-chat"))
            ], TimeSpan.Zero, TimeSpan.FromMinutes(4)));
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

            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD with chat",
                "",
                TimeSpan.FromMinutes(30),
                startedAt,
                ChatRoomId: "668");
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                Dispatch,
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            try
            {
                tab.SetVideoHandle(new IntPtr(42));
                await tab.StartAsync(settings);
                await TestWait.UntilAsync(
                    () => tab.IsNativeReplayOverlayEventHostRunning &&
                        string.Equals(tab.NativeReplayOverlayEventHostPipeName, pipeName, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(2));

                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(340, 292)),
                    TimeSpan.FromSeconds(2));

                var replayFrame = await ReadNativeOverlayPipeMatchingMessageAsync(
                    pipeName,
                    message =>
                        IsNativeOverlayRenderedChatFrame(message) &&
                        BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4)) == 227 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)) == 195,
                    TimeSpan.FromSeconds(5));
                AssertNativeOverlayChatFrame(replayFrame);
                await Task.Delay(150);

                Assert.Equal(false, File.Exists($"{positionStatePath}.size"));
                Assert.True(DockedChatMessagesContain(tab, "seekbar handoff Kick VOD replay chat"));
            }
            finally
            {
                await tab.DisposeAsync();
                File.Delete(positionStatePath);
                File.Delete($"{positionStatePath}.size");
            }
        });
    }),
    ("explicit Kick VOD native overlay resends loaded replay chat during startup warmup", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_warmup_{Guid.NewGuid():N}";
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-kick-warmup-{Guid.NewGuid():N}.txt");
            var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.Zero,
                    new ChatMessage(
                        PlatformKind.Kick,
                        "xqc",
                        "viewer",
                        "warmup Kick VOD replay chat",
                        startedAt,
                        MessageId: "warmup-kick-vod-replay-chat"))
            ], TimeSpan.Zero, TimeSpan.FromMinutes(4)));
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

            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD with chat",
                "",
                TimeSpan.FromMinutes(30),
                startedAt,
                ChatRoomId: "668");
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                Dispatch,
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            try
            {
                tab.SetVideoHandle(new IntPtr(42));
                var messagesTask = ReadNativeOverlayPipeMessagesUntilAsync(
                    pipeName,
                    messages => messages.Count(IsNativeOverlayRenderedChatFrame) >= 2,
                    TimeSpan.FromSeconds(5));

                await tab.StartAsync(settings);
                var messages = await messagesTask;

                Assert.True(messages.Count(IsNativeOverlayRenderedChatFrame) >= 2);
                Assert.True(DockedChatMessagesContain(tab, "warmup Kick VOD replay chat"));
            }
            finally
            {
                await tab.DisposeAsync();
                File.Delete(positionStatePath);
                File.Delete($"{positionStatePath}.size");
            }
        });
    }),
    ("explicit Kick VOD native overlay refreshes replay frame when video size becomes available", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_video_size_{Guid.NewGuid():N}";
            var positionStatePath = Path.Combine(Path.GetTempPath(), $"svs-replay-kick-video-size-{Guid.NewGuid():N}.txt");
            var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var engine = new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName,
                NativeOverlayPositionStatePathOverride = positionStatePath,
                VideoWidth = 0,
                VideoHeight = 0
            };
            var playbackFactory = new FakePlaybackEngineFactory(() => engine);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.Zero,
                    new ChatMessage(
                        PlatformKind.Kick,
                        "xqc",
                        "viewer",
                        "video size Kick VOD replay chat",
                        startedAt,
                        MessageId: "video-size-kick-vod-replay-chat"))
            ], TimeSpan.Zero, TimeSpan.FromMinutes(4)));
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

            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD with chat",
                "",
                TimeSpan.FromMinutes(30),
                startedAt,
                ChatRoomId: "668");
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                Dispatch,
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            try
            {
                tab.SetVideoHandle(new IntPtr(42));
                var initialFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                    pipeName,
                    IsNativeOverlayRenderedChatFrame,
                    TimeSpan.FromSeconds(4));

                await tab.StartAsync(settings);
                var initialFrame = await initialFrameTask;
                AssertNativeOverlayChatFrame(initialFrame);

                engine.VideoWidth = 1280;
                engine.VideoHeight = 720;
                var refreshVideoAspect = typeof(StreamTabViewModel).GetMethod(
                    "RefreshVideoAspectRatioPollingSample",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(refreshVideoAspect);
                refreshVideoAspect!.Invoke(tab, []);

                await WriteNativeOverlayEventPipeMessageAsync(
                    $"{pipeName}_events",
                    BuildNativeOverlayEventMessage(3, PackNativeOverlaySize(340, 292)),
                    TimeSpan.FromSeconds(2));

                var resizedFrame = await ReadNativeOverlayPipeMatchingMessageAsync(
                    pipeName,
                    message =>
                        IsNativeOverlayRenderedChatFrame(message) &&
                        BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(24, 4)) == 227 &&
                        BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)) == 195,
                    TimeSpan.FromSeconds(5));
                AssertNativeOverlayChatFrame(resizedFrame);
                await Task.Delay(150);

                Assert.Equal(false, File.Exists($"{positionStatePath}.size"));
                Assert.True(DockedChatMessagesContain(tab, "video size Kick VOD replay chat"));
            }
            finally
            {
                await tab.DisposeAsync();
                File.Delete(positionStatePath);
                File.Delete($"{positionStatePath}.size");
            }
        });
    }),
    ("explicit Kick VOD native overlay retries loaded replay chat until pipe is ready", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_retry_{Guid.NewGuid():N}";
            var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
            [
                new ReplayChatMessage(
                    TimeSpan.Zero,
                    new ChatMessage(
                        PlatformKind.Kick,
                        "xqc",
                        "viewer",
                        "retry Kick VOD replay chat",
                        startedAt,
                        MessageId: "retry-kick-vod-replay-chat"))
            ], TimeSpan.Zero, TimeSpan.FromMinutes(4)));
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD with chat",
                "",
                TimeSpan.FromMinutes(30),
                startedAt,
                ChatRoomId: "668");
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
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
            await TestWait.UntilAsync(
                () => replayChatProvider.CallCount >= 1 &&
                    DockedChatMessagesContain(tab, "retry Kick VOD replay chat"),
                TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(900));

            var renderedFrame = await ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(4));

            AssertNativeOverlayChatFrame(renderedFrame);
            await tab.DisposeAsync();
        });
    }),
    ("explicit Kick VOD seek writes native blank frame while replay chat load is delayed", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_kick_vod_seek_blank_{Guid.NewGuid():N}";
            var directVodUri = new Uri("https://vod.kick.com/xqc/index.m3u8");
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            var replayChatProvider = new BlockingReplayChatProvider();
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var target = new StreamTarget(
                PlatformKind.Kick,
                "xqc",
                directVodUri.ToString(),
                StreamTargetKind.KickVod,
                "uuid-123",
                "Kick VOD with delayed seek chat",
                "",
                TimeSpan.FromMinutes(30),
                startedAt,
                ChatRoomId: "668");
            var tab = TestViewModels.CreateTab(
                target,
                "best",
                new FakeStreamlinkService(),
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                action => action(),
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
            await replayChatProvider.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var initialFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(4));
            replayChatProvider.ReleaseFirstLoad();
            AssertNativeOverlayChatFrame(await initialFrameTask);
            await replayChatProvider.FirstLoadReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var blankFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayTransparentFrame,
                TimeSpan.FromSeconds(4));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(5));
            await replayChatProvider.SecondLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            AssertNativeOverlayTransparentFrame(await blankFrameTask);

            var renderedFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(4));
            replayChatProvider.ReleaseSecondLoad();
            await TestWait.UntilAsync(
                () => DockedChatMessagesContain(tab, "seek B chat"),
                TimeSpan.FromSeconds(2));
            AssertNativeOverlayChatFrame(await renderedFrameTask);

            await tab.DisposeAsync();
        });
    }),
    ("explicit Twitch VOD native overlay blanks while replay chat load is pending", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_vod_pending_chat_{Guid.NewGuid():N}";
            var streamlink = new FakeStreamlinkService();
            streamlink.ResolveStreamUrlOverride = (_, _) => Task.FromResult(
                new StreamlinkResolvedUrl(new Uri("https://example.com/vod/pending-chat.m3u8"), "Resolved VOD."));
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var replayChatProvider = new BlockingReplayChatProvider();
            var target = new StreamTarget(
                PlatformKind.Twitch,
                "streamer",
                "https://www.twitch.tv/videos/125",
                StreamTargetKind.TwitchVod,
                "125",
                "VOD with pending replay chat",
                "456",
                TimeSpan.FromHours(1));
            var queuedDispatches = new Queue<Action>();
            void DeferredDispatch(Action action)
            {
                lock (queuedDispatches)
                {
                    queuedDispatches.Enqueue(action);
                }
            }

            void DrainDispatches()
            {
                while (true)
                {
                    Action action;
                    lock (queuedDispatches)
                    {
                        if (queuedDispatches.Count == 0)
                        {
                            return;
                        }

                        action = queuedDispatches.Dequeue();
                    }

                    action();
                }
            }

            var tab = TestViewModels.CreateTab(
                target,
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                DeferredDispatch,
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            var blankFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayTransparentFrame,
                TimeSpan.FromSeconds(4));

            await tab.StartAsync(settings);
            await replayChatProvider.FirstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            AssertNativeOverlayTransparentFrame(await blankFrameTask);
            Assert.Equal(false, replayChatProvider.FirstLoadReturned.Task.IsCompleted);
            replayChatProvider.ReleaseFirstLoad();
            await replayChatProvider.FirstLoadReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            DrainDispatches();
            await tab.DisposeAsync();
        });
    }),
    ("explicit Twitch VOD native overlay blanks when replay chat loads empty", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_vod_empty_chat_{Guid.NewGuid():N}";
            var streamlink = new FakeStreamlinkService();
            streamlink.ResolveStreamUrlOverride = (_, _) => Task.FromResult(
                new StreamlinkResolvedUrl(new Uri("https://example.com/vod/empty-chat.m3u8"), "Resolved VOD."));
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var replayChatProvider = new FakeReplayChatProvider(
                ReplayChatLoadResult.Available([], TimeSpan.Zero, TimeSpan.FromHours(1)));
            var target = new StreamTarget(
                PlatformKind.Twitch,
                "streamer",
                "https://www.twitch.tv/videos/124",
                StreamTargetKind.TwitchVod,
                "124",
                "VOD with empty replay chat",
                "456",
                TimeSpan.FromHours(1));
            var queuedDispatchCount = 0;
            var queuedDispatches = new Queue<Action>();
            void DeferredDispatch(Action action)
            {
                lock (queuedDispatches)
                {
                    queuedDispatches.Enqueue(action);
                }

                Interlocked.Increment(ref queuedDispatchCount);
            }

            void DrainDispatches()
            {
                while (true)
                {
                    Action action;
                    lock (queuedDispatches)
                    {
                        if (queuedDispatches.Count == 0)
                        {
                            return;
                        }

                        action = queuedDispatches.Dequeue();
                    }

                    action();
                }
            }

            var tab = TestViewModels.CreateTab(
                target,
                "best",
                streamlink,
                playbackFactory,
                new FakeChatClientFactory(),
                new MemoryLogger(),
                DeferredDispatch,
                replayChatProvider: replayChatProvider);
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            var messagesTask = ReadNativeOverlayPipeMessagesUntilAsync(
                pipeName,
                messages => messages.Any(IsNativeOverlayTransparentFrame),
                TimeSpan.FromSeconds(4));
            await tab.StartAsync(settings);

            var messages = await messagesTask;
            await TestWait.UntilAsync(() => replayChatProvider.CallCount > 0, TimeSpan.FromSeconds(1));
            DrainDispatches();

            Assert.True(messages.Any(IsNativeOverlayTransparentFrame));
            Assert.Equal(false, messages.Any(IsNativeOverlayRenderedChatFrame));            Assert.Equal("124", replayChatProvider.Requests[0].ReplayId);
            Assert.True(Volatile.Read(ref queuedDispatchCount) > 0);

            await tab.DisposeAsync();
        });
    }),
    ("native VLC replay overlay advances animated emotes after seekback without changing visible chat", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            const int maxImageBytes = AnimatedEmoteImage.DefaultMaxImageBytes;
            var pipeName = $"svs_replay_animated_{Guid.NewGuid():N}";
            var imageUrl = $"https://example.invalid/replay-seekback-animated-{Guid.NewGuid():N}.gif";
            AnimatedEmoteImage.SetCachedSolidColorImageForTest(
                imageUrl,
                maxImageBytes,
                [Colors.Red, Colors.Lime],
                [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)],
                16,
                16);
            try
            {
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

                var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
                {
                    UsesNativeOverlayOverride = true,
                    NativeOverlayPipeNameOverride = pipeName
                });
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
                            "Spin",
                            DateTimeOffset.UtcNow,
                            "#8AB4F8",
                            Emotes: [new ChatEmote(0, 4, "Spin", imageUrl)],
                            MessageId: "replay-native-animated-1"))
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

                var firstFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                    pipeName,
                    IsNativeOverlayRenderedChatFrame,
                    TimeSpan.FromSeconds(3));
                await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

                var firstFrame = await firstFrameTask;
                AssertNativeOverlayChatFrame(firstFrame);
                var visibleMessages = tab.DockedChatMessages
                    .Select(message => message.MessageId ?? message.Message)
                    .ToArray();
                Assert.SequenceEqual(["replay-native-animated-1"], visibleMessages);
                var chatCollectionChanges = 0;
                var dockedChatCollectionChanges = 0;
                NotifyCollectionChangedEventHandler chatCollectionChanged = (_, _) => chatCollectionChanges++;
                NotifyCollectionChangedEventHandler dockedChatCollectionChanged = (_, _) => dockedChatCollectionChanges++;
                tab.ChatMessages.CollectionChanged += chatCollectionChanged;
                tab.DockedChatMessages.CollectionChanged += dockedChatCollectionChanged;

                try
                {
                    var secondFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                        pipeName,
                        message => IsNativeOverlayRenderedChatFrame(message) && !firstFrame.SequenceEqual(message),
                        TimeSpan.FromSeconds(4));
                    var secondFrame = await secondFrameTask;

                    AssertNativeOverlayChatFrame(secondFrame);
                    Assert.SequenceEqual(
                        visibleMessages,
                        tab.DockedChatMessages.Select(message => message.MessageId ?? message.Message).ToArray());
                    Assert.Equal(0, chatCollectionChanges);
                    Assert.Equal(0, dockedChatCollectionChanges);
                }
                finally
                {
                    tab.ChatMessages.CollectionChanged -= chatCollectionChanged;
                    tab.DockedChatMessages.CollectionChanged -= dockedChatCollectionChanged;
                }

                await tab.DisposeAsync();
            }
            finally
            {
                AnimatedEmoteImage.RemoveCachedImageForTest(imageUrl, maxImageBytes);
            }
        });
    }),
    ("native VLC replay overlay coalesces seek blank when captured chat is immediately visible", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_coalesce_{Guid.NewGuid():N}";
            var startedAt = new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero);
            TimeSpan? forcedClockPosition = null;
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName,
                PlaybackClockOverride = engine =>
                    (true, new PlaybackClock(forcedClockPosition ?? engine.Position, engine.Duration, engine.Seekable))
            });
            var chatFactory = new FakeChatClientFactory();
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "streamer",
                "https://kick.example/replay/index.m3u8",
                "kick-replay-native-overlay-coalesce",
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
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.IsReplaySeekEnabled, TimeSpan.FromSeconds(1));
            await TestWait.UntilAsync(() => chatFactory.Client.ConnectCount > 0, TimeSpan.FromSeconds(1));
            chatFactory.Client.Receive(new ChatMessage(
                PlatformKind.Kick,
                "streamer",
                "viewer",
                "captured chat ready for native overlay",
                startedAt.AddMinutes(10),
                MessageId: "captured-chat-ready-native-overlay"));

            var messagesTask = ReadNativeOverlayPipeMessagesUntilAsync(
                pipeName,
                messages => messages.Any(IsNativeOverlayRenderedChatFrame),
                TimeSpan.FromSeconds(4));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));

            var messages = await messagesTask;
            Assert.True(messages.Any(IsNativeOverlayRenderedChatFrame));
            Assert.Equal(false, messages.Any(IsNativeOverlayBlankFrame));
            await TestWait.UntilAsync(
                () => tab.ChatMessages.Any(message => message.Message == "captured chat ready for native overlay"),
                TimeSpan.FromSeconds(1));
            Assert.True(tab.ChatMessages.Any(message => message.Message == "captured chat ready for native overlay"));

            MarkReplayClockSeekConfirmed(tab, TimeSpan.FromSeconds(50));
            forcedClockPosition = TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(50));
            InvokeReplayClockUpdate(tab);
            Assert.True(tab.ChatMessages.Any(message => message.Message == "captured chat ready for native overlay"));

            var invalidateFrame = typeof(StreamTabViewModel).GetMethod(
                "InvalidateNativeReplayOverlayFrame",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(invalidateFrame);
            var retainedFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayRenderedChatFrame,
                TimeSpan.FromSeconds(4));
            invalidateFrame!.Invoke(tab, []);
            AssertNativeOverlayChatFrame(await retainedFrameTask);
            Assert.Equal(false, tab.ChatMessages.Any(message =>
                message.Message.Contains("Kick replay chat should not be requested", StringComparison.Ordinal)));

            await tab.DisposeAsync();
        });
    }),
    ("native VLC replay overlay retries blank frame after empty Kick seekback write failure", async () =>
    {
        await TestSta.RunAsync(async () =>
        {
            var pipeName = $"svs_replay_empty_kick_{Guid.NewGuid():N}";
            var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
            var playbackFactory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
            {
                UsesNativeOverlayOverride = true,
                NativeOverlayPipeNameOverride = pipeName
            });
            var chatFactory = new FakeChatClientFactory();
            chatFactory.Client.BackfillCoveredRequestedRange = true;
            var logger = new MemoryLogger();
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "streamer",
                "https://kick.example/replay/index.m3u8",
                "kick-replay-empty-native-overlay",
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
                logger,
                action => action(),
                replayResolver: new FakeReplayResolver(replay),
                replayChatProvider: new FakeReplayChatProvider(ReplayChatLoadResult.Unavailable("Kick replay chat should not be requested.")));
            var settings = new AppSettings
            {
                StreamlinkPath = "streamlink.exe",
                VlcDirectory = @"C:\VLC"
            };
            settings.Chat.ConnectAutomatically = true;
            settings.Chat.Layout = ChatLayout.Overlay;

            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(settings);

            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10));
            var hasCapturedReplayCoverage = typeof(StreamTabViewModel).GetMethod(
                "HasCapturedReplayChatBackfillCoverage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(hasCapturedReplayCoverage);
            await TestWait.UntilAsync(
                () => (bool)hasCapturedReplayCoverage!.Invoke(tab, [TimeSpan.FromMinutes(10)])!,
                TimeSpan.FromSeconds(2));

            var invalidateFrame = typeof(StreamTabViewModel).GetMethod(
                "InvalidateNativeReplayOverlayFrame",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(invalidateFrame);

            var firstFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayTransparentFrame,
                TimeSpan.FromSeconds(4));
            invalidateFrame!.Invoke(tab, []);
            AssertNativeOverlayTransparentFrame(await firstFrameTask);

            var writeFailed = typeof(StreamTabViewModel).GetMethod(
                "OnNativeReplayOverlayFrameWriteFailed",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(writeFailed);
            var secondFrameTask = ReadNativeOverlayPipeMatchingMessageAsync(
                pipeName,
                IsNativeOverlayTransparentFrame,
                TimeSpan.FromSeconds(4));
            writeFailed!.Invoke(tab, [new IOException("simulated blank frame write failure")]);

            var blankFrame = await secondFrameTask;
            AssertNativeOverlayTransparentFrame(blankFrame);
            Assert.Equal(false, tab.ChatMessages.Any(message =>
                message.Message.Contains("Kick seekback chat", StringComparison.Ordinal)));

            await tab.DisposeAsync();
        });
    }),
    ];
}
