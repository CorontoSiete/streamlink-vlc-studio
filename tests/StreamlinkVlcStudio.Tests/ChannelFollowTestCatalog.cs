using StreamlinkVlcStudio.App.Wpf.Follows;

internal static partial class ChannelFollowTestCatalog
{
    private const string Gql = "https://gql.twitch.tv/gql";
    private static readonly StreamTarget Twitch = StreamInputParser.FromChannel(PlatformKind.Twitch, "streamer");
    private static readonly StreamTarget Kick = StreamInputParser.FromChannel(PlatformKind.Kick, "streamer");

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("channel follow: status reads both states without authorizing a write", ReadOnlyContractsAsync),
        ("channel follow: unfollow verifies server state and rejects wrong targets and opposite writes", UnfollowContractsAsync),
        ("channel follow: each opened stream reads status once and toggles through unfollow and follow", StatusAndToggleAsync),
        ("channel follow: stream opening workflow checks background streams once and rechecks after closing", StatusOpenWorkflowAsync),
        ("channel follow: completed failed and unverified status attempts never repeat on selection", StatusAttemptsOnceAsync),
        ("channel follow: closing and reopening cancels old status reads and ignores late replies", StatusCloseAndReopenAsync),
        ("channel follow: clearing tabs and shutdown cancel all pending status reads", StatusCleanupAsync),
        ("channel follow: canceled and failed unfollows preserve confirmed state", FailedUnfollowAsync),
        ("channel follow: stale status reads cannot change another channel or override a mutation", StatusRacesAsync),
        ("channel follow: unfollow persists the verified Kick list while preserving manual channels", KickUnfollowSnapshotAsync),
        ("channel follow: real browsers read status without writes or sign-in prompts", BrowserStatusAsync),
        ("channel follow: real browsers unfollow once and verify persisted state", BrowserUnfollowAsync),
        ("channel follow: Twitch unfollow waits for its named channel control beside an unnamed About control", BrowserUnfollowNamedControlAsync),
        ("channel follow: Twitch unfollow observes the confirmation dialog before committing an ignored opening", BrowserUnfollowOpeningAsync),
        ("channel follow: Twitch unfollow rejects duplicate named controls and mismatched confirmation dialogs", BrowserUnfollowControlSafetyAsync),
        ("channel follow: real browsers reject unconfirmed unfollows and wrong targets", BrowserUnfollowFailureAsync),
        ("channel follow: Twitch verifies existing follows and matches the mutation's user and target ID", TwitchContractsAsync),
        ("channel follow: Twitch partial queries preserve unknown and explicit follow permission", TwitchPartialFollowStateAsync),
        ("channel follow: Twitch browser waits for follow permission after a partial player query", BrowserPartialFollowStateAsync),
        ("channel follow: Twitch browser rejects an explicit follow restriction without a mutation", BrowserFollowRestrictionAsync),
        ("channel follow: Twitch retries an ignored click only before a native follow request", BrowserIgnoredFollowClickAsync),
        ("channel follow: Twitch rejects errors malformed payloads wrong targets and optimistic states", TwitchErrorsAsync),
        ("channel follow: Twitch batch responses match their corresponding request", TwitchBatchesAsync),
        ("channel follow: Twitch integrity challenge permits one verified website retry", TwitchIntegrityRetryAsync),
        ("channel follow: website routes and unresolved VOD IDs cannot follow another target", RoutesAsync),
        ("channel follow: request guard blocks different targets premature follows and duplicate writes", RequestGuardAsync),
        ("channel follow: native request inspection preserves bodies through real HTTP transport", BrowserTransportBodyAsync),
        ("channel follow: Kick acknowledgements never replace checking the followed list", KickContractsAsync),
        ("channel follow: command captures its broadcaster across tab switches and prevents duplicate work", CommandAsync),
        ("channel follow: confirmed Kick snapshot preserves manual channels and is saved before refreshing", KickCommandAsync),
        ("channel follow: confirmed follow leaves the busy state before a delayed library refresh", ConfirmedBeforeRefreshAsync),
        ("channel follow: failed canceled and mismatched results never change the library", FailedCommandsAsync),
        ("channel follow: a failed local save remains separate from a confirmed platform follow", SaveFailureAsync),
        ("channel follow: shutdown cancels follow and ignores late results", ShutdownAsync),
        ("channel follow: importing and clearing Kick follows cannot race a follow", ImportGateAsync),
        ("channel follow: unresolved Twitch video IDs are disabled while known VOD broadcasters can be followed", VodTargetsAsync),
        ("channel follow: toolbar binds its label command tooltip and accessible name", ToolbarAsync),
        ("channel follow: real Twitch browser submits once and verifies its server result", () => BrowserSuccessAsync(PlatformKind.Twitch)),
        ("channel follow: real Twitch browser completes the website integrity retry", BrowserIntegrityRetryAsync),
        ("channel follow: Twitch verification requires real channel input and confirms the server result", BrowserUserVerificationAsync),
        ("channel follow: Twitch unfollow verification requires trusted alertdialog confirmation", () => BrowserUserVerificationAsync(false)),
        ("channel follow: real Kick browser submits once and verifies all follow-list pages", () => BrowserSuccessAsync(PlatformKind.Kick)),
        ("channel follow: real browsers do not click already-followed streamers", AlreadyFollowedAsync),
        ("channel follow: real browsers reject HTTP and GraphQL failures", BrowserErrorsAsync),
        ("channel follow: real browser requires sign-in and honors cancel before a mutation", BrowserSignInAsync),
        ("channel follow: real browser rejects an account change before clicking", BrowserAccountBeforeAsync),
        ("channel follow: real browser rejects an account change after posting", BrowserAccountAfterAsync),
        ("channel follow: real browser rejects ambiguous controls and route changes", BrowserSafetyAsync),
        ("channel follow: cancellation closes browsers and their pending native parent", BrowserCancellationAsync)
    ];

    private static string User(bool following, string login = "streamer", string id = "123") => JsonSerializer.Serialize(new
    {
        id,
        login,
        displayName = "Streamer",
        self = new { canFollow = true, follower = following ? new { followedAt = "2026-10-08T12:00:00Z", disableNotifications = false } : null }
    });

    private static string State(bool following, string login = "streamer") => "{\"data\":{\"user\":" + User(following, login) + "}}";
    private static string MutationRequest(string id = "123") => JsonSerializer.Serialize(new
    {
        operationName = "FollowButton_FollowUser",
        variables = new { input = new { targetID = id, disableNotifications = false } }
    });
    private static string MutationResponse(string login = "streamer", string id = "123") =>
        "{\"data\":{\"followUser\":{\"error\":null,\"follow\":{\"user\":" + User(true, login, id) + "}}}}";

    // Shape observed in the authenticated Twitch failure on 2026-10-20; no credentials or request IDs.
    private const string IntegrityChallenge = "{\"errors\":[{\"message\":\"failed integrity check\",\"path\":[\"followUser\"]}],\"data\":{\"followUser\":null},\"extensions\":{\"challenge\":{\"type\":\"integrity\"}}}";

    private static Task TwitchIntegrityRetryAsync()
    {
        var tracker = Unfollowed();
        Assert.Equal(false, tracker.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        tracker.Observe("POST", Gql, 200, MutationRequest(), IntegrityChallenge);
        Assert.Equal<string?>(null, tracker.Error);
        Assert.Equal(false, tracker.Confirmed);
        Assert.Equal(false, tracker.BlocksUnexpectedFollow("POST", Gql, "[" + MutationRequest() + "]"));
        tracker.Observe("POST", Gql, 200, "[" + MutationRequest() + "]", "[" + MutationResponse() + "]");
        Assert.True(tracker.Confirmed);
        Assert.True(tracker.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));

        foreach (var wrong in new[] { MutationRequest("456"), "[" + MutationRequest() + "," + MutationRequest() + "]" })
        {
            var guarded = Unfollowed();
            Assert.Equal(false, guarded.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
            guarded.Observe("POST", Gql, 200, MutationRequest(), IntegrityChallenge);
            Assert.True(guarded.BlocksUnexpectedFollow("POST", Gql, wrong));
        }
        var repeated = Unfollowed();
        Assert.Equal(false, repeated.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        repeated.Observe("POST", Gql, 200, MutationRequest(), IntegrityChallenge);
        Assert.Equal(false, repeated.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        repeated.Observe("POST", Gql, 200, MutationRequest(), IntegrityChallenge);
        Assert.True(repeated.TwitchUserRetryPending);
        Assert.Equal<string?>(null, repeated.Error);
        Assert.True(repeated.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));

        foreach (var response in new[] { IntegrityChallenge.Replace("integrity\"", "unknown\""),
            IntegrityChallenge.Replace("\"followUser\":null", "\"followUser\":{}") })
        {
            var invalid = Unfollowed();
            Assert.Equal(false, invalid.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
            invalid.Observe("POST", Gql, 200, MutationRequest(), response);
            Assert.NotNull(invalid.Error);
            Assert.True(invalid.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        }
        return Task.CompletedTask;
    }

    private static ChannelFollowResponseTracker Unfollowed()
    {
        var tracker = new ChannelFollowResponseTracker(Twitch);
        tracker.Observe("POST", Gql, 200, "{}", State(false));
        Assert.True(tracker.HasTwitchFollowState);
        Assert.Equal<bool?>(true, tracker.TwitchCanFollow);
        Assert.Equal(false, tracker.Confirmed);
        tracker.Arm();
        return tracker;
    }

    private static Task TwitchContractsAsync()
    {
        var existing = new ChannelFollowResponseTracker(Twitch);
        existing.Observe("POST", Gql, 200, "{}", State(true));
        Assert.True(existing.Confirmed);
        var expired = new ChannelFollowResponseTracker(Twitch);
        expired.Observe("POST", Gql, 401, "{}", "");
        Assert.True(expired.RequiresSignIn);
        var tracker = Unfollowed();
        tracker.Observe("POST", Gql, 200, MutationRequest(), MutationResponse());
        Assert.True(tracker.Confirmed);
        Assert.Equal<string?>(null, tracker.Error);
        tracker.Reset();
        Assert.Equal(false, tracker.Confirmed);
        Assert.Equal("", tracker.TwitchUserId);
        Assert.Equal("", tracker.TwitchDisplayName);
        var known = new ChannelFollowResponseTracker(Twitch with { BroadcasterId = "456" });
        known.Observe("POST", Gql, 200, "{}", State(true));
        Assert.Equal(false, known.Confirmed);
        return Task.CompletedTask;
    }

    private static Task RequestGuardAsync()
    {
        var premature = new ChannelFollowResponseTracker(Twitch);
        Assert.True(premature.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        foreach (var body in new[] { MutationRequest("456"), "[" + MutationRequest() + "," + MutationRequest("456") + "]", "{" })
        {
            var wrong = Unfollowed();
            Assert.True(wrong.BlocksUnexpectedFollow("POST", Gql, body));
            Assert.NotNull(wrong.Error);
        }
        var valid = Unfollowed();
        Assert.Equal(false, valid.BlocksUnexpectedFollow("POST", Gql, "{\"operationName\":\"FollowButton_FollowData\"}"));
        Assert.Equal(false, valid.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        Assert.True(valid.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        valid.Reset();
        valid.Observe("POST", Gql, 200, "{}", State(false));
        valid.Arm();
        Assert.True(valid.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        foreach (var channel in new[] { "streamer", "other" })
        {
            var kick = new ChannelFollowResponseTracker(Kick);
            kick.Arm();
            Assert.Equal(channel != "streamer", kick.BlocksUnexpectedFollow("POST", $"https://kick.com/api/v2/channels/{channel}/follow", ""));
            if (channel == "streamer") Assert.True(kick.BlocksUnexpectedFollow("POST", $"https://kick.com/api/v2/channels/{channel}/follow", ""));
        }
        return Task.CompletedTask;
    }

    private static Task TwitchErrorsAsync()
    {
        foreach (var response in new[]
        {
            "{}", "[]", "null", "{", MutationResponse("other"), MutationResponse(id: "456"),
            "{\"data\":{\"followUser\":null},\"errors\":[{\"message\":\"private-session-token\"}]}",
            "{\"data\":{\"followUser\":{\"error\":{\"code\":\"UNVERIFIED_EMAIL\"},\"follow\":null}}}",
            MutationResponse().Replace("2026-10-08T12:00:00Z", "not-a-date"),
            new string('x', ChannelFollowResponseTracker.MaximumResponseBytes + 1)
        })
        {
            var tracker = Unfollowed();
            tracker.Observe("POST", Gql, 200, MutationRequest(), response);
            Assert.Equal(false, tracker.Confirmed);
            Assert.Equal(false, (tracker.Error ?? "").Contains("private-session-token", StringComparison.Ordinal));
        }
        var wrongRequest = Unfollowed();
        wrongRequest.Observe("POST", Gql, 200, MutationRequest("456"), MutationResponse());
        Assert.Equal(false, wrongRequest.Confirmed);
        var notArmed = new ChannelFollowResponseTracker(Twitch);
        notArmed.Observe("POST", Gql, 200, MutationRequest(), MutationResponse());
        Assert.Equal(false, notArmed.Confirmed);
        foreach (var status in new[] { 401, 403, 429, 500 })
        {
            var tracker = Unfollowed();
            tracker.Observe("POST", Gql, status, MutationRequest(), "");
            Assert.Equal(false, tracker.Confirmed);
            Assert.NotNull(tracker.Error);
        }
        return Task.CompletedTask;
    }

    private static Task TwitchBatchesAsync()
    {
        var tracker = Unfollowed();
        tracker.Observe("POST", Gql, 200, "[{}," + MutationRequest() + "]", "[{}," + MutationResponse() + "]");
        Assert.True(tracker.Confirmed);
        var mismatch = Unfollowed();
        mismatch.Observe("POST", Gql, 200, "[" + MutationRequest() + ",{}]", "[{}," + MutationResponse() + "]");
        Assert.Equal(false, mismatch.Confirmed);
        return Task.CompletedTask;
    }

    private static Task RoutesAsync()
    {
        foreach (var target in new[] { Twitch, Kick })
        {
            var tracker = new ChannelFollowResponseTracker(target);
            Assert.True(tracker.IsChannelPage(target.Url));
            Assert.Equal(false, tracker.IsChannelPage(target.Url + "/videos"));
            Assert.Equal(false, tracker.IsChannelPage(target.Url.Replace("streamer", "other")));
            var route = target.Platform == PlatformKind.Twitch ? Gql : "https://kick.com/api/v2/channels/streamer/follow";
            foreach (var bad in new[] { route.Replace("https:", "http:"), route + "?redirect=x", route + "#x", route.Replace(".com/", ".com.evil.test/"), route.Replace(".tv/", ".tv.evil.test/"), route.Replace("https://", "https://user@"), route.Replace("/follow", "/unfollow") })
            {
                if (bad == route) continue;
                Assert.Equal(false, tracker.IsResponseCandidate("POST", bad));
            }
            Assert.Equal(false, tracker.IsResponseCandidate("DELETE", route));
        }
        foreach (var target in new[] { Twitch with { Channel = "../settings" }, Twitch with { Platform = (PlatformKind)200 }, StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch) })
            Assert.Equal(false, ChannelFollowResponseTracker.TryGetChannel(target, out _));
        return Task.CompletedTask;
    }

    private static Task KickContractsAsync()
    {
        var tracker = new ChannelFollowResponseTracker(Kick);
        const string route = "https://kick.com/api/v2/channels/streamer/follow";
        tracker.Observe("POST", route, 200, "", "{}");
        Assert.Equal(false, tracker.KickRequestAcknowledged);
        tracker.Arm();
        tracker.Observe("POST", route, 200, "", "{}");
        Assert.True(tracker.KickRequestAcknowledged);
        Assert.Equal(false, tracker.Confirmed);
        tracker.Reset();
        tracker.Arm();
        tracker.Observe("POST", route, 429, "", "{}");
        Assert.Contains("limiting", tracker.Error!);
        return Task.CompletedTask;
    }

    private static async Task CommandAsync()
    {
        var service = new PendingFollowService();
        var followed = new FakeFollowedStreamsService();
        await using var model = Model(service, followed: followed);
        var first = AddTab(model, Twitch);
        var other = AddTab(model, Kick);
        model.SelectedTab = first;
        var command = model.FollowSelectedChannelCommand.ExecuteAsync();
        Assert.Equal(Twitch, service.Target);
        Assert.Equal("Following…", model.FollowButtonText);
        await model.FollowSelectedChannelCommand.ExecuteAsync();
        Assert.Equal(1, service.Calls);
        model.SelectedTab = other;
        Assert.Equal("Follow", model.FollowButtonText);
        service.Completion.SetResult(new(PlatformKind.Twitch, "streamer"));
        await command;
        Assert.Equal(other, model.SelectedTab);
        Assert.Equal(false, model.IsSelectedChannelFollowed);
        Assert.Equal(1, followed.CallCount);
        model.SelectedTab = first;
        Assert.True(model.IsSelectedChannelFollowed);
        Assert.Equal("Following", model.FollowButtonText);
    }

    private static async Task KickCommandAsync()
    {
        var settings = new AppSettings();
        settings.FollowedChannels.KickChannelSlugs = ["manual"];
        settings.FollowedChannels.KickImportedChannelSlugs = ["old"];
        var save = new FakeSettingsService(settings);
        var service = new PendingFollowService();
        var refresh = new FakeFollowedStreamsService();
        var savedBeforeRefresh = false;
        refresh.EnqueueResult(_ =>
        {
            savedBeforeRefresh = save.SaveCount > 0;
            Assert.SequenceEqual(new[] { "offline", "streamer" }, settings.FollowedChannels.KickImportedChannelSlugs);
            return Task.FromResult(new FollowedLiveStreamsResult([], []));
        });
        await using var model = Model(service, settings, save, refresh);
        model.SelectedTab = AddTab(model, Kick);
        var command = model.FollowSelectedChannelCommand.ExecuteAsync();
        service.Completion.SetResult(new(PlatformKind.Kick, "streamer", ["offline", "streamer"]));
        await command;
        Assert.True(savedBeforeRefresh);
        Assert.SequenceEqual(new[] { "manual" }, settings.FollowedChannels.KickChannelSlugs);
        Assert.NotNull(settings.FollowedChannels.KickFollowsImportedAtUtc);
        Assert.Contains("2 Kick follows imported", model.KickImportedFollowsSummary);
        Assert.True(model.IsSelectedChannelFollowed);
    }

    private static async Task ConfirmedBeforeRefreshAsync()
    {
        foreach (var target in new[] { Twitch, Kick })
        {
            var service = new PendingFollowService();
            var refresh = new FakeFollowedStreamsService();
            var pendingRefresh = new TaskCompletionSource<FollowedLiveStreamsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            refresh.EnqueueResult(_ => pendingRefresh.Task);
            await using var model = Model(service, followed: refresh);
            model.SelectedTab = AddTab(model, target);
            var command = model.FollowSelectedChannelCommand.ExecuteAsync();
            service.Completion.SetResult(new(target.Platform, target.Channel,
                target.Platform == PlatformKind.Kick ? [target.Channel] : null));
            try
            {
                await TestWait.UntilAsync(() => refresh.CallCount == 1, TimeSpan.FromSeconds(5));
                Assert.True(model.IsSelectedChannelFollowed);
                Assert.Equal("Following", model.FollowButtonText);
                await command.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(command.IsCompleted, "A library refresh must not keep a confirmed follow command running.");
                Assert.Equal(false, model.IsKickFollowChangeBusy);
                Assert.True(model.FollowSelectedChannelCommand.CanExecute(null));
            }
            finally
            {
                pendingRefresh.TrySetResult(new([], []));
                await command;
            }
        }
    }

    private static async Task FailedCommandsAsync()
    {
        foreach (var failure in new[] { "closed", "error", "canceled", "platform", "channel", "missing", "invalid" })
        {
            var settings = new AppSettings();
            settings.FollowedChannels.KickImportedChannelSlugs = ["old"];
            var save = new FakeSettingsService(settings);
            var service = new PendingFollowService();
            var followed = new FakeFollowedStreamsService();
            await using var model = Model(service, settings, save, followed);
            model.SelectedTab = AddTab(model, Kick);
            var command = model.FollowSelectedChannelCommand.ExecuteAsync();
            switch (failure)
            {
                case "closed": service.Completion.SetResult(null); break;
                case "error": service.Completion.SetException(new InvalidOperationException("Kick refused the follow")); break;
                case "canceled": service.Completion.SetCanceled(); break;
                case "platform": service.Completion.SetResult(new(PlatformKind.Twitch, "streamer")); break;
                case "channel": service.Completion.SetResult(new(PlatformKind.Kick, "other", ["other"])); break;
                case "missing": service.Completion.SetResult(new(PlatformKind.Kick, "streamer", ["other"])); break;
                default: service.Completion.SetResult(new(PlatformKind.Kick, "streamer", ["streamer", "../bad"])); break;
            }
            await command;
            Assert.Equal(false, model.IsSelectedChannelFollowed);
            Assert.Equal("Follow", model.FollowButtonText);
            Assert.SequenceEqual(new[] { "old" }, settings.FollowedChannels.KickImportedChannelSlugs);
            Assert.Equal(0, save.SaveCount);
            Assert.Equal(0, followed.CallCount);
            Assert.True(model.FollowSelectedChannelCommand.CanExecute(null));
        }
    }

    private static async Task SaveFailureAsync()
    {
        var settings = new AppSettings();
        var save = new FakeSettingsService(settings) { SaveException = new IOException("fixture save failure") };
        var service = new PendingFollowService();
        await using var model = Model(service, settings, save);
        model.SelectedTab = AddTab(model, Kick);
        var command = model.FollowSelectedChannelCommand.ExecuteAsync();
        service.Completion.SetResult(new(PlatformKind.Kick, "streamer", ["streamer"]));
        await command;
        Assert.True(model.IsSelectedChannelFollowed);
        Assert.Contains("could not be saved", model.StatusMessage);
        Assert.True(model.HasSettingsSaveError);
        save.SaveException = null;
    }

    private static async Task ShutdownAsync()
    {
        var service = new PendingFollowService();
        var settings = new AppSettings();
        var save = new FakeSettingsService(settings);
        var model = Model(service, settings, save);
        model.SelectedTab = AddTab(model, Kick);
        var command = model.FollowSelectedChannelCommand.ExecuteAsync();
        var disposal = model.DisposeAsync().AsTask();
        Assert.True(service.Token.IsCancellationRequested);
        var status = model.StatusMessage;
        service.Completion.SetResult(new(PlatformKind.Kick, "streamer", ["streamer"]));
        await Task.WhenAll(command, disposal);
        Assert.Equal(status, model.StatusMessage);
        Assert.Equal(false, model.IsSelectedChannelFollowed);
        Assert.Equal(0, save.SaveCount);
        Assert.Equal(0, settings.FollowedChannels.KickImportedChannelSlugs.Count);
        Assert.Equal(false, model.FollowSelectedChannelCommand.CanExecute(null));
    }

    private static async Task ImportGateAsync()
    {
        var service = new PendingFollowService();
        var importer = new PendingImporter();
        await using var model = Model(service, importer: importer);
        model.SelectedTab = AddTab(model, Kick);
        var follow = model.FollowSelectedChannelCommand.ExecuteAsync();
        Assert.Equal(false, model.ImportKickFollowsCommand.CanExecute(null));
        Assert.Equal(false, model.ClearImportedKickFollowsCommand.CanExecute(null));
        service.Completion.SetResult(new(PlatformKind.Kick, "streamer", ["streamer"]));
        await follow;
        var import = model.ImportKickFollowsCommand.ExecuteAsync();
        Assert.Equal(false, model.FollowSelectedChannelCommand.CanExecute(null));
        importer.Completion.SetResult(["streamer"]);
        await import;
        Assert.True(model.FollowSelectedChannelCommand.CanExecute(null));
    }

    private static async Task VodTargetsAsync()
    {
        await using var model = Model(new PendingFollowService());
        Assert.Equal(false, model.FollowSelectedChannelCommand.CanExecute(null));
        model.SelectedTab = AddTab(model, StreamInputParser.Parse("https://www.twitch.tv/videos/123", PlatformKind.Twitch));
        Assert.Equal(false, model.FollowSelectedChannelCommand.CanExecute(null));
        model.SelectedTab = AddTab(model, Twitch with { Kind = StreamTargetKind.TwitchVod, MediaId = "123", BroadcasterId = "123" });
        Assert.True(model.FollowSelectedChannelCommand.CanExecute(null));
        model.SelectedTab = AddTab(model, Kick with { Kind = StreamTargetKind.KickVod, MediaId = "vod-id" });
        Assert.True(model.FollowSelectedChannelCommand.CanExecute(null));
    }

    private static Task ToolbarAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new PendingFollowService();
        await using var model = Model(service);
        var window = new MainWindow { DataContext = model };
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        try
        {
            model.SelectedTab = AddTab(model, Twitch);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
            var button = (Button)window.FindName("TopFollowButton");
            Assert.Equal(model.FollowSelectedChannelCommand, button.Command);
            Assert.Equal("Follow selected streamer", System.Windows.Automation.AutomationProperties.GetName(button));
            window.Measure(new Size(1100, 780));
            window.Arrange(new Rect(0, 0, 1100, 780));
            window.UpdateLayout();
            Assert.Equal("Follow", button.Content);
            Assert.Contains("streamer", (string)button.ToolTip);
            var command = model.FollowSelectedChannelCommand.ExecuteAsync();
            service.Completion.SetResult(new(PlatformKind.Twitch, "streamer"));
            await command;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
            Assert.Equal("Following", button.Content);
            Assert.True(button.MinWidth >= 82);
        }
        finally { window.DataContext = null; window.Close(); }
    });

    private static MainViewModel Model(IChannelFollowService service, AppSettings? settings = null,
        ISettingsService? save = null, IFollowedStreamsService? followed = null, IKickFollowedChannelsImporter? importer = null)
    {
        settings ??= new AppSettings();
        settings.Chat.ConnectAutomatically = false;
        return new MainViewModel(new MainViewModelDependencies
        {
            Settings = settings,
            SettingsService = save ?? new FakeSettingsService(settings),
            StreamlinkService = new FakeStreamlinkService(),
            PlaybackFactory = new FakePlaybackEngineFactory(),
            ChatFactory = new FakeChatClientFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            ChannelFollowService = service,
            FollowedStreamsService = followed,
            KickFollowedChannelsImporter = importer
        });
    }

    private static StreamTabViewModel AddTab(MainViewModel model, StreamTarget target)
    {
        var tab = TestViewModels.CreateTab(target, "best", new FakeStreamlinkService(), new FakePlaybackEngineFactory(),
            new FakeChatClientFactory(), new MemoryLogger(), action => action());
        model.Tabs.Add(tab);
        return tab;
    }

    private sealed class PendingFollowService : IChannelFollowService
    {
        internal TaskCompletionSource<ChannelFollowResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StreamTarget? Target { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal int Calls { get; private set; }
        public Task<ChannelFollowResult?> GetFollowStatusAsync(StreamTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(Completion.Task.IsCompletedSuccessfully && Completion.Task.Result is { } result &&
                result.Platform == target.Platform && result.Channel == target.Channel ? result : null);
        public Task<ChannelFollowResult?> UnfollowAsync(StreamTarget target, CancellationToken cancellationToken = default) =>
            FollowAsync(target, cancellationToken);
        public Task<ChannelFollowResult?> FollowAsync(StreamTarget target, CancellationToken cancellationToken = default)
        {
            Target = target; Token = cancellationToken; Calls++;
            return Completion.Task;
        }
    }

    private sealed class PendingImporter : IKickFollowedChannelsImporter
    {
        internal TaskCompletionSource<IReadOnlyList<string>?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<string>?> ImportAsync(CancellationToken cancellationToken = default) => Completion.Task;
    }
}
