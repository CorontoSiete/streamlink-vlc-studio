using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.App.Wpf.Chat;
using StreamlinkVlcStudio.App.Wpf.Controls;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Processes;
using StreamlinkVlcStudio.Infrastructure.Vlc;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class StreamTabViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PlaybackStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NativeOverlayGracefulStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NativeOverlayShutdownRequestTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan NativeOverlayInputFocusReleaseTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan NativeOverlayClearTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NativeReplayOverlayFrameWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NativeOverlayPipeConnectTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan VideoSurfaceReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VideoAspectRatioChangingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VideoAspectRatioStableInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan VideoAspectRatioRetryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReplayClockRefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReplayLiveEdgeThreshold = TimeSpan.FromSeconds(15);
    // Resume holds the exact paused timestamp even close to live; only a near-instant pause (still
    // effectively at the live edge) skips the hold, to avoid a pointless reload into replay.
    private static readonly TimeSpan ResumeHoldLiveEdgeTolerance = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReplaySeekStep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplayChatWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReplayChatPrefetchThreshold = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReplayClockSampleTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplayClockMaximumPlausibleDuration = TimeSpan.FromDays(14);
    private static readonly TimeSpan ReplayDiagnosticsSlowThreshold = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan NativeReplayOverlayRefreshDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan NativeReplayOverlayDefaultAnimationDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan[] NativeReplayOverlayWarmupRefreshDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromSeconds(3)
    ];
    private static readonly TimeSpan DefaultTwitchLiveDvrPromotionPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DockedLocalEchoDeduplicationWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ViewerCountRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ViewerCountRetryDelay = TimeSpan.FromSeconds(20);
    internal const int DefaultVolume = 80;
    private const int MaxChatMessages = 100;
    private const int MaxRecentChatMessageIds = 256;
    private const int MaxCapturedReplayChatMessages = 100_000;
    private const int NativeReplayOverlayMessagesPerScrollNotch = 3;
    private const int VideoAspectRatioStableSampleThreshold = 3;
    private const string TwitchLiveDvrReplayIdPrefix = "live-dvr-";
    private const string NativeOverlayFontSizeArgument = "--font-size";
    private const string KickVodReplayChatStatusMessageIdPrefix = "kick-vod-replay-chat-status";
    private const double DefaultVideoAspectRatio = 16.0 / 9.0;
    private readonly IStreamlinkService streamlinkService;
    private readonly IPlaybackEngineFactory playbackFactory;
    private readonly IChatClientFactory chatFactory;
    private readonly IViewerCountService? viewerCountService;
    private readonly IReplayResolver? replayResolver;
    private readonly IReplayChatProvider? replayChatProvider;
    private readonly IKickChatHistoryProvider? kickChatHistoryProvider;
    private readonly IKickEventSubscriptionService? kickEventSubscriptionService;
    private readonly ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver;
    private readonly IAppLogger logger;
    private readonly Action<Action> dispatch;
    private readonly PlaybackResourceCoordinator playbackResourceCoordinator;
    private readonly PlaybackCleanupController playbackCleanupController;
    private readonly ChatClientEventCoordinator chatClientEventCoordinator;
    private readonly BoundedProcessRunner processRunner = new();
    private readonly NativeOverlayCapabilityProbe nativeOverlayCapabilityProbe = new();
    private readonly object disposalGate = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim playbackTransitionGate = new(1, 1);
    private Task? disposalTask;
    private bool disposed;
    private readonly object chatMessageUiGate = new();
    private readonly Queue<PendingChatMessage> pendingChatMessages = [];
    private readonly List<DockedLocalEcho> pendingDockedLocalEchoes = [];
    private readonly Queue<string> recentChatMessageIds = [];
    private readonly HashSet<string> recentChatMessageIdSet = new(StringComparer.Ordinal);
    private readonly object capturedReplayChatGate = new();
    private readonly ReplayChatWindowSelector capturedReplayChatSelector = new();
    private readonly List<ChatMessage> capturedReplayChatSourceMessages = [];
    private readonly List<ReplayChatBackfillCoverageRange> capturedReplayChatBackfillCoverageRanges = [];
    private readonly object replayAvailabilityRefreshGate = new();
    private readonly object liveDvrPromotionPollingGate = new();
    private readonly TimeSpan twitchLiveDvrPromotionPollInterval;
    private readonly object videoSurfaceGate = new();
    private readonly VideoAspectRatioPollingBackoff videoAspectRatioPollingBackoff = new(
        VideoAspectRatioRetryInterval,
        VideoAspectRatioChangingInterval,
        VideoAspectRatioStableInterval,
        VideoAspectRatioStableSampleThreshold);
    private readonly object chatConnectionGate = new();
    private readonly object nativeOverlayStartupGate = new();
    private readonly object kickWebhookSubscriptionGate = new();
    private readonly object replayChatLoadGate = new();
    private readonly object replayClockAnchorGate = new();
    private readonly object replayClockUiGate = new();
    private readonly object replayChatUiGate = new();
    private readonly object replayPlaybackUrlResolutionGate = new();
    private readonly object replaySeekPreviewUiGate = new();
    private readonly object nativeReplayOverlayRefreshGate = new();
    private readonly object nativeReplayOverlayFrameSchedulerGate = new();
    private readonly object nativeReplayOverlayAnimationGate = new();
    private readonly object nativeReplayOverlayScrollGate = new();
    private readonly object nativeOverlayStopNoticeGate = new();
    private readonly ReplayChatWindowSelector replayChatSelector = new();
    private readonly SemaphoreSlim replayPlaybackTransitionGate = new(1, 1);
    private readonly NativeOverlayReplayEventHost nativeReplayOverlayEventHost;
    private readonly NativeReplayOverlayFrameWriteGate nativeReplayOverlayFrameWriteGate;
    private readonly NativeReplayOverlayRenderState nativeReplayOverlayRenderState = new();
    private NativeReplayOverlayFrameScheduler? nativeReplayOverlayFrameScheduler;
    private Task<NativeReplayOverlayFrameScheduler>? nativeReplayOverlayFrameSchedulerCreationTask;
    private TaskCompletionSource<IntPtr> videoHandleReady = CreateVideoHandleReadySource();
    private TaskCompletionSource videoSurfaceStateChanged = CreateVideoSurfaceStateChangedSource();
    private readonly SemaphoreSlim nativeOverlayProcessGate = new(1, 1);
    private IStreamTransportSession? streamSession;
    private IPlaybackEngine? playbackEngine;
    private IChatClient? chatClient;
    private ITwitchPredictionClient? twitchPredictionClient;
    private ChatSettings? chatSettings;
    private AppSettings? currentSettings;
    private ParkingVideoSurface? parkingVideoSurface;
    private Process? nativeOverlayProcess;
    private CancellationTokenSource? viewerCountPollingCancellation;
    private CancellationTokenSource? videoAspectRatioPollingCancellation;
    private CancellationTokenSource? replayClockPollingCancellation;
    private CancellationTokenSource? replayAvailabilityRefreshCancellation;
    private CancellationTokenSource? liveDvrPromotionPollingCancellation;
    private Task? chatConnectionTask;
    private CancellationTokenSource? chatConnectionCancellation;
    private Task? nativeOverlayStartupTask;
    private CancellationTokenSource? nativeOverlayStartupCancellation;
    private Task? kickWebhookSubscriptionTask;
    private CancellationTokenSource? kickWebhookSubscriptionCancellation;
    private Task? viewerCountPollingTask;
    private Task? videoAspectRatioPollingTask;
    private Task? replayClockPollingTask;
    private Task? replayAvailabilityRefreshTask;
    private Task? liveDvrPromotionPollingTask;
    private Task? replayChatLoadTask;
    private ReplayPlaybackUrlResolution? replayPlaybackUrlResolution;
    private ReplayPlaybackUrlReadiness replayPlaybackUrlReadiness = ReplayPlaybackUrlReadiness.None;
    private ReplayPlaybackUrlKey? replayPlaybackUrlReadinessKey;
    private ReplayPlaybackUrlKey? currentReplayPlaybackKey;
    private ReplaySessionInfo? replaySession;
    private CancellationTokenSource? replayChatLoadCancellation;
    private ReplayChatLoadRequest? activeCapturedReplayChatLoadRequest;
    private ReplayChatLoadRequest? pendingCapturedReplayChatLoadRequest;
    private long replayAvailabilityRefreshVersion;
    private long chatConnectionVersion;
    private long nativeOverlayStartupVersion;
    private long replayChatStateVersion;
    private long replaySeekOperationVersion;
    private TimeSpan? pendingResumeHoldPosition;
    private bool pendingResumeHoldAllowsLiveTransition;
    private ReplayClockSnapshot? pausedReplayClock;
    private ReplayClockUiUpdate? pendingReplayClockUiSample;
    private bool replayClockUiDispatchQueued;
    private ReplayChatWindowUiUpdate? pendingReplayChatWindowUiUpdate;
    private bool replayChatWindowUiDispatchQueued;
    private double pendingReplaySeekPreviewTextValue;
    private bool replaySeekPreviewTextDispatchQueued;
    private bool chatMessageUiDispatchQueued;
    private bool nativeReplayOverlayRefreshQueued;
    private bool nativeReplayOverlayRefreshPendingAfterSeek;
    private int nativeReplayOverlayVideoWidth;
    private int nativeReplayOverlayVideoHeight;
    private string nativeReplayOverlayWarmupSessionKey = "";
    private long nativeReplayOverlayWarmupVersion;
    private CancellationTokenSource? nativeReplayOverlayWarmupCancellation;
    private long nativeReplayOverlayResizePersistenceResumeAfterVersion;
    private int nativeReplayOverlayMessageOffset;
    private int nativeReplayOverlayMaximumMessageOffset;
    private ChatMessage? nativeReplayOverlayAnchorMessage;
    private string nativeReplayOverlayScrollSessionKey = "";
    private ReplayChatWindowKey lastReplayChatWindowKey = ReplayChatWindowKey.Empty;
    private TimeSpan? kickSeekbackReplayChatBacklogStart;
    private string kickSeekbackReplayChatBacklogReplayId = "";
    private TimeSpan? replayChatLoadedFrom;
    private TimeSpan? replayChatLoadedThrough;
    private bool replayClockAnchorAvailable;
    private TimeSpan replayClockAnchorOffset;
    private DateTimeOffset replayClockAnchorObservedAtUtc;
    private long replayClockAnchorSeekGeneration;
    private bool replayClockAnchorAwaitingSeekConfirmation;
    private bool replayClockAcceptedSampleAvailable;
    private TimeSpan replayClockAcceptedSamplePosition;
    private DateTimeOffset replayClockAcceptedSampleObservedAtUtc;
    private long replayClockAcceptedSampleSeekGeneration;
    private string capturedReplayChatReplayId = "";
    private DateTimeOffset? capturedReplayChatStreamStartedAtUtc;
    private string? nativeOverlayPipeName;
    private string? nativeOverlayLaunchKey;
    private string? nativeOverlayTokenFile;
    private long nativeReplayOverlayAnimationTimerVersion;
    private CancellationTokenSource? nativeReplayOverlayAnimationCancellation;
    private long nativeReplayOverlayAnimationEpochTimestamp = Stopwatch.GetTimestamp();
    private long nativeReplayOverlayRenderContentVersion;
    private object? nativeReplayOverlayActiveImageCachePinOwner;
    private readonly HashSet<AnimatedEmoteImageCacheKey> nativeReplayOverlayPendingImageLoads = [];
    private readonly HashSet<int> suppressedNativeOverlayStoppedProcessIds = [];
    private bool isDirectExplicitVodReplayPlayback;
    private CancellationTokenSource? activeStartCancellation;
    private KickOverlayChannelInfo? resolvedKickOverlayChannelInfo;
    private string? resolvedTwitchOverlayRoomId;
    private bool multiStreamResourceProfile;
    private bool playbackEngineNativeOverlayRequested;
    private string playbackEngineOverlayDirectory = "";
    private string title;
    private string profileImageUrl = "";
    private string streamTitle = "";
    private string categoryName;
    private string quality;
    private PlaybackStatus status = PlaybackStatus.Empty;
    private string errorMessage = "";
    private bool isSelected;
    private bool isVideoVisible;
    private bool videoPlacementKnown;
    private bool isMainVideoSurfaceExpected;
    private bool isDetached;
    private bool isBusy;
    private bool isChatVisible = true;
    private bool isDockedChatPanelVisible = true;
    private bool isDockedChatOverrideActive;
    private bool overlayUnavailableDockFallback;
    private int videoGridRow;
    private int videoGridColumn;
    private int videoGridRowSpan = 1;
    private int videoGridColumnSpan = 1;
    private double videoAspectRatio = DefaultVideoAspectRatio;
    private bool isMergedTabGroupMember;
    private bool isFirstMergedTabGroupMember;
    private bool isLastMergedTabGroupMember;
    private int volume = DefaultVolume;
    private bool isMuted;
    private bool isSelectedForAudio = true;
    private IntPtr videoHandle;
    private long videoHandleVersion;
    private string outgoingChatText = "";
    private string twitchPredictionTitle = "";
    private int twitchPredictionDurationSeconds = 120;
    private string viewerCountText = "--";
    private string viewerCountToolTip = "Viewer count has not loaded yet.";
    private bool isReplaySeekBarVisible;
    private bool isReplaySeekEnabled;
    private bool isReplaySeekInProgress;
    private bool isReplaySeekPreviewActive;
    private bool isReplayMode;
    private bool isBehindLive;
    private bool backgroundResourceServicesSuspended;
    private bool livePlaybackConnectionSuspended;
    private double replaySeekValue;
    private double replaySeekSliderValue;
    private double replaySeekMaximum = 1;
    private string replayElapsedText = "0:00";
    private string replayDurationText = "0:00";
    private string replayLiveStateText = "Live";
    private string replaySeekToolTip = "Replay availability has not been checked yet.";
    private TimeSpan lastReplayChatOffset = TimeSpan.MinValue;
    private bool capturedReplayChatEvictedMessages;
    private bool capturedReplayChatNoticeShown;
    private TwitchPredictionAccessState twitchPredictionAccess = TwitchPredictionAccessState.Pending;
    private TwitchPredictionFeedItemViewModel? activeTwitchPredictionFeedItem;
    private System.Threading.Timer? twitchPredictionClockTimer;
    private bool isTwitchPredictionRequestInFlight;

    internal StreamTabViewModel(StreamTabViewModelDependencies dependencies)
    {
        var target = dependencies.Target;
        var quality = dependencies.Quality;
        var streamlinkService = dependencies.StreamlinkService;
        var playbackFactory = dependencies.PlaybackFactory;
        var chatFactory = dependencies.ChatFactory;
        var logger = dependencies.Logger;
        var dispatch = dependencies.Dispatch;
        var initialVolume = dependencies.InitialVolume;
        var viewerCountService = dependencies.ViewerCountService;
        var replayResolver = dependencies.ReplayResolver;
        var replayChatProvider = dependencies.ReplayChatProvider;
        var kickChatHistoryProvider = dependencies.KickChatHistoryProvider;
        var kickEventSubscriptionService = dependencies.KickEventSubscriptionService;
        var twitchSubOnlyVodResolver = dependencies.TwitchSubOnlyVodResolver;
        var twitchLiveDvrPromotionPollInterval = dependencies.TwitchLiveDvrPromotionPollInterval;

        Target = target;
        this.quality = quality;
        this.streamlinkService = streamlinkService;
        this.playbackFactory = playbackFactory;
        this.chatFactory = chatFactory;
        this.viewerCountService = viewerCountService;
        this.logger = logger;
        this.dispatch = action => dispatch(() =>
        {
            if (!disposed)
            {
                action();
            }
        });
        this.replayResolver = replayResolver;
        this.replayChatProvider = replayChatProvider;
        this.kickChatHistoryProvider = kickChatHistoryProvider;
        this.kickEventSubscriptionService = kickEventSubscriptionService;
        this.twitchSubOnlyVodResolver = twitchSubOnlyVodResolver;
        playbackResourceCoordinator = new PlaybackResourceCoordinator(logger, () => Target.DisplayName);
        playbackCleanupController = new PlaybackCleanupController(logger, () => Target.DisplayName);
        chatClientEventCoordinator = new ChatClientEventCoordinator(
            ChatClientOnMessageReceived,
            ChatClientOnStatusChanged,
            TwitchPredictionClientOnPredictionReceived,
            TwitchPredictionClientOnPredictionAccessChanged,
            access => this.dispatch(() => ApplyTwitchPredictionAccess(access)));
        this.twitchLiveDvrPromotionPollInterval =
            twitchLiveDvrPromotionPollInterval is { } interval && interval > TimeSpan.Zero
                ? interval
                : DefaultTwitchLiveDvrPromotionPollInterval;
        nativeReplayOverlayEventHost = new NativeOverlayReplayEventHost(
            logger,
            this.dispatch,
            InvalidateNativeReplayOverlayFrame,
            GetNativeReplayOverlayVideoHeight,
            replayScrolled: ScrollNativeReplayOverlay,
            replayScrollPositionChanged: SetNativeReplayOverlayScrollPosition);
        nativeReplayOverlayFrameWriteGate = new NativeReplayOverlayFrameWriteGate(
            logger,
            WriteNativeReplayOverlayFrameMessageAsync,
            () => nativeReplayOverlayRenderState.Version,
            OnNativeReplayOverlayFrameWriteFailed,
            ReplayDiagnosticsSlowThreshold,
            OnNativeReplayOverlayFrameWriteSucceeded,
            () => playbackEngine?.NativeOverlayPipeName,
            writeTimeout: NativeReplayOverlayFrameWriteTimeout,
            validateProtocolMessages: true);
        AnimatedEmoteImage.ImageCacheEntryCompleted += OnAnimatedEmoteImageCacheEntryCompleted;
        DockedChatBadgeCatalog.Shared.CatalogChanged += OnChatRenderCatalogChanged;
        DockedChatEmoteCatalog.Shared.CatalogChanged += OnChatRenderCatalogChanged;
        title = target.TabTitle;
        profileImageUrl = (target.ProfileImageUrl ?? "").Trim();
        categoryName = target.CategoryName?.Trim() ?? "";
        volume = NormalizeVolume(initialVolume);
        SendChatMessageCommand = new AsyncRelayCommand(SendChatMessageAsync, () => !string.IsNullOrWhiteSpace(OutgoingChatText) && CanSendChatMessages);
        RewindReplay30SecondsCommand = new AsyncRelayCommand(RewindReplay30SecondsAsync, () => CanStepReplay);
        FastForwardReplay30SecondsCommand = new AsyncRelayCommand(FastForwardReplay30SecondsAsync, () => CanStepReplay);
        ReturnToLiveCommand = new AsyncRelayCommand(ReturnToLiveAsync, () => CanReturnToLive);
        StartTwitchPredictionCommand = new AsyncRelayCommand(StartTwitchPredictionAsync, () => CanStartTwitchPrediction);
        AddTwitchPredictionOutcomeCommand = new RelayCommand(AddTwitchPredictionOutcome, () => CanAddTwitchPredictionOutcome);
        InitializeTwitchPredictionOutcomeInputs();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public StreamTarget Target { get; }
    public ObservableCollection<ChatMessage> ChatMessages { get; } = [];
    public ObservableCollection<ChatMessage> DockedChatMessages { get; } = [];
    public ObservableCollection<object> DockedChatFeedItems { get; } = [];
    public ObservableCollection<TwitchPredictionOutcomeInputViewModel> TwitchPredictionOutcomeInputs { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public AsyncRelayCommand SendChatMessageCommand { get; }
    public AsyncRelayCommand RewindReplay30SecondsCommand { get; }
    public AsyncRelayCommand FastForwardReplay30SecondsCommand { get; }
    public AsyncRelayCommand ReturnToLiveCommand { get; }
    public AsyncRelayCommand StartTwitchPredictionCommand { get; }
    public RelayCommand AddTwitchPredictionOutcomeCommand { get; }
    public event EventHandler? AudioStateApplied;

    internal Task PlaybackCleanupIdleTask => playbackCleanupController.IdleTask;

    internal Task ReplayChatLoadIdleTask
    {
        get
        {
            lock (replayChatLoadGate)
            {
                return replayChatLoadTask ?? Task.CompletedTask;
            }
        }
    }

    public string ProfileImageUrl
    {
        get => profileImageUrl;
        private set
        {
            if (SetProperty(ref profileImageUrl, value))
            {
                OnPropertyChanged(nameof(HasProfileImage));
            }
        }
    }

    public bool HasProfileImage => !string.IsNullOrWhiteSpace(ProfileImageUrl);

    public void SetProfileImageUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            ProfileImageUrl = url.Trim();
        }
    }

    public string Title
    {
        get => title;
        set => SetProperty(ref title, string.IsNullOrWhiteSpace(value) ? Target.Channel : value.Trim());
    }

    public string StreamTitle
    {
        get => streamTitle;
        private set => SetProperty(ref streamTitle, value);
    }

    public string DockedChatHeaderText => $"Chat in {Target.Channel}'s channel";

    /// <summary>
    /// The category the channel is live in. Seeded from <see cref="Target"/> when the tab is
    /// created and then kept current from the live channel poll, so a mid-stream category change
    /// reaches the tab strip instead of showing whatever was set when the tab opened.
    /// </summary>
    public string CategoryName
    {
        get => categoryName;
        private set
        {
            if (SetProperty(ref categoryName, value))
            {
                OnPropertyChanged(nameof(HasCategory));
            }
        }
    }

    public bool HasCategory => !string.IsNullOrWhiteSpace(CategoryName);

    public string Quality
    {
        get => quality;
        set => SetProperty(ref quality, value);
    }

    public PlaybackStatus Status
    {
        get => status;
        private set
        {
            if (SetProperty(ref status, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => Status switch
    {
        PlaybackStatus.Empty => "Ready",
        PlaybackStatus.Resolving => "Resolving stream",
        PlaybackStatus.Starting => "Starting playback",
        PlaybackStatus.Playing => "Live",
        PlaybackStatus.Paused => "Paused",
        PlaybackStatus.Stopped => "Stopped",
        PlaybackStatus.Offline => "Offline",
        PlaybackStatus.Error => "Error",
        _ => Status.ToString()
    };

    public string ViewerCountText
    {
        get => viewerCountText;
        private set => SetProperty(ref viewerCountText, value);
    }

    public string ViewerCountToolTip
    {
        get => viewerCountToolTip;
        private set => SetProperty(ref viewerCountToolTip, value);
    }

    public bool IsReplaySeekBarVisible
    {
        get => isReplaySeekBarVisible;
        private set => SetProperty(ref isReplaySeekBarVisible, value);
    }

    public bool IsReplaySeekEnabled
    {
        get => isReplaySeekEnabled;
        private set
        {
            if (SetProperty(ref isReplaySeekEnabled, value))
            {
                RaiseReplaySeekAvailabilityChanged();
            }
        }
    }

    public bool IsReplaySeekInProgress
    {
        get => isReplaySeekInProgress;
        private set
        {
            if (SetProperty(ref isReplaySeekInProgress, value))
            {
                RaiseReplaySeekAvailabilityChanged();
                OnPropertyChanged(nameof(CanReturnToLive));
                ReturnToLiveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsReplayMode
    {
        get => isReplayMode;
        private set
        {
            if (SetProperty(ref isReplayMode, value))
            {
                OnPropertyChanged(nameof(CanReturnToLive));
                RaiseTwitchPredictionCommandState();
                ReturnToLiveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsReplaySeekPreviewActive
    {
        get => isReplaySeekPreviewActive;
        private set => SetProperty(ref isReplaySeekPreviewActive, value);
    }

    public bool IsBehindLive
    {
        get => isBehindLive;
        private set
        {
            if (SetProperty(ref isBehindLive, value))
            {
                OnPropertyChanged(nameof(CanReturnToLive));
                OnPropertyChanged(nameof(CanSendChatMessages));
                SendChatMessageCommand.RaiseCanExecuteChanged();
                RaiseTwitchPredictionCommandState();
                ReturnToLiveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanReturnToLive => !Target.IsExplicitVod &&
        (IsBehindLive || IsReplayMode) &&
        !IsReplaySeekInProgress;

    public bool CanSeekReplay => IsReplaySeekEnabled &&
        !IsReplaySeekInProgress &&
        IsCurrentReplayPlaybackUrlReadyForSeeking();

    public bool CanStepReplay => CanSeekReplay;

    public bool CanSendChatMessages => !Target.IsExplicitVod && !IsBehindLive;

    private void RaiseReplaySeekAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanSeekReplay));
        OnPropertyChanged(nameof(CanStepReplay));
        RewindReplay30SecondsCommand.RaiseCanExecuteChanged();
        FastForwardReplay30SecondsCommand.RaiseCanExecuteChanged();
    }

    public double ReplaySeekValue
    {
        get => replaySeekValue;
        set
        {
            var normalizedValue = Math.Clamp(value, 0, ReplaySeekMaximum);
            if (SetProperty(ref replaySeekValue, normalizedValue) &&
                !isReplaySeekPreviewActive)
            {
                ReplaySeekSliderValue = normalizedValue;
                ReplayElapsedText = StreamViewModelHelpers.FormatClockTime(TimeSpan.FromSeconds(normalizedValue));
            }
        }
    }

    public double ReplaySeekSliderValue
    {
        get => replaySeekSliderValue;
        set
        {
            var normalizedValue = Math.Clamp(value, 0, ReplaySeekMaximum);
            if (SetProperty(ref replaySeekSliderValue, normalizedValue) &&
                isReplaySeekPreviewActive)
            {
                QueueReplaySeekPreviewTextApply(normalizedValue);
            }
        }
    }

    public double ReplaySeekMaximum
    {
        get => replaySeekMaximum;
        private set => SetProperty(ref replaySeekMaximum, Math.Max(1, value));
    }

    public string ReplayElapsedText
    {
        get => replayElapsedText;
        private set => SetProperty(ref replayElapsedText, value);
    }

    public string ReplayDurationText
    {
        get => replayDurationText;
        private set => SetProperty(ref replayDurationText, value);
    }

    public string ReplayLiveStateText
    {
        get => replayLiveStateText;
        private set => SetProperty(ref replayLiveStateText, value);
    }

    public string ReplaySeekToolTip
    {
        get => replaySeekToolTip;
        private set => SetProperty(ref replaySeekToolTip, value);
    }

    public string ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public bool IsVideoVisible
    {
        get => isVideoVisible;
        private set => SetProperty(ref isVideoVisible, value);
    }

    public bool IsBackgroundResourceServicesSuspended
    {
        get => backgroundResourceServicesSuspended;
        private set => SetProperty(ref backgroundResourceServicesSuspended, value);
    }

    internal bool IsLivePlaybackConnectionSuspended => livePlaybackConnectionSuspended;

    public bool IsDetached
    {
        get => isDetached;
        private set => SetProperty(ref isDetached, value);
    }

    public int VideoGridRow
    {
        get => videoGridRow;
        private set => SetProperty(ref videoGridRow, value);
    }

    public int VideoGridColumn
    {
        get => videoGridColumn;
        private set => SetProperty(ref videoGridColumn, value);
    }

    public int VideoGridRowSpan
    {
        get => videoGridRowSpan;
        private set => SetProperty(ref videoGridRowSpan, value);
    }

    public int VideoGridColumnSpan
    {
        get => videoGridColumnSpan;
        private set => SetProperty(ref videoGridColumnSpan, value);
    }

    public double VideoAspectRatio
    {
        get => videoAspectRatio;
        private set => SetProperty(ref videoAspectRatio, value);
    }

    public bool IsMergedTabGroupMember
    {
        get => isMergedTabGroupMember;
        private set => SetProperty(ref isMergedTabGroupMember, value);
    }

    public bool IsFirstMergedTabGroupMember
    {
        get => isFirstMergedTabGroupMember;
        private set => SetProperty(ref isFirstMergedTabGroupMember, value);
    }

    public bool IsLastMergedTabGroupMember
    {
        get => isLastMergedTabGroupMember;
        private set => SetProperty(ref isLastMergedTabGroupMember, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public bool IsChatVisible
    {
        get => isChatVisible;
        set => SetChatVisibleCore(value, updateChatLifecycle: true);
    }

    public bool IsDockedChatPanelVisible
    {
        get => isDockedChatPanelVisible;
        set => SetProperty(ref isDockedChatPanelVisible, value);
    }

    public bool IsDockedChatOverrideActive => isDockedChatOverrideActive || overlayUnavailableDockFallback;

    public bool SetDockedChatOverrideActive(bool value)
    {
        if (value)
        {
            TryReleaseNativeOverlayChatInputFocus();
        }

        if (!SetProperty(ref isDockedChatOverrideActive, value, nameof(IsDockedChatOverrideActive)))
        {
            return false;
        }

        UpdateNativeChatOverlay();
        return true;
    }

    // Forces the tab into docked chat when the custom VLC plugin overlay cannot be loaded in
    // Overlay mode. Composed into IsDockedChatOverrideActive alongside the theatre/multi-view
    // override so the two policies do not clobber each other.
    private void SetOverlayUnavailableDockFallback(bool value)
    {
        if (value)
        {
            TryReleaseNativeOverlayChatInputFocus();
        }

        if (overlayUnavailableDockFallback == value)
        {
            return;
        }

        overlayUnavailableDockFallback = value;
        OnPropertyChanged(nameof(IsDockedChatOverrideActive));
        if (value)
        {
            IsDockedChatPanelVisible = true;
        }

        UpdateNativeChatOverlay();
    }

    public bool SetChatVisibleForDeferredLifecycle(bool value)
    {
        return SetChatVisibleCore(value, updateChatLifecycle: false);
    }

    private bool SetChatVisibleCore(bool value, bool updateChatLifecycle)
    {
        if (!SetProperty(ref isChatVisible, value))
        {
            return false;
        }

        UpdateNativeChatOverlay();
        if (updateChatLifecycle && currentSettings is not null)
        {
            _ = value ? RestartChatAsync(currentSettings, CancellationToken.None) : StopChatAsync();
        }

        return true;
    }

    public int Volume
    {
        get => volume;
        set
        {
            var clamped = NormalizeVolume(value);
            if (SetProperty(ref volume, clamped))
            {
                ApplyAudio();
            }
        }
    }

    public bool IsMuted
    {
        get => isMuted;
        set
        {
            if (SetProperty(ref isMuted, value))
            {
                ApplyAudio();
            }
        }
    }

    public bool IsAutoMuted => !isSelectedForAudio;

    public string OutgoingChatText
    {
        get => outgoingChatText;
        set
        {
            if (SetProperty(ref outgoingChatText, value))
            {
                SendChatMessageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool CanProcessTwitchPredictionEvents => Target.Platform == PlatformKind.Twitch && !Target.IsExplicitVod;

    public string TwitchPredictionStatusText => CanProcessTwitchPredictionEvents
        ? twitchPredictionAccess.Message
        : "";

    public string TwitchPredictionTitle
    {
        get => twitchPredictionTitle;
        set
        {
            if (SetProperty(ref twitchPredictionTitle, value ?? ""))
            {
                StartTwitchPredictionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int TwitchPredictionDurationSeconds
    {
        get => twitchPredictionDurationSeconds;
        set
        {
            var normalized = Math.Clamp(
                value,
                TwitchPredictionApiClient.MinPredictionWindowSeconds,
                TwitchPredictionApiClient.MaxPredictionWindowSeconds);
            if (SetProperty(ref twitchPredictionDurationSeconds, normalized))
            {
                StartTwitchPredictionCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanAddTwitchPredictionOutcome => TwitchPredictionOutcomeInputs.Count < TwitchPredictionApiClient.MaxOutcomeCount;

    public bool CanStartTwitchPrediction =>
        CanProcessTwitchPredictionEvents &&
        twitchPredictionAccess.CanManage &&
        !isTwitchPredictionRequestInFlight &&
        !IsReplayMode &&
        !IsBehindLive &&
        activeTwitchPredictionFeedItem?.IsOpen != true &&
        !string.IsNullOrWhiteSpace(TwitchPredictionTitle) &&
        TwitchPredictionOutcomeInputs.Count is >= TwitchPredictionApiClient.MinOutcomeCount and <= TwitchPredictionApiClient.MaxOutcomeCount &&
        TwitchPredictionOutcomeInputs.All(outcome => !string.IsNullOrWhiteSpace(outcome.Title));

    public bool UsesNativeOverlay => playbackEngine?.UsesNativeOverlay == true;
    public string? NativeOverlayPipeName => playbackEngine?.NativeOverlayPipeName;
    public string? NativeOverlayPositionStatePath => playbackEngine?.NativeOverlayPositionStatePath;
    internal bool IsNativeReplayOverlayEventHostRunning => nativeReplayOverlayEventHost.IsRunning;
    internal string? NativeReplayOverlayEventHostPipeName => nativeReplayOverlayEventHost.PipeName;
    internal int NativeReplayOverlayMessageOffset
    {
        get
        {
            lock (nativeReplayOverlayScrollGate)
            {
                return nativeReplayOverlayMessageOffset;
            }
        }
    }

    internal int NativeReplayOverlayMaximumMessageOffset
    {
        get
        {
            lock (nativeReplayOverlayScrollGate)
            {
                return nativeReplayOverlayMaximumMessageOffset;
            }
        }
    }
    public void SetVideoHandle(IntPtr handle)
    {
        TaskCompletionSource? stateChanged;
        var handleChanged = false;
        lock (videoSurfaceGate)
        {
            if (videoHandle != handle)
            {
                videoHandle = handle;
                videoHandleVersion++;
                handleChanged = true;
            }

            if (handle == IntPtr.Zero)
            {
                if (videoHandleReady.Task.IsCompleted)
                {
                    videoHandleReady = CreateVideoHandleReadySource();
                }
            }
            else
            {
                videoHandleReady.TrySetResult(handle);
            }

            stateChanged = videoSurfaceStateChanged;
            videoSurfaceStateChanged = CreateVideoSurfaceStateChangedSource();
        }

        stateChanged.TrySetResult();
        if (handleChanged)
        {
            ResetVideoAspectRatioPollingBackoff();
        }

        playbackEngine?.SetVideoHandle(handle);
    }

    public void ClearVideoHandle(IntPtr expectedHandle)
    {
        if (expectedHandle == IntPtr.Zero)
        {
            return;
        }

        TaskCompletionSource? stateChanged = null;
        var shouldClearPlaybackEngine = false;
        lock (videoSurfaceGate)
        {
            if (videoHandle != expectedHandle)
            {
                return;
            }

            videoHandle = IntPtr.Zero;
            videoHandleVersion++;
            if (videoHandleReady.Task.IsCompleted)
            {
                videoHandleReady = CreateVideoHandleReadySource();
            }

            stateChanged = videoSurfaceStateChanged;
            videoSurfaceStateChanged = CreateVideoSurfaceStateChangedSource();
            shouldClearPlaybackEngine = true;
        }

        stateChanged.TrySetResult();
        if (shouldClearPlaybackEngine)
        {
            ResetVideoAspectRatioPollingBackoff();
            if (playbackEngine is { } engine)
            {
                engine.SetVideoHandle(GetOrCreateParkingVideoHandle().Handle);
            }
        }
    }

    public bool SetSelectedForAudio(bool selectedForAudio)
    {
        if (isSelectedForAudio == selectedForAudio)
        {
            return false;
        }

        isSelectedForAudio = selectedForAudio;
        OnPropertyChanged(nameof(IsAutoMuted));
        ApplyAudio();
        return true;
    }

    public void SetVideoPlacement(bool visible, int row, int column, int rowSpan, int columnSpan)
    {
        videoPlacementKnown = true;
        var videoVisibilityChanged = SetProperty(ref isVideoVisible, visible, nameof(IsVideoVisible));
        IsReplaySeekBarVisible = visible;
        VideoGridRow = Math.Max(0, row);
        VideoGridColumn = Math.Max(0, column);
        VideoGridRowSpan = Math.Max(1, rowSpan);
        VideoGridColumnSpan = Math.Max(1, columnSpan);
        if (videoVisibilityChanged)
        {
            SignalVideoSurfaceStateChanged();
        }
    }

    public void SetMainVideoSurfaceExpected(bool expected)
    {
        if (isMainVideoSurfaceExpected == expected)
        {
            return;
        }

        isMainVideoSurfaceExpected = expected;
        SignalVideoSurfaceStateChanged();
    }

    public bool SetDetached(bool detached)
    {
        var changed = SetProperty(ref isDetached, detached, nameof(IsDetached));
        if (changed)
        {
            SignalVideoSurfaceStateChanged();
        }

        return changed;
    }

    public void SetMergedTabGroupPlacement(bool member, bool first, bool last)
    {
        IsMergedTabGroupMember = member;
        IsFirstMergedTabGroupMember = member && first;
        IsLastMergedTabGroupMember = member && last;
    }

    public void ReapplyAudio()
    {
        ApplyAudio();
    }

    public bool TryGetVideoSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (playbackEngine?.TryGetVideoSize(out width, out height) != true)
        {
            return false;
        }

        UpdateVideoAspectRatio(width, height);
        return true;
    }

    public bool TryGetVideoCursor(out int x, out int y)
    {
        x = 0;
        y = 0;
        return playbackEngine?.TryGetVideoCursor(out x, out y) == true;
    }

    public void RefreshChatOverlay(ChatSettings settings)
    {
        if (disposed)
        {
            return;
        }

        chatSettings = settings;
        ConfigureSharedChatCatalogs(settings);
        UpdateNativeChatOverlay();

        if (currentSettings is not null && playbackEngine?.UsesNativeOverlay == true)
        {
            if (ShouldUseNativeOverlayController(currentSettings))
            {
                StartNativeOverlayChatInBackground(currentSettings, CancellationToken.None);
            }
            else
            {
                _ = StopNativeOverlayChatAsync(clearOverlay: true);
            }
        }
    }

    public async Task RestartChatAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return;
        }

        currentSettings = settings;
        chatSettings = settings.Chat;
        ConfigureSharedChatCatalogs(settings.Chat);
        UpdateNativeChatOverlay();

        if (!settings.Chat.ConnectAutomatically || !IsChatVisible)
        {
            await StopChatAsync(clearNativeOverlay: true);
            return;
        }

        var shouldUseNativeOverlayController = ShouldUseNativeOverlayController(settings);
        var shouldKeepCaptureChatClient = ShouldKeepChatClientForCapturedReplay(settings);
        if (shouldUseNativeOverlayController)
        {
            if (!IsNativeOverlayChatCurrent(settings))
            {
                await StopNativeOverlayChatAsync(clearOverlay: false);
                await StartNativeOverlayChatTrackedAsync(settings, cancellationToken);
            }

            if (shouldKeepCaptureChatClient)
            {
                await EnsureChatClientConnectedAsync(cancellationToken);
            }
            else
            {
                await StopChatClientAsync();
            }

            return;
        }

        await StopNativeOverlayChatAsync(clearOverlay: true);
        await StartChatAsync(cancellationToken);
    }

    public bool ShouldRestartPlaybackForChatOverlaySettings(AppSettings settings)
    {
        if (playbackEngine is null ||
            Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused))
        {
            return false;
        }

        var requestedNativeOverlay = ShouldRequestNativeOverlay(settings.Chat);
        if (playbackEngineNativeOverlayRequested != requestedNativeOverlay)
        {
            return true;
        }

        return requestedNativeOverlay &&
            !string.Equals(
                playbackEngineOverlayDirectory,
                ResolveVlcOverlayDirectory(settings.Chat) ?? "",
                StringComparison.OrdinalIgnoreCase);
    }

    public async Task ReconfigurePlaybackForChatOverlaySettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return;
        }

        if (!ShouldRestartPlaybackForChatOverlaySettings(settings))
        {
            await RestartChatAsync(settings, cancellationToken);
            return;
        }

        var restorePaused = Status == PlaybackStatus.Paused;
        var restorePausedByTabSwitch = PausedByTabSwitch;

        await StopChatAsync(clearNativeOverlay: true);
        await StartAsync(
            settings,
            optimizeForMultiStream: multiStreamResourceProfile,
            cancellationToken: cancellationToken);

        if (restorePaused && Status == PlaybackStatus.Playing && playbackEngine is not null)
        {
            await playbackTransitionGate.WaitAsync(cancellationToken);
            try
            {
                if (Status == PlaybackStatus.Playing && playbackEngine is not null)
                {
                    await playbackEngine.PauseAsync(cancellationToken);
                    Status = PlaybackStatus.Paused;
                    PausedByTabSwitch = restorePausedByTabSwitch;
                }
            }
            finally
            {
                playbackTransitionGate.Release();
            }
        }
    }

    public async Task StartAsync(
        AppSettings settings,
        bool preferStableLivePlayback = false,
        bool optimizeForMultiStream = false,
        CancellationToken cancellationToken = default)
    {
        await StartWithResultAsync(
            settings,
            preferStableLivePlayback,
            optimizeForMultiStream,
            cancellationToken);
    }

    /// <summary>
    /// Starts playback and reports whether the media reached the playing state
    /// before visibility policy or another lifecycle operation changed it.
    /// <see cref="StartAsync"/> remains the public compatibility wrapper used
    /// by existing callers that only need the historical task completion.
    /// </summary>
    internal async Task<PlaybackStartResult> StartWithResultAsync(
        AppSettings settings,
        bool preferStableLivePlayback = false,
        bool optimizeForMultiStream = false,
        CancellationToken cancellationToken = default)
    {
        if (disposed)
        {
            return PlaybackStartResult.NotStarted;
        }

        if (string.IsNullOrWhiteSpace(settings.StreamlinkPath))
        {
            throw new InvalidOperationException("Configure the Streamlink executable path in Settings.");
        }

        if (string.IsNullOrWhiteSpace(settings.VlcDirectory))
        {
            throw new InvalidOperationException("Configure the VLC directory in Settings.");
        }

        try
        {
            await lifecycleGate.WaitAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
            return PlaybackStartResult.NotStarted;
        }

        if (disposed)
        {
            lifecycleGate.Release();
            return PlaybackStartResult.NotStarted;
        }

        var playbackTransitionAcquired = false;
        try
        {
            await playbackTransitionGate.WaitAsync(lifetimeCancellation.Token);
            playbackTransitionAcquired = true;
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
            lifecycleGate.Release();
            return PlaybackStartResult.NotStarted;
        }

        // Commit the start's settings only after the lifecycle gate is held.  Disposal can
        // therefore not finish between the initial guard and these state mutations.
        currentSettings = settings;
        chatSettings = settings.Chat;
        multiStreamResourceProfile = optimizeForMultiStream;
        ConfigureSharedChatCatalogs(settings.Chat);

        IsBusy = true;
        ErrorMessage = "";
        using var activeStart = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
        RegisterActiveStartCancellation(activeStart);
        var startCancellationToken = activeStart.Token;
        CancellationTokenSource? streamStartCancellation = null;
        Task<IStreamTransportSession>? pendingStreamSession = null;
        var pendingStreamSessionNeedsCleanup = false;
        Uri? directPlaybackUri = null;
        TwitchSubOnlyVodResolution? subOnlyVodResolution = null;
        var playbackStarted = false;

        try
        {
            await StopViewerCountPollingAsync();
            await StopKickWebhookSubscriptionAsync();
            if (Target.IsExplicitVod)
            {
                SetViewerCountUnavailable("Viewer count polling is disabled for VOD playback.");
            }
            else
            {
                SetViewerCountPending("Loading viewer count...");
            }

            await StopChatAsync(clearNativeOverlay: true);
            await StopPlaybackOnlyAsync(PlaybackStopTimeout);
            CancelReplayAvailabilityRefresh();
            ResetReplayState("Replay availability has not been checked yet.");

            Status = PlaybackStatus.Resolving;
            var customArguments = CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments);
            switch (Target.Kind)
            {
                case StreamTargetKind.Live:
                    var effectiveLowLatency = settings.LowLatency && !preferStableLivePlayback;
                    if (settings.LowLatency && !effectiveLowLatency)
                    {
                        logger.Write(
                            AppLogLevel.Info,
                            "Playback",
                            $"Using stable multi-stream startup profile for {Target.DisplayName}; Streamlink low-latency flags are disabled for this start.");
                    }

                    var liveRequest = new StreamTransportRequest(
                        Target,
                        Quality,
                        settings.StreamlinkPath!,
                        effectiveLowLatency,
                        customArguments,
                        IsMultiStream: optimizeForMultiStream);
                    streamStartCancellation = CancellationTokenSource.CreateLinkedTokenSource(startCancellationToken);
                    pendingStreamSession = streamlinkService.StartExternalHttpAsync(liveRequest, streamStartCancellation.Token);
                    pendingStreamSessionNeedsCleanup = true;
                    break;
                case StreamTargetKind.TwitchVod:
                    var twitchVodRequest = new StreamTransportRequest(
                        Target,
                        Quality,
                        settings.StreamlinkPath!,
                        false,
                        customArguments);
                    try
                    {
                        var resolved = await streamlinkService.ResolveStreamUrlAsync(twitchVodRequest, startCancellationToken);
                        directPlaybackUri = resolved.StreamUri;
                    }
                    catch (Exception streamlinkError) when (streamlinkError is not OperationCanceledException &&
                        twitchSubOnlyVodResolver is not null)
                    {
                        logger.Write(
                            AppLogLevel.Info,
                            "Playback",
                            $"Streamlink could not resolve {Target.Url} ({streamlinkError.Message}); trying the sub-only VOD fallback.");
                        try
                        {
                            var bypass = await twitchSubOnlyVodResolver.ResolveAsync(
                                new TwitchSubOnlyVodRequest(ResolveTwitchVodId(), Quality),
                                startCancellationToken);
                            subOnlyVodResolution = bypass;
                            directPlaybackUri = bypass.PlaybackUri;
                            AddSystemMessage($"Playing sub-only VOD via direct playlist ({bypass.QualityKey}).");
                        }
                        catch (Exception bypassError) when (bypassError is not OperationCanceledException)
                        {
                            throw new InvalidOperationException(
                                $"Streamlink could not play the VOD: {streamlinkError.Message} Sub-only fallback also failed: {bypassError.Message}",
                                bypassError);
                        }
                    }

                    break;
                case StreamTargetKind.KickVod:
                    var kickVodRequest = new StreamTransportRequest(
                        Target,
                        Quality,
                        settings.StreamlinkPath!,
                        false,
                        customArguments);
                    var kickResolved = await streamlinkService.ResolveStreamUrlAsync(kickVodRequest, startCancellationToken);
                    directPlaybackUri = kickResolved.StreamUri;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported stream target kind: {Target.Kind}.");
            }

            var enableNativeOverlay = ShouldRequestNativeOverlay(settings.Chat);
            var nativeOverlayPositionStatePath = enableNativeOverlay
                ? BuildNativeOverlayPositionStatePath(Target)
                : null;
            playbackEngine = await playbackFactory.CreateAsync(
                settings.VlcDirectory,
                enableNativeOverlay,
                nativeOverlayPositionStatePath,
                startCancellationToken,
                settings.VideoRendererMode);
            playbackEngine.VideoOutputRebound += PlaybackEngineOnVideoOutputRebound;
            playbackEngine.AudioStateReapplied += PlaybackEngineOnAudioStateReapplied;
            playbackEngineNativeOverlayRequested = enableNativeOverlay;
            playbackEngineOverlayDirectory = playbackEngine.NativeOverlayDirectory ??
                ResolveVlcOverlayDirectory(settings.Chat) ??
                "";
            var nativeOverlayUnavailable = enableNativeOverlay && !playbackEngine.UsesNativeOverlay;
            SetOverlayUnavailableDockFallback(nativeOverlayUnavailable);
            if (nativeOverlayUnavailable)
            {
                AddSystemMessage("Native VLC chat overlay could not be loaded; showing docked chat instead.");
                logger.Write(
                    AppLogLevel.Warning,
                    "ChatOverlay",
                    "Native VLC chat overlay was requested, but the playback engine did not enable it. Falling back to docked chat.");
            }

            RaiseNativeOverlayProperties();

            var appliedVideoHandle = await WaitForVideoHandleAsync(startCancellationToken);
            playbackEngine.SetVideoHandle(appliedVideoHandle.Handle);
            var nativeOverlayControllerRequested = ShouldUseNativeOverlayController(settings);

            Uri playbackUri;
            if (Target.Kind == StreamTargetKind.Live)
            {
                IStreamTransportSession resolvedStreamSession;
                try
                {
                    resolvedStreamSession = await pendingStreamSession!;
                    pendingStreamSessionNeedsCleanup = false;
                }
                catch
                {
                    pendingStreamSessionNeedsCleanup = false;
                    throw;
                }

                streamSession = resolvedStreamSession;
                streamSession.LogLineReceived += StreamSessionOnLogLineReceived;
                playbackUri = streamSession.PlaybackUri;
                isDirectExplicitVodReplayPlayback = false;
            }
            else if (directPlaybackUri is not null)
            {
                playbackUri = directPlaybackUri;
            }
            else
            {
                throw new InvalidOperationException("Streamlink did not return a playback URL.");
            }

            var playbackVideoHandle = await WaitForVideoHandleAsync(startCancellationToken);
            if (playbackVideoHandle.Version != appliedVideoHandle.Version)
            {
                playbackEngine.SetVideoHandle(playbackVideoHandle.Handle);
            }

            Status = PlaybackStatus.Starting;
            await playbackEngine.PlayAsync(playbackUri, Volume, CurrentAudioState, startCancellationToken);
            isDirectExplicitVodReplayPlayback = Target.IsExplicitVod && streamSession is null;
            ApplyAudio();
            Status = PlaybackStatus.Playing;
            playbackStarted = true;
            EnsureOfficialKickChatSubscriptionInBackground(settings, startCancellationToken);
            if (Target.IsExplicitVod)
            {
                InitializeExplicitVodReplaySession(settings, subOnlyVodResolution);
            }

            UpdateNativeChatOverlay();
            if (!IsChatVisible && playbackEngine.UsesNativeOverlay)
            {
                _ = BlankNativeOverlayAsync(playbackEngine.NativeOverlayPipeName, CancellationToken.None);
            }

            if (!backgroundResourceServicesSuspended)
            {
                StartVideoAspectRatioPolling();
                if (!Target.IsExplicitVod)
                {
                    StartViewerCountPolling(settings);
                    if (Target.Platform == PlatformKind.Kick)
                    {
                        StartReplayAvailabilityRefreshInBackground(settings);
                    }
                    else
                    {
                        await RefreshReplayAvailabilityAsync(settings, startCancellationToken);
                    }
                }
            }

            // A hidden tab can be paused by the visibility policy while its Streamlink session is
            // still resolving. Reapply that policy once the playback engine exists.
            if (backgroundResourceServicesSuspended)
            {
                await PauseForTabSwitchCoreAsync();
            }

            if (!Target.IsExplicitVod && settings.Chat.ConnectAutomatically && IsChatVisible)
            {
                if (nativeOverlayControllerRequested)
                {
                    StartNativeOverlayChatInBackground(
                        settings,
                        startCancellationToken,
                        startCaptureChatClient: ShouldKeepChatClientForCapturedReplay(settings));
                    return new PlaybackStartResult(playbackStarted, Status);
                }

                _ = StartChatAsync(startCancellationToken);
            }
        }
        catch (OperationCanceledException) when (startCancellationToken.IsCancellationRequested)
        {
            if (pendingStreamSessionNeedsCleanup && pendingStreamSession is not null)
            {
                streamStartCancellation?.Cancel();
                await DisposeUnclaimedStreamSessionAsync(pendingStreamSession);
            }

            await StopPlaybackOnlyAsync(PlaybackStopTimeout);
            Status = PlaybackStatus.Stopped;
            SetViewerCountPending("Viewer count is stopped.");
            ResetReplayState("Replay is stopped.");
            playbackStarted = false;
        }
        catch (Exception ex)
        {
            if (pendingStreamSessionNeedsCleanup && pendingStreamSession is not null)
            {
                streamStartCancellation?.Cancel();
                await DisposeUnclaimedStreamSessionAsync(pendingStreamSession);
            }

            Status = ex.Message.Contains("No streams found", StringComparison.OrdinalIgnoreCase) ? PlaybackStatus.Offline : PlaybackStatus.Error;
            ErrorMessage = ex.Message;
            AddSystemMessage(ex.Message);
            SetViewerCountUnavailable("Viewer count unavailable because playback did not start.");
            ResetReplayState("Replay unavailable because playback did not start.");
            logger.Write(AppLogLevel.Error, "Playback", $"Failed to start {Target.DisplayName}", ex);
            await StopPlaybackOnlyAsync(PlaybackStopTimeout);
            playbackStarted = false;
        }
        finally
        {
            ClearActiveStartCancellation(activeStart);
            streamStartCancellation?.Dispose();
            IsBusy = false;
            if (playbackTransitionAcquired)
            {
                playbackTransitionGate.Release();
            }

            lifecycleGate.Release();
        }

        return new PlaybackStartResult(playbackStarted, Status);
    }

    private string ResolveTwitchVodId()
    {
        if (!string.IsNullOrWhiteSpace(Target.MediaId))
        {
            return Target.MediaId.Trim();
        }

        if (Uri.TryCreate(Target.Url, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length > 0)
            {
                return segments[^1];
            }
        }

        return "";
    }

    private void EnsureOfficialKickChatSubscriptionInBackground(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (Target.Platform != PlatformKind.Kick ||
            !settings.Chat.KickWebhookListenerEnabled ||
            kickEventSubscriptionService is null ||
            disposed)
        {
            return;
        }

        var target = Target;
        lock (kickWebhookSubscriptionGate)
        {
            CancelCancellationSource(kickWebhookSubscriptionCancellation);
            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellation.Token,
                cancellationToken);
            var operationReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var task = EnsureOfficialKickChatSubscriptionWhenReadyAsync(
                target,
                settings,
                operationCancellation,
                operationReady.Task);
            kickWebhookSubscriptionCancellation = operationCancellation;
            kickWebhookSubscriptionTask = task;
            operationReady.TrySetResult();
        }
    }

    private async Task EnsureOfficialKickChatSubscriptionWhenReadyAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationTokenSource operationCancellation,
        Task operationReady)
    {
        await operationReady.ConfigureAwait(false);
        await EnsureOfficialKickChatSubscriptionAsync(target, settings, operationCancellation)
            .ConfigureAwait(false);
    }

    private async Task EnsureOfficialKickChatSubscriptionAsync(
        StreamTarget target,
        AppSettings settings,
        CancellationTokenSource operationCancellation)
    {
        try
        {
            var result = await kickEventSubscriptionService!
                .EnsureChatMessageSentSubscriptionAsync(target, settings.Chat, operationCancellation.Token)
                .ConfigureAwait(false);
            if (operationCancellation.IsCancellationRequested || disposed)
            {
                return;
            }

            var level = result.IsSuccess || result.Status == KickEventSubscriptionEnsureStatus.NotNeeded
                ? AppLogLevel.Info
                : AppLogLevel.Warning;
            logger.Write(level, "KickWebhook", result.Message);
            if (!result.IsSuccess &&
                result.Status != KickEventSubscriptionEnsureStatus.NotNeeded)
            {
                AddSystemMessage(result.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (operationCancellation.IsCancellationRequested || disposed)
            {
                return;
            }

            var message = $"Official Kick chat webhook subscription failed for {target.Channel}: {ex.Message}";
            logger.Write(AppLogLevel.Warning, "KickWebhook", message, ex);
            AddSystemMessage(message);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested || disposed)
        {
        }
        finally
        {
            lock (kickWebhookSubscriptionGate)
            {
                if (ReferenceEquals(kickWebhookSubscriptionCancellation, operationCancellation))
                {
                    kickWebhookSubscriptionCancellation = null;
                    kickWebhookSubscriptionTask = null;
                }
            }

            operationCancellation.Dispose();
        }
    }

    private async Task StopKickWebhookSubscriptionAsync()
    {
        Task? subscriptionTask;
        CancellationTokenSource? subscriptionCancellation;
        lock (kickWebhookSubscriptionGate)
        {
            subscriptionTask = kickWebhookSubscriptionTask;
            subscriptionCancellation = kickWebhookSubscriptionCancellation;
        }

        CancelCancellationSource(subscriptionCancellation);
        if (subscriptionTask is null)
        {
            return;
        }

        try
        {
            await subscriptionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "KickWebhook", $"Kick webhook subscription cleanup failed for {Target.DisplayName}.", ex);
        }
    }

    public async Task PauseOrResumeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await playbackTransitionGate.WaitAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await PauseOrResumeCoreAsync();
        }
        finally
        {
            playbackTransitionGate.Release();
        }
    }

    public bool PausedByTabSwitch { get; private set; }

    public async Task PauseForTabSwitchAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await playbackTransitionGate.WaitAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await PauseForTabSwitchCoreAsync();
        }
        finally
        {
            playbackTransitionGate.Release();
        }
    }

    public async Task ResumeFromTabSwitchAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await playbackTransitionGate.WaitAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await ResumeFromTabSwitchCoreAsync();
        }
        finally
        {
            playbackTransitionGate.Release();
        }
    }

    private async Task PauseOrResumeCoreAsync()
    {
        if (disposed || playbackEngine is null)
        {
            return;
        }

        if (Status == PlaybackStatus.Paused)
        {
            if (livePlaybackConnectionSuspended)
            {
                await ResumeLivePlaybackConnectionAsync(lifetimeCancellation.Token);
            }
            else
            {
                await ResumeWithHoldAsync(lifetimeCancellation.Token);
            }

            ApplyAudio();
        }
        else if (Status == PlaybackStatus.Playing)
        {
            // A manual pause is deliberately position-preserving, including for a live tab.
            // Automatic inactive-tab suspension uses the separate connection-stopping path below.
            livePlaybackConnectionSuspended = false;
            CapturePauseHold(allowLiveTransition: true);
            await playbackEngine.PauseAsync(lifetimeCancellation.Token);
            Status = PlaybackStatus.Paused;
            PausedByTabSwitch = false;
        }
    }

    private async Task PauseForTabSwitchCoreAsync()
    {
        IsBackgroundResourceServicesSuspended = true;
        var engine = playbackEngine;
        if (engine is not null && Status == PlaybackStatus.Playing && !PausedByTabSwitch)
        {
            if (CanSuspendLivePlaybackConnection())
            {
                // Do not capture a replay hold here.  The Streamlink process and its URI remain
                // valid, while stopping only libVLC closes the local HTTP reader and prevents the
                // external-HTTP ring buffer from advancing while this tab is hidden.
                pendingResumeHoldPosition = null;
                pendingResumeHoldAllowsLiveTransition = false;
                await engine.StopAsync(lifetimeCancellation.Token);
                livePlaybackConnectionSuspended = true;
            }
            else
            {
                CapturePauseHold(allowLiveTransition: false);
                await engine.PauseAsync(lifetimeCancellation.Token);
            }

            Status = PlaybackStatus.Paused;
            PausedByTabSwitch = true;
        }

        await StopBackgroundResourceServicesAsync();
    }

    private async Task ResumeFromTabSwitchCoreAsync()
    {
        var wasSuspended = IsBackgroundResourceServicesSuspended;
        IsBackgroundResourceServicesSuspended = false;
        if (!wasSuspended && !PausedByTabSwitch)
        {
            return;
        }

        var reconnectingLivePlayback = livePlaybackConnectionSuspended;
        if (playbackEngine is not null && Status == PlaybackStatus.Paused && PausedByTabSwitch)
        {
            try
            {
                if (reconnectingLivePlayback)
                {
                    IsBusy = true;
                    await ResumeLivePlaybackConnectionAsync(lifetimeCancellation.Token);
                }
                else
                {
                    await ResumeWithHoldAsync(lifetimeCancellation.Token);
                }

                if (reconnectingLivePlayback)
                {
                    // PlayAsync applies the requested audio state while creating the fresh
                    // player. Notify the main view model so a selected tab can reassert its
                    // shared audible state without issuing a redundant engine call here.
                    AudioStateApplied?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    ApplyAudio();
                }
            }
            catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (reconnectingLivePlayback)
            {
                SetAutomaticLiveResumeError(ex);
                return;
            }
            finally
            {
                if (reconnectingLivePlayback)
                {
                    IsBusy = false;
                }
            }
        }

        await ResumeBackgroundResourceServicesAsync(
            suppressReplayPlaybackUrlResolution: reconnectingLivePlayback);
    }

    private async Task StopBackgroundResourceServicesAsync()
    {
        CancelReplayAvailabilityPolling();
        await StopLiveDvrPromotionPollingAsync();
        await StopReplayClockPollingAsync();
        await StopViewerCountPollingAsync();
        await StopVideoAspectRatioPollingAsync();
    }

    private async Task ResumeBackgroundResourceServicesAsync(
        bool suppressReplayPlaybackUrlResolution = false)
    {
        if (IsBackgroundResourceServicesSuspended ||
            playbackEngine is null ||
            Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused) ||
            currentSettings is not { } settings)
        {
            return;
        }

        StartVideoAspectRatioPolling();
        if (Target.IsExplicitVod)
        {
            StartReplayClockPolling();
            return;
        }

        StartViewerCountPolling(settings);
        if (Target.Platform == PlatformKind.Kick)
        {
            StartReplayAvailabilityRefreshInBackground(
                settings,
                prefetchPlaybackUrl: !suppressReplayPlaybackUrlResolution);
        }
        else
        {
            await RefreshReplayAvailabilityCoreAsync(
                settings,
                CancellationToken.None,
                refreshVersion: 0,
                prefetchPlaybackUrl: !suppressReplayPlaybackUrlResolution);
        }

        if (replaySession is { IsAvailable: true })
        {
            StartReplayClockPolling();
        }
    }

    private bool CanSuspendLivePlaybackConnection()
    {
        return Target.Kind == StreamTargetKind.Live &&
            streamSession is not null &&
            !IsReplayMode &&
            !IsBehindLive;
    }

    private async Task ResumeLivePlaybackConnectionAsync(CancellationToken cancellationToken)
    {
        var engine = playbackEngine ?? throw new InvalidOperationException("Playback is no longer available.");
        var session = streamSession ?? throw new InvalidOperationException("The Streamlink session is no longer available.");

        Status = PlaybackStatus.Starting;
        await engine.PlayAsync(session.PlaybackUri, Volume, CurrentAudioState, cancellationToken);
        isDirectExplicitVodReplayPlayback = false;
        IsReplayMode = false;
        IsBehindLive = false;
        pendingResumeHoldPosition = null;
        pendingResumeHoldAllowsLiveTransition = false;
        livePlaybackConnectionSuspended = false;
        PausedByTabSwitch = false;
        ErrorMessage = "";
        Status = PlaybackStatus.Playing;
    }

    private void SetAutomaticLiveResumeError(Exception exception)
    {
        livePlaybackConnectionSuspended = false;
        PausedByTabSwitch = false;
        Status = PlaybackStatus.Error;
        ErrorMessage = $"Automatic live resume failed: {exception.Message}";
        AddSystemMessage(ErrorMessage);
        logger.Write(
            AppLogLevel.Error,
            "Playback",
            $"Failed to reconnect hidden live playback for {Target.DisplayName}.",
            exception);
    }

    // Records the offset to restore on the next manual resume so playback holds position instead of
    // snapping to the live edge (libVLC repositions a live HLS stream to live on resume).
    private void CapturePauseHold(bool allowLiveTransition)
    {
        pendingResumeHoldPosition = null;
        pendingResumeHoldAllowsLiveTransition = allowLiveTransition;

        // Explicit VODs do not drift on resume, and without an available replay/DVR source there is
        // no seekable timeline to hold against.
        if (Target.IsExplicitVod ||
            replaySession is not { IsAvailable: true } replay)
        {
            return;
        }

        // Behind live this is the real playback offset; at the live edge it is the live-edge offset
        // (which grows with wall-clock), so after a real pause it lands us behind live by the pause length.
        pendingResumeHoldPosition = ResolveReplayClock(
            replay,
            Volatile.Read(ref replaySeekOperationVersion),
            sampleBeganDuringSeek: IsReplaySeekInProgress).Position;

        // Warm the replay/DVR playback URL while the frame is frozen so the unavoidable live -> replay
        // reload on resume has no URL-resolution latency. Reuses any valid in-flight/successful resolution.
        if (currentSettings is { } settings)
        {
            QueueReplayPlaybackUrlResolution(replay, settings);
        }
    }

    private async Task ResumeWithHoldAsync(CancellationToken cancellationToken = default)
    {
        var holdPosition = pendingResumeHoldPosition;
        var allowLiveTransition = pendingResumeHoldAllowsLiveTransition;
        pendingResumeHoldPosition = null;
        pendingResumeHoldAllowsLiveTransition = false;

        if (playbackEngine is null)
        {
            return;
        }

        await playbackEngine.ResumeAsync(cancellationToken);
        Status = PlaybackStatus.Playing;
        PausedByTabSwitch = false;

        // Hold the paused position. ResumeAsync above snaps a live / current-live-DVR HLS stream to the
        // live edge, and an in-place backward seek right after resume is unreliable: libVLC rejects it as
        // not-seekable or silently overrides it back to live (no exception, so the in-place fallback never
        // fires). Reload the replay/DVR media and seek to the held offset -- the same reliable path the
        // initial rewind uses. The playback URL was warmed at pause time (CapturePauseHold) so the reload
        // resolves with no extra latency.
        if (holdPosition is not { } position ||
            Target.IsExplicitVod ||
            replaySession is not { IsAvailable: true } replay)
        {
            return;
        }

        // Only skip the hold when the paused spot is still effectively at the live edge (a near-instant
        // pause) -- resuming there is just live and a reload would be pointless. Any real pause holds the
        // exact paused timestamp below via reload+seek (holdExactPosition bypasses the 15s snap-to-live).
        var duration = GetCurrentReplayDuration(replay);
        var targetOffset = ClampReplayOffset(position, duration);
        if (duration - targetOffset <= ResumeHoldLiveEdgeTolerance)
        {
            return;
        }

        if (IsReplayMode || (allowLiveTransition && CanSeekReplay))
        {
            await SeekReplaySerializedAsync(
                position,
                cancellationToken,
                forceReload: true,
                holdExactPosition: true,
                playbackTransitionAlreadyHeld: true);
        }
    }

    public async Task StopAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await lifecycleGate.WaitAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (disposed)
        {
            lifecycleGate.Release();
            return;
        }

        CancelActiveStart();
        CancelReplayAvailabilityRefresh();
        try
        {
            await StopAsync(PlaybackStopTimeout);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task StopAsync(TimeSpan? playbackStopTimeout)
    {
        await playbackTransitionGate.WaitAsync();
        try
        {
            IsBackgroundResourceServicesSuspended = false;
            await StopKickWebhookSubscriptionAsync();
            await StopViewerCountPollingAsync();
            CancelReplayAvailabilityRefresh();
            await StopLiveDvrPromotionPollingAsync();
            await StopPlaybackOnlyAsync(playbackStopTimeout);
            await StopChatAsync();
            Status = PlaybackStatus.Stopped;
            SetViewerCountPending("Viewer count is stopped.");
            ResetReplayState("Replay is stopped.");
        }
        finally
        {
            playbackTransitionGate.Release();
        }
    }

    public void BeginReplaySeekPreview()
    {
        BeginReplaySeekPreview(ReplaySeekSliderValue);
    }

    public void BeginReplaySeekPreview(double sliderOffsetSeconds)
    {
        if (!CanSeekReplay)
        {
            return;
        }

        IsReplaySeekPreviewActive = true;
        ReplaySeekSliderValue = sliderOffsetSeconds;
        ReplayElapsedText = StreamViewModelHelpers.FormatClockTime(TimeSpan.FromSeconds(ReplaySeekSliderValue));
    }

    public void PreviewReplaySeek(double sliderOffsetSeconds)
    {
        if (!isReplaySeekPreviewActive)
        {
            return;
        }

        ReplaySeekSliderValue = sliderOffsetSeconds;
    }

    public Task CommitReplaySeekPreviewAsync(double sliderOffsetSeconds, CancellationToken cancellationToken = default)
    {
        ReplaySeekSliderValue = sliderOffsetSeconds;
        IsReplaySeekPreviewActive = false;
        return SeekReplayAsync(TimeSpan.FromSeconds(ReplaySeekSliderValue), cancellationToken);
    }

    public void CancelReplaySeekPreview()
    {
        IsReplaySeekPreviewActive = false;
        ReplaySeekSliderValue = ReplaySeekValue;
        ReplayElapsedText = StreamViewModelHelpers.FormatClockTime(TimeSpan.FromSeconds(ReplaySeekValue));
    }

    public Task RewindReplay30SecondsAsync()
    {
        return SeekReplayByAsync(-ReplaySeekStep);
    }

    public Task FastForwardReplay30SecondsAsync()
    {
        return SeekReplayByAsync(ReplaySeekStep);
    }

    private Task SeekReplayByAsync(TimeSpan delta)
    {
        if (!CanSeekReplay)
        {
            AddSystemMessage(ReplaySeekToolTip);
            return Task.CompletedTask;
        }

        return SeekReplayAsync(GetCurrentReplayStepOffset() + delta);
    }

    private TimeSpan GetCurrentReplayStepOffset()
    {
        if (IsReplayMode &&
            replaySession is { IsAvailable: true } replay)
        {
            return ResolveReplayClock(
                replay,
                Volatile.Read(ref replaySeekOperationVersion),
                sampleBeganDuringSeek: IsReplaySeekInProgress).Position;
        }

        return TimeSpan.FromSeconds(ReplaySeekValue);
    }

    private long BeginReplaySeekOperation()
    {
        // Publish the in-progress state before advancing the generation. The replay clock poller
        // reads both values on a background thread; this ordering prevents it from treating the
        // narrow gap between those writes as a stable, post-seek clock sample.
        IsReplaySeekInProgress = true;
        var operationVersion = Interlocked.Increment(ref replaySeekOperationVersion);
        pausedReplayClock = null;
        ResetReplayClockSampleTracking();
        ResetNativeReplayOverlayScrollState();
        SuspendNativeReplayOverlayResizePersistence();
        return operationVersion;
    }

    private bool IsLatestReplaySeekOperation(long operationVersion)
    {
        return operationVersion == Volatile.Read(ref replaySeekOperationVersion);
    }

    private void CancelReplaySeekOperation()
    {
        Interlocked.Increment(ref replaySeekOperationVersion);
        pausedReplayClock = null;
        IsReplaySeekInProgress = false;
    }

    public Task SeekReplayAsync(
        TimeSpan offset,
        CancellationToken cancellationToken = default,
        bool forceReload = false,
        bool holdExactPosition = false) =>
        SeekReplaySerializedAsync(
            offset,
            cancellationToken,
            forceReload,
            holdExactPosition,
            playbackTransitionAlreadyHeld: false);

    private async Task SeekReplaySerializedAsync(
        TimeSpan offset,
        CancellationToken cancellationToken,
        bool forceReload,
        bool holdExactPosition,
        bool playbackTransitionAlreadyHeld)
    {
        if (disposed)
        {
            return;
        }

        // A seek can be started directly by a WPF mouse/key event.  Several of the setup steps below
        // can complete synchronously (especially when the replay URL is already prefetched), which
        // would keep the routed input event on the dispatcher while the replay transition starts.
        // Yield before touching the seek state so the slider release is returned to WPF immediately.
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Task.Yield();
        if (disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (replaySession is not { IsAvailable: true } replay)
        {
            try
            {
                await WaitForReplayAvailabilityRefreshOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (replaySession is not { IsAvailable: true } refreshedReplay)
            {
                AddSystemMessage(ReplaySeekToolTip);
                return;
            }

            replay = refreshedReplay;
        }

        var duration = GetCurrentReplayDuration(replay);
        var targetOffset = ClampReplayOffset(offset, duration);

        if (playbackEngine is null || currentSettings is null)
        {
            AddSystemMessage("Replay seeking is unavailable because playback has not started.");
            return;
        }

        var settings = currentSettings;
        if (!CanSeekCurrentReplayInPlace(replay) &&
            !IsDirectReplayPlaybackUrl(replay) &&
            string.IsNullOrWhiteSpace(settings.StreamlinkPath))
        {
            AddSystemMessage("Replay seeking needs the Streamlink executable path.");
            return;
        }

        if (!holdExactPosition && !Target.IsExplicitVod && duration - targetOffset <= ReplayLiveEdgeThreshold)
        {
            // Return-to-live starts a fresh Streamlink transport and therefore must run before this
            // seek acquires the player transition gate.
            await ReturnToLiveAsync(cancellationToken);
            return;
        }

        var playbackTransitionAcquired = false;
        if (!playbackTransitionAlreadyHeld)
        {
            try
            {
                await playbackTransitionGate.WaitAsync(cancellationToken);
                playbackTransitionAcquired = true;
            }
            catch (OperationCanceledException) when (
                disposed ||
                lifetimeCancellation.IsCancellationRequested ||
                cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        try
        {
            var seekOperationVersion = BeginReplaySeekOperation();
            var replayChatVersion = GetReplayChatStateVersion();
            var shouldLoadReplayChat = false;
            var targetReplayWindowHasMessages = false;
            IsBusy = true;
            try
            {
                await replayPlaybackTransitionGate.WaitAsync(cancellationToken);
                try
                {
                    if (!IsCurrentReplaySession(replay) ||
                        playbackEngine is null)
                    {
                        return;
                    }

                    duration = GetCurrentReplayDuration(replay);
                    targetOffset = ClampReplayOffset(offset, duration);
                    // holdExactPosition (resume-from-pause) keeps the exact paused timestamp instead of
                    // snapping to live when the spot is within the live-edge window.
                    if (!holdExactPosition && !Target.IsExplicitVod && duration - targetOffset <= ReplayLiveEdgeThreshold)
                    {
                        // The live-edge case was handled before taking playbackTransitionGate. If the
                        // replay clock moved to the edge while waiting, leave the current media alone;
                        // the next explicit Return to Live action will perform the full transport reset.
                        return;
                    }

                    CancelActiveStart();
                    replayChatVersion = ClearReplayChat();
                    BeginKickSeekbackReplayChatBacklog(replay, targetOffset);
                    if (ShouldUseCapturedReplayChat(replay))
                    {
                        RefreshCapturedReplayChat(
                            targetOffset,
                            force: true,
                            suppressUnavailableNotice: true);
                        targetReplayWindowHasMessages = HasCapturedReplayChatWindowMessages(targetOffset);
                    }

                    var seekedInPlace = false;
                    if (!forceReload && CanSeekCurrentReplayInPlace(replay))
                    {
                        try
                        {
                            Status = PlaybackStatus.Starting;
                            await playbackEngine.SeekAsync(targetOffset, cancellationToken);
                            SetReplayClockAnchor(
                                targetOffset,
                                duration,
                                seekOperationVersion,
                                awaitingSeekConfirmation: true);

                            IsReplayMode = true;
                            IsBehindLive = !Target.IsExplicitVod;
                            Status = PlaybackStatus.Playing;
                            ApplyReplayClock(targetOffset, duration, isSeekable: true);
                            StartReplayClockPolling();
                            seekedInPlace = true;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException &&
                            !Target.IsExplicitVod &&
                            CanResolveReplayPlaybackUrl(replay, settings))
                        {
                            currentReplayPlaybackKey = null;
                            CancelReplayPlaybackUrlResolution();
                            logger.Write(
                                AppLogLevel.Info,
                                "Replay",
                                $"In-place replay seek failed for {Target.DisplayName}; reloading replay media.",
                                ex);
                        }
                    }

                    if (!seekedInPlace)
                    {
                        var replayPlaybackKey = CreateReplayPlaybackUrlKey(replay, settings);
                        var urlWaitStopwatch = Stopwatch.StartNew();
                        var resolved = await ResolveReplayPlaybackUrlForSeekAsync(replayPlaybackKey, cancellationToken);
                        urlWaitStopwatch.Stop();
                        LogReplayFirstSeekStage("URL wait", urlWaitStopwatch.Elapsed);
                        var replayTransitionWork = PrepareReplayTransitionWork(replay, settings);
                        var prePlaybackTransitionWork = replayTransitionWork
                            .Where(work => work.RunBeforePlayback)
                            .ToArray();
                        var deferredTransitionWork = replayTransitionWork
                            .Where(work => !work.RunBeforePlayback)
                            .ToArray();

                        // The live native overlay controller and the replay renderer share VLC's
                        // single frame pipe. Stop that controller before PlayAsync makes the replay
                        // eligible for overlay rendering; otherwise the first empty replay frame can
                        // occupy the writer while the controller still owns the pipe, starving the
                        // loaded replay-chat frame behind it.
                        var prePlaybackCleanupStopwatch = Stopwatch.StartNew();
                        await RunReplayTransitionWorkAsync(prePlaybackTransitionWork);
                        prePlaybackCleanupStopwatch.Stop();
                        LogReplayFirstSeekStage(
                            $"pre-playback transition cleanup ({prePlaybackTransitionWork.Length} items)",
                            prePlaybackCleanupStopwatch.Elapsed);

                        try
                        {
                            Status = PlaybackStatus.Starting;
                            var playStopwatch = Stopwatch.StartNew();
                            await playbackEngine.PlayAsync(resolved.StreamUri, Volume, CurrentAudioState, cancellationToken);
                            playStopwatch.Stop();
                            LogReplayFirstSeekStage("PlayAsync", playStopwatch.Elapsed);
                            await ClearNativeReplayOverlayForReplayTransitionAsync(
                                replay,
                                targetReplayWindowHasMessages,
                                cancellationToken);
                            isDirectExplicitVodReplayPlayback = Target.IsExplicitVod;
                            var seekStopwatch = Stopwatch.StartNew();
                            await playbackEngine.SeekAsync(targetOffset, cancellationToken);
                            seekStopwatch.Stop();
                            LogReplayFirstSeekStage("SeekAsync", seekStopwatch.Elapsed);
                            currentReplayPlaybackKey = replayPlaybackKey;
                            SetReplayClockAnchor(
                                targetOffset,
                                duration,
                                seekOperationVersion,
                                awaitingSeekConfirmation: true);
                            ApplyAudio();

                            IsReplayMode = true;
                            IsBehindLive = !Target.IsExplicitVod;
                            Status = PlaybackStatus.Playing;
                            ApplyReplayClock(targetOffset, duration, isSeekable: true);
                            StartReplayClockPolling();
                        }
                        catch
                        {
                            await RunReplayTransitionWorkAsync(
                                deferredTransitionWork.Where(work => work.RunOnPlaybackFailure).ToArray());
                            throw;
                        }

                        var cleanupScheduleStopwatch = Stopwatch.StartNew();
                        RunReplayTransitionWorkInBackground(deferredTransitionWork);
                        cleanupScheduleStopwatch.Stop();
                        LogReplayFirstSeekStage(
                            $"transition cleanup scheduling ({deferredTransitionWork.Length} items)",
                            cleanupScheduleStopwatch.Elapsed);
                    }

                    if (ChatMessages.Count > 0)
                    {
                        InvalidateNativeReplayOverlayFrame();
                    }
                    else
                    {
                        QueueNativeChatOverlayUpdateAfterReplayWindowApply();
                    }

                    shouldLoadReplayChat = CanLoadReplayChat(replay);
                }
                finally
                {
                    replayPlaybackTransitionGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Replay seek failed: {ex.Message}");
                logger.Write(AppLogLevel.Warning, "Replay", $"Replay seek failed for {Target.DisplayName}.", ex);
            }
            finally
            {
                if (IsLatestReplaySeekOperation(seekOperationVersion))
                {
                    IsBusy = false;
                    IsReplaySeekInProgress = false;
                    FlushNativeReplayOverlayRefreshAfterSeek();
                }
            }

            if (shouldLoadReplayChat && IsLatestReplaySeekOperation(seekOperationVersion))
            {
                var replayChatQueueStopwatch = Stopwatch.StartNew();
                QueueReplayChatLoad(
                    replay,
                    settings,
                    targetOffset,
                    notifyUnavailable: true,
                    replayChatVersion);
                replayChatQueueStopwatch.Stop();
                LogReplayFirstSeekStage("replay chat queueing", replayChatQueueStopwatch.Elapsed);
            }
        }
        finally
        {
            if (playbackTransitionAcquired)
            {
                playbackTransitionGate.Release();
            }
        }
    }

    public Task ReturnToLiveAsync()
    {
        return ReturnToLiveAsync(CancellationToken.None);
    }

    private async Task ReturnToLiveAsync(CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return;
        }

        if (Target.IsExplicitVod)
        {
            AddSystemMessage("Return to live is not available for VOD playback.");
            return;
        }

        if (currentSettings is null)
        {
            return;
        }

        ResetKickSeekbackReplayChatBacklog();
        if (!IsReplayMode && !IsBehindLive)
        {
            IsBehindLive = false;
            ReplayLiveStateText = "Live";
            return;
        }

        await StartAsync(
            currentSettings,
            optimizeForMultiStream: multiStreamResourceProfile,
            cancellationToken: cancellationToken);
    }

    public async Task SendChatMessageAsync()
    {
        if (disposed)
        {
            return;
        }

        var message = NormalizeOutgoingMessage(OutgoingChatText);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (Target.IsExplicitVod)
        {
            AddSystemMessage("Chat sending is disabled for VOD playback.");
            return;
        }

        if (IsBehindLive)
        {
            AddSystemMessage("Chat sending is disabled while replay is behind live.");
            return;
        }

        var client = chatClient;
        if (client is null)
        {
            // Playback starts chat in the background so a slow platform connection cannot delay
            // video.  A send requested during that short hand-off should wait for the already
            // scheduled connection instead of reporting a false "not connected" error.
            var pendingConnection = GetChatConnectionTask();
            if (pendingConnection is not null)
            {
                try
                {
                    await pendingConnection;
                }
                catch (OperationCanceledException) when (disposed)
                {
                    return;
                }

                client = chatClient;
            }
        }

        if (client is null)
        {
            AddSystemMessage("Chat is not connected yet.");
            return;
        }

        var rememberDockedLocalEcho = IsDockedChatModeActive;
        var localEcho = rememberDockedLocalEcho ? CreateLocalEchoMessage(message) : null;
        if (rememberDockedLocalEcho)
        {
            RememberDockedLocalEcho(localEcho!);
        }

        try
        {
            await client.SendMessageAsync(message);
            OutgoingChatText = "";
            localEcho ??= CreateLocalEchoMessage(message);
            AddChatMessage(localEcho, isRememberedDockedLocalEcho: rememberDockedLocalEcho);
        }
        catch (Exception ex)
        {
            if (rememberDockedLocalEcho && localEcho is not null)
            {
                ForgetDockedLocalEcho(localEcho);
            }

            if (await TryReconnectChatForSendAsync(message, ex))
            {
                return;
            }

            AddSystemMessage($"Chat send failed: {ex.Message}");
            logger.Write(AppLogLevel.Warning, "Chat", $"Failed to send chat message for {Target.DisplayName}", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposalTask is null)
            {
                disposed = true;
                disposalTask = DisposeCoreAsync();
            }

            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        lifetimeCancellation.Cancel();
        AnimatedEmoteImage.ImageCacheEntryCompleted -= OnAnimatedEmoteImageCacheEntryCompleted;
        DockedChatBadgeCatalog.Shared.CatalogChanged -= OnChatRenderCatalogChanged;
        DockedChatEmoteCatalog.Shared.CatalogChanged -= OnChatRenderCatalogChanged;
        CancelNativeReplayOverlayAnimationState();
        CancelNativeReplayOverlayWarmupRefresh();
        StopTwitchPredictionClock();
        CancelActiveStart();
        CancelReplayAvailabilityRefresh();

        var lifecycleAcquired = false;
        try
        {
            await lifecycleGate.WaitAsync();
            lifecycleAcquired = true;
            await StopAsync(PlaybackStopTimeout);
            await WaitForReplayAvailabilityRefreshOnceAsync(CancellationToken.None);

            Task? replayChatTask;
            lock (replayChatLoadGate)
            {
                replayChatTask = replayChatLoadTask;
            }

            if (replayChatTask is not null)
            {
                try
                {
                    await replayChatTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            await nativeReplayOverlayEventHost.DisposeAsync();
            Task<NativeReplayOverlayFrameScheduler>? schedulerCreationTask;
            NativeReplayOverlayFrameScheduler? scheduler;
            lock (nativeReplayOverlayFrameSchedulerGate)
            {
                schedulerCreationTask = nativeReplayOverlayFrameSchedulerCreationTask;
                nativeReplayOverlayFrameSchedulerCreationTask = null;
                scheduler = nativeReplayOverlayFrameScheduler;
                nativeReplayOverlayFrameScheduler = null;
            }

            if (schedulerCreationTask is not null)
            {
                try
                {
                    scheduler ??= await schedulerCreationTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
                {
                }
                catch (TimeoutException)
                {
                }
                catch (Exception ex)
                {
                    logger.Write(AppLogLevel.Debug, "ChatOverlay", "Native replay overlay renderer startup cleanup failed.", ex);
                }
            }

            if (scheduler is not null)
            {
                await scheduler.DisposeAsync();
            }

            parkingVideoSurface?.Dispose();
            parkingVideoSurface = null;
        }
        finally
        {
            if (lifecycleAcquired)
            {
                lifecycleGate.Release();
            }

            await nativeReplayOverlayFrameWriteGate.DisposeAsync();
            lifetimeCancellation.Dispose();
        }
    }

    private void OnChatRenderCatalogChanged(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref nativeReplayOverlayRenderContentVersion);
        dispatch(InvalidateNativeReplayOverlayFrame);
    }

    private void InitializeTwitchPredictionOutcomeInputs()
    {
        TwitchPredictionOutcomeInputs.Add(CreateTwitchPredictionOutcomeInput("Yes"));
        TwitchPredictionOutcomeInputs.Add(CreateTwitchPredictionOutcomeInput("No"));
        RefreshTwitchPredictionOutcomeInputState();
    }

    private TwitchPredictionOutcomeInputViewModel CreateTwitchPredictionOutcomeInput(string title)
    {
        var input = new TwitchPredictionOutcomeInputViewModel(title, RemoveTwitchPredictionOutcome);
        input.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TwitchPredictionOutcomeInputViewModel.Title))
            {
                StartTwitchPredictionCommand.RaiseCanExecuteChanged();
            }
        };
        return input;
    }

    private void AddTwitchPredictionOutcome()
    {
        if (!CanAddTwitchPredictionOutcome)
        {
            return;
        }

        TwitchPredictionOutcomeInputs.Add(CreateTwitchPredictionOutcomeInput(""));
        RefreshTwitchPredictionOutcomeInputState();
    }

    private void RemoveTwitchPredictionOutcome(TwitchPredictionOutcomeInputViewModel input)
    {
        if (TwitchPredictionOutcomeInputs.Count <= TwitchPredictionApiClient.MinOutcomeCount)
        {
            return;
        }

        TwitchPredictionOutcomeInputs.Remove(input);
        RefreshTwitchPredictionOutcomeInputState();
    }

    private void RefreshTwitchPredictionOutcomeInputState()
    {
        var canRemove = TwitchPredictionOutcomeInputs.Count > TwitchPredictionApiClient.MinOutcomeCount;
        foreach (var input in TwitchPredictionOutcomeInputs)
        {
            input.CanRemove = canRemove;
        }

        OnPropertyChanged(nameof(CanAddTwitchPredictionOutcome));
        AddTwitchPredictionOutcomeCommand.RaiseCanExecuteChanged();
        StartTwitchPredictionCommand.RaiseCanExecuteChanged();
    }

    private async Task StartTwitchPredictionAsync()
    {
        if (twitchPredictionClient is null)
        {
            AddSystemMessage("Twitch prediction controls are not connected yet.");
            return;
        }

        var request = new TwitchPredictionCreateRequest(
            TwitchPredictionTitle,
            TwitchPredictionOutcomeInputs.Select(outcome => outcome.Title).ToArray(),
            TwitchPredictionDurationSeconds);

        await RunTwitchPredictionActionAsync(async () =>
        {
            var prediction = await twitchPredictionClient.CreatePredictionAsync(request);
            TwitchPredictionTitle = "";
            UpsertTwitchPrediction(prediction);
            AddSystemMessage("Twitch prediction started.");
        });
    }

    public Task LockTwitchPredictionAsync(TwitchPredictionFeedItemViewModel card)
    {
        return RunTwitchPredictionActionAsync(async () =>
        {
            if (twitchPredictionClient is null)
            {
                throw new InvalidOperationException("Twitch prediction controls are not connected yet.");
            }

            var prediction = await twitchPredictionClient.LockPredictionAsync(card.PredictionId);
            UpsertTwitchPrediction(prediction);
            AddSystemMessage("Twitch prediction locked.");
        });
    }

    public Task CancelTwitchPredictionAsync(TwitchPredictionFeedItemViewModel card)
    {
        return RunTwitchPredictionActionAsync(async () =>
        {
            if (twitchPredictionClient is null)
            {
                throw new InvalidOperationException("Twitch prediction controls are not connected yet.");
            }

            var prediction = await twitchPredictionClient.CancelPredictionAsync(card.PredictionId);
            UpsertTwitchPrediction(prediction);
            AddSystemMessage("Twitch prediction canceled.");
        });
    }

    public Task ResolveTwitchPredictionAsync(TwitchPredictionFeedItemViewModel card)
    {
        return RunTwitchPredictionActionAsync(async () =>
        {
            if (twitchPredictionClient is null)
            {
                throw new InvalidOperationException("Twitch prediction controls are not connected yet.");
            }

            if (card.SelectedWinningOutcome is null)
            {
                throw new InvalidOperationException("Select a winning prediction outcome.");
            }

            var prediction = await twitchPredictionClient.ResolvePredictionAsync(
                card.PredictionId,
                card.SelectedWinningOutcome.Id);
            UpsertTwitchPrediction(prediction);
            AddSystemMessage("Twitch prediction resolved.");
        });
    }

    private async Task RunTwitchPredictionActionAsync(Func<Task> action)
    {
        if (isTwitchPredictionRequestInFlight)
        {
            return;
        }

        isTwitchPredictionRequestInFlight = true;
        RaiseTwitchPredictionCommandState();
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddSystemMessage($"Twitch prediction request failed: {ex.Message}");
            logger.Write(AppLogLevel.Warning, "TwitchPredictions", $"Twitch prediction request failed for {Target.DisplayName}.", ex);
        }
        finally
        {
            isTwitchPredictionRequestInFlight = false;
            RaiseTwitchPredictionCommandState();
        }
    }

    private void OnAnimatedEmoteImageCacheEntryCompleted(object? sender, AnimatedEmoteImageCacheCompletedEventArgs e)
    {
        var shouldInvalidate = false;
        lock (nativeReplayOverlayAnimationGate)
        {
            shouldInvalidate = nativeReplayOverlayPendingImageLoads.Remove(e.Key);
        }

        if (shouldInvalidate)
        {
            dispatch(InvalidateNativeReplayOverlayFrameIfReplayChatVisible);
        }
    }

    private async Task<bool> TryReconnectChatForSendAsync(string message, Exception sendException)
    {
        if (currentSettings is null ||
            !HasConfiguredChatToken(currentSettings.Chat) ||
            !IsRecoverableChatSendFailure(sendException))
        {
            return false;
        }

        var rememberDockedLocalEcho = false;
        ChatMessage? localEcho = null;

        try
        {
            AddSystemMessage("Reconnecting chat with updated credentials...");
            await RestartChatAsync(currentSettings);
            if (chatClient is null)
            {
                return false;
            }

            rememberDockedLocalEcho = IsDockedChatModeActive;
            localEcho = rememberDockedLocalEcho ? CreateLocalEchoMessage(message) : null;
            if (rememberDockedLocalEcho)
            {
                RememberDockedLocalEcho(localEcho!);
            }

            await chatClient.SendMessageAsync(message);
            OutgoingChatText = "";
            localEcho ??= CreateLocalEchoMessage(message);
            AddChatMessage(localEcho, isRememberedDockedLocalEcho: rememberDockedLocalEcho);
            return true;
        }
        catch (Exception retryException)
        {
            if (rememberDockedLocalEcho && localEcho is not null)
            {
                ForgetDockedLocalEcho(localEcho);
            }

            AddSystemMessage($"Chat reconnect/send failed: {retryException.Message}");
            logger.Write(AppLogLevel.Warning, "Chat", $"Failed to reconnect chat for {Target.DisplayName}", retryException);
            return true;
        }
    }

    private bool HasConfiguredChatToken(ChatSettings settings)
    {
        return Target.Platform switch
        {
            PlatformKind.Twitch => !string.IsNullOrWhiteSpace(settings.TwitchOAuthToken),
            PlatformKind.Kick => !string.IsNullOrWhiteSpace(settings.KickOAuthToken) ||
                !string.IsNullOrWhiteSpace(settings.KickRefreshToken),
            _ => false
        };
    }

    private static bool IsRecoverableChatSendFailure(Exception exception)
    {
        return exception.Message.Contains("OAuth token", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("read-only", StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartChatAsync(CancellationToken cancellationToken)
    {
        Task connectionTask;
        TaskCompletionSource? operationReady = null;
        lock (chatConnectionGate)
        {
            if (disposed)
            {
                return;
            }

            if (chatConnectionTask is not null)
            {
                connectionTask = chatConnectionTask;
            }
            else
            {
                var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    lifetimeCancellation.Token,
                    cancellationToken);
                var version = ++chatConnectionVersion;
                operationReady = new TaskCompletionSource();
                connectionTask = StartChatOperationWhenReadyAsync(
                    operationCancellation,
                    version,
                    operationReady.Task);
                chatConnectionCancellation = operationCancellation;
                chatConnectionTask = connectionTask;
            }
        }

        // Complete the hand-off outside chatConnectionGate.  The operation's synchronous prefix
        // needs to reacquire that gate when it validates its generation.  Using a synchronous TCS
        // here makes the client attach happen before a fire-and-forget caller can publish chat
        // messages, while ConnectAsync still yields as soon as it reaches real network I/O.
        operationReady?.TrySetResult();
        await connectionTask.ConfigureAwait(false);
    }

    private async Task StartChatOperationWhenReadyAsync(
        CancellationTokenSource operationCancellation,
        long version,
        Task operationReady)
    {
        await operationReady.ConfigureAwait(false);
        await StartChatOperationAsync(operationCancellation, version).ConfigureAwait(false);
    }

    private async Task StartChatOperationAsync(
        CancellationTokenSource operationCancellation,
        long version)
    {
        try
        {
            await StopChatClientCoreAsync().ConfigureAwait(false);
            if (!IsCurrentChatConnection(version, operationCancellation.Token))
            {
                return;
            }

            await StartChatCoreAsync(operationCancellation.Token, version).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddSystemMessage($"Chat unavailable: {ex.Message}");
            logger.Write(AppLogLevel.Warning, "Chat", $"Chat failed for {Target.DisplayName}", ex);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested || disposed)
        {
        }
        finally
        {
            lock (chatConnectionGate)
            {
                if (chatConnectionCancellation is not null &&
                    ReferenceEquals(chatConnectionCancellation, operationCancellation))
                {
                    chatConnectionCancellation = null;
                    chatConnectionTask = null;
                }
            }

            operationCancellation.Dispose();
        }
    }

    private async Task StartChatCoreAsync(CancellationToken cancellationToken, long version)
    {
        IChatClient? client = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentChatConnection(version, cancellationToken))
            {
                return;
            }

            client = chatFactory.Create(Target.Platform);
            if (!IsCurrentChatConnection(version, cancellationToken))
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return;
            }

            chatClient = client;
            AttachChatClient(client);
            await client.ConnectAsync(Target, cancellationToken).ConfigureAwait(false);
            if (!IsCurrentChatConnection(version, cancellationToken))
            {
                await DisposeCurrentChatClientAsync(client).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
            {
                await DisposeCurrentChatClientAsync(client).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            if (client is not null)
            {
                await DisposeCurrentChatClientAsync(client).ConfigureAwait(false);
            }

            throw;
        }
    }

    private bool IsCurrentChatConnection(long version, CancellationToken cancellationToken)
    {
        lock (chatConnectionGate)
        {
            return !disposed &&
            version == chatConnectionVersion &&
                !cancellationToken.IsCancellationRequested;
        }
    }

    private async Task EnsureChatClientConnectedAsync(CancellationToken cancellationToken)
    {
        var connectionTask = GetChatConnectionTask();
        if (connectionTask is not null)
        {
            await connectionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (chatClient is not null)
        {
            return;
        }

        await StartChatAsync(cancellationToken);
    }

    private Task? GetChatConnectionTask()
    {
        lock (chatConnectionGate)
        {
            return chatConnectionTask;
        }
    }

    private static void ConfigureSharedChatCatalogs(ChatSettings settings)
    {
        DockedChatBadgeCatalog.Shared.ConfigureTwitchCredentials(
            settings.TwitchClientId,
            settings.TwitchOAuthToken);
    }

    private async Task StopChatAsync(bool clearNativeOverlay = true)
    {
        await StopNativeOverlayChatAsync(clearNativeOverlay);
        await StopChatClientAsync();
    }

    private async Task StopChatClientAsync()
    {
        Task? connectionTask;
        CancellationTokenSource? connectionCancellation;
        lock (chatConnectionGate)
        {
            chatConnectionVersion++;
            connectionTask = chatConnectionTask;
            connectionCancellation = chatConnectionCancellation;
        }

        CancelCancellationSource(connectionCancellation);
        if (connectionTask is not null)
        {
            try
            {
                await connectionTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "Chat", $"Chat startup cleanup failed for {Target.DisplayName}.", ex);
            }
        }

        await StopChatClientCoreAsync().ConfigureAwait(false);
    }

    private async Task StopChatClientCoreAsync()
    {
        var client = DetachChatClientForStop();
        if (client is not null)
        {
            await DisposeDetachedChatClientAsync(client).ConfigureAwait(false);
        }
    }

    private async Task DisposeCurrentChatClientAsync(IChatClient client)
    {
        if (ReferenceEquals(chatClient, client))
        {
            chatClient = null;
            DetachChatClient(client);
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeException)
        {
            logger.Write(AppLogLevel.Warning, "Chat", $"Failed to dispose failed chat client for {Target.DisplayName}.", disposeException);
        }
    }

    private IChatClient? DetachChatClientForStop()
    {
        if (chatClient is null)
        {
            return null;
        }

        var client = chatClient;
        chatClient = null;
        DetachChatClient(client);
        return client;
    }

    private static async Task DisposeDetachedChatClientAsync(IChatClient client)
    {
        await client.DisposeAsync();
    }

    private void InitializeExplicitVodReplaySession(
        AppSettings settings,
        TwitchSubOnlyVodResolution? subOnlyVodResolution = null)
    {
        if (!settings.Replay.Enabled)
        {
            ResetReplayState("Replay seekbar is disabled in Settings.");
            return;
        }

        if (Target.IsExplicitTwitchVod)
        {
            InitializeExplicitTwitchVodReplaySession(subOnlyVodResolution);
            return;
        }

        if (Target.IsExplicitKickVod)
        {
            InitializeExplicitKickVodReplaySession(settings);
            return;
        }

        SetReplayUnavailable("The selected VOD type is not supported for replay seeking.");
    }

    private void InitializeExplicitTwitchVodReplaySession(
        TwitchSubOnlyVodResolution? subOnlyVodResolution = null)
    {
        if (!TryValidateExplicitVodReplayFields(
                "Twitch",
                requireDirectHlsSource: false,
                fallbackDuration: subOnlyVodResolution?.MediaDuration,
                out var mediaId,
                out var duration))
        {
            return;
        }

        var replay = new ReplaySessionInfo(
            Target.Platform,
            string.IsNullOrWhiteSpace(subOnlyVodResolution?.OwnerLogin)
                ? Target.Channel
                : subOnlyVodResolution.OwnerLogin,
            Target.Url,
            mediaId,
            subOnlyVodResolution?.CreatedAtUtc,
            duration,
            true,
            "",
            ChatRoomId: Target.BroadcasterId);

        ApplyExplicitVodReplaySession(replay);
        QueueReplayChatLoadIfNeeded(TimeSpan.Zero);
        StartReplayClockPolling();
    }

    private void InitializeExplicitKickVodReplaySession(AppSettings settings)
    {
        if (!TryValidateExplicitVodReplayFields(
                "Kick",
                requireDirectHlsSource: true,
                fallbackDuration: null,
                out var mediaId,
                out var duration))
        {
            return;
        }

        var chatRoomId = string.IsNullOrWhiteSpace(Target.ChatRoomId)
            ? Target.BroadcasterId
            : Target.ChatRoomId;
        var replay = new ReplaySessionInfo(
            Target.Platform,
            Target.Channel,
            Target.Url,
            mediaId,
            Target.MediaStartedAtUtc,
            duration,
            true,
            "",
            ChatRoomId: chatRoomId);

        ApplyExplicitVodReplaySession(replay);
        QueueReplayChatLoad(
            replay,
            settings,
            TimeSpan.Zero,
            notifyUnavailable: true,
            GetReplayChatStateVersion());
        StartReplayClockPolling();
    }

    private bool TryValidateExplicitVodReplayFields(
        string platformName,
        bool requireDirectHlsSource,
        TimeSpan? fallbackDuration,
        out string mediaId,
        out TimeSpan duration)
    {
        mediaId = Target.MediaId.Trim();
        duration = Target.MediaDuration > TimeSpan.Zero
            ? Target.MediaDuration
            : fallbackDuration.GetValueOrDefault();

        if (string.IsNullOrWhiteSpace(mediaId))
        {
            SetReplayUnavailable($"The selected {platformName} VOD did not include a video ID.");
            return false;
        }

        if (requireDirectHlsSource &&
            !TryCreateDirectReplayPlaybackUri(Target.Url, out _))
        {
            SetReplayUnavailable($"The selected {platformName} VOD did not include a usable HLS source URL.");
            return false;
        }

        if (duration <= TimeSpan.Zero)
        {
            SetReplayUnavailable($"The selected {platformName} VOD did not include a usable duration.");
            return false;
        }

        return true;
    }

    private void ApplyExplicitVodReplaySession(ReplaySessionInfo replay)
    {
        replaySession = replay;
        ResetReplayChatState();
        ClearReplayClockAnchor();
        IsReplayMode = true;
        IsBehindLive = false;
        ReplaySeekToolTip = $"{replay.Platform} VOD replay available: {replay.ReplayId}";
        ApplyReplayClock(TimeSpan.Zero, replay.Duration, isSeekable: true);
        QueueEmptyReplayChatWindowUiApply(clearNativeOverlayImmediately: true);
    }

    private Task RefreshReplayAvailabilityAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        long refreshVersion = 0)
    {
        return RefreshReplayAvailabilityCoreAsync(
            settings,
            cancellationToken,
            refreshVersion,
            prefetchPlaybackUrl: true);
    }

    private async Task RefreshReplayAvailabilityCoreAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        long refreshVersion,
        bool prefetchPlaybackUrl)
    {
        if (IsBackgroundResourceServicesSuspended)
        {
            return;
        }

        if (!settings.Replay.Enabled)
        {
            if (IsReplayAvailabilityRefreshCurrent(refreshVersion))
            {
                ResetReplayState("Replay seekbar is disabled in Settings.");
            }

            return;
        }

        if (replayResolver is null)
        {
            if (IsReplayAvailabilityRefreshCurrent(refreshVersion))
            {
                ResetReplayState("Replay resolver is not configured.");
            }

            return;
        }

        try
        {
            var replay = await replayResolver.ResolveCurrentReplayAsync(Target, Quality, settings, cancellationToken);
            if (IsBackgroundResourceServicesSuspended ||
                !IsReplayAvailabilityRefreshCurrent(refreshVersion))
            {
                return;
            }

            replaySession = replay;
            if (!replay.IsAvailable)
            {
                CancelLiveDvrPromotionPolling();
                SetReplayUnavailable(replay.UnavailableReason);
                return;
            }

            if (ShouldUseCapturedReplayChat(replay))
            {
                PrepareCapturedReplayChat(replay);
            }

            if (IsCurrentLiveDvrReplay(replay))
            {
                StartLiveDvrPromotionPolling(settings);
            }
            else
            {
                await StopLiveDvrPromotionPollingAsync();
            }

            if (prefetchPlaybackUrl)
            {
                QueueReplayPlaybackUrlResolution(replay, settings);
            }

            var duration = GetCurrentReplayDuration(replay);
            dispatch(() =>
            {
                IsReplaySeekEnabled = true;
                ApplyReplayClock(duration, duration, isSeekable: true);
                ReplayLiveStateText = "Live";
                ApplyReplaySeekToolTipForCurrentReadiness();
            });
            StartReplayClockPolling();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Replay", $"Replay lookup failed for {Target.DisplayName}.", ex);
            if (IsReplayAvailabilityRefreshCurrent(refreshVersion))
            {
                ResetReplayState($"Replay unavailable: {ex.Message}");
            }
        }
    }

    private bool IsReplayAvailabilityRefreshCurrent(long refreshVersion)
    {
        return refreshVersion == 0 ||
            refreshVersion == Volatile.Read(ref replayAvailabilityRefreshVersion);
    }

    private async Task WaitForReplayAvailabilityRefreshOnceAsync(CancellationToken cancellationToken)
    {
        Task? refreshTask;
        lock (replayAvailabilityRefreshGate)
        {
            refreshTask = replayAvailabilityRefreshTask is { IsCompleted: false } task
                ? task
                : null;
        }

        if (refreshTask is null)
        {
            return;
        }

        await refreshTask.WaitAsync(cancellationToken);
    }

    private void StartReplayAvailabilityRefreshInBackground(
        AppSettings settings,
        bool prefetchPlaybackUrl = true)
    {
        if (IsBackgroundResourceServicesSuspended)
        {
            return;
        }

        CancelReplayAvailabilityRefresh();
        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var refreshVersion = Interlocked.Increment(ref replayAvailabilityRefreshVersion);
        Task refreshTask;
        lock (replayAvailabilityRefreshGate)
        {
            replayAvailabilityRefreshCancellation = cancellation;
            refreshTask = Task.Run(async () =>
            {
                try
                {
                    await RefreshReplayAvailabilityCoreAsync(
                        settings,
                        cancellationToken,
                        refreshVersion,
                        prefetchPlaybackUrl);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.Write(AppLogLevel.Warning, "Replay", $"Background replay lookup failed for {Target.DisplayName}.", ex);
                }
                finally
                {
                    lock (replayAvailabilityRefreshGate)
                    {
                        if (ReferenceEquals(replayAvailabilityRefreshCancellation, cancellation))
                        {
                            replayAvailabilityRefreshCancellation = null;
                            replayAvailabilityRefreshTask = null;
                        }
                    }

                    cancellation.Dispose();
                }
            });
            replayAvailabilityRefreshTask = refreshTask;
        }
    }

    private void CancelReplayAvailabilityRefresh()
    {
        CancellationTokenSource? cancellation;
        lock (replayAvailabilityRefreshGate)
        {
            cancellation = replayAvailabilityRefreshCancellation;
            Interlocked.Increment(ref replayAvailabilityRefreshVersion);
        }

        CancelReplayPlaybackUrlResolution();

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelReplayAvailabilityPolling()
    {
        CancellationTokenSource? cancellation;
        lock (replayAvailabilityRefreshGate)
        {
            cancellation = replayAvailabilityRefreshCancellation;
            Interlocked.Increment(ref replayAvailabilityRefreshVersion);
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private IReadOnlyList<ReplayTransitionWork> PrepareReplayTransitionWork(
        ReplaySessionInfo replay,
        AppSettings settings)
    {
        var work = new List<ReplayTransitionWork>();
        var detachedNativeOverlayChat = TryDetachNativeOverlayChatForReplayTransition();
        work.Add(detachedNativeOverlayChat is null
            ? new ReplayTransitionWork(
                "stop live native overlay controller",
                StopNativeOverlayChatAfterReplayTransitionAsync,
                RunOnPlaybackFailure: true,
                RunBeforePlayback: true)
            : new ReplayTransitionWork(
                "stop live native overlay controller",
                () => StopDetachedNativeOverlayChatAfterReplayTransitionAsync(detachedNativeOverlayChat),
                RunOnPlaybackFailure: true,
                RunBeforePlayback: true));

        if (ShouldUseCapturedReplayChat(replay))
        {
            if (ShouldCaptureReplayChat(settings))
            {
                work.Add(new(
                    "ensure replay chat capture client",
                    () => EnsureChatClientConnectedAsync(CancellationToken.None),
                    RunOnPlaybackFailure: false));
            }
            else if (DetachChatClientForStop() is { } detachedChatClient)
            {
                work.Add(new(
                    "stop live chat client",
                    () => DisposeDetachedChatClientAsync(detachedChatClient),
                    RunOnPlaybackFailure: true));
            }
        }
        else if (DetachChatClientForStop() is { } detachedChatClient)
        {
            work.Add(new(
                "stop live chat client",
                () => DisposeDetachedChatClientAsync(detachedChatClient),
                RunOnPlaybackFailure: true));
        }

        if (DetachStreamSession() is { } detachedStreamSession)
        {
            work.Add(new(
                "stop live Streamlink HTTP transport",
                () => DisposeDetachedStreamSessionAsync(detachedStreamSession),
                RunOnPlaybackFailure: true));
        }

        return work;
    }

    private async Task StopNativeOverlayChatAfterReplayTransitionAsync()
    {
        await StopNativeOverlayChatAsync(clearOverlay: false).ConfigureAwait(false);
    }

    private async Task StopDetachedNativeOverlayChatAfterReplayTransitionAsync(DetachedNativeOverlayChat detached)
    {
        SuppressNativeOverlayStoppedNotice(detached.Process);
        await StopDetachedNativeOverlayChatAsync(detached, clearOverlay: false).ConfigureAwait(false);
    }

    private void SuppressNativeOverlayStoppedNotice(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            var processId = process.Id;
            lock (nativeOverlayStopNoticeGate)
            {
                suppressedNativeOverlayStoppedProcessIds.Add(processId);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void OnNativeOverlayProcessExited(Process process)
    {
        var suppressNotice = false;
        try
        {
            var processId = process.Id;
            lock (nativeOverlayStopNoticeGate)
            {
                suppressNotice = suppressedNativeOverlayStoppedProcessIds.Remove(processId);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        if (!suppressNotice)
        {
            AddSystemMessage("Native VLC chat overlay stopped.");
        }
    }

    private void RunReplayTransitionWorkInBackground(IReadOnlyList<ReplayTransitionWork> work)
    {
        if (work.Count == 0)
        {
            return;
        }

        var capturedWork = work.ToArray();
        _ = Task.Run(() => RunReplayTransitionWorkAsync(capturedWork));
    }

    private Task RunReplayTransitionWorkAsync(IReadOnlyList<ReplayTransitionWork> work)
    {
        return Task.WhenAll(work.Select(RunReplayTransitionWorkItemAsync));
    }

    private async Task RunReplayTransitionWorkItemAsync(ReplayTransitionWork work)
    {
        try
        {
            await work.ExecuteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Replay",
                $"Replay transition cleanup failed while trying to {work.Description}.",
                ex);
        }
    }

    private void QueueReplayPlaybackUrlResolution(ReplaySessionInfo replay, AppSettings settings)
    {
        if (!CanResolveReplayPlaybackUrl(replay, settings))
        {
            CancelReplayPlaybackUrlResolution();
            return;
        }

        var key = CreateReplayPlaybackUrlKey(replay, settings);
        if (IsDirectReplayPlaybackUrl(replay) &&
            !ShouldTrySubOnlyVodFallback(key))
        {
            ReplayPlaybackUrlResolution? previousDirectResolution;
            lock (replayPlaybackUrlResolutionGate)
            {
                previousDirectResolution = replayPlaybackUrlResolution;
                replayPlaybackUrlResolution = null;
                replayPlaybackUrlReadinessKey = key;
                replayPlaybackUrlReadiness = ReplayPlaybackUrlReadiness.Unnecessary;
            }

            CancelReplayPlaybackUrlResolution(previousDirectResolution);
            QueueReplayPlaybackUrlReadinessUiRefresh(key);
            return;
        }

        ReplayPlaybackUrlResolution? previousResolution;
        ReplayPlaybackUrlResolution? resolution = null;
        var reusedExistingResolution = false;
        lock (replayPlaybackUrlResolutionGate)
        {
            if (replayPlaybackUrlResolution is { } existingResolution &&
                existingResolution.Key.Equals(key) &&
                !existingResolution.Task.IsCanceled &&
                !existingResolution.Task.IsFaulted)
            {
                replayPlaybackUrlReadinessKey = key;
                replayPlaybackUrlReadiness = existingResolution.Task.IsCompletedSuccessfully
                    ? ReplayPlaybackUrlReadiness.Successful
                    : ReplayPlaybackUrlReadiness.Pending;
                previousResolution = null;
                reusedExistingResolution = true;
            }
            else
            {
                var cancellation = new CancellationTokenSource();
                var resolutionTask = Task.Run(
                    () => ResolveReplayPlaybackUrlCoreAsync(key, cancellation.Token),
                    cancellation.Token);

                previousResolution = replayPlaybackUrlResolution;
                resolution = new ReplayPlaybackUrlResolution(key, resolutionTask, cancellation);
                replayPlaybackUrlResolution = resolution;
                replayPlaybackUrlReadinessKey = key;
                replayPlaybackUrlReadiness = ReplayPlaybackUrlReadiness.Pending;
            }
        }

        if (reusedExistingResolution)
        {
            QueueReplayPlaybackUrlReadinessUiRefresh(key);
            return;
        }

        CancelReplayPlaybackUrlResolution(previousResolution);
        QueueReplayPlaybackUrlReadinessUiRefresh(key);
        _ = ObserveReplayPlaybackUrlResolutionAsync(resolution!);
    }

    private async Task<StreamlinkResolvedUrl> ResolveReplayPlaybackUrlForSeekAsync(
        ReplayPlaybackUrlKey key,
        CancellationToken cancellationToken)
    {
        ReplayPlaybackUrlResolution? resolution;
        lock (replayPlaybackUrlResolutionGate)
        {
            resolution = replayPlaybackUrlResolution is { } existingResolution &&
                existingResolution.Key.Equals(key)
                    ? existingResolution
                    : null;
        }

        if (resolution is not null)
        {
            try
            {
                var resolved = await resolution.Task.WaitAsync(cancellationToken);
                MarkReplayPlaybackUrlReadiness(
                    resolution,
                    ReplayPlaybackUrlReadiness.Successful,
                    logMessage: null,
                    exception: null);
                return resolved;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ClearReplayPlaybackUrlResolution(resolution);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                MarkReplayPlaybackUrlReadiness(
                    resolution,
                    ReplayPlaybackUrlReadiness.Failed,
                    logMessage: null,
                    exception: null);
                ClearReplayPlaybackUrlResolution(resolution);
                logger.Write(
                    AppLogLevel.Info,
                    "Replay",
                    $"Prefetched replay stream URL failed for {Target.DisplayName}; retrying during seek.",
                    ex);
            }
        }

        var fallbackResolved = await ResolveReplayPlaybackUrlCoreAsync(key, cancellationToken);
        MarkReplayPlaybackUrlReadiness(key, ReplayPlaybackUrlReadiness.Successful);
        return fallbackResolved;
    }

    private async Task<StreamlinkResolvedUrl> ResolveReplayPlaybackUrlCoreAsync(
        ReplayPlaybackUrlKey key,
        CancellationToken cancellationToken)
    {
        if (key.Target.Platform == PlatformKind.Twitch &&
            TryCreateDirectReplayPlaybackUri(key.Target.Url, out var directReplayUri))
        {
            if (ShouldTrySubOnlyVodFallback(key))
            {
                try
                {
                    return await ResolveSubOnlyReplayPlaybackUrlAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception fallbackError) when (fallbackError is not OperationCanceledException)
                {
                    logger.Write(
                        AppLogLevel.Info,
                        "Replay",
                        $"Sub-only replay fallback was unavailable for {Target.DisplayName}; using the direct replay HLS URL.",
                        fallbackError);
                }
            }

            return new StreamlinkResolvedUrl(directReplayUri, "Using direct replay HLS URL.");
        }

        var request = new StreamTransportRequest(
            key.Target,
            key.Quality,
            key.StreamlinkPath,
            false,
            CommandLineTokenizer.Tokenize(key.CustomArguments));

        try
        {
            return await streamlinkService.ResolveStreamUrlAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception streamlinkError) when (streamlinkError is not OperationCanceledException &&
            ShouldTrySubOnlyVodFallback(key))
        {
            logger.Write(
                AppLogLevel.Info,
                "Replay",
                $"Streamlink could not resolve replay {key.ReplayId} for {Target.DisplayName} ({streamlinkError.Message}); trying the sub-only VOD fallback.");
            try
            {
                return await ResolveSubOnlyReplayPlaybackUrlAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fallbackError) when (fallbackError is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Streamlink could not play the live replay: {streamlinkError.Message} Sub-only fallback also failed: {fallbackError.Message}",
                    fallbackError);
            }
        }
    }

    private async Task<StreamlinkResolvedUrl> ResolveSubOnlyReplayPlaybackUrlAsync(
        ReplayPlaybackUrlKey key,
        CancellationToken cancellationToken)
    {
        if (twitchSubOnlyVodResolver is null)
        {
            throw new InvalidOperationException("The sub-only VOD resolver is not configured.");
        }

        var bypass = await twitchSubOnlyVodResolver.ResolveAsync(
                new TwitchSubOnlyVodRequest(key.ReplayId, key.Quality),
                cancellationToken)
            .ConfigureAwait(false);
        AddSystemMessage($"Playing sub-only live replay via direct playlist ({bypass.QualityKey}).");
        return new StreamlinkResolvedUrl(bypass.PlaybackUri, bypass.Message);
    }

    private bool ShouldTrySubOnlyVodFallback(ReplayPlaybackUrlKey key)
    {
        if (key.Target.Platform != PlatformKind.Twitch ||
            twitchSubOnlyVodResolver is null ||
            !IsNumericTwitchVodId(key.ReplayId))
        {
            return false;
        }

        if (!TryCreateDirectReplayPlaybackUri(key.Target.Url, out _))
        {
            return true;
        }

        return Uri.TryCreate(key.Target.Url, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith(".cloudfront.net", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumericTwitchVodId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.All(static character => character is >= '0' and <= '9');
    }

    private bool CanResolveReplayPlaybackUrl(ReplaySessionInfo replay, AppSettings settings)
    {
        return !Target.IsExplicitVod &&
            replay.IsAvailable &&
            (IsDirectReplayPlaybackUrl(replay) ||
                !string.IsNullOrWhiteSpace(settings.StreamlinkPath));
    }

    private ReplayPlaybackUrlKey CreateReplayPlaybackUrlKey(ReplaySessionInfo replay, AppSettings settings)
    {
        return new ReplayPlaybackUrlKey(
            Target with { Platform = replay.Platform, Channel = replay.Channel, Url = replay.ReplayUrl },
            replay.ReplayId,
            replay.GetStreamlinkQuality(Quality),
            settings.StreamlinkPath?.Trim() ?? "",
            settings.CustomStreamlinkArguments);
    }

    private void ClearReplayPlaybackUrlResolution(ReplayPlaybackUrlResolution resolution)
    {
        lock (replayPlaybackUrlResolutionGate)
        {
            if (ReferenceEquals(replayPlaybackUrlResolution, resolution))
            {
                replayPlaybackUrlResolution = null;
            }
        }

        CancelReplayPlaybackUrlResolution(resolution);
    }

    private void CancelReplayPlaybackUrlResolution()
    {
        ReplayPlaybackUrlResolution? resolution;
        lock (replayPlaybackUrlResolutionGate)
        {
            resolution = replayPlaybackUrlResolution;
            replayPlaybackUrlResolution = null;
            replayPlaybackUrlReadinessKey = null;
            replayPlaybackUrlReadiness = ReplayPlaybackUrlReadiness.None;
        }

        CancelReplayPlaybackUrlResolution(resolution);
        dispatch(RaiseReplaySeekAvailabilityChanged);
    }

    private static void CancelReplayPlaybackUrlResolution(ReplayPlaybackUrlResolution? resolution)
    {
        if (resolution is null)
        {
            return;
        }

        try
        {
            resolution.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ObserveReplayPlaybackUrlResolutionAsync(ReplayPlaybackUrlResolution resolution)
    {
        try
        {
            await resolution.Task.ConfigureAwait(false);
            MarkReplayPlaybackUrlReadiness(
                resolution,
                ReplayPlaybackUrlReadiness.Successful,
                logMessage: null,
                exception: null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MarkReplayPlaybackUrlReadiness(
                resolution,
                ReplayPlaybackUrlReadiness.Failed,
                $"Replay stream URL prefetch failed for {Target.DisplayName}. Seek will resolve it on demand.",
                ex);
        }
        finally
        {
            lock (replayPlaybackUrlResolutionGate)
            {
                if (ReferenceEquals(replayPlaybackUrlResolution, resolution) &&
                    (resolution.Task.IsCanceled || resolution.Task.IsFaulted))
                {
                    replayPlaybackUrlResolution = null;
                }
            }

            resolution.Cancellation.Dispose();
        }
    }

    private void MarkReplayPlaybackUrlReadiness(
        ReplayPlaybackUrlResolution resolution,
        ReplayPlaybackUrlReadiness readiness,
        string? logMessage,
        Exception? exception)
    {
        var isCurrent = false;
        lock (replayPlaybackUrlResolutionGate)
        {
            if (replayPlaybackUrlReadinessKey is { } key &&
                key.Equals(resolution.Key))
            {
                replayPlaybackUrlReadiness = readiness;
                isCurrent = true;
            }
        }

        if (!isCurrent)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(logMessage))
        {
            logger.Write(AppLogLevel.Info, "Replay", logMessage, exception);
        }

        QueueReplayPlaybackUrlReadinessUiRefresh(resolution.Key);
    }

    private void MarkReplayPlaybackUrlReadiness(
        ReplayPlaybackUrlKey key,
        ReplayPlaybackUrlReadiness readiness)
    {
        var isCurrent = false;
        lock (replayPlaybackUrlResolutionGate)
        {
            if (IsReplayPlaybackUrlReadinessCurrent(key))
            {
                replayPlaybackUrlReadinessKey = key;
                replayPlaybackUrlReadiness = readiness;
                isCurrent = true;
            }
        }

        if (isCurrent)
        {
            QueueReplayPlaybackUrlReadinessUiRefresh(key);
        }
    }

    private bool IsCurrentReplayPlaybackUrlReadyForSeeking()
    {
        if (replaySession is not { IsAvailable: true } replay ||
            currentSettings is null ||
            CanSeekCurrentReplayInPlace(replay) ||
            !CanResolveReplayPlaybackUrl(replay, currentSettings))
        {
            return true;
        }

        var key = CreateReplayPlaybackUrlKey(replay, currentSettings);
        if (IsDirectReplayPlaybackUrl(replay) &&
            !ShouldTrySubOnlyVodFallback(key))
        {
            return true;
        }

        lock (replayPlaybackUrlResolutionGate)
        {
            return replayPlaybackUrlReadinessKey is { } readinessKey &&
                readinessKey.Equals(key) &&
                (replayPlaybackUrlReadiness is ReplayPlaybackUrlReadiness.Successful or
                    ReplayPlaybackUrlReadiness.Failed or
                    ReplayPlaybackUrlReadiness.Unnecessary);
        }
    }

    private void QueueReplayPlaybackUrlReadinessUiRefresh(ReplayPlaybackUrlKey key)
    {
        dispatch(() =>
        {
            if (!IsReplayPlaybackUrlReadinessCurrent(key))
            {
                return;
            }

            ApplyReplaySeekToolTipForCurrentReadiness();
            RaiseReplaySeekAvailabilityChanged();
        });
    }

    private bool IsReplayPlaybackUrlReadinessCurrent(ReplayPlaybackUrlKey key)
    {
        if (replaySession is not { IsAvailable: true } replay ||
            currentSettings is null)
        {
            return false;
        }

        return key.Equals(CreateReplayPlaybackUrlKey(replay, currentSettings));
    }

    private void ApplyReplaySeekToolTipForCurrentReadiness()
    {
        if (replaySession is not { IsAvailable: true } replay ||
            currentSettings is null)
        {
            return;
        }

        if (IsReplayPlaybackUrlPrefetchPending(replay, currentSettings))
        {
            ReplaySeekToolTip = "Preparing replay stream URL...";
            return;
        }

        ReplaySeekToolTip = Target.IsExplicitVod
            ? $"{replay.Platform} VOD replay available: {replay.ReplayId}"
            : $"Replay available: {replay.ReplayId}";
    }

    private bool IsReplayPlaybackUrlPrefetchPending(ReplaySessionInfo replay, AppSettings settings)
    {
        if (!CanResolveReplayPlaybackUrl(replay, settings))
        {
            return false;
        }

        var key = CreateReplayPlaybackUrlKey(replay, settings);
        if (IsDirectReplayPlaybackUrl(replay) &&
            !ShouldTrySubOnlyVodFallback(key))
        {
            return false;
        }

        lock (replayPlaybackUrlResolutionGate)
        {
            return replayPlaybackUrlReadinessKey is { } readinessKey &&
                readinessKey.Equals(key) &&
                replayPlaybackUrlReadiness == ReplayPlaybackUrlReadiness.Pending;
        }
    }

    private static bool IsDirectReplayPlaybackUrl(ReplaySessionInfo replay)
    {
        return replay.Platform == PlatformKind.Twitch &&
            TryCreateDirectReplayPlaybackUri(replay.ReplayUrl, out _);
    }

    private static bool TryCreateDirectReplayPlaybackUri(string replayUrl, out Uri uri)
    {
        if (Uri.TryCreate(replayUrl, UriKind.Absolute, out uri!) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private void ResetReplayState(string reason)
    {
        StopNativeReplayOverlayEventHost();
        CancelReplayPlaybackUrlResolution();
        currentReplayPlaybackKey = null;
        replaySession = null;
        CancelLiveDvrPromotionPolling();
        ResetReplayChatState();
        CancelReplaySeekPreview();
        CancelReplaySeekOperation();
        ClearReplayClockAnchor();
        IsReplayMode = false;
        IsBehindLive = false;
        IsReplaySeekEnabled = false;
        ReplaySeekValue = 0;
        ReplaySeekMaximum = 1;
        ReplayElapsedText = "0:00";
        ReplayDurationText = "0:00";
        ReplayLiveStateText = "Live";
        ReplaySeekToolTip = reason;
    }

    private void SetReplayUnavailable(string reason)
    {
        StopNativeReplayOverlayEventHost();
        CancelReplayPlaybackUrlResolution();
        currentReplayPlaybackKey = null;
        CancelLiveDvrPromotionPolling();
        ResetReplayChatState();
        CancelReplaySeekPreview();
        CancelReplaySeekOperation();
        ClearReplayClockAnchor();
        IsReplayMode = false;
        IsBehindLive = false;
        IsReplaySeekEnabled = false;
        ReplaySeekValue = 0;
        ReplaySeekMaximum = 1;
        ReplayElapsedText = "0:00";
        ReplayDurationText = "0:00";
        ReplayLiveStateText = string.IsNullOrWhiteSpace(reason) ? "Replay unavailable" : reason;
        ReplaySeekToolTip = ReplayLiveStateText;
    }

    private void StartReplayClockPolling()
    {
        if (IsBackgroundResourceServicesSuspended)
        {
            return;
        }

        replayClockPollingCancellation?.Cancel();
        replayClockPollingCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        replayClockPollingCancellation = cancellation;
        replayClockPollingTask = Task.Run(() => PollReplayClockAsync(cancellation.Token));
    }

    private async Task StopReplayClockPollingAsync()
    {
        var cancellation = replayClockPollingCancellation;
        var pollingTask = replayClockPollingTask;
        replayClockPollingCancellation = null;
        replayClockPollingTask = null;

        await StopPollingAsync(
            cancellation,
            pollingTask,
            "Replay",
            $"Replay clock cleanup failed for {Target.DisplayName}.");
    }

    /// <summary>
    /// Cancels and awaits a polling loop, swallowing cancellation, logging any other failure
    /// under <paramref name="logCategory"/>, and disposing the cancellation source. Shared by
    /// the replay clock, viewer count, aspect ratio, and DVR promotion pollers.
    /// </summary>
    private async Task StopPollingAsync(
        CancellationTokenSource? cancellation,
        Task? pollingTask,
        string logCategory,
        string failureMessage)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
            if (pollingTask is not null)
            {
                await pollingTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, logCategory, failureMessage, ex);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task PollReplayClockAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UpdateReplayClock();
                await Task.Delay(ReplayClockRefreshInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "Replay", $"Replay clock update failed for {Target.DisplayName}.", ex);
                await Task.Delay(ReplayClockRefreshInterval, cancellationToken);
            }
        }
    }

    private void UpdateReplayClock()
    {
        if (replaySession is not { IsAvailable: true } replay)
        {
            return;
        }

        // Resolving the engine clock and applying its chat window are separate operations. A seek
        // can complete between them, so carry the generation that produced this sample all the way
        // through instead of relying only on the later IsReplaySeekInProgress snapshot.
        var sampledSeekOperationVersion = Volatile.Read(ref replaySeekOperationVersion);
        var seekWasInProgress = IsReplaySeekInProgress;
        var clock = ResolveReplayClock(
            replay,
            sampledSeekOperationVersion,
            seekWasInProgress);
        var sampleIsCurrent = !seekWasInProgress &&
            IsReplayClockSampleCurrent(sampledSeekOperationVersion);

        if (sampleIsCurrent)
        {
            QueueReplayClockUiApply(clock, sampledSeekOperationVersion);
        }

        if (IsReplayMode && sampleIsCurrent)
        {
            QueueReplayChatLoadIfNeeded(clock.Position, sampledSeekOperationVersion);
            if (!ShouldUseCapturedReplayChat(replay) &&
                replayChatSelector.Count > 0)
            {
                UpdateReplayChatWindowCore(
                    clock.Position,
                    force: false,
                    replayChatSelector,
                    sampledSeekOperationVersion);
            }
        }
    }

    private bool IsReplayClockSampleCurrent(long sampledSeekOperationVersion)
    {
        return !IsReplaySeekInProgress &&
            IsLatestReplaySeekOperation(sampledSeekOperationVersion);
    }

    private void QueueReplayClockUiApply(
        ReplayClockSnapshot clock,
        long sampledSeekOperationVersion)
    {
        lock (replayClockUiGate)
        {
            pendingReplayClockUiSample = new ReplayClockUiUpdate(
                clock,
                sampledSeekOperationVersion);
            if (replayClockUiDispatchQueued)
            {
                return;
            }

            replayClockUiDispatchQueued = true;
        }

        dispatch(ApplyPendingReplayClockUiSample);
    }

    private void QueueReplaySeekPreviewTextApply(double sliderOffsetSeconds)
    {
        lock (replaySeekPreviewUiGate)
        {
            pendingReplaySeekPreviewTextValue = sliderOffsetSeconds;
            if (replaySeekPreviewTextDispatchQueued)
            {
                return;
            }

            replaySeekPreviewTextDispatchQueued = true;
        }

        dispatch(ApplyPendingReplaySeekPreviewText);
    }

    private void ApplyPendingReplaySeekPreviewText()
    {
        while (true)
        {
            double sliderOffsetSeconds;
            lock (replaySeekPreviewUiGate)
            {
                sliderOffsetSeconds = pendingReplaySeekPreviewTextValue;
                if (!isReplaySeekPreviewActive)
                {
                    replaySeekPreviewTextDispatchQueued = false;
                    return;
                }
            }

            var previewPosition = ClampReplayOffset(
                TimeSpan.FromSeconds(sliderOffsetSeconds),
                TimeSpan.FromSeconds(ReplaySeekMaximum));
            ReplayElapsedText = StreamViewModelHelpers.FormatClockTime(previewPosition);

            lock (replaySeekPreviewUiGate)
            {
                if (Math.Abs(pendingReplaySeekPreviewTextValue - sliderOffsetSeconds) < double.Epsilon)
                {
                    replaySeekPreviewTextDispatchQueued = false;
                    return;
                }
            }
        }
    }

    private void ApplyPendingReplayClockUiSample()
    {
        while (true)
        {
            ReplayClockUiUpdate? update;
            lock (replayClockUiGate)
            {
                update = pendingReplayClockUiSample;
                pendingReplayClockUiSample = null;
                if (update is null)
                {
                    replayClockUiDispatchQueued = false;
                    return;
                }
            }

            if (IsReplayClockSampleCurrent(update.Value.ReplaySeekOperationVersion))
            {
                var clock = update.Value.Clock;
                ApplyReplayClock(clock.Position, clock.Duration, clock.IsSeekable);
            }

            lock (replayClockUiGate)
            {
                if (pendingReplayClockUiSample is null)
                {
                    replayClockUiDispatchQueued = false;
                    return;
                }
            }
        }
    }

    private void ApplyReplayClock(TimeSpan position, TimeSpan duration, bool isSeekable)
    {
        var normalizedDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
        var normalizedPosition = ClampReplayOffset(position, normalizedDuration);
        ReplaySeekMaximum = normalizedDuration.TotalSeconds;
        if (isReplaySeekPreviewActive)
        {
            var previewPosition = ClampReplayOffset(TimeSpan.FromSeconds(ReplaySeekSliderValue), normalizedDuration);
            ReplaySeekSliderValue = previewPosition.TotalSeconds;
            ReplayElapsedText = StreamViewModelHelpers.FormatClockTime(previewPosition);
        }
        else
        {
            ReplaySeekValue = normalizedPosition.TotalSeconds;
            ReplaySeekSliderValue = normalizedPosition.TotalSeconds;
            ReplayElapsedText = StreamViewModelHelpers.FormatClockTime(normalizedPosition);
        }

        ReplayDurationText = StreamViewModelHelpers.FormatClockTime(normalizedDuration);
        IsReplaySeekEnabled = replaySession?.IsAvailable == true && isSeekable;
        ReplayLiveStateText = Target.IsExplicitVod
            ? "VOD"
            : IsReplayMode || IsBehindLive
                ? "Behind live"
                : "Live";
        if (!isSeekable)
        {
            ReplaySeekToolTip = "The current replay media is not seekable.";
        }
    }

    private ReplayClockSnapshot ResolveReplayClock(
        ReplaySessionInfo replay,
        long sampledSeekOperationVersion,
        bool sampleBeganDuringSeek)
    {
        // While paused, freeze the whole clock -- both the elapsed position and the total duration. A
        // live stream keeps advancing (the engine clock drifts toward the live edge and the duration
        // grows with wall-clock), so without this the timestamp would keep ticking up even though
        // playback is stopped. Captured once on the first paused sample and held until playback resumes.
        if (Status == PlaybackStatus.Paused)
        {
            return pausedReplayClock ??= ResolveLiveReplayClock(
                replay,
                sampledSeekOperationVersion,
                sampleBeganDuringSeek);
        }

        pausedReplayClock = null;
        return ResolveLiveReplayClock(
            replay,
            sampledSeekOperationVersion,
            sampleBeganDuringSeek);
    }

    private ReplayClockSnapshot ResolveLiveReplayClock(
        ReplaySessionInfo replay,
        long sampledSeekOperationVersion,
        bool sampleBeganDuringSeek)
    {
        var duration = GetCurrentReplayDuration(replay);
        if (!IsReplayMode)
        {
            return new ReplayClockSnapshot(duration, duration, true);
        }

        var observedAtUtc = DateTimeOffset.UtcNow;
        var isSeekable = true;
        if (playbackEngine?.TryGetPlaybackClock(out var clock) == true)
        {
            isSeekable = clock.IsSeekable;
            if (!Target.IsExplicitVod)
            {
                duration = NormalizeReplayClockDuration(clock.Duration, duration);
            }

            if (TryNormalizeReplayClockPosition(clock.Position, duration, out var position) &&
                IsReplayClockSampleAccepted(position, duration, observedAtUtc) &&
                TryAcceptReplayClockSample(
                    position,
                    duration,
                    sampledSeekOperationVersion,
                    sampleBeganDuringSeek,
                    observedAtUtc))
            {
                return new ReplayClockSnapshot(position, duration, isSeekable);
            }
        }

        return new ReplayClockSnapshot(EstimateReplayClockFromAnchor(duration, observedAtUtc), duration, isSeekable);
    }

    private static TimeSpan NormalizeReplayClockDuration(TimeSpan? mediaDuration, TimeSpan fallbackDuration)
    {
        if (mediaDuration is not { } duration ||
            duration <= TimeSpan.Zero ||
            duration > ReplayClockMaximumPlausibleDuration)
        {
            return fallbackDuration;
        }

        return duration > fallbackDuration ? duration : fallbackDuration;
    }

    private static bool TryNormalizeReplayClockPosition(TimeSpan position, TimeSpan duration, out TimeSpan normalizedPosition)
    {
        normalizedPosition = TimeSpan.Zero;
        if (position < TimeSpan.Zero ||
            position > SafeAdd(duration, ReplayClockSampleTolerance))
        {
            return false;
        }

        normalizedPosition = ClampReplayOffset(position, duration);
        return true;
    }

    private bool IsReplayClockSampleAccepted(TimeSpan position, TimeSpan duration, DateTimeOffset observedAtUtc)
    {
        var anchor = GetReplayClockAnchor();
        if (!anchor.HasValue)
        {
            return true;
        }

        var snapshot = anchor.Value;
        if (snapshot.AcceptedSampleAvailable &&
            snapshot.AcceptedSampleSeekGeneration == Volatile.Read(ref replaySeekOperationVersion))
        {
            var minimumPositionFromLastSample = snapshot.AcceptedSamplePosition - ReplayClockSampleTolerance;
            if (minimumPositionFromLastSample < TimeSpan.Zero)
            {
                minimumPositionFromLastSample = TimeSpan.Zero;
            }

            if (position < minimumPositionFromLastSample)
            {
                return false;
            }
        }

        var elapsed = observedAtUtc - snapshot.ObservedAtUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var expectedPosition = ClampReplayOffset(SafeAdd(snapshot.Offset, elapsed), duration);
        var lowerBound = expectedPosition - ReplayClockSampleTolerance;
        if (lowerBound < TimeSpan.Zero)
        {
            lowerBound = TimeSpan.Zero;
        }

        var upperBound = SafeAdd(expectedPosition, ReplayClockSampleTolerance);
        if (upperBound > duration)
        {
            upperBound = duration;
        }

        return position >= lowerBound && position <= upperBound;
    }

    private TimeSpan EstimateReplayClockFromAnchor(TimeSpan duration, DateTimeOffset observedAtUtc)
    {
        var anchor = GetReplayClockAnchor();
        if (anchor is null)
        {
            return ClampReplayOffset(TimeSpan.FromSeconds(ReplaySeekValue), duration);
        }

        var elapsed = Status == PlaybackStatus.Playing
            ? observedAtUtc - anchor.Value.ObservedAtUtc
            : TimeSpan.Zero;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return ClampReplayOffset(SafeAdd(anchor.Value.Offset, elapsed), duration);
    }

    private void SetReplayClockAnchor(
        TimeSpan offset,
        TimeSpan duration,
        long seekGeneration,
        bool awaitingSeekConfirmation,
        DateTimeOffset? observedAtUtc = null)
    {
        lock (replayClockAnchorGate)
        {
            replayClockAnchorAvailable = true;
            replayClockAnchorOffset = ClampReplayOffset(offset, duration);
            replayClockAnchorObservedAtUtc = observedAtUtc ?? DateTimeOffset.UtcNow;
            replayClockAnchorSeekGeneration = seekGeneration;
            replayClockAnchorAwaitingSeekConfirmation = awaitingSeekConfirmation;
        }
    }

    private bool TryAcceptReplayClockSample(
        TimeSpan position,
        TimeSpan duration,
        long seekGeneration,
        bool sampleBeganDuringSeek,
        DateTimeOffset observedAtUtc)
    {
        if (sampleBeganDuringSeek)
        {
            return false;
        }

        lock (replayClockAnchorGate)
        {
            // BeginReplaySeekOperation publishes the in-progress flag before advancing the
            // generation. Rechecking both while holding the anchor lock prevents a sample that
            // began before a seek from replacing the new seek anchor after it is reset.
            if (!IsReplayClockSampleCurrent(seekGeneration))
            {
                return false;
            }

            var normalizedPosition = ClampReplayOffset(position, duration);
            replayClockAcceptedSampleAvailable = true;
            replayClockAcceptedSamplePosition = normalizedPosition;
            replayClockAcceptedSampleObservedAtUtc = observedAtUtc;
            replayClockAcceptedSampleSeekGeneration = seekGeneration;

            if (!replayClockAnchorAvailable ||
                seekGeneration != replayClockAnchorSeekGeneration ||
                normalizedPosition > replayClockAnchorOffset)
            {
                replayClockAnchorAvailable = true;
                replayClockAnchorOffset = normalizedPosition;
                replayClockAnchorObservedAtUtc = observedAtUtc;
                replayClockAnchorSeekGeneration = seekGeneration;
            }

            replayClockAnchorAwaitingSeekConfirmation = false;
            return true;
        }
    }

    private ReplayClockAnchorSnapshot? GetReplayClockAnchor()
    {
        lock (replayClockAnchorGate)
        {
            if (!replayClockAnchorAvailable)
            {
                return null;
            }

            return new ReplayClockAnchorSnapshot(
                replayClockAnchorOffset,
                replayClockAnchorObservedAtUtc,
                replayClockAnchorSeekGeneration,
                replayClockAnchorAwaitingSeekConfirmation,
                replayClockAcceptedSampleAvailable,
                replayClockAcceptedSamplePosition,
                replayClockAcceptedSampleObservedAtUtc,
                replayClockAcceptedSampleSeekGeneration);
        }
    }

    private void ClearReplayClockAnchor()
    {
        lock (replayClockAnchorGate)
        {
            replayClockAnchorAvailable = false;
            replayClockAnchorOffset = TimeSpan.Zero;
            replayClockAnchorObservedAtUtc = DateTimeOffset.MinValue;
            replayClockAnchorSeekGeneration = 0;
            replayClockAnchorAwaitingSeekConfirmation = false;
            ResetReplayClockSampleTrackingCore();
        }
    }

    private void ResetReplayClockSampleTracking()
    {
        lock (replayClockAnchorGate)
        {
            ResetReplayClockSampleTrackingCore();
        }
    }

    private void ResetReplayClockSampleTrackingCore()
    {
        replayClockAcceptedSampleAvailable = false;
        replayClockAcceptedSamplePosition = TimeSpan.Zero;
        replayClockAcceptedSampleObservedAtUtc = DateTimeOffset.MinValue;
        replayClockAcceptedSampleSeekGeneration = 0;
    }

    private static TimeSpan SafeAdd(TimeSpan left, TimeSpan right)
    {
        if (right > TimeSpan.Zero && left > TimeSpan.MaxValue - right)
        {
            return TimeSpan.MaxValue;
        }

        if (right < TimeSpan.Zero && left < TimeSpan.MinValue - right)
        {
            return TimeSpan.MinValue;
        }

        return left + right;
    }

    private readonly record struct ReplayClockSnapshot(TimeSpan Position, TimeSpan Duration, bool IsSeekable);

    private readonly record struct ReplayClockUiUpdate(
        ReplayClockSnapshot Clock,
        long ReplaySeekOperationVersion);

    private readonly record struct ReplayClockAnchorSnapshot(
        TimeSpan Offset,
        DateTimeOffset ObservedAtUtc,
        long SeekGeneration,
        bool AwaitingSeekConfirmation,
        bool AcceptedSampleAvailable,
        TimeSpan AcceptedSamplePosition,
        DateTimeOffset AcceptedSampleObservedAtUtc,
        long AcceptedSampleSeekGeneration);

    private readonly record struct ReplayChatWindowUiUpdate(
        long StateVersion,
        ReplayChatWindowSelection Selection,
        bool Force,
        long? ReplaySeekOperationVersion);

    private readonly record struct ReplayChatLoadRequest(
        ReplaySessionInfo Replay,
        AppSettings Settings,
        TimeSpan Offset,
        bool NotifyUnavailable,
        long ReplayChatVersion,
        long? ReplaySeekOperationVersion);

    private readonly record struct ReplayPlaybackUrlKey(
        StreamTarget Target,
        string ReplayId,
        string Quality,
        string StreamlinkPath,
        string CustomArguments);

    private enum ReplayPlaybackUrlReadiness
    {
        None,
        Pending,
        Successful,
        Failed,
        Unnecessary
    }

    private sealed record ReplayPlaybackUrlResolution(
        ReplayPlaybackUrlKey Key,
        Task<StreamlinkResolvedUrl> Task,
        CancellationTokenSource Cancellation);

    private sealed record ReplayTransitionWork(
        string Description,
        Func<Task> ExecuteAsync,
        bool RunOnPlaybackFailure,
        bool RunBeforePlayback = false);

    private async Task LoadReplayChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        bool clearExisting,
        bool notifyUnavailable,
        long replayChatVersion,
        CancellationToken cancellationToken)
    {
        if (clearExisting)
        {
            replayChatVersion = ClearReplayChat();
        }

        if (!IsReplayChatLoadCurrent(replay, replayChatVersion))
        {
            return;
        }

        if (!CanLoadReplayChat(replay))
        {
            return;
        }

        if (ShouldUseCapturedReplayChat(replay))
        {
            RefreshCapturedReplayChat(offset, force: true);
            return;
        }

        if (replayChatProvider is null)
        {
            if (TryUseCapturedReplayChatFallback(replay, offset, replaceExisting: clearExisting || notifyUnavailable))
            {
                return;
            }

            QueueEmptyReplayChatWindowUiApplyIfNoReplayChatLoaded();
            if (notifyUnavailable)
            {
                AddReplayChatStatusMessage("Replay chat provider is not configured.");
            }

            return;
        }

        var loadStopwatch = Stopwatch.StartNew();
        logger.Write(
            AppLogLevel.Debug,
            "Replay",
            $"Replay chat load started for {Target.DisplayName} at {StreamViewModelHelpers.FormatClockTime(offset)} (VOD {replay.ReplayId}).");
        var result = await replayChatProvider.LoadChatAsync(replay, settings, offset, cancellationToken);
        loadStopwatch.Stop();
        logger.Write(
            result.IsAvailable ? AppLogLevel.Debug : AppLogLevel.Info,
            "Replay",
            $"Replay chat load completed for {Target.DisplayName} at {StreamViewModelHelpers.FormatClockTime(offset)} " +
            $"in {loadStopwatch.Elapsed.TotalMilliseconds:0} ms: available={result.IsAvailable.ToString().ToLowerInvariant()}, " +
            $"messages={result.Messages.Count}, coverage={FormatReplayChatCoverage(result)}.");
        if (!IsReplayChatLoadCurrent(replay, replayChatVersion))
        {
            logger.Write(
                AppLogLevel.Debug,
                "Replay",
                $"Discarded stale replay chat load for {Target.DisplayName} at {StreamViewModelHelpers.FormatClockTime(offset)} (VOD {replay.ReplayId}).");
            return;
        }

        if (!result.IsAvailable)
        {
            if (TryUseCapturedReplayChatFallback(replay, offset, replaceExisting: clearExisting || notifyUnavailable))
            {
                logger.Write(AppLogLevel.Info, "Replay", result.UnavailableReason);
                return;
            }

            if (notifyUnavailable)
            {
                QueueEmptyReplayChatWindowUiApplyIfNoReplayChatLoaded();
                AddReplayChatStatusMessage(result.UnavailableReason);
            }
            else
            {
                QueueEmptyReplayChatWindowUiApplyIfNoReplayChatLoaded();
                logger.Write(AppLogLevel.Info, "Replay", result.UnavailableReason);
            }

            return;
        }

        UpdateReplayChatRange(result);
        if (result.Messages.Count == 0 &&
            replayChatSelector.Count == 0)
        {
            QueueEmptyReplayChatWindowUiApply(clearNativeOverlayImmediately: true);
            return;
        }

        replayChatSelector.AddRange(result.Messages);
        UpdateReplayChatWindow(offset, force: true);
    }

    private static string FormatReplayChatCoverage(ReplayChatLoadResult result)
    {
        var from = result.LoadedFromOffset is { } loadedFrom
            ? StreamViewModelHelpers.FormatClockTime(loadedFrom)
            : "(none)";
        var through = result.LoadedThroughOffset is { } loadedThrough
            ? StreamViewModelHelpers.FormatClockTime(loadedThrough)
            : "(none)";
        return $"{from}-{through}";
    }

    private long ClearReplayChat()
    {
        var replayChatVersion = ResetReplayChatState();
        QueueEmptyReplayChatWindowUiApply(clearNativeOverlayImmediately: false);
        dispatch(() =>
        {
            activeTwitchPredictionFeedItem = null;
            StopTwitchPredictionClock();
        });

        return replayChatVersion;
    }

    private void QueueEmptyReplayChatWindowUiApplyIfNoReplayChatLoaded()
    {
        if (replayChatSelector.Count == 0)
        {
            QueueEmptyReplayChatWindowUiApply(clearNativeOverlayImmediately: true);
        }
    }

    private void QueueEmptyReplayChatWindowUiApply(bool clearNativeOverlayImmediately)
    {
        nativeReplayOverlayRenderState.InvalidateFrameKey();
        if (clearNativeOverlayImmediately)
        {
            nativeReplayOverlayFrameWriteGate.Invalidate();
            nativeReplayOverlayFrameScheduler?.CancelPending();
            CancelNativeReplayOverlayAnimationState();
        }

        QueueReplayChatWindowUiApply(
            new ReplayChatWindowSelection([], ReplayChatWindowKey.Empty),
            force: true);
        if (clearNativeOverlayImmediately)
        {
            ClearNativeReplayOverlayForEmptyReplayWindowInBackground();
        }
    }

    private long ResetReplayChatState()
    {
        var replayChatVersion = InvalidateReplayChatState();
        replayChatSelector.Clear();
        replayChatLoadedFrom = null;
        replayChatLoadedThrough = null;
        lastReplayChatOffset = TimeSpan.MinValue;
        ResetKickSeekbackReplayChatBacklog();
        ResetReplayChatVisibleWindowCache();
        return replayChatVersion;
    }

    private long InvalidateReplayChatState()
    {
        CancelReplayChatLoad();
        return Interlocked.Increment(ref replayChatStateVersion);
    }

    private long GetReplayChatStateVersion()
    {
        return Volatile.Read(ref replayChatStateVersion);
    }

    private bool IsReplayChatLoadCurrent(ReplaySessionInfo replay, long replayChatVersion)
    {
        return replayChatVersion == GetReplayChatStateVersion() &&
            IsCurrentReplaySession(replay);
    }

    private bool IsReplayChatLoadCurrent(ReplayChatLoadRequest request)
    {
        return IsReplayChatLoadCurrent(request.Replay, request.ReplayChatVersion) &&
            (request.ReplaySeekOperationVersion is not { } expectedVersion ||
                IsReplayClockSampleCurrent(expectedVersion));
    }

    private bool IsCurrentReplaySession(ReplaySessionInfo replay)
    {
        return replaySession is { IsAvailable: true } currentReplay &&
            currentReplay.Platform == replay.Platform &&
            string.Equals(currentReplay.Channel, replay.Channel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentReplay.ReplayId, replay.ReplayId, StringComparison.Ordinal);
    }

    private bool CanSeekCurrentReplayInPlace(ReplaySessionInfo replay)
    {
        if (!IsCurrentReplaySession(replay))
        {
            return false;
        }

        if (Target.IsExplicitVod)
        {
            return streamSession is null && isDirectExplicitVodReplayPlayback;
        }

        if ((!IsReplayMode && !IsBehindLive) ||
            streamSession is not null ||
            currentSettings is null ||
            currentReplayPlaybackKey is not { } replayPlaybackKey)
        {
            return false;
        }

        return replayPlaybackKey.Equals(CreateReplayPlaybackUrlKey(replay, currentSettings));
    }

    private void QueueReplayChatLoadIfNeeded(
        TimeSpan offset,
        long? sampledSeekOperationVersion = null)
    {
        if (replaySession is not { IsAvailable: true } replay ||
            currentSettings is null ||
            (sampledSeekOperationVersion is { } expectedVersion &&
                !IsReplayClockSampleCurrent(expectedVersion)) ||
            !CanLoadReplayChat(replay))
        {
            return;
        }

        if (ShouldUseCapturedReplayChat(replay))
        {
            RefreshCapturedReplayChat(
                offset,
                force: false,
                expectedReplaySeekOperationVersion: sampledSeekOperationVersion);
            if (NeedsCapturedReplayChatBackfill(replay, currentSettings, offset))
            {
                QueueReplayChatLoad(
                    replay,
                    currentSettings,
                    offset,
                    notifyUnavailable: false,
                    GetReplayChatStateVersion(),
                    sampledSeekOperationVersion);
            }
            return;
        }

        if (replayChatProvider is null ||
            !ShouldLoadReplayChatForOffset(offset))
        {
            return;
        }

        QueueReplayChatLoad(
            replay,
            currentSettings,
            offset,
            notifyUnavailable: false,
            GetReplayChatStateVersion(),
            sampledSeekOperationVersion);
    }

    private void QueueReplayChatLoad(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        bool notifyUnavailable,
        long replayChatVersion,
        long? expectedReplaySeekOperationVersion = null)
    {
        if (!IsReplayChatLoadCurrent(replay, replayChatVersion) ||
            (expectedReplaySeekOperationVersion is { } expectedVersion &&
                !IsReplayClockSampleCurrent(expectedVersion)))
        {
            return;
        }

        if (!CanLoadReplayChat(replay))
        {
            return;
        }

        var useCapturedReplayChat = ShouldUseCapturedReplayChat(replay);
        if (useCapturedReplayChat)
        {
            var needsCapturedBackfill = NeedsCapturedReplayChatBackfill(replay, settings, offset);
            RefreshCapturedReplayChat(
                offset,
                force: notifyUnavailable,
                suppressUnavailableNotice: needsCapturedBackfill ||
                    HasCapturedReplayChatBackfillCoverage(offset));
            if (!needsCapturedBackfill)
            {
                return;
            }
        }

        if (!useCapturedReplayChat &&
            !notifyUnavailable &&
            replayChatProvider is null)
        {
            return;
        }

        var request = new ReplayChatLoadRequest(
            replay,
            settings,
            offset,
            notifyUnavailable,
            replayChatVersion,
            expectedReplaySeekOperationVersion);
        if (useCapturedReplayChat)
        {
            QueueCapturedReplayChatLoad(request);
            return;
        }

        lock (replayChatLoadGate)
        {
            if (replayChatLoadTask is { IsCompleted: false })
            {
                return;
            }

            StartReplayChatLoadCore(request, useCapturedReplayChat: false);
        }
    }

    private void QueueCapturedReplayChatLoad(ReplayChatLoadRequest request)
    {
        CancellationTokenSource? staleCancellation = null;
        lock (replayChatLoadGate)
        {
            if (replayChatLoadTask is { IsCompleted: false })
            {
                if (activeCapturedReplayChatLoadRequest is not { } activeRequest)
                {
                    logger.Write(
                        AppLogLevel.Debug,
                        "Replay",
                        $"Skipped Kick captured replay chat load at {StreamViewModelHelpers.FormatClockTime(request.Offset)} because another replay chat load is already running.");
                    return;
                }

                pendingCapturedReplayChatLoadRequest = request;
                if (CapturedReplayChatLoadWindowsOverlap(activeRequest.Offset, request.Offset))
                {
                    logger.Write(
                        AppLogLevel.Debug,
                        "Replay",
                        $"Coalesced Kick captured replay chat load at {StreamViewModelHelpers.FormatClockTime(request.Offset)} behind active {StreamViewModelHelpers.FormatClockTime(activeRequest.Offset)}.");
                    return;
                }

                logger.Write(
                    AppLogLevel.Debug,
                    "Replay",
                    $"Canceling stale Kick captured replay chat load at {StreamViewModelHelpers.FormatClockTime(activeRequest.Offset)} for newer {StreamViewModelHelpers.FormatClockTime(request.Offset)}.");
                staleCancellation = replayChatLoadCancellation;
                replayChatLoadCancellation = null;
                replayChatLoadTask = null;
                activeCapturedReplayChatLoadRequest = null;
                pendingCapturedReplayChatLoadRequest = null;
            }

            StartReplayChatLoadCore(request, useCapturedReplayChat: true);
        }

        CancelCancellationSource(staleCancellation);
    }

    private void StartReplayChatLoadCore(ReplayChatLoadRequest request, bool useCapturedReplayChat)
    {
        var cancellation = new CancellationTokenSource();
        replayChatLoadCancellation = cancellation;
        activeCapturedReplayChatLoadRequest = useCapturedReplayChat ? request : null;
        replayChatLoadTask = Task.Run(async () =>
        {
            try
            {
                if (!IsReplayChatLoadCurrent(request))
                {
                    return;
                }

                if (useCapturedReplayChat)
                {
                    await LoadCapturedReplayChatAsync(
                        request.Replay,
                        request.Settings,
                        request.Offset,
                        request.NotifyUnavailable,
                        request.ReplayChatVersion,
                        cancellation.Token);
                }
                else
                {
                    await LoadReplayChatAsync(
                        request.Replay,
                        request.Settings,
                        request.Offset,
                        clearExisting: false,
                        request.NotifyUnavailable,
                        request.ReplayChatVersion,
                        cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "Replay", $"Replay chat load failed for {Target.DisplayName}.", ex);
            }
            finally
            {
                ReplayChatLoadRequest? pendingRequest = null;
                lock (replayChatLoadGate)
                {
                    if (ReferenceEquals(replayChatLoadCancellation, cancellation))
                    {
                        replayChatLoadCancellation = null;
                        replayChatLoadTask = null;
                        if (useCapturedReplayChat)
                        {
                            activeCapturedReplayChatLoadRequest = null;
                            pendingRequest = pendingCapturedReplayChatLoadRequest;
                            pendingCapturedReplayChatLoadRequest = null;
                        }
                    }
                }

                cancellation.Dispose();
                if (pendingRequest is { } capturedRequest)
                {
                    QueuePendingCapturedReplayChatLoad(capturedRequest);
                }
            }
        });
    }

    private void QueuePendingCapturedReplayChatLoad(ReplayChatLoadRequest request)
    {
        if (!IsReplayChatLoadCurrent(request))
        {
            logger.Write(
                AppLogLevel.Debug,
                "Replay",
                $"Skipped pending Kick captured replay chat load at {StreamViewModelHelpers.FormatClockTime(request.Offset)} because the replay session changed.");
            return;
        }

        if (!NeedsCapturedReplayChatBackfill(request.Replay, request.Settings, request.Offset))
        {
            logger.Write(
                AppLogLevel.Debug,
                "Replay",
                $"Skipped pending Kick captured replay chat load at {StreamViewModelHelpers.FormatClockTime(request.Offset)} because the window is already covered.");
            RefreshCapturedReplayChat(request.Offset, force: request.NotifyUnavailable);
            return;
        }

        logger.Write(
            AppLogLevel.Debug,
            "Replay",
            $"Restarting Kick captured replay chat load at {StreamViewModelHelpers.FormatClockTime(request.Offset)} after active load completed.");
        QueueReplayChatLoad(
            request.Replay,
            request.Settings,
            request.Offset,
            request.NotifyUnavailable,
            request.ReplayChatVersion,
            request.ReplaySeekOperationVersion);
    }

    private static bool CapturedReplayChatLoadWindowsOverlap(TimeSpan firstOffset, TimeSpan secondOffset)
    {
        var firstFrom = GetReplayChatWindowStart(firstOffset);
        var firstThrough = SafeAdd(firstOffset, ReplayChatPrefetchThreshold);
        var secondFrom = GetReplayChatWindowStart(secondOffset);
        var secondThrough = SafeAdd(secondOffset, ReplayChatPrefetchThreshold);
        return firstFrom <= secondThrough && secondFrom <= firstThrough;
    }

    private bool NeedsCapturedReplayChatBackfill(
        ReplaySessionInfo replay,
        AppSettings? settings,
        TimeSpan offset)
    {
        var canUseChatClientBackfill = chatClient is IChatHistoryBackfillClient ||
            (settings is not null && ShouldCaptureReplayChat(settings));
        if (replay.Platform != PlatformKind.Kick ||
            (!canUseChatClientBackfill && kickChatHistoryProvider is null) ||
            replay.StreamStartedAtUtc is null)
        {
            return false;
        }

        lock (capturedReplayChatGate)
        {
            var windowStart = GetReplayChatWindowStart(offset);
            if (IsCapturedReplayChatBackfillCoverageCurrent(windowStart, offset))
            {
                return false;
            }

            return true;
        }
    }

    private async Task LoadCapturedReplayChatAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        bool notifyUnavailable,
        long replayChatVersion,
        CancellationToken cancellationToken)
    {
        if (!IsReplayChatLoadCurrent(replay, replayChatVersion))
        {
            return;
        }

        var capturedCountBeforeBackfill = GetCapturedReplayChatCount();
        var backfillResult = await BackfillCapturedReplayChatForOffsetAsync(
                replay,
                settings,
                offset,
                visibleRangeOnly: notifyUnavailable,
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsReplayChatLoadCurrent(replay, replayChatVersion))
        {
            return;
        }

        AddCapturedReplayChatBackfillMessages(replay, backfillResult.Messages);
        var diagnostics = GetCapturedReplayChatDiagnostics(offset);
        LogCapturedReplayChatBackfillDiagnostics(
            replay,
            offset,
            backfillResult,
            capturedCountBeforeBackfill,
            diagnostics.CapturedCount,
            diagnostics.VisibleCount);
        MarkCapturedReplayChatBackfillCoverage(replay, backfillResult);
        var forceRefresh = backfillResult.LoadedMessageCount > 0 ||
            (notifyUnavailable &&
                !backfillResult.CoveredRequestedRange &&
                ShouldShowCapturedReplayChatNotice(offset));
        RefreshCapturedReplayChat(
            offset,
            force: forceRefresh,
            suppressUnavailableNotice: backfillResult.CoveredRequestedRange);
    }

    private async Task<ChatHistoryBackfillResult> BackfillCapturedReplayChatForOffsetAsync(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        bool visibleRangeOnly,
        CancellationToken cancellationToken)
    {
        if (replay.Platform != PlatformKind.Kick ||
            replay.StreamStartedAtUtc is not { } startedAt)
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        if (chatClient is not IChatHistoryBackfillClient &&
            ShouldCaptureReplayChat(settings))
        {
            await EnsureChatClientConnectedAsync(cancellationToken).ConfigureAwait(false);
        }

        var windowStart = GetReplayChatWindowStart(offset);
        var throughOffset = visibleRangeOnly
            ? offset
            : SafeAdd(offset, ReplayChatPrefetchThreshold);

        if (!TryGetReplayTimestamp(startedAt, windowStart, out var fromTimestampUtc) ||
            !TryGetReplayTimestamp(startedAt, throughOffset, out var throughTimestampUtc))
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        if (chatClient is IChatHistoryBackfillClient backfillClient)
        {
            var result = await backfillClient
                .BackfillRecentChatRangeAsync(fromTimestampUtc, throughTimestampUtc, cancellationToken)
                .ConfigureAwait(false);
            if (IsBackfillResultUsable(result) || kickChatHistoryProvider is null)
            {
                return FilterBackfillResultMessagesToRange(result, fromTimestampUtc, throughTimestampUtc);
            }
        }

        if (kickChatHistoryProvider is null)
        {
            return new ChatHistoryBackfillResult(false, 0, false, null, null);
        }

        var providerResult = await kickChatHistoryProvider
            .BackfillRecentChatRangeAsync(Target, settings.Chat, fromTimestampUtc, throughTimestampUtc, cancellationToken)
            .ConfigureAwait(false);
        return FilterBackfillResultMessagesToRange(providerResult, fromTimestampUtc, throughTimestampUtc);
    }

    private static bool IsBackfillResultUsable(ChatHistoryBackfillResult result)
    {
        return result.Attempted ||
            result.LoadedMessageCount > 0 ||
            result.CoveredRequestedRange ||
            result.CoveredFromTimestampUtc is not null ||
            result.CoveredThroughTimestampUtc is not null;
    }

    private static ChatHistoryBackfillResult FilterBackfillResultMessagesToRange(
        ChatHistoryBackfillResult result,
        DateTimeOffset fromTimestampUtc,
        DateTimeOffset throughTimestampUtc)
    {
        if (result.Messages.Count == 0)
        {
            return result;
        }

        fromTimestampUtc = fromTimestampUtc.ToUniversalTime();
        throughTimestampUtc = throughTimestampUtc.ToUniversalTime();
        var filteredMessages = result.Messages
            .Where(message =>
            {
                var timestampUtc = message.Timestamp.ToUniversalTime();
                return timestampUtc >= fromTimestampUtc && timestampUtc <= throughTimestampUtc;
            })
            .ToArray();
        return filteredMessages.Length == result.Messages.Count
            ? result
            : result with
            {
                LoadedMessageCount = filteredMessages.Length,
                Messages = filteredMessages
            };
    }

    private int AddCapturedReplayChatBackfillMessages(
        ReplaySessionInfo replay,
        IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return 0;
        }

        var replayMessages = new List<ReplayChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            RememberCapturedReplayChatSourceMessage(message);
            if (TryBuildCapturedReplayChatMessage(replay, message, out var replayMessage))
            {
                replayMessages.Add(replayMessage);
            }
        }

        if (replayMessages.Count == 0)
        {
            return 0;
        }

        var added = 0;
        lock (capturedReplayChatGate)
        {
            foreach (var replayMessage in replayMessages)
            {
                if (capturedReplayChatSelector.Add(
                    replayMessage,
                    MaxCapturedReplayChatMessages,
                    out var evicted))
                {
                    added++;
                }

                if (evicted)
                {
                    capturedReplayChatEvictedMessages = true;
                }
            }
        }

        return added;
    }

    private int GetCapturedReplayChatCount()
    {
        lock (capturedReplayChatGate)
        {
            return capturedReplayChatSelector.Count;
        }
    }

    private (int CapturedCount, int VisibleCount) GetCapturedReplayChatDiagnostics(TimeSpan offset)
    {
        lock (capturedReplayChatGate)
        {
            return (
                capturedReplayChatSelector.Count,
                SelectReplayChatMessages(offset, capturedReplayChatSelector).Messages.Count);
        }
    }

    private void LogCapturedReplayChatBackfillDiagnostics(
        ReplaySessionInfo replay,
        TimeSpan offset,
        ChatHistoryBackfillResult result,
        int capturedCountBeforeBackfill,
        int capturedCount,
        int visibleCount)
    {
        if (replay.Platform != PlatformKind.Kick)
        {
            return;
        }

        logger.Write(
            AppLogLevel.Debug,
            "Replay",
            $"Kick seekback replay chat backfill at {StreamViewModelHelpers.FormatClockTime(offset)} loaded={result.LoadedMessageCount.ToString(CultureInfo.InvariantCulture)}, " +
            $"covered={result.CoveredRequestedRange}, coveredRange={FormatReplayBackfillTimestamp(result.CoveredFromTimestampUtc)} through {FormatReplayBackfillTimestamp(result.CoveredThroughTimestampUtc)}, " +
            $"captured={capturedCount.ToString(CultureInfo.InvariantCulture)}, visible={visibleCount.ToString(CultureInfo.InvariantCulture)}.");

        if (result.LoadedMessageCount <= 0 || capturedCount > capturedCountBeforeBackfill)
        {
            return;
        }

        var rejectionReasons = GetCapturedReplayChatRejectionReasons(replay, sampleCount: 5);
        var reasonText = rejectionReasons.Count == 0
            ? "messages were duplicates or outside the current replay selector state"
            : string.Join("; ", rejectionReasons);
        logger.Write(
            AppLogLevel.Debug,
            "Replay",
            $"Kick seekback loaded messages but none were newly accepted into captured replay chat: {reasonText}.");
    }

    private IReadOnlyList<string> GetCapturedReplayChatRejectionReasons(ReplaySessionInfo replay, int sampleCount)
    {
        ChatMessage[] sourceMessages;
        lock (capturedReplayChatGate)
        {
            sourceMessages = capturedReplayChatSourceMessages.TakeLast(Math.Max(1, sampleCount * 4)).ToArray();
        }

        var reasons = new List<string>(sampleCount);
        foreach (var message in sourceMessages.Reverse())
        {
            var reason = GetCapturedReplayChatRejectionReason(replay, message);
            if (string.IsNullOrWhiteSpace(reason))
            {
                continue;
            }

            reasons.Add(reason);
            if (reasons.Count >= sampleCount)
            {
                break;
            }
        }

        return reasons;
    }

    private string? GetCapturedReplayChatRejectionReason(ReplaySessionInfo replay, ChatMessage message)
    {
        if (!ShouldUseCapturedReplayChat(replay))
        {
            return "captured replay chat is disabled for this replay";
        }

        if (replay.StreamStartedAtUtc is not { } startedAt)
        {
            return "replay stream start time is missing";
        }

        if (message.Platform != replay.Platform)
        {
            return $"platform mismatch {message.Platform} != {replay.Platform}";
        }

        if (!string.Equals(message.Channel, Target.Channel, StringComparison.OrdinalIgnoreCase))
        {
            return $"channel mismatch {message.Channel} != {Target.Channel}";
        }

        var messageOffset = message.Timestamp.ToUniversalTime() - startedAt;
        if (messageOffset < TimeSpan.Zero)
        {
            return $"message before replay start at {StreamViewModelHelpers.FormatClockTime(messageOffset)}";
        }

        var duration = GetCurrentReplayDuration(replay);
        if (messageOffset > SafeAdd(duration, ReplayLiveEdgeThreshold))
        {
            return $"message after replay duration at {StreamViewModelHelpers.FormatClockTime(messageOffset)}";
        }

        return null;
    }

    private static string FormatReplayBackfillTimestamp(DateTimeOffset? timestampUtc)
    {
        return timestampUtc is { } timestamp
            ? timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "none";
    }

    private static TimeSpan GetReplayChatWindowStart(TimeSpan offset)
    {
        var windowStart = offset - ReplayChatWindow;
        return windowStart < TimeSpan.Zero ? TimeSpan.Zero : windowStart;
    }

    private void BeginKickSeekbackReplayChatBacklog(ReplaySessionInfo replay, TimeSpan offset)
    {
        ResetKickSeekbackReplayChatBacklog();
        if (!ShouldUseKickSeekbackReplayChatBacklog(replay))
        {
            return;
        }

        kickSeekbackReplayChatBacklogStart = GetReplayChatWindowStart(offset);
        kickSeekbackReplayChatBacklogReplayId = replay.ReplayId;
    }

    private void ResetKickSeekbackReplayChatBacklog()
    {
        kickSeekbackReplayChatBacklogStart = null;
        kickSeekbackReplayChatBacklogReplayId = "";
    }

    private bool TryGetKickSeekbackReplayChatBacklogStart(ReplaySessionInfo replay, out TimeSpan startOffset)
    {
        if (ShouldUseKickSeekbackReplayChatBacklog(replay) &&
            kickSeekbackReplayChatBacklogStart is { } backlogStart &&
            string.Equals(kickSeekbackReplayChatBacklogReplayId, replay.ReplayId, StringComparison.Ordinal))
        {
            startOffset = backlogStart;
            return true;
        }

        startOffset = TimeSpan.Zero;
        return false;
    }

    private bool ShouldUseKickSeekbackReplayChatBacklog(ReplaySessionInfo replay)
    {
        return Target.Kind == StreamTargetKind.Live &&
            !Target.IsExplicitVod &&
            replay.Platform == PlatformKind.Kick &&
            replay.StreamStartedAtUtc is not null &&
            ShouldUseCapturedReplayChat(replay);
    }

    private bool IsCapturedReplayChatBackfillCoverageCurrent(TimeSpan windowStart, TimeSpan offset)
    {
        foreach (var range in capturedReplayChatBackfillCoverageRanges)
        {
            if (range.From <= windowStart && range.Through >= offset)
            {
                return true;
            }
        }

        return false;
    }

    private bool HasCapturedReplayChatBackfillCoverage(TimeSpan offset)
    {
        lock (capturedReplayChatGate)
        {
            return IsCapturedReplayChatBackfillCoverageCurrent(GetReplayChatWindowStart(offset), offset);
        }
    }

    private void MarkCapturedReplayChatBackfillCoverage(ReplaySessionInfo replay, ChatHistoryBackfillResult result)
    {
        if (replay.StreamStartedAtUtc is not { } startedAt ||
            result.CoveredFromTimestampUtc is not { } coveredFromTimestampUtc ||
            result.CoveredThroughTimestampUtc is not { } coveredThroughTimestampUtc)
        {
            return;
        }

        var startedAtUtc = startedAt.ToUniversalTime();
        TimeSpan from;
        TimeSpan through;
        try
        {
            from = coveredFromTimestampUtc.ToUniversalTime() - startedAtUtc;
            through = coveredThroughTimestampUtc.ToUniversalTime() - startedAtUtc;
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        if (through < TimeSpan.Zero)
        {
            return;
        }

        if (from < TimeSpan.Zero)
        {
            from = TimeSpan.Zero;
        }

        lock (capturedReplayChatGate)
        {
            AddCapturedReplayChatBackfillCoverageCore(from, through);
        }
    }

    private void AddCapturedReplayChatBackfillCoverageCore(TimeSpan from, TimeSpan through)
    {
        if (through < from)
        {
            through = from;
        }

        for (var index = capturedReplayChatBackfillCoverageRanges.Count - 1; index >= 0; index--)
        {
            var existing = capturedReplayChatBackfillCoverageRanges[index];
            if (existing.Through < from || existing.From > through)
            {
                continue;
            }

            from = existing.From < from ? existing.From : from;
            through = existing.Through > through ? existing.Through : through;
            capturedReplayChatBackfillCoverageRanges.RemoveAt(index);
        }

        var insertIndex = capturedReplayChatBackfillCoverageRanges.FindIndex(range => range.From > from);
        if (insertIndex < 0)
        {
            capturedReplayChatBackfillCoverageRanges.Add(new ReplayChatBackfillCoverageRange(from, through));
        }
        else
        {
            capturedReplayChatBackfillCoverageRanges.Insert(insertIndex, new ReplayChatBackfillCoverageRange(from, through));
        }
    }

    private void ResetCapturedReplayChatBackfillCoverageCore()
    {
        capturedReplayChatBackfillCoverageRanges.Clear();
    }

    private bool ShouldLoadReplayChatForOffset(TimeSpan offset)
    {
        if (replayChatLoadedFrom is null || replayChatLoadedThrough is null)
        {
            return true;
        }

        return offset < replayChatLoadedFrom.Value ||
            offset >= replayChatLoadedThrough.Value - ReplayChatPrefetchThreshold;
    }

    private void CancelReplayChatLoad()
    {
        CancellationTokenSource? cancellation = null;
        lock (replayChatLoadGate)
        {
            cancellation = replayChatLoadCancellation;
            replayChatLoadCancellation = null;
            replayChatLoadTask = null;
            activeCapturedReplayChatLoadRequest = null;
            pendingCapturedReplayChatLoadRequest = null;
        }

        CancelCancellationSource(cancellation);
    }

    private static void CancelCancellationSource(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void PrepareCapturedReplayChat(ReplaySessionInfo replay)
    {
        if (!ShouldUseCapturedReplayChat(replay))
        {
            return;
        }

        lock (capturedReplayChatGate)
        {
            if (string.Equals(capturedReplayChatReplayId, replay.ReplayId, StringComparison.Ordinal))
            {
                capturedReplayChatStreamStartedAtUtc ??= replay.StreamStartedAtUtc;
            }
            else
            {
                capturedReplayChatReplayId = replay.ReplayId;
                capturedReplayChatStreamStartedAtUtc = replay.StreamStartedAtUtc;
                ResetKickSeekbackReplayChatBacklog();
                capturedReplayChatSelector.Clear();
                ResetCapturedReplayChatBackfillCoverageCore();
                capturedReplayChatEvictedMessages = false;
                capturedReplayChatNoticeShown = false;
            }
        }

        CaptureBufferedReplayChatMessages(replay);
    }

    private ReplayChatMessage? TryCaptureReplayChatMessage(ChatMessage message)
    {
        RememberCapturedReplayChatSourceMessage(message);

        if (replaySession is not { IsAvailable: true } replay ||
            !TryBuildCapturedReplayChatMessage(replay, message, out var replayMessage))
        {
            return null;
        }

        lock (capturedReplayChatGate)
        {
            if (!string.Equals(capturedReplayChatReplayId, replay.ReplayId, StringComparison.Ordinal))
            {
                capturedReplayChatReplayId = replay.ReplayId;
                capturedReplayChatStreamStartedAtUtc = replay.StreamStartedAtUtc;
                ResetKickSeekbackReplayChatBacklog();
                capturedReplayChatSelector.Clear();
                ResetCapturedReplayChatBackfillCoverageCore();
                capturedReplayChatEvictedMessages = false;
                capturedReplayChatNoticeShown = false;
                ResetReplayChatVisibleWindowCache();
            }

            capturedReplayChatSelector.Add(
                replayMessage,
                MaxCapturedReplayChatMessages,
                out var evicted);
            if (evicted)
            {
                capturedReplayChatEvictedMessages = true;
            }
        }

        return replayMessage;
    }

    private void RememberCapturedReplayChatSourceMessage(ChatMessage message)
    {
        if (Target.Platform is not (PlatformKind.Twitch or PlatformKind.Kick) ||
            message.Platform != Target.Platform ||
            !string.Equals(message.Channel, Target.Channel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (capturedReplayChatGate)
        {
            capturedReplayChatSourceMessages.Add(message);
            if (capturedReplayChatSourceMessages.Count > MaxCapturedReplayChatMessages)
            {
                capturedReplayChatSourceMessages.RemoveRange(
                    0,
                    capturedReplayChatSourceMessages.Count - MaxCapturedReplayChatMessages);
            }
        }
    }

    private void CaptureBufferedReplayChatMessages(ReplaySessionInfo replay)
    {
        ChatMessage[] sourceMessages;
        lock (capturedReplayChatGate)
        {
            sourceMessages = capturedReplayChatSourceMessages.ToArray();
        }

        foreach (var message in sourceMessages)
        {
            if (!TryBuildCapturedReplayChatMessage(replay, message, out var replayMessage))
            {
                continue;
            }

            lock (capturedReplayChatGate)
            {
                capturedReplayChatSelector.Add(
                    replayMessage,
                    MaxCapturedReplayChatMessages,
                    out var evicted);
                if (evicted)
                {
                    capturedReplayChatEvictedMessages = true;
                }
            }
        }
    }

    private bool TryBuildCapturedReplayChatMessage(
        ReplaySessionInfo replay,
        ChatMessage message,
        out ReplayChatMessage replayMessage)
    {
        replayMessage = default!;
        if (!ShouldUseCapturedReplayChat(replay) ||
            replay.StreamStartedAtUtc is not { } startedAt ||
            message.Platform != replay.Platform ||
            !string.Equals(message.Channel, Target.Channel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var offset = message.Timestamp.ToUniversalTime() - startedAt;
        if (offset < TimeSpan.Zero)
        {
            return false;
        }

        var duration = GetCurrentReplayDuration(replay);
        if (offset > SafeAdd(duration, ReplayLiveEdgeThreshold))
        {
            return false;
        }

        replayMessage = new ReplayChatMessage(offset, message);
        return true;
    }

    private void RefreshCapturedReplayChat(
        TimeSpan offset,
        bool force,
        bool suppressUnavailableNotice = false,
        long? expectedReplaySeekOperationVersion = null)
    {
        if (expectedReplaySeekOperationVersion is { } expectedVersion &&
            !IsReplayClockSampleCurrent(expectedVersion))
        {
            return;
        }

        lock (capturedReplayChatGate)
        {
            SetReplayChatRangeFromSelector(capturedReplayChatSelector);
            UpdateReplayChatWindowCore(
                offset,
                force,
                capturedReplayChatSelector,
                expectedReplaySeekOperationVersion);
        }

        if (force &&
            !suppressUnavailableNotice &&
            !ShouldSuppressCapturedReplayChatNotice() &&
            !HasCapturedReplayChatBackfillCoverage(offset))
        {
            var notice = TryBuildCapturedReplayChatNotice(offset);
            if (!string.IsNullOrWhiteSpace(notice))
            {
                AddSystemMessage(notice);
            }
        }
    }

    private bool HasCapturedReplayChatWindowMessages(TimeSpan offset)
    {
        lock (capturedReplayChatGate)
        {
            return SelectReplayChatMessages(offset, capturedReplayChatSelector).Messages.Count > 0;
        }
    }

    private bool TryUseCapturedReplayChatFallback(ReplaySessionInfo replay, TimeSpan offset, bool replaceExisting)
    {
        if (!CanUseCapturedReplayChatFallback(replay))
        {
            return false;
        }

        var copied = 0;
        lock (capturedReplayChatGate)
        {
            if (capturedReplayChatSelector.Count == 0)
            {
                return false;
            }

            copied = capturedReplayChatSelector.CopyTo(replayChatSelector, replaceExisting);
        }

        SetReplayChatRangeFromSelector(replayChatSelector);
        UpdateReplayChatWindow(offset, force: true);
        return copied > 0 || replayChatSelector.Count > 0;
    }

    private bool CanUseCapturedReplayChatFallback(ReplaySessionInfo replay)
    {
        if (Target.IsExplicitVod ||
            replay.Platform != Target.Platform ||
            ShouldUseCapturedReplayChat(replay) ||
            replay.StreamStartedAtUtc is not { } replayStartedAt)
        {
            return false;
        }

        lock (capturedReplayChatGate)
        {
            return capturedReplayChatStreamStartedAtUtc is { } capturedStartedAt &&
                (capturedStartedAt - replayStartedAt).Duration() <= TimeSpan.FromMinutes(30);
        }
    }

    private string? TryBuildCapturedReplayChatNotice(TimeSpan offset)
    {
        if (ShouldSuppressCapturedReplayChatNotice())
        {
            return null;
        }

        lock (capturedReplayChatGate)
        {
            if (capturedReplayChatNoticeShown)
            {
                return null;
            }

            var noticePrefix = GetCapturedReplayChatNoticePrefix();
            if (capturedReplayChatSelector.Count == 0)
            {
                capturedReplayChatNoticeShown = true;
                return $"{noticePrefix} only includes messages captured while this tab is connected; no chat has been captured yet.";
            }

            var firstOffset = capturedReplayChatSelector.FirstOffset ?? TimeSpan.Zero;
            if (offset >= firstOffset)
            {
                return null;
            }

            capturedReplayChatNoticeShown = true;
            return capturedReplayChatEvictedMessages
                ? $"{noticePrefix} before {StreamViewModelHelpers.FormatClockTime(firstOffset)} is no longer retained in this tab."
                : $"{noticePrefix} before {StreamViewModelHelpers.FormatClockTime(firstOffset)} was not captured by this tab.";
        }
    }

    private bool ShouldShowCapturedReplayChatNotice(TimeSpan offset)
    {
        if (ShouldSuppressCapturedReplayChatNotice())
        {
            return false;
        }

        lock (capturedReplayChatGate)
        {
            if (capturedReplayChatNoticeShown)
            {
                return false;
            }

            if (capturedReplayChatSelector.Count == 0)
            {
                return true;
            }

            var firstOffset = capturedReplayChatSelector.FirstOffset ?? TimeSpan.Zero;
            return offset < firstOffset;
        }
    }

    private void SetReplayChatRangeFromSelector(ReplayChatWindowSelector selector)
    {
        replayChatLoadedFrom = selector.FirstOffset;
        replayChatLoadedThrough = selector.LastOffset;
    }

    private string GetCapturedReplayChatNoticePrefix()
    {
        if (replaySession is { IsAvailable: true } replay)
        {
            return replay.Platform switch
            {
                PlatformKind.Kick => "Kick seekback chat",
                PlatformKind.Twitch when IsCurrentLiveDvrReplay(replay) => "Current-live DVR chat",
                _ => "Replay chat"
            };
        }

        return "Replay chat";
    }

    private bool ShouldSuppressCapturedReplayChatNotice()
    {
        return replaySession is { IsAvailable: true } replay &&
            IsCurrentLiveDvrReplay(replay);
    }

    private bool ShouldCaptureReplayChat(AppSettings settings)
    {
        return Target.Platform is PlatformKind.Twitch or PlatformKind.Kick &&
            settings.Chat.ConnectAutomatically &&
            IsChatVisible &&
            replaySession is { IsAvailable: true } replay &&
            replay.StreamStartedAtUtc is not null &&
            replay.Platform == Target.Platform &&
            ShouldUseCapturedReplayChat(replay);
    }

    private bool ShouldKeepChatClientForCapturedReplay(AppSettings settings)
    {
        return ShouldCaptureReplayChat(settings) ||
            (Target.Platform == PlatformKind.Kick &&
                Target.Kind == StreamTargetKind.Live &&
                replayResolver is not null &&
                settings.Replay.Enabled &&
                settings.Chat.ConnectAutomatically &&
                IsChatVisible);
    }

    private bool ShouldUseCapturedReplayChat(ReplaySessionInfo replay)
    {
        return !Target.IsExplicitVod && CanUseCapturedReplayChat(replay);
    }

    private static bool CanUseCapturedReplayChat(ReplaySessionInfo replay)
    {
        return IsCurrentLiveDvrReplay(replay) ||
            (replay.Platform == PlatformKind.Kick && replay.StreamStartedAtUtc is not null);
    }

    private static bool CanLoadReplayChat(ReplaySessionInfo replay)
    {
        return replay.Platform == PlatformKind.Twitch ||
            replay.Platform == PlatformKind.Kick;
    }

    private static bool IsCurrentLiveDvrReplay(ReplaySessionInfo replay)
    {
        return replay.Platform == PlatformKind.Twitch &&
            (replay.MediaKind == ReplayMediaKind.CurrentLiveDvr ||
                replay.ReplayId.StartsWith(TwitchLiveDvrReplayIdPrefix, StringComparison.Ordinal));
    }

    private void StartLiveDvrPromotionPolling(AppSettings settings)
    {
        if (IsBackgroundResourceServicesSuspended || replayResolver is null)
        {
            return;
        }

        CancelLiveDvrPromotionPolling();
        var cancellation = new CancellationTokenSource();
        Task pollingTask;
        lock (liveDvrPromotionPollingGate)
        {
            liveDvrPromotionPollingCancellation = cancellation;
            pollingTask = Task.Run(() => PollLiveDvrPromotionAsync(settings, cancellation));
            liveDvrPromotionPollingTask = pollingTask;
        }
    }

    private async Task StopLiveDvrPromotionPollingAsync()
    {
        CancellationTokenSource? cancellation;
        Task? pollingTask;
        lock (liveDvrPromotionPollingGate)
        {
            cancellation = liveDvrPromotionPollingCancellation;
            pollingTask = liveDvrPromotionPollingTask;
            liveDvrPromotionPollingCancellation = null;
            liveDvrPromotionPollingTask = null;
        }

        await StopPollingAsync(
            cancellation,
            pollingTask,
            "Replay",
            $"Twitch current-live DVR promotion polling cleanup failed for {Target.DisplayName}.");
    }

    private void CancelLiveDvrPromotionPolling()
    {
        CancellationTokenSource? cancellation;
        lock (liveDvrPromotionPollingGate)
        {
            cancellation = liveDvrPromotionPollingCancellation;
            liveDvrPromotionPollingCancellation = null;
            liveDvrPromotionPollingTask = null;
        }

        cancellation?.Cancel();
    }

    private async Task PollLiveDvrPromotionAsync(AppSettings settings, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(twitchLiveDvrPromotionPollInterval, cancellationToken);
                if (replaySession is not { IsAvailable: true } currentReplay ||
                    !IsCurrentLiveDvrReplay(currentReplay) ||
                    replayResolver is null)
                {
                    return;
                }

                var resolvedReplay = await replayResolver.ResolveCurrentReplayAsync(
                        Target,
                        Quality,
                        settings,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!resolvedReplay.IsAvailable ||
                    IsCurrentLiveDvrReplay(resolvedReplay) ||
                    !IsSameReplayStream(currentReplay, resolvedReplay))
                {
                    continue;
                }

                await PromoteLiveDvrReplayAsync(resolvedReplay, settings, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "Replay", $"Twitch current-live DVR promotion polling failed for {Target.DisplayName}.", ex);
        }
        finally
        {
            lock (liveDvrPromotionPollingGate)
            {
                if (ReferenceEquals(liveDvrPromotionPollingCancellation, cancellation))
                {
                    liveDvrPromotionPollingCancellation = null;
                    liveDvrPromotionPollingTask = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task PromoteLiveDvrReplayAsync(
        ReplaySessionInfo promotedReplay,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (replaySession is not { IsAvailable: true } currentReplay ||
            !IsCurrentLiveDvrReplay(currentReplay))
        {
            return;
        }

        replaySession = promotedReplay;
        var replayChatVersion = InvalidateReplayChatState();
        ResetKickSeekbackReplayChatBacklog();
        var duration = GetCurrentReplayDuration(promotedReplay);
        var offset = GetCurrentReplayStepOffset();
        QueueReplayPlaybackUrlResolution(promotedReplay, settings);
        lock (capturedReplayChatGate)
        {
            if (capturedReplayChatSelector.Count > 0)
            {
                capturedReplayChatSelector.CopyTo(replayChatSelector, replaceExisting: false);
                SetReplayChatRangeFromSelector(replayChatSelector);
            }
        }

        dispatch(() =>
        {
            ApplyReplayClock(IsReplayMode ? offset : duration, duration, isSeekable: true);
            ApplyReplaySeekToolTipForCurrentReadiness();
        });

        if (IsReplayMode)
        {
            await LoadReplayChatAsync(
                    promotedReplay,
                    settings,
                    ClampReplayOffset(offset, duration),
                    clearExisting: false,
                    notifyUnavailable: false,
                    replayChatVersion,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        logger.Write(AppLogLevel.Info, "Replay", $"Twitch current-live DVR replay for {Target.DisplayName} was promoted to VOD {promotedReplay.ReplayId}.");
    }

    private static bool IsSameReplayStream(ReplaySessionInfo currentReplay, ReplaySessionInfo resolvedReplay)
    {
        if (!string.Equals(currentReplay.Channel, resolvedReplay.Channel, StringComparison.OrdinalIgnoreCase) ||
            currentReplay.Platform != resolvedReplay.Platform)
        {
            return false;
        }

        if (currentReplay.StreamStartedAtUtc is { } currentStartedAt &&
            resolvedReplay.StreamStartedAtUtc is { } resolvedStartedAt)
        {
            return (currentStartedAt - resolvedStartedAt).Duration() <= TimeSpan.FromMinutes(30);
        }

        return true;
    }

    private void UpdateReplayChatRange(ReplayChatLoadResult result)
    {
        var loadedFrom = result.LoadedFromOffset;
        var loadedThrough = result.LoadedThroughOffset;
        if (result.Messages.Count > 0)
        {
            loadedFrom = Min(loadedFrom, result.Messages[0].Offset);
            loadedThrough = Max(loadedThrough, result.Messages[^1].Offset);
        }

        if (loadedFrom is not null)
        {
            replayChatLoadedFrom = Min(replayChatLoadedFrom, loadedFrom.Value);
        }

        if (loadedThrough is not null)
        {
            replayChatLoadedThrough = Max(replayChatLoadedThrough, loadedThrough.Value);
        }
    }

    private static TimeSpan? Min(TimeSpan? left, TimeSpan right) =>
        left is null || right < left.Value ? right : left.Value;

    private static TimeSpan? Max(TimeSpan? left, TimeSpan right) =>
        left is null || right > left.Value ? right : left.Value;

    private void UpdateReplayChatWindow(
        TimeSpan offset,
        bool force = false,
        ReplayChatWindowSelector? selector = null)
    {
        UpdateReplayChatWindowCore(offset, force, selector, expectedReplaySeekOperationVersion: null);
    }

    private void UpdateReplayChatWindowCore(
        TimeSpan offset,
        bool force,
        ReplayChatWindowSelector? selector,
        long? expectedReplaySeekOperationVersion)
    {
        if (expectedReplaySeekOperationVersion is { } expectedVersion &&
            !IsReplayClockSampleCurrent(expectedVersion))
        {
            return;
        }

        if (!force && !HasReplayChatOffsetChangedEnough(offset))
        {
            return;
        }

        lastReplayChatOffset = offset;
        var selectionStopwatch = Stopwatch.StartNew();
        var selection = SelectReplayChatMessages(offset, selector ?? replayChatSelector);
        selectionStopwatch.Stop();
        LogReplayDebugIfSlow(
            selectionStopwatch.Elapsed,
            "Replay",
            $"Replay chat window selection took {selectionStopwatch.Elapsed.TotalMilliseconds:0} ms for {selection.Messages.Count} visible messages.");
        QueueReplayChatWindowUiApply(selection, force, expectedReplaySeekOperationVersion);
    }

    private ReplayChatWindowSelection SelectReplayChatMessages(TimeSpan offset, ReplayChatWindowSelector selector)
    {
        if (replaySession is { IsAvailable: true } replay &&
            TryGetKickSeekbackReplayChatBacklogStart(replay, out var backlogStart))
        {
            return selector.SelectRange(backlogStart, offset, MaxChatMessages);
        }

        return selector.SelectWindow(offset, ReplayChatWindow, MaxChatMessages);
    }

    private void QueueReplayChatWindowUiApply(
        ReplayChatWindowSelection selection,
        bool force,
        long? expectedReplaySeekOperationVersion = null)
    {
        var update = new ReplayChatWindowUiUpdate(
            GetReplayChatStateVersion(),
            selection,
            force,
            expectedReplaySeekOperationVersion);
        lock (replayChatUiGate)
        {
            if (!force &&
                (selection.Key == lastReplayChatWindowKey ||
                    (pendingReplayChatWindowUiUpdate is { } pending &&
                        pending.Selection.Key == selection.Key &&
                        pending.StateVersion == update.StateVersion &&
                        pending.ReplaySeekOperationVersion == update.ReplaySeekOperationVersion)))
            {
                return;
            }

            pendingReplayChatWindowUiUpdate = update;
            if (replayChatWindowUiDispatchQueued)
            {
                return;
            }

            replayChatWindowUiDispatchQueued = true;
        }

        dispatch(ApplyPendingReplayChatWindowUiUpdate);
    }

    private void ApplyPendingReplayChatWindowUiUpdate()
    {
        while (true)
        {
            ReplayChatWindowUiUpdate? update;
            lock (replayChatUiGate)
            {
                update = pendingReplayChatWindowUiUpdate;
                pendingReplayChatWindowUiUpdate = null;
                if (update is null)
                {
                    replayChatWindowUiDispatchQueued = false;
                    return;
                }
            }

            if (update.Value.StateVersion != GetReplayChatStateVersion() ||
                (update.Value.ReplaySeekOperationVersion is { } expectedVersion &&
                    !IsReplayClockSampleCurrent(expectedVersion)))
            {
                continue;
            }

            lock (replayChatUiGate)
            {
                if (!update.Value.Force &&
                    update.Value.Selection.Key == lastReplayChatWindowKey)
                {
                    continue;
                }
            }

            var applyStopwatch = Stopwatch.StartNew();
            var preservedKickVodStatusMessages = GetKickVodReplayChatStatusMessagesToPreserve(update.Value.Selection);
            ChatMessages.Clear();
            DockedChatMessages.Clear();
            DockedChatFeedItems.Clear();
            foreach (var message in update.Value.Selection.Messages)
            {
                ChatMessages.Add(message);
                DockedChatMessages.Add(message);
                DockedChatFeedItems.Add(new DockedChatMessageFeedItem(message));
            }

            foreach (var message in preservedKickVodStatusMessages)
            {
                ChatMessages.Add(message);
                DockedChatMessages.Add(message);
                DockedChatFeedItems.Add(new DockedChatMessageFeedItem(message));
            }

            if (update.Value.Selection.Messages.Count > 0 || !IsReplaySeekInProgress)
            {
                QueueNativeChatOverlayUpdateAfterReplayWindowApply();
            }
            else
            {
                MarkNativeReplayOverlayRefreshPendingAfterSeek();
            }
            applyStopwatch.Stop();
            LogReplayDebugIfSlow(
                applyStopwatch.Elapsed,
                "Replay",
                $"Replay chat UI apply took {applyStopwatch.Elapsed.TotalMilliseconds:0} ms for {update.Value.Selection.Messages.Count} visible messages.");

            lock (replayChatUiGate)
            {
                lastReplayChatWindowKey = update.Value.Selection.Key;
                if (pendingReplayChatWindowUiUpdate is null)
                {
                    replayChatWindowUiDispatchQueued = false;
                    return;
                }
            }
        }
    }

    private ChatMessage[] GetKickVodReplayChatStatusMessagesToPreserve(ReplayChatWindowSelection selection)
    {
        if (!Target.IsExplicitKickVod ||
            selection.Messages.Count > 0)
        {
            return [];
        }

        return DockedChatMessages
            .Where(IsKickVodReplayChatStatusMessage)
            .ToArray();
    }

    private void ResetReplayChatVisibleWindowCache()
    {
        lock (replayChatUiGate)
        {
            pendingReplayChatWindowUiUpdate = null;
            lastReplayChatWindowKey = ReplayChatWindowKey.Empty;
        }
    }

    private bool HasReplayChatOffsetChangedEnough(TimeSpan offset)
    {
        if (lastReplayChatOffset == TimeSpan.MinValue)
        {
            return true;
        }

        var difference = offset >= lastReplayChatOffset
            ? offset - lastReplayChatOffset
            : lastReplayChatOffset - offset;
        return difference >= TimeSpan.FromSeconds(1);
    }

    private void LogReplayDebugIfSlow(TimeSpan elapsed, string source, string message)
    {
        if (elapsed >= ReplayDiagnosticsSlowThreshold)
        {
            logger.Write(AppLogLevel.Debug, source, message);
        }
    }

    private void LogReplayFirstSeekStage(string stage, TimeSpan elapsed)
    {
        logger.Write(
            AppLogLevel.Info,
            "Replay",
            $"Replay first-seek {stage} took {elapsed.TotalMilliseconds:0} ms for {Target.DisplayName}.");
    }

    private TimeSpan GetCurrentReplayDuration(ReplaySessionInfo replay)
    {
        var duration = NormalizeReplayDuration(replay.Duration);
        if (Target.IsExplicitVod)
        {
            return duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
        }

        if (replay.StreamStartedAtUtc is { } startedAt &&
            TryGetPlausibleElapsedSince(startedAt, DateTimeOffset.UtcNow, out var elapsed))
        {
            if (elapsed > duration)
            {
                duration = elapsed;
            }
        }

        return duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
    }

    private static TimeSpan NormalizeReplayDuration(TimeSpan duration)
    {
        return duration > TimeSpan.Zero && duration <= ReplayClockMaximumPlausibleDuration
            ? duration
            : TimeSpan.FromSeconds(1);
    }

    private static bool TryGetPlausibleElapsedSince(
        DateTimeOffset startedAt,
        DateTimeOffset observedAt,
        out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;
        var elapsedTicks = observedAt.UtcDateTime.Ticks - startedAt.UtcDateTime.Ticks;
        if (elapsedTicks <= 0)
        {
            return false;
        }

        elapsed = TimeSpan.FromTicks(elapsedTicks);
        return elapsed <= ReplayClockMaximumPlausibleDuration;
    }

    private static bool TryGetReplayTimestamp(
        DateTimeOffset startedAt,
        TimeSpan offset,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        try
        {
            timestamp = startedAt.ToUniversalTime().Add(offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static TimeSpan ClampReplayOffset(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return value > duration ? duration : value;
    }

    private void StartViewerCountPolling(AppSettings settings)
    {
        if (IsBackgroundResourceServicesSuspended)
        {
            return;
        }

        if (viewerCountService is null)
        {
            SetViewerCountUnavailable("Viewer count service is not configured.");
            return;
        }

        viewerCountPollingCancellation?.Cancel();
        viewerCountPollingCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        viewerCountPollingCancellation = cancellation;
        viewerCountPollingTask = Task.Run(() => PollViewerCountAsync(settings, cancellation.Token));
    }

    private async Task StopViewerCountPollingAsync()
    {
        var cancellation = viewerCountPollingCancellation;
        var pollingTask = viewerCountPollingTask;
        viewerCountPollingCancellation = null;
        viewerCountPollingTask = null;

        await StopPollingAsync(
            cancellation,
            pollingTask,
            "Viewers",
            $"Viewer count polling cleanup failed for {Target.DisplayName}.");
    }

    private void StartVideoAspectRatioPolling()
    {
        if (IsBackgroundResourceServicesSuspended)
        {
            return;
        }

        videoAspectRatioPollingCancellation?.Cancel();
        videoAspectRatioPollingCancellation?.Dispose();
        ResetVideoAspectRatioPollingBackoff();

        var cancellation = new CancellationTokenSource();
        videoAspectRatioPollingCancellation = cancellation;
        videoAspectRatioPollingTask = Task.Run(() => PollVideoAspectRatioAsync(cancellation.Token));
    }

    private async Task StopVideoAspectRatioPollingAsync()
    {
        var cancellation = videoAspectRatioPollingCancellation;
        var pollingTask = videoAspectRatioPollingTask;
        videoAspectRatioPollingCancellation = null;
        videoAspectRatioPollingTask = null;

        await StopPollingAsync(
            cancellation,
            pollingTask,
            "Playback",
            $"Video aspect ratio polling cleanup failed for {Target.DisplayName}.");
    }

    private async Task PollVideoAspectRatioAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = RefreshVideoAspectRatioPollingSample();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private TimeSpan RefreshVideoAspectRatioPollingSample()
    {
        if (playbackEngine?.TryGetVideoSize(out var width, out var height) != true ||
            !TryCalculateVideoAspectRatio(width, height, out var ratio))
        {
            return videoAspectRatioPollingBackoff.RecordInvalidSample();
        }

        UpdateVideoAspectRatio(width, height);
        return videoAspectRatioPollingBackoff.RecordValidSample(ratio);
    }

    private void UpdateVideoAspectRatio(int width, int height)
    {
        if (TryCalculateVideoAspectRatio(width, height, out var ratio))
        {
            UpdateVideoAspectRatio(ratio);
            RefreshNativeReplayOverlayForVideoSize(width, height);
        }
    }

    private void UpdateVideoAspectRatio(double ratio)
    {
        if (Math.Abs(VideoAspectRatio - ratio) <= 0.001)
        {
            return;
        }

        dispatch(() => VideoAspectRatio = ratio);
    }

    private void RefreshNativeReplayOverlayForVideoSize(int width, int height)
    {
        if (!RecordNativeReplayOverlayVideoSize(width, height) ||
            !ShouldRefreshNativeReplayOverlayForVideoSize())
        {
            return;
        }

        dispatch(InvalidateNativeReplayOverlayFrameIfReplayChatVisible);
    }

    private bool RecordNativeReplayOverlayVideoSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var changed = false;
        lock (nativeReplayOverlayRefreshGate)
        {
            if (nativeReplayOverlayVideoWidth != width ||
                nativeReplayOverlayVideoHeight != height)
            {
                nativeReplayOverlayVideoWidth = width;
                nativeReplayOverlayVideoHeight = height;
                changed = true;
            }
        }

        if (changed)
        {
            SuspendNativeReplayOverlayResizePersistence();
        }

        return changed;
    }

    private bool ShouldRefreshNativeReplayOverlayForVideoSize()
    {
        var engine = playbackEngine;
        var settings = chatSettings;
        return engine is { UsesNativeOverlay: true } &&
            settings is { Layout: ChatLayout.Overlay } &&
            !IsProcessRunning(nativeOverlayProcess) &&
            !IsDockedChatOverrideActive &&
            IsChatVisible &&
            (IsReplayMode || IsBehindLive) &&
            !string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName) &&
            !string.IsNullOrWhiteSpace(engine.NativeOverlayPositionStatePath);
    }

    private void ResetVideoAspectRatioPollingBackoff()
    {
        videoAspectRatioPollingBackoff.Reset();
    }

    private static bool TryCalculateVideoAspectRatio(int width, int height, out double ratio)
    {
        ratio = 0;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        ratio = Math.Clamp(width / (double)height, 0.1, 10.0);
        return true;
    }

    private async Task PollViewerCountAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            try
            {
                var result = await viewerCountService!.GetViewerCountAsync(Target, settings, cancellationToken);
                ApplyViewerCountResult(result);
                delay = result.State is ViewerCountState.Available or ViewerCountState.Offline
                    ? ViewerCountRefreshInterval
                    : ViewerCountRetryDelay;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "Viewers", $"Viewer count refresh failed for {Target.DisplayName}.", ex);
                SetViewerCountUnavailable($"Viewer count unavailable: {ex.Message}");
                delay = ViewerCountRetryDelay;
            }
        }
    }

    private void ApplyViewerCountResult(ViewerCountResult result)
    {
        var updatedAt = DateTimeOffset.Now;

        // Only an Available poll carries authoritative category data. On any other state the
        // platform told us nothing usable, so keep the last known category rather than blanking it.
        if (result.State == ViewerCountState.Available)
        {
            SetCategoryName(result.CategoryName);
            SetStreamTitle(result.StreamTitle);
        }

        switch (result.State)
        {
            case ViewerCountState.Available when result.ViewerCount is { } viewerCount:
                SetViewerCount(
                    FormatViewerCount(viewerCount),
                    $"{viewerCount.ToString("N0", CultureInfo.CurrentCulture)} viewers. Updated {updatedAt:t}.");
                break;
            case ViewerCountState.Offline:
                SetViewerCount("--", $"{result.Message} Updated {updatedAt:t}.");
                break;
            case ViewerCountState.NotConfigured:
                SetViewerCount("Auth", result.Message);
                break;
            default:
                SetViewerCount("N/A", result.Message);
                break;
        }
    }

    private void SetViewerCountPending(string toolTip)
    {
        SetViewerCount("--", toolTip);
    }

    private void SetViewerCountUnavailable(string toolTip)
    {
        SetViewerCount("N/A", toolTip);
    }

    private void SetViewerCount(string text, string toolTip)
    {
        dispatch(() =>
        {
            ViewerCountText = text;
            ViewerCountToolTip = toolTip;
        });
    }

    private void SetCategoryName(string value)
    {
        var normalized = value?.Trim() ?? "";
        if (string.Equals(CategoryName, normalized, StringComparison.Ordinal))
        {
            return;
        }

        logger.Write(
            AppLogLevel.Info,
            "Playback",
            $"{Target.DisplayName} category is now {(normalized.Length == 0 ? "unset" : normalized)}.");
        dispatch(() => CategoryName = normalized);
    }

    private void SetStreamTitle(string value)
    {
        var normalized = value?.Trim() ?? "";
        if (string.Equals(StreamTitle, normalized, StringComparison.Ordinal))
        {
            return;
        }

        dispatch(() => StreamTitle = normalized);
    }

    private static string FormatViewerCount(int viewerCount)
    {
        if (viewerCount < 0)
        {
            return "--";
        }

        if (viewerCount < 1_000_000)
        {
            return viewerCount.ToString("N0", CultureInfo.CurrentCulture);
        }

        return (viewerCount / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }

    private void AttachChatClient(IChatClient client)
    {
        chatClientEventCoordinator.Attach(client);
        twitchPredictionClient = chatClientEventCoordinator.PredictionClient;
    }

    private void DetachChatClient(IChatClient client)
    {
        chatClientEventCoordinator.Detach(client);
        twitchPredictionClient = chatClientEventCoordinator.PredictionClient;
    }

    private async Task StopPlaybackOnlyAsync(TimeSpan? playbackStopTimeout = null)
    {
        livePlaybackConnectionSuspended = false;
        pendingResumeHoldPosition = null;
        pendingResumeHoldAllowsLiveTransition = false;
        await StopVideoAspectRatioPollingAsync();
        await StopReplayClockPollingAsync();
        await StopNativeReplayOverlayEventHostAsync();
        await StopNativeOverlayChatAsync(clearOverlay: false);

        await StopStreamSessionAsync();

        var engine = playbackEngine;
        playbackEngine = null;
        isDirectExplicitVodReplayPlayback = false;
        currentReplayPlaybackKey = null;
        playbackEngineNativeOverlayRequested = false;
        playbackEngineOverlayDirectory = "";
        var parkingSurface = TakeParkingVideoSurface();
        if (engine is not null)
        {
            engine.VideoOutputRebound -= PlaybackEngineOnVideoOutputRebound;
            engine.AudioStateReapplied -= PlaybackEngineOnAudioStateReapplied;
            RaiseNativeOverlayProperties();
            await StopPlaybackEngineAsync(engine, playbackStopTimeout, parkingSurface);
        }
        else
        {
            parkingSurface?.Dispose();
        }
    }

    private async Task StopStreamSessionAsync()
    {
        var session = DetachStreamSession();
        if (session is not null)
        {
            await DisposeDetachedStreamSessionAsync(session);
        }
    }

    private IStreamTransportSession? DetachStreamSession()
    {
        var session = streamSession;
        streamSession = null;
        if (session is not null)
        {
            session.LogLineReceived -= StreamSessionOnLogLineReceived;
        }

        return session;
    }

    private static async Task DisposeDetachedStreamSessionAsync(IStreamTransportSession session)
    {
        await session.DisposeAsync();
    }

    private void RaiseNativeOverlayProperties()
    {
        OnPropertyChanged(nameof(UsesNativeOverlay));
        OnPropertyChanged(nameof(NativeOverlayPipeName));
        OnPropertyChanged(nameof(NativeOverlayPositionStatePath));
    }

    private void PlaybackEngineOnVideoOutputRebound(object? sender, EventArgs e)
    {
        ResetVideoAspectRatioPollingBackoff();
        if (sender is not IPlaybackEngine engine ||
            !ReferenceEquals(engine, playbackEngine) ||
            !engine.UsesNativeOverlay)
        {
            return;
        }

        if ((IsReplayMode || IsBehindLive) && IsChatVisible)
        {
            dispatch(InvalidateNativeReplayOverlayFrame);
            return;
        }

        if (!IsChatVisible)
        {
            _ = BlankNativeOverlayAsync(engine.NativeOverlayPipeName, CancellationToken.None);
        }
    }

    private void PlaybackEngineOnAudioStateReapplied(object? sender, EventArgs e)
    {
        if (sender is not IPlaybackEngine engine ||
            !ReferenceEquals(engine, playbackEngine))
        {
            return;
        }

        dispatch(() =>
        {
            if (ReferenceEquals(engine, playbackEngine))
            {
                AudioStateApplied?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private async Task StopPlaybackEngineAsync(IPlaybackEngine engine, TimeSpan? timeout, IDisposable? parkingSurface)
    {
        await playbackResourceCoordinator.StopAsync(
            engine,
            timeout,
            parkingSurface,
            lifetimeCancellation.Token,
            playbackCleanupController.Observe);
    }

    private void RegisterActiveStartCancellation(CancellationTokenSource cancellation)
    {
        lock (videoSurfaceGate)
        {
            activeStartCancellation?.Cancel();
            activeStartCancellation = cancellation;
        }
    }

    private void ClearActiveStartCancellation(CancellationTokenSource cancellation)
    {
        lock (videoSurfaceGate)
        {
            if (ReferenceEquals(activeStartCancellation, cancellation))
            {
                activeStartCancellation = null;
            }
        }
    }

    private void CancelActiveStart()
    {
        lock (videoSurfaceGate)
        {
            activeStartCancellation?.Cancel();
        }
    }

    private async Task<(IntPtr Handle, long Version)> WaitForVideoHandleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<IntPtr> handleReadyTask;
            Task stateChangedTask;
            bool shouldWaitForVideoSurface;
            lock (videoSurfaceGate)
            {
                if (videoHandle != IntPtr.Zero)
                {
                    return (videoHandle, videoHandleVersion);
                }

                shouldWaitForVideoSurface = IsVideoSurfaceExpectedCore;
                handleReadyTask = videoHandleReady.Task;
                stateChangedTask = videoSurfaceStateChanged.Task;
            }

            if (!shouldWaitForVideoSurface)
            {
                return GetOrCreateParkingVideoHandle();
            }

            Task expectedSurfaceCompleted;
            try
            {
                expectedSurfaceCompleted = await Task.WhenAny(handleReadyTask, stateChangedTask)
                    .WaitAsync(VideoSurfaceReadyTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"The video surface did not become ready within {VideoSurfaceReadyTimeout.TotalSeconds:0} seconds.");
            }

            await expectedSurfaceCompleted.WaitAsync(cancellationToken);
        }
    }

    private bool IsVideoSurfaceExpectedCore => !videoPlacementKnown || isVideoVisible || isMainVideoSurfaceExpected || isDetached;

    private (IntPtr Handle, long Version) GetOrCreateParkingVideoHandle()
    {
        lock (videoSurfaceGate)
        {
            parkingVideoSurface ??= new ParkingVideoSurface();
            return (parkingVideoSurface.Handle, videoHandleVersion);
        }
    }

    private ParkingVideoSurface? TakeParkingVideoSurface()
    {
        lock (videoSurfaceGate)
        {
            var surface = parkingVideoSurface;
            parkingVideoSurface = null;
            return surface;
        }
    }

    private void SignalVideoSurfaceStateChanged()
    {
        TaskCompletionSource stateChanged;
        lock (videoSurfaceGate)
        {
            stateChanged = videoSurfaceStateChanged;
            videoSurfaceStateChanged = CreateVideoSurfaceStateChangedSource();
        }

        stateChanged.TrySetResult();
    }

    private static TaskCompletionSource<IntPtr> CreateVideoHandleReadySource()
    {
        return new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource CreateVideoSurfaceStateChangedSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task DisposeUnclaimedStreamSessionAsync(Task<IStreamTransportSession> streamSessionTask)
    {
        try
        {
            var session = await streamSessionTask;
            await session.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Streamlink", $"Failed to clean up partially started Streamlink session for {Target.DisplayName}.", ex);
        }
    }

    private void StreamSessionOnLogLineReceived(object? sender, string line)
    {
        dispatch(() =>
        {
            Logs.Add(line);
            while (Logs.Count > 300)
            {
                Logs.RemoveAt(0);
            }
        });
    }

    private void ChatClientOnMessageReceived(object? sender, ChatMessage message)
    {
        if (!ReferenceEquals(sender, chatClient))
        {
            return;
        }

        // The live path only touches lock-protected buffers here. AddChatMessage
        // performs the single coalesced dispatcher hop that owns the WPF-bound
        // collections, rather than scheduling one dispatcher callback per IRC
        // message before that batching can take effect.
        if (!IsBehindLive && !IsReplayMode)
        {
            // Replay capture is protected by capturedReplayChatGate and must be
            // updated immediately when a replay session is already known; only
            // remembering the source message would miss later live messages.
            _ = TryCaptureReplayChatMessage(message);
            AddChatMessage(message, isRememberedDockedLocalEcho: false);
            return;
        }

        dispatch(() =>
        {
            if (!ReferenceEquals(sender, chatClient))
            {
                return;
            }

            var capturedReplayMessage = TryCaptureReplayChatMessage(message);
            if (IsBehindLive || IsReplayMode)
            {
                if (capturedReplayMessage is not null &&
                    IsCapturedReplayChatMessageInCurrentWindow(capturedReplayMessage))
                {
                    RefreshCapturedReplayChat(GetCurrentReplayStepOffset(), force: true);
                }

                return;
            }

            AddChatMessage(message, isRememberedDockedLocalEcho: false);
        });
    }

    private bool IsCapturedReplayChatMessageInCurrentWindow(ReplayChatMessage message)
    {
        if (replaySession is not { IsAvailable: true } replay ||
            !ShouldUseCapturedReplayChat(replay))
        {
            return false;
        }

        var offset = GetCurrentReplayStepOffset();
        var start = TryGetKickSeekbackReplayChatBacklogStart(replay, out var backlogStart)
            ? backlogStart
            : GetReplayChatWindowStart(offset);

        return message.Offset >= start && message.Offset <= offset;
    }

    private void ChatClientOnStatusChanged(object? sender, string message)
    {
        dispatch(() =>
        {
            if (!ReferenceEquals(sender, chatClient))
            {
                return;
            }

            AddSystemMessage(message);
        });
    }

    private void TwitchPredictionClientOnPredictionAccessChanged(object? sender, TwitchPredictionAccessState access)
    {
        dispatch(() =>
        {
            if (!ReferenceEquals(sender, twitchPredictionClient))
            {
                return;
            }

            ApplyTwitchPredictionAccess(access);
        });
    }

    private void TwitchPredictionClientOnPredictionReceived(object? sender, TwitchPrediction prediction)
    {
        dispatch(() =>
        {
            if (!ReferenceEquals(sender, twitchPredictionClient))
            {
                return;
            }

            UpsertTwitchPredictionCore(prediction);
        });
    }

    private void ApplyTwitchPredictionAccess(TwitchPredictionAccessState access)
    {
        twitchPredictionAccess = access;
        OnPropertyChanged(nameof(TwitchPredictionStatusText));
        RaiseTwitchPredictionCommandState();
    }

    private void UpsertTwitchPrediction(TwitchPrediction prediction)
    {
        dispatch(() => UpsertTwitchPredictionCore(prediction));
    }

    private void UpsertTwitchPredictionCore(TwitchPrediction prediction)
    {
        if (!CanProcessTwitchPredictionEvents || string.IsNullOrWhiteSpace(prediction.Id))
        {
            return;
        }

        var existing = DockedChatFeedItems
            .OfType<TwitchPredictionFeedItemViewModel>()
            .FirstOrDefault(item => string.Equals(item.PredictionId, prediction.Id, StringComparison.Ordinal));

        if (prediction.IsOpen)
        {
            for (var index = DockedChatFeedItems.Count - 1; index >= 0; index--)
            {
                if (DockedChatFeedItems[index] is TwitchPredictionFeedItemViewModel card &&
                    !string.Equals(card.PredictionId, prediction.Id, StringComparison.Ordinal))
                {
                    DockedChatFeedItems.RemoveAt(index);
                }
            }

            if (existing is null)
            {
                existing = new TwitchPredictionFeedItemViewModel(
                    prediction,
                    twitchPredictionAccess.CanManage,
                    LockTwitchPredictionAsync,
                    CancelTwitchPredictionAsync,
                    ResolveTwitchPredictionAsync);
                DockedChatFeedItems.Add(existing);
            }
            else
            {
                existing.Update(prediction, twitchPredictionAccess.CanManage);
            }

            activeTwitchPredictionFeedItem = existing;
            StartTwitchPredictionClock();
        }
        else
        {
            if (existing is not null)
            {
                DockedChatFeedItems.Remove(existing);
            }

            if (activeTwitchPredictionFeedItem is not null &&
                string.Equals(activeTwitchPredictionFeedItem.PredictionId, prediction.Id, StringComparison.Ordinal))
            {
                activeTwitchPredictionFeedItem = null;
            }

            if (!DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>().Any(card => card.IsOpen))
            {
                StopTwitchPredictionClock();
            }
        }

        PruneDockedChatFeedItems();
        RaiseTwitchPredictionCommandState();
    }

    private void RaiseTwitchPredictionCommandState()
    {
        OnPropertyChanged(nameof(TwitchPredictionStatusText));
        OnPropertyChanged(nameof(CanStartTwitchPrediction));
        StartTwitchPredictionCommand.RaiseCanExecuteChanged();
        foreach (var card in DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>())
        {
            card.SetCanManage(twitchPredictionAccess.CanManage);
            card.SetRequestInFlight(isTwitchPredictionRequestInFlight);
        }
    }

    private void StartTwitchPredictionClock()
    {
        twitchPredictionClockTimer ??= new System.Threading.Timer(
            _ => dispatch(RefreshTwitchPredictionClock),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private void StopTwitchPredictionClock()
    {
        twitchPredictionClockTimer?.Dispose();
        twitchPredictionClockTimer = null;
    }

    private void RefreshTwitchPredictionClock()
    {
        foreach (var card in DockedChatFeedItems.OfType<TwitchPredictionFeedItemViewModel>())
        {
            card.RefreshTiming();
        }
    }

    private void AddSystemMessage(string message)
    {
        if (disposed)
        {
            return;
        }

        var systemMessage = new ChatMessage(Target.Platform, Target.Channel, "system", message, DateTimeOffset.Now, "#A6E3A1");
        AddChatMessage(systemMessage, isRememberedDockedLocalEcho: false);
    }

    private void AddReplayChatStatusMessage(string message)
    {
        if (!Target.IsExplicitKickVod)
        {
            AddSystemMessage(message);
            return;
        }

        var systemMessage = new ChatMessage(
            Target.Platform,
            Target.Channel,
            "system",
            message,
            DateTimeOffset.Now,
            "#A6E3A1",
            MessageId: $"{KickVodReplayChatStatusMessageIdPrefix}:{Guid.NewGuid():N}");
        AddChatMessage(systemMessage, isRememberedDockedLocalEcho: false);
    }

    private void AddChatMessage(ChatMessage message, bool isRememberedDockedLocalEcho)
    {
        if (disposed)
        {
            return;
        }

        var shouldDispatch = false;
        lock (chatMessageUiGate)
        {
            pendingChatMessages.Enqueue(new PendingChatMessage(message, isRememberedDockedLocalEcho));
            if (!chatMessageUiDispatchQueued)
            {
                chatMessageUiDispatchQueued = true;
                shouldDispatch = true;
            }
        }

        if (shouldDispatch)
        {
            dispatch(ApplyPendingChatMessages);
        }
    }

    private void ApplyPendingChatMessages()
    {
        var shouldRefreshOverlay = false;
        while (true)
        {
            PendingChatMessage[] batch;
            lock (chatMessageUiGate)
            {
                if (pendingChatMessages.Count == 0)
                {
                    chatMessageUiDispatchQueued = false;
                    break;
                }

                batch = pendingChatMessages.ToArray();
                pendingChatMessages.Clear();
            }

            foreach (var pending in batch)
            {
                var message = pending.Message;
                if (ShouldSkipDuplicateChatMessage(message))
                {
                    continue;
                }

                ChatMessages.Add(message);
                while (ChatMessages.Count > MaxChatMessages)
                {
                    ChatMessages.RemoveAt(0);
                }

                if (ShouldAddDockedChatMessage(message, pending.IsRememberedDockedLocalEcho))
                {
                    DockedChatMessages.Add(message);
                    while (DockedChatMessages.Count > MaxChatMessages)
                    {
                        DockedChatMessages.RemoveAt(0);
                    }

                    DockedChatFeedItems.Add(new DockedChatMessageFeedItem(message));
                    PruneDockedChatFeedItems();
                }

                shouldRefreshOverlay = true;
            }
        }

        if (shouldRefreshOverlay)
        {
            UpdateNativeChatOverlay();
        }
    }

    private bool ShouldSkipDuplicateChatMessage(ChatMessage message)
    {
        var sourceKey = BuildChatMessageSourceKey(message);
        if (sourceKey is null)
        {
            return false;
        }

        if (!recentChatMessageIdSet.Add(sourceKey))
        {
            return true;
        }

        recentChatMessageIds.Enqueue(sourceKey);
        while (recentChatMessageIds.Count > MaxRecentChatMessageIds)
        {
            recentChatMessageIdSet.Remove(recentChatMessageIds.Dequeue());
        }

        return false;
    }

    private void PruneDockedChatFeedItems()
    {
        const int maxFeedItems = MaxChatMessages + 3;
        while (DockedChatFeedItems.Count > maxFeedItems)
        {
            var removeIndex = 0;
            if (DockedChatFeedItems[removeIndex] is TwitchPredictionFeedItemViewModel { IsOpen: true })
            {
                removeIndex = DockedChatFeedItems
                    .Select((item, index) => new { item, index })
                    .FirstOrDefault(entry => entry.item is not TwitchPredictionFeedItemViewModel { IsOpen: true })
                    ?.index ?? 0;
            }

            DockedChatFeedItems.RemoveAt(removeIndex);
        }
    }

    private static string? BuildChatMessageSourceKey(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId))
        {
            return null;
        }

        return string.Join(
            "|",
            message.Platform,
            message.Channel.Trim().ToLowerInvariant(),
            message.MessageId.Trim());
    }

    private bool ShouldAddDockedChatMessage(ChatMessage message, bool isRememberedDockedLocalEcho)
    {
        if (isRememberedDockedLocalEcho)
        {
            return true;
        }

        return !TryConsumeMatchingDockedLocalEcho(message);
    }

    private ChatMessage CreateLocalEchoMessage(string message)
    {
        var username = string.IsNullOrWhiteSpace(chatClient?.CurrentUsername) ? "me" : chatClient.CurrentUsername;
        return new ChatMessage(Target.Platform, Target.Channel, username!, message, DateTimeOffset.Now, "#48C7B5");
    }

    private void RememberDockedLocalEcho(ChatMessage message)
    {
        PruneExpiredDockedLocalEchoes(DateTimeOffset.Now);
        pendingDockedLocalEchoes.Add(new DockedLocalEcho(
            message.Platform,
            message.Channel,
            message.Username,
            NormalizeDockedLocalEchoBody(message.Message),
            DateTimeOffset.Now));
    }

    private void ForgetDockedLocalEcho(ChatMessage message)
    {
        var normalizedMessage = NormalizeDockedLocalEchoBody(message.Message);
        for (var index = pendingDockedLocalEchoes.Count - 1; index >= 0; index--)
        {
            var pending = pendingDockedLocalEchoes[index];
            if (pending.Platform != message.Platform ||
                !string.Equals(pending.Channel, message.Channel, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pending.Username, message.Username, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pending.Message, normalizedMessage, StringComparison.Ordinal))
            {
                continue;
            }

            pendingDockedLocalEchoes.RemoveAt(index);
            return;
        }
    }

    private bool TryConsumeMatchingDockedLocalEcho(ChatMessage message)
    {
        var now = DateTimeOffset.Now;
        PruneExpiredDockedLocalEchoes(now);
        var normalizedMessage = NormalizeDockedLocalEchoBody(message.Message);

        for (var index = pendingDockedLocalEchoes.Count - 1; index >= 0; index--)
        {
            var pending = pendingDockedLocalEchoes[index];
            if (!IsMatchingDockedLocalEcho(pending, message, normalizedMessage))
            {
                continue;
            }

            pendingDockedLocalEchoes.RemoveAt(index);
            return true;
        }

        return false;
    }

    private void PruneExpiredDockedLocalEchoes(DateTimeOffset now)
    {
        pendingDockedLocalEchoes.RemoveAll(pending => now - pending.Timestamp > DockedLocalEchoDeduplicationWindow);
    }

    private static bool IsMatchingDockedLocalEcho(DockedLocalEcho pending, ChatMessage message, string normalizedMessage)
    {
        if (pending.Platform != message.Platform ||
            !string.Equals(pending.Channel, message.Channel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pending.Message, normalizedMessage, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(pending.Username, message.Username, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return pending.Platform == PlatformKind.Kick && IsPlaceholderLocalUsername(pending.Username);
    }

    private static bool IsPlaceholderLocalUsername(string username)
    {
        return string.Equals(username, "me", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(username, "bot", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDockedLocalEchoBody(string message)
    {
        return message.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private async Task<bool> StartNativeOverlayChatAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        long? operationVersion = null)
    {
        if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken))
        {
            return false;
        }

        var engine = playbackEngine;
        if (engine is not { UsesNativeOverlay: true } ||
            string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName))
        {
            return false;
        }

        if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine) ||
            IsReplayMode || IsBehindLive)
        {
            return false;
        }

        var pipeName = engine.NativeOverlayPipeName!;
        var launchKey = BuildNativeOverlayLaunchKey(settings);
        await StopNativeReplayOverlayEventHostAsync();
        if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine))
        {
            return false;
        }

        var overlayDirectory = ResolveNativeOverlayControllerDirectory(engine, settings.Chat);
        var controllerPath = string.IsNullOrWhiteSpace(overlayDirectory)
            ? GetConfiguredNativeOverlayControllerPath(settings.Chat)
            : VlcOverlayDirectoryResolver.GetControllerPath(overlayDirectory);
        if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine) ||
            IsReplayMode || IsBehindLive)
        {
            return false;
        }

        if (!File.Exists(controllerPath))
        {
            AddSystemMessage($"Native VLC chat overlay controller was not found at {controllerPath}.");
            return false;
        }

        string? tokenFile = null;
        await nativeOverlayProcessGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine))
            {
                return false;
            }

            if (IsProcessRunning(nativeOverlayProcess) &&
                string.Equals(nativeOverlayPipeName, pipeName, StringComparison.Ordinal) &&
                string.Equals(nativeOverlayLaunchKey, launchKey, StringComparison.Ordinal))
            {
                return true;
            }

            if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine) ||
                IsReplayMode || IsBehindLive)
            {
                return false;
            }

            if (DetachNativeOverlayChatCore(includePipeOnly: false) is { } detachedNativeOverlayChat)
            {
                await StopDetachedNativeOverlayChatAsync(detachedNativeOverlayChat, clearOverlay: false);
            }

            KickOverlayChannelInfo? kickInfo = null;
            string? kickToken = null;
            string? kickBadgeManifestPath = null;
            string? twitchBadgeManifestPath = null;
            string? twitchRoomId = null;

            if (Target.Platform == PlatformKind.Kick)
            {
                kickBadgeManifestPath = FindKickBadgeManifestPath();
                kickInfo = await ResolveKickOverlayChannelInfoAsync(settings.Chat, settings.Chat.KickSendAsBot, cancellationToken);
                if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine) ||
                    IsReplayMode || IsBehindLive)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(kickInfo.ChatroomId))
                {
                    AddSystemMessage("Native VLC chat overlay needs the Kick chatroom ID. Automatic lookup failed; add it to Settings for this Kick tab.");
                    return false;
                }

                kickToken = await ResolveKickOverlayTokenAsync(settings.Chat, cancellationToken);
                launchKey = BuildNativeOverlayLaunchKey(settings, kickInfo, kickToken, kickBadgeManifestPath: kickBadgeManifestPath);
            }
            else
            {
                twitchBadgeManifestPath = FindTwitchBadgeManifestPath();
                twitchRoomId = await ResolveTwitchOverlayRoomIdAsync(settings.Chat, cancellationToken);
                launchKey = BuildNativeOverlayLaunchKey(settings, twitchBadgeManifestPath: twitchBadgeManifestPath);
            }

            if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine) ||
                IsReplayMode || IsBehindLive)
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = controllerPath,
                WorkingDirectory = Path.GetDirectoryName(controllerPath) ?? overlayDirectory ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("--channel");
            startInfo.ArgumentList.Add(Target.Channel);
            startInfo.ArgumentList.Add("--channel-display-name");
            startInfo.ArgumentList.Add(Target.Channel);
            startInfo.ArgumentList.Add("--provider");
            startInfo.ArgumentList.Add(Target.Platform == PlatformKind.Kick ? "kick" : "twitch");
            startInfo.ArgumentList.Add("--pipe-name");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--width");
            startInfo.ArgumentList.Add(NativeOverlaySizing
                .ClampReferenceWidth((int)Math.Round(settings.Chat.DockWidth))
                .ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--height");
            startInfo.ArgumentList.Add("292");
            startInfo.ArgumentList.Add("--x");
            startInfo.ArgumentList.Add("24");
            startInfo.ArgumentList.Add("--y");
            startInfo.ArgumentList.Add("24");
            startInfo.ArgumentList.Add("--max-messages");
            startInfo.ArgumentList.Add("18");
            startInfo.ArgumentList.Add("--owner-process-id");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            var fontSize = GetNativeOverlayFontSize(settings);
            if (await nativeOverlayCapabilityProbe.SupportsFontSizeAsync(controllerPath, cancellationToken))
            {
                startInfo.ArgumentList.Add(NativeOverlayFontSizeArgument);
                startInfo.ArgumentList.Add(fontSize.ToString(CultureInfo.InvariantCulture));
            }
            else if (fontSize != (int)Math.Round(ChatSettings.DefaultVlcOverlayFontSize))
            {
                AddSystemMessage("Native VLC chat overlay controller does not support text size settings; rebuild vlc-overlay to enable it.");
            }

            var positionStatePath = engine.NativeOverlayPositionStatePath;
            if (!string.IsNullOrWhiteSpace(positionStatePath))
            {
                startInfo.ArgumentList.Add("--position-state-path");
                startInfo.ArgumentList.Add(positionStatePath);
            }

            if (Target.Platform == PlatformKind.Kick)
            {
                if (!string.IsNullOrWhiteSpace(kickBadgeManifestPath))
                {
                    startInfo.ArgumentList.Add("--kick-badge-manifest");
                    startInfo.ArgumentList.Add(kickBadgeManifestPath);
                }

                startInfo.ArgumentList.Add("--kick-chatroom-id");
                startInfo.ArgumentList.Add(kickInfo!.ChatroomId!);

                if (settings.Chat.KickSendAsBot)
                {
                    startInfo.ArgumentList.Add("--kick-send-as-bot");
                }
                else if (kickInfo.BroadcasterUserId is not null)
                {
                    startInfo.ArgumentList.Add("--kick-broadcaster-user-id");
                    startInfo.ArgumentList.Add(kickInfo.BroadcasterUserId.Value.ToString());
                }

                tokenFile = WriteOverlayTokenFile("kick", kickToken);
                if (tokenFile is not null)
                {
                    startInfo.ArgumentList.Add("--kick-chat-token-file");
                    startInfo.ArgumentList.Add(tokenFile);
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(twitchBadgeManifestPath))
                {
                    startInfo.ArgumentList.Add("--twitch-badge-manifest");
                    startInfo.ArgumentList.Add(twitchBadgeManifestPath);
                }

                tokenFile = WriteOverlayTokenFile("twitch", settings.Chat.TwitchOAuthToken);
                if (tokenFile is not null)
                {
                    startInfo.ArgumentList.Add("--chat-token-file");
                    startInfo.ArgumentList.Add(tokenFile);
                }
                if (!string.IsNullOrWhiteSpace(settings.Chat.TwitchUsername))
                {
                    startInfo.ArgumentList.Add("--chat-username");
                    startInfo.ArgumentList.Add(settings.Chat.TwitchUsername.Trim());
                }
                if (!string.IsNullOrWhiteSpace(settings.Chat.TwitchClientId))
                {
                    startInfo.ArgumentList.Add("--twitch-client-id");
                    startInfo.ArgumentList.Add(settings.Chat.TwitchClientId.Trim());
                }
                if (!string.IsNullOrWhiteSpace(twitchRoomId))
                {
                    startInfo.ArgumentList.Add("--twitch-room-id");
                    startInfo.ArgumentList.Add(twitchRoomId);
                }
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => LogNativeOverlayLine(args.Data);
            process.ErrorDataReceived += (_, args) => LogNativeOverlayLine(args.Data);
            process.Exited += (_, _) => OnNativeOverlayProcessExited(process);

            if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine))
            {
                process.Dispose();
                TryDeleteNativeOverlayTokenFile(tokenFile);
                tokenFile = null;
                return false;
            }

            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    TryDeleteNativeOverlayTokenFile(tokenFile);
                    tokenFile = null;
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                DisposeFailedNativeOverlayProcess(process);
                throw;
            }

            if (!IsCurrentNativeOverlayStartup(operationVersion, cancellationToken, engine))
            {
                DisposeFailedNativeOverlayProcess(process);
                TryDeleteNativeOverlayTokenFile(tokenFile);
                tokenFile = null;
                return false;
            }

            nativeOverlayProcess = process;
            nativeOverlayPipeName = pipeName;
            nativeOverlayLaunchKey = launchKey;
            nativeOverlayTokenFile = tokenFile;
            tokenFile = null;
            AddSystemMessage("Native VLC chat overlay started.");
            return true;
        }
        catch
        {
            TryDeleteNativeOverlayTokenFile(tokenFile);
            throw;
        }
        finally
        {
            nativeOverlayProcessGate.Release();
        }
    }

    private Task<bool> StartNativeOverlayChatTrackedAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        bool startCaptureChatClient = false)
    {
        lock (nativeOverlayStartupGate)
        {
            if (disposed)
            {
                return Task.FromResult(false);
            }

            CancelCancellationSource(nativeOverlayStartupCancellation);
            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellation.Token,
                cancellationToken);
            var version = ++nativeOverlayStartupVersion;
            var operationReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var task = RunNativeOverlayChatStartupWhenReadyAsync(
                settings,
                operationCancellation,
                version,
                startCaptureChatClient,
                operationReady.Task);
            nativeOverlayStartupCancellation = operationCancellation;
            nativeOverlayStartupTask = task;
            operationReady.TrySetResult();
            return task;
        }
    }

    private async Task<bool> RunNativeOverlayChatStartupWhenReadyAsync(
        AppSettings settings,
        CancellationTokenSource operationCancellation,
        long version,
        bool startCaptureChatClient,
        Task operationReady)
    {
        await operationReady.ConfigureAwait(false);
        return await RunNativeOverlayChatStartupAsync(
                settings,
                operationCancellation,
                version,
                startCaptureChatClient)
            .ConfigureAwait(false);
    }

    private async Task<bool> RunNativeOverlayChatStartupAsync(
        AppSettings settings,
        CancellationTokenSource operationCancellation,
        long version,
        bool startCaptureChatClient)
    {
        try
        {
            var started = await TryStartNativeOverlayChatAsync(
                    settings,
                    operationCancellation.Token,
                    version)
                .ConfigureAwait(false);
            if (!IsCurrentNativeOverlayStartup(version, operationCancellation.Token))
            {
                return false;
            }

            if (started &&
                startCaptureChatClient &&
                ShouldKeepChatClientForCapturedReplay(settings))
            {
                await StartChatAsync(operationCancellation.Token).ConfigureAwait(false);
                return true;
            }

            if (!started &&
                startCaptureChatClient &&
                ShouldKeepChatClientForCapturedReplay(settings))
            {
                await EnsureChatClientConnectedAsync(operationCancellation.Token).ConfigureAwait(false);
            }

            return started;
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested || disposed)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Background chat startup failed for {Target.DisplayName}.", ex);
            return false;
        }
        finally
        {
            lock (nativeOverlayStartupGate)
            {
                if (ReferenceEquals(nativeOverlayStartupCancellation, operationCancellation))
                {
                    nativeOverlayStartupCancellation = null;
                    nativeOverlayStartupTask = null;
                }
            }

            operationCancellation.Dispose();
        }
    }

    private void StartNativeOverlayChatInBackground(
        AppSettings settings,
        CancellationToken cancellationToken,
        bool startCaptureChatClient = false)
    {
        _ = StartNativeOverlayChatTrackedAsync(
            settings,
            cancellationToken,
            startCaptureChatClient);
    }

    private async Task<bool> TryStartNativeOverlayChatAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        long? operationVersion = null)
    {
        try
        {
            return await StartNativeOverlayChatAsync(settings, cancellationToken, operationVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Native VLC chat overlay unavailable: {ex.Message}");
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Failed to start native VLC chat overlay for {Target.DisplayName}.", ex);
            return false;
        }
    }

    private bool IsCurrentNativeOverlayStartup(
        long? operationVersion,
        CancellationToken cancellationToken,
        IPlaybackEngine? expectedEngine = null)
    {
        if (disposed || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (expectedEngine is not null && !ReferenceEquals(expectedEngine, playbackEngine))
        {
            return false;
        }

        if (operationVersion is not { } version)
        {
            return true;
        }

        lock (nativeOverlayStartupGate)
        {
            return version == nativeOverlayStartupVersion;
        }
    }

    private async Task StopNativeOverlayChatAsync(bool clearOverlay = false)
    {
        await StopNativeOverlayStartupAsync().ConfigureAwait(false);
        TryReleaseNativeOverlayChatInputFocus();

        DetachedNativeOverlayChat? detached;
        await nativeOverlayProcessGate.WaitAsync();
        try
        {
            detached = DetachNativeOverlayChatCore(includePipeOnly: clearOverlay);
        }
        finally
        {
            nativeOverlayProcessGate.Release();
        }

        if (detached is not null)
        {
            await StopDetachedNativeOverlayChatAsync(detached, clearOverlay);
        }
    }

    private async Task StopNativeOverlayStartupAsync()
    {
        Task? startupTask;
        CancellationTokenSource? startupCancellation;
        lock (nativeOverlayStartupGate)
        {
            nativeOverlayStartupVersion++;
            startupTask = nativeOverlayStartupTask;
            startupCancellation = nativeOverlayStartupCancellation;
        }

        CancelCancellationSource(startupCancellation);
        if (startupTask is null)
        {
            return;
        }

        try
        {
            await startupTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Native chat overlay startup cleanup failed for {Target.DisplayName}.", ex);
        }
    }

    private DetachedNativeOverlayChat? TryDetachNativeOverlayChatForReplayTransition()
    {
        if (!nativeOverlayProcessGate.Wait(0))
        {
            return null;
        }

        try
        {
            return DetachNativeOverlayChatCore(includePipeOnly: false);
        }
        finally
        {
            nativeOverlayProcessGate.Release();
        }
    }

    private DetachedNativeOverlayChat? DetachNativeOverlayChatCore(bool includePipeOnly)
    {
        var process = nativeOverlayProcess;
        var pipeName = nativeOverlayPipeName ?? playbackEngine?.NativeOverlayPipeName;
        var tokenFile = nativeOverlayTokenFile;
        if (process is null &&
            string.IsNullOrWhiteSpace(tokenFile) &&
            (!includePipeOnly || string.IsNullOrWhiteSpace(pipeName)))
        {
            return null;
        }

        nativeOverlayProcess = null;
        nativeOverlayPipeName = null;
        nativeOverlayLaunchKey = null;
        nativeOverlayTokenFile = null;
        return new DetachedNativeOverlayChat(process, pipeName, tokenFile);
    }

    private async Task StopDetachedNativeOverlayChatAsync(DetachedNativeOverlayChat detached, bool clearOverlay)
    {
        var process = detached.Process;
        var pipeName = detached.PipeName;
        if (process is not null)
        {
            try
            {
                if (IsProcessRunning(process))
                {
                    var exited = false;
                    if (clearOverlay && !string.IsNullOrWhiteSpace(pipeName))
                    {
                        if (await RequestNativeOverlayShutdownAsync(pipeName))
                        {
                            exited = await WaitForNativeOverlayProcessExitAsync(
                                process,
                                NativeOverlayGracefulStopTimeout,
                                "Timed out waiting for the native VLC chat overlay to exit after shutdown request.");
                        }
                    }

                    if (!exited && IsProcessRunning(process))
                    {
                        process.Kill(entireProcessTree: true);
                        exited = await WaitForNativeOverlayProcessExitAsync(
                            process,
                            ProcessStopTimeout,
                            "Timed out waiting for the native VLC chat overlay to exit after kill.");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
            {
                logger.Write(AppLogLevel.Warning, "ChatOverlay", "Failed to stop native VLC chat overlay.", ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (clearOverlay)
        {
            await BlankNativeOverlayAsync(pipeName);
        }

        TryDeleteNativeOverlayTokenFile(detached.TokenFile);
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static void DisposeFailedNativeOverlayProcess(Process process)
    {
        try
        {
            if (IsProcessRunning(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // Preserve the original start/read failure; cleanup is best effort.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDeleteNativeOverlayTokenFile(string? tokenFile)
    {
        if (string.IsNullOrWhiteSpace(tokenFile))
        {
            return;
        }

        try
        {
            File.Delete(tokenFile);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<bool> RequestNativeOverlayShutdownAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        var shutdownMessage = NativeOverlayProtocolCodec.BuildEventMessage(
            NativeOverlayProtocolCodec.ShutdownEventType,
            0);
        var (sent, _) = await TryWriteNativeOverlayMessageAsync(
            $"{pipeName}_events",
            shutdownMessage,
            NativeOverlayShutdownRequestTimeout,
            cancellationToken);
        return sent;
    }

    private async Task<bool> WaitForNativeOverlayProcessExitAsync(Process process, TimeSpan timeout, string timeoutMessage)
    {
        try
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(timeoutCancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", timeoutMessage);
            return false;
        }
    }

    private async Task BlankNativeOverlayAsync(string? pipeName = null, CancellationToken cancellationToken = default)
    {
        pipeName ??= playbackEngine?.NativeOverlayPipeName;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return;
        }

        var blankMessage = NativeOverlayChatFrameRenderer.BuildTransparentBlankFrameMessage();
        var emptyScrollbarState = NativeOverlayChatFrameRenderer.BuildScrollbarStateFrameMessage(
            NativeReplayOverlayRenderedSelection.Empty,
            totalMessageCount: 0);
        var (_, lastException) = await TryWriteNativeOverlayMessageAsync(
            pipeName,
            blankMessage,
            NativeOverlayClearTimeout,
            cancellationToken,
            emptyScrollbarState);
        if (lastException is not null)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", "Could not blank the native VLC chat overlay.", lastException);
        }
    }

    private Task ClearNativeReplayOverlayForReplayTransitionAsync(
        ReplaySessionInfo replay,
        bool targetWindowHasReplayMessages,
        CancellationToken cancellationToken)
    {
        if (!ShouldUseCapturedReplayChat(replay) ||
            targetWindowHasReplayMessages)
        {
            return Task.CompletedTask;
        }

        var engine = playbackEngine;
        var settings = chatSettings;
        if (engine is not { UsesNativeOverlay: true } ||
            settings is null ||
            settings.Layout != ChatLayout.Overlay ||
            IsDockedChatOverrideActive ||
            !IsChatVisible ||
            string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName))
        {
            return Task.CompletedTask;
        }

        CancelNativeReplayOverlayAnimationState();
        QueueCriticalNativeReplayOverlayFrameWrite(
            engine.NativeOverlayPipeName!,
            BuildTransparentNativeReplayOverlayFrameMessage(engine, settings));
        return Task.CompletedTask;
    }

    private void ClearNativeReplayOverlayForEmptyReplayWindowInBackground()
    {
        var engine = playbackEngine;
        var settings = chatSettings;
        if (engine is not { UsesNativeOverlay: true } ||
            IsProcessRunning(nativeOverlayProcess) ||
            settings is null ||
            settings.Layout != ChatLayout.Overlay ||
            IsDockedChatOverrideActive ||
            !IsChatVisible ||
            (!IsReplayMode && !IsBehindLive) ||
            string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName))
        {
            return;
        }

        QueueCriticalNativeReplayOverlayFrameWrite(
            engine.NativeOverlayPipeName!,
            BuildTransparentNativeReplayOverlayFrameMessage(engine, settings));
    }

    private void QueueCriticalNativeReplayOverlayFrameWrite(string pipeName, byte[] message)
    {
        nativeReplayOverlayFrameWriteGate.QueueWrite(
            pipeName,
            message,
            nativeReplayOverlayRenderState.Version,
            isCritical: true,
            writeKind: "critical-clear",
            replaySessionKey: GetNativeReplayOverlaySessionKey(),
            followupFrame: NativeOverlayChatFrameRenderer.BuildScrollbarStateFrameMessage(
                NativeReplayOverlayRenderedSelection.Empty,
                totalMessageCount: 0));
    }

    private byte[] BuildTransparentNativeReplayOverlayFrameMessage(IPlaybackEngine engine, ChatSettings settings)
    {
        return NativeOverlayChatFrameRenderer.BuildTransparentFrameMessage(
            CloneChatSettingsForNativeReplayRender(settings),
            GetNativeReplayOverlayVideoHeight(),
            engine.NativeOverlayPositionStatePath,
            out _,
            out _);
    }

    private async Task<(bool Sent, Exception? LastException)> TryWriteNativeOverlayMessageAsync(
        string pipeName,
        byte[] message,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        byte[]? followupMessage = null)
    {
        if (!NativeOverlayProtocolCodec.TryValidateEncodedMessage(message, out var invalidReason))
        {
            var exception = new InvalidDataException($"Invalid native-overlay message: {invalidReason}.");
            logger.Write(AppLogLevel.Warning, "ChatOverlay", exception.Message);
            return (false, exception);
        }

        if (followupMessage is not null &&
            !NativeOverlayProtocolCodec.TryValidateEncodedMessage(followupMessage, out invalidReason))
        {
            var exception = new InvalidDataException($"Invalid native-overlay follow-up message: {invalidReason}.");
            logger.Write(AppLogLevel.Warning, "ChatOverlay", exception.Message);
            return (false, exception);
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // ConnectAsync has its own timeout, but pipe writes and FlushAsync do not.  Use one
            // linked deadline for the complete operation so a connected-but-stalled overlay
            // cannot keep the replay writer blocked indefinitely.
            attemptCancellation.CancelAfter(remaining);
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                var connectTimeout = (int)Math.Clamp(
                    NativeOverlayPipeConnectTimeout.TotalMilliseconds,
                    1,
                    Math.Max(1, remaining.TotalMilliseconds));
                await pipe.ConnectAsync(connectTimeout, attemptCancellation.Token);
                await pipe.WriteAsync(message, attemptCancellation.Token);
                if (followupMessage is { Length: > 0 })
                {
                    await pipe.WriteAsync(followupMessage, attemptCancellation.Token);
                }

                await pipe.FlushAsync(attemptCancellation.Token);
                return (true, null);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                attemptCancellation.IsCancellationRequested)
            {
                lastException = new TimeoutException("The native VLC overlay pipe write timed out.");
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                lastException = ex;
                try
                {
                    await Task.Delay(50, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return (false, lastException);
                }
            }
        }

        return (false, lastException);
    }

    public bool TryReleaseNativeOverlayChatInputFocus()
    {
        var process = nativeOverlayProcess;
        var pipeName = nativeOverlayPipeName;

        // The controller is detached before its asynchronous shutdown completes. During that
        // interval the process can still own the global keyboard hook even though the tracked
        // process/pipe fields have already been cleared. The playback engine keeps the stable
        // per-player pipe name for the lifetime of the native overlay, so use it as the bounded
        // release path while native-overlay playback is still active.
        if (string.IsNullOrWhiteSpace(pipeName) &&
            playbackEngine is { UsesNativeOverlay: true } engine &&
            !string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName))
        {
            pipeName = engine.NativeOverlayPipeName;
        }

        if (string.IsNullOrWhiteSpace(pipeName) ||
            (process is not null && !IsProcessRunning(process)) ||
            (process is null && playbackEngine?.UsesNativeOverlay != true))
        {
            return false;
        }

        return TryWriteNativeOverlayEventSynchronously(
            $"{pipeName}_events",
            NativeOverlayProtocolCodec.BuildEventMessage(
                NativeOverlayProtocolCodec.ChatInputFocusEventType,
                0),
            NativeOverlayInputFocusReleaseTimeout);
    }

    private static bool TryWriteNativeOverlayEventSynchronously(
        string pipeName,
        byte[] message,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.None);
                var remaining = Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds);
                var connectTimeout = (int)Math.Clamp(
                    NativeOverlayPipeConnectTimeout.TotalMilliseconds,
                    1,
                    remaining);
                pipe.Connect(connectTimeout);
                pipe.Write(message, 0, message.Length);
                pipe.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                var remainingDelay = deadline - DateTimeOffset.UtcNow;
                if (remainingDelay <= TimeSpan.Zero)
                {
                    break;
                }

                Thread.Sleep((int)Math.Clamp(remainingDelay.TotalMilliseconds, 1, 10));
            }
        }

        return false;
    }

    private void LogNativeOverlayLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        logger.Write(AppLogLevel.Info, "ChatOverlay", line);
    }

    private static string? WriteOverlayTokenFile(string platform, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamlinkVlcStudio",
            "overlay-tokens");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{platform}-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, token ?? "", new UTF8Encoding(false));
        return path;
    }

    private static string? FindTwitchBadgeManifestPath()
    {
        return BundledBadgeAssets.FindTwitchBadgeManifestPath();
    }

    private static string? FindKickBadgeManifestPath()
    {
        return BundledBadgeAssets.FindKickBadgeManifestPath();
    }

    private string BuildNativeOverlayLaunchKey(
        AppSettings settings,
        KickOverlayChannelInfo? kickInfo = null,
        string? kickToken = null,
        string? kickBadgeManifestPath = null,
        string? twitchBadgeManifestPath = null)
    {
        var chat = settings.Chat;
        var overlayDirectory = ResolveActiveNativeOverlayDirectory(chat) ?? "";
        var parts = new List<string>
        {
            Target.Platform.ToString(),
            Target.Channel,
            overlayDirectory,
            NativeOverlaySizing
                .ClampReferenceWidth((int)Math.Round(chat.DockWidth))
                .ToString(CultureInfo.InvariantCulture),
            GetNativeOverlayFontSize(settings).ToString(CultureInfo.InvariantCulture)
        };

        if (Target.Platform == PlatformKind.Kick)
        {
            var effectiveKickInfo = kickInfo ?? resolvedKickOverlayChannelInfo;
            var effectiveKickChatroomId = effectiveKickInfo?.ChatroomId;
            var effectiveKickBroadcasterUserId = effectiveKickInfo?.BroadcasterUserId?.ToString(CultureInfo.InvariantCulture);
            kickBadgeManifestPath ??= FindKickBadgeManifestPath();
            parts.Add(FileFingerprint(kickBadgeManifestPath));
            parts.Add(chat.KickSendAsBot ? "bot" : "user");
            parts.Add(GetConfiguredKickSetting(chat, broadcaster: false, effectiveKickChatroomId));
            parts.Add(GetConfiguredKickSetting(chat, broadcaster: true, effectiveKickBroadcasterUserId));
            parts.Add(TokenFingerprint(kickToken ?? chat.KickOAuthToken));
            parts.Add(TokenFingerprint(chat.KickClientId));
            parts.Add(TokenFingerprint(chat.KickClientSecret));
        }
        else
        {
            twitchBadgeManifestPath ??= FindTwitchBadgeManifestPath();
            parts.Add(FileFingerprint(twitchBadgeManifestPath));
            parts.Add(chat.TwitchUsername.Trim());
            parts.Add(TokenFingerprint(chat.TwitchOAuthToken));
            parts.Add(TokenFingerprint(chat.TwitchClientId));
            parts.Add(resolvedTwitchOverlayRoomId ?? "");
        }

        return string.Join("|", parts);
    }

    private async Task<string?> ResolveTwitchOverlayRoomIdAsync(ChatSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(resolvedTwitchOverlayRoomId))
        {
            return resolvedTwitchOverlayRoomId;
        }

        var token = TwitchOAuthService.NormalizeOAuthToken(settings.TwitchOAuthToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            using var httpClient = HttpClientFactory.Create(TimeSpan.FromSeconds(15), includeUserAgent: true);
            var clientId = settings.TwitchClientId.Trim();
            TwitchTokenInfo? tokenInfo = null;

            if (string.IsNullOrWhiteSpace(clientId))
            {
                tokenInfo = await TwitchOAuthService.ValidateTokenAsync(httpClient, token, cancellationToken);
                clientId = tokenInfo.ClientId.Trim();
            }

            if (string.IsNullOrWhiteSpace(clientId))
            {
                return null;
            }

            if (tokenInfo is not null &&
                string.Equals(tokenInfo.Login, Target.Channel, StringComparison.OrdinalIgnoreCase) &&
                IsAsciiDigits(tokenInfo.UserId))
            {
                return CacheResolvedTwitchOverlayRoomId(tokenInfo.UserId);
            }

            var escapedChannel = Uri.EscapeDataString(Target.Channel.Trim());
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/users?login={escapedChannel}");
            request.Headers.Authorization = new("Bearer", token);
            request.Headers.TryAddWithoutValidation("Client-Id", clientId);
            request.Headers.UserAgent.ParseAdd("StreamlinkVlcStudio/0.1");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "ChatOverlay",
                    $"Twitch room ID lookup failed for {Target.Channel}: {(int)response.StatusCode} {response.ReasonPhrase}.");
                return null;
            }

            var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken);
            using var document = JsonDocument.Parse(responseBody);
            if (TryReadTwitchHelixUserId(document.RootElement, out var roomId))
            {
                return CacheResolvedTwitchOverlayRoomId(roomId);
            }

            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Twitch room ID lookup did not return a user ID for {Target.Channel}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Twitch room ID lookup failed for {Target.Channel}.", ex);
        }

        return null;
    }

    private string CacheResolvedTwitchOverlayRoomId(string roomId)
    {
        resolvedTwitchOverlayRoomId = roomId;
        return roomId;
    }

    private static bool TryReadTwitchHelixUserId(JsonElement root, out string roomId)
    {
        roomId = "";
        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() <= 0)
        {
            return false;
        }

        var user = data[0];
        if (!user.TryGetProperty("id", out var id))
        {
            return false;
        }

        var value = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
        if (string.IsNullOrWhiteSpace(value) || !IsAsciiDigits(value))
        {
            return false;
        }

        roomId = value;
        return true;
    }

    private static bool IsAsciiDigits(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string?> ResolveKickOverlayTokenAsync(ChatSettings settings, CancellationToken cancellationToken)
    {
        return await KickOAuthService.GetUsableAccessTokenAsync(
            settings,
            ApplyKickTokenResultOnUiThreadAsync,
            logger,
            cancellationToken);
    }

    private async Task ApplyKickTokenResultOnUiThreadAsync(
        ChatSettings settings,
        KickOAuthTokenResult token,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state =>
            {
                var source = (TaskCompletionSource)state!;
                source.TrySetCanceled();
            },
            completion);

        dispatch(() =>
        {
            try
            {
                KickOAuthService.ApplyTokenResult(settings, token);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    private string GetConfiguredKickSetting(
        ChatSettings settings,
        bool broadcaster,
        string? fallback = null)
    {
        var found = broadcaster
            ? settings.TryGetKickBroadcasterUserId(Target.Channel, out var value)
            : settings.TryGetKickChatroomId(Target.Channel, out value);
        return found && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : string.IsNullOrWhiteSpace(fallback) ? "" : fallback.Trim();
    }

    private void ApplyAudio()
    {
        if (playbackEngine is null)
        {
            return;
        }

        playbackEngine.SetAudioState(Volume, CurrentAudioState);
        AudioStateApplied?.Invoke(this, EventArgs.Empty);
    }

    private PlaybackAudioState CurrentAudioState => IsMuted
        ? PlaybackAudioState.HardMuted
        : isSelectedForAudio
            ? PlaybackAudioState.Audible
            : PlaybackAudioState.Muted;

    internal static int NormalizeVolume(int value)
    {
        return Math.Clamp(value, VolumeLimits.Min, VolumeLimits.Max);
    }

    private static string BuildNativeOverlayPositionStatePath(StreamTarget target)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StreamlinkVlcStudio",
            "vlc-overlays",
            "streams");
        Directory.CreateDirectory(directory);

        var key = target.StateKey;
        var slug = BuildFileSlug(key);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..12];
        return Path.Combine(directory, $"{slug}-{hash}.txt");
    }

    private static string BuildFileSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        var slug = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "stream";
        }

        return slug.Length <= 80 ? slug : slug[..80].TrimEnd('-');
    }

    private static string TokenFingerprint(string token)
    {
        var normalized = token.Trim();
        if (normalized.Length == 0)
        {
            return "";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string FileFingerprint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "";
        }

        try
        {
            var info = new FileInfo(path);
            return $"{info.FullName}|{info.Length.ToString(CultureInfo.InvariantCulture)}|{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return path;
        }
    }

    private async Task<KickOverlayChannelInfo> ResolveKickOverlayChannelInfoAsync(ChatSettings settings, bool sendAsBot, CancellationToken cancellationToken)
    {
        string? chatroomId = null;
        long? broadcasterUserId = null;
        var hasConfiguredBroadcasterUserId = false;
        var cachedInfo = resolvedKickOverlayChannelInfo;

        if (settings.TryGetKickChatroomId(Target.Channel, out var configuredChatroomId) &&
            !string.IsNullOrWhiteSpace(configuredChatroomId))
        {
            chatroomId = KickChannelInfoJson.NormalizeNumericId(configuredChatroomId);
        }

        if (settings.TryGetKickBroadcasterUserId(Target.Channel, out var configuredBroadcasterUserId) &&
            long.TryParse(configuredBroadcasterUserId, out var parsedBroadcasterUserId))
        {
            broadcasterUserId = parsedBroadcasterUserId;
            hasConfiguredBroadcasterUserId = true;
        }

        chatroomId ??= cachedInfo?.ChatroomId;
        broadcasterUserId ??= cachedInfo?.BroadcasterUserId;

        if (!string.IsNullOrWhiteSpace(chatroomId) && (sendAsBot || broadcasterUserId is not null))
        {
            return CacheResolvedKickOverlayChannelInfo(new KickOverlayChannelInfo(chatroomId, broadcasterUserId));
        }

        try
        {
            var metadata = await TryResolveKickChannelMetadataAsync(Target.Channel, cancellationToken);
            chatroomId ??= metadata.ChatroomId;
            broadcasterUserId ??= metadata.BroadcasterUserId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Kick overlay metadata lookup failed for {Target.Channel}.", ex);
        }

        if (!sendAsBot && !hasConfiguredBroadcasterUserId)
        {
            var publicApiBroadcasterUserId = await KickOAuthService.TryResolveBroadcasterUserIdAsync(
                Target.Channel,
                settings,
                ApplyKickTokenResultOnUiThreadAsync,
                logger,
                cancellationToken);
            if (publicApiBroadcasterUserId is not null)
            {
                broadcasterUserId = publicApiBroadcasterUserId;
            }
        }

        return CacheResolvedKickOverlayChannelInfo(new KickOverlayChannelInfo(chatroomId, broadcasterUserId));
    }

    private KickOverlayChannelInfo CacheResolvedKickOverlayChannelInfo(KickOverlayChannelInfo info)
    {
        var cached = resolvedKickOverlayChannelInfo;
        var merged = new KickOverlayChannelInfo(
            string.IsNullOrWhiteSpace(info.ChatroomId) ? cached?.ChatroomId : info.ChatroomId,
            info.BroadcasterUserId ?? cached?.BroadcasterUserId);

        if (!string.IsNullOrWhiteSpace(merged.ChatroomId) || merged.BroadcasterUserId is not null)
        {
            resolvedKickOverlayChannelInfo = merged;
        }

        return merged;
    }

    private async Task<KickOverlayChannelInfo> TryResolveKickChannelMetadataAsync(string channel, CancellationToken cancellationToken)
    {
        try
        {
            if (await TryResolveKickChannelMetadataWithHttpClientAsync(channel, cancellationToken) is { ChatroomId: not null } httpMetadata)
            {
                return httpMetadata;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Kick metadata lookup with .NET HTTP failed for {channel}.", ex);
        }

        try
        {
            if (await TryResolveKickChannelMetadataWithCurlAsync(channel, cancellationToken) is { ChatroomId: not null } curlMetadata)
            {
                AddSystemMessage("Resolved Kick chatroom ID with curl fallback.");
                return curlMetadata;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Kick metadata lookup with curl.exe failed for {channel}.", ex);
        }

        return new KickOverlayChannelInfo(null, null);
    }

    private static async Task<KickOverlayChannelInfo?> TryResolveKickChannelMetadataWithHttpClientAsync(string channel, CancellationToken cancellationToken)
    {
        using var httpClient = HttpClientFactory.Create(
            TimeSpan.FromSeconds(18),
            includeUserAgent: true,
            acceptJson: true);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) StreamlinkVlcStudio/0.1");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");

        var escapedChannel = Uri.EscapeDataString(channel);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://kick.com/api/v2/channels/{escapedChannel}");
        request.Headers.Referrer = new Uri($"https://kick.com/{escapedChannel}");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var responseBody = await BoundedHttpContentReader.ReadJsonAsync(response.Content, cancellationToken);
        using var document = JsonDocument.Parse(responseBody);
        return ToKickOverlayChannelInfo(KickChannelInfoJson.Read(document.RootElement));
    }

    private async Task<KickOverlayChannelInfo?> TryResolveKickChannelMetadataWithCurlAsync(string channel, CancellationToken cancellationToken)
    {
        var curlPath = KickCurlArguments.ResolveCurlPath();

        var escapedChannel = Uri.EscapeDataString(channel);
        var startInfo = BoundedProcessRunner.CreateRedirectedStartInfo(
            curlPath,
            KickCurlArguments.BuildJsonRequest(
                $"https://kick.com/api/v2/channels/{escapedChannel}",
                $"https://kick.com/{escapedChannel}"));
        var result = await processRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(18),
            cancellationToken);

        if (result.TimedOut)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"curl.exe timed out resolving Kick metadata for {channel}.");
            return null;
        }

        if (result.ExitCode != 0 ||
            result.OutputWasTruncated ||
            string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"curl.exe failed resolving Kick metadata for {channel}: {result.StandardError.Trim()}");
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return ToKickOverlayChannelInfo(KickChannelInfoJson.Read(document.RootElement));
    }

    private static KickOverlayChannelInfo ToKickOverlayChannelInfo(KickChannelInfo channelInfo)
    {
        return new KickOverlayChannelInfo(channelInfo.ChatroomId, channelInfo.BroadcasterUserId);
    }

    private static string NormalizeOutgoingMessage(string message)
    {
        return ChatTextNormalizer.NormalizeSingleLine(message, 500);
    }

    private void QueueNativeChatOverlayUpdateAfterReplayWindowApply()
    {
        if (playbackEngine?.UsesNativeOverlay != true)
        {
            UpdateNativeChatOverlay();
            return;
        }

        lock (nativeReplayOverlayRefreshGate)
        {
            if (nativeReplayOverlayRefreshQueued)
            {
                return;
            }

            nativeReplayOverlayRefreshQueued = true;
        }

        _ = RunQueuedNativeReplayOverlayRefreshAsync();
    }

    private void MarkNativeReplayOverlayRefreshPendingAfterSeek()
    {
        lock (nativeReplayOverlayRefreshGate)
        {
            nativeReplayOverlayRefreshPendingAfterSeek = true;
        }
    }

    private void FlushNativeReplayOverlayRefreshAfterSeek()
    {
        var shouldRefresh = false;
        lock (nativeReplayOverlayRefreshGate)
        {
            if (nativeReplayOverlayRefreshPendingAfterSeek)
            {
                nativeReplayOverlayRefreshPendingAfterSeek = false;
                shouldRefresh = true;
            }
        }

        if (shouldRefresh)
        {
            QueueNativeChatOverlayUpdateAfterReplayWindowApply();
        }
    }

    private async Task RunQueuedNativeReplayOverlayRefreshAsync()
    {
        await Task.Delay(NativeReplayOverlayRefreshDelay).ConfigureAwait(false);
        dispatch(ApplyQueuedNativeReplayOverlayRefresh);
    }

    private void ApplyQueuedNativeReplayOverlayRefresh()
    {
        lock (nativeReplayOverlayRefreshGate)
        {
            nativeReplayOverlayRefreshQueued = false;
        }

        UpdateNativeChatOverlay();
    }

    private void UpdateNativeChatOverlay()
    {
        // The custom VLC plugin overlay renders chat. The replay frame pipeline resets its own
        // state when the plugin overlay is unavailable, so there is no fallback path to drive here.
        UpdateNativeReplayChatOverlay();
    }

    private void UpdateNativeReplayChatOverlay()
    {
        UpdateNativeReplayChatOverlay(
            forceAnimationRepaint: false,
            GetNativeReplayOverlayAnimationClock());
    }

    private void UpdateNativeReplayChatOverlay(bool forceAnimationRepaint, TimeSpan animationClock)
    {
        var engine = playbackEngine;
        if (engine is not { UsesNativeOverlay: true } ||
            IsProcessRunning(nativeOverlayProcess))
        {
            ResetNativeReplayOverlayFrameState();
            StopNativeReplayOverlayEventHost();
            return;
        }

        var settings = chatSettings;
        if (settings is null ||
            settings.Layout != ChatLayout.Overlay ||
            IsDockedChatOverrideActive ||
            !IsChatVisible ||
            (!IsReplayMode && !IsBehindLive) ||
            string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName) ||
            string.IsNullOrWhiteSpace(engine.NativeOverlayPositionStatePath))
        {
            ResetNativeReplayOverlayFrameState();
            StopNativeReplayOverlayEventHost();
            return;
        }

        StartNativeReplayOverlayEventHost(
            engine.NativeOverlayPipeName!,
            engine.NativeOverlayPositionStatePath!);

        var messages = GetNativeReplayOverlayMessages();
        var replaySessionKey = GetNativeReplayOverlaySessionKey();
        EnsureNativeReplayOverlayScrollSession(replaySessionKey);
        var messageOffset = ResolveNativeReplayOverlayMessageOffset(messages);
        if (messages.Length == 0)
        {
            CancelNativeReplayOverlayAnimationState();
        }
        else
        {
            ScheduleNativeReplayOverlayWarmupRefresh(
                engine.NativeOverlayPipeName!,
                engine.NativeOverlayPositionStatePath,
                messages);
        }

        var videoHeight = 0;
        if (engine.TryGetVideoSize(out var detectedVideoWidth, out var detectedVideoHeight) && detectedVideoHeight > 0)
        {
            videoHeight = detectedVideoHeight;
            RecordNativeReplayOverlayVideoSize(detectedVideoWidth, detectedVideoHeight);
        }

        var overlayFontSize = currentSettings is null
            ? settings.VlcOverlayFontSize
            : GetNativeOverlayFontSize(currentSettings);
        var renderContentVersion = Volatile.Read(ref nativeReplayOverlayRenderContentVersion);
        var frameKey = BuildNativeReplayOverlayFrameKey(
            engine.NativeOverlayPipeName!,
            engine.NativeOverlayPositionStatePath,
            settings,
            overlayFontSize,
            videoHeight,
            messages,
            messageOffset,
            replaySessionKey);
        var renderPlan = nativeReplayOverlayRenderState.BeginRender(
            frameKey,
            forceAnimationRepaint,
            animationClock);
        if (renderPlan is not { } plan)
        {
            return;
        }

        if (plan.VersionAdvanced)
        {
            nativeReplayOverlayFrameWriteGate.Invalidate();
        }

        var imageCachePinOwner = new object();
        var request = new NativeReplayOverlayFrameRequest(
            plan.Version,
            engine.NativeOverlayPipeName!,
            messages,
            CloneChatSettingsForNativeReplayRender(settings),
            overlayFontSize,
            videoHeight,
            engine.NativeOverlayPositionStatePath,
            plan.FrameKey,
            MessageOffset: messageOffset,
            ScrollSessionKey: replaySessionKey,
            AnimationClock: plan.AnimationClock,
            ImageCachePinOwner: imageCachePinOwner,
            RenderContentVersion: renderContentVersion);
        _ = QueueNativeReplayOverlayFrameAsync(request);
    }

    private ChatMessage[] GetNativeReplayOverlayMessages()
    {
        var messages = ChatMessages
            .Where(ShouldRenderNativeReplayOverlayMessage)
            .ToArray();
        if (!Target.IsExplicitKickVod)
        {
            return messages;
        }

        var statusMessages = DockedChatMessages
            .Where(IsKickVodReplayChatStatusMessage)
            .Where(message => !messages.Contains(message))
            .ToArray();
        return statusMessages.Length == 0
            ? messages
            : messages
                .Concat(statusMessages)
                .TakeLast(MaxChatMessages)
                .ToArray();
    }

    private void ScrollNativeReplayOverlay(int wheelNotches)
    {
        if (wheelNotches == 0 || (!IsReplayMode && !IsBehindLive))
        {
            return;
        }

        var changed = false;
        lock (nativeReplayOverlayScrollGate)
        {
            var requestedOffset = (long)nativeReplayOverlayMessageOffset +
                (long)wheelNotches * NativeReplayOverlayMessagesPerScrollNotch;
            var nextOffset = (int)Math.Clamp(
                requestedOffset,
                0,
                nativeReplayOverlayMaximumMessageOffset);
            if (nextOffset != nativeReplayOverlayMessageOffset)
            {
                nativeReplayOverlayMessageOffset = nextOffset;
                nativeReplayOverlayAnchorMessage = null;
                changed = true;
            }
        }

        if (changed)
        {
            InvalidateNativeReplayOverlayFrame();
        }
    }

    private void SetNativeReplayOverlayScrollPosition(int messageOffset)
    {
        if (messageOffset < 0 || (!IsReplayMode && !IsBehindLive))
        {
            return;
        }

        var changed = false;
        lock (nativeReplayOverlayScrollGate)
        {
            var nextOffset = Math.Clamp(
                messageOffset,
                0,
                nativeReplayOverlayMaximumMessageOffset);
            if (nextOffset != nativeReplayOverlayMessageOffset)
            {
                nativeReplayOverlayMessageOffset = nextOffset;
                nativeReplayOverlayAnchorMessage = null;
                changed = true;
            }
        }

        if (changed)
        {
            InvalidateNativeReplayOverlayFrame();
        }
    }

    private void EnsureNativeReplayOverlayScrollSession(string replaySessionKey)
    {
        lock (nativeReplayOverlayScrollGate)
        {
            if (string.Equals(
                    nativeReplayOverlayScrollSessionKey,
                    replaySessionKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            ResetNativeReplayOverlayScrollStateLocked(replaySessionKey);
        }
    }

    private int ResolveNativeReplayOverlayMessageOffset(IReadOnlyList<ChatMessage> messages)
    {
        lock (nativeReplayOverlayScrollGate)
        {
            if (nativeReplayOverlayMessageOffset == 0 ||
                nativeReplayOverlayAnchorMessage is not { } anchorMessage)
            {
                return nativeReplayOverlayMessageOffset;
            }

            for (var index = messages.Count - 1; index >= 0; index--)
            {
                if (!IsSameNativeReplayOverlayMessage(messages[index], anchorMessage))
                {
                    continue;
                }

                nativeReplayOverlayMessageOffset = messages.Count - 1 - index;
                return nativeReplayOverlayMessageOffset;
            }

            // The anchored message aged out of the replay window. Stay on the oldest
            // complete page instead of jumping forward to newer chat.
            nativeReplayOverlayMessageOffset = int.MaxValue;
            nativeReplayOverlayAnchorMessage = null;
            return nativeReplayOverlayMessageOffset;
        }
    }

    private void ApplyNativeReplayOverlayRenderedSelection(
        NativeReplayOverlayFrameRequest request,
        NativeReplayOverlayRenderedSelection selection)
    {
        if (request.Messages.Count == 0)
        {
            return;
        }

        lock (nativeReplayOverlayScrollGate)
        {
            if (!string.Equals(
                    nativeReplayOverlayScrollSessionKey,
                    request.ScrollSessionKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            nativeReplayOverlayMessageOffset = selection.MessageOffset;
            nativeReplayOverlayMaximumMessageOffset = selection.MaximumMessageOffset;
            nativeReplayOverlayAnchorMessage = selection.MessageOffset > 0 &&
                selection.NewestMessageIndex >= 0 &&
                selection.NewestMessageIndex < request.Messages.Count
                    ? request.Messages[selection.NewestMessageIndex]
                    : null;
        }
    }

    private void ResetNativeReplayOverlayScrollState()
    {
        lock (nativeReplayOverlayScrollGate)
        {
            ResetNativeReplayOverlayScrollStateLocked("");
        }
    }

    private void ResetNativeReplayOverlayScrollStateLocked(string replaySessionKey)
    {
        nativeReplayOverlayMessageOffset = 0;
        nativeReplayOverlayMaximumMessageOffset = 0;
        nativeReplayOverlayAnchorMessage = null;
        nativeReplayOverlayScrollSessionKey = replaySessionKey;
    }

    private static bool IsSameNativeReplayOverlayMessage(ChatMessage candidate, ChatMessage anchor)
    {
        if (ReferenceEquals(candidate, anchor))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(candidate.MessageId) ||
            !string.IsNullOrWhiteSpace(anchor.MessageId))
        {
            return candidate.Platform == anchor.Platform &&
                string.Equals(candidate.Channel, anchor.Channel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.MessageId, anchor.MessageId, StringComparison.Ordinal);
        }

        return candidate == anchor;
    }

    private void StartNativeReplayOverlayEventHost(string pipeName, string positionStatePath)
    {
        var isReconnect = !nativeReplayOverlayEventHost.IsRunning ||
            !string.Equals(nativeReplayOverlayEventHost.PipeName, pipeName, StringComparison.Ordinal);
        nativeReplayOverlayEventHost.Start(pipeName, positionStatePath);
        if (isReconnect)
        {
            nativeReplayOverlayFrameWriteGate.NotifyReconnected(pipeName);
        }
    }

    private void SuspendNativeReplayOverlayResizePersistence()
    {
        Volatile.Write(
            ref nativeReplayOverlayResizePersistenceResumeAfterVersion,
            nativeReplayOverlayRenderState.Version);
        nativeReplayOverlayEventHost.SuspendResizePersistence();
    }

    private void ResumeNativeReplayOverlayResizePersistence(NativeReplayOverlayFrameWriteRequest request)
    {
        if (request.Version > Volatile.Read(ref nativeReplayOverlayResizePersistenceResumeAfterVersion) &&
            !string.Equals(request.WriteKind, "critical-clear", StringComparison.Ordinal))
        {
            nativeReplayOverlayEventHost.ResumeResizePersistence();
        }
    }

    private void StopNativeReplayOverlayEventHost()
    {
        ResetNativeReplayOverlayFrameState();
        nativeReplayOverlayEventHost.Stop();
    }

    private Task StopNativeReplayOverlayEventHostAsync()
    {
        ResetNativeReplayOverlayFrameState();
        return nativeReplayOverlayEventHost.StopAsync();
    }

    private void InvalidateNativeReplayOverlayFrame()
    {
        nativeReplayOverlayRenderState.InvalidateFrameKey();
        UpdateNativeReplayChatOverlay();
    }

    private void InvalidateNativeReplayOverlayFrameIfReplayChatVisible()
    {
        if (!HasNativeReplayOverlayRenderableMessages())
        {
            return;
        }

        InvalidateNativeReplayOverlayFrame();
    }

    private bool HasNativeReplayOverlayRenderableMessages()
    {
        return ChatMessages.Any(ShouldRenderNativeReplayOverlayMessage) ||
            (Target.IsExplicitKickVod && DockedChatMessages.Any(IsKickVodReplayChatStatusMessage));
    }

    private void ScheduleNativeReplayOverlayWarmupRefresh(
        string pipeName,
        string? positionStatePath,
        IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var sessionKey = BuildNativeReplayOverlayWarmupSessionKey(pipeName, positionStatePath);
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation = null;
        long warmupVersion;
        lock (nativeReplayOverlayRefreshGate)
        {
            if (string.Equals(nativeReplayOverlayWarmupSessionKey, sessionKey, StringComparison.Ordinal))
            {
                cancellation.Dispose();
                return;
            }

            previousCancellation = nativeReplayOverlayWarmupCancellation;
            nativeReplayOverlayWarmupSessionKey = sessionKey;
            nativeReplayOverlayWarmupCancellation = cancellation;
            warmupVersion = ++nativeReplayOverlayWarmupVersion;
        }

        previousCancellation?.Cancel();
        _ = RunNativeReplayOverlayWarmupRefreshAsync(cancellation, warmupVersion);
    }

    private string BuildNativeReplayOverlayWarmupSessionKey(string pipeName, string? positionStatePath)
    {
        var replay = replaySession;
        return string.Join(
            "|",
            pipeName,
            positionStatePath ?? "",
            replay?.Platform.ToString() ?? Target.Platform.ToString(),
            replay?.Channel ?? Target.Channel,
            replay?.ReplayId ?? Target.Url,
            replay?.StreamStartedAtUtc?.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    private string GetNativeReplayOverlaySessionKey()
    {
        var replay = replaySession;
        return string.Join(
            ":",
            replay?.Platform.ToString() ?? Target.Platform.ToString(),
            replay?.Channel ?? Target.Channel,
            replay?.ReplayId ?? Target.Url,
            IsReplayMode || IsBehindLive ? "replay" : "live");
    }

    private async Task RunNativeReplayOverlayWarmupRefreshAsync(
        CancellationTokenSource cancellation,
        long warmupVersion)
    {
        try
        {
            foreach (var delay in NativeReplayOverlayWarmupRefreshDelays)
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
                if (!IsNativeReplayOverlayWarmupCurrent(cancellation, warmupVersion))
                {
                    return;
                }

                dispatch(() =>
                {
                    if (IsNativeReplayOverlayWarmupCurrent(cancellation, warmupVersion))
                    {
                        InvalidateNativeReplayOverlayFrameIfReplayChatVisible();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (nativeReplayOverlayRefreshGate)
            {
                if (ReferenceEquals(nativeReplayOverlayWarmupCancellation, cancellation) &&
                    nativeReplayOverlayWarmupVersion == warmupVersion)
                {
                    nativeReplayOverlayWarmupCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private bool IsNativeReplayOverlayWarmupCurrent(
        CancellationTokenSource cancellation,
        long warmupVersion)
    {
        lock (nativeReplayOverlayRefreshGate)
        {
            return ReferenceEquals(nativeReplayOverlayWarmupCancellation, cancellation) &&
                nativeReplayOverlayWarmupVersion == warmupVersion;
        }
    }

    private void CancelNativeReplayOverlayWarmupRefresh()
    {
        CancellationTokenSource? cancellation;
        lock (nativeReplayOverlayRefreshGate)
        {
            cancellation = nativeReplayOverlayWarmupCancellation;
            nativeReplayOverlayWarmupCancellation = null;
            nativeReplayOverlayWarmupSessionKey = "";
            nativeReplayOverlayWarmupVersion++;
        }

        cancellation?.Cancel();
    }

    private void ResetNativeReplayOverlayFrameState()
    {
        lock (nativeReplayOverlayRefreshGate)
        {
            nativeReplayOverlayRefreshPendingAfterSeek = false;
            nativeReplayOverlayVideoWidth = 0;
            nativeReplayOverlayVideoHeight = 0;
        }

        ResetNativeReplayOverlayScrollState();
        CancelNativeReplayOverlayWarmupRefresh();
        nativeReplayOverlayRenderState.Reset();
        Interlocked.Exchange(ref nativeReplayOverlayAnimationEpochTimestamp, Stopwatch.GetTimestamp());
        CancelNativeReplayOverlayAnimationState();
        SuspendNativeReplayOverlayResizePersistence();
        nativeReplayOverlayFrameWriteGate.Invalidate(includeCritical: true);
        nativeReplayOverlayFrameScheduler?.CancelPending();
    }

    private async Task QueueNativeReplayOverlayFrameAsync(NativeReplayOverlayFrameRequest request)
    {
        try
        {
            var scheduler = await GetNativeReplayOverlayFrameSchedulerAsync().ConfigureAwait(false);
            if (!disposed)
            {
                scheduler.QueueRender(request);
            }
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", "Could not start the native VLC replay overlay renderer.", ex);
        }
    }

    private async Task<NativeReplayOverlayFrameScheduler> GetNativeReplayOverlayFrameSchedulerAsync()
    {
        Task<NativeReplayOverlayFrameScheduler> creationTask;
        lock (nativeReplayOverlayFrameSchedulerGate)
        {
            if (nativeReplayOverlayFrameScheduler is not null)
            {
                return nativeReplayOverlayFrameScheduler;
            }

            nativeReplayOverlayFrameSchedulerCreationTask ??=
                NativeReplayOverlayFrameScheduler.CreateAsync(
                    logger,
                    OnNativeReplayOverlayFrameRendered,
                    lifetimeCancellation.Token);
            creationTask = nativeReplayOverlayFrameSchedulerCreationTask;
        }

        NativeReplayOverlayFrameScheduler scheduler;
        try
        {
            scheduler = await creationTask.ConfigureAwait(false);
        }
        catch
        {
            lock (nativeReplayOverlayFrameSchedulerGate)
            {
                if (ReferenceEquals(nativeReplayOverlayFrameSchedulerCreationTask, creationTask))
                {
                    nativeReplayOverlayFrameSchedulerCreationTask = null;
                }
            }

            throw;
        }

        var disposeScheduler = false;
        lock (nativeReplayOverlayFrameSchedulerGate)
        {
            if (disposed)
            {
                disposeScheduler = true;
            }
            else
            {
                nativeReplayOverlayFrameScheduler = scheduler;
            }
        }

        if (disposeScheduler)
        {
            await scheduler.DisposeAsync();
            throw new OperationCanceledException(lifetimeCancellation.Token);
        }

        return scheduler;
    }

    private int GetNativeReplayOverlayVideoHeight()
    {
        return playbackEngine?.TryGetVideoSize(out _, out var height) == true && height > 0
            ? height
            : 0;
    }

    private async Task<NativeReplayOverlayFrameWriteResult> WriteNativeReplayOverlayFrameMessageAsync(
        NativeReplayOverlayFrameWriteRequest request,
        CancellationToken cancellationToken)
    {
        var (sent, lastException) = await TryWriteNativeOverlayMessageAsync(
            request.PipeName,
            request.Frame,
            NativeReplayOverlayFrameWriteTimeout,
            cancellationToken,
            request.FollowupFrame);
        return new NativeReplayOverlayFrameWriteResult(sent, lastException);
    }

    private void OnNativeReplayOverlayFrameWriteFailed(Exception exception)
    {
        nativeReplayOverlayRenderState.InvalidateFrameKey();
        logger.Write(AppLogLevel.Warning, "ChatOverlay", "Could not update the native VLC replay chat overlay.", exception);
        if (IsReplayMode || IsBehindLive)
        {
            dispatch(InvalidateNativeReplayOverlayFrame);
        }
    }

    private void OnNativeReplayOverlayFrameRendered(NativeReplayOverlayFrameResult result)
    {
        if (!nativeReplayOverlayRenderState.IsCurrent(result.Request.Version))
        {
            ReleaseNativeReplayOverlayImageCachePins(result.Request.ImageCachePinOwner);
            return;
        }

        if (!result.Succeeded || result.Frame is null)
        {
            ReleaseNativeReplayOverlayImageCachePins(result.Request.ImageCachePinOwner);
            nativeReplayOverlayRenderState.InvalidateFrameKey();
            CancelNativeReplayOverlayAnimationState();
            return;
        }

        if (!TryAdoptNativeReplayOverlayImageCachePins(
                result.Request.ImageCachePinOwner,
                result.Request.Version))
        {
            return;
        }

        if (result.Request.Messages.Count > 0)
        {
            nativeReplayOverlayFrameWriteGate.SupersedePersistentCriticalClears();
        }

        ApplyNativeReplayOverlayRenderedSelection(result.Request, result.RenderedSelection);
        TrackNativeReplayOverlayPendingImageLoads(result.PendingImageLoads);
        if (!result.HasAnimatedContent)
        {
            CancelNativeReplayOverlayAnimationTimer();
        }

        // Empty frames clear the native plugin's placeholder while replay chat is loading or
        // when the selected timestamp has no messages. The write gate cancels this persistent
        // clear as soon as a loaded chat frame is rendered, so it cannot starve that frame.
        var isCriticalWrite = result.Request.Messages.Count == 0 ||
            (Target.IsExplicitKickVod &&
                result.Request.Messages.All(IsKickVodReplayChatStatusMessage));
        var writeKind = result.Request.Messages.Count == 0
            ? "blank-frame"
            : Target.IsExplicitKickVod && result.Request.Messages.All(IsKickVodReplayChatStatusMessage)
                ? "status-frame"
                : "chat-frame";
        nativeReplayOverlayFrameWriteGate.QueueWrite(
            result.Request.PipeName,
            result.Frame,
            result.Request.Version,
            result.Request.FrameKey,
            result.Request.AnimationClock,
            result.HasAnimatedContent,
            result.NextAnimationFrameDelay,
            result.RenderDuration,
            isCritical: isCriticalWrite,
            writeKind: writeKind,
            replaySessionKey: GetNativeReplayOverlaySessionKey(),
            followupFrame: NativeOverlayChatFrameRenderer.BuildScrollbarStateFrameMessage(
                result.RenderedSelection,
                result.Request.Messages.Count));
    }

    private void OnNativeReplayOverlayFrameWriteSucceeded(NativeReplayOverlayFrameWriteRequest request)
    {
        if (!nativeReplayOverlayRenderState.IsCurrent(request.Version))
        {
            return;
        }

        ResumeNativeReplayOverlayResizePersistence(request);
        if (!request.HasAnimatedContent)
        {
            CancelNativeReplayOverlayAnimationTimer();
            return;
        }

        var delay = CalculateNativeReplayOverlayAnimationDelay(
            request.AnimationClock,
            request.NextAnimationFrameDelay,
            GetNativeReplayOverlayAnimationClock());
        ScheduleNativeReplayOverlayAnimationFrame(
            delay,
            request.Version,
            request.FrameKey);
    }

    private void TrackNativeReplayOverlayPendingImageLoads(
        IReadOnlyCollection<AnimatedEmoteImageCacheKey> pendingImageLoads)
    {
        var shouldInvalidate = false;
        lock (nativeReplayOverlayAnimationGate)
        {
            nativeReplayOverlayPendingImageLoads.Clear();
            foreach (var pendingImageLoad in pendingImageLoads)
            {
                if (AnimatedEmoteImage.IsCacheEntryCompleted(pendingImageLoad))
                {
                    shouldInvalidate = true;
                    continue;
                }

                nativeReplayOverlayPendingImageLoads.Add(pendingImageLoad);
            }
        }

        if (shouldInvalidate)
        {
            dispatch(InvalidateNativeReplayOverlayFrameIfReplayChatVisible);
        }
    }

    private void CancelNativeReplayOverlayAnimationState()
    {
        CancelNativeReplayOverlayAnimationTimer();
        object? imageCachePinOwner;
        lock (nativeReplayOverlayAnimationGate)
        {
            nativeReplayOverlayPendingImageLoads.Clear();
            imageCachePinOwner = nativeReplayOverlayActiveImageCachePinOwner;
            nativeReplayOverlayActiveImageCachePinOwner = null;
        }

        ReleaseNativeReplayOverlayImageCachePins(imageCachePinOwner);
    }

    private void CancelNativeReplayOverlayAnimationTimer()
    {
        CancellationTokenSource? cancellation;
        lock (nativeReplayOverlayAnimationGate)
        {
            cancellation = nativeReplayOverlayAnimationCancellation;
            nativeReplayOverlayAnimationCancellation = null;
            nativeReplayOverlayAnimationTimerVersion++;
        }

        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
    }

    private void ScheduleNativeReplayOverlayAnimationFrame(
        TimeSpan delay,
        long version,
        string frameKey)
    {
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation;
        long timerVersion;
        lock (nativeReplayOverlayAnimationGate)
        {
            previousCancellation = nativeReplayOverlayAnimationCancellation;
            nativeReplayOverlayAnimationCancellation = cancellation;
            timerVersion = ++nativeReplayOverlayAnimationTimerVersion;
        }

        if (previousCancellation is not null)
        {
            previousCancellation.Cancel();
        }

        _ = RunNativeReplayOverlayAnimationTimerAsync(
            cancellation,
            timerVersion,
            version,
            frameKey,
            delay);
    }

    private async Task RunNativeReplayOverlayAnimationTimerAsync(
        CancellationTokenSource cancellation,
        long timerVersion,
        long frameVersion,
        string frameKey,
        TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellation.Dispose();
            return;
        }

        var shouldInvalidate = false;
        lock (nativeReplayOverlayAnimationGate)
        {
            if (ReferenceEquals(nativeReplayOverlayAnimationCancellation, cancellation) &&
                nativeReplayOverlayAnimationTimerVersion == timerVersion)
            {
                nativeReplayOverlayAnimationCancellation = null;
                shouldInvalidate = true;
            }
        }

        cancellation.Dispose();
        if (!shouldInvalidate ||
            !nativeReplayOverlayRenderState.IsCurrent(frameVersion))
        {
            return;
        }

        dispatch(() =>
        {
            if (nativeReplayOverlayRenderState.IsCurrent(frameVersion, frameKey))
            {
                UpdateNativeReplayChatOverlay(
                    forceAnimationRepaint: true,
                    GetNativeReplayOverlayAnimationClock());
            }
        });
    }

    internal static TimeSpan CalculateNativeReplayOverlayAnimationDelay(
        TimeSpan animationClock,
        TimeSpan? nextAnimationFrameDelay,
        TimeSpan currentAnimationClock)
    {
        var normalizedDelay = nextAnimationFrameDelay is { } value && value > TimeSpan.Zero
            ? value
            : NativeReplayOverlayDefaultAnimationDelay;
        var nextFrameClock = animationClock + normalizedDelay;
        var remaining = nextFrameClock - currentAnimationClock;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private TimeSpan GetNativeReplayOverlayAnimationClock()
    {
        var epoch = Volatile.Read(ref nativeReplayOverlayAnimationEpochTimestamp);
        var now = Stopwatch.GetTimestamp();
        return now > epoch
            ? Stopwatch.GetElapsedTime(epoch, now)
            : TimeSpan.Zero;
    }

    private bool TryAdoptNativeReplayOverlayImageCachePins(object? imageCachePinOwner, long version)
    {
        if (imageCachePinOwner is null || !nativeReplayOverlayRenderState.IsCurrent(version))
        {
            ReleaseNativeReplayOverlayImageCachePins(imageCachePinOwner);
            return imageCachePinOwner is null;
        }

        object? previousOwner;
        lock (nativeReplayOverlayAnimationGate)
        {
            previousOwner = nativeReplayOverlayActiveImageCachePinOwner;
            nativeReplayOverlayActiveImageCachePinOwner = imageCachePinOwner;
        }

        if (!ReferenceEquals(previousOwner, imageCachePinOwner))
        {
            ReleaseNativeReplayOverlayImageCachePins(previousOwner);
        }

        if (nativeReplayOverlayRenderState.IsCurrent(version))
        {
            return true;
        }

        lock (nativeReplayOverlayAnimationGate)
        {
            if (ReferenceEquals(nativeReplayOverlayActiveImageCachePinOwner, imageCachePinOwner))
            {
                nativeReplayOverlayActiveImageCachePinOwner = null;
            }
        }

        ReleaseNativeReplayOverlayImageCachePins(imageCachePinOwner);
        return false;
    }

    private static void ReleaseNativeReplayOverlayImageCachePins(object? imageCachePinOwner)
    {
        if (imageCachePinOwner is not null)
        {
            AnimatedEmoteImage.ClearCachePins(imageCachePinOwner);
        }
    }

    private static ChatSettings CloneChatSettingsForNativeReplayRender(ChatSettings settings)
    {
        return new ChatSettings
        {
            DockWidth = settings.DockWidth
        };
    }

    private static string BuildNativeReplayOverlayFrameKey(
        string pipeName,
        string? positionStatePath,
        ChatSettings settings,
        double overlayFontSize,
        int videoHeight,
        IReadOnlyList<ChatMessage> messages,
        int messageOffset,
        string replaySessionKey)
    {
        var layout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
            settings,
            overlayFontSize,
            videoHeight,
            positionStatePath);
        var builder = new StringBuilder();
        builder
            .Append(pipeName)
            .Append('|')
            .Append(positionStatePath)
            .Append('|')
            .Append(videoHeight.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(settings.DockWidth.ToString("0.###", CultureInfo.InvariantCulture))
            .Append('|')
            .Append(overlayFontSize.ToString("0.###", CultureInfo.InvariantCulture))
            .Append('|')
            .Append(layout.FrameWidth.ToString(CultureInfo.InvariantCulture))
            .Append('x')
            .Append(layout.FrameHeight.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(layout.ReferenceWidth.ToString(CultureInfo.InvariantCulture))
            .Append('x')
            .Append(layout.ReferenceHeight.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(layout.EffectiveReferenceFontSize.ToString("0.###", CultureInfo.InvariantCulture))
            .Append('|')
            .Append(messages.Count.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(messageOffset.ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .Append(replaySessionKey);

        foreach (var message in messages)
        {
            builder
                .Append('|')
                .Append(message.MessageId)
                .Append('@')
                .Append(message.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture))
                .Append('@')
                .Append(message.Username)
                .Append(':')
                .Append(message.Message);
        }

        return builder.ToString();
    }

    private bool ShouldUseNativeOverlayController(AppSettings settings)
    {
        return settings.Chat.ConnectAutomatically &&
            settings.Chat.Layout == ChatLayout.Overlay &&
            !IsDockedChatOverrideActive &&
            IsChatVisible &&
            playbackEngine?.UsesNativeOverlay == true;
    }

    private bool IsDockedChatModeActive => chatSettings?.Layout == ChatLayout.Docked || IsDockedChatOverrideActive;

    private bool IsNativeOverlayChatCurrent(AppSettings settings)
    {
        return playbackEngine is { UsesNativeOverlay: true } engine &&
            !string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName) &&
            IsProcessRunning(nativeOverlayProcess) &&
            string.Equals(nativeOverlayPipeName, engine.NativeOverlayPipeName, StringComparison.Ordinal) &&
            string.Equals(nativeOverlayLaunchKey, BuildNativeOverlayLaunchKey(settings), StringComparison.Ordinal);
    }

    private static bool ShouldRequestNativeOverlay(ChatSettings settings)
    {
        return settings.Layout == ChatLayout.Overlay;
    }

    private int GetNativeOverlayFontSize(AppSettings settings)
    {
        var value = settings.StreamVlcOverlayFontSizes.TryGetValue(Target.StateKey, out var savedFontSize)
            ? savedFontSize
            : settings.Chat.VlcOverlayFontSize;
        return (int)Math.Round(Math.Clamp(
            value,
            ChatSettings.MinimumFontSize,
            ChatSettings.MaximumFontSize));
    }

    private static string? ResolveVlcOverlayDirectory(ChatSettings settings)
    {
        return VlcOverlayDirectoryResolver.TryResolve(settings.VlcOverlayDirectory);
    }

    private string? ResolveActiveNativeOverlayDirectory(ChatSettings settings)
    {
        if (playbackEngine is { } engine)
        {
            var engineDirectory = VlcOverlayDirectoryResolver.NormalizeDirectory(engine.NativeOverlayDirectory);
            if (!string.IsNullOrWhiteSpace(engineDirectory))
            {
                return engineDirectory;
            }
        }

        return ResolveVlcOverlayDirectory(settings);
    }

    private static string? ResolveNativeOverlayControllerDirectory(IPlaybackEngine engine, ChatSettings settings)
    {
        var engineDirectory = VlcOverlayDirectoryResolver.NormalizeDirectory(engine.NativeOverlayDirectory);
        return string.IsNullOrWhiteSpace(engineDirectory)
            ? ResolveVlcOverlayDirectory(settings)
            : engineDirectory;
    }

    private static string GetConfiguredNativeOverlayControllerPath(ChatSettings settings)
    {
        var configuredDirectory = VlcOverlayDirectoryResolver.NormalizeDirectory(settings.VlcOverlayDirectory);
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            configuredDirectory = VlcOverlayDirectoryResolver.GetBundledOverlayDirectory();
        }

        return VlcOverlayDirectoryResolver.GetControllerPath(configuredDirectory);
    }

    private static bool IsSystemChatMessage(ChatMessage message)
    {
        return string.Equals(message.Username, "system", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldRenderNativeReplayOverlayMessage(ChatMessage message)
    {
        return !IsSystemChatMessage(message) ||
            IsKickVodReplayChatStatusMessage(message);
    }

    private bool IsKickVodReplayChatStatusMessage(ChatMessage message)
    {
        return Target.IsExplicitKickVod &&
            message.Platform == PlatformKind.Kick &&
            string.Equals(message.Channel, Target.Channel, StringComparison.OrdinalIgnoreCase) &&
            message.MessageId?.StartsWith(KickVodReplayChatStatusMessageIdPrefix + ":", StringComparison.Ordinal) == true;
    }

    private sealed class ParkingVideoSurface : IDisposable
    {
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WsClipChildren = 0x02000000;
        private const int WsClipSiblings = 0x04000000;
        private IntPtr handle;

        public ParkingVideoSurface()
        {
            handle = CreateWindowEx(
                0,
                "STATIC",
                "",
                WsPopup | WsClipChildren | WsClipSiblings,
                -32000,
                -32000,
                1,
                1,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the parking video surface window.");
            }
        }

        public IntPtr Handle => handle;

        public void Dispose()
        {
            var window = handle;
            handle = IntPtr.Zero;
            if (window != IntPtr.Zero)
            {
                DestroyWindow(window);
            }
        }

        [DllImport("user32", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hwnd);
    }

    private readonly record struct PendingChatMessage(ChatMessage Message, bool IsRememberedDockedLocalEcho);

    private sealed record DockedLocalEcho(
        PlatformKind Platform,
        string Channel,
        string Username,
        string Message,
        DateTimeOffset Timestamp);

    private sealed record DetachedNativeOverlayChat(Process? Process, string? PipeName, string? TokenFile);

    private sealed record KickOverlayChannelInfo(string? ChatroomId, long? BroadcasterUserId);

    private readonly record struct ReplayChatBackfillCoverageRange(TimeSpan From, TimeSpan Through);
}

internal sealed class VideoAspectRatioPollingBackoff
{
    private readonly TimeSpan retryInterval;
    private readonly TimeSpan changingInterval;
    private readonly TimeSpan stableInterval;
    private readonly int stableSampleThreshold;
    private readonly object gate = new();
    private double? lastRatio;
    private int unchangedValidSamples;

    public VideoAspectRatioPollingBackoff(
        TimeSpan retryInterval,
        TimeSpan changingInterval,
        TimeSpan stableInterval,
        int stableSampleThreshold)
    {
        this.retryInterval = retryInterval;
        this.changingInterval = changingInterval;
        this.stableInterval = stableInterval;
        this.stableSampleThreshold = Math.Max(1, stableSampleThreshold);
    }

    internal int UnchangedValidSamples
    {
        get
        {
            lock (gate)
            {
                return unchangedValidSamples;
            }
        }
    }

    public TimeSpan RecordInvalidSample()
    {
        Reset();
        return retryInterval;
    }

    public TimeSpan RecordValidSample(double ratio)
    {
        lock (gate)
        {
            if (lastRatio is { } previousRatio &&
                Math.Abs(previousRatio - ratio) <= 0.001)
            {
                unchangedValidSamples++;
            }
            else
            {
                lastRatio = ratio;
                unchangedValidSamples = 0;
            }

            return unchangedValidSamples >= stableSampleThreshold
                ? stableInterval
                : changingInterval;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            lastRatio = null;
            unchangedValidSamples = 0;
        }
    }
}
