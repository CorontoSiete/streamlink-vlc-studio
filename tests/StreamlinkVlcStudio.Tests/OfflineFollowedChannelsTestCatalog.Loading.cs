internal static partial class OfflineFollowedChannelsTestCatalog
{
    private static async Task<FollowedLiveStreamsResult> LoadWithOfflineMetadataAsync(
        FollowedStreamsService service, AppSettings settings, CancellationToken cancellationToken = default)
    {
        var snapshot = await service.GetLiveFollowedStreamsAsync(settings, cancellationToken);
        return snapshot with
        {
            OfflineChannels = await service.EnrichOfflineChannelsAsync(snapshot.OfflineChannels ?? [], cancellationToken)
        };
    }

    private static Task StartupBeforeBannersAsync(PlatformKind platform) => TestSta.RunOffscreenAsync(async () =>
    {
        var bannerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBanner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/oauth2/validate") return Response(Validation);
            if (uri.AbsolutePath == "/helix/streams/followed") return Response(TwitchPage(["liveone"], false));
            if (uri.AbsolutePath == "/helix/channels/followed") return Response(TwitchPage(["liveone", "offline"], true));
            if (uri.AbsolutePath == "/helix/users") return Response("""{"data":[]}""");
            if (uri.Host == "api.kick.com")
                return Response("""{"data":[{"slug":"liveone","stream":{"is_live":true,"viewer_count":42}},{"slug":"offline","stream":{"is_live":false}}]}""");
            if (uri.AbsolutePath.EndsWith("/videos", StringComparison.Ordinal))
                return Response("""[{"is_live":false,"start_time":"2026-10-19T18:00:00Z"}]""");
            bannerStarted.TrySetResult();
            await releaseBanner.Task.WaitAsync(token);
            if (uri.Host == "gql.twitch.tv")
                return Response("""{"data":{"users":[{"id":"42","login":"offline","offlineImageURL":"https://images.example/banner.jpg","lastBroadcast":{"startedAt":"2026-10-19T18:00:00Z"}}]}}""");
            return Response("""{"id":42,"slug":"offline","offline_banner_image":{"src":"https://images.example/banner.jpg"}}""");
        }));
        var settings = new AppSettings();
        if (platform == PlatformKind.Kick) settings.FollowedChannels.KickChannelSlugs = ["liveone", "offline"];
        else settings.Chat.TwitchOAuthToken = "token";
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")));
        await using var feature = CreateFeature(service, settings);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var refresh = feature.RefreshFollowedChannelsAsync();
        try
        {
            await bannerStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Console.WriteLine($"STARTUP {platform} banner pending after {elapsed.Elapsed.TotalMilliseconds:F1} ms: " +
                $"live={feature.LiveFollowedChannels.Count}, offline={feature.OfflineFollowedChannels.Count}, refreshing={feature.IsFollowedChannelsRefreshing}");
            Assert.Equal(1, feature.LiveFollowedChannels.Count);
            Assert.Equal(1, feature.OfflineFollowedChannels.Count);
            Assert.Equal(false, feature.IsFollowedChannelsRefreshing);
            var card = feature.OfflineFollowedChannels.Single();
            var open = card.OpenCommand;
            Assert.True(open.CanExecute(null));
            Assert.True(ReferenceEquals(refresh, feature.RefreshFollowedChannelsAsync()));
            var liveCard = feature.LiveFollowedChannels.Single();
            card.TogglePinCommand.Execute(null);
            releaseBanner.TrySetResult();
            await refresh;
            Assert.True(ReferenceEquals(card, feature.OfflineFollowedChannels.Single()));
            Assert.True(ReferenceEquals(open, card.OpenCommand));
            Assert.True(ReferenceEquals(liveCard, feature.LiveFollowedChannels.Single()));
            Assert.True(card.IsPinned);
            Assert.Equal("https://images.example/banner.jpg", card.BannerImageUrl);
            Assert.Equal(new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero), card.LastLiveAtUtc);
        }
        finally
        {
            releaseBanner.TrySetResult();
            await refresh;
        }
    });

    private static async Task CachedBannerSnapshotAsync()
    {
        using var fixture = new OfflineMetadataFixture(1)
        {
            IncludeApiBanners = false,
            MetadataBody = BannerMetadata("https://images.example/old.jpg")
        };
        var initial = (await fixture.LoadAsync()).OfflineChannels!.Single();
        Assert.Equal("https://images.example/old.jpg", initial.BannerImageUrl);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.MetadataBody = BannerMetadata("https://images.example/new.jpg");
        var cached = (await fixture.LoadSnapshotAsync()).OfflineChannels!.Single();
        Assert.Equal(initial.BannerImageUrl, cached.BannerImageUrl);
        Assert.Equal(initial.LastLiveAtUtc, cached.LastLiveAtUtc);
        Assert.Equal(1, fixture.MetadataBatches.Count);
        Assert.Equal("https://images.example/new.jpg", (await fixture.LoadAsync()).OfflineChannels!.Single().BannerImageUrl);
        Assert.Equal(2, fixture.MetadataBatches.Count);

        static string BannerMetadata(string url) => JsonSerializer.Serialize(new
        {
            data = new
            {
                users = new[]
                {
                    new
                    {
                        id = "42", login = "channel000", bannerImageURL = url,
                        lastBroadcast = new { startedAt = MetadataNow.AddDays(-1) }
                    }
                }
            }
        });
    }

    private static Task SupersededMetadataAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new DeferredOfflineMetadataService();
        var settings = new AppSettings();
        await using var feature = CreateFeature(service, settings);
        var original = feature.RefreshFollowedChannelsAsync();
        var card = feature.OfflineFollowedChannels.Single();
        var seenBanners = new List<string>();
        card.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(card.BannerImageUrl)) seenBanners.Add(card.BannerImageUrl);
        };
        settings.Chat.TwitchOAuthToken = "different-account";
        service.Snapshot = Result([], [Offline("current")]);
        var replacement = feature.RefreshFollowedChannelsAsync();
        service.Metadata.SetResult([Offline("offline") with { BannerImageUrl = "https://images.example/stale.jpg" }]);
        await Task.WhenAll(original, replacement);
        Assert.Equal(false, seenBanners.Contains("https://images.example/stale.jpg"));
        Assert.Equal("current", feature.OfflineFollowedChannels.Single().Channel);
        Assert.Equal(2, service.StatusLoads);
    });

    private static Task ShutdownDuringMetadataAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new DeferredOfflineMetadataService();
        await using var feature = CreateFeature(service, new AppSettings());
        var refresh = feature.RefreshFollowedChannelsAsync();
        var card = feature.OfflineFollowedChannels.Single();
        var shutdown = feature.DisposeAsync().AsTask();
        Assert.True(service.MetadataToken.IsCancellationRequested);
        service.Metadata.SetResult([Offline("offline") with { BannerImageUrl = "https://images.example/late.jpg" }]);
        await Task.WhenAll(refresh, shutdown);
        Assert.Equal("", card.BannerImageUrl);
        Assert.Equal(false, card.OpenCommand.CanExecute(null));
    });

    private static Task MetadataFailureAfterSnapshotAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new DeferredOfflineMetadataService();
        await using var feature = CreateFeature(service, new AppSettings());
        var refresh = feature.RefreshFollowedChannelsAsync();
        var card = feature.OfflineFollowedChannels.Single();
        var status = feature.FollowedChannelsStatus;
        service.Metadata.SetException(new HttpRequestException("Optional metadata unavailable"));
        await refresh;
        Assert.True(ReferenceEquals(card, feature.OfflineFollowedChannels.Single()));
        Assert.Equal(status, feature.FollowedChannelsStatus);
        Assert.Equal("1 followed channel is offline.", feature.OfflineFollowedChannelsStatus);
        Assert.Equal(false, feature.IsFollowedChannelsRefreshing);
        Assert.True(card.OpenCommand.CanExecute(null));
    });

    private sealed class DeferredOfflineMetadataService : IFollowedStreamsService
    {
        internal FollowedLiveStreamsResult Snapshot { get; set; } = Result([], [Offline("offline")]);
        internal TaskCompletionSource<IReadOnlyList<FollowedChannel>> Metadata { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken MetadataToken { get; private set; }
        internal int StatusLoads { get; private set; }

        public Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            StatusLoads++;
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<FollowedChannel>> EnrichOfflineChannelsAsync(IReadOnlyList<FollowedChannel> channels,
            CancellationToken cancellationToken = default)
        {
            MetadataToken = cancellationToken;
            return StatusLoads == 1 ? Metadata.Task : Task.FromResult(channels);
        }
    }
}
