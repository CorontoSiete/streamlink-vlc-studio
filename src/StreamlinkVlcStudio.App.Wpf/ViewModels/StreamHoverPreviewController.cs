using System.ComponentModel;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>Owns the single hover player and serializes replacement with complete cleanup.</summary>
public sealed class StreamHoverPreviewController : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly AppSettings settings;
    private readonly IAppLogger logger;
    private readonly Func<StreamTransportRequest, string, Action<LivePreviewFrame>, CancellationToken, Task> play;
    private readonly TimeSpan hoverDelay;
    private StreamHoverPreviewSession? current;
    private Task pending = Task.CompletedTask;
    private bool disposed;

    public StreamHoverPreviewController(AppSettings settings, IStreamlinkService streamlink, IAppLogger logger)
        : this(settings, logger, new LibVlcLivePreview(streamlink, logger).RunAsync) { }

    internal StreamHoverPreviewController(AppSettings settings, IAppLogger logger,
        Func<StreamTransportRequest, string, Action<LivePreviewFrame>, CancellationToken, Task> play, TimeSpan? hoverDelay = null)
    {
        this.settings = settings;
        this.logger = logger;
        this.play = play;
        // A short dwell still avoids starting streams while the pointer passes across cards.
        this.hoverDelay = hoverDelay ?? TimeSpan.FromMilliseconds(100);
        settings.PropertyChanged += SettingsChanged;
    }

    internal StreamHoverPreviewSession? Begin(StreamTarget target)
    {
        lock (gate)
        {
            if (disposed || !settings.EnableStreamHoverPreviews || target.Kind != StreamTargetKind.Live) return null;
            current?.Stop();
            var session = new StreamHoverPreviewSession(target);
            current = session;
            // Snapshot settings on the caller's UI thread, before starting background work.
            var path = settings.StreamlinkPath ?? "";
            var vlcDirectory = settings.VlcDirectory ?? "";
            var lowLatency = settings.LowLatency;
            var arguments = settings.CustomStreamlinkArguments;
            var previous = pending;
            pending = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(hoverDelay, session.Token).ConfigureAwait(false);
                    await previous.ConfigureAwait(false);
                    session.Token.ThrowIfCancellationRequested();
                    session.SetState(StreamHoverPreviewState.Loading);
                    var request = new StreamTransportRequest(target, LibVlcLivePreview.QualityPreference, path, lowLatency,
                        CommandLineTokenizer.Tokenize(arguments), IsMultiStream: true);
                    await play(request, vlcDirectory, session.Present, session.Token).ConfigureAwait(false);
                    session.SetState(StreamHoverPreviewState.Unavailable);
                }
                catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    session.SetState(StreamHoverPreviewState.Unavailable);
                    // Provider exceptions may include signed URLs or custom authentication arguments.
                    logger.Write(AppLogLevel.Warning, "Hover preview", $"Preview unavailable ({ex.GetType().Name}).");
                }
                finally
                {
                    // Even canceled, queued hovers must retain the earlier cleanup in the chain.
                    await previous.ConfigureAwait(false);
                    session.Complete();
                }
            });
            return session;
        }
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.EnableStreamHoverPreviews) && !settings.EnableStreamHoverPreviews)
        {
            lock (gate) current?.Stop();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            settings.PropertyChanged -= SettingsChanged;
            current?.Stop();
            return new ValueTask(pending);
        }
    }
}

internal enum StreamHoverPreviewState { Waiting, Loading, Playing, Unavailable, Stopped }

internal sealed class StreamHoverPreviewSession
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private LivePreviewFrame? frame;
    private StreamHoverPreviewState state;
    private bool complete;

    internal StreamHoverPreviewSession(StreamTarget target)
    {
        Target = target;
        Token = cancellation.Token;
    }

    internal StreamTarget Target { get; }
    internal CancellationToken Token { get; }
    internal StreamHoverPreviewState State { get { lock (gate) return state; } }
    internal event Action? Changed;

    internal LivePreviewFrame? TakeFrame()
    {
        lock (gate)
        {
            var result = frame;
            frame = null;
            return result;
        }
    }

    internal void Present(LivePreviewFrame value)
    {
        lock (gate)
        {
            if (state == StreamHoverPreviewState.Stopped || complete) return;
            frame = value; // One mailbox slot: a busy UI never accumulates decoded frames.
            state = StreamHoverPreviewState.Playing;
        }
        Changed?.Invoke();
    }

    internal void SetState(StreamHoverPreviewState value)
    {
        lock (gate)
        {
            if (state == StreamHoverPreviewState.Stopped) return;
            state = value;
            if (value == StreamHoverPreviewState.Unavailable) frame = null;
        }
        Changed?.Invoke();
    }

    internal void Stop()
    {
        lock (gate)
        {
            state = StreamHoverPreviewState.Stopped;
            frame = null;
            if (!complete) cancellation.Cancel();
        }
        Changed?.Invoke();
    }

    internal void Complete()
    {
        lock (gate)
        {
            complete = true;
            cancellation.Dispose();
        }
    }
}
