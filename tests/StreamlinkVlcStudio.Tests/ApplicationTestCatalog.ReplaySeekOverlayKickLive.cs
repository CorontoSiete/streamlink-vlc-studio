internal static partial class ApplicationTestCatalog
{
    private static IReadOnlyList<(string Name, Func<Task> Run)> ReplaySeekOverlayKickLiveTests =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_KICK_LIVE_CHANNEL")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_STREAMLINK_PATH")) ||
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY"))
            ? []
            :
            [
                ("kick live seekbar: repeated playback speed selections keep controls visible",
                    () => ReplaySeekOverlayKickLiveAsync(false)),
                ("kick live seekbar: repeated playback speed selections keep topmost controls visible",
                    () => ReplaySeekOverlayKickLiveAsync(true))
            ];

    private static Task ReplaySeekOverlayKickLiveAsync(bool topmost) => TestSta.RunAsync(async () =>
    {
        if (!NativeWindowTest.TryGetCursorPosition(out var originalCursor))
            throw new InteractiveDesktopTestSkippedException("Cannot preserve the cursor for live Kick speed input.");
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var logger = new MemoryLogger();
        var settings = new AppSettings
        {
            StreamlinkPath = Environment.GetEnvironmentVariable("SVS_TEST_STREAMLINK_PATH")!,
            VlcDirectory = Environment.GetEnvironmentVariable("SVS_TEST_VLC_DIRECTORY")!
        };
        settings.Chat.Layout = ChatLayout.Docked;
        settings.Chat.ConnectAutomatically = false;
        settings.Replay.Enabled = true;
        settings.Replay.AttemptPrivateKickReplayResolution = true;
        var channel = Environment.GetEnvironmentVariable("SVS_TEST_KICK_LIVE_CHANNEL")!;
        var target = StreamInputParser.Parse($"https://kick.com/{channel}", PlatformKind.Kick);
        var streamlink = new StreamlinkService(logger);
        await using var tab = TestViewModels.CreateTab(target, "best", streamlink,
            new LibVlcPlaybackEngineFactory(logger, settings.Chat), new FakeChatClientFactory(), logger,
            action => dispatcher.BeginInvoke(action), initialVolume: 0,
            replayResolver: new ReplayResolver(logger, streamlink),
            vodChatProvider: new FakeVodChatProvider(FakeVodChatProvider.Once([])));
        using var fixture = new ReplayOverlayTestHost(tab);
        var window = Window.GetWindow(fixture.Overlay)!;
        window.Topmost = topmost;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.WorkArea.Left + 24;
        window.Top = SystemParameters.WorkArea.Top + 24;
        var traces = new List<string>();
        window.Activated += (_, _) => traces.Add("owner activated");
        window.Deactivated += (_, _) => traces.Add("owner deactivated");
        fixture.Overlay.Unloaded += (_, _) => traces.Add("overlay unloaded");
        var combo = (ComboBox)fixture.Overlay.FindName("PlaybackRateComboBox");
        combo.DropDownOpened += (_, _) => traces.Add("speed dropdown opened");
        combo.DropDownClosed += (_, _) => traces.Add("speed dropdown closed");
        combo.PreviewMouseDown += (_, eventArgs) => traces.Add($"speed mouse down: clicks={eventArgs.ClickCount}");
        combo.PreviewMouseUp += (_, eventArgs) => traces.Add($"speed mouse up: clicks={eventArgs.ClickCount}");
        var pointerTimer = (System.Windows.Threading.DispatcherTimer)typeof(ReplaySeekOverlay)
            .GetField("pointerTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Overlay)!;
        var pointerTicks = 0;
        pointerTimer.Tick += (_, _) => pointerTicks++;
        try
        {
            tab.SetVideoHandle(fixture.Target.Handle);
            tab.SetVideoPlacement(true, 0, 0, 1, 1);
            await tab.StartAsync(settings);
            await TestWait.UntilAsync(() => tab.CanSeekReplay && tab.Status == PlaybackStatus.Playing,
                TimeSpan.FromSeconds(40), "The real Kick livestream must start with an available replay.");
            var engine = (LibVlcPlaybackEngine)typeof(StreamTabViewModel)
                .GetField("playbackEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            var requestedPosition = TimeSpan.FromSeconds(Math.Max(60, tab.ReplaySeekMaximum - 180));
            await tab.SeekReplayAsync(requestedPosition);
            Assert.True(tab.IsBehindLive && tab.CanChangePlaybackRate, tab.ErrorMessage);
            await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
                health.PositionMilliseconds >= requestedPosition.TotalMilliseconds - 5000 &&
                health.DisplayedPictures > 0, TimeSpan.FromSeconds(15),
                "The live Kick replay must present video at the requested position.");
            Assert.True(engine.TryGetPlaybackHealth(out var initial));
            Console.WriteLine($"Kick live source: channel={channel}, renderer={engine.RendererMode}, " +
                $"topmost={topmost}, target={requestedPosition.TotalSeconds:0.###}, " +
                $"position={initial.PositionMilliseconds / 1000d:0.###}, pictures={initial.DisplayedPictures}.");
            await NativeWindowTest.RequireForegroundAsync(fixture.OwnerHandle, TimeSpan.FromSeconds(2),
                "Live Kick speed selections require the player to be active");
            fixture.Overlay.ProcessPointerSample(new Point(100, 100), true, Environment.TickCount64);
            pointerTimer.Start();
            await Task.Delay(250);
            fixture.FlushBindings();
            var rateGate = (SemaphoreSlim)typeof(StreamTabViewModel)
                .GetField("playbackRateChangeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            var originalOverlay = fixture.NativeOverlayHandle;
            var overlaySource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(fixture.Chrome);
            overlaySource.AddHook((IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message is 0x0201 or 0x0202 or 0x0047)
                    traces.Add($"overlay message={message:X4}, tick={pointerTicks}");
                return IntPtr.Zero;
            });
            var rates = new[] { 4, 1, 5, 6, 0, 3, 2 };
            for (var iteration = 0; iteration < 35; iteration++)
            {
                var index = rates[iteration % rates.Length];
                traces.Clear();
                var center = combo.PointToScreen(new Point(combo.ActualWidth / 2, combo.ActualHeight / 2));
                var centerX = (int)Math.Round(center.X);
                var centerY = (int)Math.Round(center.Y);
                AssertLiveKickControlHit(fixture, centerX, centerY, $"before speed {iteration + 1}");
                await ClickLiveKickControlAsync(fixture, traces, centerX, centerY);
                await TestWait.UntilAsync(() => combo.IsDropDownOpen, TimeSpan.FromSeconds(2),
                    $"Speed dropdown {iteration + 1} must open after physical input: enabled={combo.IsEnabled}, " +
                    $"capture={Mouse.Captured?.GetType().Name}, timer={pointerTimer.IsEnabled}, ticks={pointerTicks}, " +
                    $"events={string.Join(", ", traces)}.");
                fixture.FlushBindings();
                var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
                var popupHandle = (PresentationSource.FromVisual(popup.Child) as System.Windows.Interop.HwndSource)?.Handle
                    ?? IntPtr.Zero;
                var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(index);
                Assert.NotNull(item);
                var choice = item.PointToScreen(new Point(item.ActualWidth / 2, item.ActualHeight / 2));
                var choiceX = (int)Math.Round(choice.X);
                var choiceY = (int)Math.Round(choice.Y);
                Console.WriteLine($"Kick speed {iteration + 1}: popup={popupHandle}, active={window.IsActive}, " +
                    $"open={fixture.Overlay.IsOverlayOpen}, opacity={fixture.Chrome.Opacity:0.###}, " +
                    NativeWindowTest.DescribeWindowAtPoint(choiceX, choiceY));
                Assert.True(popupHandle != IntPtr.Zero &&
                    NativeWindowHitTester.Instance.WindowFromPoint(choiceX, choiceY) == popupHandle,
                    $"The live Kick speed dropdown must receive selection {iteration + 1}. " +
                    $"Events: {string.Join(", ", traces)}. " + NativeWindowTest.DescribeWindowAtPoint(choiceX, choiceY));
                await ClickLiveKickControlAsync(fixture, traces, choiceX, choiceY);
                await TestWait.UntilAsync(() => !combo.IsDropDownOpen && tab.PlaybackRateIndex == index,
                    TimeSpan.FromSeconds(2));
                NativeWindowTest.SetCursorPosition(centerX, centerY);
                await TestWait.UntilAsync(() => rateGate.CurrentCount == 1, TimeSpan.FromSeconds(8));
                await Task.Delay(150);
                fixture.FlushBindings();
                Assert.True(window.IsActive && fixture.Overlay.IsOverlayOpen && fixture.Chrome.Opacity > 0.99,
                    $"Live Kick controls disappeared after speed {iteration + 1}. Events: {string.Join(", ", traces)}.");
                AssertLiveKickControlHit(fixture, centerX, centerY, $"after speed {iteration + 1}");
                Assert.Equal(originalOverlay, fixture.NativeOverlayHandle);
                var appliedIndex = (int)typeof(StreamTabViewModel)
                    .GetField("playbackRateIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
                Assert.Equal(index, appliedIndex);
                Assert.True(engine.TryGetPlaybackHealth(out var health));
                Console.WriteLine($"Kick speed {iteration + 1} applied: index={appliedIndex}, " +
                    $"position={health.PositionMilliseconds / 1000d:0.###}, pictures={health.DisplayedPictures}.");
                await WaitForLiveKickRateClockAsync(engine);
            }
            await Task.Delay(2500);
            fixture.FlushBindings();
            Assert.True(fixture.Overlay.IsOverlayOpen, "The seekbar must stay visible while the pointer rests on speed controls.");
            var finalCenter = combo.PointToScreen(new Point(combo.ActualWidth / 2, combo.ActualHeight / 2));
            AssertLiveKickControlHit(fixture, (int)Math.Round(finalCenter.X), (int)Math.Round(finalCenter.Y),
                "after the stationary pointer idle interval");
            Assert.Equal(originalOverlay, fixture.NativeOverlayHandle);
            Assert.True(engine.TryGetPlaybackHealth(out var final));
            Assert.True(final.PositionMilliseconds >= requestedPosition.TotalMilliseconds - 5000);
            Assert.True(final.DisplayedPictures > initial.DisplayedPictures + 30,
                "Repeated speed changes must leave real Kick video advancing.");
            Console.WriteLine($"Kick live final: position={final.PositionMilliseconds / 1000d:0.###}, " +
                $"pictures={final.DisplayedPictures}, active={window.IsActive}, open={fixture.Overlay.IsOverlayOpen}.");
            using var verified = CaptureReplayVlcSurface(fixture.Target);
            SaveReplayVlcArtifact(verified, VideoRendererMode.Automatic, $"kick-{topmost}-verified");
        }
        catch
        {
            try
            {
                using (var failed = CaptureReplayVlcSurface(fixture.Target))
                    SaveReplayVlcArtifact(failed, VideoRendererMode.Automatic, $"kick-{topmost}-failure");
                await Task.Delay(2500);
                Console.WriteLine($"Kick failure after idle: active={window.IsActive}, " +
                    $"open={fixture.Overlay.IsOverlayOpen}, opacity={fixture.Chrome.Opacity:0.###}, " +
                    $"timer={pointerTimer.IsEnabled}, ticks={pointerTicks}, " +
                    $"events={string.Join(", ", traces)}.");
                using var idle = CaptureReplayVlcSurface(fixture.Target);
                SaveReplayVlcArtifact(idle, VideoRendererMode.Automatic, $"kick-{topmost}-failure-idle");
            }
            catch (Exception diagnosticError)
            {
                Console.WriteLine($"Kick failure capture: {diagnosticError.Message}");
            }
            throw;
        }
        finally
        {
            foreach (var entry in logger.Entries.Where(entry => entry.Level >= AppLogLevel.Warning))
                Console.WriteLine($"{entry.Source}: {entry.Message}");
            fixture.StopPointerSampling();
            NativeWindowTest.SetCursorPosition(originalCursor.X, originalCursor.Y);
            await tab.StopAsync();
        }
    });

    private static async Task WaitForLiveKickRateClockAsync(LibVlcPlaybackEngine engine)
    {
        var engineType = typeof(LibVlcPlaybackEngine);
        if (!(bool)engineType.GetField("playbackRateResynchronizationPending",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!) return;
        var nativeTarget = (long)engineType.GetField("lastPlaybackRateResynchronizationTimeMilliseconds",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        var source = (PlaybackMediaSource?)engineType.GetField("currentMediaSource",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine);
        var target = nativeTarget + (long)(source?.TimelineOffset.TotalMilliseconds ?? 0) + 250;
        Console.WriteLine($"Kick native rate clock waiting: targetMs={target}.");
        await TestWait.UntilAsync(() => engine.TryGetPlaybackHealth(out var health) &&
            health.State == PlaybackEngineState.Playing && health.PositionMilliseconds >= target,
            TimeSpan.FromSeconds(45),
            $"The native HLS clock must recover beyond its actual audio resynchronization target {target} before the next speed selection.");
        Assert.True(engine.TryGetPlaybackHealth(out var recovered));
        Console.WriteLine($"Kick native rate clock recovered: positionMs={recovered.PositionMilliseconds}, " +
            $"pictures={recovered.DisplayedPictures}.");
    }

    private static async Task ClickLiveKickControlAsync(
        ReplayOverlayTestHost fixture, List<string> traces, int screenX, int screenY)
    {
        NativeWindowTest.SetCursorPosition(screenX, screenY);
        await Task.Delay(50);
        NativeWindowTest.TryGetCursorPosition(out var cursor);
        traces.Add($"physical down: cursor={cursor}, hit=" +
            $"{NativeWindowHitTester.Instance.WindowFromPoint(screenX, screenY)}, " +
            $"overlay={fixture.NativeOverlayHandle}, " +
            $"aboveRenderer={fixture.Target.IsOverlayAboveRenderer(fixture.NativeOverlayHandle)}, " +
            $"capture={NativeWindowTest.GetCapture()}, foreground={NativeWindowTest.GetForegroundWindow()}");
        ReplayVlcMouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        try
        {
            await Task.Delay(50);
        }
        finally
        {
            ReplayVlcMouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        }
    }

    private static void AssertLiveKickControlHit(ReplayOverlayTestHost fixture, int screenX, int screenY, string phase)
    {
        var hit = NativeWindowHitTester.Instance.WindowFromPoint(screenX, screenY);
        var overlay = fixture.NativeOverlayHandle;
        Assert.True(fixture.Overlay.IsOverlayOpen && fixture.NativeOverlayHandle != IntPtr.Zero &&
            hit == overlay,
            $"Live Kick seekbar must receive input {phase}: open={fixture.Overlay.IsOverlayOpen}, " +
            $"opacity={fixture.Chrome.Opacity:0.###}, overlay={overlay}, " +
            $"aboveRenderer={fixture.Target.IsOverlayAboveRenderer(overlay)}, " +
            $"overlayVisible={NativeWindowTest.IsWindowVisible(overlay)}, " +
            $"hitClass={NativeWindowTest.GetClassName(hit)}, " +
            $"overlayBounds={NativeWindowTest.GetWindowBounds(overlay)}, " +
            $"surfaceBounds={NativeWindowTest.GetWindowBounds(fixture.Target.Handle)}, " +
            NativeWindowTest.DescribeWindowAtPoint(screenX, screenY));
    }
}
