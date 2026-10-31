internal static partial class OfflineFollowedChannelsTestCatalog
{
    private static readonly DateTimeOffset MetadataNow = new(2026, 10, 20, 18, 0, 0, TimeSpan.Zero);

    private static async Task TwitchOfflineMetadataAsync()
    {
        using var fixture = new OfflineMetadataFixture(105);
        var result = await fixture.LoadAsync();
        Assert.Equal(105, result.OfflineChannels!.Count);
        Assert.SequenceEqual(new[] { 100, 5 }, fixture.MetadataBatches.Select(batch => batch.Length));
        Assert.True(fixture.MetadataBatches.SelectMany(batch => batch).Distinct().Count() == 105);
        foreach (var channel in result.OfflineChannels)
        {
            Assert.Equal($"https://images.example/{channel.Channel}-offline.jpg", channel.BannerImageUrl);
            Assert.Equal(MetadataNow.AddDays(-1), channel.LastLiveAtUtc);
            Assert.Equal($"https://images.example/{channel.Channel}.png", channel.ProfileImageUrl);
        }
    }

    private static async Task OfflineMetadataCacheAsync()
    {
        using var fixture = new OfflineMetadataFixture(1);
        var original = (await fixture.LoadAsync()).OfflineChannels!.Single();
        await fixture.LoadAsync();
        Assert.Equal(1, fixture.MetadataBatches.Count);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.MetadataStatus = HttpStatusCode.ServiceUnavailable;
        var failed = (await fixture.LoadAsync()).OfflineChannels!.Single();
        Assert.Equal(original.LastLiveAtUtc, failed.LastLiveAtUtc);
        Assert.Equal(original.BannerImageUrl, failed.BannerImageUrl);
        await fixture.LoadAsync();
        Assert.Equal(2, fixture.MetadataBatches.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        fixture.LastBroadcast = MetadataNow.AddMinutes(-10);
        fixture.MetadataStatus = HttpStatusCode.OK;
        var recovered = (await fixture.LoadAsync()).OfflineChannels!.Single();
        Assert.Equal(fixture.LastBroadcast, recovered.LastLiveAtUtc);
        Assert.Equal(3, fixture.MetadataBatches.Count);
    }

    private static async Task TwitchOfflineBannerFallbackAsync()
    {
        foreach (var (offline, profile, official, expected) in new[]
        {
            ((string?)null, "//images.example/chopp-profile-banner.png", false, "https://images.example/chopp-profile-banner.png"),
            ("https://images.example/offline.jpg", "https://images.example/profile.jpg", false, "https://images.example/offline.jpg"),
            ("https://images.example/web-offline.jpg", "https://images.example/profile.jpg", true, "https://images.example/channel000-offline.jpg"),
            ((string?)null, (string?)null, false, "")
        })
        {
            using var fixture = new OfflineMetadataFixture(1)
            {
                IncludeApiBanners = official,
                MetadataBody = JsonSerializer.Serialize(new
                {
                    data = new
                    {
                        users = new[]
                        {
                            new
                            {
                                id = "853183942", login = "channel000", offlineImageURL = offline, bannerImageURL = profile,
                                lastBroadcast = new { startedAt = MetadataNow.AddDays(-1) }
                            }
                        }
                    }
                })
            };
            var channel = (await fixture.LoadAsync()).OfflineChannels!.Single();
            Assert.Equal(expected, channel.BannerImageUrl);
            Assert.Equal(MetadataNow.AddDays(-1), channel.LastLiveAtUtc);
            Assert.True(fixture.LastMetadataQuery.Contains("bannerImageURL", StringComparison.Ordinal));
            await fixture.LoadAsync();
            Assert.Equal(1, fixture.MetadataBatches.Count);
            fixture.Clock.Advance(TimeSpan.FromMinutes(6));
            fixture.MetadataStatus = HttpStatusCode.ServiceUnavailable;
            Assert.Equal(expected, (await fixture.LoadAsync()).OfflineChannels!.Single().BannerImageUrl);
        }
    }

    private static async Task OfflineMetadataLiveTransitionAsync()
    {
        using var fixture = new OfflineMetadataFixture(1);
        await fixture.LoadAsync();
        fixture.IsLive = true;
        var live = await fixture.LoadAsync();
        Assert.Equal(0, live.OfflineChannels!.Count);
        Assert.Equal(1, fixture.MetadataBatches.Count);
        fixture.IsLive = false;
        // No public recording is required: a start time observed live is still real history.
        fixture.MetadataStatus = HttpStatusCode.ServiceUnavailable;
        var ended = (await fixture.LoadAsync()).OfflineChannels!.Single();
        Assert.Equal(MetadataNow.AddHours(-1), ended.LastLiveAtUtc);
        Assert.Equal(2, fixture.MetadataBatches.Count);
    }

    private static async Task OfflineMetadataUnavailableAsync()
    {
        foreach (var body in new[]
        {
            "null", "{}", "{\"errors\":[{\"message\":\"Unavailable\"}]}",
            "{\"data\":{\"users\":[null]}}",
            "{\"data\":{\"users\":[{\"id\":\"42\",\"login\":\"outsider\",\"lastBroadcast\":{\"startedAt\":\"2026-10-19T00:00:00Z\"}}]}}",
            UserMetadata("channel000", "not-a-date"),
            UserMetadata("channel000", "0001-01-01T00:00:00Z"),
            UserMetadata("channel000", "2027-10-21T00:00:00Z"),
            "{\"data\":{\"users\":[{\"id\":\"42\",\"login\":\"channel000\",\"lastBroadcast\":null}]}}"
        })
        {
            using var fixture = new OfflineMetadataFixture(1) { MetadataBody = body };
            var result = await fixture.LoadAsync();
            var channel = result.OfflineChannels!.Single();
            Assert.Equal(null, channel.LastLiveAtUtc);
            // The official users response still supplies a banner when web metadata fails.
            Assert.Equal("https://images.example/channel000-offline.jpg", channel.BannerImageUrl);
            Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
        }
    }

    private static string UserMetadata(string login, string startedAt) => JsonSerializer.Serialize(new
    {
        data = new { users = new[] { new { id = "42", login, lastBroadcast = new { startedAt } } } }
    });

    private static async Task KickOfflineMetadataAsync()
    {
        var historyRequests = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "kick.com")
            {
                Assert.Equal(null, request.Headers.Authorization);
                Assert.Equal("https://kick.com/history", request.Headers.Referrer!.AbsoluteUri);
                if (request.RequestUri.AbsolutePath == "/api/v2/channels/history")
                    return Response("""{"id":42,"slug":"history","offline_banner_image":null,"banner_image":{"url":"//images.example/banner.jpg"}}""");
                historyRequests++;
                return Response("""
                    [{"is_live":false,"start_time":"2026-10-18 18:00:00","created_at":"2026-10-20 17:59:00"},
                     {"is_live":false,"start_time":"2026-10-19 18:00:00"},
                     {"is_live":true,"start_time":"2026-10-20 16:00:00"},
                     {"start_time":"0001-01-01T00:00:00Z"},
                     {"start_time":"2027-01-01T00:00:00Z"}]
                    """);
            }
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Response("""
                {"data":[{"slug":"history","banner_picture":"//images.example/banner.jpg","stream":{"is_live":false,"start_time":"0001-01-01T00:00:00Z"}}]}
                """);
        }));
        var settings = new AppSettings();
        settings.FollowedChannels.KickChannelSlugs = ["history"];
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")))
        { MetadataTimeProvider = new ManualTimeProvider(MetadataNow) };
        var channel = (await LoadWithOfflineMetadataAsync(service, settings)).OfflineChannels!.Single();
        Assert.Equal("https://images.example/banner.jpg", channel.BannerImageUrl);
        Assert.Equal(MetadataNow.AddDays(-1), channel.LastLiveAtUtc);
        await LoadWithOfflineMetadataAsync(service, settings);
        Assert.Equal(1, historyRequests);
    }

    private static Task OfflineMetadataDeadlineAsync() => VerifyHistoryCancellationAsync(cancelCaller: false);
    private static Task OfflineMetadataCancellationAsync() => VerifyHistoryCancellationAsync(cancelCaller: true);

    private static async Task VerifyHistoryCancellationAsync(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var historyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "kick.com")
            {
                if (!request.RequestUri.AbsolutePath.EndsWith("/videos", StringComparison.Ordinal))
                    return Response(JsonSerializer.Serialize(new
                    {
                        id = 42,
                        slug = request.RequestUri.Segments.Last(),
                        offline_banner_image = (object?)null
                    }));
                var current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                historyStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref active); }
            }
            return Response(JsonSerializer.Serialize(new
            {
                data = Enumerable.Range(0, 12).Select(index => new { slug = $"history{index}", stream = (object?)null })
            }));
        }));
        var settings = new AppSettings();
        settings.FollowedChannels.KickChannelSlugs = Enumerable.Range(0, 12).Select(index => $"history{index}").ToList();
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")))
        { OfflineMetadataTimeout = TimeSpan.FromMilliseconds(150) };
        var pending = LoadWithOfflineMetadataAsync(service, settings, caller.Token);
        await historyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(12, result.OfflineChannels!.Count);
            Assert.True(result.OfflineChannels.All(channel => channel.LastLiveAtUtc is null));
            Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Kick));
        }
        Assert.True(maximumActive is > 0 and <= 4);
        Assert.Equal(0, active);
    }

    private static Task LastLiveLabelsAsync()
    {
        foreach (var (age, expected) in new[]
        {
            (TimeSpan.Zero, "Last live just now"), (TimeSpan.FromSeconds(59), "Last live just now"),
            (TimeSpan.FromMinutes(1), "Last live 1 minute ago"), (TimeSpan.FromMinutes(59), "Last live 59 minutes ago"),
            (TimeSpan.FromHours(1), "Last live 1 hour ago"), (TimeSpan.FromHours(23), "Last live 23 hours ago"),
            (TimeSpan.FromDays(1), "Last live 1 day ago"), (TimeSpan.FromDays(2), "Last live 2 days ago"),
            (TimeSpan.FromDays(30), "Last live 1 month ago"), (TimeSpan.FromDays(365), "Last live 1 year ago")
        }) Assert.Equal(expected, OfflineFollowedChannelViewModel.FormatLastLive(MetadataNow - age, MetadataNow));
        foreach (var value in new DateTimeOffset?[] { null, DateTimeOffset.MinValue, MetadataNow.AddMinutes(1) })
            Assert.Equal("Last live unavailable", OfflineFollowedChannelViewModel.FormatLastLive(value, MetadataNow));

        var original = Offline("history");
        var card = new OfflineFollowedChannelViewModel(original, _ => Task.CompletedTask);
        var open = card.OpenCommand;
        var properties = new List<string?>();
        card.PropertyChanged += (_, args) => properties.Add(args.PropertyName);
        card.Update(original with { BannerImageUrl = "https://images.example/banner.jpg", LastLiveAtUtc = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.Equal("Last live 1 day ago", card.LastLiveText);
        Assert.True(card.LastLiveToolTip.StartsWith("Latest available broadcast started ", StringComparison.Ordinal));
        Assert.True(properties.Contains(nameof(card.BannerImageUrl)) && properties.Contains(nameof(card.LastLiveText)));
        card.Update(original);
        Assert.Equal("Last live unavailable", card.LastLiveText);
        Assert.Equal("", card.BannerImageUrl);
        Assert.True(ReferenceEquals(open, card.OpenCommand));
        return Task.CompletedTask;
    }

    private sealed class OfflineMetadataFixture : IDisposable
    {
        private readonly HttpClient client;
        private readonly FollowedStreamsService service;
        private readonly string[] channels;
        internal ManualTimeProvider Clock { get; } = new(MetadataNow);
        internal List<string[]> MetadataBatches { get; } = [];
        internal HttpStatusCode MetadataStatus { get; set; } = HttpStatusCode.OK;
        internal DateTimeOffset LastBroadcast { get; set; } = MetadataNow.AddDays(-1);
        internal bool IsLive { get; set; }
        internal string? MetadataBody { get; set; }
        internal bool IncludeApiBanners { get; init; } = true;
        internal string LastMetadataQuery { get; private set; } = "";

        internal OfflineMetadataFixture(int count)
        {
            channels = Enumerable.Range(0, count).Select(index => $"channel{index:000}").ToArray();
            client = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "gql.twitch.tv")
                {
                    Assert.Equal(null, request.Headers.Authorization);
                    Assert.Equal(TwitchGraphQlTransport.PublicClientId, request.Headers.GetValues("Client-Id").Single());
                    using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    LastMetadataQuery = body.RootElement.GetProperty("query").GetString()!;
                    var logins = body.RootElement.GetProperty("variables").GetProperty("logins").EnumerateArray()
                        .Select(login => login.GetString()!).ToArray();
                    MetadataBatches.Add(logins);
                    return Response(MetadataBody ?? JsonSerializer.Serialize(new
                    {
                        data = new { users = logins.Select(login => new { id = "42", login, lastBroadcast = new { startedAt = LastBroadcast } }) }
                    }), MetadataStatus);
                }
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                return uri.AbsolutePath switch
                {
                    "/oauth2/validate" => Response(Validation),
                    "/helix/channels/followed" => Response(TwitchPage(channels, true)),
                    "/helix/streams/followed" => Response(IsLive
                        ? "{\"data\":[{\"user_login\":\"channel000\",\"user_name\":\"Channel\",\"started_at\":\"2026-10-20T17:00:00Z\"}],\"pagination\":{}}"
                        : TwitchPage([], false)),
                    "/helix/users" => Response(JsonSerializer.Serialize(new
                    {
                        data = QueryValues(uri, "login").Select(login => new
                        {
                            login,
                            profile_image_url = $"https://images.example/{login}.png",
                            offline_image_url = IncludeApiBanners ? $"//images.example/{login}-offline.jpg" : ""
                        })
                    })),
                    _ => throw new InvalidOperationException($"Unexpected metadata request: {uri}")
                };
            }));
            service = new FollowedStreamsService(new MemoryLogger(), client) { MetadataTimeProvider = Clock };
        }

        internal Task<FollowedLiveStreamsResult> LoadAsync()
        {
            var settings = new AppSettings();
            settings.Chat.TwitchOAuthToken = "token";
            return LoadWithOfflineMetadataAsync(service, settings);
        }

        internal Task<FollowedLiveStreamsResult> LoadSnapshotAsync()
        {
            var settings = new AppSettings();
            settings.Chat.TwitchOAuthToken = "token";
            return service.GetLiveFollowedStreamsAsync(settings);
        }

        public void Dispose() => client.Dispose();
    }
}
