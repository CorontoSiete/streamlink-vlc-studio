internal static partial class CodeCleanupTestCatalog
{
    private static async Task OfflinePackageReferencesAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "studio-offline-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var playlist = OfflineHlsPlaylist.Parse(
                "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:3\n" +
                "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x1\n" +
                "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:3,\nsegment.ts\n#EXT-X-ENDLIST\n",
                new Uri("https://stream.kick.com/index.m3u8"));
            foreach (var asset in playlist.Assets)
                await File.WriteAllBytesAsync(Path.Combine(directory, asset.FileName), new byte[16]);
            var package = await OfflineVodPackage.CreateAsync(directory, playlist, CancellationToken.None);
            await OfflineVodPackage.ValidateAsync(directory, CancellationToken.None);

            foreach (var asset in playlist.Assets)
            {
                foreach (var prefix in new[] { "https://offline.invalid/", "http://offline.invalid/",
                    "//offline.invalid/", "https://offline.invalid:8443/", "/" })
                {
                    var manifest = Encoding.UTF8.GetBytes(playlist.Content.Replace(asset.FileName, prefix + asset.FileName));
                    var altered = package with { ManifestHash = Convert.ToHexString(SHA256.HashData(manifest)) };
                    await File.WriteAllBytesAsync(Path.Combine(directory, OfflineVodPackage.ManifestName), manifest);
                    await File.WriteAllTextAsync(Path.Combine(directory, OfflineVodPackage.PackageName), JsonSerializer.Serialize(altered));

                    await Assert.ThrowsAsync<InvalidDataException>(() =>
                        OfflineVodPackage.ValidateAsync(directory, CancellationToken.None));
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task SupervisorCancellationFailureAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new LiveChatConnectionSupervisor(new MemoryLogger(), "TestChat", _ => { },
            async (_, token) =>
            {
                using var registration = token.Register(() => throw new IOException("Cancellation callback failed."));
                entered.TrySetResult();
                await release.Task;
                token.ThrowIfCancellationRequested();
            });
        supervisor.Start(_ => Task.CompletedTask);
        supervisor.NotifyConnectionEnded(TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellation = (CancellationTokenSource)GetPrivateField(supervisor, "lifetimeCancellation")!;
        var worker = (Task)GetPrivateField(supervisor, "runTask")!;
        Task? disposal = null;
        try
        {
            disposal = supervisor.DisposeAsync().AsTask();
            Assert.True(!disposal.IsCompleted, "Disposal must drain the active worker even when cancellation callbacks fail.");
            Assert.True(ReferenceEquals(disposal, supervisor.DisposeAsync().AsTask()));
        }
        finally
        {
            release.TrySetResult();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
        }
        await Assert.ThrowsAsync<AggregateException>(() => disposal!);
        Assert.True(worker.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        Assert.Throws<ObjectDisposedException>(() =>
            ((SemaphoreSlim)GetPrivateField(supervisor, "reconnectSignal")!).Wait(0));
    }

    private static async Task SupervisorWorkerFailureAsync()
    {
        foreach (var twitch in new[] { true, false })
        {
            var supervisor = new LiveChatConnectionSupervisor(new MemoryLogger(), "TestChat", _ => { },
                (_, _) => Task.FromException(new IOException("Reconnect worker failed.")));
            supervisor.Start(_ => Task.CompletedTask);
            supervisor.NotifyConnectionEnded(TimeSpan.Zero);
            var worker = (Task)GetPrivateField(supervisor, "runTask")!;
            await Assert.ThrowsAsync<IOException>(() => worker.WaitAsync(TimeSpan.FromSeconds(2)));
            var cancellation = (CancellationTokenSource)GetPrivateField(supervisor, "lifetimeCancellation")!;
            using var readCancellation = new CancellationTokenSource();
            IChatClient client = twitch
                ? new TwitchChatClient(new ChatSettings(), new MemoryLogger())
                : new KickChatClient(new ChatSettings(), new MemoryLogger());
            await using (client)
            {
                SetPrivateField(client, "connectionSupervisor", supervisor);
                SetPrivateField(client, "readCancellation", readCancellation);
                SetPrivateField(client, "connectedChannel", "streamer");
                SetPrivateField(client, "canSendMessages", true);

                await Assert.ThrowsAsync<IOException>(() => client.DisconnectAsync());

                AssertChatFieldsCleared(client, "connectionSupervisor", "readCancellation", "connectedChannel");
                Assert.Equal(false, GetPrivateField(client, "canSendMessages"));
                Assert.Throws<ObjectDisposedException>(() => _ = readCancellation.Token);
                Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
                await client.DisconnectAsync();
            }
        }
    }

    private static async Task EventSubCancellationFailureAsync()
    {
        using var http = new HttpClient();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TwitchPredictionEventSubClient(new TwitchPredictionApiClient(http), new MemoryLogger(),
            "token", "client", "channel", _ => { }, _ => { }, async token =>
            {
                using var registration = token.Register(() => throw new IOException("Cancellation callback failed."));
                entered.TrySetResult();
                await release.Task;
            });
        client.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellation = (CancellationTokenSource)GetPrivateField(client, "cancellation")!;
        var worker = (Task)GetPrivateField(client, "runTask")!;
        var disposal = client.DisposeAsync().AsTask();
        try
        {
            Assert.True(!disposal.IsCompleted, "EventSub must drain its worker before releasing the worker's cancellation source.");
            Assert.True(cancellation.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
            await worker.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await Assert.ThrowsAsync<AggregateException>(() => disposal);
        Assert.Throws<ObjectDisposedException>(() => _ = cancellation.Token);
        Assert.Equal<object?>(null, GetPrivateField(client, "runTask"));
    }

    private static async Task RejectedDebounceDelayAsync()
    {
        foreach (var delay in new[] { TimeSpan.FromMilliseconds(-2), TimeSpan.FromMilliseconds(uint.MaxValue) })
        {
            using var coordinator = new CancellationDebounceCoordinator();
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.Schedule(TimeSpan.FromMilliseconds(100), () => pending.TrySetResult());

            Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Schedule(delay,
                () => throw new InvalidOperationException("A rejected replacement must never run.")));

            await pending.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task DebounceCancellationFailureAsync()
    {
        using var lifetime = new CancellationTokenSource();
        using var coordinator = new CancellationDebounceCoordinator();
        var first = coordinator.BeginOperation(lifetime.Token);
        using var registration = first.Token.Register(() => throw new IOException("Cancellation callback failed."));
        CancellationTokenSource? second = null;
        try
        {
            second = coordinator.BeginOperation(lifetime.Token);
            Assert.True(first.IsCancellationRequested);
            Assert.True(!second.IsCancellationRequested);
            coordinator.Complete(first);
            coordinator.CancelActive();
            Assert.True(second.IsCancellationRequested);
            coordinator.Complete(second);
            await coordinator.DrainAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            coordinator.Complete(first);
            if (second is not null) coordinator.Complete(second);
        }
    }

    private static async Task PredictionCancellationFailureAsync()
    {
        var client = new TwitchChatClient(new ChatSettings(), new MemoryLogger());
        using var readCancellation = new CancellationTokenSource();
        var predictionCancellation = (CancellationTokenSource)GetPrivateField(client, "predictionLifetimeCancellation")!;
        var requestsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetPrivateField(client, "predictionRequestsDrained", requestsDrained);
        SetPrivateField(client, "readCancellation", readCancellation);
        SetPrivateField(client, "connectedChannel", "streamer");
        SetPrivateField(client, "canSendMessages", true);
        Task? reentrantDisposal = null;
        using var registration = predictionCancellation.Token.Register(() =>
        {
            reentrantDisposal = client.DisposeAsync().AsTask();
            throw new IOException("Prediction cancellation callback failed.");
        });
        var disposal = client.DisposeAsync().AsTask();
        try
        {
            Assert.True(!disposal.IsCompleted, "Disposal must wait for prediction requests even if cancellation fails.");
            Assert.True(ReferenceEquals(disposal, reentrantDisposal));
            AssertChatFieldsCleared(client, "readCancellation", "connectedChannel");
            Assert.Throws<ObjectDisposedException>(() => _ = readCancellation.Token);
        }
        finally
        {
            requestsDrained.TrySetResult();
        }
        await Assert.ThrowsAsync<AggregateException>(() => disposal);
        Assert.Throws<ObjectDisposedException>(() => _ = predictionCancellation.Token);
    }

    private static async Task PredictionDisposalLockOrderAsync()
    {
        var client = new TwitchChatClient(new ChatSettings(), new MemoryLogger());
        var predictionGate = GetPrivateField(client, "predictionRequestLifecycleGate")!;
        var stateGate = GetPrivateField(client, "disposalGate")!;
        Task disposal;
        bool started;
        var stateLockAvailable = false;
        Monitor.Enter(predictionGate);
        try
        {
            disposal = Task.Run(() => client.DisposeAsync().AsTask());
            started = SpinWait.SpinUntil(() => (bool)GetPrivateField(client, "disposed")!, TimeSpan.FromSeconds(2));
            if (started && Monitor.TryEnter(stateGate, TimeSpan.FromSeconds(1)))
            {
                stateLockAvailable = true;
                Monitor.Exit(stateGate);
            }
        }
        finally
        {
            Monitor.Exit(predictionGate);
        }
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(started, "Disposal did not start within the test deadline.");
        Assert.True(stateLockAvailable, "Disposal must leave the state lock available while waiting for prediction requests.");
    }
}
