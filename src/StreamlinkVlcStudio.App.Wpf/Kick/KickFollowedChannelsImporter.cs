using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StreamlinkVlcStudio.App.Wpf.Twitch;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Viewers;

namespace StreamlinkVlcStudio.App.Wpf.Kick;

internal sealed class KickFollowedChannelsImporter(Window owner) : IKickFollowedChannelsImporter
{
    internal static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.ProductDirectoryName, "KickFollowsWebView2");

    public async Task<IReadOnlyList<string>?> ImportAsync(CancellationToken cancellationToken = default)
    {
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<string>? imported = null;
        var closed = false;
        using var browser = new WebView2();
        var status = new TextBlock
        {
            Text = "Sign in to Kick below, then select Import follows. Live and offline follows will be detected.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        var import = new Button { Content = "Import follows", IsEnabled = false, Margin = new Thickness(6), Padding = new Thickness(14, 7, 14, 7) };
        var signOut = new Button { Content = "Clear Kick sign-in", IsEnabled = false, Margin = new Thickness(6), Padding = new Thickness(14, 7, 14, 7) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(6), Padding = new Thickness(14, 7, 14, 7) };
        var buttons = new WrapPanel { Margin = new Thickness(6) };
        buttons.Children.Add(import);
        buttons.Children.Add(signOut);
        buttons.Children.Add(cancel);
        var layout = new DockPanel();
        DockPanel.SetDock(status, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Top);
        layout.Children.Add(status);
        layout.Children.Add(buttons);
        layout.Children.Add(browser);
        var window = new Window
        {
            Owner = owner,
            Title = "Detect Kick follows",
            Width = 1100,
            Height = 800,
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout
        };
        void SetBusy(bool busy)
        {
            import.IsEnabled = !busy;
            signOut.IsEnabled = !busy;
            browser.IsEnabled = !busy;
        }
        window.Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            completion.TrySetResult();
        };
        cancel.Click += (_, _) => window.Close();
        import.Click += async (_, _) =>
        {
            SetBusy(true);
            status.Text = "Detecting followed channels…";
            try
            {
                var client = new KickFollowedChannelsBrowserClient(browser.CoreWebView2);
                var result = await KickFollowedChannelsReader.ReadAllAsync(
                    (url, ct) => owner.Dispatcher.InvokeAsync(() => client.ReadPageAsync(url, ct)).Task.Unwrap(),
                    new Progress<int>(count => { if (!closed) status.Text = $"Detected {count} follows; checking remaining pages…"; }),
                    token);
                token.ThrowIfCancellationRequested();
                imported = result;
                window.Close();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
            finally { if (!closed) SetBusy(false); }
        };
        signOut.Click += async (_, _) =>
        {
            SetBusy(true);
            try
            {
                browser.CoreWebView2.Navigate("about:blank");
                await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile)
                    .WaitAsync(TimeSpan.FromSeconds(30), token);
                token.ThrowIfCancellationRequested();
                browser.CoreWebView2.Navigate("https://kick.com/");
                status.Text = "Kick sign-in cleared. Sign in to the account whose follows you want to import.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { if (!closed) status.Text = ex.Message; }
            finally { if (!closed) SetBusy(false); }
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                CoreWebView2Environment.GetAvailableBrowserVersionString();
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfileDirectory)
                    .WaitAsync(TimeSpan.FromSeconds(30), token);
                token.ThrowIfCancellationRequested();
                await browser.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(30), token);
                token.ThrowIfCancellationRequested();
                ConfigureBrowser(browser.CoreWebView2);
                browser.CoreWebView2.Navigate("https://kick.com/");
                SetBusy(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                if (!closed) status.Text = "The Kick sign-in browser could not start. Install or repair Microsoft Edge WebView2 Runtime, then try again.";
            }
        };
        using var registration = token.Register(() => owner.Dispatcher.BeginInvoke(() => { if (!closed) window.Close(); }));
        window.Show();
        await completion.Task;
        cancellationToken.ThrowIfCancellationRequested();
        return imported;
    }

    internal static void ConfigureBrowser(CoreWebView2 core)
    {
        core.IsMuted = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.NavigationStarting += (_, args) => args.Cancel = !IsAllowedNavigation(args.Uri);
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated && IsAllowedNavigation(args.Uri)) core.Navigate(args.Uri);
        };
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (TwitchBonusBrowser.IsStreamResource(args.Request.Uri, args.ResourceContext))
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Media disabled", "Cache-Control: no-store");
        };
    }

    internal static bool IsAllowedNavigation(string address) => address == "about:blank" ||
        (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
         uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host is "kick.com" or "www.kick.com" or "id.kick.com");
}
