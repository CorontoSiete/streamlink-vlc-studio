internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> CoreAndInput { get; } =
    [
    ("parses Twitch URL", () =>
    {
        var target = StreamInputParser.Parse("https://www.twitch.tv/xqc", PlatformKind.Kick);
        Assert.Equal(PlatformKind.Twitch, target.Platform);
        Assert.Equal("xqc", target.Channel);
        Assert.Equal("https://www.twitch.tv/xqc", target.Url);
        return Task.CompletedTask;
    }),
    ("parses Kick URL", () =>
    {
        var target = StreamInputParser.Parse("https://kick.com/some-channel", PlatformKind.Twitch);
        Assert.Equal(PlatformKind.Kick, target.Platform);
        Assert.Equal("some-channel", target.Channel);
        Assert.Equal("https://kick.com/some-channel", target.Url);
        return Task.CompletedTask;
    }),
    ("parses scheme-less platform URLs", () =>
    {
        var twitchTarget = StreamInputParser.Parse("www.twitch.tv/summit1g?ref=home", PlatformKind.Kick);
        Assert.Equal(PlatformKind.Twitch, twitchTarget.Platform);
        Assert.Equal("summit1g", twitchTarget.Channel);
        Assert.Equal("https://www.twitch.tv/summit1g", twitchTarget.Url);

        var kickTarget = StreamInputParser.Parse("kick.com/some-channel", PlatformKind.Twitch);
        Assert.Equal(PlatformKind.Kick, kickTarget.Platform);
        Assert.Equal("some-channel", kickTarget.Channel);
        Assert.Equal("https://kick.com/some-channel", kickTarget.Url);

        var twitchPortTarget = StreamInputParser.Parse("twitch.tv:443/summit1g", PlatformKind.Kick);
        Assert.Equal(PlatformKind.Twitch, twitchPortTarget.Platform);
        Assert.Equal("summit1g", twitchPortTarget.Channel);
        Assert.Equal("https://www.twitch.tv/summit1g", twitchPortTarget.Url);

        var kickPortTarget = StreamInputParser.Parse("kick.com:443/some-channel", PlatformKind.Twitch);
        Assert.Equal(PlatformKind.Kick, kickPortTarget.Platform);
        Assert.Equal("some-channel", kickPortTarget.Channel);
        Assert.Equal("https://kick.com/some-channel", kickPortTarget.Url);
        Assert.Throws<ArgumentException>(() => StreamInputParser.Parse("twitch.tv:notaport/xqc", PlatformKind.Twitch));
        return Task.CompletedTask;
    }),
    ("new settings pause inactive tabs by default", () =>
    {
        Assert.Equal(false, new AppSettings().KeepInactiveTabsRunning);
        return Task.CompletedTask;
    }),
    ("cancellation coordinator cancels stale work and drains active operations", async () =>
    {
        using var lifetime = new CancellationTokenSource();
        using var coordinator = new CancellationDebounceCoordinator();
        var callbacks = 0;
        coordinator.Schedule(TimeSpan.FromMilliseconds(40), () => Interlocked.Increment(ref callbacks));
        coordinator.Schedule(TimeSpan.FromMilliseconds(10), () => Interlocked.Add(ref callbacks, 10));
        await Task.Delay(100);
        Assert.Equal(10, callbacks);

        var operation = coordinator.BeginOperation(lifetime.Token);
        coordinator.CancelActive();
        Assert.True(operation.IsCancellationRequested);
        coordinator.Complete(operation);
        await coordinator.DrainAsync(TimeSpan.FromSeconds(1));
        return;
    }),
    ("cancellation coordinator reports timer callback failures", async () =>
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new CancellationDebounceCoordinator();

        Assert.True(coordinator.Schedule(
            TimeSpan.FromMilliseconds(10),
            () => throw new InvalidOperationException("debounced callback failed"),
            exception => observed.TrySetResult(exception)));

        var exception = await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("debounced callback failed", exception.Message);
    }),
    ("tab start releases its guard when UI dispatch rejects work", async () =>
    {
        var dispatchAttempts = 0;
        var settings = new AppSettings();
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action(),
            tryDispatch: _ =>
            {
                dispatchAttempts++;
                return false;
            });
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        viewModel.Tabs.Add(tab);

        var start = typeof(MainViewModel).GetMethod(
            "StartTabInBackground",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(start);
        start!.Invoke(viewModel, [tab, false, false]);
        start.Invoke(viewModel, [tab, false, false]);

        Assert.Equal(2, dispatchAttempts);
        await viewModel.DisposeAsync();
    }),
    ("stream search controller invalidates stale generations and drains cancellation", async () =>
    {
        using var lifetime = new CancellationTokenSource();
        using var controller = new StreamSearchController();
        var firstGeneration = controller.AdvanceGeneration();
        Assert.True(controller.IsCurrent(firstGeneration, "old", () => "old", () => false));

        var operation = controller.BeginOperation(lifetime.Token);
        var secondGeneration = controller.AdvanceGeneration();
        Assert.True(secondGeneration > firstGeneration);
        Assert.Equal(false, controller.IsCurrent(firstGeneration, "old", () => "old", () => false));
        controller.CancelActive();
        Assert.True(operation.IsCancellationRequested);
        controller.Complete(operation);
        await controller.DrainAsync(TimeSpan.FromSeconds(1));
    }),
    ("VOD and Browse controller cancels replaceable operations", async () =>
    {
        using var lifetime = new CancellationTokenSource();
        using var controller = new VodBrowseController();
        var first = controller.AdvanceTwitchVodGeneration();
        var operation = controller.BeginTwitchVodOperation(lifetime.Token);
        var second = controller.AdvanceTwitchVodGeneration();
        Assert.True(second > first);
        Assert.Equal(false, controller.IsCurrentTwitchVodGeneration(first));
        controller.CancelTwitchVodOperation();
        Assert.True(operation.IsCancellationRequested);
        controller.CompleteTwitchVodOperation(operation);
        await controller.DrainAsync(TimeSpan.FromSeconds(1));
    }),
    ("recent stream controller isolates transient hints and status updates", () =>
    {
        var controller = new RecentStreamController();
        controller.SetHint("twitch:channel", new RecentStreamHint("thumb", "name", "category"));
        Assert.Equal("thumb", controller.TakeHint("twitch:channel")!.ThumbnailUrl);
        Assert.Equal<RecentStreamHint?>(null, controller.TakeHint("twitch:channel"));

        var status = new RecentStreamLiveStatus(
            RecentStreamLiveState.Live,
            DateTimeOffset.UtcNow,
            "live");
        Assert.True(controller.SetLiveStatus("twitch:channel", status));
        Assert.Equal(false, controller.SetLiveStatus("twitch:channel", status));
        Assert.True(controller.TryGetLiveStatus("twitch:channel", out var observed));
        Assert.Equal(status, observed);
        return Task.CompletedTask;
    }),
    ("tab grouping controller removes closed tabs from every group", () =>
    {
        var logger = new MemoryLogger();
        var first = TestViewModels.CreateTab(
            StreamInputParser.Parse("first", PlatformKind.Twitch),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            logger,
            action => action());
        var second = TestViewModels.CreateTab(
            StreamInputParser.Parse("second", PlatformKind.Kick),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            logger,
            action => action());
        var controller = new TabGroupingController();
        controller.MultiViewGroups.Add([first, second]);
        controller.PictureInPictureGroups.Add([first, second]);
        controller.PictureInPictureVisibleGroups.Add([first, second]);
        controller.RemoveTabs([first]);
        Assert.Equal(0, controller.MultiViewGroups.Count);
        Assert.Equal(0, controller.PictureInPictureGroups.Count);
        Assert.Equal(0, controller.PictureInPictureVisibleGroups.Count);
        return Task.CompletedTask;
    }),
    ("inactive playback policy controller drains failures and rejects post-dispose requests", async () =>
    {
        Task? tracked = null;
        Exception? failure = null;
        var passes = 0;
        using var controller = new TabPlaybackPolicyController(
            action => action(),
            () => false,
            () =>
            {
                passes++;
                throw new InvalidOperationException("policy failure");
            },
            task => tracked = task,
            exception => failure = exception);

        controller.Request();
        Assert.NotNull(tracked);
        await tracked!;
        Assert.Equal(1, passes);
        Assert.Equal("policy failure", failure?.Message);
        controller.Dispose();
        controller.Request();
        Assert.Equal(1, passes);
    }),
    ("main view model moves chat settings observation to a replacement instance", async () =>
    {
        var settings = new AppSettings();
        var oldChat = settings.Chat;
        var streamlink = new FakeStreamlinkService();
        var logger = new MemoryLogger();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            logger,
            action => action());
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("xqc", PlatformKind.Twitch),
            "best",
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            logger,
            action => action());
        viewModel.Tabs.Add(tab);

        var replacement = new ChatSettings { Layout = ChatLayout.Docked };
        settings.Chat = replacement;
        var observedChatField = typeof(MainViewModel).GetField(
            "observedChatSettings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var tabChatField = typeof(StreamTabViewModel).GetField(
            "chatSettings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(observedChatField);
        Assert.NotNull(tabChatField);
        Assert.Equal(replacement, observedChatField!.GetValue(viewModel));
        Assert.Equal(replacement, tabChatField!.GetValue(tab));

        oldChat.Layout = ChatLayout.Overlay;
        Assert.Equal(replacement, observedChatField.GetValue(viewModel));
        await viewModel.DisposeAsync();
        return;
    }),
    ("stream tab disposal is idempotent and serialized", async () =>
    {
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("xqc", PlatformKind.Twitch),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        var disposals = Enumerable.Range(0, 4)
            .Select(_ => tab.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals);
        await tab.DisposeAsync();
        return;
    }),
    ("replay chat results normalize null message collections", () =>
    {
        var result = ReplayChatLoadResult.Available(null);

        Assert.True(result.IsAvailable);
        Assert.NotNull(result.Messages);
        Assert.Equal(0, result.Messages.Count);
        return Task.CompletedTask;
    }),
    ("chat backfill results normalize null and default message collections", () =>
    {
        var explicitResult = new ChatHistoryBackfillResult(
            Attempted: true,
            LoadedMessageCount: 0,
            CoveredRequestedRange: false,
            CoveredFromTimestampUtc: null,
            CoveredThroughTimestampUtc: null,
            Messages: null);
        var defaultResult = default(ChatHistoryBackfillResult);

        Assert.NotNull(explicitResult.Messages);
        Assert.Equal(0, explicitResult.Messages.Count);
        Assert.NotNull(defaultResult.Messages);
        Assert.Equal(0, defaultResult.Messages.Count);
        return Task.CompletedTask;
    }),
    ("shared string helpers tolerate null params arrays", () =>
    {
        Assert.Equal("", StringValues.FirstNonEmpty((string?[]?)null));
        Assert.Equal("trimmed", StringValues.FirstNonEmpty(null, "  trimmed  ", "later"));
        Assert.Equal<string?>(null, StringValues.NullIfEmpty("  "));

        using var json = JsonDocument.Parse("{\"value\":\"  preserve  \"}");
        Assert.Equal("preserve", JsonElementReader.GetOptionalString(json.RootElement, "value"));
        Assert.Equal("  preserve  ", JsonElementReader.GetOptionalString(json.RootElement, "value", trimStrings: false));
        return Task.CompletedTask;
    }),
    ("normalizes shared image URLs and size templates", () =>
    {
        Assert.Equal(
            "https://cdn.example.com/440x248.jpg",
            StringValues.NormalizeImageUrl(" //cdn.example.com/{width}x{height}.jpg ", "440", "248"));
        Assert.Equal(
            "https://cdn.example.com/320x180.jpg",
            StringValues.NormalizeImageUrl("//cdn.example.com/%{width}x%{height}.jpg", "320", "180"));
        Assert.Equal("", StringValues.NormalizeImageUrl(null));
        return Task.CompletedTask;
    }),
    ("OAuth expiry parsing is invariant and bounded", () =>
    {
        var expiry = OAuthTokenHelpers.TryGetExpiresAt("3600");
        Assert.True(expiry.HasValue);
        Assert.True(expiry.GetValueOrDefault() > DateTimeOffset.UtcNow);
        Assert.Equal<DateTimeOffset?>(null, OAuthTokenHelpers.TryGetExpiresAt("1,000"));
        Assert.Equal<DateTimeOffset?>(null, OAuthTokenHelpers.TryGetExpiresAt(long.MaxValue.ToString(CultureInfo.InvariantCulture)));

        using var document = JsonDocument.Parse("{\"expires_in\":\"3600\"}");
        Assert.True(OAuthTokenHelpers.TryGetExpiresAt(document.RootElement, "expires_in").HasValue);

        using var scopesDocument = JsonDocument.Parse("{\"scope\":[\"chat:read\",\"\",42]}");
        var scopes = OAuthTokenHelpers.ReadScopes(scopesDocument.RootElement);
        Assert.True(scopes.Contains("chat:read"));
        Assert.Equal(1, scopes.Count);

        using var malformedDocument = JsonDocument.Parse("[]");
        Assert.Equal(0, OAuthTokenHelpers.ReadScopes(malformedDocument.RootElement).Count);
        return Task.CompletedTask;
    }),
    ("creates Twitch live clips through the official Helix flow", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        var responseIndex = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests.Add(request);
            return responseIndex++ switch
            {
                0 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"login\":\"streamer\",\"user_id\":\"42\",\"client_id\":\"client-id\",\"expires_in\":3600,\"scopes\":[\"clips:edit\"]}",
                        Encoding.UTF8,
                        "application/json")
                },
                1 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"id\":\"123456\"}]}",
                        Encoding.UTF8,
                        "application/json")
                },
                2 => new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"id\":\"clip-id\"}]}",
                        Encoding.UTF8,
                        "application/json")
                },
                3 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
                },
                4 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"id\":\"clip-id\",\"url\":\"https://clips.twitch.tv/clip-id\"}]}",
                        Encoding.UTF8,
                        "application/json")
                },
                _ => throw new InvalidOperationException("Unexpected Twitch API request.")
            };
        }));
        var service = new TwitchClipService(httpClient, TimeSpan.Zero, readinessPollAttempts: 2);
        var target = new StreamTarget(
            PlatformKind.Twitch,
            "some-channel",
            "https://www.twitch.tv/some-channel");
        var settings = new ChatSettings { TwitchOAuthToken = "oauth-token" };

        var result = await service.CreateLiveClipAsync(target, settings);

        Assert.Equal("clip-id", result.ClipId);
        Assert.Equal("https://clips.twitch.tv/clip-id", result.ClipUri.ToString());
        Assert.Equal(5, requests.Count);
        Assert.Equal("https://id.twitch.tv/oauth2/validate", requests[0].RequestUri!.ToString());
        Assert.Equal("https://api.twitch.tv/helix/users?login=some-channel", requests[1].RequestUri!.ToString());
        Assert.Equal("https://api.twitch.tv/helix/clips?broadcaster_id=123456&duration=30", requests[2].RequestUri!.ToString());
        Assert.Equal("https://api.twitch.tv/helix/clips?id=clip-id", requests[4].RequestUri!.ToString());
        Assert.Equal("Bearer oauth-token", requests[0].Headers.Authorization?.ToString());
        Assert.Equal("client-id", requests[2].Headers.GetValues("Client-Id").Single());
        return;
    }),
    ("requires clips edit scope for Twitch clips", async () =>
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"login\":\"streamer\",\"user_id\":\"42\",\"client_id\":\"client-id\",\"expires_in\":3600,\"scopes\":[\"chat:read\"]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var service = new TwitchClipService(httpClient, TimeSpan.Zero, readinessPollAttempts: 1);
        var target = new StreamTarget(
            PlatformKind.Twitch,
            "some-channel",
            "https://www.twitch.tv/some-channel");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLiveClipAsync(
            target,
            new ChatSettings { TwitchOAuthToken = "oauth-token" }));

        Assert.Contains("clips:edit", error.Message);
        Assert.Equal(1, requestCount);
        return;
    }),
    ("reports the configured Twitch clip readiness window", async () =>
    {
        var responseIndex = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            return responseIndex++ switch
            {
                0 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"login\":\"streamer\",\"user_id\":\"42\",\"client_id\":\"client-id\",\"expires_in\":3600,\"scopes\":[\"clips:edit\"]}",
                        Encoding.UTF8,
                        "application/json")
                },
                1 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"123456\"}]}", Encoding.UTF8, "application/json")
                },
                2 => new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"clip-id\"}]}", Encoding.UTF8, "application/json")
                },
                3 or 4 => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
                },
                _ => throw new InvalidOperationException("Unexpected Twitch API request.")
            };
        }));
        var service = new TwitchClipService(httpClient, TimeSpan.Zero, readinessPollAttempts: 2);
        var target = new StreamTarget(
            PlatformKind.Twitch,
            "some-channel",
            "https://www.twitch.tv/some-channel");

        var error = await Assert.ThrowsAsync<TimeoutException>(() => service.CreateLiveClipAsync(
            target,
            new ChatSettings { TwitchOAuthToken = "oauth-token" }));

        Assert.Contains("2 readiness checks", error.Message);
        Assert.Contains("without a delay", error.Message);
        Assert.Contains("maximum 60 seconds", error.Message);
        return;
    }),
    ("selected Twitch tab clips and Kick tab stays disabled", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var logger = new MemoryLogger();
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var clipService = new FakeTwitchClipService();
        Uri? openedUri = null;
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action(),
            twitchClipService: clipService,
            openBrowser: uri => openedUri = uri);

        var twitchTab = TestViewModels.CreateTab(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/streamer"),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        var kickTab = TestViewModels.CreateTab(
            new StreamTarget(PlatformKind.Kick, "streamer", "https://kick.com/streamer"),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());
        viewModel.Tabs.Add(twitchTab);
        viewModel.Tabs.Add(kickTab);

        viewModel.SelectedTab = twitchTab;
        Assert.True(viewModel.CreateClipCommand.CanExecute(null));
        await viewModel.CreateClipCommand.ExecuteAsync();
        Assert.Equal(1, clipService.Targets.Count);
        Assert.Equal(twitchTab.Target, clipService.Targets[0]);
        Assert.Equal(clipService.ClipUri, openedUri);

        viewModel.SelectedTab = kickTab;
        Assert.Equal(false, viewModel.CreateClipCommand.CanExecute(null));
        Assert.Equal("Kick clipping is disabled", viewModel.ClipButtonToolTip);
        await viewModel.DisposeAsync();
        return;
    }),
    ("observes AsyncRelayCommand failures from WPF Execute", async () =>
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("command failed");
            },
            errorHandler: exception => observed.TrySetResult(exception));

        command.Execute(null);
        var asyncVoidException = await observed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("command failed", asyncVoidException.Message);

        var directException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteAsync());
        Assert.Equal("command failed", directException.Message);
    }),
    ("Kick send confirmation rejects malformed or unconfirmed responses", () =>
    {
        var method = typeof(KickChatClient).GetMethod(
            "KickSendResponseIndicatesSuccess",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        static bool Invoke(MethodInfo method, string response) =>
            (bool)method.Invoke(null, [response])!;

        Assert.True(Invoke(method!, ""));
        Assert.True(Invoke(method!, "{\"data\":{\"is_sent\":true}}"));
        Assert.Equal(false, Invoke(method!, "{\"data\":{\"is_sent\":false}}"));
        Assert.Equal(false, Invoke(method!, "{\"data\":{}}"));
        Assert.Equal(false, Invoke(method!, "not-json"));
        return Task.CompletedTask;
    }),
    ("serializes Kick send with concurrent disconnect and disposal", async () =>
    {
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token/introspect", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"active":true,"token_type":"user","scope":["chat:write"]}}""")
                };
            }

            if (request.RequestUri.AbsolutePath == "/public/v1/chat")
            {
                sendStarted.TrySetResult();
                await releaseSend.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":{"is_sent":true}}""")
                };
            }

            throw new InvalidOperationException($"Unexpected Kick request: {request.RequestUri}");
        }));

        var settings = new ChatSettings
        {
            KickOAuthToken = "kick-token",
            KickSendAsBot = true
        };
        var client = new KickChatClient(settings, new MemoryLogger(), httpClient);
        var sendTask = client.SendMessageAsync("hello");
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disconnectTask = client.DisconnectAsync();
        var disposeTask = client.DisposeAsync().AsTask();
        releaseSend.TrySetResult();

        await Task.WhenAll(sendTask, disconnectTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendMessageAsync("after dispose"));
    }),
    ("cancels and drains in-flight Twitch prediction disposal", async () =>
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new AsyncHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/helix/predictions")
            {
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            throw new InvalidOperationException($"Unexpected Twitch request: {request.RequestUri}");
        }));

        await using var client = new TwitchChatClient(new ChatSettings(), new MemoryLogger(), httpClient);
        SetTwitchPredictionContext(client);
        var predictionTask = client.CreatePredictionAsync(
            new TwitchPredictionCreateRequest("Will it work?", ["Yes", "No"], 60));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disposalTask = client.DisposeAsync().AsTask();
        await disposalTask.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<OperationCanceledException>(() => predictionTask);
    }),
    ("isolates throwing chat prediction and EventSub subscribers", async () =>
    {
        var logger = new MemoryLogger();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            }));

        await using var kickClient = new KickChatClient(new ChatSettings(), logger, httpClient);
        var kickMessageCount = 0;
        kickClient.MessageReceived += (_, _) => throw new InvalidOperationException("kick subscriber failed");
        kickClient.MessageReceived += (_, _) => kickMessageCount++;
        var kickRaise = typeof(KickChatClient).GetMethod(
            "EmitKickBackfillMessages",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(kickRaise);
        kickRaise!.Invoke(kickClient, [new[]
        {
            new ChatMessage(PlatformKind.Kick, "channel", "viewer", "hello", DateTimeOffset.UtcNow)
        }]);
        Assert.Equal(1, kickMessageCount);

        await using var twitchClient = new TwitchChatClient(new ChatSettings(), logger, httpClient);
        var statusCount = 0;
        twitchClient.StatusChanged += (_, _) => throw new InvalidOperationException("status subscriber failed");
        twitchClient.StatusChanged += (_, _) => statusCount++;
        var predictionCount = 0;
        twitchClient.PredictionReceived += (_, _) => throw new InvalidOperationException("prediction subscriber failed");
        twitchClient.PredictionReceived += (_, _) => predictionCount++;
        typeof(TwitchChatClient).GetMethod("RaiseStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(twitchClient, ["status"]);
        typeof(TwitchChatClient).GetMethod("RaisePredictionReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(twitchClient, [CreateTestPrediction(
                "prediction-1",
                TwitchPredictionStatus.Active,
                "prediction",
                CreateTestPredictionOutcomes())]);
        Assert.Equal(1, statusCount);
        Assert.Equal(1, predictionCount);

        var eventSubStatusCount = 0;
        var eventSubPredictionCount = 0;
        Action<string> eventSubStatus = _ => throw new InvalidOperationException("EventSub status subscriber failed");
        eventSubStatus += _ => eventSubStatusCount++;
        Action<TwitchPrediction> eventSubPrediction = _ => throw new InvalidOperationException("EventSub prediction subscriber failed");
        eventSubPrediction += _ => eventSubPredictionCount++;
        var eventSub = new TwitchPredictionEventSubClient(
            new TwitchPredictionApiClient(httpClient),
            logger,
            "token",
            "client-id",
            "broadcaster-id",
            eventSubPrediction,
            eventSubStatus);
        typeof(TwitchPredictionEventSubClient)
            .GetMethod("RaiseStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(eventSub, ["eventsub status"]);
        typeof(TwitchPredictionEventSubClient)
            .GetMethod("RaisePredictionReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(eventSub, [CreateTestPrediction(
                "prediction-2",
                TwitchPredictionStatus.Active,
                "eventsub prediction",
                CreateTestPredictionOutcomes())]);
        Assert.Equal(1, eventSubStatusCount);
        Assert.Equal(1, eventSubPredictionCount);
        await eventSub.DisposeAsync();

        Assert.True(logger.Entries.Count(entry => entry.Message.Contains("subscriber threw", StringComparison.Ordinal)) >= 4);
    }),
    ("stops and disposes canceled chat startup before returning", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var chatConnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chatFactory.Client.ConnectHandler = async (_, _, cancellationToken) =>
        {
            chatConnectStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            new MemoryLogger(),
            action => action());

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await chatConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await tab.StopAsync();

        Assert.Equal(false, chatFactory.Client.Connected);
        Assert.Equal(1, chatFactory.Client.DisposeCount);
        await tab.DisposeAsync();
    }),
    ("detects browser Twitch stream URL", () =>
    {
        Assert.True(StreamInputParser.TryParsePlatformUrl("https://www.twitch.tv/xqc?some=value", out var target));
        Assert.NotNull(target);
        Assert.Equal(PlatformKind.Twitch, target!.Platform);
        Assert.Equal("xqc", target.Channel);
        Assert.Equal("https://www.twitch.tv/xqc", target.Url);
        return Task.CompletedTask;
    }),
    ("ignores browser Twitch non-channel URL", () =>
    {
        Assert.Equal(false, StreamInputParser.TryParsePlatformUrl("https://www.twitch.tv/login", out var target));
        Assert.Equal(false, StreamInputParser.TryParsePlatformUrl("https://kick.com/register", out target));
        return Task.CompletedTask;
    }),
    ("ignores browser @-prefixed Twitch non-channel URL", () =>
    {
        Assert.Equal(false, StreamInputParser.TryParsePlatformUrl("https://www.twitch.tv/@videos/123456", out var target));
        Assert.Equal<StreamTarget?>(null, target);
        return Task.CompletedTask;
    }),
    ("parses Twitch VOD URL as a VOD target", () =>
    {
        Assert.True(StreamInputParser.TryParsePlatformUrl("https://www.twitch.tv/videos/123456?t=1h2m", out var target));
        Assert.NotNull(target);
        Assert.Equal(StreamTargetKind.TwitchVod, target!.Kind);
        Assert.Equal(PlatformKind.Twitch, target.Platform);
        Assert.Equal("123456", target.MediaId);
        Assert.Equal("https://www.twitch.tv/videos/123456", target.Url);

        var parsed = StreamInputParser.Parse("twitch.tv/videos/654321", PlatformKind.Kick);
        Assert.Equal(StreamTargetKind.TwitchVod, parsed.Kind);
        Assert.Equal("654321", parsed.MediaId);

        var candidates = StreamInputParser.ParseCandidates("https://www.twitch.tv/videos/123456");
        Assert.Equal(1, candidates.Count);
        Assert.Equal(StreamTargetKind.TwitchVod, candidates[0].Kind);

        Assert.True(StreamInputParser.TryParseTwitchVodUrl("https://www.twitch.tv/videos/123456", out _));
        Assert.Equal(false, StreamInputParser.TryParseTwitchVodUrl("https://www.twitch.tv/xqc", out _));
        Assert.Equal(false, StreamInputParser.TryParseTwitchVodUrl("https://www.twitch.tv/@videos/123456", out _));
        Assert.Equal(false, StreamInputParser.TryParseTwitchVodUrl("xqc", out _));
        return Task.CompletedTask;
    }),
    ("home search opens a pasted Twitch VOD URL as a playable VOD result", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        var viewerCountService = new FakeViewerCountService();
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            viewerCountService: viewerCountService,
            streamSearchDebounceInterval: TimeSpan.Zero);

        viewModel.NewStreamText = "https://www.twitch.tv/videos/123456";

        await TestWait.UntilAsync(
            () => viewModel.StreamSearchResults.Count == 1,
            TimeSpan.FromMilliseconds(500));

        var result = viewModel.StreamSearchResults[0];
        Assert.Equal(StreamTargetKind.TwitchVod, result.Target.Kind);
        Assert.Equal("123456", result.Target.MediaId);
        Assert.Equal(true, result.CanPlay);
        Assert.Equal("Twitch VOD", result.StatusText);
        Assert.Equal(0, viewerCountService.CallCount);
    }),
    ("sub-only VOD storyboard location parses host and special id", () =>
    {
        Assert.True(TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation(
            "https://d2e2de1etea730.cloudfront.net/abc123_def456_789/storyboards/0.jpg",
            out var host,
            out var specialId));
        Assert.Equal("d2e2de1etea730.cloudfront.net", host);
        Assert.Equal("abc123_def456_789", specialId);
        return Task.CompletedTask;
    }),
    ("sub-only VOD storyboard location rejects invalid input", () =>
    {
        Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation("", out _, out _));
        Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation(null, out _, out _));
        Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation("not a url", out _, out _));
        Assert.Equal(false, TwitchSubOnlyVodPlaylist.TryParseStoryboardLocation("https://cdn.example.com/storyboards/0.jpg", out _, out _));
        return Task.CompletedTask;
    }),
    ("sub-only VOD variant URL shapes follow broadcast type", () =>
    {
        var created = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var cutoff = new DateTimeOffset(2023, 2, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            "https://cdn.example.com/special/chunked/index-dvr.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("ARCHIVE", created, cutoff, "cdn.example.com", "special", "streamer", "123", "chunked"));
        Assert.Equal(
            "https://cdn.example.com/special/720p60/highlight-123.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("highlight", created, cutoff, "cdn.example.com", "special", "streamer", "123", "720p60"));
        Assert.Equal(
            "https://cdn.example.com/streamer/123/special/480p30/index-dvr.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("upload", created, cutoff, "cdn.example.com", "special", "streamer", "123", "480p30"));
        var recentUpload = new DateTimeOffset(2023, 2, 9, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            "https://cdn.example.com/special/480p30/index-dvr.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("upload", recentUpload, cutoff, "cdn.example.com", "special", "streamer", "123", "480p30"));
        Assert.Equal(
            "https://cdn.example.com/special/480p30/index-dvr.m3u8",
            TwitchSubOnlyVodPlaylist.BuildVariantPlaylistUrl("upload", DateTimeOffset.MinValue, cutoff, "cdn.example.com", "special", "streamer", "123", "480p30"));
        return Task.CompletedTask;
    }),
    ("sub-only VOD quality selection maps app qualities", () =>
    {
        var all = new[] { "chunked", "1080p60", "720p60", "480p30", "360p30", "160p30" };
        Assert.Equal("chunked", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "best"));
        Assert.Equal("chunked", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "source"));
        Assert.Equal("1080p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "1080p"));
        Assert.Equal("1080p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "1080p60"));
        Assert.Equal("720p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "720p60"));
        Assert.Equal("720p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "720p"));
        Assert.Equal("480p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "480p"));
        Assert.Equal("160p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "worst"));
        Assert.Equal("160p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(all, "audio_only"));

        var sparse = new[] { "chunked", "720p60", "360p30" };
        Assert.Equal("720p60", TwitchSubOnlyVodPlaylist.SelectQualityKey(sparse, "1080p"));
        Assert.Equal("360p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(sparse, "480p"));
        Assert.Equal("chunked", TwitchSubOnlyVodPlaylist.SelectQualityKey(sparse, "best"));
        Assert.Equal("360p30", TwitchSubOnlyVodPlaylist.SelectQualityKey(sparse, "worst"));
        return Task.CompletedTask;
    }),
    ("sub-only VOD playlist rewrite mutes and absolutizes", () =>
    {
        var playlist = "#EXTM3U\n" +
            "#EXT-X-TARGETDURATION:10\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n" +
            "#EXTINF:10.000,\n" +
            "0-unmuted.ts\n" +
            "#EXTINF:10.000,\n" +
            "https://d111111abcdef8.cloudfront.net/already/absolute.ts\n";
        var rewritten = TwitchSubOnlyVodPlaylist.RewriteMediaPlaylist(
            playlist,
            new Uri("https://d2e2de1etea730.cloudfront.net/special/chunked/index-dvr.m3u8"));
        Assert.Contains("https://d2e2de1etea730.cloudfront.net/special/chunked/0-muted.ts", rewritten);
        Assert.DoesNotContain("-unmuted", rewritten);
        Assert.Contains("URI=\"https://d2e2de1etea730.cloudfront.net/special/chunked/key.bin\"", rewritten);
        Assert.Contains("https://d111111abcdef8.cloudfront.net/already/absolute.ts", rewritten);
        return Task.CompletedTask;
    }),
    ("sub-only VOD resolver builds direct playlist from storyboard metadata", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"ARCHIVE\",\"createdAt\":\"2023-01-01T00:00:00Z\",\"lengthSeconds\":3600,\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url == "https://gql.twitch.tv/gql")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) };
            }

            if (url.Contains("/chunked/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:10.0,\n0-unmuted.ts\n") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(handler);
            var resolver = new TwitchSubOnlyVodResolver(
                new MemoryLogger(), httpClient, tempDir, TestReplayUrlSecurity.PublicValidator);

            var resolution = await resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best"));

            Assert.Equal("chunked", resolution.QualityKey);
            Assert.Equal(TimeSpan.FromHours(1), resolution.MediaDuration);
            Assert.Equal("streamer", resolution.OwnerLogin);
            Assert.Equal(new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), resolution.CreatedAtUtc);
            Assert.Equal(new Uri(Path.Combine(tempDir, "123456-chunked.m3u8")), resolution.PlaybackUri);
            var playlist = await File.ReadAllTextAsync(resolution.PlaybackUri.LocalPath);
            Assert.Contains("https://d2e2de1etea730.cloudfront.net/abc_def/chunked/0-muted.ts", playlist);
            Assert.DoesNotContain("-unmuted", playlist);
            var playlistBytes = await File.ReadAllBytesAsync(resolution.PlaybackUri.LocalPath);
            Assert.Equal((byte)'#', playlistBytes[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }),
    ("sub-only VOD resolver selects the requested quality among valid variants", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"ARCHIVE\",\"createdAt\":\"2023-01-01T00:00:00Z\",\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url == "https://gql.twitch.tv/gql")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) };
            }

            if (url.Contains("/720p60/", StringComparison.Ordinal) || url.Contains("/360p30/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:10.0,\n0.ts\n") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(handler);
            var resolver = new TwitchSubOnlyVodResolver(
                new MemoryLogger(), httpClient, tempDir, TestReplayUrlSecurity.PublicValidator);

            var resolution = await resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best"));

            Assert.Equal("720p60", resolution.QualityKey);
            Assert.True(File.Exists(resolution.PlaybackUri.LocalPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }),
    ("sub-only VOD resolver uses the upload URL shape for old uploads", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"UPLOAD\",\"createdAt\":\"2020-01-01T00:00:00Z\",\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            requestedUrls.Add(url);
            if (url == "https://gql.twitch.tv/gql")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:10.0,\n0.ts\n") };
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(handler);
            var resolver = new TwitchSubOnlyVodResolver(
                new MemoryLogger(), httpClient, tempDir, TestReplayUrlSecurity.PublicValidator);

            await resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "worst"));

            Assert.True(requestedUrls.Contains("https://d2e2de1etea730.cloudfront.net/streamer/123456/abc_def/chunked/index-dvr.m3u8"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }),
    ("sub-only VOD resolver reports a missing video", async () =>
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"video\":null}}") });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(handler);
        var resolver = new TwitchSubOnlyVodResolver(
            new MemoryLogger(), httpClient, tempDir, TestReplayUrlSecurity.PublicValidator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best")));
        Assert.Contains("not found", error.Message);
    }),
    ("sub-only VOD resolver errors when no variants exist", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"ARCHIVE\",\"createdAt\":\"2023-01-01T00:00:00Z\",\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            return url == "https://gql.twitch.tv/gql"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(handler);
        var resolver = new TwitchSubOnlyVodResolver(
            new MemoryLogger(), httpClient, tempDir, TestReplayUrlSecurity.PublicValidator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best")));
        Assert.Contains("No playable qualities", error.Message);
    }),
    ("sub-only VOD resolver treats a timed out variant probe as unavailable", async () =>
    {
        var gqlJson = "{\"data\":{\"video\":{\"broadcastType\":\"ARCHIVE\",\"createdAt\":\"2023-01-01T00:00:00Z\",\"seekPreviewsURL\":\"https://d2e2de1etea730.cloudfront.net/abc_def/storyboards/0.jpg\",\"owner\":{\"login\":\"streamer\"}}}}";
        var handler = new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url == "https://gql.twitch.tv/gql")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(gqlJson) };
            }

            if (url.Contains("/chunked/", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing.");
            }

            if (url.Contains("/720p60/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("#EXTM3U\n#EXTINF:10.0,\n0.ts\n") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        try
        {
            using var httpClient = new HttpClient(handler);
            var resolver = new TwitchSubOnlyVodResolver(
                new MemoryLogger(), httpClient, tempDir, TestReplayUrlSecurity.PublicValidator);

            var resolution = await resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best"));

            Assert.Equal("720p60", resolution.QualityKey);
            Assert.True(File.Exists(resolution.PlaybackUri.LocalPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }),
    ("sub-only VOD resolver surfaces the API message on GraphQL failure", async () =>
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"Invalid query\"}") });
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(handler);
        var resolver = new TwitchSubOnlyVodResolver(
            new MemoryLogger(), httpClient, tempDir, TestReplayUrlSecurity.PublicValidator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new TwitchSubOnlyVodRequest("123456", "best")));
        Assert.Contains("Invalid query", error.Message);
    }),
    ("sub-only VOD resolver rejects a non-numeric VOD id", async () =>
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("HTTP must not be called."));
        var tempDir = Path.Combine(Path.GetTempPath(), $"svs-subvod-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(handler);
        var resolver = new TwitchSubOnlyVodResolver(
            new MemoryLogger(), httpClient, tempDir, TestReplayUrlSecurity.PublicValidator);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(new TwitchSubOnlyVodRequest("not-a-vod", "best")));
    }),
    ("Twitch VOD playback falls back to the sub-only resolver when Streamlink fails", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("error: This video is only available to subscribers")
        };
        var playbackFactory = new FakePlaybackEngineFactory();
        var bypassUri = new Uri(@"C:\fake\sub-only-123.m3u8");
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver
        {
            Override = (_, _) => Task.FromResult(new TwitchSubOnlyVodResolution(bypassUri, "720p60", "Resolved."))
        };
        var tab = TestViewModels.CreateTab(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123", StreamTargetKind.TwitchVod, "123"),
            "720p60",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(1, subOnlyResolver.Requests.Count);
        Assert.Equal("123", subOnlyResolver.Requests[0].VodId);
        Assert.Equal("720p60", subOnlyResolver.Requests[0].Quality);
        Assert.Equal(bypassUri, playbackFactory.Engine!.LastPlayedUri);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        await tab.DisposeAsync();
    }),
    ("sub-only Twitch VOD fallback initializes replay chat from resolver metadata", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException(
                "error: This video is only available to subscribers")
        };
        var replayChatProvider = new FakeReplayChatProvider(ReplayChatLoadResult.Available(
        [
            new ReplayChatMessage(
                TimeSpan.Zero,
                new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    "viewer",
                    "sub-only replay chat",
                    DateTimeOffset.UtcNow))
        ],
        TimeSpan.Zero,
        TimeSpan.FromHours(1)));
        var createdAt = new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.Zero);
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver
        {
            Override = (_, _) => Task.FromResult(
                new TwitchSubOnlyVodResolution(
                    new Uri(@"C:\fake\sub-only-chat.m3u8"),
                    "chunked",
                    "Resolved.",
                    MediaDuration: TimeSpan.FromHours(1),
                    OwnerLogin: "streamer",
                    CreatedAtUtc: createdAt))
        };
        var target = new StreamTarget(
            PlatformKind.Twitch,
            "123456",
            "https://www.twitch.tv/videos/123456",
            StreamTargetKind.TwitchVod,
            "123456",
            "Sub-only VOD");
        var tab = TestViewModels.CreateTab(
            target,
            "best",
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            replayChatProvider: replayChatProvider,
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => replayChatProvider.CallCount > 0,
            TimeSpan.FromSeconds(1));

        Assert.Equal(1, subOnlyResolver.Requests.Count);
        Assert.Equal(1, replayChatProvider.CallCount);
        Assert.Equal("123456", replayChatProvider.Requests[0].ReplayId);
        Assert.Equal("streamer", replayChatProvider.Requests[0].Channel);
        Assert.Equal(TimeSpan.FromHours(1), replayChatProvider.Requests[0].Duration);
        Assert.True(tab.IsReplaySeekEnabled);

        await tab.DisposeAsync();
    }),
    ("Kick VOD playback does not use the sub-only resolver", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("streamlink failed")
        };
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver();
        var tab = TestViewModels.CreateTab(
            new StreamTarget(PlatformKind.Kick, "streamer", "https://kick.com/streamer/videos/abc", StreamTargetKind.KickVod, "abc"),
            "best",
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(0, subOnlyResolver.Requests.Count);
        Assert.Equal(PlaybackStatus.Error, tab.Status);
        Assert.Contains("streamlink failed", tab.ErrorMessage);
        await tab.DisposeAsync();
    }),
    ("sub-only VOD fallback error includes Streamlink and fallback messages", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("streamlink says no")
        };
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver
        {
            Override = (_, _) => throw new InvalidOperationException("no qualities")
        };
        var tab = TestViewModels.CreateTab(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123", StreamTargetKind.TwitchVod, "123"),
            "best",
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(PlaybackStatus.Error, tab.Status);
        Assert.Contains("streamlink says no", tab.ErrorMessage);
        Assert.Contains("no qualities", tab.ErrorMessage);
        await tab.DisposeAsync();
    }),
    ("sub-only VOD fallback is not used for cancellations", async () =>
    {
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => throw new OperationCanceledException()
        };
        var subOnlyResolver = new FakeTwitchSubOnlyVodResolver();
        var tab = TestViewModels.CreateTab(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123", StreamTargetKind.TwitchVod, "123"),
            "best",
            streamlink,
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            twitchSubOnlyVodResolver: subOnlyResolver);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(0, subOnlyResolver.Requests.Count);
        Assert.Equal(PlaybackStatus.Error, tab.Status);
        await tab.DisposeAsync();
    }),
    ("reads extension capture payload URL", () =>
    {
        Assert.True(BrowserCaptureServer.TryReadCaptureUrl("""{"url":" https://www.twitch.tv/xqc "}""", out var url));
        Assert.Equal("https://www.twitch.tv/xqc", url);
        return Task.CompletedTask;
    }),
    ("browser capture accepts multiple requests before handlers finish", async () =>
    {
        var logger = new MemoryLogger();
        var firstHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHandlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandlers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handledUrls = new List<string>();
        var handledUrlsGate = new object();

        await using var server = new BrowserCaptureServer(async url =>
        {
            int count;
            lock (handledUrlsGate)
            {
                handledUrls.Add(url);
                count = handledUrls.Count;
            }

            if (count == 1)
            {
                firstHandlerStarted.TrySetResult();
            }
            else if (count == 2)
            {
                secondHandlerStarted.TrySetResult();
            }

            await releaseHandlers.Task;
        }, logger, port: 0);

        Assert.True(server.Start());
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        try
        {
            var firstResponseTask = BrowserCaptureTestClient.PostCaptureAsync(httpClient, server.ListenerPort, "https://www.twitch.tv/xqc");
            await firstHandlerStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

            var secondResponseTask = BrowserCaptureTestClient.PostCaptureAsync(httpClient, server.ListenerPort, "https://www.twitch.tv/summit1g");
            await secondHandlerStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

            using var firstResponse = await firstResponseTask.WaitAsync(TimeSpan.FromMilliseconds(500));
            using var secondResponse = await secondResponseTask.WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
            Assert.SequenceEqual(
                new[] { "https://www.twitch.tv/xqc", "https://www.twitch.tv/summit1g" },
                handledUrls);
        }
        finally
        {
            releaseHandlers.TrySetResult();
        }
    }),
    ("browser capture disposal drains active handlers and prevents restart", async () =>
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new BrowserCaptureServer(async _ =>
        {
            handlerStarted.TrySetResult();
            await releaseHandler.Task;
        }, new MemoryLogger(), port: 0);

        Assert.True(server.Start());
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        Task disposalTask = Task.CompletedTask;
        try
        {
            using var response = await BrowserCaptureTestClient.PostCaptureAsync(
                httpClient,
                server.ListenerPort,
                "https://www.twitch.tv/xqc");
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            disposalTask = server.DisposeAsync().AsTask();
            await Task.Delay(50);
            Assert.Equal(false, disposalTask.IsCompleted);

            releaseHandler.TrySetResult();
            await disposalTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(false, server.Start());
        }
        finally
        {
            releaseHandler.TrySetResult();
            await disposalTask;
        }
    }),
    ("browser capture accepts extension origins and returns scoped CORS headers", async () =>
    {
        var handledUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new BrowserCaptureServer(
            url =>
            {
                handledUrl.TrySetResult(url);
                return Task.CompletedTask;
            },
            new MemoryLogger(),
            port: 0);

        Assert.True(server.Start());
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var request = BrowserCaptureTestClient.CreatePostRequest(
            server.ListenerPort,
            "https://www.twitch.tv/xqc",
            "chrome-extension://abcdefghijklmnop");
        using var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.SequenceEqual(
            new[] { "chrome-extension://abcdefghijklmnop" },
            response.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.True(response.Headers.Vary.Contains("Origin", StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            "https://www.twitch.tv/xqc",
            await handledUrl.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }),
    ("browser capture rejects webpage origins before dispatch", async () =>
    {
        var dispatchCount = 0;
        await using var server = new BrowserCaptureServer(
            _ =>
            {
                Interlocked.Increment(ref dispatchCount);
                return Task.CompletedTask;
            },
            new MemoryLogger(),
            port: 0);

        Assert.True(server.Start());
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var request = BrowserCaptureTestClient.CreatePostRequest(
            server.ListenerPort,
            "https://www.twitch.tv/xqc",
            "https://malicious.example");
        using var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(false, response.Headers.Contains("Access-Control-Allow-Origin"));
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref dispatchCount));
    }),
    ("browser capture rejects malformed and duplicate Origin headers", async () =>
    {
        Assert.True(BrowserCaptureServer.IsAllowedRequestOrigin(null));
        Assert.True(BrowserCaptureServer.IsAllowedRequestOrigin("moz-extension://extension-id"));
        Assert.Equal(false, BrowserCaptureServer.IsAllowedRequestOrigin("null"));
        Assert.Equal(false, BrowserCaptureServer.IsAllowedRequestOrigin("chrome-extension://extension-id/path"));
        Assert.Equal(false, BrowserCaptureServer.IsAllowedRequestOrigin("https://www.twitch.tv"));

        await using var server = new BrowserCaptureServer(_ => Task.CompletedTask, new MemoryLogger(), port: 0);
        Assert.True(server.Start());
        var response = await BrowserCaptureTestClient.SendRawRequestAsync(
            server.ListenerPort,
            "POST /capture HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.ListenerPort}\r\n" +
            "Origin: chrome-extension://first-extension\r\n" +
            "Origin: chrome-extension://second-extension\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: 30\r\n" +
            "Connection: close\r\n\r\n" +
            "{\"url\":\"https://kick.com/xqc\"}");

        Assert.True(response.StartsWith("HTTP/1.1 400 Bad Request", StringComparison.Ordinal));

        var malformedContentLengthResponse = await BrowserCaptureTestClient.SendRawRequestAsync(
            server.ListenerPort,
            "POST /capture HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.ListenerPort}\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: invalid\r\n" +
            "Connection: close\r\n\r\n" +
            "{\"url\":\"https://kick.com/xqc\"}");

        Assert.True(malformedContentLengthResponse.StartsWith("HTTP/1.1 400 Bad Request", StringComparison.Ordinal));
    }),
    ("browser capture validates canonical live URLs and HTTP framing", async () =>
    {
        var dispatchCount = 0;
        await using var server = new BrowserCaptureServer(
            _ =>
            {
                Interlocked.Increment(ref dispatchCount);
                return Task.CompletedTask;
            },
            new MemoryLogger(),
            port: 0);
        Assert.True(server.Start());

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        foreach (var url in new[]
        {
            "https://www.twitch.tv/videos/123456",
            "https://www.twitch.tv/xqc?from=home",
            " https://www.twitch.tv/xqc ",
            "https://example.com/xqc",
            "http://www.twitch.tv/xqc"
        })
        {
            using var response = await BrowserCaptureTestClient.PostCaptureAsync(httpClient, server.ListenerPort, url);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var malformedJsonResponse = await BrowserCaptureTestClient.SendRawRequestAsync(
            server.ListenerPort,
            "POST /capture HTTP/1.1\r\n" +
            "Content-Length: 5\r\n" +
            "Connection: close\r\n\r\n" +
            "oops!");
        Assert.True(malformedJsonResponse.StartsWith("HTTP/1.1 400 Bad Request", StringComparison.Ordinal));

        var transferEncodingResponse = await BrowserCaptureTestClient.SendRawRequestAsync(
            server.ListenerPort,
            "POST /capture HTTP/1.1\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n");
        Assert.True(transferEncodingResponse.StartsWith("HTTP/1.1 501 Not Implemented", StringComparison.Ordinal));

        var oversizedResponse = await BrowserCaptureTestClient.SendRawRequestAsync(
            server.ListenerPort,
            "POST /capture HTTP/1.1\r\n" +
            "Content-Length: 20000\r\n" +
            "Connection: close\r\n\r\n");
        Assert.True(oversizedResponse.StartsWith("HTTP/1.1 413 Payload Too Large", StringComparison.Ordinal));

        Assert.Equal(0, Volatile.Read(ref dispatchCount));
    }),
    ("normalizes out-of-range browser capture ports", async () =>
    {
        await using var server = new BrowserCaptureServer(_ => Task.CompletedTask, new MemoryLogger(), port: 65_536);
        var requestedPortField = typeof(BrowserCaptureServer).GetField(
            "requestedPort",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(requestedPortField);
        Assert.Equal(BrowserCaptureServer.Port, (int)requestedPortField!.GetValue(server)!);
        Assert.Equal(BrowserCaptureServer.Port, server.ListenerPort);
    }),
    ("low-level mouse hook ignores mouse move without active drag", () =>
    {
        var routeCount = 0;
        var router = new LowLevelMouseHookDispatcher(
            System.Windows.Threading.Dispatcher.CurrentDispatcher,
            _ =>
            {
                routeCount++;
                return true;
            },
            () => false,
            TimeSpan.FromMilliseconds(1));

        var suppress = router.ProcessEvent(new LowLevelMouseHookEvent(
            LowLevelMouseHookEvent.WmMouseMove,
            10,
            20,
            0));

        Assert.Equal(false, suppress);
        Assert.Equal(0, routeCount);
        return Task.CompletedTask;
    }),
    ("low-level mouse hook falls through when UI routing is busy", async () =>
    {
        var dispatcherReady = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseDispatcherThread = new ManualResetEventSlim();
        var routeCount = 0;
        var dispatcherThread = new Thread(() =>
        {
            dispatcherReady.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            releaseDispatcherThread.Wait();
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();

        try
        {
            var dispatcher = await dispatcherReady.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            var router = new LowLevelMouseHookDispatcher(
                dispatcher,
                _ =>
                {
                    routeCount++;
                    return true;
                },
                () => false,
                TimeSpan.FromMilliseconds(20));

            var suppress = router.ProcessEvent(new LowLevelMouseHookEvent(
                LowLevelMouseHookEvent.WmMouseWheel,
                10,
                20,
                Mouse.MouseWheelDeltaForOneLine << 16));

            Assert.Equal(false, suppress);
            Assert.Equal(0, routeCount);
        }
        finally
        {
            releaseDispatcherThread.Set();
            Assert.True(dispatcherThread.Join(TimeSpan.FromSeconds(1)));
        }
    }),
    ("low-level mouse hook preserves drag input while UI routing is busy", async () =>
    {
        var dispatcherReady = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var routedInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dispatcherBlocked = new ManualResetEventSlim();
        using var releaseDispatcher = new ManualResetEventSlim();
        var routedMessages = new List<int>();
        var dispatcherThread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(
                new Action(() =>
                {
                    dispatcherBlocked.Set();
                    releaseDispatcher.Wait();
                }),
                System.Windows.Threading.DispatcherPriority.Send);
            dispatcherReady.SetResult(dispatcher);
            System.Windows.Threading.Dispatcher.Run();
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();

        System.Windows.Threading.Dispatcher? dispatcher = null;
        try
        {
            dispatcher = await dispatcherReady.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.True(dispatcherBlocked.Wait(TimeSpan.FromMilliseconds(500)));
            var router = new LowLevelMouseHookDispatcher(
                dispatcher,
                hookEvent =>
                {
                    routedMessages.Add(hookEvent.Message);
                    if (routedMessages.Count == 2)
                    {
                        routedInput.TrySetResult();
                    }

                    return false;
                },
                () => false,
                TimeSpan.FromMilliseconds(20));

            Assert.Equal(false, router.ProcessEvent(new LowLevelMouseHookEvent(
                LowLevelMouseHookEvent.WmLeftButtonDown,
                10,
                20,
                0)));
            Assert.Equal(false, router.ProcessEvent(new LowLevelMouseHookEvent(
                LowLevelMouseHookEvent.WmMouseMove,
                20,
                30,
                0)));

            releaseDispatcher.Set();
            await routedInput.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(false, router.ProcessEvent(new LowLevelMouseHookEvent(
                LowLevelMouseHookEvent.WmLeftButtonUp,
                20,
                30,
                0)));
            Assert.SequenceEqual(
                new[]
                {
                    LowLevelMouseHookEvent.WmLeftButtonDown,
                    LowLevelMouseHookEvent.WmMouseMove,
                    LowLevelMouseHookEvent.WmLeftButtonUp
                },
                routedMessages);
        }
        finally
        {
            releaseDispatcher.Set();
            dispatcher?.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Send);
            Assert.True(dispatcherThread.Join(TimeSpan.FromSeconds(1)));
        }
    }),
    ("uses default platform for bare channel", () =>
    {
        var target = StreamInputParser.Parse("summit1g", PlatformKind.Twitch);
        Assert.Equal(PlatformKind.Twitch, target.Platform);
        Assert.Equal("summit1g", target.Channel);
        return Task.CompletedTask;
    }),
    ("formats docked chat header for selected channel", () =>
    {
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("summit1g", PlatformKind.Twitch),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());

        Assert.Equal("Chat in summit1g's channel", tab.DockedChatHeaderText);
        return Task.CompletedTask;
    }),
    ("tab strip shows stream category when available", () =>
    {
        var tab = TestViewModels.CreateTab(
            new StreamTarget(
                PlatformKind.Twitch,
                "summit1g",
                "https://www.twitch.tv/summit1g",
                CategoryName: "Just Chatting",
                ProfileImageUrl: "https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png"),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());
        var item = new TabStripItemViewModel([tab], tab);

        Assert.Equal("summit1g", item.Title);
        Assert.Equal("Just Chatting", item.SubtitleText);
        Assert.Equal("https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png", tab.ProfileImageUrl);
        Assert.Equal("https://static-cdn.jtvnw.net/jtv_user_pictures/summit-profile.png", item.ProfileImageUrl);
        Assert.True(item.HasProfileImage);
        Assert.Contains("Category: Just Chatting", item.ToolTip);
        Assert.Contains("Status: Ready", item.ToolTip);
        item.Dispose();
        return tab.DisposeAsync().AsTask();
    }),
    ("tab strip follows a mid-stream category change from the live channel poll", async () =>
    {
        var viewerCountService = new FakeViewerCountService
        {
            Responder = _ => new ViewerCountResult(
                ViewerCountState.Available,
                4321,
                "viewer count updated",
                CategoryName: "Grand Theft Auto V",
                StreamTitle: "Late-night ranked grind")
        };
        var tab = TestViewModels.CreateTab(
            new StreamTarget(
                PlatformKind.Twitch,
                "summit1g",
                "https://www.twitch.tv/summit1g",
                CategoryName: "Just Chatting"),
            "best",
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            viewerCountService: viewerCountService);
        var item = new TabStripItemViewModel([tab], tab);
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC"
        };
        settings.Chat.ConnectAutomatically = false;
        tab.SetVideoHandle(new IntPtr(42));

        Assert.Equal("Just Chatting", tab.CategoryName);
        Assert.Equal("Just Chatting", item.SubtitleText);

        await tab.StartAsync(settings);
        await TestWait.UntilAsync(
            () => tab.CategoryName == "Grand Theft Auto V",
            TimeSpan.FromSeconds(1));

        Assert.True(tab.HasCategory);
        Assert.Equal("Grand Theft Auto V", item.SubtitleText);
        Assert.Contains("Title: Late-night ranked grind", item.ToolTip);
        Assert.Contains("Category: Grand Theft Auto V", item.ToolTip);

        var applyViewerCountResult = typeof(StreamTabViewModel).GetMethod(
            "ApplyViewerCountResult",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyViewerCountResult);

        // A poll that could not read the channel must not blank out the last known category.
        applyViewerCountResult!.Invoke(
            tab,
            [new ViewerCountResult(ViewerCountState.Unavailable, null, "unavailable")]);

        Assert.Equal("Grand Theft Auto V", tab.CategoryName);
        Assert.Equal("Grand Theft Auto V", item.SubtitleText);

        // A successful poll that reports no category means the streamer cleared it.
        applyViewerCountResult.Invoke(
            tab,
            [new ViewerCountResult(ViewerCountState.Available, 4321, "viewer count updated")]);

        Assert.Equal("", tab.CategoryName);
        Assert.Equal(false, tab.HasCategory);
        Assert.Equal("Live", item.SubtitleText);
        Assert.DoesNotContain("Category:", item.ToolTip);

        item.Dispose();
        await tab.DisposeAsync();
    }),
    ("creates Twitch and Kick candidates for bare channel", () =>
    {
        var candidates = StreamInputParser.ParseCandidates("summit1g");
        Assert.Equal(2, candidates.Count);
        Assert.Equal(PlatformKind.Twitch, candidates[0].Platform);
        Assert.Equal("https://www.twitch.tv/summit1g", candidates[0].Url);
        Assert.Equal(PlatformKind.Kick, candidates[1].Platform);
        Assert.Equal("https://kick.com/summit1g", candidates[1].Url);
        return Task.CompletedTask;
    }),
    ("creates one candidate for platform URL", () =>
    {
        var candidates = StreamInputParser.ParseCandidates("https://kick.com/some-channel");
        Assert.Equal(1, candidates.Count);
        Assert.Equal(PlatformKind.Kick, candidates[0].Platform);
        Assert.Equal("some-channel", candidates[0].Channel);
        return Task.CompletedTask;
    }),
    ("treats Twitch-invalid bare names as Kick candidates", () =>
    {
        var candidates = StreamInputParser.ParseCandidates("some-channel");
        Assert.Equal(1, candidates.Count);
        Assert.Equal(PlatformKind.Kick, candidates[0].Platform);
        Assert.Equal("https://kick.com/some-channel", candidates[0].Url);
        return Task.CompletedTask;
    }),
    ("rejects unsupported URLs", () =>
    {
        Assert.Throws<ArgumentException>(() => StreamInputParser.Parse("https://example.com/channel", PlatformKind.Twitch));
        return Task.CompletedTask;
    }),
    ("rejects platform URLs that point to known non-channel pages", () =>
    {
        Assert.Throws<ArgumentException>(() => StreamInputParser.Parse("https://www.twitch.tv/@videos/123456", PlatformKind.Twitch));
        Assert.Throws<ArgumentException>(() => StreamInputParser.Parse("https://www.twitch.tv/xqc/videos", PlatformKind.Twitch));
        Assert.Throws<ArgumentException>(() => StreamInputParser.Parse("https://kick.com/xqc/clips", PlatformKind.Kick));
        Assert.Throws<ArgumentException>(() => StreamInputParser.ParseCandidates("https://kick.com/@search"));
        return Task.CompletedTask;
    }),
    ("rejects bare platform routes and dot segments as channels", () =>
    {
        Assert.Throws<ArgumentException>(() => StreamInputParser.Parse("videos", PlatformKind.Twitch));
        Assert.Throws<ArgumentException>(() => StreamInputParser.Parse("search", PlatformKind.Kick));
        Assert.Throws<ArgumentException>(() => StreamInputParser.FromChannel(PlatformKind.Kick, "."));
        Assert.Throws<ArgumentException>(() => StreamInputParser.FromChannel(PlatformKind.Kick, ".."));
        return Task.CompletedTask;
    }),
    ("tokenizes custom Streamlink arguments", () =>
    {
        var tokens = CommandLineTokenizer.Tokenize("--http-header \"Client-ID=abc 123\" --retry-open 5");
        Assert.SequenceEqual(new[] { "--http-header", "Client-ID=abc 123", "--retry-open", "5" }, tokens);
        return Task.CompletedTask;
    }),
    ("preserves Windows paths in custom Streamlink arguments", () =>
    {
        var tokens = CommandLineTokenizer.Tokenize("--config C:\\Users\\me\\streamlink\\config --player \"C:\\Program Files\\VideoLAN\\VLC\\vlc.exe\"");
        Assert.SequenceEqual(
            new[] { "--config", "C:\\Users\\me\\streamlink\\config", "--player", "C:\\Program Files\\VideoLAN\\VLC\\vlc.exe" },
            tokens);
        return Task.CompletedTask;
    }),
    ("supports escaped quotes in custom Streamlink arguments", () =>
    {
        var tokens = CommandLineTokenizer.Tokenize("--http-header \"X-Name=hello \\\"chat\\\"\"");
        Assert.SequenceEqual(new[] { "--http-header", "X-Name=hello \"chat\"" }, tokens);
        return Task.CompletedTask;
    }),
    ("preserves empty quoted custom Streamlink arguments", () =>
    {
        var tokens = CommandLineTokenizer.Tokenize("--http-header \"\" --retry-open 5");
        Assert.SequenceEqual(new[] { "--http-header", "", "--retry-open", "5" }, tokens);
        return Task.CompletedTask;
    }),
    ("accepts unclosed quotes like CommandLineToArgvW", () =>
    {
        var tokens = CommandLineTokenizer.Tokenize("--http-header \"broken");
        Assert.SequenceEqual(new[] { "--http-header", "broken" }, tokens);
        return Task.CompletedTask;
    }),
    ("stream playback startup does not wait for unavailable streams", () =>
    {
        var method = typeof(StreamlinkService).GetMethod(
            "BuildArguments",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var request = new StreamTransportRequest(
            StreamInputParser.Parse("https://kick.com/80", PlatformKind.Twitch),
            "best",
            "streamlink.exe",
            LowLatency: true,
            CustomArguments: []);
        var arguments = ((IEnumerable<string>)method!.Invoke(null, [request])!).ToArray();

        Assert.Equal(false, arguments.Contains("--retry-streams"));
        Assert.True(arguments.Contains("--retry-open"));
        return Task.CompletedTask;
    }),
    ("stream playback startup uses bounded buffering and H264 for Twitch low latency", () =>
    {
        var method = typeof(StreamlinkService).GetMethod(
            "BuildArguments",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var request = new StreamTransportRequest(
            StreamInputParser.Parse("https://www.twitch.tv/albralelie", PlatformKind.Kick),
            "best",
            "streamlink.exe",
            LowLatency: true,
            CustomArguments: []);
        var arguments = ((IEnumerable<string>)method!.Invoke(null, [request])!).ToArray();

        AssertOptionValue(arguments, "--ringbuffer-size", "32M");
        Assert.Equal("best", arguments[^1]);
        AssertOptionValue(arguments, "--twitch-supported-codecs", "h264");
        Assert.True(arguments.Contains("--twitch-low-latency"));
        Assert.Equal(false, arguments.Contains("h265"));
        Assert.Equal(false, arguments.Contains("av1"));
        return Task.CompletedTask;
    }),
    ];
}
