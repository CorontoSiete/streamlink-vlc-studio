using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class WebView2DependencyVerifier
{
    internal static string Verify()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { completion.TrySetResult(await VerifyOnStaAsync()); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "WebView2 dependency verification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Setup and the updater also bound this entire maintenance process. A hung
        // COM callback must never leave their completion waiting indefinitely.
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    private static async Task<string> VerifyOnStaAsync()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var profile = Path.Combine(temporaryRoot, "StreamStudio-dependency-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            using var host = new HwndSource(new HwndSourceParameters("Stream Studio dependency check")
            {
                WindowStyle = unchecked((int)0x80000000),
                Width = 1,
                Height = 1
            });
            var controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
            try
            {
                controller.IsVisible = false;
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 1, 1);
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                controller.CoreWebView2.NavigationCompleted += (_, result) => loaded.TrySetResult(result.IsSuccess);
                controller.CoreWebView2.NavigateToString("<!doctype html><html><body>Stream Studio runtime check</body></html>");
                if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)) ||
                    await controller.CoreWebView2.ExecuteScriptAsync("21 + 21") != "42")
                    throw new InvalidDataException("The WebView2 browser could not load a local page and execute JavaScript.");
                return environment.BrowserVersionString;
            }
            finally
            {
                var browserId = controller.CoreWebView2.BrowserProcessId;
                controller.Close();
                try
                {
                    using var browser = Process.GetProcessById((int)browserId);
                    await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { } // Already exited after the final controller closed.
                catch (TimeoutException) { } // The owning maintenance process also owns its process job.
            }
        }
        finally
        {
            var resolvedProfile = Path.GetFullPath(profile);
            var prefix = Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar;
            if (!resolvedProfile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedProfile).StartsWith("StreamStudio-dependency-check-", StringComparison.Ordinal))
                throw new InvalidDataException("The dependency check profile escaped its temporary directory.");
            for (var attempt = 0; attempt < 8 && Directory.Exists(resolvedProfile); attempt++)
            {
                try { Directory.Delete(resolvedProfile, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 7) await Task.Delay(150);
                }
            }
        }
    }
}
