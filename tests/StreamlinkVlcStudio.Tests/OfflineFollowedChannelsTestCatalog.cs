internal static partial class OfflineFollowedChannelsTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("offline followed channels: Twitch loads every follow page and excludes all live channels", TwitchPaginationAsync),
        ("offline followed channels: Twitch batches avatars across live and offline channels", TwitchProfileBatchingAsync),
        ("offline followed channels: a follow-list error does not hide live streams or alter notification health", TwitchFollowListFailureAsync),
        ("offline followed channels: malformed follows cannot produce offline cards", TwitchMalformedFollowsAsync),
        ("offline followed channels: incomplete live pages cannot classify Twitch channels as offline", TwitchIncompleteLiveAsync),
        ("offline followed channels: repeated follow cursors and excessive pagination stop safely", TwitchPaginationGuardsAsync),
        ("offline followed channels: optional avatar failures do not hide offline channels", TwitchProfileFailureAsync),
        ("offline followed channels: cancellation during follow-list loading propagates", TwitchCancellationAsync),
        ("offline followed channels: Kick imports and manual follows share channel and avatar batches", KickBatchingAsync),
        ("offline followed channels: Kick never treats missing or malformed channels as offline", KickUnavailableAsync),
        ("offline followed channels: matching channel names on different platforms remain distinct", PlatformIdentityAsync),
        ("offline followed channels: refresh moves channels between live and offline and removes unfollows", TransitionsAsync),
        ("offline followed channels: unchanged refreshes reuse cards and commands while metadata updates", CardReuseAsync),
        ("offline followed channels: refresh loading and failure states remain truthful", RefreshStatesAsync),
        ("offline followed channels: superseded credential refreshes cannot overwrite offline cards", SupersededRefreshAsync),
        ("offline followed channels: Twitch cards browse videos without attempting live playback", () => OpenVideosAsync(PlatformKind.Twitch)),
        ("offline followed channels: Kick cards browse videos without attempting live playback", () => OpenVideosAsync(PlatformKind.Kick)),
        ("offline followed channels: disposed cards cannot browse videos", DisposedCommandAsync),
        ("offline followed channels: shutdown ignores results from late providers", LateShutdownAsync),
        ("offline followed channels: saved pin keys are validated and keep platform identities distinct", PinSettingsAsync),
        ("offline followed channels: pinning and unpinning restore provider order without rebuilding cards", PinOrderingAsync),
        ("offline followed channels: pinned refreshes reuse commands and update channel metadata", PinRefreshAsync),
        ("offline followed channels: pins survive live transitions and ignore removed or disposed cards", PinTransitionsAsync),
        ("offline followed channels: pins edited during a refresh use the latest saved preference", PinDuringRefreshAsync),
        ("offline followed channels: replacing settings updates pin order and releases old settings", PinSettingsReplacementAsync),
        ("offline followed channels: automatic pin saves and immediate shutdown edits survive restart", PinPersistenceAsync),
        ("offline followed channels: pin buttons accept independent activation without browsing videos", PinButtonActivationAsync),
        ("offline followed channels: the expandable section renders below live cards at desktop and compact widths", LayoutAsync)
    ];

    private static async Task TwitchPaginationAsync()
    {
        using var fixture = new TwitchFixture(
            [TwitchPage(["liveone"], false, "live+next/="), TwitchPage(["secondlive"], false)],
            [TwitchPage(["LIVEONE", "off_a"], true, "follow+next/="),
             TwitchPage(["SECONDlive", "OFF_A", "off_b"], true)]);
        var result = await fixture.LoadAsync();
        Assert.SequenceEqual(new[] { "off_a", "off_b" }, result.OfflineChannels!.Select(channel => channel.Channel));
        Assert.Equal(2, result.Streams.Count);
        Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
        Assert.Equal(false, result.OfflineMessages!.Any(message => message.StartsWith("Twitch:", StringComparison.Ordinal)));
        foreach (var channel in result.OfflineChannels!)
        {
            Assert.Equal($"https://www.twitch.tv/{channel.Channel}", channel.Url);
            Assert.Equal($"https://images.example/{channel.Channel}.png", channel.ProfileImageUrl);
            Assert.Equal($"Display {channel.Channel}", channel.DisplayName);
        }
        var liveRequests = fixture.Requests.Where(uri => uri.AbsolutePath == "/helix/streams/followed").ToArray();
        var followRequests = fixture.Requests.Where(uri => uri.AbsolutePath == "/helix/channels/followed").ToArray();
        Assert.Equal(2, liveRequests.Length);
        Assert.Equal(2, followRequests.Length);
        Assert.Equal("live+next/=", QueryValues(liveRequests[1], "after").Single());
        Assert.Equal("follow+next/=", QueryValues(followRequests[1], "after").Single());
        foreach (var uri in liveRequests.Concat(followRequests))
        {
            Assert.Equal("1", QueryValues(uri, "user_id").Single());
            Assert.Equal("100", QueryValues(uri, "first").Single());
        }
    }

    private static async Task TwitchProfileBatchingAsync()
    {
        var channels = Enumerable.Range(0, 101).Select(index => $"channel{index:000}").ToArray();
        using var fixture = new TwitchFixture([TwitchPage([channels[0]], false)],
            [TwitchPage(channels.Take(100), true, "next"), TwitchPage(channels.Skip(100), true)]);
        var result = await fixture.LoadAsync();
        Assert.Equal(100, result.OfflineChannels!.Count);
        var profileRequests = fixture.Requests.Where(uri => uri.AbsolutePath == "/helix/users").ToArray();
        Assert.SequenceEqual(new[] { 100, 1 }, profileRequests.Select(uri => QueryValues(uri, "login").Length));
        Assert.Equal(101, profileRequests.SelectMany(uri => QueryValues(uri, "login")).Distinct().Count());
        Assert.True(result.OfflineChannels.All(channel => channel.ProfileImageUrl.Length > 0));
    }

    private static async Task TwitchFollowListFailureAsync()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.ServiceUnavailable })
        {
            using var fixture = new TwitchFixture([TwitchPage(["liveone"], false)], [TwitchPage(["offline"], true)])
            {
                FollowStatus = status
            };
            var result = await fixture.LoadAsync();
            Assert.Equal("liveone", result.Streams.Single().Channel);
            Assert.Equal(0, result.OfflineChannels!.Count);
            Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
            Assert.Equal(false, result.Messages.Any(message => message.StartsWith("Twitch:", StringComparison.Ordinal)));
            Assert.True(result.OfflineMessages!.Any(message => message.Contains("offline followed channels unavailable", StringComparison.Ordinal)));
        }
    }

    private static async Task TwitchMalformedFollowsAsync()
    {
        foreach (var body in new[]
        {
            "null", "{}", "{\"data\":null}", "{\"data\":[null]}",
            "{\"data\":[{\"broadcaster_login\":17}]}",
            "{\"data\":[{\"broadcaster_login\":\"bad slug\"}]}",
            "{\"data\":[{\"broadcaster_login\":\"offline\"}],\"pagination\":null}",
            "{\"data\":[{\"broadcaster_login\":\"offline\"}],\"pagination\":{\"cursor\":17}}"
        })
        {
            using var fixture = new TwitchFixture([TwitchPage([], false)],
                [TwitchPage(["valid"], true, "next"), body]);
            var result = await fixture.LoadAsync();
            Assert.Equal(0, result.OfflineChannels!.Count);
            Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
            Assert.True(result.OfflineMessages!.Any(message => message.StartsWith("Twitch:", StringComparison.Ordinal)));
        }
    }

    private static async Task TwitchIncompleteLiveAsync()
    {
        using var fixture = new TwitchFixture([TwitchPage(["liveone"], false, "next"), "{\"data\":null}"],
            [TwitchPage(["liveone", "offline"], true)]);
        var result = await fixture.LoadAsync();
        Assert.Equal("liveone", result.Streams.Single().Channel);
        Assert.Equal(0, result.OfflineChannels!.Count);
        Assert.Equal(false, result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
        Assert.Equal(0, fixture.Requests.Count(uri => uri.AbsolutePath == "/helix/channels/followed"));
    }

    private static async Task TwitchPaginationGuardsAsync()
    {
        using var repeated = new TwitchFixture([TwitchPage([], false)],
            [TwitchPage(["offline"], true, "same"), TwitchPage(["offline"], true, "same")]);
        var repeatedResult = await repeated.LoadAsync();
        Assert.Equal(0, repeatedResult.OfflineChannels!.Count);
        Assert.Equal(2, repeated.Requests.Count(uri => uri.AbsolutePath == "/helix/channels/followed"));
        Assert.True(repeatedResult.OfflineMessages!.Any(message => message.Contains("repeated a cursor", StringComparison.Ordinal)));

        using var excessive = new TwitchFixture([TwitchPage([], false)], [])
        {
            FollowPage = index => TwitchPage(["offline"], true, $"cursor-{index}")
        };
        var excessiveResult = await excessive.LoadAsync();
        Assert.Equal(0, excessiveResult.OfflineChannels!.Count);
        Assert.Equal(100, excessive.Requests.Count(uri => uri.AbsolutePath == "/helix/channels/followed"));
        Assert.True(excessiveResult.OfflineMessages!.Any(message => message.Contains("safety limit", StringComparison.Ordinal)));
    }

    private static async Task TwitchProfileFailureAsync()
    {
        using var fixture = new TwitchFixture([TwitchPage([], false)], [TwitchPage(["offline"], true)])
        {
            FailProfiles = true
        };
        var result = await fixture.LoadAsync();
        var channel = result.OfflineChannels!.Single();
        Assert.Equal("offline", channel.Channel);
        Assert.Equal("", channel.ProfileImageUrl);
        Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Twitch));
        Assert.Equal(false, result.OfflineMessages!.Any(message => message.StartsWith("Twitch:", StringComparison.Ordinal)));
    }

    private static async Task TwitchCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var followStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return Response(Validation);
            if (request.RequestUri.AbsolutePath == "/helix/streams/followed") return Response(TwitchPage([], false));
            Assert.Equal("/helix/channels/followed", request.RequestUri.AbsolutePath);
            followStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(TwitchPage([], true));
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "token";
        var service = new FollowedStreamsService(new MemoryLogger(), client);
        var pending = service.GetLiveFollowedStreamsAsync(settings, cancellation.Token);
        await followStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    private static async Task KickBatchingAsync()
    {
        var settings = new AppSettings();
        settings.FollowedChannels.KickChannelSlugs = Enumerable.Range(0, 51).Select(index => $"kick{index:00}").ToList();
        settings.FollowedChannels.KickImportedChannelSlugs = ["KICK00", "kick50", "missing"];
        var channelBatches = new List<int>();
        var profileBatches = new List<int>();
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("token", request.Headers.Authorization!.Parameter);
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/public/v1/users")
            {
                var userIds = QueryValues(uri, "id");
                profileBatches.Add(userIds.Length);
                return Response(JsonSerializer.Serialize(new
                {
                    data = userIds.Select(userId => new { user_id = userId, profile_picture = $"//images.example/{userId}.png" })
                }));
            }
            Assert.Equal("/public/v1/channels", uri.AbsolutePath);
            var slugs = QueryValues(uri, "slug");
            channelBatches.Add(slugs.Length);
            var rows = slugs.Where(slug => slug != "missing").Select(slug => new Dictionary<string, object?>
            {
                ["slug"] = slug,
                ["broadcaster_user_id"] = int.Parse(slug[4..], CultureInfo.InvariantCulture) + 100,
                ["stream"] = slug == "kick00" ? new { is_live = true, viewer_count = 42 }
                    : int.Parse(slug[4..], CultureInfo.InvariantCulture) % 2 == 0 ? null : (object)new { is_live = false }
            }).Append(new Dictionary<string, object?> { ["slug"] = "outsider", ["stream"] = null });
            return Response(JsonSerializer.Serialize(new { data = rows }));
        }));
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")));
        var result = await service.GetLiveFollowedStreamsAsync(settings);
        Assert.SequenceEqual(new[] { 50, 2 }, channelBatches);
        Assert.SequenceEqual(new[] { 50, 1 }, profileBatches);
        Assert.Equal("kick00", result.Streams.Single().Channel);
        Assert.Equal(50, result.OfflineChannels!.Count);
        Assert.Equal(false, result.OfflineChannels.Any(channel => channel.Channel is "missing" or "outsider" or "kick00"));
        Assert.True(result.OfflineChannels.All(channel => channel.ProfileImageUrl.StartsWith("https://images.example/", StringComparison.Ordinal)));
        Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Kick));
        Assert.True(result.OfflineMessages!.Any(message => message.Contains("status could not be determined for 1", StringComparison.Ordinal)));
    }

    private static async Task KickUnavailableAsync()
    {
        var settings = new AppSettings();
        settings.FollowedChannels.KickChannelSlugs = ["offline", "malformed", "missing"];
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => Response("""
            {"data":[{"slug":"offline","stream":{"is_live":false}},
                     {"slug":"malformed","stream":17},
                     {"slug":"outsider","stream":null}]}
            """)));
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")));
        var result = await service.GetLiveFollowedStreamsAsync(settings);
        Assert.Equal("offline", result.OfflineChannels!.Single().Channel);
        Assert.Equal(0, result.Streams.Count);
        Assert.Equal(false, result.SucceededPlatforms!.Contains(PlatformKind.Kick));
        Assert.True(result.OfflineMessages!.Any(message => message.Contains("malformed", StringComparison.Ordinal)));
    }

    private static async Task PlatformIdentityAsync()
    {
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "token";
        settings.FollowedChannels.KickChannelSlugs = ["same"];
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "id.twitch.tv") return Response(Validation);
            return uri.AbsolutePath switch
            {
                "/helix/streams/followed" => Response(TwitchPage([], false)),
                "/helix/channels/followed" => Response(TwitchPage(["same"], true)),
                "/helix/users" => Response("{\"data\":[]}"),
                "/public/v1/channels" => Response("""
                    {"data":[{"slug":"same","stream":{"is_live":true}},
                             {"slug":"SAME","stream":{"is_live":false}}]}
                    """),
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
        }));
        var service = new FollowedStreamsService(new MemoryLogger(), client,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")));
        var result = await service.GetLiveFollowedStreamsAsync(settings);
        Assert.Equal(PlatformKind.Kick, result.Streams.Single().Platform);
        var offline = result.OfflineChannels!.Single();
        Assert.Equal(PlatformKind.Twitch, offline.Platform);
        Assert.Equal("same", offline.Channel);
    }

    private const string Validation = """
        {"client_id":"client","user_id":"1","login":"viewer","scopes":["user:read:follows"],"expires_in":3600}
        """;

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string[] QueryValues(Uri uri, string name) => uri.Query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2))
        .Where(parts => parts.Length == 2 && parts[0] == name)
        .Select(parts => Uri.UnescapeDataString(parts[1])).ToArray();

    private static string TwitchPage(IEnumerable<string> channels, bool followed, string cursor = "") =>
        JsonSerializer.Serialize(new
        {
            data = channels.Select(channel => new Dictionary<string, object>
            {
                [followed ? "broadcaster_login" : "user_login"] = channel,
                [followed ? "broadcaster_name" : "user_name"] = $"Display {channel}",
                [followed ? "broadcaster_id" : "user_id"] = "42",
                ["title"] = "Live stream",
                ["viewer_count"] = 42
            }),
            pagination = string.IsNullOrWhiteSpace(cursor) ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["cursor"] = cursor }
        });

    private sealed class TwitchFixture : IDisposable
    {
        private readonly HttpClient client;
        private int streamPageIndex;
        private int followPageIndex;
        internal List<Uri> Requests { get; } = [];
        internal HttpStatusCode FollowStatus { get; init; } = HttpStatusCode.OK;
        internal bool FailProfiles { get; init; }
        internal Func<int, string>? FollowPage { get; init; }

        internal TwitchFixture(string[] streamPages, string[] followPages)
        {
            client = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!;
                Requests.Add(uri);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("token", request.Headers.Authorization.Parameter);
                if (uri.Host == "id.twitch.tv") return Response(Validation);
                Assert.Equal("client", request.Headers.GetValues("Client-Id").Single());
                return uri.AbsolutePath switch
                {
                    "/helix/streams/followed" => Response(streamPages[streamPageIndex++]),
                    "/helix/channels/followed" => Response(FollowPage is null
                        ? followPages[followPageIndex++] : FollowPage(followPageIndex++), FollowStatus),
                    "/helix/users" => FailProfiles ? Response("{}", HttpStatusCode.ServiceUnavailable)
                        : Response(JsonSerializer.Serialize(new
                        {
                            data = QueryValues(uri, "login").Select(login => new
                            {
                                login,
                                profile_image_url = $"https://images.example/{login}.png"
                            })
                        })),
                    _ => throw new InvalidOperationException($"Unexpected request: {uri}")
                };
            }));
        }

        internal Task<FollowedLiveStreamsResult> LoadAsync()
        {
            var settings = new AppSettings();
            settings.Chat.TwitchOAuthToken = "oauth:token";
            settings.Chat.TwitchClientId = "configured-mismatch";
            return new FollowedStreamsService(new MemoryLogger(), client).GetLiveFollowedStreamsAsync(settings);
        }

        public void Dispose() => client.Dispose();
    }
}
