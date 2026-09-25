using StreamlinkVlcStudio.App.Wpf.Twitch;

internal static partial class TwitchChannelPointsTestCatalog
{
    private static Task SignInIndicatorAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture(signedIn: false);
        Assert.True(fixture.Controller.RequiresSignIn);
        Assert.Contains("Not signed in", fixture.Controller.SignInStatus);
        fixture.Controller.Enabled = false;
        fixture.Controller.OpenPageCommand.Execute(null);
        Assert.Contains("Not signed in", fixture.Controller.SignInStatus);
        await fixture.Controller.SignInCommand.ExecuteAsync();
        Assert.True(fixture.Controller.HasSavedSession);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pending = new Fixture(signedIn: false, sessionGate: release.Task);
        pending.Controller.Enabled = false;
        Assert.Contains("Checking", pending.Controller.SignInStatus);
        Assert.Equal(false, pending.Controller.RequiresSignIn);
        Assert.Equal(false, pending.Controller.SignInCommand.CanExecute(null));
        release.SetResult();
        await Eventually(() => pending.Controller.RequiresSignIn);
        Assert.Contains("Not signed in", pending.Controller.SignInStatus);
        Assert.Contains("off", pending.Controller.Status);
        Assert.Equal(false, fixture.Controller.RequiresSignIn);
        Assert.Equal(0, fixture.Browser.OpenAttempts);
        Assert.Contains("off", fixture.Controller.Status);
        await fixture.Controller.SignOutCommand.ExecuteAsync();
        Assert.True(fixture.Controller.RequiresSignIn);

