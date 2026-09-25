using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Replay;

internal static class ChatSecurityTestCatalog
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Twitch client ID validation survives waiter cancellation", TwitchClientIdWaiterCancellationAsync),
        ("Kick token provider clears orphaned work and does not cache near-expiry tokens", KickTokenProviderExpiryAndCleanupAsync),
        ("loopback OAuth receiver rejects unrelated requests and continues after malformed callbacks", LoopbackOAuthReceiverValidationAsync),
        ("Twitch prediction EventSub start and disposal are synchronized", TwitchPredictionEventSubLifecycleAsync),
        ("Kick chat recognizes exact pings and bounds backfill disconnect cleanup", KickChatPingAndDisconnectBoundsAsync)
    ];

    private static async Task TwitchClientIdWaiterCancellationAsync()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            requestStarted.TrySetResult();
            await releaseResponse.Task.ConfigureAwait(false);
            return JsonResponse("""
                {
                  "login":"viewer",
                  "user_id":"42",
                  "client_id":"validated-client",
                  "expires_in":3600,
                  "scopes":["chat:read"]
                }
                """);
        }));
        var token = $"waiter-cancellation-{Guid.NewGuid():N}";
        var logger = new MemoryLogger();
        using var canceledWaiter = new CancellationTokenSource();
        var canceled = TwitchClientIdCache.GetOrResolveAsync(
            httpClient,
            token,
            logger,
            "Test",
            "validation failed",
            canceledWaiter.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var survivor = TwitchClientIdCache.GetOrResolveAsync(
            httpClient,
            token,
            logger,
            "Test",
            "validation failed",
            CancellationToken.None);

        canceledWaiter.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => _ = await canceled);
        Assert.Equal(false, survivor.IsCompleted);
        releaseResponse.TrySetResult();
        Assert.Equal("validated-client", await survivor);
        Assert.Equal("validated-client", await TwitchClientIdCache.GetOrResolveAsync(
            httpClient,
            token,
            logger,
            "Test",
            "validation failed",
            CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    private static async Task KickTokenProviderExpiryAndCleanupAsync()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutionCount = 0;
        var provider = new KickTokenProvider(async (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref resolutionCount);
            if (attempt == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }

            return $"token-{attempt}";
        });
        var settings = new ChatSettings
        {
            KickClientId = "near-expiry-client",
            KickClientSecret = "near-expiry-secret",
            KickTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        var logger = new MemoryLogger();
        using var canceledWaiter = new CancellationTokenSource();
        var canceled = provider.ResolveAsync(settings, logger, canceledWaiter.Token);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        canceledWaiter.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => _ = await canceled);

        releaseFirst.TrySetResult();
        await TestWait.UntilAsync(() => provider.InFlightCountForTest == 0, TimeSpan.FromSeconds(1));
        Assert.Equal("token-2", await provider.ResolveAsync(settings, logger));
        Assert.Equal("token-3", await provider.ResolveAsync(settings, logger));
        Assert.Equal(3, Volatile.Read(ref resolutionCount));
        Assert.Equal(0, provider.InFlightCountForTest);
    }

    private static async Task LoopbackOAuthReceiverValidationAsync()
    {
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var receiver = LoopbackOAuthReceiver.WaitForResultAsync(
            listener,
            "TestProvider",
            "/callback",
            "expected-state",
            TimeSpan.FromSeconds(5),
            query => query["code"],
            CancellationToken.None);
        using var client = new HttpClient();

        using (var wrongPath = await client.GetAsync(
                   $"http://127.0.0.1:{port}/wrong?state=expected-state&code=spoof"))
        {
            Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
        }

        using (var wrongMethod = await client.PostAsync(
                   $"http://127.0.0.1:{port}/callback?state=expected-state&code=spoof",
                   content: null))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        }

        using (var unauthenticatedError = await client.GetAsync(
                   $"http://127.0.0.1:{port}/callback?state=wrong&error=access_denied"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unauthenticatedError.StatusCode);
        }

        using (var malformed = await client.GetAsync(
                   $"http://127.0.0.1:{port}/callback?state=expected-state&state=expected-state&code=spoof"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        Assert.Equal(false, receiver.IsCompleted);
        using (var accepted = await client.GetAsync(
                   $"http://127.0.0.1:{port}/callback?state=expected-state&code=approved"))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        Assert.Equal("approved", await receiver);
        Assert.Equal(false, OAuthTokenHelpers.TryParseQueryString("state=ok&code=%ZZ", out _));
    }

    private static async Task TwitchPredictionEventSubLifecycleAsync()
    {
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        var stopCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => JsonResponse("{}")));
        var client = new TwitchPredictionEventSubClient(
            new TwitchPredictionApiClient(httpClient),
            new MemoryLogger(),
            "token",
            "client",
            "broadcaster",
            _ => { },
            _ => { },
            async cancellationToken =>
            {
                Interlocked.Increment(ref runCount);
                runStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Increment(ref stopCount);
                }
            });

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(client.Start)));
        await runStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref runCount));
        var disposals = Enumerable.Range(0, 32)
            .Select(_ => client.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals);
        Assert.Equal(1, Volatile.Read(ref stopCount));
        Assert.Throws<ObjectDisposedException>(client.Start);
    }

    private static async Task KickChatPingAndDisconnectBoundsAsync()
    {
        Assert.True(KickChatClient.IsPusherPing("""{"event":"pusher:ping","data":{}}"""));
        Assert.Equal(false, KickChatClient.IsPusherPing("""{"event":"PUSHER:PING","data":{}}"""));
        Assert.Equal(false, KickChatClient.IsPusherPing("""{"event":"message","data":"pusher:ping"}"""));
        Assert.Equal(false, KickChatClient.IsPusherPing("""{"event":"pusher:ping-extra"}"""));

        await using var client = new KickChatClient(new ChatSettings(), new MemoryLogger());
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(KickChatClient).GetField("recentChatBackfillTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, pending.Task);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(4));
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1));
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
        }
        finally
        {
            pending.TrySetResult();
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

}
