using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

internal static partial class LocalWebTestCatalog
{
    private static async Task ChatPresentationAsync()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host == "kick.com"
                ? "{\"user_id\":456,\"subscriber_badges\":[{\"months\":6,\"badge_image\":{\"src\":\"https://files.kick.com/subscriber.png\"}}]}"
                : "{\"data\":[{\"set_id\":\"subscriber\",\"versions\":[{\"id\":\"6\",\"title\":\"Six months\",\"image_url_4x\":\"https://static-cdn.jtvnw.net/badges/v1/fixture/3\"}]}]}")
        }));
        var badges = new DockedChatBadgeCatalog(http);
        var emotes = new DockedChatEmoteCatalog();
        foreach (var scope in new[] { "twitch:global", "twitch:123:fixture", "kick:global", "kick:456:fixture" }) PrimeWebCatalog(emotes, scope);
        using var presentation = new LocalWebChatPresentation(badges, emotes);
        var settings = new ChatSettings { TwitchClientId = "client", TwitchOAuthToken = "secret-token" };
        Assert.True(emotes.AddEmote(PlatformKind.Twitch, "fixture", "Dance", "https://cdn.7tv.app/emote/fixture/2x.gif", 28, 28));
        var twitch = TwitchIrcParser.TryParsePrivMsg("@badges=moderator/1,subscriber/6;color=#123456;emotes=25:8-12;room-id=123;id=twitch :viewer!viewer@viewer PRIVMSG #fixture :😀 Dance Kappa", "fixture")!;
        ChatMessage resolved = presentation.Present(twitch, settings);
        await WaitWebChatAsync(() => (resolved = presentation.Present(twitch, settings)).Badges?.All(b => b.ImageUrl is not null) == true);
        Assert.Equal(2, resolved.Emotes!.Count);
        Assert.Equal("Dance", resolved.Message[resolved.Emotes[0].StartIndex..resolved.Emotes[0].EndIndex]);
        Assert.Equal("Kappa", resolved.Message[resolved.Emotes[1].StartIndex..resolved.Emotes[1].EndIndex]);
        Assert.True(resolved.Emotes[1].ImageUrl!.Contains("/default/", StringComparison.Ordinal));
        Assert.True(resolved.Badges![1].ImageUrl!.StartsWith("https://static-cdn.jtvnw.net/badges/", StringComparison.Ordinal));
        var localBadge = resolved.Badges[0].ImageUrl!;
        Assert.True(localBadge.StartsWith("/chat-assets/", StringComparison.Ordinal));
        var context = new DefaultHttpContext();
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.RequestServices = provider;
        context.Response.Body = new MemoryStream();
        await presentation.ServeAssetAsync(context, localBadge[13..]);
        Assert.Equal(200, context.Response.StatusCode); Assert.True(context.Response.Body.Length > 0);
        foreach (var invalid in new[] { "../badges.json", "C:/Windows/win.ini", "unknown" })
        {
            var rejected = new DefaultHttpContext(); await presentation.ServeAssetAsync(rejected, invalid); Assert.Equal(404, rejected.Response.StatusCode);
        }
        var kick = KickPusherParser.TryParseMessageData(JsonSerializer.SerializeToElement(new
        {
            id = "kick",
            content = "😀 [emote:37226:KEKW]",
            sender = new { username = "viewer", identity = new { badges = new[] { new { type = "moderator", count = 1 }, new { type = "subscriber", count = 6 } } } }
        }), "fixture")!;
        await WaitWebChatAsync(() => (resolved = presentation.Present(kick, settings)).Badges?.All(b => b.ImageUrl is not null) == true);
        Assert.Equal(kick.Message.Length, resolved.Emotes!.Single().EndIndex);
        Assert.Equal("https://files.kick.com/emotes/37226/fullsize", resolved.Emotes.Single().ImageUrl);
        Assert.Equal("https://files.kick.com/subscriber.png", resolved.Badges![1].ImageUrl);
        Assert.Equal(456L, badges.GetKickBroadcasterId("fixture"));
        Assert.Equal(false, JsonSerializer.Serialize(resolved).Contains("secret-token", StringComparison.Ordinal));
        Assert.Equal(false, JsonSerializer.Serialize(resolved).Contains("file:", StringComparison.Ordinal));
        var malformed = twitch with { Emotes = [new(-1, 4, "bad", "javascript:alert(1)"), new(0, 10000, "bad")] };
        var recovered = presentation.Present(malformed, settings).Emotes!;
        Assert.SequenceEqual(new[] { "Dance", "Kappa" }, recovered.Select(e => e.Code));
        Assert.True(recovered.All(e => e.StartIndex >= 0 && e.EndIndex <= malformed.Message.Length));
    }

    private static void PrimeWebCatalog(object catalog, string scope)
    {
        var coordinator = (CatalogLoadCoordinator)catalog.GetType().GetField("loadCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        coordinator.Ensure(scope, () => Task.FromResult(CatalogLoadResult.Successful()), preserveFromEviction: true);
    }

    private static async Task KickWebEmotesAsync()
    {
        var requests = new ConcurrentBag<string>();
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsolutePath == "/v3/users/kick/456"
                    ? "{\"emote_set\":{\"emotes\":[{\"name\":\"KickDance\",\"data\":{\"host\":{\"url\":\"//cdn.7tv.app/emote/fixture\",\"files\":[{\"name\":\"2x.webp\"},{\"name\":\"2x.gif\"}]}}}]}}"
                    : "[]")
            };
        }));
        var catalog = new DockedChatEmoteCatalog(http);
        catalog.EnsureKickLoaded("fixture", 456);
        var message = new ChatMessage(PlatformKind.Kick, "fixture", "viewer", "KickDance", DateTimeOffset.UnixEpoch, RoomId: "999");
        await WaitWebChatAsync(() => catalog.TryGet(message, "KickDance", out _));
        Assert.True(catalog.TryGet(message, "KickDance", out var image));
        Assert.Equal("https://cdn.7tv.app/emote/fixture/2x.gif", image.ImageUrl);
        Assert.True(requests.Contains("/v3/users/kick/456"));
        Assert.Equal(false, requests.Any(path => path.Contains("999", StringComparison.Ordinal)));
        Assert.Equal(false, catalog.TryGet(message with { Platform = PlatformKind.Twitch }, "KickDance", out _));
    }

    private static async Task WaitWebChatAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Chat images did not resolve.");
            await Task.Delay(20);
        }
    }
}
