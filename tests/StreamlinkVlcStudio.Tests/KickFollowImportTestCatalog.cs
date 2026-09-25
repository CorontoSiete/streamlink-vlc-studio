using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Kick;

internal static class KickFollowImportTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Kick follow import: reads all pages including offline channels and deduplicates", PaginationAsync),
        ("Kick follow import: rejects malformed partial and looping lists", InvalidPagesAsync),
        ("Kick follow import: bounds pagination and observes cancellation", LimitsAsync),
        ("Kick follow import: settings survive save reload and preserve manual entries", PersistenceAsync),
        ("Kick follow import: polling combines imported and manual channels in bounded batches", PollingAsync),
        ("Kick follow import: known empty follows need no API credentials", EmptyAsync),
        ("Kick follow import: import and reimport save and refresh without overwriting manual channels", ImportCommandAsync),
        ("Kick follow import: canceled failed and unsaved imports keep previous follows", FailedCommandAsync),
        ("Kick follow import: shutdown cancels detection and rejects late results", ShutdownAsync),
        ("Kick follow import: browser authenticates paginates and handles expired sessions", BrowserAsync),
        ("Kick follow import: browser origin rules reject lookalikes and external navigation", OriginsAsync)
    ];

    private static string Page(string[] channels, string cursor = "null") =>
        "{\"channels\":[" + string.Join(',', channels.Select((channel, index) =>
            $$"""{"channel_slug":{{JsonSerializer.Serialize(channel)}},"is_live":{{(index % 2 == 0 ? "true" : "false")}}}""")) +
        "],\"nextCursor\":" + cursor + "}";

    private static async Task PaginationAsync()
    {
        var urls = new List<string>();
        var pages = new Queue<string>([Page(["Online", "offline"], "\"a+b=/&\""), Page(["online", "last"])]);
        var result = await KickFollowedChannelsReader.ReadAllAsync((url, _) =>
        {
            urls.Add(url);
            return Task.FromResult(pages.Dequeue());
        });
        Assert.SequenceEqual(new[] { "last", "offline", "Online" }, result);
        Assert.SequenceEqual(new[] { "https://kick.com/api/v2/channels/followed", "https://kick.com/api/v2/channels/followed?cursor=a%2Bb%3D%2F%26" }, urls);
        Assert.Equal("42", KickFollowedChannelsReader.ReadPage(Page(["one"], "42")).NextCursor);
        Assert.Equal(0, KickFollowedChannelsReader.ReadPage("{\"channels\":[]}").Channels.Count);
    }

    private static async Task InvalidPagesAsync()
    {
        foreach (var malformed in new[]
        {
            "<html>Sign in</html>", "null", "[]", "{}", "{\"channels\":null}",
            "{\"channels\":[null]}", "{\"channels\":[{\"slug\":\"wrong-contract\"}]}",
            "{\"channels\":[{\"channel_slug\":\"https://example.com/not-a-slug\"}]}",
            "{\"channels\":[{\"channel_slug\":22}]}", Page(["valid"], "{}"),
            Page(["valid"], "-1"), Page([], "\"nonempty\"")
        })
        {
            var calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => KickFollowedChannelsReader.ReadAllAsync(
                (_, _) => Task.FromResult(++calls == 1 ? Page(["good"], "\"next\"") : malformed)));
            Assert.Equal(2, calls);
        }
        var repeatCalls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KickFollowedChannelsReader.ReadAllAsync((_, _) =>
        {
            repeatCalls++;
            return Task.FromResult(Page(["good"], "\"same\""));
        }));
        Assert.Equal(2, repeatCalls);
        await Assert.ThrowsAsync<IOException>(() => KickFollowedChannelsReader.ReadAllAsync((_, _) => throw new IOException("offline")));
    }

    private static async Task LimitsAsync()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => KickFollowedChannelsReader.ReadAllAsync((_, _) =>
            Task.FromResult(Page(["one"], JsonSerializer.Serialize((++calls).ToString(CultureInfo.InvariantCulture))))));
        Assert.Equal(KickFollowedChannelsReader.MaximumPages, calls);
        using var cancellation = new CancellationTokenSource();
        calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => KickFollowedChannelsReader.ReadAllAsync((_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            calls++;
            cancellation.Cancel();
            return Task.FromResult(Page(["one"], "\"next\""));
        }, cancellationToken: cancellation.Token));
        Assert.Equal(1, calls);
    }

    private static async Task PersistenceAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kick-follows-settings-{Guid.NewGuid():N}");
        var service = new JsonSettingsService(Path.Combine(directory, "settings.json"));
        try
        {
            var settings = Settings();
            await service.SaveAsync(settings);
            var loaded = await service.LoadAsync();
            Assert.SequenceEqual(new[] { "manual" }, loaded.FollowedChannels.KickChannelSlugs);
            Assert.SequenceEqual(new[] { "old" }, loaded.FollowedChannels.KickImportedChannelSlugs);
            Assert.Equal(settings.FollowedChannels.KickFollowsImportedAtUtc, loaded.FollowedChannels.KickFollowsImportedAtUtc);
            await File.WriteAllTextAsync(service.SettingsPath, "{\"FollowedChannels\":{\"KickChannelSlugs\":[\"manual\"]}}");
            loaded = await service.LoadAsync();
            Assert.SequenceEqual(new[] { "manual" }, loaded.FollowedChannels.KickChannelSlugs);
            Assert.Equal(0, loaded.FollowedChannels.KickImportedChannelSlugs.Count);
            Assert.Equal<DateTimeOffset?>(null, loaded.FollowedChannels.KickFollowsImportedAtUtc);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static async Task PollingAsync()
    {
        var settings = Settings();
        settings.FollowedChannels.KickImportedChannelSlugs = Enumerable.Range(1, 105).Select(i => $"channel{i}").Append("manual").ToList();
        var requested = new List<string>();
        var sizes = new List<int>();
        using var client = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            Assert.Equal("api.kick.com", request.RequestUri!.Host);
            Assert.Equal("/public/v1/channels", request.RequestUri.AbsolutePath);
            var slugs = request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => Uri.UnescapeDataString(pair[5..])).ToArray();
            sizes.Add(slugs.Length);
            requested.AddRange(slugs);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[" + string.Join(',', slugs.Select(slug =>
                    $$"""{"slug":"{{slug}}","stream":{"is_live":true,"viewer_count":2},"stream_title":"Live"}""")) + "]}")
            };
        }));
        var service = new FollowedStreamsService(new MemoryLogger(), client, new TokenProvider());
        var result = await service.GetLiveFollowedStreamsAsync(settings);
        Assert.SequenceEqual(new[] { 50, 50, 6 }, sizes);
        Assert.Equal(106, requested.Distinct().Count());
        Assert.Equal(106, result.Streams.Count);
        Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Kick));
    }

    private static async Task EmptyAsync()
    {
        var settings = new AppSettings();
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No request expected.")));
        var service = new FollowedStreamsService(new MemoryLogger(), client, new TokenProvider());
        var result = await service.GetLiveFollowedStreamsAsync(settings);
        Assert.True(result.Messages.Any(message => message.Contains("Detect Kick follows", StringComparison.Ordinal)));
        settings.FollowedChannels.KickFollowsImportedAtUtc = DateTimeOffset.UtcNow;
        result = await service.GetLiveFollowedStreamsAsync(settings);
        Assert.True(result.SucceededPlatforms!.Contains(PlatformKind.Kick));
        Assert.Equal(0, result.Streams.Count);
    }

    private static async Task ImportCommandAsync()
    {
        var settings = Settings();
        var save = new FakeSettingsService(settings);
        var importer = new Importer { Result = ["new", "offline"] };
        var followed = new FakeFollowedStreamsService();
        await using var model = Model(settings, save, importer, followed);
        model.KickFollowedChannelsText = "manual\nunsaved-extra";
        await model.ImportKickFollowsCommand.ExecuteAsync();
        Assert.Equal(1, save.SaveCount);
        Assert.SequenceEqual(new[] { "new", "offline" }, settings.FollowedChannels.KickImportedChannelSlugs);
        Assert.Equal("manual\nunsaved-extra", model.KickFollowedChannelsText);
        Assert.Equal(1, followed.CallCount);
        Assert.Contains("2 Kick follows imported", model.KickImportedFollowsSummary);
        importer.Result = [];
        await model.ImportKickFollowsCommand.ExecuteAsync();
        Assert.Equal(0, settings.FollowedChannels.KickImportedChannelSlugs.Count);
        Assert.True(settings.FollowedChannels.KickFollowsImportedAtUtc is not null);
        Assert.Equal(2, followed.CallCount);
        await model.ClearImportedKickFollowsCommand.ExecuteAsync();
        Assert.Equal<DateTimeOffset?>(null, settings.FollowedChannels.KickFollowsImportedAtUtc);
        Assert.SequenceEqual(new[] { "manual", "unsaved-extra" }, settings.FollowedChannels.KickChannelSlugs);
    }

    private static async Task FailedCommandAsync()
    {
        foreach (var failure in new[] { "cancel", "network", "save" })
        {
            var settings = Settings();
            var timestamp = settings.FollowedChannels.KickFollowsImportedAtUtc;
            var save = new FakeSettingsService(settings) { SaveException = failure == "save" ? new IOException("Disk full") : null };
            var importer = new Importer { Result = failure == "cancel" ? null : ["new"], Error = failure == "network" ? new IOException("Unavailable") : null };
            var followed = new FakeFollowedStreamsService();
            await using var model = Model(settings, save, importer, followed);
            await model.ImportKickFollowsCommand.ExecuteAsync();
            Assert.SequenceEqual(new[] { "old" }, settings.FollowedChannels.KickImportedChannelSlugs);
            Assert.Equal(timestamp, settings.FollowedChannels.KickFollowsImportedAtUtc);
            Assert.Equal(0, followed.CallCount);
        }
    }

    private static async Task ShutdownAsync()
    {
        var settings = Settings();
        var save = new FakeSettingsService(settings);
        var pending = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer = new Importer { Pending = pending.Task };
        await using var model = Model(settings, save, importer);
        var command = model.ImportKickFollowsCommand.ExecuteAsync();
        Assert.Equal(false, model.ClearImportedKickFollowsCommand.CanExecute(null));
        await model.DisposeAsync();
        Assert.True(importer.Token.IsCancellationRequested);
        pending.TrySetResult(["late"]);
        await command;
        Assert.Equal(0, save.SaveCount);
        Assert.SequenceEqual(new[] { "old" }, settings.FollowedChannels.KickImportedChannelSlugs);
        Assert.Equal(false, model.ImportKickFollowsCommand.CanExecute(null));
    }

    private static Task BrowserAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kick-follows-browser-{Guid.NewGuid():N}");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: directory);
        var window = new Window();
        var controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).EnsureHandle());
        var core = controller.CoreWebView2;
        var requests = new List<string>();
        var status = 200;
        var page = Page(["live", "offline"], "\"next+page\"");
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            KickFollowedChannelsImporter.ConfigureBrowser(core);
            core.WebResourceRequested += (_, args) =>
            {
                // Every network request is served locally. Fixture credentials never leave this process.
                var isApi = new Uri(args.Request.Uri).AbsolutePath == "/api/v2/channels/followed";
                if (isApi)
                {
                    requests.Add(args.Request.Uri);
                    Assert.Equal("Bearer fixture-token", args.Request.Headers.GetHeader("Authorization"));
                    Assert.Equal("web", args.Request.Headers.GetHeader("x-app-platform"));
                    Assert.Contains("session_token=fixture-token", args.Request.Headers.GetHeader("Cookie"));
                }
                args.Response ??= environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes(isApi ? page : "<!doctype html><html><body>Kick fixture</body></html>")),
                    isApi ? status : 200, "Fixture", "Content-Type: " + (isApi ? "application/json" : "text/html"));
            };
            core.NavigationCompleted += (_, args) => { if (args.IsSuccess) navigation.TrySetResult(); };
            core.Navigate("https://kick.com/");
            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await core.ExecuteScriptAsync("document.cookie='session_token=fixture-token; Secure; Path=/; SameSite=Lax'");
            var client = new KickFollowedChannelsBrowserClient(core);
            var first = KickFollowedChannelsReader.ReadPage(await client.ReadPageAsync(KickFollowedChannelsReader.BuildPageUrl(""), default));
            Assert.SequenceEqual(new[] { "live", "offline" }, first.Channels);
            page = Page(["last"]);
            await client.ReadPageAsync(KickFollowedChannelsReader.BuildPageUrl(first.NextCursor), default);
            Assert.True(requests.Last().EndsWith("?cursor=next%2Bpage", StringComparison.Ordinal));
            status = 401;
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadPageAsync(KickFollowedChannelsReader.BuildPageUrl(""), default));
            status = 403;
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadPageAsync(KickFollowedChannelsReader.BuildPageUrl(""), default));
            status = 200;
            await core.ExecuteScriptAsync("document.cookie='session_token=other-account; Secure; Path=/; SameSite=Lax'");
            var count = requests.Count;
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReadPageAsync(KickFollowedChannelsReader.BuildPageUrl(""), default));
            Assert.Equal(count, requests.Count);
            await core.ExecuteScriptAsync("document.cookie='session_token=; Max-Age=0; Secure; Path=/'");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new KickFollowedChannelsBrowserClient(core).ReadPageAsync(KickFollowedChannelsReader.BuildPageUrl(""), default));
            Assert.Equal(count, requests.Count);
        }
        finally { controller.Close(); window.Close(); }
    });

    private static Task OriginsAsync()
    {
        foreach (var address in new[] { "https://kick.com/", "https://kick.com/api/v2/channels/followed?cursor=abc" })
            Assert.True(KickFollowedChannelsBrowserClient.IsKickOrigin(address));
        foreach (var address in new[] { "http://kick.com/", "https://kick.com.evil.test/", "https://kick.com:8443/", "https://user@kick.com/", "file:///kick.com/", "https://id.kick.com/" })
            Assert.Equal(false, KickFollowedChannelsBrowserClient.IsKickOrigin(address));
        Assert.True(KickFollowedChannelsImporter.IsAllowedNavigation("https://id.kick.com/"));
        Assert.Equal(false, KickFollowedChannelsImporter.IsAllowedNavigation("https://example.com/"));
        return Task.CompletedTask;
    }

    private static AppSettings Settings() => new()
    {
        FollowedChannels = new()
        {
            KickChannelSlugs = ["manual"],
            KickImportedChannelSlugs = ["old"],
            KickFollowsImportedAtUtc = DateTimeOffset.Parse("2026-09-01T12:00:00Z", CultureInfo.InvariantCulture)
        }
    };

    private static MainViewModel Model(AppSettings settings, ISettingsService save, IKickFollowedChannelsImporter importer,
        IFollowedStreamsService? followed = null) => new(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = save,
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            KickFollowedChannelsImporter = importer,
            FollowedStreamsService = followed
        });

    private sealed class TokenProvider : IKickTokenProvider
    {
        public Task<string?> ResolveAsync(ChatSettings settings, IAppLogger logger, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fixture-api-token");
    }

    private sealed class Importer : IKickFollowedChannelsImporter
    {
        internal IReadOnlyList<string>? Result { get; set; }
        internal Exception? Error { get; init; }
        internal Task<IReadOnlyList<string>?>? Pending { get; init; }
        internal CancellationToken Token { get; private set; }
        public Task<IReadOnlyList<string>?> ImportAsync(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            if (Error is not null) throw Error;
            return Pending ?? Task.FromResult(Result);
        }
    }
}
