using System.Net.Http;

internal static class ChatCatalogLifecycleTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("chat catalog lifecycle: Twitch badges accept shared credential prefixes", BadgeRequestsNormalizeCredentialsAsync),
        ("chat catalog lifecycle: equivalent credentials reuse loaded badges", EquivalentCredentialsReuseBadgesAsync),
        ("chat catalog lifecycle: eviction finishes before a scope can reload", EvictionFinishesBeforeReloadAsync)
    ];

    private static Task BadgeRequestsNormalizeCredentialsAsync()
    {
        foreach (var token in new[] { " token ", "oAuTh: token ", "oauth token", "Bearer token" })
        {
            var requests = new List<(string? Authorization, string ClientId)>();
            using var http = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                requests.Add((request.Headers.GetValues("Authorization").Single(), request.Headers.GetValues("Client-Id").Single()));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
            }));
            var catalog = new DockedChatBadgeCatalog(http);
            catalog.ConfigureTwitchCredentials(" client ", token);
            catalog.EnsureForMessage(Message());
            Assert.Equal(2, requests.Count);
            foreach (var request in requests)
            {
                Assert.Equal("Bearer token", request.Authorization);
                Assert.Equal("client", request.ClientId);
            }
        }
        return Task.CompletedTask;
    }

    private static Task EquivalentCredentialsReuseBadgesAsync()
    {
        var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
        }));
        var catalog = new DockedChatBadgeCatalog(http);
        catalog.ConfigureTwitchCredentials("client", "token");
        catalog.EnsureForMessage(Message());
        Assert.Equal(2, requests);
        catalog.ConfigureTwitchCredentials(" client ", "Bearer token");
        catalog.EnsureForMessage(Message());
        Assert.Equal(2, requests);
        catalog.ConfigureTwitchCredentials("client", "replacement");
        catalog.EnsureForMessage(Message());
        Assert.Equal(4, requests);
        return Task.CompletedTask;
    }

    private static ChatMessage Message() => new(PlatformKind.Twitch, "streamer", "viewer", "hello",
        DateTimeOffset.UtcNow, RoomId: "1234");

    private static async Task EvictionFinishesBeforeReloadAsync()
    {
        using var releaseCleanup = new ManualResetEventSlim();
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payloads = new ConcurrentDictionary<string, bool>();
        var coordinator = new CatalogLoadCoordinator(maximumEntries: 2, scopeEvicted: scope =>
        {
            if (scope == "first")
            {
                cleanupStarted.TrySetResult();
                if (!releaseCleanup.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cleanup was not released.");
            }
            payloads.TryRemove(scope, out _);
        });
        Task<CatalogLoadResult> Load(string scope)
        {
            payloads[scope] = true;
            return Task.FromResult(CatalogLoadResult.Successful());
        }
        Assert.True(coordinator.Ensure("first", () => Load("first")));
        Assert.True(coordinator.Ensure("second", () => Load("second")));
        var third = Task.Run(() => coordinator.Ensure("third", () => Load("third")));
        try
        {
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(!coordinator.Ensure("FIRST", () => Load("first")),
                "A replacement load must wait until the old scope's payload cleanup finishes.");
        }
        finally
        {
            releaseCleanup.Set();
            await third;
        }
        Assert.True(coordinator.Ensure("first", () => Load("first")));
        Assert.True(payloads.ContainsKey("first"), "Cleanup must not erase a replacement catalog.");
        Assert.Equal(2, coordinator.EntryCount);
    }
}
