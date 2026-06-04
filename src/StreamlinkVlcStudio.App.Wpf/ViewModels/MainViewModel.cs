using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan DetachedDisposalWaitTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultRecentThumbnailRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultStreamSearchDebounceInterval = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan DefaultTwitchVodSearchDebounceInterval = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan DefaultBrowseCategorySearchDebounceInterval = TimeSpan.FromMilliseconds(450);
    private const int BrowseCategoryPageSize = 10;
    private const int BrowseCategoryViewerCountBatchSize = 1;
    private const int BrowseCategoryViewerCountConcurrency = 4;
    private const int BrowseStreamPageSize = 50;
    private const int VlcPluginMultiViewChatDisableThreshold = 3;
    private readonly ISettingsService settingsService;
    private readonly IStreamlinkService streamlinkService;
    private readonly IPlaybackEngineFactory playbackFactory;
    private readonly IChatClientFactory chatFactory;
    private readonly IViewerCountService? viewerCountService;
    private readonly IReplayResolver? replayResolver;
    private readonly IReplayChatProvider? replayChatProvider;
    private readonly IKickChatHistoryProvider? kickChatHistoryProvider;
    private readonly IFollowedStreamsService? followedStreamsService;
    private readonly IStreamMetadataService? streamMetadataService;
    private readonly ITwitchVodService? twitchVodService;
    private readonly IBrowseService? browseService;
    private readonly TimeSpan recentThumbnailRefreshInterval;
    private readonly TimeSpan streamSearchDebounceInterval;
    private readonly TimeSpan twitchVodSearchDebounceInterval;
    private readonly TimeSpan browseCategorySearchDebounceInterval;
    private readonly IAppLogger logger;
    private readonly Action<Action> dispatch;
    private EventHandler<LogEntry>? loggerEntryWrittenHandler;
    private readonly object detachedDisposalsGate = new();
    private readonly object tabStartsGate = new();
    private readonly object recentStreamHintsGate = new();
    private readonly object recentThumbnailRefreshTimerGate = new();
    private readonly object streamSearchGate = new();
    private readonly object twitchVodSearchGate = new();
    private readonly object browseCategorySearchGate = new();
    private readonly object browseCategoryViewerCountGate = new();
    private readonly object browseStreamSearchGate = new();
    private readonly SemaphoreSlim streamOpenGate = new(1, 1);
    private readonly SemaphoreSlim chatSettingsApplyGate = new(1, 1);
    private readonly SemaphoreSlim vlcPluginMultiViewChatPolicyGate = new(1, 1);
    private readonly SemaphoreSlim recentStreamsGate = new(1, 1);
    private readonly SemaphoreSlim recentThumbnailRefreshGate = new(1, 1);
    private readonly CancellationTokenSource recentThumbnailRefreshCancellation = new();
    private readonly List<Task> detachedDisposals = [];
    private readonly List<List<StreamTabViewModel>> multiViewTabGroups = [];
    private readonly List<List<StreamTabViewModel>> pictureInPictureTabGroups = [];
    private readonly List<List<StreamTabViewModel>> pictureInPictureVisibleTabGroups = [];
    private readonly Dictionary<string, RecentStreamHint> recentStreamHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecentStreamLiveStatus> recentStreamLiveStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> activeTabStarts = [];
    private readonly HashSet<StreamTabViewModel> vlcPluginMultiViewChatPolicyHiddenTabs = [];
    private StreamTabViewModel? selectedTab;
    private TabStripItemViewModel? selectedTabStripItem;
    private string newStreamText = "";
    private string streamSearchStatus = "";
    private string selectedQuality;
    private string statusMessage = "Ready";
    private string browserClickStatus = "Browser extension capture keeps Twitch and Kick on their home pages";
    private string followedChannelsStatus = "Live followed channels are not loaded";
    private string twitchVodSearchText = "";
    private string twitchVodStatus = "Search a Twitch streamer to browse VODs.";
    private string browseCategorySearchText = "";
    private string browseStatus = "Browse Twitch or Kick categories.";
    private string browseCategoryStatus = "Browse Twitch or Kick categories.";
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
    private bool isStreamSearchRunning;
    private bool hasStreamSearchCompleted;
    private bool isStreamSearchDropdownOpen;
    private bool isFollowedChannelsRefreshing;
    private bool isTwitchVodSearchRunning;
    private bool hasTwitchVodSearchCompleted;
    private bool isBrowseCategoriesLoading;
    private bool hasBrowseCategorySearchCompleted;
    private bool isBrowseStreamsLoading;
    private bool hasBrowseStreamSearchCompleted;
    private bool isReplaySeekBarUiVisible = true;
    private bool isStreamOnlyFullscreenActive;
    private bool isVideoFullscreenActive;
    private bool suppressInactiveTabPause;
    private bool applyingSelectedTabSelection;
    private bool disposed;
    private int streamSearchGeneration;
    private System.Threading.Timer? recentThumbnailRefreshTimer;
    private System.Threading.Timer? streamSearchDebounceTimer;
    private System.Threading.Timer? twitchVodSearchDebounceTimer;
    private CancellationTokenSource? streamSearchCancellation;
    private CancellationTokenSource? twitchVodSearchCancellation;
    private CancellationTokenSource? browseCategorySearchCancellation;
    private CancellationTokenSource? browseCategoryViewerCountCancellation;
    private CancellationTokenSource? browseStreamSearchCancellation;
    private bool browseCategoryViewerCountLoadPending;
    private TwitchVodTypeFilter selectedTwitchVodType = TwitchVodTypeFilter.Archive;
    private PlatformKind selectedBrowsePlatform = PlatformKind.Twitch;
    private BrowseCategoryViewModel? selectedBrowseCategory;
    private string twitchVodNextCursor = "";
    private int twitchVodSearchGeneration;
    private int browseCategorySearchGeneration;
    private int browseCategoryViewerCountGeneration;
    private int browseStreamSearchGeneration;
    private int videoGridRows = VideoGridLayoutCalculator.BaseGridSize;
    private int videoGridColumns = VideoGridLayoutCalculator.BaseGridSize;
    private System.Threading.Timer? browseCategorySearchDebounceTimer;

    public MainViewModel(
        AppSettings settings,
        ISettingsService settingsService,
        IStreamlinkService streamlinkService,
        IPlaybackEngineFactory playbackFactory,
        IChatClientFactory chatFactory,
        IAppLogger logger,
        Action<Action> dispatch,
        IViewerCountService? viewerCountService = null,
        IFollowedStreamsService? followedStreamsService = null,
        IStreamMetadataService? streamMetadataService = null,
        IReplayResolver? replayResolver = null,
        IReplayChatProvider? replayChatProvider = null,
        TimeSpan? recentThumbnailRefreshInterval = null,
        TimeSpan? streamSearchDebounceInterval = null,
        ITwitchVodService? twitchVodService = null,
        TimeSpan? twitchVodSearchDebounceInterval = null,
        IBrowseService? browseService = null,
        TimeSpan? browseCategorySearchDebounceInterval = null,
        IKickChatHistoryProvider? kickChatHistoryProvider = null)
    {
        Settings = settings;
        this.settingsService = settingsService;
        this.streamlinkService = streamlinkService;
        this.playbackFactory = playbackFactory;
        this.chatFactory = chatFactory;
        this.viewerCountService = viewerCountService;
        this.replayResolver = replayResolver;
        this.replayChatProvider = replayChatProvider;
        this.kickChatHistoryProvider = kickChatHistoryProvider;
        this.followedStreamsService = followedStreamsService;
        this.streamMetadataService = streamMetadataService;
        this.twitchVodService = twitchVodService;
        this.browseService = browseService;
        this.recentThumbnailRefreshInterval = recentThumbnailRefreshInterval ?? DefaultRecentThumbnailRefreshInterval;
        this.streamSearchDebounceInterval = streamSearchDebounceInterval ?? DefaultStreamSearchDebounceInterval;
        this.twitchVodSearchDebounceInterval = twitchVodSearchDebounceInterval ?? DefaultTwitchVodSearchDebounceInterval;
        this.browseCategorySearchDebounceInterval = browseCategorySearchDebounceInterval ?? DefaultBrowseCategorySearchDebounceInterval;
        this.logger = logger;
        this.dispatch = dispatch;
        selectedQuality = settings.DefaultQuality;
        kickFollowedChannelsText = FormatKickFollowedChannelsText(settings.FollowedChannels.KickChannelSlugs);

        AddAndPlayCommand = new AsyncRelayCommand(AddAndPlayAsync, () => HasNewStreamSearchText);
        SelectHomeCommand = new RelayCommand(SelectHome);
        ShowFollowedHomePageCommand = new RelayCommand(ShowFollowedHomePage);
        ShowTwitchVodsHomePageCommand = new RelayCommand(ShowTwitchVodsHomePage);
        ShowRecentHomePageCommand = new RelayCommand(ShowRecentHomePage);
        ShowBrowseHomePageCommand = new RelayCommand(ShowBrowseHomePage);
        ReturnToBrowseCategoriesCommand = new RelayCommand(ReturnToBrowseCategoriesPage, () => IsBrowseStreamsPageVisible);
        RefreshFollowedChannelsCommand = new AsyncRelayCommand(RefreshFollowedChannelsAsync, () => followedStreamsService is not null);
        SearchTwitchVodsCommand = new AsyncRelayCommand(
            () => SearchTwitchVodsAsync(reset: true),
            () => twitchVodService is not null && HasTwitchVodSearchText);
        LoadMoreTwitchVodsCommand = new AsyncRelayCommand(
            () => SearchTwitchVodsAsync(reset: false),
            () => twitchVodService is not null && CanLoadMoreTwitchVods);
        ShowPastBroadcastsVodFilterCommand = new RelayCommand(() => SelectTwitchVodType(TwitchVodTypeFilter.Archive));
        ShowHighlightsVodFilterCommand = new RelayCommand(() => SelectTwitchVodType(TwitchVodTypeFilter.Highlight));
        ShowUploadsVodFilterCommand = new RelayCommand(() => SelectTwitchVodType(TwitchVodTypeFilter.Upload));
        ShowAllVodFilterCommand = new RelayCommand(() => SelectTwitchVodType(TwitchVodTypeFilter.All));
        SelectTwitchBrowsePlatformCommand = new RelayCommand(() => SelectBrowsePlatform(PlatformKind.Twitch));
        SelectKickBrowsePlatformCommand = new RelayCommand(() => SelectBrowsePlatform(PlatformKind.Kick));
        RefreshBrowseCommand = new AsyncRelayCommand(RefreshBrowseAsync, () => browseService is not null);
        LoadMoreBrowseCategoriesCommand = new AsyncRelayCommand(
            () => LoadBrowseCategoriesAsync(reset: false),
            () => browseService is not null && CanLoadMoreBrowseCategories);
        LoadMoreBrowseStreamsCommand = new AsyncRelayCommand(
            () => LoadBrowseStreamsAsync(reset: false),
            () => browseService is not null && CanLoadMoreBrowseStreams);
        PlaySelectedCommand = new AsyncRelayCommand(PlaySelectedAsync, () => SelectedTab is not null);
        ReloadSelectedCommand = new AsyncRelayCommand(ReloadSelectedAsync, () => SelectedTab is not null);
        StopSelectedCommand = new AsyncRelayCommand(StopSelectedAsync, () => SelectedTab is not null);
        PauseSelectedCommand = new AsyncRelayCommand(PauseSelectedAsync, () => SelectedTab is not null);
        CloseSelectedCommand = new AsyncRelayCommand(CloseSelectedAsync, () => SelectedTab is not null);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        AuthorizeTwitchCommand = new AsyncRelayCommand(AuthorizeTwitchAsync);
        ClearTwitchTokenCommand = new AsyncRelayCommand(ClearTwitchTokenAsync, HasTwitchToken);
        AuthorizeKickCommand = new AsyncRelayCommand(AuthorizeKickAsync);
        ClearKickTokenCommand = new AsyncRelayCommand(ClearKickTokenAsync, HasKickToken);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        ToggleMultiStreamCommand = new RelayCommand(ToggleMultiStream);
        ToggleReplaySeekBarCommand = new RelayCommand(ToggleReplaySeekBar);
        ToggleChatCommand = new AsyncRelayCommand(ToggleChatAsync, () => SelectedTab is not null);
        MoveTabLeftCommand = new RelayCommand(MoveTabLeft, () => SelectedTab is not null && Tabs.IndexOf(SelectedTab) > 0);
        MoveTabRightCommand = new RelayCommand(MoveTabRight, () => SelectedTab is not null && Tabs.IndexOf(SelectedTab) < Tabs.Count - 1);
        RebuildRecentStreams();
        Tabs.CollectionChanged += TabsOnCollectionChanged;
        StreamSearchResults.CollectionChanged += StreamSearchResultsOnCollectionChanged;
        LiveFollowedChannels.CollectionChanged += LiveFollowedChannelsOnCollectionChanged;
        TwitchVods.CollectionChanged += TwitchVodsOnCollectionChanged;
        RecentStreams.CollectionChanged += RecentStreamsOnCollectionChanged;
        BrowseCategories.CollectionChanged += BrowseCategoriesOnCollectionChanged;
        BrowseStreams.CollectionChanged += BrowseStreamsOnCollectionChanged;
        Settings.PropertyChanged += SettingsOnPropertyChanged;
        Settings.Chat.PropertyChanged += ChatSettingsOnPropertyChanged;
    }

    public AppSettings Settings { get; }
    public ObservableCollection<StreamTabViewModel> Tabs { get; } = [];
    public ObservableCollection<TabStripItemViewModel> TabStripItems { get; } = [];
    public ObservableCollection<StreamTabViewModel> VideoTabs { get; } = [];
    public ObservableCollection<StreamSearchResultViewModel> StreamSearchResults { get; } = [];
    public ObservableCollection<FollowedChannelViewModel> LiveFollowedChannels { get; } = [];
    public ObservableCollection<TwitchVodViewModel> TwitchVods { get; } = [];
    public ObservableCollection<RecentStreamViewModel> RecentStreams { get; } = [];
    public ObservableCollection<BrowseCategoryViewModel> BrowseCategories { get; } = [];
    public ObservableCollection<BrowseLiveStreamViewModel> BrowseStreams { get; } = [];
    public IReadOnlyList<ChatLayout> ChatLayoutOptions { get; } = Enum.GetValues<ChatLayout>();
    public IReadOnlyList<QualityOption> QualityOptions { get; } = QualityOption.Defaults;
    public ObservableCollection<string> AppLogLines { get; } = [];

    public AsyncRelayCommand AddAndPlayCommand { get; }
    public RelayCommand SelectHomeCommand { get; }
    public RelayCommand ShowFollowedHomePageCommand { get; }
    public RelayCommand ShowTwitchVodsHomePageCommand { get; }
    public RelayCommand ShowRecentHomePageCommand { get; }
    public RelayCommand ShowBrowseHomePageCommand { get; }
    public RelayCommand ReturnToBrowseCategoriesCommand { get; }
    public AsyncRelayCommand RefreshFollowedChannelsCommand { get; }
    public AsyncRelayCommand SearchTwitchVodsCommand { get; }
    public AsyncRelayCommand LoadMoreTwitchVodsCommand { get; }
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
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand AuthorizeTwitchCommand { get; }
    public AsyncRelayCommand ClearTwitchTokenCommand { get; }
    public AsyncRelayCommand AuthorizeKickCommand { get; }
    public AsyncRelayCommand ClearKickTokenCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ToggleMultiStreamCommand { get; }
    public RelayCommand ToggleReplaySeekBarCommand { get; }
    public AsyncRelayCommand ToggleChatCommand { get; }
    public RelayCommand MoveTabLeftCommand { get; }
    public RelayCommand MoveTabRightCommand { get; }

    public StreamTabViewModel? SelectedTab
    {
        get => selectedTab;
        set
        {
            if (selectedTab == value)
            {
                ApplySelectedTabSelection();
                ApplyVideoLayout();
                return;
            }

            var previous = selectedTab;
            if (previous is not null)
            {
                previous.PropertyChanged -= SelectedTabOnPropertyChanged;
            }

            selectedTab = value;
            ApplyImmediateSelectedTabAudioState(previous, selectedTab);

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
            OnPropertyChanged(nameof(IsSelectedTabDetached));
            OnPropertyChanged(nameof(IsReplaySeekBarVisible));
            RaiseChatVisibilityProperties();
            RaiseCommandStates();
            ApplySelectedTabSelection();
            ApplyVideoLayout();
            if (!suppressInactiveTabPause)
            {
                ApplyInactivePlaybackPolicyInBackground();
            }
        }
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
                streamSearchGeneration++;
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
                twitchVodSearchGeneration++;
                CancelTwitchVodSearchDebounce();
                CancelActiveTwitchVodSearch();
                IsTwitchVodSearchRunning = false;
                ClearTwitchVodSearchResults();
                OnPropertyChanged(nameof(HasTwitchVodSearchText));
                OnPropertyChanged(nameof(IsTwitchVodSearchPlaceholderVisible));
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

    public bool CanLoadMoreTwitchVods => !IsTwitchVodSearchRunning &&
        !string.IsNullOrWhiteSpace(TwitchVodNextCursor);

    public bool IsTwitchVodLoadMoreVisible => HasTwitchVods && CanLoadMoreTwitchVods;

    public string TwitchVodResultsTitle => TwitchVods.Count switch
    {
        0 => "Twitch VODs",
        1 => "1 Twitch VOD",
        _ => $"{TwitchVods.Count} Twitch VODs"
    };

    public string BrowseCategorySearchText
    {
        get => browseCategorySearchText;
        set
        {
            if (SetProperty(ref browseCategorySearchText, value ?? ""))
            {
                browseCategorySearchGeneration++;
                browseCategoryViewerCountGeneration++;
                browseStreamSearchGeneration++;
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

    public string BrowserClickStatus
    {
        get => browserClickStatus;
        private set => SetProperty(ref browserClickStatus, value);
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

    public bool IsSettingsOpen
    {
        get => isSettingsOpen;
        set => SetProperty(ref isSettingsOpen, value);
    }

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

    public bool IsOverlayChatInputVisible => !IsVideoFullscreenActive &&
        !IsStreamOnlyFullscreenActive &&
        SelectedTab is { IsChatVisible: true, IsDockedChatOverrideActive: false } tab &&
        Settings.Chat.Layout == ChatLayout.Overlay &&
        !IsVlcPluginOverlayConfigured(Settings.Chat) &&
        tab.UsesNativeOverlay == false;

    public bool IsAnyStreamPlaying => Tabs.Any(tab => tab.Status == PlaybackStatus.Playing);

    public bool HasSelectedKickTab => SelectedTab?.Target.Platform == PlatformKind.Kick;

    public bool HasSelectedTab => SelectedTab is not null;

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
            tab.RefreshChatOverlay(Settings.Chat);
            RaiseChatVisibilityProperties();
        }
    }

    public string SelectedKickChatroomId
    {
        get => GetSelectedKickSetting(Settings.Chat.KickChatroomIds);
        set => SetSelectedKickSetting(Settings.Chat.KickChatroomIds, value, nameof(SelectedKickChatroomId));
    }

    public string SelectedKickBroadcasterUserId
    {
        get => GetSelectedKickSetting(Settings.Chat.KickBroadcasterUserIds);
        set => SetSelectedKickSetting(Settings.Chat.KickBroadcasterUserIds, value, nameof(SelectedKickBroadcasterUserId));
    }

    public void Initialize()
    {
        if (loggerEntryWrittenHandler is null)
        {
            loggerEntryWrittenHandler = (_, entry) =>
            {
                dispatch(() =>
                {
                    AppLogLines.Add($"{entry.Timestamp:HH:mm:ss} [{entry.Level}] {entry.Source}: {entry.Message}");
                    while (AppLogLines.Count > 250)
                    {
                        AppLogLines.RemoveAt(0);
                    }
                });
            };
            logger.EntryWritten += loggerEntryWrittenHandler;
        }

        if (followedStreamsService is not null)
        {
            _ = RefreshFollowedChannelsAsync();
        }
    }

    public void RefreshSettingsBindings()
    {
        OnPropertyChanged(nameof(Settings));
    }

    public void SetBrowserClickStatus(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            BrowserClickStatus = message;
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
            ApplySelectedTabSelection();
            ApplyVideoLayout();
            ApplyInactivePlaybackPolicyInBackground();
        }

        StatusMessage = "Home";
    }

    private void ShowFollowedHomePage()
    {
        CancelActiveBrowseCategoryViewerCountLoad();
        IsRecentHomePageSelected = false;
        IsTwitchVodsHomePageSelected = false;
        IsBrowseHomePageSelected = false;
        StatusMessage = "Live followed channels";
    }

    private void ShowTwitchVodsHomePage()
    {
        CancelActiveBrowseCategoryViewerCountLoad();
        IsRecentHomePageSelected = false;
        IsTwitchVodsHomePageSelected = true;
        IsBrowseHomePageSelected = false;
        StatusMessage = "Twitch VODs";
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
    }

    private void ShowBrowseCategoriesPage(bool clearSelection)
    {
        SetBrowseStreamsPageSelected(false);
        if (!clearSelection)
        {
            return;
        }

        browseStreamSearchGeneration++;
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
    }

    private void SetBrowseStreamsPageSelected(bool value)
    {
        if (isBrowseStreamsPageSelected == value)
        {
            return;
        }

        isBrowseStreamsPageSelected = value;
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

    private async Task RefreshFollowedChannelsAsync()
    {
        if (followedStreamsService is null)
        {
            FollowedChannelsStatus = "Live followed channels are not available.";
            return;
        }

        try
        {
            Settings.FollowedChannels.KickChannelSlugs = ParseKickFollowedChannelSlugs(
                KickFollowedChannelsText,
                skipInvalidEntries: true,
                out var invalidKickFollowedEntries);
            IsFollowedChannelsRefreshing = true;
            FollowedChannelsStatus = "Refreshing live followed channels";

            var result = await followedStreamsService.GetLiveFollowedStreamsAsync(Settings);
            LiveFollowedChannels.Clear();
            foreach (var stream in result.Streams)
            {
                LiveFollowedChannels.Add(new FollowedChannelViewModel(stream, OpenFollowedChannelAsync));
            }

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FollowedChannelsStatus = ex.Message;
            logger.Write(AppLogLevel.Warning, "Followed", "Failed to refresh live followed channels.", ex);
        }
        finally
        {
            IsFollowedChannelsRefreshing = false;
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
        if (HasTwitchVodSearchText)
        {
            _ = SearchTwitchVodsAsync(reset: true);
        }
    }

    private async Task SearchTwitchVodsAsync(bool reset)
    {
        if (twitchVodService is null)
        {
            TwitchVodStatus = "Twitch VOD search is not available.";
            return;
        }

        var query = TwitchVodSearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            TwitchVodStatus = "Enter a Twitch streamer login.";
            return;
        }

        var type = SelectedTwitchVodType;
        var cursor = reset ? "" : TwitchVodNextCursor;
        if (!reset && string.IsNullOrWhiteSpace(cursor))
        {
            return;
        }

        var searchGeneration = reset ? ++twitchVodSearchGeneration : twitchVodSearchGeneration;
        var searchCancellation = ReplaceTwitchVodSearchCancellation();
        if (reset)
        {
            TwitchVods.Clear();
            TwitchVodNextCursor = "";
            HasTwitchVodSearchCompleted = false;
        }

        IsTwitchVodSearchRunning = true;
        TwitchVodStatus = reset
            ? $"Searching Twitch VODs for {query}"
            : $"Loading more Twitch VODs for {query}";
        StatusMessage = TwitchVodStatus;

        try
        {
            var result = await twitchVodService.SearchAsync(
                new TwitchVodSearchRequest(query, type, cursor, 100),
                Settings,
                searchCancellation.Token);
            if (!IsCurrentTwitchVodSearch(searchGeneration, query, type))
            {
                return;
            }

            foreach (var vod in result.Videos)
            {
                TwitchVods.Add(new TwitchVodViewModel(vod, OpenTwitchVodAsync));
            }

            TwitchVodNextCursor = result.NextCursor;
            HasTwitchVodSearchCompleted = true;
            TwitchVodStatus = result.Message;
            StatusMessage = result.Message;
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentTwitchVodSearch(searchGeneration, query, type))
            {
                return;
            }

            HasTwitchVodSearchCompleted = true;
            TwitchVodStatus = ex.Message;
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "VODs", "Twitch VOD search failed.", ex);
        }
        finally
        {
            if (IsCurrentTwitchVodSearch(searchGeneration, query, type))
            {
                IsTwitchVodSearchRunning = false;
            }

            DisposeTwitchVodSearchCancellation(searchCancellation);
        }
    }

    private async Task OpenTwitchVodAsync(TwitchVodViewModel vod, bool stayOnHome)
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
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "VODs", $"Failed to open Twitch VOD {vod.Id}.", ex);
        }
    }

    private bool IsCurrentTwitchVodSearch(int searchGeneration, string query, TwitchVodTypeFilter type)
    {
        return twitchVodSearchGeneration == searchGeneration &&
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
            TwitchVodStatus = "Search a Twitch streamer to browse VODs.";
        }
    }

    private void ScheduleAutomaticTwitchVodSearch()
    {
        var query = TwitchVodSearchText.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var searchGeneration = twitchVodSearchGeneration;
        var type = SelectedTwitchVodType;
        if (twitchVodSearchDebounceInterval <= TimeSpan.Zero)
        {
            dispatch(() => _ = RunAutomaticTwitchVodSearchAsync(query, type, searchGeneration));
            return;
        }

        lock (twitchVodSearchGate)
        {
            twitchVodSearchDebounceTimer?.Dispose();
            twitchVodSearchDebounceTimer = new System.Threading.Timer(
                _ => dispatch(() => _ = RunAutomaticTwitchVodSearchAsync(query, type, searchGeneration)),
                null,
                twitchVodSearchDebounceInterval,
                Timeout.InfiniteTimeSpan);
        }
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
        lock (twitchVodSearchGate)
        {
            twitchVodSearchDebounceTimer?.Dispose();
            twitchVodSearchDebounceTimer = null;
        }
    }

    private CancellationTokenSource ReplaceTwitchVodSearchCancellation()
    {
        lock (twitchVodSearchGate)
        {
            twitchVodSearchCancellation?.Cancel();
            twitchVodSearchCancellation = new CancellationTokenSource();
            return twitchVodSearchCancellation;
        }
    }

    private void CancelActiveTwitchVodSearch()
    {
        lock (twitchVodSearchGate)
        {
            twitchVodSearchCancellation?.Cancel();
            twitchVodSearchCancellation = null;
        }
    }

    private void DisposeTwitchVodSearchCancellation(CancellationTokenSource cancellation)
    {
        lock (twitchVodSearchGate)
        {
            if (ReferenceEquals(twitchVodSearchCancellation, cancellation))
            {
                twitchVodSearchCancellation = null;
            }
        }

        cancellation.Dispose();
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

        browseCategorySearchGeneration++;
        browseCategoryViewerCountGeneration++;
        browseStreamSearchGeneration++;
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
        _ = LoadBrowseCategoriesAsync(reset: true);
    }

    private async Task RefreshBrowseAsync()
    {
        if (IsBrowseStreamsPageVisible && SelectedBrowseCategory is not null)
        {
            await LoadBrowseStreamsAsync(reset: true);
            return;
        }

        await LoadBrowseCategoriesAsync(reset: true);
    }

    private async Task LoadBrowseCategoriesAsync(bool reset)
    {
        if (browseService is null)
        {
            BrowseStatus = "Browse is not available.";
            return;
        }

        var platform = SelectedBrowsePlatform;
        var query = BrowseCategorySearchText.Trim();
        var cursor = reset ? "" : BrowseCategoryNextCursor;
        if (!reset && string.IsNullOrWhiteSpace(cursor))
        {
            return;
        }

        var searchGeneration = reset ? ++browseCategorySearchGeneration : browseCategorySearchGeneration;
        var searchCancellation = ReplaceBrowseCategorySearchCancellation();
        if (reset)
        {
            browseCategoryViewerCountGeneration++;
            CancelActiveBrowseCategoryViewerCountLoad();
            SetBrowseStreamsPageSelected(false);
            ClearBrowseCategories(clearStatus: false);
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
            var result = await browseService.GetCategoriesAsync(
                new BrowseCategoryRequest(platform, query, cursor, BrowseCategoryPageSize),
                Settings,
                searchCancellation.Token);
            if (!IsCurrentBrowseCategorySearch(searchGeneration, platform, query))
            {
                return;
            }

            AppendBrowseCategories(result.Items);
            if (platform == PlatformKind.Kick)
            {
                SortBrowseCategoriesByViewerCount();
            }

            BrowseCategoryNextCursor = result.NextCursor;
            HasBrowseCategorySearchCompleted = true;
            BrowseStatus = result.Message;
            browseCategoryStatus = result.Message;
            StatusMessage = result.Message;
            StartBrowseCategoryViewerCountLoad(platform, query);
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

    private void AppendBrowseCategories(IReadOnlyList<BrowseCategory> categories)
    {
        var existing = BrowseCategories
            .Select(category => $"{category.Platform}:{category.Id}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            var key = $"{category.Platform}:{category.Id}";
            if (!existing.Add(key))
            {
                continue;
            }

            BrowseCategories.Add(new BrowseCategoryViewModel(category, SelectBrowseCategoryAsync));
        }
    }

    private void SortBrowseCategoriesByViewerCount()
    {
        var sortedCategories = BrowseCategories
            .OrderBy(category => category.Category.ViewerCount is null ? 1 : 0)
            .ThenByDescending(category => category.Category.ViewerCount ?? 0)
            .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
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

            viewerCountGeneration = ++browseCategoryViewerCountGeneration;
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
            var shouldStartPendingLoad = !cancellation.IsCancellationRequested &&
                IsCurrentBrowseCategoryViewerCountLoad(viewerCountGeneration, platform, query) &&
                DisposeBrowseCategoryViewerCountCancellation(cancellation);
            if (shouldStartPendingLoad)
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
        if (category.Platform != SelectedBrowsePlatform)
        {
            return;
        }

        browseCategorySearchGeneration++;
        CancelBrowseCategorySearchDebounce();
        CancelActiveBrowseCategorySearch();
        CancelActiveBrowseCategoryViewerCountLoad();
        browseStreamSearchGeneration++;
        CancelActiveBrowseStreamSearch();
        SelectedBrowseCategory = category;
        SetBrowseStreamsPageSelected(true);
        ClearBrowseStreams(clearSelectedCategory: false);
        HasBrowseStreamSearchCompleted = false;
        BrowseStatus = $"Loading live streams in {category.Name}";
        StatusMessage = BrowseStatus;
        await LoadBrowseStreamsAsync(reset: true);
    }

    private async Task LoadBrowseStreamsAsync(bool reset)
    {
        if (browseService is null)
        {
            BrowseStatus = "Browse is not available.";
            return;
        }

        var category = SelectedBrowseCategory;
        if (category is null)
        {
            BrowseStatus = "Select a category first.";
            return;
        }

        var platform = SelectedBrowsePlatform;
        var categoryId = category.Id;
        var categoryName = category.Name;
        var cursor = reset ? "" : BrowseStreamNextCursor;
        if (!reset && string.IsNullOrWhiteSpace(cursor))
        {
            return;
        }

        var searchGeneration = reset ? ++browseStreamSearchGeneration : browseStreamSearchGeneration;
        var searchCancellation = ReplaceBrowseStreamSearchCancellation();
        if (reset)
        {
            ClearBrowseStreams(clearSelectedCategory: false);
            HasBrowseStreamSearchCompleted = false;
        }

        IsBrowseStreamsLoading = true;
        BrowseStatus = reset
            ? $"Loading live streams in {categoryName}"
            : $"Loading more live streams in {categoryName}";
        StatusMessage = BrowseStatus;

        try
        {
            var result = await browseService.GetStreamsAsync(
                new BrowseStreamRequest(platform, categoryId, categoryName, cursor, BrowseStreamPageSize),
                Settings,
                searchCancellation.Token);
            if (!IsCurrentBrowseStreamSearch(searchGeneration, platform, categoryId))
            {
                return;
            }

            AppendBrowseStreams(result.Items);
            BrowseStreamNextCursor = result.NextCursor;
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

    private void AppendBrowseStreams(IReadOnlyList<BrowseLiveStream> streams)
    {
        var existing = BrowseStreams
            .Select(stream => stream.Target.TabIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in streams)
        {
            if (!existing.Add(stream.Target.TabIdentityKey))
            {
                continue;
            }

            BrowseStreams.Add(new BrowseLiveStreamViewModel(stream, OpenBrowseStreamAsync));
        }
    }

    private async Task OpenBrowseStreamAsync(BrowseLiveStreamViewModel stream, bool stayOnHome)
    {
        try
        {
            if (!stayOnHome)
            {
                IsHomeSelected = false;
            }

            SetRecentStreamHint(stream.Target, stream.ThumbnailUrl, stream.DisplayName);
            await OpenCandidatesAsync([stream.Target], clearInputOnSuccess: false, selectOpenedTab: !stayOnHome);
        }
        catch (Exception ex)
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Browse", $"Failed to open {stream.Target.DisplayName} from browse.", ex);
        }
    }

    private bool IsCurrentBrowseCategorySearch(int searchGeneration, PlatformKind platform, string query)
    {
        return browseCategorySearchGeneration == searchGeneration &&
            SelectedBrowsePlatform == platform &&
            string.Equals(BrowseCategorySearchText.Trim(), query, StringComparison.Ordinal);
    }

    private bool IsCurrentBrowseCategoryViewerCountLoad(
        int viewerCountGeneration,
        PlatformKind platform,
        string query)
    {
        return browseCategoryViewerCountGeneration == viewerCountGeneration &&
            SelectedBrowsePlatform == platform &&
            string.Equals(BrowseCategorySearchText.Trim(), query, StringComparison.Ordinal);
    }

    private bool IsCurrentBrowseStreamSearch(int searchGeneration, PlatformKind platform, string categoryId)
    {
        return browseStreamSearchGeneration == searchGeneration &&
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
        if (browseService is null)
        {
            return;
        }

        var query = BrowseCategorySearchText.Trim();
        var platform = SelectedBrowsePlatform;
        var searchGeneration = browseCategorySearchGeneration;
        if (browseCategorySearchDebounceInterval <= TimeSpan.Zero)
        {
            dispatch(() => _ = RunAutomaticBrowseCategorySearchAsync(query, platform, searchGeneration));
            return;
        }

        lock (browseCategorySearchGate)
        {
            browseCategorySearchDebounceTimer?.Dispose();
            browseCategorySearchDebounceTimer = new System.Threading.Timer(
                _ => dispatch(() => _ = RunAutomaticBrowseCategorySearchAsync(query, platform, searchGeneration)),
                null,
                browseCategorySearchDebounceInterval,
                Timeout.InfiniteTimeSpan);
        }
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
        lock (browseCategorySearchGate)
        {
            browseCategorySearchDebounceTimer?.Dispose();
            browseCategorySearchDebounceTimer = null;
        }
    }

    private CancellationTokenSource ReplaceBrowseCategorySearchCancellation()
    {
        lock (browseCategorySearchGate)
        {
            browseCategorySearchCancellation?.Cancel();
            browseCategorySearchCancellation = new CancellationTokenSource();
            return browseCategorySearchCancellation;
        }
    }

    private CancellationTokenSource ReplaceBrowseStreamSearchCancellation()
    {
        lock (browseStreamSearchGate)
        {
            browseStreamSearchCancellation?.Cancel();
            browseStreamSearchCancellation = new CancellationTokenSource();
            return browseStreamSearchCancellation;
        }
    }

    private void CancelActiveBrowseCategorySearch()
    {
        lock (browseCategorySearchGate)
        {
            browseCategorySearchCancellation?.Cancel();
            browseCategorySearchCancellation = null;
        }
    }

    private void CancelActiveBrowseCategoryViewerCountLoad()
    {
        browseCategoryViewerCountGeneration++;
        lock (browseCategoryViewerCountGate)
        {
            browseCategoryViewerCountCancellation?.Cancel();
            browseCategoryViewerCountCancellation = null;
            browseCategoryViewerCountLoadPending = false;
        }
    }

    private void CancelActiveBrowseStreamSearch()
    {
        lock (browseStreamSearchGate)
        {
            browseStreamSearchCancellation?.Cancel();
            browseStreamSearchCancellation = null;
        }
    }

    private void DisposeBrowseCategorySearchCancellation(CancellationTokenSource cancellation)
    {
        lock (browseCategorySearchGate)
        {
            if (ReferenceEquals(browseCategorySearchCancellation, cancellation))
            {
                browseCategorySearchCancellation = null;
            }
        }

        cancellation.Dispose();
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
        lock (browseStreamSearchGate)
        {
            if (ReferenceEquals(browseStreamSearchCancellation, cancellation))
            {
                browseStreamSearchCancellation = null;
            }
        }

        cancellation.Dispose();
    }

    private void RaiseBrowseCommandStates()
    {
        ReturnToBrowseCategoriesCommand.RaiseCanExecuteChanged();
        RefreshBrowseCommand.RaiseCanExecuteChanged();
        LoadMoreBrowseCategoriesCommand.RaiseCanExecuteChanged();
        LoadMoreBrowseStreamsCommand.RaiseCanExecuteChanged();
    }

    private async Task OpenFollowedChannelAsync(FollowedChannelViewModel channel, bool stayOnHome)
    {
        try
        {
            if (!stayOnHome)
            {
                IsHomeSelected = false;
            }

            SetRecentStreamHint(channel.Target, channel.ThumbnailUrl, channel.DisplayName);
            await OpenCandidatesAsync([channel.Target], clearInputOnSuccess: false, selectOpenedTab: !stayOnHome);
        }
        catch (Exception ex)
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Followed", $"Failed to open {channel.Target.DisplayName} from home.", ex);
        }
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
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Recent", $"Failed to open {stream.Target.DisplayName} from recent streams.", ex);
        }
    }

    private async Task DeleteRecentStreamAsync(RecentStreamViewModel stream)
    {
        var target = stream.Target;
        try
        {
            await recentStreamsGate.WaitAsync();
            try
            {
                if (!Settings.RecentStreams.Any(recentStream => IsSameRecentStream(recentStream, target)))
                {
                    return;
                }

                Settings.RecentStreams = Settings.RecentStreams
                    .Where(recentStream => !IsSameRecentStream(recentStream, target))
                    .ToList();
                recentStreamLiveStatuses.Remove(target.StateKey);
                lock (recentStreamHintsGate)
                {
                    recentStreamHints.Remove(target.StateKey);
                }

                RebuildRecentStreams();
                StatusMessage = $"{target.DisplayName} removed from recent streams";
                await SaveRecentStreamRemovalAsync(target);
            }
            finally
            {
                recentStreamsGate.Release();
            }
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

        return [tab];
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

    public bool TryMergeTabsIntoMultiView(StreamTabViewModel draggedTab, StreamTabViewModel targetTab)
    {
        return TryMergeTabsIntoMultiView([draggedTab], targetTab, draggedTab);
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
            multiViewTabGroups.Add(targetGroup);
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
            pictureInPictureTabGroups.Add(validTabs.ToList());
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
            pictureInPictureVisibleTabGroups.Add(validTabs.ToList());
        }

        ApplyVlcPluginMultiViewChatPolicyInBackground(restoreWhenAllowed: true);
    }

    internal void ClearPictureInPictureVisibleTabGroup(IReadOnlyCollection<StreamTabViewModel> tabs)
    {
        RemoveTabsFromPictureInPictureVisibleGroups(tabs, applyPolicy: true);
    }

    private List<StreamTabViewModel>? GetMultiViewTabGroupList(StreamTabViewModel tab)
    {
        foreach (var group in multiViewTabGroups)
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
        if (tabs.Count == 0 || multiViewTabGroups.Count == 0)
        {
            return;
        }

        var tabsToRemove = tabs.ToHashSet();
        var changed = false;
        for (var index = 0; index < multiViewTabGroups.Count; index++)
        {
            var group = multiViewTabGroups[index];
            var originalCount = group.Count;
            group.RemoveAll(tabsToRemove.Contains);
            if (group.Count != originalCount)
            {
                changed = true;
            }

            if (group.Count <= 1)
            {
                multiViewTabGroups.RemoveAt(index);
                index--;
                changed = true;
            }
        }

        if (changed && applyLayout)
        {
            ApplyVideoLayout();
        }
    }

    private void RemoveTabsFromPictureInPictureVisibleGroups(IReadOnlyCollection<StreamTabViewModel> tabs, bool applyPolicy)
    {
        if (tabs.Count == 0 || pictureInPictureVisibleTabGroups.Count == 0)
        {
            return;
        }

        var tabsToRemove = tabs.ToHashSet();
        var changed = false;
        for (var index = 0; index < pictureInPictureVisibleTabGroups.Count; index++)
        {
            var group = pictureInPictureVisibleTabGroups[index];
            if (group.Any(tabsToRemove.Contains))
            {
                pictureInPictureVisibleTabGroups.RemoveAt(index);
                index--;
                changed = true;
            }
        }

        if (changed && applyPolicy)
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
        foreach (var group in pictureInPictureTabGroups)
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
        if (tabs.Count == 0 || pictureInPictureTabGroups.Count == 0)
        {
            return;
        }

        var tabsToRemove = tabs.ToHashSet();
        var changed = false;
        for (var index = 0; index < pictureInPictureTabGroups.Count; index++)
        {
            var group = pictureInPictureTabGroups[index];
            var originalCount = group.Count;
            group.RemoveAll(tabsToRemove.Contains);
            if (group.Count != originalCount)
            {
                changed = true;
            }

            if (group.Count <= 1)
            {
                pictureInPictureTabGroups.RemoveAt(index);
                index--;
                changed = true;
            }
        }

        if (changed && applyLayout)
        {
            ApplyVideoLayout();
        }
    }

    public async Task OpenDetectedStreamAsync(StreamTarget target)
    {
        await streamOpenGate.WaitAsync();
        try
        {
            BrowserClickStatus = $"Detected {target.DisplayName}";
            var existing = FindTab(target);
            if (existing is not null)
            {
                FocusOrStartExistingTab(existing, updateBrowserStatus: true);
                BrowserClickStatus = StatusMessage;
                return;
            }

            var tab = CreateAndSelectTab(target);
            StatusMessage = $"Starting {target.DisplayName}";
            BrowserClickStatus = StatusMessage;
            StartTabInBackground(tab, clearInputOnSuccess: false, updateBrowserStatus: true);
        }
        catch (Exception ex)
        {
            BrowserClickStatus = ex.Message;
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "UI", "Detected stream open failed.", ex);
        }
        finally
        {
            streamOpenGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (loggerEntryWrittenHandler is not null)
        {
            logger.EntryWritten -= loggerEntryWrittenHandler;
            loggerEntryWrittenHandler = null;
        }

        CancelStreamSearchDebounce();
        CancelActiveStreamSearch();
        CancelTwitchVodSearchDebounce();
        CancelActiveTwitchVodSearch();
        CancelBrowseCategorySearchDebounce();
        CancelActiveBrowseCategorySearch();
        CancelActiveBrowseCategoryViewerCountLoad();
        CancelActiveBrowseStreamSearch();
        recentThumbnailRefreshCancellation.Cancel();
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
        Settings.Chat.PropertyChanged -= ChatSettingsOnPropertyChanged;
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

        var tabDisposals = Tabs
            .ToArray()
            .Select(tab => tab.DisposeAsync().AsTask())
            .ToArray();
        if (tabDisposals.Length > 0)
        {
            await Task.WhenAll(tabDisposals);
        }

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

    private async Task AddAndPlayAsync()
    {
        var query = NewStreamText.Trim();
        var searchGeneration = ++streamSearchGeneration;
        CancelStreamSearchDebounce();
        await RunStreamSearchAsync(query, searchGeneration);
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

            var playableProbes = probes
                .Where(probe => probe.Result.HasPlayableStream)
                .ToArray();
            var enrichedPlayableProbes = await LoadStreamSearchResultMetadataAsync(
                playableProbes,
                searchCancellation.Token);
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            foreach (var probe in enrichedPlayableProbes)
            {
                StreamSearchResults.Add(new StreamSearchResultViewModel(
                    probe.Target,
                    probe.Result,
                    probe.Metadata,
                    OpenSearchResultAsync));
            }

            SetStreamSearchCompleted(true);
            StreamSearchStatus = playableProbes.Length == 0
                ? FormatNoPlayableStreamSearchResult(query, probes)
                : FormatPlayableStreamSearchResult(query, playableProbes.Length);
            StatusMessage = StreamSearchStatus;
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
        return await ProbeCandidatesAsync(candidates, customArguments, cancellationToken);
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

    private async Task OpenSearchResultAsync(StreamSearchResultViewModel result, bool stayOnHome)
    {
        try
        {
            if (!stayOnHome)
            {
                IsHomeSelected = false;
            }

            SetRecentStreamHint(result.Target, result.ThumbnailUrl, result.DisplayName);
            await OpenCandidatesAsync([result.Target], clearInputOnSuccess: true, selectOpenedTab: !stayOnHome);
        }
        catch (Exception ex)
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Search", $"Failed to open {result.Target.DisplayName} from search results.", ex);
        }
    }

    private bool IsCurrentStreamSearch(int searchGeneration, string query)
    {
        return streamSearchGeneration == searchGeneration &&
            string.Equals(NewStreamText.Trim(), query, StringComparison.Ordinal);
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
        var query = NewStreamText.Trim();
        var searchGeneration = streamSearchGeneration;
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

        lock (streamSearchGate)
        {
            streamSearchDebounceTimer?.Dispose();
            streamSearchDebounceTimer = new System.Threading.Timer(
                _ => dispatch(() => _ = RunAutomaticStreamSearchAsync(query, searchGeneration)),
                null,
                streamSearchDebounceInterval,
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task RunAutomaticStreamSearchAsync(string query, int searchGeneration)
    {
        if (disposed || !IsCurrentStreamSearch(searchGeneration, query))
        {
            return;
        }

        await RunStreamSearchAsync(query, searchGeneration);
    }

    private void CancelStreamSearchDebounce()
    {
        lock (streamSearchGate)
        {
            streamSearchDebounceTimer?.Dispose();
            streamSearchDebounceTimer = null;
        }
    }

    private CancellationTokenSource ReplaceStreamSearchCancellation()
    {
        lock (streamSearchGate)
        {
            streamSearchCancellation?.Cancel();
            streamSearchCancellation = new CancellationTokenSource();
            return streamSearchCancellation;
        }
    }

    private void CancelActiveStreamSearch()
    {
        lock (streamSearchGate)
        {
            streamSearchCancellation?.Cancel();
            streamSearchCancellation = null;
        }
    }

    private void DisposeStreamSearchCancellation(CancellationTokenSource cancellation)
    {
        lock (streamSearchGate)
        {
            if (ReferenceEquals(streamSearchCancellation, cancellation))
            {
                streamSearchCancellation = null;
            }
        }

        cancellation.Dispose();
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

    private static string FormatPlayableStreamSearchResult(string query, int resultCount)
    {
        return resultCount == 1
            ? $"1 playable stream found for {query}."
            : $"{resultCount} playable streams found for {query}.";
    }

    private static string FormatNoPlayableStreamSearchResult(string query, IReadOnlyList<StreamCandidateProbe> probes)
    {
        var platformNames = probes
            .Select(probe => probe.Target.Platform)
            .Distinct()
            .Select(platform => platform.ToString())
            .ToArray();
        var platformText = platformNames.Length switch
        {
            0 => "Twitch or Kick",
            1 => platformNames[0],
            2 => $"{platformNames[0]} or {platformNames[1]}",
            _ => string.Join(", ", platformNames)
        };
        var failures = string.Join(
            " | ",
            probes.Select(probe => $"{probe.Target.Platform}: {probe.Result.Message}"));

        return string.IsNullOrWhiteSpace(failures)
            ? $"No playable {platformText} stream found for {query}."
            : $"No playable {platformText} stream found for {query}. {failures}";
    }

    private async Task OpenCandidatesAsync(
        IReadOnlyList<StreamTarget> parsedCandidates,
        bool clearInputOnSuccess,
        bool selectOpenedTab = true)
    {
        var candidates = await ResolvePlayableCandidatesAsync(parsedCandidates);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("No stream target was provided.");
        }

        var target = candidates[0];
        await streamOpenGate.WaitAsync();
        try
        {
            var existing = FindTab(target);
            if (existing is not null)
            {
                if (selectOpenedTab)
                {
                    FocusOrStartExistingTab(existing, updateBrowserStatus: false);
                }
                else
                {
                    StartExistingTabWithoutSelecting(existing, updateBrowserStatus: false);
                }

                return;
            }

            var tab = selectOpenedTab
                ? CreateAndSelectTab(target)
                : CreateTab(target);
            StatusMessage = $"Starting {target.DisplayName}";
            StartTabInBackground(tab, clearInputOnSuccess, updateBrowserStatus: false);
        }
        finally
        {
            streamOpenGate.Release();
        }
    }

    private void FocusOrStartExistingTab(StreamTabViewModel tab, bool updateBrowserStatus)
    {
        SelectedTab = tab;
        SelectedQuality = tab.Quality;

        if (IsTabOpenOrStarting(tab))
        {
            StatusMessage = $"{tab.Target.DisplayName} already open";
            if (updateBrowserStatus)
            {
                BrowserClickStatus = StatusMessage;
            }
            return;
        }

        StatusMessage = $"Starting {tab.Target.DisplayName}";
        if (updateBrowserStatus)
        {
            BrowserClickStatus = StatusMessage;
        }

        StartTabInBackground(tab, clearInputOnSuccess: false, updateBrowserStatus: updateBrowserStatus);
    }

    private void StartExistingTabWithoutSelecting(StreamTabViewModel tab, bool updateBrowserStatus)
    {
        if (IsTabOpenOrStarting(tab))
        {
            StatusMessage = $"{tab.Target.DisplayName} already open";
            if (updateBrowserStatus)
            {
                BrowserClickStatus = StatusMessage;
            }

            return;
        }

        StatusMessage = $"Starting {tab.Target.DisplayName}";
        if (updateBrowserStatus)
        {
            BrowserClickStatus = StatusMessage;
        }

        StartTabInBackground(tab, clearInputOnSuccess: false, updateBrowserStatus: updateBrowserStatus);
    }

    private StreamTabViewModel CreateAndSelectTab(StreamTarget target)
    {
        var tab = CreateTab(target);
        SelectedTab = tab;
        return tab;
    }

    private StreamTabViewModel CreateTab(StreamTarget target)
    {
        var tab = new StreamTabViewModel(
            target,
            SelectedQuality,
            streamlinkService,
            playbackFactory,
            chatFactory,
            logger,
            dispatch,
            GetSavedStreamVolume(target),
            viewerCountService,
            replayResolver,
            replayChatProvider,
            kickChatHistoryProvider: kickChatHistoryProvider);
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

    private void SetRecentStreamHint(StreamTarget target, string thumbnailUrl, string displayName)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl) && string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        lock (recentStreamHintsGate)
        {
            recentStreamHints[target.StateKey] = new RecentStreamHint(
                thumbnailUrl?.Trim() ?? "",
                displayName?.Trim() ?? "");
        }
    }

    private RecentStreamHint? TakeRecentStreamHint(StreamTarget target)
    {
        lock (recentStreamHintsGate)
        {
            if (!recentStreamHints.Remove(target.StateKey, out var hint))
            {
                return null;
            }

            return hint;
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

            _ = RefreshRecentThumbnailsAsync(recentThumbnailRefreshCancellation.Token);
        });
    }

    private void RefreshRecentThumbnailsInBackground()
    {
        if (disposed || streamMetadataService is null)
        {
            return;
        }

        _ = RefreshRecentThumbnailsAsync(recentThumbnailRefreshCancellation.Token);
    }

    private async Task RefreshRecentThumbnailsAsync(CancellationToken cancellationToken)
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
            var snapshot = Settings.RecentStreams
                .Select(stream => new StreamTarget(stream.Platform, stream.Channel, stream.Url))
                .ToArray();
            if (snapshot.Length == 0)
            {
                return;
            }

            await MarkRecentStreamsCheckingAsync(snapshot, cancellationToken);

            var metadataByStream = new Dictionary<string, StreamMetadataResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = await GetRecentStreamMetadataAsync(target, cancellationToken);
                if (metadata is not null)
                {
                    metadataByStream[target.StateKey] = metadata;
                }
            }

            if (disposed || metadataByStream.Count == 0)
            {
                return;
            }

            await recentStreamsGate.WaitAsync(cancellationToken);
            try
            {
                var settingsChanged = false;
                var currentStreamKeys = Settings.RecentStreams
                    .Select(stream => new StreamTarget(stream.Platform, stream.Channel, stream.Url).StateKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var statusChanged = ApplyRecentStreamLiveStatuses(metadataByStream, currentStreamKeys, DateTimeOffset.UtcNow);
                var updated = new List<RecentStreamSettings>();
                foreach (var stream in Settings.RecentStreams)
                {
                    var target = new StreamTarget(stream.Platform, stream.Channel, stream.Url);
                    if (!metadataByStream.TryGetValue(target.StateKey, out var metadata) ||
                        metadata.State != StreamMetadataState.Available)
                    {
                        updated.Add(stream);
                        continue;
                    }

                    var displayName = FirstNonEmpty(metadata.DisplayName, stream.DisplayName, stream.Channel);
                    var thumbnailUrl = FirstNonEmpty(metadata.ThumbnailUrl, stream.ThumbnailUrl);
                    if (string.Equals(displayName, stream.DisplayName, StringComparison.Ordinal) &&
                        string.Equals(thumbnailUrl, stream.ThumbnailUrl, StringComparison.Ordinal))
                    {
                        updated.Add(stream);
                        continue;
                    }

                    settingsChanged = true;
                    updated.Add(new RecentStreamSettings
                    {
                        Platform = stream.Platform,
                        Channel = stream.Channel,
                        Url = stream.Url,
                        DisplayName = displayName,
                        ThumbnailUrl = thumbnailUrl,
                        LastQuality = stream.LastQuality,
                        LastWatchedAtUtc = stream.LastWatchedAtUtc
                    });
                }

                if (!settingsChanged && !statusChanged)
                {
                    return;
                }

                if (settingsChanged)
                {
                    Settings.RecentStreams = updated;
                }

                RebuildRecentStreams();
                if (settingsChanged)
                {
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
                if (!recentStreamLiveStatuses.TryGetValue(target.StateKey, out var current) ||
                    current.State != RecentStreamLiveState.Checking)
                {
                    recentStreamLiveStatuses[target.StateKey] = status;
                    changed = true;
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

    private bool ApplyRecentStreamLiveStatuses(
        IReadOnlyDictionary<string, StreamMetadataResult> metadataByStream,
        IReadOnlySet<string> currentStreamKeys,
        DateTimeOffset checkedAtUtc)
    {
        var changed = false;
        foreach (var (stateKey, metadata) in metadataByStream)
        {
            if (!currentStreamKeys.Contains(stateKey))
            {
                continue;
            }

            var status = CreateRecentStreamLiveStatus(metadata, checkedAtUtc);
            if (!recentStreamLiveStatuses.TryGetValue(stateKey, out var current) ||
                current != status)
            {
                recentStreamLiveStatuses[stateKey] = status;
                changed = true;
            }
        }

        return changed;
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

    private async Task RememberRecentStreamAsync(StreamTabViewModel tab)
    {
        var target = tab.Target;
        try
        {
            var hint = TakeRecentStreamHint(target);
            var watchedAtUtc = DateTimeOffset.UtcNow;
            var needsMetadata = false;

            await recentStreamsGate.WaitAsync();
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
                recentStreamLiveStatuses[target.StateKey] = new RecentStreamLiveStatus(
                    RecentStreamLiveState.Live,
                    watchedAtUtc,
                    "Playback started successfully.");
                RebuildRecentStreams();
                await SaveRecentStreamSettingsAsync(target);
                needsMetadata = streamMetadataService is not null &&
                    string.IsNullOrWhiteSpace(recentStream.ThumbnailUrl);
            }
            finally
            {
                recentStreamsGate.Release();
            }

            if (!needsMetadata)
            {
                return;
            }

            var metadata = await TryGetRecentStreamMetadataAsync(target);
            if (metadata is null ||
                (string.IsNullOrWhiteSpace(metadata.ThumbnailUrl) &&
                    string.IsNullOrWhiteSpace(metadata.DisplayName)))
            {
                return;
            }

            await recentStreamsGate.WaitAsync();
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
                    .Where(stream => !IsSameRecentStream(stream, target))
                    .Prepend(recentStream)
                    .ToList();
                recentStreamLiveStatuses[target.StateKey] = CreateRecentStreamLiveStatus(metadata, DateTimeOffset.UtcNow);
                RebuildRecentStreams();
                await SaveRecentStreamSettingsAsync(target);
            }
            finally
            {
                recentStreamsGate.Release();
            }
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

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
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
    {
        lock (tabStartsGate)
        {
            return activeTabStarts.Contains(tab.Id);
        }
    }

    private bool TryBeginTabStart(StreamTabViewModel tab)
    {
        lock (tabStartsGate)
        {
            return activeTabStarts.Add(tab.Id);
        }
    }

    private void EndTabStart(StreamTabViewModel tab)
    {
        lock (tabStartsGate)
        {
            activeTabStarts.Remove(tab.Id);
        }
    }

    private void StartTabInBackground(StreamTabViewModel tab, bool clearInputOnSuccess, bool updateBrowserStatus)
    {
        if (!TryBeginTabStart(tab))
        {
            return;
        }

        ApplyVideoLayout();
        dispatch(() => _ = StartTabAndUpdateStatusAsync(tab, clearInputOnSuccess, updateBrowserStatus));
    }

    private async Task StartTabAndUpdateStatusAsync(StreamTabViewModel tab, bool clearInputOnSuccess, bool updateBrowserStatus)
    {
        await Task.Yield();

        try
        {
            await tab.StartAsync(Settings);
            if (tab.Status == PlaybackStatus.Playing)
            {
                if (clearInputOnSuccess)
                {
                    NewStreamText = "";
                }

                StatusMessage = $"{tab.Target.DisplayName} playing";
                if (tab.Target.Kind == StreamTargetKind.Live)
                {
                    _ = RememberRecentStreamAsync(tab);
                }
            }
            else
            {
                StatusMessage = $"{tab.Target.DisplayName}: {GetStartFailure(tab)}";
            }

            if (updateBrowserStatus)
            {
                BrowserClickStatus = StatusMessage;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            if (updateBrowserStatus)
            {
                BrowserClickStatus = ex.Message;
            }

            logger.Write(AppLogLevel.Error, "UI", $"Failed to start {tab.Target.DisplayName}.", ex);
        }
        finally
        {
            EndTabStart(tab);
            ApplyVlcPluginMultiViewChatPolicyInBackground(restoreWhenAllowed: true);
        }
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
        var probes = await ProbeCandidatesAsync(candidates, customArguments, CancellationToken.None);
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
            var request = new StreamTransportRequest(target, SelectedQuality, Settings.StreamlinkPath!, Settings.LowLatency, customArguments);
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

    private async Task PlaySelectedAsync()
    {
        if (SelectedTab is null)
        {
            return;
        }

        StatusMessage = $"Starting {SelectedTab.Target.DisplayName}";
        StartTabInBackground(SelectedTab, clearInputOnSuccess: false, updateBrowserStatus: false);
        await Task.CompletedTask;
    }

    private async Task ReloadSelectedAsync()
    {
        if (SelectedTab is null)
        {
            return;
        }

        StatusMessage = $"Reloading {SelectedTab.Target.DisplayName}";
        StartTabInBackground(SelectedTab, clearInputOnSuccess: false, updateBrowserStatus: false);
        await Task.CompletedTask;
    }

    private async Task StopSelectedAsync()
    {
        if (SelectedTab is not null)
        {
            await SelectedTab.StopAsync();
            StatusMessage = $"{SelectedTab.Target.DisplayName} stopped";
        }
    }

    private async Task PauseSelectedAsync()
    {
        if (SelectedTab is not null)
        {
            await SelectedTab.PauseOrResumeAsync();
            StatusMessage = $"{SelectedTab.Target.DisplayName}: {SelectedTab.StatusText}";
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
            _ = RestartTheatreModeChatTabsAsync(tabsNeedingChatRestart.Distinct().ToArray());
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
        _ = RestartTheatreModeChatTabsAsync(changedTabs);
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

        foreach (var visibleGroup in pictureInPictureVisibleTabGroups)
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

    public bool SetTabDetached(StreamTabViewModel tab, bool detached)
    {
        return SetTabsDetached([tab], detached);
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

    private async Task AuthorizeTwitchAsync()
    {
        try
        {
            StatusMessage = "Waiting for Twitch authorization";
            var token = await TwitchOAuthService.AuthorizeUserTokenAsync(Settings.Chat);
            TwitchOAuthService.ApplyTokenResult(Settings.Chat, token);

            await settingsService.SaveAsync(Settings);
            await RestartChatTabsAsync();
            ClearTwitchTokenCommand.RaiseCanExecuteChanged();
            StatusMessage = token.ExpiresAtUtc is { } expiresAt
                ? $"Twitch authorized until {expiresAt.ToLocalTime():g}"
                : "Twitch authorized";
            _ = RefreshFollowedChannelsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        try
        {
            StatusMessage = "Waiting for Kick authorization";
            var token = await KickOAuthService.AuthorizeUserTokenAsync(Settings.Chat);
            KickOAuthService.ApplyTokenResult(Settings.Chat, token);

            if (string.IsNullOrWhiteSpace(Settings.Chat.KickUsername))
            {
                try
                {
                    var username = await KickOAuthService.TryGetCurrentUsernameAsync(token.AccessToken);
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        Settings.Chat.KickUsername = username;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Write(AppLogLevel.Warning, "KickOAuth", "Could not resolve authorized Kick username.", ex);
                }
            }

            await settingsService.SaveAsync(Settings);
            await RestartChatTabsAsync();
            ClearKickTokenCommand.RaiseCanExecuteChanged();
            StatusMessage = token.ExpiresAtUtc is { } expiresAt
                ? $"Kick authorized until {expiresAt.ToLocalTime():g}"
                : "Kick authorized";
            _ = RefreshFollowedChannelsAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "KickOAuth", "Kick authorization failed.", ex);
        }
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
        ToggleChatCommand.RaiseCanExecuteChanged();
        MoveTabLeftCommand.RaiseCanExecuteChanged();
        MoveTabRightCommand.RaiseCanExecuteChanged();
    }

    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
    }

    private void ChatSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatSettings.VlcOverlayFontSize))
        {
            OnPropertyChanged(nameof(SelectedVlcOverlayFontSize));
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
            _ = ApplyChatSettingsAsync(reconfigurePlayback);
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
        await chatSettingsApplyGate.WaitAsync();
        try
        {
            foreach (var tab in Tabs.ToArray())
            {
                try
                {
                    if (reconfigurePlayback && tab.ShouldRestartPlaybackForChatOverlaySettings(Settings))
                    {
                        StatusMessage = $"Reloading {tab.Target.DisplayName} for chat layout";
                        await tab.ReconfigurePlaybackForChatOverlaySettingsAsync(Settings);
                    }
                    else
                    {
                        await tab.RestartChatAsync(Settings);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusMessage = ex.Message;
                    logger.Write(AppLogLevel.Warning, "Chat", $"Failed to apply chat settings for {tab.Target.DisplayName}.", ex);
                }
            }
        }
        finally
        {
            chatSettingsApplyGate.Release();
        }

        RaiseChatVisibilityProperties();
    }

    private async Task RestartTabChatAsync(StreamTabViewModel tab)
    {
        await chatSettingsApplyGate.WaitAsync();
        try
        {
            if (!Tabs.Contains(tab))
            {
                return;
            }

            await tab.RestartChatAsync(Settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Chat", $"Failed to restart chat for {tab.Target.DisplayName}.", ex);
        }
        finally
        {
            chatSettingsApplyGate.Release();
        }

        RaiseChatVisibilityProperties();
    }

    private async Task RestartTheatreModeChatTabsAsync(IReadOnlyList<StreamTabViewModel> tabs)
    {
        await chatSettingsApplyGate.WaitAsync();
        try
        {
            foreach (var tab in tabs.Distinct().Where(Tabs.Contains))
            {
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
        finally
        {
            chatSettingsApplyGate.Release();
        }

        RaiseChatVisibilityProperties();
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
        RecentStreams.Clear();
        foreach (var stream in Settings.RecentStreams)
        {
            var target = new StreamTarget(stream.Platform, stream.Channel, stream.Url);
            var liveStatus = recentStreamLiveStatuses.TryGetValue(target.StateKey, out var status)
                ? status
                : RecentStreamLiveStatus.Unknown;
            RecentStreams.Add(new RecentStreamViewModel(stream, OpenRecentStreamAsync, DeleteRecentStreamAsync, liveStatus));
        }
    }

    private void TabOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is StreamTabViewModel tab && e.PropertyName == nameof(StreamTabViewModel.Volume))
        {
            RememberStreamVolume(tab);
        }

        if (sender is StreamTabViewModel detachedTab && e.PropertyName == nameof(StreamTabViewModel.IsDetached))
        {
            if (ReferenceEquals(detachedTab, SelectedTab))
            {
                OnPropertyChanged(nameof(IsSelectedTabDetached));
            }

            ApplyVideoLayout();
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
            ReferenceEquals(tab, selectedTab) ||
            selectedTab is null ||
            !Tabs.Contains(selectedTab))
        {
            return;
        }

        selectedTab.ReapplyAudio();
    }

    private void RaiseChatVisibilityProperties()
    {
        OnPropertyChanged(nameof(IsDockedChatVisible));
        OnPropertyChanged(nameof(IsSelectedChatShowing));
        OnPropertyChanged(nameof(IsChatLayoutHidden));
        OnPropertyChanged(nameof(IsOverlayChatInputVisible));
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

    private string GetSelectedKickSetting(Dictionary<string, string> values)
    {
        return SelectedTab?.Target.Platform == PlatformKind.Kick &&
            values.TryGetValue(SelectedTab.Target.Channel, out var value)
            ? value
            : "";
    }

    private void SetSelectedKickSetting(Dictionary<string, string> values, string? value, string propertyName)
    {
        if (SelectedTab?.Target.Platform != PlatformKind.Kick)
        {
            return;
        }

        var channel = SelectedTab.Target.Channel;
        var normalized = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            values.Remove(channel);
        }
        else
        {
            values[channel] = normalized;
        }

        OnPropertyChanged(propertyName);
        SelectedTab.RefreshChatOverlay(Settings.Chat);
        _ = SelectedTab.RestartChatAsync(Settings);
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
                tab.SetVideoPlacement(visible: false, row: 0, column: 0, rowSpan: 1, columnSpan: 1);
            }
        }

        SyncVideoTabs(visibleTabs);
        ApplyVlcPluginMultiViewChatPolicyInBackground(restoreWhenAllowed: true);
    }

    private void ApplyVlcPluginMultiViewChatPolicyInBackground(bool restoreWhenAllowed = false)
    {
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
            _ = ReconfigureVlcPluginMultiViewChatPolicyTabsAsync(reconfigureTabs);
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
        await vlcPluginMultiViewChatPolicyGate.WaitAsync();
        try
        {
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
        finally
        {
            vlcPluginMultiViewChatPolicyGate.Release();
        }

        RaiseChatVisibilityProperties();
    }

    private async Task<(StreamTabViewModel Tab, Exception? Exception)> ReconfigureVlcPluginChatTabAsync(StreamTabViewModel tab)
    {
        try
        {
            await tab.ReconfigurePlaybackForChatOverlaySettingsAsync(Settings);
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
            foreach (var group in multiViewTabGroups.ToArray())
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

        foreach (var group in pictureInPictureTabGroups.ToArray())
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

            var group = ResolveTabStripGroup(tab);
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

    private IReadOnlyList<StreamTabViewModel>? ResolveTabStripGroup(StreamTabViewModel tab)
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
        _ = ApplyInactivePlaybackPolicyAsync();
    }

    private async Task ApplyInactivePlaybackPolicyAsync()
    {
        var tabs = Tabs.ToArray();
        foreach (var tab in tabs)
        {
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
        }
    }

    private bool ShouldKeepTabRunning(StreamTabViewModel tab)
    {
        return Settings.KeepInactiveTabsRunning || tab.IsVideoVisible || tab.IsDetached;
    }

    private void ApplySelectedTabSelection()
    {
        var selected = selectedTab is not null && Tabs.Contains(selectedTab) ? selectedTab : null;
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
                if (ReferenceEquals(tab, selected))
                {
                    continue;
                }

                if (!tab.SetSelectedForAudio(false))
                {
                    tab.ReapplyAudio();
                }

                if (tab.IsSelected)
                {
                    tab.IsSelected = false;
                }
            }

            if (selected is null)
            {
                return;
            }

            if (!selected.IsSelected)
            {
                selected.IsSelected = true;
            }

            ApplySelectedTabAudioState(selected);
        }
        finally
        {
            applyingSelectedTabSelection = wasApplyingSelectedTabSelection;
        }
    }

    private void ApplyImmediateSelectedTabAudioState(StreamTabViewModel? previous, StreamTabViewModel? selected)
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
                ApplySelectedTabAudioState(selected);
            }
        }
        finally
        {
            applyingSelectedTabSelection = wasApplyingSelectedTabSelection;
        }
    }

    private static void ApplySelectedTabAudioState(StreamTabViewModel selected)
    {
        if (!selected.SetSelectedForAudio(true))
        {
            selected.ReapplyAudio();
        }
    }

    private sealed record RecentStreamHint(string ThumbnailUrl, string DisplayName);

    private sealed record StreamCandidateProbe(
        StreamTarget Target,
        StreamlinkProbeResult Result,
        StreamMetadataResult? Metadata = null);

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
