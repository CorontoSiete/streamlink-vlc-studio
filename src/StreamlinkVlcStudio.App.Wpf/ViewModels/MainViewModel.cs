using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using StreamlinkVlcStudio.App.Wpf.Notifications;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Vlc;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public enum SettingsCategory
{
    General,
    Playback,
    Accounts,
    Chat,
    Hotkeys,
    Advanced
}

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan DetachedDisposalWaitTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultRecentThumbnailRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultFollowedChannelsRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultStreamSearchDebounceInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultTwitchVodSearchDebounceInterval = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan DefaultBrowseCategorySearchDebounceInterval = TimeSpan.FromMilliseconds(450);
    private const int BrowseCategoryPageSize = 10;
    private const int BrowseCategoryViewerCountBatchSize = 1;
    private const int BrowseCategoryViewerCountConcurrency = 4;
    private const int BrowseStreamPageSize = 50;
    private const int StreamSearchViewerCountConcurrency = 4;
    private const int RecentMetadataConcurrency = 4;
    private const int VlcPluginMultiViewChatDisableThreshold = 3;
    private const int DenseMultiStreamStartupThreshold = 4;
    private const int MaxConcurrentTabStarts = 2;
    private static long nextLiveThumbnailCacheVersion;
    private readonly ISettingsService settingsService;
    private readonly IStreamlinkService streamlinkService;
    private readonly IPlaybackEngineFactory playbackFactory;
    private readonly IChatClientFactory chatFactory;
    private readonly IViewerCountService? viewerCountService;
    private readonly IReplayResolver? replayResolver;
    private readonly IVodChatProvider? vodChatProvider;
    private readonly IVodPlaybackHistory? vodPlaybackHistory;
    private readonly IFollowedStreamsService? followedStreamsService;
    private readonly IKickFollowedChannelsImporter? kickFollowedChannelsImporter;
    private string kickFollowImportStatus = "";
    private int kickFollowImportBusy;
    private readonly ILiveNotificationService? liveNotificationService;
    private FollowedChannelsSettings? observedFollowedChannelsSettings;
    private HashSet<string>? previousLiveFollowedKeys;
    private readonly HashSet<PlatformKind> baselinedLivePlatforms = [];
    private readonly IStreamMetadataService? streamMetadataService;
    private readonly IStreamSearchService? streamSearchService;
    private readonly ITwitchVodService? twitchVodService;
    private readonly IKickVodService? kickVodService;
    private readonly ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver;
    private readonly ITwitchClipService? twitchClipService;
    private readonly IAppUpdateService? appUpdateService;
    private readonly IBrowseService? browseService;
    private readonly TimeSpan recentThumbnailRefreshInterval;
    private readonly TimeSpan followedChannelsRefreshInterval;
    private readonly TimeSpan streamSearchDebounceInterval;
    private readonly TimeSpan twitchVodSearchDebounceInterval;
    private readonly TimeSpan browseCategorySearchDebounceInterval;
    private readonly IAppLogger logger;
    private readonly Action<Action> dispatch;
    private readonly Func<Action, bool>? tryDispatch;
    private readonly Action<Uri> openBrowser;
    private readonly Action? requestShutdown;
    private EventHandler<LogEntry>? loggerEntryWrittenHandler;
    private readonly AppLogBuffer appLogBuffer;
    private readonly object disposalGate = new();
    private readonly object detachedDisposalsGate = new();
    private readonly object recentThumbnailRefreshTimerGate = new();
    private readonly object followedChannelsRefreshTimerGate = new();
    private readonly object followedChannelsRefreshTaskGate = new();
    private readonly object browseCategoryViewerCountGate = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly StreamSearchController streamSearchController = new();
    private readonly VodBrowseController vodBrowseController = new();
    private readonly PagedResultTracker vodPages = new();
    private readonly PagedResultTracker browseCategoryPages = new();
    private readonly PagedResultTracker browseStreamPages = new();
    private readonly SemaphoreSlim streamOpenGate = new(1, 1);
    private readonly TabStartController tabStartController = new(MaxConcurrentTabStarts);
    private readonly SemaphoreSlim chatSettingsApplyGate = new(1, 1);
    private readonly SemaphoreSlim vlcPluginMultiViewChatPolicyGate = new(1, 1);
    private readonly SemaphoreSlim recentStreamsGate = new(1, 1);
    private readonly SemaphoreSlim recentThumbnailRefreshGate = new(1, 1);
    private readonly SemaphoreSlim followedChannelsRefreshGate = new(1, 1);
    private readonly CancellationTokenSource recentThumbnailRefreshCancellation = new();
    private readonly CancellationTokenSource followedChannelsRefreshCancellation = new();
    private readonly List<Task> detachedDisposals = [];
    private readonly BackgroundOperationController backgroundOperationController;
    private readonly TabGroupingController tabGroupingController = new();
    private readonly RecentStreamController recentStreamController = new();
    private Dictionary<string, StreamMetadataResult>? pendingRecentMetadata;
    private readonly HashSet<StreamTabViewModel> vlcPluginMultiViewChatPolicyHiddenTabs = [];
    private readonly List<NavigationDestination> navigationHistory = [];
    // Stream opening can hide Home before selecting its tab. Keep the last completed
    // destination so that this intermediate loading state does not replace history.
    private NavigationDestination currentNavigationDestination = new(NavigationPage.Followed);
    private bool restoringNavigation;
    private readonly TabPlaybackPolicyController inactivePlaybackPolicyController;
    private ChatSettings? observedChatSettings;
    private Task? disposalTask;
    private StreamTabViewModel? selectedTab;
    // PiP can own audio when there is no selected stream in the main window.
    private StreamTabViewModel? audioActiveTab;
    private TabStripItemViewModel? selectedTabStripItem;
    private string newStreamText = "";
    private string streamSearchStatus = "";
    private string selectedQuality;
    private string statusMessage = "Ready";
    private string followedChannelsStatus = "Live followed channels are not loaded";
    private string twitchVodSearchText = "";
    private string twitchVodStatus = "Search a Twitch streamer to browse VODs.";
    private string browseCategorySearchText = "";
    private string browseStatus = "Browse Twitch or Kick categories.";
    private string browseCategoryStatus = "Browse Twitch or Kick categories.";
    private string appUpdateStatus = "Updates are checked automatically. Automatic downloads are optional; you choose when to restart and install.";
    private Version? canceledAutomaticDownloadVersion;
    private string appUpdateActionText = "Check for updates";
    private bool isUpdateBannerVisible;
    private Task? automaticUpdateTask;
    private CancellationTokenSource? updateDownloadCancellation;
    private bool updateDownloadCanceledByUser;
    private int updateActionInProgress;
    private string browseCategoryNextCursor = "";
    private string browseStreamNextCursor = "";
    private string kickFollowedChannelsText;
    private DateTimeOffset? followedChannelsLastUpdatedAt;
    private bool isHomeSelected = true;
    private bool isRecentHomePageSelected;
    private bool isTwitchVodsHomePageSelected;
    private bool isBrowseHomePageSelected;
    private bool isBrowseStreamsPageSelected;
    private bool isSettingsOpen;
    private SettingsCategory selectedSettingsCategory = SettingsCategory.General;
    private bool isStreamSearchRunning;
    private bool hasStreamSearchCompleted;
    private Task? activeStreamSearchTask;
    private int activeStreamSearchGeneration;
    private string activeStreamSearchQuality = "";
    private bool isStreamSearchDropdownOpen;
    private bool isFollowedChannelsRefreshing;
    private Task? activeFollowedChannelsRefreshTask;
    private string activeFollowedChannelsRefreshKey = "";
    private int followedChannelsRefreshGeneration;
    private bool isTwitchVodSearchRunning;
    private Task? activeTwitchVodSearchTask;
    private int activeTwitchVodSearchGeneration;
    private (PlatformKind Platform, string Query, TwitchVodTypeFilter Type) activeTwitchVodSearch;
    private bool hasTwitchVodSearchCompleted;
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
    private bool isReplaySeekBarUiVisible = true;
    private bool isStreamOnlyFullscreenActive;
    private bool isVideoFullscreenActive;
    private bool suppressInactiveTabPause;
    private bool applyingSelectedTabSelection;
    private bool disposed;
    private int followedChannelsAutomaticRefreshActive;
    private int inactivePlaybackPolicyApplyPassCount;
    private System.Threading.Timer? recentThumbnailRefreshTimer;
    private System.Threading.Timer? followedChannelsRefreshTimer;
    private CancellationTokenSource? browseCategoryViewerCountCancellation;
    private bool browseCategoryViewerCountLoadPending;
    private TwitchVodTypeFilter selectedTwitchVodType = TwitchVodTypeFilter.Archive;
    private PlatformKind selectedVodPlatform = PlatformKind.Twitch;
    private PlatformKind selectedBrowsePlatform = PlatformKind.Twitch;
    private BrowseCategoryViewModel? selectedBrowseCategory;
    private string twitchVodNextCursor = "";
    private int videoGridRows = VideoGridLayoutCalculator.BaseGridSize;
    private int videoGridColumns = VideoGridLayoutCalculator.BaseGridSize;

    internal MainViewModel(MainViewModelDependencies dependencies)
    {
        var settings = dependencies.Settings;
        var settingsService = dependencies.SettingsService;
        var streamlinkService = dependencies.StreamlinkService;
        var playbackFactory = dependencies.PlaybackFactory;
        var chatFactory = dependencies.ChatFactory;
        var logger = dependencies.Logger;
        var dispatch = dependencies.Dispatch;
        var viewerCountService = dependencies.ViewerCountService;
        var followedStreamsService = dependencies.FollowedStreamsService;
        var streamMetadataService = dependencies.StreamMetadataService;
        var replayResolver = dependencies.ReplayResolver;
        var vodChatProvider = dependencies.VodChatProvider;
        var liveNotificationService = dependencies.LiveNotificationService;
        var streamSearchService = dependencies.StreamSearchService;
        var twitchVodService = dependencies.TwitchVodService;
        var kickVodService = dependencies.KickVodService;
        var twitchSubOnlyVodResolver = dependencies.TwitchSubOnlyVodResolver;
        var twitchClipService = dependencies.TwitchClipService;
        var appUpdateService = dependencies.AppUpdateService;
        var browseService = dependencies.BrowseService;
        var recentThumbnailRefreshInterval = dependencies.RecentThumbnailRefreshInterval;
        var followedChannelsRefreshInterval = dependencies.FollowedChannelsRefreshInterval;
        var streamSearchDebounceInterval = dependencies.StreamSearchDebounceInterval;
        var twitchVodSearchDebounceInterval = dependencies.TwitchVodSearchDebounceInterval;
        var browseCategorySearchDebounceInterval = dependencies.BrowseCategorySearchDebounceInterval;
        var openBrowser = dependencies.OpenBrowser;
        var requestShutdown = dependencies.RequestShutdown;
        var tryDispatch = dependencies.TryDispatch;

        Settings = settings;
        this.settingsService = settingsService;
        this.streamlinkService = streamlinkService;
        this.playbackFactory = playbackFactory;
        this.chatFactory = chatFactory;
        this.viewerCountService = viewerCountService;
        this.replayResolver = replayResolver;
        this.vodChatProvider = vodChatProvider;
        vodPlaybackHistory = dependencies.VodPlaybackHistory;
        this.followedStreamsService = followedStreamsService;
        kickFollowedChannelsImporter = dependencies.KickFollowedChannelsImporter;
        this.liveNotificationService = liveNotificationService;
        this.streamMetadataService = streamMetadataService;
        this.streamSearchService = streamSearchService;
        this.twitchVodService = twitchVodService;
        this.kickVodService = kickVodService;
        this.twitchSubOnlyVodResolver = twitchSubOnlyVodResolver;
        this.twitchClipService = twitchClipService;
        this.appUpdateService = appUpdateService;
        if (appUpdateService is not null)
        {
            appUpdateService.StateChanged += OnAppUpdateStateChanged;
        }
        this.browseService = browseService;
        this.recentThumbnailRefreshInterval = recentThumbnailRefreshInterval ?? DefaultRecentThumbnailRefreshInterval;
        this.followedChannelsRefreshInterval = followedChannelsRefreshInterval ?? DefaultFollowedChannelsRefreshInterval;
        this.streamSearchDebounceInterval = streamSearchDebounceInterval ?? DefaultStreamSearchDebounceInterval;
        this.twitchVodSearchDebounceInterval = twitchVodSearchDebounceInterval ?? DefaultTwitchVodSearchDebounceInterval;
        this.browseCategorySearchDebounceInterval = browseCategorySearchDebounceInterval ?? DefaultBrowseCategorySearchDebounceInterval;
        this.logger = logger;
        backgroundOperationController = new BackgroundOperationController(logger);
        this.dispatch = dispatch;
        this.tryDispatch = tryDispatch;
        appLogBuffer = new AppLogBuffer(AppLogLines, dispatch, tryDispatch);
        this.openBrowser = openBrowser ?? OpenExternalBrowser;
        this.requestShutdown = requestShutdown;
        inactivePlaybackPolicyController = new TabPlaybackPolicyController(
            this.dispatch,
            () => disposed,
            ApplyInactivePlaybackPolicyPassAsync,
            backgroundOperationController.Track,
            exception => this.logger.Write(
                AppLogLevel.Warning,
                "UI",
                "Failed to apply playback visibility policy.",
                exception));
        selectedQuality = settings.DefaultQuality;
        kickFollowedChannelsText = FormatKickFollowedChannelsText(settings.FollowedChannels.KickChannelSlugs);

        AddAndPlayCommand = CreateCommand(AddAndPlayAsync, () => HasNewStreamSearchText);
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack);
        SelectHomeCommand = new RelayCommand(SelectHome);
        ShowFollowedHomePageCommand = new RelayCommand(ShowFollowedHomePage);
        ShowTwitchVodsHomePageCommand = new RelayCommand(ShowTwitchVodsHomePage);
        ShowRecentHomePageCommand = new RelayCommand(ShowRecentHomePage);
        ShowBrowseHomePageCommand = new RelayCommand(ShowBrowseHomePage);
        ReturnToBrowseCategoriesCommand = new RelayCommand(ReturnToBrowseCategoriesPage, () => IsBrowseStreamsPageVisible);
        RefreshFollowedChannelsCommand = CreateCommand(RefreshFollowedChannelsAsync, () => followedStreamsService is not null);
        ImportKickFollowsCommand = CreateCommand(ImportKickFollowsAsync,
            () => kickFollowedChannelsImporter is not null && Volatile.Read(ref kickFollowImportBusy) == 0);
        ClearImportedKickFollowsCommand = CreateCommand(ClearImportedKickFollowsAsync,
            () => Volatile.Read(ref kickFollowImportBusy) == 0);
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
        SelectTwitchBrowsePlatformCommand = new RelayCommand(() => SelectBrowsePlatform(PlatformKind.Twitch));
        SelectKickBrowsePlatformCommand = new RelayCommand(() => SelectBrowsePlatform(PlatformKind.Kick));
        RefreshBrowseCommand = CreateCommand(RefreshBrowseAsync, () => browseService is not null);
        LoadMoreBrowseCategoriesCommand = CreateCommand(
            () => LoadBrowseCategoriesAsync(reset: false),
            () => browseService is not null && CanLoadMoreBrowseCategories);
        LoadMoreBrowseStreamsCommand = CreateCommand(
            () => LoadBrowseStreamsAsync(reset: false),
            () => browseService is not null && CanLoadMoreBrowseStreams);
        PlaySelectedCommand = CreateCommand(() => StartSelectedTabAsync("Starting"), () => SelectedTab is not null);
        ReloadSelectedCommand = CreateCommand(() => StartSelectedTabAsync("Reloading"), () => SelectedTab is not null);
        StopSelectedCommand = CreateCommand(StopSelectedAsync, () => SelectedTab is not null);
        PauseSelectedCommand = CreateCommand(PauseSelectedAsync, () => SelectedTab is not null);
        CloseSelectedCommand = CreateCommand(CloseSelectedAsync, () => SelectedTab is not null);
        CreateClipCommand = CreateCommand(CreateClipAsync, CanCreateClip);
        SaveSettingsCommand = CreateCommand(SaveSettingsAsync);
        AuthorizeTwitchCommand = CreateCommand(AuthorizeTwitchAsync);
        ClearTwitchTokenCommand = CreateCommand(ClearTwitchTokenAsync, HasTwitchToken);
        AuthorizeKickCommand = CreateCommand(AuthorizeKickAsync);
        ClearKickTokenCommand = CreateCommand(ClearKickTokenAsync, HasKickToken);
        UpdateAppCommand = CreateCommand(() => UpdateAppAsync(checkOnly: false), CanRunUpdateAction);
        CheckForUpdatesCommand = CreateCommand(() => UpdateAppAsync(checkOnly: true), CanRunUpdateAction);
        CancelUpdateCommand = new RelayCommand(CancelUpdateDownload, () => updateDownloadCancellation is not null);
        LaterUpdateCommand = CreateCommand(SnoozeUpdateAsync, () => Volatile.Read(ref updateActionInProgress) == 0 && appUpdateService?.State is
        { Release: not null, Phase: AppUpdatePhase.Available or AppUpdatePhase.Ready or AppUpdatePhase.NotifyOnly or AppUpdatePhase.DownloadFailed });
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        ShowGeneralSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.General);
        ShowPlaybackSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Playback);
        ShowAccountsSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Accounts);
        ShowChatSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Chat);
        ShowHotkeysSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Hotkeys);
        ShowAdvancedSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Advanced);
        ToggleMultiStreamCommand = new RelayCommand(ToggleMultiStream);
        ToggleReplaySeekBarCommand = new RelayCommand(ToggleReplaySeekBar);
        ToggleChatCommand = CreateCommand(ToggleChatAsync, () => SelectedTab is not null);
        MoveTabLeftCommand = new RelayCommand(MoveTabLeft, () => SelectedTab is not null && Tabs.IndexOf(SelectedTab) > 0);
        MoveTabRightCommand = new RelayCommand(MoveTabRight, () => SelectedTab is not null && Tabs.IndexOf(SelectedTab) < Tabs.Count - 1);
        RebuildRecentStreams();
        Tabs.CollectionChanged += TabsOnCollectionChanged;
        StreamSearchResults.CollectionChanged += StreamSearchResultsOnCollectionChanged;
        LiveFollowedChannels.CollectionChanged += LiveFollowedChannelsOnCollectionChanged;
        TwitchVods.CollectionChanged += TwitchVodsOnCollectionChanged;
        if (vodPlaybackHistory is not null) vodPlaybackHistory.BookmarkChanged += OnVodBookmarkChanged;
        RecentStreams.CollectionChanged += RecentStreamsOnCollectionChanged;
        BrowseCategories.CollectionChanged += BrowseCategoriesOnCollectionChanged;
        BrowseStreams.CollectionChanged += BrowseStreamsOnCollectionChanged;
        Settings.PropertyChanged += SettingsOnPropertyChanged;
        ObserveChatSettings(Settings.Chat);
        ObserveFollowedChannelsSettings(Settings.FollowedChannels);
    }

    public AppSettings Settings { get; }
    public ObservableCollection<StreamTabViewModel> Tabs { get; } = [];
    public ObservableCollection<TabStripItemViewModel> TabStripItems { get; } = [];
    public ObservableCollection<StreamTabViewModel> VideoTabs { get; } = [];
    public ObservableCollection<StreamSearchResultViewModel> StreamSearchResults { get; } = [];
    public ObservableCollection<LiveStreamCardViewModel> LiveFollowedChannels { get; } = [];
    public ObservableCollection<VodViewModel> TwitchVods { get; } = [];
    public ObservableCollection<RecentStreamViewModel> RecentStreams { get; } = [];
    public ObservableCollection<BrowseCategoryViewModel> BrowseCategories { get; } = [];
    public ObservableCollection<LiveStreamCardViewModel> BrowseStreams { get; } = [];
    public IReadOnlyList<ChatLayout> ChatLayoutOptions { get; } = Enum.GetValues<ChatLayout>();
    public IReadOnlyList<QualityOption> QualityOptions { get; } = QualityOption.Defaults;
    public IReadOnlyList<VideoRendererModeOption> VideoRendererOptions { get; } = VideoRendererModeOption.All;
    public IReadOnlyList<WindowCloseBehaviorOption> CloseBehaviorOptions { get; } = WindowCloseBehaviorOption.All;
    public IReadOnlyList<AppThemeOption> ThemeOptions { get; } = AppThemeOption.All;
    public ObservableCollection<string> AppLogLines { get; } = [];

    public AsyncRelayCommand AddAndPlayCommand { get; }
    public RelayCommand GoBackCommand { get; }
    public RelayCommand SelectHomeCommand { get; }
    public RelayCommand ShowFollowedHomePageCommand { get; }
    public RelayCommand ShowTwitchVodsHomePageCommand { get; }
    public RelayCommand ShowRecentHomePageCommand { get; }
    public RelayCommand ShowBrowseHomePageCommand { get; }
    public RelayCommand ReturnToBrowseCategoriesCommand { get; }
    public AsyncRelayCommand RefreshFollowedChannelsCommand { get; }
    public AsyncRelayCommand ImportKickFollowsCommand { get; }
    public AsyncRelayCommand ClearImportedKickFollowsCommand { get; }
    public AsyncRelayCommand SearchTwitchVodsCommand { get; }
    public AsyncRelayCommand LoadMoreTwitchVodsCommand { get; }
    public RelayCommand SelectTwitchVodPlatformCommand { get; }
    public RelayCommand SelectKickVodPlatformCommand { get; }
    public RelayCommand ShowPastBroadcastsVodFilterCommand { get; }
    public RelayCommand ShowHighlightsVodFilterCommand { get; }
    public RelayCommand ShowUploadsVodFilterCommand { get; }
    public RelayCommand ShowAllVodFilterCommand { get; }
    public RelayCommand SelectTwitchBrowsePlatformCommand { get; }
    public RelayCommand SelectKickBrowsePlatformCommand { get; }
    public AsyncRelayCommand RefreshBrowseCommand { get; }
    public AsyncRelayCommand LoadMoreBrowseCategoriesCommand { get; }
    public AsyncRelayCommand LoadMoreBrowseStreamsCommand { get; }
    public AsyncRelayCommand PlaySelectedCommand { get; }
    public AsyncRelayCommand ReloadSelectedCommand { get; }
    public AsyncRelayCommand StopSelectedCommand { get; }
    public AsyncRelayCommand PauseSelectedCommand { get; }
    public AsyncRelayCommand CloseSelectedCommand { get; }
    public AsyncRelayCommand CreateClipCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand AuthorizeTwitchCommand { get; }
    public AsyncRelayCommand ClearTwitchTokenCommand { get; }
    public AsyncRelayCommand AuthorizeKickCommand { get; }
    public AsyncRelayCommand ClearKickTokenCommand { get; }
    public AsyncRelayCommand UpdateAppCommand { get; }
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand LaterUpdateCommand { get; }
    public RelayCommand CancelUpdateCommand { get; }
    public bool CanCancelUpdate => updateDownloadCancellation is not null;
    public bool IsUpdateRefreshVisible => appUpdateService?.State.Phase is
        AppUpdatePhase.Available or AppUpdatePhase.DownloadFailed or AppUpdatePhase.Ready or AppUpdatePhase.NotifyOnly;
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ShowGeneralSettingsCommand { get; }
    public RelayCommand ShowPlaybackSettingsCommand { get; }
    public RelayCommand ShowAccountsSettingsCommand { get; }
    public RelayCommand ShowChatSettingsCommand { get; }
    public RelayCommand ShowHotkeysSettingsCommand { get; }
    public RelayCommand ShowAdvancedSettingsCommand { get; }
    public RelayCommand ToggleMultiStreamCommand { get; }
    public RelayCommand ToggleReplaySeekBarCommand { get; }
    public AsyncRelayCommand ToggleChatCommand { get; }
    public RelayCommand MoveTabLeftCommand { get; }
    public RelayCommand MoveTabRightCommand { get; }

    internal int InactivePlaybackPolicyApplyPassCount => Volatile.Read(ref inactivePlaybackPolicyApplyPassCount);
    internal Task InactivePlaybackPolicyIdleTask => inactivePlaybackPolicyController.IdleTask;

    public StreamTabViewModel? SelectedTab
    {
        get => selectedTab;
        set
        {
            if (selectedTab == value)
            {
                ActivateMainWindowAudio();
                ApplyVideoLayout();
                return;
            }

            var previous = selectedTab;
            if (previous is not null)
            {
                previous.PropertyChanged -= SelectedTabOnPropertyChanged;
            }

            selectedTab = value;
            SetAudioActiveTab(selectedTab);

            if (selectedTab is not null)
            {
                IsHomeSelected = false;
                selectedTab.PropertyChanged += SelectedTabOnPropertyChanged;
                SelectedQuality = selectedTab.Quality;
                selectedTab.RefreshChatOverlay(Settings.Chat);
            }
            else
            {
                IsHomeSelected = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedTab));
            OnPropertyChanged(nameof(HasSelectedKickTab));
            OnPropertyChanged(nameof(SelectedKickChatroomId));
            OnPropertyChanged(nameof(SelectedKickBroadcasterUserId));
            OnPropertyChanged(nameof(SelectedVlcOverlayFontSize));
            RaiseChatTextSizeProperties();
            OnPropertyChanged(nameof(IsSelectedTabDetached));
            OnPropertyChanged(nameof(IsReplaySeekBarVisible));
            OnPropertyChanged(nameof(ClipButtonToolTip));
            RaiseChatVisibilityProperties();
            RaiseCommandStates();
            ApplySelectedTabSelection();
            ApplyVideoLayout();
            if (!suppressInactiveTabPause)
            {
                ApplyInactivePlaybackPolicyInBackground();
            }

            RecordNavigation();
        }
    }

    internal void ActivatePictureInPictureTab(StreamTabViewModel tab)
    {
        if (!Tabs.Contains(tab) || !tab.IsDetached)
        {
            return;
        }

        // Focusing PiP must not interrupt the stream still selected in the main
        // window. PiP retains its audio selection behavior when main shows Home
        // or the selected stream is itself detached.
        SetAudioActiveTab(selectedTab is { IsDetached: false } mainTab && Tabs.Contains(mainTab)
            ? mainTab
            : tab);
        ApplySelectedTabSelection();
    }

    internal void ActivateMainWindowAudio()
    {
        SetAudioActiveTab(selectedTab);
        ApplySelectedTabSelection();
    }

    private void SetAudioActiveTab(StreamTabViewModel? tab)
    {
        tab = tab is not null && Tabs.Contains(tab) ? tab : null;
        if (ReferenceEquals(audioActiveTab, tab))
        {
            return;
        }

        var previous = audioActiveTab;
        audioActiveTab = tab;
        ApplyImmediateAudioOwnerState(previous, audioActiveTab);
    }

    public TabStripItemViewModel? SelectedTabStripItem
    {
        get => selectedTabStripItem;
        set
        {
            if (ReferenceEquals(selectedTabStripItem, value))
            {
                return;
            }

            selectedTabStripItem = value;
            OnPropertyChanged();
            if (value is { } item && Tabs.Contains(item.ActiveTab) && !ReferenceEquals(SelectedTab, item.ActiveTab))
            {
                SelectedTab = item.ActiveTab;
            }
        }
    }

    public string NewStreamText
    {
        get => newStreamText;
        set
        {
            if (SetProperty(ref newStreamText, value ?? ""))
            {
                streamSearchController.AdvanceGeneration();
                CancelStreamSearchDebounce();
                CancelActiveStreamSearch();
                IsStreamSearchRunning = false;
                ClearStreamSearchResults();
                OnPropertyChanged(nameof(HasNewStreamSearchText));
                OnPropertyChanged(nameof(IsNewStreamSearchPlaceholderVisible));
                AddAndPlayCommand.RaiseCanExecuteChanged();
                ScheduleAutomaticStreamSearch();
            }
        }
    }

    public bool HasNewStreamSearchText => !string.IsNullOrWhiteSpace(NewStreamText);

    public bool IsNewStreamSearchPlaceholderVisible => !HasNewStreamSearchText;

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
        private set
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
        private set
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
        private set => SetProperty(ref twitchVodStatus, value);
    }

    public bool IsTwitchVodSearchRunning
    {
        get => isTwitchVodSearchRunning;
        private set
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
        private set
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
                RecordNavigation();
            }
        }
    }

    public bool HasBrowseCategorySearchText => !string.IsNullOrWhiteSpace(BrowseCategorySearchText);

    public bool IsBrowseCategorySearchPlaceholderVisible => !HasBrowseCategorySearchText;

    public PlatformKind SelectedBrowsePlatform
    {
        get => selectedBrowsePlatform;
        private set
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
        private set => SetProperty(ref browseStatus, value ?? "");
    }

    public BrowseCategoryViewModel? SelectedBrowseCategory
    {
        get => selectedBrowseCategory;
        private set
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
        private set
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
        private set
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
        private set
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
        private set
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

    public bool HasStreamSearchResults => StreamSearchResults.Count > 0;

    public bool IsStreamSearchPanelVisible => isStreamSearchDropdownOpen && (IsStreamSearchRunning ||
        hasStreamSearchCompleted ||
        HasStreamSearchResults);

    public bool IsStreamSearchResultsVisible => HasStreamSearchResults;

    public bool IsStreamSearchEmptyVisible => hasStreamSearchCompleted &&
        !IsStreamSearchRunning &&
        !HasStreamSearchResults;

    public string StreamSearchResultsTitle => StreamSearchResults.Count switch
    {
        0 => "Search results",
        1 => "1 search result",
        _ => $"{StreamSearchResults.Count} search results"
    };

    public string StreamSearchStatus
    {
        get => streamSearchStatus;
        private set
        {
            if (SetProperty(ref streamSearchStatus, value ?? ""))
            {
                OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
                OnPropertyChanged(nameof(IsStreamSearchEmptyVisible));
            }
        }
    }

    public bool IsStreamSearchRunning
    {
        get => isStreamSearchRunning;
        private set
        {
            if (SetProperty(ref isStreamSearchRunning, value))
            {
                OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
                OnPropertyChanged(nameof(IsStreamSearchEmptyVisible));
            }
        }
    }

    public void ShowStreamSearchDropdown()
    {
        if (HasNewStreamSearchText &&
            (IsStreamSearchRunning || hasStreamSearchCompleted || HasStreamSearchResults))
        {
            SetStreamSearchDropdownOpen(true);
        }
    }

    public void DismissStreamSearchDropdown()
    {
        SetStreamSearchDropdownOpen(false);
    }

    public string SelectedQuality
    {
        get => selectedQuality;
        set
        {
            if (SetProperty(ref selectedQuality, value) && SelectedTab is not null)
            {
                SelectedTab.Quality = value;
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsHomeSelected
    {
        get => isHomeSelected;
        private set
        {
            if (SetProperty(ref isHomeSelected, value))
            {
                OnPropertyChanged(nameof(IsHomeVisible));
            }
        }
    }

    public bool IsHomeVisible => IsHomeSelected && !IsVideoFullscreenActive && !IsStreamOnlyFullscreenActive;

    public bool IsFollowedHomePageSelected => !IsRecentHomePageSelected &&
        !IsTwitchVodsHomePageSelected &&
        !IsBrowseHomePageSelected;

    public bool IsFollowedHomePageVisible => IsFollowedHomePageSelected;

    public bool IsRecentHomePageSelected
    {
        get => isRecentHomePageSelected;
        private set
        {
            if (SetProperty(ref isRecentHomePageSelected, value))
            {
                OnPropertyChanged(nameof(IsFollowedHomePageSelected));
                OnPropertyChanged(nameof(IsFollowedHomePageVisible));
                OnPropertyChanged(nameof(IsRecentHomePageVisible));
                OnPropertyChanged(nameof(IsTwitchVodsHomePageSelected));
                OnPropertyChanged(nameof(IsTwitchVodsHomePageVisible));
                OnPropertyChanged(nameof(IsBrowseHomePageSelected));
                OnPropertyChanged(nameof(IsBrowseHomePageVisible));
            }
        }
    }

    public bool IsRecentHomePageVisible => IsRecentHomePageSelected;

    public bool IsTwitchVodsHomePageSelected
    {
        get => isTwitchVodsHomePageSelected;
        private set
        {
            if (SetProperty(ref isTwitchVodsHomePageSelected, value))
            {
                OnPropertyChanged(nameof(IsFollowedHomePageSelected));
                OnPropertyChanged(nameof(IsFollowedHomePageVisible));
                OnPropertyChanged(nameof(IsRecentHomePageSelected));
                OnPropertyChanged(nameof(IsRecentHomePageVisible));
                OnPropertyChanged(nameof(IsTwitchVodsHomePageVisible));
                OnPropertyChanged(nameof(IsBrowseHomePageSelected));
                OnPropertyChanged(nameof(IsBrowseHomePageVisible));
            }
        }
    }

    public bool IsTwitchVodsHomePageVisible => IsTwitchVodsHomePageSelected;

    public bool IsBrowseHomePageSelected
    {
        get => isBrowseHomePageSelected;
        private set
        {
            if (SetProperty(ref isBrowseHomePageSelected, value))
            {
                OnPropertyChanged(nameof(IsFollowedHomePageSelected));
                OnPropertyChanged(nameof(IsFollowedHomePageVisible));
                OnPropertyChanged(nameof(IsRecentHomePageSelected));
                OnPropertyChanged(nameof(IsRecentHomePageVisible));
                OnPropertyChanged(nameof(IsTwitchVodsHomePageSelected));
                OnPropertyChanged(nameof(IsTwitchVodsHomePageVisible));
                OnPropertyChanged(nameof(IsBrowseHomePageVisible));
                RaiseBrowsePageStateChanged();
            }
        }
    }

    public bool IsBrowseHomePageVisible => IsBrowseHomePageSelected;

    public bool IsBrowseCategoriesPageVisible => IsBrowseHomePageVisible && !isBrowseStreamsPageSelected;

    public bool IsBrowseStreamsPageVisible => IsBrowseHomePageVisible && isBrowseStreamsPageSelected;

    public string FollowedChannelsStatus
    {
        get => followedChannelsStatus;
        private set => SetProperty(ref followedChannelsStatus, value);
    }

    public bool IsFollowedChannelsRefreshing
    {
        get => isFollowedChannelsRefreshing;
        private set
        {
            if (SetProperty(ref isFollowedChannelsRefreshing, value))
            {
                OnPropertyChanged(nameof(IsFollowedChannelsEmptyVisible));
            }
        }
    }

    public bool HasLiveFollowedChannels => LiveFollowedChannels.Count > 0;

    public bool IsFollowedChannelsEmptyVisible => !IsFollowedChannelsRefreshing && LiveFollowedChannels.Count == 0;

    public bool HasRecentStreams => RecentStreams.Count > 0;

    public bool IsRecentStreamsEmptyVisible => RecentStreams.Count == 0;

    public string RecentStreamsStatus => RecentStreams.Count switch
    {
        0 => "No recent streams yet.",
        1 => "1 recent stream.",
        _ => $"{RecentStreams.Count} recent streams."
    };

    private string TwitchVodNextCursor
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

    private string BrowseCategoryNextCursor
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

    private string BrowseStreamNextCursor
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

    public string FollowedChannelsLastUpdatedText => followedChannelsLastUpdatedAt is { } updatedAt
        ? $"Updated {updatedAt.ToLocalTime():g}"
        : "";

    public string KickFollowedChannelsText
    {
        get => kickFollowedChannelsText;
        set => SetProperty(ref kickFollowedChannelsText, value ?? "");
    }

    public string KickFollowImportStatus
    {
        get => kickFollowImportStatus;
        private set => SetProperty(ref kickFollowImportStatus, value);
    }

    public string KickImportedFollowsSummary => Settings.FollowedChannels.KickFollowsImportedAtUtc is { } importedAt
        ? $"{Settings.FollowedChannels.KickImportedChannelSlugs.Count} Kick follows imported • {importedAt.ToLocalTime():g}"
        : "No Kick follows imported yet.";

    public string AppUpdateStatus
    {
        get => appUpdateStatus;
        private set => SetProperty(ref appUpdateStatus, value);
    }

    public string AppUpdateActionText
    {
        get => appUpdateActionText;
        private set => SetProperty(ref appUpdateActionText, value);
    }

    public bool IsUpdateBannerVisible
    {
        get => isUpdateBannerVisible;
        private set => SetProperty(ref isUpdateBannerVisible, value);
    }

    public bool IsSettingsOpen
    {
        get => isSettingsOpen;
        set
        {
            if (SetProperty(ref isSettingsOpen, value))
            {
                OnPropertyChanged(nameof(IsPlaybackWorkspaceVisible));
                RecordNavigation();
            }
        }
    }

    public bool IsPlaybackWorkspaceVisible => !IsSettingsOpen;

    public bool CanGoBack => navigationHistory.Any(destination =>
        IsNavigationDestinationAvailable(destination) && destination != CaptureNavigationDestination());

    public SettingsCategory SelectedSettingsCategory
    {
        get => selectedSettingsCategory;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref selectedSettingsCategory, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsGeneralSettingsSelected));
            OnPropertyChanged(nameof(IsPlaybackSettingsSelected));
            OnPropertyChanged(nameof(IsAccountsSettingsSelected));
            OnPropertyChanged(nameof(IsChatSettingsSelected));
            OnPropertyChanged(nameof(IsHotkeysSettingsSelected));
            OnPropertyChanged(nameof(IsAdvancedSettingsSelected));
        }
    }

    public bool IsGeneralSettingsSelected => SelectedSettingsCategory == SettingsCategory.General;

    public bool IsPlaybackSettingsSelected => SelectedSettingsCategory == SettingsCategory.Playback;

    public bool IsAccountsSettingsSelected => SelectedSettingsCategory == SettingsCategory.Accounts;

    public bool IsChatSettingsSelected => SelectedSettingsCategory == SettingsCategory.Chat;

    public bool IsHotkeysSettingsSelected => SelectedSettingsCategory == SettingsCategory.Hotkeys;

    public bool IsAdvancedSettingsSelected => SelectedSettingsCategory == SettingsCategory.Advanced;

    public bool IsStreamOnlyFullscreenActive
    {
        get => isStreamOnlyFullscreenActive;
        set
        {
            if (SetProperty(ref isStreamOnlyFullscreenActive, value))
            {
                ApplyVideoLayout();
                ApplyInactivePlaybackPolicyInBackground();
                RaiseChatVisibilityProperties();
                OnPropertyChanged(nameof(IsHomeVisible));
            }
        }
    }

    public bool IsVideoFullscreenActive
    {
        get => isVideoFullscreenActive;
        set
        {
            if (SetProperty(ref isVideoFullscreenActive, value))
            {
                RaiseChatVisibilityProperties();
                OnPropertyChanged(nameof(IsHomeVisible));
            }
        }
    }

    public bool IsMultiStreamEnabled
    {
        get => Settings.MultiStreamEnabled;
        set
        {
            if (Settings.MultiStreamEnabled == value)
            {
                return;
            }

            Settings.MultiStreamEnabled = value;
            StatusMessage = value
                ? "Multi-stream grid enabled"
                : "Single-stream view enabled";
        }
    }

    public string MultiStreamToggleToolTip => IsMultiStreamEnabled
        ? "Show selected stream only"
        : "Show up to 16 streams";

    public int VideoGridRows
    {
        get => videoGridRows;
        private set => SetProperty(ref videoGridRows, value);
    }

    public int VideoGridColumns
    {
        get => videoGridColumns;
        private set => SetProperty(ref videoGridColumns, value);
    }

    public bool IsDockedChatVisible => !IsVideoFullscreenActive &&
        !IsStreamOnlyFullscreenActive &&
        SelectedTab is { IsChatVisible: true, IsDockedChatPanelVisible: true } tab &&
        IsDockedChatPanelActive(tab);

    public bool IsSelectedChatShowing => SelectedTab is { IsChatVisible: true } tab &&
        (!IsDockedChatPanelActive(tab) || tab.IsDockedChatPanelVisible);

    public bool IsChatLayoutHidden => Settings.Chat.Layout == ChatLayout.Hidden;

    public bool IsAnyStreamPlaying => Tabs.Any(tab => tab.Status == PlaybackStatus.Playing);

    public bool HasSelectedKickTab => SelectedTab?.Target.Platform == PlatformKind.Kick;

    public bool HasSelectedTab => SelectedTab is not null;

    public string ClipButtonToolTip => SelectedTab?.Target switch
    {
        { Platform: PlatformKind.Kick } => "Kick clipping is disabled",
        { Platform: PlatformKind.Twitch, Kind: StreamTargetKind.Live } => "Create a 30-second Twitch clip",
        { Platform: PlatformKind.Twitch } => "Twitch clips are available for live tabs only",
        _ => "Select a live Twitch tab to create a clip"
    };

    public bool IsSelectedTabDetached => SelectedTab?.IsDetached == true;

    public bool IsReplaySeekBarUiVisible
    {
        get => isReplaySeekBarUiVisible;
        private set
        {
            if (SetProperty(ref isReplaySeekBarUiVisible, value))
            {
                OnPropertyChanged(nameof(IsReplaySeekBarVisible));
                OnPropertyChanged(nameof(ReplaySeekBarToggleToolTip));
            }
        }
    }

    public bool IsReplaySeekBarVisible => IsReplaySeekBarUiVisible &&
        SelectedTab?.IsReplaySeekBarVisible == true;

    public string ReplaySeekBarToggleToolTip => IsReplaySeekBarUiVisible
        ? "Hide replay seekbar"
        : "Show replay seekbar";

    public double ChatTextSize
    {
        get => Settings.Chat.Layout == ChatLayout.Overlay
            ? SelectedVlcOverlayFontSize
            : Settings.Chat.FontSize;
        set
        {
            if (Settings.Chat.Layout == ChatLayout.Overlay)
            {
                SelectedVlcOverlayFontSize = value;
            }
            else if (Settings.Chat.Layout == ChatLayout.Docked)
            {
                Settings.Chat.FontSize = value;
            }
        }
    }

    public bool IsChatTextSizeEnabled => Settings.Chat.Layout != ChatLayout.Hidden;

    public string ChatTextSizeLabel => Settings.Chat.Layout switch
    {
        ChatLayout.Overlay => "Overlay chat text size",
        ChatLayout.Docked => "Docked chat text size",
        _ => "Chat text size"
    };

    public string ChatTextSizeDescription => Settings.Chat.Layout switch
    {
        ChatLayout.Overlay when SelectedTab is { } tab =>
            $"Applies to {tab.Target.Platform}: {tab.Target.Channel}. Save changes to remember this stream's overlay size.",
        ChatLayout.Overlay =>
            "Default for streams without a saved overlay size. Save changes to keep this default.",
        ChatLayout.Docked => "Applies to docked chat for all streams. Save changes to keep this size.",
        _ => "Choose Docked or Overlay to change the text size."
    };

    private void RaiseChatTextSizeProperties()
    {
        OnPropertyChanged(nameof(ChatTextSize));
        OnPropertyChanged(nameof(IsChatTextSizeEnabled));
        OnPropertyChanged(nameof(ChatTextSizeLabel));
        OnPropertyChanged(nameof(ChatTextSizeDescription));
    }

    public double SelectedVlcOverlayFontSize
    {
        get => SelectedTab is { } tab
            ? GetSavedStreamVlcOverlayFontSize(tab.Target)
            : Settings.Chat.VlcOverlayFontSize;
        set
        {
            var normalized = ChatSettings.NormalizeFontSize(value, Settings.Chat.VlcOverlayFontSize);
            if (SelectedTab is not { } tab)
            {
                Settings.Chat.VlcOverlayFontSize = normalized;
                OnPropertyChanged();
                return;
            }

            var current = GetSavedStreamVlcOverlayFontSize(tab.Target);
            if (Math.Abs(current - normalized) < 0.01 &&
                Settings.StreamVlcOverlayFontSizes.ContainsKey(tab.Target.StateKey))
            {
                return;
            }

            Settings.StreamVlcOverlayFontSizes[tab.Target.StateKey] = normalized;
            OnPropertyChanged();
            RaiseChatTextSizeProperties();
            foreach (var matchingTab in Tabs.Where(candidate =>
                string.Equals(candidate.Target.StateKey, tab.Target.StateKey, StringComparison.OrdinalIgnoreCase)))
            {
                matchingTab.RefreshChatOverlay(Settings.Chat);
            }
            RaiseChatVisibilityProperties();
        }
    }

    public string SelectedKickChatroomId
    {
        get => GetSelectedKickSetting(broadcaster: false);
        set => SetSelectedKickSetting(value, nameof(SelectedKickChatroomId), broadcaster: false);
    }

    public string SelectedKickBroadcasterUserId
    {
        get => GetSelectedKickSetting(broadcaster: true);
        set => SetSelectedKickSetting(value, nameof(SelectedKickBroadcasterUserId), broadcaster: true);
    }

    public void Initialize()
    {
        if (disposed) return;

        if (appUpdateService is not null)
        {
            automaticUpdateTask ??= CheckForStartupUpdateAsync();
        }

        if (loggerEntryWrittenHandler is null)
        {
            loggerEntryWrittenHandler = (_, entry) => appLogBuffer.Enqueue(entry);
            logger.EntryWritten += loggerEntryWrittenHandler;
        }

        if (followedStreamsService is not null)
        {
            EnsureFollowedChannelsRefreshTimerStarted();
            _ = RefreshFollowedChannelsAsync();
        }
    }

    public void RefreshSettingsBindings()
    {
        OnPropertyChanged(nameof(Settings));
    }

    internal void SetStartupWarning(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusMessage = message.Trim();
        }
    }

    private enum NavigationPage
    {
        Followed,
        Recent,
        Vods,
        BrowseCategories,
        BrowseStreams,
        Stream,
        Settings
    }

    private sealed record NavigationDestination(
        NavigationPage Page,
        StreamTabViewModel? Tab = null,
        PlatformKind BrowsePlatform = PlatformKind.Twitch,
        BrowseCategoryViewModel? BrowseCategory = null);

    private NavigationDestination CaptureNavigationDestination()
    {
        if (IsSettingsOpen)
        {
            return new(NavigationPage.Settings);
        }

        if (!IsHomeSelected && SelectedTab is { } tab)
        {
            return new(NavigationPage.Stream, Tab: tab);
        }

        if (IsBrowseHomePageSelected)
        {
            return isBrowseStreamsPageSelected && SelectedBrowseCategory is { } category
                ? new(NavigationPage.BrowseStreams, BrowsePlatform: SelectedBrowsePlatform, BrowseCategory: category)
                : new(NavigationPage.BrowseCategories, BrowsePlatform: SelectedBrowsePlatform);
        }

        return new(IsRecentHomePageSelected
            ? NavigationPage.Recent
            : IsTwitchVodsHomePageSelected
                ? NavigationPage.Vods
                : NavigationPage.Followed);
    }

    private bool IsNavigationDestinationAvailable(NavigationDestination destination)
    {
        return destination.Page != NavigationPage.Stream ||
            destination.Tab is { } tab && Tabs.Contains(tab);
    }

    private void RecordNavigation()
    {
        var destination = CaptureNavigationDestination();
        if (destination != currentNavigationDestination)
        {
            if (!restoringNavigation && IsNavigationDestinationAvailable(currentNavigationDestination) &&
                (navigationHistory.Count == 0 || navigationHistory[^1] != currentNavigationDestination))
            {
                navigationHistory.Add(currentNavigationDestination);
            }

            currentNavigationDestination = destination;
        }

        RaiseNavigationCommandState();
    }

    private void RaiseNavigationCommandState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        GoBackCommand.RaiseCanExecuteChanged();
    }

    private void GoBack()
    {
        var current = CaptureNavigationDestination();
        while (navigationHistory.Count > 0)
        {
            var destination = navigationHistory[^1];
            navigationHistory.RemoveAt(navigationHistory.Count - 1);
            if (!IsNavigationDestinationAvailable(destination) || destination == current)
            {
                continue;
            }

            restoringNavigation = true;
            try
            {
                RestoreNavigationDestination(destination);
            }
            finally
            {
                restoringNavigation = false;
                currentNavigationDestination = CaptureNavigationDestination();
                RaiseNavigationCommandState();
            }

            return;
        }

        RaiseNavigationCommandState();
    }

    private void RestoreNavigationDestination(NavigationDestination destination)
    {
        if (destination.Page == NavigationPage.Settings)
        {
            IsSettingsOpen = true;
            return;
        }

        IsSettingsOpen = false;
        if (destination.Page == NavigationPage.Stream)
        {
            SelectedTab = destination.Tab;
            IsHomeSelected = false;
            ApplyVideoLayout();
            return;
        }

        SelectHome();
        switch (destination.Page)
        {
            case NavigationPage.Followed:
                ShowFollowedHomePage();
                break;
            case NavigationPage.Recent:
                ShowRecentHomePage();
                break;
            case NavigationPage.Vods:
                ShowTwitchVodsHomePage();
                break;
            case NavigationPage.BrowseCategories:
            case NavigationPage.BrowseStreams:
                if (SelectedBrowsePlatform != destination.BrowsePlatform)
                {
                    SelectBrowsePlatform(destination.BrowsePlatform);
                }

                if (destination.Page == NavigationPage.BrowseStreams && destination.BrowseCategory is { } category)
                {
                    IsRecentHomePageSelected = false;
                    IsTwitchVodsHomePageSelected = false;
                    IsBrowseHomePageSelected = true;
                    if (!isBrowseStreamsPageSelected || SelectedBrowseCategory != category)
                    {
                        backgroundOperationController.Track(SelectBrowseCategoryAsync(category));
                    }
                }
                else
                {
                    ShowBrowseHomePage();
                }

                break;
        }
    }

    private void SelectHome()
    {
        if (SelectedTab is not null)
        {
            SelectedTab = null;
        }
        else
        {
            IsHomeSelected = true;
            ActivateMainWindowAudio();
            ApplyVideoLayout();
            ApplyInactivePlaybackPolicyInBackground();
        }

        StatusMessage = "Home";
        RecordNavigation();
    }

    private void ShowFollowedHomePage()
    {
        CancelActiveBrowseCategoryViewerCountLoad();
        IsRecentHomePageSelected = false;
        IsTwitchVodsHomePageSelected = false;
        IsBrowseHomePageSelected = false;
        StatusMessage = "Live followed channels";
        RecordNavigation();
    }

    private void ShowTwitchVodsHomePage()
    {
        CancelActiveBrowseCategoryViewerCountLoad();
        IsRecentHomePageSelected = false;
        IsTwitchVodsHomePageSelected = true;
        IsBrowseHomePageSelected = false;
        StatusMessage = $"{VodPlatformText} VODs";
        RecordNavigation();
    }

    private void ShowRecentHomePage()
    {
        CancelActiveBrowseCategoryViewerCountLoad();
        IsTwitchVodsHomePageSelected = false;
        IsBrowseHomePageSelected = false;
        IsRecentHomePageSelected = true;
        StatusMessage = RecentStreamsStatus;
        EnsureRecentThumbnailRefreshTimerStarted();
        RefreshRecentThumbnailsInBackground();
        RecordNavigation();
    }

    private void ShowBrowseHomePage()
    {
        IsRecentHomePageSelected = false;
        IsTwitchVodsHomePageSelected = false;
        IsBrowseHomePageSelected = true;
        ReturnToBrowseCategoriesPage();
        if (browseService is not null &&
            !HasBrowseCategories &&
            !IsBrowseCategoriesLoading &&
            !HasBrowseCategorySearchCompleted)
        {
            _ = LoadBrowseCategoriesAsync(reset: true);
        }

        RecordNavigation();
    }

    private void ShowBrowseCategoriesPage(bool clearSelection)
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

    private void ReturnToBrowseCategoriesPage()
    {
        ShowBrowseCategoriesPage(clearSelection: true);
        BrowseStatus = browseCategoryStatus;
        StatusMessage = BrowseStatus;
        StartBrowseCategoryViewerCountLoad(SelectedBrowsePlatform, BrowseCategorySearchText.Trim());
        RecordNavigation();
    }

    private void SetBrowseStreamsPageSelected(bool value)
    {
        if (isBrowseStreamsPageSelected == value)
        {
            return;
        }

        isBrowseStreamsPageSelected = value;
        RaiseBrowsePageStateChanged();
    }

    private void RaiseBrowsePageStateChanged()
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

    private void ToggleReplaySeekBar()
    {
        IsReplaySeekBarUiVisible = !IsReplaySeekBarUiVisible;
        StatusMessage = IsReplaySeekBarUiVisible
            ? "Replay seekbar shown"
            : "Replay seekbar hidden";
    }

    private Task RefreshFollowedChannelsAsync()
    {
        lock (followedChannelsRefreshTaskGate)
        {
            if (disposed) return Task.CompletedTask;

            var chat = Settings.Chat;
            var key = OAuthTokenHelpers.CreateCredentialFingerprint(
                KickFollowedChannelsText, string.Join('\n', Settings.FollowedChannels.KickImportedChannelSlugs),
                Settings.FollowedChannels.KickFollowsImportedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                chat.TwitchOAuthToken, chat.TwitchClientId,
                chat.KickOAuthToken, chat.KickRefreshToken, chat.KickClientId, chat.KickClientSecret,
                chat.KickTokenExpiresAtUtc?.ToString("O", CultureInfo.InvariantCulture));
            if (activeFollowedChannelsRefreshTask is { IsCompleted: false } active &&
                activeFollowedChannelsRefreshKey == key)
            {
                return active;
            }

            var generation = Interlocked.Increment(ref followedChannelsRefreshGeneration);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Publish before starting: synchronous providers and property-change callbacks
            // can reenter refresh while the operation is being started.
            activeFollowedChannelsRefreshTask = completion.Task;
            activeFollowedChannelsRefreshKey = key;
            backgroundOperationController.Track(completion.Task);
            _ = CompleteFollowedChannelsRefreshAsync(completion, generation, followedChannelsRefreshCancellation.Token);
            return completion.Task;
        }
    }

    private async Task CompleteFollowedChannelsRefreshAsync(
        TaskCompletionSource completion, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshFollowedChannelsCoreAsync(generation, cancellationToken);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private bool IsCurrentFollowedRefresh(int generation) =>
        !disposed && generation == Volatile.Read(ref followedChannelsRefreshGeneration);

    private async Task RefreshFollowedChannelsCoreAsync(int generation, CancellationToken cancellationToken)
    {
        if (followedStreamsService is null)
        {
            FollowedChannelsStatus = "Live followed channels are not available.";
            return;
        }

        var enteredRefreshGate = false;
        try
        {
            await followedChannelsRefreshGate.WaitAsync(cancellationToken);
            enteredRefreshGate = true;

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentFollowedRefresh(generation)) return;
            Settings.FollowedChannels.KickChannelSlugs = ParseKickFollowedChannelSlugs(
                KickFollowedChannelsText,
                skipInvalidEntries: true,
                out var invalidKickFollowedEntries);
            IsFollowedChannelsRefreshing = true;
            FollowedChannelsStatus = "Refreshing live followed channels";

            var result = await followedStreamsService.GetLiveFollowedStreamsAsync(Settings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentFollowedRefresh(generation))
            {
                return;
            }

            var thumbnailCacheVersion = Interlocked.Increment(ref nextLiveThumbnailCacheVersion);
            UpdateLiveStreamCards(LiveFollowedChannels,
                result.Streams.Select(LiveStreamCardData.FromFollowedStream), thumbnailCacheVersion);

            ProcessFollowedChannelLiveNotifications(result);

            followedChannelsLastUpdatedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(FollowedChannelsLastUpdatedText));

            var statusPrefix = LiveFollowedChannels.Count switch
            {
                0 => "No followed channels are live.",
                1 => "1 followed channel is live.",
                _ => $"{LiveFollowedChannels.Count} followed channels are live."
            };
            var messages = result.Messages.ToList();
            if (invalidKickFollowedEntries.Count > 0)
            {
                var invalidMessage = FormatInvalidKickFollowedChannelsMessage(invalidKickFollowedEntries.Count);
                messages.Add(invalidMessage);
                logger.Write(
                    AppLogLevel.Warning,
                    "Followed",
                    $"{invalidMessage} Entries: {string.Join(", ", invalidKickFollowedEntries)}");
            }

            FollowedChannelsStatus = messages.Count == 0
                ? statusPrefix
                : $"{statusPrefix} {string.Join(' ', messages)}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || disposed)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsCurrentFollowedRefresh(generation)) FollowedChannelsStatus = ex.Message;
            logger.Write(AppLogLevel.Warning, "Followed", "Failed to refresh live followed channels.", ex);
        }
        finally
        {
            if (enteredRefreshGate && IsCurrentFollowedRefresh(generation) && !cancellationToken.IsCancellationRequested)
            {
                IsFollowedChannelsRefreshing = false;
            }

            if (enteredRefreshGate)
            {
                followedChannelsRefreshGate.Release();
            }
        }
    }

    private void UpdateLiveStreamCards(ObservableCollection<LiveStreamCardViewModel> cards,
        IEnumerable<LiveStreamCardData> streams, long thumbnailCacheVersion)
    {
        PagedResultTracker.ApplyItems(cards, streams,
            card => card.Target.StateKey, data => data.Target.StateKey,
            data => new LiveStreamCardViewModel(data, OpenLiveStreamCardAsync, thumbnailCacheVersion),
            (card, data) => card.Update(data, thumbnailCacheVersion), reset: true);
    }

    private void ProcessFollowedChannelLiveNotifications(FollowedLiveStreamsResult result)
    {
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in result.Streams)
        {
            currentKeys.Add(stream.Target.StateKey);
        }

        var previousKeys = previousLiveFollowedKeys ?? [];

        if (liveNotificationService is not null && Settings.FollowedChannels.NotifyWhenLive)
        {
            foreach (var stream in result.Streams)
            {
                // Only platforms that already completed a refresh may toast: each platform's
                // first successful round seeds its baseline silently, so neither app startup nor
                // a platform that was failing when the app started announces channels that were
                // already live before we could observe them.
                if (baselinedLivePlatforms.Contains(stream.Platform) &&
                    !previousKeys.Contains(stream.Target.StateKey))
                {
                    NotifyChannelLive(stream);
                }
            }
        }

        if (result.SucceededPlatforms is null)
        {
            // Services that do not report platform health (e.g. test stubs) baseline everything.
            baselinedLivePlatforms.UnionWith(Enum.GetValues<PlatformKind>());
        }
        else
        {
            baselinedLivePlatforms.UnionWith(result.SucceededPlatforms);
        }

        previousLiveFollowedKeys = BuildNextLiveFollowedKeys(previousKeys, currentKeys, result.SucceededPlatforms);
    }

    private static HashSet<string> BuildNextLiveFollowedKeys(
        HashSet<string> previousKeys,
        HashSet<string> currentKeys,
        IReadOnlyList<PlatformKind>? succeededPlatforms)
    {
        var nextKeys = new HashSet<string>(currentKeys, StringComparer.OrdinalIgnoreCase);

        // When the service does not report platform health (e.g. in tests), trust every
        // platform so genuinely-offline channels are pruned normally.
        if (succeededPlatforms is null)
        {
            return nextKeys;
        }

        var healthyPlatforms = new HashSet<PlatformKind>(succeededPlatforms);

        // Carry over channels that belong to a platform that failed this round. A transient
        // API error must not drop a still-live channel, otherwise it would be re-announced
        // as "live" the moment the platform recovers.
        foreach (var key in previousKeys)
        {
            if (!IsKeyForHealthyPlatform(key, healthyPlatforms))
            {
                nextKeys.Add(key);
            }
        }

        return nextKeys;
    }

    private static bool IsKeyForHealthyPlatform(string stateKey, HashSet<PlatformKind> healthyPlatforms)
    {
        var separatorIndex = stateKey.IndexOf(':');
        if (separatorIndex > 0 &&
            Enum.TryParse<PlatformKind>(stateKey[..separatorIndex], ignoreCase: true, out var platform))
        {
            return healthyPlatforms.Contains(platform);
        }

        // Unknown key shape: treat as healthy so the carry-over set cannot grow without bound.
        return true;
    }

    private void NotifyChannelLive(FollowedLiveStream stream)
    {
        if (liveNotificationService is null)
        {
            return;
        }

        try
        {
            var displayName = string.IsNullOrWhiteSpace(stream.DisplayName) ? stream.Channel : stream.DisplayName;
            liveNotificationService.NotifyChannelLive(new LiveChannelNotification(
                stream.Platform,
                stream.Channel,
                displayName,
                stream.Title,
                stream.CategoryName,
                stream.ViewerCount,
                stream.ThumbnailUrl));
            logger.Write(AppLogLevel.Info, "Followed", $"Live notification sent for {displayName} ({stream.Platform}).");
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Followed", $"Failed to notify that {stream.Channel} is live.", ex);
        }
    }

    public void OpenChannelFromNotification(PlatformKind platform, string channel)
    {
        // Invoked on the UI thread by the window's notification-activation handler.
        _ = OpenChannelFromNotificationAsync(platform, channel);
    }

    private async Task OpenChannelFromNotificationAsync(PlatformKind platform, string channel)
    {
        try
        {
            var target = StreamInputParser.FromChannel(platform, channel);
            IsHomeSelected = false;
            await OpenCandidatesAsync([target], clearInputOnSuccess: false, selectOpenedTab: true);
        }
        catch (Exception ex)
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            RecordNavigation();
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Followed", $"Failed to open {channel} from a live notification.", ex);
        }
    }

    private void SelectVodPlatform(PlatformKind platform)
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

    private void SelectTwitchVodType(TwitchVodTypeFilter type)
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

    private Task SearchTwitchVodsAsync(bool reset)
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

    private async Task RunTwitchVodSearchAsync(bool reset, PlatformKind platform,
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
                        vod => new VodViewModel(vod, OpenTwitchVodAsync), (card, vod) => card.Update(vod),
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
                        vod => new VodViewModel(vod, OpenTwitchVodAsync), (card, vod) => card.Update(vod),
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

    private async Task LoadVodWatchProgressAsync(CancellationToken cancellationToken)
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

    private void OnVodBookmarkChanged(StreamTarget target, VodPlaybackBookmark bookmark)
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

    private async Task OpenTwitchVodAsync(VodViewModel vod, bool stayOnHome)
    {
        try
        {
            if (!stayOnHome)
            {
                IsHomeSelected = false;
            }

            await OpenCandidatesAsync([vod.Target], clearInputOnSuccess: false, selectOpenedTab: !stayOnHome);
        }
        catch (Exception ex)
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            RecordNavigation();
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "VODs", $"Failed to open {vod.Platform} VOD {vod.Id}.", ex);
        }
    }

    private bool IsCurrentTwitchVodSearch(int searchGeneration, string query, TwitchVodTypeFilter type)
    {
        return IsCurrentTwitchVodSearch(searchGeneration, query, type, SelectedVodPlatform);
    }

    private bool IsCurrentTwitchVodSearch(int searchGeneration, string query, TwitchVodTypeFilter type, PlatformKind platform)
    {
        return !disposed &&
            vodBrowseController.IsCurrentTwitchVodGeneration(searchGeneration) &&
            SelectedVodPlatform == platform &&
            SelectedTwitchVodType == type &&
            string.Equals(TwitchVodSearchText.Trim(), query, StringComparison.Ordinal);
    }

    private void ClearTwitchVodSearchResults()
    {
        TwitchVods.Clear();
        TwitchVodNextCursor = "";
        HasTwitchVodSearchCompleted = false;
        if (!HasTwitchVodSearchText)
        {
            TwitchVodStatus = $"Search a {VodPlatformText} streamer to browse VODs.";
        }
    }

    private void ScheduleAutomaticTwitchVodSearch()
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

    private async Task RunAutomaticTwitchVodSearchAsync(
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

    private void CancelTwitchVodSearchDebounce()
    {
        vodBrowseController.CancelScheduledTwitchVod();
    }

    private CancellationTokenSource ReplaceTwitchVodSearchCancellation()
    {
        return vodBrowseController.BeginTwitchVodOperation(lifetimeCancellation.Token);
    }

    private void CancelActiveTwitchVodSearch()
    {
        vodBrowseController.CancelTwitchVodOperation();
    }

    private void DisposeTwitchVodSearchCancellation(CancellationTokenSource cancellation)
    {
        vodBrowseController.CompleteTwitchVodOperation(cancellation);
    }

    private void RaiseTwitchVodCommandStates()
    {
        SearchTwitchVodsCommand.RaiseCanExecuteChanged();
        LoadMoreTwitchVodsCommand.RaiseCanExecuteChanged();
    }

    private void SelectBrowsePlatform(PlatformKind platform)
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
        RecordNavigation();
        _ = LoadBrowseCategoriesAsync(reset: true);
    }

    private async Task RefreshBrowseAsync()
    {
        if (disposed) return;
        if (IsBrowseStreamsPageVisible && SelectedBrowseCategory is not null)
        {
            await LoadBrowseStreamsAsync(reset: true);
            return;
        }

        await LoadBrowseCategoriesAsync(reset: true);
    }

    private Task LoadBrowseCategoriesAsync(bool reset)
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

    private async Task RunBrowseCategoriesAsync(bool reset, BrowseCategoryRequest request, int searchGeneration)
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
                IEnumerable<BrowseCategory> categories = platform == PlatformKind.Kick && reset
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
                if (platform == PlatformKind.Kick && !reset)
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

    private void SortBrowseCategoriesByViewerCount()
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

    private static IOrderedEnumerable<T> OrderBrowseCategories<T>(IEnumerable<T> categories,
        Func<T, int?> viewerCount, Func<T, string> name) => categories
        .OrderBy(category => viewerCount(category) is null ? 1 : 0)
        .ThenByDescending(category => viewerCount(category) ?? 0)
        .ThenBy(name, StringComparer.OrdinalIgnoreCase);

    private void StartBrowseCategoryViewerCountLoad(PlatformKind platform, string query)
    {
        if (browseService is null || platform != PlatformKind.Twitch)
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
            if (browseCategoryViewerCountCancellation is not null)
            {
                browseCategoryViewerCountLoadPending = true;
                return;
            }

            viewerCountGeneration = vodBrowseController.AdvanceBrowseCategoryViewerCountGeneration();
            cancellation = new CancellationTokenSource();
            browseCategoryViewerCountCancellation = cancellation;
        }

        _ = LoadBrowseCategoryViewerCountsAsync(
            platform,
            query,
            viewerCountGeneration,
            categoryIds,
            cancellation);
    }

    private async Task LoadBrowseCategoryViewerCountsAsync(
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

    private static bool ShouldPrioritizeFirstBrowseCategoryViewerCount(PlatformKind platform, string query)
    {
        return platform == PlatformKind.Twitch && string.IsNullOrWhiteSpace(query);
    }

    private async Task LoadBrowseCategoryViewerCountBatchAsync(
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

    private void ReportBrowseCategoryViewerCountFailure(
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
            cancellation.Cancel();
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

    private void ApplyBrowseCategoryViewerCount(
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

    private void SetBrowseCategoryStatus(string message)
    {
        browseCategoryStatus = message;
        if (!IsBrowseCategoriesPageVisible)
        {
            return;
        }

        BrowseStatus = message;
        StatusMessage = message;
    }

    private async Task SelectBrowseCategoryAsync(BrowseCategoryViewModel category)
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
        RecordNavigation();
        await LoadBrowseStreamsAsync(reset: true);
    }

    private Task LoadBrowseStreamsAsync(bool reset)
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

    private async Task RunBrowseStreamsAsync(bool reset, BrowseStreamRequest request, int searchGeneration)
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
                var thumbnailCacheVersion = Interlocked.Increment(ref nextLiveThumbnailCacheVersion);
                BrowseStreamNextCursor = browseStreamPages.ApplyPage(
                    BrowseStreams, result.Items.Select(LiveStreamCardData.FromBrowseStream),
                    card => card.Target.StateKey, data => data.Target.StateKey,
                    data => new LiveStreamCardViewModel(data, OpenLiveStreamCardAsync, thumbnailCacheVersion),
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

    private async Task OpenLiveStreamCardAsync(LiveStreamCardViewModel stream, bool stayOnHome)
    {
        try
        {
            if (!stayOnHome)
            {
                IsHomeSelected = false;
            }

            SetRecentStreamHint(stream.Target, stream.ThumbnailUrl, stream.DisplayName, stream.CategoryName);
            await OpenCandidatesAsync([stream.Target], clearInputOnSuccess: false, selectOpenedTab: !stayOnHome);
        }
        catch (Exception ex)
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            RecordNavigation();
            StatusMessage = ex.Message;
            var (area, origin) = stream.Source switch
            {
                LiveStreamCardSource.Followed => ("Followed", "home"),
                _ => ("Browse", "browse")
            };
            logger.Write(AppLogLevel.Error, area, $"Failed to open {stream.Target.DisplayName} from {origin}.", ex);
        }
    }

    private bool IsCurrentBrowseCategorySearch(int searchGeneration, PlatformKind platform, string query)
    {
        return !disposed &&
            vodBrowseController.IsCurrentBrowseCategoryGeneration(searchGeneration) &&
            SelectedBrowsePlatform == platform &&
            string.Equals(BrowseCategorySearchText.Trim(), query, StringComparison.Ordinal);
    }

    private bool IsCurrentBrowseCategoryViewerCountLoad(
        int viewerCountGeneration,
        PlatformKind platform,
        string query)
    {
        return !disposed &&
            vodBrowseController.IsCurrentBrowseCategoryViewerCountGeneration(viewerCountGeneration) &&
            SelectedBrowsePlatform == platform &&
            string.Equals(BrowseCategorySearchText.Trim(), query, StringComparison.Ordinal);
    }

    private bool IsCurrentBrowseStreamSearch(int searchGeneration, PlatformKind platform, string categoryId)
    {
        return !disposed &&
            vodBrowseController.IsCurrentBrowseStreamGeneration(searchGeneration) &&
            SelectedBrowsePlatform == platform &&
            SelectedBrowseCategory is { } selectedCategory &&
            string.Equals(selectedCategory.Id, categoryId, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearBrowseCategories(bool clearStatus)
    {
        BrowseCategories.Clear();
        BrowseCategoryNextCursor = "";
        if (clearStatus)
        {
            BrowseStatus = "Browse Twitch or Kick categories.";
            browseCategoryStatus = BrowseStatus;
        }
    }

    private void ClearBrowseStreams(bool clearSelectedCategory)
    {
        BrowseStreams.Clear();
        BrowseStreamNextCursor = "";
        HasBrowseStreamSearchCompleted = false;
        if (clearSelectedCategory)
        {
            SelectedBrowseCategory = null;
        }
    }

    private void ScheduleAutomaticBrowseCategorySearch()
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

    private async Task RunAutomaticBrowseCategorySearchAsync(
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

    private void CancelBrowseCategorySearchDebounce()
    {
        vodBrowseController.CancelScheduledBrowseCategory();
    }

    private CancellationTokenSource ReplaceBrowseCategorySearchCancellation()
    {
        return vodBrowseController.BeginBrowseCategoryOperation(lifetimeCancellation.Token);
    }

    private CancellationTokenSource ReplaceBrowseStreamSearchCancellation()
    {
        return vodBrowseController.BeginBrowseStreamOperation(lifetimeCancellation.Token);
    }

    private void CancelActiveBrowseCategorySearch()
    {
        vodBrowseController.CancelBrowseCategoryOperation();
    }

    /// <summary>
    /// Builds an async command whose unhandled failures are surfaced and logged. Without an
    /// error handler <see cref="AsyncRelayCommand"/>'s async-void entry point swallows them,
    /// so a failing command looks like a button that simply does nothing.
    /// </summary>
    private AsyncRelayCommand CreateCommand(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(execute, () => !disposed && (canExecute?.Invoke() ?? true), ReportCommandFailure);

    private void ReportCommandFailure(Exception exception)
    {
        if (disposed || exception is OperationCanceledException)
        {
            return;
        }

        StatusMessage = $"Command failed. {exception.Message}";
        logger.Write(AppLogLevel.Error, "Command", "An application command failed.", exception);
    }

    private void CancelActiveBrowseCategoryViewerCountLoad()
    {
        vodBrowseController.AdvanceBrowseCategoryViewerCountGeneration();
        lock (browseCategoryViewerCountGate)
        {
            browseCategoryViewerCountCancellation?.Cancel();
            browseCategoryViewerCountCancellation = null;
            browseCategoryViewerCountLoadPending = false;
        }
    }

    private void CancelActiveBrowseStreamSearch()
    {
        vodBrowseController.CancelBrowseStreamOperation();
    }

    private void DisposeBrowseCategorySearchCancellation(CancellationTokenSource cancellation)
    {
        vodBrowseController.CompleteBrowseCategoryOperation(cancellation);
    }

    private bool DisposeBrowseCategoryViewerCountCancellation(CancellationTokenSource cancellation)
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

    private void DisposeBrowseStreamSearchCancellation(CancellationTokenSource cancellation)
    {
        vodBrowseController.CompleteBrowseStreamOperation(cancellation);
    }

    private void RaiseBrowseCommandStates()
    {
        ReturnToBrowseCategoriesCommand.RaiseCanExecuteChanged();
        RefreshBrowseCommand.RaiseCanExecuteChanged();
        LoadMoreBrowseCategoriesCommand.RaiseCanExecuteChanged();
        LoadMoreBrowseStreamsCommand.RaiseCanExecuteChanged();
    }

    private async Task OpenRecentStreamAsync(RecentStreamViewModel stream, bool stayOnHome)
    {
        try
        {
            if (!stayOnHome)
            {
                IsHomeSelected = false;
            }

            await OpenCandidatesAsync([stream.Target], clearInputOnSuccess: false, selectOpenedTab: !stayOnHome);
        }
        catch (Exception ex)
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            RecordNavigation();
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Recent", $"Failed to open {stream.Target.DisplayName} from recent streams.", ex);
        }
    }

    private async Task DeleteRecentStreamAsync(RecentStreamViewModel stream)
    {
        if (disposed)
        {
            return;
        }

        var target = stream.Target;
        try
        {
            await recentStreamsGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                if (!Settings.RecentStreams.Any(recentStream => IsSameRecentStream(recentStream, target)))
                {
                    return;
                }

                Settings.RecentStreams = Settings.RecentStreams
                    .Where(recentStream => !IsSameRecentStream(recentStream, target))
                    .ToList();
                recentStreamController.RemoveLiveStatus(target.StateKey);
                recentStreamController.TakeHint(target.StateKey);
                pendingRecentMetadata?.Remove(target.StateKey);

                RebuildRecentStreams();
                StatusMessage = $"{target.DisplayName} removed from recent streams";
                await SaveRecentStreamRemovalAsync(target);
            }
            finally
            {
                recentStreamsGate.Release();
            }
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to remove {target.DisplayName} from recent streams.", ex);
        }
    }

    public void RememberPictureInPictureWindowBounds(PictureInPictureWindowLocation bounds)
    {
        Settings.PictureInPictureWindowLocation = bounds;
    }

    public async Task RememberPictureInPictureWindowBoundsAsync(PictureInPictureWindowLocation bounds)
    {
        RememberPictureInPictureWindowBounds(bounds);

        try
        {
            await settingsService.SaveAsync(Settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "UI", "Failed to save picture-in-picture window bounds.", ex);
        }
    }

    public async Task RememberStreamPictureInPictureTopBarVisibilityAsync(
        StreamTarget target,
        bool showTopBar)
    {
        Settings.StreamPictureInPictureTopBarVisibility[target.StateKey] = showTopBar;

        try
        {
            await settingsService.SaveAsync(Settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(
                AppLogLevel.Warning,
                "UI",
                $"Failed to save picture-in-picture top bar preference for {target.DisplayName}.",
                ex);
        }
    }

    public bool SelectAdjacentTab(int direction)
    {
        if (SelectedTab is null || Tabs.Count <= 1 || direction == 0)
        {
            return false;
        }

        var index = Tabs.IndexOf(SelectedTab);
        if (index < 0)
        {
            return false;
        }

        var nextIndex = direction < 0
            ? (index == 0 ? Tabs.Count - 1 : index - 1)
            : (index == Tabs.Count - 1 ? 0 : index + 1);

        if (nextIndex == index)
        {
            return false;
        }

        SelectedTab = Tabs[nextIndex];
        return true;
    }

    public IReadOnlyList<StreamTabViewModel> GetPictureInPictureDragTabs(StreamTabViewModel tab)
    {
        if (!Tabs.Contains(tab))
        {
            return [];
        }

        return ResolveHostableTabGroup(tab) ?? [tab];
    }

    public bool IsCurrentVideoViewMultiStream()
    {
        var visibleTabs = GetVisibleVideoTabs();
        return visibleTabs.Count > 1 &&
            SelectedTab is not null &&
            visibleTabs.Contains(SelectedTab);
    }

    public bool CanReorderVisibleVideoTab(StreamTabViewModel tab)
    {
        if (IsVideoFullscreenActive ||
            IsStreamOnlyFullscreenActive ||
            tab.IsDetached ||
            !Tabs.Contains(tab))
        {
            return false;
        }

        var visibleTabs = GetVisibleVideoTabs();
        return visibleTabs.Count > 1 && visibleTabs.Contains(tab);
    }

    public bool TryReorderVisibleVideoTab(StreamTabViewModel draggedTab, StreamTabViewModel targetTab)
    {
        if (ReferenceEquals(draggedTab, targetTab) ||
            IsVideoFullscreenActive ||
            IsStreamOnlyFullscreenActive)
        {
            return false;
        }

        var visibleTabs = GetVisibleVideoTabs();
        if (visibleTabs.Count <= 1 ||
            !visibleTabs.Contains(draggedTab) ||
            !visibleTabs.Contains(targetTab))
        {
            return false;
        }

        var oldIndex = Tabs.IndexOf(draggedTab);
        var newIndex = Tabs.IndexOf(targetTab);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            return false;
        }

        Tabs.Move(oldIndex, newIndex);
        SelectedTab = draggedTab;
        StatusMessage = $"{draggedTab.Target.DisplayName} moved";
        RaiseCommandStates();
        return true;
    }

    public bool TryReorderTabStripTabs(
        IReadOnlyList<StreamTabViewModel> draggedTabs,
        StreamTabViewModel targetTab,
        bool insertAfterTarget,
        StreamTabViewModel? selectedDraggedTab = null)
    {
        var validDraggedTabs = draggedTabs
            .Where(Tabs.Contains)
            .Distinct()
            .ToArray();
        if (validDraggedTabs.Length == 0 ||
            validDraggedTabs.Length != draggedTabs.Count ||
            validDraggedTabs.Length != draggedTabs.Distinct().Count() ||
            !Tabs.Contains(targetTab))
        {
            return false;
        }

        var tabStripGroups = BuildTabStripGroups();
        var draggedSet = validDraggedTabs.ToHashSet();
        var draggedGroup = tabStripGroups.FirstOrDefault(group => group.Count == draggedSet.Count && group.All(draggedSet.Contains));
        var targetGroup = tabStripGroups.FirstOrDefault(group => group.Contains(targetTab));
        if (draggedGroup is null ||
            targetGroup is null ||
            draggedGroup.Any(targetGroup.Contains))
        {
            return false;
        }

        var desiredGroups = tabStripGroups
            .Where(group => !ReferenceEquals(group, draggedGroup))
            .ToList();
        var targetIndex = desiredGroups.FindIndex(group => ReferenceEquals(group, targetGroup));
        if (targetIndex < 0)
        {
            return false;
        }

        desiredGroups.Insert(insertAfterTarget ? targetIndex + 1 : targetIndex, draggedGroup);
        var desiredTabs = desiredGroups
            .SelectMany(group => group)
            .ToArray();
        if (desiredTabs.SequenceEqual(Tabs))
        {
            return false;
        }

        for (var index = 0; index < desiredTabs.Length; index++)
        {
            var currentIndex = Tabs.IndexOf(desiredTabs[index]);
            if (currentIndex >= 0 && currentIndex != index)
            {
                Tabs.Move(currentIndex, index);
            }
        }

        SelectedTab = selectedDraggedTab is not null && draggedSet.Contains(selectedDraggedTab)
            ? selectedDraggedTab
            : draggedGroup[0];
        StatusMessage = draggedGroup.Count == 1
            ? $"{draggedGroup[0].Target.DisplayName} moved"
            : $"{draggedGroup.Count.ToString(CultureInfo.InvariantCulture)} streams moved";
        RaiseCommandStates();
        return true;
    }

    public bool TryMergeTabsIntoMultiView(
        IReadOnlyList<StreamTabViewModel> draggedTabs,
        StreamTabViewModel targetTab,
        StreamTabViewModel? selectedDraggedTab = null)
    {
        var validDraggedTabs = draggedTabs
            .Where(Tabs.Contains)
            .Distinct()
            .ToArray();
        var draggedSet = validDraggedTabs.ToHashSet();
        if (validDraggedTabs.Length == 0 ||
            validDraggedTabs.Length != draggedTabs.Distinct().Count() ||
            !Tabs.Contains(targetTab) ||
            draggedSet.Contains(targetTab) ||
            targetTab.IsDetached ||
            validDraggedTabs.Any(tab => tab.IsDetached) ||
            (validDraggedTabs.Length == 1 && validDraggedTabs[0].IsMergedTabGroupMember))
        {
            return false;
        }

        var insertAfterTab = GetMultiViewTabGroup(targetTab)?
            .Where(tab => !draggedSet.Contains(tab))
            .LastOrDefault() ?? targetTab;

        RemoveTabsFromMultiViewGroups(validDraggedTabs, applyLayout: false);
        var targetGroup = GetMultiViewTabGroupList(targetTab);
        if (targetGroup is null)
        {
            targetGroup = [targetTab];
            tabGroupingController.MultiViewGroups.Add(targetGroup);
        }

        foreach (var draggedTab in validDraggedTabs)
        {
            if (!targetGroup.Contains(draggedTab))
            {
                targetGroup.Add(draggedTab);
            }
        }

        MoveTabsAfterTarget(validDraggedTabs, insertAfterTab);
        SelectedTab = selectedDraggedTab is not null && draggedSet.Contains(selectedDraggedTab)
            ? selectedDraggedTab
            : validDraggedTabs[^1];
        StatusMessage = validDraggedTabs.Length == 1
            ? $"{validDraggedTabs[0].Target.DisplayName} merged with {targetTab.Target.DisplayName}"
            : $"{validDraggedTabs.Length.ToString(CultureInfo.InvariantCulture)} streams merged with {targetTab.Target.DisplayName}";
        RaiseCommandStates();
        ApplyVideoLayout();
        ApplyInactivePlaybackPolicyInBackground();
        return true;
    }

    internal void SetPictureInPictureTabGroup(IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        var validTabs = tabs
            .Where(Tabs.Contains)
            .Distinct()
            .ToArray();
        if (validTabs.Length == 0)
        {
            return;
        }

        RemoveTabsFromPictureInPictureGroups(validTabs, applyLayout: false);
        if (validTabs.Length > 1)
        {
            tabGroupingController.PictureInPictureGroups.Add(validTabs.ToList());
        }

        ApplyVideoLayout();
    }

    internal void ClearPictureInPictureTabGroup(IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        RemoveTabsFromPictureInPictureGroups(tabs, applyLayout: true);
    }

    internal void SetPictureInPictureVisibleTabGroup(IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        var validTabs = tabs
            .Where(Tabs.Contains)
            .Distinct()
            .ToArray();
        if (validTabs.Length == 0)
        {
            return;
        }

        RemoveTabsFromPictureInPictureVisibleGroups(validTabs, applyPolicy: false);
        if (validTabs.Length > 1)
        {
            tabGroupingController.PictureInPictureVisibleGroups.Add(validTabs.ToList());
        }

        ApplyVlcPluginMultiViewChatPolicyInBackground(restoreWhenAllowed: true);
    }

    internal void ClearPictureInPictureVisibleTabGroup(IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        RemoveTabsFromPictureInPictureVisibleGroups(tabs, applyPolicy: true);
    }

    private List<StreamTabViewModel>? GetMultiViewTabGroupList(StreamTabViewModel tab)
    {
        foreach (var group in tabGroupingController.MultiViewGroups)
        {
            if (group.Contains(tab))
            {
                return group;
            }
        }

        return null;
    }

    private IReadOnlyList<StreamTabViewModel>? GetMultiViewTabGroup(StreamTabViewModel tab)
    {
        var group = GetMultiViewTabGroupList(tab);
        if (group is null)
        {
            return null;
        }

        var orderedGroup = Tabs
            .Where(group.Contains)
            .ToArray();
        return orderedGroup.Length > 1 ? orderedGroup : null;
    }

    private void RemoveTabsFromMultiViewGroups(IReadOnlyCollection<StreamTabViewModel> tabs, bool applyLayout)
    {
        if (tabGroupingController.RemoveFromMultiViewGroups(tabs) && applyLayout)
        {
            ApplyVideoLayout();
        }
    }

    private void RemoveTabsFromPictureInPictureVisibleGroups(IReadOnlyCollection<StreamTabViewModel> tabs, bool applyPolicy)
    {
        if (tabGroupingController.RemoveFromPictureInPictureVisibleGroups(tabs) && applyPolicy)
        {
            ApplyVlcPluginMultiViewChatPolicyInBackground(restoreWhenAllowed: true);
        }
    }

    private void MoveTabsAfterTarget(IReadOnlyList<StreamTabViewModel> draggedTabs, StreamTabViewModel targetTab)
    {
        var draggedSet = draggedTabs.ToHashSet();
        var orderedDraggedTabs = Tabs
            .Where(draggedSet.Contains)
            .ToArray();
        if (orderedDraggedTabs.Length == 0)
        {
            return;
        }

        var remainingTabs = Tabs
            .Where(tab => !draggedSet.Contains(tab))
            .ToList();
        var targetIndex = remainingTabs.IndexOf(targetTab);
        if (targetIndex < 0)
        {
            return;
        }

        var desiredTabs = remainingTabs
            .Take(targetIndex + 1)
            .Concat(orderedDraggedTabs)
            .Concat(remainingTabs.Skip(targetIndex + 1))
            .ToArray();

        for (var index = 0; index < desiredTabs.Length; index++)
        {
            var currentIndex = Tabs.IndexOf(desiredTabs[index]);
            if (currentIndex >= 0 && currentIndex != index)
            {
                Tabs.Move(currentIndex, index);
            }
        }
    }

    private IReadOnlyList<StreamTabViewModel>? GetPictureInPictureTabGroup(StreamTabViewModel tab)
    {
        foreach (var group in tabGroupingController.PictureInPictureGroups)
        {
            if (!group.Contains(tab))
            {
                continue;
            }

            var orderedGroup = Tabs
                .Where(group.Contains)
                .ToArray();
            return orderedGroup.Length > 1 ? orderedGroup : null;
        }

        return null;
    }

    private void RemoveTabsFromPictureInPictureGroups(IReadOnlyCollection<StreamTabViewModel> tabs, bool applyLayout)
    {
        if (tabGroupingController.RemoveFromPictureInPictureGroups(tabs) && applyLayout)
        {
            ApplyVideoLayout();
        }
    }

    internal async Task OpenStreamAsync(
        StreamTarget target,
        bool clearInputOnSuccess = false,
        bool selectOpenedTab = true)
    {
        if (disposed) return;

        // Capture the initiating search before playback can yield to new input.
        int? searchGenerationToClear = clearInputOnSuccess ? streamSearchController.CurrentGeneration : null;
        if (TryOpenExistingTab(target, selectOpenedTab, searchGenerationToClear))
        {
            return;
        }

        await streamOpenGate.WaitAsync(lifetimeCancellation.Token);
        try
        {
            if (disposed) return;
            // Recheck after entering the gate so overlapping opens share the tab.
            if (TryOpenExistingTab(target, selectOpenedTab, searchGenerationToClear))
            {
                return;
            }

            var tab = selectOpenedTab ? CreateAndSelectTab(target) : CreateTab(target);
            StatusMessage = $"Starting {target.DisplayName}";
            var openingMetadata = LoadOpeningTabMetadataInBackground(tab);
            StartTabInBackground(tab, searchGenerationToClear, openingMetadata);
        }
        finally
        {
            streamOpenGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposalTask is null)
            {
                // Set this before starting asynchronous cleanup.  Event handlers,
                // timers, and command callbacks can therefore only observe a
                // live view model or a disposal-in-progress state.
                foreach (var tab in Tabs) tab.CaptureVodResumePosition(closing: true);
                disposed = true;
                if (vodPlaybackHistory is not null) vodPlaybackHistory.BookmarkChanged -= OnVodBookmarkChanged;
                disposalTask = DisposeCoreAsync();
            }

            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Window shutdown has a deadline. Save before update/search/player cleanup can use it up,
        // including snapshots from tabs whose detached cleanup is still queued.
        if (vodPlaybackHistory is not null)
        {
            try { await vodPlaybackHistory.SaveAsync(); }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "VOD resume", "Could not save VOD progress during shutdown.", ex);
            }
        }
        appLogBuffer.Dispose();
        lifetimeCancellation.Cancel();
        tabStartController.Clear();

        inactivePlaybackPolicyController.Dispose();
        streamSearchController.Dispose();
        vodBrowseController.Dispose();
        CancelActiveBrowseCategoryViewerCountLoad();
        followedChannelsRefreshCancellation.Cancel();
        recentThumbnailRefreshCancellation.Cancel();

        if (loggerEntryWrittenHandler is not null)
        {
            logger.EntryWritten -= loggerEntryWrittenHandler;
            loggerEntryWrittenHandler = null;
        }

        lock (followedChannelsRefreshTimerGate)
        {
            followedChannelsRefreshTimer?.Dispose();
            followedChannelsRefreshTimer = null;
        }

        lock (recentThumbnailRefreshTimerGate)
        {
            recentThumbnailRefreshTimer?.Dispose();
            recentThumbnailRefreshTimer = null;
        }

        Tabs.CollectionChanged -= TabsOnCollectionChanged;
        StreamSearchResults.CollectionChanged -= StreamSearchResultsOnCollectionChanged;
        LiveFollowedChannels.CollectionChanged -= LiveFollowedChannelsOnCollectionChanged;
        TwitchVods.CollectionChanged -= TwitchVodsOnCollectionChanged;
        RecentStreams.CollectionChanged -= RecentStreamsOnCollectionChanged;
        BrowseCategories.CollectionChanged -= BrowseCategoriesOnCollectionChanged;
        BrowseStreams.CollectionChanged -= BrowseStreamsOnCollectionChanged;
        Settings.PropertyChanged -= SettingsOnPropertyChanged;
        if (observedChatSettings is not null)
        {
            observedChatSettings.PropertyChanged -= ChatSettingsOnPropertyChanged;
            observedChatSettings = null;
        }

        if (observedFollowedChannelsSettings is not null)
        {
            observedFollowedChannelsSettings.PropertyChanged -= FollowedChannelsSettingsOnPropertyChanged;
            observedFollowedChannelsSettings = null;
        }

        if (selectedTab is not null)
        {
            selectedTab.PropertyChanged -= SelectedTabOnPropertyChanged;
        }

        foreach (var tab in Tabs)
        {
            tab.PropertyChanged -= TabOnPropertyChanged;
            tab.AudioStateApplied -= TabOnAudioStateApplied;
        }

        foreach (var item in TabStripItems)
        {
            item.Dispose();
        }

        TabStripItems.Clear();

        try
        {
            if (automaticUpdateTask is not null) await automaticUpdateTask;
            await DrainSearchOperationsAsync();

            var tabDisposals = Tabs
                .ToArray()
                .Select(tab => tab.DisposeAsync().AsTask())
                .ToArray();
            if (tabDisposals.Length > 0)
            {
                await Task.WhenAll(tabDisposals);
            }

            await backgroundOperationController.DrainAsync(DetachedDisposalWaitTimeout);

            Task[] pendingDisposals;
            lock (detachedDisposalsGate)
            {
                pendingDisposals = detachedDisposals.ToArray();
            }

            if (pendingDisposals.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pendingDisposals).WaitAsync(DetachedDisposalWaitTimeout);
                }
                catch (TimeoutException)
                {
                    logger.Write(AppLogLevel.Warning, "UI", "Timed out waiting for already closed tabs to finish cleanup during shutdown.");
                    foreach (var pendingDisposal in pendingDisposals)
                    {
                        ObserveDetachedDisposal(pendingDisposal);
                    }
                }
            }
        }
        finally
        {
            lifetimeCancellation.Dispose();
            recentThumbnailRefreshCancellation.Dispose();
            followedChannelsRefreshCancellation.Dispose();
            streamOpenGate.Dispose();
            tabStartController.Dispose();
            chatSettingsApplyGate.Dispose();
            vlcPluginMultiViewChatPolicyGate.Dispose();
            recentStreamsGate.Dispose();
            recentThumbnailRefreshGate.Dispose();
            followedChannelsRefreshGate.Dispose();
            if (appUpdateService is not null)
            {
                appUpdateService.StateChanged -= OnAppUpdateStateChanged;
                (appUpdateService as IDisposable)?.Dispose();
            }
        }
    }

    private async Task DrainSearchOperationsAsync()
    {
        try
        {
            await Task.WhenAll(
                streamSearchController.DrainAsync(DetachedDisposalWaitTimeout),
                vodBrowseController.DrainAsync(DetachedDisposalWaitTimeout));
        }
        catch (TimeoutException)
        {
            logger.Write(AppLogLevel.Warning, "UI", "Timed out waiting for search operations during shutdown.");
        }
    }

    private Task AddAndPlayAsync()
    {
        var query = NewStreamText.Trim();
        CancelStreamSearchDebounce();
        if (activeStreamSearchTask is { IsCompleted: false } &&
            activeStreamSearchGeneration == streamSearchController.CurrentGeneration &&
            string.Equals(activeStreamSearchQuality, SelectedQuality, StringComparison.Ordinal))
        {
            SetStreamSearchDropdownOpen(true);
            return activeStreamSearchTask;
        }

        return StartStreamSearchAsync(query, streamSearchController.AdvanceGeneration());
    }

    private Task StartStreamSearchAsync(string query, int searchGeneration)
    {
        activeStreamSearchGeneration = searchGeneration;
        activeStreamSearchQuality = SelectedQuality;
        activeStreamSearchTask = RunStreamSearchAsync(query, searchGeneration);
        return activeStreamSearchTask;
    }

    private async Task RunStreamSearchAsync(string query, int searchGeneration)
    {
        if (string.IsNullOrWhiteSpace(query) || !IsCurrentStreamSearch(searchGeneration, query))
        {
            return;
        }

        var searchCancellation = ReplaceStreamSearchCancellation();
        StreamSearchResults.Clear();
        StreamSearchStatus = "";
        SetStreamSearchCompleted(false);
        SetStreamSearchDropdownOpen(true);
        IsStreamSearchRunning = true;

        try
        {
            var probes = await SearchStreamCandidatesAsync(query, searchCancellation.Token);
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            var enrichedProbes = await LoadStreamSearchResultMetadataAsync(
                probes,
                searchCancellation.Token);
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            var displayProbes = OrderStreamSearchProbesForDisplay(enrichedProbes);
            ReplaceStreamSearchResults(displayProbes);
            SetStreamSearchCompleted(true);
            StreamSearchStatus = FormatStreamSearchResult(query, displayProbes);
            StatusMessage = StreamSearchStatus;
            IsStreamSearchRunning = false;

            var viewerCountProbes = await LoadStreamSearchResultViewerCountsAsync(
                enrichedProbes,
                searchCancellation.Token);
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            if (!viewerCountProbes.SequenceEqual(enrichedProbes))
            {
                displayProbes = OrderStreamSearchProbesForDisplay(viewerCountProbes);
                UpdateStreamSearchViewerCounts(displayProbes);
                StreamSearchStatus = FormatStreamSearchResult(query, displayProbes);
                StatusMessage = StreamSearchStatus;
            }
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            SetStreamSearchCompleted(true);
            StreamSearchStatus = ex.Message;
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "UI", "Stream search failed.", ex);
        }
        finally
        {
            if (IsCurrentStreamSearch(searchGeneration, query))
            {
                IsStreamSearchRunning = false;
            }

            DisposeStreamSearchCancellation(searchCancellation);
        }
    }

    private async Task<IReadOnlyList<StreamCandidateProbe>> SearchStreamCandidatesAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (StreamInputParser.TryParseTwitchVodUrl(query, out var vodTarget) && vodTarget is not null)
        {
            return [new StreamCandidateProbe(vodTarget, new StreamlinkProbeResult(true, "Twitch VOD"))];
        }

        if (streamSearchService is not null)
        {
            var serviceMessage = $"Searching Twitch and Kick for {query}";
            StatusMessage = serviceMessage;
            StreamSearchStatus = serviceMessage;
            var result = await streamSearchService.SearchAsync(
                new StreamSearchRequest(query, SelectedQuality, 10),
                Settings,
                cancellationToken);
            return result.Channels
                .Select(channel => new StreamCandidateProbe(
                    channel.Target,
                    new StreamlinkProbeResult(channel.CanPlay, channel.StatusMessage),
                    Channel: channel,
                    ViewerCount: channel.ViewerCount))
                .ToArray();
        }

        var candidates = StreamInputParser.ParseCandidates(query);
        if (candidates.Count == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(Settings.StreamlinkPath))
        {
            throw new InvalidOperationException("Configure the Streamlink executable path in Settings.");
        }

        var message = candidates.Count == 1
            ? $"Searching {candidates[0].DisplayName}"
            : $"Searching Twitch and Kick for {candidates[0].Channel}";
        StatusMessage = message;
        StreamSearchStatus = message;
        var customArguments = CommandLineTokenizer.Tokenize(Settings.CustomStreamlinkArguments);
        return await ProbeCandidatesAsync(
            candidates,
            customArguments,
            cancellationToken);
    }

    private async Task<IReadOnlyList<StreamCandidateProbe>> LoadStreamSearchResultMetadataAsync(
        IReadOnlyList<StreamCandidateProbe> probes,
        CancellationToken cancellationToken)
    {
        if (streamMetadataService is null || probes.Count == 0)
        {
            return probes;
        }

        return await Task.WhenAll(probes.Select(probe => LoadStreamSearchResultMetadataAsync(
            probe,
            cancellationToken)));
    }

    private async Task<StreamCandidateProbe> LoadStreamSearchResultMetadataAsync(
        StreamCandidateProbe probe,
        CancellationToken cancellationToken)
    {
        if (probe.Channel is not null || probe.Target.Kind != StreamTargetKind.Live)
        {
            return probe;
        }

        var metadataService = streamMetadataService;
        if (metadataService is null)
        {
            return probe;
        }

        try
        {
            var metadata = await metadataService.GetLiveStreamMetadataAsync(
                probe.Target,
                Settings,
                cancellationToken);
            return metadata.State == StreamMetadataState.Available
                ? probe with { Metadata = metadata }
                : probe;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Failed to load metadata for {probe.Target.DisplayName}.", ex);
            return probe;
        }
    }

    private async Task<IReadOnlyList<StreamCandidateProbe>> LoadStreamSearchResultViewerCountsAsync(
        IReadOnlyList<StreamCandidateProbe> probes,
        CancellationToken cancellationToken)
    {
        if (viewerCountService is null ||
            !probes.Any(probe => IsLiveStreamSearchProbe(probe) && probe.Target.Kind == StreamTargetKind.Live && probe.ViewerCount is null))
        {
            return probes;
        }

        using var throttle = new SemaphoreSlim(StreamSearchViewerCountConcurrency);
        var tasks = probes.Select(async probe =>
        {
            if (!IsLiveStreamSearchProbe(probe) || probe.Target.Kind != StreamTargetKind.Live || probe.ViewerCount is not null)
            {
                return probe;
            }

            await throttle.WaitAsync(cancellationToken);
            try
            {
                return await LoadStreamSearchResultViewerCountAsync(probe, cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    private void ReplaceStreamSearchResults(IReadOnlyList<StreamCandidateProbe> probes)
    {
        StreamSearchResults.Clear();
        foreach (var probe in probes)
        {
            StreamSearchResults.Add(probe.Channel is { } channel
                ? new StreamSearchResultViewModel(channel, OpenSearchResultAsync, probe.ViewerCount)
                : new StreamSearchResultViewModel(
                    probe.Target,
                    probe.Result,
                    probe.Metadata,
                    OpenSearchResultAsync,
                    probe.ViewerCount));
        }
    }

    private void UpdateStreamSearchViewerCounts(IReadOnlyList<StreamCandidateProbe> probes)
    {
        // Enrichment only changes counts and ordering. Keep the existing rows so
        // WPF retains their controls, focus, and any in-flight Open command.
        for (var index = 0; index < probes.Count; index++)
        {
            var probe = probes[index];
            var identity = probe.Target.TabIdentityKey;
            for (var currentIndex = index; currentIndex < StreamSearchResults.Count; currentIndex++)
            {
                var result = StreamSearchResults[currentIndex];
                if (!string.Equals(result.Target.TabIdentityKey, identity, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.UpdateViewerCount(probe.ViewerCount);
                if (currentIndex != index)
                {
                    StreamSearchResults.Move(currentIndex, index);
                }
                break;
            }
        }
    }

    private async Task<StreamCandidateProbe> LoadStreamSearchResultViewerCountAsync(
        StreamCandidateProbe probe,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await viewerCountService!.GetViewerCountAsync(
                probe.Target,
                Settings,
                cancellationToken);
            return result.State == ViewerCountState.Available && result.ViewerCount is { } viewerCount
                ? probe with { ViewerCount = Math.Max(0, viewerCount) }
                : probe;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Failed to load viewer count for {probe.Target.DisplayName}.", ex);
            return probe;
        }
    }

    private async Task OpenSearchResultAsync(StreamSearchResultViewModel result, bool stayOnHome)
    {
        try
        {
            if (result.IsOffline)
            {
                await OpenOfflineSearchResultVodsAsync(result);
                return;
            }

            if (!result.CanPlay)
            {
                StatusMessage = $"{result.PlatformText}: {result.StatusText}";
                return;
            }

            if (!stayOnHome)
            {
                IsHomeSelected = false;
            }

            SetRecentStreamHint(result.Target, result.ThumbnailUrl, result.DisplayName, result.CategoryName);
            await OpenCandidatesAsync([result.Target], clearInputOnSuccess: true, selectOpenedTab: !stayOnHome);
        }
        catch (Exception ex)
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            RecordNavigation();
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Search", $"Failed to open {result.Target.DisplayName} from search results.", ex);
        }
    }

    private async Task OpenOfflineSearchResultVodsAsync(StreamSearchResultViewModel result)
    {
        IsHomeSelected = true;
        ShowTwitchVodsHomePage();
        SelectVodPlatform(result.Platform);
        TwitchVodSearchText = result.Channel;
        CancelTwitchVodSearchDebounce();
        SetStreamSearchDropdownOpen(false);
        await SearchTwitchVodsAsync(reset: true);
    }

    private bool IsCurrentStreamSearch(int searchGeneration, string query)
    {
        return !disposed &&
            streamSearchController.IsCurrent(
                searchGeneration,
                query,
                () => NewStreamText,
                () => disposed);
    }

    private void ClearStreamSearchResults()
    {
        StreamSearchResults.Clear();
        StreamSearchStatus = "";
        SetStreamSearchCompleted(false);
        if (!HasNewStreamSearchText)
        {
            SetStreamSearchDropdownOpen(false);
        }
    }

    private void ScheduleAutomaticStreamSearch()
    {
        if (disposed)
        {
            return;
        }

        var query = NewStreamText.Trim();
        var searchGeneration = streamSearchController.CurrentGeneration;
        if (string.IsNullOrWhiteSpace(query))
        {
            SetStreamSearchDropdownOpen(false);
            return;
        }

        if (streamSearchDebounceInterval <= TimeSpan.Zero)
        {
            dispatch(() => _ = RunAutomaticStreamSearchAsync(query, searchGeneration));
            return;
        }

        streamSearchController.Schedule(
            streamSearchDebounceInterval,
            () => dispatch(() => _ = RunAutomaticStreamSearchAsync(query, searchGeneration)),
            ReportDebouncedCallbackFailure);
    }

    private void ReportDebouncedCallbackFailure(Exception exception)
    {
        logger.Write(AppLogLevel.Warning, "UI", "A debounced UI operation could not be dispatched.", exception);
    }

    private async Task RunAutomaticStreamSearchAsync(string query, int searchGeneration)
    {
        if (disposed || !IsCurrentStreamSearch(searchGeneration, query))
        {
            return;
        }

        await StartStreamSearchAsync(query, searchGeneration);
    }

    private void CancelStreamSearchDebounce()
    {
        streamSearchController.CancelScheduled();
    }

    private CancellationTokenSource ReplaceStreamSearchCancellation()
    {
        return streamSearchController.BeginOperation(lifetimeCancellation.Token);
    }

    private void CancelActiveStreamSearch()
    {
        streamSearchController.CancelActive();
    }

    private void DisposeStreamSearchCancellation(CancellationTokenSource cancellation)
    {
        streamSearchController.Complete(cancellation);
    }

    private void SetStreamSearchDropdownOpen(bool value)
    {
        SetProperty(ref isStreamSearchDropdownOpen, value, nameof(IsStreamSearchPanelVisible));
    }

    private void SetStreamSearchCompleted(bool value)
    {
        if (hasStreamSearchCompleted == value)
        {
            return;
        }

        hasStreamSearchCompleted = value;
        OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
        OnPropertyChanged(nameof(IsStreamSearchEmptyVisible));
    }

    private static string FormatStreamSearchResult(string query, IReadOnlyList<StreamCandidateProbe> probes)
    {
        if (probes.Count == 0)
        {
            return $"No Twitch or Kick channels found for {query}.";
        }

        var live = probes.Count(IsLiveStreamSearchProbe);
        var offline = probes.Count(probe =>
            !IsLiveStreamSearchProbe(probe) &&
            probe.Channel?.State == StreamSearchChannelState.Offline);
        var unavailable = probes.Count - live - offline;
        return StreamSearchSummary.Format(query, live, offline, unavailable);
    }

    private static IReadOnlyList<StreamCandidateProbe> OrderStreamSearchProbesForDisplay(
        IReadOnlyList<StreamCandidateProbe> probes)
    {
        return probes
            .Select((probe, index) => new { Probe = probe, Index = index })
            .OrderBy(item => IsLiveStreamSearchProbe(item.Probe) ? 0 : 1)
            .ThenBy(item => item.Probe.ViewerCount is null ? 1 : 0)
            .ThenByDescending(item => item.Probe.ViewerCount ?? 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Probe)
            .ToArray();
    }

    private static bool IsLiveStreamSearchProbe(StreamCandidateProbe probe)
    {
        return probe.Channel?.IsLive ?? probe.Result.HasPlayableStream;
    }

    private async Task OpenCandidatesAsync(
        IReadOnlyList<StreamTarget> parsedCandidates,
        bool clearInputOnSuccess,
        bool selectOpenedTab = true)
    {
        if (disposed)
        {
            return;
        }

        var candidates = await ResolvePlayableCandidatesAsync(parsedCandidates);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No stream target was provided.");
        }

        await OpenStreamAsync(candidates[0], clearInputOnSuccess, selectOpenedTab);
    }

    private Task<StreamMetadataResult?>? LoadOpeningTabMetadataInBackground(StreamTabViewModel tab)
    {
        var target = tab.Target;
        if (target.Kind != StreamTargetKind.Live ||
            (!string.IsNullOrWhiteSpace(target.CategoryName) &&
                !string.IsNullOrWhiteSpace(target.ProfileImageUrl)) ||
            streamMetadataService is null)
        {
            return null;
        }

        var operation = LoadOpeningTabMetadataAsync(tab);
        backgroundOperationController.Track(operation);
        return operation;
    }

    private async Task<StreamMetadataResult?> LoadOpeningTabMetadataAsync(StreamTabViewModel tab)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token, tab.LifetimeToken);
        try
        {
            var metadata = await streamMetadataService!.GetLiveStreamMetadataAsync(
                tab.Target,
                Settings,
                cancellation.Token);
            if (disposed || cancellation.IsCancellationRequested || !Tabs.Contains(tab))
            {
                return null;
            }

            tab.ApplyOpeningMetadata(metadata);
            return metadata;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "UI", $"Failed to load metadata for {tab.Target.DisplayName}.", ex);
            return null;
        }
    }

    private bool TryOpenExistingTab(StreamTarget target, bool selectOpenedTab, int? searchGenerationToClear)
    {
        var tab = FindTab(target);
        if (tab is null)
        {
            return false;
        }

        tab.SetProfileImageUrl(target.ProfileImageUrl);
        if (selectOpenedTab)
        {
            SelectedTab = tab;
            SelectedQuality = tab.Quality;
        }

        // Selection policy resumes automatically hidden tabs; manual pause stays intact.
        if (IsTabOpenOrStarting(tab) || tab.Status == PlaybackStatus.Paused)
        {
            StatusMessage = $"{tab.Target.DisplayName} already open";
            ClearStreamSearchAfterOpen(searchGenerationToClear);
        }
        else
        {
            StatusMessage = $"Starting {tab.Target.DisplayName}";
            StartTabInBackground(tab, searchGenerationToClear);
        }

        return true;
    }

    private void ClearStreamSearchAfterOpen(int? searchGenerationToClear)
    {
        if (searchGenerationToClear == streamSearchController.CurrentGeneration)
        {
            NewStreamText = "";
        }
    }

    private StreamTabViewModel CreateAndSelectTab(StreamTarget target)
    {
        var tab = CreateTab(target);
        SelectedTab = tab;
        return tab;
    }

    private StreamTabViewModel CreateTab(StreamTarget target)
    {
        var tab = new StreamTabViewModel(new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = SelectedQuality,
            StreamlinkService = streamlinkService,
            PlaybackFactory = playbackFactory,
            ChatFactory = chatFactory,
            Logger = logger,
            Dispatch = dispatch,
            InitialVolume = GetSavedStreamVolume(target),
            ViewerCountService = viewerCountService,
            ReplayResolver = replayResolver,
            VodChatProvider = vodChatProvider,
            VodPlaybackHistory = vodPlaybackHistory,
            TwitchSubOnlyVodResolver = twitchSubOnlyVodResolver
        });
        Tabs.Add(tab);
        return tab;
    }

    private StreamTabViewModel? FindTab(StreamTarget target)
    {
        return Tabs.FirstOrDefault(tab =>
            string.Equals(tab.Target.TabIdentityKey, target.TabIdentityKey, StringComparison.OrdinalIgnoreCase));
    }

    private int GetSavedStreamVolume(StreamTarget target)
    {
        return Settings.StreamVolumes.TryGetValue(target.StateKey, out var savedVolume)
            ? StreamTabViewModel.NormalizeVolume(savedVolume)
            : StreamTabViewModel.DefaultVolume;
    }

    private void ApplySavedStreamVolume(StreamTabViewModel tab)
    {
        if (Settings.StreamVolumes.TryGetValue(tab.Target.StateKey, out var savedVolume))
        {
            tab.Volume = savedVolume;
        }
    }

    private void RememberStreamVolume(StreamTabViewModel tab)
    {
        Settings.StreamVolumes[tab.Target.StateKey] = tab.Volume;
    }

    private double GetSavedStreamVlcOverlayFontSize(StreamTarget target)
    {
        return Settings.StreamVlcOverlayFontSizes.TryGetValue(target.StateKey, out var savedFontSize)
            ? ChatSettings.NormalizeFontSize(savedFontSize, Settings.Chat.VlcOverlayFontSize)
            : Settings.Chat.VlcOverlayFontSize;
    }

    private void SetRecentStreamHint(
        StreamTarget target,
        string thumbnailUrl,
        string displayName,
        string categoryName)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl) &&
            string.IsNullOrWhiteSpace(displayName) &&
            string.IsNullOrWhiteSpace(categoryName))
        {
            return;
        }

        recentStreamController.SetHint(
            target.StateKey,
            new RecentStreamHint(
                thumbnailUrl?.Trim() ?? "",
                displayName?.Trim() ?? "",
                categoryName?.Trim() ?? ""));
    }

    private RecentStreamHint? TakeRecentStreamHint(StreamTarget target)
    {
        return recentStreamController.TakeHint(target.StateKey);
    }

    private void EnsureFollowedChannelsRefreshTimerStarted()
    {
        if (followedStreamsService is null ||
            followedChannelsRefreshInterval <= TimeSpan.Zero ||
            disposed)
        {
            return;
        }

        lock (followedChannelsRefreshTimerGate)
        {
            if (followedChannelsRefreshTimer is not null || disposed)
            {
                return;
            }

            followedChannelsRefreshTimer = new System.Threading.Timer(
                _ => RefreshFollowedChannelsOnUi(),
                null,
                followedChannelsRefreshInterval,
                followedChannelsRefreshInterval);
        }
    }

    private void RefreshFollowedChannelsOnUi()
    {
        if (disposed || followedStreamsService is null || followedChannelsRefreshCancellation.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref followedChannelsAutomaticRefreshActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            dispatch(() =>
            {
                if (disposed ||
                    followedStreamsService is null ||
                    followedChannelsRefreshCancellation.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref followedChannelsAutomaticRefreshActive, 0);
                    return;
                }

                var refreshTask = RefreshFollowedChannelsAsync();
                backgroundOperationController.Track(ReleaseFollowedChannelsAutomaticRefreshAfterAsync(refreshTask));
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref followedChannelsAutomaticRefreshActive, 0);
            logger.Write(AppLogLevel.Warning, "Followed", "Failed to schedule live followed channels refresh.", ex);
        }
    }

    private async Task ReleaseFollowedChannelsAutomaticRefreshAfterAsync(Task refreshTask)
    {
        try
        {
            await refreshTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (followedChannelsRefreshCancellation.IsCancellationRequested || disposed)
        {
        }
        finally
        {
            Interlocked.Exchange(ref followedChannelsAutomaticRefreshActive, 0);
        }
    }

    private void EnsureRecentThumbnailRefreshTimerStarted()
    {
        if (streamMetadataService is null ||
            recentThumbnailRefreshInterval <= TimeSpan.Zero ||
            disposed)
        {
            return;
        }

        lock (recentThumbnailRefreshTimerGate)
        {
            if (recentThumbnailRefreshTimer is not null || disposed)
            {
                return;
            }

            recentThumbnailRefreshTimer = new System.Threading.Timer(
                _ => RefreshRecentThumbnailsOnUiIfVisible(),
                null,
                recentThumbnailRefreshInterval,
                recentThumbnailRefreshInterval);
        }
    }

    private void RefreshRecentThumbnailsOnUiIfVisible()
    {
        if (disposed || streamMetadataService is null)
        {
            return;
        }

        dispatch(() =>
        {
            if (disposed || !IsHomeVisible || !IsRecentHomePageVisible)
            {
                return;
            }

            // Navigation can reuse fresh results; periodic polling retains its cadence.
            backgroundOperationController.Track(RefreshRecentThumbnailsAsync(recentThumbnailRefreshCancellation.Token, force: true));
        });
    }

    private void RefreshRecentThumbnailsInBackground()
    {
        if (disposed || streamMetadataService is null)
        {
            return;
        }

        backgroundOperationController.Track(RefreshRecentThumbnailsAsync(recentThumbnailRefreshCancellation.Token));
    }

    private async Task RefreshRecentThumbnailsAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (streamMetadataService is null || Settings.RecentStreams.Count == 0)
        {
            return;
        }

        try
        {
            if (!await recentThumbnailRefreshGate.WaitAsync(0, cancellationToken))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = Settings.RecentStreams
                .Select(stream => new StreamTarget(stream.Platform, stream.Channel, stream.Url, CategoryName: stream.CategoryName))
                .DistinctBy(target => target.StateKey, StringComparer.OrdinalIgnoreCase)
                .Where(target => force || !recentStreamController.IsMetadataFresh(target.StateKey, now, recentThumbnailRefreshInterval))
                .ToArray();
            if (snapshot.Length == 0)
            {
                return;
            }

            var metadataByStream = new Dictionary<string, StreamMetadataResult>(StringComparer.OrdinalIgnoreCase);
            pendingRecentMetadata = metadataByStream;
            await MarkRecentStreamsCheckingAsync(snapshot, cancellationToken);

            // A fixed worker count bounds both provider requests and queued tasks
            // even when the user's Recent history has grown large.
            var nextIndex = -1;
            await Task.WhenAll(Enumerable.Range(0, Math.Min(RecentMetadataConcurrency, snapshot.Length)).Select(async _ =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= snapshot.Length)
                    {
                        return;
                    }

                    var metadata = await GetRecentStreamMetadataAsync(snapshot[index], cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (metadata is not null)
                    {
                        await ApplyRecentStreamMetadataAsync(snapshot[index], metadata, cancellationToken);
                    }
                }
            }));
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || metadataByStream.Count == 0)
            {
                return;
            }

            await recentStreamsGate.WaitAsync(cancellationToken);
            try
            {
                if (disposed) return;
                var settingsChanged = false;
                var updated = new List<RecentStreamSettings>(Settings.RecentStreams.Count);
                foreach (var stream in Settings.RecentStreams)
                {
                    var key = new StreamTarget(stream.Platform, stream.Channel, stream.Url).StateKey;
                    var next = metadataByStream.TryGetValue(key, out var metadata)
                        ? MergeRecentStreamMetadata(stream, metadata) : stream;
                    updated.Add(next);
                    settingsChanged |= !ReferenceEquals(stream, next);
                }
                // Publish a new settings list once, keeping a concurrent save's snapshot
                // stable while individual results are already visible in the cards.
                if (settingsChanged)
                {
                    Settings.RecentStreams = updated;
                    RebuildRecentStreams();
                    await SaveRecentThumbnailSettingsAsync(cancellationToken);
                }
            }
            finally
            {
                recentStreamsGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", "Failed to refresh recent stream thumbnails.", ex);
        }
        finally
        {
            pendingRecentMetadata = null;
            recentThumbnailRefreshGate.Release();
        }
    }

    private async Task MarkRecentStreamsCheckingAsync(
        IReadOnlyList<StreamTarget> targets,
        CancellationToken cancellationToken)
    {
        await recentStreamsGate.WaitAsync(cancellationToken);
        try
        {
            var changed = false;
            var status = new RecentStreamLiveStatus(
                RecentStreamLiveState.Checking,
                null,
                "Checking live status from the platform.");

            foreach (var target in targets)
            {
                if (!recentStreamController.TryGetLiveStatus(target.StateKey, out var current) ||
                    current.State != RecentStreamLiveState.Checking)
                {
                    changed |= recentStreamController.SetLiveStatus(target.StateKey, status);
                }
            }

            if (changed)
            {
                RebuildRecentStreams();
            }
        }
        finally
        {
            recentStreamsGate.Release();
        }
    }

    private async Task ApplyRecentStreamMetadataAsync(
        StreamTarget target,
        StreamMetadataResult metadata,
        CancellationToken cancellationToken)
    {
        await recentStreamsGate.WaitAsync(cancellationToken);
        try
        {
            if (disposed)
            {
                return;
            }

            // A request can finish after its row was deleted. It must not restore either
            // the row or its transient freshness/status entry.
            var current = FindRecentStream(target);
            if (current is null) return;
            pendingRecentMetadata![target.StateKey] = metadata;
            var checkedAtUtc = DateTimeOffset.UtcNow;
            var status = CreateRecentStreamLiveStatus(metadata, checkedAtUtc);
            recentStreamController.SetLiveStatus(target.StateKey, status);
            recentStreamController.RecordMetadataRefresh(target.StateKey, checkedAtUtc,
                metadata.State is StreamMetadataState.Available or StreamMetadataState.Offline);
            foreach (var card in RecentStreams)
            {
                if (card.Platform == target.Platform &&
                    string.Equals(card.Channel, target.Channel, StringComparison.OrdinalIgnoreCase))
                    card.Update(MergeRecentStreamMetadata(current, metadata), status);
            }
        }
        finally { recentStreamsGate.Release(); }
    }

    private static RecentStreamSettings MergeRecentStreamMetadata(RecentStreamSettings stream, StreamMetadataResult metadata)
    {
        if (metadata.State != StreamMetadataState.Available) return stream;
        var displayName = FirstNonEmpty(metadata.DisplayName, stream.DisplayName, stream.Channel);
        var thumbnailUrl = NormalizeImageUrl(FirstNonEmpty(metadata.ThumbnailUrl, stream.ThumbnailUrl));
        var categoryName = FirstNonEmpty(metadata.CategoryName, stream.CategoryName);
        if (displayName == stream.DisplayName && thumbnailUrl == stream.ThumbnailUrl && categoryName == stream.CategoryName)
            return stream;

        // Merge into the latest settings, preserving watch order, quality and timestamps
        // that playback or a user action may have changed while the request was running.
        return new RecentStreamSettings
        {
            Platform = stream.Platform,
            Channel = stream.Channel,
            Url = stream.Url,
            DisplayName = displayName,
            CategoryName = categoryName,
            ThumbnailUrl = thumbnailUrl,
            LastQuality = stream.LastQuality,
            LastWatchedAtUtc = stream.LastWatchedAtUtc
        };
    }

    private static RecentStreamLiveStatus CreateRecentStreamLiveStatus(
        StreamMetadataResult metadata,
        DateTimeOffset checkedAtUtc)
    {
        return metadata.State switch
        {
            StreamMetadataState.Available => new RecentStreamLiveStatus(
                RecentStreamLiveState.Live,
                checkedAtUtc,
                FirstNonEmpty(metadata.Message, "The platform reports this stream is live.")),
            StreamMetadataState.Offline => new RecentStreamLiveStatus(
                RecentStreamLiveState.Offline,
                checkedAtUtc,
                FirstNonEmpty(metadata.Message, "The platform reports this stream is offline.")),
            _ => new RecentStreamLiveStatus(
                RecentStreamLiveState.Unknown,
                checkedAtUtc,
                FirstNonEmpty(metadata.Message, "The platform did not return a usable live status."))
        };
    }

    private async Task RememberRecentStreamAsync(
        StreamTabViewModel tab,
        Task<StreamMetadataResult?>? openingMetadata = null)
    {
        if (disposed)
        {
            return;
        }

        var target = tab.Target;
        try
        {
            var hint = TakeRecentStreamHint(target);
            var watchedAtUtc = DateTimeOffset.UtcNow;
            var needsMetadata = false;

            await recentStreamsGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                var existing = FindRecentStream(target);
                var recentStream = CreateRecentStreamSettings(
                    target,
                    tab.Quality,
                    watchedAtUtc,
                    hint,
                    existing,
                    metadata: null);

                Settings.RecentStreams = Settings.RecentStreams
                    .Where(stream => !IsSameRecentStream(stream, target))
                    .Prepend(recentStream)
                    .ToList();
                recentStreamController.SetLiveStatus(target.StateKey, new RecentStreamLiveStatus(
                    RecentStreamLiveState.Live,
                    watchedAtUtc,
                    "Playback started successfully."));
                RebuildRecentStreams();
                await SaveRecentStreamSettingsAsync(target);
                needsMetadata = openingMetadata is not null ||
                    (streamMetadataService is not null && string.IsNullOrWhiteSpace(recentStream.ThumbnailUrl));
            }
            finally
            {
                recentStreamsGate.Release();
            }

            if (!needsMetadata)
            {
                return;
            }

            // Reuse the opening lookup; playback and the initial Recent card never wait for it.
            var metadata = openingMetadata is not null
                ? await openingMetadata
                : await TryGetRecentStreamMetadataAsync(target, lifetimeCancellation.Token);
            if (disposed || metadata?.State != StreamMetadataState.Available ||
                (string.IsNullOrWhiteSpace(metadata.ThumbnailUrl) &&
                    string.IsNullOrWhiteSpace(metadata.DisplayName) &&
                    string.IsNullOrWhiteSpace(metadata.CategoryName)))
            {
                return;
            }

            await recentStreamsGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                var existing = FindRecentStream(target);
                if (existing is null)
                {
                    return;
                }

                var recentStream = CreateRecentStreamSettings(
                    target,
                    existing.LastQuality,
                    existing.LastWatchedAtUtc,
                    hint: null,
                    existing,
                    metadata);

                Settings.RecentStreams = Settings.RecentStreams
                    .Select(stream => IsSameRecentStream(stream, target) ? recentStream : stream)
                    .ToList();
                var checkedAtUtc = DateTimeOffset.UtcNow;
                recentStreamController.SetLiveStatus(target.StateKey, CreateRecentStreamLiveStatus(metadata, checkedAtUtc));
                recentStreamController.RecordMetadataRefresh(target.StateKey, checkedAtUtc, succeeded: true);
                RebuildRecentStreams();
                await SaveRecentStreamSettingsAsync(target);
            }
            finally
            {
                recentStreamsGate.Release();
            }
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to update recent stream {target.DisplayName}.", ex);
        }
    }

    private RecentStreamSettings? FindRecentStream(StreamTarget target)
    {
        return Settings.RecentStreams.FirstOrDefault(stream => IsSameRecentStream(stream, target));
    }

    private static RecentStreamSettings CreateRecentStreamSettings(
        StreamTarget target,
        string quality,
        DateTimeOffset lastWatchedAtUtc,
        RecentStreamHint? hint,
        RecentStreamSettings? existing,
        StreamMetadataResult? metadata)
    {
        return new RecentStreamSettings
        {
            Platform = target.Platform,
            Channel = target.Channel,
            Url = target.Url,
            DisplayName = FirstNonEmpty(
                hint?.DisplayName,
                metadata?.DisplayName,
                existing?.DisplayName,
                target.Channel),
            ThumbnailUrl = FirstNonEmpty(
                hint?.ThumbnailUrl,
                metadata?.ThumbnailUrl,
                existing?.ThumbnailUrl),
            CategoryName = FirstNonEmpty(
                target.CategoryName,
                hint?.CategoryName,
                metadata?.CategoryName,
                existing?.CategoryName),
            LastQuality = quality,
            LastWatchedAtUtc = lastWatchedAtUtc
        };
    }

    private async Task<StreamMetadataResult?> TryGetRecentStreamMetadataAsync(
        StreamTarget target,
        CancellationToken cancellationToken = default)
    {
        var result = await GetRecentStreamMetadataAsync(target, cancellationToken);
        return result?.State == StreamMetadataState.Available ? result : null;
    }

    private async Task<StreamMetadataResult?> GetRecentStreamMetadataAsync(
        StreamTarget target,
        CancellationToken cancellationToken = default)
    {
        if (streamMetadataService is null)
        {
            return null;
        }

        try
        {
            return await streamMetadataService.GetLiveStreamMetadataAsync(target, Settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to load metadata for {target.DisplayName}.", ex);
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "The platform metadata request failed.");
        }
    }

    private async Task SaveRecentThumbnailSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await settingsService.SaveAsync(Settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", "Failed to save refreshed recent stream thumbnails.", ex);
        }
    }

    private async Task SaveRecentStreamRemovalAsync(StreamTarget target)
    {
        try
        {
            await settingsService.SaveAsync(Settings);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to save recent stream removal for {target.DisplayName}.", ex);
        }
    }

    private async Task SaveRecentStreamSettingsAsync(StreamTarget target)
    {
        try
        {
            await settingsService.SaveAsync(Settings);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to save recent stream {target.DisplayName}.", ex);
        }
    }

    private static bool IsSameRecentStream(RecentStreamSettings stream, StreamTarget target)
    {
        return stream.Platform == target.Platform &&
            string.Equals(stream.Channel, target.Channel, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsTabOpenOrStarting(StreamTabViewModel tab)
    {
        return IsTabStartActive(tab) ||
            tab.Status is PlaybackStatus.Playing or PlaybackStatus.Resolving or PlaybackStatus.Starting;
    }

    private bool IsTabStartActive(StreamTabViewModel tab)
        => tabStartController.IsActive(tab.Id);

    private bool TryBeginTabStart(StreamTabViewModel tab)
        => !disposed && tabStartController.TryBegin(tab.Id);

    private void EndTabStart(StreamTabViewModel tab)
        => tabStartController.End(tab.Id);

    private void StartTabInBackground(
        StreamTabViewModel tab,
        int? searchGenerationToClear = null,
        Task<StreamMetadataResult?>? openingMetadata = null)
    {
        if (disposed || !TryBeginTabStart(tab))
        {
            return;
        }

        ApplyVideoLayout();
        var start = () => backgroundOperationController.Track(
            StartTabAndUpdateStatusAsync(tab, searchGenerationToClear, openingMetadata));
        try
        {
            if (tryDispatch is not null)
            {
                if (!tryDispatch(start))
                {
                    EndTabStart(tab);
                }
            }
            else
            {
                dispatch(start);
            }
        }
        catch
        {
            EndTabStart(tab);
            throw;
        }
    }

    private async Task StartTabAndUpdateStatusAsync(
        StreamTabViewModel tab,
        int? searchGenerationToClear,
        Task<StreamMetadataResult?>? openingMetadata)
    {
        if (disposed)
        {
            EndTabStart(tab);
            return;
        }

        await Task.Yield();
        try
        {
            await tabStartController.RunBegunAsync(
                tab.Id,
                async cancellationToken =>
                {
                    var startResult = await tab.StartWithResultAsync(
                        Settings,
                        ShouldUseStableMultiStreamStartupProfile(tab),
                        ShouldUseMultiStreamResourceProfile(tab),
                        cancellationToken);
                    if (disposed)
                    {
                        return;
                    }

                    if (startResult.Succeeded)
                    {
                        ClearStreamSearchAfterOpen(searchGenerationToClear);

                        StatusMessage = $"{tab.Target.DisplayName} playing";
                        if (tab.Target.Kind == StreamTargetKind.Live)
                        {
                            backgroundOperationController.Track(RememberRecentStreamAsync(tab, openingMetadata));
                        }
                    }
                    else
                    {
                        StatusMessage = $"{tab.Target.DisplayName}: {GetStartFailure(tab)}";
                    }
                },
                lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;

            logger.Write(AppLogLevel.Error, "UI", $"Failed to start {tab.Target.DisplayName}.", ex);
        }
        finally
        {
            ApplyVlcPluginMultiViewChatPolicyInBackground(restoreWhenAllowed: true);
        }
    }

    private bool ShouldUseStableMultiStreamStartupProfile(StreamTabViewModel tab)
    {
        if (tab.Target.Kind != StreamTargetKind.Live ||
            tab.IsDetached ||
            IsStreamOnlyFullscreenActive)
        {
            return false;
        }

        var visibleLiveTabs = GetVisibleVideoTabs()
            .Count(candidate => candidate.Target.Kind == StreamTargetKind.Live && !candidate.IsDetached);
        return visibleLiveTabs >= DenseMultiStreamStartupThreshold;
    }

    private bool ShouldUseMultiStreamResourceProfile(StreamTabViewModel tab)
    {
        if (tab.IsDetached || IsStreamOnlyFullscreenActive)
        {
            return false;
        }

        var visibleTabs = GetVisibleVideoTabs();
        return visibleTabs.Count > 1 && visibleTabs.Contains(tab);
    }

    private async Task<IReadOnlyList<StreamTarget>> ResolvePlayableCandidatesAsync(IReadOnlyList<StreamTarget> candidates)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        if (string.IsNullOrWhiteSpace(Settings.StreamlinkPath))
        {
            throw new InvalidOperationException("Configure the Streamlink executable path in Settings.");
        }

        StatusMessage = $"Checking Twitch and Kick for {candidates[0].Channel}";
        var customArguments = CommandLineTokenizer.Tokenize(Settings.CustomStreamlinkArguments);
        var probes = await ProbeCandidatesAsync(
            candidates,
            customArguments,
            CancellationToken.None);
        var playableProbes = probes
            .Where(probe => probe.Result.HasPlayableStream)
            .ToArray();
        if (playableProbes.Length == 1)
        {
            return [playableProbes[0].Target];
        }

        if (playableProbes.Length > 1)
        {
            throw new InvalidOperationException(
                $"Both Twitch and Kick have playable streams for {candidates[0].Channel}. Enter a Twitch or Kick URL to choose a platform.");
        }

        var failures = string.Join(
            " | ",
            probes.Select(probe => $"{probe.Target.Platform}: {probe.Result.Message}"));
        throw new InvalidOperationException($"No playable Twitch or Kick stream found for {candidates[0].Channel}. {failures}");
    }

    private async Task<IReadOnlyList<StreamCandidateProbe>> ProbeCandidatesAsync(
        IReadOnlyList<StreamTarget> candidates,
        IReadOnlyList<string> customArguments,
        CancellationToken cancellationToken)
    {
        return await Task.WhenAll(candidates.Select(target => ProbeCandidateAsync(
            target,
            customArguments,
            cancellationToken)));
    }

    private async Task<StreamCandidateProbe> ProbeCandidateAsync(
        StreamTarget target,
        IReadOnlyList<string> customArguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new StreamTransportRequest(
                target,
                SelectedQuality,
                Settings.StreamlinkPath!,
                Settings.LowLatency,
                customArguments);
            var result = await streamlinkService.ProbeStreamsAsync(request, cancellationToken);
            return new StreamCandidateProbe(target, result);
        }
        catch (OperationCanceledException)
        {
            return new StreamCandidateProbe(target, new StreamlinkProbeResult(false, "Canceled."));
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Streamlink probe failed for {target.DisplayName}.", ex);
            return new StreamCandidateProbe(target, new StreamlinkProbeResult(false, ex.Message));
        }
    }

    private Task StartSelectedTabAsync(string action)
    {
        if (SelectedTab is { } tab)
        {
            StatusMessage = $"{action} {tab.Target.DisplayName}";
            StartTabInBackground(tab);
        }

        return Task.CompletedTask;
    }

    private bool CanCreateClip()
    {
        return twitchClipService is not null &&
            SelectedTab?.Target is { Platform: PlatformKind.Twitch, Kind: StreamTargetKind.Live };
    }

    private async Task CreateClipAsync()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        if (tab.Target.Platform == PlatformKind.Kick)
        {
            StatusMessage = "Kick clipping is disabled because no official clip API is available.";
            return;
        }

        if (tab.Target.Kind != StreamTargetKind.Live)
        {
            StatusMessage = "Twitch clips are available for live tabs only.";
            return;
        }

        if (twitchClipService is null)
        {
            StatusMessage = "Twitch clip service is unavailable.";
            return;
        }

        var cancellationToken = lifetimeCancellation.Token;
        try
        {
            StatusMessage = $"Creating Twitch clip for {tab.Target.DisplayName}";
            var result = await twitchClipService.CreateLiveClipAsync(tab.Target, Settings.Chat, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                openBrowser(result.ClipUri);
                StatusMessage = $"Twitch clip opened for {tab.Target.DisplayName}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Twitch clip created, but the browser could not be opened: {result.ClipUri}";
                logger.Write(AppLogLevel.Warning, "TwitchClip", "Twitch clip was created but could not be opened in the browser.", ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown must not open a browser or publish a late status update.
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Twitch clip creation was cancelled.";
        }
        catch (Exception ex) when (!disposed)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "TwitchClip", "Twitch clip creation failed.", ex);
        }
    }

    private async Task StopSelectedAsync()
    {
        if (SelectedTab is { } tab)
        {
            await tab.StopAsync();
            if (!disposed && ReferenceEquals(SelectedTab, tab))
                StatusMessage = $"{tab.Target.DisplayName} stopped";
        }
    }

    private async Task PauseSelectedAsync()
    {
        if (SelectedTab is { } tab)
        {
            await tab.PauseOrResumeAsync();
            if (disposed) return;
            if (ReferenceEquals(SelectedTab, tab))
                StatusMessage = $"{tab.Target.DisplayName}: {tab.StatusText}";
            ApplyVlcPluginMultiViewChatPolicyInBackground();
        }
    }

    private async Task ToggleChatAsync()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        var showChat = !tab.IsChatVisible;
        if (Settings.Chat.Layout == ChatLayout.Docked)
        {
            var showDockedChat = !tab.IsChatVisible || !tab.IsDockedChatPanelVisible;
            tab.IsDockedChatPanelVisible = showDockedChat;
            if (showDockedChat && !tab.IsChatVisible)
            {
                tab.SetChatVisibleForDeferredLifecycle(true);
                RaiseChatVisibilityProperties();
                StatusMessage = $"{tab.Target.DisplayName}: docked chat shown";
                await RestartTabChatAsync(tab);
                return;
            }

            RaiseChatVisibilityProperties();
            StatusMessage = $"{tab.Target.DisplayName}: docked chat {(showDockedChat ? "shown" : "hidden")}";
            return;
        }

        if (IsVlcPluginOverlayMode(Settings.Chat))
        {
            var targetTabs = GetChatToggleTargetTabs(tab).ToArray();
            showChat = ShouldDisableVlcPluginMultiViewChats(targetTabs) ? false : showChat;
            var changedTabs = targetTabs
                .Where(targetTab => targetTab.SetChatVisibleForDeferredLifecycle(showChat))
                .ToArray();
            if (changedTabs.Length == 0)
            {
                return;
            }

            RaiseChatVisibilityProperties();
            StatusMessage = changedTabs.Length == 1
                ? $"{changedTabs[0].Target.DisplayName}: {(showChat ? "showing" : "hiding")} chat"
                : $"{changedTabs.Length} streams: {(showChat ? "showing" : "hiding")} chat";

            await ReconfigureVlcPluginChatTabsWithGateAsync(changedTabs);

            StatusMessage = changedTabs.Length == 1
                ? $"{changedTabs[0].Target.DisplayName}: chat {(showChat ? "shown" : "hidden")}"
                : $"{changedTabs.Length} streams: chat {(showChat ? "shown" : "hidden")}";
            return;
        }

        tab.IsChatVisible = showChat;
    }

    public IReadOnlyList<StreamTabViewModel> GetTheatreModeChatTargetTabs()
    {
        if (SelectedTab is not { } tab)
        {
            return [];
        }

        return Settings.Chat.Layout == ChatLayout.Overlay
            ? GetChatToggleTargetTabs(tab).ToArray()
            : [tab];
    }

    public void ApplyTheatreModeDockedChat(IReadOnlyList<StreamTabViewModel> targetTabs)
    {
        ReleaseNativeOverlayChatInputFocus();

        var tabs = targetTabs
            .Distinct()
            .Where(Tabs.Contains)
            .ToArray();
        if (tabs.Length == 0)
        {
            return;
        }

        var tabsNeedingChatRestart = new List<StreamTabViewModel>();
        var useDockedOverride = Settings.Chat.Layout != ChatLayout.Docked;
        foreach (var tab in tabs)
        {
            if (useDockedOverride && tab.SetDockedChatOverrideActive(true))
            {
                tabsNeedingChatRestart.Add(tab);
            }

            if (tab.SetChatVisibleForDeferredLifecycle(true))
            {
                tabsNeedingChatRestart.Add(tab);
            }

            tab.IsDockedChatPanelVisible = true;
        }

        RaiseChatVisibilityProperties();
        StatusMessage = tabs.Length == 1
            ? $"{tabs[0].Target.DisplayName}: docked chat shown"
            : $"{tabs.Length} streams: docked chat shown";

        if (tabsNeedingChatRestart.Count > 0)
        {
            backgroundOperationController.Track(RestartTheatreModeChatTabsAsync(tabsNeedingChatRestart.Distinct().ToArray()));
        }
    }

    public void ClearTheatreModeDockedChatOverrides()
    {
        var changedTabs = Tabs
            .Where(tab => tab.SetDockedChatOverrideActive(false))
            .ToArray();
        if (changedTabs.Length == 0)
        {
            return;
        }

        RaiseChatVisibilityProperties();
        backgroundOperationController.Track(RestartTheatreModeChatTabsAsync(changedTabs));
    }

    public void ReleaseNativeOverlayChatInputFocus()
    {
        foreach (var tab in Tabs.ToArray())
        {
            tab.TryReleaseNativeOverlayChatInputFocus();
        }
    }

    private IReadOnlyList<StreamTabViewModel> GetChatToggleTargetTabs(StreamTabViewModel selected)
    {
        if (IsStreamOnlyFullscreenActive)
        {
            return [selected];
        }

        var visibleTabs = GetVisibleVideoTabs();
        return visibleTabs.Count > 1 && visibleTabs.Contains(selected) ? visibleTabs : [selected];
    }

    private bool ShouldDisableVlcPluginMultiViewChats(IReadOnlyList<StreamTabViewModel> targetTabs)
    {
        return GetVlcPluginMultiViewChatPolicyTabs(targetTabs, ResolveExplicitMultiViewGroup).Length > 0;
    }

    private StreamTabViewModel[] GetVlcPluginMultiViewChatPolicyTabs(
        IReadOnlyList<StreamTabViewModel> targetTabs,
        Func<StreamTabViewModel, IReadOnlyList<StreamTabViewModel>?> resolveMultiViewGroup)
    {
        if (!IsVlcPluginOverlayMode(Settings.Chat) || targetTabs.Count < VlcPluginMultiViewChatDisableThreshold)
        {
            return [];
        }

        foreach (var targetTab in targetTabs)
        {
            if (resolveMultiViewGroup(targetTab) is not { Count: >= VlcPluginMultiViewChatDisableThreshold } multiViewGroup)
            {
                continue;
            }

            var visibleGroupTabs = targetTabs
                .Where(multiViewGroup.Contains)
                .Distinct()
                .ToArray();
            if (visibleGroupTabs.Length < VlcPluginMultiViewChatDisableThreshold)
            {
                continue;
            }

            var pluginOverlayTabs = visibleGroupTabs
                .Where(tab => !tab.IsDockedChatOverrideActive)
                .ToArray();
            if (pluginOverlayTabs.Count(tab => tab.Status == PlaybackStatus.Playing) >= VlcPluginMultiViewChatDisableThreshold &&
                pluginOverlayTabs.Any(tab => tab.UsesNativeOverlay || vlcPluginMultiViewChatPolicyHiddenTabs.Contains(tab)))
            {
                return pluginOverlayTabs;
            }
        }

        return [];
    }

    private IReadOnlyList<StreamTabViewModel>? ResolveExplicitMultiViewGroup(StreamTabViewModel tab)
    {
        return GetMultiViewTabGroup(tab);
    }

    private StreamTabViewModel[] GetCurrentVlcPluginMultiViewChatPolicyTabs()
    {
        if (!IsVlcPluginOverlayMode(Settings.Chat))
        {
            return [];
        }

        var policyTabs = new List<StreamTabViewModel>();
        if (SelectedTab is not null)
        {
            policyTabs.AddRange(GetVlcPluginMultiViewChatPolicyTabs(
                GetChatToggleTargetTabs(SelectedTab),
                ResolveExplicitMultiViewGroup));
        }

        foreach (var visibleGroup in tabGroupingController.PictureInPictureVisibleGroups)
        {
            policyTabs.AddRange(GetVlcPluginMultiViewChatPolicyTabs(
                visibleGroup,
                tab => visibleGroup.Contains(tab) ? visibleGroup : null));
        }

        return policyTabs.Distinct().ToArray();
    }

    private Task CloseSelectedAsync()
    {
        if (SelectedTab is not null)
        {
            CloseTab(SelectedTab);
        }

        return Task.CompletedTask;
    }

    public bool CloseTab(StreamTabViewModel closing)
    {
        var index = Tabs.IndexOf(closing);
        if (index < 0)
        {
            return false;
        }

        var wasSelected = ReferenceEquals(SelectedTab, closing);
        if (wasSelected)
        {
            var replacement = index < Tabs.Count - 1
                ? Tabs[index + 1]
                : index > 0
                    ? Tabs[index - 1]
                    : null;

            suppressInactiveTabPause = true;
            try
            {
                SelectedTab = replacement;
                Tabs.RemoveAt(index);
            }
            finally
            {
                suppressInactiveTabPause = false;
            }
        }
        else
        {
            Tabs.RemoveAt(index);
        }

        RaiseCommandStates();

        DisposeDetachedTab(closing);
        StatusMessage = $"{closing.Target.DisplayName} closed";
        return true;
    }

    public bool CloseTabStripItem(TabStripItemViewModel item)
    {
        var closingTabs = item.Tabs
            .Where(Tabs.Contains)
            .Distinct()
            .ToArray();
        if (closingTabs.Length == 0)
        {
            return false;
        }

        if (closingTabs.Length == 1)
        {
            return CloseTab(closingTabs[0]);
        }

        return CloseTabs(closingTabs);
    }

    public bool CloseAllTabs()
    {
        return CloseTabs(Tabs.ToArray());
    }

    private bool CloseTabs(IReadOnlyList<StreamTabViewModel> closingTabs)
    {
        var closingSet = closingTabs
            .Where(Tabs.Contains)
            .Distinct()
            .ToHashSet();
        if (closingSet.Count == 0)
        {
            return false;
        }

        var firstClosingIndex = Tabs
            .Select((tab, index) => (tab, index))
            .Where(item => closingSet.Contains(item.tab))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .Min();
        if (firstClosingIndex < 0)
        {
            return false;
        }

        var wasSelected = SelectedTab is not null && closingSet.Contains(SelectedTab);
        var replacement = wasSelected
            ? Tabs
                .Skip(firstClosingIndex)
                .FirstOrDefault(tab => !closingSet.Contains(tab)) ??
              Tabs
                .Take(firstClosingIndex)
                .LastOrDefault(tab => !closingSet.Contains(tab))
            : SelectedTab;

        suppressInactiveTabPause = true;
        try
        {
            if (wasSelected)
            {
                SelectedTab = replacement;
            }

            foreach (var tab in Tabs.Where(closingSet.Contains).ToArray())
            {
                Tabs.Remove(tab);
            }
        }
        finally
        {
            suppressInactiveTabPause = false;
        }

        RaiseCommandStates();
        foreach (var tab in closingSet)
        {
            DisposeDetachedTab(tab);
        }

        StatusMessage = $"{closingSet.Count} streams closed";
        return true;
    }

    public bool SetTabsDetached(IReadOnlyCollection<StreamTabViewModel> tabs, bool detached)
    {
        var validTabs = tabs
            .Where(Tabs.Contains)
            .Distinct()
            .ToArray();
        if (validTabs.Length == 0)
        {
            return false;
        }

        if (!detached)
        {
            RemoveTabsFromPictureInPictureGroups(validTabs, applyLayout: false);
        }
        else
        {
            RemoveTabsFromMultiViewGroups(validTabs, applyLayout: false);
        }

        var changed = false;
        var selectedTabChanged = false;
        foreach (var tab in validTabs)
        {
            if (tab.IsDetached == detached)
            {
                continue;
            }

            changed = tab.SetDetached(detached) || changed;
            selectedTabChanged = selectedTabChanged || ReferenceEquals(SelectedTab, tab);
        }

        if (!changed)
        {
            return false;
        }

        if (selectedTabChanged)
        {
            OnPropertyChanged(nameof(IsSelectedTabDetached));
        }

        StatusMessage = GetTabDetachedStatusMessage(validTabs, detached);
        ApplyVideoLayout();
        ApplyInactivePlaybackPolicyInBackground();
        return true;
    }

    private void DisposeDetachedTab(StreamTabViewModel tab)
    {
        tab.CaptureVodResumePosition(closing: true);
        var disposalTask = Task.Run(() => DisposeDetachedTabAsync(tab));

        lock (detachedDisposalsGate)
        {
            detachedDisposals.Add(disposalTask);
        }

        _ = disposalTask.ContinueWith(
            completed =>
            {
                lock (detachedDisposalsGate)
                {
                    detachedDisposals.Remove(completed);
                }
            },
            TaskScheduler.Default);

        ObserveDetachedDisposal(disposalTask);

        _ = disposalTask.ContinueWith(
            completed =>
            {
                try
                {
                    dispatch(() => VideoTabs.Remove(tab));
                }
                catch (Exception ex)
                {
                    logger.Write(AppLogLevel.Warning, "UI", $"Failed to remove video surface for closed tab {tab.Target.DisplayName}.", ex);
                }
            },
            TaskScheduler.Default);
    }

    private void ObserveDetachedDisposal(Task disposalTask)
    {
        _ = disposalTask.ContinueWith(
            completed =>
            {
                if (completed.Exception is not null)
                {
                    logger.Write(
                        AppLogLevel.Warning,
                        "UI",
                        "Detached tab cleanup failed.",
                        completed.Exception.GetBaseException());
                }
            },
            TaskScheduler.Default);
    }

    private async Task DisposeDetachedTabAsync(StreamTabViewModel tab)
    {
        try
        {
            await tab.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "UI", $"Failed to dispose closed tab {tab.Target.DisplayName}.", ex);
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            Settings.DefaultQuality = SelectedQuality;
            Settings.FollowedChannels.KickChannelSlugs = ParseKickFollowedChannelSlugs(KickFollowedChannelsText);
            await settingsService.SaveAsync(Settings);
            await ApplyChatSettingsAsync(reconfigurePlayback: true);
            StatusMessage = "Settings saved";
            _ = RefreshFollowedChannelsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Settings", "Failed to save settings.", ex);
        }
    }

    private bool CanRunUpdateAction() => !disposed && Volatile.Read(ref updateActionInProgress) == 0 &&
        updateDownloadCancellation is null && appUpdateService is not null &&
        appUpdateService.State.Phase is not (AppUpdatePhase.Checking or AppUpdatePhase.Downloading or AppUpdatePhase.Verifying or AppUpdatePhase.Launching);

    private void RaiseUpdateActionCanExecuteChanged()
    {
        UpdateAppCommand.RaiseCanExecuteChanged();
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        LaterUpdateCommand.RaiseCanExecuteChanged();
    }

    private async Task UpdateAppAsync(bool checkOnly)
    {
        if (appUpdateService is null)
        {
            AppUpdateStatus = "App updater is not available in this build.";
            StatusMessage = AppUpdateStatus;
            return;
        }

        // Both manual commands share admission, including the settings save before
        // the service enters Checking or Launching and announces its busy state.
        if (Interlocked.CompareExchange(ref updateActionInProgress, 1, 0) != 0) return;
        try
        {
            RaiseUpdateActionCanExecuteChanged();
            if (Settings.Updates.SnoozedVersion.Length != 0 || Settings.Updates.SnoozedUntilUtc is not null)
            {
                Settings.Updates.SnoozedVersion = "";
                Settings.Updates.SnoozedUntilUtc = null;
                await settingsService.SaveAsync(Settings, lifetimeCancellation.Token);
            }
            if (!checkOnly && appUpdateService.State is { Phase: AppUpdatePhase.NotifyOnly, Release: { } notification })
            {
                openBrowser(notification.ReleasePage);
                return;
            }
            if (!checkOnly && appUpdateService.State is { Phase: AppUpdatePhase.Available or AppUpdatePhase.DownloadFailed, Release: { } release })
            {
                await DownloadUpdateAsync(release, automatic: false, lifetimeCancellation.Token);
                return;
            }
            if (!checkOnly && appUpdateService.State is { Phase: AppUpdatePhase.Ready, PreparedUpdate: { } prepared })
            {
                var launch = await appUpdateService.ApplyAndRestartAsync(prepared, lifetimeCancellation.Token);
                AppUpdateStatus = launch.Message;
                StatusMessage = launch.Message;
                logger.Write(AppLogLevel.Info, "Updater", launch.Message);
                if (launch.Started) requestShutdown?.Invoke();
                return;
            }
            AppUpdateStatus = "Checking the latest signed release…";
            var result = await appUpdateService.CheckAsync(UpdateCheckReason.Manual, lifetimeCancellation.Token);
            AppUpdateStatus = result.Message;
            StatusMessage = result.Message;
            logger.Write(AppLogLevel.Info, "Updater", result.Message);
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppUpdateStatus = $"Update failed. {ex.Message}";
            StatusMessage = AppUpdateStatus;
            logger.Write(AppLogLevel.Error, "Updater", "Application update failed.", ex);
        }
        finally
        {
            Volatile.Write(ref updateActionInProgress, 0);
            RaiseUpdateActionCanExecuteChanged();
        }
    }

    private void CancelUpdateDownload()
    {
        updateDownloadCanceledByUser = true;
        updateDownloadCancellation?.Cancel();
    }

    private async Task DownloadUpdateAsync(AppUpdateRelease release, bool automatic, CancellationToken token)
    {
        if (updateDownloadCancellation is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeCancellation.Token);
        var preferences = Settings.Updates;
        void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!preferences.AutomaticChecksEnabled || !preferences.AutomaticDownloadsEnabled)
                cancellation.Cancel();
        }
        if (automatic) preferences.PropertyChanged += PreferencesChanged;
        updateDownloadCanceledByUser = false;
        updateDownloadCancellation = cancellation;
        OnPropertyChanged(nameof(CanCancelUpdate));
        CancelUpdateCommand.RaiseCanExecuteChanged();
        RaiseUpdateActionCanExecuteChanged();
        try
        {
            await appUpdateService!.DownloadAsync(release, cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Only the explicit Cancel action pauses this version for the session.
            // A preference change must allow downloading again after re-enabling it.
            if (updateDownloadCanceledByUser) canceledAutomaticDownloadVersion = release.Version;
        }
        finally
        {
            if (automatic) preferences.PropertyChanged -= PreferencesChanged;
            updateDownloadCancellation = null;
            OnPropertyChanged(nameof(CanCancelUpdate));
            CancelUpdateCommand.RaiseCanExecuteChanged();
            RaiseUpdateActionCanExecuteChanged();
        }
    }

    internal Task PrepareAutomaticUpdateAsync(AppUpdateCheckResult result, CancellationToken token)
    {
        if (disposed || Volatile.Read(ref updateActionInProgress) != 0 ||
            !Settings.Updates.AutomaticChecksEnabled || !Settings.Updates.AutomaticDownloadsEnabled ||
            !result.IsUpdateAvailable || result.IsNotifyOnly || result.Release is not { } release ||
            result.InstallKind is not (AppInstallKind.Managed or AppInstallKind.LegacyManaged) ||
            release.Version == canceledAutomaticDownloadVersion ||
            Settings.Updates.IsSnoozed(release.Version, DateTimeOffset.UtcNow) ||
            appUpdateService?.State is not { Phase: AppUpdatePhase.Available or AppUpdatePhase.DownloadFailed })
            return Task.CompletedTask;

        return DownloadUpdateAsync(release, automatic: true, token);
    }

    private Task CheckForStartupUpdateAsync() => new AutomaticUpdateController(
        appUpdateService!,
        () => Settings.Updates.AutomaticChecksEnabled && Volatile.Read(ref updateActionInProgress) == 0,
        ApplySnooze,
        completion => dispatch(() => AppUpdateStatus = completion.Message),
        logger,
        prepareUpdate: PrepareAutomaticUpdateAsync).RunAsync(lifetimeCancellation.Token);

    private void OnAppUpdateStateChanged(object? sender, AppUpdateStateChangedEventArgs e)
    {
        dispatch(() =>
        {
            if (disposed) return;
            AppUpdateStatus = e.State.Message;
            OnPropertyChanged(nameof(IsUpdateRefreshVisible));
            AppUpdateActionText = e.State.Phase switch
            {
                AppUpdatePhase.Available => "Download update",
                AppUpdatePhase.DownloadFailed => "Retry download",
                AppUpdatePhase.Ready => "Restart and install",
                AppUpdatePhase.NotifyOnly => "Open release page",
                AppUpdatePhase.Downloading or AppUpdatePhase.Verifying or AppUpdatePhase.Launching => "Please wait…",
                _ => "Check for updates"
            };
            IsUpdateBannerVisible = e.State.Phase is AppUpdatePhase.Available or AppUpdatePhase.Ready or AppUpdatePhase.NotifyOnly or
                AppUpdatePhase.Downloading or AppUpdatePhase.Verifying or AppUpdatePhase.DownloadFailed ||
                (e.State.Phase == AppUpdatePhase.Failed && e.State.Release is not null);
            if (e.State.Phase is not (AppUpdatePhase.Downloading or AppUpdatePhase.Verifying) &&
                e.State.Release is { } release && Settings.Updates.IsSnoozed(release.Version, DateTimeOffset.UtcNow))
            {
                IsUpdateBannerVisible = false;
            }
            RaiseUpdateActionCanExecuteChanged();
        });
    }

    private void ApplySnooze(AppUpdateCheckResult result)
    {
        if (result.Release is { } release && Settings.Updates.IsSnoozed(release.Version, DateTimeOffset.UtcNow))
        {
            dispatch(() =>
            {
                IsUpdateBannerVisible = false;
                AppUpdateStatus = $"Version {release.Version} is snoozed until {Settings.Updates.SnoozedUntilUtc:g}.";
            });
        }
    }

    private async Task SnoozeUpdateAsync()
    {
        if (appUpdateService?.State.Release is not { } release)
        {
            return;
        }

        Settings.Updates.SnoozedVersion = release.Version.ToString(3);
        Settings.Updates.SnoozedUntilUtc = DateTimeOffset.UtcNow.AddHours(24);
        await settingsService.SaveAsync(Settings, lifetimeCancellation.Token);
        IsUpdateBannerVisible = false;
        AppUpdateStatus = $"Version {release.Version} will be offered again in 24 hours.";
    }

    private async Task AuthorizeTwitchAsync()
    {
        var cancellationToken = lifetimeCancellation.Token;
        try
        {
            StatusMessage = "Waiting for Twitch authorization";
            var token = await TwitchOAuthService.AuthorizeUserTokenAsync(Settings.Chat, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            TwitchOAuthService.ApplyTokenResult(Settings.Chat, token);

            await settingsService.SaveAsync(Settings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await RestartChatTabsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            ClearTwitchTokenCommand.RaiseCanExecuteChanged();
            StatusMessage = token.ExpiresAtUtc is { } expiresAt
                ? $"Twitch authorized until {expiresAt.ToLocalTime():g}"
                : "Twitch authorized";
            _ = RefreshFollowedChannelsAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!disposed)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "TwitchOAuth", "Twitch authorization failed.", ex);
        }
    }

    private async Task ClearTwitchTokenAsync()
    {
        try
        {
            TwitchOAuthService.ClearToken(Settings.Chat);
            await settingsService.SaveAsync(Settings);
            await RestartChatTabsAsync();
            ClearTwitchTokenCommand.RaiseCanExecuteChanged();
            StatusMessage = "Twitch token cleared";
            _ = RefreshFollowedChannelsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "TwitchOAuth", "Failed to clear Twitch token.", ex);
            ClearTwitchTokenCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task AuthorizeKickAsync()
    {
        var cancellationToken = lifetimeCancellation.Token;
        try
        {
            StatusMessage = "Waiting for Kick authorization";
            var token = await KickOAuthService.AuthorizeUserTokenAsync(Settings.Chat, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            KickOAuthService.ApplyTokenResult(Settings.Chat, token);

            if (string.IsNullOrWhiteSpace(Settings.Chat.KickUsername))
            {
                var username = await KickOAuthService.TryGetCurrentUsernameAsync(token.AccessToken, cancellationToken, logger);
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(username))
                {
                    Settings.Chat.KickUsername = username;
                }
            }

            await settingsService.SaveAsync(Settings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await RestartChatTabsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            ClearKickTokenCommand.RaiseCanExecuteChanged();
            StatusMessage = token.ExpiresAtUtc is { } expiresAt
                ? $"Kick authorized until {expiresAt.ToLocalTime():g}"
                : "Kick authorized";
            _ = RefreshFollowedChannelsAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!disposed)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "KickOAuth", "Kick authorization failed.", ex);
        }
    }

    private async Task ImportKickFollowsAsync()
    {
        if (kickFollowedChannelsImporter is null) return;
        if (Interlocked.CompareExchange(ref kickFollowImportBusy, 1, 0) != 0) return;
        var token = lifetimeCancellation.Token;
        try
        {
            ClearImportedKickFollowsCommand.RaiseCanExecuteChanged();
            KickFollowImportStatus = "Sign in to Kick and import your follows in the opened window.";
            var channels = await kickFollowedChannelsImporter.ImportAsync(token);
            token.ThrowIfCancellationRequested();
            if (channels is null)
            {
                KickFollowImportStatus = "Detection canceled. Your saved follows were kept.";
                return;
            }
            await SaveImportedKickFollowsAsync(channels, DateTimeOffset.UtcNow, token);
            token.ThrowIfCancellationRequested();
            KickFollowImportStatus = $"Imported {channels.Count} Kick follows. Detect again after following or unfollowing channels.";
            await RefreshFollowedChannelsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (!disposed)
        {
            KickFollowImportStatus = $"Kick follows could not be imported: {ex.Message}";
        }
        finally
        {
            Volatile.Write(ref kickFollowImportBusy, 0);
            ClearImportedKickFollowsCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ClearImportedKickFollowsAsync()
    {
        if (Interlocked.CompareExchange(ref kickFollowImportBusy, 1, 0) != 0) return;
        var token = lifetimeCancellation.Token;
        try
        {
            ImportKickFollowsCommand.RaiseCanExecuteChanged();
            await SaveImportedKickFollowsAsync([], null, token);
            token.ThrowIfCancellationRequested();
            KickFollowImportStatus = "Imported Kick follows cleared.";
            await RefreshFollowedChannelsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (!disposed) { KickFollowImportStatus = ex.Message; }
        finally
        {
            Volatile.Write(ref kickFollowImportBusy, 0);
            ImportKickFollowsCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task SaveImportedKickFollowsAsync(
        IReadOnlyList<string> channels, DateTimeOffset? importedAt, CancellationToken token)
    {
        var settings = Settings.FollowedChannels;
        var previousChannels = settings.KickImportedChannelSlugs;
        var previousTime = settings.KickFollowsImportedAtUtc;
        settings.KickImportedChannelSlugs = channels.ToList();
        settings.KickFollowsImportedAtUtc = importedAt;
        try { await settingsService.SaveAsync(Settings, token); }
        catch
        {
            settings.KickImportedChannelSlugs = previousChannels;
            settings.KickFollowsImportedAtUtc = previousTime;
            throw;
        }
        OnPropertyChanged(nameof(KickImportedFollowsSummary));
    }

    private async Task ClearKickTokenAsync()
    {
        try
        {
            KickOAuthService.ClearToken(Settings.Chat);
            await settingsService.SaveAsync(Settings);
            await RestartChatTabsAsync();
            ClearKickTokenCommand.RaiseCanExecuteChanged();
            StatusMessage = "Kick token cleared";
            _ = RefreshFollowedChannelsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "KickOAuth", "Failed to clear Kick token.", ex);
            ClearKickTokenCommand.RaiseCanExecuteChanged();
        }
    }

    private void MoveTabLeft()
    {
        if (SelectedTab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(SelectedTab);
        if (index > 0)
        {
            Tabs.Move(index, index - 1);
            RaiseCommandStates();
        }
    }

    private void MoveTabRight()
    {
        if (SelectedTab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(SelectedTab);
        if (index >= 0 && index < Tabs.Count - 1)
        {
            Tabs.Move(index, index + 1);
            RaiseCommandStates();
        }
    }

    private void ToggleMultiStream()
    {
        IsMultiStreamEnabled = !IsMultiStreamEnabled;
    }

    private void RaiseCommandStates()
    {
        PlaySelectedCommand.RaiseCanExecuteChanged();
        ReloadSelectedCommand.RaiseCanExecuteChanged();
        StopSelectedCommand.RaiseCanExecuteChanged();
        PauseSelectedCommand.RaiseCanExecuteChanged();
        CloseSelectedCommand.RaiseCanExecuteChanged();
        CreateClipCommand.RaiseCanExecuteChanged();
        ToggleChatCommand.RaiseCanExecuteChanged();
        MoveTabLeftCommand.RaiseCanExecuteChanged();
        MoveTabRightCommand.RaiseCanExecuteChanged();
    }

    private static void OpenExternalBrowser(Uri uri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true
        });
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        if (e.PropertyName == nameof(AppSettings.Chat))
        {
            ObserveChatSettings(Settings.Chat, applyImmediately: true);
        }

        if (e.PropertyName == nameof(AppSettings.StreamVlcOverlayFontSizes))
        {
            OnPropertyChanged(nameof(SelectedVlcOverlayFontSize));
            RaiseChatTextSizeProperties();
            foreach (var tab in Tabs)
            {
                tab.RefreshChatOverlay(Settings.Chat);
            }
        }

        if (e.PropertyName == nameof(AppSettings.FollowedChannels))
        {
            ObserveFollowedChannelsSettings(Settings.FollowedChannels);
        }

        if (e.PropertyName == nameof(AppSettings.MultiStreamEnabled))
        {
            OnPropertyChanged(nameof(IsMultiStreamEnabled));
            OnPropertyChanged(nameof(MultiStreamToggleToolTip));
            ApplyVideoLayout();
            ApplyInactivePlaybackPolicyInBackground();
            return;
        }

        if (e.PropertyName == nameof(AppSettings.KeepInactiveTabsRunning))
        {
            ApplyInactivePlaybackPolicyInBackground();
        }

        if (e.PropertyName == nameof(AppSettings.Theme))
        {
            Themes.ThemeManager.ApplyTheme(Settings.Theme);
        }
    }

    private void ObserveChatSettings(ChatSettings settings, bool applyImmediately = false)
    {
        if (ReferenceEquals(observedChatSettings, settings))
        {
            return;
        }

        if (observedChatSettings is not null)
        {
            observedChatSettings.PropertyChanged -= ChatSettingsOnPropertyChanged;
        }

        observedChatSettings = settings;
        observedChatSettings.PropertyChanged += ChatSettingsOnPropertyChanged;
        OnPropertyChanged(nameof(SelectedVlcOverlayFontSize));
        RaiseChatTextSizeProperties();

        if (!applyImmediately || disposed)
        {
            return;
        }

        foreach (var tab in Tabs.ToArray())
        {
            tab.RefreshChatOverlay(settings);
        }

        backgroundOperationController.Track(ApplyChatSettingsAsync(reconfigurePlayback: true));
        RaiseChatVisibilityProperties();
        ClearTwitchTokenCommand.RaiseCanExecuteChanged();
        ClearKickTokenCommand.RaiseCanExecuteChanged();
    }

    private void ObserveFollowedChannelsSettings(FollowedChannelsSettings settings)
    {
        if (!ReferenceEquals(observedFollowedChannelsSettings, settings))
        {
            if (observedFollowedChannelsSettings is not null)
            {
                observedFollowedChannelsSettings.PropertyChanged -= FollowedChannelsSettingsOnPropertyChanged;
            }

            observedFollowedChannelsSettings = settings;
            observedFollowedChannelsSettings.PropertyChanged += FollowedChannelsSettingsOnPropertyChanged;
        }

        ApplyLiveNotificationSetting();
    }

    private void FollowedChannelsSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FollowedChannelsSettings.NotifyWhenLive))
        {
            ApplyLiveNotificationSetting();
        }
    }

    private void ApplyLiveNotificationSetting()
    {
        if (liveNotificationService is not null)
        {
            liveNotificationService.IsEnabled = Settings.FollowedChannels.NotifyWhenLive;
        }
    }

    private void ChatSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Token refreshes and Kick identity lookups also publish settings from worker threads.
        // Keep collection access, playback reconfiguration, and command notifications on the UI.
        dispatch(() => ApplyChatSettingsChange(sender, e));
    }

    private void ApplyChatSettingsChange(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || !ReferenceEquals(sender, observedChatSettings))
        {
            return;
        }

        if (e.PropertyName == nameof(ChatSettings.Layout) &&
            Settings.Chat.Layout != ChatLayout.Overlay)
        {
            ReleaseNativeOverlayChatInputFocus();
        }

        if (e.PropertyName == nameof(ChatSettings.VlcOverlayFontSize))
        {
            OnPropertyChanged(nameof(SelectedVlcOverlayFontSize));
        }

        if (e.PropertyName is nameof(ChatSettings.Layout) or nameof(ChatSettings.FontSize) or
            nameof(ChatSettings.VlcOverlayFontSize))
        {
            RaiseChatTextSizeProperties();
        }

        var reconfigurePlayback = IsNativeOverlayPlaybackSetting(e.PropertyName);
        ApplyVlcPluginMultiViewChatPolicyInBackground(restoreWhenAllowed: true);
        if (!reconfigurePlayback)
        {
            foreach (var tab in Tabs)
            {
                tab.RefreshChatOverlay(Settings.Chat);
            }
        }

        if (IsChatConnectionSetting(e.PropertyName))
        {
            backgroundOperationController.Track(ApplyChatSettingsAsync(reconfigurePlayback));
        }

        RaiseChatVisibilityProperties();
        ClearTwitchTokenCommand.RaiseCanExecuteChanged();
        ClearKickTokenCommand.RaiseCanExecuteChanged();
    }

    private async Task RestartChatTabsAsync()
    {
        await ApplyChatSettingsAsync(reconfigurePlayback: false);
    }

    private async Task ApplyChatSettingsAsync(bool reconfigurePlayback)
    {
        var enteredGate = false;
        try
        {
            await chatSettingsApplyGate.WaitAsync(lifetimeCancellation.Token);
            enteredGate = true;
            if (disposed)
            {
                return;
            }

            foreach (var tab in Tabs.ToArray())
            {
                if (disposed)
                {
                    return;
                }

                try
                {
                    if (reconfigurePlayback && tab.ShouldRestartPlaybackForChatOverlaySettings(Settings))
                    {
                        StatusMessage = $"Reloading {tab.Target.DisplayName} for chat layout";
                        await tab.ReconfigurePlaybackForChatOverlaySettingsAsync(Settings, lifetimeCancellation.Token);
                    }
                    else
                    {
                        await tab.RestartChatAsync(Settings, lifetimeCancellation.Token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusMessage = ex.Message;
                    logger.Write(AppLogLevel.Warning, "Chat", $"Failed to apply chat settings for {tab.Target.DisplayName}.", ex);
                }
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || disposed)
        {
        }
        finally
        {
            if (enteredGate)
            {
                chatSettingsApplyGate.Release();
            }
        }

        if (!disposed)
        {
            RaiseChatVisibilityProperties();
        }
    }

    private async Task RestartTabChatAsync(StreamTabViewModel tab)
    {
        if (disposed)
        {
            return;
        }

        var enteredGate = false;
        try
        {
            await chatSettingsApplyGate.WaitAsync(lifetimeCancellation.Token);
            enteredGate = true;
            if (!Tabs.Contains(tab))
            {
                return;
            }

            await tab.RestartChatAsync(Settings);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || disposed)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Chat", $"Failed to restart chat for {tab.Target.DisplayName}.", ex);
        }
        finally
        {
            if (enteredGate)
            {
                chatSettingsApplyGate.Release();
            }
        }

        if (!disposed)
        {
            RaiseChatVisibilityProperties();
        }
    }

    private async Task RestartTheatreModeChatTabsAsync(IReadOnlyList<StreamTabViewModel> tabs)
    {
        if (disposed)
        {
            return;
        }

        var enteredGate = false;
        try
        {
            await chatSettingsApplyGate.WaitAsync(lifetimeCancellation.Token);
            enteredGate = true;
            foreach (var tab in tabs.Distinct().Where(Tabs.Contains))
            {
                if (disposed)
                {
                    return;
                }

                try
                {
                    await tab.RestartChatAsync(Settings);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusMessage = ex.Message;
                    logger.Write(AppLogLevel.Warning, "Chat", $"Failed to update theatre chat for {tab.Target.DisplayName}.", ex);
                }
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || disposed)
        {
        }
        finally
        {
            if (enteredGate)
            {
                chatSettingsApplyGate.Release();
            }
        }

        if (!disposed)
        {
            RaiseChatVisibilityProperties();
        }
    }

    private static bool IsChatConnectionSetting(string? propertyName)
    {
        return propertyName is nameof(ChatSettings.Layout) or
            nameof(ChatSettings.ConnectAutomatically) or
            nameof(ChatSettings.VlcOverlayDirectory) or
            nameof(ChatSettings.TwitchUsername) or
            nameof(ChatSettings.TwitchOAuthToken) or
            nameof(ChatSettings.TwitchClientId) or
            nameof(ChatSettings.TwitchTokenScopes) or
            nameof(ChatSettings.KickUsername) or
            nameof(ChatSettings.KickClientId) or
            nameof(ChatSettings.KickClientSecret) or
            nameof(ChatSettings.KickSendAsBot) or
            nameof(ChatSettings.KickChatroomIds) or
            nameof(ChatSettings.KickBroadcasterUserIds);
    }

    private static bool IsNativeOverlayPlaybackSetting(string? propertyName)
    {
        return propertyName is nameof(ChatSettings.Layout) or
            nameof(ChatSettings.VlcOverlayDirectory);
    }

    private static string GetStartFailure(StreamTabViewModel tab)
    {
        return string.IsNullOrWhiteSpace(tab.ErrorMessage) ? tab.StatusText : tab.ErrorMessage;
    }

    private static string FormatKickFollowedChannelsText(IEnumerable<string> slugs)
    {
        return string.Join(Environment.NewLine, slugs);
    }

    private static List<string> ParseKickFollowedChannelSlugs(string text)
    {
        return ParseKickFollowedChannelSlugs(text, skipInvalidEntries: false, out _);
    }

    private static List<string> ParseKickFollowedChannelSlugs(
        string text,
        bool skipInvalidEntries,
        out IReadOnlyList<string> invalidEntries)
    {
        var slugs = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = (text ?? "").Split(
            ['\r', '\n', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            var normalized = entry.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!TryParseKickFollowedChannelEntry(normalized, out var target, out var errorMessage))
            {
                if (skipInvalidEntries)
                {
                    invalid.Add(normalized);
                    continue;
                }

                throw new FormatException(errorMessage);
            }

            if (seen.Add(target!.Channel))
            {
                slugs.Add(target.Channel);
            }
        }

        invalidEntries = invalid;
        return slugs;
    }

    private static bool TryParseKickFollowedChannelEntry(
        string value,
        out StreamTarget? target,
        out string errorMessage)
    {
        if (StreamInputParser.TryParsePlatformUrl(value, out var parsedTarget) && parsedTarget is not null)
        {
            if (parsedTarget.Platform != PlatformKind.Kick)
            {
                target = null;
                errorMessage = $"Kick followed channels only accept Kick channel URLs or slugs: {value}";
                return false;
            }

            target = parsedTarget;
            errorMessage = "";
            return true;
        }

        try
        {
            target = StreamInputParser.FromChannel(PlatformKind.Kick, value);
            errorMessage = "";
            return true;
        }
        catch (ArgumentException ex)
        {
            target = null;
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string FormatInvalidKickFollowedChannelsMessage(int count)
    {
        return count == 1
            ? "1 invalid Kick followed channel entry was skipped."
            : $"{count} invalid Kick followed channel entries were skipped.";
    }

    private bool HasKickToken()
    {
        return !string.IsNullOrWhiteSpace(Settings.Chat.KickOAuthToken) ||
            !string.IsNullOrWhiteSpace(Settings.Chat.KickRefreshToken);
    }

    private bool HasTwitchToken()
    {
        return !string.IsNullOrWhiteSpace(Settings.Chat.TwitchOAuthToken);
    }

    private void SelectedTabOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StreamTabViewModel.IsChatVisible) ||
            e.PropertyName == nameof(StreamTabViewModel.IsDockedChatPanelVisible) ||
            e.PropertyName == nameof(StreamTabViewModel.IsDockedChatOverrideActive) ||
            e.PropertyName == nameof(StreamTabViewModel.UsesNativeOverlay))
        {
            RaiseChatVisibilityProperties();
        }

        if (e.PropertyName == nameof(StreamTabViewModel.IsReplaySeekBarVisible))
        {
            OnPropertyChanged(nameof(IsReplaySeekBarVisible));
        }
    }

    private void TabsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        navigationHistory.RemoveAll(destination => !IsNavigationDestinationAvailable(destination));
        RaiseNavigationCommandState();

        if (e.Action != NotifyCollectionChangedAction.Move && e.OldItems is not null)
        {
            var oldTabs = e.OldItems.Cast<StreamTabViewModel>().ToArray();
            RemoveTabsFromMultiViewGroups(oldTabs, applyLayout: false);
            RemoveTabsFromPictureInPictureGroups(oldTabs, applyLayout: false);
            RemoveTabsFromPictureInPictureVisibleGroups(oldTabs, applyPolicy: false);
        }

        if (e.Action != NotifyCollectionChangedAction.Move && e.NewItems is not null)
        {
            foreach (StreamTabViewModel tab in e.NewItems)
            {
                ApplySavedStreamVolume(tab);
                tab.PropertyChanged += TabOnPropertyChanged;
                tab.AudioStateApplied += TabOnAudioStateApplied;
            }
        }

        if (e.Action != NotifyCollectionChangedAction.Move && e.OldItems is not null)
        {
            foreach (StreamTabViewModel tab in e.OldItems)
            {
                vlcPluginMultiViewChatPolicyHiddenTabs.Remove(tab);
                tab.PropertyChanged -= TabOnPropertyChanged;
                tab.AudioStateApplied -= TabOnAudioStateApplied;
                tab.SetSelectedForAudio(false);
                tab.SetVideoPlacement(visible: false, row: 0, column: 0, rowSpan: 1, columnSpan: 1);
                tab.SetMainVideoSurfaceExpected(false);
                tab.SetMergedTabGroupPlacement(member: false, first: false, last: false);
                tab.SetDetached(false);
                tab.IsSelected = false;
            }
        }

        OnPropertyChanged(nameof(IsAnyStreamPlaying));

        if (audioActiveTab is not null && !Tabs.Contains(audioActiveTab))
        {
            audioActiveTab.SetSelectedForAudio(false);
            SetAudioActiveTab(selectedTab);
        }

        if (selectedTab is not null && !Tabs.Contains(selectedTab))
        {
            SelectedTab = null;
            return;
        }

        ApplySelectedTabSelection();
        ApplyVideoLayout();
        ApplyInactivePlaybackPolicyInBackground();
    }

    private void LiveFollowedChannelsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasLiveFollowedChannels));
        OnPropertyChanged(nameof(IsFollowedChannelsEmptyVisible));
    }

    private void StreamSearchResultsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasStreamSearchResults));
        OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
        OnPropertyChanged(nameof(IsStreamSearchResultsVisible));
        OnPropertyChanged(nameof(IsStreamSearchEmptyVisible));
        OnPropertyChanged(nameof(StreamSearchResultsTitle));
    }

    private void TwitchVodsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTwitchVods));
        OnPropertyChanged(nameof(IsTwitchVodEmptyVisible));
        OnPropertyChanged(nameof(IsTwitchVodLoadMoreVisible));
        OnPropertyChanged(nameof(TwitchVodResultsTitle));
    }

    private void RecentStreamsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRecentStreams));
        OnPropertyChanged(nameof(IsRecentStreamsEmptyVisible));
        OnPropertyChanged(nameof(RecentStreamsStatus));
    }

    private void BrowseCategoriesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasBrowseCategories));
        OnPropertyChanged(nameof(IsBrowseCategoriesEmptyVisible));
        OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreVisible));
        OnPropertyChanged(nameof(IsBrowseCategoryLoadMoreIndicatorVisible));
        OnPropertyChanged(nameof(BrowseCategoriesTitle));
        RaiseBrowseCommandStates();
    }

    private void BrowseStreamsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasBrowseStreams));
        OnPropertyChanged(nameof(IsBrowseStreamsEmptyVisible));
        OnPropertyChanged(nameof(IsBrowseStreamLoadMoreVisible));
        OnPropertyChanged(nameof(BrowseStreamsTitle));
        RaiseBrowseCommandStates();
    }

    private void RebuildRecentStreams()
    {
        var existing = RecentStreams.ToDictionary(card => card.Target.StateKey, StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desired = new List<RecentStreamViewModel>(Settings.RecentStreams.Count);
        foreach (var stream in Settings.RecentStreams)
        {
            var target = new StreamTarget(stream.Platform, stream.Channel, stream.Url, CategoryName: stream.CategoryName);
            if (!keys.Add(target.StateKey)) continue;
            var liveStatus = recentStreamController.TryGetLiveStatus(target.StateKey, out var status)
                ? status
                : RecentStreamLiveStatus.Unknown;
            var displayed = pendingRecentMetadata is not null && pendingRecentMetadata.TryGetValue(target.StateKey, out var metadata)
                ? MergeRecentStreamMetadata(stream, metadata) : stream;
            if (existing.TryGetValue(target.StateKey, out var card)) card.Update(displayed, liveStatus);
            else card = new RecentStreamViewModel(displayed, OpenRecentStreamAsync, DeleteRecentStreamAsync, liveStatus);
            desired.Add(card);
        }
        for (var index = RecentStreams.Count - 1; index >= 0; index--)
        {
            if (!keys.Contains(RecentStreams[index].Target.StateKey)) RecentStreams.RemoveAt(index);
        }
        for (var index = 0; index < desired.Count; index++)
        {
            var card = desired[index];
            if (index < RecentStreams.Count && ReferenceEquals(RecentStreams[index], card)) continue;
            var currentIndex = RecentStreams.IndexOf(card);
            if (currentIndex < 0) RecentStreams.Insert(index, card);
            else RecentStreams.Move(currentIndex, index);
        }
    }

    private void TabOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StreamTabViewModel.NeverMute))
        {
            ApplyInactivePlaybackPolicyInBackground();
        }

        if (sender is StreamTabViewModel tab && e.PropertyName == nameof(StreamTabViewModel.Volume))
        {
            RememberStreamVolume(tab);
        }

        if (sender is StreamTabViewModel detachedTab && e.PropertyName == nameof(StreamTabViewModel.IsDetached))
        {
            if (!detachedTab.IsDetached && ReferenceEquals(detachedTab, audioActiveTab))
            {
                ActivateMainWindowAudio();
            }

            if (ReferenceEquals(detachedTab, SelectedTab))
            {
                OnPropertyChanged(nameof(IsSelectedTabDetached));
            }

            ApplyVideoLayout();
            ApplyInactivePlaybackPolicyInBackground();
        }

        if (sender is StreamTabViewModel busyTab &&
            e.PropertyName == nameof(StreamTabViewModel.IsBusy) &&
            !busyTab.IsBusy)
        {
            // Busy tabs are allowed to finish their current playback transition. Re-run the
            // policy afterward in case the tab became hidden while that transition was running.
            ApplyInactivePlaybackPolicyInBackground();
        }

        if (e.PropertyName == nameof(StreamTabViewModel.Status))
        {
            OnPropertyChanged(nameof(IsAnyStreamPlaying));
            ApplyVlcPluginMultiViewChatPolicyInBackground();
        }
    }

    private void TabOnAudioStateApplied(object? sender, EventArgs e)
    {
        if (applyingSelectedTabSelection ||
            sender is not StreamTabViewModel tab ||
            ReferenceEquals(tab, audioActiveTab) ||
            audioActiveTab is null ||
            !Tabs.Contains(audioActiveTab))
        {
            return;
        }

        audioActiveTab.ReapplyAudio();
    }

    private void RaiseChatVisibilityProperties()
    {
        OnPropertyChanged(nameof(IsDockedChatVisible));
        OnPropertyChanged(nameof(IsSelectedChatShowing));
        OnPropertyChanged(nameof(IsChatLayoutHidden));
    }

    private bool IsDockedChatPanelActive(StreamTabViewModel tab)
    {
        return Settings.Chat.Layout == ChatLayout.Docked || tab.IsDockedChatOverrideActive;
    }

    private static bool IsVlcPluginOverlayConfigured(ChatSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.VlcOverlayDirectory) ||
            VlcOverlayDirectoryResolver.TryResolve(settings.VlcOverlayDirectory) is not null;
    }

    private static bool IsVlcPluginOverlayMode(ChatSettings settings)
    {
        return settings.Layout == ChatLayout.Overlay && IsVlcPluginOverlayConfigured(settings);
    }

    private string GetSelectedKickSetting(bool broadcaster)
    {
        if (SelectedTab?.Target.Platform != PlatformKind.Kick)
        {
            return "";
        }

        var found = broadcaster
            ? Settings.Chat.TryGetKickBroadcasterUserId(SelectedTab.Target.Channel, out var value)
            : Settings.Chat.TryGetKickChatroomId(SelectedTab.Target.Channel, out value);
        return found
            ? value
            : "";
    }

    private void SetSelectedKickSetting(string? value, string propertyName, bool broadcaster)
    {
        if (SelectedTab?.Target.Platform != PlatformKind.Kick)
        {
            return;
        }

        var channel = SelectedTab.Target.Channel;
        _ = broadcaster
            ? Settings.Chat.SetKickBroadcasterUserId(channel, value)
            : Settings.Chat.SetKickChatroomId(channel, value);

        OnPropertyChanged(propertyName);
        SelectedTab.RefreshChatOverlay(Settings.Chat);
        backgroundOperationController.Track(SelectedTab.RestartChatAsync(Settings, lifetimeCancellation.Token));
        RaiseChatVisibilityProperties();
    }

    private void ApplyVideoLayout()
    {
        var visibleTabs = GetVisibleVideoTabs();
        var layout = VideoGridLayoutCalculator.GetLayout(visibleTabs.Count);
        VideoGridRows = layout.Rows;
        VideoGridColumns = layout.Columns;
        ApplyMergedTabGroup(visibleTabs);
        RefreshTabStripItems();

        var visibleSet = visibleTabs.ToHashSet();
        for (var index = 0; index < visibleTabs.Count; index++)
        {
            var placement = VideoGridLayoutCalculator.GetPlacement(index, visibleTabs.Count, layout);
            visibleTabs[index].SetVideoPlacement(
                visible: true,
                placement.Row,
                placement.Column,
                placement.RowSpan,
                placement.ColumnSpan);
        }

        foreach (var tab in Tabs)
        {
            if (tab.IsDetached || !visibleSet.Contains(tab))
            {
                // Hidden HWNDs stay mounted. Keep their single-stream allocation so
                // revealing a tab does not first expose a quarter-size video surface.
                tab.SetVideoPlacement(visible: false, row: 0, column: 0,
                    rowSpan: layout.Rows, columnSpan: layout.Columns);
            }
        }

        SyncVideoTabs(visibleTabs);
        ApplyVlcPluginMultiViewChatPolicyInBackground(restoreWhenAllowed: true);
    }

    private void ApplyVlcPluginMultiViewChatPolicyInBackground(bool restoreWhenAllowed = false)
    {
        if (disposed)
        {
            return;
        }

        var policyTabs = GetCurrentVlcPluginMultiViewChatPolicyTabs();
        var changedTabs = policyTabs.Length > 0
            ? DisableVlcPluginMultiViewChats(policyTabs)
            : [];
        var restoredTabs = restoreWhenAllowed
            ? RestoreVlcPluginMultiViewChats(policyTabs.ToHashSet())
            : [];
        var reconfigureTabs = changedTabs
            .Concat(restoredTabs)
            .Distinct()
            .ToArray();
        if (reconfigureTabs.Length > 0)
        {
            backgroundOperationController.Track(ReconfigureVlcPluginMultiViewChatPolicyTabsAsync(reconfigureTabs));
        }
    }

    private StreamTabViewModel[] DisableVlcPluginMultiViewChats(IReadOnlyList<StreamTabViewModel> targetTabs)
    {
        var changedTabs = targetTabs
            .Where(tab => tab.SetChatVisibleForDeferredLifecycle(false))
            .ToArray();
        if (changedTabs.Length == 0)
        {
            return [];
        }

        foreach (var tab in changedTabs)
        {
            vlcPluginMultiViewChatPolicyHiddenTabs.Add(tab);
        }

        RaiseChatVisibilityProperties();
        StatusMessage = changedTabs.Length == 1
            ? $"{changedTabs[0].Target.DisplayName}: chat hidden"
            : $"{changedTabs.Length} streams: chat hidden";
        return changedTabs;
    }

    private StreamTabViewModel[] RestoreVlcPluginMultiViewChats(IReadOnlySet<StreamTabViewModel> policyTabs)
    {
        if (vlcPluginMultiViewChatPolicyHiddenTabs.Count == 0)
        {
            return [];
        }

        var restoreTabs = vlcPluginMultiViewChatPolicyHiddenTabs
            .Where(Tabs.Contains)
            .Where(tab => !policyTabs.Contains(tab))
            .ToArray();
        foreach (var tab in vlcPluginMultiViewChatPolicyHiddenTabs.Where(tab => !Tabs.Contains(tab)).ToArray())
        {
            vlcPluginMultiViewChatPolicyHiddenTabs.Remove(tab);
        }

        if (restoreTabs.Length == 0)
        {
            return [];
        }

        var changedTabs = new List<StreamTabViewModel>();
        foreach (var tab in restoreTabs)
        {
            vlcPluginMultiViewChatPolicyHiddenTabs.Remove(tab);
            if (tab.SetChatVisibleForDeferredLifecycle(true))
            {
                changedTabs.Add(tab);
            }
        }

        if (changedTabs.Count == 0)
        {
            return [];
        }

        RaiseChatVisibilityProperties();
        StatusMessage = changedTabs.Count == 1
            ? $"{changedTabs[0].Target.DisplayName}: chat shown"
            : $"{changedTabs.Count} streams: chat shown";
        return changedTabs.ToArray();
    }

    private async Task ReconfigureVlcPluginMultiViewChatPolicyTabsAsync(IReadOnlyList<StreamTabViewModel> tabs)
    {
        await Task.Yield();
        await ReconfigureVlcPluginChatTabsWithGateAsync(tabs);
    }

    private async Task ReconfigureVlcPluginChatTabsWithGateAsync(IReadOnlyList<StreamTabViewModel> tabs)
    {
        var enteredGate = false;
        try
        {
            await vlcPluginMultiViewChatPolicyGate.WaitAsync(lifetimeCancellation.Token);
            enteredGate = true;
            if (disposed)
            {
                return;
            }

            var reconfigureResults = await Task.WhenAll(tabs
                .Distinct()
                .Where(Tabs.Contains)
                .Select(ReconfigureVlcPluginChatTabAsync));
            foreach (var (tab, exception) in reconfigureResults)
            {
                if (exception is null)
                {
                    continue;
                }

                StatusMessage = exception.Message;
                logger.Write(AppLogLevel.Warning, "Chat", $"Failed to update VLC plugin chat for {tab.Target.DisplayName}.", exception);
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested || disposed)
        {
        }
        finally
        {
            if (enteredGate)
            {
                vlcPluginMultiViewChatPolicyGate.Release();
            }
        }

        if (!disposed)
        {
            RaiseChatVisibilityProperties();
        }
    }

    private async Task<(StreamTabViewModel Tab, Exception? Exception)> ReconfigureVlcPluginChatTabAsync(StreamTabViewModel tab)
    {
        try
        {
            await tab.ReconfigurePlaybackForChatOverlaySettingsAsync(Settings, lifetimeCancellation.Token);
            return (tab, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (tab, ex);
        }
    }

    private void ApplyMergedTabGroup(IReadOnlyList<StreamTabViewModel> visibleTabs)
    {
        foreach (var tab in Tabs)
        {
            tab.SetMergedTabGroupPlacement(member: false, first: false, last: false);
        }

        if (IsMultiStreamEnabled && !IsStreamOnlyFullscreenActive && visibleTabs.Count > 1)
        {
            ApplyMergedTabGroupPlacement(visibleTabs);
        }

        if (!IsStreamOnlyFullscreenActive)
        {
            foreach (var group in tabGroupingController.MultiViewGroups.ToArray())
            {
                var orderedGroup = Tabs
                    .Where(group.Contains)
                    .Where(tab => !tab.IsDetached)
                    .Take(VideoGridLayoutCalculator.TileLimit)
                    .ToArray();
                if (orderedGroup.Length <= 1)
                {
                    continue;
                }

                ApplyMergedTabGroupPlacement(orderedGroup);
            }
        }

        foreach (var group in tabGroupingController.PictureInPictureGroups.ToArray())
        {
            var orderedGroup = Tabs
                .Where(group.Contains)
                .ToArray();
            if (orderedGroup.Length <= 1)
            {
                continue;
            }

            ApplyMergedTabGroupPlacement(orderedGroup);
        }
    }

    private static void ApplyMergedTabGroupPlacement(IReadOnlyList<StreamTabViewModel> tabs)
    {
        for (var index = 0; index < tabs.Count; index++)
        {
            tabs[index].SetMergedTabGroupPlacement(
                member: true,
                first: index == 0,
                last: index == tabs.Count - 1);
        }
    }

    private void RefreshTabStripItems()
    {
        foreach (var item in TabStripItems)
        {
            item.Dispose();
        }

        TabStripItems.Clear();
        foreach (var group in BuildTabStripGroups())
        {
            TabStripItems.Add(new TabStripItemViewModel(group, selectedTab));
        }

        SelectCurrentTabStripItem();
    }

    private IReadOnlyList<IReadOnlyList<StreamTabViewModel>> BuildTabStripGroups()
    {
        var groups = new List<IReadOnlyList<StreamTabViewModel>>();
        var groupedTabs = new HashSet<StreamTabViewModel>();
        foreach (var tab in Tabs)
        {
            if (groupedTabs.Contains(tab))
            {
                continue;
            }

            var group = ResolveHostableTabGroup(tab);
            if (group is null)
            {
                groups.Add([tab]);
                groupedTabs.Add(tab);
            }
            else
            {
                groups.Add(group);
                foreach (var groupTab in group)
                {
                    groupedTabs.Add(groupTab);
                }
            }
        }

        return groups;
    }

    private IReadOnlyList<StreamTabViewModel>? ResolveHostableTabGroup(StreamTabViewModel tab)
    {
        if (GetPictureInPictureTabGroup(tab) is { Count: > 1 } pictureInPictureGroup)
        {
            return pictureInPictureGroup;
        }

        if (!tab.IsDetached && GetMultiViewTabGroup(tab) is { Count: > 1 } multiViewGroup)
        {
            var hostableGroup = multiViewGroup
                .Where(candidate => !candidate.IsDetached)
                .Take(VideoGridLayoutCalculator.TileLimit)
                .ToArray();
            if (hostableGroup.Length > 1 && hostableGroup.Contains(tab))
            {
                return hostableGroup;
            }
        }

        return null;
    }

    private void SelectCurrentTabStripItem()
    {
        var item = selectedTab is null
            ? null
            : TabStripItems.FirstOrDefault(candidate => candidate.Contains(selectedTab));
        if (ReferenceEquals(selectedTabStripItem, item))
        {
            return;
        }

        selectedTabStripItem = item;
        OnPropertyChanged(nameof(SelectedTabStripItem));
    }

    private void SyncVideoTabs(IReadOnlyList<StreamTabViewModel> visibleTabs)
    {
        var mountedTabs = IsHomeSelected
            ? Tabs
                .Where(tab => !tab.IsDetached && (VideoTabs.Contains(tab) || IsTabOpenOrStarting(tab)))
                .ToHashSet()
            : Tabs
                .Where(tab => !tab.IsDetached)
                .ToHashSet();
        foreach (var tab in visibleTabs)
        {
            mountedTabs.Add(tab);
        }

        foreach (var tab in Tabs)
        {
            tab.SetMainVideoSurfaceExpected(mountedTabs.Contains(tab));
        }

        var seenTabs = new HashSet<StreamTabViewModel>();
        for (var index = 0; index < VideoTabs.Count; index++)
        {
            var tab = VideoTabs[index];
            if (!mountedTabs.Contains(tab) || !seenTabs.Add(tab))
            {
                VideoTabs.RemoveAt(index);
                index--;
            }
        }

        var tabsToAdd = IsHomeSelected
            ? Tabs.Where(mountedTabs.Contains)
            : visibleTabs;

        // Keep mounted HwndHost surfaces hidden on Home so VLC retains the same
        // native handle while the WPF Home view is on top.
        foreach (var tab in tabsToAdd)
        {
            if (!VideoTabs.Contains(tab))
            {
                VideoTabs.Add(tab);
            }
        }
    }

    private List<StreamTabViewModel> GetVisibleVideoTabs()
    {
        if (IsHomeSelected)
        {
            return [];
        }

        if (Tabs.Count == 0)
        {
            return [];
        }

        var selected = selectedTab is not null && Tabs.Contains(selectedTab) ? selectedTab : null;
        if (selected is not { } selectedVideoTab)
        {
            return [];
        }

        if (selectedVideoTab.IsDetached)
        {
            return [];
        }

        if (IsStreamOnlyFullscreenActive)
        {
            return [selectedVideoTab];
        }

        if (GetMultiViewTabGroup(selectedVideoTab) is { Count: > 1 } multiViewGroup)
        {
            var hostableGroup = multiViewGroup
                .Where(tab => !tab.IsDetached)
                .Take(VideoGridLayoutCalculator.TileLimit)
                .ToList();
            if (hostableGroup.Count > 1 && hostableGroup.Contains(selectedVideoTab))
            {
                return hostableGroup;
            }
        }

        if (!IsMultiStreamEnabled)
        {
            return [selectedVideoTab];
        }

        var selectedIndex = Tabs.IndexOf(selectedVideoTab);
        var pageStart = Math.Max(0, selectedIndex / VideoGridLayoutCalculator.TileLimit * VideoGridLayoutCalculator.TileLimit);
        return Tabs
            .Skip(pageStart)
            .Where(tab => !tab.IsDetached)
            .Take(VideoGridLayoutCalculator.TileLimit)
            .ToList();
    }

    private void ApplyInactivePlaybackPolicyInBackground()
    {
        inactivePlaybackPolicyController.Request();
    }

    private async Task ApplyInactivePlaybackPolicyPassAsync(long generation)
    {
        if (disposed || !inactivePlaybackPolicyController.IsCurrent(generation))
        {
            return;
        }

        Interlocked.Increment(ref inactivePlaybackPolicyApplyPassCount);
        try
        {
            var tabs = Tabs.ToArray();
            foreach (var tab in tabs)
            {
                if (!inactivePlaybackPolicyController.IsCurrent(generation))
                {
                    return;
                }
                if (!Tabs.Contains(tab))
                {
                    continue;
                }

                try
                {
                    if (ShouldKeepTabRunning(tab))
                    {
                        await tab.ResumeFromTabSwitchAsync();
                    }
                    else
                    {
                        await tab.PauseForTabSwitchAsync();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Write(AppLogLevel.Warning, "UI", $"Failed to apply playback visibility policy for {tab.Target.DisplayName}.", ex);
                }

                if (!inactivePlaybackPolicyController.IsCurrent(generation))
                {
                    return;
                }
            }
        }
        finally
        {
            // Resume/pause operations may themselves touch audio. Only the current pass may win,
            // and it always finishes by reasserting the current main/PiP audio owner.
            if (!disposed && inactivePlaybackPolicyController.IsCurrent(generation) &&
                audioActiveTab is { } selected && Tabs.Contains(selected))
            {
                ApplyAudioOwnerState(selected);
            }
        }
    }

    private bool ShouldKeepTabRunning(StreamTabViewModel tab)
    {
        // Let a stream finish its initial resolve/start even if the user changes
        // pages while it is still loading. Once startup completes, the normal
        // off-grid pause policy applies without interrupting the initial handoff.
        return tab.NeverMute || Settings.KeepInactiveTabsRunning || tab.IsVideoVisible || tab.IsDetached || tab.IsBusy;
    }

    private void ApplySelectedTabSelection()
    {
        var selected = selectedTab is not null && Tabs.Contains(selectedTab) ? selectedTab : null;
        var audioSelected = audioActiveTab is not null && Tabs.Contains(audioActiveTab) ? audioActiveTab : null;
        var wasApplyingSelectedTabSelection = applyingSelectedTabSelection;
        applyingSelectedTabSelection = true;
        try
        {
            if (selected is not null)
            {
                if (!selected.IsSelected)
                {
                    selected.IsSelected = true;
                }
            }

            foreach (var tab in Tabs)
            {
                if (!ReferenceEquals(tab, audioSelected))
                {
                    if (!tab.SetSelectedForAudio(false))
                    {
                        tab.ReapplyAudio();
                    }
                }

                if (!ReferenceEquals(tab, selected) && tab.IsSelected)
                {
                    tab.IsSelected = false;
                }
            }

            if (audioSelected is not null)
            {
                ApplyAudioOwnerState(audioSelected);
            }
        }
        finally
        {
            applyingSelectedTabSelection = wasApplyingSelectedTabSelection;
        }
    }

    private void ApplyImmediateAudioOwnerState(StreamTabViewModel? previous, StreamTabViewModel? selected)
    {
        selected = selected is not null && Tabs.Contains(selected) ? selected : null;
        var wasApplyingSelectedTabSelection = applyingSelectedTabSelection;
        applyingSelectedTabSelection = true;
        try
        {
            if (previous is not null &&
                !ReferenceEquals(previous, selected) &&
                Tabs.Contains(previous))
            {
                if (!previous.SetSelectedForAudio(false))
                {
                    previous.ReapplyAudio();
                }
            }

            if (selected is not null)
            {
                ApplyAudioOwnerState(selected);
            }
        }
        finally
        {
            applyingSelectedTabSelection = wasApplyingSelectedTabSelection;
        }
    }

    private static void ApplyAudioOwnerState(StreamTabViewModel selected)
    {
        if (!selected.SetSelectedForAudio(true))
        {
            selected.ReapplyAudio();
        }
    }

    private sealed record StreamCandidateProbe(
        StreamTarget Target,
        StreamlinkProbeResult Result,
        StreamMetadataResult? Metadata = null,
        StreamSearchChannel? Channel = null,
        int? ViewerCount = null);

    private static string GetTabDetachedStatusMessage(IReadOnlyList<StreamTabViewModel> tabs, bool detached)
    {
        if (tabs.Count == 1)
        {
            return detached
                ? $"{tabs[0].Target.DisplayName} detached to picture-in-picture"
                : $"{tabs[0].Target.DisplayName} returned to the main window";
        }

        return detached
            ? $"{tabs.Count} streams detached to picture-in-picture"
            : $"{tabs.Count} streams returned to the main window";
    }
}
