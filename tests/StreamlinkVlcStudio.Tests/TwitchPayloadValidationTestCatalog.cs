using System.Net;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Twitch;

internal static class TwitchPayloadValidationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Twitch payload validation: EventSub rejects non-object roots", InvalidRoots),
        ("Twitch payload validation: EventSub rejects malformed metadata", InvalidMetadata),
        ("Twitch payload validation: invalid notifications do not suppress valid retries", InvalidNotifications),
        ("Twitch payload validation: invalid control messages do not suppress valid retries", InvalidControls),
        ("Twitch payload validation: prediction lookup selects the requested user", MatchingPredictionUserAsync),
        ("Twitch payload validation: prediction lookup rejects unusable users", InvalidPredictionUsersAsync),
        ("Twitch payload validation: clip creation selects the requested broadcaster", MatchingClipBroadcasterAsync),
        ("Twitch payload validation: unresolved clip broadcaster prevents creation", InvalidClipBroadcasterAsync),
        ("Twitch payload validation: empty prediction rows do not hide usable rows", EmptyPredictionRowsAsync)
    ];

    private const string User = """{"id":"42","login":"streamer","display_name":"Streamer"}""";
    private const string Event = """{"id":"prediction-1","title":"Will this work?","outcomes":[]}""";

    private static Task InvalidRoots()
    {
        foreach (var json in new[] { "null", "[]", "17", "true", "\"text\"" })
            AssertRejected(new TwitchPredictionEventSubParser(), json);
        return Task.CompletedTask;
    }

    private static Task InvalidMetadata()
    {
        foreach (var metadata in new[] { "{}", "null", "[]",
            "{\"message_id\":17,\"message_type\":\"session_keepalive\"}",
            "{\"message_id\":\"m\",\"message_type\":17}",
            "{\"message_id\":\" \",\"message_type\":\"session_keepalive\"}" })
            AssertRejected(new TwitchPredictionEventSubParser(), $$$"""{"metadata":{{{metadata}}},"payload":{}}""");
        return Task.CompletedTask;
    }

    private static Task InvalidNotifications()
    {
        foreach (var payload in new[] { "null", "[]", "{}",
            "{\"subscription\":{\"type\":\"channel.prediction.begin\"},\"event\":null}",
            "{\"subscription\":{\"type\":\"channel.prediction.begin\"},\"event\":{}}" })
        {
            var parser = new TwitchPredictionEventSubParser();
            AssertRejected(parser, Envelope("notification", payload));
            Assert.True(parser.TryParse(Envelope("notification",
                $$"""{"subscription":{"type":"channel.prediction.begin"},"event":{{Event}}}"""), out var message));
            Assert.Equal(false, message.IsDuplicate);
            Assert.Equal("prediction-1", message.Prediction!.Id);
        }
        return Task.CompletedTask;
    }

    private static Task InvalidControls()
    {
        foreach (var (type, validPayload) in new[]
        {
            ("session_welcome", "{\"session\":{\"id\":\"session-1\"}}"),
            ("session_reconnect", "{\"session\":{\"reconnect_url\":\"wss://eventsub.wss.twitch.tv/ws\"}}"),
            ("revocation", "{\"subscription\":{\"status\":\"authorization_revoked\"}}")
        })
        {
            var parser = new TwitchPredictionEventSubParser();
            AssertRejected(parser, Envelope(type, "{}"));
            Assert.True(parser.TryParse(Envelope(type, validPayload), out var message));
            Assert.Equal(false, message.IsDuplicate);
        }
        return Task.CompletedTask;
    }

    private static async Task MatchingPredictionUserAsync()
    {
        using var client = Client("""{"data":[{"id":"wrong","login":"someone_else"},""" + User + "]}");
        var user = await new TwitchPredictionApiClient(client).ResolveUserByLoginAsync(" Streamer ", "token", "client");
        Assert.Equal("42", user!.Id);
        Assert.Equal("streamer", user.Login);
    }

    private static async Task InvalidPredictionUsersAsync()
    {
        foreach (var row in new[] { "null", "{}", "{\"id\":\"wrong\"}",
            "{\"id\":true,\"login\":\"streamer\"}", "{\"id\":\" \",\"login\":\"streamer\"}",
            "{\"id\":\"wrong\",\"login\":\"someone_else\"}" })
        {
            using var client = Client($$"""{"data":[{{row}}]}""");
            var user = await new TwitchPredictionApiClient(client).ResolveUserByLoginAsync("streamer", "token", "client");
            Assert.Equal<TwitchUserInfo?>(null, user);
        }
    }

    private static async Task MatchingClipBroadcasterAsync()
    {
        var posted = new List<string>();
        using var client = ClipClient("""{"data":[{"id":"wrong","login":"other"},""" + User + "]}", posted);
        var result = await CreateClipAsync(client);
        Assert.Equal("clip-id", result.ClipId);
        Assert.SequenceEqual(["?broadcaster_id=42&duration=30"], posted);
    }

    private static async Task InvalidClipBroadcasterAsync()
    {
        var posted = new List<string>();
        using var client = ClipClient("""{"data":[{"id":"wrong","login":"other"}]}""", posted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClipAsync(client));
        Assert.Equal(0, posted.Count);
    }

    private static async Task EmptyPredictionRowsAsync()
    {
        using var client = Client("""{"data":[{},""" + Event + "]}");
        var prediction = await new TwitchPredictionApiClient(client).GetLatestPredictionAsync("42", "token", "client");
        Assert.Equal("prediction-1", prediction!.Id);
    }

    private static Task<TwitchClipResult> CreateClipAsync(HttpClient client) =>
        new TwitchClipService(client, TimeSpan.Zero, readinessPollAttempts: 1).CreateLiveClipAsync(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/streamer"),
            new ChatSettings { TwitchOAuthToken = "token" });

    private static HttpClient ClipClient(string users, List<string> posted) => new(new FakeHttpMessageHandler(request =>
    {
        if (request.Method == HttpMethod.Post) posted.Add(request.RequestUri!.Query);
        return JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/validate" => """{"login":"streamer","user_id":"42","client_id":"client","scopes":["clips:edit"]}""",
            "/helix/users" => users,
            "/helix/clips" => """{"data":[{"id":"clip-id","url":"https://clips.twitch.tv/clip-id"}]}""",
            _ => throw new InvalidOperationException("Unexpected API request.")
        });
    }));

    private static HttpClient Client(string body) => new(new FakeHttpMessageHandler(_ => JsonResponse(body)));
    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static string Envelope(string type, string payload) =>
        $$"""{"metadata":{"message_id":"message-1","message_type":"{{type}}"},"payload":{{payload}}}""";

    private static void AssertRejected(TwitchPredictionEventSubParser parser, string json)
    {
        Assert.Equal(false, parser.TryParse(json, out var message));
        Assert.Equal(TwitchEventSubMessage.Empty, message);
    }
}
