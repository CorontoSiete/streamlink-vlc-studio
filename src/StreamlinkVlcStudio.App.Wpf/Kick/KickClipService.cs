using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Kick;

internal sealed class KickClipService(
    Window owner,
    Func<nint, Task<CoreWebView2Controller>>? controllerFactory = null) : IKickClipService
{
    public async Task<KickClipResult?> CreateLiveClipAsync(StreamTarget target, CancellationToken cancellationToken = default)
    {
        owner.Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        _ = new KickClipPublicationTracker(target);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        try
        {
            var environment = controllerFactory is null
                ? await CoreWebView2Environment.CreateAsync(userDataFolder: KickFollowedChannelsImporter.ProfileDirectory)
                    .WaitAsync(TimeSpan.FromSeconds(30), token)
                : null;
            token.ThrowIfCancellationRequested();

            // Keep Chromium rendering for Kick's editor, but never show or activate its native parent.
            var host = new HwndSource(new HwndSourceParameters("Kick clip background host")
            {
                Width = 1100,
                Height = 800,
                WindowStyle = 0
            });
            Task<CoreWebView2Controller> creation;
            try
            {
                creation = controllerFactory is null
                    ? environment!.CreateCoreWebView2ControllerAsync(host.Handle)
                    : controllerFactory(host.Handle);
            }
            catch { host.Dispose(); throw; }

            CoreWebView2Controller controller;
            try { controller = await creation.WaitAsync(TimeSpan.FromSeconds(30), token); }
            catch
            {
                // Native creation cannot be canceled. Retain its parent until the late controller is closed.
                _ = CloseLateControllerAsync(creation, host);
                throw;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                controller.Bounds = new Rectangle(0, 0, 1100, 800);
                controller.IsVisible = true;
                KickFollowedChannelsImporter.ConfigureBrowser(controller.CoreWebView2, blockMedia: false);
                using var client = new KickClipBrowserClient(controller.CoreWebView2, target);
                await client.InitializeAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
                var title = $"{target.Channel} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                return await client.CreateClipAsync(title[..Math.Min(title.Length, 50)], token);
            }
            finally
            {
                try { controller.Close(); }
                finally { host.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(TimeoutMessage);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(TimeoutMessage);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new InvalidOperationException("Install or repair Microsoft Edge WebView2 Runtime to create Kick clips.", ex);
        }
    }

    private const string TimeoutMessage = "Kick clip creation timed out. Check your channel clips before retrying. " +
        "If sign-in or a browser check is needed, open Detect Kick follows in Settings first.";

    private static async Task CloseLateControllerAsync(Task<CoreWebView2Controller> creation, HwndSource host)
    {
        try { (await creation).Close(); }
        catch (Exception) { /* A failed native creation has no controller to close. */ }
        finally { host.Dispose(); }
    }
}
