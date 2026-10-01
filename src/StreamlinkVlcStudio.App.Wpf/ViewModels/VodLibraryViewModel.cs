using System.Collections.ObjectModel;
using System.Collections.Specialized;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class VodLibraryViewModel : HomeFeatureViewModel
{
    private readonly Func<VodViewModel, bool, Task> openVod;
    private readonly Func<VodViewModel, AsyncRelayCommand>? downloadCommand;
    internal VodLibraryViewModel(MainViewModelDependencies dependencies, Action<string> setStatus,
        Func<VodViewModel, bool, Task> openVod,
        Func<VodViewModel, AsyncRelayCommand>? downloadCommand = null) : base(dependencies, setStatus)
    {
        this.openVod = openVod;
        this.downloadCommand = downloadCommand;
        twitchVodService = dependencies.TwitchVodService;
        kickVodService = dependencies.KickVodService;
        vodPlaybackHistory = dependencies.VodPlaybackHistory;
        twitchVodSearchDebounceInterval = dependencies.TwitchVodSearchDebounceInterval ?? DefaultTwitchVodSearchDebounceInterval;
        SearchTwitchVodsCommand = CreateCommand(
            () => SearchTwitchVodsAsync(reset: true),
            () => CanSearchSelectedVodPlatform);
        LoadMoreTwitchVodsCommand = CreateCommand(
            () => SearchTwitchVodsAsync(reset: false),
            () => CanLoadMoreTwitchVods);
        SelectTwitchVodPlatformCommand = new RelayCommand(() => SelectVodPlatform(PlatformKind.Twitch));
        SelectKickVodPlatformCommand = new RelayCommand(() => SelectVodPlatform(PlatformKind.Kick));
        ShowPastBroadcastsVodFilterCommand = new RelayCommand(() => SelectTwitchVodType(TwitchVodTypeFilter.Archive));
        ShowHighlightsVodFilterCommand = new RelayCommand(() => SelectTwitchVodType(TwitchVodTypeFilter.Highlight));
        ShowUploadsVodFilterCommand = new RelayCommand(() => SelectTwitchVodType(TwitchVodTypeFilter.Upload));
        ShowAllVodFilterCommand = new RelayCommand(() => SelectTwitchVodType(TwitchVodTypeFilter.All));
        TwitchVods.CollectionChanged += TwitchVodsOnCollectionChanged;
        if (vodPlaybackHistory is not null) vodPlaybackHistory.BookmarkChanged += OnVodBookmarkChanged;
    }

    protected override void StopOperations()
    {
        TwitchVods.CollectionChanged -= TwitchVodsOnCollectionChanged;
        if (vodPlaybackHistory is not null) vodPlaybackHistory.BookmarkChanged -= OnVodBookmarkChanged;
        vodBrowseController.Dispose();
    }
    protected override Task WaitForOperationsAsync() => vodBrowseController.DrainAsync(Timeout.InfiniteTimeSpan);
    private static readonly TimeSpan DefaultTwitchVodSearchDebounceInterval = TimeSpan.FromMilliseconds(450);
    private readonly IVodPlaybackHistory? vodPlaybackHistory;
    private readonly ITwitchVodService? twitchVodService;
    private readonly IKickVodService? kickVodService;
    private readonly TimeSpan twitchVodSearchDebounceInterval;
    private readonly VodBrowseController vodBrowseController = new();
    private readonly PagedResultTracker vodPages = new();
    private string twitchVodSearchText = "";
    private string twitchVodStatus = "Search a Twitch streamer to browse VODs.";
    private bool isTwitchVodSearchRunning;
    private Task? activeTwitchVodSearchTask;
    private int activeTwitchVodSearchGeneration;
    private (PlatformKind Platform, string Query, TwitchVodTypeFilter Type) activeTwitchVodSearch;
    private bool hasTwitchVodSearchCompleted;
    private TwitchVodTypeFilter selectedTwitchVodType = TwitchVodTypeFilter.Archive;
    private PlatformKind selectedVodPlatform = PlatformKind.Twitch;
    private string twitchVodNextCursor = "";
    public ObservableCollection<VodViewModel> TwitchVods { get; } = [];
    public AsyncRelayCommand SearchTwitchVodsCommand { get; }
    public AsyncRelayCommand LoadMoreTwitchVodsCommand { get; }
    public RelayCommand SelectTwitchVodPlatformCommand { get; }
    public RelayCommand SelectKickVodPlatformCommand { get; }
    public RelayCommand ShowPastBroadcastsVodFilterCommand { get; }
    public RelayCommand ShowHighlightsVodFilterCommand { get; }
    public RelayCommand ShowUploadsVodFilterCommand { get; }
    public RelayCommand ShowAllVodFilterCommand { get; }

    public string TwitchVodSearchText
    {
        get => twitchVodSearchText;
        set
        {
            if (SetProperty(ref twitchVodSearchText, value ?? ""))
            {
                vodBrowseController.AdvanceTwitchVodGeneration();
                CancelTwitchVodSearchDebounce();
                CancelActiveTwitchVodSearch();
                IsTwitchVodSearchRunning = false;
                ClearTwitchVodSearchResults();
                OnPropertyChanged(nameof(HasTwitchVodSearchText));
                OnPropertyChanged(nameof(IsTwitchVodSearchPlaceholderVisible));
                OnPropertyChanged(nameof(CanSearchSelectedVodPlatform));
                RaiseTwitchVodCommandStates();
                ScheduleAutomaticTwitchVodSearch();
            }
        }
    }

    public bool HasTwitchVodSearchText => !string.IsNullOrWhiteSpace(TwitchVodSearchText);

    public bool IsTwitchVodSearchPlaceholderVisible => !HasTwitchVodSearchText;

    public TwitchVodTypeFilter SelectedTwitchVodType
    {
        get => selectedTwitchVodType;
        internal set
        {
            if (selectedTwitchVodType == value)
            {
                return;
            }

            selectedTwitchVodType = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPastBroadcastsVodFilterSelected));
            OnPropertyChanged(nameof(IsHighlightsVodFilterSelected));
            OnPropertyChanged(nameof(IsUploadsVodFilterSelected));
            OnPropertyChanged(nameof(IsAllVodFilterSelected));
        }
    }

    public bool IsPastBroadcastsVodFilterSelected => SelectedTwitchVodType == TwitchVodTypeFilter.Archive;

    public bool IsHighlightsVodFilterSelected => SelectedTwitchVodType == TwitchVodTypeFilter.Highlight;

    public bool IsUploadsVodFilterSelected => SelectedTwitchVodType == TwitchVodTypeFilter.Upload;

    public bool IsAllVodFilterSelected => SelectedTwitchVodType == TwitchVodTypeFilter.All;

    public PlatformKind SelectedVodPlatform
    {
        get => selectedVodPlatform;
        internal set
        {
            if (selectedVodPlatform == value)
            {
                return;
            }

            selectedVodPlatform = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTwitchVodPlatformSelected));
            OnPropertyChanged(nameof(IsKickVodPlatformSelected));
            OnPropertyChanged(nameof(VodPlatformText));
            OnPropertyChanged(nameof(IsTwitchVodFilterVisible));
            OnPropertyChanged(nameof(TwitchVodResultsTitle));
            OnPropertyChanged(nameof(CanSearchSelectedVodPlatform));
            RaiseTwitchVodCommandStates();
        }
    }

    public bool IsTwitchVodPlatformSelected => SelectedVodPlatform == PlatformKind.Twitch;

    public bool IsKickVodPlatformSelected => SelectedVodPlatform == PlatformKind.Kick;

    public string VodPlatformText => SelectedVodPlatform.ToString();

    public bool IsTwitchVodFilterVisible => IsTwitchVodPlatformSelected;

    public string TwitchVodStatus
    {
        get => twitchVodStatus;
        internal set => SetProperty(ref twitchVodStatus, value);
    }

    public bool IsTwitchVodSearchRunning
    {
        get => isTwitchVodSearchRunning;
        internal set
        {
            if (SetProperty(ref isTwitchVodSearchRunning, value))
            {
                OnPropertyChanged(nameof(IsTwitchVodEmptyVisible));
                OnPropertyChanged(nameof(IsTwitchVodLoadMoreVisible));
                OnPropertyChanged(nameof(CanLoadMoreTwitchVods));
                RaiseTwitchVodCommandStates();
            }
        }
    }

    public bool HasTwitchVodSearchCompleted
    {
        get => hasTwitchVodSearchCompleted;
        internal set
        {
            if (SetProperty(ref hasTwitchVodSearchCompleted, value))
            {
                OnPropertyChanged(nameof(IsTwitchVodEmptyVisible));
            }
        }
    }

    public bool HasTwitchVods => TwitchVods.Count > 0;

    public bool IsTwitchVodEmptyVisible => HasTwitchVodSearchCompleted &&
        !IsTwitchVodSearchRunning &&
        !HasTwitchVods;

    public bool CanSearchSelectedVodPlatform => HasTwitchVodSearchText &&
        SelectedVodPlatform switch
        {
            PlatformKind.Twitch => twitchVodService is not null,
            PlatformKind.Kick => kickVodService is not null,
            _ => false
        };

    public bool CanLoadMoreTwitchVods => !IsTwitchVodSearchRunning &&
        !string.IsNullOrWhiteSpace(TwitchVodNextCursor);

    public bool IsTwitchVodLoadMoreVisible => HasTwitchVods && CanLoadMoreTwitchVods;

    public string TwitchVodResultsTitle => TwitchVods.Count switch
    {
        0 => $"{VodPlatformText} VODs",
        1 => $"1 {VodPlatformText} VOD",
        _ => $"{TwitchVods.Count} {VodPlatformText} VODs"
    };

    internal string TwitchVodNextCursor
    {
        get => twitchVodNextCursor;
        set
        {
            if (twitchVodNextCursor == value)
            {
                return;
            }

            twitchVodNextCursor = value;
            OnPropertyChanged(nameof(CanLoadMoreTwitchVods));
            OnPropertyChanged(nameof(IsTwitchVodLoadMoreVisible));
            RaiseTwitchVodCommandStates();
        }
    }

    internal void SelectVodPlatform(PlatformKind platform)
    {
        if (SelectedVodPlatform == platform)
        {
            return;
        }

        SelectedVodPlatform = platform;
        vodBrowseController.AdvanceTwitchVodGeneration();
        CancelTwitchVodSearchDebounce();
        CancelActiveTwitchVodSearch();
        IsTwitchVodSearchRunning = false;
        ClearTwitchVodSearchResults();
        if (!HasTwitchVodSearchText)
        {
            TwitchVodStatus = $"Search a {VodPlatformText} streamer to browse VODs.";
        }

        if (HasTwitchVodSearchText)
        {
            _ = SearchTwitchVodsAsync(reset: true);
        }
    }

    internal void SelectTwitchVodType(TwitchVodTypeFilter type)
    {
        if (SelectedTwitchVodType == type)
        {
            return;
        }

        SelectedTwitchVodType = type;
        CancelTwitchVodSearchDebounce();
        TwitchVodNextCursor = "";
        TwitchVods.Clear();
        HasTwitchVodSearchCompleted = false;
        if (SelectedVodPlatform == PlatformKind.Twitch && HasTwitchVodSearchText)
        {
            _ = SearchTwitchVodsAsync(reset: true);
        }
    }

    internal Task SearchTwitchVodsAsync(bool reset)
    {
        if (disposed) return Task.CompletedTask;
        if (reset) CancelTwitchVodSearchDebounce();
        var query = TwitchVodSearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            TwitchVodStatus = $"Enter a {VodPlatformText} streamer.";
            return Task.CompletedTask;
        }

        if (SelectedVodPlatform == PlatformKind.Twitch && twitchVodService is null)
        {
            TwitchVodStatus = "Twitch VOD search is not available.";
            return Task.CompletedTask;
        }

        if (SelectedVodPlatform == PlatformKind.Kick && kickVodService is null)
        {
            TwitchVodStatus = "Kick VOD search is not available.";
            return Task.CompletedTask;
        }

        var platform = SelectedVodPlatform;
        var type = SelectedTwitchVodType;
        var cursor = reset ? "" : TwitchVodNextCursor;
        if (!reset && string.IsNullOrWhiteSpace(cursor))
        {
            return Task.CompletedTask;
        }

        // Only share a first-page load. Refresh during pagination must replace
        // that page, and a completed search must remain explicitly refreshable.
        if (reset && activeTwitchVodSearchTask is { IsCompleted: false } &&
            activeTwitchVodSearchGeneration == vodBrowseController.CurrentTwitchVodGeneration &&
            activeTwitchVodSearch == (platform, query, type))
        {
            return activeTwitchVodSearchTask;
        }

        var searchGeneration = reset
            ? vodBrowseController.AdvanceTwitchVodGeneration()
            : vodBrowseController.CurrentTwitchVodGeneration;
        activeTwitchVodSearchGeneration = searchGeneration;
        activeTwitchVodSearch = (platform, query, type);
        var task = RunTwitchVodSearchAsync(reset, platform,
            new TwitchVodSearchRequest(query, type, cursor, 100), searchGeneration);
        activeTwitchVodSearchTask = reset ? task : null;
        return task;
    }

    internal async Task RunTwitchVodSearchAsync(bool reset, PlatformKind platform,
        TwitchVodSearchRequest request, int searchGeneration)
    {
        var query = request.Streamer;
        var type = request.Type;
        var cursor = request.Cursor;
        var searchCancellation = ReplaceTwitchVodSearchCancellation();
        if (reset)
        {
            // Query/platform/filter changes clear at their navigation boundary. Refresh
            // keeps the last successful page usable until its replacement is available.
            HasTwitchVodSearchCompleted = false;
        }

        IsTwitchVodSearchRunning = true;
        TwitchVodStatus = reset
            ? $"Searching {platform} VODs for {query}"
            : $"Loading more {platform} VODs for {query}";
        StatusMessage = TwitchVodStatus;

        try
        {
            string message;
            if (platform == PlatformKind.Twitch)
            {
                var result = await twitchVodService!.SearchAsync(
                    request,
                    Settings,
                    searchCancellation.Token);
                if (!IsCurrentTwitchVodSearch(searchGeneration, query, type, platform)) return;
                if (result.IsAvailable)
                {
                    TwitchVodNextCursor = vodPages.ApplyPage(
                        TwitchVods, result.Videos, card => card.Identity, VodViewModel.GetIdentity,
                        vod => new VodViewModel(vod, openVod, downloadCommand), (card, vod) => card.Update(vod),
                        cursor, result.NextCursor);
                }
                message = result.Message;
            }
            else
            {
                var result = await kickVodService!.SearchAsync(
                    new KickVodSearchRequest(query, cursor, 100),
                    Settings,
                    searchCancellation.Token);
                if (!IsCurrentTwitchVodSearch(searchGeneration, query, type, platform)) return;
                if (result.IsAvailable)
                {
                    TwitchVodNextCursor = vodPages.ApplyPage(
                        TwitchVods, result.Videos, card => card.Identity, VodViewModel.GetIdentity,
                        vod => new VodViewModel(vod, openVod, downloadCommand), (card, vod) => card.Update(vod),
                        cursor, result.NextCursor);
                }
                message = result.Message;
            }

            await LoadVodWatchProgressAsync(searchCancellation.Token);
            if (!IsCurrentTwitchVodSearch(searchGeneration, query, type, platform)) return;

            HasTwitchVodSearchCompleted = true;
            TwitchVodStatus = message;
            StatusMessage = message;
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentTwitchVodSearch(searchGeneration, query, type, platform))
            {
                return;
            }

            HasTwitchVodSearchCompleted = true;
            TwitchVodStatus = ex.Message;
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "VODs", $"{platform} VOD search failed.", ex);
        }
        finally
        {
            if (IsCurrentTwitchVodSearch(searchGeneration, query, type, platform))
            {
                IsTwitchVodSearchRunning = false;
            }

            DisposeTwitchVodSearchCancellation(searchCancellation);
        }
    }

    internal async Task LoadVodWatchProgressAsync(CancellationToken cancellationToken)
    {
        if (vodPlaybackHistory is null) return;
        try
        {
            foreach (var card in TwitchVods.ToArray())
            {
                var bookmark = await vodPlaybackHistory.GetAsync(card.Target, cancellationToken);
                if (disposed || cancellationToken.IsCancellationRequested) return;
                card.UpdateWatchProgress(bookmark);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "VODs", "Could not load VOD watch progress.", ex);
        }
    }

    internal void OnVodBookmarkChanged(StreamTarget target, VodPlaybackBookmark bookmark)
    {
        if (disposed) return;
        dispatch(() =>
        {
            if (disposed) return;
            foreach (var card in TwitchVods)
            {
                if (card.Platform == target.Platform &&
                    string.Equals(card.Id.Trim(), target.MediaId.Trim(), StringComparison.OrdinalIgnoreCase))
                    card.UpdateWatchProgress(bookmark);
            }
        });
    }

    internal bool IsCurrentTwitchVodSearch(int searchGeneration, string query, TwitchVodTypeFilter type)
    {
        return IsCurrentTwitchVodSearch(searchGeneration, query, type, SelectedVodPlatform);
    }

    internal bool IsCurrentTwitchVodSearch(int searchGeneration, string query, TwitchVodTypeFilter type, PlatformKind platform)
    {
        return !disposed &&
            vodBrowseController.IsCurrentTwitchVodGeneration(searchGeneration) &&
            SelectedVodPlatform == platform &&
            SelectedTwitchVodType == type &&
            string.Equals(TwitchVodSearchText.Trim(), query, StringComparison.Ordinal);
    }

    internal void ClearTwitchVodSearchResults()
    {
        TwitchVods.Clear();
        TwitchVodNextCursor = "";
        HasTwitchVodSearchCompleted = false;
        if (!HasTwitchVodSearchText)
        {
            TwitchVodStatus = $"Search a {VodPlatformText} streamer to browse VODs.";
        }
    }

    internal void ScheduleAutomaticTwitchVodSearch()
    {
        if (disposed)
        {
            return;
        }

        var query = TwitchVodSearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var searchGeneration = vodBrowseController.CurrentTwitchVodGeneration;
        var type = SelectedTwitchVodType;
        if (twitchVodSearchDebounceInterval <= TimeSpan.Zero)
        {
            dispatch(() => _ = RunAutomaticTwitchVodSearchAsync(query, type, searchGeneration));
            return;
        }

        vodBrowseController.ScheduleTwitchVod(
            twitchVodSearchDebounceInterval,
            () => dispatch(() => _ = RunAutomaticTwitchVodSearchAsync(query, type, searchGeneration)),
            ReportDebouncedCallbackFailure);
    }

    internal async Task RunAutomaticTwitchVodSearchAsync(
        string query,
        TwitchVodTypeFilter type,
        int searchGeneration)
    {
        if (disposed || !IsCurrentTwitchVodSearch(searchGeneration, query, type))
        {
            return;
        }

        await SearchTwitchVodsAsync(reset: true);
    }

    internal void CancelTwitchVodSearchDebounce()
    {
        vodBrowseController.CancelScheduledTwitchVod();
    }

    internal CancellationTokenSource ReplaceTwitchVodSearchCancellation()
    {
        return vodBrowseController.BeginTwitchVodOperation(lifetimeCancellation.Token);
    }

    internal void CancelActiveTwitchVodSearch()
    {
        vodBrowseController.CancelTwitchVodOperation();
    }

    internal void DisposeTwitchVodSearchCancellation(CancellationTokenSource cancellation)
    {
        vodBrowseController.CompleteTwitchVodOperation(cancellation);
    }

    internal void RaiseTwitchVodCommandStates()
    {
        SearchTwitchVodsCommand.RaiseCanExecuteChanged();
        LoadMoreTwitchVodsCommand.RaiseCanExecuteChanged();
    }

    internal void TwitchVodsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTwitchVods));
        OnPropertyChanged(nameof(IsTwitchVodEmptyVisible));
        OnPropertyChanged(nameof(IsTwitchVodLoadMoreVisible));
        OnPropertyChanged(nameof(TwitchVodResultsTitle));
    }
}
