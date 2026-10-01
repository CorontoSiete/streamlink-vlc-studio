using System.Windows.Threading;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> VodLinkMetadataTests { get; } =
    [
        ("VOD link metadata: lookup resolves the requested video and avatar without sign-in", VodLinkLookupAsync),
        ("VOD link metadata: lookup rejects malformed or mismatched video identities", VodLinkValidationAsync),
        ("VOD link metadata: cancellation stops the video request", VodLinkLookupCancellationAsync),
        ("VOD link metadata: automatic search opens the real title and paints the tab avatar", VodLinkSearchUiAsync),
        ("VOD link metadata: a deleted video is unavailable", () => VodLinkFailureAsync(deleted: true)),
        ("VOD link metadata: a failed lookup reports missing metadata while preserving playback", () => VodLinkFailureAsync(deleted: false)),
        ("VOD link metadata: a superseded lookup cannot replace the next video", VodLinkStaleSearchAsync),
        ("VOD link metadata: reopening a VOD repairs its placeholder title and avatar", () => ReopenVodLinkMetadataAsync(renamed: false)),
        ("VOD link metadata: reopening a VOD preserves a manually renamed tab", () => ReopenVodLinkMetadataAsync(renamed: true)),
        ("VOD link metadata: failed refresh preserves an existing resolved title and avatar", FailedVodLinkRefreshAsync)
    ];

    internal static IReadOnlyList<(string Name, Func<Task> Run)> LiveVodLinkMetadataTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VOD_LINK_URL")) ? [] :
        [("VOD link metadata: live pasted URL resolves its real title and renders the remote avatar", LiveVodLinkMetadataAsync)];

    private const string VodLinkTestId = "2888300423";
    private const string VodLinkTestTitle = "Recorded stream 🚀";
    private const string VodLinkTestAvatar = "https://static-cdn.jtvnw.net/jtv_user_pictures/vod-link-avatar.jpeg";
    private const string VodLinkTestThumbnail = "https://vod-secure.twitch.tv/vod-link-thumbnail-440x248.jpg";

    private static async Task VodLinkLookupAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (request, token) =>
        {
            requests++;
            Assert.Equal("https://gql.twitch.tv/gql", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.True(request.Headers.Authorization is null);
            Assert.SequenceEqual(["kimne78kx3ncx6brgo4mv6wki5h1ko"], request.Headers.GetValues("Client-Id"));
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(VodLinkTestId, payload.RootElement.GetProperty("variables").GetProperty("id").GetString());
            Assert.Contains("owner { id login displayName profileImageURL", payload.RootElement.GetProperty("query").GetString()!);
            return VodLinkJsonResponse(VodLinkResponse());
        }));
        var video = await new TwitchVodService(new MemoryLogger(), client).GetVideoAsync(VodLinkTestId);
        Assert.NotNull(video);
        Assert.Equal(VodLinkTestId, video!.Id);
        Assert.Equal(VodLinkTestTitle, video.Title);
        Assert.Equal("xqc", video.ChannelLogin);
        Assert.Equal("xQc", video.ChannelDisplayName);
        Assert.Equal("71092938", video.BroadcasterId);
        Assert.Equal(VodLinkTestAvatar, video.ProfileImageUrl);
        Assert.Equal(VodLinkTestThumbnail, video.ThumbnailUrl);
        Assert.Equal("Just Chatting", video.CategoryName);
        Assert.Equal(TimeSpan.FromSeconds(35379), video.Duration);
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T19:24:12Z", CultureInfo.InvariantCulture), video.CreatedAtUtc);
        Assert.Equal(TwitchVodTypeFilter.Archive, video.Type);
        Assert.Equal(1, requests);
    }

    private static async Task VodLinkValidationAsync()
    {
        foreach (var body in new[]
        {
            "[]", "{}", "{\"data\":{}}", "{\"data\":{\"video\":42}}",
            VodLinkResponse(id: "123456"),
            "{\"data\":{\"video\":{\"id\":\"2888300423\",\"owner\":null}}}",
            VodLinkResponse(login: "https://www.twitch.tv/anotherstream"),
            VodLinkResponse().Replace("\"id\":\"71092938\"", "\"id\":null", StringComparison.Ordinal)
        })
        {
            using var client = new HttpClient(new FakeHttpMessageHandler(_ => VodLinkJsonResponse(body)));
            await Assert.ThrowsAsync<JsonException>(() => new TwitchVodService(new MemoryLogger(), client).GetVideoAsync(VodLinkTestId));
        }

        using var missingClient = new HttpClient(new FakeHttpMessageHandler(_ => VodLinkJsonResponse("{\"data\":{\"video\":null}}")));
        Assert.Equal<TwitchVodItem?>(null, await new TwitchVodService(new MemoryLogger(), missingClient).GetVideoAsync(VodLinkTestId));

        using var errorClient = new HttpClient(new FakeHttpMessageHandler(_ => VodLinkJsonResponse("{\"errors\":[{\"message\":\"Video lookup rejected\"}]}")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TwitchVodService(new MemoryLogger(), errorClient).GetVideoAsync(VodLinkTestId));

        var invalidRequests = 0;
        using var invalidClient = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            invalidRequests++;
            return VodLinkJsonResponse(VodLinkResponse());
        }));
        foreach (var id in new[] { "", "123/456", "123\"", "streamer", "１２３" })
            await Assert.ThrowsAsync<ArgumentException>(() => new TwitchVodService(new MemoryLogger(), invalidClient).GetVideoAsync(id));
        Assert.Equal(0, invalidRequests);
    }

    private static async Task VodLinkLookupCancellationAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new AsyncHttpMessageHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return VodLinkJsonResponse(VodLinkResponse());
        }));
        var lookup = new TwitchVodService(new MemoryLogger(), client).GetVideoAsync(VodLinkTestId, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lookup);
    }

    private static Task VodLinkSearchUiAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var imageDirectory = Path.Combine(Path.GetTempPath(), "svs-vod-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imageDirectory);
        try
        {
            var avatarPath = Path.Combine(imageDirectory, "avatar.png");
            await File.WriteAllBytesAsync(avatarPath, VodDownloadTestCatalog.CreateProfileImageBytes());
            var avatarUrl = new Uri(avatarPath).AbsoluteUri;
            using var client = new HttpClient(new FakeHttpMessageHandler(_ => VodLinkJsonResponse(VodLinkResponse(avatar: avatarUrl))));
            var service = new TwitchVodService(new MemoryLogger(), client);
            var video = await service.GetVideoAsync(VodLinkTestId);
            await VerifyVodLinkSearchAndTabAsync(service,
                $"https://www.twitch.tv/videos/{VodLinkTestId}?t=1h2m", video!, "fixture");
        }
        finally { Directory.Delete(imageDirectory, recursive: true); }
    });

    private static Task VodLinkFailureAsync(bool deleted) => TestSta.RunOffscreenAsync(async () =>
    {
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => deleted
            ? VodLinkJsonResponse("{\"data\":{\"video\":null}}")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Unavailable") }));
        await using var main = CreateVodLinkMain(new TwitchVodService(new MemoryLogger(), client));
        main.NewStreamText = $"https://www.twitch.tv/videos/{VodLinkTestId}";
        await TestWait.UntilAsync(() => main.StreamSearchResults.Count == 1 && !main.IsStreamSearchRunning, TimeSpan.FromSeconds(3));
        var result = main.StreamSearchResults.Single();
        Assert.Equal(!deleted, result.CanPlay);
        Assert.Equal(!deleted, result.CanOpen);
        Assert.Equal(false, result.IsLive);
        Assert.Equal("", result.Target.ProfileImageUrl);
        Assert.Contains(deleted ? "not found" : "Title and avatar could not be loaded", result.StatusText);
        Assert.Equal(result.StatusText, main.StreamSearchStatus);
    });

    private static Task VodLinkStaleSearchAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new PendingVodLinkService();
        await using var main = CreateVodLinkMain(service);
        try
        {
            main.NewStreamText = $"https://www.twitch.tv/videos/{VodLinkTestId}";
            await TestWait.UntilAsync(() => service.Requests.Count == 1, TimeSpan.FromSeconds(3));
            // Join the automatic first search so its entire completion can be awaited
            // after the replacement, while the view model is still alive.
            var firstSearch = main.AddAndPlayCommand.ExecuteAsync();
            main.NewStreamText = "https://www.twitch.tv/videos/2888300424";
            await TestWait.UntilAsync(() => service.Requests.Count == 2, TimeSpan.FromSeconds(3));
            Assert.True(service.Requests[0].Token.IsCancellationRequested);
            var latest = new TwitchVodItem("2888300424", "", "1234", "anotherstream", "AnotherStream", "Another recording", "",
                "https://www.twitch.tv/videos/2888300424", "", null, null, TimeSpan.FromHours(1), null, TwitchVodTypeFilter.Archive,
                ProfileImageUrl: "https://static-cdn.jtvnw.net/another-avatar.jpg");
            service.Requests[1].Completion.SetResult(latest);
            await TestWait.UntilAsync(() => main.StreamSearchResults.Count == 1 && !main.IsStreamSearchRunning, TimeSpan.FromSeconds(3));
            var nextResult = main.StreamSearchResults.Single();
            service.Requests[0].Completion.SetResult(latest with { Id = VodLinkTestId, Title = "Old recording", ProfileImageUrl = VodLinkTestAvatar });
            await firstSearch.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(nextResult, main.StreamSearchResults.Single());
            Assert.Equal("Another recording", nextResult.DisplayName);
            Assert.Equal(latest.ProfileImageUrl, nextResult.Target.ProfileImageUrl);
            Assert.Equal(latest.Id, nextResult.Target.MediaId);
        }
        finally
        {
            foreach (var request in service.Requests) request.Completion.TrySetResult(null);
        }
    });

    private static Task LiveVodLinkMetadataAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var url = Environment.GetEnvironmentVariable("SVS_TEST_VOD_LINK_URL")!;
        Assert.True(StreamInputParser.TryParseTwitchVodUrl(url, out var target));
        var service = new TwitchVodService(new MemoryLogger());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var video = await service.GetVideoAsync(target!.MediaId, cancellation.Token);
        Assert.NotNull(video);
        Assert.True(!string.IsNullOrWhiteSpace(video!.Title));
        Assert.True(!string.IsNullOrWhiteSpace(video.ProfileImageUrl));
        await VerifyVodLinkSearchAndTabAsync(service, url, video, "live");
        Console.WriteLine($"Live VOD {video.Id}: owner {video.ChannelDisplayName}, title {video.Title}, avatar {video.ProfileImageUrl}.");
    });

    private static Task ReopenVodLinkMetadataAsync(bool renamed) => TestSta.RunOffscreenAsync(async () =>
    {
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => VodLinkJsonResponse(VodLinkResponse())));
        var streamlink = new FakeStreamlinkService();
        await using var main = CreateVodLinkMain(new TwitchVodService(new MemoryLogger(), client), streamlink);
        var url = $"https://www.twitch.tv/videos/{VodLinkTestId}";
        await main.OpenStreamAsync(StreamInputParser.ParseCandidates(url).Single());
        var tab = main.SelectedTab!;
        tab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing, TimeSpan.FromSeconds(3));
        if (renamed) tab.Title = "My saved tab";
        main.SelectHomeCommand.Execute(null);
        main.NewStreamText = url;
        await TestWait.UntilAsync(() => main.StreamSearchResults.Count == 1 && !main.IsStreamSearchRunning, TimeSpan.FromSeconds(3));
        await main.StreamSearchResults.Single().OpenCommand.ExecuteAsync();
        Assert.Equal(tab, main.SelectedTab);
        Assert.Equal(1, main.Tabs.Count);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(renamed ? "My saved tab" : VodLinkTestTitle, tab.Title);
        Assert.Equal(VodLinkTestAvatar, tab.ProfileImageUrl);
    });

    private static Task FailedVodLinkRefreshAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var available = true;
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => available
            ? VodLinkJsonResponse(VodLinkResponse())
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Unavailable") }));
        var streamlink = new FakeStreamlinkService();
        await using var main = CreateVodLinkMain(new TwitchVodService(new MemoryLogger(), client), streamlink);
        var url = $"https://www.twitch.tv/videos/{VodLinkTestId}";
        main.NewStreamText = url;
        await TestWait.UntilAsync(() => main.StreamSearchResults.Count == 1 && !main.IsStreamSearchRunning, TimeSpan.FromSeconds(3));
        await main.StreamSearchResults.Single().OpenCommand.ExecuteAsync();
        var tab = main.SelectedTab!;
        tab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing && main.NewStreamText.Length == 0, TimeSpan.FromSeconds(3));
        main.SelectHomeCommand.Execute(null);
        available = false;
        main.NewStreamText = url;
        await TestWait.UntilAsync(() => main.StreamSearchResults.Count == 1 && !main.IsStreamSearchRunning, TimeSpan.FromSeconds(3));
        Assert.Contains("Title and avatar could not be loaded", main.StreamSearchResults.Single().StatusText);
        await main.StreamSearchResults.Single().OpenCommand.ExecuteAsync();
        Assert.Equal(tab, main.SelectedTab);
        Assert.Equal(1, main.Tabs.Count);
        Assert.Equal(1, streamlink.ResolveStreamUrlCount);
        Assert.Equal(VodLinkTestTitle, tab.Title);
        Assert.Equal(VodLinkTestAvatar, tab.ProfileImageUrl);
    });

    private static async Task VerifyVodLinkSearchAndTabAsync(ITwitchVodService service, string url,
        TwitchVodItem expectedVideo, string artifactName)
    {
        var title = expectedVideo.Title;
        var avatarUrl = expectedVideo.ProfileImageUrl;
        var viewers = new FakeViewerCountService();
        var liveMetadata = new FakeStreamMetadataService(new StreamMetadataResult(StreamMetadataState.Available, "", "", "Unexpected live lookup"));
        var streamlink = new FakeStreamlinkService();
        await using var main = CreateVodLinkMain(service, streamlink, viewers, liveMetadata);
        main.NewStreamText = url;
        await TestWait.UntilAsync(() => main.StreamSearchResults.Count == 1 && !main.IsStreamSearchRunning, TimeSpan.FromSeconds(20));
        var result = main.StreamSearchResults.Single();
        Assert.Equal(title, result.DisplayName);
        Assert.Equal(title, result.Target.DisplayTitle);
        Assert.Equal(avatarUrl, result.Target.ProfileImageUrl);
        Assert.Equal(expectedVideo.ChannelLogin, result.Target.Channel);
        Assert.Equal(expectedVideo.BroadcasterId, result.Target.BroadcasterId);
        Assert.True(result.Target.MediaDuration > TimeSpan.Zero);
        Assert.True(result.Target.MediaStartedAtUtc is not null);
        Assert.True(result.HasThumbnail);
        Assert.Equal(StreamInputParser.ParseCandidates(url).Single().TabIdentityKey, result.Target.TabIdentityKey);
        Assert.Equal(StreamTargetKind.TwitchVod, result.Target.Kind);
        Assert.Equal(true, result.CanPlay);
        Assert.Equal(false, result.IsLive);
        Assert.Equal("VOD", result.StateText);
        Assert.Contains(title, main.StreamSearchStatus);
        Assert.Equal(0, viewers.CallCount);
        Assert.Equal(0, liveMetadata.CallCount);
        Assert.Equal(0, streamlink.ProbeRequests.Count);

        await result.OpenCommand.ExecuteAsync();
        var tab = main.SelectedTab!;
        Assert.Equal(title, tab.Title);
        Assert.Equal(result.Target, tab.Target);
        Assert.Equal(avatarUrl, tab.ProfileImageUrl);
        Assert.True(tab.HasProfileImage);
        Assert.Equal(title, main.TabStripItems.Single().Title);
        Assert.Equal(avatarUrl, main.TabStripItems.Single().ProfileImageUrl);
        tab.SetVideoHandle(new IntPtr(1234));
        await TestWait.UntilAsync(() => tab.Status == PlaybackStatus.Playing, TimeSpan.FromSeconds(3));
        Assert.Equal(result.Target, streamlink.ResolveStreamUrlRequests.Single().Target);

        var window = new MainWindow { DataContext = main };
        RemoveMainWindowAutomaticStartup(window);
        SetMainWindowViewModel(window, main);
        try
        {
            var strip = (ListBox)window.FindName("TabListBox");
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(theme);
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                var avatar = FindVisualDescendants<AnimatedEmoteImage>(strip).Single(image => image.ImageUrl == avatarUrl);
                await TestWait.UntilAsync(() => avatar.Source is not null, TimeSpan.FromSeconds(12));
                LayoutStudioPolishWindow(window, new Size(1320, 820));
                Assert.Equal(Visibility.Visible, avatar.Visibility);
                Assert.True(avatar.ActualWidth > 0 && avatar.ActualHeight > 0);
                Assert.True(BitmapAssert.CountRgbaPixels(CopyVodLinkPixels(WpfVisualTest.Render(avatar)), 0,
                    (_, _, _) => true) > 0, "The tab must render decoded avatar pixels.");
                SaveDownloadCardImage(strip, $"vod-link-{artifactName}-{theme}");
            }
        }
        finally
        {
            window.Close();
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
        }
    }

    private static byte[] CopyVodLinkPixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static MainViewModel CreateVodLinkMain(ITwitchVodService service, FakeStreamlinkService? streamlink = null,
        IViewerCountService? viewers = null, IStreamMetadataService? metadata = null)
    {
        var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\Program Files\VideoLAN\VLC" };
        settings.Chat.ConnectAutomatically = false;
        var dispatcher = Dispatcher.CurrentDispatcher;
        return TestViewModels.CreateMain(settings, new FakeSettingsService(settings), streamlink ?? new FakeStreamlinkService(),
            new FakePlaybackEngineFactory(), new FakeChatClientFactory(), new MemoryLogger(),
            action => dispatcher.BeginInvoke(action), twitchVodService: service, viewerCountService: viewers,
            streamMetadataService: metadata, streamSearchDebounceInterval: TimeSpan.Zero);
    }

    private static string VodLinkResponse(string id = VodLinkTestId, string avatar = VodLinkTestAvatar, string login = "xqc") =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                video = new
                {
                    id,
                    title = VodLinkTestTitle,
                    lengthSeconds = 35379,
                    createdAt = "2026-09-30T19:24:12Z",
                    broadcastType = "ARCHIVE",
                    previewThumbnailURL = VodLinkTestThumbnail,
                    game = new { name = "Just Chatting" },
                    owner = new { id = "71092938", login, displayName = "xQc", profileImageURL = avatar }
                }
            }
        });

    private static HttpResponseMessage VodLinkJsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class PendingVodLinkService : ITwitchVodService
    {
        internal List<(CancellationToken Token, TaskCompletionSource<TwitchVodItem?> Completion)> Requests { get; } = [];

        public Task<TwitchVodItem?> GetVideoAsync(string vodId, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<TwitchVodItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((cancellationToken, completion));
            return completion.Task;
        }

        public Task<TwitchVodSearchResult> SearchAsync(TwitchVodSearchRequest request, AppSettings settings,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("A pasted video must use its ID lookup.");
    }
}
