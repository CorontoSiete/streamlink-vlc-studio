using System.Net;
using System.Text;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Limits;
using StreamlinkVlcStudio.Infrastructure.Logging;
using StreamlinkVlcStudio.Infrastructure.Replay;
using StreamlinkVlcStudio.Infrastructure.Text;
using StreamlinkVlcStudio.Infrastructure.Viewers;

internal static class InfrastructureRuntimeTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("infrastructure runtime: bounded HTTP sender does not prebuffer response bodies", BoundedHttpSenderUsesHeadersOnlyAsync),
        ("infrastructure runtime: Kick website JSON shares bounded curl fallback", KickWebsiteJsonFallbackIsSharedAsync),
        ("infrastructure runtime: GraphQL errors share batched and response message parsing", GraphQlErrorsAreShared),
        ("infrastructure runtime: file logger flush completes when its writer faults", FileLoggerFaultedFlushAsync),
        ("infrastructure runtime: live snapshot TTL begins when loading completes", LiveSnapshotTtlStartsAtCompletionAsync),
        ("infrastructure runtime: browse retry delays clamp and timestamp math saturates", BrowseRetryMathIsBounded),
        ("infrastructure runtime: bounded stream lines drain oversized records", BoundedStreamLinesDrainOversizedRecordsAsync),
        ("infrastructure runtime: Kick replay cache skips oversized JSONL records", KickReplayCacheSkipsOversizedRecordsAsync),
        ("infrastructure runtime: Twitch replay chat stays anonymous", TwitchReplayChatStaysAnonymousAsync),
        ("infrastructure runtime: replay chat rejects overflowing offsets", ReplayChatRejectsOverflowingOffsets),
        ("infrastructure runtime: external input and lifecycle boundaries are enforced", ExternalInputAndLifecycleBoundariesAreEnforcedAsync)
    ];

    private static async Task BoundedHttpSenderUsesHeadersOnlyAsync()
    {
        var content = new SerializationProbeContent();
        using var client = new HttpClient(new RuntimeHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/");

        using var response = await BoundedHttpResponseSender
            .SendAsync(client, request)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(false, content.WasSerialized);
    }

    private static async Task KickWebsiteJsonFallbackIsSharedAsync()
    {
        var fallbackCalls = 0;
        using var client = new HttpClient(new RuntimeHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var reader = new KickWebsiteJsonReader(
            client,
            new MemoryLogger(),
            "Test",
            TimeSpan.FromSeconds(1),
            (_, _, _) =>
            {
                fallbackCalls++;
                return Task.FromResult<string?>("{}");
            });

        Assert.Equal("{}", await reader.ReadAsync("https://kick.com/api/test", "https://kick.com/", CancellationToken.None));
        Assert.Equal(1, fallbackCalls);

        fallbackCalls = 0;
        var direct = await reader.ReadDirectAsync(
            "https://kick.com/api/test",
            "https://kick.com/",
            CancellationToken.None);
        Assert.Equal<string?>(null, direct.Body);
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        Assert.Equal(0, fallbackCalls);

        using var htmlClient = new HttpClient(new RuntimeHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><script>window.__data={}</script></html>")
            })));
        var htmlReader = new KickWebsiteJsonReader(
            htmlClient,
            new MemoryLogger(),
            "Test",
            TimeSpan.FromSeconds(1));
        Assert.Equal(
            "<html><script>window.__data={}</script></html>",
            await htmlReader.ReadAsync(
                "https://kick.com/test/videos",
                "https://kick.com/test",
                CancellationToken.None,
                KickWebsitePayloadKind.Html));

        var oversized = new KickWebsiteJsonReader(
            client,
            new MemoryLogger(),
            "Test",
            TimeSpan.FromSeconds(1),
            (_, _, _) => Task.FromResult<string?>(new string('x', PayloadLimits.HttpJsonBytes + 1)));
        Assert.Equal<string?>(
            null,
            await oversized.ReadAsync("https://kick.com/api/test", "https://kick.com/", CancellationToken.None));

        using var failedClient = new HttpClient(new RuntimeHttpHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("network failed"))));
        var networkFallbackCalls = 0;
        var networkFallback = new KickWebsiteJsonReader(
            failedClient,
            new MemoryLogger(),
            "Test",
            TimeSpan.FromSeconds(1),
            (_, _, _) =>
            {
                networkFallbackCalls++;
                return Task.FromResult<string?>("{\"source\":\"curl\"}");
            });
        Assert.Equal(
            "{\"source\":\"curl\"}",
            await networkFallback.ReadAsync("https://kick.com/api/test", "https://kick.com/", CancellationToken.None));
        Assert.Equal(1, networkFallbackCalls);

        using var blankClient = new HttpClient(new RuntimeHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("   ") })));
        var blankFallbackCalls = 0;
        var blankFallback = new KickWebsiteJsonReader(
            blankClient,
            new MemoryLogger(),
            "Test",
            TimeSpan.FromSeconds(1),
            (_, _, _) =>
            {
                blankFallbackCalls++;
                return Task.FromResult<string?>("[]");
            });
        Assert.Equal(
            "[]",
            await blankFallback.ReadAsync("https://kick.com/api/test", "https://kick.com/", CancellationToken.None));
        Assert.Equal(1, blankFallbackCalls);

        using var canceledClient = new HttpClient(new RuntimeHttpHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }));
        var canceledFallbackCalls = 0;
        var canceledReader = new KickWebsiteJsonReader(
            canceledClient,
            new MemoryLogger(),
            "Test",
            TimeSpan.FromSeconds(1),
            (_, _, _) =>
            {
                canceledFallbackCalls++;
                return Task.FromResult<string?>("{}");
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => canceledReader.ReadAsync(
            "https://kick.com/api/test",
            "https://kick.com/",
            cancellation.Token));
        Assert.Equal(0, canceledFallbackCalls);
    }

    private static Task GraphQlErrorsAreShared()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """[{"data":null},{"errors":[{"message":123},{"message":"  second operation failed  "}]}]""");
        Assert.Equal("second operation failed", GraphQlErrorReader.Extract(document.RootElement));
        Assert.Equal(
            "top-level failure",
            GraphQlErrorReader.ExtractResponseMessage("""{"message":"  top-level failure  "}"""));
        Assert.Equal("plain", GraphQlErrorReader.ExtractResponseMessage("  plain  "));
        return Task.CompletedTask;
    }

    private static async Task FileLoggerFaultedFlushAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"svs-logger-fault-{Guid.NewGuid():N}");
        var logger = new FileAppLogger(
            root,
            capacity: 4,
            maximumFileBytes: 4096,
            maximumFileCount: 1,
            shutdownTimeout: TimeSpan.FromMilliseconds(250),
            beforeWriteAsync: _ => Task.FromException(new InvalidOperationException("writer failed")));
        try
        {
            logger.Write(AppLogLevel.Info, "Test", "entry");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => logger.FlushAsync().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Contains("writer failed", error.Message);
        }
        finally
        {
            try
            {
                await logger.DisposeAsync();
            }
            catch (InvalidOperationException)
            {
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task LiveSnapshotTtlStartsAtCompletionAsync()
    {
        var clock = new RuntimeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var client = new HttpClient(new RuntimeHttpHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                await firstRelease.Task;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            };
        }));
        var provider = new LiveChannelSnapshotProvider(client, clock);

        var first = provider.GetTwitchAsync("channel", "token", "client", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        firstRelease.TrySetResult();
        await first;

        clock.Advance(TimeSpan.FromSeconds(4));
        await provider.GetTwitchAsync("channel", "token", "client", CancellationToken.None);
        Assert.Equal(1, requests);

        clock.Advance(TimeSpan.FromSeconds(2));
        await provider.GetTwitchAsync("channel", "token", "client", CancellationToken.None);
        Assert.Equal(2, requests);

        var faultRequests = 0;
        using var faultClient = new HttpClient(new RuntimeHttpHandler((_, _) =>
        {
            if (Interlocked.Increment(ref faultRequests) == 1)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("transient"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
        }));
        var faultProvider = new LiveChannelSnapshotProvider(faultClient, clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => faultProvider.GetTwitchAsync(
            "fault-channel",
            "token",
            "client",
            CancellationToken.None));
        await faultProvider.GetTwitchAsync("fault-channel", "token", "client", CancellationToken.None);
        Assert.Equal(2, faultRequests);
    }

    private static Task BrowseRetryMathIsBounded()
    {
        Assert.Equal(TimeSpan.Zero, BrowseService.ClampTwitchRateLimitDelay(TimeSpan.FromSeconds(-1)));
        Assert.Equal(TimeSpan.FromMinutes(1), BrowseService.ClampTwitchRateLimitDelay(TimeSpan.MaxValue));
        Assert.Equal(
            DateTimeOffset.MaxValue,
            BrowseService.SaturatingAdd(DateTimeOffset.MaxValue, TimeSpan.FromMilliseconds(1)));
        return Task.CompletedTask;
    }

    private static async Task BoundedStreamLinesDrainOversizedRecordsAsync()
    {
        var bytes = Encoding.UTF8.GetBytes($"{new string('a', 64)}\nnext\n");
        await using var stream = new MemoryStream(bytes);
        using var reader = new BoundedStreamLineReader(stream, new UTF8Encoding(false, true), 16);

        var oversized = await reader.ReadLineAsync();
        Assert.NotNull(oversized);
        Assert.True(oversized!.Value.WasTruncated);
        Assert.Equal(16, oversized.Value.Text.Length);
        Assert.Equal("next", (await reader.ReadLineAsync())!.Value.Text);
    }

    private static async Task KickReplayCacheSkipsOversizedRecordsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"svs-kick-replay-{Guid.NewGuid():N}");
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        try
        {
            var path = Path.Combine(root, "channel", "20260102.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, new string('x', (1024 * 1024) + 1) + "\n");

            var store = new KickOfficialChatReplayStore(root);
            await store.AppendAsync(new ChatMessage(
                PlatformKind.Kick,
                "channel",
                "viewer",
                "hello",
                timestamp,
                MessageId: "one"));

            var result = await store.ReadMessagesAsync(
                "channel",
                timestamp.AddMinutes(-1),
                timestamp.AddMinutes(1));
            Assert.Equal(1, result.Messages.Count);
            Assert.Equal("hello", result.Messages[0].Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task TwitchReplayChatStaysAnonymousAsync()
    {
        string? authorization = null;
        using var client = new HttpClient(new RuntimeHttpHandler((request, _) =>
        {
            authorization = request.Headers.TryGetValues("Authorization", out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"data":{"video":{"comments":{"edges":[],"pageInfo":{"hasNextPage":false}}}}}]""",
                    Encoding.UTF8,
                    "application/json")
            });
        }));
        var provider = new ReplayChatProvider(client);
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "  oauth:test-token  ";
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "channel",
            "https://www.twitch.tv/videos/1",
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddHours(-1),
            TimeSpan.FromHours(1),
            true,
            "");

        await provider.LoadChatAsync(replay, settings, TimeSpan.Zero);
        Assert.Equal(null, authorization);
    }

    private static async Task KickReplayDateBoundariesDoNotOverflowAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"svs-kick-boundary-{Guid.NewGuid():N}");
        try
        {
            var store = new KickOfficialChatReplayStore(root);
            var lastInstant = DateTimeOffset.MaxValue;
            var read = await store.ReadMessagesAsync("channel", lastInstant, lastInstant);
            Assert.Equal(0, read.CacheFileCount);

            var provider = new ReplayChatProvider(store);
            var replay = new ReplaySessionInfo(
                PlatformKind.Kick,
                "channel",
                "https://kick.com/channel/videos/example",
                "example",
                DateTimeOffset.MaxValue.AddMinutes(-1),
                TimeSpan.FromHours(1),
                true,
                "");
            var result = await provider.LoadKickOfficialWebhookChatAsync(
                replay,
                TimeSpan.FromHours(1));
            Assert.Equal(false, result.IsAvailable);
            Assert.Contains("outside the supported date range", result.UnavailableReason);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task ReplayChatRejectsOverflowingOffsets()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "comments": [
            {
              "_id": "overflow",
              "content_offset_seconds": 922337203685.4776,
              "commenter": { "name": "invalid" },
              "message": { "body": "must be rejected" }
            },
            {
              "_id": "zero",
              "content_offset_seconds": 0,
              "commenter": { "name": "valid" },
              "message": { "body": "must remain" }
            }
          ]
        }
        """);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "channel",
            "https://www.twitch.tv/videos/1",
            "1",
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            true,
            "");

        var messages = ReplayChatProvider.ReadTwitchDownloaderMessages(document.RootElement, replay);

        Assert.Equal(1, messages.Count);
        Assert.Equal("zero", messages[0].Message.MessageId);
        Assert.Equal(TimeSpan.Zero, messages[0].Offset);
        return Task.CompletedTask;
    }

    private static Task ReplayChatRejectsUnsafeTwitchVodCacheIds()
    {
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "channel",
            "https://www.twitch.tv/videos/1",
            "../outside-cache",
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            true,
            "");

        var result = ReplayChatProvider.LoadTwitchChat(replay);

        Assert.Equal(false, result.IsAvailable);
        Assert.Contains("valid numeric VOD ID", result.UnavailableReason);
        return Task.CompletedTask;
    }

    private static async Task TwitchChatDoesNotDuplicateUserAgentAsync()
    {
        using var client = HttpClientFactory.CreateDefault();
        await using var chat = new TwitchChatClient(new ChatSettings(), new MemoryLogger(), client);

        Assert.Equal(1, client.DefaultRequestHeaders.UserAgent.Count());
        Assert.Equal(HttpClientFactory.ApplicationUserAgent, client.DefaultRequestHeaders.UserAgent.Single().ToString());
    }

    private static Task EventSubInputIsBounded()
    {
        Assert.True(TwitchPredictionEventSubClient.TryCreateReconnectUri(
            "wss://eventsub.wss.twitch.tv/ws?reconnect=1",
            out _));
        Assert.Equal(false, TwitchPredictionEventSubClient.TryCreateReconnectUri(
            "ws://eventsub.wss.twitch.tv/ws",
            out _));
        Assert.Equal(false, TwitchPredictionEventSubClient.TryCreateReconnectUri(
            "wss://eventsub.wss.twitch.tv.evil.example/ws",
            out _));
        Assert.Equal(false, TwitchPredictionEventSubClient.TryCreateReconnectUri(
            "wss://user@eventsub.wss.twitch.tv/ws",
            out _));

        var parser = new TwitchPredictionEventSubParser();
        var oversizedId = new string('x', 257);
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            metadata = new { message_id = oversizedId, message_type = "notification" },
            payload = new { }
        });
        Assert.Equal(false, parser.TryParse(
            payload,
            out _));
        return Task.CompletedTask;
    }

    private static async Task LiveChatSupervisorRecoversFromNonFiniteJitterAsync()
    {
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? observedDelay = null;
        await using var supervisor = new LiveChatConnectionSupervisor(
            new MemoryLogger(),
            "TestChat",
            _ => { },
            (delay, _) =>
            {
                observedDelay = delay;
                return Task.CompletedTask;
            },
            () => double.NaN);
        supervisor.Start(_ =>
        {
            reconnected.TrySetResult();
            return Task.CompletedTask;
        });

        supervisor.NotifyConnectionEnded(TimeSpan.Zero);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), observedDelay);
    }

    private static async Task ExternalInputAndLifecycleBoundariesAreEnforcedAsync()
    {
        await ValidatedReplayRejectsHiddenRedirectsAsync();
        await KickReplayDateBoundariesDoNotOverflowAsync();
        await TwitchChatDoesNotDuplicateUserAgentAsync();
        await KickTokenExpiryBoundaryDoesNotOverflowAsync();
        await LiveChatSupervisorRecoversFromNonFiniteJitterAsync();
        await ReplayChatRejectsUnsafeTwitchVodCacheIds();
        await EventSubInputIsBounded();
    }

    private static async Task KickTokenExpiryBoundaryDoesNotOverflowAsync()
    {
        var provider = new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token"));
        var settings = new ChatSettings
        {
            KickClientId = "client",
            KickClientSecret = "secret",
            KickTokenExpiresAtUtc = DateTimeOffset.MinValue
        };

        Assert.Equal("token", await provider.ResolveAsync(settings, new MemoryLogger()));
    }

    private static async Task ValidatedReplayRejectsHiddenRedirectsAsync()
    {
        var initial = new Uri("https://d2e2de1etea730.cloudfront.net/replay.m3u8");
        using var client = new HttpClient(new RuntimeHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://d1g1f25tn8m2e6.cloudfront.net/redirected.m3u8")
            })));
        var validator = new ReplayUrlSecurityValidator(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ValidatedReplayHttpClient.SendGetAsync(
            client,
            validator,
            initial,
            PlatformKind.Twitch,
            uri => new HttpRequestMessage(HttpMethod.Get, uri),
            CancellationToken.None));
    }

    private sealed class RuntimeHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
    }

    private sealed class SerializationProbeContent : HttpContent
    {
        internal bool WasSerialized { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasSerialized = true;
            return Task.FromException(new InvalidOperationException("Response body was prebuffered."));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class RuntimeTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }
}