        fixture.Browser.FailSession = true;
        await fixture.Controller.RetryCommand.ExecuteAsync();
        Assert.Contains("Could not check", fixture.Controller.SignInStatus);
        Assert.Equal(false, fixture.Controller.RequiresSignIn);
        Assert.Equal(false, fixture.Controller.HasSavedSession);
        fixture.Browser.FailSession = false;
        await fixture.Controller.SignInCommand.ExecuteAsync();
        fixture.Controller.Enabled = true;
        var tab = fixture.Add("alpha");
        SetStatus(tab, PlaybackStatus.Playing);
        await Eventually(() => fixture.Browser.Pages.Count == 1);
        fixture.Browser.Pages[0].Expired = true;
        await Eventually(() => fixture.Controller.RequiresSignIn);
        SetStatus(tab, PlaybackStatus.Stopped);
        fixture.Controller.OpenPageCommand.Execute(null);
        Assert.Contains("Session expired", fixture.Controller.SignInStatus);
        fixture.Controller.Enabled = false;
        Assert.Contains("Session expired", fixture.Controller.SignInStatus);
        await fixture.Controller.SignInCommand.ExecuteAsync();
        Assert.True(fixture.Controller.HasSavedSession);
    });

    private static Task ClaimCountsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bonus-history-{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(path);
            await using (var fixture = new Fixture(settingsService: service))
            {
                var alpha = fixture.Add("ALPHA");
                var duplicate = fixture.Add("alpha");
                SetStatus(alpha, PlaybackStatus.Playing);
                SetStatus(duplicate, PlaybackStatus.Paused);
                SetStatus(fixture.Add("bravo"), PlaybackStatus.Playing);
                fixture.Add("vod", kind: StreamTargetKind.TwitchVod);
                fixture.Add("kick", platform: PlatformKind.Kick);
                Assert.Equal(2, fixture.Controller.ChannelClaims.Count);
                Assert.True(fixture.Controller.ChannelClaims.All(row => row.Count == 0));
                await Eventually(() => fixture.Browser.Pages.Count == 2);
                var firstPage = fixture.Browser.Pages.Single(page => page.Channel == "alpha");
                firstPage.Result = "Bonus claim clicked; waiting for Twitch.";
                await Eventually(() => firstPage.Checks > 2);
                Assert.Equal(0L, fixture.Controller.ChannelClaims.Single(row => row.Channel == "alpha").Count);
                firstPage.Confirm("claim-1");
                firstPage.Confirm("claim-1");
                firstPage.Confirm("claim-2");
                fixture.Browser.Pages.Single(page => page.Channel == "bravo").Confirm("bravo-1");
                Assert.Equal(2L, fixture.Controller.ChannelClaims.Single(row => row.Channel == "alpha").Count);
                Assert.Equal(1L, fixture.Controller.ChannelClaims.Single(row => row.Channel == "bravo").Count);
                fixture.Controller.Enabled = false;
                firstPage.Confirm("late-response");
                fixture.Controller.Enabled = true;
                await Eventually(() => fixture.Browser.Pages.Count == 4);
                var newPage = fixture.Browser.Pages.Last(page => page.Channel == "alpha");
                newPage.Confirm("claim-2");
                newPage.Confirm("claim-3");
                firstPage.Confirm("stale-page");
                Assert.Equal(3L, fixture.Settings.TwitchBonusClaims["alpha"].Count);
                fixture.Tabs.Clear();
                Assert.Equal(2, fixture.Controller.ChannelClaims.Count);
                Assert.Equal(3L, (await service.LoadAsync()).TwitchBonusClaims["alpha"].Count);
            }
            await using var restarted = new Fixture(settings: await service.LoadAsync(), settingsService: service);
            Assert.Equal(3L, restarted.Controller.ChannelClaims.Single(row => row.Channel == "alpha").Count);
            SetStatus(restarted.Add("alpha"), PlaybackStatus.Playing);
            await Eventually(() => restarted.Browser.Pages.Count == 1);
            restarted.Browser.Pages[0].Confirm("claim-3");
            restarted.Browser.Pages[0].Confirm("claim-4");
            await restarted.Controller.SignOutCommand.ExecuteAsync();
            Assert.Equal(4L, restarted.Settings.TwitchBonusClaims["alpha"].Count);
            Assert.Equal(4L, (await service.LoadAsync()).TwitchBonusClaims["alpha"].Count);
        }
        finally { System.IO.File.Delete(path); }
    });

    private static Task ClaimSaveFailureAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var service = new BonusHistorySettingsService { FailSave = true };
        await using var fixture = new Fixture(settingsService: service);
        SetStatus(fixture.Add("alpha"), PlaybackStatus.Playing);
        await Eventually(() => fixture.Browser.Pages.Count == 1);
        fixture.Browser.Pages[0].Confirm("claim-1");
        Assert.Equal(1L, fixture.Settings.TwitchBonusClaims["alpha"].Count);
        Assert.Contains("could not be saved", fixture.Controller.HistorySaveStatus);
        service.FailSave = false;
        await fixture.Controller.RetryCommand.ExecuteAsync();
        Assert.Equal("", fixture.Controller.HistorySaveStatus);
        Assert.Equal(1L, (await service.LoadAsync()).TwitchBonusClaims["alpha"].Count);
    });

    private static string ClaimRequest(string id) => JsonSerializer.Serialize(new
    {
        operationName = "ClaimCommunityPoints",
        variables = new { input = new { channelID = "123", claimID = id } }
    });

    private static string ClaimSuccess(string id) => JsonSerializer.Serialize(new
    {
        data = new
        {
            claimCommunityPoints = new
            {
                error = (object?)null,
                currentPoints = 150,
                claim = new { id, pointsEarnedTotal = 50, pointsEarnedBaseline = 50, multipliers = Array.Empty<object>() }
            }
        }
    });

    private static Task ClaimResponseAsync()
    {
        var request = ClaimRequest("one");
        var success = ClaimSuccess("one");
        Assert.Equal("one", TwitchBonusClaimResponse.ReadConfirmedClaims(request, success).Single());
        Assert.Equal(2, TwitchBonusClaimResponse.ReadConfirmedClaims(
            $"[{request},{ClaimRequest("two")}]", $"[{success},{ClaimSuccess("two")}]").Count);
        Assert.Equal(1, TwitchBonusClaimResponse.ReadConfirmedClaims($"[{request},{request}]", $"[{success},{success}]").Count);
        foreach (var response in new[]
        {
            "", "null", "[]", "true", "42", "{", "{}", "{\"data\":null}",
            "{\"data\":{\"claimCommunityPoints\":null}}", ClaimSuccess("different"),
            success.Replace("\"error\":null", "\"error\":{\"code\":\"NOT_FOUND\"}"),
            success.Replace("\"pointsEarnedTotal\":50", "\"pointsEarnedTotal\":0"),
            success.Replace("\"pointsEarnedTotal\":50", "\"pointsEarnedTotal\":\"50\""),
            success.Replace("\"error\":null,", ""),
            "{\"errors\":[{\"message\":\"rejected\"}]," + success[1..],
            $"[{success}]"
        }) Assert.Equal(0, TwitchBonusClaimResponse.ReadConfirmedClaims(request, response).Count);
        Assert.Equal(0, TwitchBonusClaimResponse.ReadConfirmedClaims(ClaimRequest("two"), success).Count);
        Assert.Equal(0, TwitchBonusClaimResponse.ReadConfirmedClaims(request.Replace("ClaimCommunityPoints", "Other"), success).Count);
        Assert.Equal(0, TwitchBonusClaimResponse.ReadConfirmedClaims("not json", success).Count);
        foreach (var address in new[] { "http://gql.twitch.tv/gql", "https://gql.twitch.tv.evil.test/gql",
            "https://www.twitch.tv/gql", "https://gql.twitch.tv:444/gql", "https://user@gql.twitch.tv/gql" })
            Assert.Equal(false, TwitchBonusClaimResponse.IsGraphQlEndpoint(address));
        Assert.True(TwitchBonusClaimResponse.IsGraphQlEndpoint("https://gql.twitch.tv/gql"));
        return Task.CompletedTask;
    }

    private static Task ClaimHistorySettingsAsync()
    {
        Assert.Equal(0, JsonSerializer.Deserialize<AppSettings>("{}")!.TwitchBonusClaims.Count);
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"TwitchBonusClaims":{" ALPHA ":{"Count":3,"RecentClaimIds":["a","a",null,""]},
            "alpha":{"Count":2},"bravo":{"Count":-1,"RecentClaimIds":null},"bad/channel":{"Count":5},"null":null}}
            """)!;
        Assert.Equal(2, settings.TwitchBonusClaims.Count);
        Assert.Equal(3L, settings.TwitchBonusClaims["alpha"].Count);
        Assert.Equal(1, settings.TwitchBonusClaims["alpha"].RecentClaimIds.Count);
        Assert.Equal(0L, settings.TwitchBonusClaims["bravo"].Count);
        var history = new TwitchBonusClaimHistory();
        for (var i = 0; i < 100; i++) history = history.Record($"claim-{i}");
        Assert.Equal(100L, history.Count);
        Assert.Equal(64, history.RecentClaimIds.Count);
        Assert.Equal(long.MaxValue, (history with { Count = long.MaxValue }).Record("new").Count);
        return Task.CompletedTask;
    }

    private static Task IndicatorsUiAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        await using var fixture = new Fixture(signedIn: false);
        fixture.Add("abcdefghijklmnopqrstuvwxy");
        var window = new MainWindow();
        ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(window);
        var panel = (StackPanel)window.FindName("TwitchBonusesPanel");
        var status = (TextBlock)window.FindName("TwitchBonusSignInStatus");
        var claims = (ItemsControl)window.FindName("TwitchBonusChannelClaims");
        ((Panel)panel.Parent).Children.Remove(panel);
        var host = new Border
        {
            Child = panel,
            Padding = new Thickness(16),
            Resources = window.Resources,
            Background = WpfVisualTest.PaletteBrush(window, "StudioSurface0Brush")
        };
        TextElement.SetForeground(host, WpfVisualTest.PaletteBrush(window, "StudioTextBrush"));
        panel.DataContext = fixture.Controller;
        try
        {
            foreach (var width in new[] { 360, 580 })
            {
                Layout();
                Assert.Contains("Not signed in", status.Text);
                Assert.Equal(1, claims.Items.Count);
                Assert.True(status.ActualWidth > 0 && status.ActualHeight > 0);
                Save($"bonus-indicators-signed-out-{width}");
                void Layout()
                {
                    host.Measure(new Size(width, double.PositiveInfinity));
                    host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
                    host.UpdateLayout();
                }
            }
            await fixture.Controller.SignInCommand.ExecuteAsync();
            SetStatus(fixture.Tabs.Single(), PlaybackStatus.Playing);
            await Eventually(() => fixture.Browser.Pages.Count == 1);
            fixture.Browser.Pages[0].Confirm("ui-confirmed");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
            host.Measure(new Size(580, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 580, host.DesiredSize.Height));
            host.UpdateLayout();
            Assert.Contains("session saved", status.Text);
            Assert.Equal(1L, ((TwitchChannelPointsController.ChannelClaimSummary)claims.Items[0]).Count);
            var row = (ContentPresenter)claims.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Contains("1 claimed", string.Join(" ", FindText(row)));
            Save("bonus-indicators-confirmed");
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Light);
            // Detached offscreen trees do not receive Application window resource
            // invalidation. Load the production controls afresh for the light palette.
            var lightWindow = new MainWindow();
            ApplicationTestCatalog.RemoveMainWindowAutomaticStartup(lightWindow);
            try
            {
                var lightPanel = (StackPanel)lightWindow.FindName("TwitchBonusesPanel");
                ((Panel)lightPanel.Parent).Children.Remove(lightPanel);
                var lightHost = new Border
                {
                    Child = lightPanel,
                    Padding = new Thickness(16),
                    Resources = lightWindow.Resources,
                    Background = WpfVisualTest.PaletteBrush(lightWindow, "StudioSurface0Brush")
                };
                TextElement.SetForeground(lightHost, WpfVisualTest.PaletteBrush(lightWindow, "StudioTextBrush"));
                lightPanel.DataContext = fixture.Controller;
                lightHost.Measure(new Size(580, double.PositiveInfinity));
                lightHost.Arrange(new Rect(0, 0, 580, lightHost.DesiredSize.Height));
                lightHost.UpdateLayout();
                Assert.True(lightHost.ActualHeight > 100);
                var description = lightPanel.Children.OfType<TextBlock>().Single(text => text.Text.StartsWith("Successful bonus claims", StringComparison.Ordinal));
                Assert.Equal(WpfVisualTest.PaletteBrush(lightWindow, "StudioTextSecondaryBrush").ToString(), description.Foreground.ToString());
                Save("bonus-indicators-confirmed-light", lightHost);
            }
            finally { lightWindow.Close(); }
        }
        finally
        {
            StreamlinkVlcStudio.App.Wpf.Themes.ThemeManager.ApplyTheme(AppTheme.Dark);
            window.Close();
        }

        void Save(string name, FrameworkElement? visual = null)
        {
            var directory = Environment.GetEnvironmentVariable("SVS_TEST_ARTIFACT_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            System.IO.Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(WpfVisualTest.Render(visual ?? host)));
            using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
            encoder.Save(output);
        }

        static IEnumerable<string> FindText(DependencyObject element)
        {
            if (element is TextBlock text) yield return text.Text;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                foreach (var value in FindText(VisualTreeHelper.GetChild(element, i))) yield return value;
        }
    });

    private sealed class BonusHistorySettingsService : ISettingsService
    {
        internal bool FailSave;
        private string saved = "{}";
        public string SettingsPath => "memory";
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize<AppSettings>(saved)!);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new System.IO.IOException("test save failure");
            saved = JsonSerializer.Serialize(settings);
            return Task.CompletedTask;
        }
    }
}
