using SkiaSharp;

internal static class AnimatedEmoteDecodingTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("Animated emote decoder composes partial GIF frames and both disposal modes", () =>
            VerifyDecodedFramesAsync(CreateGifFixture())),
        ("Animated emote decoder composes partial WebP frames with transparency and background disposal", () =>
            VerifyDecodedFramesAsync(CreateWebPFixture())),
        ("Native Twitch and Kick replay overlays preserve all pixels in decoded GIF emote frames", () =>
            VerifyReplayOverlaysAsync(CreateGifFixture())),
        ("Native Twitch and Kick replay overlays preserve all pixels in decoded WebP emote frames", () =>
            VerifyReplayOverlaysAsync(CreateWebPFixture()))
    ];

    private static Task VerifyDecodedFramesAsync(AnimationFixture fixture)
    {
        return TestSta.RunOffscreenAsync(async () =>
        {
            VerifyFixtureDependencies(fixture);
            var path = NewTemporaryPath(fixture.Extension);
            var url = new Uri(path).AbsoluteUri;
            try
            {
                File.WriteAllBytes(path, fixture.Bytes);
                var image = await LoadImageAsync(url);
                Assert.True(AnimatedEmoteImage.ContainsCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes));
                var cachedImage = await LoadImageAsync(url);
                Assert.True(ReferenceEquals(image.Source, cachedImage.Source));

                // Check the whole canvas, including alpha and regions outside the frame rectangle.
                // Repeat after wrapping so a repaired decoder cannot mask a damaged cached frame.
                var duration = fixture.Delays.Sum();
                for (var loop = 0; loop < 2; loop++)
                {
                    var clock = loop * duration;
                    for (var frameIndex = 0; frameIndex < fixture.Frames.Length; frameIndex++)
                    {
                        foreach (var sampleOffset in new[] { 0, fixture.Delays[frameIndex] - 1 })
                        {
                            var sampleClock = TimeSpan.FromMilliseconds(clock + sampleOffset);
                            Assert.True(image.ApplyAnimationClock(sampleClock, out var remaining));
                            Assert.True(cachedImage.ApplyAnimationClock(sampleClock, out var cachedRemaining));
                            Assert.Equal(TimeSpan.FromMilliseconds(Math.Max(20, fixture.Delays[frameIndex] - sampleOffset)), remaining);
                            Assert.Equal(remaining, cachedRemaining);
                            AssertPixels(fixture.Frames[frameIndex], image.Source, $"{fixture.Extension} frame {frameIndex}");
                            AssertPixels(fixture.Frames[frameIndex], cachedImage.Source, $"cached {fixture.Extension} frame {frameIndex}");
                        }

                        clock += fixture.Delays[frameIndex];
                    }
                }
            }
            finally
            {
                AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes);
                File.Delete(path);
            }
        });
    }

    private static Task VerifyReplayOverlaysAsync(AnimationFixture fixture)
    {
        return TestSta.RunOffscreenAsync(async () =>
        {
            var paths = new List<string>();
            var animationPath = NewTemporaryPath(fixture.Extension);
            paths.Add(animationPath);
            try
            {
                File.WriteAllBytes(animationPath, fixture.Bytes);
                var animationUrl = await LoadReplayImageAsync(animationPath);
                var expectedUrls = new List<string>();
                foreach (var expectedFrame in fixture.Frames)
                {
                    // The reference images are independent, complete PNG canvases. They do not
                    // call the animated decoder or seed its cache with synthetic animation data.
                    var expectedPath = NewTemporaryPath("png");
                    paths.Add(expectedPath);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(CreateBitmap(expectedFrame)));
                    using (var output = File.Create(expectedPath))
                    {
                        encoder.Save(output);
                    }

                    expectedUrls.Add(await LoadReplayImageAsync(expectedPath));
                }

                foreach (var platform in new[] { PlatformKind.Twitch, PlatformKind.Kick })
                {
                    var clock = 0;
                    for (var frameIndex = 0; frameIndex < fixture.Frames.Length; frameIndex++)
                    {
                        var actual = RenderReplayFrame(platform, animationUrl, clock);
                        var expected = RenderReplayFrame(platform, expectedUrls[frameIndex], clock);
                        Assert.True(actual.HasAnimatedContent);
                        Assert.Equal(false, actual.HasPendingImageLoads);
                        Assert.Equal(false, expected.HasPendingImageLoads);
                        Assert.Equal(false, expected.HasAnimatedContent);
                        Assert.Equal<TimeSpan?>(TimeSpan.FromMilliseconds(fixture.Delays[frameIndex]), actual.NextAnimationFrameDelay);
                        Assert.True(expected.Frame.SequenceEqual(actual.Frame),
                            $"{platform} replay {fixture.Extension} frame {frameIndex} differs from the complete expected canvas.");
                        clock += fixture.Delays[frameIndex];
                    }

                    var wrapped = RenderReplayFrame(platform, animationUrl, clock);
                    var first = RenderReplayFrame(platform, expectedUrls[0], clock);
                    Assert.True(first.Frame.SequenceEqual(wrapped.Frame), $"{platform} replay did not wrap to the first complete frame.");
                }
            }
            finally
            {
                foreach (var path in paths)
                {
                    AnimatedEmoteImage.RemoveCachedImageForTest(new Uri(path).AbsoluteUri, AnimatedEmoteImage.DefaultMaxImageBytes);
                    AnimatedEmoteImage.RemoveCachedImageForTest(ReplayUrl(path), AnimatedEmoteImage.DefaultMaxImageBytes);
                    File.Delete(path);
                }
            }
        });
    }

    private static NativeOverlayChatFrame RenderReplayFrame(PlatformKind platform, string url, int clock)
    {
        var message = new ChatMessage(
            platform,
            "streamer",
            "viewer",
            "Spin",
            DateTimeOffset.UnixEpoch,
            "#8AB4F8",
            Emotes: [new ChatEmote(0, 4, "Spin", url)],
            MessageId: "partial-animation-regression");
        var frame = NativeOverlayChatFrameRenderer.TryBuildFrame(
            [message],
            new ChatSettings { DockWidth = 340 },
            18,
            1080,
            null,
            TimeSpan.FromMilliseconds(clock),
            out _,
            out _);
        Assert.NotNull(frame);
        return frame!;
    }

    private static async Task<AnimatedEmoteImage> LoadImageAsync(string url)
    {
        var image = new AnimatedEmoteImage { ImageUrl = url };
        await TestWait.UntilAsync(
            () => !image.IsImageLoadPending && image.Source is BitmapSource,
            TimeSpan.FromSeconds(5),
            "real encoded emote file load");
        return image;
    }

    private static async Task<string> LoadReplayImageAsync(string path)
    {
        var image = await LoadImageAsync(new Uri(path).AbsoluteUri);
        // Chat messages accept HTTPS emote URLs. Alias the result of the real file loader,
        // preserving its actual decoded pixels and timings while avoiding a network server.
        var decoded = typeof(AnimatedEmoteImage)
            .GetField("decodedImage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(image);
        Assert.NotNull(decoded);
        var url = ReplayUrl(path);
        typeof(AnimatedEmoteImage)
            .GetMethod("SetCompletedCacheEntry", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new AnimatedEmoteImageCacheKey(url, AnimatedEmoteImage.DefaultMaxImageBytes, 0), decoded]);
        return url;
    }

    private static string ReplayUrl(string path) => $"https://example.invalid/{Path.GetFileName(path)}";

    private static void VerifyFixtureDependencies(AnimationFixture fixture)
    {
        using var stream = new MemoryStream(fixture.Bytes, writable: false);
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(4, codec!.Info.Width);
        Assert.Equal(4, codec.Info.Height);
        Assert.Equal(fixture.Frames.Length, codec.FrameCount);
        for (var index = 0; index < fixture.Frames.Length; index++)
        {
            Assert.True(codec.GetFrameInfo(index, out var info));
            Assert.Equal(fixture.Delays[index], info.Duration);
            if (fixture.RequiredFrames[index] is { } requiredFrame)
            {
                Assert.Equal(requiredFrame, info.RequiredFrame);
            }
        }
    }

    private static void AssertPixels(string[] expectedRows, ImageSource? source, string context)
    {
        Assert.True(source is BitmapSource, $"{context} did not produce a bitmap.");
        var bitmap = (BitmapSource)source!;
        Assert.Equal(4, bitmap.PixelWidth);
        Assert.Equal(4, bitmap.PixelHeight);
        Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);
        var actual = new byte[64];
        bitmap.CopyPixels(actual, 16, 0);
        var expected = PixelBytes(expectedRows);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(expected[index] == actual[index],
                $"{context}, pixel ({index / 4 % 4},{index / 16}), BGRA component {index % 4}: expected {expected[index]}, got {actual[index]}.");
        }
    }

    private static BitmapSource CreateBitmap(string[] rows) => BitmapSource.Create(
        rows[0].Length, rows.Length, 96, 96, PixelFormats.Pbgra32, null, PixelBytes(rows), rows[0].Length * 4);

    private static byte[] PixelBytes(string[] rows) => rows
        .SelectMany(row => row)
        .SelectMany(pixel =>
        {
            var color = ColorFor(pixel);
            return new[] { color.Blue, color.Green, color.Red, color.Alpha };
        })
        .ToArray();

    private static SKColor ColorFor(char pixel) => pixel switch
    {
        '.' => new SKColor(0, 0, 0, 0),
        'R' => SKColors.Red,
        'G' => SKColors.Lime,
        'B' => SKColors.Blue,
        'Y' => SKColors.Yellow,
        'C' => SKColors.Cyan,
        'M' => SKColors.Magenta,
        _ => throw new ArgumentOutOfRangeException(nameof(pixel))
    };

    private static string NewTemporaryPath(string extension) => Path.Combine(
        Path.GetTempPath(), $"svs-dependent-emote-{Guid.NewGuid():N}.{extension}");

    private static AnimationFixture CreateGifFixture()
    {
        // A transparent red ring, a retained green delta, a temporary blue delta,
        // a yellow delta disposed to background, then cyan and an independent keyframe.
        // Frame 3 must restore frame 1, skipping the restore-to-previous frame 2.
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
        writer.Write((ushort)4);
        writer.Write((ushort)4);
        writer.Write(new byte[] { 0xf2, 0, 0 });
        foreach (var pixel in ".RGBYCM.")
        {
            var color = ColorFor(pixel);
            writer.Write(new[] { color.Red, color.Green, color.Blue });
        }

        writer.Write(new byte[] { 0x21, 0xff, 11 });
        writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        writer.Write(new byte[] { 3, 1, 0, 0, 0 });
        WriteGifFrame(writer, 0, 0, ["RRRR", "R..R", "R..R", "RRRR"], 1, 100);
        WriteGifFrame(writer, 0, 0, ["G.", ".G"], 1, 70);
        WriteGifFrame(writer, 2, 0, ["BB", "BB"], 3, 90);
        WriteGifFrame(writer, 2, 2, ["YY", "YY"], 2, 110);
        WriteGifFrame(writer, 0, 2, ["C.", ".C"], 1, 130);
        WriteGifFrame(writer, 0, 0, ["MMMM", "MMMM", "MMMM", "MMMM"], 1, 50, transparency: false);
        writer.Write((byte)0x3b);
        return new AnimationFixture("gif", output.ToArray(),
        [
            ["RRRR", "R..R", "R..R", "RRRR"],
            ["GRRR", "RG.R", "R..R", "RRRR"],
            ["GRBB", "RGBB", "R..R", "RRRR"],
            ["GRRR", "RG.R", "R.YY", "RRYY"],
            ["GRRR", "RG.R", "C...", "RC.."],
            ["MMMM", "MMMM", "MMMM", "MMMM"]
        ], [100, 70, 90, 110, 130, 50], [-1, 0, 1, 1, null, -1]);
    }

    private static void WriteGifFrame(BinaryWriter writer, int x, int y, string[] rows, int disposal, int delay, bool transparency = true)
    {
        writer.Write(new byte[] { 0x21, 0xf9, 4, (byte)((disposal << 2) | (transparency ? 1 : 0)) });
        writer.Write((ushort)(delay / 10));
        writer.Write(new byte[] { 0, 0, 0x2c });
        writer.Write((ushort)x);
        writer.Write((ushort)y);
        writer.Write((ushort)rows[0].Length);
        writer.Write((ushort)rows.Length);
        writer.Write((byte)0);
        writer.Write((byte)3); // Eight palette colors; all LZW codes stay four bits.
        var pixels = rows.SelectMany(row => row).ToArray();
        writer.Write((byte)(pixels.Length + 1));
        foreach (var pixel in pixels)
        {
            // Clear before every literal: deterministic LZW without a growing dictionary.
            writer.Write((byte)(8 | (".RGBYCM".IndexOf(pixel) << 4)));
        }

        writer.Write(new byte[] { 9, 0 }); // End-of-information and end-of-data blocks.
    }

    private static AnimationFixture CreateWebPFixture()
    {
        // Assemble ANMF rectangles explicitly: animation optimizers otherwise turn these
        // into full keyframes and silently remove the regression's prior-frame dependency.
        // RIFF layout: https://developers.google.com/speed/webp/docs/riff_container
        using var contents = new MemoryStream();
        using (var writer = new BinaryWriter(contents, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("WEBP"));
            WriteRiffChunk(writer, "VP8X", [0x12, 0, 0, 0, 3, 0, 0, 3, 0, 0]);
            WriteRiffChunk(writer, "ANIM", [0, 0, 0, 0, 0, 0]);
            WriteWebPFrame(writer, 0, 0, ["RRRR", "R..R", "R..R", "RRRR"], 0, 100);
            WriteWebPFrame(writer, 0, 0, ["G.", ".G"], 0, 70);
            WriteWebPFrame(writer, 2, 0, ["BB", "BB"], 1, 90);
            WriteWebPFrame(writer, 2, 2, ["YY", "YY"], 0, 110);
            WriteWebPFrame(writer, 0, 2, ["C.", ".C"], 2, 130);
            WriteWebPFrame(writer, 0, 0, ["MMMM", "MMMM", "MMMM", "MMMM"], 2, 50);
        }

        using var output = new MemoryStream();
        using var container = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        WriteRiffChunk(container, "RIFF", contents.ToArray());
        return new AnimationFixture("webp", output.ToArray(),
        [
            ["RRRR", "R..R", "R..R", "RRRR"],
            ["GRRR", "RG.R", "R..R", "RRRR"],
            ["GRBB", "RGBB", "R..R", "RRRR"],
            ["GR..", "RG..", "R.YY", "RRYY"],
            ["GR..", "RG..", "C.YY", ".CYY"],
            ["MMMM", "MMMM", "MMMM", "MMMM"]
        ], [100, 70, 90, 110, 130, 50], [-1, 0, 1, null, null, -1]);
    }

    private static void WriteWebPFrame(BinaryWriter writer, int x, int y, string[] rows, byte flags, int delay)
    {
        using var bitmap = new SKBitmap(rows[0].Length, rows.Length, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < rows[row].Length; column++)
            {
                bitmap.SetPixel(column, row, ColorFor(rows[row][column]));
            }
        }

        using var pixels = bitmap.PeekPixels();
        using var encoded = pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100));
        Assert.NotNull(encoded);
        var staticWebP = encoded!.ToArray();
        using var frame = new MemoryStream();
        using (var header = new BinaryWriter(frame, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var value in new[] { x / 2, y / 2, rows[0].Length - 1, rows.Length - 1, delay })
            {
                header.Write(new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16) });
            }

            header.Write(flags);
            for (var offset = 12; offset < staticWebP.Length;)
            {
                var chunkName = Encoding.ASCII.GetString(staticWebP, offset, 4);
                var length = BinaryPrimitives.ReadInt32LittleEndian(staticWebP.AsSpan(offset + 4, 4));
                var paddedLength = 8 + length + (length & 1);
                if (chunkName is "VP8L" or "VP8 " or "ALPH")
                {
                    header.Write(staticWebP, offset, paddedLength);
                }

                offset += paddedLength;
            }
        }

        WriteRiffChunk(writer, "ANMF", frame.ToArray());
    }

    private static void WriteRiffChunk(BinaryWriter writer, string name, byte[] bytes)
    {
        writer.Write(Encoding.ASCII.GetBytes(name));
        writer.Write(bytes.Length);
        writer.Write(bytes);
        if ((bytes.Length & 1) != 0)
        {
            writer.Write((byte)0);
        }
    }

    private sealed record AnimationFixture(
        string Extension,
        byte[] Bytes,
        string[][] Frames,
        int[] Delays,
        int?[] RequiredFrames);
}
