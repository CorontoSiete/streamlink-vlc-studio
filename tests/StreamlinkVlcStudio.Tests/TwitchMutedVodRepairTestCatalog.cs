using System.Net.Http;

/// <summary>
/// Regression tests for Twitch VODs that froze about two seconds after playback started.
/// Twitch's muted segments (<c>N-muted.ts</c>) stamp one packet every two seconds with a clock
/// reference and presentation timestamp of 2^33-1; libVLC's HLS demuxer never recovers from it.
/// </summary>
internal static class TwitchMutedVodRepairTestCatalog
{
    private const long InvalidTimestamp = 0x1_FFFF_FFFF;
    private const int PacketSize = TwitchMutedSegmentSanitizer.PacketSize;
    private const string PlaylistUrl = "https://d2vi6trrdongqn.cloudfront.net/vod_special/chunked/index-muted-ABC123.m3u8";
    private const string SegmentBaseUrl = "https://d2vi6trrdongqn.cloudfront.net/vod_special/chunked/";

    // The libVLC release the freeze was reported on; VLC 3.0.18 is the first one that is unaffected.
    private static readonly Version AffectedLibVlc = new(3, 0, 12);

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("muted segment sanitizer strips the invalid clock reference and timestamp Twitch writes every two seconds", SanitizerStripsTwitchSentinels),
        ("muted segment sanitizer leaves valid clock references and timestamps untouched", SanitizerLeavesValidPacketsUntouched),
        ("muted segment sanitizer keeps later adaptation and PES header fields when removing sentinels", SanitizerKeepsLaterHeaderFields),
        ("muted segment sanitizer strips both timestamps when only the decode timestamp is invalid", SanitizerStripsBothTimestampsForInvalidDts),
        ("muted segment sanitizer ignores malformed packets and copies trailing partial data verbatim", SanitizerIgnoresMalformedPackets),
        ("muted VOD playlist detection only matches muted media segments", PlaylistDetectionMatchesOnlyMutedSegments),
        ("muted VOD playlist inspection reports growth master playlists and unsupported containers", PlaylistInspectionReportsShape),
        ("muted VOD playlist rewrite proxies muted segments and absolutizes everything else", PlaylistRewriteProxiesOnlyMutedSegments),
        ("muted VOD playlist rewrite rejects unapproved playlist and media URIs", PlaylistRewriteRejectsUnapprovedUris),
        ("muted segment repair copy realigns packets across arbitrary read boundaries", RepairCopyRealignsPacketsAsync),
        ("muted segment repair copy enforces its byte limit and idle timeout", RepairCopyEnforcesLimitsAsync),
        ("muted VOD repair proxy serves a rewritten playlist and repaired muted segments", ProxyServesRepairedSegmentsAsync),
        ("muted VOD repair proxy refreshes growing playlists and keeps segment URLs stable", ProxyKeepsSegmentUrlsStableAsync),
        ("muted VOD repair proxy rejects unknown tokens segments methods and released sessions", ProxyRejectsUnknownRequestsAsync),
        ("muted VOD repair proxy reports upstream failures as bad gateway", ProxyReportsUpstreamFailuresAsync),
        ("muted VOD repair proxy logs interrupted upstream segments and muted segments without repairs", ProxyLogsInterruptedAndUnrepairedSegmentsAsync),
        ("muted VOD repair proxy restarts its listener after the accept loop ends", ProxyRestartsListenerAfterAcceptLoopEndsAsync),
        ("muted VOD gateway only engages for libVLC releases that freeze on muted segments", GatewayOnlyEngagesForAffectedLibVlcAsync),
        ("libVLC version text parses release and development builds", LibVlcVersionTextParses),
        ("muted VOD gateway leaves live transports other providers and clean VODs untouched", GatewayLeavesCleanMediaUntouchedAsync),
        ("muted VOD gateway routes muted Twitch VODs through the repair proxy", GatewayRoutesMutedVodsThroughProxyAsync),
        ("muted VOD gateway repairs sub-only fallback playlists stored on disk", GatewayRepairsLocalPlaylistsAsync),
        ("muted VOD gateway fails open when the playlist probe fails", GatewayFailsOpenAsync),
        ("muted VOD gateway propagates caller cancellation", GatewayPropagatesCancellationAsync),
        ("libVLC engine releases a prepared media lease when playback cannot start", LibVlcEngineReleasesMediaLeaseAsync),
        ("libVLC engine plays the original media when the gateway fails", LibVlcEngineFallsBackWhenGatewayFailsAsync)
    ];

    private static Task SanitizerStripsTwitchSentinels()
    {
        // Byte-for-byte the packet shape captured from a real 0-muted.ts segment:
        // 47 41 01 32 | 07 10 ff ff ff ff fe 00 | 00 00 01 e0 00 00 80 80 05 2f ff ff ff ff | slice data
        var elementaryStream = CreatePattern(162, seed: 3);
        var source = BuildPacket(
            pid: 257,
            payloadUnitStart: true,
            continuityCounter: 2,
            adaptationFlags: 0x10,
            adaptationBody: EncodePcr(InvalidTimestamp),
            payload: [.. BuildPesHeader(flags: 0x80, EncodePesTimestamp(0b0010, InvalidTimestamp)), .. elementaryStream]);
        Assert.SequenceEqual(
            new byte[] { 0x47, 0x41, 0x01, 0x32, 0x07, 0x10, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0x00, 0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80, 0x80, 0x05, 0x2F, 0xFF, 0xFF, 0xFF, 0xFF },
            source[..26]);
        var original = source.ToArray();
        var destination = new byte[source.Length];

        var repairs = TwitchMutedSegmentSanitizer.Repair(source, destination);

        Assert.Equal(2, repairs);
        Assert.SequenceEqual(original, source);
        Assert.SequenceEqual(source[..4], destination[..4]);
        Assert.Equal((byte)7, destination[4]);
        Assert.Equal((byte)0x00, destination[5]);
        Assert.SequenceEqual(Enumerable.Repeat((byte)0xFF, 6), destination[6..12]);
        Assert.SequenceEqual(new byte[] { 0x00, 0x00, 0x01, 0xE0, 0x00, 0x00, 0x80 }, destination[12..19]);
        Assert.Equal((byte)0x00, destination[19]);
        Assert.Equal((byte)0x05, destination[20]);
        Assert.SequenceEqual(Enumerable.Repeat((byte)0xFF, 5), destination[21..26]);
        Assert.SequenceEqual(elementaryStream, destination[26..]);
        return Task.CompletedTask;
    }

    private static Task SanitizerLeavesValidPacketsUntouched()
    {
        var withClock = BuildPacket(
            pid: 257,
            payloadUnitStart: true,
            continuityCounter: 0,
            adaptationFlags: 0x50,
            adaptationBody: EncodePcr(5_579_910),
            payload:
            [
                .. BuildPesHeader(
                    flags: 0xC0,
                    [.. EncodePesTimestamp(0b0011, 5_582_970), .. EncodePesTimestamp(0b0001, 5_579_910)]),
                .. CreatePattern(157, seed: 9)
            ]);
        var payloadOnly = BuildPacket(
            pid: 256,
            payloadUnitStart: false,
            continuityCounter: 7,
            adaptationFlags: null,
            adaptationBody: [],
            payload: CreatePattern(184, seed: 1));
        // A maximal wrapped timestamp one tick below the sentinel must survive.
        var nearSentinel = BuildPacket(
            pid: 257,
            payloadUnitStart: true,
            continuityCounter: 1,
            adaptationFlags: 0x10,
            adaptationBody: EncodePcr(InvalidTimestamp - 1),
            payload: [.. BuildPesHeader(flags: 0x80, EncodePesTimestamp(0b0010, InvalidTimestamp - 1)), .. CreatePattern(162, seed: 4)]);
        byte[] source = [.. withClock, .. payloadOnly, .. nearSentinel];
        var destination = new byte[source.Length];

        var repairs = TwitchMutedSegmentSanitizer.Repair(source, destination);

        Assert.Equal(0, repairs);
        Assert.SequenceEqual(source, destination);
        return Task.CompletedTask;
    }

    private static Task SanitizerKeepsLaterHeaderFields()
    {
        // PCR + OPCR + splice countdown: removing the PCR must slide the later fields up and pad
        // the end of the adaptation field, not leave stuffing where a parser expects the OPCR.
        var originalProgramClock = EncodePcr(1_234_567);
        byte[] adaptationBody = [.. EncodePcr(InvalidTimestamp), .. originalProgramClock, 0x05];
        // PTS + DTS + ESCR: the ESCR must follow the fixed PES header once the timestamps go.
        byte[] escr = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
        byte[] pesHeaderData =
        [
            .. EncodePesTimestamp(0b0011, InvalidTimestamp),
            .. EncodePesTimestamp(0b0001, InvalidTimestamp),
            .. escr
        ];
        var elementaryStream = CreatePattern(PacketSize - 4 - 1 - 1 - adaptationBody.Length - 9 - pesHeaderData.Length, seed: 5);
        var source = BuildPacket(
            pid: 257,
            payloadUnitStart: true,
            continuityCounter: 3,
            adaptationFlags: 0x1C,
            adaptationBody: adaptationBody,
            payload: [.. BuildPesHeader(flags: 0xE0, pesHeaderData), .. elementaryStream]);
        var destination = new byte[source.Length];

        var repairs = TwitchMutedSegmentSanitizer.Repair(source, destination);

        Assert.Equal(2, repairs);
        Assert.Equal(source[4], destination[4]);
        Assert.Equal((byte)0x0C, destination[5]);
        Assert.SequenceEqual(originalProgramClock, destination[6..12]);
        Assert.Equal((byte)0x05, destination[12]);
        Assert.SequenceEqual(Enumerable.Repeat((byte)0xFF, 6), destination[13..19]);
        var pes = 5 + source[4];
        Assert.Equal((byte)0x20, destination[pes + 7]);
        Assert.Equal((byte)pesHeaderData.Length, destination[pes + 8]);
        Assert.SequenceEqual(escr, destination[(pes + 9)..(pes + 15)]);
        Assert.SequenceEqual(Enumerable.Repeat((byte)0xFF, 10), destination[(pes + 15)..(pes + 25)]);
        Assert.SequenceEqual(elementaryStream, destination[(pes + 25)..]);
        return Task.CompletedTask;
    }

    private static Task SanitizerStripsBothTimestampsForInvalidDts()
    {
        var source = BuildPacket(
            pid: 256,
            payloadUnitStart: true,
            continuityCounter: 4,
            adaptationFlags: null,
            adaptationBody: [],
            payload:
            [
                .. BuildPesHeader(
                    flags: 0xC0,
                    [.. EncodePesTimestamp(0b0011, 5_582_970), .. EncodePesTimestamp(0b0001, InvalidTimestamp)],
                    streamId: 0xC0),
                .. CreatePattern(165, seed: 6)
            ]);
        var destination = new byte[source.Length];

        var repairs = TwitchMutedSegmentSanitizer.Repair(source, destination);

        Assert.Equal(1, repairs);
        Assert.Equal((byte)0x00, destination[4 + 7]);
        Assert.SequenceEqual(Enumerable.Repeat((byte)0xFF, 10), destination[(4 + 9)..(4 + 19)]);
        Assert.SequenceEqual(source[(4 + 19)..], destination[(4 + 19)..]);
        return Task.CompletedTask;
    }

    private static Task SanitizerIgnoresMalformedPackets()
    {
        var sentinelPacket = BuildPacket(
            pid: 257,
            payloadUnitStart: true,
            continuityCounter: 2,
            adaptationFlags: 0x10,
            adaptationBody: EncodePcr(InvalidTimestamp),
            payload: [.. BuildPesHeader(flags: 0x80, EncodePesTimestamp(0b0010, InvalidTimestamp)), .. CreatePattern(162, seed: 3)]);

        var lostSync = sentinelPacket.ToArray();
        lostSync[0] = 0x48;

        var oversizedAdaptationField = sentinelPacket.ToArray();
        oversizedAdaptationField[4] = 184;

        // The PES header claims more header bytes than the packet holds, so its optional fields
        // cannot be relocated safely; the clock reference in the same packet is still repairable.
        var truncatedPesHeader = sentinelPacket.ToArray();
        truncatedPesHeader[12 + 8] = 200;

        // Private stream 1 (0xBD) carries no audio/video timestamps this repair is meant for.
        var privateStream = BuildPacket(
            pid: 300,
            payloadUnitStart: true,
            continuityCounter: 0,
            adaptationFlags: null,
            adaptationBody: [],
            payload: [.. BuildPesHeader(flags: 0x80, EncodePesTimestamp(0b0010, InvalidTimestamp), streamId: 0xBD), .. CreatePattern(170, seed: 8)]);

        byte[] trailingPartialPacket = [0x47, 0x41, 0x01, 0x32, 0x07, 0x10, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0x00];
        byte[] source = [.. lostSync, .. oversizedAdaptationField, .. truncatedPesHeader, .. privateStream, .. trailingPartialPacket];
        var destination = new byte[source.Length];

        var repairs = TwitchMutedSegmentSanitizer.Repair(source, destination);

        Assert.Equal(1, repairs);
        Assert.SequenceEqual(lostSync, destination[..PacketSize]);
        Assert.SequenceEqual(oversizedAdaptationField, destination[PacketSize..(2 * PacketSize)]);
        Assert.Equal((byte)0x00, destination[(2 * PacketSize) + 5]);
        Assert.SequenceEqual(truncatedPesHeader[12..], destination[((2 * PacketSize) + 12)..(3 * PacketSize)]);
        Assert.SequenceEqual(privateStream, destination[(3 * PacketSize)..(4 * PacketSize)]);
        Assert.SequenceEqual(trailingPartialPacket, destination[(4 * PacketSize)..]);
        Assert.Throws<ArgumentException>(() => TwitchMutedSegmentSanitizer.Repair(new byte[PacketSize], new byte[PacketSize - 1]));
        return Task.CompletedTask;
    }

    private static Task PlaylistDetectionMatchesOnlyMutedSegments()
    {
        Assert.True(TwitchMutedVodPlaylist.Inspect("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n#EXTINF:10.000,\n1.ts\n").MutedSegments > 0);
        Assert.True(TwitchMutedVodPlaylist.Inspect("#EXTM3U\r\n#EXTINF:10.000,\r\n0.ts\r\n#EXTINF:10.000,\r\n  41-MUTED.ts?sig=abc  \r\n").MutedSegments > 0);
        Assert.True(TwitchMutedVodPlaylist.Inspect(
            "#EXTM3U\n#EXTINF:10.000,\nhttps://d2vi6trrdongqn.cloudfront.net/vod_special/chunked/7-muted.ts\n").MutedSegments > 0);
        Assert.Equal(2, TwitchMutedVodPlaylist.Inspect("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n#EXTINF:10.000,\n1.ts\n#EXTINF:10.000,\n2-muted.ts\n").MutedSegments);

        Assert.Equal(false, TwitchMutedVodPlaylist.Inspect(null).MutedSegments > 0);
        Assert.Equal(false, TwitchMutedVodPlaylist.Inspect("").MutedSegments > 0);
        Assert.Equal(false, TwitchMutedVodPlaylist.Inspect("#EXTM3U\n#EXTINF:10.000,\n0.ts\n#EXTINF:10.000,\n1-unmuted.ts\n").MutedSegments > 0);
        Assert.Equal(false, TwitchMutedVodPlaylist.Inspect("#EXTM3U\n# previously 0-muted.ts\n#EXTINF:10.000,\n0.ts\n").MutedSegments > 0);
        Assert.Equal(false, TwitchMutedVodPlaylist.Inspect("#EXTM3U\n#EXTINF:10.000,\n0.ts?next=1-muted.ts\n").MutedSegments > 0);
        Assert.Equal(false, TwitchMutedVodPlaylist.Inspect(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8000000\nchunked/index-muted-ABC123.m3u8\n").MutedSegments > 0);

        Assert.True(TwitchMutedVodPlaylist.IsMutedSegment(new Uri(SegmentBaseUrl + "0-muted.ts")));
        Assert.True(TwitchMutedVodPlaylist.IsMutedSegment(new Uri(SegmentBaseUrl + "0-muted.ts?token=1")));
        Assert.Equal(false, TwitchMutedVodPlaylist.IsMutedSegment(new Uri(SegmentBaseUrl + "0.ts")));
        Assert.Equal(false, TwitchMutedVodPlaylist.IsMutedSegment(new Uri(SegmentBaseUrl + "0-unmuted.ts")));
        return Task.CompletedTask;
    }

    private static Task PlaylistInspectionReportsShape()
    {
        var complete = TwitchMutedVodPlaylist.Inspect("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n#EXTINF:10.000,\n1.ts\n#EXT-X-ENDLIST\n");
        Assert.Equal(new TwitchMutedVodPlaylistInspection(IsMediaPlaylist: true, IsComplete: true, MutedSegments: 1, UnsupportedMutedSegments: 0), complete);

        // The VOD of a broadcast that is still live has no end tag yet.
        var growing = TwitchMutedVodPlaylist.Inspect("﻿#EXTM3U\r\n#EXT-X-PLAYLIST-TYPE:EVENT\r\n#EXTINF:10.000,\r\n0.ts\r\n");
        Assert.Equal(new TwitchMutedVodPlaylistInspection(IsMediaPlaylist: true, IsComplete: false, MutedSegments: 0, UnsupportedMutedSegments: 0), growing);

        // Twitch also serves fragmented MP4 VODs; the MPEG-TS repair cannot help with those.
        var fragmentedMp4 = TwitchMutedVodPlaylist.Inspect(
            "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-MAP:URI=\"init-0.mp4\"\n#EXTINF:10.000,\n0-muted.mp4\n#EXTINF:10.000,\n1.mp4\n#EXT-X-ENDLIST\n");
        Assert.Equal(new TwitchMutedVodPlaylistInspection(IsMediaPlaylist: true, IsComplete: true, MutedSegments: 0, UnsupportedMutedSegments: 1), fragmentedMp4);

        // A directory that merely mentions "-muted." is not a muted segment.
        var mutedDirectory = TwitchMutedVodPlaylist.Inspect("#EXTM3U\n#EXTINF:10.000,\nvod-muted.v2/0.ts\n#EXT-X-ENDLIST\n");
        Assert.Equal(0, mutedDirectory.MutedSegments + mutedDirectory.UnsupportedMutedSegments);

        var master = TwitchMutedVodPlaylist.Inspect("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8000000\nchunked/index-muted-ABC123.m3u8\n");
        Assert.Equal(default, master);
        Assert.Equal(default, TwitchMutedVodPlaylist.Inspect(null));
        Assert.Equal(default, TwitchMutedVodPlaylist.Inspect("not a playlist\n0-muted.ts\n"));
        return Task.CompletedTask;
    }

    private static Task PlaylistRewriteProxiesOnlyMutedSegments()
    {
        var playlist =
            "﻿#EXTM3U\r\n" +
            "#EXT-X-TARGETDURATION:12\r\n" +
            "#EXT-X-MAP:URI=\"init.mp4\"\r\n" +
            "#EXTINF:10.000,\r\n" +
            "0-muted.ts\r\n" +
            "#EXTINF:10.000,\r\n" +
            "1.ts\r\n" +
            "#EXTINF:10.000,\r\n" +
            "2-unmuted.ts\r\n" +
            "#EXTINF:10.000,\r\n" +
            "https://d111111abcdef8.cloudfront.net/other/3-muted.ts\r\n" +
            "#EXT-X-ENDLIST\r\n";
        var proxied = new List<Uri>();

        var rewritten = TwitchMutedVodPlaylist.RewriteForRepair(
            playlist,
            new Uri(PlaylistUrl),
            uri =>
            {
                proxied.Add(uri);
                return $"http://127.0.0.1:5000/token/s/{proxied.Count - 1}.ts";
            });

        Assert.Equal(
            "#EXTM3U\n" +
            "#EXT-X-TARGETDURATION:12\n" +
            "#EXT-X-MAP:URI=\"" + SegmentBaseUrl + "init.mp4\"\n" +
            "#EXTINF:10.000,\n" +
            "http://127.0.0.1:5000/token/s/0.ts\n" +
            "#EXTINF:10.000,\n" +
            SegmentBaseUrl + "1.ts\n" +
            "#EXTINF:10.000,\n" +
            SegmentBaseUrl + "2-unmuted.ts\n" +
            "#EXTINF:10.000,\n" +
            "http://127.0.0.1:5000/token/s/1.ts\n" +
            "#EXT-X-ENDLIST\n",
            rewritten);
        Assert.SequenceEqual(
            new[]
            {
                new Uri(SegmentBaseUrl + "0-muted.ts"),
                new Uri("https://d111111abcdef8.cloudfront.net/other/3-muted.ts")
            },
            proxied);

        // A sub-only fallback playlist lives on disk and already carries absolute media URIs.
        var localPlaylistUri = new Uri(Path.Combine(Path.GetTempPath(), "svs-muted-playlist-test", "123-chunked.m3u8"));
        var localRewritten = TwitchMutedVodPlaylist.RewriteForRepair(
            "#EXTM3U\n#EXTINF:10.000,\n" + SegmentBaseUrl + "0-muted.ts\n",
            localPlaylistUri,
            static _ => "http://127.0.0.1:5000/token/s/0.ts");
        Assert.Equal("#EXTM3U\n#EXTINF:10.000,\nhttp://127.0.0.1:5000/token/s/0.ts\n", localRewritten);
        return Task.CompletedTask;
    }

    private static Task PlaylistRewriteRejectsUnapprovedUris()
    {
        static string Proxy(Uri _) => "http://127.0.0.1:5000/token/s/0.ts";

        Assert.Throws<InvalidDataException>(() => TwitchMutedVodPlaylist.RewriteForRepair(
            "#EXTM3U\n#EXTINF:10.000,\nhttps://evil.example/0-muted.ts\n",
            new Uri(PlaylistUrl),
            Proxy));
        Assert.Throws<InvalidDataException>(() => TwitchMutedVodPlaylist.RewriteForRepair(
            "#EXTM3U\n#EXTINF:10.000,\nhttp://d2vi6trrdongqn.cloudfront.net/vod/0-muted.ts\n",
            new Uri(PlaylistUrl),
            Proxy));
        Assert.Throws<InvalidDataException>(() => TwitchMutedVodPlaylist.RewriteForRepair(
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://evil.example/key.bin\"\n#EXTINF:10.000,\n0-muted.ts\n",
            new Uri(PlaylistUrl),
            Proxy));
        Assert.Throws<InvalidDataException>(() => TwitchMutedVodPlaylist.RewriteForRepair(
            "#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n",
            new Uri("https://evil.example/vod/index-muted-ABC123.m3u8"),
            Proxy));
        // Relative entries in a local playlist would resolve to local files, never to Twitch.
        Assert.Throws<InvalidDataException>(() => TwitchMutedVodPlaylist.RewriteForRepair(
            "#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n",
            new Uri(Path.Combine(Path.GetTempPath(), "svs-muted-playlist-test", "123-chunked.m3u8")),
            Proxy));
        return Task.CompletedTask;
    }

    private static async Task RepairCopyRealignsPacketsAsync()
    {
        var source = BuildMutedSegment(packetCount: 40, sentinelPacketIndexes: [0, 17, 39], trailingBytes: 23);
        var expected = new byte[source.Length];
        var expectedRepairs = TwitchMutedSegmentSanitizer.Repair(source, expected);
        Assert.Equal(6, expectedRepairs);

        foreach (var chunkSize in new[] { 1, 7, 187, 188, 189, 4096, source.Length })
        {
            await using var upstream = new ChunkedReadStream(source, chunkSize);
            using var downstream = new MemoryStream();

            var result = await TwitchMutedSegmentRepairCopier.CopyAsync(
                upstream,
                downstream,
                maxBytes: source.Length,
                idleTimeout: TimeSpan.FromSeconds(5),
                CancellationToken.None);

            Assert.Equal((long)source.Length, result.BytesCopied);
            Assert.Equal(expectedRepairs, result.Repairs);
            Assert.SequenceEqual(expected, downstream.ToArray());
        }
    }

    private static async Task RepairCopyEnforcesLimitsAsync()
    {
        var source = BuildMutedSegment(packetCount: 8, sentinelPacketIndexes: [1], trailingBytes: 0);

        await using (var upstream = new ChunkedReadStream(source, 500))
        {
            using var downstream = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => TwitchMutedSegmentRepairCopier.CopyAsync(
                upstream,
                downstream,
                maxBytes: source.Length - 1,
                idleTimeout: TimeSpan.FromSeconds(5),
                CancellationToken.None));
            Assert.True(downstream.Length < source.Length, "A segment over the limit must not be forwarded completely.");
        }

        await using (var stalled = new StalledReadStream())
        {
            using var downstream = new MemoryStream();
            await Assert.ThrowsAsync<TimeoutException>(() => TwitchMutedSegmentRepairCopier.CopyAsync(
                stalled,
                downstream,
                maxBytes: 1024,
                idleTimeout: TimeSpan.FromMilliseconds(100),
                CancellationToken.None));
        }

        await using (var stalled = new StalledReadStream())
        {
            using var downstream = new MemoryStream();
            using var cancellation = new CancellationTokenSource();
            var pending = TwitchMutedSegmentRepairCopier.CopyAsync(
                stalled,
                downstream,
                maxBytes: 1024,
                idleTimeout: TimeSpan.FromSeconds(30),
                cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
    }

    private static async Task ProxyServesRepairedSegmentsAsync()
    {
        var segment = BuildMutedSegment(packetCount: 12, sentinelPacketIndexes: [3, 9], trailingBytes: 0);
        var expectedSegment = new byte[segment.Length];
        TwitchMutedSegmentSanitizer.Repair(segment, expectedSegment);
        var upstreamRequests = new ConcurrentQueue<string>();
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            upstreamRequests.Enqueue(url);
            return url == SegmentBaseUrl + "0-muted.ts"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(segment) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var logger = new MemoryLogger();
        await using var proxy = new TwitchMutedVodRepairProxy(logger, upstream, TestReplayUrlSecurity.PublicValidator);
        var playlistReads = 0;
        using var session = proxy.OpenSession(new TwitchVodPlaylistSource(
            new Uri(PlaylistUrl),
            _ =>
            {
                Interlocked.Increment(ref playlistReads);
                return Task.FromResult("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n#EXTINF:10.000,\n1.ts\n#EXT-X-ENDLIST\n");
            }));

        Assert.Equal("http", session.PlaylistUri.Scheme);
        Assert.Equal("127.0.0.1", session.PlaylistUri.Host);
        Assert.True(session.PlaylistUri.AbsolutePath.EndsWith("/playlist.m3u8", StringComparison.Ordinal));

        using var player = new HttpClient();
        using var playlistResponse = await player.GetAsync(session.PlaylistUri);
        Assert.Equal(HttpStatusCode.OK, playlistResponse.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", playlistResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", playlistResponse.Headers.CacheControl?.ToString());
        var playlist = await playlistResponse.Content.ReadAsStringAsync();
        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length);
        var proxiedSegment = new Uri(lines[2]);
        Assert.Equal(session.PlaylistUri.Authority, proxiedSegment.Authority);
        Assert.True(proxiedSegment.AbsolutePath.EndsWith("/s/0.ts", StringComparison.Ordinal));
        Assert.Equal(SegmentBaseUrl + "1.ts", lines[4]);
        Assert.Equal(1, Volatile.Read(ref playlistReads));

        using var segmentResponse = await player.GetAsync(proxiedSegment);
        Assert.Equal(HttpStatusCode.OK, segmentResponse.StatusCode);
        Assert.Equal("video/mp2t", segmentResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal((long)segment.Length, segmentResponse.Content.Headers.ContentLength ?? -1);
        Assert.SequenceEqual(expectedSegment, await segmentResponse.Content.ReadAsByteArrayAsync());
        Assert.SequenceEqual(new[] { SegmentBaseUrl + "0-muted.ts" }, upstreamRequests.ToArray());
    }

    private static async Task ProxyKeepsSegmentUrlsStableAsync()
    {
        using var upstream = new HttpClient(new FakeHttpMessageHandler(
            static _ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var proxy = new TwitchMutedVodRepairProxy(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        var playlistReads = 0;
        using var session = proxy.OpenSession(
            new TwitchVodPlaylistSource(
                new Uri(PlaylistUrl),
                _ =>
                {
                    Interlocked.Increment(ref playlistReads);
                    return Task.FromResult(
                        "#EXTM3U\n#EXTINF:10.000,\n0.ts\n#EXTINF:10.000,\n1-muted.ts\n#EXTINF:10.000,\n2-muted.ts\n");
                }),
            initialPlaylist: "#EXTM3U\n#EXTINF:10.000,\n0.ts\n#EXTINF:10.000,\n1-muted.ts\n");
        using var player = new HttpClient();

        // The playlist fetched while probing is served once so playback start costs one upstream read.
        var first = await player.GetStringAsync(session.PlaylistUri);
        Assert.Equal(0, Volatile.Read(ref playlistReads));
        var firstLines = first.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, firstLines.Length);

        var second = await player.GetStringAsync(session.PlaylistUri);
        Assert.Equal(1, Volatile.Read(ref playlistReads));
        var secondLines = second.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, secondLines.Length);
        Assert.Equal(firstLines[4], secondLines[4]);
        Assert.True(new Uri(secondLines[4]).AbsolutePath.EndsWith("/s/0.ts", StringComparison.Ordinal));
        Assert.True(new Uri(secondLines[6]).AbsolutePath.EndsWith("/s/1.ts", StringComparison.Ordinal));
    }

    private static async Task ProxyRejectsUnknownRequestsAsync()
    {
        var upstreamRequests = 0;
        using var upstream = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref upstreamRequests);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[PacketSize]) };
        }));
        await using var proxy = new TwitchMutedVodRepairProxy(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        var session = proxy.OpenSession(new TwitchVodPlaylistSource(
            new Uri(PlaylistUrl),
            static _ => Task.FromResult("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n")));
        using var player = new HttpClient();
        var root = session.PlaylistUri.GetLeftPart(UriPartial.Authority);
        var token = session.PlaylistUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Equal(32, token.Length);

        // Segment URLs exist only after the playlist that names them was served.
        using (var unknownSegment = await player.GetAsync($"{root}/{token}/s/0.ts"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownSegment.StatusCode);
        }

        _ = await player.GetStringAsync(session.PlaylistUri);
        foreach (var path in new[]
        {
            "/",
            "/playlist.m3u8",
            $"/{new string('0', 32)}/playlist.m3u8",
            $"/{token}/other.m3u8",
            $"/{token}/s/1.ts",
            $"/{token}/s/-1.ts",
            $"/{token}/s/0.mp4",
            $"/{token}/s/00.ts",
            $"/{token}/s/0.ts/extra",
            $"/{token}/s/99999999999999999999.ts"
        })
        {
            using var response = await player.GetAsync(root + path);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"Expected 404 for '{path}', got {(int)response.StatusCode}.");
        }

        using (var post = await player.PostAsync(session.PlaylistUri, new StringContent("")))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        }

        Assert.Equal(0, Volatile.Read(ref upstreamRequests));

        session.Dispose();
        session.Dispose();
        using (var released = await player.GetAsync(session.PlaylistUri))
        {
            Assert.Equal(HttpStatusCode.NotFound, released.StatusCode);
        }

        using (var releasedSegment = await player.GetAsync($"{root}/{token}/s/0.ts"))
        {
            Assert.Equal(HttpStatusCode.NotFound, releasedSegment.StatusCode);
        }

        Assert.Equal(0, Volatile.Read(ref upstreamRequests));

        await proxy.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => proxy.OpenSession(new TwitchVodPlaylistSource(
            new Uri(PlaylistUrl),
            static _ => Task.FromResult("#EXTM3U\n"))));
        await Assert.ThrowsAsync<HttpRequestException>(() => player.GetAsync(session.PlaylistUri));
    }

    private static async Task ProxyReportsUpstreamFailuresAsync()
    {
        using var upstream = new HttpClient(new FakeHttpMessageHandler(
            static _ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var logger = new MemoryLogger();
        await using var proxy = new TwitchMutedVodRepairProxy(logger, upstream, TestReplayUrlSecurity.PublicValidator);
        var failPlaylist = false;
        using var session = proxy.OpenSession(new TwitchVodPlaylistSource(
            new Uri(PlaylistUrl),
            _ => Volatile.Read(ref failPlaylist)
                ? Task.FromException<string>(new HttpRequestException("playlist unavailable"))
                : Task.FromResult("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n")));
        using var player = new HttpClient();

        var playlist = await player.GetStringAsync(session.PlaylistUri);
        var segmentUri = new Uri(playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries)[2]);
        using (var segmentResponse = await player.GetAsync(segmentUri))
        {
            Assert.Equal(HttpStatusCode.BadGateway, segmentResponse.StatusCode);
        }

        Volatile.Write(ref failPlaylist, true);
        using (var playlistResponse = await player.GetAsync(session.PlaylistUri))
        {
            Assert.Equal(HttpStatusCode.BadGateway, playlistResponse.StatusCode);
        }

        var token = session.PlaylistUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.True(
            logger.Entries.Any(entry => entry.Level == AppLogLevel.Warning && entry.Source == "MutedVodRepair"),
            "Upstream failures must be logged.");
        Assert.True(
            logger.Entries.All(entry => !entry.Message.Contains(token, StringComparison.Ordinal)),
            "Session tokens must not be written to the log.");
    }

    private static async Task ProxyLogsInterruptedAndUnrepairedSegmentsAsync()
    {
        var withoutSentinels = BuildMutedSegment(packetCount: 4, sentinelPacketIndexes: [], trailingBytes: 0);
        var truncated = BuildMutedSegment(packetCount: 6, sentinelPacketIndexes: [1], trailingBytes: 0);
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("/0-muted.ts", StringComparison.Ordinal)
                    ? new ByteArrayContent(withoutSentinels)
                    : new StreamContent(new FailingReadStream(truncated, failAfter: 3 * PacketSize))
            }));
        var logger = new MemoryLogger();
        await using var proxy = new TwitchMutedVodRepairProxy(logger, upstream, TestReplayUrlSecurity.PublicValidator);
        using var session = proxy.OpenSession(new TwitchVodPlaylistSource(
            new Uri(PlaylistUrl),
            static _ => Task.FromResult("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts?sig=secret\n#EXTINF:10.000,\n1-muted.ts\n#EXT-X-ENDLIST\n")));
        using var player = new HttpClient();
        var lines = (await player.GetStringAsync(session.PlaylistUri)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // A muted segment without the invalid timestamps is forwarded unchanged. It could also mean
        // Twitch changed the layout the repair recognizes, so it is reported -- once per session.
        Assert.SequenceEqual(withoutSentinels, await player.GetByteArrayAsync(lines[2]));
        Assert.SequenceEqual(withoutSentinels, await player.GetByteArrayAsync(lines[2]));
        await WaitForLogAsync(logger, entry =>
            entry.Level == AppLogLevel.Warning &&
            entry.Message.Contains("nothing to repair", StringComparison.Ordinal) &&
            entry.Message.Contains("/0-muted.ts", StringComparison.Ordinal));

        // An upstream body that ends early must not vanish as if the player had gone away.
        var partial = await player.GetByteArrayAsync(lines[4]);
        Assert.True(partial.Length < truncated.Length, "The truncated segment cannot be complete.");
        await WaitForLogAsync(logger, entry =>
            entry.Level == AppLogLevel.Warning &&
            entry.Message.Contains("/1-muted.ts was interrupted", StringComparison.Ordinal) &&
            entry.Exception is TwitchMutedSegmentSourceException);

        Assert.Equal(1, logger.Entries.Count(entry => entry.Message.Contains("nothing to repair", StringComparison.Ordinal)));
        Assert.True(
            logger.Entries.All(entry => !entry.Message.Contains("secret", StringComparison.Ordinal)),
            "Query strings must never be logged.");
    }

    private static async Task ProxyRestartsListenerAfterAcceptLoopEndsAsync()
    {
        using var upstream = new HttpClient(new FakeHttpMessageHandler(
            static _ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var logger = new MemoryLogger();
        await using var proxy = new TwitchMutedVodRepairProxy(logger, upstream, TestReplayUrlSecurity.PublicValidator);
        var source = new TwitchVodPlaylistSource(
            new Uri(PlaylistUrl),
            static _ => Task.FromResult("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n#EXT-X-ENDLIST\n"));
        using var first = proxy.OpenSession(source);
        using var player = new HttpClient();
        Assert.Contains("/s/0.ts", await player.GetStringAsync(first.PlaylistUri));

        // Stop the listener underneath the running proxy: the accept loop ends although nobody
        // disposed the proxy, which used to leave every later session pointing at a dead port.
        var proxyType = typeof(TwitchMutedVodRepairProxy);
        var listener = (System.Net.Sockets.TcpListener)proxyType
            .GetField("listener", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(proxy)!;
        var acceptLoop = (Task)proxyType
            .GetField("acceptLoop", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(proxy)!;
        listener.Stop();
        await acceptLoop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(
            logger.Entries.Any(entry => entry.Level == AppLogLevel.Error && entry.Source == "MutedVodRepair"),
            "A listener that stops underneath a running proxy must be reported as an error.");

        using var second = proxy.OpenSession(source);

        Assert.Contains("/s/0.ts", await player.GetStringAsync(second.PlaylistUri));
        Assert.True(
            logger.Entries.Any(entry => entry.Message.Contains("restarted", StringComparison.Ordinal)),
            "Restarting the listener must be logged.");
        if (second.PlaylistUri.Port == first.PlaylistUri.Port)
        {
            // The previous port was free again, so the session that was already playing recovers too.
            Assert.Contains("/s/0.ts", await player.GetStringAsync(first.PlaylistUri));
        }
    }

    private static async Task GatewayLeavesCleanMediaUntouchedAsync()
    {
        var upstreamRequests = new ConcurrentQueue<string>();
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            upstreamRequests.Enqueue(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("#EXTM3U\n#EXTINF:10.000,\n0.ts\n#EXTINF:10.000,\n1-unmuted.ts\n#EXT-X-ENDLIST\n")
            };
        }));
        var logger = new MemoryLogger();
        await using var gateway = new TwitchMutedVodPlaybackGateway(logger, upstream, TestReplayUrlSecurity.PublicValidator);

        foreach (var untouched in new[]
        {
            "http://127.0.0.1:50081/",
            "https://stream.kick.com/ivs/v1/1/media/hls/1080p60/playlist.m3u8",
            "https://evil.example/vod/index-muted-ABC123.m3u8",
            "https://d2vi6trrdongqn.cloudfront.net/vod_special/chunked/0-muted.ts",
            "http://d2vi6trrdongqn.cloudfront.net/vod_special/chunked/index-muted-ABC123.m3u8"
        })
        {
            var uri = new Uri(untouched);
            using var source = await gateway.PrepareAsync(uri, AffectedLibVlc, CancellationToken.None);
            Assert.True(ReferenceEquals(uri, source.PlaybackUri), $"'{untouched}' must be played unchanged.");
        }

        Assert.Equal(0, upstreamRequests.Count);

        var cleanVod = new Uri("https://d2vi6trrdongqn.cloudfront.net/vod_special/chunked/index-dvr.m3u8?sig=secret");
        using (var source = await gateway.PrepareAsync(cleanVod, AffectedLibVlc, CancellationToken.None))
        {
            Assert.True(ReferenceEquals(cleanVod, source.PlaybackUri), "A VOD without muted segments must be played unchanged.");
        }

        Assert.SequenceEqual(new[] { cleanVod.AbsoluteUri }, upstreamRequests.ToArray());
        // The verdict leaves a breadcrumb, without the query string that can carry a signature.
        Assert.True(
            logger.Entries.Any(entry =>
                entry.Level == AppLogLevel.Debug &&
                entry.Source == "MutedVodRepair" &&
                entry.Message.Contains("index-dvr.m3u8 is complete and lists no muted segments", StringComparison.Ordinal)),
            "A clean verdict must be recorded for diagnostics.");
        Assert.True(
            logger.Entries.All(entry => !entry.Message.Contains("secret", StringComparison.Ordinal)),
            "Query strings must never be logged.");
    }

    private static async Task GatewayOnlyEngagesForAffectedLibVlcAsync()
    {
        var upstreamRequests = 0;
        using var upstream = new HttpClient(new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref upstreamRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n#EXT-X-ENDLIST\n")
            };
        }));
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        var uri = new Uri(PlaylistUrl);

        // VLC 3.0.18 fixed the demuxer, so newer releases must not pay for the workaround at all.
        foreach (var unaffected in new[] { new Version(3, 0, 18), new Version(3, 0, 18, 1), new Version(3, 0, 23), new Version(4, 0, 0) })
        {
            using var source = await gateway.PrepareAsync(uri, unaffected, CancellationToken.None);
            Assert.True(ReferenceEquals(uri, source.PlaybackUri), $"libVLC {unaffected} must play muted VODs directly.");
        }

        Assert.Equal(0, Volatile.Read(ref upstreamRequests));

        // Releases that were measured to freeze, anything older, and an unknown release get the repair.
        foreach (var affected in new[] { new Version(3, 0, 12), new Version(3, 0, 17, 4), new Version(2, 2, 8), null })
        {
            using var source = await gateway.PrepareAsync(uri, affected, CancellationToken.None);
            Assert.True(source.PlaybackUri.IsLoopback, $"libVLC {affected?.ToString() ?? "(unknown)"} must get the repair proxy.");
        }

        Assert.Equal(new Version(3, 0, 18), TwitchMutedVodPlaybackGateway.FirstUnaffectedLibVlcVersion);
    }

    private static Task LibVlcVersionTextParses()
    {
        Assert.Equal(new Version(3, 0, 12), LibVlcVersion.TryParse("3.0.12 Vetinari"));
        Assert.Equal(new Version(3, 0, 17, 4), LibVlcVersion.TryParse("3.0.17.4 Vetinari"));
        Assert.Equal(new Version(3, 0, 23), LibVlcVersion.TryParse("  3.0.23 Vetinari"));
        Assert.Equal(new Version(4, 0, 0), LibVlcVersion.TryParse("4.0.0-dev Otto Chriek"));
        Assert.Equal(new Version(3, 0), LibVlcVersion.TryParse("3.0"));
        foreach (var unparseable in new[] { null, "", "Vetinari", "v3.0.12", "3", "3.0.12.1.5", "99999999999.0.1" })
        {
            Assert.True(LibVlcVersion.TryParse(unparseable) is null, $"'{unparseable}' must not parse as a libVLC version.");
        }

        return Task.CompletedTask;
    }

    private static async Task GatewayRoutesMutedVodsThroughProxyAsync()
    {
        var segment = BuildMutedSegment(packetCount: 6, sentinelPacketIndexes: [2], trailingBytes: 0);
        var expectedSegment = new byte[segment.Length];
        TwitchMutedSegmentSanitizer.Repair(segment, expectedSegment);
        var playlistRequests = 0;
        using var upstream = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == PlaylistUrl)
            {
                Interlocked.Increment(ref playlistRequests);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("#EXTM3U\n#EXTINF:10.000,\n0-muted.ts\n#EXTINF:10.000,\n1.ts\n#EXT-X-ENDLIST\n")
                };
            }

            return request.RequestUri.AbsoluteUri == SegmentBaseUrl + "0-muted.ts"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(segment) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var logger = new MemoryLogger();
        await using var gateway = new TwitchMutedVodPlaybackGateway(logger, upstream, TestReplayUrlSecurity.PublicValidator);
        using var player = new HttpClient();

        var source = await gateway.PrepareAsync(new Uri(PlaylistUrl), AffectedLibVlc, CancellationToken.None);

        Assert.True(source.PlaybackUri.IsLoopback, "A muted VOD must be played through the loopback repair proxy.");
        Assert.Equal("http", source.PlaybackUri.Scheme);
        var playlist = await player.GetStringAsync(source.PlaybackUri);
        Assert.Equal(1, Volatile.Read(ref playlistRequests));
        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.SequenceEqual(expectedSegment, await player.GetByteArrayAsync(lines[2]));
        Assert.Equal(SegmentBaseUrl + "1.ts", lines[4]);
        Assert.True(
            logger.Entries.Any(entry =>
                entry.Level == AppLogLevel.Info &&
                entry.Source == "MutedVodRepair" &&
                entry.Message.Contains("1 muted segment", StringComparison.Ordinal) &&
                entry.Message.Contains("libVLC 3.0.12", StringComparison.Ordinal) &&
                entry.Message.Contains("Updating VLC", StringComparison.Ordinal)),
            "Engaging the repair proxy must be logged with the muted segment count, the affected libVLC and the remedy.");

        source.Dispose();
        using var released = await player.GetAsync(source.PlaybackUri);
        Assert.Equal(HttpStatusCode.NotFound, released.StatusCode);
    }

    private static async Task GatewayRepairsLocalPlaylistsAsync()
    {
        using var upstream = new HttpClient(new FakeHttpMessageHandler(
            static _ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        var directory = Path.Combine(Path.GetTempPath(), $"svs-muted-vod-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var mutedPath = Path.Combine(directory, "123-chunked.m3u8");
            await File.WriteAllTextAsync(
                mutedPath,
                "#EXTM3U\n#EXTINF:10.000,\n" + SegmentBaseUrl + "0-muted.ts\n#EXTINF:10.000,\n" + SegmentBaseUrl + "1.ts\n#EXT-X-ENDLIST\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            using var player = new HttpClient();

            using (var source = await gateway.PrepareAsync(new Uri(mutedPath), AffectedLibVlc, CancellationToken.None))
            {
                Assert.True(source.PlaybackUri.IsLoopback, "A muted local playlist must be played through the repair proxy.");
                var lines = (await player.GetStringAsync(source.PlaybackUri)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.True(new Uri(lines[2]).IsLoopback, "The muted segment must be fetched through the repair proxy.");
                Assert.Equal(SegmentBaseUrl + "1.ts", lines[4]);
            }

            var cleanPath = Path.Combine(directory, "456-chunked.m3u8");
            await File.WriteAllTextAsync(cleanPath, "#EXTM3U\n#EXTINF:10.000,\n" + SegmentBaseUrl + "0.ts\n#EXT-X-ENDLIST\n");
            var cleanUri = new Uri(cleanPath);
            using (var source = await gateway.PrepareAsync(cleanUri, AffectedLibVlc, CancellationToken.None))
            {
                Assert.True(ReferenceEquals(cleanUri, source.PlaybackUri), "A clean local playlist must be played unchanged.");
            }

            var missingUri = new Uri(Path.Combine(directory, "missing.m3u8"));
            using (var source = await gateway.PrepareAsync(missingUri, AffectedLibVlc, CancellationToken.None))
            {
                Assert.True(ReferenceEquals(missingUri, source.PlaybackUri), "A missing local playlist must be left to the player.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task GatewayFailsOpenAsync()
    {
        var mode = "throw";
        using var upstream = new HttpClient(new AsyncHttpMessageHandler(async (_, cancellationToken) =>
        {
            switch (Volatile.Read(ref mode))
            {
                case "throw":
                    throw new HttpRequestException("network down");
                case "status":
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                case "unapproved":
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("#EXTM3U\n#EXTINF:10.000,\nhttps://evil.example/0-muted.ts\n")
                    };
                default:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
            }
        }));
        var logger = new MemoryLogger();
        await using var gateway = new TwitchMutedVodPlaybackGateway(
            logger,
            upstream,
            TestReplayUrlSecurity.PublicValidator,
            probeTimeout: TimeSpan.FromMilliseconds(150));
        var uri = new Uri(PlaylistUrl);

        foreach (var nextMode in new[] { "throw", "status", "unapproved", "stall" })
        {
            Volatile.Write(ref mode, nextMode);
            var warningsBefore = logger.Entries.Count(entry => entry.Level == AppLogLevel.Warning);
            using var source = await gateway.PrepareAsync(uri, AffectedLibVlc, CancellationToken.None);
            Assert.True(ReferenceEquals(uri, source.PlaybackUri), $"Probe failure '{nextMode}' must fall back to the original URL.");
            Assert.Equal(warningsBefore + 1, logger.Entries.Count(entry => entry.Level == AppLogLevel.Warning));
            // Falling back can bring the freeze back, so the warning has to say so and name the remedy.
            var warning = logger.Entries.Last(entry => entry.Level == AppLogLevel.Warning);
            Assert.Contains("libVLC 3.0.12 will freeze", warning.Message);
            Assert.Contains("updating VLC to 3.0.18", warning.Message);
            Assert.NotNull(warning.Exception);
        }

        // A validator that refuses the host (for example DNS pointing at a private address) also fails open.
        var privateValidator = new ReplayUrlSecurityValidator(
            static (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }));
        await using var guardedGateway = new TwitchMutedVodPlaybackGateway(logger, upstream, privateValidator);
        using var guarded = await guardedGateway.PrepareAsync(uri, AffectedLibVlc, CancellationToken.None);
        Assert.True(ReferenceEquals(uri, guarded.PlaybackUri), "A rejected upstream must fall back to the original URL.");
    }

    private static async Task GatewayPropagatesCancellationAsync()
    {
        using var upstream = new HttpClient(new AsyncHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }));
        var gateway = new TwitchMutedVodPlaybackGateway(new MemoryLogger(), upstream, TestReplayUrlSecurity.PublicValidator);
        await using (gateway)
        {
            using var cancellation = new CancellationTokenSource();

            var pending = gateway.PrepareAsync(new Uri(PlaylistUrl), AffectedLibVlc, cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }

        var uri = new Uri(PlaylistUrl);
        using var afterDispose = await gateway.PrepareAsync(uri, AffectedLibVlc, CancellationToken.None);
        Assert.True(ReferenceEquals(uri, afterDispose.PlaybackUri), "A disposed gateway must fall back to the original URL.");
    }

    private static async Task LibVlcEngineReleasesMediaLeaseAsync()
    {
        if (!TryFindVlcDirectory(out var vlcDirectory))
        {
            return;
        }

        var gateway = new RecordingMediaSourceGateway();
        var factory = new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings(), gateway);
        var mediaUri = new Uri(PlaylistUrl);

        using (var engine = await factory.CreateAsync(vlcDirectory, enableNativeOverlay: false))
        {
            // Without a video surface PlayAsync fails after it has taken ownership of the lease.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.PlayAsync(mediaUri, 50, PlaybackAudioState.Audible));
            Assert.SequenceEqual(new[] { mediaUri }, gateway.Requests);
            // The gateway decides by the libVLC that was really loaded, not by a configured guess.
            Assert.True(
                gateway.Versions[0] is { Major: >= 2 },
                $"Expected the loaded libVLC version, got '{gateway.Versions[0]}'.");
            Assert.Equal(0, gateway.Leases[0].DisposeCount);

            await engine.StopAsync();
            Assert.Equal(1, gateway.Leases[0].DisposeCount);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.PlayAsync(mediaUri, 50, PlaybackAudioState.Audible));
            Assert.Equal(0, gateway.Leases[1].DisposeCount);
        }

        Assert.Equal(1, gateway.Leases[1].DisposeCount);

        using (var engine = await factory.CreateAsync(vlcDirectory, enableNativeOverlay: false))
        {
            using var cancelled = new CancellationTokenSource();
            gateway.CancelAfterPrepare = cancelled;
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => engine.PlayAsync(mediaUri, 50, PlaybackAudioState.Audible, cancelled.Token));
            Assert.Equal(1, gateway.Leases[2].DisposeCount);
        }

        Assert.Equal(1, gateway.Leases[2].DisposeCount);
    }

    private static async Task LibVlcEngineFallsBackWhenGatewayFailsAsync()
    {
        if (!TryFindVlcDirectory(out var vlcDirectory))
        {
            return;
        }

        var logger = new MemoryLogger();
        var gateway = new RecordingMediaSourceGateway { PrepareException = new InvalidOperationException("gateway broke") };
        var factory = new LibVlcPlaybackEngineFactory(logger, new ChatSettings(), gateway);
        using var engine = await factory.CreateAsync(vlcDirectory, enableNativeOverlay: false);

        // The gateway failure is swallowed; the engine then fails for the unrelated missing surface.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.PlayAsync(new Uri(PlaylistUrl), 50, PlaybackAudioState.Audible));

        Assert.Equal("The video surface is not ready yet.", failure.Message);
        Assert.True(
            logger.Entries.Any(entry => entry.Level == AppLogLevel.Warning && ReferenceEquals(entry.Exception, gateway.PrepareException)),
            "A gateway failure must be logged.");
    }

    private static bool TryFindVlcDirectory(out string vlcDirectory)
    {
        vlcDirectory = "";
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var configured = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY");
        vlcDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC")
            : configured;
        return File.Exists(Path.Combine(vlcDirectory, "libvlc.dll"));
    }

    private static byte[] BuildMutedSegment(int packetCount, int[] sentinelPacketIndexes, int trailingBytes)
    {
        var segment = new List<byte>((packetCount * PacketSize) + trailingBytes);
        for (var index = 0; index < packetCount; index++)
        {
            var timestamp = sentinelPacketIndexes.Contains(index) ? InvalidTimestamp : 5_579_910 + (index * 1500L);
            segment.AddRange(BuildPacket(
                pid: 257,
                payloadUnitStart: true,
                continuityCounter: index,
                adaptationFlags: 0x10,
                adaptationBody: EncodePcr(timestamp),
                payload: [.. BuildPesHeader(flags: 0x80, EncodePesTimestamp(0b0010, timestamp)), .. CreatePattern(162, seed: index)]));
        }

        segment.AddRange(CreatePattern(trailingBytes, seed: 99));
        return [.. segment];
    }

    private static byte[] BuildPacket(
        int pid,
        bool payloadUnitStart,
        int continuityCounter,
        byte? adaptationFlags,
        byte[] adaptationBody,
        byte[] payload)
    {
        var packet = new byte[PacketSize];
        packet[0] = 0x47;
        packet[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);
        var adaptationFieldControl = adaptationFlags is null ? 0x1 : 0x3;
        packet[3] = (byte)((adaptationFieldControl << 4) | (continuityCounter & 0x0F));
        var offset = 4;
        if (adaptationFlags is { } flags)
        {
            var adaptationFieldLength = PacketSize - 4 - 1 - payload.Length;
            if (adaptationFieldLength < 1 + adaptationBody.Length)
            {
                throw new ArgumentException("The payload leaves no room for the adaptation field.", nameof(payload));
            }

            packet[4] = (byte)adaptationFieldLength;
            packet[5] = flags;
            adaptationBody.CopyTo(packet, 6);
            packet.AsSpan(6 + adaptationBody.Length, adaptationFieldLength - 1 - adaptationBody.Length).Fill(0xFF);
            offset = 5 + adaptationFieldLength;
        }

        if (offset + payload.Length != PacketSize)
        {
            throw new ArgumentException("The payload must fill the packet exactly.", nameof(payload));
        }

        payload.CopyTo(packet, offset);
        return packet;
    }

    private static byte[] BuildPesHeader(byte flags, byte[] headerData, byte streamId = 0xE0) =>
        [0x00, 0x00, 0x01, streamId, 0x00, 0x00, 0x80, flags, (byte)headerData.Length, .. headerData];

    private static byte[] EncodePcr(long programClockReferenceBase) =>
    [
        (byte)(programClockReferenceBase >> 25),
        (byte)(programClockReferenceBase >> 17),
        (byte)(programClockReferenceBase >> 9),
        (byte)(programClockReferenceBase >> 1),
        (byte)(((programClockReferenceBase & 1) << 7) | 0x7E),
        0x00
    ];

    private static byte[] EncodePesTimestamp(int prefix, long timestamp) =>
    [
        (byte)((prefix << 4) | (int)((timestamp >> 29) & 0x0E) | 0x01),
        (byte)(timestamp >> 22),
        (byte)(((timestamp >> 14) & 0xFE) | 0x01),
        (byte)(timestamp >> 7),
        (byte)(((timestamp << 1) & 0xFE) | 0x01)
    ];

    private static byte[] CreatePattern(int length, int seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            // Values stay below 0xFB so stuffing (0xFF) written by a repair is always distinguishable.
            bytes[index] = (byte)(((index * 31) + (seed * 17) + 1) % 251);
        }

        return bytes;
    }

    /// <summary>
    /// The proxy logs after it finished writing a response, so a client that already has the whole
    /// body can be a moment ahead of the log entry it is about to assert.
    /// </summary>
    private static async Task WaitForLogAsync(MemoryLogger logger, Func<LogEntry, bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (!logger.Entries.Any(predicate))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new InvalidOperationException(
                    "The expected log entry was not written. Entries: " +
                    string.Join(" | ", logger.Entries.Select(entry => $"[{entry.Level}] {entry.Message}")));
            }

            await Task.Delay(20);
        }
    }

    private sealed class FailingReadStream(byte[] data, int failAfter) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (position >= failAfter)
            {
                throw new IOException("The response ended prematurely.");
            }

            var count = Math.Min(buffer.Length, failAfter - position);
            data.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ValueTask.FromResult(Read(buffer.Span));
            }
            catch (IOException ex)
            {
                return ValueTask.FromException<int>(ex);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingMediaSourceGateway : IPlaybackMediaSourceGateway
    {
        public List<Uri> Requests { get; } = [];

        public List<Version?> Versions { get; } = [];

        public List<RecordingLease> Leases { get; } = [];

        public Exception? PrepareException { get; init; }

        public CancellationTokenSource? CancelAfterPrepare { get; set; }

        public Task<PlaybackMediaSource> PrepareAsync(Uri mediaUri, Version? libVlcVersion, CancellationToken cancellationToken)
        {
            if (PrepareException is not null)
            {
                return Task.FromException<PlaybackMediaSource>(PrepareException);
            }

            Requests.Add(mediaUri);
            Versions.Add(libVlcVersion);
            var lease = new RecordingLease();
            Leases.Add(lease);
            // Cancelling after the lease exists models a tab that is closed while the playlist
            // probe is finishing: the engine must release a lease it never got to play.
            CancelAfterPrepare?.Cancel();
            return Task.FromResult(new PlaybackMediaSource(new Uri("http://127.0.0.1:9/token/playlist.m3u8"), lease));
        }
    }

    private sealed class RecordingLease : IDisposable
    {
        private int disposeCount;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public void Dispose() => Interlocked.Increment(ref disposeCount);
    }

    private sealed class ChunkedReadStream(byte[] data, int chunkSize) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - position);
            data.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
