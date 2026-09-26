internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> OverlayReadabilityTests =>
    [
        ("overlay readability keeps chosen font when constrained to the source canvas", () => TestSta.RunAsync(() =>
        {
            var settings = new ChatSettings();
            foreach (var sourceWidth in new[] { 240, 480, 1920 })
            foreach (var sourceHeight in new[] { 720, 1080, 2160 })
            {
                var source = new NativeOverlaySourceSize(sourceWidth, sourceHeight);
                var layout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
                    settings, 15, sourceHeight, null, source);
                Assert.True(Math.Abs(layout.Presentation.MessageFontSize - 15d * sourceHeight / 1080) <= 1,
                    $"Source bounds {sourceWidth}x{sourceHeight} must not reduce the chosen font size.");
                Assert.True(layout.FrameWidth <= source.Width && layout.FrameHeight <= source.Height);
                var selected = NativeOverlayChatFrameRenderer.MeasureVisibleMessages(
                    Enumerable.Range(0, 30).Select(i => new ChatMessage(PlatformKind.Twitch, "fixture", "Viewer",
                        $"Message {i}: readable", DateTimeOffset.UnixEpoch)).ToArray(),
                    layout, 0);
                Assert.True(selected.MessageBlocks.Count > 0,
                    $"Expected a complete message in source canvas {sourceWidth}x{sourceHeight}.");
                Assert.True(selected.UsedHeight <= selected.AvailableHeight,
                    $"Messages must fit in source canvas {sourceWidth}x{sourceHeight}.");
            }
        })),
        ("overlay readability accepts scale events only for the active replay session", async () =>
        {
            var scale = 0;
            var actions = new ConcurrentQueue<Action>();
            await using var host = new NativeOverlayReplayEventHost(new MemoryLogger(), actions.Enqueue,
                () => { }, () => 1080, uiScaleChanged: value => scale = value);
            var pipeName = $"overlay-readability-{Guid.NewGuid():N}";
            var root = CreateTempTestDirectory();
            try
            {
                host.Start(pipeName, Path.Combine(root, "position"));
                await using (var pipe = new NamedPipeClientStream(".", pipeName + "_events", PipeDirection.Out, PipeOptions.Asynchronous))
                {
                    await pipe.ConnectAsync(3000);
                    await pipe.WriteAsync(NativeOverlayProtocolCodec.BuildEventMessage(NativeOverlayProtocolCodec.UiScaleEventType, 3240));
                    await pipe.WriteAsync(NativeOverlayProtocolCodec.BuildEventMessage(NativeOverlayProtocolCodec.UiScaleEventType, -1));
                }
                await TestWait.UntilAsync(() => !actions.IsEmpty, TimeSpan.FromSeconds(2));
                while (actions.TryDequeue(out var action)) action();
                Assert.Equal(3240, scale);
                await using (var pipe = new NamedPipeClientStream(".", pipeName + "_events", PipeDirection.Out, PipeOptions.Asynchronous))
                {
                    await pipe.ConnectAsync(3000);
                    await pipe.WriteAsync(NativeOverlayProtocolCodec.BuildEventMessage(NativeOverlayProtocolCodec.UiScaleEventType, 1620));
                }
                await TestWait.UntilAsync(() => !actions.IsEmpty, TimeSpan.FromSeconds(2));
                host.Stop();
                while (actions.TryDequeue(out var action)) action();
                Assert.Equal(3240, scale);
            }
            finally { DeleteTempTestDirectory(root); }
        }),
        ("overlay readability replay pipeline refreshes cached messages after source resolution changes", OverlayReadabilityReplayScaleAsync),
        .. OverlayReadabilityVlcTests
    ];

    private static Task OverlayReadabilityReplayScaleAsync() => TestSta.RunAsync(async () =>
    {
        var root = CreateTempTestDirectory();
        var pipeName = $"overlay-readability-replay-{Guid.NewGuid():N}";
        var position = Path.Combine(root, "position");
        var factory = new FakePlaybackEngineFactory(() => new FakePlaybackEngine
        {
            UsesNativeOverlayOverride = true,
            NativeOverlayPipeNameOverride = pipeName,
            NativeOverlayPositionStatePathOverride = position
        });
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var target = new StreamTarget(PlatformKind.Kick, "fixture", "https://vod.kick.com/fixture/index.m3u8",
            StreamTargetKind.KickVod, "fixture-vod", "Chat scaling fixture", "", TimeSpan.FromMinutes(30),
            DateTimeOffset.UnixEpoch, ChatRoomId: "668");
        await using var tab = TestViewModels.CreateTab(target, "best", new FakeStreamlinkService(), factory,
            new FakeChatClientFactory(), new MemoryLogger(), action => dispatcher.Invoke(action),
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once(
                [new VodChatMessage(TimeSpan.Zero, new ChatMessage(PlatformKind.Kick, "fixture", "Viewer",
                    "Cached text stays readable", DateTimeOffset.UnixEpoch))], TimeSpan.Zero, TimeSpan.FromMinutes(4))));
        try
        {
            var settings = new AppSettings { StreamlinkPath = "streamlink.exe", VlcDirectory = @"C:\VLC" };
            tab.SetVideoHandle(new IntPtr(42));
            var initial = ReadNativeOverlayPipeMatchingMessageAsync(pipeName, IsNativeOverlayRenderedChatFrame, TimeSpan.FromSeconds(4));
            await tab.StartAsync(settings);
            var originalBounds = GetNativeOverlayAlphaBounds(await initial);

            async Task<byte[]> ReceiveScaledFrame(int scale, int height)
            {
                var next = ReadNativeOverlayPipeMatchingMessageAsync(pipeName, message => IsNativeOverlayRenderedChatFrame(message) &&
                    BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(28, 4)) == height, TimeSpan.FromSeconds(5));
                // Match the plugin's periodic announcement: a live/replay host
                // handoff may occur after the first startup frame was received.
                while (!next.IsCompleted)
                {
                    await WriteNativeOverlayEventPipeMessageAsync(pipeName + "_events",
                        BuildNativeOverlayEventMessage(NativeOverlayProtocolCodec.UiScaleEventType, scale), TimeSpan.FromSeconds(2));
                    await Task.WhenAny(next, Task.Delay(500));
                }
                return await next;
            }

            var enlarged = GetNativeOverlayAlphaBounds(await ReceiveScaledFrame(3240, 876));
            Assert.True(enlarged.Height >= originalBounds.Height * 2.5,
                "Cached messages must rerender when the source resolution changes.");
            var restored = GetNativeOverlayAlphaBounds(await ReceiveScaledFrame(1080, 292));
            Assert.True(Math.Abs(restored.Height - originalBounds.Height) <= 3);
            Assert.True(!File.Exists(position + ".size"), "Source resolution changes must not overwrite the user's panel size.");
        }
        finally { await tab.DisposeAsync(); DeleteTempTestDirectory(root); }
    });

    private static IReadOnlyList<(string Name, Func<Task> Run)> OverlayReadabilityVlcTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")) ? [] :
        [("overlay readability keeps the overlay layout unchanged through real VLC window resizing", () => TestSta.RunAsync(async () =>
        {
            var root = CreateTempTestDirectory();
            var artifactDirectory = Environment.GetEnvironmentVariable("SVS_TEST_OVERLAY_ARTIFACTS");
            var baseline = Environment.GetEnvironmentVariable("SVS_TEST_OVERLAY_BASELINE") == "1";
            var nativeFrames = Environment.GetEnvironmentVariable("SVS_TEST_NATIVE_OVERLAY_FRAMES");
            var surface = new VideoSurface();
            var window = new Window { Content = surface, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Left = 0, Top = 0, Width = 960, Height = 540, Topmost = true, Title = "Overlay readability verification" };
            var settings = new ChatSettings { VlcOverlayDirectory = Environment.GetEnvironmentVariable("SVS_TEST_OVERLAY_DIRECTORY") ?? "" };
            var logger = new MemoryLogger();
            try
            {
                File.WriteAllText(Path.Combine(root, "position"), "32 32 0");
                const string savedSize = "460 350 reference";
                File.WriteAllText(Path.Combine(root, "position.size"), savedSize);
                window.Show(); window.UpdateLayout();
                var factory = new LibVlcPlaybackEngineFactory(logger, settings);
                using var engine = await factory.CreateAsync(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!,
                    nativeOverlayPositionStatePath: Path.Combine(root, "position"));
                Assert.True(engine.UsesNativeOverlay);
                engine.SetVideoHandle(surface.Handle);
                var scaleHeight = 0;
                var scaleNotifications = 0;
                await using var events = new NativeOverlayReplayEventHost(logger, action => window.Dispatcher.InvokeAsync(action),
                    () => { }, () => scaleHeight, uiScaleChanged: value =>
                    {
                        scaleHeight = value;
                        scaleNotifications++;
                    });
                events.Start(engine.NativeOverlayPipeName!, engine.NativeOverlayPositionStatePath!);
                // Use a full-resolution decoded image with no white pixels. The
                // ordinary 64x64 replay fixture cannot resolve a 15px overlay font.
                var media = Path.Combine(root, "video.bmp");
                using (var bitmap = new System.Drawing.Bitmap(1920, 1080))
                {
                    using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                    graphics.Clear(System.Drawing.Color.FromArgb(65, 70, 78));
                    bitmap.Save(media);
                }
                await engine.PlayAsync(new Uri(Path.GetFullPath(media)), 0, PlaybackAudioState.HardMuted);
                await TestWait.UntilAsync(() => engine.TryGetVideoSize(out _, out var h) && h > 0, TimeSpan.FromSeconds(8));
                engine.TryGetVideoSize(out var sourceWidth, out var sourceHeight);
                byte[]? originalFrame = null;
                var sizes = new List<(int, int)> { (960, 540), (640, 360), (853, 480), (480, 270), (720, 720), (1280, 720) };
                if (SystemParameters.PrimaryScreenWidth >= 1920 && SystemParameters.PrimaryScreenHeight >= 1080)
                    sizes.Add((1920, 1080));
                sizes.Add((960, 540));
                foreach (var size in sizes)
                {
                    var previousNotification = scaleNotifications;
                    window.Width = size.Item1; window.Height = size.Item2; window.UpdateLayout(); surface.SyncNativeBounds();
                    var dpi = VisualTreeHelper.GetDpi(surface).DpiScaleY;
                    if (!baseline)
                    {
                        await TestWait.UntilAsync(() => scaleNotifications > previousNotification,
                            TimeSpan.FromSeconds(4), $"Expected a scale announcement after resizing to {size}.");
                        Assert.Equal(sourceHeight, scaleHeight);
                    }
                    var frame = NativeOverlayChatFrameRenderer.TryBuildFrame(
                        [new ChatMessage(PlatformKind.Twitch, "fixture", "Sumosanta420", "fuuuuck — clean small text", DateTimeOffset.UnixEpoch)],
                        settings, 24, baseline ? sourceHeight : scaleHeight, engine.NativeOverlayPositionStatePath, TimeSpan.Zero, out _, out _,
                        sourceSize: baseline ? null : new NativeOverlaySourceSize(sourceWidth, sourceHeight))!;
                    var frameMessage = string.IsNullOrWhiteSpace(nativeFrames) ? frame.Frame :
                        File.ReadAllBytes(Path.Combine(nativeFrames, $"native-{scaleHeight}.rgba"));
                    // One-pixel stems reproduce the lost rows/columns in small
                    // glyphs. Measure their final screen coverage, not just
                    // whether any white text survived VLC's final scaling.
                    AddOverlayScalingProbe(frameMessage);
                    if (!baseline)
                    {
                        originalFrame ??= frameMessage;
                        Assert.True(frameMessage.AsSpan().SequenceEqual(originalFrame),
                            $"Window {size} must not resize the chat bitmap, change its font or reflow its messages.");
                        Assert.Equal(savedSize, File.ReadAllText(Path.Combine(root, "position.size")));
                    }
                    await using (var pipe = new NamedPipeClientStream(".", engine.NativeOverlayPipeName!, PipeDirection.Out, PipeOptions.Asynchronous))
                    {
                        await pipe.ConnectAsync(3000);
                        await pipe.WriteAsync(frameMessage);
                    }
                    await Task.Delay(450);
                    using var capture = CaptureReplayVlcSurface(surface);
                    if (!string.IsNullOrWhiteSpace(artifactDirectory))
                    {
                        Directory.CreateDirectory(artifactDirectory);
                        var renderer = string.IsNullOrWhiteSpace(nativeFrames) ? "" : "-native";
                        capture.Save(Path.Combine(artifactDirectory, $"{(baseline ? "before" : "after")}{renderer}-{size.Item1}x{size.Item2}.png"));
                    }
                    // The video has no white pixels. Restrict measurement to the
                    // actual overlay footprint: a desktop pointer elsewhere in
                    // a large capture is not a chat glyph. The plugin clamps the
                    // explicit 32,32 origin toward zero when the panel is large.
                    var displayedVideoHeight = Math.Min(capture.Height, capture.Width * sourceHeight / (double)sourceWidth);
                    var displayScale = displayedVideoHeight / sourceHeight;
                    var videoLeft = (capture.Width - sourceWidth * displayScale) / 2;
                    var videoTop = (capture.Height - displayedVideoHeight) / 2;
                    if (!baseline && Math.Abs(displayScale - 0.5) < 0.001)
                    {
                        AssertOverlayScalingProbe(capture, videoLeft, videoTop, displayScale);
                    }
                    var frameWidth = BinaryPrimitives.ReadUInt32LittleEndian(frameMessage.AsSpan(24, 4));
                    var frameHeight = BinaryPrimitives.ReadUInt32LittleEndian(frameMessage.AsSpan(28, 4));
                    var right = Math.Min(capture.Width, (int)Math.Ceiling(videoLeft + (32 + frameWidth) * displayScale));
                    var bottom = Math.Min(capture.Height, (int)Math.Ceiling(videoTop + (32 + frameHeight) * displayScale));
                    var rows = new List<int>();
                    for (var y = (int)Math.Ceiling(videoTop + 72 * displayScale); y < bottom; y++)
                    {
                        var pixels = 0;
                        for (var x = (int)videoLeft; x < right; x++)
                        {
                            var c = capture.GetPixel(x, y);
                            // A 15px source font is only 3.75px at quarter size.
                            // Correct area filtering averages its coverage, so
                            // it need not contain any nearly opaque white pixel.
                            // Require contrast against the known (65,70,78)
                            // video; the separate probe asserts filter quality.
                            if (c.R > 105 && c.G > 110 && c.B > 118 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 20) pixels++;
                        }
                        if (pixels >= 3) rows.Add(y);
                    }
                    // Native fixtures contain multiple messages. Wrapping can
                    // change their line count at fractional scales, so compare
                    // the tallest contiguous glyph line, not total text rows.
                    var glyphHeight = 0;
                    var lineHeight = 0;
                    var previousRow = -2;
                    foreach (var row in rows)
                    {
                        lineHeight = row == previousRow + 1 ? lineHeight + 1 : 1;
                        glyphHeight = Math.Max(glyphHeight, lineHeight);
                        previousRow = row;
                    }
                    Console.WriteLine($"Overlay {size}: scale={scaleHeight}, contrasting glyph rows={rows.Count}, tallest line={glyphHeight}, dpi={dpi:0.##}");
                    Assert.True(rows.Count > 0, $"Chat must remain visible in the composed video at {size}.");
                }
            }
            finally { window.Close(); DeleteTempTestDirectory(root); }
        }))];

    private static void AddOverlayScalingProbe(byte[] frame)
    {
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24, 4));
        for (var y = 8; y < 32; y++)
            for (var x = 8; x < 72; x++)
            {
                var offset = NativeOverlayProtocolCodec.HeaderSize + (y * width + x) * 4;
                var value = (byte)((y < 20 ? x : y) % 2 * 255);
                frame[offset] = frame[offset + 1] = frame[offset + 2] = value;
                frame[offset + 3] = 255;
            }
    }

    private static void AssertOverlayScalingProbe(System.Drawing.Bitmap capture,
        double videoLeft, double videoTop, double scale)
    {
        var filtered = 0;
        var total = 0;
        // Inset excludes the probe's boundary and the orientation transition.
        foreach (var sourceY in new[] { 44, 46, 56, 58 })
            for (var sourceX = 44; sourceX < 100; sourceX += 2)
            {
                var pixel = capture.GetPixel((int)Math.Round(videoLeft + sourceX * scale),
                    (int)Math.Round(videoTop + sourceY * scale));
                if (pixel.R is >= 115 and <= 140 && pixel.G is >= 115 and <= 140 && pixel.B is >= 115 and <= 140)
                    filtered++;
                total++;
            }
        Console.WriteLine($"Overlay scaling: {filtered}/{total} samples preserve half-covered stems.");
        Assert.True(filtered >= total * 0.95,
            $"Windowed scaling must preserve coverage of thin glyph stems and gaps; only {filtered}/{total} samples were filtered.");
    }
}
