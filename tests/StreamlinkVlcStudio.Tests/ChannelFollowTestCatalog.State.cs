using StreamlinkVlcStudio.App.Wpf.Follows;

internal static partial class ChannelFollowTestCatalog
{
    private static string UnfollowRequest(string id = "123") => MutationRequest(id).Replace("FollowButton_FollowUser", "FollowButton_UnfollowUser");
    private static string UnfollowResponse(string login = "streamer", string id = "123") =>
        "{\"data\":{\"unfollowUser\":{\"follow\":{\"user\":" + User(false, login, id) + "}}}}";

    private static Task ReadOnlyContractsAsync()
    {
        foreach (var following in new[] { false, true })
        {
            var tracker = new ChannelFollowResponseTracker(Twitch, null);
            tracker.Observe("POST", Gql, 200, "{}", State(following));
            Assert.True(tracker.Confirmed);
            Assert.Equal<bool?>(following, tracker.IsFollowing);
            tracker.Arm();
            Assert.True(tracker.BlocksUnexpectedFollow("POST", Gql, MutationRequest()));
        }
        var malformed = new ChannelFollowResponseTracker(Twitch, null);
        malformed.Observe("POST", Gql, 200, "{}", State(true).Replace("2026-10-08T12:00:00Z", "not-a-date"));
        Assert.Equal(false, malformed.Confirmed);
        Assert.Equal<bool?>(null, malformed.IsFollowing);
        return Task.CompletedTask;
    }

    private static Task UnfollowContractsAsync()
    {
        ChannelFollowResponseTracker Following()
        {
            var tracker = new ChannelFollowResponseTracker(Twitch, false);
            tracker.Observe("POST", Gql, 200, "{}", State(true));
            Assert.Equal(false, tracker.Confirmed);
            tracker.Arm();
            return tracker;
        }
        var valid = Following();
        Assert.Equal(false, valid.BlocksUnexpectedFollow("POST", Gql, UnfollowRequest()));
        valid.Observe("POST", Gql, 200, "{}", State(false));
        Assert.Equal(false, valid.Confirmed); // A concurrent read cannot confirm an outstanding mutation.
        valid.Observe("POST", Gql, 200, UnfollowRequest(), UnfollowResponse());
        Assert.True(valid.Confirmed);
        Assert.Equal<bool?>(false, valid.IsFollowing);
        Assert.True(valid.BlocksUnexpectedFollow("POST", Gql, UnfollowRequest()));
        foreach (var body in new[] { MutationRequest(), UnfollowRequest("456"), "[" + UnfollowRequest() + "," + UnfollowRequest() + "]" })
            Assert.True(Following().BlocksUnexpectedFollow("POST", Gql, body));
        foreach (var response in new[] { UnfollowResponse("other"), UnfollowResponse(id: "456"), MutationResponse(), "{\"data\":{\"unfollowUser\":null}}", "{}", "{\"errors\":[{\"message\":\"private\"}]}" })
        {
            var tracker = Following();
            tracker.BlocksUnexpectedFollow("POST", Gql, UnfollowRequest());
            tracker.Observe("POST", Gql, 200, UnfollowRequest(), response);
            Assert.Equal(false, tracker.Confirmed);
        }
        var kick = new ChannelFollowResponseTracker(Kick, false);
        kick.Arm();
        Assert.Equal(false, kick.BlocksUnexpectedFollow("DELETE", "https://kick.com/api/v2/channels/streamer/follow", ""));
        Assert.True(kick.BlocksUnexpectedFollow("POST", "https://kick.com/api/v2/channels/streamer/follow", ""));
        return Task.CompletedTask;
    }

