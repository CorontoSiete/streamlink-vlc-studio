using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.App.Wpf.Kick;
using StreamlinkVlcStudio.App.Wpf.Services;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Viewers;

namespace StreamlinkVlcStudio.App.Wpf.Follows;

internal sealed class ChannelFollowBrowserClient : IDisposable
{
    private readonly CoreWebView2 core;
    private readonly ChannelFollowResponseTracker tracker;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan operationTimeout;
    private readonly Func<CancellationToken, Task>? verifySession;
    private readonly Func<bool>? showVerification;
    private readonly string scriptKey = "__streamStudioFollow_" + Guid.NewGuid().ToString("N");
    private Task responseTail = Task.CompletedTask;
    private int navigationVersion;
    private string currentSource = "";
    private bool twitchMutationRequested;
    private bool disposed;

    internal ChannelFollowBrowserClient(CoreWebView2 core, StreamTarget target, TimeSpan? operationTimeout = null,
        Func<CancellationToken, Task>? verifySession = null, Func<bool>? showVerification = null, bool? desiredFollowing = true)
    {
        this.core = core;
        tracker = new ChannelFollowResponseTracker(target, desiredFollowing);
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(45);
        this.verifySession = verifySession;
        this.showVerification = showVerification;
        core.WebResourceResponseReceived += OnResponse;
        core.WebResourceRequested += OnRequest;
        core.NavigationStarting += OnNavigation;
        core.SourceChanged += OnSourceChanged;
    }

    internal async Task<ChannelFollowResult> FollowAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.CancelAfter(operationTimeout);
        var token = linked.Token;
        var clickAttempted = false;
        var clickNavigationVersion = -1;
        var verificationShown = false;
        var controlIdentityPending = false;
        try
        {
            await NavigateAsync(token);
            if (tracker.Target.Platform == PlatformKind.Kick)
            {
                var existing = await ReadKickFollowsAsync(token);
                var following = existing.Contains(tracker.Target.Channel, StringComparer.OrdinalIgnoreCase);
                if (tracker.DesiredFollowing is null || following == tracker.DesiredFollowing)
                    return Result(existing, following);
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!tracker.IsChannelPage(core.Source))
                    throw new InvalidOperationException($"{tracker.Target.Platform} left the selected channel. The follow was not confirmed.");
                if (clickAttempted && clickNavigationVersion != navigationVersion)
                    throw new InvalidOperationException($"{tracker.Target.Platform} navigated during the follow. Check the channel before retrying.");
                if (tracker.Error is { } error) throw new InvalidOperationException(error);
                if (tracker.Confirmed) return Result();
                if (tracker.TwitchUserRetryPending && !verificationShown)
                {
                    if (showVerification?.Invoke() != true)
                        throw new InvalidOperationException("Twitch could not confirm the follow because its browser verification is required. Complete the Twitch check inside the app, then try again.");
                    verificationShown = true;
                    linked.CancelAfter(TimeSpan.FromMinutes(5));
                }
                if (tracker.RequiresSignIn) throw new ChannelFollowSignInRequiredException();
                if (tracker.DesiredFollowing == true && tracker.HasTwitchFollowState && tracker.TwitchCanFollow == false)
                    throw new InvalidOperationException("Twitch does not allow this account to follow the selected channel.");
                if (tracker.KickRequestAcknowledged)
                {
                    var channels = await ReadKickFollowsAsync(token);
                    if (channels.Contains(tracker.Target.Channel, StringComparer.OrdinalIgnoreCase) != tracker.DesiredFollowing)
                        throw new InvalidOperationException(tracker.DesiredFollowing == true
                            ? "Kick accepted the request, but the channel was not in your followed list. Check Kick before retrying."
                            : "Kick accepted the request, but the channel was still in your followed list. Check Kick before retrying.");
                    return Result(channels);
                }

                if (tracker.DesiredFollowing is null)
                {
                    await Task.Delay(150, token);
                    continue;
                }

                var canClick = tracker.DesiredFollowing is not null && (tracker.Target.Platform == PlatformKind.Kick ||
                    tracker.HasTwitchFollowState && tracker.TwitchDisplayName.Length != 0 &&
                    (tracker.DesiredFollowing == false || tracker.TwitchCanFollow == true));
                var canDispatch = canClick && (!clickAttempted ||
                    tracker.Target.Platform == PlatformKind.Twitch && tracker.DesiredFollowing == true && !twitchMutationRequested);
                if (canDispatch && verifySession is not null) await verifySession(token);
                // A native request may have arrived while the account check was awaiting.
                if (clickAttempted && twitchMutationRequested) canDispatch = false;
                if (canDispatch) tracker.Arm();
                token.ThrowIfCancellationRequested();
                var versionBeforeClick = navigationVersion;
                var json = await core.ExecuteScriptAsync(ChannelFollowScript.Build(tracker.Target, scriptKey, canDispatch,
                    tracker.TwitchDisplayName, tracker.DesiredFollowing != false, retryUndispatched: clickAttempted && canDispatch))
                    .WaitAsync(token);
                token.ThrowIfCancellationRequested();
                var state = JsonSerializer.Deserialize<string>(json);
                // WebView2 returns JSON null when script evaluation fails. The click may already have
                // happened, so an unknown result must never cause a retry in a fresh document.
                if (state is not ("clicked" or "pending" or "waiting" or "ready" or "following" or "ambiguous" or "identity" or "identity-pending" or "page"))
                    throw new InvalidOperationException($"{tracker.Target.Platform}'s follow control could not be verified. Check the channel before retrying.");
                controlIdentityPending = state == "identity-pending";
                if (state == "clicked")
                {
                    clickAttempted = true;
                    clickNavigationVersion = versionBeforeClick;
                }
                if (state == "ambiguous")
                    throw new InvalidOperationException($"{tracker.Target.Platform}'s follow control was ambiguous. No follow was clicked.");
                if (state == "identity")
                    throw new InvalidOperationException("Twitch's follow control did not identify the selected broadcaster. No follow was clicked.");
                if (state == "page")
                    throw new InvalidOperationException($"{tracker.Target.Platform} left the selected channel. The follow was not confirmed.");
                await Task.Delay(150, token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            if (controlIdentityPending)
                throw new InvalidOperationException("Twitch's follow control did not identify the selected broadcaster. No follow was clicked.");
            throw new InvalidOperationException($"{tracker.Target.Platform} did not confirm the follow in time. Check the channel and complete any website check before retrying.");
        }
    }

