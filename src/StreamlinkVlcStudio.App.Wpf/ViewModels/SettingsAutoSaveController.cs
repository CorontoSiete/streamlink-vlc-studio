using System.ComponentModel;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class SettingsAutoSaveController : IAsyncDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);
    private readonly AppSettings settings;
    private readonly ISettingsService settingsService;
    private readonly Action<Action> dispatch;
    private readonly Action<Exception?> reportResult;
    private readonly CancellationDebounceCoordinator debounce = new();
    private readonly object gate = new();
    private readonly HashSet<INotifyPropertyChanged> observed = [];
    private long changeVersion;
    private long savedVersion;
    private Task? activeSave;
    private bool disposed;

    public SettingsAutoSaveController(
        AppSettings settings,
        ISettingsService settingsService,
        Action<Action> dispatch,
        Action<Exception?> reportResult)
    {
        this.settings = settings;
        this.settingsService = settingsService;
        this.dispatch = dispatch;
        this.reportResult = reportResult;
        ObserveSettings();
    }

    public void RequestSave()
    {
        lock (gate)
        {
            if (disposed) return;
            changeVersion++;
            debounce.Schedule(SaveDelay, () => dispatch(() =>
            {
                lock (gate)
                {
                    if (disposed) return;
                    _ = SavePendingAsync();
                }
            }), ReportResult);
        }
    }

    public Task FlushAsync()
    {
        debounce.CancelScheduled();
        return SavePendingAsync();
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            debounce.Dispose();
            foreach (var source in observed)
                source.PropertyChanged -= SettingsChanged;
            observed.Clear();
        }

        // Finish the last edit before playback shutdown can consume the window's deadline.
        return new ValueTask(SavePendingAsync());
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        lock (gate)
        {
            if (disposed) return;
            if (ReferenceEquals(sender, settings)) ObserveSettings();
            RequestSave();
        }
    }

    private void ObserveSettings()
    {
        HashSet<INotifyPropertyChanged> current =
        [settings, settings.Chat, settings.Replay, settings.Downloads, settings.Hotkeys, settings.FollowedChannels, settings.Updates];
        foreach (var source in observed.Except(current).ToArray())
        {
            source.PropertyChanged -= SettingsChanged;
            observed.Remove(source);
        }

        foreach (var source in current)
        {
            if (observed.Add(source)) source.PropertyChanged += SettingsChanged;
        }
    }

    private Task SavePendingAsync()
    {
        lock (gate)
        {
            if (activeSave is not null) return activeSave;
            if (changeVersion == savedVersion) return Task.CompletedTask;

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            activeSave = completion.Task;
            _ = SaveCoreAsync(completion);
            return completion.Task;
        }
    }

    private async Task SaveCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                long version;
                lock (gate) version = changeVersion;

                try
                {
                    await settingsService.SaveAsync(settings);
                }
                catch (Exception error)
                {
                    // Keep the changes dirty so the next edit, explicit retry, or close retries them.
                    ReportResult(error);
                    return;
                }

                ReportResult(null);
                lock (gate)
                {
                    savedVersion = version;
                    if (savedVersion != changeVersion) continue;
                    activeSave = null;
                    completion.TrySetResult();
                    return;
                }
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeSave, completion.Task)) activeSave = null;
                completion.TrySetResult();
            }
        }
    }

    private void ReportResult(Exception? error) =>
        SafeEventDispatcher.Invoke(reportResult, error, null, "Settings", nameof(reportResult));
}
