using System.Net.Http.Headers;
using System.Net.WebSockets;
using StreamlinkVlcStudio.Infrastructure.Http;

internal static partial class CodeCleanupTestCatalog
{
    private static async Task TwitchRemoteDisconnectRecoveryAsync(bool failedCancellation)
    {
        var logger = failedCancellation ? new MemoryLogger() : MemoryLogger.WithWriteFailure();
        await using var supervisor = CreateChatRecoverySupervisor(logger, "TwitchChat", out var reconnectStarted);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
        {
            if (failedCancellation) throw new IOException("The read cancellation callback failed.");
        });
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        using var reader = new BoundedUtf8LineReader(input);
        using var writer = new StreamWriter(output);
        await using var client = new TwitchChatClient(new ChatSettings(), logger);
        SetPrivateField(client, "reader", reader);
        SetPrivateField(client, "writer", writer);
        SetPrivateField(client, "readCancellation", cancellation);
        SetPrivateField(client, "connectionSupervisor", supervisor);
        SetPrivateField(client, "connectedChannel", "streamer");
        SetPrivateField(client, "canSendMessages", true);
        typeof(TwitchChatClient).GetMethod("SetPredictionAccess", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [new TwitchPredictionAccessState(true, true, "Connected.")]);
        if (!failedCancellation)
        {
            var eventSub = new TwitchPredictionEventSubClient(
                (TwitchPredictionApiClient)GetPrivateField(client, "predictionApiClient")!, logger,
                "token", "client", "channel", _ => { }, _ => { },
                _ => Task.FromException(new IOException("The prediction worker failed.")));
            eventSub.Start();
            SetPrivateField(client, "predictionEventSubClient", eventSub);
        }

        var readLoop = (Task)typeof(TwitchChatClient)
            .GetMethod("ReadLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, ["streamer", reader, writer, supervisor, cancellation.Token])!;
        await readLoop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(false, GetPrivateField(client, "canSendMessages"));
        Assert.Equal(false, client.PredictionAccess.CanManage);
        AssertChatFieldsCleared(client, "predictionEventSubClient");
        await reconnectStarted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task KickRemoteDisconnectRecoveryAsync(bool failedDiagnostics)
    {
        var logger = failedDiagnostics ? MemoryLogger.WithWriteFailure() : new MemoryLogger();
        await using var supervisor = CreateChatRecoverySupervisor(logger, "KickChat", out var reconnectStarted);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
            throw new IOException("The Kick read cancellation callback failed."));
        using var socket = new ClientWebSocket();
        socket.Abort();
        await using var client = new KickChatClient(new ChatSettings(), logger);
        SetPrivateField(client, "webSocket", socket);
        SetPrivateField(client, "readCancellation", cancellation);
        SetPrivateField(client, "connectionSupervisor", supervisor);
        SetPrivateField(client, "canSendMessages", true);

        var readLoop = (Task)typeof(KickChatClient)
            .GetMethod("ReadLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, ["streamer", socket, supervisor, cancellation.Token])!;
        await readLoop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(false, GetPrivateField(client, "canSendMessages"));
        await reconnectStarted.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static LiveChatConnectionSupervisor CreateChatRecoverySupervisor(
        IAppLogger logger, string source, out Task reconnectStarted)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reconnectStarted = signal.Task;
        var supervisor = new LiveChatConnectionSupervisor(logger, source, _ => { },
            async (_, token) =>
            {
                signal.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        supervisor.Start(_ => Task.CompletedTask);
        return supervisor;
    }

    private static async Task ChatQueuedDisconnectDuringDisposalAsync(bool twitch)
    {
        IChatClient client = twitch
            ? new TwitchChatClient(new ChatSettings(), new MemoryLogger())
            : new KickChatClient(new ChatSettings(), new MemoryLogger());
        await using (client)
        {
            var lifecycle = (SemaphoreSlim)GetPrivateField(client, "lifecycleGate")!;
            await lifecycle.WaitAsync();
            Task disconnect;
            Task disposal;
            try
            {
                disconnect = client.DisconnectAsync();
                disposal = client.DisposeAsync().AsTask();
                Assert.Equal(false, disconnect.IsCompleted);
                Assert.Equal(false, disposal.IsCompleted);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DisconnectAsync());
            }
            finally { lifecycle.Release(); }

            await Task.WhenAll(disconnect, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task TwitchRateLimitDiagnosticsAsync()
    {
        using var firstBody = new MemoryStream("rate limited"u8.ToArray());
        using var firstResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StreamContent(firstBody)
        };
        firstResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => ++requests == 1
            ? firstResponse
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await new TwitchRateLimitCoordinator().SendAsync(http,
            "https://api.twitch.tv/helix/games/top", "token", "client", MemoryLogger.WithWriteFailure(), cancellation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, requests);
        Assert.Equal(false, firstBody.CanRead);
    }

    private static async Task KickWebsiteFallbackDiagnosticsAsync(string failure)
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => failure switch
        {
            "status" => new HttpResponseMessage(HttpStatusCode.Forbidden),
            "timeout" => throw new OperationCanceledException("The request deadline elapsed."),
            "read" => throw new HttpRequestException("The connection failed."),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") }
        }));
        var fallbacks = 0;
        const string fallback = "{\"fixture\":true}";
        var reader = new KickWebsiteJsonReader(http, MemoryLogger.WithWriteFailure(), "Test", TimeSpan.FromSeconds(5),
            (_, _, _) =>
            {
                fallbacks++;
                return Task.FromResult<string?>(fallback);
            });

        Assert.Equal(fallback, await reader.ReadAsync("https://kick.com/api/v2/channels/streamer",
            "https://kick.com/streamer", default));
        Assert.Equal(1, fallbacks);
    }

    private static async Task KickFallbackPayloadDiagnosticsAsync()
    {
        using var http = new HttpClient();
        var reader = new KickWebsiteJsonReader(http, MemoryLogger.WithWriteFailure(), "Test", TimeSpan.FromSeconds(5),
            (_, _, _) => Task.FromResult<string?>("not JSON"));

        Assert.Equal<string?>(null, await reader.ReadFallbackAsync("https://kick.com/api/v2/channels/streamer",
            "https://kick.com/streamer", default));
    }
}
