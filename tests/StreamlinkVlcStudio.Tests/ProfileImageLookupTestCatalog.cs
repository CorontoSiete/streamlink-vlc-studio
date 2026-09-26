using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;

internal static class ProfileImageLookupTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("profile image lookup: Twitch preserves batching, authentication and case-insensitive logins",
            () => BatchingAsync(PlatformKind.Twitch)),
        ("profile image lookup: Kick preserves batching, authentication and image normalization",
            () => BatchingAsync(PlatformKind.Kick)),
        ("profile image lookup: malformed optional rows do not hide valid images", MalformedRowsAsync),
        ("profile image lookup: empty identities do not send requests", EmptyIdentitiesAsync),
        ("profile image lookup: errors, limits and cancellation propagate", FailuresAsync)
    ];

    private static async Task BatchingAsync(PlatformKind platform)
    {
        var twitch = platform == PlatformKind.Twitch;
        var batchSize = twitch ? 100 : 50;
        var identities = Enumerable.Range(0, batchSize + 1).Select(index => $"user{index}").ToArray();
        var expected = identities.Concat(twitch ? ["a&b"] : new[] { "USER0", "a&b" }).ToArray();
        var requested = new List<string>();
        var batchLengths = new List<int>();
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(twitch ? "api.twitch.tv" : "api.kick.com", request.RequestUri!.Host);
            Assert.Equal(twitch ? "/helix/users" : "/public/v1/users", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("token", request.Headers.Authorization.Parameter);
            if (twitch) Assert.Equal("client", request.Headers.GetValues("Client-Id").Single());
            else Assert.Equal(false, request.Headers.Contains("Client-Id"));

            var batch = request.RequestUri.Query[1..].Split('&').Select(pair =>
            {
                var parts = pair.Split('=', 2);
                Assert.Equal(twitch ? "login" : "id", parts[0]);
                return Uri.UnescapeDataString(parts[1]);
            }).ToArray();
            requested.AddRange(batch);
            batchLengths.Add(batch.Length);
            var data = batch.Select(identity => new Dictionary<string, string>
            {
                [twitch ? "login" : "user_id"] = identity,
                [twitch ? "profile_image_url" : "profile_picture"] =
                    $"{(twitch ? "https:" : "")}//images.example/{identity}.png"
            });
            return JsonResponse(JsonSerializer.Serialize(new { data }));
        }));

        var result = await ReadAsync(platform, client, [.. identities, " user0 ", "USER0", "a&b", "", " ", null!]);
        Assert.SequenceEqual(expected, requested);
        Assert.SequenceEqual(new[] { batchSize, expected.Length - batchSize }, batchLengths);
        Assert.Equal(expected.Length, result.Count);
        Assert.Equal(twitch, result.ContainsKey("User1"));
        foreach (var identity in expected)
            Assert.Equal($"https://images.example/{identity}.png", result[identity]);
    }

    private static async Task MalformedRowsAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            foreach (var body in new[] { "null", "[]", "17", "{}", "{\"data\":null}" })
            {
                using var client = new HttpClient(new FakeHttpMessageHandler(_ => JsonResponse(body)));
                Assert.Equal(0, (await ReadAsync(platform, client, ["123"])).Count);
            }

            const string mixed = """
                {"data":[null,17,{},
                    {"login":"123","profile_image_url":"https://images.example/123.png",
                     "user_id":123,"profile_picture":"//images.example/123.png"},
                    {"login":"missing","user_id":"missing"},
                    {"profile_image_url":"orphan.png","profile_picture":"orphan.png"}]}
                """;
            using var mixedClient = new HttpClient(new FakeHttpMessageHandler(_ => JsonResponse(mixed)));
            var result = await ReadAsync(platform, mixedClient, ["123"]);
            Assert.Equal(1, result.Count);
            Assert.Equal("https://images.example/123.png", result["123"]);
        }
    }

    private static async Task EmptyIdentitiesAsync()
    {
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("An empty lookup must not send a request.")));
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            Assert.Equal(0, (await ReadAsync(platform, client, ["", " ", null!])).Count);
    }

    private static async Task FailuresAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            using var failedClient = new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }));
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => ReadAsync(platform, failedClient, ["123"]));
            Assert.Contains($"{platform} user profile lookup failed: 503", failure.Message);

            using var oversizedClient = new HttpClient(new FakeHttpMessageHandler(_ =>
            {
                var response = JsonResponse("{}");
                response.Content.Headers.ContentLength = PayloadLimits.HttpJsonBytes + 1;
                return response;
            }));
            await Assert.ThrowsAsync<PayloadTooLargeException>(() => ReadAsync(platform, oversizedClient, ["123"]));

            using var cancellation = new CancellationTokenSource();
            using var canceledClient = new HttpClient(new AsyncHttpMessageHandler((_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(token);
            }));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                ReadAsync(platform, canceledClient, ["123"], cancellation.Token));
        }
    }

    private static Task<IReadOnlyDictionary<string, string>> ReadAsync(
        PlatformKind platform, HttpClient client, IEnumerable<string> identities, CancellationToken token = default) =>
        platform == PlatformKind.Twitch
            ? ProfileImageLookup.GetTwitchAsync(client, "Bearer token", " client ", identities, token)
            : ProfileImageLookup.GetKickAsync(client, "Bearer token", identities, token);

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
