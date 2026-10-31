internal static partial class OfflineFollowedChannelsTestCatalog
{
    private static Task FastProviderBeforeSlowProviderAsync(PlatformKind slowPlatform) => TestSta.RunOffscreenAsync(async () =>
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentDictionary<string, int>();
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            requests.AddOrUpdate(uri.AbsoluteUri, 1, (_, count) => count + 1);
            var platform = uri.Host == "api.kick.com" ? PlatformKind.Kick : PlatformKind.Twitch;
            if (platform == slowPlatform)
            {
                slowStarted.TrySetResult();
                await releaseSlow.Task.WaitAsync(token);
            }
            if (uri.Host == "id.twitch.tv") return Response(Validation);
            if (uri.AbsolutePath == "/helix/streams/followed") return Response(TwitchPage(["twitchlive"], false));
            if (uri.AbsolutePath == "/helix/channels/followed") return Response(TwitchPage([], true));
            if (uri.AbsolutePath == "/helix/users") return Response("""{"data":[]}""");
            if (uri.Host == "api.kick.com")
                return Response("""{"data":[{"slug":"kicklive","stream":{"is_live":true,"viewer_count":42}}]}""");
            throw new InvalidOperationException($"Unexpected request: {uri}");
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "token";
        settings.FollowedChannels.KickChannelSlugs = ["kicklive"];
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")));
        await using var feature = CreateFeature(service, settings);
        var elapsed = Stopwatch.StartNew();
        var refresh = feature.RefreshFollowedChannelsAsync();
        try
        {
            await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await TestWait.UntilAsync(() => feature.LiveFollowedChannels.Count == 1, TimeSpan.FromMilliseconds(500));
            var firstCard = feature.LiveFollowedChannels.Single();
            Console.WriteLine($"LOADING {slowPlatform} pending: first usable card after {elapsed.Elapsed.TotalMilliseconds:F1} ms");
            Assert.True(firstCard.Platform != slowPlatform);
            Assert.True(firstCard.OpenCommand.CanExecute(null));
            Assert.True(feature.IsFollowedChannelsRefreshing);
            Assert.True(feature.FollowedChannelsStatus.Contains(slowPlatform.ToString(), StringComparison.Ordinal));
            Assert.True(ReferenceEquals(refresh, feature.RefreshFollowedChannelsAsync()));
            releaseSlow.TrySetResult();
            await refresh;
            Assert.Equal(2, feature.LiveFollowedChannels.Count);
            Assert.True(feature.LiveFollowedChannels.Contains(firstCard));
            Assert.Equal(false, feature.IsFollowedChannelsRefreshing);
            Assert.True(requests.Values.All(count => count == 1));
        }
        finally
        {
            releaseSlow.TrySetResult();
            await refresh;
        }
    });

    private static Task PendingProviderCardsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ProgressiveFollowedFixture();
        var notifications = new FakeLiveNotificationService();
        var settings = new AppSettings();
        settings.FollowedChannels.NotifyWhenLive = true;
        await using var model = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            FollowedStreamsService = service,
            LiveNotificationService = notifications,
            FollowedChannelsRefreshInterval = TimeSpan.Zero
        });
        await model.RefreshFollowedChannelsCommand.ExecuteAsync();
        var liveCard = model.LiveFollowedChannels.Single(card => card.Platform == PlatformKind.Kick);
        var offlineCard = model.OfflineFollowedChannels.Single(card => card.Platform == PlatformKind.Kick);
        var liveCommand = liveCard.OpenCommand;
        var offlineCommand = offlineCard.OpenCommand;
        offlineCard.TogglePinCommand.Execute(null);
        Assert.Equal(0, notifications.Notifications.Count);
        var refresh = model.RefreshFollowedChannelsCommand.ExecuteAsync();
        try
        {
            await service.PartialConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(model.LiveFollowedChannels.Contains(liveCard));
            Assert.True(model.OfflineFollowedChannels.Contains(offlineCard));
            Assert.True(offlineCard.IsPinned);
            Assert.Equal("https://images.example/kickoff/banner.jpg", offlineCard.BannerImageUrl);
            Assert.True(ReferenceEquals(liveCommand, liveCard.OpenCommand));
            Assert.True(ReferenceEquals(offlineCommand, offlineCard.OpenCommand));
            Assert.True(model.IsFollowedChannelsRefreshing);
            Assert.Equal(0, notifications.Notifications.Count);
            service.Release.TrySetResult();
            await refresh;
            Assert.True(model.LiveFollowedChannels.Contains(liveCard));
            Assert.True(model.OfflineFollowedChannels.Contains(offlineCard));
            Assert.True(offlineCard.IsPinned);
            Assert.Equal("newtwitch", notifications.Notifications.Single().Channel);
            Assert.Equal(false, model.IsFollowedChannelsRefreshing);
        }
        finally
        {
            service.Release.TrySetResult();
            await refresh;
        }
    });

    private static Task StopProgressiveProviderAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ProgressiveFollowedFixture { SkipInitial = true };
        var feature = CreateFeature(service, new AppSettings());
        var refresh = feature.RefreshFollowedChannelsAsync();
        await service.PartialConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var card = feature.LiveFollowedChannels.Single();
        await feature.DisposeAsync();
        await refresh;
        Assert.True(service.Token.IsCancellationRequested);
        Assert.True(ReferenceEquals(card, feature.LiveFollowedChannels.Single()));
        await feature.RefreshFollowedChannelsAsync();
        Assert.Equal(1, service.Calls);
    });

    private static Task SupersededProgressiveProviderAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new ProgressiveFollowedFixture { SkipInitial = true };
        var settings = new AppSettings();
        await using var feature = CreateFeature(service, settings);
        var original = feature.RefreshFollowedChannelsAsync();
        await service.PartialConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var published = new List<string>();
        feature.LiveFollowedChannels.CollectionChanged += (_, _) =>
            published.AddRange(feature.LiveFollowedChannels.Select(card => card.Channel));
        settings.Chat.TwitchOAuthToken = "new-account";
        var replacement = feature.RefreshFollowedChannelsAsync();
        service.Release.TrySetResult();
        await Task.WhenAll(original, replacement);
        Assert.Equal(false, published.Contains("stale"));
        Assert.Equal("current", feature.LiveFollowedChannels.Single().Channel);
        Assert.Equal(2, service.Calls);
    });

    private static async Task ProgressiveProviderFailureAsync()
    {
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host == "api.kick.com") throw new HttpRequestException("Kick unavailable");
            if (request.RequestUri.Host == "id.twitch.tv") return Response(Validation);
            if (request.RequestUri.AbsolutePath == "/helix/streams/followed") return Response(TwitchPage(["liveone"], false));
            if (request.RequestUri.AbsolutePath == "/helix/channels/followed") return Response(TwitchPage([], true));
            return Response("""{"data":[]}""");
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "token";
        settings.FollowedChannels.KickChannelSlugs = ["kicklive"];
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")));
        var updates = new List<FollowedStreamsUpdate>();
        await foreach (var update in service.GetFollowedStreamUpdatesAsync(settings)) updates.Add(update);
        var final = updates.Last();
        Assert.Equal(0, final.PendingPlatforms.Count);
        Assert.Equal("liveone", final.Result.Streams.Single().Channel);
        Assert.SequenceEqual(new[] { PlatformKind.Twitch }, final.Result.SucceededPlatforms!);
        Assert.True(final.Result.Messages.Any(message => message.Contains("Kick", StringComparison.Ordinal)));
    }

    private static async Task StopProviderEnumerationAsync()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            throw new InvalidOperationException("Unreachable");
        }));
        var settings = new AppSettings();
        settings.FollowedChannels.KickChannelSlugs = ["kicklive"];
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")));
        await using (var enumerator = service.GetFollowedStreamUpdatesAsync(settings).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.SequenceEqual(new[] { PlatformKind.Kick }, enumerator.Current.PendingPlatforms);
        }
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class ProgressiveFollowedFixture : IFollowedStreamsService
    {
        internal bool SkipInitial { get; init; }
        internal int Calls { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal TaskCompletionSource PartialConsumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static FollowedLiveStream KickLive => Live("kicklive") with
        { Platform = PlatformKind.Kick, Url = "https://kick.com/kicklive" };

        public Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The UI must consume progressive results.");

        public Task<IReadOnlyList<FollowedChannel>> EnrichOfflineChannelsAsync(IReadOnlyList<FollowedChannel> channels,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FollowedChannel>>(
                channels.Select(channel => channel with { BannerImageUrl = $"https://images.example/{channel.Channel}/banner.jpg" }).ToArray());

        public async IAsyncEnumerable<FollowedStreamsUpdate> GetFollowedStreamUpdatesAsync(AppSettings settings,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            if (!SkipInitial && Calls == 1)
            {
                yield return new(Result([Live("oldtwitch"), KickLive], [Offline("twitchoff"), Offline("kickoff", PlatformKind.Kick)]), []);
                yield break;
            }
            if (settings.Chat.TwitchOAuthToken == "new-account")
            {
                yield return new(Result([Live("current")], []), []);
                yield break;
            }
            yield return new(new([Live("newtwitch")], [], [PlatformKind.Twitch], [Offline("twitchoff")], []), [PlatformKind.Kick]);
            PartialConsumed.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new(Result([Live(SkipInitial ? "stale" : "newtwitch"), KickLive],
                [Offline("twitchoff"), Offline("kickoff", PlatformKind.Kick)]), []);
        }
    }
}
