internal static class CommandLifetimeTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("command lifetime: malformed executable paths do not prevent discovery", MalformedExecutablePaths),
        ("command lifetime: shutdown cancels pending clip creation", ClipCancellationAsync),
        ("command lifetime: late clip results do not open a browser after shutdown", LateClipResultAsync),
        ("command lifetime: disposed view models reject new commands", DisposedCommandsAsync),
        ("command lifetime: canceled Twitch authorization stops before setup", () => CanceledAuthorizationAsync(PlatformKind.Twitch)),
        ("command lifetime: canceled Kick authorization stops before setup", () => CanceledAuthorizationAsync(PlatformKind.Kick)),
        ("command lifetime: stop survives selection changes", () => SelectionChangesAsync(pause: false)),
        ("command lifetime: pause survives selection changes", () => SelectionChangesAsync(pause: true)),
        ("command lifetime: optional Kick username lookup survives deadlines", UsernameDeadlineAsync),
        ("command lifetime: Kick username lookup respects caller cancellation", UsernameCancellationAsync),
        ("command lifetime: Kick username lookup keeps supported payloads", UsernamePayloadsAsync)
    ];

    private static Task MalformedExecutablePaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"StreamStudio-path-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "tool.exe");
        try
        {
            File.WriteAllText(executable, "fixture");
            foreach (var invalid in new[] { "\"", " \" ", "\"\"", "\" \"", "\"unterminated", "trailing\"" })
            {
                Assert.Equal<string?>(null, ExecutableResolver.FindOnPath("tool.exe", invalid));
                Assert.Equal(executable, ExecutableResolver.FindOnPath("tool.exe", $"{invalid};\"{directory}\""));
            }
        }
        finally
        {
            File.Delete(executable);
            Directory.Delete(directory);
        }
        return Task.CompletedTask;
    }

    private static async Task ClipCancellationAsync()
    {
        var service = new PendingClipService();
        await using var model = CreateModel(service, _ => { });
        var command = model.CreateClipCommand.ExecuteAsync();
        try
        {
            await model.DisposeAsync();
            Assert.True(service.Token.CanBeCanceled);
            Assert.True(service.Token.IsCancellationRequested);
        }
        finally
        {
            service.Completion.TrySetCanceled();
            await command;
        }
    }

    private static async Task LateClipResultAsync()
    {
        var opened = 0;
        var service = new PendingClipService();
        await using var model = CreateModel(service, _ => opened++);
        var command = model.CreateClipCommand.ExecuteAsync();
        try
        {
            await model.DisposeAsync();
            var statusAfterShutdown = model.StatusMessage;
            service.Completion.SetResult(new("clip", new Uri("https://clips.twitch.tv/test-clip")));
            await command;
            Assert.Equal(0, opened);
            Assert.Equal(statusAfterShutdown, model.StatusMessage);
        }
        finally
        {
            service.Completion.TrySetCanceled();
            await command;
        }
    }

    private static async Task DisposedCommandsAsync()
    {
        await using var model = CreateModel(new PendingClipService(), _ => { });
        await model.DisposeAsync();
        foreach (var command in new[] { model.CreateClipCommand, model.AuthorizeTwitchCommand, model.AuthorizeKickCommand, model.SaveSettingsCommand })
        {
            Assert.Equal(false, command.CanExecute(null));
        }
    }

    private static async Task CanceledAuthorizationAsync(PlatformKind platform)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Empty settings deliberately stop the old code before any browser or listener is opened.
        // Cancellation must be observed before validation, just as before external side effects.
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            if (platform == PlatformKind.Twitch)
                await TwitchOAuthService.AuthorizeUserTokenAsync(new ChatSettings(), cancellation.Token);
            else
                await KickOAuthService.AuthorizeUserTokenAsync(new ChatSettings(), cancellation.Token);
        });
    }

    private static async Task SelectionChangesAsync(bool pause)
    {
        await using var model = CreateModel(new PendingClipService(), _ => { });
        var tab = model.SelectedTab!;
        var transitionGate = (SemaphoreSlim)typeof(StreamTabViewModel)
            .GetField("playbackTransitionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
        await transitionGate.WaitAsync();
        Task command;
        try
        {
            command = (pause ? model.PauseSelectedCommand : model.StopSelectedCommand).ExecuteAsync();
            Assert.Equal(false, command.IsCompleted);
            model.SelectedTab = null;
        }
        finally
        {
            transitionGate.Release();
        }
        var statusAfterSelection = model.StatusMessage;
        await command;
        Assert.Equal(statusAfterSelection, model.StatusMessage);
    }

    private static async Task UsernameDeadlineAsync()
    {
        var logger = new MemoryLogger();
        using var client = new HttpClient(new FakeHttpMessageHandler(_ => throw new OperationCanceledException("HTTP deadline")));
        Assert.Equal<string?>(null, await KickOAuthService.TryGetCurrentUsernameAsync(client, "access-token", default, logger));
        Assert.True(logger.Entries.Any(entry => entry.Level == AppLogLevel.Warning));
    }

    private static async Task UsernameCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            KickOAuthService.TryGetCurrentUsernameAsync(client, "access-token", cancellation.Token));
    }

    private static async Task UsernamePayloadsAsync()
    {
        foreach (var (payload, expected) in new (string, string?)[]
        {
            ("{\"data\":[{\"name\":\" display name \",\"username\":\"login\"}]}", "display name"),
            ("{\"data\":[null,{\"username\":\"login\"}]}", "login"),
            ("{\"data\":[]}", null),
            ("broken", null)
        })
        {
            using var client = new HttpClient(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }));
            Assert.Equal(expected, await KickOAuthService.TryGetCurrentUsernameAsync(client, "access-token", default));
        }
    }

    private static MainViewModel CreateModel(ITwitchClipService service, Action<Uri> openBrowser)
    {
        var logger = new MemoryLogger();
        var streamlink = new FakeStreamlinkService();
        var playback = new FakePlaybackEngineFactory();
        var chat = new FakeChatClientFactory();
        var settings = new AppSettings();
        var model = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            streamlink, playback, chat, logger, action => action(), twitchClipService: service, openBrowser: openBrowser);
        var tab = TestViewModels.CreateTab(StreamInputParser.FromChannel(PlatformKind.Twitch, "streamer"),
            "best", streamlink, playback, chat, logger, action => action());
        model.Tabs.Add(tab);
        model.SelectedTab = tab;
        return model;
    }

    private sealed class PendingClipService : ITwitchClipService
    {
        internal TaskCompletionSource<TwitchClipResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }

        public Task<TwitchClipResult> CreateLiveClipAsync(StreamTarget target, ChatSettings settings, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return Completion.Task;
        }
    }
}
