internal static partial class ChannelFollowTestCatalog
{
    private static Task StatusOpenWorkflowAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var target in new[] { Twitch, Kick })
        {
            var service = new StatefulFollowService();
            service.States[target.StateKey] = true;
            await using var model = Model(service);
            await model.OpenStreamAsync(target, selectOpenedTab: false);
            var tab = model.Tabs.Single();
            Assert.Equal<StreamTabViewModel?>(null, model.SelectedTab);
            Assert.Equal(1, service.Reads);
            await model.OpenStreamAsync(target);
            Assert.Equal(tab, model.SelectedTab);
            Assert.Equal("Following", model.FollowButtonText);
            model.SelectedTab = null;
            await model.OpenStreamAsync(target);
            Assert.Equal(1, model.Tabs.Count);
            Assert.Equal(1, service.Reads);
            Assert.True(model.CloseTab(tab));
            service.States[target.StateKey] = false;
            await model.OpenStreamAsync(target);
            Assert.Equal(2, service.Reads);
            Assert.Equal("Follow", model.FollowButtonText);
            Assert.Equal(0, service.Writes);
        }
    });

    private static async Task StatusAttemptsOnceAsync()
    {
        foreach (var target in new[] { Twitch, Kick })
            foreach (var outcome in new[] { "following", "not-following", "unverified", "error", "canceled", "wrong-channel", "wrong-platform" })
            {
                var service = new StatefulFollowService
                {
                    Read = (_, _) => outcome switch
                    {
                        "unverified" => Task.FromResult<ChannelFollowResult?>(null),
                        "error" => Task.FromException<ChannelFollowResult?>(new InvalidOperationException("Status lookup failed")),
                        "canceled" => Task.FromCanceled<ChannelFollowResult?>(new CancellationToken(true)),
                        "wrong-channel" => Task.FromResult<ChannelFollowResult?>(new(target.Platform, "other")),
                        "wrong-platform" => Task.FromResult<ChannelFollowResult?>(new(
                            target.Platform == PlatformKind.Twitch ? PlatformKind.Kick : PlatformKind.Twitch, target.Channel)),
                        _ => Task.FromResult<ChannelFollowResult?>(new(target.Platform, target.Channel, IsFollowing: outcome == "following"))
                    }
                };
                await using var model = Model(service);
                var tab = AddTab(model, target);
                Assert.Equal(1, service.Reads); // Opening a background tab also checks once.
                for (var selection = 0; selection < 4; selection++)
                {
                    model.SelectedTab = tab;
                    Assert.Equal(outcome == "following", model.IsSelectedChannelFollowed);
                    Assert.Equal(outcome == "following" ? "Following" : "Follow", model.FollowButtonText);
                    Assert.True(model.FollowSelectedChannelCommand.CanExecute(null));
                    model.SelectedTab = null;
                }
                Assert.Equal(1, service.Reads);
                Assert.Equal(0, service.Writes);
            }
    }

    private static Task StatusCloseAndReopenAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var closeSelected in new[] { false, true })
        {
            var service = PendingStatusService(out var reads);
            await using var model = Model(service);
            var first = AddTab(model, Twitch);
            var other = AddTab(model, Kick);
            model.SelectedTab = closeSelected ? first : other;
            Assert.True(model.CloseTab(first));
            Assert.True(reads[0].Token.IsCancellationRequested);
            Assert.Equal(false, reads[1].Token.IsCancellationRequested);
            var reopened = AddTab(model, Twitch);
            model.SelectedTab = reopened;
            Assert.Equal(3, service.Reads);
            Assert.Equal("Checking\u2026", model.FollowButtonText);
            // Reordering tabs does not create another attempt or cancel an existing one.
            model.Tabs.Move(0, 1);
            Assert.Equal(3, service.Reads);
            Assert.Equal(false, reads[2].Token.IsCancellationRequested);
            reads[2].Completion.SetResult(new(PlatformKind.Twitch, "streamer", IsFollowing: false));
            reads[1].Completion.SetResult(new(PlatformKind.Kick, "streamer"));
            await TestWait.UntilAsync(() => model.FollowSelectedChannelCommand.CanExecute(null), TimeSpan.FromSeconds(5));
            // The old request deliberately ignores cancellation and returns after its replacement.
            reads[0].Completion.SetResult(new(PlatformKind.Twitch, "streamer"));
            await WaitForFollowStatusOperationsAsync(model);
            Assert.Equal("Follow", model.FollowButtonText);
            model.SelectedTab = other;
            Assert.Equal("Following", model.FollowButtonText);
            model.SelectedTab = reopened;
            Assert.Equal("Follow", model.FollowButtonText);
            Assert.Equal(3, service.Reads);
        }
    });

    private static Task StatusCleanupAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        foreach (var shutdown in new[] { false, true })
        {
            var service = PendingStatusService(out var reads);
            await using var model = Model(service);
            var first = AddTab(model, Twitch);
            var other = AddTab(model, Kick);
            model.SelectedTab = first;
            Task disposal = Task.CompletedTask;
            if (shutdown) disposal = model.DisposeAsync().AsTask();
            else model.Tabs.Clear();
            Assert.True(reads[0].Token.IsCancellationRequested);
            Assert.True(reads[1].Token.IsCancellationRequested);
            reads[0].Completion.SetResult(new(PlatformKind.Twitch, "streamer"));
            reads[1].Completion.SetResult(new(PlatformKind.Kick, "streamer"));
            await disposal;
            await WaitForFollowStatusOperationsAsync(model);
            Assert.Equal(false, model.IsSelectedChannelFollowed);
            if (!shutdown)
            {
                // A collection reset must discard completed-attempt tracking as well.
                service.Read = null;
                model.Tabs.Add(first);
                model.SelectedTab = first;
                Assert.Equal(3, service.Reads);
                Assert.Equal("Follow", model.FollowButtonText);
                await other.DisposeAsync(); // Clear removes it from the model's ownership.
            }
        }
    });

    private static StatefulFollowService PendingStatusService(out List<PendingStatusRead> reads)
    {
        var pending = new List<PendingStatusRead>();
        reads = pending;
        return new StatefulFollowService
        {
            Read = (_, token) =>
            {
                var read = new PendingStatusRead(token);
                pending.Add(read);
                return read.Completion.Task;
            }
        };
    }

    private static Task WaitForFollowStatusOperationsAsync(MainViewModel model) =>
        ((BackgroundOperationController)typeof(MainViewModel)
            .GetField("backgroundOperationController", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!)
        .WaitForCurrentAsync().WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class PendingStatusRead(CancellationToken token)
    {
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<ChannelFollowResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
