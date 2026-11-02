using System.Net;
using System.Text.Json;
using StreamlinkVlcStudio.App.Wpf.ViewModels;
using StreamlinkVlcStudio.Infrastructure.Chat;

internal static class KickChatConnectionTestCatalog
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("Kick chat connection: requests fresh channel-specific Pusher settings on reconnect", NegotiationAsync),
        ("Kick chat connection: rejects failed unsupported and malformed discovery", InvalidDiscoveryAsync),
        ("Kick chat connection: discovery body honors timeout and caller cancellation", DiscoveryBodyDeadlineAsync),
        ("Kick chat connection: preserves channel identity independently of the chatroom", OverlayIdentityAsync),
        ("Kick chat connection: exposes Pusher rejection codes and messages", RejectionsAsync)
    ];

    private static async Task NegotiationAsync()
    {
        var requests = 0;
        var clientId = Guid.NewGuid().ToString();
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://web.kick.com/api/v1/realtime/channels/42/chat/connection", request.RequestUri!.AbsoluteUri);
            Assert.Equal("https://kick.com", request.Headers.GetValues("Origin").Single());
            Assert.Equal("web", request.Headers.GetValues("x-app-platform").Single());
            Assert.Equal(false, request.Headers.Contains("Authorization"));
            Assert.Equal(false, request.Headers.Contains("Cookie"));
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = document.RootElement;
            Assert.Equal(clientId, root.GetProperty("client").GetProperty("id").GetString());
            Assert.Equal("web", root.GetProperty("client").GetProperty("type").GetString());
            var providers = root.GetProperty("capabilities").GetProperty("accepted_providers");
            Assert.Equal(1, providers.GetArrayLength());
            Assert.Equal("pusher", providers[0].GetProperty("provider").GetString());
            return Response(++requests == 1 ? "currentKey1" : "currentKey2", requests == 1 ? "eu" : "us2");
        }));
        var first = await KickPusherConnection.ResolveAsync(http, "42", clientId, default);
        var second = await KickPusherConnection.ResolveAsync(http, "42", clientId, default);
        Assert.Equal("ws-eu.pusher.com", first.WebSocketUri.Host);
        Assert.Equal("/app/currentKey1", first.WebSocketUri.AbsolutePath);
        Assert.Equal("ws-us2.pusher.com", second.WebSocketUri.Host);
        Assert.Equal("/app/currentKey2", second.WebSocketUri.AbsolutePath);
        Assert.Equal(2, requests);
    }

    private static async Task InvalidDiscoveryAsync()
    {
        using var unavailable = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => KickPusherConnection.ResolveAsync(unavailable, "42", "reader", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => KickPusherConnection.ResolveAsync(unavailable, "../42", "reader", default));
        var invalid = new[]
        {
            "{}", "[]", "null", "{\"data\":{\"mode\":\"polling\",\"connections\":[]}}",
            "{\"data\":{\"mode\":\"websocket\",\"connections\":[{\"provider\":\"centrifugo\"}]}}",
            Discovery("key", "us2.attacker"), Discovery("bad/key", "us2"), Discovery("key", "-us2"),
            Discovery(new string('x', 129), "us2"), Discovery("key", new string('x', 33))
        };
        foreach (var body in invalid)
        {
            using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            }));
            await Assert.ThrowsAsync<InvalidDataException>(() => KickPusherConnection.ResolveAsync(http, "42", "reader", default));
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => KickPusherConnection.ResolveAsync(unavailable, "42", "reader", cancellation.Token));
    }

    private static async Task DiscoveryBodyDeadlineAsync()
    {
        foreach (var callerCancels in new[] { false, true })
        {
            using var stream = new CodeReviewTestCatalog.StalledReadStream();
            using var http = new HttpClient(new FakeHttpMessageHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            }))
            { Timeout = callerCancels ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(150) };
            using var cancellation = new CancellationTokenSource();
            var pending = KickPusherConnection.ResolveAsync(http, "42", "reader", cancellation.Token);
            try
            {
                await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (callerCancels) cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(stream.SawCancellation);
                Assert.Equal(callerCancels, cancellation.IsCancellationRequested);
            }
            finally
            {
                cancellation.Cancel();
                try { await pending; } catch (OperationCanceledException) { }
            }
        }
    }

    private static Task OverlayIdentityAsync()
    {
        var info = NativeChatOverlayController.ReadKickOverlayChannelInfo("""{"id":42,"chatroom":{"id":9001},"user_id":7}""")!;
        Assert.Equal("42", info.ChannelId);
        Assert.Equal("9001", info.ChatroomId);
        Assert.Equal<long?>(7, info.BroadcasterUserId);
        return Task.CompletedTask;
    }

    private static Task RejectionsAsync()
    {
        foreach (var data in new[] { "{\"code\":4003,\"message\":\"Application disabled\"}", "\"{\\\"code\\\":4003,\\\"message\\\":\\\"Application disabled\\\"}\"" })
        {
            var failure = false;
            try { KickChatClient.ThrowIfPusherError("{\"event\":\"pusher:error\",\"data\":" + data + "}"); }
            catch (InvalidOperationException error)
            {
                failure = true;
                Assert.Contains("4003", error.Message);
                Assert.Contains("Application disabled", error.Message);
            }
            Assert.True(failure);
        }
        KickChatClient.ThrowIfPusherError("""{"event":"App\\Events\\ChatMessageEvent","data":"pusher:error"}""");
        KickChatClient.ThrowIfPusherError("not JSON");
        return Task.CompletedTask;
    }

    private static HttpResponseMessage Response(string key, string cluster) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Discovery(key, cluster))
    };

    private static string Discovery(string key, string cluster) => JsonSerializer.Serialize(new
    {
        data = new
        {
            mode = "websocket",
            connections = new[] { new { provider = "pusher", credentials = new { app_key = key, cluster } } }
        }
    });
}
