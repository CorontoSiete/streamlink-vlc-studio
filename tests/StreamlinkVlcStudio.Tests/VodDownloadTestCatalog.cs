using System.Net.Http.Headers;

internal static partial class VodDownloadTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD downloads: accept Twitch and both Kick VOD ID formats, never live streams or clips", UrlsAsync),
        ("VOD downloads: reject incomplete, missing, master, DRM, and unsafe media", InvalidPlaylistsAsync),
        ("VOD downloads: preserve sequence, discontinuities, and muted segment repair markers", PlaylistTimelineAsync),
        ("VOD downloads: never guess an implicit byte range on a different resource", AmbiguousRangesAsync),
        ("VOD downloads: download every Twitch segment and reopen the library without internet", () => CompletedAsync(PlatformKind.Twitch)),
        ("VOD downloads: download every Kick segment and reopen the library without internet", () => CompletedAsync(PlatformKind.Kick)),
        ("VOD downloads: materialize byte ranges, initialization data, and AES keys locally", RangesAndKeysAsync),
        ("VOD downloads: resolve relative media against the final redirected playlist URL", RedirectAsync),
        ("VOD downloads: reject unapproved segment hosts before fetching media", UnsafeHostAsync),
        ("VOD downloads: retry transient segment errors without skipping any segment", TransientAsync),
        ("VOD downloads: failed segments cannot produce a completed offline VOD", MissingSegmentAsync),
        ("VOD downloads: reject ignored range requests, error pages, and truncated media", InvalidResponsesAsync),
        ("VOD downloads: deduplicate queued VODs and preserve quality and authentication options", DuplicateAndOptionsAsync),
        ("VOD downloads: cancel active and queued work and delete partial data", CancelAsync),
        ("VOD downloads: retry Kick with a refreshed page URL and current credentials", RetryAsync),
        ("VOD downloads: shutdown interrupts work and restart never marks partial data complete", InterruptedAsync),
        ("VOD downloads: recover fully committed media after an interrupted metadata update", CommitRecoveryAsync),
        ("VOD downloads: missing or truncated completed media disables offline playback", DamagedMediaAsync),
        ("VOD downloads: tampered playlists and escaping inventories cannot access external resources", TamperedPackageAsync),
        ("VOD downloads: corrupt records are preserved rather than overwritten", CorruptRecordAsync),
        ("VOD downloads: deletion touches only the requested download directory", RemoveAsync),
        ("VOD downloads: offline Twitch startup, seeking, reload, and resume never call online services", () => OfflinePlaybackAsync(PlatformKind.Twitch)),
        ("VOD downloads: offline Kick startup, seeking, reload, and resume never call online services", () => OfflinePlaybackAsync(PlatformKind.Kick)),
        ("VOD downloads: offline tabs are separate from online tabs but share VOD history", IdentityAsync),
        ("VOD downloads: offline playback rejects network shares and missing files without resolving online", InvalidOfflineSourceAsync),
        ("VOD downloads: decode real Next.js channel metadata without guessing channel IDs", KickPageMetadataAsync),
        ("VOD downloads: current Kick VOD pages work with an older Streamlink Kick plugin", CurrentKickApiAsync),
        ("VOD downloads: reject mismatched Kick metadata and unsafe recording URLs", InvalidKickMetadataAsync),
        ("VOD downloads: fall back to the configured Kick plugin if the website format changes", KickPluginFallbackAsync),
        ("VOD downloads: repair muted Twitch transport packets before saving them locally", MutedDownloadAsync),
        ("VOD downloads: retry Kick HTML with curl when managed HTTP returns a challenge page", KickHtmlFallbackAsync),
        ("VOD downloads: playback, deletion, and retry actions cannot race on the same VOD", ExclusiveActionsAsync),
        ("VOD downloads: redirected or missing Videos folders cannot put the offline library on a network share", LocalLibraryFolderAsync),
        ("VOD downloads: download preferences persist independently and migrate the legacy quality", DownloadPreferencesAsync),
        ("VOD downloads: bandwidth limits are shared by concurrent media transfers", DownloadBandwidthAsync),
        ("VOD downloads: bandwidth changes and cancellation take effect during active transfers", DownloadBandwidthChangesAsync),
        ("VOD downloads: bandwidth reservations are cancelable and unlimited mode releases waiting readers", DownloadBandwidthReservationsAsync),
        ("VOD downloads: changing folders preserves completed, active, and queued downloads across restart", ChangeDownloadFolderAsync),
        ("VOD downloads: retries keep their original folder and invalid destinations preserve the library", DownloadFolderFailuresAsync),
        ("VOD downloads: failed resolutions cannot persist configured credentials or signed URL tokens", CredentialFailuresAsync),
        ("VOD downloads: Twitch subscriber-only fallback playlists still download all remote media locally", TwitchFallbackAsync),
        ("VOD downloads: local Twitch fallback playlists cannot reference local files or incomplete media", UnsafeTwitchFallbackAsync),
        ("VOD downloads: normal Streamlink resolution cannot authorize arbitrary local playlist reads", UntrustedLocalSourceAsync),
        ("VOD downloads: Kick metadata and storage errors cannot be hidden by plugin fallback", KickMetadataStorageFailureAsync),
        ("VOD downloads: delta playlists cannot become complete offline videos", DeltaPlaylistAsync),
        ("VOD downloads: validate channel IDs across every Flight record", KickFlightRecordsAsync),
        ("VOD downloads: encryption key names do not inherit muted segment markers", MutedKeyNameAsync),
        ("VOD downloads: shutdown releases every canceled and replaced queued job", ReplacedQueuedJobsAsync),
        .. ProfileImageTests
    ];

    private static Task UrlsAsync()
    {
        var twitch = VodDownloadUrlParser.Parse("twitch.tv/videos/12345?t=1h");
        Assert.True(twitch.IsExplicitTwitchVod);
        Assert.Equal("https://www.twitch.tv/videos/12345", twitch.Url);
        foreach (var id in new[] { "ec263e99-7ad7-47f9-b2e6-50c2f3c744e1", "01K62MXDAY68GRSY43VZX5KVAZ" })
        {
            var kick = VodDownloadUrlParser.Parse($"https://kick.com/streamer/videos/{id}");
            Assert.True(kick.IsExplicitKickVod);
            Assert.Equal(id, kick.MediaId);
        }
        foreach (var input in new[] { "https://twitch.tv/streamer", "https://kick.com/streamer", "https://clips.twitch.tv/Clip",
            "https://kick.com/streamer/clips/12345", "https://twitch.tv.evil.example/videos/12345",
            "https://kick.com.evil.example/streamer/videos/12345", "file:///videos/12345", "https://secret@twitch.tv/videos/12345",
            "https://kick.com:1234/streamer/videos/12345", "https://twitch.tv/videos/12345/extra", "https://kick.com/streamer/videos/.." })
            Assert.Throws<ArgumentException>(() => VodDownloadUrlParser.Parse(input));
        return Task.CompletedTask;
    }

    private static Task InvalidPlaylistsAsync()
    {
        var address = new Uri("https://stream.kick.com/index.m3u8");
        foreach (var playlist in new[] { "not a playlist", "#EXTM3U\n#EXT-X-ENDLIST\n",
            DownloadFixture.SimplePlaylist.Replace("#EXT-X-ENDLIST", ""),
            DownloadFixture.SimplePlaylist.Replace("#EXTINF:3,", "#EXTINF:NaN,"),
            DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "file:///C:/secret.ts"),
            DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "#EXT-X-GAP\nsegment-a.ts"),
            DownloadFixture.SimplePlaylist.Replace("#EXTINF:3,\nsegment-a.ts", "#EXTINF:3,\n#EXTINF:3,\nsegment-a.ts") })
            Assert.Throws<InvalidDataException>(() => OfflineHlsPlaylist.Parse(playlist, address));
        foreach (var tag in new[] { "#EXT-X-STREAM-INF:BANDWIDTH=100", "#EXT-X-I-FRAMES-ONLY",
            "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"key\"", "#EXT-X-KEY:METHOD=AES-128,KEYFORMAT=\"com.apple.streamingkeydelivery\",URI=\"key\"",
            "#EXT-X-DEFINE:NAME=\"segment\",VALUE=\"file\"", "#EXT-X-UNKNOWN:URI=\"resource\"" })
            Assert.Throws<NotSupportedException>(() => OfflineHlsPlaylist.Parse(
                DownloadFixture.SimplePlaylist.Replace("#EXT-X-TARGETDURATION:3", "#EXT-X-TARGETDURATION:3\n" + tag), address));
        return Task.CompletedTask;
    }

    private static Task PlaylistTimelineAsync()
    {
        var content = DownloadFixture.SimplePlaylist.Replace("#EXT-X-TARGETDURATION:3", "#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:42")
            .Replace("segment-a.ts", "segment-a-muted.ts").Replace("#EXTINF:3,\nsegment-b.ts", "#EXT-X-DISCONTINUITY\n#EXTINF:3,\nsegment-b.ts");
        var parsed = OfflineHlsPlaylist.Parse(content, new Uri("https://vod-secure.twitch.tv/vod/index.m3u8"));
        Assert.Equal(2, parsed.SegmentCount);
        Assert.Equal(TimeSpan.FromSeconds(6), parsed.Duration);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:42", parsed.Content);
        Assert.Contains("#EXT-X-DISCONTINUITY", parsed.Content);
        Assert.Contains("asset-000000-muted.ts", parsed.Content);
        Assert.DoesNotContain("https://", parsed.Content);
        return Task.CompletedTask;
    }

    private static Task AmbiguousRangesAsync()
    {
        var playlist = DownloadFixture.SimplePlaylist.Replace("#EXTINF:3,\nsegment-a.ts", "#EXTINF:3,\n#EXT-X-BYTERANGE:4@0\nsegment-a.ts")
            .Replace("#EXTINF:3,\nsegment-b.ts", "#EXTINF:3,\n#EXT-X-BYTERANGE:4\nsegment-b.ts");
        Assert.Throws<InvalidDataException>(() => OfflineHlsPlaylist.Parse(playlist, new Uri("https://stream.kick.com/index.m3u8")));
        return Task.CompletedTask;
    }

    private static async Task CompletedAsync(PlatformKind platform)
    {
        await using var fixture = new DownloadFixture(platform);
        var item = await fixture.DownloadAsync();
        Assert.Equal(VodDownloadState.Completed, item.State);
        Assert.Equal(2, item.TotalSegments);
        Assert.Equal(item.TotalSegments, item.CompletedSegments);
        Assert.Equal(20L, item.BytesDownloaded);
        Assert.Equal(TimeSpan.FromSeconds(6), item.Duration);
        var offline = await fixture.Service.GetOfflineTargetAsync(item.Id);
        Assert.True(offline.IsOfflineVod);
        Assert.Equal(item.Target.MediaId, offline.MediaId);
        Assert.True(File.Exists(offline.LocalMediaPath));
        var manifest = await File.ReadAllTextAsync(offline.LocalMediaPath);
        Assert.DoesNotContain("https://", manifest);
        Assert.DoesNotContain("segment-a.ts", manifest);
        fixture.Handler.NetworkAvailable = false;
        var requestCount = fixture.Handler.RequestCount;
        await fixture.RestartAsync();
        var restored = (await fixture.Service.GetDownloadsAsync()).Single();
        Assert.Equal(VodDownloadState.Completed, restored.State);
        Assert.True((await fixture.Service.GetOfflineTargetAsync(restored.Id)).IsOfflineVod);
        Assert.Equal(requestCount, fixture.Handler.RequestCount);
        Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
    }

    private static async Task RangesAndKeysAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:42\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin?version=1\",IV=0x0000000000000000000000000000002a\n#EXT-X-KEY:METHOD=NONE\n" +
            "#EXT-X-MAP:URI=\"combined.mp4\",BYTERANGE=\"4@0\"\n" +
            "#EXTINF:3,\n#EXT-X-BYTERANGE:5@4\ncombined.mp4\n#EXTINF:3,\n#EXT-X-BYTERANGE:5\ncombined.mp4\n#EXT-X-ENDLIST\n"));
        fixture.Handler.Put("/redirected/key.bin", Enumerable.Range(0, 16).Select(value => (byte)value).ToArray());
        fixture.Handler.Put("/redirected/combined.mp4", Enumerable.Range(0, 14).Select(value => (byte)value).ToArray());
        var item = await fixture.DownloadAsync();
        Assert.Equal(30L, item.BytesDownloaded);
        var directory = Path.GetDirectoryName(item.LocalMediaPath)!;
        Assert.Equal(16L, new FileInfo(Path.Combine(directory, "asset-000000.key")).Length);
        Assert.Equal(4L, new FileInfo(Path.Combine(directory, "asset-000001.mp4")).Length);
        Assert.True((await File.ReadAllBytesAsync(Path.Combine(directory, "asset-000002.mp4"))).SequenceEqual(new byte[] { 4, 5, 6, 7, 8 }));
        Assert.True((await File.ReadAllBytesAsync(Path.Combine(directory, "asset-000003.mp4"))).SequenceEqual(new byte[] { 9, 10, 11, 12, 13 }));
        Assert.DoesNotContain("BYTERANGE", await File.ReadAllTextAsync(item.LocalMediaPath));
        Assert.True(fixture.Handler.Ranges.Contains("bytes=0-3"));
        Assert.True(fixture.Handler.Ranges.Contains("bytes=4-8"));
        Assert.True(fixture.Handler.Ranges.Contains("bytes=9-13"));
        await fixture.Service.GetOfflineTargetAsync(item.Id);
    }

    private static async Task RedirectAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        fixture.Resolver.ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(new Uri("https://stream.kick.com/before.m3u8"), "Fixture"));
        fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/before.m3u8"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/redirected/index.m3u8", UriKind.Relative) }, RequestMessage = request }
            : null);
        await fixture.DownloadAsync();
        Assert.True(fixture.Handler.Addresses.Any(address => address.EndsWith("/redirected/segment-a.ts", StringComparison.Ordinal)));
    }

    private static async Task UnsafeHostAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "https://evil.example/segment.ts")));
        var item = await fixture.EnqueueAsync();
        var failed = await fixture.WaitAsync(item.Id, VodDownloadState.Failed);
        Assert.Contains("approved", failed.Error);
        Assert.Equal(1, fixture.Handler.RequestCount);
    }

    private static async Task TransientAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var attempts = 0;
        fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("segment-a.ts", StringComparison.Ordinal) && Interlocked.Increment(ref attempts) <= 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request } : null);
        var item = await fixture.DownloadAsync();
        Assert.Equal(3, attempts);
        Assert.Equal(20L, item.BytesDownloaded);
    }

    private static async Task MissingSegmentAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("segment-b.ts", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request } : null);
        var item = await fixture.EnqueueAsync();
        await fixture.WaitAsync(item.Id, VodDownloadState.Failed);
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, item.Id.ToString("N"), "media")));
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, item.Id.ToString("N"), ".partial")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GetOfflineTargetAsync(item.Id));
    }

    private static async Task InvalidResponsesAsync()
    {
        foreach (var invalid in new[] { "range", "html", "truncated", "key" })
        {
            await using var fixture = new DownloadFixture(PlatformKind.Kick);
            if (invalid == "range") fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(
                DownloadFixture.SimplePlaylist.Replace("#EXTINF:3,\nsegment-a.ts", "#EXTINF:3,\n#EXT-X-BYTERANGE:4@0\nsegment-a.ts")));
            if (invalid == "key")
            {
                fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(DownloadFixture.SimplePlaylist.Replace("#EXT-X-TARGETDURATION:3",
                    "#EXT-X-TARGETDURATION:3\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\"")));
                fixture.Handler.Put("/redirected/key", new byte[15]);
            }
            fixture.Handler.Override = (request, _) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("segment-a.ts", StringComparison.Ordinal) || invalid == "key")
                    return Task.FromResult<HttpResponseMessage?>(null);
                var response = DownloadHttpHandler.Reply(request, new byte[8], invalid == "html" ? "text/html" : "application/octet-stream");
                if (invalid == "truncated") response.Content.Headers.ContentLength = 9;
                return Task.FromResult<HttpResponseMessage?>(response);
            };
            var item = await fixture.EnqueueAsync();
            await fixture.WaitAsync(item.Id, VodDownloadState.Failed);
            Assert.True(!Directory.Exists(Path.Combine(fixture.Library, item.Id.ToString("N"), "media")));
        }
    }

    private static async Task DuplicateAndOptionsAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Resolver.ResolveStreamUrlOverride = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new StreamlinkResolvedUrl(fixture.PlaylistUri, "Fixture");
        };
        var arguments = new List<string> { "--twitch-api-header", "Authorization=OAuth-private-fixture-token" };
        var request = new VodDownloadRequest(fixture.Target, "720p", new VodDownloadOptions("fixture-streamlink.exe", arguments));
        var first = await fixture.Service.EnqueueAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await fixture.Service.EnqueueAsync(request);
        Assert.Equal(first.Id, duplicate.Id);
        arguments.Clear();
        var resolved = fixture.Resolver.ResolveStreamUrlRequests.Single();
        Assert.Equal("720p", resolved.Quality);
        Assert.True(!resolved.LowLatency);
        Assert.Equal(2, resolved.CustomArguments.Count);
        Assert.DoesNotContain("private-fixture-token", await File.ReadAllTextAsync(fixture.Record(first.Id)));
        release.TrySetResult();
        await fixture.WaitAsync(first.Id, VodDownloadState.Completed);
        Assert.Equal(first.Id, (await fixture.Service.EnqueueAsync(request)).Id);
    }

    private static async Task CancelAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var entered = fixture.BlockSegment();
        var active = await fixture.EnqueueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = await fixture.EnqueueAsync(fixture.Target with { MediaId = "99999", Url = "https://www.twitch.tv/videos/99999" });
        await fixture.Service.CancelAsync(queued.Id);
        Assert.Equal(VodDownloadState.Canceled, (await fixture.Service.GetDownloadsAsync()).Single(item => item.Id == queued.Id).State);
        await fixture.Service.CancelAsync(active.Id);
        await fixture.WaitAsync(active.Id, VodDownloadState.Canceled);
        Assert.True(!Directory.Exists(Path.Combine(fixture.Library, active.Id.ToString("N"), ".partial")));
        Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
    }

    private static async Task RetryAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        fixture.Handler.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("segment-a.ts", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request } : null);
        var item = await fixture.EnqueueAsync(fixture.Target with { Url = "https://stream.kick.com/stale/index.m3u8?expired=true" });
        await fixture.WaitAsync(item.Id, VodDownloadState.Failed);
        fixture.Handler.Override = null;
        await fixture.Service.RetryAsync(item.Id, new VodDownloadOptions("new-streamlink.exe", ["--http-header", "X-Fixture=current"]));
        await fixture.WaitAsync(item.Id, VodDownloadState.Completed);
        var retried = fixture.Resolver.ResolveStreamUrlRequests.Last();
        Assert.Equal("https://kick.com/streamer/videos/01K62MXDAY68GRSY43VZX5KVAZ", retried.Target.Url);
        Assert.Equal("new-streamlink.exe", retried.StreamlinkPath);
        Assert.Equal("X-Fixture=current", retried.CustomArguments.Last());
    }

    private static async Task InterruptedAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var entered = fixture.BlockSegment();
        var item = await fixture.EnqueueAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.RestartAsync();
        var interrupted = (await fixture.Service.GetDownloadsAsync()).Single();
        Assert.Equal(VodDownloadState.Interrupted, interrupted.State);
        Assert.Equal("", interrupted.LocalMediaPath);
        fixture.Handler.Override = null;
        await fixture.Service.RetryAsync(item.Id, fixture.Options);
        await fixture.WaitAsync(item.Id, VodDownloadState.Completed);
    }

    private static async Task CommitRecoveryAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var item = await fixture.DownloadAsync();
        var stored = JsonSerializer.Deserialize<VodDownloadItem>(await File.ReadAllTextAsync(fixture.Record(item.Id)))!;
        await File.WriteAllTextAsync(fixture.Record(item.Id), JsonSerializer.Serialize(stored with { State = VodDownloadState.Downloading }));
        fixture.Handler.NetworkAvailable = false;
        await fixture.RestartAsync();
        Assert.Equal(VodDownloadState.Completed, (await fixture.Service.GetDownloadsAsync()).Single().State);
    }

    private static async Task DamagedMediaAsync()
    {
        foreach (var missing in new[] { true, false })
        {
            await using var fixture = new DownloadFixture(PlatformKind.Kick);
            var item = await fixture.DownloadAsync();
            var segment = Path.Combine(Path.GetDirectoryName(item.LocalMediaPath)!, "asset-000000.ts");
            if (missing) File.Delete(segment);
            else await File.WriteAllBytesAsync(segment, new byte[1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GetOfflineTargetAsync(item.Id));
            await fixture.RestartAsync();
            Assert.Equal(VodDownloadState.Failed, (await fixture.Service.GetDownloadsAsync()).Single().State);
        }
    }

    private static async Task TamperedPackageAsync()
    {
        foreach (var escaping in new[] { true, false })
        {
            await using var fixture = new DownloadFixture(PlatformKind.Kick);
            var item = await fixture.DownloadAsync();
            var packagePath = Path.Combine(Path.GetDirectoryName(item.LocalMediaPath)!, OfflineVodPackage.PackageName);
            var package = JsonSerializer.Deserialize<OfflineVodPackage>(await File.ReadAllTextAsync(packagePath))!;
            if (escaping) package = package with { Files = [new OfflineVodFile("../secret.ts", 8), package.Files[1]] };
            else
            {
                var manifest = Encoding.UTF8.GetBytes((await File.ReadAllTextAsync(item.LocalMediaPath)).Replace("asset-000000.ts", "https://stream.kick.com/asset-000000.ts"));
                await File.WriteAllBytesAsync(item.LocalMediaPath, manifest);
                package = package with { ManifestHash = Convert.ToHexString(SHA256.HashData(manifest)) };
            }
            await File.WriteAllTextAsync(packagePath, JsonSerializer.Serialize(package));
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GetOfflineTargetAsync(item.Id));
        }
    }

    private static async Task CorruptRecordAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var item = await fixture.DownloadAsync();
        await File.WriteAllTextAsync(fixture.Record(item.Id), "broken json");
        await fixture.RestartAsync();
        Assert.Equal(0, (await fixture.Service.GetDownloadsAsync()).Count);
        Assert.Equal("broken json", await File.ReadAllTextAsync(fixture.Record(item.Id)));
        Assert.True(File.Exists(item.LocalMediaPath));
    }

    private static async Task RemoveAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var first = await fixture.DownloadAsync();
        var second = await fixture.EnqueueAsync(fixture.Target with { MediaId = "different-id", Url = "https://kick.com/streamer/videos/different-id" });
        second = await fixture.WaitAsync(second.Id, VodDownloadState.Completed);
        var unrelated = Path.Combine(fixture.Library, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        await fixture.Service.RemoveAsync(first.Id);
        Assert.True(!File.Exists(first.LocalMediaPath));
        Assert.True(File.Exists(second.LocalMediaPath));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(second.Id, (await fixture.Service.GetDownloadsAsync()).Single().Id);
    }

    private static async Task OfflinePlaybackAsync(PlatformKind platform)
    {
        await using var fixture = new DownloadFixture(platform);
        var item = await fixture.DownloadAsync();
        var target = await fixture.Service.GetOfflineTargetAsync(item.Id);
        fixture.Handler.NetworkAvailable = false;
        fixture.Resolver.ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("Internet is disabled.");
        using var historyFiles = new VodResumeTestCatalog.HistoryFiles();
        var history = historyFiles.Create();
        await history.GetAsync(item.Target);
        history.Remember(item.Target, new VodPlaybackBookmark(TimeSpan.FromSeconds(1), item.Duration, DateTimeOffset.UtcNow));
        await history.SaveAsync();
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine { Duration = item.Duration });
        var chat = new DeniedVodChat();
        await using var tab = new StreamTabViewModel(new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = item.Quality,
            StreamlinkService = fixture.Resolver,
            PlaybackFactory = factory,
            ChatFactory = new DeniedChatFactory(),
            Logger = new MemoryLogger(),
            Dispatch = action => action(),
            VodPlaybackHistory = history,
            VodChatProvider = chat
        });
        var settings = VodResumeTestCatalog.Settings();
        settings.StreamlinkPath = null;
        settings.CustomStreamlinkArguments = "\"unfinished argument";
        settings.Chat.ConnectAutomatically = true;
        settings.Chat.Layout = ChatLayout.Overlay;
        tab.SetVideoHandle(new IntPtr(42));
        await tab.StartAsync(settings);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.True(factory.Engine!.LastPlayedUri!.IsFile);
        Assert.Equal(TimeSpan.FromSeconds(1), factory.Engine.LastStartPosition!.Value);
        Assert.True(tab.IsReplaySeekEnabled);
        Assert.True(!factory.LastEnableNativeOverlay!.Value);
        await tab.SeekReplayAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), factory.Engine.Position);
        tab.IsChatVisible = true;
        await tab.RestartChatAsync(settings);
        await tab.StopAsync();
        await tab.StartAsync(settings);
        Assert.Equal(PlaybackStatus.Playing, tab.Status);
        Assert.True(factory.Engines.SelectMany(engine => engine.PlayedUris).All(uri => uri.IsFile));
        Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
        Assert.Equal(0, fixture.Resolver.StartCount);
        Assert.Equal(0, chat.Calls);
    }

    private static async Task IdentityAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var item = await fixture.DownloadAsync();
        var offline = item.OfflineTarget;
        Assert.True(item.Target.TabIdentityKey != offline.TabIdentityKey);
        using var files = new VodResumeTestCatalog.HistoryFiles();
        var history = files.Create();
        await history.GetAsync(item.Target);
        var bookmark = new VodPlaybackBookmark(TimeSpan.FromSeconds(2), item.Duration, DateTimeOffset.UtcNow);
        history.Remember(offline, bookmark);
        Assert.Equal(bookmark, (await history.GetAsync(item.Target))!);
    }

    private static async Task InvalidOfflineSourceAsync()
    {
        foreach (var source in new[] { "https://stream.kick.com/index.m3u8", @"\\unavailable-server\vods\index.m3u8", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".m3u8") })
        {
            var streamlink = new FakeStreamlinkService { ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("Must not resolve online.") };
            var target = VodResumeTestCatalog.Target() with { LocalMediaPath = source };
            using var files = new VodResumeTestCatalog.HistoryFiles();
            await using var tab = VodResumeTestCatalog.Tab(target, files.Create(), new FakePlaybackEngineFactory(), streamlink: streamlink);
            tab.SetVideoHandle(new IntPtr(42));
            await tab.StartAsync(VodResumeTestCatalog.Settings());
            Assert.True(tab.Status != PlaybackStatus.Playing);
            Assert.True(!string.IsNullOrWhiteSpace(tab.ErrorMessage));
            Assert.Equal(0, streamlink.ResolveStreamUrlCount);
        }
    }

    private static Task KickPageMetadataAsync()
    {
        var record = "2a:{\"channel\":{\"subscriber_badges\":[{\"channel_id\":668},{\"channel_id\":668}],\"title\":\"Unicode 🎲 and \\\"quotes\\\"\"}}\n";
        var split = record.Length / 2;
        var html = "<script>var unrelated={channel_id:999};</script>" + NextScript(":HL[\"https://assets.kick.com/style.css\",\"style\"]\n1:I[4,[],\"component\"]\n" + record[..split]) + NextScript(record[split..]);
        Assert.Equal(668L, KickVodDownloadResolver.ReadChannelId(html));
        var text = "Text frame\nUnicode 🎲 and channel_id:999";
        Assert.Equal(668L, KickVodDownloadResolver.ReadChannelId(NextScript($"ff:T{Encoding.UTF8.GetByteCount(text):x},{text}" + record)));
        var binary = Encoding.UTF8.GetBytes("ff:O2,").Concat(new byte[] { 0, 255 }).Concat(Encoding.UTF8.GetBytes(record)).ToArray();
        Assert.Equal(668L, KickVodDownloadResolver.ReadChannelId($"<script>self.__next_f.push({JsonSerializer.Serialize(new object[] { 3, Convert.ToBase64String(binary) })})</script>"));
        foreach (var invalid in new[] { "<html>channel_id:123</html>", NextScript("1:{\"channel_id\":0}\n"),
            NextScript("1:{\"channel_id\":\"668\"}\n"), NextScript("1:{\"channel_id\":668,\"other\":{\"channel_id\":12}}\n"),
            NextScript("1:{\"channel_id\":668\n"), NextScript("1:{\"title\":\"channel_id:668\"}\n"),
            NextScript("ff:Tffff,short" + record) })
            Assert.Throws<InvalidDataException>(() => KickVodDownloadResolver.ReadChannelId(invalid));
        return Task.CompletedTask;
    }

    private static string NextScript(string payload) => $"<script>self.__next_f.push({JsonSerializer.Serialize(new object[] { 1, payload })})</script>";

    private static Task LocalLibraryFolderAsync()
    {
        var local = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(Path.Combine(local, "StreamStudio VODs"), VodDownloadService.GetDefaultDownloadDirectory(["", @"\\server\videos", "relative", local]));
        Assert.Throws<DirectoryNotFoundException>(() => VodDownloadService.GetDefaultDownloadDirectory(["", @"\\server\videos", "relative"]));
        Assert.Throws<ArgumentException>(() => new VodDownloadService(@"\\server\videos", new FakeStreamlinkService(), new MemoryLogger()));
        return Task.CompletedTask;
    }

    private static string KickMetadata(DownloadFixture fixture, string? source = null, string? id = null, string slug = "streamer", long channelId = 668) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                id = id ?? fixture.Target.MediaId,
                title = "A downloaded Kick VOD",
                recording_url = source ?? fixture.PlaylistUri.AbsoluteUri,
                channel = new { id = channelId, slug, username = slug },
                category = new { name = "Just Chatting" }
            }
        });

    private static async Task CredentialFailuresAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var options = new VodDownloadOptions("fixture-streamlink.exe", ["--http-header", "Cookie=session-private-123456",
            "--twitch-api-header=Authorization=OAuth access-private-123456"]);
        fixture.Resolver.ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException(
            "Streamlink rejected Cookie=session-private-123456 Authorization=OAuth access-private-123456 and https://vod-secure.twitch.tv/vod.m3u8?token=cdn-private-123456");
        var queued = await fixture.Service.EnqueueAsync(new VodDownloadRequest(fixture.Target, "best", options));
        var item = await fixture.WaitAsync(queued.Id, VodDownloadState.Failed);
        var stored = await File.ReadAllTextAsync(fixture.Record(item.Id));
        foreach (var secret in new[] { "session-private-123456", "access-private-123456", "cdn-private-123456" })
        {
            Assert.DoesNotContain(secret, item.Error);
            Assert.DoesNotContain(secret, stored);
        }
        Assert.Contains("Streamlink rejected", item.Error);
    }

    private static async Task<KickVodDownloadResolver> ConfigureKickAsync(DownloadFixture fixture)
    {
        fixture.Handler.Put(new Uri(fixture.Target.Url).AbsolutePath, Encoding.UTF8.GetBytes(NextScript("1:{\"channel\":{\"subscriber_badges\":[{\"channel_id\":668}]}}\n")));
        fixture.Handler.Put($"/api/v1/channels/668/videos/{fixture.Target.MediaId}", Encoding.UTF8.GetBytes(KickMetadata(fixture)));
        var resolver = new KickVodDownloadResolver(new MemoryLogger(), fixture.Client, (_, _, _) => Task.FromResult<string?>(null));
        fixture.KickResolver = resolver.ResolveAsync;
        await fixture.RestartAsync();
        return resolver;
    }

    private static async Task CurrentKickApiAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        await ConfigureKickAsync(fixture);
        fixture.Resolver.ResolveStreamUrlOverride = (request, _) =>
        {
            if (!request.Target.Url.EndsWith(".m3u8", StringComparison.Ordinal))
                throw new InvalidOperationException("The old installed Kick plugin uses a removed API endpoint.");
            Assert.Equal("best", request.Quality);
            return Task.FromResult(new StreamlinkResolvedUrl(fixture.PlaylistUri, "Direct HLS"));
        };
        var item = await fixture.DownloadAsync();
        Assert.Equal("A downloaded Kick VOD", item.Target.DisplayTitle);
        Assert.Equal("668", item.Target.BroadcasterId);
        Assert.Equal(fixture.Target.Url, item.Target.Url);
        Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
        Assert.True(fixture.Handler.Addresses.Contains($"https://web.kick.com/api/v1/channels/668/videos/{fixture.Target.MediaId}"));
        Assert.DoesNotContain(fixture.PlaylistUri.AbsoluteUri, await File.ReadAllTextAsync(fixture.Record(item.Id)));
        var offline = await fixture.Service.GetOfflineTargetAsync(item.Id);
        Assert.True(offline.IsOfflineVod);
        Assert.True(File.Exists(offline.LocalMediaPath));
    }

    private static async Task InvalidKickMetadataAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        var resolver = await ConfigureKickAsync(fixture);
        foreach (var json in new[] { "{\"data\":null}", "[]", KickMetadata(fixture, id: "another-video"),
            KickMetadata(fixture, slug: "another-channel"), KickMetadata(fixture, channelId: 111),
            KickMetadata(fixture, source: "https://kick.com.evil.example/index.m3u8"),
            KickMetadata(fixture, source: "http://stream.kick.com/index.m3u8"),
            KickMetadata(fixture, source: "https://stream.kick.com/recording.mp4"),
            KickMetadata(fixture, source: "https://secret@stream.kick.com/index.m3u8") })
        {
            fixture.Handler.Put($"/api/v1/channels/668/videos/{fixture.Target.MediaId}", Encoding.UTF8.GetBytes(json));
            await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync(fixture.Target, CancellationToken.None));
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => resolver.ResolveAsync(fixture.Target, cancellation.Token));
    }

    private static async Task KickPluginFallbackAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        fixture.KickResolver = (_, _) => throw new InvalidDataException("The public website changed its format.");
        await fixture.RestartAsync();
        var item = await fixture.DownloadAsync();
        Assert.Equal(VodDownloadState.Completed, item.State);
        Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
        Assert.Equal(fixture.Target.Url, fixture.Resolver.ResolveStreamUrlRequests.Single().Target.Url);
    }

    private static async Task KickMetadataStorageFailureAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        fixture.KickResolver = (target, _) => Task.FromResult(target with { Url = fixture.PlaylistUri.AbsoluteUri, DisplayTitle = new string('a', 5000) });
        await fixture.RestartAsync();
        var item = await fixture.WaitAsync((await fixture.EnqueueAsync()).Id, VodDownloadState.Failed);
        Assert.Equal(1, fixture.Resolver.ResolveStreamUrlCount);
        Assert.Equal(0, fixture.Handler.RequestCount);
        Assert.Contains("metadata", item.Error);
    }

    private static async Task KickHtmlFallbackAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Kick);
        await ConfigureKickAsync(fixture);
        fixture.Handler.Put(new Uri(fixture.Target.Url).AbsolutePath, Encoding.UTF8.GetBytes("<html>HTTP 200 challenge page</html>"));
        var fallbackCalls = 0;
        var resolver = new KickVodDownloadResolver(new MemoryLogger(), fixture.Client, (url, referrer, _) =>
        {
            Assert.Equal(fixture.Target.Url, url);
            Assert.Equal(fixture.Target.Url, referrer);
            fallbackCalls++;
            return Task.FromResult<string?>(NextScript("1:{\"channel_id\":668}\n"));
        });
        var target = await resolver.ResolveAsync(fixture.Target, CancellationToken.None);
        Assert.Equal(1, fallbackCalls);
        Assert.Equal(fixture.PlaylistUri.AbsoluteUri, target.Url);
    }

    private static async Task ExclusiveActionsAsync()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = 0;
        var deleted = 0;
        var item = new VodDownloadItem(Guid.NewGuid(), VodDownloadUrlParser.Parse("https://twitch.tv/videos/12345"), "best",
            DateTimeOffset.UtcNow, VodDownloadState.Completed, LocalMediaPath: Path.Combine(Path.GetTempPath(), "vod.m3u8"));
        var card = new VodDownloadViewModel(item, (execute, canExecute) => new AsyncRelayCommand(execute, canExecute),
            async _ => { played++; await block.Task; }, _ => Task.CompletedTask, _ => Task.CompletedTask,
            _ => { deleted++; return Task.CompletedTask; });
        var playing = card.PlayCommand.ExecuteAsync();
        Assert.Equal(1, played);
        Assert.True(!card.DeleteCommand.CanExecute(null));
        await card.DeleteCommand.ExecuteAsync();
        Assert.Equal(0, deleted);
        block.SetResult();
        await playing;
        Assert.True(card.DeleteCommand.CanExecute(null));
        await card.DeleteCommand.ExecuteAsync();
        Assert.Equal(1, deleted);
    }

    private static async Task ConfigureTwitchFallbackAsync(DownloadFixture fixture, string playlist)
    {
        var path = Path.Combine(fixture.Root, "subscriber fallback.m3u8");
        await File.WriteAllTextAsync(path, playlist);
        fixture.Resolver.ResolveStreamUrlOverride = (_, _) => throw new InvalidOperationException("Twitch resolution failed.");
        fixture.TwitchFallback = new FakeTwitchSubOnlyVodResolver
        {
            Override = (request, _) =>
            {
                Assert.Equal(fixture.Target.MediaId, request.VodId);
                Assert.Equal("best", request.Quality);
                return Task.FromResult(new TwitchSubOnlyVodResolution(new Uri(path), "chunked", "Resolved.", TimeSpan.FromHours(2)));
            }
        };
        await fixture.RestartAsync();
    }

    private static async Task TwitchFallbackAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var content = DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "https://vod-secure.twitch.tv/redirected/segment-a.ts")
            .Replace("segment-b.ts", "https://vod-secure.twitch.tv/redirected/segment-b.ts");
        await ConfigureTwitchFallbackAsync(fixture, content);
        var item = await fixture.DownloadAsync();
        Assert.Equal(TimeSpan.FromSeconds(6), item.Duration);
        Assert.Equal(2, item.CompletedSegments);
        Assert.Equal(2, fixture.Handler.RequestCount);
        Assert.DoesNotContain("https://", await File.ReadAllTextAsync(item.LocalMediaPath));
        fixture.Handler.NetworkAvailable = false;
        Assert.True((await fixture.Service.GetOfflineTargetAsync(item.Id)).IsOfflineVod);
    }

    private static async Task UnsafeTwitchFallbackAsync()
    {
        foreach (var content in new[] { DownloadFixture.SimplePlaylist.Replace("#EXT-X-ENDLIST", ""),
            DownloadFixture.SimplePlaylist, DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "https://evil.example/segment.ts") })
        {
            await using var fixture = new DownloadFixture(PlatformKind.Twitch);
            await ConfigureTwitchFallbackAsync(fixture, content);
            var item = await fixture.WaitAsync((await fixture.EnqueueAsync()).Id, VodDownloadState.Failed);
            Assert.Equal(0, fixture.Handler.RequestCount);
            Assert.True(!string.IsNullOrWhiteSpace(item.Error));
            Assert.Equal("", item.LocalMediaPath);
        }
    }

    private static async Task UntrustedLocalSourceAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var source = Path.Combine(fixture.Root, "not a trusted fallback.m3u8");
        await File.WriteAllTextAsync(source, DownloadFixture.SimplePlaylist);
        fixture.Resolver.ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(new Uri(source), "Untrusted"));
        var item = await fixture.WaitAsync((await fixture.EnqueueAsync()).Id, VodDownloadState.Failed);
        Assert.Equal(0, fixture.Handler.RequestCount);
        Assert.Equal("", item.LocalMediaPath);
    }

    private static async Task MutedDownloadAsync()
    {
        await using var fixture = new DownloadFixture(PlatformKind.Twitch);
        var source = Enumerable.Repeat((byte)0xFF, TwitchMutedSegmentSanitizer.PacketSize).ToArray();
        byte[] header = [0x47, 0x41, 0x01, 0x32, 0x07, 0x10, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0x00,
            0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x05, 0x2F, 0xFF, 0xFF, 0xFF, 0xFF];
        header.CopyTo(source, 0);
        var expected = new byte[source.Length];
        Assert.Equal(2, TwitchMutedSegmentSanitizer.Repair(source, expected));
        fixture.Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(DownloadFixture.SimplePlaylist.Replace("segment-a.ts", "segment-a-muted.ts")));
        fixture.Handler.Put("/redirected/segment-a-muted.ts", source);
        fixture.Service.SetBandwidthLimit(400);
        var timer = Stopwatch.StartNew();
        var item = await fixture.DownloadAsync();
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(400), "Muted-segment repair bypassed the shared download bandwidth limit.");
        var saved = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(item.LocalMediaPath)!, "asset-000000-muted.ts"));
        Assert.SequenceEqual(expected, saved);
        Assert.Equal((long)source.Length + 12, item.BytesDownloaded);
    }

    internal sealed class DownloadFixture : IAsyncDisposable
    {
        internal const string SimplePlaylist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\nsegment-a.ts\n#EXTINF:3,\nsegment-b.ts\n#EXT-X-ENDLIST\n";
        internal string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StreamStudioTests", "VodDownloads-" + Guid.NewGuid().ToString("N")));
        internal string Library => Path.Combine(Root, "offline VODs 日本語");
        internal StreamTarget Target { get; }
        internal Uri PlaylistUri { get; }
        internal DownloadHttpHandler Handler { get; } = new();
        internal FakeStreamlinkService Resolver { get; } = new();
        internal VodDownloadOptions Options { get; } = new("fixture-streamlink.exe", []);
        internal HttpClient Client { get; }
        internal VodDownloadService Service { get; private set; }
        internal Func<StreamTarget, CancellationToken, Task<StreamTarget>>? KickResolver { get; set; }
        internal ITwitchSubOnlyVodResolver? TwitchFallback { get; set; }

        internal DownloadFixture(PlatformKind platform)
        {
            Directory.CreateDirectory(Root);
            Target = platform == PlatformKind.Twitch ? VodDownloadUrlParser.Parse("https://www.twitch.tv/videos/12345") :
                VodDownloadUrlParser.Parse("https://kick.com/streamer/videos/01K62MXDAY68GRSY43VZX5KVAZ");
            PlaylistUri = new Uri(platform == PlatformKind.Twitch ? "https://vod-secure.twitch.tv/redirected/index.m3u8" : "https://stream.kick.com/redirected/index.m3u8");
            Resolver.ResolveStreamUrlOverride = (_, _) => Task.FromResult(new StreamlinkResolvedUrl(PlaylistUri, "Fixture"));
            Handler.Put("/redirected/index.m3u8", Encoding.UTF8.GetBytes(SimplePlaylist));
            Handler.Put("/redirected/segment-a.ts", new byte[8]);
            Handler.Put("/redirected/segment-b.ts", new byte[12]);
            Client = new HttpClient(Handler) { Timeout = TimeSpan.FromSeconds(2) };
            Service = CreateService();
        }

        private VodDownloadService CreateService() => new(Library, Resolver, new MemoryLogger(), TwitchFallback, Client,
            new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })), KickResolver);
        internal string Record(Guid id) => Path.Combine(Library, id.ToString("N"), "download.json");
        internal Task<VodDownloadItem> EnqueueAsync(StreamTarget? target = null) =>
            Service.EnqueueAsync(new VodDownloadRequest(target ?? Target, "best", Options));
        internal async Task<VodDownloadItem> DownloadAsync() => await WaitAsync((await EnqueueAsync()).Id, VodDownloadState.Completed);
        internal async Task<VodDownloadItem> WaitAsync(Guid id, VodDownloadState state)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                var item = (await Service.GetDownloadsAsync()).Single(entry => entry.Id == id);
                if (item.State == state) return item;
                if (!item.IsActive) throw new InvalidOperationException($"Expected {state}, got {item.State}: {item.Error}");
                await Task.Delay(15);
            }
            throw new TimeoutException($"VOD download did not reach {state}.");
        }

        internal TaskCompletionSource BlockSegment()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Handler.Override = async (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("segment-a.ts", StringComparison.Ordinal))
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return null;
            };
            return entered;
        }

        internal async Task RestartAsync()
        {
            await Service.DisposeAsync();
            Service = CreateService();
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Client.Dispose();
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StreamStudioTests")) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(Root).StartsWith("VodDownloads-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe download fixture cleanup path.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    internal sealed class DownloadHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> resources = [];
        private int requestCount;
        internal bool NetworkAvailable { get; set; } = true;
        internal int RequestCount => Volatile.Read(ref requestCount);
        internal ConcurrentQueue<string> Addresses { get; } = new();
        internal ConcurrentQueue<string> Ranges { get; } = new();
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
        internal void Put(string path, byte[] data) => resources[path] = data;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!NetworkAvailable) throw new InvalidOperationException("Internet is disabled for this test.");
            Interlocked.Increment(ref requestCount);
            Addresses.Enqueue(request.RequestUri!.AbsoluteUri);
            if (request.Headers.Range is { } range) Ranges.Enqueue(range.ToString());
            if (Override is not null && await Override(request, cancellationToken) is { } overridden) return overridden;
            if (!resources.TryGetValue(request.RequestUri.AbsolutePath, out var data))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            if (request.Headers.Range?.Ranges.Single() is { From: { } from, To: { } to })
            {
                var response = Reply(request, data[(int)from..((int)to + 1)], status: HttpStatusCode.PartialContent);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
                return response;
            }
            return Reply(request, data);
        }

        internal static HttpResponseMessage Reply(HttpRequestMessage request, byte[] bytes,
            string type = "application/octet-stream", HttpStatusCode status = HttpStatusCode.OK)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes), RequestMessage = request };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            return response;
        }
    }

    private sealed class DeniedChatFactory : IChatClientFactory
    {
        public IChatClient Create(PlatformKind platform) => throw new InvalidOperationException("Offline playback must not connect live chat.");
    }

    private sealed class DeniedVodChat : IVodChatProvider
    {
        internal int Calls { get; private set; }
        public Task<VodChatFetchResult> FetchAsync(ReplaySessionInfo replay, AppSettings settings, TimeSpan fromOffset, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Offline playback must not fetch replay chat.");
        }
    }
}
