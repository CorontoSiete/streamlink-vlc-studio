using StreamlinkVlcStudio.App.Wpf.Twitch;
using StreamlinkVlcStudio.Infrastructure.Logging;

internal static partial class TwitchPlaybackAuthenticationTestCatalog
{
    private static Task LiveSableOcAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = await new JsonSettingsService().LoadAsync();
        var output = Environment.GetEnvironmentVariable("SVS_SABLEOC_ARTIFACT_DIRECTORY") ??
            Path.Combine(Path.GetTempPath(), "sableoc-live");
        Directory.CreateDirectory(output);
        using var logger = new FileAppLogger(output);
        var surface = new VideoSurface();
        var window = new Window
        {
            Content = surface,
            Width = 960,
            Height = 640,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        using var browser = new TwitchBonusBrowser(window);
        var cookieReads = 0;
        var service = StreamlinkService.WithTwitchWebsiteSession(logger, token => window.Dispatcher.InvokeAsync(async () =>
        {
            cookieReads++;
            return await browser.GetPlaybackOAuthTokenAsync(token);
        }).Task.Unwrap());
        // Exercise the same transport, tab startup, overlay renderer and hardware
        // decoder as the application; metadata/chat are unnecessary for this probe.
        await using var tab = TestViewModels.CreateTab(
            StreamInputParser.FromChannel(PlatformKind.Twitch, "sableoc"), "best", service,
            new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
            action => window.Dispatcher.BeginInvoke(action));
        try
        {
            window.Show();
            window.UpdateLayout();
            tab.SetVideoHandle(surface.Handle);
            tab.Volume = 0;
            await tab.StartAsync(settings);
            Assert.Equal(PlaybackStatus.Playing, tab.Status);
            Assert.Equal("best", tab.Quality);
            Assert.Equal(1, cookieReads);
            var engine = (LibVlcPlaybackEngine)typeof(StreamTabViewModel)
                .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
                health.DecodedVideo > 60 && health.DisplayedPictures > 60 && health.DecodedAudio > 0,
                TimeSpan.FromSeconds(30));
            Assert.True(engine.TryGetVideoSize(out var width, out var height));
            Assert.Equal(2304, width);
            Assert.Equal(1440, height);
            Assert.True(engine.TryGetPlaybackHealth(out var first));
            var player = (nint)typeof(LibVlcPlaybackEngine)
                .GetField("player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            var snapshot = Path.Combine(output, "source-frame.png");
            Assert.Equal(0, TakeSnapshot(player, 0, snapshot, 0, 0));
            await TestWait.UntilAsync(() => File.Exists(snapshot) && new FileInfo(snapshot).Length > 0, TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.True(engine.TryGetPlaybackHealth(out var second));
            Assert.True(second.Generation == first.Generation && second.DecodedVideo > first.DecodedVideo + 100);
            Assert.True(second.DisplayedPictures > first.DisplayedPictures + 100);
            Assert.True(second.DecodedAudio > first.DecodedAudio);
            var report = JsonSerializer.Serialize(new
            {
                Utc = DateTimeOffset.UtcNow,
                Target = tab.Target.Url,
                tab.Quality,
                Width = width,
                Height = height,
                CookieReads = cookieReads,
                engine.RendererMode,
                engine.UsesNativeOverlay,
                engine.HardwareOverlayComposition,
                First = first,
                AfterFiveSeconds = second,
                Snapshot = Path.GetFileName(snapshot)
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(output, "live-playback.json"), report);
            Console.WriteLine(report);
        }
        finally
        {
            await tab.StopAsync();
            window.Close();
        }
    });

    [DllImport("libvlc", EntryPoint = "libvlc_video_take_snapshot", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TakeSnapshot(nint player, uint number,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint width, uint height);
}
