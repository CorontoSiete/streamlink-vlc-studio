using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task VerifyFollowStatusAsync(LocalWebDependencies dependencies)
    {
        foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
        {
            var target = StreamInputParser.FromChannel(platform, "creator");
            foreach (var outcome in new[] { "following", "not-following", "unverified", "wrong-channel", "wrong-platform", "error" })
            {
                var reads = 0;
                var writes = 0;
                var services = dependencies with
                {
                    GetFollowStatusAsync = (selected, _) =>
                    {
                        reads++;
                        Assert.Equal(target.Platform, selected.Platform);
                        Assert.Equal(target.Channel, selected.Channel);
                        return outcome switch
                        {
                            "unverified" => Task.FromResult<ChannelFollowResult?>(null),
                            "wrong-channel" => Task.FromResult<ChannelFollowResult?>(new(platform, "other")),
                            "wrong-platform" => Task.FromResult<ChannelFollowResult?>(new(platform == PlatformKind.Twitch ? PlatformKind.Kick : PlatformKind.Twitch, target.Channel)),
                            "error" => Task.FromException<ChannelFollowResult?>(new InvalidOperationException("Provider failed")),
                            _ => Task.FromResult<ChannelFollowResult?>(new(platform, "CREATOR", IsFollowing: outcome == "following"))
                        };
                    },
                    FollowAsync = (_, _, _) => { writes++; throw new InvalidOperationException("Status must not write."); }
                };
                await using var workspace = new LocalWebWorkspace(services);
                await using var otherDevice = new LocalWebWorkspace(services);
                var player = (await Call(workspace, "open", new { input = target.Url })).GetProperty("id").GetString();
                var status = await Call(workspace, "follow/status", new { player });
                Assert.Equal(false, status.GetProperty("pending").GetBoolean());
                if (outcome is "following" or "not-following") Assert.Equal(outcome == "following", status.GetProperty("following").GetBoolean());
                else
                {
                    Assert.Equal(JsonValueKind.Null, status.GetProperty("following").ValueKind);
                    Assert.True(status.GetProperty("message").GetString()!.Length > 0);
                }
                await Call(workspace, "follow/status", new { player });
                Assert.Equal(1, reads);
                Assert.Equal(0, writes);
                await Assert.ThrowsAsync<ArgumentException>(() => Call(otherDevice, "follow/status", new { player }));
                await Call(workspace, "close", new { player });
                await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "follow/status", new { player }));
            }
        }

        var pending = new TaskCompletionSource<ChannelFollowResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        var writeCount = 0;
        CancellationToken readToken = default;
        var delayedServices = dependencies with
        {
            GetFollowStatusAsync = (_, token) => { readCount++; readToken = token; return pending.Task; },
            FollowAsync = (target, desired, _) =>
            {
                writeCount++;
                return Task.FromResult<ChannelFollowResult?>(new(target.Platform, target.Channel, IsFollowing: desired));
            }
        };
        await using var delayed = new LocalWebWorkspace(delayedServices);
        var opened = await Call(delayed, "open", new { input = "https://twitch.tv/creator" });
        var id = opened.GetProperty("id").GetString();
        var first = await Call(delayed, "follow/status", new { player = id });
        Assert.True(first.GetProperty("pending").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("following").ValueKind);
        await Call(delayed, "follow/status", new { player = id });
        Assert.Equal(1, readCount);
        await Call(delayed, "settings", new { }); // A pending website response does not block another command.
        var sibling = await Call(delayed, "open", new { input = "https://twitch.tv/creator" });
        var changed = await Call(delayed, "unfollow", new { player = id });
        Assert.Equal(false, changed.GetProperty("following").GetBoolean());
        pending.SetResult(new(PlatformKind.Twitch, "creator", IsFollowing: true));
        var confirmed = await Call(delayed, "follow/status", new { player = id });
        Assert.Equal(false, confirmed.GetProperty("pending").GetBoolean());
        Assert.Equal(false, confirmed.GetProperty("following").GetBoolean());
        var siblingStatus = await Call(delayed, "follow/status", new { player = sibling.GetProperty("id").GetString() });
        Assert.Equal(false, siblingStatus.GetProperty("following").GetBoolean());
        Assert.Equal(1, readCount);
        Assert.Equal(1, writeCount);
        await Call(delayed, "close", new { player = id });
        Assert.True(readToken.IsCancellationRequested);

        // Closing an unresolved read cancels its provider token; reopening checks again.
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reopened = await Call(delayed, "open", new { input = "https://twitch.tv/creator" });
        var reopenedId = reopened.GetProperty("id").GetString();
        await Call(delayed, "follow/status", new { player = reopenedId });
        Assert.Equal(2, readCount);
        await Call(delayed, "close", new { player = reopenedId });
        Assert.True(readToken.IsCancellationRequested);
        pending.SetResult(null);
        var vod = await Call(delayed, "open", new { input = "https://www.twitch.tv/videos/12345" });
        await Assert.ThrowsAsync<ArgumentException>(() => Call(delayed, "follow/status", new { player = vod.GetProperty("id").GetString() }));
        Assert.Equal(2, readCount);
    }
}
