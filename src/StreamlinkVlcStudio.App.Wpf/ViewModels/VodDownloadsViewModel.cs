using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class VodDownloadsViewModel : HomeFeatureViewModel
{
    private readonly IVodDownloadService? service;
    private readonly Func<string> getQuality;
    private readonly Func<StreamTarget, string, Task> openOffline;
    private readonly Action<VodDownloadItem> ensureNotOpen;
    private readonly Func<VodDownloadItem, bool>? confirmDelete;
    private readonly Dictionary<Guid, VodDownloadViewModel> cards = [];
    private readonly HashSet<Guid> removed = [];
    private ObservableCollection<VodViewModel>? vodCards;
    private DownloadSettings? observedDownloadSettings;
    private bool isDownloadDirectoryChanging;
    private Task serviceCleanup = Task.CompletedTask;
    private string vodDownloadUrl = "";
    private string downloadUrlError = "";
    private string downloadsStatus = "Download a completed Twitch or Kick VOD to watch without internet.";

    internal VodDownloadsViewModel(MainViewModelDependencies dependencies, Action<string> setStatus,
        Func<string> getQuality, Func<StreamTarget, string, Task> openOffline, Action<VodDownloadItem> ensureNotOpen)
        : base(dependencies, setStatus)
    {
        service = dependencies.VodDownloadService;
        this.getQuality = getQuality;
        this.openOffline = openOffline;
        this.ensureNotOpen = ensureNotOpen;
        confirmDelete = dependencies.ConfirmDeleteVodDownload;
        DownloadVodUrlCommand = CreateCommand(DownloadUrlAsync, () => service is not null && !string.IsNullOrWhiteSpace(VodDownloadUrl));
        OpenDownloadFolderCommand = CreateCommand(OpenFolderAsync, () => service is not null);
        if (service is not null) service.DownloadChanged += OnDownloadChanged;
        Settings.PropertyChanged += OnSettingsChanged;
        ObserveDownloadSettings();
    }

    public ObservableCollection<VodDownloadViewModel> VodDownloads { get; } = [];
    public AsyncRelayCommand DownloadVodUrlCommand { get; }
    public AsyncRelayCommand OpenDownloadFolderCommand { get; }
    public string DownloadDirectory => service?.DownloadDirectory ?? "";
    public bool CanChangeDownloadDirectory => service is not null && !isDownloadDirectoryChanging;
    public bool HasVodDownloads => VodDownloads.Count != 0;
    public bool HasDownloadUrlError => DownloadUrlError.Length > 0;
    public string DownloadUrlError
    {
        get => downloadUrlError;
        private set
        {
            if (SetProperty(ref downloadUrlError, value)) OnPropertyChanged(nameof(HasDownloadUrlError));
        }
    }
    public string DownloadsStatus
    {
        get => downloadsStatus;
        private set => SetProperty(ref downloadsStatus, value);
    }
    public string VodDownloadUrl
    {
        get => vodDownloadUrl;
        set
        {
            if (!SetProperty(ref vodDownloadUrl, value ?? "")) return;
            DownloadUrlError = "";
            DownloadVodUrlCommand.RaiseCanExecuteChanged();
        }
    }

    internal void Initialize() => Track(LoadAsync());

    internal Task ChangeDownloadDirectoryAsync(string directory) => Track(ChangeDownloadDirectoryCoreAsync(directory));

    private async Task ChangeDownloadDirectoryCoreAsync(string directory)
    {
        if (!CanChangeDownloadDirectory) throw new InvalidOperationException("The download folder cannot be changed right now.");
        isDownloadDirectoryChanging = true;
        OnPropertyChanged(nameof(CanChangeDownloadDirectory));
        try
        {
            var previousDirectory = service!.DownloadDirectory;
            await service.ChangeDownloadDirectoryAsync(directory, lifetimeCancellation.Token);
            if (disposed) return;
            Settings.Downloads.PreviousDirectories = Settings.Downloads.PreviousDirectories.Append(previousDirectory)
                .Where(path => !path.Equals(service.DownloadDirectory, StringComparison.OrdinalIgnoreCase)).ToList();
            Settings.Downloads.Directory = service.DownloadDirectory;
            OnPropertyChanged(nameof(DownloadDirectory));
            await LoadAsync();
            StatusMessage = "Download folder changed. Existing and queued VODs stay in their original folders.";
        }
        finally
        {
            isDownloadDirectoryChanging = false;
            OnPropertyChanged(nameof(CanChangeDownloadDirectory));
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (arguments.PropertyName != nameof(AppSettings.Downloads)) return;
        dispatch(() =>
        {
            if (!disposed) ObserveDownloadSettings();
        });
    }

    private void ObserveDownloadSettings()
    {
        if (observedDownloadSettings is not null) observedDownloadSettings.PropertyChanged -= OnDownloadSettingsChanged;
        observedDownloadSettings = Settings.Downloads;
        observedDownloadSettings.PropertyChanged += OnDownloadSettingsChanged;
        service?.SetBandwidthLimit(observedDownloadSettings.BandwidthLimitBytesPerSecond);
        OnPropertyChanged(nameof(MainViewModel.SelectedVodDownloadQuality));
        RefreshVodCards();
    }

    private void OnDownloadSettingsChanged(object? sender, PropertyChangedEventArgs arguments)
    {
        if (disposed || !ReferenceEquals(sender, observedDownloadSettings)) return;
        if (arguments.PropertyName == nameof(DownloadSettings.BandwidthLimitMegabytesPerSecond))
            service?.SetBandwidthLimit(Settings.Downloads.BandwidthLimitBytesPerSecond);
        if (arguments.PropertyName != nameof(DownloadSettings.Quality)) return;
        dispatch(() =>
        {
            if (disposed || !ReferenceEquals(sender, observedDownloadSettings)) return;
            OnPropertyChanged(nameof(MainViewModel.SelectedVodDownloadQuality));
            RefreshVodCards();
        });
    }

    internal AsyncRelayCommand CreateDownloadCommand(VodViewModel vod) =>
        CreateCommand(() => DownloadFromCardAsync(vod), () => service is not null &&
            (vod.Download is null ||
             (vod.Download.CanPlay && vod.Download.PlayCommand.CanExecute(null)) ||
             (vod.Download.CanRetry && vod.Download.RetryCommand.CanExecute(null))));

    internal void BindVodCards(ObservableCollection<VodViewModel> collection)
    {
        if (vodCards is not null) vodCards.CollectionChanged -= OnVodCardsChanged;
        vodCards = collection;
        vodCards.CollectionChanged += OnVodCardsChanged;
        RefreshVodCards();
    }

    internal void RefreshVodCards()
    {
        if (vodCards is null || disposed) return;
        foreach (var vod in vodCards) UpdateVodCard(vod);
    }

    private void OnVodCardsChanged(object? sender, NotifyCollectionChangedEventArgs arguments)
    {
        if (disposed) return;
        if (arguments.NewItems is not null)
            foreach (VodViewModel vod in arguments.NewItems) UpdateVodCard(vod);
        else if (arguments.Action == NotifyCollectionChangedAction.Reset) RefreshVodCards();
    }

    private void UpdateVodCard(VodViewModel vod)
    {
        var identity = vod.Target.TabIdentityKey;
        var quality = getQuality().Trim();
        var matches = VodDownloads.Where(card => card.Item.Target.TabIdentityKey == identity &&
            card.Item.Quality.Equals(quality, StringComparison.OrdinalIgnoreCase));
        vod.UpdateDownload(matches.FirstOrDefault(card => card.CanPlay || card.CanCancel) ?? matches.FirstOrDefault());
    }

    private void RefreshVodCards(StreamTarget target)
    {
        if (vodCards is null || disposed) return;
        foreach (var vod in vodCards.Where(vod => vod.Target.TabIdentityKey == target.TabIdentityKey)) UpdateVodCard(vod);
    }

    private async Task DownloadFromCardAsync(VodViewModel vod)
    {
        var download = vod.Download;
        if (download?.CanPlay == true)
        {
            await download.PlayCommand.ExecuteAsync();
            return;
        }

        vod.IsDownloadStarting = true;
        try
        {
            if (download?.CanRetry == true) await download.RetryCommand.ExecuteAsync();
            else await EnqueueAsync(vod.Target);
        }
        finally { vod.IsDownloadStarting = false; }
    }

    private async Task LoadAsync()
    {
        if (service is null) return;
        try
        {
            var entries = await service.GetDownloadsAsync(lifetimeCancellation.Token);
            if (disposed) return;
            foreach (var entry in entries) Apply(entry);
            RefreshStatus();
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DownloadsStatus = $"Could not load downloaded VODs: {exception.Message}";
            logger.Write(AppLogLevel.Warning, "VOD downloads", "Could not load the offline library.", exception);
        }
    }

    private async Task DownloadUrlAsync()
    {
        var input = VodDownloadUrl;
        DownloadUrlError = "";
        StreamTarget target;
        try
        {
            target = VodDownloadUrlParser.Parse(input);
        }
        catch (ArgumentException)
        {
            DownloadUrlError = "Enter a Twitch or Kick video page link. Live streams and clips cannot be downloaded.";
            throw;
        }
        try
        {
            await EnqueueAsync(target);
            if (!disposed && VodDownloadUrl == input) VodDownloadUrl = "";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!disposed && VodDownloadUrl == input) DownloadUrlError = exception.Message;
            throw;
        }
    }

    private async Task EnqueueAsync(StreamTarget target)
    {
        if (service is null) throw new InvalidOperationException("VOD downloads are unavailable.");
        var item = await service.EnqueueAsync(new VodDownloadRequest(target, getQuality(), Options()), lifetimeCancellation.Token);
        if (disposed) return;
        Apply(item);
        StatusMessage = item.State == VodDownloadState.Completed ? "This VOD is already downloaded. Open Downloads to watch it offline." :
            "VOD queued. Progress appears on its card and in Downloads; you can keep watching while it downloads.";
    }

    private VodDownloadOptions Options() => new(Settings.StreamlinkPath ?? "",
        CommandLineTokenizer.Tokenize(Settings.CustomStreamlinkArguments));

    private void OnDownloadChanged(VodDownloadItem item) => dispatch(() =>
    {
        if (!disposed && !removed.Contains(item.Id)) Apply(item);
    });

    private void Apply(VodDownloadItem item)
    {
        if (removed.Contains(item.Id)) return;
        if (cards.TryGetValue(item.Id, out var card)) card.Update(item);
        else
        {
            card = new VodDownloadViewModel(item, (execute, canExecute) => CreateCommand(execute, canExecute),
                PlayAsync, CancelAsync, RetryAsync, DeleteAsync);
            cards.Add(item.Id, card);
            var index = 0;
            while (index < VodDownloads.Count && VodDownloads[index].Item.CreatedAtUtc > item.CreatedAtUtc) index++;
            VodDownloads.Insert(index, card);
            OnPropertyChanged(nameof(HasVodDownloads));
        }
        RefreshStatus();
        RefreshVodCards(card.Item.Target);
    }

    private void RefreshStatus()
    {
        var ready = VodDownloads.Count(card => card.CanPlay);
        var active = VodDownloads.Count(card => card.CanCancel);
        DownloadsStatus = VodDownloads.Count == 0 ? "No downloaded VODs yet. Paste a VOD URL or download one from Past broadcasts." :
            $"{ready} ready to watch offline · {active} queued or downloading";
    }

    private async Task PlayAsync(VodDownloadViewModel card)
    {
        var target = await service!.GetOfflineTargetAsync(card.Id, lifetimeCancellation.Token);
        if (!disposed) await openOffline(target, card.Item.Quality);
    }

    private Task CancelAsync(VodDownloadViewModel card) => service!.CancelAsync(card.Id, lifetimeCancellation.Token);
    private Task RetryAsync(VodDownloadViewModel card)
    {
        ensureNotOpen(card.Item);
        return service!.RetryAsync(card.Id, Options(), lifetimeCancellation.Token);
    }

    private async Task DeleteAsync(VodDownloadViewModel card)
    {
        ensureNotOpen(card.Item);
        if (confirmDelete is not null && !confirmDelete(card.Item)) return;
        await service!.RemoveAsync(card.Id, lifetimeCancellation.Token);
        if (disposed) return;
        removed.Add(card.Id);
        cards.Remove(card.Id);
        VodDownloads.Remove(card);
        OnPropertyChanged(nameof(HasVodDownloads));
        RefreshStatus();
        RefreshVodCards(card.Item.Target);
        StatusMessage = "Downloaded VOD deleted from disk.";
    }

    private Task OpenFolderAsync()
    {
        Directory.CreateDirectory(DownloadDirectory);
        Process.Start(new ProcessStartInfo(DownloadDirectory) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    protected override void StopOperations()
    {
        Settings.PropertyChanged -= OnSettingsChanged;
        if (observedDownloadSettings is not null) observedDownloadSettings.PropertyChanged -= OnDownloadSettingsChanged;
        if (vodCards is not null) vodCards.CollectionChanged -= OnVodCardsChanged;
        if (service is not null)
        {
            service.DownloadChanged -= OnDownloadChanged;
            serviceCleanup = service.DisposeAsync().AsTask();
        }
    }

    protected override Task WaitForOperationsAsync() => serviceCleanup;
}
