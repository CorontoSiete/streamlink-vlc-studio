using StreamlinkVlcStudio.App.Wpf.Follows;

internal static partial class ChannelFollowTestCatalog
{
    // Authenticated PlayerTrackingContextQuery and EmotePicker replies contain follower
    // without canFollow. FollowButton_User supplies that separate capability later.
    private static string PartialState(bool following = false) => State(following).Replace("\"canFollow\":true,", "", StringComparison.Ordinal);

    private static Task TwitchPartialFollowStateAsync()
    {
        var tracker = new ChannelFollowResponseTracker(Twitch);
        tracker.Observe("POST", Gql, 200, "{}", PartialState());
        Assert.True(tracker.HasTwitchFollowState);
        Assert.Equal<bool?>(false, tracker.IsFollowing);
        Assert.Equal<bool?>(null, tracker.TwitchCanFollow);
        Assert.Equal(false, tracker.Confirmed);

        tracker.Observe("POST", Gql, 200, "{}", State(false));
        Assert.Equal<bool?>(true, tracker.TwitchCanFollow);
        tracker.Observe("POST", Gql, 200, "{}", PartialState());
        Assert.Equal<bool?>(true, tracker.TwitchCanFollow);
        tracker.Observe("POST", Gql, 200, "{}", State(false).Replace("\"canFollow\":true", "\"canFollow\":false", StringComparison.Ordinal));
        Assert.Equal<bool?>(false, tracker.TwitchCanFollow);
        tracker.Observe("POST", Gql, 200, "{}", PartialState());
        Assert.Equal<bool?>(false, tracker.TwitchCanFollow);
        tracker.Reset();
        Assert.Equal<bool?>(null, tracker.TwitchCanFollow);

        foreach (var value in new[] { "null", "\"false\"", "0" })
        {
            var unknown = new ChannelFollowResponseTracker(Twitch);
            unknown.Observe("POST", Gql, 200, "{}", State(false).Replace("\"canFollow\":true", "\"canFollow\":" + value, StringComparison.Ordinal));
            Assert.Equal<bool?>(null, unknown.TwitchCanFollow);
        }
        var split = new ChannelFollowResponseTracker(Twitch);
        split.Observe("POST", Gql, 200, "{}", State(false).Replace(",\"follower\":null", "", StringComparison.Ordinal));
        Assert.Equal(false, split.HasTwitchFollowState);
        split.Observe("POST", Gql, 200, "{}", PartialState());
        Assert.Equal<bool?>(true, split.TwitchCanFollow);
        Assert.True(split.HasTwitchFollowState);

        var readOnly = new ChannelFollowResponseTracker(Twitch, null);
        readOnly.Observe("POST", Gql, 200, "{}", PartialState(true));
        Assert.True(readOnly.Confirmed);
        Assert.Equal<bool?>(true, readOnly.IsFollowing);
        return Task.CompletedTask;
    }

    private static Task BrowserPartialFollowStateAsync() => WithFixtureAsync(PlatformKind.Twitch, async fixture =>
    {
        fixture.PartialFollowStateFirst = true;
        fixture.DeferMutation = true;
        var operation = fixture.Service().FollowAsync(fixture.Target);
        await TestWait.UntilAsync(() => fixture.FollowStateReads >= 1 || operation.IsCompleted, TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.Equal(false, operation.IsCompleted);
        Assert.Equal(0, fixture.Mutations);
        await TestWait.UntilAsync(() => fixture.Mutations == 1 || operation.IsCompleted, TimeSpan.FromSeconds(10));
        Assert.Equal(false, operation.IsCompleted);
        Assert.Equal(1, fixture.Mutations);
        // A subsequent partial reply must not erase the established permission while
        // the real mutation's response is still outstanding.
        await fixture.Core!.ExecuteScriptAsync($"fetch({JsonSerializer.Serialize(Gql)}, {{method:'POST',body:JSON.stringify({{operationName:'PlayerTrackingContextQuery'}})}})");
        await TestWait.UntilAsync(() => fixture.FollowStateReads >= 3, TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        Assert.Equal(false, operation.IsCompleted);
        fixture.ReleaseMutation();
        var result = await operation;
        Assert.NotNull(result);
        Assert.True(result!.IsFollowing);
        Assert.Equal(1, fixture.AcceptedMutations);
        Assert.Equal(1, fixture.Mutations);
    });

    private static Task BrowserFollowRestrictionAsync() => WithFixtureAsync(PlatformKind.Twitch, async fixture =>
    {
        fixture.RestrictFollow = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service().FollowAsync(fixture.Target));
        Assert.Contains("does not allow", error.Message);
        Assert.Equal(0, fixture.Mutations);
        Assert.Equal(false, fixture.Following);
    });

    private static Task BrowserIgnoredFollowClickAsync() => WithFixtureAsync(PlatformKind.Twitch, async fixture =>
    {
        fixture.IgnoreFirstFollowClick = true;
        fixture.DeferMutation = true;
        var operation = fixture.Service().FollowAsync(fixture.Target);
        await TestWait.UntilAsync(() => fixture.Mutations == 1 || operation.IsCompleted, TimeSpan.FromSeconds(15));
        Assert.Equal(false, operation.IsCompleted);
        Assert.Equal(1, fixture.Mutations);
        Assert.Equal("2", await fixture.Core!.ExecuteScriptAsync("window.followClicks"));
        // The native request has arrived, but its response remains deferred. No further
        // scripted click may be attempted while it is pending or after it completes.
        await Task.Delay(1500);
        Assert.Equal("2", await fixture.Core.ExecuteScriptAsync("window.followClicks"));
        Assert.Equal(false, operation.IsCompleted);
        fixture.ReleaseMutation();
        var result = await operation;
        Assert.NotNull(result);
        Assert.True(result!.IsFollowing);
        Assert.Equal(1, fixture.Mutations);
        Assert.Equal(1, fixture.AcceptedMutations);
    });
}
