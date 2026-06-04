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
using StreamlinkVlcStudio.Infrastructure.Vlc;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class StreamTabViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PlaybackStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NativeOverlayGracefulStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NativeOverlayShutdownRequestTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan NativeOverlayClearTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NativeReplayOverlayFrameWriteTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NativeOverlayPipeConnectTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NativeOverlayCapabilityProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VideoSurfaceReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VideoAspectRatioRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VideoAspectRatioRetryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReplayClockRefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReplayLiveEdgeThreshold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReplaySeekStep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplayChatWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReplayChatPrefetchThreshold = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReplayClockSampleTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplayClockMaximumPlausibleDuration = TimeSpan.FromDays(14);
    private static readonly TimeSpan ReplayDiagnosticsSlowThreshold = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan NativeReplayOverlayRefreshDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan NativeReplayOverlayDefaultAnimationDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NativeReplayOverlayMinimumAnimationDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NativeReplayOverlayMaximumAnimationDelay = TimeSpan.FromSeconds(1);
    private const double NativeReplayOverlayRenderCostDelayMultiplier = 3.0;
    private static readonly TimeSpan DefaultTwitchLiveDvrPromotionPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DockedLocalEchoDeduplicationWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ViewerCountRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ViewerCountRetryDelay = TimeSpan.FromSeconds(20);
    internal const int DefaultVolume = 80;
    private const int MaxChatMessages = 100;
    private const int MaxRecentChatMessageIds = 256;
    private const int MaxCapturedReplayChatMessages = 100_000;
    private const string TwitchLiveDvrReplayIdPrefix = "live-dvr-";
    private const uint NativeOverlayMagic = 0x564C4F56u;
    private const uint NativeOverlayVersion = 1u;
    private const byte NativeOverlayFrameType = 1;
    private const int NativeOverlayHeaderSize = 36;
    private const int NativeOverlayBlankFramePayloadSize = 4;
    private const uint NativeOverlayShutdownEventType = 6;
    private const int NativeOverlayEventMessageSize = 16;
    private const string NativeOverlayFontSizeArgument = "--font-size";
    private const int FallbackOverlayMaxPhysicalLines = 8;
    private const int FallbackOverlayLineMaxTextElements = 72;
    private const int FallbackOverlayUsernameMaxTextElements = 18;
    private const int FallbackOverlayContinuationPrefixTextElements = 2;
    private const double DefaultVideoAspectRatio = 16.0 / 9.0;
    private static readonly object NativeOverlayControllerCapabilityGate = new();
    private static readonly Dictionary<string, bool> NativeOverlayFontSizeSupportByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStreamlinkService streamlinkService;
    private readonly IPlaybackEngineFactory playbackFactory;
    private readonly IChatClientFactory chatFactory;
    private readonly IViewerCountService? viewerCountService;
    private readonly IReplayResolver? replayResolver;
    private readonly IReplayChatProvider? replayChatProvider;
    private readonly IKickChatHistoryProvider? kickChatHistoryProvider;
    private readonly IAppLogger logger;
    private readonly Action<Action> dispatch;
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
    private readonly object chatConnectionGate = new();
    private readonly object replayChatLoadGate = new();
    private readonly object replayClockAnchorGate = new();
    private readonly object replayClockUiGate = new();
    private readonly object replayChatUiGate = new();
    private readonly object replaySeekPreviewUiGate = new();
    private readonly object nativeReplayOverlayRefreshGate = new();
    private readonly object nativeReplayOverlayFrameSchedulerGate = new();
    private readonly object nativeReplayOverlayAnimationGate = new();
    private readonly ReplayChatWindowSelector replayChatSelector = new();
    private readonly SemaphoreSlim replayPlaybackTransitionGate = new(1, 1);
    private readonly NativeOverlayReplayEventHost nativeReplayOverlayEventHost;
    private readonly NativeReplayOverlayFrameWriteGate nativeReplayOverlayFrameWriteGate;
    private readonly NativeReplayOverlayRenderState nativeReplayOverlayRenderState = new();
    private NativeReplayOverlayFrameScheduler? nativeReplayOverlayFrameScheduler;
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
    private Task? viewerCountPollingTask;
    private Task? videoAspectRatioPollingTask;
    private Task? replayClockPollingTask;
    private Task? replayAvailabilityRefreshTask;
    private Task? liveDvrPromotionPollingTask;
    private Task? replayChatLoadTask;
    private ReplaySessionInfo? replaySession;
    private CancellationTokenSource? replayChatLoadCancellation;
    private ReplayChatLoadRequest? activeCapturedReplayChatLoadRequest;
    private ReplayChatLoadRequest? pendingCapturedReplayChatLoadRequest;
    private long replayChatStateVersion;
    private long replaySeekOperationVersion;
    private long replayAvailabilityRefreshVersion;
    private ReplayClockSnapshot? pendingReplayClockUiSample;
    private bool replayClockUiDispatchQueued;
    private ReplayChatWindowUiUpdate? pendingReplayChatWindowUiUpdate;
    private bool replayChatWindowUiDispatchQueued;
    private double pendingReplaySeekPreviewTextValue;
    private bool replaySeekPreviewTextDispatchQueued;
    private bool nativeReplayOverlayRefreshQueued;
    private ReplayChatWindowKey lastReplayChatWindowKey = ReplayChatWindowKey.Empty;
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
    private readonly HashSet<AnimatedEmoteImageCacheKey> nativeReplayOverlayPendingImageLoads = [];
    private bool isDirectTwitchVodReplayPlayback;
    private KickOverlayChannelInfo? resolvedKickOverlayChannelInfo;
    private string? resolvedTwitchOverlayRoomId;
    private bool playbackEngineNativeOverlayRequested;
    private string playbackEngineOverlayDirectory = "";
    private string title;
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
    private CancellationTokenSource? activeStartCancellation;
    private TwitchPredictionAccessState twitchPredictionAccess = TwitchPredictionAccessState.Pending;
    private TwitchPredictionFeedItemViewModel? activeTwitchPredictionFeedItem;
    private System.Threading.Timer? twitchPredictionClockTimer;
    private bool isTwitchPredictionRequestInFlight;

    public StreamTabViewModel(
        StreamTarget target,
        string quality,
        IStreamlinkService streamlinkService,
        IPlaybackEngineFactory playbackFactory,
        IChatClientFactory chatFactory,
        IAppLogger logger,
        Action<Action> dispatch,
        int initialVolume = DefaultVolume,
        IViewerCountService? viewerCountService = null,
        IReplayResolver? replayResolver = null,
        IReplayChatProvider? replayChatProvider = null,
        TimeSpan? twitchLiveDvrPromotionPollInterval = null,
        IKickChatHistoryProvider? kickChatHistoryProvider = null)
    {
        Target = target;
        this.quality = quality;
        this.streamlinkService = streamlinkService;
        this.playbackFactory = playbackFactory;
        this.chatFactory = chatFactory;
        this.viewerCountService = viewerCountService;
        this.logger = logger;
        this.dispatch = dispatch;
        this.replayResolver = replayResolver;
        this.replayChatProvider = replayChatProvider;
        this.kickChatHistoryProvider = kickChatHistoryProvider;
        this.twitchLiveDvrPromotionPollInterval =
            twitchLiveDvrPromotionPollInterval is { } interval && interval > TimeSpan.Zero
                ? interval
                : DefaultTwitchLiveDvrPromotionPollInterval;
        nativeReplayOverlayEventHost = new NativeOverlayReplayEventHost(
            logger,
            dispatch,
            InvalidateNativeReplayOverlayFrame,
            GetNativeReplayOverlayVideoHeight);
        nativeReplayOverlayFrameWriteGate = new NativeReplayOverlayFrameWriteGate(
            logger,
            WriteNativeReplayOverlayFrameMessageAsync,
            () => nativeReplayOverlayRenderState.Version,
            OnNativeReplayOverlayFrameWriteFailed,
            ReplayDiagnosticsSlowThreshold,
            OnNativeReplayOverlayFrameWriteSucceeded);
        AnimatedEmoteImage.ImageCacheEntryCompleted += OnAnimatedEmoteImageCacheEntryCompleted;
        DockedChatBadgeCatalog.Shared.CatalogChanged += OnChatRenderCatalogChanged;
        DockedChatEmoteCatalog.Shared.CatalogChanged += OnChatRenderCatalogChanged;
        title = target.TabTitle;
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

    public string Title
    {
        get => title;
        set => SetProperty(ref title, string.IsNullOrWhiteSpace(value) ? Target.Channel : value.Trim());
    }

    public string DockedChatHeaderText => $"Chat in {Target.Channel}'s channel";

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

    public bool CanReturnToLive => !Target.IsExplicitTwitchVod &&
        (IsBehindLive || IsReplayMode) &&
        !IsReplaySeekInProgress;

    public bool CanSeekReplay => IsReplaySeekEnabled && !IsReplaySeekInProgress;

    public bool CanStepReplay => CanSeekReplay;

    public bool CanSendChatMessages => !Target.IsExplicitTwitchVod && !IsBehindLive;

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
                ReplayElapsedText = FormatReplayTime(TimeSpan.FromSeconds(normalizedValue));
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

    public bool IsDockedChatOverrideActive => isDockedChatOverrideActive;

    public bool SetDockedChatOverrideActive(bool value)
    {
        if (!SetProperty(ref isDockedChatOverrideActive, value, nameof(IsDockedChatOverrideActive)))
        {
            return false;
        }

        UpdateNativeChatOverlay();
        return true;
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

    public bool IsTwitchPredictionPanelVisible => false;

    private bool CanProcessTwitchPredictionEvents => Target.Platform == PlatformKind.Twitch && !Target.IsExplicitTwitchVod;

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
    internal bool IsNativeReplayOverlayAnimationTimerActive
    {
        get
        {
            lock (nativeReplayOverlayAnimationGate)
            {
                return nativeReplayOverlayAnimationCancellation is not null;
            }
        }
    }

    public void SetVideoHandle(IntPtr handle)
    {
        TaskCompletionSource? stateChanged;
        lock (videoSurfaceGate)
        {
            if (videoHandle != handle)
            {
                videoHandle = handle;
                videoHandleVersion++;
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
                await TryStartNativeOverlayChatAsync(settings, cancellationToken);
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
        if (!ShouldRestartPlaybackForChatOverlaySettings(settings))
        {
            await RestartChatAsync(settings, cancellationToken);
            return;
        }

        var restorePaused = Status == PlaybackStatus.Paused;
        var restorePausedByTabSwitch = PausedByTabSwitch;

        await StopChatAsync(clearNativeOverlay: true);
        await StartAsync(settings, cancellationToken);

        if (restorePaused && Status == PlaybackStatus.Playing && playbackEngine is not null)
        {
            await playbackEngine.PauseAsync(cancellationToken);
            Status = PlaybackStatus.Paused;
            PausedByTabSwitch = restorePausedByTabSwitch;
        }
    }

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        currentSettings = settings;
        chatSettings = settings.Chat;
        ConfigureSharedChatCatalogs(settings.Chat);

        if (string.IsNullOrWhiteSpace(settings.StreamlinkPath))
        {
            throw new InvalidOperationException("Configure the Streamlink executable path in Settings.");
        }

        if (string.IsNullOrWhiteSpace(settings.VlcDirectory))
        {
            throw new InvalidOperationException("Configure the VLC directory in Settings.");
        }

        IsBusy = true;
        ErrorMessage = "";
        using var activeStart = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterActiveStartCancellation(activeStart);
        var startCancellationToken = activeStart.Token;
        CancellationTokenSource? streamStartCancellation = null;
        Task<IStreamTransportSession>? pendingStreamSession = null;
        var pendingStreamSessionNeedsCleanup = false;
        Uri? directPlaybackUri = null;

        try
        {
            await StopViewerCountPollingAsync();
            if (Target.IsExplicitTwitchVod)
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
            var request = new StreamTransportRequest(
                Target,
                Quality,
                settings.StreamlinkPath,
                Target.Kind == StreamTargetKind.Live && settings.LowLatency,
                customArguments);
            switch (Target.Kind)
            {
                case StreamTargetKind.Live:
                    streamStartCancellation = CancellationTokenSource.CreateLinkedTokenSource(startCancellationToken);
                    pendingStreamSession = streamlinkService.StartExternalHttpAsync(request, streamStartCancellation.Token);
                    pendingStreamSessionNeedsCleanup = true;
                    break;
                case StreamTargetKind.TwitchVod:
                    var resolved = await streamlinkService.ResolveStreamUrlAsync(request, startCancellationToken);
                    directPlaybackUri = resolved.StreamUri;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported stream target kind: {Target.Kind}.");
            }

            var enableNativeOverlay = ShouldRequestNativeOverlay(settings.Chat);
            var nativeOverlayPositionStatePath = enableNativeOverlay
                ? BuildNativeOverlayPositionStatePath(Target)
                : null;
            playbackEngine = await Task.Run(
                () => playbackFactory.Create(settings.VlcDirectory, enableNativeOverlay, nativeOverlayPositionStatePath),
                startCancellationToken);
            playbackEngine.VideoOutputRebound += PlaybackEngineOnVideoOutputRebound;
            playbackEngine.AudioStateReapplied += PlaybackEngineOnAudioStateReapplied;
            playbackEngineNativeOverlayRequested = enableNativeOverlay;
            playbackEngineOverlayDirectory = ResolveVlcOverlayDirectory(settings.Chat) ?? "";
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
                isDirectTwitchVodReplayPlayback = false;
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
            isDirectTwitchVodReplayPlayback = Target.IsExplicitTwitchVod && streamSession is null;
            ApplyAudio();
            Status = PlaybackStatus.Playing;
            UpdateNativeChatOverlay();
            if (!IsChatVisible && playbackEngine.UsesNativeOverlay)
            {
                _ = BlankNativeOverlayAsync(playbackEngine.NativeOverlayPipeName, CancellationToken.None);
            }

            StartVideoAspectRatioPolling();
            if (Target.IsExplicitTwitchVod)
            {
                InitializeExplicitTwitchVodReplaySession(settings);
            }
            else
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

            if (!Target.IsExplicitTwitchVod && settings.Chat.ConnectAutomatically && IsChatVisible)
            {
                if (nativeOverlayControllerRequested)
                {
                    StartNativeOverlayChatInBackground(
                        settings,
                        startCancellationToken,
                        startCaptureChatClient: ShouldKeepChatClientForCapturedReplay(settings));
                    return;
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
        }
        finally
        {
            ClearActiveStartCancellation(activeStart);
            streamStartCancellation?.Dispose();
            IsBusy = false;
        }
    }

    public async Task PauseOrResumeAsync()
    {
        if (playbackEngine is null)
        {
            return;
        }

        if (Status == PlaybackStatus.Paused)
        {
            await playbackEngine.ResumeAsync();
            Status = PlaybackStatus.Playing;
            PausedByTabSwitch = false;
        }
        else if (Status == PlaybackStatus.Playing)
        {
            await playbackEngine.PauseAsync();
            Status = PlaybackStatus.Paused;
            PausedByTabSwitch = false;
        }
    }

    public bool PausedByTabSwitch { get; private set; }

    public async Task PauseForTabSwitchAsync()
    {
        if (playbackEngine is not null && Status == PlaybackStatus.Playing)
        {
            await playbackEngine.PauseAsync();
            Status = PlaybackStatus.Paused;
            PausedByTabSwitch = true;
        }
    }

    public async Task ResumeFromTabSwitchAsync()
    {
        if (playbackEngine is not null && Status == PlaybackStatus.Paused && PausedByTabSwitch)
        {
            await playbackEngine.ResumeAsync();
            Status = PlaybackStatus.Playing;
            PausedByTabSwitch = false;
            ApplyAudio();
        }
    }

    public async Task StopAsync()
    {
        CancelActiveStart();
        CancelReplayAvailabilityRefresh();
        await StopAsync(PlaybackStopTimeout);
    }

    private async Task StopAsync(TimeSpan? playbackStopTimeout)
    {
        await StopViewerCountPollingAsync();
        CancelReplayAvailabilityRefresh();
        await StopLiveDvrPromotionPollingAsync();
        await StopPlaybackOnlyAsync(playbackStopTimeout);
        await StopChatAsync();
        Status = PlaybackStatus.Stopped;
        SetViewerCountPending("Viewer count is stopped.");
        ResetReplayState("Replay is stopped.");
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
        ReplayElapsedText = FormatReplayTime(TimeSpan.FromSeconds(ReplaySeekSliderValue));
    }

    public void PreviewReplaySeek(double sliderOffsetSeconds)
    {
        if (!isReplaySeekPreviewActive)
        {
            return;
        }

        ReplaySeekSliderValue = sliderOffsetSeconds;
    }

    public Task CommitReplaySeekPreviewAsync(CancellationToken cancellationToken = default)
    {
        return CommitReplaySeekPreviewAsync(ReplaySeekSliderValue, cancellationToken);
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
        ReplayElapsedText = FormatReplayTime(TimeSpan.FromSeconds(ReplaySeekValue));
    }

    public Task SeekReplayToCurrentSliderAsync(CancellationToken cancellationToken = default)
    {
        return SeekReplayAsync(TimeSpan.FromSeconds(ReplaySeekSliderValue), cancellationToken);
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
            return ResolveReplayClock(replay).Position;
        }

        return TimeSpan.FromSeconds(ReplaySeekValue);
    }

    private long BeginReplaySeekOperation()
    {
        var operationVersion = Interlocked.Increment(ref replaySeekOperationVersion);
        ResetReplayClockSampleTracking();
        IsReplaySeekInProgress = true;
        return operationVersion;
    }

    private bool IsLatestReplaySeekOperation(long operationVersion)
    {
        return operationVersion == Volatile.Read(ref replaySeekOperationVersion);
    }

    private void CancelReplaySeekOperation()
    {
        Interlocked.Increment(ref replaySeekOperationVersion);
        IsReplaySeekInProgress = false;
    }

    public async Task SeekReplayAsync(TimeSpan offset, CancellationToken cancellationToken = default)
    {
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
            string.IsNullOrWhiteSpace(settings.StreamlinkPath))
        {
            AddSystemMessage("Replay seeking needs the Streamlink executable path.");
            return;
        }

        var seekOperationVersion = BeginReplaySeekOperation();
        var replayChatVersion = GetReplayChatStateVersion();
        var shouldLoadReplayChat = false;
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
                if (!Target.IsExplicitTwitchVod && duration - targetOffset <= ReplayLiveEdgeThreshold)
                {
                    await ReturnToLiveAsync(cancellationToken);
                    return;
                }

                CancelActiveStart();
                replayChatVersion = ClearReplayChat();
                if (CanUseCapturedReplayChat(replay))
                {
                    RefreshCapturedReplayChat(
                        targetOffset,
                        force: true,
                        suppressUnavailableNotice: true);
                }

                if (CanSeekCurrentReplayInPlace(replay))
                {
                    Status = PlaybackStatus.Starting;
                    await playbackEngine.SeekAsync(targetOffset, cancellationToken);
                    SetReplayClockAnchor(
                        targetOffset,
                        duration,
                        seekOperationVersion,
                        awaitingSeekConfirmation: true);

                    IsReplayMode = true;
                    IsBehindLive = false;
                    Status = PlaybackStatus.Playing;
                    ApplyReplayClock(targetOffset, duration, isSeekable: true);
                    StartReplayClockPolling();
                }
                else
                {
                    if (CanUseCapturedReplayChat(replay))
                    {
                        await StopNativeOverlayChatAsync(clearOverlay: true);
                        if (ShouldCaptureReplayChat(settings))
                        {
                            await EnsureChatClientConnectedAsync(cancellationToken);
                        }
                        else
                        {
                            await StopChatClientAsync();
                        }
                    }
                    else
                    {
                        await StopChatAsync(clearNativeOverlay: true);
                    }

                    await StopStreamSessionAsync();

                    var customArguments = CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments);
                    var request = new StreamTransportRequest(
                        Target with { Url = replay.ReplayUrl },
                        replay.GetStreamlinkQuality(Quality),
                        settings.StreamlinkPath!,
                        false,
                        customArguments);
                    var resolved = await streamlinkService.ResolveStreamUrlAsync(request, cancellationToken);

                    Status = PlaybackStatus.Starting;
                    await playbackEngine.PlayAsync(resolved.StreamUri, Volume, CurrentAudioState, cancellationToken);
                    isDirectTwitchVodReplayPlayback = Target.IsExplicitTwitchVod;
                    await playbackEngine.SeekAsync(targetOffset, cancellationToken);
                    SetReplayClockAnchor(
                        targetOffset,
                        duration,
                        seekOperationVersion,
                        awaitingSeekConfirmation: true);
                    ApplyAudio();

                    IsReplayMode = true;
                    IsBehindLive = !Target.IsExplicitTwitchVod;
                    Status = PlaybackStatus.Playing;
                    ApplyReplayClock(targetOffset, duration, isSeekable: true);
                    StartReplayClockPolling();
                }

                if (ChatMessages.Count > 0)
                {
                    InvalidateNativeReplayOverlayFrame();
                }
                else
                {
                    QueueNativeChatOverlayUpdateAfterReplayWindowApply();
                }

                shouldLoadReplayChat = true;
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
            }
        }

        if (shouldLoadReplayChat && IsLatestReplaySeekOperation(seekOperationVersion))
        {
            QueueReplayChatLoad(
                replay,
                settings,
                targetOffset,
                notifyUnavailable: true,
                replayChatVersion);
        }
    }

    public Task ReturnToLiveAsync()
    {
        return ReturnToLiveAsync(CancellationToken.None);
    }

    private async Task ReturnToLiveAsync(CancellationToken cancellationToken)
    {
        if (Target.IsExplicitTwitchVod)
        {
            AddSystemMessage("Return to live is not available for VOD playback.");
            return;
        }

        if (currentSettings is null)
        {
            return;
        }

        if (!IsReplayMode && !IsBehindLive)
        {
            IsBehindLive = false;
            ReplayLiveStateText = "Live";
            return;
        }

        await StartAsync(currentSettings, cancellationToken);
    }

    public async Task SendChatMessageAsync()
    {
        var message = NormalizeOutgoingMessage(OutgoingChatText);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (Target.IsExplicitTwitchVod)
        {
            AddSystemMessage("Chat sending is disabled for VOD playback.");
            return;
        }

        if (IsBehindLive)
        {
            AddSystemMessage("Chat sending is disabled while replay is behind live.");
            return;
        }

        if (chatClient is null)
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
            await chatClient.SendMessageAsync(message);
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

    public async ValueTask DisposeAsync()
    {
        AnimatedEmoteImage.ImageCacheEntryCompleted -= OnAnimatedEmoteImageCacheEntryCompleted;
        DockedChatBadgeCatalog.Shared.CatalogChanged -= OnChatRenderCatalogChanged;
        DockedChatEmoteCatalog.Shared.CatalogChanged -= OnChatRenderCatalogChanged;
        CancelNativeReplayOverlayAnimationState();
        StopTwitchPredictionClock();
        CancelActiveStart();
        await StopAsync(PlaybackStopTimeout);
        await nativeReplayOverlayEventHost.DisposeAsync();
        if (nativeReplayOverlayFrameScheduler is not null)
        {
            await nativeReplayOverlayFrameScheduler.DisposeAsync();
        }

        nativeReplayOverlayFrameWriteGate.Dispose();
    }

    private void OnChatRenderCatalogChanged(object? sender, EventArgs e)
    {
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
        var connectionTask = StartChatCoreAsync(cancellationToken);
        lock (chatConnectionGate)
        {
            chatConnectionTask = connectionTask;
        }

        try
        {
            await connectionTask.ConfigureAwait(false);
        }
        finally
        {
            lock (chatConnectionGate)
            {
                if (ReferenceEquals(chatConnectionTask, connectionTask))
                {
                    chatConnectionTask = null;
                }
            }
        }
    }

    private async Task StartChatCoreAsync(CancellationToken cancellationToken)
    {
        IChatClient? client = null;
        try
        {
            await StopChatClientAsync();
            client = chatFactory.Create(Target.Platform);
            AttachChatClient(client);
            chatClient = client;
            await client.ConnectAsync(Target, cancellationToken);
        }
        catch (Exception ex)
        {
            if (client is not null && ReferenceEquals(chatClient, client))
            {
                chatClient = null;
                DetachChatClient(client);
                try
                {
                    await client.DisposeAsync();
                }
                catch (Exception disposeException)
                {
                    logger.Write(AppLogLevel.Warning, "Chat", $"Failed to dispose failed chat client for {Target.DisplayName}.", disposeException);
                }
            }

            AddSystemMessage($"Chat unavailable: {ex.Message}");
            logger.Write(AppLogLevel.Warning, "Chat", $"Chat failed for {Target.DisplayName}", ex);
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
        if (chatClient is not null)
        {
            var client = chatClient;
            chatClient = null;
            DetachChatClient(client);
            await client.DisposeAsync();
        }
    }

    private void InitializeExplicitTwitchVodReplaySession(AppSettings settings)
    {
        if (!settings.Replay.Enabled)
        {
            ResetReplayState("Replay seekbar is disabled in Settings.");
            return;
        }

        if (string.IsNullOrWhiteSpace(Target.MediaId))
        {
            SetReplayUnavailable("The selected Twitch VOD did not include a video ID.");
            return;
        }

        if (Target.MediaDuration <= TimeSpan.Zero)
        {
            SetReplayUnavailable("The selected Twitch VOD did not include a usable duration.");
            return;
        }

        var replay = new ReplaySessionInfo(
            Target.Platform,
            Target.Channel,
            Target.Url,
            Target.MediaId,
            null,
            Target.MediaDuration,
            true,
            "",
            ChatRoomId: Target.BroadcasterId);
        replaySession = replay;
        ResetReplayChatState();
        ClearReplayClockAnchor();
        IsReplayMode = true;
        IsBehindLive = false;
        ReplaySeekToolTip = $"Twitch VOD replay available: {replay.ReplayId}";
        ApplyReplayClock(TimeSpan.Zero, Target.MediaDuration, isSeekable: true);
        ReplayLiveStateText = "VOD";
        StartReplayClockPolling();
        QueueReplayChatLoadIfNeeded(TimeSpan.Zero);
    }

    private async Task RefreshReplayAvailabilityAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        long refreshVersion = 0)
    {
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
            if (!IsReplayAvailabilityRefreshCurrent(refreshVersion))
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

            if (CanUseCapturedReplayChat(replay))
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

            var duration = GetCurrentReplayDuration(replay);
            dispatch(() =>
            {
                IsReplaySeekEnabled = true;
                ReplaySeekToolTip = $"Replay available: {replay.ReplayId}";
                ApplyReplayClock(duration, duration, isSeekable: true);
                ReplayLiveStateText = "Live";
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

    private void StartReplayAvailabilityRefreshInBackground(AppSettings settings)
    {
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
                    await RefreshReplayAvailabilityAsync(settings, cancellationToken, refreshVersion);
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

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ResetReplayState(string reason)
    {
        StopNativeReplayOverlayEventHost();
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
            logger.Write(AppLogLevel.Warning, "Replay", $"Replay clock cleanup failed for {Target.DisplayName}.", ex);
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

        var clock = ResolveReplayClock(replay);

        QueueReplayClockUiApply(clock);
        if (IsReplayMode)
        {
            QueueReplayChatLoadIfNeeded(clock.Position);
            if (!CanUseCapturedReplayChat(replay) &&
                replayChatSelector.Count > 0)
            {
                UpdateReplayChatWindow(clock.Position);
            }
        }
    }

    private void QueueReplayClockUiApply(ReplayClockSnapshot clock)
    {
        lock (replayClockUiGate)
        {
            pendingReplayClockUiSample = clock;
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
            ReplayElapsedText = FormatReplayTime(previewPosition);

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
            ReplayClockSnapshot? clock;
            lock (replayClockUiGate)
            {
                clock = pendingReplayClockUiSample;
                pendingReplayClockUiSample = null;
                if (clock is null)
                {
                    replayClockUiDispatchQueued = false;
                    return;
                }
            }

            ApplyReplayClock(clock.Value.Position, clock.Value.Duration, clock.Value.IsSeekable);

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
            ReplayElapsedText = FormatReplayTime(previewPosition);
        }
        else
        {
            ReplaySeekValue = normalizedPosition.TotalSeconds;
            ReplaySeekSliderValue = normalizedPosition.TotalSeconds;
            ReplayElapsedText = FormatReplayTime(normalizedPosition);
        }

        ReplayDurationText = FormatReplayTime(normalizedDuration);
        IsReplaySeekEnabled = replaySession?.IsAvailable == true && isSeekable;
        ReplayLiveStateText = Target.IsExplicitTwitchVod
            ? "VOD"
            : IsReplayMode || IsBehindLive
                ? "Behind live"
                : "Live";
        if (!isSeekable)
        {
            ReplaySeekToolTip = "The current replay media is not seekable.";
        }
    }

    private ReplayClockSnapshot ResolveReplayClock(ReplaySessionInfo replay)
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
            if (!Target.IsExplicitTwitchVod)
            {
                duration = NormalizeReplayClockDuration(clock.Duration, duration);
            }

            if (TryNormalizeReplayClockPosition(clock.Position, duration, out var position) &&
                IsReplayClockSampleAccepted(position, duration, observedAtUtc))
            {
                AcceptReplayClockSample(
                    position,
                    duration,
                    Volatile.Read(ref replaySeekOperationVersion),
                    observedAtUtc);
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

    private void AcceptReplayClockSample(
        TimeSpan position,
        TimeSpan duration,
        long seekGeneration,
        DateTimeOffset observedAtUtc)
    {
        lock (replayClockAnchorGate)
        {
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
        bool Force);

    private readonly record struct ReplayChatLoadRequest(
        ReplaySessionInfo Replay,
        AppSettings Settings,
        TimeSpan Offset,
        bool NotifyUnavailable,
        long ReplayChatVersion);

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

        if (CanUseCapturedReplayChat(replay))
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

            if (notifyUnavailable)
            {
                AddSystemMessage("Replay chat provider is not configured.");
            }

            return;
        }

        var result = await replayChatProvider.LoadChatAsync(replay, settings, offset, cancellationToken);
        if (!IsReplayChatLoadCurrent(replay, replayChatVersion))
        {
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
                replayChatSelector.Clear();
                replayChatLoadedFrom = null;
                replayChatLoadedThrough = null;
                ResetReplayChatVisibleWindowCache();
                AddSystemMessage(result.UnavailableReason);
            }
            else
            {
                logger.Write(AppLogLevel.Info, "Replay", result.UnavailableReason);
            }

            return;
        }

        replayChatSelector.AddRange(result.Messages);
        UpdateReplayChatRange(result);
        UpdateReplayChatWindow(offset, force: true);
    }

    private long ClearReplayChat()
    {
        var replayChatVersion = ResetReplayChatState();
        QueueReplayChatWindowUiApply(
            new ReplayChatWindowSelection([], ReplayChatWindowKey.Empty),
            force: true);
        dispatch(() =>
        {
            activeTwitchPredictionFeedItem = null;
            StopTwitchPredictionClock();
        });

        return replayChatVersion;
    }

    private long ResetReplayChatState()
    {
        var replayChatVersion = InvalidateReplayChatState();
        replayChatSelector.Clear();
        replayChatLoadedFrom = null;
        replayChatLoadedThrough = null;
        lastReplayChatOffset = TimeSpan.MinValue;
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

    private bool IsCurrentReplaySession(ReplaySessionInfo replay)
    {
        return replaySession is { IsAvailable: true } currentReplay &&
            currentReplay.Platform == replay.Platform &&
            string.Equals(currentReplay.Channel, replay.Channel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentReplay.ReplayId, replay.ReplayId, StringComparison.Ordinal);
    }

    private bool CanSeekCurrentReplayInPlace(ReplaySessionInfo replay)
    {
        return Target.IsExplicitTwitchVod &&
            isDirectTwitchVodReplayPlayback &&
            IsCurrentReplaySession(replay);
    }

    private void QueueReplayChatLoadIfNeeded(TimeSpan offset)
    {
        if (replaySession is not { IsAvailable: true } replay ||
            currentSettings is null ||
            IsReplaySeekInProgress)
        {
            return;
        }

        if (CanUseCapturedReplayChat(replay))
        {
            RefreshCapturedReplayChat(offset, force: false);
            if (NeedsCapturedReplayChatBackfill(replay, offset))
            {
                QueueReplayChatLoad(
                    replay,
                    currentSettings,
                    offset,
                    notifyUnavailable: false,
                    GetReplayChatStateVersion());
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
            GetReplayChatStateVersion());
    }

    private void QueueReplayChatLoad(
        ReplaySessionInfo replay,
        AppSettings settings,
        TimeSpan offset,
        bool notifyUnavailable,
        long replayChatVersion)
    {
        if (!IsReplayChatLoadCurrent(replay, replayChatVersion))
        {
            return;
        }

        var useCapturedReplayChat = CanUseCapturedReplayChat(replay);
        if (useCapturedReplayChat)
        {
            var needsCapturedBackfill = NeedsCapturedReplayChatBackfill(replay, offset);
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

        var request = new ReplayChatLoadRequest(replay, settings, offset, notifyUnavailable, replayChatVersion);
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
                        $"Skipped Kick captured replay chat load at {FormatReplayTime(request.Offset)} because another replay chat load is already running.");
                    return;
                }

                pendingCapturedReplayChatLoadRequest = request;
                if (CapturedReplayChatLoadWindowsOverlap(activeRequest.Offset, request.Offset))
                {
                    logger.Write(
                        AppLogLevel.Debug,
                        "Replay",
                        $"Coalesced Kick captured replay chat load at {FormatReplayTime(request.Offset)} behind active {FormatReplayTime(activeRequest.Offset)}.");
                    return;
                }

                logger.Write(
                    AppLogLevel.Debug,
                    "Replay",
                    $"Canceling stale Kick captured replay chat load at {FormatReplayTime(activeRequest.Offset)} for newer {FormatReplayTime(request.Offset)}.");
                staleCancellation = replayChatLoadCancellation;
                replayChatLoadCancellation = null;
                replayChatLoadTask = null;
                activeCapturedReplayChatLoadRequest = null;
                pendingCapturedReplayChatLoadRequest = null;
            }

            StartReplayChatLoadCore(request, useCapturedReplayChat: true);
        }

        CancelReplayChatLoadSource(staleCancellation);
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
        if (!IsReplayChatLoadCurrent(request.Replay, request.ReplayChatVersion))
        {
            logger.Write(
                AppLogLevel.Debug,
                "Replay",
                $"Skipped pending Kick captured replay chat load at {FormatReplayTime(request.Offset)} because the replay session changed.");
            return;
        }

        if (!NeedsCapturedReplayChatBackfill(request.Replay, request.Offset))
        {
            logger.Write(
                AppLogLevel.Debug,
                "Replay",
                $"Skipped pending Kick captured replay chat load at {FormatReplayTime(request.Offset)} because the window is already covered.");
            RefreshCapturedReplayChat(request.Offset, force: request.NotifyUnavailable);
            return;
        }

        logger.Write(
            AppLogLevel.Debug,
            "Replay",
            $"Restarting Kick captured replay chat load at {FormatReplayTime(request.Offset)} after active load completed.");
        QueueReplayChatLoad(
            request.Replay,
            request.Settings,
            request.Offset,
            request.NotifyUnavailable,
            request.ReplayChatVersion);
    }

    private static bool CapturedReplayChatLoadWindowsOverlap(TimeSpan firstOffset, TimeSpan secondOffset)
    {
        var firstFrom = GetReplayChatWindowStart(firstOffset);
        var firstThrough = SafeAdd(firstOffset, ReplayChatPrefetchThreshold);
        var secondFrom = GetReplayChatWindowStart(secondOffset);
        var secondThrough = SafeAdd(secondOffset, ReplayChatPrefetchThreshold);
        return firstFrom <= secondThrough && secondFrom <= firstThrough;
    }

    private bool NeedsCapturedReplayChatBackfill(ReplaySessionInfo replay, TimeSpan offset)
    {
        if (replay.Platform != PlatformKind.Kick ||
            (chatClient is not IChatHistoryBackfillClient && kickChatHistoryProvider is null) ||
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
                capturedReplayChatSelector.SelectWindow(offset, ReplayChatWindow, MaxChatMessages).Messages.Count);
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
            $"Kick seekback replay chat backfill at {FormatReplayTime(offset)} loaded={result.LoadedMessageCount.ToString(CultureInfo.InvariantCulture)}, " +
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
        if (!CanUseCapturedReplayChat(replay))
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
            return $"message before replay start at {FormatReplayTime(messageOffset)}";
        }

        var duration = GetCurrentReplayDuration(replay);
        if (messageOffset > SafeAdd(duration, ReplayLiveEdgeThreshold))
        {
            return $"message after replay duration at {FormatReplayTime(messageOffset)}";
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

        CancelReplayChatLoadSource(cancellation);
    }

    private static void CancelReplayChatLoadSource(CancellationTokenSource? cancellation)
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
        if (!CanUseCapturedReplayChat(replay))
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
        if (!CanUseCapturedReplayChat(replay) ||
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

    private IReadOnlyList<ReplayChatMessage> GetCapturedReplayChatMessagesSnapshot()
    {
        lock (capturedReplayChatGate)
        {
            return capturedReplayChatSelector.Snapshot();
        }
    }

    private void RefreshCapturedReplayChat(
        TimeSpan offset,
        bool force,
        bool suppressUnavailableNotice = false)
    {
        lock (capturedReplayChatGate)
        {
            SetReplayChatRangeFromSelector(capturedReplayChatSelector);
            UpdateReplayChatWindow(offset, force, capturedReplayChatSelector);
        }

        if (force && !suppressUnavailableNotice)
        {
            var notice = TryBuildCapturedReplayChatNotice(offset);
            if (!string.IsNullOrWhiteSpace(notice))
            {
                AddSystemMessage(notice);
            }
        }
    }

    private bool TryUseCapturedReplayChatFallback(ReplaySessionInfo replay, TimeSpan offset, bool replaceExisting)
    {
        if (!CanUseCapturedReplayChatFallback(replay))
        {
            return false;
        }

        var capturedMessages = GetCapturedReplayChatMessagesSnapshot();
        if (capturedMessages.Count == 0)
        {
            return false;
        }

        if (replaceExisting)
        {
            replayChatSelector.Replace(capturedMessages);
        }
        else
        {
            replayChatSelector.AddRange(capturedMessages);
        }

        SetReplayChatRangeFromSelector(replayChatSelector);
        UpdateReplayChatWindow(offset, force: true);
        return true;
    }

    private bool CanUseCapturedReplayChatFallback(ReplaySessionInfo replay)
    {
        if (replay.Platform != Target.Platform ||
            CanUseCapturedReplayChat(replay) ||
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
                ? $"{noticePrefix} before {FormatReplayTime(firstOffset)} is no longer retained in this tab."
                : $"{noticePrefix} before {FormatReplayTime(firstOffset)} was not captured by this tab.";
        }
    }

    private bool ShouldShowCapturedReplayChatNotice(TimeSpan offset)
    {
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

    private bool ShouldCaptureReplayChat(AppSettings settings)
    {
        return Target.Platform is PlatformKind.Twitch or PlatformKind.Kick &&
            settings.Chat.ConnectAutomatically &&
            IsChatVisible &&
            replaySession is { IsAvailable: true } replay &&
            replay.StreamStartedAtUtc is not null &&
            replay.Platform == Target.Platform &&
            CanUseCapturedReplayChat(replay);
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

    private static bool CanUseCapturedReplayChat(ReplaySessionInfo replay)
    {
        return IsCurrentLiveDvrReplay(replay) ||
            (replay.Platform == PlatformKind.Kick && replay.StreamStartedAtUtc is not null);
    }

    private static bool IsCurrentLiveDvrReplay(ReplaySessionInfo replay)
    {
        return replay.Platform == PlatformKind.Twitch &&
            (replay.MediaKind == ReplayMediaKind.CurrentLiveDvr ||
                replay.ReplayId.StartsWith(TwitchLiveDvrReplayIdPrefix, StringComparison.Ordinal));
    }

    private void StartLiveDvrPromotionPolling(AppSettings settings)
    {
        if (replayResolver is null)
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
            logger.Write(AppLogLevel.Warning, "Replay", $"Twitch current-live DVR promotion polling cleanup failed for {Target.DisplayName}.", ex);
        }
        finally
        {
            cancellation.Dispose();
        }
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
        var duration = GetCurrentReplayDuration(promotedReplay);
        var offset = GetCurrentReplayStepOffset();
        var capturedMessages = GetCapturedReplayChatMessagesSnapshot();
        if (capturedMessages.Count > 0)
        {
            replayChatSelector.AddRange(capturedMessages);
            SetReplayChatRangeFromSelector(replayChatSelector);
        }

        dispatch(() =>
        {
            ReplaySeekToolTip = $"Replay available: {promotedReplay.ReplayId}";
            ApplyReplayClock(IsReplayMode ? offset : duration, duration, isSeekable: true);
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
        if (!force && !HasReplayChatOffsetChangedEnough(offset))
        {
            return;
        }

        lastReplayChatOffset = offset;
        var selectionStopwatch = Stopwatch.StartNew();
        var selection = (selector ?? replayChatSelector).SelectWindow(offset, ReplayChatWindow, MaxChatMessages);
        selectionStopwatch.Stop();
        LogReplayDebugIfSlow(
            selectionStopwatch.Elapsed,
            "Replay",
            $"Replay chat window selection took {selectionStopwatch.Elapsed.TotalMilliseconds:0} ms for {selection.Messages.Count} visible messages.");

        QueueReplayChatWindowUiApply(selection, force);
    }

    private void QueueReplayChatWindowUiApply(ReplayChatWindowSelection selection, bool force)
    {
        var update = new ReplayChatWindowUiUpdate(GetReplayChatStateVersion(), selection, force);
        lock (replayChatUiGate)
        {
            if (!force &&
                (selection.Key == lastReplayChatWindowKey ||
                    (pendingReplayChatWindowUiUpdate is { } pending &&
                        pending.Selection.Key == selection.Key)))
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

            if (update.Value.StateVersion != GetReplayChatStateVersion())
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
            ChatMessages.Clear();
            DockedChatMessages.Clear();
            DockedChatFeedItems.Clear();
            foreach (var message in update.Value.Selection.Messages)
            {
                ChatMessages.Add(message);
                DockedChatMessages.Add(message);
                DockedChatFeedItems.Add(new DockedChatMessageFeedItem(message));
            }

            if (update.Value.Selection.Messages.Count > 0 || !IsReplaySeekInProgress)
            {
                QueueNativeChatOverlayUpdateAfterReplayWindowApply();
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

    private TimeSpan GetCurrentReplayDuration(ReplaySessionInfo replay)
    {
        var duration = NormalizeReplayDuration(replay.Duration);
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

    private static string FormatReplayTime(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private void StartViewerCountPolling(AppSettings settings)
    {
        if (viewerCountService is null)
        {
            SetViewerCountUnavailable("Viewer count service is not configured.");
            return;
        }

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
            logger.Write(AppLogLevel.Warning, "Viewers", $"Viewer count polling cleanup failed for {Target.DisplayName}.", ex);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void StartVideoAspectRatioPolling()
    {
        videoAspectRatioPollingCancellation?.Cancel();
        videoAspectRatioPollingCancellation?.Dispose();

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
            logger.Write(AppLogLevel.Warning, "Playback", $"Video aspect ratio polling cleanup failed for {Target.DisplayName}.", ex);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task PollVideoAspectRatioAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var refreshed = TryRefreshVideoAspectRatio();
            await Task.Delay(refreshed ? VideoAspectRatioRefreshInterval : VideoAspectRatioRetryInterval, cancellationToken);
        }
    }

    private bool TryRefreshVideoAspectRatio()
    {
        if (playbackEngine?.TryGetVideoSize(out var width, out var height) != true)
        {
            return false;
        }

        UpdateVideoAspectRatio(width, height);
        return true;
    }

    private void UpdateVideoAspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var ratio = Math.Clamp(width / (double)height, 0.1, 10.0);
        if (Math.Abs(VideoAspectRatio - ratio) <= 0.001)
        {
            return;
        }

        dispatch(() => VideoAspectRatio = ratio);
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
        client.MessageReceived += ChatClientOnMessageReceived;
        client.StatusChanged += ChatClientOnStatusChanged;
        if (client is ITwitchPredictionClient predictions)
        {
            twitchPredictionClient = predictions;
            predictions.PredictionReceived += TwitchPredictionClientOnPredictionReceived;
            predictions.PredictionAccessChanged += TwitchPredictionClientOnPredictionAccessChanged;
            ApplyTwitchPredictionAccess(predictions.PredictionAccess);
        }
    }

    private void DetachChatClient(IChatClient client)
    {
        client.MessageReceived -= ChatClientOnMessageReceived;
        client.StatusChanged -= ChatClientOnStatusChanged;
        if (client is ITwitchPredictionClient predictions)
        {
            predictions.PredictionReceived -= TwitchPredictionClientOnPredictionReceived;
            predictions.PredictionAccessChanged -= TwitchPredictionClientOnPredictionAccessChanged;
            if (ReferenceEquals(twitchPredictionClient, predictions))
            {
                twitchPredictionClient = null;
                ApplyTwitchPredictionAccess(TwitchPredictionAccessState.Pending);
            }
        }
    }

    private async Task StopPlaybackOnlyAsync(TimeSpan? playbackStopTimeout = null)
    {
        await StopVideoAspectRatioPollingAsync();
        await StopReplayClockPollingAsync();
        await StopNativeReplayOverlayEventHostAsync();
        await StopNativeOverlayChatAsync(clearOverlay: false);

        await StopStreamSessionAsync();

        var engine = playbackEngine;
        playbackEngine = null;
        isDirectTwitchVodReplayPlayback = false;
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
        var session = streamSession;
        streamSession = null;
        if (session is not null)
        {
            session.LogLineReceived -= StreamSessionOnLogLineReceived;
            await session.DisposeAsync();
        }
    }

    private void RaiseNativeOverlayProperties()
    {
        OnPropertyChanged(nameof(UsesNativeOverlay));
        OnPropertyChanged(nameof(NativeOverlayPipeName));
        OnPropertyChanged(nameof(NativeOverlayPositionStatePath));
    }

    private void PlaybackEngineOnVideoOutputRebound(object? sender, EventArgs e)
    {
        if (sender is not IPlaybackEngine engine ||
            !ReferenceEquals(engine, playbackEngine) ||
            IsChatVisible ||
            !engine.UsesNativeOverlay)
        {
            return;
        }

        _ = BlankNativeOverlayAsync(engine.NativeOverlayPipeName, CancellationToken.None);
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
        var shutdownTask = Task.Run(async () =>
        {
            try
            {
                engine.SetOverlayText(null, false, 0, 0);
                await engine.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                engine.Dispose();
                parkingSurface?.Dispose();
            }
        });

        try
        {
            if (timeout is null)
            {
                await shutdownTask;
            }
            else
            {
                await shutdownTask.WaitAsync(timeout.Value);
            }
        }
        catch (TimeoutException)
        {
            logger.Write(
                AppLogLevel.Warning,
                "Playback",
                $"Timed out stopping playback for {Target.DisplayName}; cleanup will continue in the background.");
            ObserveBackgroundShutdown(shutdownTask);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Playback", $"Failed to stop playback for {Target.DisplayName}.", ex);
        }
    }

    private void ObserveBackgroundShutdown(Task shutdownTask)
    {
        _ = shutdownTask.ContinueWith(
            completed =>
            {
                if (completed.Exception is not null)
                {
                    logger.Write(
                        AppLogLevel.Warning,
                        "Playback",
                        $"Background playback cleanup failed for {Target.DisplayName}.",
                        completed.Exception.GetBaseException());
                }
            },
            TaskScheduler.Default);
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

            var timeoutTask = Task.Delay(VideoSurfaceReadyTimeout);
            var expectedSurfaceCompleted = await Task.WhenAny(handleReadyTask, stateChangedTask, timeoutTask).WaitAsync(cancellationToken);
            if (ReferenceEquals(expectedSurfaceCompleted, timeoutTask))
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
    }

    private bool IsCapturedReplayChatMessageInCurrentWindow(ReplayChatMessage message)
    {
        if (replaySession is not { IsAvailable: true } replay ||
            !CanUseCapturedReplayChat(replay))
        {
            return false;
        }

        var offset = GetCurrentReplayStepOffset();
        var start = offset - ReplayChatWindow;
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        return message.Offset >= start && message.Offset <= offset;
    }

    private void ChatClientOnStatusChanged(object? sender, string message)
    {
        if (!ReferenceEquals(sender, chatClient))
        {
            return;
        }

        AddSystemMessage(message);
    }

    private void TwitchPredictionClientOnPredictionAccessChanged(object? sender, TwitchPredictionAccessState access)
    {
        if (!ReferenceEquals(sender, twitchPredictionClient))
        {
            return;
        }

        dispatch(() => ApplyTwitchPredictionAccess(access));
    }

    private void TwitchPredictionClientOnPredictionReceived(object? sender, TwitchPrediction prediction)
    {
        if (!ReferenceEquals(sender, twitchPredictionClient))
        {
            return;
        }

        UpsertTwitchPrediction(prediction);
    }

    private void ApplyTwitchPredictionAccess(TwitchPredictionAccessState access)
    {
        twitchPredictionAccess = access;
        OnPropertyChanged(nameof(TwitchPredictionStatusText));
        RaiseTwitchPredictionCommandState();
    }

    private void UpsertTwitchPrediction(TwitchPrediction prediction)
    {
        if (!CanProcessTwitchPredictionEvents ||
            string.IsNullOrWhiteSpace(prediction.Id))
        {
            return;
        }

        dispatch(() =>
        {
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
        });
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
        var systemMessage = new ChatMessage(Target.Platform, Target.Channel, "system", message, DateTimeOffset.Now, "#A6E3A1");
        AddChatMessage(systemMessage, isRememberedDockedLocalEcho: false);
    }

    private void AddChatMessage(ChatMessage message, bool isRememberedDockedLocalEcho)
    {
        dispatch(() =>
        {
            if (ShouldSkipDuplicateChatMessage(message))
            {
                return;
            }

            ChatMessages.Add(message);
            while (ChatMessages.Count > MaxChatMessages)
            {
                ChatMessages.RemoveAt(0);
            }

            if (ShouldAddDockedChatMessage(message, isRememberedDockedLocalEcho))
            {
                DockedChatMessages.Add(message);
                while (DockedChatMessages.Count > MaxChatMessages)
                {
                    DockedChatMessages.RemoveAt(0);
                }

                DockedChatFeedItems.Add(new DockedChatMessageFeedItem(message));
                PruneDockedChatFeedItems();
            }

            UpdateNativeChatOverlay();
        });
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

    private async Task<bool> StartNativeOverlayChatAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var engine = playbackEngine;
        if (engine is not { UsesNativeOverlay: true } ||
            string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName))
        {
            return false;
        }

        if (IsReplayMode || IsBehindLive)
        {
            return false;
        }

        var pipeName = engine.NativeOverlayPipeName!;
        var launchKey = BuildNativeOverlayLaunchKey(settings);
        await StopNativeReplayOverlayEventHostAsync();
        var overlayDirectory = ResolveVlcOverlayDirectory(settings.Chat);
        var controllerPath = string.IsNullOrWhiteSpace(overlayDirectory)
            ? GetConfiguredNativeOverlayControllerPath(settings.Chat)
            : VlcOverlayDirectoryResolver.GetControllerPath(overlayDirectory);
        if (!File.Exists(controllerPath))
        {
            AddSystemMessage($"Native VLC chat overlay controller was not found at {controllerPath}.");
            return false;
        }

        string? tokenFile = null;
        await nativeOverlayProcessGate.WaitAsync(cancellationToken);
        try
        {
            if (IsProcessRunning(nativeOverlayProcess) &&
                string.Equals(nativeOverlayPipeName, pipeName, StringComparison.Ordinal) &&
                string.Equals(nativeOverlayLaunchKey, launchKey, StringComparison.Ordinal))
            {
                return true;
            }

            await StopNativeOverlayChatCoreAsync(clearOverlay: false);

            KickOverlayChannelInfo? kickInfo = null;
            string? kickToken = null;
            string? kickBadgeManifestPath = null;
            string? twitchBadgeManifestPath = null;
            string? twitchRoomId = null;

            if (Target.Platform == PlatformKind.Kick)
            {
                kickBadgeManifestPath = FindKickBadgeManifestPath();
                kickInfo = await ResolveKickOverlayChannelInfoAsync(settings.Chat, settings.Chat.KickSendAsBot, cancellationToken);
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
            if (NativeOverlayControllerSupportsFontSize(controllerPath))
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
            process.Exited += (_, _) => AddSystemMessage("Native VLC chat overlay stopped.");
            engine.SetOverlayText(null, false, 0, 0);

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

    private void StartNativeOverlayChatInBackground(
        AppSettings settings,
        CancellationToken cancellationToken,
        bool startCaptureChatClient = false)
    {
        _ = Task.Run(async () =>
        {
            var started = await TryStartNativeOverlayChatAsync(settings, cancellationToken);
            if (started &&
                startCaptureChatClient &&
                ShouldKeepChatClientForCapturedReplay(settings))
            {
                await StartChatAsync(cancellationToken);
                return;
            }

            if (!started &&
                startCaptureChatClient &&
                ShouldKeepChatClientForCapturedReplay(settings))
            {
                await EnsureChatClientConnectedAsync(cancellationToken);
            }
        });
    }

    private async Task<bool> TryStartNativeOverlayChatAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return await StartNativeOverlayChatAsync(settings, cancellationToken);
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

    private async Task StopNativeOverlayChatAsync(bool clearOverlay = false)
    {
        await nativeOverlayProcessGate.WaitAsync();
        try
        {
            await StopNativeOverlayChatCoreAsync(clearOverlay);
        }
        finally
        {
            nativeOverlayProcessGate.Release();
        }
    }

    private async Task StopNativeOverlayChatCoreAsync(bool clearOverlay)
    {
        var process = nativeOverlayProcess;
        var pipeName = nativeOverlayPipeName ?? playbackEngine?.NativeOverlayPipeName;
        nativeOverlayProcess = null;
        nativeOverlayPipeName = null;
        nativeOverlayLaunchKey = null;
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

        TryDeleteNativeOverlayTokenFile(nativeOverlayTokenFile);
        nativeOverlayTokenFile = null;
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
        var shutdownMessage = BuildNativeOverlayEventMessage(NativeOverlayShutdownEventType, 0);
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

        var blankMessage = BuildNativeOverlayBlankFrameMessage();
        var (_, lastException) = await TryWriteNativeOverlayMessageAsync(
            pipeName,
            blankMessage,
            NativeOverlayClearTimeout,
            cancellationToken);
        if (lastException is not null)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", "Could not blank the native VLC chat overlay.", lastException);
        }
    }

    private async Task<(bool Sent, Exception? LastException)> TryWriteNativeOverlayMessageAsync(
        string pipeName,
        byte[] message,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
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
                    Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds));
                await pipe.ConnectAsync(connectTimeout, cancellationToken);
                await pipe.WriteAsync(message, cancellationToken);
                await pipe.FlushAsync(cancellationToken);
                return (true, null);
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

    private static byte[] BuildNativeOverlayBlankFrameMessage()
    {
        var message = new byte[NativeOverlayHeaderSize + NativeOverlayBlankFramePayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(0, 4), NativeOverlayMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4, 4), NativeOverlayVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8, 4), NativeOverlayBlankFramePayloadSize);
        message[12] = NativeOverlayFrameType;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(24, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(28, 4), 1);
        message[32] = 0;
        return message;
    }

    private static byte[] BuildNativeOverlayEventMessage(uint type, int value)
    {
        var message = new byte[NativeOverlayEventMessageSize];
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(0, 4), NativeOverlayMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4, 4), NativeOverlayVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8, 4), type);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(12, 4), value);
        return message;
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
        var overlayDirectory = ResolveVlcOverlayDirectory(chat) ?? "";
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
            parts.Add(GetConfiguredKickSetting(chat.KickChatroomIds, effectiveKickChatroomId));
            parts.Add(GetConfiguredKickSetting(chat.KickBroadcasterUserIds, effectiveKickBroadcasterUserId));
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
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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

    private string GetConfiguredKickSetting(Dictionary<string, string> values, string? fallback = null)
    {
        return values.TryGetValue(Target.Channel, out var value) && !string.IsNullOrWhiteSpace(value)
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
        return Math.Clamp(value, 0, 125);
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

        if (settings.KickChatroomIds.TryGetValue(Target.Channel, out var configuredChatroomId) &&
            !string.IsNullOrWhiteSpace(configuredChatroomId))
        {
            chatroomId = configuredChatroomId.Trim();
        }

        if (settings.KickBroadcasterUserIds.TryGetValue(Target.Channel, out var configuredBroadcasterUserId) &&
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
        using var httpClient = new HttpClient();
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

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadKickOverlayChannelInfo(document.RootElement);
    }

    private async Task<KickOverlayChannelInfo?> TryResolveKickChannelMetadataWithCurlAsync(string channel, CancellationToken cancellationToken)
    {
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", "curl.exe was not found; Kick chatroom metadata fallback is unavailable.");
            return null;
        }

        var escapedChannel = Uri.EscapeDataString(channel);
        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickMetadataCurlArguments(escapedChannel))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(18));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"curl.exe timed out resolving Kick metadata for {channel}.");
            return null;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"curl.exe failed resolving Kick metadata for {channel}: {stderr.Trim()}");
            return null;
        }

        using var document = JsonDocument.Parse(stdout);
        return ReadKickOverlayChannelInfo(document.RootElement);
    }

    private static string? ResolveCurlPath()
    {
        var configured = Environment.GetEnvironmentVariable("STREAMLINK_KICK_CURL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return "curl.exe";
    }

    private static IEnumerable<string> BuildKickMetadataCurlArguments(string escapedChannel)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return "15";
        yield return "--user-agent";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        yield return "--header";
        yield return "Accept: application/json,text/plain,*/*";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return $"https://kick.com/{escapedChannel}";
        yield return $"https://kick.com/api/v2/channels/{escapedChannel}";
    }

    private static KickOverlayChannelInfo ReadKickOverlayChannelInfo(JsonElement root)
    {
        string? chatroomId = null;
        long? broadcasterUserId = null;

        if (root.TryGetProperty("chatroom", out var chatroom) &&
            chatroom.TryGetProperty("id", out var id))
        {
            chatroomId = id.ToString();
        }

        if (string.IsNullOrWhiteSpace(chatroomId) &&
            root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("chatroom", out var dataChatroom) &&
            dataChatroom.TryGetProperty("id", out var dataChatroomId))
        {
            chatroomId = dataChatroomId.ToString();
        }

        if (string.IsNullOrWhiteSpace(chatroomId) &&
            root.TryGetProperty("chatroom_id", out var chatroomIdProperty))
        {
            chatroomId = chatroomIdProperty.ToString();
        }

        if (root.TryGetProperty("user", out var user) &&
            user.TryGetProperty("id", out var userId))
        {
            broadcasterUserId = TryGetInt64(userId);
        }

        if (broadcasterUserId is null &&
            root.TryGetProperty("data", out data) &&
            data.TryGetProperty("user", out var dataUser) &&
            dataUser.TryGetProperty("id", out var dataUserId))
        {
            broadcasterUserId = TryGetInt64(dataUserId);
        }

        if (broadcasterUserId is null &&
            root.TryGetProperty("broadcaster_user_id", out var broadcasterUserIdProperty))
        {
            broadcasterUserId = TryGetInt64(broadcasterUserIdProperty);
        }

        if (string.IsNullOrWhiteSpace(chatroomId) || !chatroomId.All(char.IsDigit))
        {
            chatroomId = null;
        }

        return new KickOverlayChannelInfo(chatroomId, broadcasterUserId);
    }

    private static long? TryGetInt64(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out var value) => value,
            System.Text.Json.JsonValueKind.String when long.TryParse(element.GetString(), out var value) => value,
            _ => null
        };
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
        if (playbackEngine is null)
        {
            return;
        }

        if (playbackEngine.UsesNativeOverlay)
        {
            playbackEngine.SetOverlayText(null, false, 0, 0);
            UpdateNativeReplayChatOverlay();
            return;
        }

        if (IsProcessRunning(nativeOverlayProcess))
        {
            return;
        }

        var settings = chatSettings;
        var shouldShow = settings?.Layout == ChatLayout.Overlay &&
            !IsDockedChatOverrideActive &&
            IsChatVisible;
        var text = shouldShow ? BuildOverlayText() : "";
        playbackEngine.SetOverlayText(
            text,
            shouldShow && !string.IsNullOrWhiteSpace(text),
            settings?.Opacity ?? 0.92,
            settings?.FontSize ?? 13);
    }

    private void UpdateNativeReplayChatOverlay()
    {
        UpdateNativeReplayChatOverlay(forceAnimationRepaint: false, animationClock: TimeSpan.Zero);
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

        var messages = ChatMessages.ToArray();
        if (messages.Length == 0)
        {
            CancelNativeReplayOverlayAnimationState();
        }

        var videoHeight = 0;
        if (engine.TryGetVideoSize(out _, out var detectedVideoHeight) && detectedVideoHeight > 0)
        {
            videoHeight = detectedVideoHeight;
        }

        var overlayFontSize = currentSettings is null
            ? settings.VlcOverlayFontSize
            : GetNativeOverlayFontSize(currentSettings);
        var frameKey = BuildNativeReplayOverlayFrameKey(
            engine.NativeOverlayPipeName!,
            engine.NativeOverlayPositionStatePath,
            settings,
            overlayFontSize,
            videoHeight,
            messages);
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

        GetNativeReplayOverlayFrameScheduler().QueueRender(new NativeReplayOverlayFrameRequest(
            plan.Version,
            engine.NativeOverlayPipeName!,
            messages,
            CloneChatSettingsForNativeReplayRender(settings),
            overlayFontSize,
            videoHeight,
            engine.NativeOverlayPositionStatePath,
            plan.FrameKey,
            plan.AnimationClock));
    }

    private void StartNativeReplayOverlayEventHost(string pipeName, string positionStatePath)
    {
        nativeReplayOverlayEventHost.Start(pipeName, positionStatePath);
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
        if (ChatMessages.Count == 0)
        {
            return;
        }

        InvalidateNativeReplayOverlayFrame();
    }

    private void ResetNativeReplayOverlayFrameState()
    {
        nativeReplayOverlayRenderState.Reset();
        CancelNativeReplayOverlayAnimationState();
        nativeReplayOverlayFrameWriteGate.Invalidate();
        nativeReplayOverlayFrameScheduler?.CancelPending();
    }

    private NativeReplayOverlayFrameScheduler GetNativeReplayOverlayFrameScheduler()
    {
        lock (nativeReplayOverlayFrameSchedulerGate)
        {
            return nativeReplayOverlayFrameScheduler ??= new NativeReplayOverlayFrameScheduler(
                logger,
                OnNativeReplayOverlayFrameRendered);
        }
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
            cancellationToken);
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
            return;
        }

        if (!result.Succeeded || result.Frame is null)
        {
            nativeReplayOverlayRenderState.InvalidateFrameKey();
            CancelNativeReplayOverlayAnimationState();
            return;
        }

        TrackNativeReplayOverlayPendingImageLoads(result.PendingImageLoads);
        if (!result.HasAnimatedContent)
        {
            CancelNativeReplayOverlayAnimationTimer();
        }

        nativeReplayOverlayFrameWriteGate.QueueWrite(
            result.Request.PipeName,
            result.Frame,
            result.Request.Version,
            result.Request.FrameKey,
            result.Request.AnimationClock,
            result.HasAnimatedContent,
            result.NextAnimationFrameDelay,
            result.RenderDuration);
    }

    private void OnNativeReplayOverlayFrameWriteSucceeded(NativeReplayOverlayFrameWriteRequest request)
    {
        if (!nativeReplayOverlayRenderState.IsCurrent(request.Version))
        {
            return;
        }

        if (!request.HasAnimatedContent)
        {
            CancelNativeReplayOverlayAnimationTimer();
            return;
        }

        var delay = NormalizeNativeReplayOverlayAnimationDelay(
            request.NextAnimationFrameDelay,
            request.RenderDuration);
        var animationClockStep = NormalizeNativeReplayOverlayAnimationClockStep(request.NextAnimationFrameDelay);
        ScheduleNativeReplayOverlayAnimationFrame(
            delay,
            request.Version,
            request.FrameKey,
            request.AnimationClock + animationClockStep);
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
        lock (nativeReplayOverlayAnimationGate)
        {
            nativeReplayOverlayPendingImageLoads.Clear();
        }
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
        string frameKey,
        TimeSpan animationClock)
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
            animationClock,
            delay);
    }

    private async Task RunNativeReplayOverlayAnimationTimerAsync(
        CancellationTokenSource cancellation,
        long timerVersion,
        long frameVersion,
        string frameKey,
        TimeSpan animationClock,
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
                UpdateNativeReplayChatOverlay(forceAnimationRepaint: true, animationClock);
            }
        });
    }

    internal static TimeSpan NormalizeNativeReplayOverlayAnimationDelay(TimeSpan? delay, TimeSpan renderDuration)
    {
        var normalizedDelay = delay is { } value && value > TimeSpan.Zero
            ? value
            : NativeReplayOverlayDefaultAnimationDelay;
        if (normalizedDelay < NativeReplayOverlayMinimumAnimationDelay)
        {
            normalizedDelay = NativeReplayOverlayMinimumAnimationDelay;
        }

        if (renderDuration <= TimeSpan.Zero)
        {
            return normalizedDelay;
        }

        var pacedTicks = (long)Math.Ceiling(renderDuration.Ticks * NativeReplayOverlayRenderCostDelayMultiplier);
        if (pacedTicks <= 0)
        {
            return normalizedDelay;
        }

        var renderPacedDelay = TimeSpan.FromTicks(Math.Min(
            pacedTicks,
            NativeReplayOverlayMaximumAnimationDelay.Ticks));
        return renderPacedDelay > normalizedDelay
            ? renderPacedDelay
            : normalizedDelay;
    }

    private static TimeSpan NormalizeNativeReplayOverlayAnimationClockStep(TimeSpan? delay)
    {
        return delay is { } value && value > TimeSpan.Zero
            ? value
            : NativeReplayOverlayDefaultAnimationDelay;
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
        IReadOnlyList<ChatMessage> messages)
    {
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
            .Append(messages.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var message in messages.TakeLast(40))
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

    private static bool NativeOverlayControllerSupportsFontSize(string controllerPath)
    {
        var key = BuildNativeOverlayControllerCapabilityKey(controllerPath);
        lock (NativeOverlayControllerCapabilityGate)
        {
            if (NativeOverlayFontSizeSupportByPath.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var supported = ProbeNativeOverlayControllerFontSize(controllerPath);
        lock (NativeOverlayControllerCapabilityGate)
        {
            NativeOverlayFontSizeSupportByPath[key] = supported;
        }

        return supported;
    }

    private static string BuildNativeOverlayControllerCapabilityKey(string controllerPath)
    {
        try
        {
            var info = new FileInfo(controllerPath);
            return $"{info.FullName}|{info.Length.ToString(CultureInfo.InvariantCulture)}|{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return controllerPath;
        }
    }

    private static bool ProbeNativeOverlayControllerFontSize(string controllerPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = controllerPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--help");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)NativeOverlayCapabilityProbeTimeout.TotalMilliseconds))
            {
                TryKillProcess(process);
                return false;
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            return output.Contains(NativeOverlayFontSizeArgument, StringComparison.OrdinalIgnoreCase) ||
                error.Contains(NativeOverlayFontSizeArgument, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string? ResolveVlcOverlayDirectory(ChatSettings settings)
    {
        return VlcOverlayDirectoryResolver.TryResolve(settings.VlcOverlayDirectory);
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

    private string BuildOverlayText()
    {
        return string.Join(
            '\n',
            BuildFallbackOverlayLines());
    }

    private IReadOnlyList<string> BuildFallbackOverlayLines()
    {
        var selectedMessages = new Stack<IReadOnlyList<string>>();
        var remainingLines = FallbackOverlayMaxPhysicalLines;
        foreach (var message in ChatMessages.Reverse().Where(message => !IsSystemChatMessage(message)))
        {
            var lines = BuildFallbackOverlayMessageLines(message);
            if (lines.Count == 0)
            {
                continue;
            }

            IReadOnlyList<string> selectedLines = lines.Count <= remainingLines
                ? lines
                : lines.Take(remainingLines).ToArray();
            selectedMessages.Push(selectedLines);
            remainingLines -= selectedLines.Count;
            if (remainingLines <= 0)
            {
                break;
            }
        }

        return selectedMessages.SelectMany(lines => lines).ToArray();
    }

    private static IReadOnlyList<string> BuildFallbackOverlayMessageLines(ChatMessage message)
    {
        var username = TrimOverlayPart(message.Username, FallbackOverlayUsernameMaxTextElements);
        var prefix = $"{username}: ";
        var firstLineBodyWidth = Math.Max(
            1,
            FallbackOverlayLineMaxTextElements - TextElementCount(prefix));
        var continuationPrefix = new string(' ', FallbackOverlayContinuationPrefixTextElements);
        var continuationBodyWidth = Math.Max(
            1,
            FallbackOverlayLineMaxTextElements - FallbackOverlayContinuationPrefixTextElements);
        var bodyLines = WrapOverlayText(
            ChatTextNormalizer.NormalizeSingleLine(message.Message),
            firstLineBodyWidth,
            continuationBodyWidth);

        if (bodyLines.Count == 0)
        {
            return [prefix.TrimEnd()];
        }

        var lines = new List<string>(bodyLines.Count)
        {
            prefix + bodyLines[0]
        };
        for (var index = 1; index < bodyLines.Count; index++)
        {
            lines.Add(continuationPrefix + bodyLines[index]);
        }

        return lines;
    }

    private static string TrimOverlayPart(string value, int maxLength)
    {
        var normalized = ChatTextNormalizer.NormalizeSingleLine(value);
        if (TextElementCount(normalized) <= maxLength)
        {
            return normalized;
        }

        return ChatTextNormalizer.TruncateTextElements(normalized, Math.Max(0, maxLength - 3)) + "...";
    }

    private static IReadOnlyList<string> WrapOverlayText(string value, int firstLineMaxTextElements, int continuationMaxTextElements)
    {
        var words = SplitTextElementWords(value);
        if (words.Count == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var line = "";
        var lineTextElementCount = 0;
        var maxLineTextElements = Math.Max(1, firstLineMaxTextElements);
        foreach (var word in words)
        {
            foreach (var chunk in SplitTextElementChunks(word, maxLineTextElements))
            {
                var chunkTextElementCount = TextElementCount(chunk);
                if (lineTextElementCount == 0)
                {
                    line = chunk;
                    lineTextElementCount = chunkTextElementCount;
                    continue;
                }

                if (lineTextElementCount + 1 + chunkTextElementCount <= maxLineTextElements)
                {
                    line += " " + chunk;
                    lineTextElementCount += 1 + chunkTextElementCount;
                    continue;
                }

                lines.Add(line);
                maxLineTextElements = Math.Max(1, continuationMaxTextElements);
                line = chunk;
                lineTextElementCount = chunkTextElementCount;
            }
        }

        if (lineTextElementCount > 0)
        {
            lines.Add(line);
        }

        return lines;
    }

    private static IReadOnlyList<string> SplitTextElementWords(string value)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var textElement = enumerator.GetTextElement();
            if (string.IsNullOrWhiteSpace(textElement))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(textElement);
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static IEnumerable<string> SplitTextElementChunks(string value, int maxTextElements)
    {
        maxTextElements = Math.Max(1, maxTextElements);
        var builder = new StringBuilder();
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            if (count >= maxTextElements)
            {
                yield return builder.ToString();
                builder.Clear();
                count = 0;
            }

            builder.Append(enumerator.GetTextElement());
            count++;
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static bool IsSystemChatMessage(ChatMessage message)
    {
        return string.Equals(message.Username, "system", StringComparison.OrdinalIgnoreCase);
    }

    private static int TextElementCount(string value)
    {
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            count++;
        }

        return count;
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

    private sealed record DockedLocalEcho(
        PlatformKind Platform,
        string Channel,
        string Username,
        string Message,
        DateTimeOffset Timestamp);

    private sealed record KickOverlayChannelInfo(string? ChatroomId, long? BroadcasterUserId);

    private readonly record struct ReplayChatBackfillCoverageRange(TimeSpan From, TimeSpan Through);
}
