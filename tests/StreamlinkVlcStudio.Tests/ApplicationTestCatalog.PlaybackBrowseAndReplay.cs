internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> PlaybackBrowseAndReplay { get; } =
    [
    ("multi-stream playback uses a smaller buffer without changing quality", () =>
    {
        var method = typeof(StreamlinkService).GetMethod(
            "BuildArguments",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var request = new StreamTransportRequest(
            StreamInputParser.Parse("https://www.twitch.tv/albralelie", PlatformKind.Twitch),
            "source",
            "streamlink.exe",
            LowLatency: true,
            CustomArguments: [],
            IsMultiStream: true);
        var arguments = ((IEnumerable<string>)method!.Invoke(null, [request])!).ToArray();

        AssertOptionValue(arguments, "--ringbuffer-size", "16M");
        Assert.Equal("source", arguments[^1]);
        return Task.CompletedTask;
    }),
    ("streamlink direct URL arguments place URL and quality last", () =>
    {
        var method = typeof(StreamlinkService).GetMethod(
            "BuildStreamUrlArguments",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var request = new StreamTransportRequest(
            new StreamTarget(PlatformKind.Twitch, "streamer", "https://www.twitch.tv/videos/123456"),
            "720p60",
            "streamlink.exe",
            LowLatency: false,
            CustomArguments: ["--http-header", "X-Test=1"]);
        var arguments = ((IEnumerable<string>)method!.Invoke(null, [request])!).ToArray();

        Assert.True(arguments.Contains("--stream-url"));
        Assert.Equal(false, arguments.Contains("--player-external-http"));
        Assert.Equal(false, arguments.Contains("--retry-streams"));
        Assert.Equal("https://www.twitch.tv/videos/123456", arguments[^2]);
        Assert.Equal("720p60", arguments[^1]);
        Assert.True(Array.IndexOf(arguments, "--http-header") < arguments.Length - 2);
        return Task.CompletedTask;
    }),
    ("stream probes require an absolute HTTP URL", () =>
    {
        var method = typeof(StreamlinkService).GetMethod(
            "TryReadFirstAbsoluteUri",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var invalidArguments = new object?[] { "Streamlink warning output", null };
        Assert.Equal(false, (bool)method!.Invoke(null, invalidArguments)!);

        var validArguments = new object?[] { "diagnostic\nhttps://cdn.example.test/live.m3u8\n", null };
        Assert.True((bool)method.Invoke(null, validArguments)!);
        Assert.Equal(
            "https://cdn.example.test/live.m3u8",
            ((Uri)validArguments[1]!).ToString());
        return Task.CompletedTask;
    }),
    ("streamlink external HTTP URLs accept output without a trailing slash", () =>
    {
        var method = typeof(StreamlinkService).GetMethod(
            "TryReadLocalHttpUri",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var arguments = new object?[] { "[cli] Player external HTTP server: http://0.0.0.0:12345", null };
        Assert.True((bool)method!.Invoke(null, arguments)!);
        var uri = (Uri)arguments[1]!;
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(12345, uri.Port);
        Assert.Equal("/", uri.AbsolutePath);
        return Task.CompletedTask;
    }),
    ("stable live playback startup disables low latency flags for a tab", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC",
            LowLatency = true
        };
        settings.Chat.ConnectAutomatically = false;
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://www.twitch.tv/albralelie", PlatformKind.Kick),
            "best",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            logger,
            action => action());

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings, preferStableLivePlayback: true);

        Assert.Equal(1, streamlink.StartExternalHttpRequests.Count);
        Assert.Equal(false, streamlink.StartExternalHttpRequests[0].LowLatency);
        Assert.Equal(true, playbackFactory.Engine!.Played);
        await tab.DisposeAsync();
    }),
    ("live chat burst batches UI drain with 100-message cap and dedup", async () =>
    {
        var dispatched = new Queue<Action>();
        void QueueDispatch(Action action) => dispatched.Enqueue(action);
        void PumpDispatchedActions()
        {
            while (dispatched.Count > 0)
            {
                dispatched.Dequeue()();
            }
        }

        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var chatFactory = new FakeChatClientFactory();
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\Program Files\VideoLAN\VLC"
        };
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Docked;
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("albralelie", PlatformKind.Twitch),
            "best",
            streamlink,
            playbackFactory,
            chatFactory,
            logger,
            QueueDispatch);

        tab.SetVideoHandle(new IntPtr(1234));
        await tab.StartAsync(settings);
        await TestWait.UntilAsync(() => chatFactory.Client.Connected, TimeSpan.FromMilliseconds(500));
        PumpDispatchedActions();

        for (var index = 0; index < 105; index++)
        {
            chatFactory.Client.Receive(new ChatMessage(
                PlatformKind.Twitch,
                "albralelie",
                $"viewer-{index}",
                $"message {index}",
                DateTimeOffset.UtcNow.AddSeconds(index),
                MessageId: $"burst-message-{index}"));
        }

        chatFactory.Client.Receive(new ChatMessage(
            PlatformKind.Twitch,
            "albralelie",
            "duplicate-viewer",
            "duplicate should be skipped",
            DateTimeOffset.UtcNow.AddSeconds(106),
            MessageId: "burst-message-104"));

        Assert.Equal(0, tab.ChatMessages.Count);
        Assert.Equal(1, dispatched.Count);
        PumpDispatchedActions();

        Assert.Equal(100, tab.ChatMessages.Count);
        Assert.Equal(100, tab.DockedChatMessages.Count);
        Assert.Equal("message 5", tab.ChatMessages[0].Message);
        Assert.Equal("message 104", tab.ChatMessages[^1].Message);
        await tab.DisposeAsync();
    }),
    ("video aspect polling backs off after stable samples and resets on change", () =>
    {
        var retry = TimeSpan.FromMilliseconds(250);
        var changing = TimeSpan.FromSeconds(2);
        var stable = TimeSpan.FromSeconds(15);
        var backoff = new VideoAspectRatioPollingBackoff(retry, changing, stable, stableSampleThreshold: 3);

        Assert.Equal(retry, backoff.RecordInvalidSample());
        Assert.Equal(changing, backoff.RecordValidSample(16.0 / 9.0));
        Assert.Equal(changing, backoff.RecordValidSample(16.0 / 9.0));
        Assert.Equal(changing, backoff.RecordValidSample(16.0 / 9.0));
        Assert.Equal(stable, backoff.RecordValidSample(16.0 / 9.0));
        Assert.Equal(changing, backoff.RecordValidSample(4.0 / 3.0));
        Assert.Equal(changing, backoff.RecordValidSample(4.0 / 3.0));
        Assert.Equal(changing, backoff.RecordValidSample(4.0 / 3.0));
        Assert.Equal(stable, backoff.RecordValidSample(4.0 / 3.0));

        backoff.Reset();
        Assert.Equal(changing, backoff.RecordValidSample(16.0 / 9.0));
        return Task.CompletedTask;
    }),
    ("renderer settings default safely and select Direct3D11 only when available", () =>
    {
        var settings = new AppSettings();
        Assert.Equal(VideoRendererMode.Automatic, settings.VideoRendererMode);
        settings.VideoRendererMode = (VideoRendererMode)999;
        Assert.Equal(VideoRendererMode.Automatic, settings.VideoRendererMode);

        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        var root = CreateTempTestDirectory();
        try
        {
            var pluginDirectory = Path.Combine(root, "plugins", "video_output");
            Directory.CreateDirectory(pluginDirectory);
            File.WriteAllText(Path.Combine(pluginDirectory, "libdirect3d11_plugin.dll"), "test");

            Assert.Equal(
                VideoRendererMode.Direct3D11,
                LibVlcRendererSelection.Resolve(root, VideoRendererMode.Automatic, usesNativeOverlay: false));
            Assert.Equal(
                VideoRendererMode.Direct3D11,
                LibVlcRendererSelection.Resolve(root, VideoRendererMode.Direct3D11, usesNativeOverlay: false));
            Assert.Equal(
                VideoRendererMode.Gdi,
                LibVlcRendererSelection.Resolve(root, VideoRendererMode.Automatic, usesNativeOverlay: true));
            Assert.Equal(
                VideoRendererMode.Gdi,
                LibVlcRendererSelection.Resolve(Path.Combine(root, "missing"), VideoRendererMode.Direct3D11, usesNativeOverlay: false));

            var directOptions = LibVlcPlaybackEngine.BuildLibVlcOptionsForRenderer(VideoRendererMode.Direct3D11);
            var directOptionsText = string.Join("\n", directOptions);
            Assert.Contains("--vout=direct3d11", directOptionsText);
            Assert.DoesNotContain("--vout=wingdi", directOptionsText);
        }
        finally
        {
            DeleteTempTestDirectory(root);
        }

        return Task.CompletedTask;
    }),
    ("shared LibVLC runtime leases release only after the last reference", () =>
    {
        var registry = new ReferenceCountedRuntimeRegistry();
        var createCount = 0;
        var releaseCount = 0;
        var first = registry.Acquire(
            "vlc|direct3d11",
            () =>
            {
                createCount++;
                return new IntPtr(123);
            },
            _ => releaseCount++);
        var second = registry.Acquire(
            "VLC|DIRECT3D11",
            () =>
            {
                createCount++;
                return new IntPtr(456);
            },
            _ => releaseCount++);

        Assert.Equal(1, createCount);
        Assert.Equal(1, registry.EntryCount);
        Assert.Equal(2, registry.GetReferenceCount("vlc|direct3d11"));
        first.Dispose();
        Assert.Equal(0, releaseCount);
        Assert.Equal(1, registry.GetReferenceCount("vlc|direct3d11"));
        second.Dispose();
        second.Dispose();
        Assert.Equal(1, releaseCount);
        Assert.Equal(0, registry.EntryCount);
        return Task.CompletedTask;
    }),
    ("shared LibVLC runtime keys include all compatibility options", () =>
    {
        var baseOptions = new[] { "--vout=wingdi", "--avcodec-hw=any" };
        var firstKey = LibVlcRuntime.BuildSharedRuntimeKey(
            @"C:\VLC",
            VideoRendererMode.Gdi,
            baseOptions,
            @"C:\VLC\plugins;C:\overlay-a");
        var differentOptionKey = LibVlcRuntime.BuildSharedRuntimeKey(
            @"C:\VLC",
            VideoRendererMode.Gdi,
            ["--vout=direct3d11", "--avcodec-hw=any"],
            @"C:\VLC\plugins;C:\overlay-a");
        var differentPluginKey = LibVlcRuntime.BuildSharedRuntimeKey(
            @"C:\VLC",
            VideoRendererMode.Gdi,
            baseOptions,
            @"C:\VLC\plugins;C:\overlay-b");

        Assert.True(firstKey != differentOptionKey);
        Assert.True(firstKey != differentPluginKey);
        var rootDirectory = Path.GetPathRoot(Environment.SystemDirectory)!;
        var rootKey = LibVlcRuntime.BuildSharedRuntimeKey(
            rootDirectory,
            VideoRendererMode.Gdi,
            baseOptions);
        Assert.True(rootKey.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.True(LibVlcRuntime.ShouldShareRuntime(usesNativeOverlay: false));
        Assert.Equal(false, LibVlcRuntime.ShouldShareRuntime(usesNativeOverlay: true));
        return Task.CompletedTask;
    }),
    ("libVLC overlay options stay on an isolated runtime", () =>
    {
        var nativeType = typeof(LibVlcPlaybackEngine).Assembly.GetType(
            "StreamlinkVlcStudio.Infrastructure.Vlc.LibVlcNative");
        Assert.NotNull(nativeType);
        Assert.True(nativeType!.GetMethod(
            "libvlc_media_add_option",
            BindingFlags.NonPublic | BindingFlags.Static) is null);

        var options = LibVlcPlaybackEngine.BuildLibVlcOptions();
        Assert.DoesNotContain("--sub-source=", string.Join("\n", options));
        var overlayOption = (string)typeof(LibVlcPlaybackEngine).GetMethod(
            "BuildOverlaySubSourceOption",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(
                null,
                ["svs_media", @"C:\State\overlay.txt"])!;
        Assert.Contains("--sub-source=myoverlay{", overlayOption);
        Assert.Contains("pipe=svs_media", overlayOption);
        Assert.Equal(false, LibVlcRuntime.ShouldShareRuntime(usesNativeOverlay: true));
        return Task.CompletedTask;
    }),
    ("libVLC overlay plugin path keeps bundled and installed VLC modules discoverable", () =>
    {
        var buildPluginPath = typeof(LibVlcPlaybackEngine).GetMethod(
            "BuildPluginPath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildPluginPath);

        var pluginPath = (string)buildPluginPath!.Invoke(
            null,
            [
                @"C:\VLC",
                @"C:\AppData\vlc-overlay-plugins",
                @"C:\Existing\plugins;C:\VLC\plugins"
            ])!;
        var paths = pluginPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(@"C:\AppData\vlc-overlay-plugins", paths[0]);
        Assert.True(paths.Contains(@"C:\Existing\plugins", StringComparer.OrdinalIgnoreCase));
        Assert.True(paths.Contains(@"C:\VLC\plugins", StringComparer.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }),
    ("playback factory receives the selected renderer without changing stream quality", async () =>
    {
        var streamlink = new FakeStreamlinkService();
        var playbackFactory = new FakePlaybackEngineFactory();
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = @"C:\VLC",
            LowLatency = true,
            VideoRendererMode = VideoRendererMode.Direct3D11
        };
        settings.Chat.ConnectAutomatically = false;
        var tab = TestViewModels.CreateTab(
            StreamInputParser.Parse("https://www.twitch.tv/streamer", PlatformKind.Kick),
            "1080p60",
            streamlink,
            playbackFactory,
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action());
        tab.SetVideoHandle(new IntPtr(42));

        await tab.StartAsync(settings);

        Assert.Equal(VideoRendererMode.Direct3D11, playbackFactory.LastRendererMode);
        Assert.Equal("1080p60", streamlink.StartExternalHttpRequests.Single().Quality);
        Assert.Equal(true, streamlink.StartExternalHttpRequests.Single().LowLatency);
        await tab.DisposeAsync();
    }),
    ("libVLC options keep native overlay settings scoped to sub-source", () =>
    {
        var buildOptions = typeof(LibVlcPlaybackEngine).GetMethod(
            "BuildLibVlcOptions",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildOptions);

        var options = ((IEnumerable<string>)buildOptions!.Invoke(null, null)!).ToArray();
        Assert.Equal(false, options.Any(option => option.StartsWith("--myoverlay-", StringComparison.Ordinal)));

        var buildSubSourceOption = typeof(LibVlcPlaybackEngine).GetMethod(
            "BuildOverlaySubSourceOption",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(buildSubSourceOption);

        var subSourceOption = (string)buildSubSourceOption!.Invoke(null, ["svs_test", @"C:\State\overlay.txt"])!;
        Assert.Contains("--sub-source=myoverlay{", subSourceOption);
        Assert.Contains("show-placeholder=0", subSourceOption);
        return Task.CompletedTask;
    }),
    ("libVLC instance creation does not use unsupported string-array marshalling", () =>
    {
        var nativeType = typeof(LibVlcPlaybackEngine).Assembly.GetType(
            "StreamlinkVlcStudio.Infrastructure.Vlc.LibVlcNative");
        Assert.NotNull(nativeType);

        var createInstance = nativeType!.GetMethod(
            "CreateInstance",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(createInstance);

        var nativeCreate = nativeType.GetMethod(
            "libvlc_new",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(nativeCreate);
        Assert.Equal(typeof(IntPtr), nativeCreate!.GetParameters()[1].ParameterType);
        return Task.CompletedTask;
    }),
    ("libVLC CRT environment values use UTF-8 marshalling", () =>
    {
        var nativeType = typeof(LibVlcPlaybackEngine).Assembly.GetType(
            "StreamlinkVlcStudio.Infrastructure.Vlc.LibVlcNative");
        Assert.NotNull(nativeType);

        var putEnvironment = nativeType!.GetMethod(
            "putenv_s",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(putEnvironment);

        var parameters = putEnvironment!.GetParameters();
        Assert.Equal(2, parameters.Length);
        foreach (var parameter in parameters)
        {
            var marshalAs = parameter.GetCustomAttribute<MarshalAsAttribute>();
            Assert.NotNull(marshalAs);
            Assert.Equal(UnmanagedType.LPUTF8Str, marshalAs!.Value);
        }

        return Task.CompletedTask;
    }),
    ("libVLC recreates active video output when its host surface changes", () =>
    {
        var shouldRebind = typeof(LibVlcPlaybackEngine).GetMethod(
            "ShouldRebindVideoOutput",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(shouldRebind);

        Assert.Equal(true, (bool)shouldRebind!.Invoke(null, [
            new IntPtr(100),
            new IntPtr(200),
            true,
            true
        ])!);
        Assert.Equal(false, (bool)shouldRebind.Invoke(null, [
            new IntPtr(100),
            new IntPtr(100),
            true,
            true
        ])!);
        Assert.Equal(false, (bool)shouldRebind.Invoke(null, [
            new IntPtr(100),
            IntPtr.Zero,
            true,
            true
        ])!);
        Assert.Equal(false, (bool)shouldRebind.Invoke(null, [
            new IntPtr(100),
            new IntPtr(200),
            false,
            true
        ])!);
        Assert.Equal(false, (bool)shouldRebind.Invoke(null, [
            new IntPtr(100),
            new IntPtr(200),
            true,
            false
        ])!);
        return Task.CompletedTask;
    }),
    ("libVLC engine constructs with an installed native library", async () =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var vlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY");
        if (string.IsNullOrWhiteSpace(vlcDirectory))
        {
            vlcDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "VideoLAN",
                "VLC");
        }

        if (!File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")))
        {
            return;
        }

        var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings());
        using var engine = await factory.CreateAsync(vlcDirectory, enableNativeOverlay: false);
        Assert.Equal(false, engine.UsesNativeOverlay);
    }),
    ("browse service loads Twitch top categories quickly and exact viewer totals separately", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer browse-top-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            requests.Add(request);
            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("Bearer browse-top-token", request.Headers.Authorization?.ToString());
            Assert.SequenceEqual(new[] { "twitch-client-id" }, request.Headers.GetValues("Client-Id").ToArray());

            if (request.RequestUri.AbsolutePath == "/helix/games/top")
            {
                Assert.Contains("first=50", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "509658",
                          "name": "Just Chatting",
                          "box_art_url": "https://static-cdn.jtvnw.net/ttv-boxart/509658-{width}x{height}.jpg"
                        },
                        {
                          "id": "263490",
                          "name": "Rust",
                          "box_art_url": "//static-cdn.jtvnw.net/ttv-boxart/263490-{width}x{height}.jpg"
                        }
                      ],
                      "pagination": { "cursor": "next-games" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Assert.Contains("first=100", request.RequestUri.Query);
            Assert.Contains("game_id=509658", request.RequestUri.Query);
            Assert.Contains("game_id=263490", request.RequestUri.Query);
            if (request.RequestUri.Query.Contains("after=combined-next", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        { "id": "chat-two", "game_id": "509658", "viewer_count": 300 },
                        { "id": "chat-three", "game_id": "509658", "viewer_count": 45 }
                      ],
                      "pagination": {}
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.DoesNotContain("after=", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    { "id": "chat-one", "game_id": "509658", "viewer_count": 12000 },
                    { "id": "chat-two", "game_id": "509658", "viewer_count": 300 },
                    { "id": "rust-one", "game_id": "263490", "viewer_count": 20 }
                  ],
                  "pagination": { "cursor": "combined-next" }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:browse-top-token";
        settings.StreamlinkPath = "streamlink.exe";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Twitch, PageSize: 50),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("next-games", result.NextCursor);
        Assert.Equal(2, result.Items.Count);
        Assert.SequenceEqual(new[] { "Just Chatting", "Rust" }, result.Items.Select(category => category.Name).ToArray());
        Assert.Equal("509658", result.Items[0].Id);
        Assert.Equal("https://static-cdn.jtvnw.net/ttv-boxart/509658-285x380.jpg", result.Items[0].ThumbnailUrl);
        Assert.Equal<int?>(null, result.Items[0].ViewerCount);
        Assert.Equal("263490", result.Items[1].Id);
        Assert.Equal("https://static-cdn.jtvnw.net/ttv-boxart/263490-285x380.jpg", result.Items[1].ThumbnailUrl);
        Assert.Equal<int?>(null, result.Items[1].ViewerCount);
        Assert.Equal(1, requests.Count);

        var countResult = await service.GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["509658", "263490"]),
            settings);

        Assert.Equal(BrowseResultStatus.Available, countResult.Status);
        Assert.Equal(2, countResult.Items.Count);
        Assert.Equal("509658", countResult.Items[0].CategoryId);
        Assert.Equal(12345, countResult.Items[0].ViewerCount);
        Assert.Equal("263490", countResult.Items[1].CategoryId);
        Assert.Equal(20, countResult.Items[1].ViewerCount);
        var viewModel = new BrowseCategoryViewModel(result.Items[0], _ => Task.CompletedTask);
        viewModel.SetViewerCount(countResult.Items[0].ViewerCount);
        Assert.Equal("12.3K viewers", viewModel.MetadataText);
        Assert.Equal(3, requests.Count);
    }),
    ("browse Twitch category viewer counts stop after excessive pagination", async () =>
    {
        var pageRequests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer browse-pagination-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            pageRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"data\":[],\"pagination\":{{\"cursor\":\"cursor-{pageRequests}\"}}}}")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "browse-pagination-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["509658"]),
            settings);

        Assert.Equal(BrowseResultStatus.Unavailable, result.Status);
        Assert.Equal(100, pageRequests);
        Assert.True(result.Message.Contains("safety limit", StringComparison.OrdinalIgnoreCase));
    }),
    ("browse service maps Twitch category search and exact viewer totals separately preserving order", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer browse-search-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            requests.Add(request);
            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("Bearer browse-search-token", request.Headers.Authorization?.ToString());
            Assert.SequenceEqual(new[] { "twitch-client-id" }, request.Headers.GetValues("Client-Id").ToArray());

            if (request.RequestUri.AbsolutePath == "/helix/search/categories")
            {
                Assert.Contains("query=Rust", request.RequestUri.Query);
                Assert.Contains("after=search-cursor", request.RequestUri.Query);
                Assert.Contains("first=25", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "101",
                          "name": "Rust",
                          "box_art_url": "https://static-cdn.jtvnw.net/ttv-boxart/101-{width}x{height}.jpg"
                        },
                        {
                          "id": "202",
                          "name": "Rust Slots",
                          "box_art_url": "https://static-cdn.jtvnw.net/ttv-boxart/202-{width}x{height}.jpg"
                        }
                      ],
                      "pagination": { "cursor": "search-next" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Assert.Contains("first=100", request.RequestUri.Query);
            Assert.Contains("game_id=101", request.RequestUri.Query);
            Assert.Contains("game_id=202", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    { "id": "rust-one", "game_id": "101", "viewer_count": 20 },
                    { "id": "slots-one", "game_id": "202", "viewer_count": 700 }
                  ],
                  "pagination": {}
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "browse-search-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Twitch, "Rust", "search-cursor", 25),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("search-next", result.NextCursor);
        Assert.Equal(2, result.Items.Count);
        Assert.SequenceEqual(new[] { "Rust", "Rust Slots" }, result.Items.Select(category => category.Name).ToArray());
        Assert.Equal<int?>(null, result.Items[0].ViewerCount);
        Assert.Equal<int?>(null, result.Items[1].ViewerCount);
        Assert.Equal(1, requests.Count);

        var countResult = await service.GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["101", "202"]),
            settings);

        Assert.Equal(BrowseResultStatus.Available, countResult.Status);
        Assert.Equal(2, countResult.Items.Count);
        Assert.Equal("101", countResult.Items[0].CategoryId);
        Assert.Equal(20, countResult.Items[0].ViewerCount);
        Assert.Equal("202", countResult.Items[1].CategoryId);
        Assert.Equal(700, countResult.Items[1].ViewerCount);
        Assert.Equal(2, requests.Count);
    }),
    ("browse service caches Twitch client ID resolved from OAuth token for category counts", async () =>
    {
        var validateRequests = 0;
        var apiRequests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                validateRequests++;
                Assert.Equal("/oauth2/validate", request.RequestUri.AbsolutePath);
                Assert.Equal("Bearer cache-token", request.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "client_id": "cached-client-id",
                      "login": "viewer",
                      "user_id": "1234",
                      "scopes": [],
                      "expires_in": 3600
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            apiRequests++;
            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.SequenceEqual(new[] { "cached-client-id" }, request.Headers.GetValues("Client-Id").ToArray());
            Assert.Equal("Bearer cache-token", request.Headers.Authorization?.ToString());

            if (request.RequestUri.AbsolutePath == "/helix/games/top")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": "509658",
                          "name": "Just Chatting",
                          "box_art_url": "https://static-cdn.jtvnw.net/ttv-boxart/509658-{width}x{height}.jpg"
                        }
                      ],
                      "pagination": {}
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Assert.Contains("game_id=509658", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    { "id": "chat-one", "game_id": "509658", "viewer_count": 100 }
                  ],
                  "pagination": {}
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchOAuthToken = "oauth:cache-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var categoryResult = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Twitch),
            settings);
        var countResult = await service.GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["509658"]),
            settings);

        Assert.Equal(BrowseResultStatus.Available, categoryResult.Status);
        Assert.Equal(BrowseResultStatus.Available, countResult.Status);
        Assert.Equal(1, validateRequests);
        Assert.Equal(2, apiRequests);
    }),
    ("browse service maps Twitch category streams to playable targets", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer browse-streams-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            if (request.RequestUri!.AbsolutePath == "/helix/users")
            {
                Assert.Contains("login=streamer", request.RequestUri.Query);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("browse-streams-token", request.Headers.Authorization?.Parameter);
                Assert.SequenceEqual(["twitch-client-id"], request.Headers.GetValues("Client-Id"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "login": "streamer",
                          "profile_image_url": "https://static-cdn.jtvnw.net/jtv_user_pictures/streamer-profile.png"
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("api.twitch.tv", request.RequestUri!.Host);
            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Assert.Contains("game_id=509658", request.RequestUri.Query);
            Assert.Contains("after=stream-cursor", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "user_login": "streamer",
                      "user_name": "Streamer",
                      "title": "Live now",
                      "game_id": "509658",
                      "game_name": "Just Chatting",
                      "viewer_count": 12345,
                      "thumbnail_url": "https://static-cdn.jtvnw.net/previews-ttv/live_user_streamer-{width}x{height}.jpg",
                      "started_at": "2026-06-01T20:00:00Z",
                      "is_mature": true,
                      "language": "en"
                    }
                  ],
                  "pagination": { "cursor": "next-streams" }
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "browse-streams-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetStreamsAsync(
            new BrowseStreamRequest(PlatformKind.Twitch, "509658", "Just Chatting", "stream-cursor", 20),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("next-streams", result.NextCursor);
        Assert.Equal(1, result.Items.Count);
        Assert.Equal("https://www.twitch.tv/streamer", result.Items[0].Target.Url);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/streamer-profile.png",
            result.Items[0].ProfileImageUrl);
        var browseItem = new LiveStreamCardViewModel(
            LiveStreamCardData.FromBrowseStream(result.Items[0]),
            (_, _) => Task.CompletedTask);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/jtv_user_pictures/streamer-profile.png",
            browseItem.ProfileImageUrl);
        Assert.Equal(LiveStreamCardSource.Browse, browseItem.Source);
        Assert.Equal(0L, browseItem.ThumbnailImageRequest.CacheVersion);
        Assert.True(browseItem.HasProfileImage);
        Assert.Equal("12.3K", browseItem.ViewerCountText);
        Assert.Equal("https://static-cdn.jtvnw.net/previews-ttv/live_user_streamer-440x248.jpg", result.Items[0].ThumbnailUrl);
        Assert.Equal(true, result.Items[0].IsMature);
    }),
    ("browse service reports Twitch auth failures", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer browse-auth-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"message":"invalid token"}""", Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "browse-auth-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Twitch),
            settings);

        Assert.Equal(BrowseResultStatus.Unauthorized, result.Status);
        Assert.Equal(0, result.Items.Count);
    }),
    ("browse service reports Twitch category viewer count auth failures", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer browse-viewer-auth-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            requests.Add(request);
            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"message":"invalid token"}""", Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "browse-viewer-auth-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["509658"]),
            settings);

        Assert.Equal(BrowseResultStatus.Unauthorized, result.Status);
        Assert.Equal(0, result.Items.Count);
        Assert.Equal(1, requests.Count);
    }),
    ("browse service reports Twitch category viewer count rate limits without partial counts", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer browse-rate-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            requests.Add(request);
            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Assert.Contains("game_id=1", request.RequestUri.Query);
            Assert.Contains("game_id=2", request.RequestUri.Query);
            if (!request.RequestUri.Query.Contains("after=rate-next", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        { "id": "one-stream", "game_id": "1", "viewer_count": 100 }
                      ],
                      "pagination": { "cursor": "rate-next" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"message":"rate limited"}""", Encoding.UTF8, "application/json")
            };
            rateLimited.Headers.TryAddWithoutValidation("Retry-After", "0");
            return rateLimited;
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "browse-rate-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1", "2"]),
            settings);

        Assert.Equal(BrowseResultStatus.Unavailable, result.Status);
        Assert.Equal(0, result.Items.Count);
        Assert.Equal(4, requests.Count);
    }),
    ("browse service retries Twitch category viewer count rate limits before failing", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        var rateLimitReturned = false;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer browse-retry-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            requests.Add(request);
            Assert.Equal("api.twitch.tv", request.RequestUri.Host);
            Assert.Equal("/helix/streams", request.RequestUri.AbsolutePath);
            Assert.Contains("game_id=1", request.RequestUri.Query);

            if (!request.RequestUri.Query.Contains("after=retry-next", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        { "id": "one-stream", "game_id": "1", "viewer_count": 100 }
                      ],
                      "pagination": { "cursor": "retry-next" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (!rateLimitReturned)
            {
                rateLimitReturned = true;
                var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("""{"message":"rate limited"}""", Encoding.UTF8, "application/json")
                };
                rateLimited.Headers.TryAddWithoutValidation("Retry-After", "0");
                return rateLimited;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    { "id": "one-later", "game_id": "1", "viewer_count": 25 }
                  ],
                  "pagination": {}
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "browse-retry-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoryViewerCountsAsync(
            new BrowseCategoryViewerCountRequest(PlatformKind.Twitch, ["1"]),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal(125, result.Items[0].ViewerCount);
        Assert.Equal(3, requests.Count);
    }),
    ("browse service maps Kick categories from categories endpoint cursor tags thumbnail and viewer count", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        var requestsGate = new object();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            lock (requestsGate)
            {
                requests.Add(request);
            }

            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("Bearer kick-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri.AbsolutePath == "/public/v2/categories")
            {
                Assert.Contains("limit=50", request.RequestUri.Query);
                Assert.DoesNotContain("name=", request.RequestUri.Query);
                Assert.DoesNotContain("cursor=", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": 101,
                          "name": "Rust",
                          "tags": ["FPS", { "name": "Survival" }],
                          "thumbnail": "//kick.example/rust.jpeg"
                        },
                        {
                          "id": 202,
                          "name": "Just Chatting",
                          "tags": ["IRL"],
                          "thumbnail": "https://kick.example/chatting.jpeg"
                        }
                      ],
                      "message": "OK",
                      "pagination": { "next_cursor": "kick-next" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath == "/public/v1/livestreams")
            {
                Assert.Contains("limit=100", request.RequestUri.Query);
                Assert.Contains("sort=viewer_count", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[],"message":"OK"}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.DoesNotContain("?", request.RequestUri.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/public/v1/categories/101")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": {
                        "id": 101,
                        "name": "Rust",
                        "tags": ["FPS", "Survival"],
                        "thumbnail": "//kick.example/rust.jpeg",
                        "viewer_count": 900
                      },
                      "message": "OK"
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/public/v1/categories/202", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                      "id": 202,
                      "name": "Just Chatting",
                      "tags": ["IRL"],
                      "thumbnail": "https://kick.example/chatting.jpeg",
                      "viewer_count": 1200
                  },
                  "message": "OK"
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Kick),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("kick-next", result.NextCursor);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("202", result.Items[0].Id);
        Assert.Equal("Just Chatting", result.Items[0].Name);
        Assert.Equal(1200, result.Items[0].ViewerCount);
        Assert.Equal("101", result.Items[1].Id);
        Assert.Equal("Rust", result.Items[1].Name);
        Assert.Equal(900, result.Items[1].ViewerCount);
        Assert.Equal("https://kick.example/rust.jpeg", result.Items[1].ThumbnailUrl);
        Assert.SequenceEqual(new[] { "FPS", "Survival" }, result.Items[1].Tags.ToArray());
        Assert.Equal("1.2K viewers | IRL", new BrowseCategoryViewModel(result.Items[0], _ => Task.CompletedTask).MetadataText);
        Assert.Equal("900 viewers | FPS | Survival", new BrowseCategoryViewModel(result.Items[1], _ => Task.CompletedTask).MetadataText);
        Assert.Equal(4, requests.Count);
    }),
    ("browse service discovers Kick top live categories missing from the first category page", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        var requestsGate = new object();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            lock (requestsGate)
            {
                requests.Add(request);
            }

            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("Bearer kick-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri.AbsolutePath == "/public/v2/categories")
            {
                Assert.Contains("limit=2", request.RequestUri.Query);
                Assert.DoesNotContain("name=", request.RequestUri.Query);
                Assert.DoesNotContain("cursor=", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": 404,
                          "name": "Minecraft",
                          "tags": ["Sandbox"],
                          "thumbnail": "//kick.example/minecraft.jpeg"
                        },
                        {
                          "id": 505,
                          "name": "Music",
                          "tags": ["Music"],
                          "thumbnail": "//kick.example/music.jpeg"
                        }
                      ],
                      "message": "OK",
                      "pagination": { "next_cursor": "kick-next" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath == "/public/v1/livestreams")
            {
                Assert.Contains("limit=100", request.RequestUri.Query);
                Assert.Contains("sort=viewer_count", request.RequestUri.Query);
                Assert.DoesNotContain("category_id=", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "slug": "slots-top",
                          "viewer_count": 9000,
                          "category": {
                            "id": 101,
                            "name": "Slots & Casino",
                            "thumbnail": "//kick.example/slots.jpeg"
                          }
                        },
                        {
                          "slug": "gta-top",
                          "viewer_count": 8500,
                          "category": {
                            "id": 202,
                            "name": "Grand Theft Auto V",
                            "thumbnail": "//kick.example/gta.jpeg"
                          }
                        },
                        {
                          "slug": "irl-top",
                          "viewer_count": 8000,
                          "category": {
                            "id": 303,
                            "name": "IRL",
                            "thumbnail": "//kick.example/irl.jpeg"
                          }
                        },
                        {
                          "slug": "slots-second",
                          "viewer_count": 7000,
                          "category": {
                            "id": 101,
                            "name": "Slots & Casino",
                            "thumbnail": "//kick.example/slots.jpeg"
                          }
                        }
                      ],
                      "message": "OK"
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            var categoryId = request.RequestUri.AbsolutePath.Split('/').Last();
            var detailJson = categoryId switch
            {
                "101" => """
                {
                  "data": {
                    "id": 101,
                    "name": "Slots & Casino",
                    "tags": ["Casino"],
                    "thumbnail": "//kick.example/slots.jpeg",
                    "viewer_count": 50000
                  },
                  "message": "OK"
                }
                """,
                "202" => """
                {
                  "data": {
                    "id": 202,
                    "name": "Grand Theft Auto V",
                    "tags": ["Action"],
                    "thumbnail": "//kick.example/gta.jpeg",
                    "viewer_count": 42000
                  },
                  "message": "OK"
                }
                """,
                "303" => """
                {
                  "data": {
                    "id": 303,
                    "name": "IRL",
                    "tags": ["IRL"],
                    "thumbnail": "//kick.example/irl.jpeg",
                    "viewer_count": 34000
                  },
                  "message": "OK"
                }
                """,
                "404" => """
                {
                  "data": {
                    "id": 404,
                    "name": "Minecraft",
                    "tags": ["Sandbox"],
                    "thumbnail": "//kick.example/minecraft.jpeg",
                    "viewer_count": 5000
                  },
                  "message": "OK"
                }
                """,
                "505" => """
                {
                  "data": {
                    "id": 505,
                    "name": "Music",
                    "tags": ["Music"],
                    "thumbnail": "//kick.example/music.jpeg",
                    "viewer_count": 3000
                  },
                  "message": "OK"
                }
                """,
                _ => throw new InvalidOperationException($"Unexpected category detail path: {request.RequestUri.AbsolutePath}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(detailJson, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Kick, PageSize: 2),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("kick-next", result.NextCursor);
        Assert.SequenceEqual(
            new[] { "Slots & Casino", "Grand Theft Auto V", "IRL", "Minecraft", "Music" },
            result.Items.Select(category => category.Name).ToArray());
        Assert.Equal("303", result.Items[2].Id);
        Assert.Equal(34000, result.Items[2].ViewerCount);
        Assert.Equal("34K viewers | IRL", new BrowseCategoryViewModel(result.Items[2], _ => Task.CompletedTask).MetadataText);
        Assert.Equal(7, requests.Count);
    }),
    ("browse service returns loaded Kick category page without prefetching later pages", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests.Add(request);
            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("Bearer kick-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri.AbsolutePath == "/public/v2/categories")
            {
                Assert.Contains("limit=1", request.RequestUri.Query);
                if (request.RequestUri.Query.Contains("cursor=page-two", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Kick category browse should not prefetch later category pages before returning.");
                }

                Assert.DoesNotContain("cursor=", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": 101,
                          "name": "Rust",
                          "tags": ["FPS"],
                          "thumbnail": "//kick.example/rust.jpeg"
                        }
                      ],
                      "message": "OK",
                      "pagination": { "next_cursor": "page-two" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath == "/public/v1/livestreams")
            {
                Assert.Contains("limit=100", request.RequestUri.Query);
                Assert.Contains("sort=viewer_count", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[],"message":"OK"}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/public/v1/categories/101", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "id": 101,
                    "name": "Rust",
                    "tags": ["FPS"],
                    "thumbnail": "//kick.example/rust.jpeg",
                    "viewer_count": 100
                  },
                  "message": "OK"
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var firstPage = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Kick, PageSize: 1),
            settings);

        Assert.Equal(BrowseResultStatus.Available, firstPage.Status);
        Assert.Equal(1, firstPage.Items.Count);
        Assert.Equal("101", firstPage.Items[0].Id);
        Assert.Equal(100, firstPage.Items[0].ViewerCount);
        Assert.Equal("page-two", firstPage.NextCursor);
        Assert.Equal(3, requests.Count);
    }),
    ("browse service maps Kick category search with cursor tags thumbnail and detail viewer counts", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        var requestsGate = new object();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            lock (requestsGate)
            {
                requests.Add(request);
            }

            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("Bearer kick-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri.AbsolutePath == "/public/v2/categories")
            {
                Assert.Contains("name=Rust", request.RequestUri.Query);
                Assert.Contains("cursor=kick-cursor", request.RequestUri.Query);
                Assert.Contains("limit=25", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": 101,
                          "name": "Rust",
                          "tags": ["FPS", "Survival"],
                          "thumbnail": "//kick.example/rust.jpeg"
                        },
                        {
                          "id": 202,
                          "name": "Rust Slots",
                          "tags": ["Casino"],
                          "thumbnail": "https://kick.example/rust-slots.jpeg"
                        }
                      ],
                      "message": "OK",
                      "pagination": { "next_cursor": "kick-next" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath == "/public/v1/categories/101")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": {
                        "id": 101,
                        "name": "Rust",
                        "tags": ["FPS", "Survival"],
                        "thumbnail": "//kick.example/rust.jpeg",
                        "viewer_count": 200
                      },
                      "message": "OK"
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/public/v1/categories/202", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                      "id": 202,
                      "name": "Rust Slots",
                      "tags": ["Casino"],
                      "thumbnail": "https://kick.example/rust-slots.jpeg",
                      "viewer_count": 700
                  },
                  "message": "OK"
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "Bearer kick-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Kick, "Rust", "kick-cursor", 25),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("kick-next", result.NextCursor);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("202", result.Items[0].Id);
        Assert.Equal("Rust Slots", result.Items[0].Name);
        Assert.Equal(700, result.Items[0].ViewerCount);
        Assert.Equal("101", result.Items[1].Id);
        Assert.Equal("Rust", result.Items[1].Name);
        Assert.Equal(200, result.Items[1].ViewerCount);
        Assert.Equal("https://kick.example/rust.jpeg", result.Items[1].ThumbnailUrl);
        Assert.SequenceEqual(new[] { "FPS", "Survival" }, result.Items[1].Tags.ToArray());
        Assert.Equal("700 viewers | Casino", new BrowseCategoryViewModel(result.Items[0], _ => Task.CompletedTask).MetadataText);
        Assert.Equal("200 viewers | FPS | Survival", new BrowseCategoryViewModel(result.Items[1], _ => Task.CompletedTask).MetadataText);
        Assert.Equal(3, requests.Count);
    }),
    ("browse service keeps Kick categories when detail omits viewer count", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests.Add(request);
            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("Bearer kick-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri.AbsolutePath == "/public/v2/categories")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": 101,
                          "name": "Rust",
                          "tags": ["List FPS"],
                          "thumbnail": "//kick.example/rust.jpeg"
                        }
                      ],
                      "message": "OK",
                      "pagination": {}
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath == "/public/v1/livestreams")
            {
                Assert.Contains("limit=100", request.RequestUri.Query);
                Assert.Contains("sort=viewer_count", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[],"message":"OK"}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/public/v1/categories/101", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "id": 101,
                    "name": "Rust",
                    "tags": ["Detail FPS", "Survival"],
                    "thumbnail": "//kick.example/rust-detail.jpeg"
                  },
                  "message": "OK"
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Kick),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal(1, result.Items.Count);
        Assert.Equal("101", result.Items[0].Id);
        Assert.Equal("Rust", result.Items[0].Name);
        Assert.Equal<int?>(null, result.Items[0].ViewerCount);
        Assert.Equal("https://kick.example/rust-detail.jpeg", result.Items[0].ThumbnailUrl);
        Assert.SequenceEqual(new[] { "Detail FPS", "Survival" }, result.Items[0].Tags.ToArray());
        var viewModel = new BrowseCategoryViewModel(result.Items[0], _ => Task.CompletedTask);
        Assert.Equal("", viewModel.ViewerCountText);
        Assert.Equal("Detail FPS | Survival", viewModel.MetadataText);
        Assert.Contains("Loaded 1 category from Kick.", result.Message);
        Assert.Contains("Viewer counts unavailable for 1 category.", result.Message);
        Assert.Equal(3, requests.Count);
    }),
    ("browse service keeps Kick categories when detail returns not found", async () =>
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests.Add(request);
            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("Bearer kick-token", request.Headers.Authorization?.ToString());
            if (request.RequestUri.AbsolutePath == "/public/v2/categories")
            {
                Assert.Contains("cursor=kick-cursor", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "id": 101,
                          "name": "Rust",
                          "tags": ["FPS"],
                          "thumbnail": "//kick.example/rust.jpeg"
                        },
                        {
                          "id": 202,
                          "name": "Just Chatting",
                          "tags": ["IRL"],
                          "thumbnail": "https://kick.example/chatting.jpeg"
                        }
                      ],
                      "message": "OK",
                      "pagination": { "next_cursor": "kick-next" }
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath == "/public/v1/categories/101")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    ReasonPhrase = "Not Found",
                    Content = new StringContent("""{"message":"Not found"}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("/public/v1/categories/202", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": {
                    "id": 202,
                    "name": "Just Chatting",
                    "tags": ["IRL"],
                    "thumbnail": "https://kick.example/chatting.jpeg",
                    "viewer_count": 1200
                  },
                  "message": "OK"
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Kick, Cursor: "kick-cursor"),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("kick-next", result.NextCursor);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("202", result.Items[0].Id);
        Assert.Equal(1200, result.Items[0].ViewerCount);
        Assert.Equal("101", result.Items[1].Id);
        Assert.Equal<int?>(null, result.Items[1].ViewerCount);
        Assert.Equal("https://kick.example/rust.jpeg", result.Items[1].ThumbnailUrl);
        Assert.SequenceEqual(new[] { "FPS" }, result.Items[1].Tags.ToArray());
        Assert.Contains("Loaded 2 categories from Kick.", result.Message);
        Assert.Contains("Viewer counts unavailable for 1 category.", result.Message);
        Assert.Equal(3, requests.Count);
    }),
    ("browse service still reports Kick category detail auth failures", async () =>
    {
        foreach (var statusCode in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            {
                Assert.Equal("api.kick.com", request.RequestUri!.Host);
                Assert.Equal("Bearer kick-token", request.Headers.Authorization?.ToString());
                if (request.RequestUri.AbsolutePath == "/public/v2/categories")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        {
                          "data": [
                            {
                              "id": 101,
                              "name": "Rust",
                              "tags": ["FPS"],
                              "thumbnail": "//kick.example/rust.jpeg"
                            }
                          ],
                          "message": "OK",
                          "pagination": {}
                        }
                        """, Encoding.UTF8, "application/json")
                    };
                }

                if (request.RequestUri.AbsolutePath == "/public/v1/livestreams")
                {
                    Assert.Contains("limit=100", request.RequestUri.Query);
                    Assert.Contains("sort=viewer_count", request.RequestUri.Query);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"data":[],"message":"OK"}""", Encoding.UTF8, "application/json")
                    };
                }

                Assert.Equal("/public/v1/categories/101", request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent("""{"message":"Unauthorized"}""", Encoding.UTF8, "application/json")
                };
            }));
            var settings = new AppSettings();
            settings.Chat.KickOAuthToken = "kick-token";
            var service = new BrowseService(new MemoryLogger(), httpClient);

            var result = await service.GetCategoriesAsync(
                new BrowseCategoryRequest(PlatformKind.Kick),
                settings);

            Assert.Equal(BrowseResultStatus.Unauthorized, result.Status);
            Assert.Contains("Check Kick API credentials", result.Message);
        }
    }),
    ("browse service maps Kick category livestreams", async () =>
    {
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("/public/v1/livestreams", request.RequestUri.AbsolutePath);
            Assert.Contains("category_id=101", request.RequestUri.Query);
            Assert.Contains("limit=30", request.RequestUri.Query);
            Assert.Contains("sort=viewer_count", request.RequestUri.Query);
            Assert.Contains("cursor=stream-cursor", request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "data": [
                    {
                      "broadcaster_user_id": 123,
                      "category": {
                        "id": 101,
                        "name": "Rust",
                        "thumbnail": "https://kick.example/rust.jpeg"
                      },
                      "has_mature_content": false,
                      "language": "en",
                      "profile_picture": "https://kick.example/avatar.jpeg",
                      "slug": "kick-streamer",
                      "started_at": "2026-06-01T20:00:00Z",
                      "stream_title": "Rust drops",
                      "thumbnail": "https://kick.example/live.jpeg",
                      "viewer_count": 987
                    }
                  ],
                  "pagination": { "next_cursor": "stream-next" },
                  "message": "OK"
                }
                """, Encoding.UTF8, "application/json")
            };
        }));
        var settings = new AppSettings();
        settings.Chat.KickOAuthToken = "kick-token";
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var result = await service.GetStreamsAsync(
            new BrowseStreamRequest(PlatformKind.Kick, "101", "Rust", Cursor: "stream-cursor", PageSize: 30),
            settings);

        Assert.Equal(BrowseResultStatus.Available, result.Status);
        Assert.Equal("stream-next", result.NextCursor);
        Assert.Equal(1, result.Items.Count);
        Assert.Equal("https://kick.com/kick-streamer", result.Items[0].Target.Url);
        Assert.Equal("Rust drops", result.Items[0].Title);
        Assert.Equal(987, result.Items[0].ViewerCount);
        Assert.Equal("https://kick.example/avatar.jpeg", result.Items[0].ProfileImageUrl);
        Assert.Equal("https://kick.example/live.jpeg", result.Items[0].ThumbnailUrl);
        Assert.Equal(false, result.Items[0].IsMature);
    }),
    ("browse service missing credentials avoids network calls", async () =>
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new BrowseService(new MemoryLogger(), httpClient);

        var twitchResult = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Twitch),
            new AppSettings());
        var kickResult = await service.GetCategoriesAsync(
            new BrowseCategoryRequest(PlatformKind.Kick),
            new AppSettings());

        Assert.Equal(BrowseResultStatus.NotConfigured, twitchResult.Status);
        Assert.Equal(BrowseResultStatus.NotConfigured, kickResult.Status);
        Assert.Equal(0, requestCount);
    }),
    ("browse home segment selection loads categories and deselects live vods and recent", () =>
    {
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "1", "Twitch Cat", "https://example/twitch.jpg", [])],
            "",
            "Loaded Twitch"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        Assert.Equal(true, viewModel.IsFollowedHomePageSelected);

        viewModel.ShowTwitchVodsHomePageCommand.Execute(null);
        Assert.Equal(true, viewModel.IsTwitchVodsHomePageSelected);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        Assert.Equal(true, viewModel.IsBrowseHomePageSelected);
        Assert.Equal(true, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(false, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal(false, viewModel.IsTwitchVodsHomePageSelected);
        Assert.Equal(false, viewModel.IsRecentHomePageSelected);
        Assert.Equal(false, viewModel.IsFollowedHomePageSelected);
        Assert.Equal(1, browseService.CategoryRequests.Count);
        Assert.Equal(1, viewModel.BrowseCategories.Count);

        viewModel.ShowRecentHomePageCommand.Execute(null);
        Assert.Equal(true, viewModel.IsRecentHomePageSelected);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        Assert.Equal(true, viewModel.IsBrowseHomePageSelected);
        Assert.Equal(true, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(false, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal(1, browseService.CategoryRequests.Count);
        return Task.CompletedTask;
    }),
    ("browse Twitch categories render before exact viewer counts finish", async () =>
    {
        var countRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countResult = new TaskCompletionSource<BrowseResult<BrowseCategoryViewerCount>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browseService = new FakeBrowseService
        {
            CategoryViewerCountResponder = request =>
            {
                countRequestStarted.TrySetResult();
                return countResult.Task;
            }
        };
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "509658", "Just Chatting", "https://example/twitch.jpg", [])],
            "",
            "Loaded Twitch categories"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);

        await countRequestStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(false, viewModel.IsBrowseCategoriesLoading);
        Assert.Equal(1, viewModel.BrowseCategories.Count);
        Assert.Equal("Just Chatting", viewModel.BrowseCategories[0].Name);
        Assert.Equal("Twitch", viewModel.BrowseCategories[0].MetadataText);
        Assert.Equal("Loaded Twitch categories", viewModel.BrowseStatus);
        Assert.Equal(10, browseService.CategoryRequests[0].PageSize);

        countResult.SetResult(new BrowseResult<BrowseCategoryViewerCount>(
            BrowseResultStatus.Available,
            [new BrowseCategoryViewerCount("509658", 12345)],
            "",
            "Loaded exact Twitch viewer counts."));
        await TestWait.UntilAsync(
            () => viewModel.BrowseCategories[0].MetadataText == "12.3K viewers",
            TimeSpan.FromMilliseconds(500));

        Assert.Equal("12.3K viewers", viewModel.BrowseCategories[0].MetadataText);
        Assert.Equal("Loaded Twitch categories", viewModel.BrowseStatus);
        Assert.Equal(1, browseService.CategoryViewerCountRequests.Count);
        Assert.SequenceEqual(["509658"], browseService.CategoryViewerCountRequests[0].CategoryIds);
    }),
    ("browse Twitch top category viewer count loads before lower categories", async () =>
    {
        var firstCountStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCountStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCountResult = new TaskCompletionSource<BrowseResult<BrowseCategoryViewerCount>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCountResult = new TaskCompletionSource<BrowseResult<BrowseCategoryViewerCount>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browseService = new FakeBrowseService
        {
            CategoryViewerCountResponder = request =>
            {
                if (request.CategoryIds.Count == 1 &&
                    request.CategoryIds[0] == "top")
                {
                    firstCountStarted.TrySetResult();
                    return firstCountResult.Task;
                }

                if (request.CategoryIds.Count == 1 &&
                    request.CategoryIds[0] == "second")
                {
                    secondCountStarted.TrySetResult();
                    return secondCountResult.Task;
                }

                throw new InvalidOperationException($"Unexpected category count request: {string.Join(",", request.CategoryIds)}");
            }
        };
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [
                new BrowseCategory(PlatformKind.Twitch, "top", "Top Category", "https://example/top.jpg", []),
                new BrowseCategory(PlatformKind.Twitch, "second", "Second Category", "https://example/second.jpg", [])
            ],
            "",
            "Loaded Twitch categories"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);

        await firstCountStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        await secondCountStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, browseService.CategoryViewerCountRequests.Count);
        Assert.SequenceEqual(["top"], browseService.CategoryViewerCountRequests[0].CategoryIds);
        Assert.SequenceEqual(["second"], browseService.CategoryViewerCountRequests[1].CategoryIds);
        Assert.Equal("Twitch", viewModel.BrowseCategories[0].MetadataText);

        secondCountResult.SetResult(new BrowseResult<BrowseCategoryViewerCount>(
            BrowseResultStatus.Available,
            [new BrowseCategoryViewerCount("second", 12000)],
            "",
            "Loaded exact Twitch viewer counts."));
        await TestWait.UntilAsync(
            () => viewModel.BrowseCategories[1].MetadataText == "12K viewers",
            TimeSpan.FromMilliseconds(500));
        Assert.Equal("Twitch", viewModel.BrowseCategories[0].MetadataText);

        firstCountResult.SetResult(new BrowseResult<BrowseCategoryViewerCount>(
            BrowseResultStatus.Available,
            [new BrowseCategoryViewerCount("top", 20000)],
            "",
            "Loaded exact Twitch viewer counts."));
        await TestWait.UntilAsync(
            () => viewModel.BrowseCategories[0].MetadataText == "20K viewers",
            TimeSpan.FromMilliseconds(500));
    }),
    ("browse Twitch category viewer counts request one exact category per scheduled job", async () =>
    {
        var browseService = new FakeBrowseService
        {
            CategoryViewerCountResponder = request => Task.FromResult(new BrowseResult<BrowseCategoryViewerCount>(
                BrowseResultStatus.Available,
                request.CategoryIds
                    .Select((categoryId, index) => new BrowseCategoryViewerCount(categoryId, (index + 1) * 100))
                    .ToArray(),
                "",
                "Loaded exact Twitch viewer counts."))
        };
        var categories = Enumerable.Range(1, 23)
            .Select(index => new BrowseCategory(
                PlatformKind.Twitch,
                $"id-{index:00}",
                $"Category {index:00}",
                $"https://example/category-{index:00}.jpg",
                []))
            .ToArray();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            categories,
            "",
            "Loaded Twitch categories"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);

        await TestWait.UntilAsync(
            () => browseService.CategoryViewerCountRequests.Count == 23,
            TimeSpan.FromMilliseconds(500));

        for (var index = 0; index < 23; index++)
        {
            Assert.SequenceEqual([$"id-{index + 1:00}"], browseService.CategoryViewerCountRequests[index].CategoryIds);
        }
    }),
    ("browse load more queues new exact viewer counts without restarting active count load", async () =>
    {
        var firstCountResult = new TaskCompletionSource<BrowseResult<BrowseCategoryViewerCount>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browseService = new FakeBrowseService
        {
            CategoryViewerCountResponder = request =>
                request.CategoryIds[0] == "1"
                    ? firstCountResult.Task
                    : Task.FromResult(new BrowseResult<BrowseCategoryViewerCount>(
                        BrowseResultStatus.Available,
                        [new BrowseCategoryViewerCount("2", 2000)],
                        "",
                        "Loaded exact Twitch viewer counts."))
        };
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "1", "One", "", [])],
            "next-categories",
            "Page 1"));
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "2", "Two", "", [])],
            "",
            "Page 2"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        await TestWait.UntilAsync(
            () => browseService.CategoryViewerCountRequests.Count == 1,
            TimeSpan.FromMilliseconds(500));

        await viewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync();

        Assert.Equal(2, viewModel.BrowseCategories.Count);
        Assert.Equal(1, browseService.CategoryViewerCountRequests.Count);
        Assert.SequenceEqual(["1"], browseService.CategoryViewerCountRequests[0].CategoryIds);

        firstCountResult.SetResult(new BrowseResult<BrowseCategoryViewerCount>(
            BrowseResultStatus.Available,
            [new BrowseCategoryViewerCount("1", 1000)],
            "",
            "Loaded exact Twitch viewer counts."));
        await TestWait.UntilAsync(
            () => browseService.CategoryViewerCountRequests.Count == 2,
            TimeSpan.FromMilliseconds(500));

        Assert.SequenceEqual(["2"], browseService.CategoryViewerCountRequests[1].CategoryIds);
    }),
    ("browse category click cancels background exact viewer count loading", async () =>
    {
        var countStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countResult = new TaskCompletionSource<BrowseResult<BrowseCategoryViewerCount>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browseService = new FakeBrowseService
        {
            CategoryViewerCountResponderWithCancellation = (request, cancellationToken) =>
            {
                countStarted.TrySetResult();
                cancellationToken.Register(() => countCanceled.TrySetResult());
                return countResult.Task;
            }
        };
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "509658", "Just Chatting", "https://example/twitch.jpg", [])],
            "",
            "Loaded Twitch categories"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer",
                "Streamer",
                "Live now",
                "509658",
                "Just Chatting",
                100,
                "https://example/live.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer")],
            "",
            "Loaded Twitch streams"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        await countStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        await viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();

        await countCanceled.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(true, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal(1, viewModel.BrowseStreams.Count);

        countResult.SetResult(new BrowseResult<BrowseCategoryViewerCount>(
            BrowseResultStatus.Available,
            [new BrowseCategoryViewerCount("509658", 12345)],
            "",
            "Loaded exact Twitch viewer counts."));
        await Task.Delay(50);
        Assert.Equal("Twitch", viewModel.BrowseCategories[0].MetadataText);
    }),
    ("browse platform toggle from channel page returns to categories and loads selected categories", async () =>
    {
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "1", "Twitch Cat", "https://example/twitch.jpg", [])],
            "",
            "Loaded Twitch"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer",
                "Streamer",
                "Live now",
                "1",
                "Twitch Cat",
                100,
                "https://example/live.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer")],
            "",
            "Loaded streams"));
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Kick, "2", "Kick Cat", "https://example/kick.jpg", [])],
            "",
            "Loaded Kick"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        Assert.Equal(1, viewModel.BrowseCategories.Count);
        Assert.Equal(PlatformKind.Twitch, viewModel.BrowseCategories[0].Platform);
        await viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        Assert.Equal(true, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal(1, viewModel.BrowseStreams.Count);

        viewModel.SelectKickBrowsePlatformCommand.Execute(null);
        Assert.Equal(PlatformKind.Kick, viewModel.SelectedBrowsePlatform);
        Assert.Equal(true, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(false, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal<BrowseCategoryViewModel?>(null, viewModel.SelectedBrowseCategory);
        Assert.Equal(1, viewModel.BrowseCategories.Count);
        Assert.Equal("Kick Cat", viewModel.BrowseCategories[0].Name);
        Assert.Equal(0, viewModel.BrowseStreams.Count);
        Assert.Equal(2, browseService.CategoryRequests.Count);
        Assert.Equal(PlatformKind.Kick, browseService.CategoryRequests[^1].Platform);
    }),
    ("browse selected platform click from channel page returns to preserved categories", async () =>
    {
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "509658", "Just Chatting", "", [])],
            "",
            "Loaded Twitch categories"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer",
                "Streamer",
                "Live now",
                "509658",
                "Just Chatting",
                100,
                "https://example/live.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer")],
            "",
            "Loaded Twitch streams"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        await viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        Assert.Equal(true, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal("Loaded Twitch streams", viewModel.BrowseStatus);
        browseService.ClearRequests();

        viewModel.SelectTwitchBrowsePlatformCommand.Execute(null);

        Assert.Equal(PlatformKind.Twitch, viewModel.SelectedBrowsePlatform);
        Assert.Equal(true, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(false, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal(1, viewModel.BrowseCategories.Count);
        Assert.Equal("Just Chatting", viewModel.BrowseCategories[0].Name);
        Assert.Equal<BrowseCategoryViewModel?>(null, viewModel.SelectedBrowseCategory);
        Assert.Equal(0, viewModel.BrowseStreams.Count);
        Assert.Equal("Loaded Twitch categories", viewModel.BrowseStatus);
        Assert.Equal(0, browseService.CategoryRequests.Count);
        Assert.Equal(0, browseService.StreamRequests.Count);
    }),
    ("browse back command from channel page returns to preserved categories", async () =>
    {
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "509658", "Just Chatting", "", [])],
            "",
            "Loaded Twitch categories"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer",
                "Streamer",
                "Live now",
                "509658",
                "Just Chatting",
                100,
                "https://example/live.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer")],
            "",
            "Loaded Twitch streams"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        Assert.Equal(false, viewModel.ReturnToBrowseCategoriesCommand.CanExecute(null));

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        Assert.Equal(false, viewModel.ReturnToBrowseCategoriesCommand.CanExecute(null));
        await viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        Assert.Equal(true, viewModel.ReturnToBrowseCategoriesCommand.CanExecute(null));
        browseService.ClearRequests();

        viewModel.ReturnToBrowseCategoriesCommand.Execute(null);

        Assert.Equal(true, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(false, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal(1, viewModel.BrowseCategories.Count);
        Assert.Equal("Just Chatting", viewModel.BrowseCategories[0].Name);
        Assert.Equal<BrowseCategoryViewModel?>(null, viewModel.SelectedBrowseCategory);
        Assert.Equal(0, viewModel.BrowseStreams.Count);
        Assert.Equal("Loaded Twitch categories", viewModel.BrowseStatus);
        Assert.Equal(false, viewModel.ReturnToBrowseCategoriesCommand.CanExecute(null));
        Assert.Equal(0, browseService.CategoryRequests.Count);
        Assert.Equal(0, browseService.StreamRequests.Count);
    }),
    ("browse category search debounces to latest query", async () =>
    {
        var browseService = new FakeBrowseService
        {
            CategoryResponder = request => new BrowseResult<BrowseCategory>(
                BrowseResultStatus.Available,
                [new BrowseCategory(request.Platform, request.Query, request.Query, "", [])],
                "",
                $"Loaded {request.Query}")
        };
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService,
            browseCategorySearchDebounceInterval: TimeSpan.FromMilliseconds(25));

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        browseService.ClearRequests();
        viewModel.BrowseCategorySearchText = "ru";
        viewModel.BrowseCategorySearchText = "rust";
        await Task.Delay(120);

        Assert.Equal(1, browseService.CategoryRequests.Count);
        Assert.Equal("rust", browseService.CategoryRequests[0].Query);
        Assert.Equal(1, viewModel.BrowseCategories.Count);
        Assert.Equal("rust", viewModel.BrowseCategories[0].Name);
    }),
    ("browse category click loads category streams", async () =>
    {
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "509658", "Just Chatting", "", [])],
            "",
            "Loaded categories"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer",
                "Streamer",
                "Live now",
                "509658",
                "Just Chatting",
                100,
                "https://example/live.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer")],
            "",
            "Loaded streams"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        Assert.Equal(true, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(false, viewModel.IsBrowseStreamsPageVisible);
        await viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();

        Assert.Equal(false, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(true, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal("Just Chatting", viewModel.SelectedBrowseCategoryName);
        Assert.Equal(1, viewModel.BrowseStreams.Count);
        Assert.Equal("streamer", viewModel.BrowseStreams[0].Channel);
        Assert.Equal("509658", browseService.StreamRequests[0].CategoryId);
    }),
    ("browse segment from channel page returns to preserved categories and clears channel results", async () =>
    {
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "509658", "Just Chatting", "", [])],
            "",
            "Loaded categories"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer",
                "Streamer",
                "Live now",
                "509658",
                "Just Chatting",
                100,
                "https://example/live.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer")],
            "",
            "Loaded streams"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        await viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        Assert.Equal(true, viewModel.IsBrowseStreamsPageVisible);
        browseService.ClearRequests();

        viewModel.ShowBrowseHomePageCommand.Execute(null);

        Assert.Equal(true, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(false, viewModel.IsBrowseStreamsPageVisible);
        Assert.Equal(1, viewModel.BrowseCategories.Count);
        Assert.Equal("Just Chatting", viewModel.BrowseCategories[0].Name);
        Assert.Equal<BrowseCategoryViewModel?>(null, viewModel.SelectedBrowseCategory);
        Assert.Equal(0, viewModel.BrowseStreams.Count);
        Assert.Equal("Loaded categories", viewModel.BrowseStatus);
        Assert.Equal(0, browseService.CategoryRequests.Count);
        Assert.Equal(0, browseService.StreamRequests.Count);
    }),
    ("browse Kick load more sends cursor and appends without duplicating categories", async () =>
    {
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Kick, "1", "One", "", [], 10)],
            "kick-next-categories",
            "Page 1"));
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [
                new BrowseCategory(PlatformKind.Kick, "1", "One Duplicate", "", [], 1),
                new BrowseCategory(PlatformKind.Kick, "2", "Two", "", [], 25)
            ],
            "",
            "Page 2"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.SelectKickBrowsePlatformCommand.Execute(null);
        viewModel.ShowBrowseHomePageCommand.Execute(null);
        browseService.ClearRequests();
        await viewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync();

        Assert.Equal(PlatformKind.Kick, viewModel.SelectedBrowsePlatform);
        Assert.Equal(1, browseService.CategoryRequests.Count);
        Assert.Equal(PlatformKind.Kick, browseService.CategoryRequests[0].Platform);
        Assert.Equal("kick-next-categories", browseService.CategoryRequests[0].Cursor);
        Assert.Equal(2, viewModel.BrowseCategories.Count);
        Assert.SequenceEqual(new[] { "Two", "One" }, viewModel.BrowseCategories.Select(category => category.Name).ToArray());
    }),
    ("browse refresh and load more target the visible browse subpage", async () =>
    {
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "101", "Rust", "", [])],
            "category-cursor",
            "Category page 1"));
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "102", "Games", "", [])],
            "",
            "Category page 2"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer",
                "Streamer",
                "Rust drops",
                "101",
                "Rust",
                500,
                "https://example/live-1.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer")],
            "stream-cursor",
            "Stream page 1"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer-refresh",
                "Streamer Refresh",
                "Rust refreshed",
                "101",
                "Rust",
                600,
                "https://example/live-2.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer-refresh")],
            "stream-refresh-cursor",
            "Stream refresh"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer-more",
                "Streamer More",
                "More Rust",
                "101",
                "Rust",
                700,
                "https://example/live-3.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer-more")],
            "",
            "More streams"));
        var viewModel = TestViewModels.CreateMain(
            new AppSettings(),
            new FakeSettingsService(new AppSettings()),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        browseService.ClearRequests();
        await viewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync();

        Assert.Equal(true, viewModel.IsBrowseCategoriesPageVisible);
        Assert.Equal(1, browseService.CategoryRequests.Count);
        Assert.Equal("category-cursor", browseService.CategoryRequests[0].Cursor);
        Assert.Equal(0, browseService.StreamRequests.Count);
        Assert.Equal(2, viewModel.BrowseCategories.Count);

        await viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        Assert.Equal(true, viewModel.IsBrowseStreamsPageVisible);
        browseService.ClearRequests();
        await viewModel.RefreshBrowseCommand.ExecuteAsync();

        Assert.Equal(0, browseService.CategoryRequests.Count);
        Assert.Equal(1, browseService.StreamRequests.Count);
        Assert.Equal("", browseService.StreamRequests[0].Cursor);
        Assert.Equal("101", browseService.StreamRequests[0].CategoryId);

        browseService.ClearRequests();
        await viewModel.LoadMoreBrowseCategoriesCommand.ExecuteAsync();
        Assert.Equal(0, browseService.CategoryRequests.Count);
        Assert.Equal(0, browseService.StreamRequests.Count);

        await viewModel.LoadMoreBrowseStreamsCommand.ExecuteAsync();
        Assert.Equal(0, browseService.CategoryRequests.Count);
        Assert.Equal(1, browseService.StreamRequests.Count);
        Assert.Equal("stream-refresh-cursor", browseService.StreamRequests[0].Cursor);
        Assert.Equal(2, viewModel.BrowseStreams.Count);
    }),
    ("browse stream open uses live playback path and can keep Home selected", async () =>
    {
        var settings = new AppSettings
        {
            StreamlinkPath = "streamlink.exe",
            VlcDirectory = "vlc"
        };
        var browseService = new FakeBrowseService();
        browseService.EnqueueCategories(new BrowseResult<BrowseCategory>(
            BrowseResultStatus.Available,
            [new BrowseCategory(PlatformKind.Twitch, "101", "Rust", "", [])],
            "",
            "Loaded categories"));
        browseService.EnqueueStreams(new BrowseResult<BrowseLiveStream>(
            BrowseResultStatus.Available,
            [new BrowseLiveStream(
                PlatformKind.Twitch,
                "streamer",
                "Streamer",
                "Rust drops",
                "101",
                "Rust",
                500,
                "https://example/kick-live.jpg",
                null,
                false,
                "en",
                "https://www.twitch.tv/streamer")],
            "",
            "Loaded streams"));
        var viewModel = TestViewModels.CreateMain(
            settings,
            new FakeSettingsService(settings),
            new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(),
            new MemoryLogger(),
            action => action(),
            browseService: browseService);

        viewModel.ShowBrowseHomePageCommand.Execute(null);
        await viewModel.BrowseCategories[0].SelectCommand.ExecuteAsync();
        await viewModel.BrowseStreams[0].OpenAndStayOnHomeCommand.ExecuteAsync();

        Assert.Equal(1, viewModel.Tabs.Count);
        Assert.Equal<StreamTabViewModel?>(null, viewModel.SelectedTab);
        Assert.Equal(true, viewModel.IsHomeSelected);
        Assert.Equal("streamer", viewModel.Tabs[0].Target.Channel);
        Assert.Equal(PlatformKind.Twitch, viewModel.Tabs[0].Target.Platform);
        Assert.Equal("Rust", viewModel.Tabs[0].Target.CategoryName);
        Assert.Equal("Rust", viewModel.TabStripItems.Single().SubtitleText);
    }),
    ("parses Twitch VOD durations", () =>
    {
        Assert.True(ReplayResolver.TryParseTwitchDuration("3h8m33s", out var duration));
        Assert.Equal(new TimeSpan(3, 8, 33), duration);
        Assert.True(ReplayResolver.TryParseTwitchDuration("42m7s", out duration));
        Assert.Equal(new TimeSpan(0, 42, 7), duration);
        Assert.True(ReplayResolver.TryParseTwitchDuration("18s", out duration));
        Assert.Equal(TimeSpan.FromSeconds(18), duration);
        Assert.Equal(false, ReplayResolver.TryParseTwitchDuration("not-a-duration", out _));
        return Task.CompletedTask;
    }),
    ("rejects non-finite and overflowing Twitch replay durations", () =>
    {
        var overflowingNumber = new string('9', 400);

        Assert.Equal(false, ReplayResolver.TryParseTwitchDuration($"{overflowingNumber}h", out var vodDuration));
        Assert.Equal(TimeSpan.Zero, vodDuration);
        Assert.Equal(false, ReplayResolver.TryReadTwitchDvrTotalSeconds(
            $"#EXTM3U\n#EXT-X-TWITCH-TOTAL-SECS:{overflowingNumber}",
            out var dvrDuration));
        Assert.Equal(TimeSpan.Zero, dvrDuration);
        Assert.Equal(false, ReplayResolver.TryReadTwitchDvrTotalSeconds(
            "#EXTM3U\n#EXT-X-TWITCH-TOTAL-SECS:NaN",
            out _));
        Assert.Equal(false, ReplayResolver.TryReadTwitchDvrTotalSeconds(
            "#EXTM3U\n#EXT-X-TWITCH-TOTAL-SECS:Infinity",
            out _));
        return Task.CompletedTask;
    }),
    ("matches Twitch VOD by stream id before start time", () =>
    {
        var live = new TwitchLiveStreamInfo("user-1", "stream-abc", new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero));
        var byTime = new TwitchVodInfo("vod-time", "", "https://www.twitch.tv/videos/1", live.StartedAtUtc.AddMinutes(2), TimeSpan.FromHours(1));
        var byStream = new TwitchVodInfo("vod-stream", "stream-abc", "https://www.twitch.tv/videos/2", live.StartedAtUtc.AddHours(5), TimeSpan.FromHours(2));

        var match = ReplayResolver.MatchTwitchVod(live, [byTime, byStream]);

        Assert.NotNull(match);
        Assert.Equal("vod-stream", match!.Id);
        return Task.CompletedTask;
    }),
    ("matches Twitch VOD by nearest start time when stream id is missing", () =>
    {
        var live = new TwitchLiveStreamInfo("user-1", "stream-abc", new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero));
        var tooFar = new TwitchVodInfo("vod-far", "", "https://www.twitch.tv/videos/1", live.StartedAtUtc.AddHours(-2), TimeSpan.FromHours(1));
        var near = new TwitchVodInfo("vod-near", "", "https://www.twitch.tv/videos/2", live.StartedAtUtc.AddMinutes(8), TimeSpan.FromHours(2));

        var match = ReplayResolver.MatchTwitchVod(live, [tooFar, near]);

        Assert.NotNull(match);
        Assert.Equal("vod-near", match!.Id);
        return Task.CompletedTask;
    }),
    ("does not match Twitch VOD by time when stream id is different", () =>
    {
        var live = new TwitchLiveStreamInfo("user-1", "stream-abc", new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero));
        var mismatched = new TwitchVodInfo("vod-other", "stream-other", "https://www.twitch.tv/videos/1", live.StartedAtUtc.AddMinutes(1), TimeSpan.FromHours(1));

        var match = ReplayResolver.MatchTwitchVod(live, [mismatched]);

        Assert.Equal<TwitchVodInfo?>(null, match);
        return Task.CompletedTask;
    }),
    ("parses Twitch DVR total seconds from playlist", () =>
    {
        var playlist = """
        #EXTM3U
        #EXT-X-TWITCH-TOTAL-SECS:3723.5
        #EXTINF:10.000,
        0.ts
        """;

        Assert.True(ReplayResolver.TryReadTwitchDvrTotalSeconds(playlist, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(3723.5), duration);
        Assert.True(ReplayResolver.IsValidTwitchDvrPlaylist(playlist));
        Assert.Equal(false, ReplayResolver.IsValidTwitchDvrPlaylist("not a playlist"));
        return Task.CompletedTask;
    }),
    ("resolves Twitch replay from GraphQL archive preview HLS after Helix miss", async () =>
    {
        const string streamId = "123456789";
        const string vodId = "2786354640";
        const string dvrPath = "abcdefabcdefabcdefab_streamer_123456789_1780344000";
        var expectedUrl = $"https://ds0h3roq6wcgc.cloudfront.net/{dvrPath}/chunked/index-dvr.m3u8";
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:replay-archive-preview-token";
        var gqlRequests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                Assert.Equal("Bearer replay-archive-preview-token", request.Headers.Authorization?.ToString());
                return CreateTwitchTokenValidationResponse("twitch-client-id");
            }

            if (request.RequestUri!.Host == "api.twitch.tv" &&
                request.RequestUri.AbsolutePath == "/helix/streams")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "user_login": "streamer",
                          "user_id": "user-1",
                          "id": "123456789",
                          "started_at": "2026-06-01T20:00:00Z"
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host == "api.twitch.tv" &&
                request.RequestUri.AbsolutePath == "/helix/videos")
            {
                Assert.Contains("type=archive", request.RequestUri.Query);
                Assert.Contains("first=100", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host == "gql.twitch.tv")
            {
                gqlRequests++;
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
                Assert.Contains("FilterableVideoTower_Videos", body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    [
                      {
                        "data": {
                          "user": {
                            "videos": {
                              "edges": [
                                {
                                  "node": {
                                    "id": "2786354640",
                                    "title": "current archive",
                                    "createdAt": "2026-06-01T20:00:00Z",
                                    "publishedAt": "2026-06-01T20:00:00Z",
                                    "lengthSeconds": 7200,
                                    "broadcastType": "ARCHIVE",
                                    "animatedPreviewURL": "https://ds0h3roq6wcgc.cloudfront.net/abcdefabcdefabcdefab_streamer_123456789_1780344000/storyboards/2786354640-strip-0.jpg"
                                  }
                                }
                              ]
                            }
                          }
                        }
                      }
                    ]
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host == "ds0h3roq6wcgc.cloudfront.net" &&
                request.RequestUri.AbsolutePath == $"/{dvrPath}/chunked/index-dvr.m3u8")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-TWITCH-TOTAL-SECS:3600
                    #EXTINF:10.000,
                    0.ts
                    """, Encoding.UTF8, "application/vnd.apple.mpegurl")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }));
        var resolver = new ReplayResolver(
            new MemoryLogger(), new FakeStreamlinkService(), httpClient, TestReplayUrlSecurity.PublicValidator);

        var replay = await resolver.ResolveCurrentReplayAsync(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            settings);

        Assert.Equal(true, replay.IsAvailable);
        Assert.Equal(vodId, replay.ReplayId);
        Assert.Contains(streamId, replay.ReplayUrl);
        Assert.Equal(expectedUrl, replay.ReplayUrl);
        Assert.Equal("best", replay.StreamlinkQuality);
        Assert.Equal(TimeSpan.FromHours(1), replay.Duration);
        Assert.Equal("user-1", replay.ChatRoomId);
        Assert.Equal(1, gqlRequests);
    }),
    ("resolves Twitch current live DVR after Helix and GraphQL miss", async () =>
    {
        const string streamId = "123456789";
        const long startSeconds = 1780344000;
        const long expectedSeconds = startSeconds + 1;
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes($"streamer_{streamId}_{expectedSeconds}"))).ToLowerInvariant()[..20];
        var expectedPath = $"/{expectedHash}_streamer_{streamId}_{expectedSeconds}/chunked/index-dvr.m3u8";
        var expectedUrl = $"https://d1g1f25tn8m2e6.cloudfront.net{expectedPath}";
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:dvr-current-token";
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"client_id":"twitch-client-id","login":"streamer","user_id":"user-1","scopes":[],"expires_in":3600}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.RequestUri.Host == "api.twitch.tv" &&
                request.RequestUri.AbsolutePath == "/helix/streams")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "user_login": "streamer",
                          "user_id": "user-1",
                          "id": "123456789",
                          "started_at": "2026-06-01T20:00:00Z"
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host == "api.twitch.tv" &&
                request.RequestUri.AbsolutePath == "/helix/videos")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host == "gql.twitch.tv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"data":{"user":{"videos":{"edges":[]}}}}]""", Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase) &&
                request.RequestUri.AbsolutePath == expectedPath)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-TWITCH-TOTAL-SECS:42
                    #EXTINF:10.000,
                    0.ts
                    """, Encoding.UTF8, "application/vnd.apple.mpegurl")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }));
        var resolver = new ReplayResolver(
            new MemoryLogger(), new FakeStreamlinkService(), httpClient, TestReplayUrlSecurity.PublicValidator);

        var replay = await resolver.ResolveCurrentReplayAsync(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            settings);

        Assert.Equal(true, replay.IsAvailable);
        Assert.Equal("live-dvr-123456789", replay.ReplayId);
        Assert.Equal(expectedUrl, replay.ReplayUrl);
        Assert.Equal("best", replay.StreamlinkQuality);
        Assert.Equal(TimeSpan.FromSeconds(42), replay.Duration);
        Assert.Equal("user-1", replay.ChatRoomId);
    }),
    ("fails Twitch current DVR probing closed for mismatched and invalid playlists", async () =>
    {
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "twitch-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:dvr-invalid-token";
        var returnedNonHls = false;
        var mismatchedPreviewRequests = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"client_id":"twitch-client-id","login":"streamer","user_id":"user-1","scopes":[],"expires_in":3600}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.RequestUri.Host == "api.twitch.tv" &&
                request.RequestUri.AbsolutePath == "/helix/streams")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                      "data": [
                        {
                          "user_login": "streamer",
                          "user_id": "user-1",
                          "id": "123456789",
                          "started_at": "2026-06-01T20:00:00Z"
                        }
                      ]
                    }
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host == "api.twitch.tv" &&
                request.RequestUri.AbsolutePath == "/helix/videos")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host == "gql.twitch.tv")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    [
                      {
                        "data": {
                          "user": {
                            "videos": {
                              "edges": [
                                {
                                  "node": {
                                    "id": "2786354640",
                                    "createdAt": "2026-06-01T20:00:00Z",
                                    "publishedAt": "2026-06-01T20:00:00Z",
                                    "lengthSeconds": 7200,
                                    "broadcastType": "ARCHIVE",
                                    "animatedPreviewURL": "https://ds0h3roq6wcgc.cloudfront.net/bbbbbbbbbbbbbbbbbbbb_streamer_999999999_1780344000/storyboards/2786354640-strip-0.jpg"
                                  }
                                }
                              ]
                            }
                          }
                        }
                      }
                    ]
                    """, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase))
            {
                if (request.RequestUri.AbsolutePath.Contains("_999999999_", StringComparison.Ordinal))
                {
                    mismatchedPreviewRequests++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                        #EXTM3U
                        #EXTINF:10.000,
                        0.ts
                        """, Encoding.UTF8, "application/vnd.apple.mpegurl")
                    };
                }

                if (!returnedNonHls)
                {
                    returnedNonHls = true;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("not hls", Encoding.UTF8, "text/plain")
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        }));
        var resolver = new ReplayResolver(
            new MemoryLogger(), new FakeStreamlinkService(), httpClient, TestReplayUrlSecurity.PublicValidator);

        var replay = await resolver.ResolveCurrentReplayAsync(
            StreamInputParser.Parse("streamer", PlatformKind.Twitch),
            "source",
            settings);

        Assert.Equal(false, replay.IsAvailable);
        Assert.Contains("current-live DVR", replay.UnavailableReason);
        Assert.Equal(0, mismatchedPreviewRequests);
        Assert.True(returnedNonHls);
    }),
    ("reads Kick live stream failure states", () =>
    {
        using var offline = JsonDocument.Parse("""{"data":[{"slug":"xqc","stream":null}]}""");
        Assert.Equal<KickLiveStreamInfo?>(null, ReplayResolver.ReadKickLiveStream(offline.RootElement, "xqc"));

        using var live = JsonDocument.Parse("""{"data":[{"slug":"xqc","stream":{"id":123,"is_live":true,"started_at":"2026-06-01T20:00:00Z"}}]}""");
        var stream = ReplayResolver.ReadKickLiveStream(live.RootElement, "xqc");
        Assert.NotNull(stream);
        Assert.Equal("123", stream!.StreamId);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero), stream.StartedAtUtc);
        return Task.CompletedTask;
    }),
    ("reads Kick website live stream metadata", () =>
    {
        using var live = JsonDocument.Parse("""
        {
          "slug": "xqc",
          "livestream": {
            "id": 111132734,
            "is_live": true,
            "start_time": "2026-06-01 18:46:11"
          }
        }
        """);

        var stream = ReplayResolver.ReadKickWebsiteLiveStream(live.RootElement, "xqc");

        Assert.NotNull(stream);
        Assert.Equal("111132734", stream!.StreamId);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 18, 46, 11, TimeSpan.Zero), stream.StartedAtUtc);
        return Task.CompletedTask;
    }),
    ("extracts Kick private replay candidates", () =>
    {
        var body = """
        {
          "data": [
            {
              "uuid": "abc-123",
              "created_at": "2026-06-01T20:04:00Z",
              "duration": 3600,
              "playback_url": "https:\/\/stream.kick.com\/replay\/abc-123\/index.m3u8"
            }
          ]
        }
        """;

        var candidates = ReplayResolver.ReadKickPrivateReplayCandidates(
            "xqc",
            body,
            new KickLiveStreamInfo(
                "",
                new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero)));

        Assert.True(candidates.Count >= 1);
        Assert.True(candidates.Any(candidate => candidate.Url == "https://stream.kick.com/replay/abc-123/index.m3u8"));
        Assert.True(candidates.Any(candidate => candidate.Url == "https://kick.com/xqc/videos/abc-123"));
        return Task.CompletedTask;
    }),
    ("extracts current Kick videos API source and video uuid", () =>
    {
        var body = """
        [
          {
            "id": 111132734,
            "slug": "0486df59-live-not-a-video-uuid",
            "created_at": "2026-06-01 18:46:16",
            "is_live": true,
            "start_time": "2026-06-01 18:46:11",
            "source": "https:\/\/stream.kick.com\/channel\/2026\/6\/1\/18\/46\/live123\/media\/hls\/master.m3u8",
            "duration": 0,
            "video": {
              "id": 106839871,
              "live_stream_id": 111132734,
              "uuid": "947a530d-12e6-4029-9a47-c48bcb27e827"
            }
          },
          {
            "id": 110881803,
            "slug": "old-stream-slug",
            "created_at": "2026-05-30 21:58:09",
            "is_live": false,
            "start_time": "2026-05-30 21:58:05",
            "source": "https:\/\/stream.kick.com\/channel\/2026\/5\/30\/21\/58\/old456\/media\/hls\/master.m3u8",
            "duration": 33416000,
            "video": {
              "id": 106589029,
              "live_stream_id": 110881803,
              "uuid": "0dd9f4ab-ca10-43ef-98e4-0730da7a7ca1"
            }
          }
        ]
        """;

        var candidates = ReplayResolver.ReadKickPrivateReplayCandidates(
            "xqc",
            body,
            new KickLiveStreamInfo("111132734", new DateTimeOffset(2026, 6, 1, 18, 46, 11, TimeSpan.Zero)));

        Assert.Equal("https://stream.kick.com/channel/2026/6/1/18/46/live123/media/hls/master.m3u8", candidates[0].Url);
        Assert.True(candidates.Any(candidate => candidate.Url == "https://kick.com/xqc/videos/947a530d-12e6-4029-9a47-c48bcb27e827"));
        Assert.Equal(false, candidates.Any(candidate => candidate.Url.Contains("old456", StringComparison.Ordinal)));
        Assert.Equal(false, candidates.Any(candidate => candidate.Url == "https://kick.com/xqc/videos/0486df59-live-not-a-video-uuid"));
        Assert.Equal(false, candidates.Any(candidate => candidate.Url == "https://kick.com/xqc/videos/0dd9f4ab-ca10-43ef-98e4-0730da7a7ca1"));
        return Task.CompletedTask;
    }),
    ("reads Kick videos API duration as milliseconds", () =>
    {
        var body = """
        [
          {
            "id": 110881803,
            "created_at": "2026-05-30 21:58:09",
            "source": "https:\/\/stream.kick.com\/channel\/2026\/5\/30\/21\/58\/old456\/media\/hls\/master.m3u8",
            "duration": 33416000,
            "video": {
              "live_stream_id": 110881803,
              "uuid": "0dd9f4ab-ca10-43ef-98e4-0730da7a7ca1"
            }
          }
        ]
        """;

        var candidates = ReplayResolver.ReadKickPrivateReplayCandidates(
            "xqc",
            body,
            new KickLiveStreamInfo("110881803", new DateTimeOffset(2026, 5, 30, 21, 58, 5, TimeSpan.Zero)));

        Assert.True(candidates.Count > 0);
        Assert.Equal(new TimeSpan(9, 16, 56), candidates[0].Duration);
        return Task.CompletedTask;
    }),
    ("rejects non-finite and overflowing Kick replay durations", () =>
    {
        var body = """
        [
          {
            "id": "current-stream",
            "source": "https://stream.kick.com/replay/overflow/index.m3u8",
            "duration": 1e999
          },
          {
            "id": "current-stream",
            "source": "https://stream.kick.com/replay/non-finite/index.m3u8",
            "duration_seconds": "NaN"
          }
        ]
        """;

        var candidates = ReplayResolver.ReadKickPrivateReplayCandidates(
            "streamer",
            body,
            new KickLiveStreamInfo("current-stream", null));

        Assert.Equal(2, candidates.Count);
        Assert.True(candidates.All(candidate => candidate.Duration == TimeSpan.Zero));
        return Task.CompletedTask;
    }),
    ("does not recover mismatched Kick replay URLs from valid JSON", () =>
    {
        var body = """
        [
          {
            "id": "old-stream",
            "live_stream_id": "old-stream",
            "created_at": "2026-05-30T20:00:00Z",
            "source": "https://stream.kick.com/replay/old-stream/index.m3u8",
            "video": {
              "live_stream_id": "old-stream",
              "uuid": "0dd9f4ab-ca10-43ef-98e4-0730da7a7ca1"
            }
          }
        ]
        """;

        var candidates = ReplayResolver.ReadKickPrivateReplayCandidates(
            "streamer",
            body,
            new KickLiveStreamInfo(
                "current-stream",
                new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero)));

        Assert.Equal(0, candidates.Count);
        return Task.CompletedTask;
    }),
    ("applies nested Kick video duration to replay candidates", () =>
    {
        var body = """
        {
          "id": "current-stream",
          "source": "https://stream.kick.com/replay/current-stream/index.m3u8",
          "video": {
            "live_stream_id": "current-stream",
            "uuid": "947a530d-12e6-4029-9a47-c48bcb27e827",
            "duration": 120000
          }
        }
        """;

        var candidates = ReplayResolver.ReadKickPrivateReplayCandidates(
            "streamer",
            body,
            new KickLiveStreamInfo("current-stream", null));

        var source = candidates.Single(candidate => candidate.Url.EndsWith("index.m3u8", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromMinutes(2), source.Duration);
        var video = candidates.Single(candidate => candidate.Url.Contains("/videos/", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromMinutes(2), video.Duration);
        return Task.CompletedTask;
    }),
    ("keeps Kick replay seekable when the reported start time is in the future", async () =>
    {
        var futureStart = DateTimeOffset.UtcNow.AddMinutes(10);
        var responseBody = """
        {
          "id": "stream-1",
          "source": "https://stream.kick.com/replay/stream-1/index.m3u8",
          "created_at": "FUTURE_START"
        }
        """.Replace(
            "FUTURE_START",
            futureStart.ToString("O", CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host.Equals("api.kick.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"data\":[{{\"slug\":\"streamer\",\"stream\":{{\"id\":\"stream-1\",\"is_live\":true,\"started_at\":\"{futureStart:O}\"}}}}]}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (uri.AbsolutePath.EndsWith("/api/v2/channels/streamer/videos", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        }));

        var settings = new AppSettings();
        settings.Replay.Enabled = true;
        settings.Replay.AttemptPrivateKickReplayResolution = true;
        settings.Chat.KickOAuthToken = "kick-token";
        settings.Chat.KickTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(
                new Uri("https://stream.kick.com/replay/stream-1/720p/index.m3u8"),
                "resolved"))
        };
        var resolver = new ReplayResolver(
            new MemoryLogger(), streamlink, httpClient, TestReplayUrlSecurity.PublicValidator);

        var replay = await resolver.ResolveCurrentReplayAsync(
            StreamInputParser.Parse("https://kick.com/streamer", PlatformKind.Twitch),
            "best",
            settings);

        Assert.True(replay.IsAvailable);
        Assert.Equal(TimeSpan.FromHours(12), replay.Duration);
        return;
    }),
    ("maps TwitchDownloader replay chat messages by offset", () =>
    {
        using var document = JsonDocument.Parse("""
        {
          "comments": [
            {
              "_id": "msg-1",
              "channel_id": "channel-room-id",
              "content_offset_seconds": 12.5,
              "commenter": { "display_name": "ViewerOne", "name": "viewerone" },
              "message": {
                "body": "Kappa hello replay",
                "fragments": [
                  { "text": "Kappa", "emoticon": { "emoticon_id": "25" } },
                  { "text": " hello replay" }
                ],
                "emoticons": [{ "_id": "25", "begin": 0, "end": 4 }],
                "user_color": "#48C7B5",
                "user_badges": [
                  {
                    "_id": "subscriber",
                    "version": "12",
                    "title": "12-Month Subscriber",
                    "image_url": "https://static-cdn.jtvnw.net/badges/v1/channel-subscriber/3"
                  }
                ]
              }
            },
            {
              "_id": "msg-2",
              "content_offset_seconds": 18,
              "commenter": { "name": "viewer_two" },
              "message": {
                "fragments": [{ "text": "split " }, { "text": "message" }],
                "userBadges": [{ "_id": "OZS=", "setID": "moderator", "version": "1" }]
              }
            }
          ]
        }
        """);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            true,
            "",
            ChatRoomId: "fallback-room-id");

        var messages = ReplayChatProvider.ReadTwitchDownloaderMessages(document.RootElement, replay);

        Assert.Equal(2, messages.Count);
        Assert.Equal(TimeSpan.FromSeconds(12.5), messages[0].Offset);
        Assert.Equal("ViewerOne", messages[0].Message.Username);
        Assert.Equal("Kappa hello replay", messages[0].Message.Message);
        Assert.Equal("msg-1", messages[0].Message.MessageId);
        Assert.Equal("channel-room-id", messages[0].Message.RoomId);
        var firstEmotes = messages[0].Message.Emotes ?? throw new InvalidOperationException("Expected first replay message emotes.");
        Assert.Equal(1, firstEmotes.Count);
        Assert.Equal("Kappa", firstEmotes[0].Code);
        Assert.Equal(0, firstEmotes[0].StartIndex);
        Assert.Equal(5, firstEmotes[0].EndIndex);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/emoticons/v2/25/static/light/2.0",
            firstEmotes[0].ImageUrl);
        var firstBadges = messages[0].Message.Badges ?? throw new InvalidOperationException("Expected first replay message badges.");
        Assert.Equal("subscriber", firstBadges[0].Id);
        Assert.Equal("12", firstBadges[0].Version);
        Assert.Equal("12-Month Subscriber", firstBadges[0].Title);
        Assert.Equal("https://static-cdn.jtvnw.net/badges/v1/channel-subscriber/3", firstBadges[0].ImageUrl);
        Assert.Equal("split message", messages[1].Message.Message);
        Assert.Equal("fallback-room-id", messages[1].Message.RoomId);
        var secondBadges = messages[1].Message.Badges ?? throw new InvalidOperationException("Expected second replay message badges.");
        Assert.Equal("moderator", secondBadges[0].Id);
        Assert.Equal("1", secondBadges[0].Version);
        Assert.Equal("Moderator", secondBadges[0].Title);
        return Task.CompletedTask;
    }),
    ("maps Twitch VOD Downloader GraphQL replay chat messages by offset", () =>
    {
        using var document = JsonDocument.Parse("""
        [
          {
            "data": {
              "video": {
                "comments": {
                  "pageInfo": { "hasNextPage": true },
                  "edges": [
                    {
                      "cursor": "cursor-1",
                      "node": {
                        "id": "gql-msg-1",
                        "contentOffsetSeconds": 601.25,
                        "createdAt": "2026-06-01T20:10:01.250Z",
                        "commenter": { "displayName": "ViewerOne", "login": "viewerone", "id": "1" },
                        "message": {
                          "fragments": [
                            { "text": "hello " },
                            { "text": "Kappa", "emoticon": { "emoticon_id": "25" } },
                            { "text": " from gql" }
                          ],
                          "userColor": "#9146FF",
                          "userBadges": [
                            {
                              "id": "OZS=",
                              "setId": "subscriber",
                              "version": "12",
                              "title": "Subscriber",
                              "imageURL": "https://static-cdn.jtvnw.net/badges/v1/channel-subscriber/3"
                            }
                          ]
                        }
                      }
                    },
                    {
                      "cursor": "cursor-2",
                      "node": {
                        "id": "gql-msg-2",
                        "contentOffsetSeconds": 602,
                        "createdAt": "2026-06-01T20:10:02Z",
                        "commenter": { "login": "viewer_two" },
                        "message": { "body": "body wins", "userColor": "#48C7B5" }
                      }
                    }
                  ]
                }
              }
            }
          }
        ]
        """);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            true,
            "",
            ChatRoomId: "12345");

        var page = ReplayChatProvider.ReadTwitchGraphQlChatPage(document.RootElement, replay);

        Assert.Equal(true, page.HasNextPage);
        Assert.Equal("cursor-2", page.EndCursor);
        Assert.Equal(2, page.Messages.Count);
        Assert.Equal(TimeSpan.FromSeconds(601.25), page.Messages[0].Offset);
        Assert.Equal("ViewerOne", page.Messages[0].Message.Username);
        Assert.Equal("hello Kappa from gql", page.Messages[0].Message.Message);
        Assert.Equal("#9146FF", page.Messages[0].Message.Color);
        Assert.Equal("gql-msg-1", page.Messages[0].Message.MessageId);
        Assert.Equal("12345", page.Messages[0].Message.RoomId);
        var graphQlEmotes = page.Messages[0].Message.Emotes ?? throw new InvalidOperationException("Expected GraphQL replay message emotes.");
        Assert.Equal(1, graphQlEmotes.Count);
        Assert.Equal("Kappa", graphQlEmotes[0].Code);
        Assert.Equal(6, graphQlEmotes[0].StartIndex);
        Assert.Equal(11, graphQlEmotes[0].EndIndex);
        Assert.Equal(
            "https://static-cdn.jtvnw.net/emoticons/v2/25/static/light/2.0",
            graphQlEmotes[0].ImageUrl);
        var graphQlBadges = page.Messages[0].Message.Badges ?? throw new InvalidOperationException("Expected GraphQL replay message badges.");
        Assert.Equal("subscriber", graphQlBadges[0].Id);
        Assert.Equal("12", graphQlBadges[0].Version);
        Assert.Equal("Subscriber", graphQlBadges[0].Title);
        Assert.Equal("https://static-cdn.jtvnw.net/badges/v1/channel-subscriber/3", graphQlBadges[0].ImageUrl);
        Assert.Equal(false, graphQlBadges.Any(badge => string.Equals(badge.Id, "OZS=", StringComparison.Ordinal)));
        Assert.Equal("body wins", page.Messages[1].Message.Message);
        return Task.CompletedTask;
    }),
    ("skips malformed non-finite and overflowing replay chat offsets", () =>
    {
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/123",
            "123",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            true,
            "");
        using var downloaderDocument = JsonDocument.Parse("""
        {
          "comments": [
            {
              "_id": "overflowing-offset",
              "content_offset_seconds": 1.7976931348623157E+308,
              "message": { "body": "must be skipped" }
            },
            {
              "_id": "negative-offset",
              "content_offset_seconds": -1,
              "message": { "body": "must be skipped" }
            },
            {
              "_id": "valid-offset",
              "content_offset_seconds": 12.5,
              "message": { "body": "valid downloader message" }
            }
          ]
        }
        """);

        var downloaderMessages = ReplayChatProvider.ReadTwitchDownloaderMessages(
            downloaderDocument.RootElement,
            replay);

        Assert.Equal(1, downloaderMessages.Count);
        Assert.Equal("valid downloader message", downloaderMessages[0].Message.Message);

        using var graphQlDocument = JsonDocument.Parse("""
        [
          {
            "data": {
              "video": {
                "comments": {
                  "pageInfo": { "hasNextPage": false },
                  "edges": [
                    {
                      "cursor": "malformed",
                      "node": {
                        "contentOffsetSeconds": {},
                        "message": { "body": "malformed" }
                      }
                    },
                    {
                      "cursor": "non-finite",
                      "node": {
                        "contentOffsetSeconds": "NaN",
                        "message": { "body": "non-finite" }
                      }
                    },
                    {
                      "cursor": "overflowing",
                      "node": {
                        "contentOffsetSeconds": 1.7976931348623157E+308,
                        "message": { "body": "overflowing" }
                      }
                    },
                    {
                      "cursor": "negative",
                      "node": {
                        "contentOffsetSeconds": -1,
                        "message": { "body": "negative" }
                      }
                    },
                    {
                      "cursor": "valid",
                      "node": {
                        "contentOffsetSeconds": 8,
                        "message": { "body": "valid GraphQL message" }
                      }
                    }
                  ]
                }
              }
            }
          }
        ]
        """);

        var graphQlPage = ReplayChatProvider.ReadTwitchGraphQlChatPage(graphQlDocument.RootElement, replay);

        Assert.Equal(1, graphQlPage.Messages.Count);
        Assert.Equal("valid GraphQL message", graphQlPage.Messages[0].Message.Message);

        using var timestampOverflowDocument = JsonDocument.Parse("""
        {
          "comments": [
            {
              "content_offset_seconds": 5,
              "message": { "body": "timestamp overflow" }
            }
          ]
        }
        """);
        var nearMaximumTimestampReplay = replay with
        {
            StreamStartedAtUtc = DateTimeOffset.MaxValue.AddSeconds(-1)
        };

        var timestampOverflowMessages = ReplayChatProvider.ReadTwitchDownloaderMessages(
            timestampOverflowDocument.RootElement,
            nearMaximumTimestampReplay);

        Assert.Equal(0, timestampOverflowMessages.Count);
        return Task.CompletedTask;
    }),
    ("loads Twitch replay chat directly when TwitchDownloader cache is missing", async () =>
    {
        var requestBodies = new List<string>();
        var clientIds = new List<string>();
        var authorizationHeaders = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requestBodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            if (request.Headers.TryGetValues("Client-Id", out var clientIdValues))
            {
                clientIds.Add(clientIdValues.Single());
            }

            if (request.Headers.TryGetValues("Authorization", out var authorizationValues))
            {
                authorizationHeaders.Add(authorizationValues.Single());
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        "{\"error\":\"Unauthorized\",\"message\":\"The Authorization token is invalid\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                [
                  {
                    "data": {
                      "video": {
                        "comments": {
                          "pageInfo": { "hasNextPage": false },
                          "edges": [
                            {
                              "cursor": "cursor-direct",
                              "node": {
                                "id": "direct-msg-1",
                                "contentOffsetSeconds": 600,
                                "createdAt": "2026-06-01T20:10:00Z",
                                "commenter": { "displayName": "DirectViewer" },
                                "message": {
                                  "fragments": [{ "text": "direct chat works" }],
                                  "userColor": "#00FF7F"
                                }
                              }
                            }
                          ]
                        }
                      }
                    }
                  }
                ]
                """, Encoding.UTF8, "application/json")
            };
        }));
        var provider = new ReplayChatProvider(httpClient);
        var settings = new AppSettings();
        settings.Chat.TwitchClientId = "test-client-id";
        settings.Chat.TwitchOAuthToken = "oauth:test-token";
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/codex-direct-test-vod",
            "codex-direct-test-vod",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            true,
            "");

        var result = await provider.LoadChatAsync(replay, settings, TimeSpan.FromMinutes(10));

        Assert.Equal(true, result.IsAvailable);
        Assert.Equal(1, result.Messages.Count);
        Assert.Equal("DirectViewer", result.Messages[0].Message.Username);
        Assert.Equal("direct chat works", result.Messages[0].Message.Message);
        Assert.Equal(TimeSpan.FromMinutes(9), result.LoadedFromOffset);
        Assert.Equal(TimeSpan.FromMinutes(14), result.LoadedThroughOffset);
        Assert.Equal(1, requestBodies.Count);
        Assert.Contains("VideoCommentsByOffsetOrCursor", requestBodies[0]);
        Assert.Contains("b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a", requestBodies[0]);
        Assert.Contains("\"contentOffsetSeconds\":540", requestBodies[0]);
        Assert.SequenceEqual(new[] { "kimne78kx3ncx6brgo4mv6wki5h1ko" }, clientIds);
        Assert.Equal(0, authorizationHeaders.Count);
    }),
    ("sends Twitch replay chat GraphQL offsets as integers", () =>
    {
        var method = typeof(ReplayChatProvider).GetMethod(
            "BuildTwitchVideoCommentsPayload",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var payload = (string)method!.Invoke(
            null,
            ["2786354640", TimeSpan.FromSeconds(5971.6437039), null])!;
        using var document = JsonDocument.Parse(payload);
        var offset = document.RootElement[0]
            .GetProperty("variables")
            .GetProperty("contentOffsetSeconds");

        Assert.Equal(JsonValueKind.Number, offset.ValueKind);
        Assert.True(offset.TryGetInt32(out var parsedOffset));
        Assert.Equal(5971, parsedOffset);
        Assert.Equal(false, offset.ToString().Contains('.', StringComparison.Ordinal));
        return Task.CompletedTask;
    }),
    ("pages Twitch replay chat by end cursor before offset fallback", async () =>
    {
        var requestBodies = new List<string>();
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            requestBodies.Add(body);
            var firstPage = requestBodies.Count == 1;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(firstPage ? """
                [
                  {
                    "data": {
                      "video": {
                        "comments": {
                          "pageInfo": { "hasNextPage": true },
                          "edges": [
                            {
                              "cursor": "cursor-page-1",
                              "node": {
                                "id": "page-msg-1",
                                "contentOffsetSeconds": 600,
                                "createdAt": "2026-06-01T20:10:00Z",
                                "commenter": { "displayName": "FirstPage" },
                                "message": { "body": "first page" }
                              }
                            }
                          ]
                        }
                      }
                    }
                  }
                ]
                """ : """
                [
                  {
                    "data": {
                      "video": {
                        "comments": {
                          "pageInfo": { "hasNextPage": false },
                          "edges": [
                            {
                              "cursor": "cursor-page-2",
                              "node": {
                                "id": "page-msg-2",
                                "contentOffsetSeconds": 841,
                                "createdAt": "2026-06-01T20:14:01Z",
                                "commenter": { "displayName": "SecondPage" },
                                "message": { "body": "second page" }
                              }
                            }
                          ]
                        }
                      }
                    }
                  }
                ]
                """, Encoding.UTF8, "application/json")
            };
        }));
        var provider = new ReplayChatProvider(httpClient);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/codex-offset-paging-test-vod",
            "codex-offset-paging-test-vod",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            true,
            "");

        var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromMinutes(10));

        Assert.Equal(true, result.IsAvailable);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal(2, requestBodies.Count);
        Assert.Contains("\"contentOffsetSeconds\":540", requestBodies[0]);
        Assert.Contains("\"cursor\":\"cursor-page-1\"", requestBodies[1]);
        Assert.DoesNotContain("\"contentOffsetSeconds\"", requestBodies[1]);
    }),
    ("reports only loaded Twitch replay coverage when the page limit is reached", async () =>
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            var pageIndex = requestCount++;
            var offsetSeconds = 540 + pageIndex;
            var body = $$"""
            [
              {
                "data": {
                  "video": {
                    "comments": {
                      "pageInfo": { "hasNextPage": true },
                      "edges": [
                        {
                          "cursor": "page-limit-cursor-{{pageIndex}}",
                          "node": {
                            "id": "page-limit-message-{{pageIndex}}",
                            "contentOffsetSeconds": {{offsetSeconds}},
                            "createdAt": "2026-06-01T20:00:00Z",
                            "commenter": { "displayName": "Viewer" },
                            "message": { "body": "page {{pageIndex}}" }
                          }
                        }
                      ]
                    }
                  }
                }
              }
            ]
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }));
        var provider = new ReplayChatProvider(httpClient);
        var replay = new ReplaySessionInfo(
            PlatformKind.Twitch,
            "streamer",
            "https://www.twitch.tv/videos/page-limit-test-vod",
            "page-limit-test-vod",
            new DateTimeOffset(2026, 6, 1, 20, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(1),
            true,
            "");

        var result = await provider.LoadChatAsync(replay, new AppSettings(), TimeSpan.FromMinutes(10));

        Assert.True(result.IsAvailable, result.UnavailableReason);
        Assert.Equal(60, requestCount);
        Assert.Equal(60, result.Messages.Count);
        Assert.Equal(TimeSpan.FromSeconds(599), result.LoadedThroughOffset);
    }),
    ("replay chat window selector slices 100k captured messages", () =>
    {
        var selector = new ReplayChatWindowSelector();
        var timestamp = DateTimeOffset.UtcNow;
        var messages = Enumerable.Range(0, 100_000)
            .Select(index => new ReplayChatMessage(
                TimeSpan.FromSeconds(index),
                new ChatMessage(
                    PlatformKind.Twitch,
                    "streamer",
                    $"viewer-{index}",
                    $"message {index}",
                    timestamp.AddSeconds(index),
                    MessageId: $"message-{index}")))
            .ToArray();

        selector.Replace(messages);

        var selection = selector.SelectWindow(
            TimeSpan.FromSeconds(50_000),
            TimeSpan.FromSeconds(45),
            100);
        var repeatedSelection = selector.SelectWindow(
            TimeSpan.FromSeconds(50_000),
            TimeSpan.FromSeconds(45),
            100);

        Assert.Equal(46, selection.Messages.Count);
        Assert.Equal("message-49955", selection.Messages[0].MessageId);
        Assert.Equal("message-50000", selection.Messages[^1].MessageId);
        Assert.Equal(selection.Key, repeatedSelection.Key);
        return Task.CompletedTask;
    }),
    ("replay chat window selector evicts capped head without losing 100k slicing", () =>
    {
        var selector = new ReplayChatWindowSelector();
        var timestamp = DateTimeOffset.UtcNow;
        for (var index = 0; index < 100_500; index++)
        {
            selector.Add(
                new ReplayChatMessage(
                    TimeSpan.FromSeconds(index),
                    new ChatMessage(
                        PlatformKind.Twitch,
                        "streamer",
                        $"viewer-{index}",
                        $"message {index}",
                        timestamp.AddSeconds(index),
                        MessageId: $"message-{index}")),
                100_000,
                out _);
        }

        var selection = selector.SelectWindow(
            TimeSpan.FromSeconds(100_000),
            TimeSpan.FromSeconds(45),
            100);

        Assert.Equal(100_000, selector.Count);
        Assert.Equal(TimeSpan.FromSeconds(500), selector.FirstOffset);
        Assert.Equal(TimeSpan.FromSeconds(100_499), selector.LastOffset);
        Assert.Equal(46, selection.Messages.Count);
        Assert.Equal("message-99955", selection.Messages[0].MessageId);
        Assert.Equal("message-100000", selection.Messages[^1].MessageId);
        return Task.CompletedTask;
    }),
    ("replay chat window selector moves backward without retaining future messages", () =>
    {
        var selector = new ReplayChatWindowSelector();
        var timestamp = DateTimeOffset.UtcNow;
        selector.Replace([
            new ReplayChatMessage(
                TimeSpan.FromMinutes(10),
                new ChatMessage(PlatformKind.Twitch, "streamer", "early", "early replay chat", timestamp, MessageId: "early")),
            new ReplayChatMessage(
                TimeSpan.FromMinutes(50),
                new ChatMessage(PlatformKind.Twitch, "streamer", "late", "late replay chat", timestamp, MessageId: "late"))
        ]);

        var lateSelection = selector.SelectWindow(
            TimeSpan.FromMinutes(50),
            TimeSpan.FromSeconds(45),
            100);
        var earlySelection = selector.SelectWindow(
            TimeSpan.FromMinutes(10),
            TimeSpan.FromSeconds(45),
            100);

        Assert.SequenceEqual(new[] { "late replay chat" }, lateSelection.Messages.Select(message => message.Message).ToArray());
        Assert.SequenceEqual(new[] { "early replay chat" }, earlySelection.Messages.Select(message => message.Message).ToArray());
        return Task.CompletedTask;
    }),
    ("replay chat window selector selects explicit range with capped tail", () =>
    {
        var selector = new ReplayChatWindowSelector();
        var timestamp = DateTimeOffset.UtcNow;
        selector.Replace(Enumerable.Range(0, 150)
            .Select(index => new ReplayChatMessage(
                TimeSpan.FromSeconds(index),
                new ChatMessage(
                    PlatformKind.Kick,
                    "streamer",
                    $"viewer-{index}",
                    $"range message {index}",
                    timestamp.AddSeconds(index),
                    MessageId: $"range-message-{index}")))
            .ToArray());

        var selection = selector.SelectRange(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(120),
            100);
        var repeatedSelection = selector.SelectRange(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(120),
            100);

        Assert.Equal(100, selection.Messages.Count);
        Assert.Equal("range-message-21", selection.Messages[0].MessageId);
        Assert.Equal("range-message-120", selection.Messages[^1].MessageId);
        Assert.Equal(selection.Key, repeatedSelection.Key);
        return Task.CompletedTask;
    }),
    ];
}