    private ChannelFollowResult Result(IReadOnlyList<string>? kickChannels = null, bool? following = null) =>
        new(tracker.Target.Platform, tracker.Target.Channel, kickChannels, following ?? tracker.IsFollowing ?? tracker.DesiredFollowing == true);

    private async Task<IReadOnlyList<string>> ReadKickFollowsAsync(CancellationToken token)
    {
        var client = new KickFollowedChannelsBrowserClient(core, (error, status) =>
            error == "signin" || status == 401 ? tracker.KickRequestAcknowledged
                ? new InvalidOperationException("Kick's website session expired after the follow request. Sign in again and check the channel before retrying.")
                : new ChannelFollowSignInRequiredException() :
            new InvalidOperationException((error, status) switch
            {
                (_, 429) => "Kick is limiting requests. Wait before retrying Follow.",
                (_, 403) => "Kick refused the follow-list check. Complete any website check, then retry Follow.",
                ("account", _) => "The Kick website session changed while checking follows. Check the channel before retrying.",
                _ => "Kick's followed channels could not be verified. Check your connection and website sign-in, then retry Follow."
            }));
        return await KickFollowedChannelsReader.ReadAllAsync(CoreReadAsync, cancellationToken: token);

        // ReadAllAsync runs later pages off the caller's context. WebView2 always belongs to its dispatcher.
        Task<string> CoreReadAsync(string url, CancellationToken ct) => dispatcher.InvokeAsync(
            () => client.ReadPageAsync(url, ct), System.Windows.Threading.DispatcherPriority.Normal, ct).Task.Unwrap();
    }

