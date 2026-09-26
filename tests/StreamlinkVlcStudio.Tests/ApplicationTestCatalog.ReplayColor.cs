internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> ReplayColorTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [
            ("replay color: native overlay presents correct MP4 pixels after resume", () => ReplayColorAsync(false, false)),
            ("replay color: native overlay presents correct HLS pixels after resume", () => ReplayColorAsync(true, false)),
            ("replay color: native overlay presents correct prepared live replay pixels", () => ReplayColorAsync(true, true)),
            ("replay color: native overlay presents correct completed VOD pixels", () => ReplayColorAsync(true, false, fast: true)),
            ("replay color: native overlay presents correct muted VOD pixels", () => ReplayColorAsync(true, false, fast: true, muted: true)),
            ("replay color: stock GDI presents correct HLS pixels after resume", () => ReplayColorAsync(true, false, nativeOverlay: false)),
            ("replay color: stock GDI presents correct prepared live replay pixels", () => ReplayColorAsync(true, true, nativeOverlay: false)),
            ("replay color: hardware replay stays black until its output gate is released", ReplayColorGateAsync)
        ];

    private static Task ReplayColorAsync(bool hls, bool prepared, bool fast = false, bool muted = false, bool nativeOverlay = true) =>
        WithResponsiveWindowAsync(withVideo: true, async (window, _) =>
        {
            window.Topmost = true;
            window.Activate();
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay-position-colors.mp4");
            await using var server = hls && !fast ? new ReplayFixtureServer(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "replay-position-audio")) : null;
            await using var fixture = fast ? new FastVodFixture(muted) : null;
            var uri = fast ? FastVodFixture.MediaUri : server?.Uri ?? new Uri(path);
            var logger = new MemoryLogger();
            using var engine = (LibVlcPlaybackEngine)await new LibVlcPlaybackEngineFactory(logger, new ChatSettings(), fixture?.Gateway).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: nativeOverlay);
            Assert.Equal(nativeOverlay, engine.UsesNativeOverlay);
            Assert.Equal(nativeOverlay, engine.HardwareOverlayComposition);
            engine.SetVideoHandle(surface.Handle);
            try
            {
                if (prepared)
                {
                    await engine.PlayAsync(new Uri(path), 0, PlaybackAudioState.Muted);
                    await engine.PrepareReplayAsync(uri);
                    Assert.True(GetPreparedReplayInput(engine) is not null, "The test must adopt a prepared input.");
                }
                // Blue begins at 40s. Green is deliberately not the expected color:
                // a corrupt GPU surface can itself be solid green.
                await engine.PlayFromAsync(uri, TimeSpan.FromSeconds(45.25), 0, PlaybackAudioState.Muted);
                if (prepared)
                    Assert.True(logger.Entries.Any(entry => entry.Message.Contains("Prepared replay seek confirmed")));
                await AssertColorAsync("resumed-blue", blue: true);
                await engine.SeekAsync(TimeSpan.FromSeconds(15.25));
                await AssertColorAsync("seeked-red", blue: false);
                if (muted)
                {
                    // index3 is the repaired muted segment; its early frames are red.
                    await engine.SeekAsync(TimeSpan.FromSeconds(30.25));
                    await AssertColorAsync("muted-segment-red", blue: false);
                    Assert.True(fixture!.Requests.Contains("index3-muted.ts"), "The muted segment must actually be fetched.");
                }
                await engine.SeekAsync(TimeSpan.FromSeconds(45.25));
                await AssertColorAsync("seeked-blue", blue: true);
                await engine.PauseAsync();
                await engine.SeekAsync(TimeSpan.FromSeconds(15.25));
                await engine.ResumeAsync();
                await TestWait.UntilAsync(() => engine.TryGetPlaybackClock(out var clock) &&
                    clock.Position >= TimeSpan.FromSeconds(15) && clock.Position < TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(5));
                await AssertColorAsync("paused-seek-red", blue: false);
                // A second open must own a fresh gate, even after the first was released.
                await engine.StopAsync();
                await engine.PlayFromAsync(uri, TimeSpan.FromSeconds(45.25), 0, PlaybackAudioState.Muted);
                await AssertColorAsync("reopened-blue", blue: true);
            }
            finally { await engine.StopAsync(); }

            async Task AssertColorAsync(string phase, bool blue)
            {
                await Task.Delay(750);
                using var frame = CaptureReplayVlcSurface(surface);
                SaveReplayVlcArtifact(frame, VideoRendererMode.Gdi, $"color-{hls}-{prepared}-{fast}-{muted}-{nativeOverlay}-{phase}");
                Console.WriteLine($"Replay color HLS={hls} prepared={prepared} fast={fast} muted={muted} overlay={nativeOverlay} {phase}: {frame.GetPixel(frame.Width / 2, frame.Height / 2)}");
                // Stay inside the square video rather than its pillarbox borders.
                foreach (var dx in new[] { -frame.Height / 8, 0, frame.Height / 8 })
                    foreach (var dy in new[] { -frame.Height / 8, 0, frame.Height / 8 })
                    {
                        var pixel = frame.GetPixel(frame.Width / 2 + dx, frame.Height / 2 + dy);
                        Assert.True(blue ? pixel.B > 200 && pixel.R < 30 && pixel.G < 30 :
                            pixel.R > 200 && pixel.G < 30 && pixel.B < 30,
                            $"Expected {(blue ? "blue" : "red")} replay content, got {pixel}.");
                    }
            }
        });

    private static Task ReplayColorGateAsync() =>
        WithResponsiveWindowAsync(withVideo: true, async (window, _) =>
        {
            window.Topmost = true;
            window.Activate();
            var surface = FindVisualDescendants<VideoSurface>(window).Single();
            await using var server = new ReplayFixtureServer(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "replay-position-audio"));
            using var engine = (LibVlcPlaybackEngine)await new LibVlcPlaybackEngineFactory(new MemoryLogger(), new ChatSettings()).CreateAsync(
                Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!, enableNativeOverlay: true);
            Assert.True(engine.HardwareOverlayComposition);
            engine.SetVideoHandle(surface.Handle);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(LibVlcPlaybackEngine).GetMethod("CreatePlayerCore", flags)!
                .Invoke(engine, [server.Uri, TimeSpan.FromSeconds(45.25)]);
            var player = (IntPtr)typeof(LibVlcPlaybackEngine).GetField("player", flags)!.GetValue(engine)!;
            LibVlcNative.libvlc_audio_set_mute(player, 1);
            try
            {
                Assert.Equal(0, LibVlcNative.libvlc_media_player_play(player));
                await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) && health.DisplayedPictures > 2,
                    TimeSpan.FromSeconds(8));
                using (var frame = CaptureReplayVlcSurface(surface))
                {
                    SaveReplayVlcArtifact(frame, VideoRendererMode.Gdi, "hardware-gated");
                    var pixel = frame.GetPixel(frame.Width / 2, frame.Height / 2);
                    Console.WriteLine($"Hardware replay gated frame: {pixel}");
                    Assert.True(pixel.R < 10 && pixel.G < 10 && pixel.B < 10,
                        $"Decoded preroll must remain black until confirmation, got {pixel}.");
                }
                typeof(LibVlcPlaybackEngine).GetMethod("ReleaseReplayOutputCore", flags)!.Invoke(engine, null);
                await Task.Delay(750);
                using var released = CaptureReplayVlcSurface(surface);
                var blue = released.GetPixel(released.Width / 2, released.Height / 2);
                Assert.True(blue.B > 200 && blue.R < 30 && blue.G < 30, $"Output release must present the decoded blue video, got {blue}.");
            }
            finally { await engine.StopAsync(); }
        });
}
