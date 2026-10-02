using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public enum SettingsCategory
{
    General,
    Playback,
    Accounts,
    Chat,
    Hotkeys,
    Advanced,
    Downloads
}

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan DetachedDisposalWaitTimeout = TimeSpan.FromSeconds(4);
    private const int VlcPluginMultiViewChatDisableThreshold = 3;
    private const int DenseMultiStreamStartupThreshold = 4;
    private const int MaxConcurrentTabStarts = 2;
    private readonly VodLibraryViewModel vodLibrary;
    private readonly VodDownloadsViewModel downloads;
    private readonly BrowseViewModel browse;
    private readonly FollowedChannelsViewModel followed;
    private readonly RecentStreamsViewModel recent;
    private readonly StreamSearchViewModel streamSearch;
    private readonly ISettingsService settingsService;
    private readonly SettingsAutoSaveController settingsAutoSave;
    private readonly IStreamlinkService streamlinkService;
    private readonly IPlaybackEngineFactory playbackFactory;
    private readonly IChatClientFactory chatFactory;
    private readonly IViewerCountService? viewerCountService;
    private readonly IReplayResolver? replayResolver;
    private readonly IVodChatProvider? vodChatProvider;
    private readonly IVodPlaybackHistory? vodPlaybackHistory;
    private readonly IStreamMetadataService? streamMetadataService;
    private readonly ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver;
    private readonly ITwitchClipService? twitchClipService;
    private readonly IKickClipService? kickClipService;
    private readonly IAppUpdateService? appUpdateService;
    private readonly IBrowseService? browseService;
    private readonly IAppLogger logger;
    private readonly Action<Action> dispatch;
    private readonly Func<Action, bool>? tryDispatch;
    private readonly Action<Uri> openBrowser;
    private readonly Action? requestShutdown;
    private EventHandler<LogEntry>? loggerEntryWrittenHandler;
    private readonly AppLogBuffer appLogBuffer;
    private readonly object disposalGate = new();
    private readonly object detachedDisposalsGate = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly SemaphoreSlim streamOpenGate = new(1, 1);
    private readonly TabStartController tabStartController = new(MaxConcurrentTabStarts);
    private readonly SemaphoreSlim chatSettingsApplyGate = new(1, 1);
    private readonly SemaphoreSlim vlcPluginMultiViewChatPolicyGate = new(1, 1);
    private readonly List<Task> detachedDisposals = [];
    private readonly BackgroundOperationController backgroundOperationController;
    private readonly TabGroupingController tabGroupingController = new();
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
    private string selectedQuality;
    private bool isDownloadsHomePageSelected;
    private string statusMessage = "Ready";
    private string settingsSaveStatus = "Changes are saved automatically.";
    private bool isSavingSettings;
    private bool hasSettingsSaveError;
    private string appUpdateStatus = "Updates are checked automatically. Automatic downloads are optional; you choose when to restart and install.";
    private Version? canceledAutomaticDownloadVersion;
    private string appUpdateActionText = "Check for updates";
    private bool isUpdateBannerVisible;
    private Task? automaticUpdateTask;
    private CancellationTokenSource? updateDownloadCancellation;
    private bool updateDownloadCanceledByUser;
    private int updateActionInProgress;
    private bool isHomeSelected = true;
    private bool isRecentHomePageSelected;
    private bool isTwitchVodsHomePageSelected;
    private bool isBrowseHomePageSelected;
    private bool isSettingsOpen;
    private SettingsCategory selectedSettingsCategory = SettingsCategory.General;
    private bool isReplaySeekBarUiVisible = true;
    private bool isStreamOnlyFullscreenActive;
    private bool isVideoFullscreenActive;
    private bool suppressInactiveTabPause;
    private bool applyingSelectedTabSelection;
    private bool disposed;
    private int inactivePlaybackPolicyApplyPassCount;
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
        var streamMetadataService = dependencies.StreamMetadataService;
        var replayResolver = dependencies.ReplayResolver;
        var vodChatProvider = dependencies.VodChatProvider;
        var twitchSubOnlyVodResolver = dependencies.TwitchSubOnlyVodResolver;
        var twitchClipService = dependencies.TwitchClipService;
        var appUpdateService = dependencies.AppUpdateService;
        var browseService = dependencies.BrowseService;
        var openBrowser = dependencies.OpenBrowser;
        var requestShutdown = dependencies.RequestShutdown;
        var tryDispatch = dependencies.TryDispatch;

        Settings = settings;
        HoverPreviews = new StreamHoverPreviewController(settings, streamlinkService, logger);
        this.settingsService = settingsService;
        this.streamlinkService = streamlinkService;
        this.playbackFactory = playbackFactory;
        this.chatFactory = chatFactory;
        this.viewerCountService = viewerCountService;
        this.replayResolver = replayResolver;
        this.vodChatProvider = vodChatProvider;
        vodPlaybackHistory = dependencies.VodPlaybackHistory;
        this.streamMetadataService = streamMetadataService;
        this.twitchSubOnlyVodResolver = twitchSubOnlyVodResolver;
        this.twitchClipService = twitchClipService;
        kickClipService = dependencies.KickClipService;
        this.appUpdateService = appUpdateService;
        if (appUpdateService is not null)
        {
            appUpdateService.StateChanged += OnAppUpdateStateChanged;
        }
        this.browseService = browseService;
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

        streamSearch = new StreamSearchViewModel(dependencies, value => StatusMessage = value,
            () => SelectedQuality, () => IsHomeVisible && !IsSettingsOpen, OpenSearchResultAsync);
        streamSearch.PropertyChanged += HomeFeatureOnPropertyChanged;
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack);
        SelectHomeCommand = new RelayCommand(SelectHome);
        ShowFollowedHomePageCommand = new RelayCommand(ShowFollowedHomePage);
        ShowTwitchVodsHomePageCommand = new RelayCommand(ShowTwitchVodsHomePage);
        ShowRecentHomePageCommand = new RelayCommand(ShowRecentHomePage);
        ShowBrowseHomePageCommand = new RelayCommand(ShowBrowseHomePage);
        ShowDownloadsHomePageCommand = new RelayCommand(ShowDownloadsHomePage);
        browse = new BrowseViewModel(dependencies, value => StatusMessage = value,
            OpenLiveStreamCardAsync, RecordNavigation, () => IsBrowseHomePageVisible);
        browse.PropertyChanged += HomeFeatureOnPropertyChanged;
        followed = new FollowedChannelsViewModel(dependencies, value => StatusMessage = value, OpenLiveStreamCardAsync,
            channel => OpenChannelVodsAsync(channel.Platform, channel.Channel));
        followed.PropertyChanged += HomeFeatureOnPropertyChanged;
        downloads = new VodDownloadsViewModel(dependencies, value => StatusMessage = value,
            () => SelectedVodDownloadQuality, OpenOfflineVodAsync, EnsureDownloadNotOpen);
        downloads.PropertyChanged += HomeFeatureOnPropertyChanged;
        vodLibrary = new VodLibraryViewModel(dependencies, value => StatusMessage = value, OpenTwitchVodAsync,
            downloads.CreateDownloadCommand);
        downloads.BindVodCards(vodLibrary.TwitchVods);
        vodLibrary.PropertyChanged += HomeFeatureOnPropertyChanged;
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
        ShowDownloadsSettingsCommand = new RelayCommand(() =>
        {
            SelectedSettingsCategory = SettingsCategory.Downloads;
            IsSettingsOpen = true;
        });
        ShowAccountsSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Accounts);
        ShowChatSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Chat);
        ShowHotkeysSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Hotkeys);
        ShowAdvancedSettingsCommand = new RelayCommand(() => SelectedSettingsCategory = SettingsCategory.Advanced);
        ToggleMultiStreamCommand = new RelayCommand(ToggleMultiStream);
        ToggleReplaySeekBarCommand = new RelayCommand(ToggleReplaySeekBar);
        ToggleChatCommand = CreateCommand(ToggleChatAsync, () => SelectedTab is not null);
        MoveTabLeftCommand = new RelayCommand(MoveTabLeft, () => SelectedTab is not null && Tabs.IndexOf(SelectedTab) > 0);
        MoveTabRightCommand = new RelayCommand(MoveTabRight, () => SelectedTab is not null && Tabs.IndexOf(SelectedTab) < Tabs.Count - 1);
        recent = new RecentStreamsViewModel(dependencies, value => StatusMessage = value,
            OpenRecentStreamAsync, () => IsHomeVisible && IsRecentHomePageVisible);
        recent.PropertyChanged += HomeFeatureOnPropertyChanged;
        Tabs.CollectionChanged += TabsOnCollectionChanged;
        Settings.PropertyChanged += SettingsOnPropertyChanged;
        ObserveChatSettings(Settings.Chat);
        settingsAutoSave = new SettingsAutoSaveController(Settings, settingsService, dispatch, OnSettingsAutoSaved);
    }

    public AppSettings Settings { get; }

    public StreamHoverPreviewController HoverPreviews { get; }
    public ObservableCollection<StreamTabViewModel> Tabs { get; } = [];
    public ObservableCollection<TabStripItemViewModel> TabStripItems { get; } = [];
    public ObservableCollection<StreamTabViewModel> VideoTabs { get; } = [];

    public ObservableCollection<StreamSearchResultViewModel> StreamSearchResults { get => streamSearch.StreamSearchResults; }

    public ObservableCollection<LiveStreamCardViewModel> LiveFollowedChannels { get => followed.LiveFollowedChannels; }

    public ObservableCollection<OfflineFollowedChannelViewModel> OfflineFollowedChannels { get => followed.OfflineFollowedChannels; }

    public ObservableCollection<VodViewModel> TwitchVods { get => vodLibrary.TwitchVods; }

    public ObservableCollection<VodDownloadViewModel> VodDownloads => downloads.VodDownloads;
    public bool HasVodDownloads => downloads.HasVodDownloads;
    public string DownloadsStatus => downloads.DownloadsStatus;
    public string DownloadDirectory => downloads.DownloadDirectory;
    public bool CanChangeDownloadDirectory => downloads.CanChangeDownloadDirectory;
    public Task ChangeDownloadDirectoryAsync(string directory) => downloads.ChangeDownloadDirectoryAsync(directory);
    public string VodDownloadUrl { get => downloads.VodDownloadUrl; set => downloads.VodDownloadUrl = value; }
    public string DownloadUrlError => downloads.DownloadUrlError;
    public bool HasDownloadUrlError => downloads.HasDownloadUrlError;
    public AsyncRelayCommand DownloadVodUrlCommand => downloads.DownloadVodUrlCommand;
    public AsyncRelayCommand OpenDownloadFolderCommand => downloads.OpenDownloadFolderCommand;
    public string SelectedVodDownloadQuality
    {
        get => Settings.Downloads.Quality;
        set
        {
            if (Settings.Downloads.Quality == value) return;
            Settings.Downloads.Quality = value;
            OnPropertyChanged();
            downloads.RefreshVodCards();
        }
    }

    public ObservableCollection<RecentStreamViewModel> RecentStreams { get => recent.RecentStreams; }

    public ObservableCollection<BrowseCategoryViewModel> BrowseCategories { get => browse.BrowseCategories; }

    public ObservableCollection<LiveStreamCardViewModel> BrowseStreams { get => browse.BrowseStreams; }
    public IReadOnlyList<ChatLayout> ChatLayoutOptions { get; } = Enum.GetValues<ChatLayout>();
    public IReadOnlyList<QualityOption> QualityOptions { get; } = QualityOption.Defaults;
    public IReadOnlyList<VideoRendererModeOption> VideoRendererOptions { get; } = VideoRendererModeOption.All;
    public IReadOnlyList<WindowCloseBehaviorOption> CloseBehaviorOptions { get; } = WindowCloseBehaviorOption.All;
    public IReadOnlyList<AppThemeOption> ThemeOptions { get; } = AppThemeOption.All;
    public ObservableCollection<string> AppLogLines { get; } = [];

    public AsyncRelayCommand AddAndPlayCommand { get => streamSearch.AddAndPlayCommand; }
    public RelayCommand GoBackCommand { get; }
    public RelayCommand SelectHomeCommand { get; }
    public RelayCommand ShowFollowedHomePageCommand { get; }
    public RelayCommand ShowTwitchVodsHomePageCommand { get; }
    public RelayCommand ShowRecentHomePageCommand { get; }
    public RelayCommand ShowBrowseHomePageCommand { get; }
    public RelayCommand ShowDownloadsHomePageCommand { get; }

    public RelayCommand ReturnToBrowseCategoriesCommand { get => browse.ReturnToBrowseCategoriesCommand; }

    public AsyncRelayCommand RefreshFollowedChannelsCommand { get => followed.RefreshFollowedChannelsCommand; }

    public AsyncRelayCommand ImportKickFollowsCommand { get => followed.ImportKickFollowsCommand; }

    public AsyncRelayCommand ClearImportedKickFollowsCommand { get => followed.ClearImportedKickFollowsCommand; }

    public AsyncRelayCommand SearchTwitchVodsCommand { get => vodLibrary.SearchTwitchVodsCommand; }

    public AsyncRelayCommand LoadMoreTwitchVodsCommand { get => vodLibrary.LoadMoreTwitchVodsCommand; }

    public RelayCommand SelectTwitchVodPlatformCommand { get => vodLibrary.SelectTwitchVodPlatformCommand; }

    public RelayCommand SelectKickVodPlatformCommand { get => vodLibrary.SelectKickVodPlatformCommand; }

    public RelayCommand ShowPastBroadcastsVodFilterCommand { get => vodLibrary.ShowPastBroadcastsVodFilterCommand; }

    public RelayCommand ShowHighlightsVodFilterCommand { get => vodLibrary.ShowHighlightsVodFilterCommand; }

    public RelayCommand ShowUploadsVodFilterCommand { get => vodLibrary.ShowUploadsVodFilterCommand; }

    public RelayCommand ShowAllVodFilterCommand { get => vodLibrary.ShowAllVodFilterCommand; }

    public RelayCommand SelectTwitchBrowsePlatformCommand { get => browse.SelectTwitchBrowsePlatformCommand; }

    public RelayCommand SelectKickBrowsePlatformCommand { get => browse.SelectKickBrowsePlatformCommand; }

    public AsyncRelayCommand RefreshBrowseCommand { get => browse.RefreshBrowseCommand; }

    public AsyncRelayCommand LoadMoreBrowseCategoriesCommand { get => browse.LoadMoreBrowseCategoriesCommand; }

    public AsyncRelayCommand LoadMoreBrowseStreamsCommand { get => browse.LoadMoreBrowseStreamsCommand; }
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
    public RelayCommand ShowDownloadsSettingsCommand { get; }
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

    internal void SelectStreamTab(StreamTabViewModel tab)
    {
        if (!Tabs.Contains(tab))
        {
            return;
        }

        // Finish selecting the stream before closing Settings so history records
        // the destination once, including when the same stream is selected again.
        SelectedTab = tab;
        IsSettingsOpen = false;
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
                SelectStreamTab(item.ActiveTab);
            }
        }
    }

    public string NewStreamText { get => streamSearch.NewStreamText; set => streamSearch.NewStreamText = value; }

    public bool HasNewStreamSearchText { get => streamSearch.HasNewStreamSearchText; }

    public bool IsNewStreamSearchPlaceholderVisible { get => streamSearch.IsNewStreamSearchPlaceholderVisible; }

    public string TwitchVodSearchText { get => vodLibrary.TwitchVodSearchText; set => vodLibrary.TwitchVodSearchText = value; }

    public bool HasTwitchVodSearchText { get => vodLibrary.HasTwitchVodSearchText; }

    public bool IsTwitchVodSearchPlaceholderVisible { get => vodLibrary.IsTwitchVodSearchPlaceholderVisible; }

    public TwitchVodTypeFilter SelectedTwitchVodType { get => vodLibrary.SelectedTwitchVodType; private set => vodLibrary.SelectedTwitchVodType = value; }

    public bool IsPastBroadcastsVodFilterSelected { get => vodLibrary.IsPastBroadcastsVodFilterSelected; }

    public bool IsHighlightsVodFilterSelected { get => vodLibrary.IsHighlightsVodFilterSelected; }

    public bool IsUploadsVodFilterSelected { get => vodLibrary.IsUploadsVodFilterSelected; }

    public bool IsAllVodFilterSelected { get => vodLibrary.IsAllVodFilterSelected; }

    public PlatformKind SelectedVodPlatform { get => vodLibrary.SelectedVodPlatform; private set => vodLibrary.SelectedVodPlatform = value; }

    public bool IsTwitchVodPlatformSelected { get => vodLibrary.IsTwitchVodPlatformSelected; }

    public bool IsKickVodPlatformSelected { get => vodLibrary.IsKickVodPlatformSelected; }

    public string VodPlatformText { get => vodLibrary.VodPlatformText; }

    public bool IsTwitchVodFilterVisible { get => vodLibrary.IsTwitchVodFilterVisible; }

    public string TwitchVodStatus { get => vodLibrary.TwitchVodStatus; private set => vodLibrary.TwitchVodStatus = value; }

    public bool IsTwitchVodSearchRunning { get => vodLibrary.IsTwitchVodSearchRunning; private set => vodLibrary.IsTwitchVodSearchRunning = value; }

    public bool HasTwitchVodSearchCompleted { get => vodLibrary.HasTwitchVodSearchCompleted; private set => vodLibrary.HasTwitchVodSearchCompleted = value; }

    public bool HasTwitchVods { get => vodLibrary.HasTwitchVods; }

    public bool IsTwitchVodEmptyVisible { get => vodLibrary.IsTwitchVodEmptyVisible; }

    public bool CanSearchSelectedVodPlatform { get => vodLibrary.CanSearchSelectedVodPlatform; }

    public bool CanLoadMoreTwitchVods { get => vodLibrary.CanLoadMoreTwitchVods; }

    public bool IsTwitchVodLoadMoreVisible { get => vodLibrary.IsTwitchVodLoadMoreVisible; }

    public string TwitchVodResultsTitle { get => vodLibrary.TwitchVodResultsTitle; }

    public string BrowseCategorySearchText { get => browse.BrowseCategorySearchText; set => browse.BrowseCategorySearchText = value; }

    public bool HasBrowseCategorySearchText { get => browse.HasBrowseCategorySearchText; }

    public bool IsBrowseCategorySearchPlaceholderVisible { get => browse.IsBrowseCategorySearchPlaceholderVisible; }

    public PlatformKind SelectedBrowsePlatform { get => browse.SelectedBrowsePlatform; private set => browse.SelectedBrowsePlatform = value; }

    public bool IsTwitchBrowsePlatformSelected { get => browse.IsTwitchBrowsePlatformSelected; }

    public bool IsKickBrowsePlatformSelected { get => browse.IsKickBrowsePlatformSelected; }

    public string BrowsePlatformText { get => browse.BrowsePlatformText; }

    public string BrowseStatus { get => browse.BrowseStatus; private set => browse.BrowseStatus = value; }

    public BrowseCategoryViewModel? SelectedBrowseCategory { get => browse.SelectedBrowseCategory; private set => browse.SelectedBrowseCategory = value; }

    public bool HasSelectedBrowseCategory { get => browse.HasSelectedBrowseCategory; }

    public string SelectedBrowseCategoryName { get => browse.SelectedBrowseCategoryName; }

    public bool IsBrowseCategoriesLoading { get => browse.IsBrowseCategoriesLoading; private set => browse.IsBrowseCategoriesLoading = value; }

    public bool HasBrowseCategorySearchCompleted { get => browse.HasBrowseCategorySearchCompleted; private set => browse.HasBrowseCategorySearchCompleted = value; }

    public bool IsBrowseStreamsLoading { get => browse.IsBrowseStreamsLoading; private set => browse.IsBrowseStreamsLoading = value; }

    public bool HasBrowseStreamSearchCompleted { get => browse.HasBrowseStreamSearchCompleted; private set => browse.HasBrowseStreamSearchCompleted = value; }

    public bool HasBrowseCategories { get => browse.HasBrowseCategories; }

    public bool HasBrowseStreams { get => browse.HasBrowseStreams; }

    public bool IsBrowseCategoriesEmptyVisible { get => browse.IsBrowseCategoriesEmptyVisible; }

    public bool IsBrowseStreamsEmptyVisible { get => browse.IsBrowseStreamsEmptyVisible; }

    public bool CanLoadMoreBrowseCategories { get => browse.CanLoadMoreBrowseCategories; }

    public bool CanLoadMoreBrowseStreams { get => browse.CanLoadMoreBrowseStreams; }

    public bool IsBrowseCategoryLoadMoreVisible { get => browse.IsBrowseCategoryLoadMoreVisible; }

    public bool IsBrowseCategoryLoadMoreIndicatorVisible { get => browse.IsBrowseCategoryLoadMoreIndicatorVisible; }

    public bool IsBrowseStreamLoadMoreVisible { get => browse.IsBrowseStreamLoadMoreVisible; }

    public string BrowseCategoriesTitle { get => browse.BrowseCategoriesTitle; }

    public string BrowseStreamsTitle { get => browse.BrowseStreamsTitle; }

    public bool HasStreamSearchResults { get => streamSearch.HasStreamSearchResults; }

    public bool IsStreamSearchPanelVisible { get => streamSearch.IsStreamSearchPanelVisible; }

    public bool IsStreamSearchResultsVisible { get => streamSearch.IsStreamSearchResultsVisible; }

    public bool IsStreamSearchEmptyVisible { get => streamSearch.IsStreamSearchEmptyVisible; }

    public string StreamSearchResultsTitle { get => streamSearch.StreamSearchResultsTitle; }

    public string StreamSearchStatus { get => streamSearch.StreamSearchStatus; private set => streamSearch.StreamSearchStatus = value; }

    public bool IsStreamSearchRunning { get => streamSearch.IsStreamSearchRunning; private set => streamSearch.IsStreamSearchRunning = value; }

    public void ShowStreamSearchDropdown() => streamSearch.ShowStreamSearchDropdown();

    public void DismissStreamSearchDropdown() => streamSearch.DismissStreamSearchDropdown();

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

    public string SettingsSaveStatus
    {
        get => settingsSaveStatus;
        private set => SetProperty(ref settingsSaveStatus, value);
    }

    public bool IsSavingSettings
    {
        get => isSavingSettings;
        private set => SetProperty(ref isSavingSettings, value);
    }

    public bool HasSettingsSaveError
    {
        get => hasSettingsSaveError;
        private set => SetProperty(ref hasSettingsSaveError, value);
    }

    public bool IsHomeSelected
    {
        get => isHomeSelected;
        private set
        {
            if (SetProperty(ref isHomeSelected, value))
            {
                if (!value) DismissStreamSearchDropdown();
                OnPropertyChanged(nameof(IsHomeVisible));
                OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
            }
        }
    }

    public bool IsHomeVisible => IsHomeSelected && !IsVideoFullscreenActive && !IsStreamOnlyFullscreenActive;

    public bool IsFollowedHomePageSelected => !IsRecentHomePageSelected &&
        !IsTwitchVodsHomePageSelected &&
        !IsBrowseHomePageSelected && !IsDownloadsHomePageSelected;

    public bool IsDownloadsHomePageSelected
    {
        get => isDownloadsHomePageSelected;
        private set
        {
            if (!SetProperty(ref isDownloadsHomePageSelected, value)) return;
            OnPropertyChanged(nameof(IsDownloadsHomePageVisible));
            OnPropertyChanged(nameof(IsFollowedHomePageSelected));
            OnPropertyChanged(nameof(IsFollowedHomePageVisible));
        }
    }

    public bool IsDownloadsHomePageVisible => IsDownloadsHomePageSelected;

    public bool IsFollowedHomePageVisible => IsFollowedHomePageSelected;

    public bool IsRecentHomePageSelected
    {
        get => isRecentHomePageSelected;
        private set
        {
            if (SetProperty(ref isRecentHomePageSelected, value))
            {
                if (value) IsDownloadsHomePageSelected = false;
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
                if (value) IsDownloadsHomePageSelected = false;
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
                if (value) IsDownloadsHomePageSelected = false;
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

    public bool IsBrowseCategoriesPageVisible { get => browse.IsBrowseCategoriesPageVisible; }

    public bool IsBrowseStreamsPageVisible { get => browse.IsBrowseStreamsPageVisible; }

    public string FollowedChannelsStatus { get => followed.FollowedChannelsStatus; private set => followed.FollowedChannelsStatus = value; }

    public bool IsFollowedChannelsRefreshing { get => followed.IsFollowedChannelsRefreshing; private set => followed.IsFollowedChannelsRefreshing = value; }

    public bool HasLiveFollowedChannels { get => followed.HasLiveFollowedChannels; }

    public bool IsFollowedChannelsEmptyVisible { get => followed.IsFollowedChannelsEmptyVisible; }

    public string OfflineFollowedChannelsStatus => followed.OfflineFollowedChannelsStatus;

    public string OfflineFollowedChannelsCountText => followed.OfflineFollowedChannelsCountText;

    public bool HasOfflineFollowedChannels => followed.HasOfflineFollowedChannels;

    public bool IsOfflineFollowedChannelsEmptyVisible => followed.IsOfflineFollowedChannelsEmptyVisible;

    public bool HasRecentStreams { get => recent.HasRecentStreams; }

    public bool IsRecentStreamsEmptyVisible { get => recent.IsRecentStreamsEmptyVisible; }

    public string RecentStreamsStatus { get => recent.RecentStreamsStatus; }

    public string FollowedChannelsLastUpdatedText { get => followed.FollowedChannelsLastUpdatedText; }

    public string KickFollowedChannelsText { get => followed.KickFollowedChannelsText; set => followed.KickFollowedChannelsText = value; }

    public string KickFollowImportStatus { get => followed.KickFollowImportStatus; private set => followed.KickFollowImportStatus = value; }

    public string KickImportedFollowsSummary { get => followed.KickImportedFollowsSummary; }

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
                if (value) DismissStreamSearchDropdown();
                else backgroundOperationController.Track(settingsAutoSave.FlushAsync());
                OnPropertyChanged(nameof(IsPlaybackWorkspaceVisible));
                OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
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
            OnPropertyChanged(nameof(IsDownloadsSettingsSelected));
            OnPropertyChanged(nameof(IsAccountsSettingsSelected));
            OnPropertyChanged(nameof(IsChatSettingsSelected));
            OnPropertyChanged(nameof(IsHotkeysSettingsSelected));
            OnPropertyChanged(nameof(IsAdvancedSettingsSelected));
        }
    }

    public bool IsGeneralSettingsSelected => SelectedSettingsCategory == SettingsCategory.General;

    public bool IsPlaybackSettingsSelected => SelectedSettingsCategory == SettingsCategory.Playback;

    public bool IsDownloadsSettingsSelected => SelectedSettingsCategory == SettingsCategory.Downloads;

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
                if (value) DismissStreamSearchDropdown();
                ApplyVideoLayout();
                ApplyInactivePlaybackPolicyInBackground();
                RaiseChatVisibilityProperties();
                OnPropertyChanged(nameof(IsHomeVisible));
                OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
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
                if (value) DismissStreamSearchDropdown();
                RaiseChatVisibilityProperties();
                OnPropertyChanged(nameof(IsHomeVisible));
                OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
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
        { Platform: PlatformKind.Kick, Kind: StreamTargetKind.Live } => "Create a 30-second Kick clip in the background and open it in your browser",
        { Platform: PlatformKind.Kick } => "Kick clips are available for live tabs only",
        { Platform: PlatformKind.Twitch, Kind: StreamTargetKind.Live } => "Create a 30-second Twitch clip",
        { Platform: PlatformKind.Twitch } => "Twitch clips are available for live tabs only",
        _ => "Select a live Twitch or Kick tab to create a clip"
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
            $"Applies to {tab.Target.Platform}: {tab.Target.Channel}.",
        ChatLayout.Overlay =>
            "Default for streams without a saved overlay size.",
        ChatLayout.Docked => "Applies to docked chat for all streams.",
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
            settingsAutoSave.RequestSave();
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

        downloads.Initialize();
        followed.Initialize();
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
        Downloads,
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
            return browse.IsStreamsPageSelected && SelectedBrowseCategory is { } category
                ? new(NavigationPage.BrowseStreams, BrowsePlatform: SelectedBrowsePlatform, BrowseCategory: category)
                : new(NavigationPage.BrowseCategories, BrowsePlatform: SelectedBrowsePlatform);
        }

        if (IsDownloadsHomePageSelected) return new(NavigationPage.Downloads);

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
            case NavigationPage.Downloads:
                ShowDownloadsHomePage();
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
                    if (!browse.IsStreamsPageSelected || SelectedBrowseCategory != category)
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

        // Finish selecting Home before closing Settings so navigation history records
        // the final destination instead of an intermediate return to the playing tab.
        IsSettingsOpen = false;
        StatusMessage = "Home";
        RecordNavigation();
    }

    private void ShowFollowedHomePage()
    {
        IsDownloadsHomePageSelected = false;
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

    private void ShowDownloadsHomePage()
    {
        CancelActiveBrowseCategoryViewerCountLoad();
        IsRecentHomePageSelected = false;
        IsTwitchVodsHomePageSelected = false;
        IsBrowseHomePageSelected = false;
        IsDownloadsHomePageSelected = true;
        StatusMessage = DownloadsStatus;
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

    private void ReturnToBrowseCategoriesPage() => browse.ReturnToBrowseCategoriesPage();

    internal void SetBrowseStreamsPageSelected(bool value) => browse.SetBrowseStreamsPageSelected(value);

    private void RaiseBrowsePageStateChanged() => browse.RaiseBrowsePageStateChanged();

    private void ToggleReplaySeekBar()
    {
        IsReplaySeekBarUiVisible = !IsReplaySeekBarUiVisible;
        StatusMessage = IsReplaySeekBarUiVisible
            ? "Replay seekbar shown"
            : "Replay seekbar hidden";
    }

    private Task RefreshFollowedChannelsAsync() => followed.RefreshFollowedChannelsAsync();

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

    private void SelectVodPlatform(PlatformKind platform) => vodLibrary.SelectVodPlatform(platform);

    private Task SearchTwitchVodsAsync(bool reset) => vodLibrary.SearchTwitchVodsAsync(reset);

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

    private async Task OpenOfflineVodAsync(StreamTarget target, string quality)
    {
        IsHomeSelected = false;
        try { await OpenStreamAsync(target, quality: quality); }
        catch
        {
            IsHomeSelected = true;
            ApplyVideoLayout();
            RecordNavigation();
            throw;
        }
    }

    private void EnsureDownloadNotOpen(VodDownloadItem item)
    {
        var itemDirectoryName = item.Id.ToString("N");
        if (Tabs.Any(tab => tab.Target.IsOfflineVod &&
            (string.Equals(tab.Target.LocalMediaPath, item.LocalMediaPath, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(tab.Target.LocalMediaPath))),
                 itemDirectoryName, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Close this VOD's offline playback tab before replacing or deleting its downloaded files.");
    }

    private void CancelTwitchVodSearchDebounce() => vodLibrary.CancelTwitchVodSearchDebounce();

    private void SelectBrowsePlatform(PlatformKind platform) => browse.SelectBrowsePlatform(platform);

    private Task LoadBrowseCategoriesAsync(bool reset) => browse.LoadBrowseCategoriesAsync(reset);

    private Task SelectBrowseCategoryAsync(BrowseCategoryViewModel category) => browse.SelectBrowseCategoryAsync(category);

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

    private void CancelActiveBrowseCategoryViewerCountLoad() => browse.CancelActiveBrowseCategoryViewerCountLoad();

    private async Task OpenRecentStreamAsync(RecentStreamViewModel stream, bool stayOnHome)
    {
        try
        {
            if (stream.IsOffline)
            {
                await OpenChannelVodsAsync(stream.Platform, stream.Channel);
                return;
            }

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
        bool selectOpenedTab = true,
        string? quality = null)
    {
        if (disposed) return;

        // Capture the initiating search before playback can yield to new input.
        int? searchGenerationToClear = clearInputOnSuccess ? streamSearch.CurrentGeneration : null;
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

            var tab = quality is null
                ? selectOpenedTab ? CreateAndSelectTab(target) : CreateTab(target)
                : selectOpenedTab ? CreateAndSelectTabWithQuality(target, quality) : CreateTabWithQuality(target, quality);
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
                disposalTask = DisposeCoreAsync();
            }

            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var previewCleanup = HoverPreviews.DisposeAsync().AsTask();
        var vodLibraryCleanup = vodLibrary.DisposeAsync().AsTask();
        var downloadsCleanup = downloads.DisposeAsync().AsTask();
        var browseCleanup = browse.DisposeAsync().AsTask();
        var followedCleanup = followed.DisposeAsync().AsTask();
        var recentCleanup = recent.DisposeAsync().AsTask();
        var searchCleanup = streamSearch.DisposeAsync().AsTask();
        streamSearch.PropertyChanged -= HomeFeatureOnPropertyChanged;
        vodLibrary.PropertyChanged -= HomeFeatureOnPropertyChanged;
        downloads.PropertyChanged -= HomeFeatureOnPropertyChanged;
        browse.PropertyChanged -= HomeFeatureOnPropertyChanged;
        followed.PropertyChanged -= HomeFeatureOnPropertyChanged;
        recent.PropertyChanged -= HomeFeatureOnPropertyChanged;
        await settingsAutoSave.DisposeAsync();
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

        if (loggerEntryWrittenHandler is not null)
        {
            logger.EntryWritten -= loggerEntryWrittenHandler;
            loggerEntryWrittenHandler = null;
        }

        Tabs.CollectionChanged -= TabsOnCollectionChanged;
        Settings.PropertyChanged -= SettingsOnPropertyChanged;
        if (observedChatSettings is not null)
        {
            observedChatSettings.PropertyChanged -= ChatSettingsOnPropertyChanged;
            observedChatSettings = null;
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
            await Task.WhenAll(searchCleanup, vodLibraryCleanup, downloadsCleanup, browseCleanup, followedCleanup, recentCleanup);

            var tabDisposals = Tabs
                .ToArray()
                .Select(tab => tab.DisposeAsync().AsTask())
                .ToArray();
            await Task.WhenAll(tabDisposals.Append(previewCleanup));

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
            streamOpenGate.Dispose();
            tabStartController.Dispose();
            chatSettingsApplyGate.Dispose();
            vlcPluginMultiViewChatPolicyGate.Dispose();
            if (appUpdateService is not null)
            {
                appUpdateService.StateChanged -= OnAppUpdateStateChanged;
                (appUpdateService as IDisposable)?.Dispose();
            }
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

    private Task OpenOfflineSearchResultVodsAsync(StreamSearchResultViewModel result) =>
        OpenChannelVodsAsync(result.Platform, result.Channel);

    private async Task OpenChannelVodsAsync(PlatformKind platform, string channel)
    {
        if (disposed) return;
        IsHomeSelected = true;
        ShowTwitchVodsHomePage();
        SelectVodPlatform(platform);
        TwitchVodSearchText = channel;
        CancelTwitchVodSearchDebounce();
        SetStreamSearchDropdownOpen(false);
        await SearchTwitchVodsAsync(reset: true);
    }

    private void HomeFeatureOnPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(e.PropertyName);

    private void SetStreamSearchDropdownOpen(bool value) => streamSearch.SetStreamSearchDropdownOpen(value);

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
        // A failed lookup still carries the parser's VOD-ID placeholder. Only
        // metadata with a resolved broadcaster can replace the default tab title.
        if (target.IsExplicitTwitchVod && !string.IsNullOrWhiteSpace(target.BroadcasterId) &&
            !string.IsNullOrWhiteSpace(target.DisplayTitle) &&
            string.Equals(tab.Title, tab.Target.TabTitle, StringComparison.Ordinal))
        {
            tab.Title = target.TabTitle;
        }
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
        if (searchGenerationToClear == streamSearch.CurrentGeneration)
        {
            NewStreamText = "";
        }
    }

    private StreamTabViewModel CreateAndSelectTab(StreamTarget target) => CreateAndSelectTabWithQuality(target, null);

    private StreamTabViewModel CreateAndSelectTabWithQuality(StreamTarget target, string? quality)
    {
        var tab = quality is null ? CreateTab(target) : CreateTabWithQuality(target, quality);
        SelectedTab = tab;
        return tab;
    }

    private StreamTabViewModel CreateTab(StreamTarget target) => CreateTabWithQuality(target, null);

    private StreamTabViewModel CreateTabWithQuality(StreamTarget target, string? quality)
    {
        var tab = new StreamTabViewModel(new StreamTabViewModelDependencies
        {
            Target = target,
            Quality = quality ?? SelectedQuality,
            StreamlinkService = streamlinkService,
            PlaybackFactory = playbackFactory,
            ChatFactory = chatFactory,
            Logger = logger,
            Dispatch = dispatch,
            OpenChatLink = OpenChatLink,
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

    private void SetRecentStreamHint(StreamTarget target, string thumbnailUrl, string displayName, string categoryName) => recent.SetRecentStreamHint(target, thumbnailUrl, displayName, categoryName);

    private void EnsureRecentThumbnailRefreshTimerStarted() => recent.EnsureRecentThumbnailRefreshTimerStarted();

    private void RefreshRecentThumbnailsInBackground() => recent.RefreshRecentThumbnailsInBackground();

    private Task RememberRecentStreamAsync(StreamTabViewModel tab, Task<StreamMetadataResult?>? openingMetadata = null) => recent.RememberRecentStreamAsync(tab, openingMetadata);

    private bool IsTabOpenOrStarting(StreamTabViewModel tab)
    {
        return IsTabStartActive(tab) ||
            tab.Status is PlaybackStatus.Playing or PlaybackStatus.Resolving or PlaybackStatus.Starting;
    }

    private bool IsTabStartActive(StreamTabViewModel tab)
        => tabStartController.IsActive(tab.Id);

    private void StartTabInBackground(
        StreamTabViewModel tab,
        int? searchGenerationToClear = null,
        Task<StreamMetadataResult?>? openingMetadata = null)
    {
        if (disposed || tabStartController.TryBegin(tab.Id) is not { } registration)
        {
            return;
        }

        ApplyVideoLayout();
        var start = () => backgroundOperationController.Track(
            StartTabAndUpdateStatusAsync(tab, registration, searchGenerationToClear, openingMetadata));
        try
        {
            if (tryDispatch is not null)
            {
                if (!tryDispatch(start))
                {
                    tabStartController.End(registration);
                }
            }
            else
            {
                dispatch(start);
            }
        }
        catch
        {
            tabStartController.End(registration);
            throw;
        }
    }

    private async Task StartTabAndUpdateStatusAsync(
        StreamTabViewModel tab,
        TabStartController.StartRegistration registration,
        int? searchGenerationToClear,
        Task<StreamMetadataResult?>? openingMetadata)
    {
        if (disposed)
        {
            tabStartController.End(registration);
            return;
        }

        await Task.Yield();
        try
        {
            await tabStartController.RunBegunAsync(
                registration,
                async cancellationToken =>
                {
                    var startResult = await tab.StartWithResultAsync(
                        Settings,
                        ShouldUseStableMultiStreamStartupProfile(tab),
                        ShouldUseMultiStreamResourceProfile(tab),
                        cancellationToken);
                    if (disposed || cancellationToken.IsCancellationRequested || !Tabs.Contains(tab))
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
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested || registration.Token.IsCancellationRequested)
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

    private Task<IReadOnlyList<StreamCandidateProbe>> ProbeCandidatesAsync(IReadOnlyList<StreamTarget> candidates, IReadOnlyList<string> customArguments, CancellationToken cancellationToken) => streamSearch.ProbeCandidatesAsync(candidates, customArguments, cancellationToken);

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
        return SelectedTab?.Target switch
        {
            { Platform: PlatformKind.Twitch, Kind: StreamTargetKind.Live } => twitchClipService is not null,
            { Platform: PlatformKind.Kick, Kind: StreamTargetKind.Live } => kickClipService is not null,
            _ => false
        };
    }

    private async Task CreateClipAsync()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        if (tab.Target.Kind != StreamTargetKind.Live)
        {
            StatusMessage = "Clips are available for live tabs only.";
            return;
        }

        var platform = tab.Target.Platform.ToString();
        if (!CanCreateClip())
        {
            StatusMessage = $"{platform} clip service is unavailable.";
            return;
        }

        var cancellationToken = lifetimeCancellation.Token;
        try
        {
            StatusMessage = $"Creating {platform} clip for {tab.Target.Channel}";
            Uri? clipUri;
            if (tab.Target.Platform == PlatformKind.Kick)
                clipUri = (await kickClipService!.CreateLiveClipAsync(tab.Target, cancellationToken))?.ClipUri;
            else
                clipUri = (await twitchClipService!.CreateLiveClipAsync(tab.Target, Settings.Chat, cancellationToken)).ClipUri;
            cancellationToken.ThrowIfCancellationRequested();
            if (clipUri is null)
            {
                StatusMessage = "Kick clip creation ended without a confirmed publication.";
                return;
            }

            try
            {
                openBrowser(clipUri);
                StatusMessage = $"{platform} clip opened for {tab.Target.Channel}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"{platform} clip created, but the browser could not be opened: {clipUri}";
                logger.Write(AppLogLevel.Warning, $"{platform}Clip", $"{platform} clip was created but could not be opened in the browser.", ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown must not open a browser or publish a late status update.
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{platform} clip creation was cancelled.";
        }
        catch (Exception ex) when (!disposed)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, $"{platform}Clip", $"{platform} clip creation failed.", ex);
        }
    }

    private async Task StopSelectedAsync()
    {
        if (SelectedTab is { } tab)
        {
            tabStartController.Cancel(tab.Id);
            await tab.StopAsync();
            if (!disposed && ReferenceEquals(SelectedTab, tab))
                StatusMessage = $"{tab.Target.DisplayName} stopped";
        }
    }

    private async Task PauseSelectedAsync()
    {
        if (SelectedTab is { } tab)
        {
            await tab.PauseOrResumeCommand.ExecuteAsync();
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
            backgroundOperationController.Track(tab.TryReleaseNativeOverlayChatInputFocusAsync());
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

    internal bool CloseTabs(IReadOnlyList<StreamTabViewModel> closingTabs)
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
        tabStartController.Cancel(tab.Id);
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
        IsSavingSettings = true;
        HasSettingsSaveError = false;
        SettingsSaveStatus = "Saving changes…";
        var saved = false;
        try
        {
            Settings.FollowedChannels.KickChannelSlugs = ParseKickFollowedChannelSlugs(KickFollowedChannelsText);
            await settingsService.SaveAsync(Settings);
            saved = true;
            await ApplyChatSettingsAsync(reconfigurePlayback: true);
            StatusMessage = "Settings saved";
            SettingsSaveStatus = $"Settings saved at {DateTime.Now:t}.";
            _ = RefreshFollowedChannelsAsync();
        }
        catch (OperationCanceledException)
        {
            SettingsSaveStatus = saved
                ? "Settings saved. Applying playback changes was canceled."
                : "Saving was canceled. Try again to keep your changes.";
            throw;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HasSettingsSaveError = true;
            SettingsSaveStatus = saved
                ? $"Settings saved, but playback could not be updated. {ex.Message}"
                : $"Could not save settings. {ex.Message}";
            logger.Write(AppLogLevel.Warning, "Settings", "Failed to save settings.", ex);
        }
        finally
        {
            IsSavingSettings = false;
        }
    }

    private void OnSettingsAutoSaved(Exception? error)
    {
        if (error is not null)
        {
            logger.Write(AppLogLevel.Warning, "Settings", "Could not automatically save settings.", error);
        }

        dispatch(() =>
        {
            if (disposed) return;
            HasSettingsSaveError = error is not null;
            SettingsSaveStatus = error is null
                ? "Changes are saved automatically."
                : $"Could not save settings. {error.Message}";
        });
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

    internal void OpenChatLink(Uri uri)
    {
        if (!ChatLinkParser.IsSupportedWebUri(uri))
        {
            return;
        }

        try
        {
            openBrowser(uri);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Write(AppLogLevel.Warning, "Chat", "Could not open a link from chat.", ex);
        }
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

        if (e.PropertyName == nameof(AppSettings.DefaultQuality))
        {
            SelectedQuality = Settings.DefaultQuality;
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

        if (e.PropertyName == nameof(AppSettings.MultiStreamEnabled))
        {
            OnPropertyChanged(nameof(IsMultiStreamEnabled));
            OnPropertyChanged(nameof(MultiStreamToggleToolTip));
            ApplyVideoLayout();
            ApplyInactivePlaybackPolicyInBackground();
            return;
        }

        if (e.PropertyName is nameof(AppSettings.KeepInactiveTabsRunning) or nameof(AppSettings.PauseInactiveVodTabs))
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

    private static List<string> ParseKickFollowedChannelSlugs(string text) => FollowedChannelsViewModel.ParseKickFollowedChannelSlugs(text);

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

    private void StreamSearchResultsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => streamSearch.StreamSearchResultsOnCollectionChanged(sender, e);

    private void TwitchVodsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => vodLibrary.TwitchVodsOnCollectionChanged(sender, e);

    private void BrowseCategoriesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => browse.BrowseCategoriesOnCollectionChanged(sender, e);

    private void BrowseStreamsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => browse.BrowseStreamsOnCollectionChanged(sender, e);

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
        if (tab.NeverMute || tab.IsVideoVisible || tab.IsDetached || tab.IsBusy)
        {
            return true;
        }

        return tab.Target.IsExplicitVod
            ? !Settings.PauseInactiveVodTabs
            : Settings.KeepInactiveTabsRunning;
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
