using DownloadFixture = VodDownloadTestCatalog.DownloadFixture;

internal static partial class VodDownloadFormatTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD formats: existing Twitch downloads convert to MP4 and back without internet", () => RoundTripAsync(PlatformKind.Twitch)),
        ("VOD formats: existing Kick downloads convert to MP4 and back without internet", () => RoundTripAsync(PlatformKind.Kick)),
        ("VOD formats: new downloads finish as MP4 with real video and audio", NewMp4DownloadAsync),
        ("VOD formats: unused original media is removed immediately after conversion commits", UnusedConversionCleanupAsync),
        ("VOD formats: each media generation remains until its final playback owner closes", PlaybackGenerationsCleanupAsync),
        ("VOD formats: Windows path casing cannot bypass playback protection or obsolete-media cleanup", PlaybackFolderCaseCleanupAsync),
        ("VOD formats: locked originals retry cleanup while independent and read-only files are removed", LockedConversionCleanupAsync),
        ("VOD formats: locked failed conversion output is retried without losing the original", LockedFailedConversionCleanupAsync),
        ("VOD formats: temporary media locks do not discard a complete conversion during promotion", () => ConversionPromotionLockAsync(permanent: false)),
        ("VOD formats: persistent media locks preserve the original and retry cleanup", () => ConversionPromotionLockAsync(permanent: true)),
        ("VOD formats: playback copies can finish cleanup after service shutdown", PlaybackCleanupAfterShutdownAsync),
        ("VOD formats: damaged converted media preserves the original until a complete redownload commits", DamagedCurrentCleanupAsync),
        ("VOD formats: invalid conversion sources preserve older media and require a complete redownload", InvalidConversionSourceCleanupAsync),
        ("VOD formats: offline tab close releases its copy without leaking duplicate playback owners", () => PlaybackTabCleanupAsync(false)),
        ("VOD formats: delayed player disposal retains every original segment until teardown finishes", () => PlaybackTabCleanupAsync(true)),
        ("VOD formats: deleting a just-closed offline tab waits for its player to release the files", () => ClosedPlaybackActionAsync(false)),
        ("VOD formats: retrying a just-closed offline tab waits for its player to release the files", () => ClosedPlaybackActionAsync(true)),
        ("VOD formats: fragmented MP4 HLS downloads become a standalone MP4", FragmentedMp4DownloadAsync),
        ("VOD formats: H.264 filler is removed without changing coded pictures or decoded audio", H264FillerReductionAsync),
        ("VOD formats: non-H.264 video still converts to MP4", NonH264Mp4ConversionAsync),
        ("VOD formats: transport continuity warnings preserve every video frame and audio sample", TransportContinuityWarningsAsync),
        ("VOD formats: real FFmpeg write failures keep the original playable", FailedMediaWriteAsync),
        ("VOD formats: incomplete FFmpeg output keeps the original playable", IncompleteMediaConversionAsync),
        ("VOD formats: libraries from earlier versions still load and convert locally", LegacyLibraryAsync),
        ("VOD formats: metadata commit failures keep the original and remove uncommitted conversions", CommitFailureAsync),
        ("VOD formats: failed download commits preserve recovery files without stranding MP4 work", DownloadFormatCommitFailureAsync),
        ("VOD formats: conversion failures preserve playback and retry locally", FailureAndRetryAsync),
        ("VOD formats: canceling conversion preserves the complete original", CancelConversionAsync),
        ("VOD formats: shutdown during conversion recovers the original offline", InterruptedConversionAsync),
        ("VOD formats: the latest format selection supersedes an in-progress conversion", () => SupersededConversionAsync(false)),
        ("VOD formats: canceling a superseded conversion does not report a failure for the selected format", () => SupersededConversionAsync(true)),
        ("VOD formats: a format change during a download applies before completion", ChangeDuringDownloadAsync),
        ("VOD formats: canceling a queued conversion cannot cancel another VOD", CancelQueuedConversionAsync),
        ("VOD formats: conversions cover previous and current download folders", ConvertPreviousFoldersAsync),
        ("VOD formats: damaged MP4 files and escaping media folders cannot become playable", InvalidConvertedMediaAsync)
    ];

    private static async Task RoundTripAsync(PlatformKind platform)
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(platform);
        await UseRealMediaAsync(fixture);
        var original = await fixture.DownloadAsync();
        await using var playback = await fixture.Service.AcquireOfflinePlaybackAsync(original.Id);
        var profile = Path.Combine(Path.GetDirectoryName(original.LocalMediaPath)!, OfflineVodProfileImage.FileName);
        await File.WriteAllBytesAsync(profile, [1, 2, 3, 4]);
        var requests = fixture.Handler.RequestCount;
        fixture.Handler.NetworkAvailable = false;
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var mp4 = await WaitForFormatAsync(fixture.Service, original.Id, VodDownloadFormat.Mp4);
        Assert.Equal(original.Id, mp4.Id);
        Assert.Equal(original.Target.TabIdentityKey, mp4.Target.TabIdentityKey);
        Assert.Equal(original.Duration, mp4.Duration);
        Assert.True(mp4.LocalMediaPath.EndsWith(".mp4", StringComparison.Ordinal));
        Assert.True(File.Exists(original.LocalMediaPath), "An open playback tab lost its original files.");
        Assert.SequenceEqual(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(
            Path.Combine(Path.GetDirectoryName(mp4.LocalMediaPath)!, OfflineVodProfileImage.FileName)));
        Assert.Equal(mp4.LocalMediaPath, (await fixture.Service.GetOfflineTargetAsync(mp4.Id)).LocalMediaPath);
        await DecodeMediaAsync(mp4.LocalMediaPath);

        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Hls, fixture.Options);
        var hls = await WaitForFormatAsync(fixture.Service, original.Id, VodDownloadFormat.Hls);
        Assert.True(hls.LocalMediaPath.EndsWith(".m3u8", StringComparison.Ordinal));
        Assert.True(Math.Abs((hls.Duration - original.Duration).TotalSeconds) < 1);
        await DecodeMediaAsync(hls.LocalMediaPath);
        Assert.Equal(requests, fixture.Handler.RequestCount);
        await playback.DisposeAsync();
        Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
        Assert.True(!Directory.Exists(Path.GetDirectoryName(mp4.LocalMediaPath)!));
        await fixture.RestartAsync();
        var restored = (await fixture.Service.GetDownloadsAsync()).Single();
        Assert.Equal(hls.LocalMediaPath, restored.LocalMediaPath);
        Assert.Equal(VodDownloadFormat.Hls, restored.Format);
        Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
        Assert.True(!Directory.Exists(Path.GetDirectoryName(mp4.LocalMediaPath)!));
        Assert.True(File.Exists(restored.LocalMediaPath));
        await fixture.Service.GetOfflineTargetAsync(restored.Id);
    }

    private static async Task NewMp4DownloadAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        await UseRealMediaAsync(fixture);
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var item = await fixture.DownloadAsync();
        Assert.Equal(VodDownloadFormat.Mp4, item.Format);
        Assert.True(string.IsNullOrEmpty(item.Error), item.Error);
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, item.Id.ToString("N"), "media")),
            "A new MP4 download left its temporary HLS source on disk.");
        await DecodeMediaAsync(item.LocalMediaPath);
        fixture.Handler.NetworkAvailable = false;
        await fixture.RestartAsync();
        var restored = (await fixture.Service.GetDownloadsAsync()).Single();
        Assert.Equal(VodDownloadFormat.Mp4, restored.Format);
        Assert.Equal(item.LocalMediaPath, (await fixture.Service.GetOfflineTargetAsync(item.Id)).LocalMediaPath);
    }

    private static async Task FailureAndRetryAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var converter = new TestConverter { Fail = true };
        await using var service = CreateService(fixture, converter);
        fixture.Handler.NetworkAvailable = false;
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var failed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Hls);
        Assert.True(failed.CanRetryConversion);
        Assert.Contains("disk space", failed.Error);
        Assert.Equal(original.LocalMediaPath, (await service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, original.Id.ToString("N"), ".conversion")));
        converter.Fail = false;
        await service.RetryAsync(original.Id, new VodDownloadOptions("", []));
        var completed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
        Assert.True(!completed.CanRetryConversion);
        Assert.Equal("", completed.Error);
        Assert.Equal(2, converter.Calls);
    }

    private static async Task FragmentedMp4DownloadAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        await UseRealMediaAsync(fixture, "hover-preview-fmp4");
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var item = await fixture.DownloadAsync();
        Assert.Equal(VodDownloadFormat.Mp4, item.Format);
        await DecodeMediaAsync(item.LocalMediaPath, requireAudio: false);
    }

    private static async Task LegacyLibraryAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var record = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.Record(original.Id)))!.AsObject();
        foreach (var property in new[] { "Format", "RequestedFormat", "MediaFolder" }) record.Remove(property);
        await File.WriteAllTextAsync(fixture.Record(original.Id), record.ToJsonString());
        var packagePath = Path.Combine(Path.GetDirectoryName(original.LocalMediaPath)!, OfflineVodPackage.PackageName);
        var package = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(packagePath))!.AsObject();
        package.Remove("Format");
        await File.WriteAllTextAsync(packagePath, package.ToJsonString());
        await using var service = CreateService(fixture, new TestConverter());
        fixture.Handler.NetworkAvailable = false;
        var restored = (await service.GetDownloadsAsync()).Single();
        Assert.Equal(original.LocalMediaPath, restored.LocalMediaPath);
        Assert.Equal(VodDownloadFormat.Hls, restored.Format);
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
    }

    private static async Task CommitFailureAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var converter = new TestConverter { Block = true };
        var recordPath = fixture.Record(original.Id);
        string previousRecord;
        await using (var service = CreateService(fixture, converter))
        {
            await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
            await converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            previousRecord = await File.ReadAllTextAsync(recordPath);
            File.Delete(recordPath);
            Directory.CreateDirectory(recordPath);
            converter.Release.TrySetResult();
            var failed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Hls);
            Assert.True(failed.CanRetryConversion);
            Assert.Equal(original.LocalMediaPath, failed.LocalMediaPath);
            Assert.True(File.Exists(original.LocalMediaPath));
            Assert.Equal(1, Directory.GetDirectories(Path.GetDirectoryName(recordPath)!)
                .Count(path => Path.GetFileName(path) != "download.json"));
        }
        Directory.Delete(recordPath);
        await File.WriteAllTextAsync(recordPath, previousRecord);
        fixture.Handler.NetworkAvailable = false;
        await fixture.RestartAsync();
        await fixture.Service.GetOfflineTargetAsync(original.Id);
        Assert.Equal(1, Directory.GetDirectories(Path.GetDirectoryName(recordPath)!).Length);
    }

    private static async Task CancelConversionAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var converter = new TestConverter { Block = true };
        await using var service = CreateService(fixture, converter);
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        await converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(original.LocalMediaPath, (await service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
        await service.CancelAsync(original.Id);
        var canceled = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Hls);
        Assert.Contains("canceled", canceled.Error);
        Assert.True(canceled.CanRetryConversion);
        Assert.True(File.Exists(original.LocalMediaPath));
        await service.GetOfflineTargetAsync(original.Id);
    }

    private static async Task InterruptedConversionAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var converter = new TestConverter { Block = true };
        await using (var service = CreateService(fixture, converter))
        {
            await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
            await converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        fixture.Handler.NetworkAvailable = false;
        await fixture.RestartAsync();
        var restored = (await fixture.Service.GetDownloadsAsync()).Single();
        Assert.Equal(VodDownloadState.Completed, restored.State);
        Assert.Equal(original.LocalMediaPath, (await fixture.Service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
        Assert.Equal(VodDownloadFormat.Hls, restored.Format);
    }

    private static async Task SupersededConversionAsync(bool cancel)
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        var converter = new TestConverter { Block = true };
        await using var service = CreateService(fixture, converter);
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        await converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Hls, fixture.Options);
        if (cancel) await service.CancelAsync(original.Id);
        else converter.Release.TrySetResult();
        var completed = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Hls);
        Assert.Equal(original.LocalMediaPath, completed.LocalMediaPath);
        Assert.Equal("", completed.Error);
        Assert.True(completed.RequestedFormat is null);
        Assert.Equal(1, converter.Calls);
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, original.Id.ToString("N"), ".conversion")));
    }

    private static async Task ChangeDuringDownloadAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        await fixture.Service.DisposeAsync();
        await using var service = CreateService(fixture, new TestConverter());
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
        var item = await service.EnqueueAsync(new VodDownloadRequest(fixture.Target, "best", fixture.Options));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        release.TrySetResult();
        var completed = await WaitForFormatAsync(service, item.Id, VodDownloadFormat.Mp4);
        Assert.Equal(item.Id, completed.Id);
        Assert.Equal(3, fixture.Handler.RequestCount);
    }

    private static async Task CancelQueuedConversionAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var first = await fixture.DownloadAsync();
        var second = await fixture.EnqueueAsync(fixture.Target with { MediaId = "67890", Url = "https://www.twitch.tv/videos/67890" });
        second = await fixture.WaitAsync(second.Id, VodDownloadState.Completed);
        await fixture.Service.DisposeAsync();
        var converter = new TestConverter { Block = true };
        await using var service = CreateService(fixture, converter);
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        await converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var entries = await service.GetDownloadsAsync();
        var queued = entries.Single(item => item.State == VodDownloadState.ConversionQueued);
        var running = entries.Single(item => item.State == VodDownloadState.Converting);
        await service.CancelAsync(queued.Id);
        Assert.Equal(VodDownloadState.Completed, (await service.GetDownloadsAsync()).Single(item => item.Id == queued.Id).State);
        converter.Release.TrySetResult();
        var completed = await WaitForFormatAsync(service, running.Id, VodDownloadFormat.Mp4);
        await service.GetOfflineTargetAsync(queued.Id);
        Assert.Equal(1, converter.Calls);
        Assert.True(File.Exists(queued.LocalMediaPath) && File.Exists(completed.LocalMediaPath));
        Assert.True(!Directory.Exists(Path.GetDirectoryName((running.Id == first.Id ? first : second).LocalMediaPath)!));
    }

    private static async Task ConvertPreviousFoldersAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var first = await fixture.DownloadAsync();
        var newRoot = Path.Combine(fixture.Root, "another library 日本語");
        await fixture.Service.ChangeDownloadDirectoryAsync(newRoot);
        var second = await fixture.EnqueueAsync(fixture.Target with { MediaId = "67890", Url = "https://www.twitch.tv/videos/67890" });
        second = await fixture.WaitAsync(second.Id, VodDownloadState.Completed);
        await fixture.Service.DisposeAsync();
        await using var service = new VodDownloadService(newRoot, fixture.Resolver, fixture.Logger, null, fixture.Client,
            null, previousDownloadDirectories: [fixture.Library], mediaConverter: new TestConverter());
        fixture.Handler.NetworkAvailable = false;
        await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var convertedFirst = await WaitForFormatAsync(service, first.Id, VodDownloadFormat.Mp4);
        var convertedSecond = await WaitForFormatAsync(service, second.Id, VodDownloadFormat.Mp4);
        Assert.True(convertedFirst.LocalMediaPath.StartsWith(fixture.Library + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.True(convertedSecond.LocalMediaPath.StartsWith(newRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.True(!Directory.Exists(Path.GetDirectoryName(first.LocalMediaPath)!));
        Assert.True(!Directory.Exists(Path.GetDirectoryName(second.LocalMediaPath)!));
    }

    private static async Task InvalidConvertedMediaAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var original = await fixture.DownloadAsync();
        await fixture.Service.DisposeAsync();
        VodDownloadItem converted;
        await using (var service = CreateService(fixture, new TestConverter()))
        {
            await service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
            converted = await WaitForFormatAsync(service, original.Id, VodDownloadFormat.Mp4);
        }
        await File.WriteAllBytesAsync(converted.LocalMediaPath, [1, 2, 3]);
        await fixture.RestartAsync();
        Assert.Equal(VodDownloadState.Failed, (await fixture.Service.GetDownloadsAsync()).Single().State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GetOfflineTargetAsync(original.Id));
        await fixture.Service.DisposeAsync();
        var external = Path.Combine(fixture.Root, "keep.mp4");
        await File.WriteAllTextAsync(external, "Do not touch");
        var record = JsonSerializer.Deserialize<VodDownloadItem>(await File.ReadAllTextAsync(fixture.Record(original.Id)))!;
        await File.WriteAllTextAsync(fixture.Record(original.Id), JsonSerializer.Serialize(record with { MediaFolder = ".." }));
        await fixture.RestartAsync();
        Assert.Equal(0, (await fixture.Service.GetDownloadsAsync()).Count);
        Assert.Equal("Do not touch", await File.ReadAllTextAsync(external));
    }

    private static VodDownloadService CreateService(DownloadFixture fixture, IVodMediaConverter converter, TimeSpan? cleanupInterval = null) =>
        new(fixture.Library, fixture.Resolver, fixture.Logger, null, fixture.Client,
            new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })),
            mediaConverter: converter, cleanupInterval: cleanupInterval);

    private static async Task<VodDownloadItem> WaitForFormatAsync(IVodDownloadService service, Guid id, VodDownloadFormat format)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(12))
        {
            var result = (await service.GetDownloadsAsync()).Single(item => item.Id == id);
            if (!result.IsActive)
            {
                Assert.True(result.State == VodDownloadState.Completed && result.Format == format,
                    $"Expected completed {format}, got {result.State} {result.Format}: {result.Error}");
                return result;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("The VOD conversion did not finish.");
    }

    private static async Task UseRealMediaAsync(DownloadFixture fixture, string name = "replay-position-audio")
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var playlist = await File.ReadAllTextAsync(Path.Combine(directory, "index.m3u8"));
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(playlist + "\n#EXT-X-ENDLIST\n"));
        foreach (var path in Directory.GetFiles(directory).Where(path => Path.GetExtension(path) != ".m3u8"))
            fixture.Handler.Put("/redirected/" + Path.GetFileName(path), await File.ReadAllBytesAsync(path));
    }

    private static void RequireFfmpeg()
    {
        if (ExecutableResolver.FindFfmpeg() is null)
            throw new InteractiveDesktopTestSkippedException("FFmpeg is not installed for the real VOD conversion tests.");
    }

    private static async Task DecodeMediaAsync(string path, bool requireAudio = true)
    {
        var start = new ProcessStartInfo(ExecutableResolver.FindFfmpeg()!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-nostdin", "-v", "error", "-xerror", "-protocol_whitelist", "file,crypto,data",
            "-i", path, "-map", "0:v:0", "-map", requireAudio ? "0:a:0" : "0:a:0?", "-t", "1", "-f", "null", "-" }) start.ArgumentList.Add(argument);
        using var owner = RedirectedProcessOwner.Start(start);
        var error = owner.StandardError.ReadToEndAsync();
        var output = owner.StandardOutput.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.Process.WaitForExitAsync(budget.Token);
        await output;
        Assert.Equal(0, owner.Process.ExitCode);
        Assert.Equal("", await error);
    }

    private sealed class TestConverter : IVodMediaConverter
    {
        internal bool Fail { get; set; }
        internal bool Block { get; init; }
        internal int Calls { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OfflineVodPackage> ConvertAsync(string sourceDirectory, OfflineVodPackage source,
            string outputDirectory, VodDownloadFormat format, string streamlinkPath, CancellationToken cancellationToken)
        {
            Calls++;
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "unfinished"), "partial", cancellationToken);
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("Not enough disk space for conversion.");
            Assert.Equal(VodDownloadFormat.Mp4, format);
            var header = new byte[64];
            "ftyp"u8.CopyTo(header.AsSpan(4));
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, OfflineVodPackage.Mp4FileName), header, cancellationToken);
            return await OfflineVodPackage.CreateMp4Async(outputDirectory, source.Duration, cancellationToken);
        }
    }
}
