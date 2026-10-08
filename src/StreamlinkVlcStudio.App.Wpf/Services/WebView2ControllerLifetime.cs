using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace StreamlinkVlcStudio.App.Wpf.Services;

/// <summary>Retains a native browser parent until controller creation and failure cleanup finish.</summary>
internal static class WebView2ControllerLifetime
{
    internal static async Task<CoreWebView2Controller> CreateAsync(
        HwndSource host,
        Func<nint, Task<CoreWebView2Controller>> createController,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<CoreWebView2Controller> creation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            creation = createController(host.Handle);
        }
        catch
        {
            host.Dispose();
            throw;
        }

        try { return await creation.WaitAsync(timeout, cancellationToken); }
        catch
        {
            // Native creation cannot be canceled. Close its eventual result before releasing the parent.
            _ = CloseLateAsync(creation, host);
            throw;
        }
    }

    private static async Task CloseLateAsync(Task<CoreWebView2Controller> creation, HwndSource host)
    {
        try { (await creation).Close(); }
        catch (Exception) { /* Failed creation has no controller to close. */ }
        finally { host.Dispose(); }
    }
}
