using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task VerifyTwitchBadgeRecoveryAsync(LocalWebDependencies dependencies, List<FakeChatClientFactory> chats)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var available = false; var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests++;
            var success = available && request.RequestUri!.Host == "gql.twitch.tv";
            return new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(success ? TwitchBadgeTestCatalog.Fixture("alinity-public-2026-10-22.json") : "{}")
            };
        }));
        var badges = new DockedChatBadgeCatalog(http, clock);
        var settings = await dependencies.GetSettingsAsync(CancellationToken.None);
        badges.ConfigureTwitchCredentials(settings.Chat.TwitchClientId, settings.Chat.TwitchOAuthToken);
        foreach (var scope in new[] { "badges:twitch:global", "badges:twitch:bundled" }) PrimeWebCatalog(badges, scope);
        var emotes = new DockedChatEmoteCatalog(http);
        foreach (var scope in new[] { "twitch:global", "twitch:38718052:alinity" }) PrimeWebCatalog(emotes, scope);
        using var presentation = new LocalWebChatPresentation(badges, emotes);
        await using var workspace = new LocalWebWorkspace(dependencies with { ChatPresentation = presentation });
        var opened = await Call(workspace, "open", new { input = "https://www.twitch.tv/alinity" });
        var player = opened.GetProperty("id").GetString()!;
        var message = TwitchIrcParser.TryParsePrivMsg("@badges=subscriber/0;room-id=38718052;id=quiet-subscriber :viewer!viewer@viewer PRIVMSG #alinity :hello", "alinity")!;
        chats.Single().Client.Receive(message);
        var chat = await Call(workspace, "chat", new { player, after = 0 });
        Assert.Equal(JsonValueKind.Null, chat.GetProperty("messages")[0].GetProperty("message").GetProperty("badges")[0].GetProperty("imageUrl").ValueKind);
        var version = presentation.Version;
        available = true;
        var beforeRetry = requests;
        var unchanged = await Call(workspace, "chat/presentation", new { player, version });
        Assert.Equal(0, unchanged.GetProperty("messages").GetArrayLength());
        Assert.Equal(beforeRetry, requests); // The coordinator still honors the failure backoff.
        clock.Advance(TimeSpan.FromSeconds(6));
        await Call(workspace, "chat/presentation", new { player, version });
        await TestWait.UntilAsync(() => presentation.Version > version, TimeSpan.FromSeconds(3));
        var recovered = await Call(workspace, "chat/presentation", new { player, version });
        var resolved = recovered.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("quiet-subscriber", resolved.GetProperty("messageId").GetString());
        Assert.Equal("https://static-cdn.jtvnw.net/badges/v1/aa8e478b-477c-4389-b51a-d7ba218be938/2",
            resolved.GetProperty("badges")[0].GetProperty("imageUrl").GetString());
        Assert.True(requests > beforeRetry);
        Assert.Equal(0, (await Call(workspace, "chat", new { player, after = 1 })).GetProperty("messages").GetArrayLength());
    }
}
