using System.Globalization;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class VodDownloadViewModel : ObservableObject
{
    private VodDownloadItem item;
    private int actionRunning;

    internal VodDownloadViewModel(VodDownloadItem item,
        Func<Func<Task>, Func<bool>, AsyncRelayCommand> command,
        Func<VodDownloadViewModel, Task> play, Func<VodDownloadViewModel, Task> cancel,
        Func<VodDownloadViewModel, Task> retry, Func<VodDownloadViewModel, Task> remove)
    {
        this.item = item;
        PlayCommand = command(() => RunActionAsync(() => play(this)), () => CanPlay && ActionAvailable);
        CancelCommand = command(() => RunActionAsync(() => cancel(this)), () => CanCancel && ActionAvailable);
        RetryCommand = command(() => RunActionAsync(() => retry(this)), () => CanRetry && ActionAvailable);
        DeleteCommand = command(() => RunActionAsync(() => remove(this)), () => CanDelete && ActionAvailable);
    }

    internal VodDownloadItem Item => item;
    private bool ActionAvailable => Volatile.Read(ref actionRunning) == 0;
    public Guid Id => item.Id;
    public string Title => string.IsNullOrWhiteSpace(item.Target.DisplayTitle) ? $"VOD {item.Target.MediaId}" : item.Target.DisplayTitle;
    public string ChannelText => $"{item.Target.Platform} · {item.Target.Channel} · {item.Quality}";
    public string Error => item.Error;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool CanPlay => item.State == VodDownloadState.Completed;
    public bool CanCancel => item.IsActive;
    public bool CanRetry => item.State is VodDownloadState.Failed or VodDownloadState.Canceled or VodDownloadState.Interrupted;
    public bool CanDelete => !item.IsActive;
    private bool IsFinalizing => item.State == VodDownloadState.Downloading && item.TotalSegments > 0 &&
        item.CompletedSegments >= item.TotalSegments;
    public bool IsIndeterminate => item.IsActive && (item.TotalSegments == 0 || IsFinalizing);
    public double ProgressPercentage => item.TotalSegments == 0 ? 0 : Math.Clamp(100d * item.CompletedSegments / item.TotalSegments, 0, 100);
    public string CardButtonText => item.State switch
    {
        VodDownloadState.Completed => "Downloaded",
        VodDownloadState.Queued => "Queued",
        VodDownloadState.Resolving => "Preparing…",
        VodDownloadState.Downloading when IsFinalizing => "Finishing…",
        VodDownloadState.Downloading when item.TotalSegments > 0 => $"{Math.Floor(ProgressPercentage):0}%",
        VodDownloadState.Downloading => "Downloading…",
        _ => "Retry"
    };
    public string CardActionName => CanPlay ? "Watch downloaded VOD offline" :
        CanRetry ? "Retry VOD download" : "Download VOD for offline playback";
    public string CardToolTip => CanPlay ? $"Downloaded at {item.Quality}. Click to watch offline.\n{ProgressText}" :
        CanRetry ? $"{StateText} at {item.Quality}.\n{Error}\nClick to retry this download." :
        $"{(IsFinalizing ? "Finishing offline files…" : StateText)} at {item.Quality}.\n{ProgressText}\nManage or cancel this download in Downloads.";
    public string StateText => item.State switch
    {
        VodDownloadState.Completed => "Ready to watch offline",
        VodDownloadState.Resolving => "Resolving VOD…",
        VodDownloadState.Downloading => "Downloading…",
        VodDownloadState.Queued => "Queued",
        VodDownloadState.Canceled => "Canceled",
        VodDownloadState.Interrupted => "Interrupted — retry to finish",
        _ => "Download failed"
    };
    public string ProgressText
    {
        get
        {
            var size = item.BytesDownloaded >= 1024L * 1024 * 1024
                ? $"{(item.BytesDownloaded / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.CurrentCulture)} GiB"
                : $"{(item.BytesDownloaded / (1024d * 1024)).ToString("0.0", CultureInfo.CurrentCulture)} MiB";
            return item.State == VodDownloadState.Completed
                ? $"{StreamViewModelHelpers.FormatClockTime(item.Duration)} · {size} on disk"
                : item.TotalSegments > 0 ? $"{item.CompletedSegments:N0} / {item.TotalSegments:N0} segments · {size}" : size;
        }
    }

    public AsyncRelayCommand PlayCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }

    internal void Update(VodDownloadItem updated)
    {
        if (updated.Revision < item.Revision || updated == item) return;
        item = updated;
        foreach (var name in new[] { nameof(Title), nameof(ChannelText), nameof(Error), nameof(HasError), nameof(CanPlay),
            nameof(CanCancel), nameof(CanRetry), nameof(CanDelete), nameof(IsIndeterminate), nameof(ProgressPercentage),
            nameof(StateText), nameof(ProgressText), nameof(CardButtonText), nameof(CardActionName),
            nameof(CardToolTip) }) OnPropertyChanged(name);
        NotifyCommands();
    }

    private async Task RunActionAsync(Func<Task> execute)
    {
        if (Interlocked.CompareExchange(ref actionRunning, 1, 0) != 0) return;
        try
        {
            NotifyCommands();
            await execute();
        }
        finally
        {
            Volatile.Write(ref actionRunning, 0);
            NotifyCommands();
        }
    }

    private void NotifyCommands()
    {
        PlayCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

}
