internal static partial class VodDownloadTestCatalog
{
    private static async Task DownloadPreferencesAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var path = Path.Combine(fixture.Root, "settings.json");
        await File.WriteAllTextAsync(path, "{\"DefaultQuality\":\"480p\"}");
        var service = new JsonSettingsService(path);
        var settings = await service.LoadAsync();
        Assert.Equal("480p", settings.Downloads.Quality);
        Assert.Equal(0L, settings.Downloads.BandwidthLimitBytesPerSecond);
        Assert.True(settings.Downloads.Directory is null);
        settings.DefaultQuality = "1080p";
        settings.Downloads.Quality = "720p";
        settings.Downloads.BandwidthLimitMegabytesPerSecond = 1.25;
        settings.Downloads.Directory = Path.Combine(fixture.Root, "new downloads");
        settings.Downloads.PreviousDirectories = [fixture.Library, fixture.Library];
        await service.SaveAsync(settings);
        var restored = await service.LoadAsync();
        Assert.Equal("1080p", restored.DefaultQuality);
        Assert.Equal("720p", restored.Downloads.Quality);
        Assert.Equal(1.25, restored.Downloads.BandwidthLimitMegabytesPerSecond);
        Assert.Equal(1_250_000L, restored.Downloads.BandwidthLimitBytesPerSecond);
        Assert.Equal(settings.Downloads.Directory, restored.Downloads.Directory);
        Assert.SequenceEqual(new[] { fixture.Library }, restored.Downloads.PreviousDirectories);
        Assert.DoesNotContain("BandwidthLimitBytesPerSecond", await File.ReadAllTextAsync(path));
        restored.Downloads.BandwidthLimitMegabytesPerSecond = double.MaxValue;
        Assert.Equal(long.MaxValue, restored.Downloads.BandwidthLimitBytesPerSecond);
        foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
        {
            restored.Downloads.BandwidthLimitMegabytesPerSecond = invalid;
            Assert.Equal(0L, restored.Downloads.BandwidthLimitBytesPerSecond);
        }
        restored.Downloads.BandwidthLimitMegabytesPerSecond = 0.000001;
        Assert.Equal(1L, restored.Downloads.BandwidthLimitBytesPerSecond);
    }

    private static async Task DownloadBandwidthAsync()
    {
        var limiter = new VodDownloadBandwidthLimiter();
        limiter.SetLimit(1024);
        var timer = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            var bytes = Enumerable.Repeat((byte)(index + 1), 128).ToArray();
            using var source = new MemoryStream(bytes);
            using var throttled = new VodDownloadThrottledStream(source, limiter);
            using var destination = new MemoryStream();
            await throttled.CopyToAsync(destination);
            Assert.SequenceEqual(bytes, destination.ToArray());
        }));
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(450), "Each transfer got its own bandwidth allowance instead of sharing one limit.");

        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        fixture.Service.SetBandwidthLimit(40);
        timer.Restart();
        var item = await fixture.DownloadAsync();
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(450), "The VOD downloader did not use its configured bandwidth limit.");
        Assert.Equal(20L, item.BytesDownloaded);
    }

    private static async Task DownloadBandwidthChangesAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        fixture.Service.SetBandwidthLimit(1);
        var item = await fixture.EnqueueAsync();
        await fixture.WaitAsync(item.Id, VodDownloadState.Downloading);
        fixture.Service.SetBandwidthLimit(0);
        await fixture.WaitAsync(item.Id, VodDownloadState.Completed).WaitAsync(TimeSpan.FromSeconds(2));

        fixture.Service.SetBandwidthLimit(1);
        item = await fixture.EnqueueAsync(DownloadTarget(fixture, "23456"));
        await fixture.WaitAsync(item.Id, VodDownloadState.Downloading);
        await fixture.Service.CancelAsync(item.Id);
        await fixture.WaitAsync(item.Id, VodDownloadState.Canceled).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, item.Id.ToString("N"), ".partial")));
    }

    private static async Task DownloadBandwidthReservationsAsync()
    {
        var limiter = new VodDownloadBandwidthLimiter();
        Assert.Equal(65536, (await limiter.ReserveAsync(65536, CancellationToken.None)).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.SetLimit(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => limiter.ReserveAsync(-1, CancellationToken.None).AsTask());
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => limiter.ReserveAsync(65536, alreadyCanceled.Token).AsTask());
        limiter.SetLimit(1);
        var waiting = limiter.ReserveAsync(65536, CancellationToken.None).AsTask();
        await Task.Delay(30);
        Assert.True(!waiting.IsCompleted);
        limiter.SetLimit(0);
        Assert.Equal(65536, (await waiting.WaitAsync(TimeSpan.FromSeconds(1))).Count);

        limiter.SetLimit(1);
        using var cancellation = new CancellationTokenSource();
        var first = limiter.ReserveAsync(65536, cancellation.Token).AsTask();
        var second = limiter.ReserveAsync(65536, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        limiter.SetLimit(0);
        Assert.Equal(65536, (await limiter.ReserveAsync(65536, CancellationToken.None)).Count);
    }

    private static async Task ChangeDownloadFolderAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var completed = await fixture.DownloadAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Override = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("segment-a.ts", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return null;
        };
        var active = await fixture.EnqueueAsync(DownloadTarget(fixture, "23456"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = await fixture.EnqueueAsync(DownloadTarget(fixture, "34567"));
        var directory = Path.Combine(fixture.Root, "new downloads 日本語");
        await fixture.Service.ChangeDownloadDirectoryAsync(directory);
        Assert.Equal(directory, fixture.Service.DownloadDirectory);
        Assert.True(!Directory.EnumerateFiles(directory, ".streamstudio-write-*").Any());
        var future = await fixture.EnqueueAsync(DownloadTarget(fixture, "45678"));
        release.TrySetResult();
        active = await fixture.WaitAsync(active.Id, VodDownloadState.Completed);
        queued = await fixture.WaitAsync(queued.Id, VodDownloadState.Completed);
        future = await fixture.WaitAsync(future.Id, VodDownloadState.Completed);
        foreach (var existing in new[] { completed, active, queued })
            Assert.True(existing.LocalMediaPath.StartsWith(fixture.Library + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.True(future.LocalMediaPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(completed.LocalMediaPath));
        Assert.Equal(completed.Id, (await fixture.EnqueueAsync()).Id);
        await fixture.Service.DisposeAsync();
        fixture.Handler.NetworkAvailable = false;
        await using var restored = new VodDownloadService(directory, fixture.Resolver, new MemoryLogger(), null,
            fixture.Client, new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })),
            previousDownloadDirectories: [fixture.Library]);
        var items = await restored.GetDownloadsAsync();
        Assert.Equal(4, items.Count);
        foreach (var item in items)
        {
            Assert.Equal(VodDownloadState.Completed, item.State);
            Assert.True(File.Exists((await restored.GetOfflineTargetAsync(item.Id)).LocalMediaPath));
        }
        await restored.RemoveAsync(completed.Id);
        Assert.True(!File.Exists(completed.LocalMediaPath));
        Assert.True(File.Exists(active.LocalMediaPath));
        Assert.True(File.Exists(future.LocalMediaPath));
    }

    private static async Task DownloadFolderFailuresAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var entered = fixture.BlockSegment();
        var item = await fixture.EnqueueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var directory = Path.Combine(fixture.Root, "new downloads");
        await fixture.Service.ChangeDownloadDirectoryAsync(directory);
        await fixture.Service.CancelAsync(item.Id);
        await fixture.WaitAsync(item.Id, VodDownloadState.Canceled);
        fixture.Handler.Override = null;
        await fixture.Service.RetryAsync(item.Id, fixture.Options);
        item = await fixture.WaitAsync(item.Id, VodDownloadState.Completed);
        Assert.True(item.LocalMediaPath.StartsWith(fixture.Library + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ChangeDownloadDirectoryAsync("relative"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ChangeDownloadDirectoryAsync(@"\\server\videos"));
        var file = Path.Combine(fixture.Root, "not a folder");
        await File.WriteAllTextAsync(file, "keep");
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.ChangeDownloadDirectoryAsync(file));
        Assert.Equal(directory, fixture.Service.DownloadDirectory);
        Assert.Equal("keep", await File.ReadAllTextAsync(file));
        Assert.True(File.Exists(item.LocalMediaPath));
    }

    private static StreamTarget DownloadTarget(DownloadFixture fixture, string id) =>
        fixture.Target with { MediaId = id, Url = $"https://www.twitch.tv/videos/{id}" };
}
