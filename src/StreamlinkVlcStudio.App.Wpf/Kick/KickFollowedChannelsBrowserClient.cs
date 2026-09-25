using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Infrastructure.Viewers;

namespace StreamlinkVlcStudio.App.Wpf.Kick;

internal sealed class KickFollowedChannelsBrowserClient(CoreWebView2 core)
{
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private bool firstPage = true;

    internal async Task<string> ReadPageAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsKickOrigin(core.Source))
            throw new InvalidOperationException("Open Kick and sign in before importing follows.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!IsKickOrigin(args.Source)) return;
            try
            {
                var json = args.WebMessageAsJson;
                if (json.Length > KickFollowedChannelsReader.MaximumResponseBytes * 6 + 1024) return;
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("id", out var requestId) || requestId.ValueKind != JsonValueKind.String ||
                    requestId.GetString() != id) return;
                var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString() : null;
                var status = root.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var code)
                    ? code : 0;
                if (status == 200 && root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
                    completion.TrySetResult(body.GetString()!);
                else
                    completion.TrySetException(new InvalidOperationException(ErrorMessage(error, status)));
            }
            catch (JsonException) { /* Ignore unrelated website messages. */ }
            catch (InvalidOperationException) { /* Ignore unrelated website messages. */ }
        }
        void OnNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args) =>
            completion.TrySetException(new InvalidOperationException("Kick navigated during detection. Wait for the page to load, then import again."));
        core.WebMessageReceived += OnMessage;
        core.NavigationStarting += OnNavigation;
        try
        {
            var script = KickFollowedChannelsScript.Build(id, url, sessionId, firstPage);
            firstPage = false;
            await core.ExecuteScriptAsync(script)
                .WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Kick did not answer in time. Complete any sign-in or browser check, then import again.");
        }
        finally
        {
            core.WebMessageReceived -= OnMessage;
            core.NavigationStarting -= OnNavigation;
        }
    }

    internal static bool IsKickOrigin(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "kick.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static string ErrorMessage(string? error, int status) => (error, status) switch
    {
        ("signin", _) or (_, 401) => "Sign in to Kick in this window, then select Import follows.",
        (_, 403) => "Kick refused the request. Complete any browser check or sign in again, then retry.",
        (_, 429) => "Kick is limiting requests. Wait a little before importing again.",
        ("account", _) => "The Kick session changed during detection. Import again to read one account's follows.",
        ("origin", _) => "Return to Kick before importing follows.",
        ("size", _) => "Kick's follow-list response was too large. Nothing was imported.",
        _ => "The Kick follow list could not be read. Check your connection and sign-in, then retry."
    };
}
