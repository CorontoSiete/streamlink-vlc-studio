internal static class SeekingWorkflowTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("seeking workflow: an input burst submits only the final target", BurstAsync),
        ("seeking workflow: a loading seek retains only the newest waiting target", () => PendingAsync(false)),
        ("seeking workflow: queued seeks preserve paused playback", () => PendingAsync(true)),
        ("seeking workflow: only the newest target passes a busy playback gate", BusyPlaybackGateAsync),
        ("seeking workflow: a replacement from Starting skips the obsolete native seek", ReentrantStartingAsync),
        ("seeking workflow: slow URL resolution opens only the latest target without resolving twice", () => SlowResolutionAsync(true)),
        ("seeking workflow: a newer in-place seek avoids an obsolete reload", () => SlowResolutionAsync(false)),
        ("seeking workflow: a cached resolution respects a new quality", () => SlowResolutionAsync(true, changeQuality: true)),
        ("seeking workflow: duplicate loading targets reuse the confirmed seek", () => DuplicateAsync()),
        ("seeking workflow: duplicate paused targets reuse the confirmed seek", () => DuplicateAsync(paused: true)),
        ("seeking workflow: a fresh request after confirmation still reaches the player", PostConfirmationRequestAsync),
        ("seeking workflow: a failed duplicate target still retries", () => DuplicateAsync(failFirst: true)),
        ("seeking workflow: a duplicate force reload still replaces the input", () => DuplicateAsync(forceReload: true)),
        ("seeking workflow: duplicate timestamps preserve distinct live edge intents", DuplicateLiveIntentAsync),
        ("seeking workflow: a canceled preview commit restores the playback position", CanceledPreviewCommitAsync),
        ("seeking workflow: restoring the slider preserves the confirmed clock precision", ClockPrecisionAsync),
        ("seeking workflow: canceling a preview restores the newest requested target", PreviewAsync),
        ("seeking workflow: a new drag survives the previous seek confirmation", PreviewDuringConfirmationAsync),
        ("seeking workflow: property notifications can replace a request before it starts", ReentrantRequestAsync),
        ("seeking workflow: canceled waiting targets never reach the player", CanceledPendingAsync),
        ("seeking workflow: Stop cancels active and waiting seeks promptly", () => CancelLifecycleAsync(false)),
        ("seeking workflow: restarting discards old active and waiting targets", () => CancelLifecycleAsync(true)),
        ("seeking workflow: closing cancels active seeks without a late restore", DisposeAsync),
        ("seeking workflow: Go live interrupts the first replay open", GoLiveAsync),
        ("seeking workflow: requests during a cold live seek retain the opened input", LiveOpenAsync),
        ("seeking workflow: canceling a replay open clears errors and allows a retry", CancelOpenAsync),
        ("seeking workflow: a forward step at the live edge returns to live", LiveEdgeAsync),
        ("seeking workflow: repeated transport buttons accumulate and clamp targets", StepsAsync),
        ("seeking workflow: a waiting backward seek reopens an input that just ended", EndedInputAsync),
        .. NativeTests
    ];

    private static IReadOnlyList<(string Name, Func<Task> Run)> NativeTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("seeking workflow: native ended input accepts the newest target before EOF polling", NativeEndedInputAsync),
            ("seeking workflow: native duplicate targets reuse a confirmed cold open", () => NativeDuplicateAsync(false)),
            ("seeking workflow: native duplicate targets keep a cold open paused", () => NativeDuplicateAsync(true))
        ];

    private static Task BurstAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine());
        var tasks = Enumerable.Range(1, 12)
            .Select(index => fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(index * 10))).ToArray();
        Assert.Equal(120d, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal("2:00", fixture.Tab.ReplayElapsedText);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Engine.SeekCount);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Engine.Position);
        Assert.Equal(false, fixture.Tab.IsReplaySeekInProgress);
    });

    private static Task PendingAsync(bool paused) => TestSta.RunOffscreenAsync(async () =>
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targets = new List<TimeSpan>();
        var engine = new FakePlaybackEngine
        {
            SeekOverride = async (position, token) =>
            {
                targets.Add(position);
                if (targets.Count == 1) await first.Task.WaitAsync(token);
                else { lastStarted.TrySetResult(); await last.Task.WaitAsync(token); }
            }
        };
        await using var fixture = await Fixture.CreateAsync(engine);
        if (paused) await fixture.Tab.PauseOrResumeAsync();
        var active = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        await engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = new List<Task>();
        for (var target = 20; target <= 120; target += 10)
            pending.Add(fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(target)));
        Assert.True(fixture.Tab.CanSeekReplay);
        Assert.True(fixture.Tab.CanStepReplay);
        Assert.Equal(120d, fixture.Tab.ReplaySeekSliderValue);
        first.TrySetResult();
        await lastStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(120d, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal("2:00", fixture.Tab.ReplayElapsedText);
        Assert.True(fixture.Tab.IsReplaySeekInProgress);
        last.TrySetResult();
        await Task.WhenAll(pending.Append(active)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.SequenceEqual(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120) }, targets);
        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(paused, engine.Paused);
        Assert.Equal(paused ? PlaybackStatus.Paused : PlaybackStatus.Playing, fixture.Tab.Status);
        Assert.Equal(120d, fixture.Tab.ReplaySeekValue);
        Assert.Equal(false, fixture.Tab.IsBusy);
    });

    private static Task PreviewAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine { SeekCompletion = release.Task });
        var seek = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        await fixture.Engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Tab.BeginReplaySeekPreview(240);
        Assert.True(fixture.Tab.IsReplaySeekPreviewActive);
        fixture.Tab.CancelReplaySeekPreview();
        Assert.Equal(120d, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal("2:00", fixture.Tab.ReplayElapsedText);
        release.TrySetResult();
        await seek.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Engine.SeekCount);
    });

    private static Task BusyPlaybackGateAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine { PauseCompletion = release.Task });
        var pause = fixture.Tab.PauseOrResumeAsync();
        await TestWait.UntilAsync(() => fixture.Engine.PauseCount == 1, TimeSpan.FromSeconds(3));
        var first = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        await TestWait.UntilAsync(() => typeof(StreamTabViewModel)
            .GetField("activeReplaySeekRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Tab) is not null, TimeSpan.FromSeconds(3));
        var last = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        release.TrySetResult();
        await Task.WhenAll(pause, first, last).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Engine.SeekCount);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Engine.Position);
        Assert.True(fixture.Engine.Paused);
        Assert.Equal(PlaybackStatus.Paused, fixture.Tab.Status);
    });

    private static Task ReentrantStartingAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine());
        Task? replacement = null;
        fixture.Tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(fixture.Tab.Status) &&
                fixture.Tab.Status == PlaybackStatus.Starting && replacement is null)
                replacement = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        };
        var original = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        await original.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(replacement);
        await replacement!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Engine.SeekCount);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Engine.Position);
    });

    private static Task DuplicateAsync(bool paused = false, bool failFirst = false, bool forceReload = false)
        => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var engine = new FakePlaybackEngine
        {
            SeekOverride = async (_, token) =>
            {
                if (++attempts != 1) return;
                await release.Task.WaitAsync(token);
                if (failFirst) throw new InvalidOperationException("First attempt failed.");
            }
        };
        await using var fixture = await Fixture.CreateAsync(engine);
        if (paused) await fixture.Tab.PauseOrResumeAsync();
        var active = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        await engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replaced = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(240));
        var duplicate = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120), forceReload: forceReload);
        release.TrySetResult();
        await Task.WhenAll(active, replaced, duplicate).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(failFirst || forceReload ? 2 : 1, engine.SeekCount);
        Assert.Equal(forceReload ? 2 : 1, engine.PlayCount);
        Assert.Equal(TimeSpan.FromSeconds(120), engine.Position);
        Assert.Equal(120d, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal(paused, engine.Paused);
        Assert.Equal(paused ? PlaybackStatus.Paused : PlaybackStatus.Playing, fixture.Tab.Status);
        Assert.Equal(false, fixture.Tab.IsReplaySeekInProgress);
    });

    private static Task SlowResolutionAsync(bool reloadLatest, bool changeQuality = false) => TestSta.RunOffscreenAsync(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutions = 0;
        var streamlink = new FakeStreamlinkService
        {
            ResolveStreamUrlOverride = async (_, token) =>
            {
                if (++resolutions > 1)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return new StreamlinkResolvedUrl(new Uri("https://cdn.example.com/replay.m3u8"), "Resolved.");
            }
        };
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine(), streamlink: streamlink);
        var first = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10), forceReload: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replaced = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(240), forceReload: true);
        if (changeQuality) fixture.Tab.Quality = "720p";
        var last = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120), forceReload: reloadLatest);
        release.TrySetResult();
        await Task.WhenAll(first, replaced, last).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(reloadLatest ? 2 : 1, fixture.Engine.PlayCount);
        Assert.Equal(1, fixture.Engine.SeekCount);
        Assert.Equal(changeQuality ? 3 : 2, resolutions);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Engine.Position);
        if (reloadLatest) Assert.Equal(TimeSpan.FromSeconds(120), fixture.Engine.LastStartPosition);
    });

    private static Task PostConfirmationRequestAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine());
        Task? next = null;
        fixture.Tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(fixture.Tab.Status) &&
                fixture.Tab.Status == PlaybackStatus.Playing && next is null)
                next = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        };
        var first = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(next);
        await next!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, fixture.Engine.SeekCount);
        Assert.Equal(1, fixture.Engine.PlayCount);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Engine.Position);
        Assert.Equal(false, fixture.Tab.IsReplaySeekInProgress);
    });

    private static Task DuplicateLiveIntentAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine { SeekCompletion = release.Task }, live: true);
        var offset = TimeSpan.FromSeconds(fixture.Tab.ReplaySeekMaximum - 1);
        var exact = fixture.Tab.SeekReplayAsync(offset, holdExactPosition: true);
        await fixture.Engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var live = fixture.Tab.SeekReplayAsync(offset);
        release.TrySetResult();
        await Task.WhenAll(exact, live).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, fixture.Engine.PlayCount);
        Assert.Equal(false, fixture.Tab.IsReplayMode);
        Assert.Equal(false, fixture.Tab.IsBehindLive);
    });

    private static Task CanceledPreviewCommitAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine());
        await fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        fixture.Tab.BeginReplaySeekPreview(240);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await fixture.Tab.CommitReplaySeekPreviewAsync(240, cancellation.Token);
        Assert.Equal(false, fixture.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(120d, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal("2:00", fixture.Tab.ReplayElapsedText);
        Assert.Equal(1, fixture.Engine.SeekCount);
    });

    private static Task ClockPrecisionAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine());
        var position = TimeSpan.FromMilliseconds(2151);
        await fixture.Tab.SeekReplayAsync(position);
        Assert.Equal(position.TotalSeconds, fixture.Tab.ReplaySeekValue);
        Assert.Equal(position.TotalSeconds, fixture.Tab.ReplaySeekSliderValue);
        fixture.Tab.BeginReplaySeekPreview(120);
        fixture.Tab.CancelReplaySeekPreview();
        Assert.Equal(position.TotalSeconds, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal(1, fixture.Engine.SeekCount);
    });

    private static Task CanceledPendingAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine { SeekCompletion = release.Task });
        var active = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120), cancellation.Token);
        cancellation.Cancel();
        await canceled.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(10d, fixture.Tab.ReplaySeekSliderValue);
        release.TrySetResult();
        await Task.WhenAll(active, canceled).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Engine.SeekCount);
        Assert.Equal(TimeSpan.FromSeconds(10), fixture.Engine.Position);
        Assert.Equal(10d, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal(false, fixture.Tab.IsBusy);
    });

    private static Task PreviewDuringConfirmationAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine { SeekCompletion = release.Task });
        var seek = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        await fixture.Engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Tab.BeginReplaySeekPreview(240);
        release.TrySetResult();
        await seek.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Tab.IsReplaySeekPreviewActive);
        Assert.Equal(240d, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal(120d, fixture.Tab.ReplaySeekValue);
        fixture.Tab.CancelReplaySeekPreview();
        Assert.Equal(120d, fixture.Tab.ReplaySeekSliderValue);
        Assert.Equal("2:00", fixture.Tab.ReplayElapsedText);
    });

    private static Task ReentrantRequestAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine());
        Task? replacement = null;
        fixture.Tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(fixture.Tab.IsReplaySeekInProgress) &&
                fixture.Tab.IsReplaySeekInProgress && replacement is null)
                replacement = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        };
        var original = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(replacement);
        await Task.WhenAll(original, replacement!).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Engine.SeekCount);
        Assert.Equal(TimeSpan.FromSeconds(120), fixture.Engine.Position);
    });

    private static Task CancelLifecycleAsync(bool restart) => TestSta.RunOffscreenAsync(async () =>
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine { SeekCompletion = blocked.Task });
        var active = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        var transition = restart ? fixture.Tab.StartAsync(fixture.Settings) : fixture.Tab.StopAsync();
        await Task.WhenAll(active, pending, transition).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Engine.SeekCount);
        Assert.Equal(restart ? PlaybackStatus.Playing : PlaybackStatus.Stopped, fixture.Tab.Status);
        Assert.Equal(false, fixture.Tab.IsReplaySeekInProgress);
        Assert.Equal(false, fixture.Tab.IsBusy);
        blocked.TrySetResult();
        await Task.Yield();
        Assert.Equal(TimeSpan.Zero, fixture.Engine.Position);
    });

    private static Task DisposeAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine { SeekCompletion = blocked.Task });
        var active = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        await fixture.Engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        await fixture.Tab.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(active, pending).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, fixture.Engine.SeekCount);
        Assert.Equal(false, fixture.Tab.CanSeekReplay);
        blocked.TrySetResult();
        await Task.Yield();
        Assert.Equal(TimeSpan.Zero, fixture.Engine.Position);
    });

    private static Task GoLiveAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playRequests = 0;
        var engine = new FakePlaybackEngine
        {
            PlayCompletionOverride = _ =>
            {
                if (++playRequests != 2) return Task.CompletedTask;
                replayStarted.TrySetResult();
                return blocked.Task;
            }
        };
        await using var fixture = await Fixture.CreateAsync(engine, live: true);
        var seek = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        await replayStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Tab.CanReturnToLive);
        var pending = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        await fixture.Tab.ReturnToLiveCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(seek, pending).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PlaybackStatus.Playing, fixture.Tab.Status);
        Assert.Equal(false, fixture.Tab.IsReplayMode);
        Assert.Equal(false, fixture.Tab.IsBehindLive);
        Assert.Equal(new Uri("http://127.0.0.1:5000/"), engine.LastPlayedUri);
        Assert.Equal(0, engine.SeekCount);
    });

    private static Task LiveEdgeAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine(), live: true);
        await fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(600));
        await fixture.Tab.SeekReplayAsync(TimeSpan.FromHours(2)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PlaybackStatus.Playing, fixture.Tab.Status);
        Assert.Equal(false, fixture.Tab.IsReplayMode);
        Assert.Equal(false, fixture.Tab.IsBehindLive);
        Assert.Equal(new Uri("http://127.0.0.1:5000/"), fixture.Engine.LastPlayedUri);
    });

    private static Task LiveOpenAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakePlaybackEngine
        {
            PlayCompletionOverride = number =>
            {
                if (number != 2) return Task.CompletedTask;
                replayStarted.TrySetResult();
                return release.Task;
            }
        };
        await using var fixture = await Fixture.CreateAsync(engine, live: true);
        var first = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10));
        await replayStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var requests = Enumerable.Range(1, 12)
            .Select(index => fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(index * 10))).ToArray();
        release.TrySetResult();
        await Task.WhenAll(requests.Append(first)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, engine.PlayCount);
        Assert.Equal(2, engine.SeekCount);
        Assert.Equal(TimeSpan.FromSeconds(120), engine.Position);
        Assert.True(fixture.Tab.IsReplayMode);
    });

    private static Task CancelOpenAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var replayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playRequests = 0;
        var engine = new FakePlaybackEngine
        {
            PlayCompletionOverride = _ =>
            {
                if (++playRequests != 2) return Task.CompletedTask;
                replayStarted.TrySetResult();
                return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }
        };
        await using var fixture = await Fixture.CreateAsync(engine, live: true);
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        await replayStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PlaybackStatus.Stopped, fixture.Tab.Status);
        Assert.Equal("", fixture.Tab.ErrorMessage);
        Assert.Equal(false, fixture.Tab.IsBusy);
        await fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(120), engine.Position);
        Assert.Equal(PlaybackStatus.Playing, fixture.Tab.Status);
    });

    private static Task StepsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync(new FakePlaybackEngine());
        var steps = new[]
        {
            fixture.Tab.FastForwardReplay30SecondsCommand.ExecuteAsync(),
            fixture.Tab.FastForwardReplay30SecondsCommand.ExecuteAsync(),
            fixture.Tab.RewindReplay30SecondsCommand.ExecuteAsync(),
            fixture.Tab.FastForwardReplay30SecondsCommand.ExecuteAsync()
        };
        await Task.WhenAll(steps).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(60), fixture.Engine.Position);
        Assert.Equal(1, fixture.Engine.SeekCount);
        var backwards = Enumerable.Range(0, 4)
            .Select(_ => fixture.Tab.SkipBackwardCommand.ExecuteAsync()).ToArray();
        await Task.WhenAll(backwards).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.Zero, fixture.Engine.Position);
        Assert.Equal(2, fixture.Engine.SeekCount);
    });

    private static Task EndedInputAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = false;
        var engine = new FakePlaybackEngine
        {
            PlayCompletionOverride = _ => { ended = false; return Task.CompletedTask; },
            SeekOverride = async (position, token) =>
            {
                if (ended) throw new InvalidOperationException("Playback ended before the replay position could be restored.");
                if (position == TimeSpan.FromHours(2))
                {
                    await release.Task.WaitAsync(token);
                    ended = true;
                }
            },
            PlaybackHealthOverride = () => new(1, ended ? PlaybackEngineState.Ended : PlaybackEngineState.Playing,
                0, 0, 0, 0)
        };
        await using var fixture = await Fixture.CreateAsync(engine);
        var first = fixture.Tab.SeekReplayAsync(TimeSpan.FromHours(2));
        await engine.SeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var last = fixture.Tab.SeekReplayAsync(TimeSpan.FromSeconds(120));
        release.TrySetResult();
        await Task.WhenAll(first, last).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(120), engine.Position);
        Assert.Equal(2, engine.PlayCount);
        Assert.Equal(PlaybackStatus.Playing, fixture.Tab.Status);
        Assert.Equal(false, fixture.Tab.IsVodFinished);
    });

    private static Task NativeEndedInputAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            var media = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4"));
            var streamlink = new FakeStreamlinkService
            {
                ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(media, "Local VOD"))
            };
            var settings = VodResumeTestCatalog.Settings();
            settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
            settings.VideoRendererMode = VideoRendererMode.Gdi;
            settings.Chat.Layout = ChatLayout.Hidden;
            var logger = new MemoryLogger();
            await using var tab = TestViewModels.CreateTab(VodResumeTestCatalog.Target() with
            { MediaDuration = TimeSpan.FromSeconds(60) }, "best", streamlink,
                new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
                action => action(), initialVolume: 0);
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            var engine = (IPlaybackEngine)typeof(StreamTabViewModel)
                .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            await TestWait.UntilAsync(() => tab.CanSeekReplay && engine.TryGetPlaybackHealth(out var health) &&
                health.DisplayedPictures > 0, TimeSpan.FromSeconds(5));
            await ((Task)typeof(StreamTabViewModel).GetMethod("StopReplayClockPollingAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tab, null)!);
            await engine.SeekAsync(TimeSpan.FromSeconds(60));
            PlaybackHealth ended = default;
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out ended) &&
                ended.State == PlaybackEngineState.Ended, TimeSpan.FromSeconds(5));
            Assert.Equal(false, tab.IsVodFinished);
            var targets = Enumerable.Range(1, 12).Select(index => tab.SeekReplayAsync(
                TimeSpan.FromSeconds(index == 12 ? 35.25 : index))).ToArray();
            await Task.WhenAll(targets).WaitAsync(TimeSpan.FromSeconds(20));
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                (clock.Position - TimeSpan.FromSeconds(35.25)).Duration() < TimeSpan.FromSeconds(3) &&
                engine.TryGetPlaybackHealth(out var health) && health.Generation != ended.Generation &&
                health.State == PlaybackEngineState.Playing && health.DisplayedPictures > 0,
                TimeSpan.FromSeconds(5), "The ended input must reopen at the newest target and present video.");
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.Equal("", tab.ErrorMessage);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private static Task NativeDuplicateAsync(bool paused) => TestSta.RunOffscreenAsync(async () =>
    {
        var handle = NativeWindowTest.CreateHiddenParentWindow();
        try
        {
            var media = new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4"));
            var streamlink = new FakeStreamlinkService
            {
                ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(media, "Local VOD"))
            };
            var settings = VodResumeTestCatalog.Settings();
            settings.VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!;
            settings.VideoRendererMode = VideoRendererMode.Gdi;
            settings.Chat.Layout = ChatLayout.Hidden;
            var logger = new MemoryLogger();
            await using var tab = TestViewModels.CreateTab(VodResumeTestCatalog.Target() with
            { MediaDuration = TimeSpan.FromSeconds(60) }, "best", streamlink,
                new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
                action => action(), initialVolume: 0);
            tab.SetVideoHandle(handle);
            await tab.StartAsync(settings);
            var engine = (IPlaybackEngine)typeof(StreamTabViewModel)
                .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            PlaybackHealth initial = default;
            await TestWait.UntilAsync(() => tab.CanSeekReplay && engine.TryGetPlaybackHealth(out initial) &&
                initial.DisplayedPictures > 0, TimeSpan.FromSeconds(5));
            if (paused) await tab.PauseOrResumeAsync();
            var version = typeof(StreamTabViewModel).GetField("replaySeekOperationVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var before = (long)version.GetValue(tab)!;
            var offset = TimeSpan.FromSeconds(35.25);
            var active = tab.SeekReplayAsync(offset, forceReload: true);
            PlaybackHealth opening = default;
            await TestWait.UntilAsync(() => tab.IsReplaySeekInProgress && engine.TryGetPlaybackHealth(out opening) &&
                opening.Generation != initial.Generation, TimeSpan.FromSeconds(10));
            var duplicate = tab.SeekReplayAsync(offset);
            await Task.WhenAll(active, duplicate).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(before + 1, (long)version.GetValue(tab)!);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                (clock.Position - offset).Duration() < TimeSpan.FromSeconds(3) &&
                engine.TryGetPlaybackHealth(out var health) && health.Generation == opening.Generation &&
                health.State == (paused ? PlaybackEngineState.Paused : PlaybackEngineState.Playing) && health.DisplayedPictures > 0,
                TimeSpan.FromSeconds(5), "The confirmed input must retain its target, pause state and video output.");
            Assert.Equal(paused ? PlaybackStatus.Paused : PlaybackStatus.Playing, tab.Status);
            Assert.Equal(false, tab.IsReplaySeekInProgress);
            Assert.Equal("", tab.ErrorMessage);
        }
        finally { NativeWindowTest.DestroyWindow(handle); }
    });

    private sealed class Fixture(StreamTabViewModel tab, FakePlaybackEngine engine, AppSettings settings) : IAsyncDisposable
    {
        internal StreamTabViewModel Tab { get; } = tab;
        internal FakePlaybackEngine Engine { get; } = engine;
        internal AppSettings Settings { get; } = settings;

        internal static async Task<Fixture> CreateAsync(FakePlaybackEngine engine, bool live = false,
            FakeStreamlinkService? streamlink = null)
        {
            var settings = VodResumeTestCatalog.Settings();
            settings.Chat.Layout = ChatLayout.Hidden;
            var replay = new ReplaySessionInfo(PlatformKind.Twitch, "streamer", "https://cdn.example.com/replay.m3u8",
                "12345", null, TimeSpan.FromHours(2), true, "");
            var tab = TestViewModels.CreateTab(live ? StreamInputParser.Parse("streamer", PlatformKind.Twitch)
                    : VodResumeTestCatalog.Target(), "best", streamlink ?? new FakeStreamlinkService(),
                new FakePlaybackEngineFactory(() => engine), new FakeChatClientFactory(), new MemoryLogger(),
                action => action(), replayResolver: new FakeReplayResolver(replay));
            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.CanSeekReplay, TimeSpan.FromSeconds(3));
            return new(tab, engine, settings);
        }

        public ValueTask DisposeAsync() => Tab.DisposeAsync();
    }
}
