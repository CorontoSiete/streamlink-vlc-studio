internal static class VodHistoryRegressionTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD history regression: a rewatch before loading keeps saved watched status", RewatchBeforeLoadAsync),
        ("VOD history regression: loading rejects an older pending playback sample", OlderSampleBeforeLoadAsync),
        ("VOD history regression: stationary samples reject stale progress after restart", StationarySampleAsync),
        ("VOD history regression: a pause during clock sampling cannot advance the bookmark", PauseDuringSampleAsync)
    ];

    private static async Task RewatchBeforeLoadAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var target = VodResumeTestCatalog.Target();
        // Legacy completion records have no separate sticky HasBeenWatched flag.
        await File.WriteAllTextAsync(files.Path,
            """[{"Platform":0,"MediaId":"12345","Bookmark":{"Position":"02:00:00","Duration":"02:00:00","UpdatedAtUtc":"2026-01-01T00:00:00Z","Completed":true}}]""");
        var history = files.Create();
        var rewatch = VodWatchProgressTestCatalog.Bookmark(120);
        history.Remember(target, rewatch);
        await history.SaveAsync();

        foreach (var loaded in new[] { history, files.Create() })
        {
            var bookmark = (await loaded.GetAsync(target))!;
            Assert.Equal(rewatch.Position, bookmark.Position);
            Assert.Equal(false, bookmark.Completed);
            Assert.True(bookmark.HasBeenWatched, "Loading saved completion must preserve the Watched badge during a rewatch.");
        }
    }

    private static async Task OlderSampleBeforeLoadAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var target = VodResumeTestCatalog.Target();
        var saved = VodWatchProgressTestCatalog.Bookmark(300);
        var original = files.Create();
        original.Remember(target, saved);
        await original.SaveAsync();

        var history = files.Create();
        history.Remember(target, saved with { Position = TimeSpan.FromSeconds(100), UpdatedAtUtc = saved.UpdatedAtUtc.AddSeconds(-1) });
        await history.SaveAsync();
        Assert.Equal(saved, await history.GetAsync(target));
        Assert.Equal(saved, await files.Create().GetAsync(target));
    }

    private static async Task StationarySampleAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var target = VodResumeTestCatalog.Target();
        var first = VodWatchProgressTestCatalog.Bookmark(300);
        var latest = first with { UpdatedAtUtc = first.UpdatedAtUtc.AddSeconds(2) };
        var delayed = first with { Position = TimeSpan.FromSeconds(100), UpdatedAtUtc = first.UpdatedAtUtc.AddSeconds(1) };
        var history = files.Create();
        history.Remember(target, first);
        await history.SaveAsync();
        history.Remember(target, latest);
        history.Remember(target, delayed);
        Assert.Equal(latest, await history.GetAsync(target));
        await history.SaveAsync();

        var reloaded = files.Create();
        Assert.Equal(latest, await reloaded.GetAsync(target));
        reloaded.Remember(target, delayed);
        Assert.Equal(latest, await reloaded.GetAsync(target));
    }

    private static async Task PauseDuringSampleAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = VodResumeTestCatalog.Tab(VodResumeTestCatalog.Target(), history, factory);
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(VodResumeTestCatalog.Settings(replay: false));
        await (Task)typeof(StreamTabViewModel)
            .GetMethod("StopReplayClockPollingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tab, null)!;
        var engine = factory.Engine!;
        engine.PlaybackClockOverride = _ => (true, new(TimeSpan.FromMinutes(30), tab.Target.MediaDuration, true));
        tab.CaptureVodResumePosition();
        var confirmed = await history.GetAsync(tab.Target);

        engine.PlaybackClockOverride = _ =>
        {
            // Model a UI pause being published while the worker is reading the native clock.
            typeof(StreamTabViewModel).GetProperty(nameof(StreamTabViewModel.Status))!.SetValue(tab, PlaybackStatus.Paused);
            return (true, new(TimeSpan.FromSeconds(1801), tab.Target.MediaDuration, true));
        };
        tab.CaptureVodResumePosition();
        engine.PlaybackClockOverride = _ => (false, new(TimeSpan.Zero, null, false));
        Assert.Equal(PlaybackStatus.Paused, tab.Status);
        Assert.Equal(confirmed, await history.GetAsync(tab.Target));
    }
}
