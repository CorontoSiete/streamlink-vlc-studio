internal static class ResourceUsageTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All =>
    [
        ("resource usage pixel conversion exactly preserves every alpha and channel value", PixelConversionAsync),
        ("resource usage cached replay frames preserve pixels through animation scrolling and layout changes", CachedFramesAsync),
        ("resource usage hidden emotes stop automatic animation and resume when shown", HiddenAnimationAsync),
        .. (Environment.GetEnvironmentVariable("SVS_RESOURCE_BENCHMARK") == "1"
            ? new (string, Func<Task>)[] { ("resource benchmark native chat animation", BenchmarkOverlayAsync) }
            : [])
    ];

    private static Task PixelConversionAsync()
    {
        var pixels = new byte[256 * 256 * 4];
        for (var alpha = 0; alpha < 256; alpha++)
            for (var value = 0; value < 256; value++)
            {
                var offset = (alpha * 256 + value) * 4;
                pixels[offset] = (byte)value;
                pixels[offset + 1] = (byte)(255 - value);
                pixels[offset + 2] = (byte)(value ^ 85);
                pixels[offset + 3] = (byte)alpha;
            }
        var expected = ReferenceConvert(pixels);
        NativeOverlayPixelConverter.ConvertPbgraToRgba(pixels);
        Assert.True(expected.SequenceEqual(pixels));

        // Cover vector boundaries, unaligned protocol offsets, empty frames and
        // partially transparent runs mixed with opaque/transparent pixels.
        var random = new Random(37);
        for (var count = 0; count < 70; count++)
        {
            var buffer = new byte[36 + count * 4 + 13];
            random.NextBytes(buffer);
            var original = buffer.ToArray();
            expected = ReferenceConvert(buffer.AsSpan(36, count * 4).ToArray());
            NativeOverlayPixelConverter.ConvertPbgraToRgba(buffer.AsSpan(36, count * 4));
            Assert.True(expected.AsSpan().SequenceEqual(buffer.AsSpan(36, count * 4)));
            Assert.True(original.AsSpan(0, 36).SequenceEqual(buffer.AsSpan(0, 36)));
            Assert.True(original.AsSpan(36 + count * 4).SequenceEqual(buffer.AsSpan(36 + count * 4)));
        }
        Assert.Throws<ArgumentException>(() => NativeOverlayPixelConverter.ConvertPbgraToRgba(new byte[3]));
        return Task.CompletedTask;

        static byte[] ReferenceConvert(byte[] original)
        {
            var result = new byte[original.Length];
            for (var i = 0; i < original.Length; i += 4)
            {
                var alpha = original[i + 3];
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = original[i + 2 - channel];
                    result[i + channel] = alpha is 0 or 255 ? value :
                        (byte)Math.Clamp((value * 255 + alpha / 2) / alpha, 0, 255);
                }
                result[i + 3] = alpha;
            }
            return result;
        }
    }

    private static Task CachedFramesAsync() => TestSta.RunOffscreenAsync(() =>
    {
        const string url = "https://example.invalid/resource-pixels.gif";
        AnimatedEmoteImage.SetCachedSolidColorImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes,
            [Colors.Red, Colors.Transparent, Colors.Lime],
            [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20)], 24, 24);
        try
        {
            var context = new NativeReplayOverlayFrameRenderContext();
            var referenceContext = new NativeReplayOverlayFrameRenderContext();
            var messages = Enumerable.Range(0, 35).Select(index => new ChatMessage(
                PlatformKind.Twitch, "", "viewer" + index, "Spin " + new string('x', index * 3),
                DateTimeOffset.UnixEpoch, "#8AB4F8", Emotes: [new ChatEmote(0, 4, "Spin", url)])).ToArray();
            for (var step = 0; step < 24; step++)
            {
                var settings = new ChatSettings { DockWidth = step < 8 ? 500 : step < 16 ? 260 : 700 };
                var height = step < 16 ? 1080 : 720;
                var font = step < 12 ? 18 : 25;
                var offset = step < 4 ? 0 : step < 8 ? 6 : step < 12 ? 25 : step;
                var visibleMessages = step < 18 ? messages : step < 23 ? messages.Reverse().Take(15).ToArray() : [];
                var clock = TimeSpan.FromMilliseconds(step * 20);
                if (step == 14)
                {
                    context.EnsureContentVersion(1);
                    referenceContext.EnsureContentVersion(1);
                }
                // A second renderer deliberately remeasures each row, retaining
                // layout history (including last-row margins) like the original.
                foreach (var block in referenceContext.Stack.Children.OfType<DockedChatMessageTextBlock>())
                    block.InvalidateMeasure();
                var expected = NativeOverlayChatFrameRenderer.TryBuildFrame(visibleMessages, settings, font,
                    height, null, clock, out _, out _, offset, renderContext: referenceContext)!;
                var actual = NativeOverlayChatFrameRenderer.TryBuildFrame(visibleMessages, settings, font,
                    height, null, clock, out _, out _, offset, renderContext: context)!;
                Assert.True(expected.Frame.SequenceEqual(actual.Frame), $"Cached frame differs at step {step}.");
                Assert.Equal(expected.RenderedSelection, actual.RenderedSelection);
                Assert.Equal(expected.NextAnimationFrameDelay, actual.NextAnimationFrameDelay);
            }
        }
        finally { AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes); }
        return Task.CompletedTask;
    });

    private static Task HiddenAnimationAsync() => TestSta.RunAsync(async () =>
    {
        const string url = "https://example.invalid/resource-hidden.gif";
        AnimatedEmoteImage.SetCachedSolidColorImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes,
            [Colors.Red, Colors.Lime], [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)]);
        var image = new AnimatedEmoteImage { ImageUrl = url, Width = 24, Height = 24 };
        var parent = new Border { Child = image };
        var window = new Window { Content = parent, Width = 150, Height = 150, ShowActivated = false, ShowInTaskbar = false };
        var changes = 0;
        var property = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        EventHandler changed = (_, _) => changes++;
        property.AddValueChanged(image, changed);
        try
        {
            window.Show();
            await TestWait.UntilAsync(() => changes >= 2, TimeSpan.FromSeconds(3));
            foreach (var visibility in new[] { Visibility.Hidden, Visibility.Collapsed })
            {
                parent.Visibility = visibility;
                var before = changes;
                await Task.Delay(150);
                Assert.Equal(before, changes);
                // The replay renderer explicitly drives offscreen images; visibility must
                // only stop the automatic UI timer, not change its requested frame.
                Assert.True(image.ApplyAnimationClock(TimeSpan.FromMilliseconds(20), out var delay));
                Assert.Equal(TimeSpan.FromMilliseconds(20), delay);
                parent.Visibility = Visibility.Visible;
                var resumed = changes;
                await TestWait.UntilAsync(() => changes > resumed, TimeSpan.FromSeconds(3));
            }
            window.Content = null;
            await Task.Delay(50);
            var unloaded = changes;
            await Task.Delay(150);
            Assert.Equal(unloaded, changes);
        }
        finally
        {
            property.RemoveValueChanged(image, changed);
            window.Close();
            AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes);
        }
    });

    private static Task BenchmarkOverlayAsync() => TestSta.RunOffscreenAsync(() =>
    {
        const string url = "https://example.invalid/resource-benchmark.gif";
        AnimatedEmoteImage.SetCachedSolidColorImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes,
            [Colors.Red, Colors.Lime], [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)], 24, 24);
        try
        {
            var messages = Enumerable.Range(0, 100).Select(index => new ChatMessage(
                PlatformKind.Twitch, "", "viewer" + index, "Spin a message with antialiased text",
                DateTimeOffset.UnixEpoch, "#8AB4F8", Emotes: [new ChatEmote(0, 4, "Spin", url)])).ToArray();
            var settings = new ChatSettings { DockWidth = 500 };
            var contexts = Enumerable.Range(0, 4).Select(_ => new NativeReplayOverlayFrameRenderContext()).ToArray();
            long bytes = 0;
            NativeOverlayChatFrame? lastFrame = null;
            void Render(int index)
            {
                var frame = NativeOverlayChatFrameRenderer.TryBuildFrame(messages, settings, 18, 1080,
                    null, TimeSpan.FromMilliseconds(index / contexts.Length * 20), out _, out _, renderContext: contexts[index % 4]);
                bytes += frame!.Frame.Length;
                lastFrame = frame;
            }
            for (var i = 0; i < 80; i++) Render(i);
            bytes = 0;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            using var process = Process.GetCurrentProcess();
            var beforeCpu = process.TotalProcessorTime;
            var beforeAllocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 1200; i++) Render(i);
            timer.Stop();
            var cpuMilliseconds = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
            var allocatedBytes = GC.GetTotalAllocatedBytes(true) - beforeAllocated;
            var encodedBytes = bytes;
            var hashes = new List<string> { Convert.ToHexString(SHA256.HashData(lastFrame!.Frame)) };
            for (var i = 0; i < 3; i++)
            {
                Render(i * contexts.Length);
                hashes.Add(Convert.ToHexString(SHA256.HashData(lastFrame!.Frame)));
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Benchmark = "four native chat render contexts, 1200 frames, 500px wide, 1080p video",
                WallMilliseconds = timer.Elapsed.TotalMilliseconds,
                CpuMilliseconds = cpuMilliseconds,
                AllocatedBytes = allocatedBytes,
                EncodedBytes = encodedBytes,
                PixelHashes = hashes
            }));
        }
        finally
        {
            AnimatedEmoteImage.RemoveCachedImageForTest(url, AnimatedEmoteImage.DefaultMaxImageBytes);
        }
        return Task.CompletedTask;
    });
}
