using System.Windows.Interop;

internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekHoverTests =>
    [
        ("replay seek overlay hover maps time without changing playback and clamps preview edges", ReplaySeekHoverPositionAsync),
        ("replay seek overlay hover rejects late thumbnails and closes across lifecycle changes", ReplaySeekHoverLifecycleAsync),
        ("replay seek overlay hover stays stable during native placement refreshes", ReplaySeekHoverNativeStabilityAsync),
        ("replay seek overlay hover stays visible while replacing a thumbnail", ReplaySeekHoverImageStabilityAsync),
        ("replay seek overlay hover storyboard selects sprite cells and rejects unsafe metadata", ReplaySeekStoryboardAsync),
        ("replay seek overlay hover loads bounded provider storyboards and rejects unsafe images", ReplaySeekStoryboardHttpAsync),
        ("replay seek overlay hover decodes thumbnail sprites into frozen images", ReplaySeekPreviewDecodeAsync),
        .. LiveSeekPreviewTests,
        .. ReplaySeekHoverProviderTests
    ];

    private static Task ReplaySeekHoverPositionAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        var originalPosition = session.Tab.ReplaySeekSliderValue;
        var originalElapsed = session.Tab.ReplayElapsedText;
        var originalSeekCount = session.SeekCount;
        var preview = (Border)fixture.Overlay.FindName("SeekPreviewChrome");
        var timestamp = (TextBlock)fixture.Overlay.FindName("SeekPreviewTimestamp");
        foreach (var (fraction, expected) in new[] { (0d, "0:00"), (0.5, "30:00"), (1d, "1:00:00") })
        {
            fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth * fraction, 14));
            fixture.FlushBindings();
            Assert.True(fixture.Overlay.IsSeekHoverOpen);
            Assert.Equal(expected, timestamp.Text);
            var videoBounds = NativeWindowTest.GetWindowBounds(fixture.Target.Handle);
            var previewHandle = ((HwndSource)PresentationSource.FromVisual(preview)).Handle;
            var bounds = NativeWindowTest.GetWindowBounds(previewHandle);
            Assert.True(bounds.Left >= videoBounds.Left && bounds.Right <= videoBounds.Right);
            Assert.True(bounds.Top >= videoBounds.Top && bounds.Bottom <= fixture.Chrome.PointToScreen(new Point()).Y);
        }
        Assert.Equal(originalPosition, session.Tab.ReplaySeekSliderValue);
        Assert.Equal(originalElapsed, session.Tab.ReplayElapsedText);
        Assert.Equal(originalSeekCount, session.SeekCount);
        Assert.Equal(false, session.Tab.IsReplaySeekPreviewActive);
        fixture.Overlay.UpdateSeekHover(new Point(-1, 14));
        Assert.Equal(false, fixture.Overlay.IsSeekHoverOpen);

        // A thumbnail scales down with narrow PiP and never increases the transport's reserved height.
        fixture.Overlay.PreviewImageLoader = (_, _, _) => Task.FromResult<BitmapSource?>(CreateSeekHoverTestImage(0x80));
        fixture.Target.Width = 196;
        fixture.Target.Height = 260;
        fixture.FlushBindings();
        var reservedHeight = fixture.Overlay.ReservedBottomHeight;
        fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth / 2, 14));
        var image = (Image)fixture.Overlay.FindName("SeekPreviewImage");
        await TestWait.UntilAsync(() => image.Source is not null, TimeSpan.FromSeconds(2));
        fixture.FlushBindings();
        Assert.Equal(reservedHeight, fixture.Overlay.ReservedBottomHeight);
        Assert.True(preview.ActualWidth <= fixture.Target.ActualWidth - 16);
        Assert.True(image.ActualWidth > 0 && image.ActualWidth < 192);
        using var screenshot = CaptureReplayVlcSurface(fixture.Target);
        SaveReplayVlcArtifact(screenshot, VideoRendererMode.Gdi, "hover-compact");
    });

    private static Task ReplaySeekHoverLifecycleAsync() => TestSta.RunAsync(async () =>
    {
        await using var session = await ReplayOverlayTestSession.CreateAsync();
        using var fixture = new ReplayOverlayTestHost(session.Tab);
        var first = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        fixture.Overlay.PreviewImageLoader = (_, _, _) => Interlocked.Increment(ref requests) == 1 ? first.Task : second.Task;
        fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth / 4, 14));
        await TestWait.UntilAsync(() => Volatile.Read(ref requests) == 1, TimeSpan.FromSeconds(2));
        fixture.Overlay.UpdateSeekHover(new Point(fixture.Slider.ActualWidth * 0.75, 14));
        await TestWait.UntilAsync(() => Volatile.Read(ref requests) == 2, TimeSpan.FromSeconds(2));
        var currentImage = CreateSeekHoverTestImage(0xD0);
        second.SetResult(currentImage);
        var image = (Image)fixture.Overlay.FindName("SeekPreviewImage");
        await TestWait.UntilAsync(() => ReferenceEquals(currentImage, image.Source), TimeSpan.FromSeconds(2));
        first.SetResult(CreateSeekHoverTestImage(0x20));
        await Task.Delay(150);
        Assert.True(ReferenceEquals(currentImage, image.Source), "An obsolete request must not overwrite the current frame.");
        fixture.FlushBindings();
        using (var screenshot = CaptureReplayVlcSurface(fixture.Target))
            SaveReplayVlcArtifact(screenshot, VideoRendererMode.Gdi, "hover-wide");

        fixture.Overlay.IsOverlayEnabled = false;
        Assert.Equal(false, fixture.Overlay.IsSeekHoverOpen);
        Assert.True(image.Source is null);
        fixture.Overlay.IsOverlayEnabled = true;
        fixture.StopPointerSampling();
        fixture.Overlay.ProcessPointerSample(new Point(101, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        fixture.Overlay.UpdateSeekHover(new Point(100, 14));
        Assert.True(fixture.Overlay.IsSeekHoverOpen);
        fixture.Overlay.DataContext = null;
        Assert.Equal(false, fixture.Overlay.IsSeekHoverOpen);
        Assert.True(image.Source is null);
        fixture.Overlay.DataContext = session.Tab;
        fixture.Overlay.ProcessPointerSample(new Point(102, 100), true, Environment.TickCount64);
        fixture.FlushBindings();
        fixture.Overlay.UpdateSeekHover(new Point(100, 14));
        fixture.Root.Children.Remove(fixture.Overlay);
        fixture.FlushBindings();
        Assert.Equal(false, fixture.Overlay.IsSeekHoverOpen);
    });

    private const string SeekStoryboardJson = """
        [{"width":2,"height":2,"cols":2,"rows":2,"count":6,"images":["0.jpg","1.jpg"]}]
        """;

    private static Task ReplaySeekStoryboardAsync()
    {
        var uri = new Uri("https://d123.cloudfront.net/video/storyboards/123-info.json");
        var storyboard = TwitchSeekStoryboard.Parse(SeekStoryboardJson, uri, 60)!;
        Assert.NotNull(storyboard);
        Assert.Equal(new TwitchSeekPreviewFrame(new Uri(uri, "0.jpg"), 0, 0, 2, 2), storyboard.GetFrame(-5));
        Assert.Equal(new TwitchSeekPreviewFrame(new Uri(uri, "0.jpg"), 2, 2, 2, 2), storyboard.GetFrame(30));
        Assert.Equal(new TwitchSeekPreviewFrame(new Uri(uri, "1.jpg"), 0, 0, 2, 2), storyboard.GetFrame(40));
        Assert.Equal(new TwitchSeekPreviewFrame(new Uri(uri, "1.jpg"), 2, 0, 2, 2), storyboard.GetFrame(60));
        Assert.True(storyboard.GetFrame(61) is null);
        Assert.True(storyboard.GetFrame(double.NaN) is null);
        var rounded = TwitchSeekStoryboard.Parse(SeekStoryboardJson.Replace("\"count\":6", "\"count\":6,\"interval\":11"), uri, 60)!;
        Assert.Equal(0, rounded.GetFrame(10.9)!.X);
        Assert.Equal(2, rounded.GetFrame(11)!.X);
        foreach (var invalid in new[]
        {
            SeekStoryboardJson.Replace("0.jpg", "https://example.com/image.jpg"),
            SeekStoryboardJson.Replace("0.jpg", "file:///C:/image.jpg"),
            SeekStoryboardJson.Replace("\"cols\":2", "\"cols\":0"),
            SeekStoryboardJson.Replace("\"width\":2", "\"width\":2147483647"),
            SeekStoryboardJson.Replace("\"count\":6", "\"count\":100"),
            "{}"
        }) Assert.True(TwitchSeekStoryboard.Parse(invalid, uri, 60) is null);
        Assert.True(TwitchSeekStoryboard.Parse(SeekStoryboardJson, uri, double.PositiveInfinity) is null);
        return Task.CompletedTask;
    }

    private static async Task ReplaySeekStoryboardHttpAsync()
    {
        var requests = 0;
        using var http = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(request.RequestUri!.Host == "gql.twitch.tv"
                    ? """{"data":{"video":{"lengthSeconds":60,"seekPreviewsURL":"https://d123.cloudfront.net/video/storyboards/info.json"}}}"""
                    : SeekStoryboardJson)
            };
        }));
        var validator = new ReplayUrlSecurityValidator((_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
        var client = new TwitchSeekPreviewClient(http, validator);
        var storyboard = await client.GetStoryboardAsync("123", CancellationToken.None);
        Assert.NotNull(storyboard);
        Assert.Equal(2, requests);
        Assert.True(await client.GetStoryboardAsync("bad-id", CancellationToken.None) is null);
        Assert.Equal(2, requests);
        var rejected = false;
        try { await client.GetImageAsync(new Uri("https://127.0.0.1/image.jpg"), CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        Assert.True(rejected);
        Assert.Equal(2, requests);
    }

    private static Task ReplaySeekPreviewDecodeAsync() => TestSta.RunOffscreenAsync(() =>
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateSeekHoverTestImage(0x40)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var decoded = ReplaySeekPreviewImages.DecodeSheet(stream.ToArray());
        Assert.NotNull(decoded);
        Assert.True(decoded!.IsFrozen);
        Assert.Equal(192, decoded.PixelWidth);
        Assert.Equal(108, decoded.PixelHeight);
        Assert.True(ReplaySeekPreviewImages.DecodeSheet([1, 2, 3]) is null);
        return Task.CompletedTask;
    });

    private static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekHoverProviderTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_PREVIEW_VIDEO_ID")) ? [] :
        [("replay seek overlay hover loads a real provider thumbnail", async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var images = new ReplaySeekPreviewImages();
            var image = await images.GetAsync(Environment.GetEnvironmentVariable("SVS_TEST_PREVIEW_VIDEO_ID")!, 30, timeout.Token);
            Assert.NotNull(image);
            Assert.True(image!.IsFrozen && image.PixelWidth > 0 && image.PixelHeight > 0);
        })];

    private static BitmapSource CreateSeekHoverTestImage(byte blue)
    {
        var pixels = new byte[192 * 108 * 4];
        for (var y = 0; y < 108; y++)
            for (var x = 0; x < 192; x++)
            {
                var i = (y * 192 + x) * 4;
                pixels[i] = blue;
                pixels[i + 1] = (byte)(y * 2);
                pixels[i + 2] = (byte)x;
                pixels[i + 3] = 255;
            }
        var image = BitmapSource.Create(192, 108, 96, 96, PixelFormats.Pbgra32, null, pixels, 192 * 4);
        image.Freeze();
        return image;
    }
}
