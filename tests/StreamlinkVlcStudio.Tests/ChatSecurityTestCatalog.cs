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
        ("Kick webhook retains keys throttles refresh and releases failed persistence reservations", KickWebhookReliabilityAsync),
        ("loopback OAuth receiver rejects unrelated requests and continues after malformed callbacks", LoopbackOAuthReceiverValidationAsync),
        ("Kick event subscription ensure is keyed single-flight", KickEventSubscriptionSingleFlightAsync),
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

    private static async Task KickWebhookReliabilityAsync()
    {
        await KickWebhookPublicKeyReliabilityAsync();
        await KickWebhookReservationReleaseAsync();
    }

    private static async Task KickWebhookPublicKeyReliabilityAsync()
    {
        using var oldKey = RSA.Create(2048);
        using var currentKey = RSA.Create(2048);
        using var invalidKey = RSA.Create(2048);
        var thirdRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseThirdRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (_, _) =>
        {
            var requestNumber = Interlocked.Increment(ref requestCount);
            if (requestNumber == 3)
            {
                thirdRequestStarted.TrySetResult();
                await releaseThirdRequest.Task.ConfigureAwait(false);
            }

            if (requestNumber == 4)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }

            var key = requestNumber == 1 ? oldKey : currentKey;
            return JsonResponse(JsonSerializer.Serialize(new
            {
                data = new { public_key = key.ExportSubjectPublicKeyInfoPem() }
            }));
        }));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        var directory = CreateTemporaryPath("svs-webhook-key");
        Directory.CreateDirectory(directory);
        try
        {
            await using var server = new KickWebhookChatServer(
                new KickOfficialChatReplayStore(directory),
                new MemoryLogger(),
                port: 0,
                httpClient,
                clock);
            var rotated = CreateSignedWebhookRequest(currentKey, "rotated", clock.GetUtcNow(), "{}");
            Assert.Equal(
                KickWebhookChatServer.WebhookAuthenticationResult.Valid,
                await server.AuthenticateRequestAsync(rotated, CancellationToken.None));
            Assert.Equal(2, Volatile.Read(ref requestCount));

            clock.Advance(TimeSpan.FromSeconds(31));
            var invalidRequests = Enumerable.Range(0, 24)
                .Select(index => server.AuthenticateRequestAsync(
                    CreateSignedWebhookRequest(invalidKey, $"invalid-{index}", clock.GetUtcNow(), "{}"),
                    CancellationToken.None))
                .ToArray();
            await thirdRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(3, Volatile.Read(ref requestCount));
            releaseThirdRequest.TrySetResult();
            var invalidResults = await Task.WhenAll(invalidRequests);
            Assert.True(invalidResults.All(result =>
                result == KickWebhookChatServer.WebhookAuthenticationResult.Invalid));
            Assert.Equal(3, Volatile.Read(ref requestCount));

            clock.Advance(TimeSpan.FromHours(25));
            var lastKnownGood = CreateSignedWebhookRequest(
                currentKey,
                "last-known-good",
                clock.GetUtcNow(),
                "{}");
            Assert.Equal(
                KickWebhookChatServer.WebhookAuthenticationResult.Valid,
                await server.AuthenticateRequestAsync(lastKnownGood, CancellationToken.None));
            Assert.Equal(4, Volatile.Read(ref requestCount));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task KickWebhookReservationReleaseAsync()
    {
        using var key = RSA.Create(2048);
        using var keyClient = new HttpClient(new FakeHttpMessageHandler(_ => JsonResponse(
            JsonSerializer.Serialize(new
            {
                data = new { public_key = key.ExportSubjectPublicKeyInfoPem() }
            }))));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 16, 13, 0, 0, TimeSpan.Zero));
        var blockedRoot = CreateTemporaryPath("svs-webhook-reservation");
        await File.WriteAllTextAsync(blockedRoot, "blocks directory creation");
        try
        {
            await using var server = new KickWebhookChatServer(
                new KickOfficialChatReplayStore(blockedRoot),
                new MemoryLogger(),
                port: 0,
                keyClient,
                clock);
            Assert.True(server.Start());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var body = """
                {
                  "message_id":"persisted-message",
                  "broadcaster":{"channel_slug":"streamer"},
                  "sender":{"username":"viewer"},
                  "content":"retry me",
                  "created_at":"2026-08-16T13:00:00Z"
                }
                """;
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                using var failed = CreateSignedWebhookHttpRequest(
                    server.LocalWebhookUrl,
                    key,
                    "reservation-id",
                    clock.GetUtcNow(),
                    body);
                using var _ = await client.SendAsync(failed);
            });

            File.Delete(blockedRoot);
            Directory.CreateDirectory(blockedRoot);
            using (var retry = CreateSignedWebhookHttpRequest(
                       server.LocalWebhookUrl,
                       key,
                       "reservation-id",
                       clock.GetUtcNow(),
                       body))
            using (var retryResponse = await client.SendAsync(retry))
            {
                Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
            }

            using var replay = CreateSignedWebhookHttpRequest(
                server.LocalWebhookUrl,
                key,
                "reservation-id",
                clock.GetUtcNow(),
                body);
            using var replayResponse = await client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.Conflict, replayResponse.StatusCode);
        }
        finally
        {
            if (File.Exists(blockedRoot))
            {
                File.Delete(blockedRoot);
            }
            else if (Directory.Exists(blockedRoot))
            {
                Directory.Delete(blockedRoot, recursive: true);
            }
        }
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

    private static async Task KickEventSubscriptionSingleFlightAsync()
    {
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupCount = 0;
        var createCount = 0;
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref lookupCount);
                lookupStarted.TrySetResult();
                await releaseLookup.Task.ConfigureAwait(false);
                return JsonResponse("""{"data":[]}""");
            }

            Interlocked.Increment(ref createCount);
            return JsonResponse("""
                {"data":[{"name":"chat.message.sent","version":1,"subscription_id":"subscription-1"}]}
                """);
        }));
        var tokenCount = 0;
        var broadcasterCount = 0;
        var persistCount = 0;
        await using var service = new KickEventSubscriptionService(
            new MemoryLogger(),
            httpClient,
            (_, _, _) =>
            {
                Interlocked.Increment(ref tokenCount);
                return Task.FromResult<string?>("app-token");
            },
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref broadcasterCount);
                return Task.FromResult<long?>(42);
            },
            (_, _) =>
            {
                Interlocked.Increment(ref persistCount);
                return Task.CompletedTask;
            });
        var target = new StreamTarget(
            PlatformKind.Kick,
            "streamer",
            "https://kick.com/streamer");
        var settings = new ChatSettings
        {
            KickClientId = "event-client",
            KickClientSecret = "event-secret"
        };
        using var canceledWaiter = new CancellationTokenSource();
        var canceled = service.EnsureChatMessageSentSubscriptionAsync(
            target,
            settings,
            canceledWaiter.Token);
        await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var survivors = Enumerable.Range(0, 24)
            .Select(_ => service.EnsureChatMessageSentSubscriptionAsync(
                target,
                settings,
                CancellationToken.None))
            .ToArray();
        canceledWaiter.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => _ = await canceled);
        releaseLookup.TrySetResult();
        var results = await Task.WhenAll(survivors);

        Assert.True(results.All(result =>
            result.Status == KickEventSubscriptionEnsureStatus.Subscribed &&
            result.SubscriptionId == "subscription-1"));
        Assert.Equal(1, Volatile.Read(ref lookupCount));
        Assert.Equal(1, Volatile.Read(ref createCount));
        Assert.Equal(1, Volatile.Read(ref tokenCount));
        Assert.Equal(1, Volatile.Read(ref broadcasterCount));
        Assert.Equal(1, Volatile.Read(ref persistCount));
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

        var client = new KickChatClient(new ChatSettings(), new MemoryLogger());
        var gate = (SemaphoreSlim?)typeof(KickChatClient)
            .GetField("recentChatBackfillGate", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client);
        Assert.NotNull(gate);
        await gate!.WaitAsync();
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
            gate.Release();
            await client.DisposeAsync();
        }
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static LocalHttpRequest CreateSignedWebhookRequest(
        RSA key,
        string messageId,
        DateTimeOffset timestamp,
        string body)
    {
        var timestampText = timestamp.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            System.Globalization.CultureInfo.InvariantCulture);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var signedPrefix = Encoding.UTF8.GetBytes($"{messageId}.{timestampText}.");
        var signedBytes = new byte[signedPrefix.Length + bodyBytes.Length];
        Buffer.BlockCopy(signedPrefix, 0, signedBytes, 0, signedPrefix.Length);
        Buffer.BlockCopy(bodyBytes, 0, signedBytes, signedPrefix.Length, bodyBytes.Length);
        var signature = Convert.ToBase64String(key.SignData(
            signedBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
        return new LocalHttpRequest(
            "POST",
            KickWebhookChatServer.WebhookPath,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Kick-Event-Message-Id"] = messageId,
                ["Kick-Event-Message-Timestamp"] = timestampText,
                ["Kick-Event-Signature"] = signature
            },
            bodyBytes);
    }

    private static HttpRequestMessage CreateSignedWebhookHttpRequest(
        string url,
        RSA key,
        string messageId,
        DateTimeOffset timestamp,
        string body)
    {
        var timestampText = timestamp.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            System.Globalization.CultureInfo.InvariantCulture);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        KickWebhookTestSignature.AddKickHeaders(
            request,
            key,
            KickOfficialChatWebhookParser.ChatMessageSentEventType,
            messageId,
            timestampText,
            bodyBytes);
        return request;
    }

    private static string CreateTemporaryPath(string prefix)
    {
        return Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
    }
}