    private static async Task StatusAndToggleAsync()
    {
        var service = new StatefulFollowService();
        service.States[Twitch.StateKey] = true;
        await using var model = Model(service);
        var twitch = AddTab(model, Twitch);
        var kick = AddTab(model, Kick);
        Assert.Equal(2, service.Reads);
        model.SelectedTab = twitch;
        Assert.True(model.IsSelectedChannelFollowed);
        Assert.Equal("Following", model.FollowButtonText);
        Assert.Contains("Unfollow", model.FollowButtonToolTip);
        Assert.Equal("Unfollow selected streamer", model.FollowButtonAccessibleName);
        Assert.Equal(0, service.Writes);
        await model.FollowSelectedChannelCommand.ExecuteAsync();
        Assert.Equal(false, service.LastIntent);
        Assert.Equal("Follow", model.FollowButtonText);
        await model.FollowSelectedChannelCommand.ExecuteAsync();
        Assert.Equal(true, service.LastIntent);
        Assert.True(model.IsSelectedChannelFollowed);
        model.SelectedTab = kick;
        Assert.Equal("Follow", model.FollowButtonText);
        model.SelectedTab = twitch;
        Assert.True(model.IsSelectedChannelFollowed);
        // Tab selection reuses the state confirmed for this opened stream.
        service.States[Twitch.StateKey] = false;
        model.SelectedTab = kick;
        model.SelectedTab = twitch;
        Assert.True(model.IsSelectedChannelFollowed);
        Assert.Equal(2, service.Reads);
        Assert.True(model.CloseTab(twitch));
        model.SelectedTab = AddTab(model, Twitch);
        Assert.Equal(false, model.IsSelectedChannelFollowed);
        Assert.Equal(3, service.Reads);
    }

    private static async Task FailedUnfollowAsync()
    {
        foreach (var failure in new[] { "cancel", "error", "wrong-state", "wrong-channel" })
        {
            var service = new StatefulFollowService();
            service.States[Twitch.StateKey] = true;
            service.Change = (_, _) => failure switch
            {
                "cancel" => Task.FromResult<ChannelFollowResult?>(null),
                "error" => Task.FromException<ChannelFollowResult?>(new InvalidOperationException("Unfollow failed")),
                "wrong-state" => Task.FromResult<ChannelFollowResult?>(new(PlatformKind.Twitch, "streamer")),
                _ => Task.FromResult<ChannelFollowResult?>(new(PlatformKind.Twitch, "other", IsFollowing: false))
            };
            await using var model = Model(service);
            model.SelectedTab = AddTab(model, Twitch);
            await model.FollowSelectedChannelCommand.ExecuteAsync();
            Assert.True(model.IsSelectedChannelFollowed);
            Assert.Equal("Following", model.FollowButtonText);
            Assert.True(model.FollowSelectedChannelCommand.CanExecute(null));
        }
    }

    private static Task StatusRacesAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new StatefulFollowService();
        var reads = new List<(StreamTarget Target, CancellationToken Token, TaskCompletionSource<ChannelFollowResult?> Completion)>();
        service.Read = (target, token) =>
        {
            var completion = new TaskCompletionSource<ChannelFollowResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            reads.Add((target, token, completion));
            return completion.Task;
        };
        await using var model = Model(service);
        var first = AddTab(model, Twitch);
        var other = AddTab(model, Kick);
        model.SelectedTab = first;
        Assert.Equal("Checking\u2026", model.FollowButtonText);
        Assert.Equal(false, model.FollowSelectedChannelCommand.CanExecute(null));
        model.SelectedTab = other;
        Assert.Equal(false, reads[0].Token.IsCancellationRequested);
        model.SelectedTab = first;
        model.SelectedTab = null;
        model.SelectedTab = other;
        Assert.Equal(2, service.Reads);
        Assert.Equal("Checking\u2026", model.FollowButtonText);
        reads[1].Completion.SetResult(new(PlatformKind.Kick, "streamer", [], false));
        reads[0].Completion.SetResult(new(PlatformKind.Twitch, "streamer"));
        await WaitForFollowStatusOperationsAsync(model);
        Assert.Equal(false, model.IsSelectedChannelFollowed);
        model.SelectedTab = first;
        Assert.True(model.IsSelectedChannelFollowed);
        Assert.Equal("Following", model.FollowButtonText);
        Assert.Equal(2, service.Reads);

