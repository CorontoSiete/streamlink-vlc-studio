internal static partial class OfflineFollowedChannelsTestCatalog
{
    private static async Task KickOfflineBannerSourcesAsync()
    {
        foreach (var (body, expected) in new[]
        {
            // Kick advertises the working responsive URLs; its fullsize conversion and
            // official banner_picture can both return AccessDenied for these same channels.
            ("""{"id":42,"slug":"artwork","offline_banner_image":{"src":"https://images.example/fullsize.jpg","srcset":"https://images.example/1200.webp 1200w, //images.example/588.webp 588w, https://images.example/702.webp 702w"},"banner_image":{"url":"https://images.example/profile.jpg"}}""",
                "https://images.example/702.webp"),
            ("""{"id":42,"slug":"ARTWORK","offline_banner_image":{"srcset":"https://images.example/344.webp 344w, https://images.example/588.webp 588w"}}""",
                "https://images.example/588.webp"),
            ("""{"id":42,"slug":"artwork","offline_banner_image":{"src":"//images.example/fullsize.webp","srcset":"file:///C:/private.png 700w, https://images.example/invalid.webp nope, https://images.example/zero.webp 0w"}}""",
                "https://images.example/fullsize.webp"),
            ("""{"id":42,"slug":"artwork","offline_banner_image":null,"banner_image":{"url":"//images.example/profile.jpg"}}""",
                "https://images.example/profile.jpg"),
            ("""{"id":42,"slug":"artwork","offline_banner_image":null}""", ""),
            ("""{"id":42,"slug":"other","offline_banner_image":{"src":"https://images.example/other.jpg"}}""",
                KickBannerFixture.ApiBanner),
            ("""{"id":42,"slug":"artwork","offline_banner_image":"malformed"}""", KickBannerFixture.ApiBanner),
            ("""{"id":42,"slug":"artwork","offline_banner_image":{"src":"file:///C:/private.png"}}""", KickBannerFixture.ApiBanner),
            ("null", KickBannerFixture.ApiBanner)
        })
        {
            using var fixture = new KickBannerFixture { BannerBody = body };
            var result = await fixture.LoadAsync();
            Assert.Equal(expected, result.OfflineChannels!.Single().BannerImageUrl);
            Assert.Equal(MetadataNow.AddDays(-1), result.OfflineChannels!.Single().LastLiveAtUtc);
            Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Kick));
            Assert.Equal(1, fixture.BannerRequests);
            Assert.Equal(1, fixture.HistoryRequests);
            await fixture.LoadAsync();
            Assert.Equal(1, fixture.BannerRequests);
            Assert.Equal(1, fixture.HistoryRequests);
        }
    }

    private static async Task KickOfflineBannerRecoveryAsync()
    {
        using var fixture = new KickBannerFixture();
        var original = (await fixture.LoadAsync()).OfflineChannels!.Single();
        Assert.Equal("https://images.example/working.webp", original.BannerImageUrl);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.BannerBody = "null";
        var failed = (await fixture.LoadAsync()).OfflineChannels!.Single();
        Assert.Equal(original.BannerImageUrl, failed.BannerImageUrl);
        Assert.Equal(original.LastLiveAtUtc, failed.LastLiveAtUtc);
        await fixture.LoadAsync();
        Assert.Equal(2, fixture.BannerRequests);
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        fixture.BannerBody = """{"id":42,"slug":"artwork","offline_banner_image":{"src":"https://images.example/updated.webp"}}""";
        Assert.Equal("https://images.example/updated.webp", (await fixture.LoadAsync()).OfflineChannels!.Single().BannerImageUrl);
        Assert.Equal(3, fixture.BannerRequests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        fixture.BannerBody = """{"id":42,"slug":"artwork","offline_banner_image":null}""";
        Assert.Equal("", (await fixture.LoadAsync()).OfflineChannels!.Single().BannerImageUrl);
    }

    private static async Task KickOfflineBannerHistoryDeadlineAsync()
    {
        using var fixture = new KickBannerFixture { BlockHistory = true };
        var result = await fixture.LoadAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("https://images.example/working.webp", result.OfflineChannels!.Single().BannerImageUrl);
        Assert.Equal(null, result.OfflineChannels!.Single().LastLiveAtUtc);
        Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Kick));
        fixture.BlockHistory = false;
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        var recovered = (await fixture.LoadAsync()).OfflineChannels!.Single();
        Assert.Equal("https://images.example/working.webp", recovered.BannerImageUrl);
        Assert.Equal(MetadataNow.AddDays(-1), recovered.LastLiveAtUtc);
        Assert.Equal(2, fixture.HistoryRequests);
    }

    private sealed class KickBannerFixture : IDisposable
    {
        internal const string ApiBanner = "https://images.example/stale-api-banner";
        private readonly HttpClient client;
        private readonly FollowedStreamsService service;
        internal ManualTimeProvider Clock { get; } = new(MetadataNow);
        internal string BannerBody { get; set; } = """{"id":42,"slug":"artwork","offline_banner_image":{"src":"https://images.example/inaccessible.jpg","srcset":"https://images.example/working.webp 702w"}}""";
        internal bool BlockHistory { get; set; }
        internal int BannerRequests { get; private set; }
        internal int HistoryRequests { get; private set; }

        internal KickBannerFixture()
        {
            client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
            {
                var uri = request.RequestUri!;
                if (uri.Host == "kick.com")
                {
                    Assert.Equal(null, request.Headers.Authorization);
                    Assert.Equal("https://kick.com/artwork", request.Headers.Referrer!.AbsoluteUri);
                    if (uri.AbsolutePath == "/api/v2/channels/artwork")
                    {
                        BannerRequests++;
                        return Response(BannerBody);
                    }
                    Assert.Equal("/api/v2/channels/artwork/videos", uri.AbsolutePath);
                    HistoryRequests++;
                    if (BlockHistory) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return Response("""[{"is_live":false,"start_time":"2026-10-19T18:00:00Z"}]""");
                }
                Assert.Equal("api.kick.com", uri.Host);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                return Response("""{"data":[{"slug":"artwork","banner_picture":"https://images.example/stale-api-banner","stream":{"is_live":false}}]}""");
            }));
            service = new FollowedStreamsService(new MemoryLogger(), client,
                new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")))
            {
                MetadataTimeProvider = Clock,
                OfflineMetadataTimeout = TimeSpan.FromMilliseconds(300)
            };
        }

        internal Task<FollowedLiveStreamsResult> LoadAsync()
        {
            var settings = new AppSettings();
            settings.FollowedChannels.KickChannelSlugs = ["artwork"];
            return LoadWithOfflineMetadataAsync(service, settings);
        }

        public void Dispose() => client.Dispose();
    }
}