    private readonly System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    private async Task NavigateAsync(CancellationToken token)
    {
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (navigationId is null && tracker.IsChannelPage(args.Uri)) navigationId = args.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            // A previous about:blank/sign-in navigation can finish after Navigate is called again.
            // Only the requested channel's navigation may release the follow operation.
            if (navigationId != args.NavigationId) return;
            if (args.IsSuccess && tracker.IsChannelPage(core.Source)) navigation.TrySetResult();
            else navigation.TrySetException(new InvalidOperationException($"{tracker.Target.Platform} did not load. Check your connection and retry Follow."));
        }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            token.ThrowIfCancellationRequested();
            core.Navigate(tracker.ChannelUri.AbsoluteUri);
            await navigation.Task.WaitAsync(token);
        }
        finally
        {
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Completed;
        }
    }

    private void OnNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        navigationVersion++;
        tracker.Reset();
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (tracker.IsChannelPage(currentSource) && !tracker.IsChannelPage(core.Source))
        {
            navigationVersion++;
            tracker.Reset();
        }
        currentSource = core.Source;
    }

    private void OnResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (disposed || !tracker.IsChannelPage(core.Source) ||
            !tracker.IsResponseCandidate(args.Request.Method, args.Request.Uri)) return;
        var requestBody = tracker.Target.Platform == PlatformKind.Twitch ? ReadRequestBody(args.Request) : "";
        // GetContentAsync must start while the native response event is active. Keep response processing ordered.
        var body = tracker.Target.Platform == PlatformKind.Twitch && args.Response.StatusCode is >= 200 and < 300
            ? ReadResponseBodyAsync(args.Response, lifetime.Token) : Task.FromResult("");
        responseTail = ObserveResponseAsync(responseTail, body, args.Request.Method, args.Request.Uri,
            args.Response.StatusCode, requestBody, navigationVersion, lifetime.Token);
    }

    private async void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (disposed) return;
        var twitchRequest = tracker.Target.Platform == PlatformKind.Twitch &&
            tracker.IsResponseCandidate(args.Request.Method, args.Request.Uri);
        var body = twitchRequest ? ReadRequestBody(args.Request, preserveForSend: true) : "";
        if (twitchRequest && !twitchMutationRequested)
        {
            // Record arrival before any response/session deferral. A click is retryable
            // only when it produced no request, never while a native request is pending.
            try
            {
                using var request = JsonDocument.Parse(body);
                var items = request.RootElement.ValueKind == JsonValueKind.Array
                    ? request.RootElement.EnumerateArray().ToArray() : [request.RootElement];
                twitchMutationRequested = items.Any(item => item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("operationName", out var name) && name.ValueKind == JsonValueKind.String &&
                    name.GetString() is "FollowButton_FollowUser" or "FollowButton_UnfollowUser");
            }
            catch (JsonException) { twitchMutationRequested = true; }
        }
        CoreWebView2Deferral? deferral = null;
        try
        {
            deferral = args.GetDeferral();
            if (twitchRequest)
            {
                // The website can replay a challenged mutation before GetContentAsync has finished.
                // Hold the request until the prior response authorizes (or rejects) that retry.
                await responseTail.WaitAsync(lifetime.Token);
                if (tracker.TwitchUserRetryPending)
                {
                    var trusted = await core.ExecuteScriptAsync(ChannelFollowScript.ConsumeTrustedFollowClick(tracker.Target, scriptKey))
                        .WaitAsync(lifetime.Token);
                    if (trusted == "true") tracker.AuthorizeTwitchUserRetry();
                }
                if (tracker.TwitchIntegrityRetryPending && verifySession is not null)
                    await verifySession(lifetime.Token);
            }
            if (disposed || tracker.BlocksUnexpectedFollow(args.Request.Method, args.Request.Uri, body))
                BlockRequest();
        }
        catch (Exception)
        {
            tracker.RejectRequestVerification();
            try { BlockRequest(); }
            catch (Exception) { /* The controller may already have closed during cancellation. */ }
        }
        finally
        {
            try { deferral?.Dispose(); }
            catch (System.Runtime.InteropServices.COMException) { /* Cancellation may already have closed the controller. */ }
            catch (InvalidOperationException) { }
        }

        void BlockRequest() => args.Response = core.Environment.CreateWebResourceResponse(
            null, 403, "Unexpected follow blocked", "Cache-Control: no-store");
    }

    private async Task ObserveResponseAsync(Task previous, Task<string> body, string method, string address,
        int status, string requestBody, int version, CancellationToken token)
    {
        try
        {
            await previous;
            var response = await body;
            token.ThrowIfCancellationRequested();
            if (!disposed && version == navigationVersion && tracker.IsChannelPage(core.Source))
                tracker.Observe(method, address, status, requestBody, response);
        }
        catch (Exception)
        {
            // A missing body can never prove success. The bounded operation reports an unconfirmed result.
        }
    }

    internal static string ReadRequestBody(CoreWebView2WebResourceRequest request, bool preserveForSend = false)
    {
        try
        {
            // The browser owns this stream and still needs its content to send the request.
            var stream = request.Content;
            if (stream is null) return "";
            var position = stream.CanSeek ? stream.Position : 0;
            try
            {
                if (stream.CanSeek) stream.Position = 0;
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                int read;
                while ((read = stream.Read(bytes, 0, bytes.Length)) != 0)
                {
                    if (buffer.Length + read > ChannelFollowResponseTracker.MaximumRequestBytes) return "";
                    buffer.Write(bytes, 0, read);
                }
                var content = buffer.ToArray();
                // WebView2 can expose a forward-only stream. Replace it with identical bytes after inspection,
                // so the website's request still reaches the server with its original body.
                if (preserveForSend && !stream.CanSeek) request.Content = new MemoryStream(content, writable: false);
                return Encoding.UTF8.GetString(content);
            }
            finally { if (stream.CanSeek) stream.Position = position; }
        }
        catch (Exception) { return ""; }
    }

    private static async Task<string> ReadResponseBodyAsync(CoreWebView2WebResourceResponseView response, CancellationToken token)
    {
        try
        {
            var bytes = await WebView2ResponseReader.ReadAsync(response.GetContentAsync(),
                ChannelFollowResponseTracker.MaximumResponseBytes, TimeSpan.FromSeconds(15), token);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (PayloadTooLargeException)
        {
            throw new InvalidOperationException("Follow response exceeded the verification limit.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        core.WebResourceResponseReceived -= OnResponse;
        core.WebResourceRequested -= OnRequest;
        core.NavigationStarting -= OnNavigation;
        core.SourceChanged -= OnSourceChanged;
        lifetime.Dispose();
    }
}

internal sealed class ChannelFollowSignInRequiredException : Exception;
