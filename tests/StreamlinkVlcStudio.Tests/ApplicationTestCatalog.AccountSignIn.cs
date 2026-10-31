using System.Windows.Automation;
using System.Windows.Threading;
using StreamlinkVlcStudio.App.Wpf.Themes;

internal static partial class ApplicationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> AccountSignInTests { get; } =
    [
        ("account sign-in: saved tokens and clear commands determine status independently of usernames", AccountSignInCredentialsAsync),
        ("account sign-in: Kick expiry respects managed and personal refresh credentials", AccountSignInKickRefreshAsync),
        ("account sign-in: badges, account names and accessible labels update in wide and compact layouts", AccountSignInLayoutAsync),
        ("account sign-in: expiry and background token changes notify the UI and settings replacement detaches old credentials", AccountSignInNotificationsAsync)
    ];

    private static Task AccountSignInCredentialsAsync() => WithStudioPolishWindowAsync(async (_, main, _) =>
    {
        var chat = main.Settings.Chat;
        chat.TwitchUsername = "saved-twitch-user";
        chat.KickUsername = "saved-kick-user";
        Assert.Equal("Signed out", main.TwitchAccountSignInStatus);
        Assert.Equal("Signed out", main.KickAccountSignInStatus);
        foreach (var emptyToken in new[] { " ", "oauth:", "Bearer ", "oauth " })
        {
            chat.TwitchOAuthToken = emptyToken;
            chat.KickOAuthToken = emptyToken;
            Assert.Equal(false, main.IsTwitchSignedIn);
            Assert.Equal(false, main.IsKickSignedIn);
        }
        foreach (var token in new[] { "token", " oauth:token ", " Bearer token ", " oauth token " })
        {
            chat.TwitchOAuthToken = token;
            chat.KickOAuthToken = token;
            Assert.True(main.IsTwitchSignedIn);
            Assert.True(main.IsKickSignedIn);
        }

        TwitchOAuthService.ApplyTokenResult(chat, new TwitchOAuthTokenResult(
            "twitch-access", "connected-twitch-user", DateTimeOffset.UtcNow.AddHours(1), "bearer", [], "client"));
        KickOAuthService.ApplyTokenResult(chat, new KickOAuthTokenResult(
            "kick-access", "kick-refresh", DateTimeOffset.UtcNow.AddHours(1), "bearer", []));
        Assert.Equal("Signed in", main.TwitchAccountSignInStatus);
        Assert.Equal("Signed in", main.KickAccountSignInStatus);

        chat.TwitchTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(false, main.IsTwitchSignedIn);
        Assert.Contains("expired", main.TwitchAccountSignInDescription);
        chat.TwitchTokenExpiresAtUtc = null;
        Assert.True(main.IsTwitchSignedIn, "Legacy manual tokens without an expiry remain supported.");

        await main.ClearTwitchTokenCommand.ExecuteAsync();
        Assert.Equal("Signed out", main.TwitchAccountSignInStatus);
        Assert.True(main.IsKickSignedIn);
        Assert.Equal("connected-twitch-user", chat.TwitchUsername);
        await main.ClearKickTokenCommand.ExecuteAsync();
        Assert.Equal("Signed out", main.KickAccountSignInStatus);
        Assert.Equal("saved-kick-user", chat.KickUsername);
    });

    private static Task AccountSignInKickRefreshAsync() => WithStudioPolishWindowAsync((_, main, _) =>
    {
        var chat = main.Settings.Chat;
        chat.KickOAuthToken = "expired-kick-token";
        chat.KickTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(false, main.IsKickSignedIn);
        Assert.Contains("renewed", main.KickAccountSignInDescription);
        chat.KickRefreshToken = "refresh-token";
        Assert.Equal(false, main.IsKickSignedIn);
        chat.KickClientId = "personal-client";
        Assert.Equal(false, main.IsKickSignedIn);
        chat.KickClientSecret = "personal-secret";
        Assert.True(main.IsKickSignedIn);
        chat.KickOAuthToken = "";
        Assert.True(main.IsKickSignedIn, "A complete refresh connection can restore its access token.");

        chat.KickUseTokenService = true;
        Assert.True(!main.IsKickSignedIn, "A personal client cannot use the publisher's token service.");
        chat.KickClientId = AccountConnectionConfiguration.Current.KickClientId;
        chat.KickClientSecret = "";
        Assert.True(main.IsKickSignedIn);
        chat.KickRefreshToken = "";
        Assert.True(!main.IsKickSignedIn, "Client credentials alone are not a signed-in user.");
        return Task.CompletedTask;
    });

    private static Task AccountSignInLayoutAsync() => WithStudioPolishWindowAsync((window, main, _) =>
    {
        main.IsSettingsOpen = true;
        main.SelectedSettingsCategory = SettingsCategory.Accounts;
        var chat = main.Settings.Chat;
        chat.TwitchUsername = "twitch-user";
        chat.KickUsername = "kick-user";
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeManager.ApplyTheme(theme);
                foreach (var size in new[] { new Size(1320, 820), new Size(700, 640), new Size(500, 720) })
                {
                    foreach (var signedIn in new[] { false, true })
                    {
                        chat.TwitchOAuthToken = signedIn ? "twitch-token" : "";
                        chat.KickOAuthToken = signedIn ? "kick-token" : "";
                        LayoutStudioPolishWindow(window, size);
                        foreach (var platform in new[] { "Twitch", "Kick" })
                        {
                            var status = (TextBlock)window.FindName(platform + "AccountSignInStatus");
                            var username = (TextBlock)window.FindName(platform + "AccountUsername");
                            var badge = (Border)((StackPanel)status.Parent).Parent;
                            Assert.Equal(signedIn ? "Signed in" : "Signed out", status.Text);
                            Assert.Equal($"{platform} account: {status.Text}", AutomationProperties.GetName(status));
                            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                            Assert.Equal(signedIn ? Visibility.Visible : Visibility.Collapsed, username.Visibility);
                            Assert.True(status.ActualHeight > 0 && status.ActualWidth > 0);
                            var bounds = status.TransformToAncestor(badge).TransformBounds(new Rect(status.RenderSize));
                            Assert.True(bounds.Right <= badge.ActualWidth && bounds.Bottom <= badge.ActualHeight,
                                $"{platform} status is clipped at {size}.");
                            Assert.Equal(((SolidColorBrush)window.FindResource(signedIn
                                ? "StudioSuccessTextBrush" : "StudioTextMutedBrush")).Color,
                                ((SolidColorBrush)status.Foreground).Color);
                        }
                        SaveResponsiveWindowImage(window, $"accounts-{theme}-{size.Width}-{(signedIn ? "in" : "out")}");
                    }
                }
            }
        }
        finally { ThemeManager.ApplyTheme(AppTheme.Dark); }
        return Task.CompletedTask;
    });

    private static Task AccountSignInNotificationsAsync() => TestSta.RunOffscreenAsync(async () =>
    {
        var settings = new AppSettings();
        var dispatcher = Dispatcher.CurrentDispatcher;
        await using var main = TestViewModels.CreateMain(settings, new FakeSettingsService(settings),
            new FakeStreamlinkService(), new FakePlaybackEngineFactory(), new FakeChatClientFactory(),
            new MemoryLogger(), action => dispatcher.BeginInvoke(action));
        var notifications = new List<string>();
        var observedTwitchStatus = main.TwitchAccountSignInStatus;
        var observedKickStatus = main.KickAccountSignInStatus;
        main.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(MainViewModel.TwitchAccountSignInStatus) or nameof(MainViewModel.KickAccountSignInStatus))
            {
                Assert.True(dispatcher.CheckAccess(), "Account notifications must run on the UI dispatcher.");
                notifications.Add(change.PropertyName);
                observedTwitchStatus = main.TwitchAccountSignInStatus;
                observedKickStatus = main.KickAccountSignInStatus;
            }
        };
        var oldChat = settings.Chat;
        await Task.Run(() => oldChat.TwitchOAuthToken = "worker-token");
        await TestWait.UntilAsync(() => notifications.Count > 0, TimeSpan.FromSeconds(3));
        Assert.True(main.IsTwitchSignedIn);

        main.IsSettingsOpen = true;
        main.SelectedSettingsCategory = SettingsCategory.Accounts;
        oldChat.TwitchTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
        oldChat.KickOAuthToken = "kick-token";
        oldChat.KickTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(400);
        await TestWait.UntilAsync(() => observedKickStatus == "Signed in", TimeSpan.FromSeconds(3));
        // Wait for notifications, not computed getters, to prove expiry updates existing bindings.
        await TestWait.UntilAsync(() => observedTwitchStatus == "Signed out" && observedKickStatus == "Signed out",
            TimeSpan.FromSeconds(3));
        Assert.Equal(false, main.IsTwitchSignedIn);

        settings.Chat = new ChatSettings { TwitchOAuthToken = "replacement-token" };
        Assert.True(main.IsTwitchSignedIn);
        Assert.Equal(false, main.IsKickSignedIn);
        notifications.Clear();
        await Task.Run(() => oldChat.TwitchOAuthToken = "detached-token");
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, notifications.Count);

        await main.DisposeAsync();
        settings.Chat.TwitchOAuthToken = "after-disposal";
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, notifications.Count);
    });
}
