using StreamlinkVlcStudio.Infrastructure.LocalWeb;

internal static partial class LocalWebTestCatalog
{
    private static async Task VerifyFollowingLastLiveAsync(LocalWebDependencies dependencies)
    {
        var confirmed = DateTimeOffset.Parse("2026-10-22T19:04:22Z", CultureInfo.InvariantCulture);
        var following = new LastLiveFollowingService
        {
            Result = new([], [], OfflineChannels:
            [
                new(PlatformKind.Twitch, "creator", "Creator", "https://twitch.tv/creator", LastLiveAtUtc: confirmed.AddDays(-1)),
                new(PlatformKind.Kick, "creator", "Creator", "https://kick.com/creator"),
                new(PlatformKind.Kick, "unknown", "Unknown", "https://kick.com/unknown")
            ]),
            Enrich = (channels, _) => Task.FromResult<IReadOnlyList<FollowedChannel>>(channels.Select(channel => channel with
            {
                LastLiveAtUtc = channel.Channel == "unknown" ? null : confirmed
            }).ToArray())
        };
        var services = dependencies with { Following = following };
        await using var workspace = new LocalWebWorkspace(services);
        var snapshot = await Call(workspace, "following", new { });
        Assert.Equal(0, following.HistoryCalls);
        Assert.Equal(confirmed.AddDays(-1), snapshot.GetProperty("offline")[0].GetProperty("lastLiveAtUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("offline")[1].GetProperty("lastLiveAtUtc").ValueKind);
        var history = await Call(workspace, "following/history", new { channel = "unlisted", lastLiveAtUtc = confirmed.AddYears(1) });
        Assert.Equal(3, history.GetProperty("items").GetArrayLength());
        Assert.SequenceEqual(new[] { "Twitch", "Kick", "Kick" }, history.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("platform").GetString()!));
        Assert.Equal(confirmed, history.GetProperty("items")[0].GetProperty("lastLiveAtUtc").GetDateTimeOffset());
        Assert.Equal(confirmed, history.GetProperty("items")[1].GetProperty("lastLiveAtUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("items")[2].GetProperty("lastLiveAtUtc").ValueKind);
        Assert.DoesNotContain("secret-", history.ToString());
        Assert.DoesNotContain("unlisted", history.ToString());
        Assert.Equal(1, following.HistoryCalls);

        // A device can enrich only its own previously loaded snapshot.
        await using var otherDevice = new LocalWebWorkspace(services);
        Assert.Equal(0, (await Call(otherDevice, "following/history", new { })).GetProperty("items").GetArrayLength());

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        following.Enrich = async (channels, token) => { started.TrySetResult(); await release.Task.WaitAsync(token); return channels; };
        using var cancellation = new CancellationTokenSource();
        var active = workspace.InvokeAsync("following/history", JsonSerializer.SerializeToElement(new { }), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var historyCalls = following.HistoryCalls;
        var queued = workspace.InvokeAsync("following/history", JsonSerializer.SerializeToElement(new { }), cancellation.Token);
        Assert.Equal(historyCalls, following.HistoryCalls);
        await Call(workspace, "settings", new { }).WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await active);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await queued);
        Assert.Equal(historyCalls, following.HistoryCalls);

        // Also exercise authentication, CSRF and the real HTTPS command gate while history waits.
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var certificate = Certificate();
        await using var server = await Server(certificate, () => new LocalWebWorkspace(services));
        using var http = Client(certificate, server.Address);
        using (var denied = await Post(http, "following/history", new { })) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var auth = await Login(http);
        using (var denied = await Post(http, "following/history", new { }, auth.Cookie)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await Post(http, "following/history", new { }, auth.Cookie, auth.Csrf, "https://attacker.example")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var loaded = await Post(http, "following", new { }, auth.Cookie, auth.Csrf)) Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
        var pending = Post(http, "following/history", new { }, auth.Cookie, auth.Csrf);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var command = await Post(http, "settings", new { }, auth.Cookie, auth.Csrf).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(HttpStatusCode.OK, command.StatusCode);
        }
        finally { release.TrySetResult(); }
        using (var completed = await pending) Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        // Refresh membership before enriching again; no previous offline channel leaks back in.
        following.Result = new([], [], OfflineChannels: []);
        await Call(workspace, "following", new { });
        Assert.Equal(0, (await Call(workspace, "following/history", new { })).GetProperty("items").GetArrayLength());
    }

    private sealed class LastLiveFollowingService : IFollowedStreamsService
    {
        internal required FollowedLiveStreamsResult Result { get; set; }
        internal required Func<IReadOnlyList<FollowedChannel>, CancellationToken, Task<IReadOnlyList<FollowedChannel>>> Enrich { get; set; }
        internal int HistoryCalls { get; private set; }
        public Task<FollowedLiveStreamsResult> GetLiveFollowedStreamsAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.FromResult(Result);
        public Task<IReadOnlyList<FollowedChannel>> EnrichOfflineChannelsAsync(IReadOnlyList<FollowedChannel> channels, CancellationToken cancellationToken = default)
        {
            HistoryCalls++;
            return Enrich(channels, cancellationToken);
        }
    }
}
