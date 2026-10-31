using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;
using StreamlinkVlcStudio.Infrastructure.Vod;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class MainWindow
{
    private LocalWebDependencies? localWebDependencies;
    private LocalWebServer? localWebServer;
    private LocalWebUserStore? localWebUsers;
    private LocalWebCertificatePair? localWebCertificates;
    private Window? localWebWindow;
    private bool localWebWindowOpening;
    private Action? refreshLocalWebWindow;
    private CancellationTokenSource? localWebStartup;

    private void ConfigureLocalWebServer(AppSettings settings, IStreamlinkService streamlink, IStreamSearchService search,
        IFollowedStreamsService following, IBrowseService browse, ITwitchVodService twitchVods, IKickVodService kickVods,
        IVodDownloadService downloads, IVodPlaybackHistory history, IVodChatProvider vodChat, IReplayResolver replay,
        ITwitchSubOnlyVodResolver subOnlyVods, ITwitchClipService twitchClips, IKickClipService kickClips,
        IChannelFollowService follows, IAppLogger logger)
    {
        localWebDependencies = new()
        {
            // Use the same account state as desktop services so refreshed credentials are persisted.
            GetSettingsAsync = token => Dispatcher.InvokeAsync(() => settings, DispatcherPriority.Background, token).Task,
            ChangeSettingsAsync = (change, token) => Dispatcher.InvokeAsync(async () =>
            {
                change(settings);
                if (settingsService is not null) await settingsService.SaveAsync(settings, token);
            }, DispatcherPriority.Background, token).Task.Unwrap(),
            Streamlink = streamlink,
            Search = search,
            Following = following,
            Browse = browse,
            TwitchVods = twitchVods,
            KickVods = kickVods,
            Downloads = downloads,
            History = history,
            VodChat = vodChat,
            Replay = replay,
            SubOnlyVods = subOnlyVods,
            ResolveKickVodAsync = new KickVodDownloadResolver(logger).ResolveAsync,
            ChatFactory = snapshot => new ChatClientFactory(snapshot, logger),
            ChatPresentation = Chat.LocalWebChatPresentation.Shared,
            CreateClipAsync = (target, token) => Dispatcher.InvokeAsync(async () => target.Platform == PlatformKind.Twitch
                ? (await twitchClips.CreateLiveClipAsync(target, settings.Chat, token)).ClipUri
                : (await kickClips.CreateLiveClipAsync(target, token))?.ClipUri, DispatcherPriority.Normal, token).Task.Unwrap(),
            GetFollowStatusAsync = (target, token) => Dispatcher.InvokeAsync(() => follows.GetFollowStatusAsync(target, token),
                DispatcherPriority.Background, token).Task.Unwrap(),
            FollowAsync = (target, desired, token) => Dispatcher.InvokeAsync(async () =>
            {
                var result = desired ? await follows.FollowAsync(target, token) : await follows.UnfollowAsync(target, token);
                if (result?.KickFollowedChannels is { } channels)
                {
                    settings.FollowedChannels.KickImportedChannelSlugs = channels.ToList();
                    settings.FollowedChannels.KickFollowsImportedAtUtc = DateTimeOffset.UtcNow;
                    if (settingsService is not null) await settingsService.SaveAsync(settings, token);
                }
                return result;
            }, DispatcherPriority.Normal, token).Task.Unwrap()
        };
    }

    private async void LocalWebServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (localWebDependencies is null || shutdownStarted || localWebWindowOpening) return;
        if (localWebWindow is not null) { localWebWindow.Activate(); return; }
        localWebWindowOpening = true;
        try { localWebUsers ??= await LocalWebUserStore.OpenAsync(); }
        catch (Exception error)
        {
            MessageBox.Show(this, $"Could not load web accounts: {error.Message}", "LAN browser server", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { localWebWindowOpening = false; }
        if (shutdownStarted) return;
        var window = new LocalWebServerWindow { Owner = this };
        localWebWindow = window;
        var networks = window.Networks;
        networks.ItemsSource = LocalWebInterface.GetAvailable();
        networks.SelectedItem = networks.Items.Cast<LocalWebInterface>().FirstOrDefault(n => n.Address.ToString() == localWebServer?.Address.Host)
            ?? networks.Items.Cast<LocalWebInterface>().FirstOrDefault();
        var port = window.Port;
        if (localWebServer is not null) port.Text = localWebServer.Address.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var status = window.Status;
        var start = window.Start;
        var stop = window.Stop;
        var rotate = window.Rotate;
        var export = window.Export;
        var users = window.Users;
        var accountStatus = window.AccountStatus;
        var addUser = window.AddUser;
        var changePassword = window.ChangePassword;
        var removeUser = window.RemoveUser;
        var accountBusy = false;
        void RefreshUsers(string? selectedId = null)
        {
            selectedId ??= (users.SelectedItem as LocalWebUserSummary)?.Id;
            var entries = localWebUsers!.ListUsers();
            users.ItemsSource = entries;
            users.SelectedItem = entries.FirstOrDefault(entry => entry.Id == selectedId);
            if (users.SelectedItem is null && entries.Count > 0) users.SelectedIndex = 0;
        }
        void Refresh()
        {
            var starting = localWebStartup is not null;
            start.IsEnabled = !starting && localWebServer is null && networks.SelectedItem is LocalWebInterface && localWebUsers!.ListUsers().Count > 0;
            stop.IsEnabled = rotate.IsEnabled = !starting && localWebServer is not null;
            export.IsEnabled = localWebCertificates is not null;
            networks.IsEnabled = port.IsEnabled = !starting && localWebServer is null;
            addUser.IsEnabled = !accountBusy;
            changePassword.IsEnabled = removeUser.IsEnabled = !accountBusy && users.SelectedItem is LocalWebUserSummary;
            status.Text = starting ? "Starting the browser server…" : localWebServer is null ? "Stopped. The browser server is off by default and does not start automatically." :
                $"Address: {localWebServer.Address}\nCA SHA-256: {LocalWebCertificate.Fingerprint(localWebCertificates!.Authority)}\nServer SHA-256: {LocalWebCertificate.Fingerprint(localWebCertificates.Server)}";
        }
        async Task ManageAccountAsync(Func<Task<string?>> operation)
        {
            accountBusy = true; accountStatus.Text = "Saving web account…"; Refresh();
            try
            {
                var selectedId = await operation();
                window.Password.Clear(); window.ConfirmPassword.Clear();
                RefreshUsers(selectedId);
                accountStatus.Text = "Web account saved.";
            }
            catch (Exception error) { accountStatus.Text = error.Message; }
            finally { accountBusy = false; Refresh(); }
        }
        void CheckPasswords()
        {
            if (window.Password.Password != window.ConfirmPassword.Password)
                throw new ArgumentException("Passwords do not match.");
        }
        addUser.Click += async (_, _) => await ManageAccountAsync(async () =>
        {
            CheckPasswords();
            var added = await localWebUsers!.AddAsync(window.Username.Text.Trim(), window.Password.Password);
            window.Username.Clear();
            return added.Id;
        });
        changePassword.Click += async (_, _) => await ManageAccountAsync(async () =>
        {
            if (users.SelectedItem is not LocalWebUserSummary selected) throw new ArgumentException("Choose an account.");
            CheckPasswords();
            await localWebUsers!.ChangePasswordAsync(selected.Id, window.Password.Password);
            if (localWebServer is not null) await localWebServer.DisconnectUserAsync(selected.Id);
            return selected.Id;
        });
        removeUser.Click += async (_, _) =>
        {
            if (users.SelectedItem is not LocalWebUserSummary selected) return;
            if (MessageBox.Show(window, $"Remove web account '{selected.Username}' and its saved browser preferences?",
                "Remove web account", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await ManageAccountAsync(async () =>
            {
                await localWebUsers!.RemoveAsync(selected.Id);
                if (localWebServer is not null) await localWebServer.DisconnectUserAsync(selected.Id);
                return null;
            });
        };
        users.SelectionChanged += (_, _) => Refresh();
        refreshLocalWebWindow = Refresh;
        start.Click += async (_, _) =>
        {
            using var startup = new CancellationTokenSource();
            localWebStartup = startup;
            Refresh();
            string? errorMessage = null;
            try
            {
                if (networks.SelectedItem is not LocalWebInterface network || !int.TryParse(port.Text, out var number) || number is < 1024 or > 65535)
                    throw new ArgumentException("Choose a connected LAN interface and a port from 1024 to 65535.");
                localWebCertificates?.Dispose();
                localWebCertificates = null;
                localWebCertificates = LocalWebCertificate.GetOrCreate(network.Address);
                localWebServer = await LocalWebServer.StartAsync(network, localWebCertificates.Server, number,
                    () => new LocalWebWorkspace(localWebDependencies), localWebUsers!, startup.Token);
                if (shutdownStarted || startup.IsCancellationRequested) await StopLocalWebServerAsync();
                else
                {
                    try { OpenLocalWebBrowser(localWebServer, info => Process.Start(info)?.Dispose()); }
                    catch (Exception)
                    {
                        // Shell errors may include the local server URI. Show a stable message.
                        errorMessage = "The server is running, but the default browser could not open. Open the address above and sign in.";
                    }
                }
            }
            catch (Exception error) { errorMessage = $"Could not start: {error.Message}"; }
            finally
            {
                if (ReferenceEquals(localWebStartup, startup)) localWebStartup = null;
                refreshLocalWebWindow?.Invoke();
                if (errorMessage is not null && localWebWindow is LocalWebServerWindow currentWindow)
                    currentWindow.Status.Text += "\n\n" + errorMessage;
            }
        };
        stop.Click += async (_, _) => { stop.IsEnabled = false; await StopLocalWebServerAsync(); Refresh(); };
        rotate.Click += async (_, _) =>
        {
            rotate.IsEnabled = false;
            try { if (localWebServer is not null) await localWebServer.DisconnectAllAsync(); }
            finally { Refresh(); }
        };
        export.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { FileName = "StreamStudio-LAN-CA.cer", Filter = "Public CA certificate (*.cer)|*.cer" };
            if (localWebCertificates is not null && dialog.ShowDialog(window) == true)
                File.WriteAllBytes(dialog.FileName, localWebCertificates.Authority.Export(X509ContentType.Cert));
        };
        window.Closed += (_, _) => { localWebWindow = null; refreshLocalWebWindow = null; };
        RefreshUsers();
        Refresh();
        window.Show();
    }

    internal static void OpenLocalWebBrowser(LocalWebServer server, Action<ProcessStartInfo> startProcess)
    {
        startProcess(new ProcessStartInfo(server.Address.AbsoluteUri) { UseShellExecute = true });
    }

    private async Task StopLocalWebServerAsync()
    {
        localWebStartup?.Cancel();
        var server = localWebServer;
        localWebServer = null;
        if (server is null) return;
        try { await server.DisposeAsync(); }
        catch (Exception error) { appLogger?.Write(AppLogLevel.Warning, "LocalWeb", "Browser server shutdown failed.", error); }
    }
}
