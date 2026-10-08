internal static partial class CodeCleanupTestCatalog
{
    private static async Task VodResumeDelayedRateSampleAsync(bool slowBeforeSample, bool impossibleJump = false)
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var target = VodResumeTestCatalog.Target();
        var history = files.Create();
        var initialPosition = TimeSpan.FromMinutes(30);
        var observedPosition = initialPosition + TimeSpan.FromSeconds(impossibleJump ? 30 : 20);
        var expectedPosition = impossibleJump ? initialPosition : observedPosition;
        var factory = new FakePlaybackEngineFactory();
        await using (var tab = VodResumeTestCatalog.Tab(target, history, factory))
        {
            await StartCleanupVodTabAsync(tab);
            await SetCleanupVodPlaybackRateAsync(tab, 6);
            Assert.Equal(2f, factory.Engine!.PlaybackRate);
            factory.Engine.PlaybackClockOverride = _ => (true, new(initialPosition, target.MediaDuration, true));
            tab.CaptureVodResumePosition();
            Assert.Equal(initialPosition, (await history.GetAsync(target))!.Position);

            // Model a ten-second polling interruption without sleeping or estimating progress.
            SetPrivateField(tab, "vodLastSampleTimestamp", Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency);
            if (slowBeforeSample)
            {
                await SetCleanupVodPlaybackRateAsync(tab, 0);
                Assert.Equal(0.5f, factory.Engine.PlaybackRate);
            }
            factory.Engine.PlaybackClockOverride = _ => (true, new(observedPosition, target.MediaDuration, true));
            tab.CaptureVodResumePosition(closing: true);
            Assert.Equal(expectedPosition, (await history.GetAsync(target))!.Position);
        }

        var reopenedFactory = new FakePlaybackEngineFactory();
        await using var reopened = VodResumeTestCatalog.Tab(target, files.Create(), reopenedFactory);
        await StartCleanupVodTabAsync(reopened);
        Assert.Equal<TimeSpan?>(expectedPosition, reopenedFactory.Engine!.LastStartPosition);
    }

    private static async Task VodResumeReadDiagnosticsAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = new CleanupVodHistory(await files.SeedAsync()) { FailRead = true };
        var logger = new MemoryLogger();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = VodResumeTestCatalog.Tab(VodResumeTestCatalog.Target(), history, factory, logger: logger);
        logger.EntryWritten += ThrowVodResumeDiagnosticWrite;
        try
        {
            await StartCleanupVodTabAsync(tab);
            factory.Engine!.PlaybackClockOverride = _ => (true, new(TimeSpan.FromMinutes(10), tab.Target.MediaDuration, true));
            tab.CaptureVodResumePosition();
            Assert.Equal(0, history.RememberAttempts);
            history.FailRead = false;
            Assert.Equal(TimeSpan.FromHours(1), (await history.GetAsync(tab.Target))!.Position);
        }
        finally { logger.EntryWritten -= ThrowVodResumeDiagnosticWrite; }
    }

    private static async Task VodResumeCaptureDiagnosticsAsync(bool completion)
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = new CleanupVodHistory(files.Create());
        var logger = new MemoryLogger();
        var factory = new FakePlaybackEngineFactory();
        await using var tab = VodResumeTestCatalog.Tab(VodResumeTestCatalog.Target(), history, factory, logger: logger);
        await StartCleanupVodTabAsync(tab);
        factory.Engine!.PlaybackClockOverride = _ => (true, new(TimeSpan.FromMinutes(10), tab.Target.MediaDuration, true));
        if (completion) factory.Engine.PlaybackHealthOverride = () => new(1, PlaybackEngineState.Ended, 0, 0, 0, 0);
        history.FailRemember = true;
        logger.EntryWritten += ThrowVodResumeDiagnosticWrite;
        try
        {
            tab.CaptureVodResumePosition();
            Assert.Equal<VodPlaybackBookmark?>(null, await history.GetAsync(tab.Target));
            history.FailRemember = false;
            tab.CaptureVodResumePosition();
            var bookmark = (await history.GetAsync(tab.Target))!;
            Assert.Equal(completion ? tab.Target.MediaDuration : TimeSpan.FromMinutes(10), bookmark.Position);
            Assert.Equal(completion, bookmark.Completed);
        }
        finally
        {
            history.FailRemember = false;
            logger.EntryWritten -= ThrowVodResumeDiagnosticWrite;
        }
    }

    private static async Task VodResumeSaveDiagnosticsAsync()
    {
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = new CleanupVodHistory(files.Create());
        var logger = new MemoryLogger();
        await using var tab = VodResumeTestCatalog.Tab(VodResumeTestCatalog.Target(), history,
            new FakePlaybackEngineFactory(), logger: logger);
        await StartCleanupVodTabAsync(tab, replay: true);
        history.FailSave = true;
        logger.EntryWritten += ThrowVodResumeDiagnosticWrite;
        try
        {
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(10)).WaitAsync(TimeSpan.FromSeconds(3));
            await tab.SeekReplayAsync(TimeSpan.FromMinutes(20)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(TimeSpan.FromMinutes(20), (await history.GetAsync(tab.Target))!.Position);
            Assert.Equal(false, tab.IsReplaySeekInProgress);
            Assert.Equal(false, GetPrivateField(tab, "replaySeekRequestWorkerRunning"));
            Assert.True(history.SaveAttempts >= 2);
        }
        finally
        {
            history.FailSave = false;
            logger.EntryWritten -= ThrowVodResumeDiagnosticWrite;
        }
        await history.SaveAsync();
        Assert.Equal(TimeSpan.FromMinutes(20), (await files.Create().GetAsync(tab.Target))!.Position);
    }

    private static void ThrowVodResumeDiagnosticWrite(object? sender, LogEntry entry)
    {
        if (entry.Source == "VOD resume" && entry.Level == AppLogLevel.Warning)
            throw new IOException("Injected VOD bookmark diagnostics failure.");
    }

    private static async Task StartCleanupVodTabAsync(StreamTabViewModel tab, bool replay = false)
    {
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(VodResumeTestCatalog.Settings(replay));
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        await (Task)typeof(StreamTabViewModel)
            .GetMethod("StopReplayClockPollingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tab, null)!;
    }

    private static async Task SetCleanupVodPlaybackRateAsync(StreamTabViewModel tab, int index)
    {
        tab.PlaybackRateIndex = index;
        await TestWait.UntilAsync(() => GetPrivateField(tab, "playbackRateChangeCancellation") is null,
            TimeSpan.FromSeconds(3), "The playback speed selection did not finish.");
        Assert.Equal(index, tab.PlaybackRateIndex);
    }

    private sealed class CleanupVodHistory(IVodPlaybackHistory history) : IVodPlaybackHistory
    {
        internal bool FailRead { get; set; }
        internal bool FailRemember { get; set; }
        internal bool FailSave { get; set; }
        internal int RememberAttempts { get; private set; }
        internal int SaveAttempts { get; private set; }
        public event Action<StreamTarget, VodPlaybackBookmark>? BookmarkChanged
        {
            add => history.BookmarkChanged += value;
            remove => history.BookmarkChanged -= value;
        }
        public Task<VodPlaybackBookmark?> GetAsync(StreamTarget target, CancellationToken cancellationToken = default) =>
            FailRead ? Task.FromException<VodPlaybackBookmark?>(new IOException("Injected bookmark read failure.")) :
                history.GetAsync(target, cancellationToken);
        public void Remember(StreamTarget target, VodPlaybackBookmark bookmark)
        {
            RememberAttempts++;
            if (FailRemember) throw new IOException("Injected bookmark capture failure.");
            history.Remember(target, bookmark);
        }
        public Task SaveAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            return FailSave ? Task.FromException(new IOException("Injected bookmark save failure.")) :
                history.SaveAsync(cancellationToken);
        }
    }
}
