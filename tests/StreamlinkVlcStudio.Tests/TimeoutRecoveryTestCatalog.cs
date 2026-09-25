internal static class TimeoutRecoveryTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("timeout recovery: Twitch credential validation remains retryable", TwitchCredentialsAsync),
        ("timeout recovery: shared Kick acquisition reports timeout without canceling waiters", KickAcquisitionAsync),
        ("timeout recovery: Kick refresh retains the current token", () => KickRefreshAsync(cancel: false)),
        ("timeout recovery: Kick refresh preserves caller cancellation", () => KickRefreshAsync(cancel: true)),
        ("timeout recovery: Kick replay falls back to website metadata", () => KickReplayAsync(cancel: false)),
        ("timeout recovery: Kick replay preserves caller cancellation", () => KickReplayAsync(cancel: true)),
        ("timeout recovery: Twitch archive timeout falls back to live DVR", () => TwitchReplayAsync(cancel: false)),
        ("timeout recovery: Twitch archive preserves caller cancellation", () => TwitchReplayAsync(cancel: true)),
        ("timeout recovery: optional prediction lookup does not cancel chat", () => PredictionsAsync(cancel: false)),
        ("timeout recovery: optional predictions preserve caller cancellation", () => PredictionsAsync(cancel: true)),
        ("timeout recovery: VOD chat reports a retryable failed page", () => VodChatAsync(cancel: false)),
        ("timeout recovery: VOD chat preserves caller cancellation", () => VodChatAsync(cancel: true))
    ];

    private const string TokenInfo = """{"client_id":"client","login":"streamer","user_id":"42","scopes":[],"expires_in":3600}""";
    private static readonly StreamTarget TwitchTarget = StreamInputParser.Parse("streamer", PlatformKind.Twitch);

    private static async Task TwitchCredentialsAsync()
    {
        var calls = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
            ++calls == 1 ? throw new OperationCanceledException("HTTP deadline") : Json(TokenInfo)));
        var token = Guid.NewGuid().ToString("N");
        var logger = new MemoryLogger();
        Assert.Equal<string?>(null, await TwitchClientIdCache.GetOrResolveAsync(
            client, token, logger, "Test", "Validation unavailable", default));
        Assert.Equal("client", await TwitchClientIdCache.GetOrResolveAsync(
            client, token, logger, "Test", "Validation unavailable", default));
        Assert.Equal(2, calls);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => TwitchClientIdCache.GetOrResolveAsync(
            client, token, logger, "Test", "Validation unavailable", canceled.Token));
        Assert.Equal(2, calls);
    }

    private static async Task KickRefreshAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var settings = new ChatSettings
        {
            KickOAuthToken = "current-token",
            KickRefreshToken = "refresh-token",
            KickClientId = "client",
            KickClientSecret = "secret",
            KickTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30)
        };
        var applied = false;
        var task = KickOAuthService.GetUsableAccessTokenAsync(settings,
            (_, _, _) => { applied = true; return Task.CompletedTask; },
            (_, _) =>
            {
                if (cancel) cancellation.Cancel();
                return Task.FromException<KickOAuthTokenResult>(new OperationCanceledException("HTTP deadline"));
            }, new MemoryLogger(), cancellation.Token);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else Assert.Equal("current-token", await task);
        Assert.True(!applied);
        Assert.Equal("refresh-token", settings.KickRefreshToken);
    }

    private static async Task KickAcquisitionAsync()
    {
        var calls = 0;
        var provider = new KickTokenProvider((_, _, _) =>
        {
            calls++;
            return Task.FromException<string?>(new OperationCanceledException("HTTP deadline"));
        });
        var settings = new ChatSettings();
        var logger = new MemoryLogger();
        Assert.Equal<string?>(null, await provider.ResolveAsync(settings, logger));
        Assert.Equal<string?>(null, await provider.ResolveAsync(settings, logger));
        Assert.Equal(1, calls);
        Assert.Equal(0, provider.InFlightCountForTest);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ResolveAsync(settings, logger, canceled.Token));
        Assert.Equal(1, calls);
    }

    private static async Task KickReplayAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var websiteCalls = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "api.kick.com")
            {
                if (cancel) cancellation.Cancel();
                throw new OperationCanceledException("HTTP deadline");
            }
            websiteCalls++;
            return Json("""{"slug":"streamer","livestream":{"id":123,"is_live":true,"start_time":"2026-09-25T12:00:00Z"}}""");
        }));
        var resolver = new ReplayResolver(new MemoryLogger(), new FakeStreamlinkService(), client,
            TestReplayUrlSecurity.PublicValidator,
            new KickTokenProvider((_, _, _) => Task.FromResult<string?>("token")));
        var settings = new AppSettings();
        settings.Replay.AttemptPrivateKickReplayResolution = false;
        var task = resolver.ResolveCurrentReplayAsync(StreamInputParser.Parse("streamer", PlatformKind.Kick),
            "best", settings, cancellation.Token);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else Assert.Equal<DateTimeOffset?>(DateTimeOffset.Parse("2026-09-25T12:00:00Z"), (await task).StreamStartedAtUtc);
        Assert.Equal(cancel ? 0 : 1, websiteCalls);
    }

    private static async Task TwitchReplayAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var dvrCalls = 0;
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "id.twitch.tv") return Json(TokenInfo);
            if (uri.AbsolutePath == "/helix/streams")
                return Json("""{"data":[{"user_login":"streamer","user_id":"42","id":"123456789","started_at":"2026-06-01T20:00:00Z"}]}""");
            if (uri.AbsolutePath == "/helix/videos") return Json("""{"data":[]}""");
            if (uri.Host == "gql.twitch.tv")
            {
                if (cancel) cancellation.Cancel();
                throw new OperationCanceledException("HTTP deadline");
            }
            dvrCalls++;
            return Json("#EXTM3U\n#EXT-X-TWITCH-TOTAL-SECS:42\n#EXTINF:10,\n0.ts\n");
        }));
        var resolver = new ReplayResolver(new MemoryLogger(), new FakeStreamlinkService(), client,
            TestReplayUrlSecurity.PublicValidator);
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = Guid.NewGuid().ToString("N");
        var task = resolver.ResolveCurrentReplayAsync(TwitchTarget, "best", settings, cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => task);
            Assert.Equal(0, dvrCalls);
        }
        else
        {
            var result = await task;
            Assert.True(result.IsAvailable);
            Assert.Equal("live-dvr-123456789", result.ReplayId);
            Assert.True(dvrCalls > 0);
        }
    }

    private static async Task PredictionsAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            if (cancel) cancellation.Cancel();
            throw new OperationCanceledException("HTTP deadline");
        }));
        await using var chat = new TwitchChatClient(new ChatSettings(), new MemoryLogger(), client);
        var info = new TwitchTokenInfo("streamer", "42", "client", null, [], true, true, true, true, true);
        var initialize = typeof(TwitchChatClient).GetMethod("InitializePredictionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)initialize.Invoke(chat, [TwitchTarget, "token", info, cancellation.Token])!;
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else
        {
            await task;
            Assert.True(!chat.PredictionAccess.CanManage);
            Assert.Contains("unavailable", chat.PredictionAccess.Message);
        }
    }

    private static async Task VodChatAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            if (cancel) cancellation.Cancel();
            throw new OperationCanceledException("HTTP deadline");
        }));
        var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123",
            "123", null, TimeSpan.FromHours(1), true, "");
        var task = new TwitchVodChatFetcher(client).FetchAsync(replay, TimeSpan.Zero, cancellation.Token);
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else
        {
            var result = await task;
            Assert.Equal(VodChatFetchOutcome.Failed, result.Outcome);
            Assert.Equal(0, result.Messages.Count);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