        var vod = AddTab(model, Twitch with { Kind = StreamTargetKind.TwitchVod, MediaId = "123", BroadcasterId = "123" });
        AddTab(model, StreamInputParser.FromChannel(PlatformKind.Kick, "another"));
        var change = new TaskCompletionSource<ChannelFollowResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Change = (_, _) => change.Task;
        var operation = model.FollowSelectedChannelCommand.ExecuteAsync();
        Assert.Equal("Unfollowing\u2026", model.FollowButtonText);
        Assert.True(reads[2].Token.IsCancellationRequested);
        Assert.Equal(false, reads[3].Token.IsCancellationRequested);
        var openedDuringChange = AddTab(model, vod.Target with { MediaId = "456" });
        model.SelectedTab = openedDuringChange;
        Assert.Equal("Unfollowing\u2026", model.FollowButtonText);
        Assert.Equal(4, service.Reads);
        model.SelectedTab = other;
        change.SetResult(new(PlatformKind.Twitch, "streamer", IsFollowing: false));
        await operation;
        // A read from another tab for this broadcaster cannot undo the confirmed unfollow.
        reads[2].Completion.SetResult(new(PlatformKind.Twitch, "streamer"));
        reads[3].Completion.SetResult(new(PlatformKind.Kick, "another", IsFollowing: false));
        await WaitForFollowStatusOperationsAsync(model);
        Assert.Equal(Twitch, service.LastTarget);
        Assert.Equal(false, model.IsSelectedChannelFollowed);
        model.SelectedTab = vod;
        Assert.Equal("Follow", model.FollowButtonText);
        model.SelectedTab = first;
        Assert.Equal("Follow", model.FollowButtonText);
        model.SelectedTab = openedDuringChange;
        Assert.Equal("Follow", model.FollowButtonText);
        Assert.Equal(4, service.Reads);
        service.Read = null;
    });

    private static async Task KickUnfollowSnapshotAsync()
    {
        var settings = new AppSettings();
        settings.FollowedChannels.KickChannelSlugs = ["manual", "streamer"];
        settings.FollowedChannels.KickImportedChannelSlugs = ["streamer", "offline"];
        var save = new FakeSettingsService(settings);
        var service = new StatefulFollowService();
        service.States[Kick.StateKey] = true;
        await using var model = Model(service, settings, save);
        model.SelectedTab = AddTab(model, Kick);
        await model.FollowSelectedChannelCommand.ExecuteAsync();
        Assert.Equal(false, model.IsSelectedChannelFollowed);
        Assert.SequenceEqual(new[] { "offline" }, settings.FollowedChannels.KickImportedChannelSlugs);
        Assert.SequenceEqual(new[] { "manual", "streamer" }, settings.FollowedChannels.KickChannelSlugs);
        Assert.True(save.SaveCount > 0);
    }

    private static async Task BrowserStatusAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            foreach (var following in new[] { false, true })
                await WithFixtureAsync(platform, async fixture =>
                {
                    fixture.Following = following;
                    var result = await fixture.Service().GetFollowStatusAsync(fixture.Target);
                    Assert.NotNull(result);
                    Assert.Equal(following, result!.IsFollowing);
                    Assert.Equal(0, fixture.Mutations);
                    Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
                });
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            await WithFixtureAsync(platform, async fixture =>
            {
                fixture.HasSession = false;
                var prompts = 0;
                var result = await fixture.Service((_, _) => { prompts++; return Task.FromResult(true); }).GetFollowStatusAsync(fixture.Target);
                Assert.Equal<ChannelFollowResult?>(null, result);
                Assert.Equal(0, prompts);
                Assert.Equal(0, fixture.Mutations);
            });
    }

    private static async Task BrowserUnfollowAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            foreach (var following in new[] { false, true })
                await WithFixtureAsync(platform, async fixture =>
                {
                    fixture.Following = following;
                    var result = await fixture.Service().UnfollowAsync(fixture.Target);
                    Assert.NotNull(result);
                    Assert.Equal(false, result!.IsFollowing);
                    Assert.Equal(following ? 1 : 0, fixture.AcceptedMutations);
                    Assert.Equal(false, fixture.Following);
                    var status = await fixture.Service().GetFollowStatusAsync(fixture.Target);
                    Assert.Equal(false, status!.IsFollowing);
                    Assert.Equal(false, NativeWindowTest.IsWindow(fixture.Host));
                });
    }

    private static async Task BrowserUnfollowNamedControlAsync()
    {
        // Reproduced on deansocool: the main control names the broadcaster, but the
        // About control in the same channel-info-content region says only "Unfollow ".
        foreach (var delayed in new[] { false, true })
            await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
            {
                fixture.Following = true;
                fixture.UnnamedUnfollowButton = true;
                fixture.DelayNamedUnfollowButton = delayed;
                var result = await fixture.Service().UnfollowAsync(fixture.Target);
                Assert.NotNull(result);
                Assert.Equal(false, result!.IsFollowing);
                Assert.Equal(1, fixture.AcceptedMutations);
                Assert.Equal(false, fixture.Following);
                var status = await fixture.Service().GetFollowStatusAsync(fixture.Target);
                Assert.Equal(false, status!.IsFollowing);
            });
    }

    private static Task BrowserUnfollowOpeningAsync() => WithFixtureAsync(PlatformKind.Twitch, async fixture =>
    {
        fixture.Following = true;
        fixture.IgnoreFirstUnfollowOpen = true;
        fixture.DeferMutation = true;
        var operation = fixture.Service().UnfollowAsync(fixture.Target);
        try
        {
            await TestWait.UntilAsync(() => fixture.Mutations == 1 || operation.IsCompleted, TimeSpan.FromSeconds(15));
            if (operation.IsCompleted) await operation;
            Assert.Equal("2", await fixture.Core!.ExecuteScriptAsync("window.unfollowOpens"));
            await Task.Delay(1250);
            Assert.Equal(false, operation.IsCompleted);
            Assert.Equal(1, fixture.Mutations);
        }
        finally { fixture.ReleaseMutation(); }
        var result = await operation;
        Assert.Equal(false, result!.IsFollowing);
        Assert.Equal(1, fixture.AcceptedMutations);
        Assert.Equal(false, (await fixture.Service().GetFollowStatusAsync(fixture.Target))!.IsFollowing);
    });

    private static async Task BrowserUnfollowControlSafetyAsync()
    {
        foreach (var duplicate in new[] { false, true })
            await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
            {
                fixture.Following = true;
                fixture.DuplicateButtons = duplicate;
                fixture.WrongUnfollowDialogName = !duplicate;
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().UnfollowAsync(fixture.Target));
                Assert.Contains(duplicate ? "ambiguous" : "identify the selected broadcaster", error.Message);
                Assert.Equal(0, fixture.Mutations);
                Assert.True(fixture.Following);
            });
    }

    private static async Task BrowserUnfollowFailureAsync()
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
            await WithFixtureAsync(platform, async fixture =>
            {
                fixture.Following = true;
                fixture.Status = 403;
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().UnfollowAsync(fixture.Target));
                Assert.True(fixture.Following);
                Assert.Equal(0, fixture.AcceptedMutations);
            });
        await WithFixtureAsync(PlatformKind.Kick, async fixture =>
        {
            fixture.Following = true;
            fixture.CommitMutation = false;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().UnfollowAsync(fixture.Target));
            Assert.Contains("still in your followed list", error.Message);
        });
        await WithFixtureAsync(PlatformKind.Twitch, async fixture =>
        {
            fixture.Following = true;
            fixture.WrongMutationTarget = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().UnfollowAsync(fixture.Target));
            Assert.True(fixture.Following);
            Assert.Equal(0, fixture.AcceptedMutations);
        });
    }

    private sealed class StatefulFollowService : IChannelFollowService
    {
        internal Dictionary<string, bool> States { get; } = new();
        internal Func<StreamTarget, CancellationToken, Task<ChannelFollowResult?>>? Read { get; set; }
        internal Func<StreamTarget, bool, Task<ChannelFollowResult?>>? Change { get; set; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal bool LastIntent { get; private set; }
        internal StreamTarget? LastTarget { get; private set; }

        public Task<ChannelFollowResult?> GetFollowStatusAsync(StreamTarget target, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Read?.Invoke(target, cancellationToken) ?? Task.FromResult<ChannelFollowResult?>(new(target.Platform, target.Channel,
                IsFollowing: States.GetValueOrDefault(target.StateKey)));
        }

        public Task<ChannelFollowResult?> FollowAsync(StreamTarget target, CancellationToken cancellationToken = default) => SetAsync(target, true);
        public Task<ChannelFollowResult?> UnfollowAsync(StreamTarget target, CancellationToken cancellationToken = default) => SetAsync(target, false);

        private Task<ChannelFollowResult?> SetAsync(StreamTarget target, bool following)
        {
            Writes++;
            LastIntent = following;
            LastTarget = target;
            if (Change is not null) return Change(target, following);
            States[target.StateKey] = following;
            return Task.FromResult<ChannelFollowResult?>(new(target.Platform, target.Channel,
                target.Platform == PlatformKind.Kick ? following ? ["offline", target.Channel] : ["offline"] : null, following));
        }
    }
}
