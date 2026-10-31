using DownloadFixture = VodDownloadTestCatalog.DownloadFixture;

internal static partial class VodDownloadFormatTestCatalog
{
    private static async Task H264FillerReductionAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var sourceDirectory = Path.Combine(fixture.Root, "filler source");
        Directory.CreateDirectory(sourceDirectory);
        var playlistPath = Path.Combine(sourceDirectory, OfflineVodPackage.ManifestName);
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "h264-filler");
        File.Copy(Path.Combine(fixtureDirectory, OfflineVodPackage.ManifestName), playlistPath);
        File.Copy(Path.Combine(fixtureDirectory, "asset-000000.ts"), Path.Combine(sourceDirectory, "asset-000000.ts"));
        var content = await File.ReadAllTextAsync(playlistPath);
        var playlist = OfflineHlsPlaylist.Parse(content, new Uri("https://offline.invalid/index.m3u8"), requireLocalAssets: true);
        var source = await OfflineVodPackage.CreateAsync(sourceDirectory, playlist, CancellationToken.None);
        var ordinaryPath = Path.Combine(fixture.Root, "ordinary.mp4");
        var ordinary = await RunMediaValidationAsync([
            "-y", "-v", "error", "-protocol_whitelist", "file,crypto,data", "-allowed_extensions", "ALL",
            "-i", playlistPath, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-f", "mp4", ordinaryPath
        ]);
        Assert.Equal(0, ordinary.ExitCode);

        var outputDirectory = Path.Combine(fixture.Root, "optimized");
        await new FfmpegVodMediaConverter().ConvertAsync(sourceDirectory, source, outputDirectory,
            VodDownloadFormat.Mp4, fixture.Options.StreamlinkPath, CancellationToken.None);
        var optimizedPath = Path.Combine(outputDirectory, OfflineVodPackage.Mp4FileName);
        var ordinaryBytes = new FileInfo(ordinaryPath).Length;
        var optimizedBytes = new FileInfo(optimizedPath).Length;
        Assert.True(optimizedBytes < ordinaryBytes * 0.9,
            $"Filler removal did not save material space: {ordinaryBytes} -> {optimizedBytes} bytes.");
        Assert.Equal(await ReadRetainedH264HashAsync(playlistPath), await ReadRetainedH264HashAsync(optimizedPath));
        Assert.Equal(await ReadDecodedMediaHashesAsync(playlistPath), await ReadDecodedMediaHashesAsync(optimizedPath));
    }

    private static async Task NonH264Mp4ConversionAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var sourceDirectory = Path.Combine(fixture.Root, "hevc source");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, OfflineVodPackage.Mp4FileName);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hevc-video", OfflineVodPackage.Mp4FileName), path);
        var source = await OfflineVodPackage.CreateMp4Async(sourceDirectory, TimeSpan.FromSeconds(1), CancellationToken.None);
        var outputDirectory = Path.Combine(fixture.Root, "hevc output");
        await new FfmpegVodMediaConverter().ConvertAsync(sourceDirectory, source, outputDirectory,
            VodDownloadFormat.Mp4, fixture.Options.StreamlinkPath, CancellationToken.None);
        var outputPath = Path.Combine(outputDirectory, OfflineVodPackage.Mp4FileName);
        Assert.Equal(await ReadDecodedVideoHashAsync(path), await ReadDecodedVideoHashAsync(outputPath));
    }

    private static async Task<string> ReadRetainedH264HashAsync(string path)
    {
        List<string> arguments = ["-v", "error", "-protocol_whitelist", "file,crypto,data"];
        if (Path.GetExtension(path) == ".m3u8") arguments.AddRange(["-allowed_extensions", "ALL"]);
        arguments.AddRange(["-i", path, "-map", "0:v:0", "-c:v", "copy", "-bsf:v",
            "h264_mp4toannexb,filter_units=remove_types=12", "-f", "streamhash", "-hash", "sha256", "-"]);
        var result = await RunMediaValidationAsync(arguments);
        Assert.Equal(0, result.ExitCode);
        return result.Output;
    }

    private static async Task<string> ReadDecodedVideoHashAsync(string path)
    {
        var result = await RunMediaValidationAsync([
            "-v", "error", "-i", path, "-map", "0:v:0", "-c:v", "rawvideo", "-f", "streamhash", "-hash", "sha256", "-"
        ]);
        Assert.Equal(0, result.ExitCode);
        return result.Output;
    }

    private static async Task TransportContinuityWarningsAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        await UseRealMediaAsync(fixture);
        var original = await fixture.DownloadAsync();
        await using var playback = await fixture.Service.AcquireOfflinePlaybackAsync(original.Id);
        var expected = await ReadDecodedMediaHashesAsync(original.LocalMediaPath);

        // Reset transport counters in one segment without changing any compressed
        // video/audio payload. Twitch's muted segments also contain invalid counters
        // that mark otherwise usable PES packets as corrupt in FFmpeg's demuxer.
        var segmentPath = Path.Combine(Path.GetDirectoryName(original.LocalMediaPath)!, "asset-000001.ts");
        var segment = await File.ReadAllBytesAsync(segmentPath);
        Assert.Equal(0, segment.Length % 188);
        for (var offset = 0; offset < segment.Length; offset += 188)
        {
            Assert.Equal((byte)0x47, segment[offset]);
            segment[offset + 3] = (byte)((segment[offset + 3] & 0xF0) | ((segment[offset + 3] + 1) & 0x0F));
        }
        await File.WriteAllBytesAsync(segmentPath, segment);
        var strict = await RunMediaValidationAsync([
            "-v", "error", "-xerror", "-protocol_whitelist", "file,crypto,data", "-allowed_extensions", "ALL",
            "-i", original.LocalMediaPath, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-f", "null", "-"
        ]);
        Assert.True(strict.ExitCode != 0, "The regression source did not reproduce FFmpeg's strict demux failure.");
        Assert.Contains("corrupt input packet", strict.Error);

        var requests = fixture.Handler.RequestCount;
        fixture.Handler.NetworkAvailable = false;
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var converted = await WaitForFormatAsync(fixture.Service, original.Id, VodDownloadFormat.Mp4);
        Assert.Equal("", converted.Error);
        Assert.Equal(original.Duration, converted.Duration);
        Assert.Equal(expected, await ReadDecodedMediaHashesAsync(converted.LocalMediaPath));
        Assert.True(File.Exists(original.LocalMediaPath));
        Assert.SequenceEqual(segment, await File.ReadAllBytesAsync(segmentPath));
        Assert.Equal(requests, fixture.Handler.RequestCount);
        await playback.DisposeAsync();
        Assert.True(!Directory.Exists(Path.GetDirectoryName(original.LocalMediaPath)!));
    }

    private static async Task FailedMediaWriteAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        await UseRealMediaAsync(fixture);
        var original = await fixture.DownloadAsync();
        fixture.Handler.NetworkAvailable = false;
        var directory = Path.GetDirectoryName(original.LocalMediaPath)!;
        var source = await OfflineVodPackage.ValidateAsync(directory, CancellationToken.None);
        var output = Path.Combine(fixture.Root, "locked conversion");
        Directory.CreateDirectory(output);
        // Force a real FFmpeg output error while the complete source remains readable.
        await using var locked = new FileStream(Path.Combine(output, OfflineVodPackage.Mp4FileName),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new FfmpegVodMediaConverter().ConvertAsync(
            directory, source, output, VodDownloadFormat.Mp4, fixture.Options.StreamlinkPath, CancellationToken.None));
        Assert.Contains("FFmpeg could not convert", exception.Message);
        Assert.Equal(original, (await fixture.Service.GetDownloadsAsync()).Single());
        Assert.Equal(original.LocalMediaPath, (await fixture.Service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
        await DecodeMediaAsync(original.LocalMediaPath);
    }

    private static async Task IncompleteMediaConversionAsync()
    {
        RequireFfmpeg();
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        await UseRealMediaAsync(fixture);
        var playlist = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "replay-position-audio", "index.m3u8"));
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(playlist
            .Replace("#EXT-X-TARGETDURATION:10", "#EXT-X-TARGETDURATION:15", StringComparison.Ordinal)
            .Replace("#EXTINF:10.000000,", "#EXTINF:15.000000,", StringComparison.Ordinal) + "\n#EXT-X-ENDLIST\n"));
        var original = await fixture.DownloadAsync();
        Assert.Equal(TimeSpan.FromSeconds(90), original.Duration);
        fixture.Handler.NetworkAvailable = false;
        await fixture.Service.ChangeDownloadFormatAsync(VodDownloadFormat.Mp4, fixture.Options);
        var failed = await WaitForFormatAsync(fixture.Service, original.Id, VodDownloadFormat.Hls);
        Assert.Contains("does not contain the full VOD", failed.Error);
        await AssertOriginalKeptAsync(fixture, original, failed);
    }

    private static async Task AssertOriginalKeptAsync(DownloadFixture fixture, VodDownloadItem original, VodDownloadItem failed)
    {
        Assert.True(failed.CanRetryConversion);
        Assert.Equal(original.LocalMediaPath, (await fixture.Service.GetOfflineTargetAsync(original.Id)).LocalMediaPath);
        Assert.True(File.Exists(original.LocalMediaPath));
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, original.Id.ToString("N"), ".conversion")));
    }

    private static async Task<string> ReadDecodedMediaHashesAsync(string path)
    {
        List<string> arguments = ["-v", "error", "-xerror", "-err_detect", "explode", "-protocol_whitelist", "file,crypto,data"];
        if (Path.GetExtension(path) == ".m3u8") arguments.AddRange(["-allowed_extensions", "ALL"]);
        arguments.AddRange(["-i", path, "-map", "0:v:0", "-map", "0:a:0", "-c:v", "rawvideo", "-c:a", "pcm_s16le",
            "-fps_mode", "passthrough", "-f", "streamhash", "-hash", "sha256", "-"]);
        var result = await RunMediaValidationAsync(arguments);
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal("", result.Error);
        Assert.Equal(2, result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        return result.Output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunMediaValidationAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(ExecutableResolver.FindFfmpeg()!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-hide_banner", "-nostdin" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var owner = RedirectedProcessOwner.Start(start);
        var output = owner.StandardOutput.ReadToEndAsync();
        var error = owner.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await owner.Process.WaitForExitAsync(budget.Token);
        return (owner.Process.ExitCode, await output, await error);
    }
}
