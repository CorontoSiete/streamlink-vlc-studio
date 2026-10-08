using System.Collections.ObjectModel;
using System.Collections.Specialized;
using StreamlinkVlcStudio.Core.Commands;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class BrowseViewModel : HomeFeatureViewModel
{
    private readonly Func<LiveStreamCardViewModel, bool, Task> openStream;
    private readonly Action recordNavigation;
    private readonly Func<bool> isBrowseHomeVisible;
    internal BrowseViewModel(MainViewModelDependencies dependencies, Action<string> setStatus,
        Func<LiveStreamCardViewModel, bool, Task> openStream, Action recordNavigation,
        Func<bool> isBrowseHomeVisible) : base(dependencies, setStatus)
    {
        this.openStream = openStream;
        this.recordNavigation = recordNavigation;
        this.isBrowseHomeVisible = isBrowseHomeVisible;
        browseService = dependencies.BrowseService;
        browseCategorySearchDebounceInterval = dependencies.BrowseCategorySearchDebounceInterval ?? DefaultBrowseCategorySearchDebounceInterval;
        ReturnToBrowseCategoriesCommand = new RelayCommand(ReturnToBrowseCategoriesPage, () => IsBrowseStreamsPageVisible);
        SelectTwitchBrowsePlatformCommand = new RelayCommand(() => SelectBrowsePlatform(PlatformKind.Twitch));
        SelectKickBrowsePlatformCommand = new RelayCommand(() => SelectBrowsePlatform(PlatformKind.Kick));
        RefreshBrowseCommand = CreateCommand(RefreshBrowseAsync, () => browseService is not null);
        LoadMoreBrowseCategoriesCommand = CreateCommand(
            () => LoadBrowseCategoriesAsync(reset: false),
            () => browseService is not null && CanLoadMoreBrowseCategories);
        LoadMoreBrowseStreamsCommand = CreateCommand(
            () => LoadBrowseStreamsAsync(reset: false),
            () => browseService is not null && CanLoadMoreBrowseStreams);
        BrowseCategories.CollectionChanged += BrowseCategoriesOnCollectionChanged;
        BrowseStreams.CollectionChanged += BrowseStreamsOnCollectionChanged;
    }

    internal bool IsStreamsPageSelected => isBrowseStreamsPageSelected;
    protected override void StopOperations()
    {
        BrowseCategories.CollectionChanged -= BrowseCategoriesOnCollectionChanged;
        BrowseStreams.CollectionChanged -= BrowseStreamsOnCollectionChanged;
        vodBrowseController.Dispose();
        CancelActiveBrowseCategoryViewerCountLoad();
    }
    protected override Task WaitForOperationsAsync() => vodBrowseController.DrainAsync(Timeout.InfiniteTimeSpan);
    private static readonly TimeSpan DefaultBrowseCategorySearchDebounceInterval = TimeSpan.FromMilliseconds(450);
    private const int BrowseCategoryPageSize = 10;
    private const int BrowseCategoryViewerCountBatchSize = 1;
    private const int BrowseCategoryViewerCountConcurrency = 4;
    private const int BrowseStreamPageSize = 50;
    private readonly IBrowseService? browseService;
    private readonly TimeSpan browseCategorySearchDebounceInterval;
    private readonly object browseCategoryViewerCountGate = new();
    private readonly VodBrowseController vodBrowseController = new();
    private readonly PagedResultTracker browseCategoryPages = new();
    private readonly PagedResultTracker browseStreamPages = new();
    private string browseCategorySearchText = "";
    private string browseStatus = "Browse Twitch or Kick categories.";
    private string browseCategoryStatus = "Browse Twitch or Kick categories.";
    private string browseCategoryNextCursor = "";
    private string browseStreamNextCursor = "";
    private bool isBrowseStreamsPageSelected;
    private bool isBrowseCategoriesLoading;
    private Task? activeBrowseCategoryTask;
    private int activeBrowseCategoryGeneration;
    private (PlatformKind Platform, string Query) activeBrowseCategorySearch;
    private bool hasBrowseCategorySearchCompleted;
    private bool isBrowseStreamsLoading;
    private Task? activeBrowseStreamTask;
    private int activeBrowseStreamGeneration;
    private (PlatformKind Platform, string CategoryId) activeBrowseStreamSearch;
    private bool hasBrowseStreamSearchCompleted;
    private CancellationTokenSource? browseCategoryViewerCountCancellation;
    private bool browseCategoryViewerCountLoadPending;
    private PlatformKind selectedBrowsePlatform = PlatformKind.Twitch;
    private BrowseCategoryViewModel? selectedBrowseCategory;
    public ObservableCollection<BrowseCategoryViewModel> BrowseCategories { get; } = [];
    public ObservableCollection<LiveStreamCardViewModel> BrowseStreams { get; } = [];
    public RelayCommand ReturnToBrowseCategoriesCommand { get; }
    public RelayCommand SelectTwitchBrowsePlatformCommand { get; }
    public RelayCommand SelectKickBrowsePlatformCommand { get; }
    public AsyncRelayCommand RefreshBrowseCommand { get; }
    public AsyncRelayCommand LoadMoreBrowseCategoriesCommand { get; }
    public AsyncRelayCommand LoadMoreBrowseStreamsCommand { get; }

    public string BrowseCategorySearchText
    {
        get => browseCategorySearchText;
        set
        {
            if (SetProperty(ref browseCategorySearchText, value ?? ""))
            {
                vodBrowseController.AdvanceBrowseCategoryGeneration();
                vodBrowseController.AdvanceBrowseCategoryViewerCountGeneration();
                vodBrowseController.AdvanceBrowseStreamGeneration();
                CancelBrowseCategorySearchDebounce();
                CancelActiveBrowseCategorySearch();
                CancelActiveBrowseCategoryViewerCountLoad();
                CancelActiveBrowseStreamSearch();
                IsBrowseCategoriesLoading = false;
                IsBrowseStreamsLoading = false;
                SetBrowseStreamsPageSelected(false);
                ClearBrowseCategories(clearStatus: false);
                ClearBrowseStreams(clearSelectedCategory: true);
                HasBrowseCategorySearchCompleted = false;
                OnPropertyChanged(nameof(HasBrowseCategorySearchText));
                OnPropertyChanged(nameof(IsBrowseCategorySearchPlaceholderVisible));
                RaiseBrowseCommandStates();
                ScheduleAutomaticBrowseCategorySearch();
                recordNavigation();
            }
        }
    }

    public bool HasBrowseCategorySearchText => !string.IsNullOrWhiteSpace(BrowseCategorySearchText);

    public bool IsBrowseCategorySearchPlaceholderVisible => !HasBrowseCategorySearchText;

    public PlatformKind SelectedBrowsePlatform
    {
        get => selectedBrowsePlatform;
        internal set
        {
            if (selectedBrowsePlatform == value)
            {
                return;
            }

            selectedBrowsePlatform = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTwitchBrowsePlatformSelected));
            OnPropertyChanged(nameof(IsKickBrowsePlatformSelected));
            OnPropertyChanged(nameof(BrowsePlatformText));
            OnPropertyChanged(nameof(BrowseCategoriesTitle));
        }
    }

    public bool IsTwitchBrowsePlatformSelected => SelectedBrowsePlatform == PlatformKind.Twitch;

    public bool IsKickBrowsePlatformSelected => SelectedBrowsePlatform == PlatformKind.Kick;

    public string BrowsePlatformText => SelectedBrowsePlatform.ToString();

    public string BrowseStatus
    {
        get => browseStatus;
        internal set => SetProperty(ref browseStatus, value ?? "");
    }

    public BrowseCategoryViewModel? SelectedBrowseCategory
    {
        get => selectedBrowseCategory;
        internal set
        {
            if (ReferenceEquals(selectedBrowseCategory, value))
            {
                return;
            }

            selectedBrowseCategory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedBrowseCategory));
            OnPropertyChanged(nameof(SelectedBrowseCategoryName));
            OnPropertyChanged(nameof(BrowseStreamsTitle));
            OnPropertyChanged(nameof(IsBrowseStreamsEmptyVisible));
            OnPropertyChanged(nameof(CanLoadMoreBrowseStreams));
            OnPropertyChanged(nameof(IsBrowseStreamLoadMoreVisible));
            RaiseBrowseCommandStates();
        }
    }

    public bool HasSelectedBrowseCategory => SelectedBrowseCategory is not null;

    public string SelectedBrowseCategoryName => SelectedBrowseCategory?.Name ?? "";

    public bool IsBrowseCategoriesLoading
    {
        get => isBrowseCategoriesLoading;
        internal set
        {
            if (SetProperty(ref isBrowseCategoriesLoading, value))
            {
                OnPropertyChanged(nameof(IsBrowseCategoriesEmptyVisible));
                OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreVisible));
                OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreIndicatorVisible));
                OnPropertyChanged(nameof(CanLoadMoreBrowseCategories));
                RaiseBrowseCommandStates();
            }
        }
    }

    public bool HasBrowseCategorySearchCompleted
    {
        get => hasBrowseCategorySearchCompleted;
        internal set
        {
            if (SetProperty(ref hasBrowseCategorySearchCompleted, value))
            {
                OnPropertyChanged(nameof(IsBrowseCategoriesEmptyVisible));
            }
        }
    }

    public bool IsBrowseStreamsLoading
    {
        get => isBrowseStreamsLoading;
        internal set
        {
            if (SetProperty(ref isBrowseStreamsLoading, value))
            {
                OnPropertyChanged(nameof(IsBrowseStreamsEmptyVisible));
                OnPropertyChanged(nameof(IsBrowseStreamLoadMoreVisible));
                OnPropertyChanged(nameof(CanLoadMoreBrowseStreams));
                RaiseBrowseCommandStates();
            }
        }
    }

    public bool HasBrowseStreamSearchCompleted
    {
        get => hasBrowseStreamSearchCompleted;
        internal set
        {
            if (SetProperty(ref hasBrowseStreamSearchCompleted, value))
            {
                OnPropertyChanged(nameof(IsBrowseStreamsEmptyVisible));
            }
        }
    }

    public bool HasBrowseCategories => BrowseCategories.Count > 0;

    public bool HasBrowseStreams => BrowseStreams.Count > 0;

    public bool IsBrowseCategoriesEmptyVisible => IsBrowseCategoriesPageVisible &&
        HasBrowseCategorySearchCompleted &&
        !IsBrowseCategoriesLoading &&
        !HasBrowseCategories;

    public bool IsBrowseStreamsEmptyVisible => IsBrowseStreamsPageVisible &&
        HasSelectedBrowseCategory &&
        HasBrowseStreamSearchCompleted &&
        !IsBrowseStreamsLoading &&
        !HasBrowseStreams;

    public bool CanLoadMoreBrowseCategories => IsBrowseCategoriesPageVisible &&
        !IsBrowseCategoriesLoading &&
        !string.IsNullOrWhiteSpace(BrowseCategoryNextCursor);

    public bool CanLoadMoreBrowseStreams => IsBrowseStreamsPageVisible &&
        HasSelectedBrowseCategory &&
        !IsBrowseStreamsLoading &&
        !string.IsNullOrWhiteSpace(BrowseStreamNextCursor);

    public bool IsBrowseCategoryLoadMoreVisible => HasBrowseCategories && CanLoadMoreBrowseCategories;

    public bool IsBrowseCategoryLoadMoreIndicatorVisible => HasBrowseCategories &&
        (IsBrowseCategoriesLoading || CanLoadMoreBrowseCategories);

    public bool IsBrowseStreamLoadMoreVisible => HasBrowseStreams && CanLoadMoreBrowseStreams;

    public string BrowseCategoriesTitle => BrowseCategories.Count switch
    {
        0 => $"{BrowsePlatformText} categories",
        1 => $"1 {BrowsePlatformText} category",
        _ => $"{BrowseCategories.Count} {BrowsePlatformText} categories"
    };

    public string BrowseStreamsTitle
    {
        get
        {
            if (SelectedBrowseCategory is null)
            {
                return "Select a category";
            }

            return BrowseStreams.Count switch
            {
                0 => $"Live in {SelectedBrowseCategory.Name}",
                1 => $"1 stream in {SelectedBrowseCategory.Name}",
                _ => $"{BrowseStreams.Count} streams in {SelectedBrowseCategory.Name}"
            };
        }
    }

    public bool IsBrowseCategoriesPageVisible => isBrowseHomeVisible() && !isBrowseStreamsPageSelected;

    public bool IsBrowseStreamsPageVisible => isBrowseHomeVisible() && isBrowseStreamsPageSelected;

    internal string BrowseCategoryNextCursor
    {
        get => browseCategoryNextCursor;
        set
        {
            if (browseCategoryNextCursor == value)
            {
                return;
            }

            browseCategoryNextCursor = value;
            OnPropertyChanged(nameof(CanLoadMoreBrowseCategories));
            OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreVisible));
            OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreIndicatorVisible));
            RaiseBrowseCommandStates();
        }
    }

    internal string BrowseStreamNextCursor
    {
        get => browseStreamNextCursor;
        set
        {
            if (browseStreamNextCursor == value)
            {
                return;
            }

            browseStreamNextCursor = value;
            OnPropertyChanged(nameof(CanLoadMoreBrowseStreams));
            OnPropertyChanged(nameof(IsBrowseStreamLoadMoreVisible));
            RaiseBrowseCommandStates();
        }
    }

    internal void ShowBrowseCategoriesPage(bool clearSelection)
    {
        SetBrowseStreamsPageSelected(false);
        if (!clearSelection)
        {
            return;
        }

        vodBrowseController.AdvanceBrowseStreamGeneration();
        CancelActiveBrowseStreamSearch();
        IsBrowseStreamsLoading = false;
        ClearBrowseStreams(clearSelectedCategory: true);
    }

    internal void ReturnToBrowseCategoriesPage()
    {
        ShowBrowseCategoriesPage(clearSelection: true);
        BrowseStatus = browseCategoryStatus;
        StatusMessage = BrowseStatus;
        StartBrowseCategoryViewerCountLoad(SelectedBrowsePlatform, BrowseCategorySearchText.Trim());
        recordNavigation();
    }

    internal void SetBrowseStreamsPageSelected(bool value)
    {
        if (isBrowseStreamsPageSelected == value)
        {
            return;
        }

        isBrowseStreamsPageSelected = value;
        RaiseBrowsePageStateChanged();
    }

    internal void RaiseBrowsePageStateChanged()
    {
        OnPropertyChanged(nameof(IsBrowseCategoriesPageVisible));
        OnPropertyChanged(nameof(IsBrowseStreamsPageVisible));
        OnPropertyChanged(nameof(IsBrowseCategoriesEmptyVisible));
        OnPropertyChanged(nameof(IsBrowseStreamsEmptyVisible));
        OnPropertyChanged(nameof(CanLoadMoreBrowseCategories));
        OnPropertyChanged(nameof(CanLoadMoreBrowseStreams));
        OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreVisible));
        OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreIndicatorVisible));
        OnPropertyChanged(nameof(IsBrowseStreamLoadMoreVisible));
        ReturnToBrowseCategoriesCommand.RaiseCanExecuteChanged();
        RaiseBrowseCommandStates();
    }

    internal void SelectBrowsePlatform(PlatformKind platform)
    {
        if (SelectedBrowsePlatform == platform)
        {
            if (IsBrowseStreamsPageVisible)
            {
                ReturnToBrowseCategoriesPage();
            }

            if (!HasBrowseCategories && !IsBrowseCategoriesLoading)
            {
                _ = LoadBrowseCategoriesAsync(reset: true);
            }

            return;
        }

        vodBrowseController.AdvanceBrowseCategoryGeneration();
        vodBrowseController.AdvanceBrowseCategoryViewerCountGeneration();
        vodBrowseController.AdvanceBrowseStreamGeneration();
        CancelBrowseCategorySearchDebounce();
        CancelActiveBrowseCategorySearch();
        CancelActiveBrowseCategoryViewerCountLoad();
        CancelActiveBrowseStreamSearch();
        SelectedBrowsePlatform = platform;
        SetBrowseStreamsPageSelected(false);
        ClearBrowseCategories(clearStatus: false);
        ClearBrowseStreams(clearSelectedCategory: true);
        HasBrowseCategorySearchCompleted = false;
        browseCategoryStatus = $"Loading {platform} categories";
        BrowseStatus = $"Loading {platform} categories";
        StatusMessage = BrowseStatus;
        recordNavigation();
        _ = LoadBrowseCategoriesAsync(reset: true);
    }

    internal async Task RefreshBrowseAsync()
    {
        if (disposed) return;
        if (IsBrowseStreamsPageVisible && SelectedBrowseCategory is not null)
        {
            await LoadBrowseStreamsAsync(reset: true);
            return;
        }

        await LoadBrowseCategoriesAsync(reset: true);
    }

    internal Task LoadBrowseCategoriesAsync(bool reset)
    {
        if (disposed) return Task.CompletedTask;
        if (reset) CancelBrowseCategorySearchDebounce();
        if (browseService is null)
        {
            BrowseStatus = "Browse is not available.";
            return Task.CompletedTask;
        }

        var platform = SelectedBrowsePlatform;
        var query = BrowseCategorySearchText.Trim();
        var cursor = reset ? "" : BrowseCategoryNextCursor;
        if (!reset && string.IsNullOrWhiteSpace(cursor))
        {
            return Task.CompletedTask;
        }

        if (reset && activeBrowseCategoryTask is { IsCompleted: false } &&
            activeBrowseCategoryGeneration == vodBrowseController.CurrentBrowseCategoryGeneration &&
            activeBrowseCategorySearch == (platform, query))
        {
            return activeBrowseCategoryTask;
        }

        var searchGeneration = reset
            ? vodBrowseController.AdvanceBrowseCategoryGeneration()
            : vodBrowseController.CurrentBrowseCategoryGeneration;
        activeBrowseCategoryGeneration = searchGeneration;
        activeBrowseCategorySearch = (platform, query);
        var task = RunBrowseCategoriesAsync(reset,
            new BrowseCategoryRequest(platform, query, cursor, BrowseCategoryPageSize), searchGeneration);
        activeBrowseCategoryTask = reset ? task : null;
        return task;
    }

    internal async Task RunBrowseCategoriesAsync(bool reset, BrowseCategoryRequest request, int searchGeneration)
    {
        var platform = request.Platform;
        var query = request.Query;
        var cursor = request.Cursor;
        var searchCancellation = ReplaceBrowseCategorySearchCancellation();
        if (reset)
        {
            vodBrowseController.AdvanceBrowseCategoryViewerCountGeneration();
            CancelActiveBrowseCategoryViewerCountLoad();
            SetBrowseStreamsPageSelected(false);
            // Preserve cards and pagination during refresh; navigation clears old results.
            ClearBrowseStreams(clearSelectedCategory: true);
            HasBrowseCategorySearchCompleted = false;
        }

        IsBrowseCategoriesLoading = true;
        BrowseStatus = string.IsNullOrWhiteSpace(query)
            ? $"Loading {platform} categories"
            : $"Searching {platform} categories for {query}";
        browseCategoryStatus = BrowseStatus;
        StatusMessage = BrowseStatus;

        try
        {
            var result = await browseService!.GetCategoriesAsync(
                request,
                Settings,
                searchCancellation.Token);
            if (!IsCurrentBrowseCategorySearch(searchGeneration, platform, query))
            {
                return;
            }

            if (result.IsAvailable)
            {
                var sortByViewerCount = platform == PlatformKind.Kick || !string.IsNullOrWhiteSpace(query);
                IEnumerable<BrowseCategory> categories = sortByViewerCount && reset
                    ? OrderBrowseCategories(
                        result.Items.DistinctBy(category => $"{category.Platform}:{category.Id}", StringComparer.OrdinalIgnoreCase),
                        category => category.ViewerCount,
                        category => string.IsNullOrWhiteSpace(category.Name) ? "Untitled category" : category.Name)
                    : result.Items;
                BrowseCategoryNextCursor = browseCategoryPages.ApplyPage(
                    BrowseCategories, categories,
                    card => $"{card.Platform}:{card.Id}", category => $"{category.Platform}:{category.Id}",
                    category => new BrowseCategoryViewModel(category, SelectBrowseCategoryAsync),
                    (card, category) => card.Update(category), cursor, result.NextCursor);
                if (sortByViewerCount && !reset)
                {
                    SortBrowseCategoriesByViewerCount();
                }
            }

            HasBrowseCategorySearchCompleted = true;
            BrowseStatus = result.Message;
            browseCategoryStatus = result.Message;
            StatusMessage = result.Message;
            if (result.IsAvailable) StartBrowseCategoryViewerCountLoad(platform, query);
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentBrowseCategorySearch(searchGeneration, platform, query))
            {
                return;
            }

            HasBrowseCategorySearchCompleted = true;
            BrowseStatus = ex.Message;
            browseCategoryStatus = ex.Message;
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Browse", $"{platform} category browse failed.", ex);
        }
        finally
        {
            if (IsCurrentBrowseCategorySearch(searchGeneration, platform, query))
            {
                IsBrowseCategoriesLoading = false;
            }

            DisposeBrowseCategorySearchCancellation(searchCancellation);
        }
    }

    internal void SortBrowseCategoriesByViewerCount()
    {
        var sortedCategories = OrderBrowseCategories(BrowseCategories,
                category => category.Category.ViewerCount, category => category.Name)
            .ToArray();

        for (var index = 0; index < sortedCategories.Length; index++)
        {
            var category = sortedCategories[index];
            var currentIndex = BrowseCategories.IndexOf(category);
            if (currentIndex >= 0 && currentIndex != index)
            {
                BrowseCategories.Move(currentIndex, index);
            }
        }
    }

    internal static IOrderedEnumerable<T> OrderBrowseCategories<T>(IEnumerable<T> categories,
        Func<T, int?> viewerCount, Func<T, string> name) => categories
        .OrderBy(category => viewerCount(category) is null ? 1 : 0)
        .ThenByDescending(category => viewerCount(category) ?? 0)
        .ThenBy(name, StringComparer.OrdinalIgnoreCase);

    internal void StartBrowseCategoryViewerCountLoad(PlatformKind platform, string query)
    {
        if (disposed || browseService is null || platform != PlatformKind.Twitch)
        {
            return;
        }

        var categoryIds = BrowseCategories
            .Where(category => category.Platform == platform && category.Category.ViewerCount is null)
            .Select(category => category.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (categoryIds.Length == 0)
        {
            return;
        }

        int viewerCountGeneration;
        CancellationTokenSource cancellation;
        lock (browseCategoryViewerCountGate)
        {
            if (disposed) return;
            if (browseCategoryViewerCountCancellation is not null)
            {
                browseCategoryViewerCountLoadPending = true;
                return;
            }

            viewerCountGeneration = vodBrowseController.AdvanceBrowseCategoryViewerCountGeneration();
            cancellation = new CancellationTokenSource();
            browseCategoryViewerCountCancellation = cancellation;
        }

        _ = Track(LoadBrowseCategoryViewerCountsAsync(
            platform,
            query,
            viewerCountGeneration,
            categoryIds,
            cancellation));
    }

    internal async Task LoadBrowseCategoryViewerCountsAsync(
        PlatformKind platform,
        string query,
        int viewerCountGeneration,
        IReadOnlyList<string> categoryIds,
        CancellationTokenSource cancellation)
    {
        var failureReported = 0;
        using var throttle = new SemaphoreSlim(BrowseCategoryViewerCountConcurrency);

        try
        {
            var batches = new List<IReadOnlyList<string>>();
            var remainingCategoryIds = categoryIds;
            if (ShouldPrioritizeFirstBrowseCategoryViewerCount(platform, query) &&
                remainingCategoryIds.Count > 1)
            {
                batches.Add([remainingCategoryIds[0]]);
                remainingCategoryIds = remainingCategoryIds.Skip(1).ToArray();
            }

            batches.AddRange(remainingCategoryIds
                .Chunk(BrowseCategoryViewerCountBatchSize)
                .Select(batch => (IReadOnlyList<string>)batch.ToArray()));

            var tasks = batches
                .Select(categoryIdBatch => LoadBrowseCategoryViewerCountBatchAsync(
                    platform,
                    query,
                    viewerCountGeneration,
                    categoryIdBatch,
                    throttle,
                    cancellation,
                    () => Interlocked.CompareExchange(ref failureReported, 1, 0) == 0))
                .ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentBrowseCategoryViewerCountLoad(viewerCountGeneration, platform, query))
            {
                return;
            }

            var message = $"{platform} category viewer counts unavailable. {ex.Message}";
            SetBrowseCategoryStatus(message);
            logger.Write(AppLogLevel.Error, "Browse", $"{platform} category viewer counts failed.", ex);
        }
        finally
        {
            // Dispose first and unconditionally: short-circuiting on the cancellation checks
            // leaked one CancellationTokenSource per cancelled load (one per keystroke while a
            // viewer-count load was in flight).
            var hasPendingLoad = DisposeBrowseCategoryViewerCountCancellation(cancellation);
            if (hasPendingLoad &&
                !cancellation.IsCancellationRequested &&
                IsCurrentBrowseCategoryViewerCountLoad(viewerCountGeneration, platform, query))
            {
                dispatch(() => StartBrowseCategoryViewerCountLoad(platform, query));
            }
        }
    }

    internal static bool ShouldPrioritizeFirstBrowseCategoryViewerCount(PlatformKind platform, string query)
    {
        return platform == PlatformKind.Twitch && string.IsNullOrWhiteSpace(query);
    }

    internal async Task LoadBrowseCategoryViewerCountBatchAsync(
        PlatformKind platform,
        string query,
        int viewerCountGeneration,
        IReadOnlyList<string> categoryIds,
        SemaphoreSlim throttle,
        CancellationTokenSource cancellation,
        Func<bool> tryReportFailure)
    {
        await throttle.WaitAsync(cancellation.Token);
        try
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var result = await browseService!.GetCategoryViewerCountsAsync(
                new BrowseCategoryViewerCountRequest(platform, categoryIds),
                Settings,
                cancellation.Token);
            if (!IsCurrentBrowseCategoryViewerCountLoad(viewerCountGeneration, platform, query))
            {
                return;
            }

            if (!result.IsAvailable)
            {
                ReportBrowseCategoryViewerCountFailure(
                    platform,
                    query,
                    viewerCountGeneration,
                    result.Message,
                    cancellation,
                    tryReportFailure,
                    result.Status is BrowseResultStatus.NotConfigured or BrowseResultStatus.Unauthorized);
                return;
            }

            var requestedCategoryIds = categoryIds.ToHashSet(StringComparer.Ordinal);
            var viewerCounts = result.Items
                .Where(count => requestedCategoryIds.Contains(count.CategoryId))
                .ToArray();
            if (viewerCounts.Length > 0)
            {
                dispatch(() =>
                {
                    if (IsCurrentBrowseCategoryViewerCountLoad(viewerCountGeneration, platform, query))
                    {
                        foreach (var viewerCount in viewerCounts)
                        {
                            ApplyBrowseCategoryViewerCount(platform, viewerCount.CategoryId, viewerCount.ViewerCount);
                        }
                        if (!string.IsNullOrWhiteSpace(query)) SortBrowseCategoriesByViewerCount();
                    }
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportBrowseCategoryViewerCountFailure(
                platform,
                query,
                viewerCountGeneration,
                $"{platform} category viewer counts unavailable. {ex.Message}",
                cancellation,
                tryReportFailure,
                cancelRemaining: false);
            logger.Write(AppLogLevel.Error, "Browse", $"{platform} category viewer count failed for {string.Join(", ", categoryIds)}.", ex);
        }
        finally
        {
            throttle.Release();
        }
    }

    internal void ReportBrowseCategoryViewerCountFailure(
        PlatformKind platform,
        string query,
        int viewerCountGeneration,
        string message,
        CancellationTokenSource cancellation,
        Func<bool> tryReportFailure,
        bool cancelRemaining)
    {
        if (!tryReportFailure())
        {
            return;
        }

        if (cancelRemaining)
        {
            CancelOperation(cancellation);
        }

        dispatch(() =>
        {
            if (!IsCurrentBrowseCategoryViewerCountLoad(viewerCountGeneration, platform, query))
            {
                return;
            }

            SetBrowseCategoryStatus(message);
        });
    }

    internal void ApplyBrowseCategoryViewerCount(
        PlatformKind platform,
        string categoryId,
        int viewerCount)
    {
        foreach (var category in BrowseCategories)
        {
            if (category.Platform == platform &&
                string.Equals(category.Id, categoryId, StringComparison.Ordinal))
            {
                category.SetViewerCount(viewerCount);
                return;
            }
        }
    }

    internal void SetBrowseCategoryStatus(string message)
    {
        browseCategoryStatus = message;
        if (!IsBrowseCategoriesPageVisible)
        {
            return;
        }

        BrowseStatus = message;
        StatusMessage = message;
    }

    internal async Task SelectBrowseCategoryAsync(BrowseCategoryViewModel category)
    {
        if (disposed || category.Platform != SelectedBrowsePlatform)
        {
            return;
        }

        vodBrowseController.AdvanceBrowseCategoryGeneration();
        CancelBrowseCategorySearchDebounce();
        CancelActiveBrowseCategorySearch();
        // The canceled generation no longer owns this flag and cannot clear it in
        // its finally block. Returning to categories must be able to load again.
        IsBrowseCategoriesLoading = false;
        CancelActiveBrowseCategoryViewerCountLoad();
        vodBrowseController.AdvanceBrowseStreamGeneration();
        CancelActiveBrowseStreamSearch();
        SelectedBrowseCategory = category;
        SetBrowseStreamsPageSelected(true);
        ClearBrowseStreams(clearSelectedCategory: false);
        HasBrowseStreamSearchCompleted = false;
        BrowseStatus = $"Loading live streams in {category.Name}";
        StatusMessage = BrowseStatus;
        recordNavigation();
        await LoadBrowseStreamsAsync(reset: true);
    }

    internal Task LoadBrowseStreamsAsync(bool reset)
    {
        if (disposed) return Task.CompletedTask;
        if (browseService is null)
        {
            BrowseStatus = "Browse is not available.";
            return Task.CompletedTask;
        }

        var category = SelectedBrowseCategory;
        if (category is null)
        {
            BrowseStatus = "Select a category first.";
            return Task.CompletedTask;
        }

        var platform = SelectedBrowsePlatform;
        var categoryId = category.Id;
        var categoryName = category.Name;
        var cursor = reset ? "" : BrowseStreamNextCursor;
        if (!reset && string.IsNullOrWhiteSpace(cursor))
        {
            return Task.CompletedTask;
        }

        if (reset && activeBrowseStreamTask is { IsCompleted: false } &&
            activeBrowseStreamGeneration == vodBrowseController.CurrentBrowseStreamGeneration &&
            activeBrowseStreamSearch == (platform, categoryId))
        {
            return activeBrowseStreamTask;
        }

        var searchGeneration = reset
            ? vodBrowseController.AdvanceBrowseStreamGeneration()
            : vodBrowseController.CurrentBrowseStreamGeneration;
        activeBrowseStreamGeneration = searchGeneration;
        activeBrowseStreamSearch = (platform, categoryId);
        var task = RunBrowseStreamsAsync(reset,
            new BrowseStreamRequest(platform, categoryId, categoryName, cursor, BrowseStreamPageSize), searchGeneration);
        // A refresh during Load More must restart the first page, not share pagination.
        activeBrowseStreamTask = reset ? task : null;
        return task;
    }

    internal async Task RunBrowseStreamsAsync(bool reset, BrowseStreamRequest request, int searchGeneration)
    {
        var platform = request.Platform;
        var categoryId = request.CategoryId;
        var categoryName = request.CategoryName;
        var searchCancellation = ReplaceBrowseStreamSearchCancellation();
        if (reset)
        {
            // Keep usable cards and the last successful cursor until the refresh succeeds.
            // Selecting a different category already clears them at the navigation boundary.
            HasBrowseStreamSearchCompleted = false;
        }

        IsBrowseStreamsLoading = true;
        BrowseStatus = reset
            ? $"Loading live streams in {categoryName}"
            : $"Loading more live streams in {categoryName}";
        StatusMessage = BrowseStatus;

        try
        {
            var result = await browseService!.GetStreamsAsync(
                request,
                Settings,
                searchCancellation.Token);
            if (!IsCurrentBrowseStreamSearch(searchGeneration, platform, categoryId))
            {
                return;
            }

            if (result.IsAvailable)
            {
                var thumbnailCacheVersion = LiveThumbnailCacheVersion.Next();
                BrowseStreamNextCursor = browseStreamPages.ApplyPage(
                    BrowseStreams, result.Items.Select(LiveStreamCardData.FromBrowseStream),
                    card => card.Target.StateKey, data => data.Target.StateKey,
                    data => new LiveStreamCardViewModel(data, openStream, thumbnailCacheVersion),
                    (card, data) => card.Update(data, thumbnailCacheVersion),
                    request.Cursor, result.NextCursor);
            }
            HasBrowseStreamSearchCompleted = true;
            BrowseStatus = result.Message;
            StatusMessage = result.Message;
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentBrowseStreamSearch(searchGeneration, platform, categoryId))
            {
                return;
            }

            HasBrowseStreamSearchCompleted = true;
            BrowseStatus = ex.Message;
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Browse", $"{platform} category stream browse failed.", ex);
        }
        finally
        {
            if (IsCurrentBrowseStreamSearch(searchGeneration, platform, categoryId))
            {
                IsBrowseStreamsLoading = false;
            }

            DisposeBrowseStreamSearchCancellation(searchCancellation);
        }
    }

    internal bool IsCurrentBrowseCategorySearch(int searchGeneration, PlatformKind platform, string query)
    {
        return !disposed &&
            vodBrowseController.IsCurrentBrowseCategoryGeneration(searchGeneration) &&
            SelectedBrowsePlatform == platform &&
            string.Equals(BrowseCategorySearchText.Trim(), query, StringComparison.Ordinal);
    }

    internal bool IsCurrentBrowseCategoryViewerCountLoad(
        int viewerCountGeneration,
        PlatformKind platform,
        string query)
    {
        return !disposed &&
            vodBrowseController.IsCurrentBrowseCategoryViewerCountGeneration(viewerCountGeneration) &&
            SelectedBrowsePlatform == platform &&
            string.Equals(BrowseCategorySearchText.Trim(), query, StringComparison.Ordinal);
    }

    internal bool IsCurrentBrowseStreamSearch(int searchGeneration, PlatformKind platform, string categoryId)
    {
        return !disposed &&
            vodBrowseController.IsCurrentBrowseStreamGeneration(searchGeneration) &&
            SelectedBrowsePlatform == platform &&
            SelectedBrowseCategory is { } selectedCategory &&
            string.Equals(selectedCategory.Id, categoryId, StringComparison.OrdinalIgnoreCase);
    }

    internal void ClearBrowseCategories(bool clearStatus)
    {
        BrowseCategories.Clear();
        BrowseCategoryNextCursor = "";
        if (clearStatus)
        {
            BrowseStatus = "Browse Twitch or Kick categories.";
            browseCategoryStatus = BrowseStatus;
        }
    }

    internal void ClearBrowseStreams(bool clearSelectedCategory)
    {
        BrowseStreams.Clear();
        BrowseStreamNextCursor = "";
        HasBrowseStreamSearchCompleted = false;
        if (clearSelectedCategory)
        {
            SelectedBrowseCategory = null;
        }
    }

    internal void ScheduleAutomaticBrowseCategorySearch()
    {
        if (disposed || browseService is null)
        {
            return;
        }

        var query = BrowseCategorySearchText.Trim();
        var platform = SelectedBrowsePlatform;
        var searchGeneration = vodBrowseController.CurrentBrowseCategoryGeneration;
        if (browseCategorySearchDebounceInterval <= TimeSpan.Zero)
        {
            dispatch(() => _ = RunAutomaticBrowseCategorySearchAsync(query, platform, searchGeneration));
            return;
        }

        vodBrowseController.ScheduleBrowseCategory(
            browseCategorySearchDebounceInterval,
            () => dispatch(() => _ = RunAutomaticBrowseCategorySearchAsync(query, platform, searchGeneration)),
            ReportDebouncedCallbackFailure);
    }

    internal async Task RunAutomaticBrowseCategorySearchAsync(
        string query,
        PlatformKind platform,
        int searchGeneration)
    {
        if (disposed || !IsCurrentBrowseCategorySearch(searchGeneration, platform, query))
        {
            return;
        }

        await LoadBrowseCategoriesAsync(reset: true);
    }

    internal void CancelBrowseCategorySearchDebounce()
    {
        vodBrowseController.CancelScheduledBrowseCategory();
    }

    internal CancellationTokenSource ReplaceBrowseCategorySearchCancellation()
    {
        return vodBrowseController.BeginBrowseCategoryOperation(lifetimeCancellation.Token);
    }

    internal CancellationTokenSource ReplaceBrowseStreamSearchCancellation()
    {
        return vodBrowseController.BeginBrowseStreamOperation(lifetimeCancellation.Token);
    }

    internal void CancelActiveBrowseCategorySearch()
    {
        vodBrowseController.CancelBrowseCategoryOperation();
    }

    internal void CancelActiveBrowseCategoryViewerCountLoad()
    {
        vodBrowseController.AdvanceBrowseCategoryViewerCountGeneration();
        CancellationTokenSource? cancellation;
        lock (browseCategoryViewerCountGate)
        {
            cancellation = browseCategoryViewerCountCancellation;
            browseCategoryViewerCountCancellation = null;
            browseCategoryViewerCountLoadPending = false;
        }

        CancelOperation(cancellation);
    }

    internal void CancelActiveBrowseStreamSearch()
    {
        vodBrowseController.CancelBrowseStreamOperation();
    }

    internal void DisposeBrowseCategorySearchCancellation(CancellationTokenSource cancellation)
    {
        vodBrowseController.CompleteBrowseCategoryOperation(cancellation);
    }

    internal bool DisposeBrowseCategoryViewerCountCancellation(CancellationTokenSource cancellation)
    {
        var shouldStartPendingLoad = false;
        lock (browseCategoryViewerCountGate)
        {
            if (ReferenceEquals(browseCategoryViewerCountCancellation, cancellation))
            {
                browseCategoryViewerCountCancellation = null;
                shouldStartPendingLoad = browseCategoryViewerCountLoadPending;
                browseCategoryViewerCountLoadPending = false;
            }
        }

        cancellation.Dispose();
        return shouldStartPendingLoad;
    }

    internal void DisposeBrowseStreamSearchCancellation(CancellationTokenSource cancellation)
    {
        vodBrowseController.CompleteBrowseStreamOperation(cancellation);
    }

    internal void RaiseBrowseCommandStates()
    {
        ReturnToBrowseCategoriesCommand.RaiseCanExecuteChanged();
        RefreshBrowseCommand.RaiseCanExecuteChanged();
        LoadMoreBrowseCategoriesCommand.RaiseCanExecuteChanged();
        LoadMoreBrowseStreamsCommand.RaiseCanExecuteChanged();
    }

    internal void BrowseCategoriesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasBrowseCategories));
        OnPropertyChanged(nameof(IsBrowseCategoriesEmptyVisible));
        OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreVisible));
        OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreIndicatorVisible));
        OnPropertyChanged(nameof(BrowseCategoriesTitle));
        RaiseBrowseCommandStates();
    }

    internal void BrowseStreamsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasBrowseStreams));
        OnPropertyChanged(nameof(IsBrowseStreamsEmptyVisible));
        OnPropertyChanged(nameof(IsBrowseStreamLoadMoreVisible));
        OnPropertyChanged(nameof(BrowseStreamsTitle));
        RaiseBrowseCommandStates();
    }
}
