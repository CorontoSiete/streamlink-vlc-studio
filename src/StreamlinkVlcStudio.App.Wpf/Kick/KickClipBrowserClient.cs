using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Kick;

internal sealed class KickClipBrowserClient : IDisposable
{
    private readonly CoreWebView2 core;
    private readonly KickClipPublicationTracker tracker;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string scriptKey = "__streamStudioClip_" + Guid.NewGuid().ToString("N");
    private string? scriptId;
    private Task responseTail = Task.CompletedTask;
    private int navigationVersion;
    private string currentSource = "";
    private bool disposed;

    internal KickClipBrowserClient(CoreWebView2 core, StreamTarget target)
    {
        this.core = core;
        tracker = new KickClipPublicationTracker(target);
        core.WebResourceResponseReceived += OnResponse;
        core.NavigationStarting += OnNavigation;
        core.SourceChanged += OnSourceChanged;
    }

    internal Uri ChannelUri => tracker.ChannelUri;
    internal event Action<KickClipResult>? Published;
    internal event Action<string>? Failed;

    internal async Task<KickClipResult> CreateClipAsync(string title, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        var completion = new TaskCompletionSource<KickClipResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(KickClipResult result) => completion.TrySetResult(result);
        void OnFailed(string message) => completion.TrySetException(new InvalidOperationException(message));
        void OnLoaded(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) navigation.TrySetResult();
            else navigation.TrySetException(new InvalidOperationException("Kick did not load. Check your connection and try again."));
        }
        Published += OnPublished;
        Failed += OnFailed;
        core.NavigationCompleted += OnLoaded;
        try
        {
            token.ThrowIfCancellationRequested();
            core.Navigate(ChannelUri.AbsoluteUri);
            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            if (!await OpenEditorAsync(waitUntilReady: true, token))
                throw new InvalidOperationException("Kick's clip editor is not ready. Open Detect Kick follows in Settings to complete sign-in or any browser check, then try again.");
            while (!completion.Task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (!tracker.IsChannelPage(core.Source))
                    throw new InvalidOperationException("Kick left the selected stream. Open Detect Kick follows in Settings to check your sign-in, then try again.");
                var json = await core.ExecuteScriptAsync(KickClipEditorScript.Publish(
                    scriptKey, ChannelUri.AbsolutePath, title, tracker.HasDraft)).WaitAsync(token);
                if (JsonSerializer.Deserialize<string>(json) == "signin")
                    throw new InvalidOperationException("Sign in to Kick using Detect Kick follows in Settings, then click Clip again.");
                await Task.WhenAny(completion.Task, Task.Delay(250, token));
            }
            token.ThrowIfCancellationRequested();
            return await completion.Task;
        }
        finally
        {
            Published -= OnPublished;
            Failed -= OnFailed;
            core.NavigationCompleted -= OnLoaded;
            // Observe a failure that raced cancellation or a navigation/script error.
            _ = completion.Task.Exception;
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var installedId = await core.AddScriptToExecuteOnDocumentCreatedAsync(KickClipEditorScript.Install(scriptKey));
        if (disposed || cancellationToken.IsCancellationRequested)
        {
            core.RemoveScriptToExecuteOnDocumentCreated(installedId);
            throw new OperationCanceledException(cancellationToken);
        }
        scriptId = installedId;
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal async Task<bool> OpenEditorAsync(bool waitUntilReady, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(waitUntilReady ? 30 : 5));
        var token = linked.Token;
        var version = navigationVersion;
        var attempts = waitUntilReady ? 60 : 1;
        try
        {
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (version != navigationVersion || !tracker.IsChannelPage(core.Source)) return false;
                var json = await core.ExecuteScriptAsync(KickClipEditorScript.Check(scriptKey, ChannelUri.AbsolutePath, open: true))
                    .WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (JsonSerializer.Deserialize<string>(json) == "ready") return true;
                if (attempt + 1 < attempts) await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
        return false;
    }

    private void OnNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        navigationVersion++;
        tracker.Reset();
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        // Client-side routing need not raise NavigationStarting. A different channel must
        // invalidate drafts too, even if the user later returns to the original channel.
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
            !KickClipPublicationTracker.IsClipRequest(args.Request.Method, args.Request.Uri)) return;
        // Request the body while the native response event is active, then preserve draft/final
        // processing order even if WebView2 supplies their bodies in a different order.
        var body = ReadResponseBodyAsync(args.Response, lifetime.Token);
        responseTail = ObserveResponseAsync(responseTail, body, args.Request.Method, args.Request.Uri,
            args.Response.StatusCode, navigationVersion, lifetime.Token);
    }

    private static async Task<string> ReadResponseBodyAsync(CoreWebView2WebResourceResponseView response, CancellationToken token)
    {
        if (response.StatusCode is < 200 or >= 300) return "";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var content = response.GetContentAsync();
        Stream stream;
        try { stream = await content.WaitAsync(timeout.Token); }
        catch
        {
            _ = DisposeLateContentAsync(content);
            throw;
        }
        using var responseStream = stream;
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int read;
        while ((read = await responseStream.ReadAsync(bytes, timeout.Token)) != 0)
        {
            if (buffer.Length + read > KickClipPublicationTracker.MaximumResponseBytes)
                throw new InvalidOperationException("Kick's clip response was too large to verify.");
            buffer.Write(bytes, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }

    private async Task ObserveResponseAsync(Task previous, Task<string> bodyTask, string method, string address,
        int status, int version, CancellationToken token)
    {
        try
        {
            await previous;
            var body = await bodyTask;
            token.ThrowIfCancellationRequested();
            if (version != navigationVersion || !tracker.IsChannelPage(core.Source)) return;
            var result = tracker.Observe(method, address, status, body);
            if (result is not null) Published?.Invoke(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!disposed && version == navigationVersion)
                Failed?.Invoke(ex is InvalidOperationException ? ex.Message :
                    "The clip response could not be verified. Check your channel clips before trying again.");
        }
    }

    private static async Task DisposeLateContentAsync(Task<Stream> content)
    {
        try { (await content).Dispose(); }
        catch (Exception) { /* The browser may already be closed. */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        core.WebResourceResponseReceived -= OnResponse;
        core.NavigationStarting -= OnNavigation;
        core.SourceChanged -= OnSourceChanged;
        if (scriptId is not null) core.RemoveScriptToExecuteOnDocumentCreated(scriptId);
        lifetime.Dispose();
    }
}
