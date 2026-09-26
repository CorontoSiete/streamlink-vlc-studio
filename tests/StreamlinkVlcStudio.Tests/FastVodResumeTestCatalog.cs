using System.Net.Http;

internal static class FastVodResumeTestCatalog
{
    private static readonly Uri MediaUri = new("https://d2vi6trrdongqn.cloudfront.net/fixture/chunked/index.m3u8");
    private static readonly Version Vlc = new(3, 0, 23);
    private const string Playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:9.75,\n0.ts\n#EXTINF:10.0,\n1-muted.ts\n#EXT-X-ENDLIST\n";
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("VOD fast resume: policy bounds decoder preroll to validated completed TS playlists", PolicyAsync),
        ("VOD fast resume: local transport preserves ordinary bytes and repairs only muted segments", TransportAsync),
        ("VOD fast resume: source inspection preserves legacy playback unless requested", OptInAsync),
        ("VOD fast resume: source inspection rejects unsafe URLs before enabling FFmpeg", UnsafeAsync),
        ("VOD fast resume: revoked sessions interrupt pending player reads", RevocationAsync),
        ("VOD fast resume: long timeline retains demux and preroll ownership", TimelineAsync)
    ];

    private static Task PolicyAsync()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), TwitchVodReplayPolicy.GetPreroll(Playlist, Vlc));
        foreach (var version in new Version?[] { null, new(3, 0, 12), new(4, 0, 0), new(3, 0, 24) })
            Assert.Equal(TimeSpan.Zero, TwitchVodReplayPolicy.GetPreroll(Playlist, version));
        foreach (var content in new[]
        {
            Playlist.Replace("#EXT-X-ENDLIST", ""), Playlist.Replace("0.ts", "0.m4s"),
            Playlist.Replace("9.75", "NaN"), Playlist.Replace("9.75", "31"),
            Playlist.Replace("0.ts\n", ""), Playlist.Replace("#EXTINF:9.75,\n", ""),
            Playlist.Replace("#EXTM3U", "#BAD"), Playlist + "#EXTINF:10,\n2.ts\n",
            Playlist.Replace("0.ts", "0.ts\n#EXT-X-ENDLIST"), Playlist.Replace("TARGETDURATION:10", "TARGETDURATION:bad"),
            Playlist.Replace("TARGETDURATION:10", "TARGETDURATION:1"), Playlist.Replace("#EXT-X-TARGETDURATION:10\n", "")
        }) Assert.Equal(TimeSpan.Zero, TwitchVodReplayPolicy.GetPreroll(content, Vlc));
        foreach (var tag in new[] { "KEY:METHOD=AES-128,URI=\"key\"", "MAP:URI=\"init.mp4\"", "BYTERANGE:99@0",
            "DISCONTINUITY", "DISCONTINUITY-SEQUENCE:1", "GAP", "STREAM-INF:BANDWIDTH=100", "PART:DURATION=1,URI=\"part.ts\"" })
            Assert.Equal(TimeSpan.Zero, TwitchVodReplayPolicy.GetPreroll(Playlist.Replace("#EXTINF:9.75,", "#EXT-X-" + tag + "\n#EXTINF:9.75,"), Vlc));
        return Task.CompletedTask;
    }

    private static byte[] Packet()
    {
        var bytes = Enumerable.Repeat((byte)0x55, 188).ToArray();
        byte[] prefix = [0x47, 0x41, 0x01, 0x32, 0x07, 0x10, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0x00,
            0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x05, 0x2F, 0xFF, 0xFF, 0xFF, 0xFF];
        prefix.CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task TransportAsync()
    {
        var bytes = Packet();
        var requested = new List<Uri>();
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            lock (requested) requested.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri == MediaUri
                    ? new StringContent(Playlist) : new ByteArrayContent(bytes)
            };
        }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        using var source = await gateway.PrepareAsync(MediaUri, Vlc, CancellationToken.None, preferFastReplay: true);
        Assert.True(source.UseAvformatDemuxer);
        using var client = new HttpClient();
        var content = await client.GetStringAsync(source.PlaybackUri);
        var segments = content.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).Select(line => new Uri(line)).ToArray();
        Assert.True(segments.All(uri => uri.IsLoopback));
        Assert.SequenceEqual(bytes, await client.GetByteArrayAsync(segments[0]));
        var repaired = await client.GetByteArrayAsync(segments[1]);
        Assert.Equal((byte)0, repaired[5]);
        Assert.Equal((byte)0, repaired[19]);
        Assert.SequenceEqual(bytes[26..], repaired[26..]);
        Assert.Equal(3, requested.Count);
        source.Dispose();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(source.PlaybackUri)).StatusCode);
    }

    private static async Task OptInAsync()
    {
        using var upstream = new HttpClient(new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Playlist.Replace("-muted", "")) }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        using var ordinary = await gateway.PrepareAsync(MediaUri, Vlc, CancellationToken.None);
        Assert.Equal(MediaUri, ordinary.PlaybackUri);
        Assert.Equal(false, ordinary.UseAvformatDemuxer);
        using var unsupported = await gateway.PrepareAsync(MediaUri, new Version(3, 0, 12), CancellationToken.None, true);
        Assert.Equal(MediaUri, unsupported.PlaybackUri);
        using var resumed = await gateway.PrepareAsync(MediaUri, Vlc, CancellationToken.None, true);
        Assert.True(resumed.UseAvformatDemuxer);
    }

    private static async Task UnsafeAsync()
    {
        using var upstream = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Playlist.Replace("0.ts", "http://127.0.0.1/private.ts")) }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        using var source = await gateway.PrepareAsync(MediaUri, Vlc, CancellationToken.None, true);
        Assert.Equal(false, source.UseAvformatDemuxer);
        Assert.Equal(MediaUri, source.PlaybackUri);
    }

    private static async Task RevocationAsync()
    {
        var waiting = new WaitingStream();
        using var upstream = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(waiting) }));
        await using var proxy = new TwitchMutedVodRepairProxy(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        using var session = proxy.OpenSession(new TwitchVodPlaylistSource(MediaUri, _ => Task.FromResult(Playlist)), Playlist, true);
        using var client = new HttpClient();
        var playlist = await client.GetStringAsync(session.PlaylistUri);
        var segment = playlist.Split('\n').First(line => line.StartsWith("http", StringComparison.Ordinal));
        using var response = await client.GetAsync(segment, HttpCompletionOption.ResponseHeadersRead);
        var reading = response.Content.ReadAsByteArrayAsync();
        await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Dispose();
        try { await reading.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (HttpRequestException) { }
        Assert.True(reading.IsCompleted, "Revoking the source must unblock the player without awaiting the upstream transfer timeout.");
        await waiting.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task TimelineAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "fast-vod-" + Guid.NewGuid().ToString("N") + ".m3u8");
        await File.WriteAllTextAsync(path, Playlist.Replace("0.ts", new Uri(MediaUri, "0.ts").AbsoluteUri)
            .Replace("1-muted.ts", new Uri(MediaUri, "1-muted.ts").AbsoluteUri));
        try
        {
            // Add segments so the existing long-timeline adapter can retain preroll.
            var content = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, content.Replace("#EXT-X-ENDLIST", "#EXTINF:10,\n" + new Uri(MediaUri, "2.ts").AbsoluteUri + "\n#EXT-X-ENDLIST"));
            using var source = await HlsReplayTimeline.PrepareAsync(new PlaybackMediaSource(new Uri(path), null,
                useAvformatDemuxer: true, replaySeekPreroll: TimeSpan.FromSeconds(10)), TimeSpan.FromHours(14), CancellationToken.None);
            Assert.True(source.UseAvformatDemuxer);
            Assert.Equal(TimeSpan.FromSeconds(10), source.ReplaySeekPreroll);
            Assert.True(source.TimelineOffset > TimeSpan.Zero);
        }
        finally { File.Delete(path); }
    }

    internal sealed class WaitingStream : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
