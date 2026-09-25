using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.App.Wpf.Twitch;

/// <summary>
/// An isolated, persistent Twitch website session. Cookies stay in WebView2's
/// profile; no website access token is copied into app settings or sent by us.
/// All methods are called on the window's dispatcher.
/// </summary>
internal sealed class TwitchBonusBrowser(
    Window owner,
    Func<nint, Task<CoreWebView2Controller>>? controllerFactory = null) : ITwitchBonusBrowser
{
    private static readonly TimeSpan BrowserOperationTimeout = TimeSpan.FromSeconds(30);
    private readonly HashSet<BonusPage> pages = [];
    private Task<CoreWebView2Environment>? environmentTask;
    private Task<BonusPage>? sessionTask;
    private bool disposed;

    internal static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.ProductDirectoryName, "TwitchBonusesWebView2");

    public async Task<bool> HasSessionAsync(CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync().WaitAsync(BrowserOperationTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var cookies = await session.Core.CookieManager.GetCookiesAsync("https://www.twitch.tv/")
            .WaitAsync(BrowserOperationTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Presence is an initial sign-in check, not proof of a successful claim.
        return cookies.Any(cookie => cookie.Name == "auth-token" && !string.IsNullOrEmpty(cookie.Value));
    }

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync().WaitAsync(BrowserOperationTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        session.Core.Navigate("https://www.twitch.tv/login");
        try { await session.ShowUntilClosedAsync("Twitch sign-in — close this window when finished", cancellationToken); }
        finally { if (!disposed) session.Core.Navigate("about:blank"); }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync().WaitAsync(BrowserOperationTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        session.Core.Navigate("about:blank");
        await session.Core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
    }

    public async Task<ITwitchBonusPage> OpenChannelAsync(string channel, CancellationToken cancellationToken)
    {
        if (!IsChannelLogin(channel)) throw new ArgumentException("Invalid Twitch channel.", nameof(channel));
        var page = await CreatePageAsync(channel, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            page.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        page.Core.Navigate(ChannelChatUrl(channel));
        return page;
    }

    internal static string ChannelChatUrl(string channel) => $"https://www.twitch.tv/popout/{channel}/chat?popout=";

    internal static bool IsChannelLogin(string channel) => channel.Length is > 0 and <= 25 &&
        channel.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    internal static bool IsAllowedNavigation(string address) => address == "about:blank" ||
        (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
         uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
         uri.Host is "www.twitch.tv" or "twitch.tv" or "passport.twitch.tv" or "id.twitch.tv");

    internal static bool IsAllowedChannelNavigation(string address, string channel) =>
        address == "about:blank" || (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        IsAllowedNavigation(address) && uri.Host == "www.twitch.tv" &&
        string.Equals(uri.AbsolutePath.TrimEnd('/'), $"/popout/{channel}/chat", StringComparison.OrdinalIgnoreCase));

    internal static bool IsStreamResource(string address, CoreWebView2WebResourceContext context)
    {
        if (context == CoreWebView2WebResourceContext.Media) return true;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        // Twitch's HLS playlists/segments are usually fetched by scripts or
        // workers, not classified as Media. Block their hosts and media paths
        // as well, before a response can download or reach a player.
        return uri.Host.Equals("ttvnw.net", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".ttvnw.net", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("player.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is
                ".m3u8" or ".mpd" or ".ts" or ".m4s" or ".mp4" or ".webm" or ".mp3" or ".m4a" or ".aac";
    }

    private Task<BonusPage> GetSessionAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sessionTask is null || sessionTask.IsFaulted || sessionTask.IsCanceled)
            sessionTask = CreatePageAsync(null);
        return sessionTask;
    }

    private async Task<BonusPage> CreatePageAsync(string? channel, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.Dispatcher.VerifyAccess();
        if (controllerFactory is null && (environmentTask is null || environmentTask.IsFaulted || environmentTask.IsCanceled))
        {
            // Throws a specific, user-facing error when Evergreen is not installed.
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            environmentTask = CoreWebView2Environment.CreateAsync(userDataFolder: ProfileDirectory);
        }
        var environment = controllerFactory is null
            ? await environmentTask!.WaitAsync(BrowserOperationTimeout, cancellationToken)
            : null;
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Keep the native parent hidden, rather than hiding the WebView itself.
        // IsVisible=false stops Chromium rendering/animation-driven startup and
        // made claims depend on opening the inspection window. An independent
        // parent also keeps chat running when the main window is hidden.
        var backgroundHost = new HwndSource(new HwndSourceParameters("Twitch bonus background host")
        {
            Width = 1280,
            Height = 800,
            WindowStyle = 0
        });
        Task<CoreWebView2Controller> creation;
        try
        {
            creation = controllerFactory is null
                ? environment!.CreateCoreWebView2ControllerAsync(backgroundHost.Handle)
                : controllerFactory(backgroundHost.Handle);
        }
        catch
        {
            backgroundHost.Dispose();
            throw;
        }
        CoreWebView2Controller controller;
        try { controller = await creation.WaitAsync(BrowserOperationTimeout, cancellationToken); }
        catch
        {
            // The native creation call cannot be canceled; close its result if it
            // finishes after a tab was closed, timed out, or the app shut down.
            _ = CloseLateControllerAsync(creation, backgroundHost);
            throw;
        }
        if (disposed || cancellationToken.IsCancellationRequested)
        {
            try { controller.Close(); }
            finally { backgroundHost.Dispose(); }
            cancellationToken.ThrowIfCancellationRequested();
            throw new ObjectDisposedException(nameof(TwitchBonusBrowser));
        }

        try
        {
            controller.IsVisible = channel is not null;
            // Keep Twitch's chat/bonus control available even without a window.
            controller.Bounds = new Rectangle(0, 0, 1280, 800);
            var core = controller.CoreWebView2;
            core.IsMuted = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            // Install before the first navigation, including worker fetches and
            // the sign-in page. A preview/player must not add a second stream.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (IsStreamResource(args.Request.Uri, args.ResourceContext))
                    args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Media disabled",
                        "Cache-Control: no-store");
            };
            core.NavigationStarting += (_, args) => args.Cancel = channel is null
                ? !IsAllowedNavigation(args.Uri)
                : !IsAllowedChannelNavigation(args.Uri, channel);
            core.SourceChanged += (_, _) =>
            {
                // Twitch raids and client-side routing may use history.pushState
                // instead of a document navigation. Stay on the app's channel.
                if (channel is not null && !string.IsNullOrEmpty(core.Source) &&
                    !IsAllowedChannelNavigation(core.Source, channel))
                    core.Navigate(ChannelChatUrl(channel));
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (args.IsUserInitiated && IsAllowedNavigation(args.Uri)) core.Navigate(args.Uri);
            };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            var page = new BonusPage(controller, backgroundHost, owner, channel, closed => pages.Remove(closed));
            if (channel is not null) page.ObserveClaims();
            pages.Add(page);
            return page;
        }
        catch
        {
            try { controller.Close(); }
            finally { backgroundHost.Dispose(); }
            throw;
        }
    }

    private static async Task CloseLateControllerAsync(Task<CoreWebView2Controller> creation, HwndSource backgroundHost)
    {
        try { (await creation).Close(); }
        catch (Exception) { /* A failed native creation has no controller to close. */ }
        finally { backgroundHost.Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var page in pages.ToArray()) page.Dispose();
        pages.Clear();
    }

    private sealed class BonusPage(
        CoreWebView2Controller controller,
        HwndSource backgroundHost,
        Window owner,
        string? channel,
        Action<BonusPage> onDisposed) : ITwitchBonusPage
    {
        private Window? window;
        private bool disposed;
        private bool sessionRejected;
        private readonly CancellationTokenSource responseLifetime = new();
        public event EventHandler<string>? ClaimConfirmed;
        internal CoreWebView2 Core => controller.CoreWebView2;

        internal void ObserveClaims() => Core.WebResourceResponseReceived += ResponseReceived;

        private async void ResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            try
            {
                if (disposed || args.Request.Method != "POST" ||
                    !TwitchBonusClaimResponse.IsGraphQlEndpoint(args.Request.Uri) ||
                    !IsAllowedChannelNavigation(Core.Source, channel!) || Core.Source == "about:blank") return;
                if (args.Response.StatusCode == 401) { sessionRejected = true; return; }
                if (args.Response.StatusCode != 200) return;
                var token = responseLifetime.Token;
                // Start capturing the response before yielding to read the request.
                // WebView2 can discard the response body once this event returns.
                using var content = await args.Response.GetContentAsync().WaitAsync(BrowserOperationTimeout, token);
                if (content is null) return;
                using var requestContent = args.Request.Content;
                if (requestContent is null) return;
                var requestBytes = await BoundedByteReader.ReadOrThrowAsync(requestContent, 256 * 1024, token);
                var requestJson = System.Text.Encoding.UTF8.GetString(requestBytes);
                if (!requestJson.Contains("ClaimCommunityPoints", StringComparison.Ordinal)) return;
                var bytes = await BoundedByteReader.ReadOrThrowAsync(content, 1024 * 1024, token);
                if (disposed) return;
                foreach (var id in TwitchBonusClaimResponse.ReadConfirmedClaims(requestJson, System.Text.Encoding.UTF8.GetString(bytes)))
                    ClaimConfirmed?.Invoke(this, id);
            }
            // Observation must never interrupt Twitch or expose response bodies/account data.
            catch (Exception error) when (error is COMException or InvalidOperationException or IOException or
                OperationCanceledException or TimeoutException or NotSupportedException)
            { }
        }

        public async Task<string> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessionRejected) throw new TwitchBonusSessionExpiredException();
            Core.IsMuted = true;
            var cookies = await Core.CookieManager.GetCookiesAsync("https://www.twitch.tv/")
                .WaitAsync(BrowserOperationTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!cookies.Any(cookie => cookie.Name == "auth-token" && !string.IsNullOrEmpty(cookie.Value)))
                throw new TwitchBonusSessionExpiredException();
            var result = await Core.ExecuteScriptAsync(TwitchBonusScript.Build(channel!))
                .WaitAsync(BrowserOperationTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize<string>(result) ?? "Waiting for Twitch to load.";
        }

        public void Show() => ShowWindow($"Twitch bonus chat — {channel}");

        internal async Task ShowUntilClosedAsync(string title, CancellationToken cancellationToken)
        {
            var dialog = ShowWindow(title);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();
            dialog.Closed += OnClosed;
            using var registration = cancellationToken.Register(() => owner.Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(window, dialog)) dialog.Close();
            }));
            try { await closed.Task; }
            finally { dialog.Closed -= OnClosed; }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private Window ShowWindow(string title)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (window is not null)
            {
                window.Activate();
                return window;
            }
            var dialog = new Window
            {
                Title = title,
                Owner = owner,
                Width = 1280,
                Height = 850,
                MinWidth = 760,
                MinHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            window = dialog;
            void Resize()
            {
                GetClientRect(new WindowInteropHelper(dialog).Handle, out var rect);
                controller.Bounds = new Rectangle(0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom));
            }
            dialog.SourceInitialized += (_, _) =>
            {
                controller.ParentWindow = new WindowInteropHelper(dialog).Handle;
                Resize();
                controller.IsVisible = true;
            };
            dialog.SizeChanged += (_, _) => Resize();
            dialog.Closing += (_, _) =>
            {
                try
                {
                    controller.ParentWindow = backgroundHost.Handle;
                    controller.Bounds = new Rectangle(0, 0, 1280, 800);
                    controller.IsVisible = channel is not null;
                }
                catch (COMException) { /* The runtime or owner may already have closed. */ }
                catch (InvalidOperationException) when (disposed) { }
            };
            dialog.Closed += (_, _) => window = null;
            dialog.Show();
            return dialog;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            responseLifetime.Cancel();
            ClaimConfirmed = null;
            try { Core.WebResourceResponseReceived -= ResponseReceived; window?.Close(); }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            finally
            {
                try { controller.Close(); }
                catch (COMException) { /* The browser process may already have exited. */ }
                catch (InvalidOperationException) { }
                backgroundHost.Dispose();
                onDisposed(this);
                responseLifetime.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(nint window, out Rect rect);
    }
}
