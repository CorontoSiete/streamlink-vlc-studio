using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Kick;

internal static class KickClipTestCatalog
{
    private static readonly StreamTarget Target = new(PlatformKind.Kick, "streamer", "https://kick.com/streamer");
    private const string InternalDraft = "https://kick.com/api/internal/v1/livestreams/stream-session/clips";
    private const string WebDraft = "https://web.kick.com/api/v1/clips";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Kick clips: verifies both website publication contracts and public links", PublicationsAsync),
        ("Kick clips: rejects unconfirmed mismatched malformed and oversized responses", InvalidPublicationsAsync),
        ("Kick clips: rejects foreign origins routes and invalid targets", RoutesAsync),
        ("Kick clips: exposes authentication cooldown and server failures without publishing", ErrorsAsync),
        ("Kick clips: selected tab routes to Kick and opens only the confirmed result", CommandAsync),
        ("Kick clips: cancellation failures and browser launch failures remain truthful", FailedCommandAsync),
        ("Kick clips: shutdown cancels and ignores late publication", ShutdownAsync),
        ("Kick clips: real browser dispatches mounted editor and observes ordered publication", BrowserAsync),
        ("Kick clips: background service stays hidden and muted and publishes once", BackgroundAsync),
        ("Kick clips: background sign-in and HTTP failures never report success", BackgroundErrorsAsync),
        ("Kick clips: cancellation closes the hidden browser including late creation", BackgroundCancellationAsync),
        ("Kick clips: real browser retains preview media while follow import blocks it", MediaAsync)
    ];

    private static string Body(bool web, string id = "clip_123")
    {
        var clip = new { id, title = "test", thumbnail_url = "https://example.test/clip.jpg" };
        return web ? JsonSerializer.Serialize(new { data = clip }) : JsonSerializer.Serialize(clip);
    }

    private static Task PublicationsAsync()
    {
        foreach (var (route, web) in new[] { (InternalDraft, false), (WebDraft, true) })
        {
            var tracker = new KickClipPublicationTracker(Target);
            Assert.Equal<KickClipResult?>(null, tracker.Observe("POST", route, 201, Body(web)));
            var result = tracker.Observe("POST", route + "/clip_123/finalize", 200, Body(web));
            Assert.NotNull(result);
            Assert.Equal("clip_123", result!.ClipId);
            Assert.Equal("https://kick.com/streamer/clips/clip_123", result.ClipUri.AbsoluteUri);
            tracker.Observe("POST", route, 200, Body(web, "draft_456"));
            var renamed = tracker.Observe("POST", route + "/draft_456/finalize", 200, Body(web, "published_789"));
            Assert.Equal("https://kick.com/streamer/clips/published_789", renamed!.ClipUri.AbsoluteUri);
        }
        return Task.CompletedTask;
    }

    private static async Task InvalidPublicationsAsync()
    {
        foreach (var body in new[] { "{}", "[]", "null", "{", "{\"id\":2}", "{\"id\":\"../escape\"}", new string('x', 256 * 1024 + 1) })
        {
            var tracker = new KickClipPublicationTracker(Target);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(tracker.Observe("POST", InternalDraft, 200, body)));
        }
        var matched = new KickClipPublicationTracker(Target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(matched.Observe("POST", InternalDraft + "/clip_123/finalize", 200, Body(false))));
        matched.Observe("POST", InternalDraft, 200, Body(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(matched.Observe("POST", InternalDraft + "/unknown_draft/finalize", 200, Body(false))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(matched.Observe("POST", WebDraft + "/clip_123/finalize", 200, Body(true))));
        matched.Reset();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(matched.Observe("POST", InternalDraft + "/clip_123/finalize", 200, Body(false))));
    }

    private static async Task RoutesAsync()
    {
        foreach (var route in new[]
        {
            "http://web.kick.com/api/v1/clips", "https://web.kick.com.evil.test/api/v1/clips",
            "https://user@web.kick.com/api/v1/clips", "https://web.kick.com:8443/api/v1/clips",
            WebDraft + "?redirect=x", WebDraft + "#fragment", WebDraft + "/id/download",
            "https://kick.com/api/internal/v1/videos/id/clips", InternalDraft + "/%2F/finalize"
        }) Assert.Equal(false, KickClipPublicationTracker.IsClipRequest("POST", route));
        Assert.Equal(false, KickClipPublicationTracker.IsClipRequest("GET", WebDraft));
        var tracker = new KickClipPublicationTracker(Target);
        Assert.True(tracker.IsChannelPage("https://kick.com/streamer/"));
        Assert.Equal(false, tracker.IsChannelPage("https://kick.com/other"));
        Assert.Equal(false, tracker.IsChannelPage("https://kick.com/streamer/clips/123"));
        foreach (var target in new[] { Target with { Platform = PlatformKind.Twitch }, Target with { Kind = StreamTargetKind.KickVod }, Target with { Channel = "../settings" }, Target with { Channel = "" } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(new KickClipPublicationTracker(target)));
    }

    private static async Task ErrorsAsync()
    {
        foreach (var (code, message) in new[] { (401, "Sign in"), (403, "refused"), (429, "limiting"), (500, "HTTP 500") })
        {
            var tracker = new KickClipPublicationTracker(Target);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(tracker.Observe("POST", WebDraft, code, Body(true))));
            Assert.Contains(message, error.Message);
        }
    }

    private static async Task CommandAsync()
    {
        var service = new PendingService();
        var opened = new List<Uri>();
        await using var model = Model(service, opened.Add);
        Assert.True(model.CreateClipCommand.CanExecute(null));
        var command = model.CreateClipCommand.ExecuteAsync();
        Assert.Equal(Target, service.Target);
        Assert.Equal(false, model.CreateClipCommand.CanExecute(null));
        model.SelectedTab = null;
        service.Completion.SetResult(new KickClipResult("clip_123", new Uri("https://kick.com/streamer/clips/clip_123")));
        await command;
        Assert.Equal(1, opened.Count);
        Assert.Equal("https://kick.com/streamer/clips/clip_123", opened[0].AbsoluteUri);
        Assert.Contains("Kick clip opened for streamer", model.StatusMessage);
        model.SelectedTab = model.Tabs[0];
        model.Tabs.Add(TestViewModels.CreateTab(Target with { Kind = StreamTargetKind.KickVod }, "best",
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(), action => action()));
        model.SelectedTab = model.Tabs[1];
        Assert.Equal(false, model.CreateClipCommand.CanExecute(null));
    }

    private static async Task FailedCommandAsync()
    {
        foreach (var failure in new[] { "closed", "error", "cancel", "browser" })
        {
            var service = new PendingService();
            var opens = 0;
            await using var model = Model(service, _ => { opens++; throw new IOException("browser unavailable"); });
            var command = model.CreateClipCommand.ExecuteAsync();
            switch (failure)
            {
                case "closed": service.Completion.SetResult(null); break;
                case "error": service.Completion.SetException(new InvalidOperationException("Kick refused clipping")); break;
                case "cancel": service.Completion.SetCanceled(); break;
                default: service.Completion.SetResult(new KickClipResult("clip_123", new Uri("https://kick.com/streamer/clips/clip_123"))); break;
            }
            await command;
            Assert.Equal(failure == "browser" ? 1 : 0, opens);
            Assert.Contains(failure switch { "closed" => "without a confirmed", "error" => "refused", "cancel" => "cancelled", _ => "browser could not be opened: https://kick.com/streamer/clips/clip_123" }, model.StatusMessage);
        }
    }

    private static async Task ShutdownAsync()
    {
        var service = new PendingService();
        var opened = 0;
        var model = Model(service, _ => opened++);
        var command = model.CreateClipCommand.ExecuteAsync();
        await model.DisposeAsync();
        Assert.True(service.Token.IsCancellationRequested);
        var status = model.StatusMessage;
        service.Completion.SetResult(new KickClipResult("clip_123", new Uri("https://kick.com/streamer/clips/clip_123")));
        await command;
        Assert.Equal(0, opened);
        Assert.Equal(status, model.StatusMessage);
    }

    private static MainViewModel Model(IKickClipService service, Action<Uri> open)
    {
        var settings = new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        var streamlink = new FakeStreamlinkService();
        var playback = new FakePlaybackEngineFactory();
        var chat = new FakeChatClientFactory();
        var logger = new MemoryLogger();
        var model = new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = new FakeSettingsService(settings),
            StreamlinkService = streamlink,
            PlaybackFactory = playback,
            ChatFactory = chat,
            Logger = logger,
            Dispatch = action => action(),
            KickClipService = service,
            OpenBrowser = open
        });
        var tab = TestViewModels.CreateTab(Target, "best", streamlink, playback, chat, logger, action => action());
        model.Tabs.Add(tab);
        model.SelectedTab = tab;
        return model;
    }

    private sealed class PendingService : IKickClipService
    {
        internal TaskCompletionSource<KickClipResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StreamTarget? Target { get; private set; }
        internal CancellationToken Token { get; private set; }
        public Task<KickClipResult?> CreateLiveClipAsync(StreamTarget target, CancellationToken cancellationToken = default)
        {
            Target = target;
            Token = cancellationToken;
            return Completion.Task;
        }
    }

    private static Task BrowserAsync() => WithBrowserAsync(async (core, environment) =>
    {
        var responseStatus = 200;
        var requests = new List<string>();
        var page = """
            <!doctype html><html><body><video src="https://media.test/preview.mp4"></video><script>
            window.clips = 0;
            window.listener = e => { if(e.detail.mode === 'livestream') window.clips++; };
            window.mount = () => window.addEventListener('openClipCreator', window.listener);
            window.unmount = () => window.removeEventListener('openClipCreator', window.listener);
            </script></body></html>
            """;
        KickFollowedChannelsImporter.ConfigureBrowser(core, blockMedia: false);
        core.WebResourceRequested += (_, args) =>
        {
            var api = KickClipPublicationTracker.IsClipRequest(args.Request.Method, args.Request.Uri);
            if (api) requests.Add(args.Request.Method + " " + args.Request.Uri);
            args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(api ? Body(args.Request.Uri.StartsWith("https://web.", StringComparison.Ordinal)) : page)),
                api ? responseStatus : 200, "Fixture", "Content-Type: " + (api ? "application/json" : "text/html") + "\r\nAccess-Control-Allow-Origin: https://kick.com");
        };
        using var client = new KickClipBrowserClient(core, Target);
        await client.InitializeAsync(default);
        var results = new List<KickClipResult>();
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Published += results.Add;
        client.Failed += message => failure.TrySetResult(message);
        await NavigateAsync(core, client.ChannelUri.AbsoluteUri);
        Assert.Equal(false, await client.OpenEditorAsync(false, default));
        await core.ExecuteScriptAsync("window.mount()");
        Assert.True(await client.OpenEditorAsync(false, default));
        Assert.Equal("1", await core.ExecuteScriptAsync("window.clips"));
        await core.ExecuteScriptAsync("window.unmount()");
        Assert.Equal(false, await client.OpenEditorAsync(false, default));
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => client.OpenEditorAsync(true, canceled.Token));
        }
        foreach (var route in new[] { InternalDraft, WebDraft })
        {
            await core.ExecuteScriptAsync($$"""(async () => { await (await fetch({{JsonSerializer.Serialize(route)}}, {method:'POST'})).json(); await (await fetch({{JsonSerializer.Serialize(route + "/clip_123/finalize")}}, {method:'POST'})).json(); })()""");
            var expected = route == InternalDraft ? 1 : 2;
            try { await WaitUntilAsync(() => results.Count == expected); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Publication missing for {route}: {results.Count} confirmed; error: {(failure.Task.IsCompletedSuccessfully ? failure.Task.Result : "none")}; requests: {string.Join(", ", requests)}", ex);
            }
        }
        responseStatus = 429;
        await core.ExecuteScriptAsync($$"""fetch({{JsonSerializer.Serialize(InternalDraft)}}, {method:'POST'})""");
        Assert.Contains("limiting", await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await core.ExecuteScriptAsync("history.pushState({}, '', '/other'); window.mount()");
        Assert.Equal(false, await client.OpenEditorAsync(false, default));
        await core.ExecuteScriptAsync($$"""fetch({{JsonSerializer.Serialize(InternalDraft + "/clip_123/finalize")}}, {method:'POST'})""");
        Assert.Equal(2, results.Count);
    });

    private static async Task MediaAsync()
    {
        foreach (var blockMedia in new[] { false, true })
        {
            await WithBrowserAsync(async (core, environment) =>
            {
                KickFollowedChannelsImporter.ConfigureBrowser(core, blockMedia);
                var mediaAllowed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.WebResourceRequested += (_, args) =>
                {
                    if (args.Request.Uri.EndsWith(".m3u8", StringComparison.Ordinal)) mediaAllowed.TrySetResult(args.Response is null);
                    args.Response ??= environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html>")), 200, "Fixture", "Content-Type: text/html");
                };
                await NavigateAsync(core, "https://kick.com/streamer");
                await core.ExecuteScriptAsync("fetch('/preview.m3u8')");
                Assert.Equal(!blockMedia, await mediaAllowed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            });
        }
    }

    private static string EditorFixture(string route, bool signIn = false) => $$"""
        <!doctype html><html><body><script>
        window.addEventListener('openClipCreator', async e => {
            if (e.detail.mode !== 'livestream') return;
            if ({{(signIn ? "true" : "false")}}) {
                document.body.innerHTML = '<input type="password">';
                return;
            }
            const draft = await fetch({{JsonSerializer.Serialize(route)}}, {method:'POST'});
            if (!draft.ok) return;
            await draft.json();
            document.body.innerHTML = '<form><input data-testid="clip-creator-title"><button type="submit" data-testid="clip-creator-publish" disabled>Publish</button></form>';
            const input = document.querySelector('input');
            const button = document.querySelector('button');
            input.addEventListener('input', () => setTimeout(() => { button.disabled = !input.value.trim(); }, 50));
            document.querySelector('form').addEventListener('submit', async event => {
                event.preventDefault();
                const gate = new Promise(resolve => { window.releasePublication = resolve; });
                await fetch('/fixture/submitted', {method:'POST', body: input.value});
                await gate;
                await fetch({{JsonSerializer.Serialize(route + "/clip_123/finalize")}}, {method:'POST'});
            });
        });
        </script></body></html>
        """;

    private static Task BackgroundAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder:
            Path.Combine(Path.GetTempPath(), $"kick-clips-test-{Guid.NewGuid():N}"));
        var owner = new Window();
        try
        {
            foreach (var route in new[] { InternalDraft, WebDraft })
            {
                CoreWebView2? core = null;
                nint host = 0;
                var submissions = 0;
                var drafts = 0;
                var finalized = 0;
                var title = "";
                var service = new KickClipService(owner, async handle =>
                {
                    host = handle;
                    var controller = await environment.CreateCoreWebView2ControllerAsync(handle);
                    core = controller.CoreWebView2;
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += (_, args) =>
                    {
                        var api = KickClipPublicationTracker.IsClipRequest(args.Request.Method, args.Request.Uri);
                        if (args.Request.Uri == route) drafts++;
                        if (args.Request.Uri.EndsWith("/finalize", StringComparison.Ordinal)) finalized++;
                        if (args.Request.Uri == "https://kick.com/fixture/submitted")
                        {
                            submissions++;
                            using var reader = new StreamReader(args.Request.Content);
                            title = reader.ReadToEnd();
                        }
                        args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(
                            api ? Body(route == WebDraft) : EditorFixture(route))), 200, "Fixture",
                            "Content-Type: " + (api ? "application/json" : "text/html") + "\r\nAccess-Control-Allow-Origin: https://kick.com");
                    };
                    return controller;
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var creation = service.CreateLiveClipAsync(Target, timeout.Token);
                await WaitUntilAsync(() => submissions > 0);
                Assert.Equal(false, NativeWindowTest.IsWindowVisible(host));
                Assert.Equal(false, owner.IsVisible);
                Assert.True(core!.IsMuted);
                Assert.Equal(false, creation.IsCompleted);
                Assert.True(title.StartsWith("streamer - ", StringComparison.Ordinal) && title.Length <= 50);
                // Keep the final response pending across multiple polling intervals; never submit twice.
                await Task.Delay(800);
                Assert.Equal(1, submissions);
                Assert.Equal(1, drafts);
                Assert.Equal(0, finalized);
                await core.ExecuteScriptAsync("window.releasePublication()");
                var result = await creation;
                Assert.Equal("https://kick.com/streamer/clips/clip_123", result!.ClipUri.AbsoluteUri);
                Assert.Equal(1, finalized);
                Assert.Equal(false, NativeWindowTest.IsWindow(host));
            }
        }
        finally { owner.Close(); }
    });

    private static Task BackgroundErrorsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder:
            Path.Combine(Path.GetTempPath(), $"kick-clips-test-{Guid.NewGuid():N}"));
        var owner = new Window();
        try
        {
            foreach (var (signIn, status, message) in new[] { (true, 200, "Sign in"), (false, 401, "Sign in"), (false, 403, "refused"), (false, 429, "limiting") })
            {
                nint host = 0;
                var finalized = 0;
                var service = new KickClipService(owner, async handle =>
                {
                    host = handle;
                    var controller = await environment.CreateCoreWebView2ControllerAsync(handle);
                    var core = controller.CoreWebView2;
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                    core.WebResourceRequested += (_, args) =>
                    {
                        var api = KickClipPublicationTracker.IsClipRequest(args.Request.Method, args.Request.Uri);
                        if (args.Request.Uri.EndsWith("/finalize", StringComparison.Ordinal)) finalized++;
                        args.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(
                            api ? "{}" : EditorFixture(InternalDraft, signIn))), api ? status : 200, "Fixture",
                            "Content-Type: " + (api ? "application/json" : "text/html"));
                    };
                    return controller;
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateLiveClipAsync(Target, timeout.Token));
                Assert.Contains(message, error.Message);
                Assert.Equal(0, finalized);
                Assert.Equal(false, NativeWindowTest.IsWindow(host));
            }
        }
        finally { owner.Close(); }
    });

    private static Task BackgroundCancellationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder:
            Path.Combine(Path.GetTempPath(), $"kick-clips-test-{Guid.NewGuid():N}"));
        var owner = new Window();
        try
        {
            nint host = 0;
            var pending = new TaskCompletionSource<CoreWebView2Controller>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new KickClipService(owner, handle => { host = handle; return pending.Task; });
            using var cancellation = new CancellationTokenSource();
            var creation = service.CreateLiveClipAsync(Target, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => creation);
            Assert.True(NativeWindowTest.IsWindow(host));
            Assert.Equal(false, NativeWindowTest.IsWindowVisible(host));
            pending.SetResult(await environment.CreateCoreWebView2ControllerAsync(host));
            await WaitUntilAsync(() => !NativeWindowTest.IsWindow(host));

            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service = new KickClipService(owner, async handle =>
            {
                host = handle;
                var controller = await environment.CreateCoreWebView2ControllerAsync(handle);
                var core = controller.CoreWebView2;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, args) => args.Response = environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html>")), 200, "Fixture", "Content-Type: text/html");
                core.NavigationCompleted += (_, _) => loaded.TrySetResult();
                return controller;
            });
            using var navigationCancellation = new CancellationTokenSource();
            creation = service.CreateLiveClipAsync(Target, navigationCancellation.Token);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            navigationCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => creation);
            Assert.Equal(false, NativeWindowTest.IsWindow(host));
        }
        finally { owner.Close(); }
    });

    private static Task WithBrowserAsync(Func<CoreWebView2, CoreWebView2Environment, Task> test) => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kick-clips-test-{Guid.NewGuid():N}");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: directory);
        var window = new Window();
        var controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).EnsureHandle());
        try { await test(controller.CoreWebView2, environment); }
        finally { controller.Close(); window.Close(); }
    });

    private static async Task NavigateAsync(CoreWebView2 core, string address)
    {
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) navigation.TrySetResult();
            else navigation.TrySetException(new InvalidOperationException("Fixture navigation failed."));
        }
        core.NavigationCompleted += Completed;
        try { core.Navigate(address); await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { core.NavigationCompleted -= Completed; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
