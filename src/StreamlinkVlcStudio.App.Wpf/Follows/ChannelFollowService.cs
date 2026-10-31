using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Kick;
using StreamlinkVlcStudio.App.Wpf.Services;
using StreamlinkVlcStudio.App.Wpf.Twitch;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Follows;

internal sealed class ChannelFollowService(
    Window owner,
    Func<nint, Task<CoreWebView2Controller>>? controllerFactory = null,
    Func<PlatformKind, CancellationToken, Task<bool>>? signIn = null,
    TimeSpan? operationTimeout = null,
    Func<CoreWebView2Controller, StreamTarget, Action, IDisposable?>? verificationFactory = null) : IChannelFollowService
{
    internal static string TwitchProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.ProductDirectoryName, "TwitchFollowsWebView2");

    public Task<ChannelFollowResult?> GetFollowStatusAsync(StreamTarget target, CancellationToken cancellationToken = default) =>
        RunAsync(target, null, cancellationToken);

    public Task<ChannelFollowResult?> FollowAsync(StreamTarget target, CancellationToken cancellationToken = default) =>
        RunAsync(target, true, cancellationToken);

    public Task<ChannelFollowResult?> UnfollowAsync(StreamTarget target, CancellationToken cancellationToken = default) =>
        RunAsync(target, false, cancellationToken);

    private async Task<ChannelFollowResult?> RunAsync(StreamTarget target, bool? desiredFollowing, CancellationToken cancellationToken)
    {
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        var channel = new ChannelFollowResponseTracker(target).Target;
        try
        {
            CoreWebView2Environment? environment = null;
            if (controllerFactory is null)
            {
                CoreWebView2Environment.GetAvailableBrowserVersionString();
                var profile = channel.Platform == PlatformKind.Twitch
                    ? TwitchProfileDirectory : KickFollowedChannelsImporter.ProfileDirectory;
                environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile)
                    .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var host = new HwndSource(new HwndSourceParameters("Stream Studio follow background host")
            {
                Width = 1280,
                Height = 800,
                WindowStyle = 0
            });
            var controller = await WebView2ControllerLifetime.CreateAsync(host,
                controllerFactory ?? (handle => environment!.CreateCoreWebView2ControllerAsync(handle)),
                TimeSpan.FromSeconds(30), cancellationToken);
            using var followLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IDisposable? verification = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                controller.Bounds = new Rectangle(0, 0, 1280, 800);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                ConfigureBrowser(core, channel.Platform);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var session = await GetSessionFingerprintAsync(core, channel.Platform, cancellationToken);
                    if (session is null)
                    {
                        if (desiredFollowing is null) return null;
                        if (attempt != 0) throw new InvalidOperationException($"Sign in to {channel.Platform} before following a streamer.");
                        if (!await SignInAsync()) return null;
                        continue;
                    }

                    ChannelFollowResult result;
                    try
                    {
                        using var client = new ChannelFollowBrowserClient(core, target, operationTimeout, async ct =>
                        {
                            if (await GetSessionFingerprintAsync(core, channel.Platform, ct) != session)
                                throw new InvalidOperationException($"The {channel.Platform} website session changed during the follow. Check the channel before retrying.");
                        }, () =>
                        {
                            verification ??= verificationFactory is not null
                                ? verificationFactory(controller, channel, () => followLifetime.Cancel())
                                : new ChannelFollowVerificationWindow(owner, controller, host.Handle, channel, () => followLifetime.Cancel(), desiredFollowing != false);
                            return verification is not null;
                        }, desiredFollowing);
                        result = await client.FollowAsync(followLifetime.Token);
                    }
                    catch (ChannelFollowSignInRequiredException) when (attempt == 0)
                    {
                        if (desiredFollowing is null) return null;
                        core.Stop();
                        core.Navigate("about:blank");
                        if (!await SignInAsync()) return null;
                        continue;
                    }
                    catch (ChannelFollowSignInRequiredException)
                    {
                        throw new InvalidOperationException($"The {channel.Platform} website session expired. Sign in again, then retry Follow.");
                    }
                    followLifetime.Token.ThrowIfCancellationRequested();
                    var currentSession = await GetSessionFingerprintAsync(core, channel.Platform, followLifetime.Token);
                    if (currentSession != session)
                        throw new InvalidOperationException($"The {channel.Platform} website session changed during the follow. Check the channel before retrying.");
                    return result;
                }
                throw new InvalidOperationException($"The {channel.Platform} website session could not be verified. Sign in again, then retry Follow.");

                Task<bool> SignInAsync() => signIn is not null ? signIn(channel.Platform, cancellationToken) :
                    environment is not null ? ChannelFollowSignInWindow.ShowAsync(owner, environment, channel, cancellationToken) :
                    throw new InvalidOperationException($"Sign in to {channel.Platform} before following a streamer.");
            }
            finally
            {
                try { verification?.Dispose(); }
                finally
                {
                    try { controller.Close(); }
                    finally { host.Dispose(); }
                }
            }
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new InvalidOperationException("Install or repair Microsoft Edge WebView2 Runtime to follow Twitch or Kick streamers.", ex);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"The {channel.Platform} follow browser did not respond in time. Check the channel before retrying.");
        }
    }

    internal static async Task<string?> GetSessionFingerprintAsync(CoreWebView2 core, PlatformKind platform, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var address = platform == PlatformKind.Twitch ? "https://www.twitch.tv/" : "https://kick.com/";
        var name = platform == PlatformKind.Twitch ? "auth-token" : "session_token";
        var cookies = await core.CookieManager.GetCookiesAsync(address).WaitAsync(TimeSpan.FromSeconds(15), token);
        token.ThrowIfCancellationRequested();
        var values = cookies.Where(cookie => cookie.Name == name && !string.IsNullOrEmpty(cookie.Value))
            .Select(cookie => cookie.Value).Distinct(StringComparer.Ordinal).ToArray();
        // Keep website credentials in the browser profile. Only an in-memory hash is compared between stages.
        return values.Length == 1 ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(values[0]))) : null;
    }

    internal static void ConfigureBrowser(CoreWebView2 core, PlatformKind platform)
    {
        core.IsMuted = true;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = platform == PlatformKind.Kick;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        bool Allowed(string address) => platform == PlatformKind.Twitch
            ? TwitchBonusBrowser.IsAllowedNavigation(address) : KickFollowedChannelsImporter.IsAllowedNavigation(address);
        core.NavigationStarting += (_, args) => args.Cancel = !Allowed(args.Uri);
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated && Allowed(args.Uri)) core.Navigate(args.Uri);
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
}
