using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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

public sealed partial class StreamTabViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan PlaybackStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VideoSurfaceReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VideoAspectRatioChangingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VideoAspectRatioStableInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan VideoAspectRatioRetryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReplayClockRefreshInterval = TimeSpan.FromMilliseconds(500);
    // Shared by near-live seek routing and the live-DVR playback-rate safety window.
    private static readonly TimeSpan ReplayLiveEdgeThreshold = TimeSpan.FromSeconds(15);
    // Resume holds the paused timestamp when the DVR has published it. A near-instant
    // pause stays at the live edge instead of opening replay for a negligible delay.
    private static readonly TimeSpan ResumeHoldLiveEdgeTolerance = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PublishedReplayEdgeMargin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PublishedReplayProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReplaySeekStep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReplayClockSampleTolerance = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplayClockMaximumPlausibleDuration = TimeSpan.FromDays(14);
    private const int DefaultPlaybackRateIndex = 2;
    private static readonly float[] PlaybackRateValues = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];
    private static readonly IReadOnlyList<string> PlaybackRateOptionLabels = Array.AsReadOnly(
        new[] { "0.5×", "0.75×", "1×", "1.25×", "1.5×", "1.75×", "2×" });
    private static readonly TimeSpan DefaultTwitchLiveDvrPromotionPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DockedLocalEchoDeduplicationWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ViewerCountRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ViewerCountRetryDelay = TimeSpan.FromSeconds(20);
    internal const int DefaultVolume = 80;
    private const int MaxChatMessages = 100;
    private const int MaxRecentChatMessageIds = 256;
    private const int VideoAspectRatioStableSampleThreshold = 3;
    private const string TwitchLiveDvrReplayIdPrefix = "live-dvr-";
    private const double DefaultVideoAspectRatio = 16.0 / 9.0;
    private readonly ReplayClockState replayClock;
    private readonly NativeChatOverlayController nativeOverlay;
    private readonly IStreamlinkService streamlinkService;
    private readonly IPlaybackEngineFactory playbackFactory;
    private readonly IChatClientFactory chatFactory;
    private readonly IViewerCountService? viewerCountService;
    private readonly IReplayResolver? replayResolver;
    private readonly ITwitchSubOnlyVodResolver? twitchSubOnlyVodResolver;
    private readonly VodChatController vodChat;
    private readonly IAppLogger logger;
    private readonly Action<Action> dispatch;
    private readonly BoundedUiLogBuffer<string> logBuffer;
    private readonly PlaybackResourceCoordinator playbackResourceCoordinator;
    private readonly PlaybackCleanupController playbackCleanupController;
    private readonly ChatClientEventCoordinator chatClientEventCoordinator;
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
    private readonly object replayClockUiGate = new();
    private readonly object replayPlaybackUrlResolutionGate = new();
    private readonly object replaySeekPreviewUiGate = new();
    private readonly SemaphoreSlim replayPlaybackTransitionGate = new(1, 1);
    private readonly SemaphoreSlim playbackRateChangeGate = new(1, 1);
    private readonly object playbackRateSelectionGate = new();
    private TaskCompletionSource<IntPtr> videoHandleReady = CreateVideoHandleReadySource();
    private TaskCompletionSource videoSurfaceStateChanged = CreateVideoSurfaceStateChangedSource();
    private IStreamTransportSession? streamSession;
    private IPlaybackEngine? playbackEngine;
    private IChatClient? chatClient;
    private ITwitchPredictionClient? twitchPredictionClient;
    private ChatSettings? chatSettings;
    private long? manualLivePausedAtTimestamp;
    private AppSettings? currentSettings;
    private ParkingVideoSurface? parkingVideoSurface;
    private VideoSurface? videoSurface;
    internal VideoSurfacePresenter? VideoSurfacePresenterOwner { get; set; }
    private CancellationTokenSource? viewerCountPollingCancellation;
    private CancellationTokenSource? videoAspectRatioPollingCancellation;
    private CancellationTokenSource? replayClockPollingCancellation;
    private CancellationTokenSource? replayAvailabilityRefreshCancellation;
    private CancellationTokenSource? liveDvrPromotionPollingCancellation;
    private Task? chatConnectionTask;
    private CancellationTokenSource? chatConnectionCancellation;
    private Task? viewerCountPollingTask;
    private Task? videoAspectRatioPollingTask;
    private Task? replayClockPollingTask;
    private Task? replayAvailabilityRefreshTask;
    private Task? liveDvrPromotionPollingTask;
    private ReplayPlaybackUrlResolution? replayPlaybackUrlResolution;
    private CancellationTokenSource? replayInputPreparationCancellation;
    private Uri? replayInputPreparationUri;
    private IPlaybackEngine? replayInputPreparationEngine;
    private ReplayPlaybackUrlReadiness replayPlaybackUrlReadiness = ReplayPlaybackUrlReadiness.None;
    private ReplayPlaybackUrlKey? replayPlaybackUrlReadinessKey;
    private ReplayPlaybackUrlKey? currentReplayPlaybackKey;
    private ReplaySessionInfo? replaySession;
    private Uri? explicitVodPlaybackUri;
    private long replayAvailabilityRefreshVersion;
    private long chatConnectionVersion;
    private long replaySeekOperationVersion;
    private ReplayClockUiUpdate? pendingReplayClockUiSample;
    private bool replayClockUiDispatchQueued;
    private double pendingReplaySeekPreviewTextValue;
    private bool replaySeekPreviewTextDispatchQueued;
    private bool chatMessageUiDispatchQueued;
    // Bumped when a VOD seek clears the visible chat, so any message already queued for the
    // dispatcher from the old position is dropped instead of reappearing after the clear.
    private long chatEpoch;
    private string requestedReplayChatStatus = "";
    private string replayChatStatusText = "";
    private ChatMessage? replayChatStatusMessage;
    private bool isDirectExplicitVodReplayPlayback;
    private CancellationTokenSource? activeStartCancellation;
    private bool multiStreamResourceProfile;
    private bool playbackEngineNativeOverlayRequested;
    private string playbackEngineOverlayDirectory = "";
    private string title;
    private string profileImageUrl = "";
    private string streamTitle = "";
    private string categoryName;
    private bool hasPolledCategory;
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
    private bool neverMute;
    private bool isSelectedForAudio = true;
    private IntPtr videoHandle;
    private long videoHandleVersion;
    private string outgoingChatText = "";
    private long outgoingChatRevision;
    private string twitchPredictionTitle = "";
    private int twitchPredictionDurationSeconds = 120;
    private string viewerCountText = "--";
    private string viewerCountToolTip = "Viewer count has not loaded yet.";
    private bool isReplaySeekBarVisible;
    private bool isReplaySeekEnabled;
    private bool isReplaySeekInProgress;
    private bool isReplaySkipInProgress;
    private bool isReplaySeekPreviewActive;
    private bool isReplayMode;
    private bool isBehindLive;
    private bool backgroundResourceServicesSuspended;
    private bool livePlaybackConnectionSuspended;
    private double replaySeekValue;
    private double replaySeekSliderValue;
    private double replaySeekMaximum = 1;
    private int playbackRateIndex = DefaultPlaybackRateIndex;
    private int selectedPlaybackRateIndex = DefaultPlaybackRateIndex;
    private long playbackRateChangeVersion;
    private string replayElapsedText = "0:00";
    private string replayDurationText = "0:00";
    private string replayLiveStateText = "Live";
    private string replaySeekToolTip = "Replay availability has not been checked yet.";
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
        var vodChatProvider = dependencies.VodChatProvider;
        var twitchSubOnlyVodResolver = dependencies.TwitchSubOnlyVodResolver;
        var twitchLiveDvrPromotionPollInterval = dependencies.TwitchLiveDvrPromotionPollInterval;

        Target = target;
        if (target.IsOfflineVod) isChatVisible = false;
        replayClock = new ReplayClockState(Target, () => Status, () => IsReplayMode,
            () => Volatile.Read(ref replaySeekOperationVersion), IsReplayClockSampleCurrent,
            () => ReplaySeekValue,
            () => PlaybackRateValues[Volatile.Read(ref playbackRateIndex)],
            () => playbackEngine?.TryGetPlaybackClock(out var clock) == true ? clock : null);
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
        logBuffer = new BoundedUiLogBuffer<string>(Logs, this.dispatch, null, 300, static line => line);
        this.replayResolver = replayResolver;
        this.twitchSubOnlyVodResolver = twitchSubOnlyVodResolver;
        vodChat = new VodChatController(vodChatProvider, logger);
        vodPlaybackHistory = dependencies.VodPlaybackHistory;
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
        nativeOverlay = new NativeChatOverlayController(Target, logger, this.dispatch, AddSystemMessage,
            CaptureNativeOverlayPlayback, CaptureNativeOverlayChat, () => [.. ChatMessages], () => currentSettings,
            StartChatAsync, EnsureChatClientConnectedAsync, ShouldKeepChatClientForVodChatCapture,
            dependencies.OpenChatLink, lifetimeCancellation.Token);
        title = target.TabTitle;
        profileImageUrl = (target.ProfileImageUrl ?? "").Trim();
        categoryName = target.CategoryName?.Trim() ?? "";
        volume = NormalizeVolume(initialVolume);
        SendChatMessageCommand = CreateCommand(SendChatMessageAsync, () => !string.IsNullOrWhiteSpace(OutgoingChatText) && CanSendChatMessages);
        RewindReplay30SecondsCommand = CreateCommand(RewindReplay30SecondsAsync, () => CanStepReplay);
        FastForwardReplay30SecondsCommand = CreateCommand(FastForwardReplay30SecondsAsync, () => CanStepReplay);
        SkipBackwardCommand = CreateCommand(
            () => SkipReplayAsync(-TimeSpan.FromSeconds(currentSettings?.Hotkeys.SkipBackwardSeconds ?? HotkeySettings.DefaultSkipSeconds)),
            () => CanStepReplay && !isReplaySkipInProgress);
        SkipForwardCommand = CreateCommand(
            () => SkipReplayAsync(TimeSpan.FromSeconds(currentSettings?.Hotkeys.SkipForwardSeconds ?? HotkeySettings.DefaultSkipSeconds)),
            () => CanStepReplay && !isReplaySkipInProgress);
        ReturnToLiveCommand = CreateCommand(ReturnToLiveAsync, () => CanReturnToLive);
        StartTwitchPredictionCommand = CreateCommand(StartTwitchPredictionAsync, () => CanStartTwitchPrediction);
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
    public AsyncRelayCommand SkipBackwardCommand { get; }
    public AsyncRelayCommand SkipForwardCommand { get; }
    internal HotkeySettings? PlaybackHotkeys => currentSettings?.Hotkeys;
    public AsyncRelayCommand ReturnToLiveCommand { get; }
    public AsyncRelayCommand StartTwitchPredictionCommand { get; }
    public RelayCommand AddTwitchPredictionOutcomeCommand { get; }
    public event EventHandler? AudioStateApplied;

    internal Task PlaybackCleanupIdleTask => playbackCleanupController.IdleTask;
    internal CancellationToken LifetimeToken => lifetimeCancellation.Token;

    internal NativeChatOverlayController NativeOverlay => nativeOverlay;
    internal ReplayClockState ReplayClock => replayClock;

    internal Task VodChatIdleTask => vodChat.WaitUntilCaughtUpAsync();

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

    internal void ApplyOpeningMetadata(StreamMetadataResult metadata)
    {
        if (disposed) return;
        if (!HasProfileImage) SetProfileImageUrl(metadata.ProfileImageUrl);
        // A live poll is newer than the opening request, even when it clears the category.
        if (!hasPolledCategory && !HasCategory && metadata.State == StreamMetadataState.Available)
        {
            SetCategoryName(metadata.CategoryName);
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

    public string ChatModeText => Target.IsOfflineVod ? "OFFLINE VOD" :
        Target.IsExplicitVod || IsReplayMode || IsBehindLive ? "REPLAY CHAT" : "LIVE CHAT";

    public string ReplayChatStatusText => replayChatStatusText;

    public bool HasReplayChatStatus => replayChatStatusText.Length > 0;

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
            if (status == value)
            {
                return;
            }

            // Capture when playback actually pauses, rather than on the next 500ms clock poll.
            // The live timeline (and sometimes VLC's HLS clock) can keep moving while paused.
            var pauseClock = value == PlaybackStatus.Paused && replaySession is { IsAvailable: true } replay
                ? ResolveReplayClock(replay, Volatile.Read(ref replaySeekOperationVersion), IsReplaySeekInProgress)
                : (ReplayClockSnapshot?)null;
            replayClock.ApplyPlaybackState(status, value, pauseClock, IsReplayMode, () => status = value);

            UpdateLivePlaybackMonitoring();
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsVodFinished));
        }
    }

    public bool IsVodFinished => Target.IsExplicitVod && Status == PlaybackStatus.Finished;

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
        PlaybackStatus.Finished => "VOD finished",
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
                UpdateLivePlaybackMonitoring();
                OnPropertyChanged(nameof(ChatModeText));
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
                UpdateLivePlaybackMonitoring();
                OnPropertyChanged(nameof(ChatModeText));
                OnPropertyChanged(nameof(IsPlaybackRateControlVisible));
                OnPropertyChanged(nameof(CanChangePlaybackRate));
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

    public IReadOnlyList<string> PlaybackRateOptions => PlaybackRateOptionLabels;

    public int PlaybackRateIndex
    {
        get => Volatile.Read(ref selectedPlaybackRateIndex);
        set
        {
            if ((uint)value >= PlaybackRateValues.Length)
            {
                dispatch(() => OnPropertyChanged(nameof(PlaybackRateIndex)));
                return;
            }

            if (value == Volatile.Read(ref selectedPlaybackRateIndex))
            {
                return;
            }

            _ = ApplyPlaybackRateSelectionAsync(value);
        }
    }

    public bool IsPlaybackRateControlVisible => Target.IsExplicitVod || IsBehindLive;

    public bool CanChangePlaybackRate => IsPlaybackRateControlVisible && playbackEngine is not null;

    private async Task ApplyPlaybackRateSelectionAsync(
        int requestedIndex,
        long? expectedSeekOperationVersion = null,
        long? expectedPlaybackStateVersion = null)
    {
        long requestVersion;
        bool selectionChanged;
        lock (playbackRateSelectionGate)
        {
            requestVersion = Interlocked.Increment(ref playbackRateChangeVersion);
            selectionChanged = Interlocked.Exchange(ref selectedPlaybackRateIndex, requestedIndex) != requestedIndex;
        }

        if (selectionChanged)
        {
            dispatch(() =>
            {
                if (!disposed && requestVersion == Volatile.Read(ref playbackRateChangeVersion))
                {
                    OnPropertyChanged(nameof(PlaybackRateIndex));
                }
            });
        }

        var enteredGate = false;
        try
        {
            await playbackRateChangeGate.WaitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            enteredGate = true;
            if (disposed || requestVersion != Volatile.Read(ref playbackRateChangeVersion))
            {
                return;
            }

            if (expectedSeekOperationVersion is { } expectedSeekVersion &&
                !IsReplayClockSampleCurrent(expectedSeekVersion, expectedPlaybackStateVersion))
            {
                RestorePlaybackRateSelection(requestVersion);
                return;
            }

            var engine = playbackEngine;
            if (engine is null || !CanChangePlaybackRate)
            {
                RestorePlaybackRateSelection(requestVersion);
                return;
            }

            var rate = PlaybackRateValues[requestedIndex];
            var applied = await engine.TrySetPlaybackRateAsync(rate, lifetimeCancellation.Token)
                .ConfigureAwait(false);
            var appliedToCurrentEngine = applied && ReferenceEquals(engine, playbackEngine) && CanChangePlaybackRate;
            if (appliedToCurrentEngine)
            {
                if (!IsReplaySeekInProgress && replaySession is { IsAvailable: true } replay)
                {
                    var observedAtUtc = DateTimeOffset.UtcNow;
                    var seekVersion = Volatile.Read(ref replaySeekOperationVersion);
                    var playbackStateVersion = replayClock.PlaybackStateVersion;
                    // A rate resynchronization can temporarily make VLC report time zero.
                    // Use the same clock validation as the seekbar before moving its anchor.
                    var clock = ResolveReplayClock(replay, seekVersion, sampleBeganDuringSeek: false,
                        sampledPlaybackStateVersion: playbackStateVersion);
                    if (IsReplayClockSampleCurrent(seekVersion, playbackStateVersion))
                    {
                        replayClock.ReanchorForPlaybackRateChange(
                            clock.Position, clock.Duration, seekVersion, playbackStateVersion, observedAtUtc);
                    }
                }

                Interlocked.Exchange(ref playbackRateIndex, requestedIndex);
            }
            else
            {
                logger.Write(AppLogLevel.Warning, "Playback",
                    $"Could not change playback speed to {PlaybackRateOptionLabels[requestedIndex]} for {Target.DisplayName}.");
                RestorePlaybackRateSelection(requestVersion);
                return;
            }

            dispatch(() =>
            {
                if (disposed || requestVersion != Volatile.Read(ref playbackRateChangeVersion))
                {
                    return;
                }

                OnPropertyChanged(nameof(PlaybackRateIndex));
            });
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Playback", $"Changing playback speed failed for {Target.DisplayName}.", ex);
            RestorePlaybackRateSelection(requestVersion);
        }
        finally
        {
            if (enteredGate)
            {
                playbackRateChangeGate.Release();
            }
        }
    }

    private void RestorePlaybackRateSelection(long requestVersion)
    {
        lock (playbackRateSelectionGate)
        {
            if (requestVersion != Volatile.Read(ref playbackRateChangeVersion))
            {
                return;
            }

            Interlocked.Exchange(ref selectedPlaybackRateIndex, Volatile.Read(ref playbackRateIndex));
        }

        dispatch(() =>
        {
            if (!disposed && requestVersion == Volatile.Read(ref playbackRateChangeVersion))
            {
                OnPropertyChanged(nameof(PlaybackRateIndex));
            }
        });
    }

    internal ReplaySeekPreviewSource? ReplayPreviewSource
    {
        get
        {
            if (replaySession is not { IsAvailable: true } replay) return null;
            var videoId = replay.Platform == PlatformKind.Twitch && replay.MediaKind == ReplayMediaKind.Archive &&
                replay.ReplayId.Length is > 0 and <= 32 && replay.ReplayId.All(char.IsAsciiDigit)
                    ? replay.ReplayId : null;
            // Kick has no Twitch storyboard. Preview the selected media playlist already
            // resolved for playback, rather than its original master playlist or VOD page.
            Uri? playlist = Target.IsExplicitKickVod ? explicitVodPlaybackUri : null;
            if (!Target.IsExplicitVod && currentSettings is { } settings)
            {
                // Reuse the resolved replay URL. Hovering must never resolve or seek the live player.
                if (Uri.TryCreate(replay.ReplayUrl, UriKind.Absolute, out var direct) &&
                    direct.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) playlist = direct;
                var key = CreateReplayPlaybackUrlKey(replay, settings);
                lock (replayPlaybackUrlResolutionGate)
                {
                    if (replayPlaybackUrlResolution is { } resolved && resolved.Key.Equals(key) &&
                        resolved.Task.IsCompletedSuccessfully) playlist = resolved.Task.Result.StreamUri;
                }
            }
            return videoId is null && playlist is null ? null : new(replay.ReplayId, videoId, playlist,
                replay.Platform, replay.StreamStartedAtUtc, currentSettings?.VlcDirectory ?? "");
        }
    }

    public bool CanSendChatMessages => !Target.IsExplicitVod && !IsBehindLive;

    private void RaiseReplaySeekAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanSeekReplay));
        OnPropertyChanged(nameof(CanStepReplay));
        RewindReplay30SecondsCommand.RaiseCanExecuteChanged();
        FastForwardReplay30SecondsCommand.RaiseCanExecuteChanged();
        SkipBackwardCommand.RaiseCanExecuteChanged();
        SkipForwardCommand.RaiseCanExecuteChanged();
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
        private set
        {
            if (SetProperty(ref backgroundResourceServicesSuspended, value))
                UpdateLivePlaybackMonitoring();
        }
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
            _ = TryReleaseNativeOverlayChatInputFocusAsync();
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
            _ = TryReleaseNativeOverlayChatInputFocusAsync();
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
            if (value && NeverMute)
            {
                return;
            }

            if (SetProperty(ref isMuted, value))
            {
                ApplyAudio();
            }
        }
    }

    public bool NeverMute
    {
        get => neverMute;
        set
        {
            if (!SetProperty(ref neverMute, value))
            {
                return;
            }

            // Enabling protection clears manual mute instead of leaving a hidden mute
            // request that could unexpectedly return when protection is turned off.
            if (value && isMuted)
            {
                isMuted = false;
                OnPropertyChanged(nameof(IsMuted));
            }

            OnPropertyChanged(nameof(IsAutoMuted));
            ApplyAudio();
        }
    }

    public bool IsAutoMuted => !NeverMute && !isSelectedForAudio;

    public string OutgoingChatText
    {
        get => outgoingChatText;
        set
        {
            if (outgoingChatText != value)
            {
                outgoingChatRevision++;
                SetProperty(ref outgoingChatText, value);
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

    internal bool IsNativeReplayOverlayEventHostRunning { get => nativeOverlay.IsNativeReplayOverlayEventHostRunning; }

    internal string? NativeReplayOverlayEventHostPipeName { get => nativeOverlay.NativeReplayOverlayEventHostPipeName; }

    internal int NativeReplayOverlayMessageOffset { get => nativeOverlay.NativeReplayOverlayMessageOffset; }

    internal int NativeReplayOverlayMaximumMessageOffset { get => nativeOverlay.NativeReplayOverlayMaximumMessageOffset; }
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

        // New playback engines are bound explicitly when they are created. For an
        // existing engine, avoid another libVLC set_hwnd call when a presenter reuses
        // the same native surface in a different window.
        if (handleChanged)
        {
            playbackEngine?.SetVideoHandle(handle);
        }
    }

    internal VideoSurface GetOrCreateVideoSurface()
    {
        return videoSurface ??= new VideoSurface();
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

    internal bool TryGetLastVideoSize(out int width, out int height) => nativeOverlay.TryGetLastVideoSize(out width, out height);

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
                var preserveReplayOverlay = (Target.IsExplicitVod || IsReplayMode || IsBehindLive) &&
                    settings.Layout == ChatLayout.Overlay && !IsDockedChatOverrideActive && IsChatVisible;
                _ = StopNativeOverlayChatAsync(clearOverlay: !preserveReplayOverlay);
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

        if (Target.IsExplicitVod)
        {
            // Chat restarts also happen during settings changes and before replay metadata
            // is ready. The immutable target, not that transient state, chooses the source.
            await StopChatAsync(clearNativeOverlay: false);
            if (replaySession is { IsAvailable: true } replay)
            {
                StartVodChat(replay, GetCurrentReplayStepOffset());
            }
            InvalidateNativeReplayOverlayFrame();
            UpdateNativeChatOverlay();
            return;
        }

        var shouldUseNativeOverlayController = ShouldUseNativeOverlayController(settings);
        var shouldKeepCaptureChatClient = ShouldKeepChatClientForVodChatCapture(settings);
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

        var requestedNativeOverlay = !Target.IsOfflineVod && ShouldRequestNativeOverlay(settings.Chat);
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
                if (Status == PlaybackStatus.Playing && playbackEngine is not null &&
                    !(restorePausedByTabSwitch && NeverMute))
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
        if (disposed || cancellationToken.IsCancellationRequested)
        {
            return PlaybackStartResult.NotStarted;
        }

        CancelLivePlaybackRecovery();

        if (!Target.IsOfflineVod && string.IsNullOrWhiteSpace(settings.StreamlinkPath))
        {
            throw new InvalidOperationException("Configure the Streamlink executable path in Settings.");
        }

        if (string.IsNullOrWhiteSpace(settings.VlcDirectory))
        {
            throw new InvalidOperationException("Configure the VLC directory in Settings.");
        }

        using var activeStart = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
        var startCancellationToken = activeStart.Token;
        try
        {
            await lifecycleGate.WaitAsync(startCancellationToken);
        }
        catch (OperationCanceledException) when (startCancellationToken.IsCancellationRequested)
        {
            return PlaybackStartResult.NotStarted;
        }

        if (disposed || startCancellationToken.IsCancellationRequested)
        {
            lifecycleGate.Release();
            return PlaybackStartResult.NotStarted;
        }

        RegisterActiveStartCancellation(activeStart);
        var playbackTransitionAcquired = false;
        try
        {
            await playbackTransitionGate.WaitAsync(startCancellationToken);
            playbackTransitionAcquired = true;
        }
        catch (OperationCanceledException) when (startCancellationToken.IsCancellationRequested)
        {
            ClearActiveStartCancellation(activeStart);
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
        CancellationTokenSource? streamStartCancellation = null;
        Task<IStreamTransportSession>? pendingStreamSession = null;
        var pendingStreamSessionNeedsCleanup = false;
        Uri? directPlaybackUri = null;
        TwitchSubOnlyVodResolution? subOnlyVodResolution = null;
        var playbackStarted = false;

        try
        {
            await StopViewerCountPollingAsync();
            if (Target.IsExplicitVod)
            {
                SetViewerCount("VOD", "Video on demand.");
            }
            else
            {
                SetViewerCountPending("Loading viewer count...");
            }

            await StopChatAsync(clearNativeOverlay: true);
            await StopPlaybackOnlyAsync(PlaybackStopTimeout);
            ResetPlaybackRate();
            CancelReplayAvailabilityRefresh();
            ResetReplayState("Replay availability has not been checked yet.");

            Status = PlaybackStatus.Resolving;
            var customArguments = Target.IsOfflineVod ? [] : CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments);
            if (Target.IsOfflineVod) directPlaybackUri = GetOfflinePlaybackUri();
            else
            {
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
                        liveRecoveryRequest = liveRequest;
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
            }

            var enableNativeOverlay = !Target.IsOfflineVod && ShouldRequestNativeOverlay(settings.Chat);
            var nativeOverlayPositionStatePath = enableNativeOverlay
                ? BuildNativeOverlayPositionStatePath(Target)
                : null;
            playbackEngine = await playbackFactory.CreateAsync(
                settings.VlcDirectory,
                enableNativeOverlay,
                nativeOverlayPositionStatePath,
                startCancellationToken,
                settings.VideoRendererMode);
            OnPropertyChanged(nameof(CanChangePlaybackRate));
            startCancellationToken.ThrowIfCancellationRequested();
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
                    resolvedStreamSession = await pendingStreamSession!.WaitAsync(startCancellationToken);
                    pendingStreamSessionNeedsCleanup = false;
                }
                catch
                {
                    // WaitAsync can be cancelled while a provider still owns an
                    // in-flight transport. Keep ownership of that eventual result.
                    pendingStreamSessionNeedsCleanup = !pendingStreamSession!.IsCanceled && !pendingStreamSession.IsFaulted;
                    throw;
                }

                streamSession = resolvedStreamSession;
                startCancellationToken.ThrowIfCancellationRequested();
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
            vodStartupPosition = await GetVodResumePositionAsync(startCancellationToken);
            if (vodStartupPosition > TimeSpan.Zero)
            {
                await playbackEngine.PlayFromAsync(playbackUri, vodStartupPosition, Volume, CurrentAudioState, startCancellationToken);
            }
            else
            {
                await playbackEngine.PlayAsync(playbackUri, Volume, CurrentAudioState, startCancellationToken);
            }
            startCancellationToken.ThrowIfCancellationRequested();
            isDirectExplicitVodReplayPlayback = Target.IsExplicitVod && streamSession is null;
            // A fresh player is running even when the previous player was automatically
            // paused. The current visibility policy below decides whether to pause it again.
            PausedByTabSwitch = false;
            ApplyAudio();
            Status = PlaybackStatus.Playing;
            playbackStarted = true;
            StartVodResumeTracking();
            StartLivePlaybackMonitoring();
            if (Target.IsExplicitVod)
            {
                explicitVodPlaybackUri = playbackUri;
                InitializeExplicitVodReplaySession(settings, subOnlyVodResolution);
                StartReplayClockPolling();
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
                    StartReplayAvailabilityRefreshInBackground(settings);
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
                        startCaptureChatClient: ShouldKeepChatClientForVodChatCapture(settings));
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
                playbackCleanupController.Observe(DisposeUnclaimedStreamSessionAsync(pendingStreamSession, streamStartCancellation));
                streamStartCancellation = null;
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
                playbackCleanupController.Observe(DisposeUnclaimedStreamSessionAsync(pendingStreamSession, streamStartCancellation));
                streamStartCancellation = null;
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

    public Task PauseOrResumeAsync()
    {
        CancelLivePlaybackRecovery();
        return RunPlaybackTransitionAsync(PauseOrResumeCoreAsync);
    }

    public bool PausedByTabSwitch { get; private set; }

    public Task PauseForTabSwitchAsync()
    {
        if (!NeverMute) CancelLivePlaybackRecovery();
        return RunPlaybackTransitionAsync(PauseForTabSwitchCoreAsync);
    }

    public Task ResumeFromTabSwitchAsync() => RunPlaybackTransitionAsync(ResumeFromTabSwitchCoreAsync);

    private async Task RunPlaybackTransitionAsync(Func<Task> transition)
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
            await transition();
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

            if (Status == PlaybackStatus.Playing) manualLivePausedAtTimestamp = null;
            ApplyAudio();
        }
        else if (Status == PlaybackStatus.Playing)
        {
            // A manual pause captures a position to restore when replay has published it.
            // Automatic inactive-tab suspension uses the separate connection-stopping path below.
            livePlaybackConnectionSuspended = false;
            CapturePauseHold(allowLiveTransition: true);
            await playbackEngine.PauseAsync(lifetimeCancellation.Token);
            manualLivePausedAtTimestamp = Target.Kind == StreamTargetKind.Live && !IsReplayMode
                ? Stopwatch.GetTimestamp()
                : null;
            Status = PlaybackStatus.Paused;
            PausedByTabSwitch = false;
            CaptureVodResumePosition();
            await SaveVodResumePositionAsync(force: true);
        }
    }

    private async Task PauseForTabSwitchCoreAsync()
    {
        // The request may have waited for a playback transition while the user
        // enabled protection. Startup also calls this method directly.
        if (NeverMute)
        {
            return;
        }

        IsBackgroundResourceServicesSuspended = true;
        var engine = playbackEngine;
        if (engine is not null && Status == PlaybackStatus.Playing && !PausedByTabSwitch)
        {
            if (CanSuspendLivePlaybackConnection())
            {
                // Do not capture a replay hold here.  The Streamlink process and its URI remain
                // valid, while stopping only libVLC closes the local HTTP reader and prevents the
                // external-HTTP ring buffer from advancing while this tab is hidden.
                replayClock.ClearResumeHold();
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
            logger.Write(
                AppLogLevel.Info,
                "Playback",
                $"Paused {Target.DisplayName} because its video is off-grid " +
                $"(visible={IsVideoVisible}, detached={IsDetached}, " +
                $"suspendedConnection={livePlaybackConnectionSuspended}). " +
                "Enable \"Keep inactive tabs running\" to keep hidden tabs playing.");
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
                logger.Write(
                    AppLogLevel.Info,
                    "Playback",
                    $"Resuming {Target.DisplayName} after it became visible again " +
                    $"(reconnecting={reconnectingLivePlayback}).");
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

        ResumeBackgroundResourceServices(
            suppressReplayPlaybackUrlResolution: reconnectingLivePlayback);
    }

    private async Task StopBackgroundResourceServicesAsync()
    {
        CancelReplayInputPreparation();
        CaptureVodResumePosition();
        await SaveVodResumePositionAsync(force: true);
        CancelReplayAvailabilityPolling();
        await StopLiveDvrPromotionPollingAsync();
        await StopReplayClockPollingAsync();
        await StopViewerCountPollingAsync();
        await StopVideoAspectRatioPollingAsync();
    }

    private void ResumeBackgroundResourceServices(
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
        StartReplayAvailabilityRefreshInBackground(
            settings,
            prefetchPlaybackUrl: !suppressReplayPlaybackUrlResolution);

        if (replaySession is { IsAvailable: true })
        {
            QueueCachedReplayInputPreparation();
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
        replayClock.ClearResumeHold();
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
        replayClock.CaptureResumeHold(allowLiveTransition, null);

        // Explicit VODs do not drift on resume, and without an available replay/DVR source there is
        // no seekable timeline to hold against.
        if (Target.IsExplicitVod ||
            replaySession is not { IsAvailable: true } replay)
        {
            return;
        }

        // Behind live this is the real playback offset; at the live edge it is the live-edge offset
        // (which grows with wall-clock), so after a real pause it lands us behind live by the pause length.
        replayClock.CaptureResumeHold(allowLiveTransition, ResolveReplayClock(
            replay,
            Volatile.Read(ref replaySeekOperationVersion),
            sampleBeganDuringSeek: IsReplaySeekInProgress).Position);

        // Warm the replay/DVR playback URL while the frame is frozen so a live -> replay
        // resume has no URL-resolution latency. Reuses any valid in-flight/successful resolution.
        if (currentSettings is { } settings)
        {
            QueueReplayPlaybackUrlResolution(replay, settings);
        }
    }

    private async Task ResumeWithHoldAsync(CancellationToken cancellationToken = default)
    {
        var holdPosition = replayClock.ResumeHoldPosition;
        var allowLiveTransition = replayClock.ResumeHoldAllowsLiveTransition;

        if (playbackEngine is null)
        {
            return;
        }

        if (await ShouldReconnectPausedLiveInputAsync(holdPosition, cancellationToken))
        {
            logger.Write(AppLogLevel.Info, "Playback",
                $"Reconnecting {Target.DisplayName} at the live edge because its paused position is not confirmed in the published replay.");
            await ResumeLivePlaybackConnectionAsync(cancellationToken);
            return;
        }

        // The adaptive input filter confirms that the current replay retained its
        // decoder and HLS buffer. Unpause that input without opening or seeking it.
        if (IsReplayMode && await playbackEngine.TryResumeReplayAsync(cancellationToken))
        {
            Status = PlaybackStatus.Playing;
            replayClock.ClearResumeHold();
            PausedByTabSwitch = false;
            return;
        }

        // Decide while the old player is still paused. Unpausing it first can present the
        // live edge; opening a replacement at zero then seeking presents the VOD beginning.
        // Open directly at the held timestamp and publish Playing only after it is ready.
        if (holdPosition is { } position &&
            !Target.IsExplicitVod &&
            replaySession is { IsAvailable: true } replay &&
            (IsReplayMode || (allowLiveTransition && CanSeekReplay)))
        {
            var duration = GetCurrentReplayDuration(replay);
            var targetOffset = ClampReplayOffset(position, duration);
            if (duration - targetOffset > ResumeHoldLiveEdgeTolerance)
            {
                await SeekReplaySerializedAsync(
                    position,
                    cancellationToken,
                    forceReload: true,
                    holdExactPosition: true,
                    playbackTransitionAlreadyHeld: true,
                    completionIntent: SeekCompletionIntent.Resume);
                PausedByTabSwitch = false;
                return;
            }
        }

        await playbackEngine.ResumeAsync(cancellationToken);
        Status = PlaybackStatus.Playing;
        replayClock.ClearResumeHold();
        PausedByTabSwitch = false;
    }

    private async Task<bool> ShouldReconnectPausedLiveInputAsync(
        TimeSpan? holdPosition, CancellationToken cancellationToken)
    {
        if (Target.Kind != StreamTargetKind.Live || IsReplayMode || streamSession is null ||
            manualLivePausedAtTimestamp is not { } pausedAt ||
            Stopwatch.GetElapsedTime(pausedAt) <= ResumeHoldLiveEdgeTolerance)
        {
            return false;
        }

        if (holdPosition is not { } position ||
            replaySession is not { IsAvailable: true } replay ||
            !CanSeekReplay ||
            !TryGetReadyReplayPlaybackUri(replay, out var replayUri))
        {
            return true;
        }

        if (!HlsReplayTimeline.IsPlaylist(replayUri)) return false;

        // The wall-clock live position can outrun the published DVR playlist.
        // Check its current edge because replay metadata may be older than the
        // playlist, while opening beyond the playlist leaves VLC's gate black.
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(PublishedReplayProbeTimeout);
            var published = await HlsReplayTimeline.ReadPublishedDurationAsync(
                replayUri, replay.Platform, probe.Token);
            return published is not { } edge || position >= edge - PublishedReplayEdgeMargin;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Debug, "Playback",
                $"Could not confirm the published replay edge for {Target.DisplayName}.", ex);
            return true;
        }
    }

    private bool TryGetReadyReplayPlaybackUri(ReplaySessionInfo replay, out Uri uri)
    {
        if (Target.IsOfflineVod)
        {
            uri = GetOfflinePlaybackUri();
            return true;
        }
        if (TryCreateDirectReplayPlaybackUri(replay.ReplayUrl, out uri)) return true;
        if (currentSettings is not { } settings)
        {
            uri = null!;
            return false;
        }

        var key = CreateReplayPlaybackUrlKey(replay, settings);
        lock (replayPlaybackUrlResolutionGate)
        {
            if (replayPlaybackUrlResolution is { } resolution &&
                resolution.Key.Equals(key) && resolution.Task.IsCompletedSuccessfully)
            {
                uri = resolution.Task.Result.StreamUri;
                return true;
            }
        }

        uri = null!;
        return false;
    }

    public async Task StopAsync()
    {
        CancelLivePlaybackRecovery();
        if (disposed)
        {
            return;
        }

        // The start owns lifecycleGate until its transport/player is ready. Cancel
        // it before waiting for that gate, including a wait for the video surface.
        CancelActiveStart();
        CancelReplayAvailabilityRefresh();
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

    private async Task SkipReplayAsync(TimeSpan delta)
    {
        // Both directions share admission before the seek yields to the dispatcher.
        // A second keypress must not queue a seek based on the old playback clock.
        if (isReplaySkipInProgress || !CanStepReplay) return;
        isReplaySkipInProgress = true;
        try
        {
            await SeekReplayByAsync(delta, useExactStep: true);
        }
        finally
        {
            isReplaySkipInProgress = false;
            SkipBackwardCommand.RaiseCanExecuteChanged();
            SkipForwardCommand.RaiseCanExecuteChanged();
        }
    }

    private Task SeekReplayByAsync(TimeSpan delta, bool useExactStep = false)
    {
        if (!CanSeekReplay)
        {
            AddSystemMessage(ReplaySeekToolTip);
            return Task.CompletedTask;
        }

        var targetOffset = GetCurrentReplayStepOffset() + delta;
        // A short configured skip may land inside the usual return-to-live tolerance.
        // Keep that exact offset until a forward step actually reaches the live edge.
        var holdExactPosition = useExactStep && replaySession is { } replay &&
            targetOffset < GetCurrentReplayDuration(replay);
        return SeekReplayAsync(targetOffset, holdExactPosition: holdExactPosition);
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
        replayClock.BeginSeek();
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
        replayClock.CancelSeek();
        IsReplaySeekInProgress = false;
    }

    private enum SeekCompletionIntent { PreservePlaybackState, Resume }

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
        bool playbackTransitionAlreadyHeld,
        SeekCompletionIntent completionIntent = SeekCompletionIntent.PreservePlaybackState)
    {
        CancelLivePlaybackRecovery();
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

        if (!holdExactPosition && Status != PlaybackStatus.Paused && !Target.IsExplicitVod && duration - targetOffset <= ReplayLiveEdgeThreshold)
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
            var startPaused = completionIntent == SeekCompletionIntent.PreservePlaybackState && Status == PlaybackStatus.Paused;
            var seekOperationVersion = BeginReplaySeekOperation();
            var targetReplayWindowHasMessages = false;
            IsBusy = true;
            try
            {
                await replayPlaybackTransitionGate.WaitAsync(cancellationToken);
                try
                {
                    // Promotion can win the gate after this seek captured the live DVR.
                    // Its offsets still describe the same broadcast, so use the published
                    // VOD instead of silently discarding the user's seek as stale.
                    if (IsCurrentLiveDvrReplay(replay) &&
                        replaySession is { IsAvailable: true } promotedReplay &&
                        !IsCurrentLiveDvrReplay(promotedReplay) &&
                        IsSameReplayStream(replay, promotedReplay))
                    {
                        replay = promotedReplay;
                    }

                    if (!IsCurrentReplaySession(replay) ||
                        playbackEngine is null)
                    {
                        return;
                    }

                    duration = GetCurrentReplayDuration(replay);
                    targetOffset = ClampReplayOffset(offset, duration);
                    // holdExactPosition (resume-from-pause) keeps the exact paused timestamp instead of
                    // snapping to live when the spot is within the live-edge window.
                    if (!holdExactPosition && !startPaused && !Target.IsExplicitVod && duration - targetOffset <= ReplayLiveEdgeThreshold)
                    {
                        // The live-edge case was handled before taking playbackTransitionGate. If the
                        // replay clock moved to the edge while waiting, leave the current media alone;
                        // the next explicit Return to Live action will perform the full transport reset.
                        return;
                    }

                    CancelActiveStart();
                    // Re-anchor VOD chat first: it keeps everything already downloaded, so chat the
                    // seek target is already covered by is republished immediately instead of after
                    // a round trip.
                    ClearChatForVodChatSeek();
                    StartVodChat(replay, targetOffset);
                    targetReplayWindowHasMessages = PumpVodChat(targetOffset) > 0;

                    var seekedInPlace = false;
                    if (!forceReload && !IsVodFinished && CanSeekCurrentReplayInPlace(replay))
                    {
                        var statusBeforeSeek = Status;
                        try
                        {
                            if (!startPaused) Status = PlaybackStatus.Starting;
                            await playbackEngine.SeekAsync(targetOffset, cancellationToken);
                            CompleteReplaySeek(targetOffset, duration, seekOperationVersion, startPaused);
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
                        finally
                        {
                            // A failed or cancelled seek leaves the existing input in place.
                            // Restore its status so an EOF that raced the seek can still finish
                            // the VOD, and ordinary seek failures cannot strand it at Starting.
                            if (Status == PlaybackStatus.Starting)
                            {
                                Status = statusBeforeSeek;
                            }
                        }
                    }

                    if (!seekedInPlace)
                    {
                        var replayPlaybackKey = CreateReplayPlaybackUrlKey(replay, settings);
                        var urlWaitStopwatch = Stopwatch.StartNew();
                        var resolved = await ResolveReplayPlaybackUrlForSeekAsync(replayPlaybackKey, cancellationToken);
                        urlWaitStopwatch.Stop();
                        LogReplayFirstSeekStage("URL wait", urlWaitStopwatch.Elapsed);
                        var replayTransitionWork = PrepareReplayTransitionWork(settings);
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
                            if (!startPaused) Status = PlaybackStatus.Starting;
                            // The live controller has released the shared frame pipe. Clear its
                            // retained image before the potentially slow replay open/seek, so an
                            // empty historical window cannot appear to contain frozen live chat.
                            ClearNativeReplayOverlayForReplayTransition(
                                replay,
                                targetReplayWindowHasMessages);
                            var playStopwatch = Stopwatch.StartNew();
                            await playbackEngine.PlayFromAsync(resolved.StreamUri, targetOffset, Volume, CurrentAudioState, startPaused, cancellationToken);
                            playStopwatch.Stop();
                            LogReplayFirstSeekStage("PlayFromAsync (open at requested position)", playStopwatch.Elapsed);
                            ClearNativeReplayOverlayForReplayTransition(
                                replay,
                                targetReplayWindowHasMessages);
                            isDirectExplicitVodReplayPlayback = Target.IsExplicitVod;
                            if (Target.IsExplicitVod)
                            {
                                explicitVodPlaybackUri = resolved.StreamUri;
                            }

                            currentReplayPlaybackKey = replayPlaybackKey;
                            ApplyAudio();
                            CompleteReplaySeek(targetOffset, duration, seekOperationVersion, startPaused);
                        }
                        catch (Exception ex)
                        {
                            // Opening a replay starts it at its default position. If restoring the
                            // requested timestamp fails, do not leave that new media playing from
                            // the beginning while the seekbar still displays the requested offset.
                            try
                            {
                                await playbackEngine.StopAsync(CancellationToken.None);
                            }
                            catch (Exception stopException)
                            {
                                logger.Write(AppLogLevel.Warning, "Replay", "Failed to stop an unsuccessful replay restore.", stopException);
                            }

                            Status = PlaybackStatus.Error;
                            ErrorMessage = $"Replay position could not be restored: {ex.Message}";
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
                    CaptureVodResumePosition();
                    await SaveVodResumePositionAsync(force: true);
                    FlushNativeReplayOverlayRefreshAfterSeek();
                }
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

    private void CompleteReplaySeek(TimeSpan position, TimeSpan duration, long generation, bool paused)
    {
        ExpectVodResumeSeek(position);
        SetReplayClockAnchor(position, duration, generation, awaitingSeekConfirmation: true);
        IsReplayMode = true;
        IsBehindLive = !Target.IsExplicitVod;
        replayClock.CommitSeek(position, duration, paused);
        Status = paused ? PlaybackStatus.Paused : PlaybackStatus.Playing;
        ApplyReplayClock(position, duration, isSeekable: true);
        StartReplayClockPolling();
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

        if (!IsReplayMode && !IsBehindLive && Status != PlaybackStatus.Paused)
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

        var draftRevision = outgoingChatRevision;
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

        var cancellationToken = lifetimeCancellation.Token;
        try
        {
            // The client is published before ConnectAsync completes. Always wait for startup,
            // including when that client is already visible to the UI.
            var pendingConnection = GetChatConnectionTask();
            if (pendingConnection is not null)
            {
                await pendingConnection.WaitAsync(cancellationToken);
            }

            if (disposed || !CanSendChatMessages) return;

            var client = chatClient;
            if (client is null)
            {
                AddSystemMessage("Chat is not connected yet.");
                return;
            }

            await SendChatWithLocalEchoAsync(client, message, draftRevision, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (disposed || !CanSendChatMessages) return;

            if (await TryReconnectChatForSendAsync(message, draftRevision, ex, cancellationToken))
            {
                return;
            }

            AddSystemMessage($"Chat send failed: {ex.Message}");
            logger.Write(AppLogLevel.Warning, "Chat", $"Failed to send chat message for {Target.DisplayName}", ex);
        }
    }

    private async Task SendChatWithLocalEchoAsync(
        IChatClient client, string message, long draftRevision, CancellationToken cancellationToken)
    {
        var rememberDockedLocalEcho = IsDockedChatModeActive;
        var localEcho = CreateLocalEchoMessage(message);
        var added = false;
        if (rememberDockedLocalEcho) RememberDockedLocalEcho(localEcho);
        try
        {
            await client.SendMessageAsync(message, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || !CanSendChatMessages || !ReferenceEquals(chatClient, client)) return;

            // Even an identical draft may have been cleared and retyped during the send.
            if (outgoingChatRevision == draftRevision) OutgoingChatText = "";
            AddChatMessage(localEcho, isRememberedDockedLocalEcho: rememberDockedLocalEcho);
            added = true;
        }
        finally
        {
            if (!added && rememberDockedLocalEcho) ForgetDockedLocalEcho(localEcho);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposalTask is null)
            {
                CaptureVodResumePosition(closing: true);
                disposed = true;
                logBuffer.Dispose();
                disposalTask = DisposeCoreAsync();
            }

            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        lifetimeCancellation.Cancel();
        // Persist the frozen close snapshot before waiting for player/chat cleanup.
        await SaveVodResumePositionAsync(force: true);
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

            await vodChat.DisposeAsync();
            await nativeOverlay.DisposeAsync();

            parkingVideoSurface?.Dispose();
            parkingVideoSurface = null;
        }
        finally
        {
            if (lifecycleAcquired)
            {
                lifecycleGate.Release();
            }

            await nativeOverlay.DisposeAsync();
            lifetimeCancellation.Dispose();
        }
    }

    private void OnChatRenderCatalogChanged(object? sender, EventArgs e) => nativeOverlay.OnChatRenderCatalogChanged(sender, e);

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

    private async Task<bool> TryReconnectChatForSendAsync(
        string message, long draftRevision, Exception sendException, CancellationToken cancellationToken)
    {
        if (currentSettings is null ||
            !HasConfiguredChatToken(currentSettings.Chat) ||
            !IsRecoverableChatSendFailure(sendException))
        {
            return false;
        }

        try
        {
            AddSystemMessage("Reconnecting chat with updated credentials...");
            await RestartChatAsync(currentSettings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || !CanSendChatMessages) return true;

            var client = chatClient;
            if (client is null)
            {
                return false;
            }

            await SendChatWithLocalEchoAsync(client, message, draftRevision, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception retryException)
        {
            if (disposed || !CanSendChatMessages) return true;

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
        if (Target.IsExplicitVod) return;
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

        if (Target.IsOfflineVod)
        {
            ApplyExplicitVodReplaySession(new ReplaySessionInfo(Target.Platform, Target.Channel,
                GetOfflinePlaybackUri().AbsoluteUri, Target.MediaId, Target.MediaStartedAtUtc,
                Target.MediaDuration, true, ""));
            return;
        }

        if (Target.IsExplicitTwitchVod)
        {
            InitializeExplicitTwitchVodReplaySession(subOnlyVodResolution);
            return;
        }

        if (Target.IsExplicitKickVod)
        {
            InitializeExplicitKickVodReplaySession();
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
        StartVodChat(replay, vodStartupPosition);
    }

    private void InitializeExplicitKickVodReplaySession()
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
        StartVodChat(replay, vodStartupPosition);
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
        if (vodStartupPosition > TimeSpan.Zero && vodStartupDuration > duration)
        {
            duration = vodStartupDuration;
        }

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
        ClearReplayClockAnchor();
        IsReplayMode = true;
        IsBehindLive = false;
        ReplaySeekToolTip = $"{replay.Platform} VOD replay available: {replay.ReplayId}";
        if (vodStartupPosition > TimeSpan.Zero)
        {
            SetReplayClockAnchor(vodStartupPosition, replay.Duration,
                Volatile.Read(ref replaySeekOperationVersion), awaitingSeekConfirmation: true);
        }
        ApplyReplayClock(vodStartupPosition, replay.Duration, isSeekable: true);
        ClearChatForVodChatSeek();
    }

    private async Task RefreshReplayAvailabilityCoreAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        long refreshVersion,
        bool prefetchPlaybackUrl)
    {
        if (!IsReplayAvailabilityRefreshCurrent(refreshVersion))
        {
            return;
        }

        if (!settings.Replay.Enabled)
        {
            if (IsReplayAvailabilityRefreshCurrent(refreshVersion))
            {
                dispatch(() =>
                {
                    if (IsReplayAvailabilityRefreshCurrent(refreshVersion))
                        ResetReplayState("Replay seekbar is disabled in Settings.");
                });
            }

            return;
        }

        if (replayResolver is null)
        {
            if (IsReplayAvailabilityRefreshCurrent(refreshVersion))
            {
                dispatch(() =>
                {
                    if (IsReplayAvailabilityRefreshCurrent(refreshVersion))
                        ResetReplayState("Replay resolver is not configured.");
                });
            }

            return;
        }

        try
        {
            var replay = await replayResolver.ResolveCurrentReplayAsync(Target, Quality, settings, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !IsReplayAvailabilityRefreshCurrent(refreshVersion))
            {
                return;
            }

            replaySession = replay;
            if (!replay.IsAvailable)
            {
                CancelLiveDvrPromotionPolling();
                var unavailableReason = replay.UnavailableReason;
                dispatch(() =>
                {
                    if (IsReplayAvailabilityRefreshCurrent(refreshVersion)) SetReplayUnavailable(unavailableReason);
                });
                return;
            }

            StartVodChat(replay, GetCurrentReplayStepOffset());

            if (IsCurrentLiveDvrReplay(replay))
            {
                StartLiveDvrPromotionPolling(settings);
            }
            else
            {
                await StopLiveDvrPromotionPollingAsync();
            }

            if (!IsReplayAvailabilityRefreshCurrent(refreshVersion)) return;

            if (prefetchPlaybackUrl)
            {
                QueueReplayPlaybackUrlResolution(replay, settings);
            }

            var duration = GetCurrentReplayDuration(replay);
            dispatch(() =>
            {
                if (!IsReplayAvailabilityRefreshCurrent(refreshVersion)) return;
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
                var failureReason = $"Replay unavailable: {ex.Message}";
                dispatch(() =>
                {
                    if (IsReplayAvailabilityRefreshCurrent(refreshVersion)) ResetReplayState(failureReason);
                });
            }
        }
    }

    private bool IsReplayAvailabilityRefreshCurrent(long refreshVersion)
    {
        return !disposed && !IsBackgroundResourceServicesSuspended &&
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
        if (disposed || IsBackgroundResourceServicesSuspended)
        {
            return;
        }

        // Preserve a valid resolved URL when an inactive tab resumes. Metadata
        // replacement invalidates the lookup, not the already prepared source.
        CancelReplayAvailabilityPolling();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        var refreshVersion = Interlocked.Increment(ref replayAvailabilityRefreshVersion);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (replayAvailabilityRefreshGate)
        {
            replayAvailabilityRefreshCancellation = cancellation;
            replayAvailabilityRefreshTask = completion.Task;
        }

        // Publish and track ownership before calling a provider, which may finish
        // synchronously or reenter the tab. Completed metadata needs no worker thread;
        // a slow provider must not retain the playback transition or a startup slot.
        playbackCleanupController.Observe(completion.Task);
        _ = RefreshAsync();

        async Task RefreshAsync()
        {
            try
            {
                await RefreshReplayAvailabilityCoreAsync(
                    settings, cancellation.Token, refreshVersion, prefetchPlaybackUrl);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
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
                completion.TrySetResult();
            }
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

    private IReadOnlyList<ReplayTransitionWork> PrepareReplayTransitionWork(AppSettings settings)
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

        if (ShouldKeepChatClientForVodChatCapture(settings))
        {
            work.Add(new(
                "ensure VOD chat capture client",
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

        if (DetachStreamSession() is { } detachedStreamSession)
        {
            work.Add(new(
                "stop live Streamlink HTTP transport",
                () => DisposeDetachedStreamSessionAsync(detachedStreamSession),
                RunOnPlaybackFailure: true));
        }

        return work;
    }

    private Task StopNativeOverlayChatAfterReplayTransitionAsync() => nativeOverlay.StopNativeOverlayChatAfterReplayTransitionAsync();

    private Task StopDetachedNativeOverlayChatAfterReplayTransitionAsync(DetachedNativeOverlayChat detached) => nativeOverlay.StopDetachedNativeOverlayChatAfterReplayTransitionAsync(detached);

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
            if (TryCreateDirectReplayPlaybackUri(key.Target.Url, out var directUri))
                QueueReplayInputPreparation(key, directUri);
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
            QueueCachedReplayInputPreparation();
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
        if (Target.IsOfflineVod)
            return new StreamlinkResolvedUrl(GetOfflinePlaybackUri(), "Using downloaded VOD files.");
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
        CancelReplayInputPreparation();
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
            var resolved = await resolution.Task.ConfigureAwait(false);
            MarkReplayPlaybackUrlReadiness(
                resolution,
                ReplayPlaybackUrlReadiness.Successful,
                logMessage: null,
                exception: null);
            QueueReplayInputPreparation(resolution.Key, resolved.StreamUri);
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

    private void QueueCachedReplayInputPreparation()
    {
        if (replaySession is not { IsAvailable: true } replay || currentSettings is not { } settings) return;
        var key = CreateReplayPlaybackUrlKey(replay, settings);
        if (!ShouldTrySubOnlyVodFallback(key) && TryCreateDirectReplayPlaybackUri(key.Target.Url, out var directUri))
        {
            QueueReplayInputPreparation(key, directUri);
            return;
        }
        Uri? uri = null;
        lock (replayPlaybackUrlResolutionGate)
        {
            if (replayPlaybackUrlResolution is { } resolution && resolution.Key.Equals(key) &&
                resolution.Task.IsCompletedSuccessfully)
                uri = resolution.Task.Result.StreamUri;
        }
        if (uri is not null) QueueReplayInputPreparation(key, uri);
    }

    private void CancelReplayInputPreparation()
    {
        CancellationTokenSource? cancellation;
        lock (replayPlaybackUrlResolutionGate)
        {
            cancellation = replayInputPreparationCancellation;
            replayInputPreparationCancellation = null;
            replayInputPreparationUri = null;
            replayInputPreparationEngine = null;
        }
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void QueueReplayInputPreparation(ReplayPlaybackUrlKey key, Uri uri)
    {
        dispatch(() =>
        {
            if (disposed || IsBackgroundResourceServicesSuspended || Target.IsExplicitVod || IsReplayMode ||
                !IsReplayPlaybackUrlReadinessCurrent(key) || playbackEngine is not { } engine) return;
            CancellationTokenSource? previous;
            CancellationToken token;
            lock (replayPlaybackUrlResolutionGate)
            {
                if (replayInputPreparationUri == uri && ReferenceEquals(replayInputPreparationEngine, engine) &&
                    replayInputPreparationCancellation is { IsCancellationRequested: false }) return;
                previous = replayInputPreparationCancellation;
                replayInputPreparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
                replayInputPreparationUri = uri;
                replayInputPreparationEngine = engine;
                token = replayInputPreparationCancellation.Token;
            }
            previous?.Cancel();
            previous?.Dispose();
            _ = Task.Run(async () =>
            {
                try { await engine.PrepareReplayAsync(uri, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { logger.Write(AppLogLevel.Info, "Replay", "Could not prepare the replay input in the background.", ex); }
            });
        });
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
            : IsCurrentLiveDvrReplay(replay)
                ? "Replay video is available. Twitch has not published chat for this broadcast; " +
                    "only chat captured while this tab was open can be replayed."
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

    /// <summary>
    /// Builds an async command whose unhandled failures are reported and logged.
    /// <see cref="AsyncRelayCommand"/> drops them silently when no error handler is supplied.
    /// </summary>
    private AsyncRelayCommand CreateCommand(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(execute, canExecute, ReportCommandFailure);

    private void ReportCommandFailure(Exception exception)
    {
        if (disposed || exception is OperationCanceledException)
        {
            return;
        }

        logger.Write(
            AppLogLevel.Error,
            "Command",
            $"A tab command failed for {Target.DisplayName}.",
            exception);
    }

    private void ResetReplayState(string reason)
    {
        StopNativeReplayOverlayEventHost();
        CancelReplayPlaybackUrlResolution();
        currentReplayPlaybackKey = null;
        replaySession = null;
        explicitVodPlaybackUri = null;
        CancelLiveDvrPromotionPolling();
        StopVodChat();
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

    private void ResetPlaybackRate()
    {
        int appliedIndex;
        int selectedIndex;
        lock (playbackRateSelectionGate)
        {
            Interlocked.Increment(ref playbackRateChangeVersion);
            appliedIndex = Interlocked.Exchange(ref playbackRateIndex, DefaultPlaybackRateIndex);
            selectedIndex = Interlocked.Exchange(ref selectedPlaybackRateIndex, DefaultPlaybackRateIndex);
        }

        if (appliedIndex == DefaultPlaybackRateIndex && selectedIndex == DefaultPlaybackRateIndex)
        {
            return;
        }

        OnPropertyChanged(nameof(PlaybackRateIndex));
    }

    private void SetReplayUnavailable(string reason)
    {
        StopNativeReplayOverlayEventHost();
        CancelReplayPlaybackUrlResolution();
        currentReplayPlaybackKey = null;
        CancelLiveDvrPromotionPolling();
        StopVodChat();
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

        StartPolling(ref replayClockPollingCancellation, ref replayClockPollingTask,
            PollReplayClockAsync, "Replay", $"Replay clock cleanup failed for {Target.DisplayName}.");
    }

    private void StartPolling(
        ref CancellationTokenSource? cancellation,
        ref Task? pollingTask,
        Func<CancellationToken, Task> poll,
        string logCategory,
        string failureMessage)
    {
        // Replaced requests may still be unwinding or may ignore cancellation. Keep their
        // source alive until completion and observe cleanup even after replacing the task.
        if (cancellation is not null)
            playbackCleanupController.Observe(StopPollingAsync(cancellation, pollingTask, logCategory, failureMessage));

        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        pollingTask = Task.Run(() => poll(token));
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
                await UpdateReplayClockAsync(cancellationToken).ConfigureAwait(false);
                CaptureVodResumePosition();
                CheckVodPlaybackCompletion();
                await SaveVodResumePositionAsync();
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

    private async Task UpdateReplayClockAsync(CancellationToken cancellationToken)
    {
        if (IsVodFinished || replaySession is not { IsAvailable: true } replay)
        {
            return;
        }

        // Resolving the engine clock and applying its chat window are separate operations. A seek
        // can complete between them, so carry the generation that produced this sample all the way
        // through instead of relying only on the later IsReplaySeekInProgress snapshot.
        var sampledSeekOperationVersion = Volatile.Read(ref replaySeekOperationVersion);
        var sampledPlaybackStateVersion = replayClock.PlaybackStateVersion;
        var seekWasInProgress = IsReplaySeekInProgress;
        var clock = ResolveReplayClock(
            replay,
            sampledSeekOperationVersion,
            seekWasInProgress,
            sampledPlaybackStateVersion);
        var sampleIsCurrent = !seekWasInProgress &&
            IsReplayClockSampleCurrent(sampledSeekOperationVersion, sampledPlaybackStateVersion);

        if (sampleIsCurrent)
        {
            QueueReplayClockUiApply(clock, sampledSeekOperationVersion, sampledPlaybackStateVersion);
        }

        if (IsReplayMode && sampleIsCurrent)
        {
            PumpVodChat(clock.Position, sampledSeekOperationVersion, sampledPlaybackStateVersion);
        }
        else if (sampleIsCurrent)
        {
            // Still at the live edge: keep the pump's idea of "now" fresh so a later seek back
            // already has chat loaded around it.
            vodChat.UpdatePosition(clock.Position);
        }

        if (sampleIsCurrent && ShouldResetPlaybackRateAtLiveEdge(clock))
        {
            var transitionAcquired = false;
            try
            {
                // Serialize the automatic rate change with seeks and live transitions. The
                // sample may have been current before waiting for a transition already in flight.
                await playbackTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                transitionAcquired = true;
                if (IsReplayClockSampleCurrent(sampledSeekOperationVersion, sampledPlaybackStateVersion) &&
                    ShouldResetPlaybackRateAtLiveEdge(clock))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ApplyPlaybackRateSelectionAsync(
                        DefaultPlaybackRateIndex,
                        sampledSeekOperationVersion,
                        sampledPlaybackStateVersion).ConfigureAwait(false);
                }
            }
            finally
            {
                if (transitionAcquired)
                {
                    playbackTransitionGate.Release();
                }
            }
        }
    }

    private bool ShouldResetPlaybackRateAtLiveEdge(ReplayClockSnapshot clock)
    {
        if (Target.IsExplicitVod ||
            !IsReplayMode ||
            !IsBehindLive ||
            Status != PlaybackStatus.Playing ||
            Volatile.Read(ref playbackRateIndex) <= DefaultPlaybackRateIndex)
        {
            return false;
        }

        // Prefer VLC's current input duration because an EVENT playlist can trail the
        // wall-clock replay duration while its next segments are being published. The
        // normalized clock duration remains the fallback when VLC has no duration yet.
        var liveEdge = clock.Duration;
        if (clock.SourceDuration is { } sourceDuration &&
            sourceDuration > TimeSpan.Zero &&
            sourceDuration < liveEdge)
        {
            liveEdge = sourceDuration;
        }

        return liveEdge - clock.Position <= ReplayLiveEdgeThreshold;
    }

    private bool IsReplayClockSampleCurrent(long sampledSeekOperationVersion, long? sampledPlaybackStateVersion = null)
    {
        return !IsReplaySeekInProgress &&
            IsLatestReplaySeekOperation(sampledSeekOperationVersion) &&
            (sampledPlaybackStateVersion is null ||
                sampledPlaybackStateVersion == replayClock.PlaybackStateVersion);
    }

    /// <summary>
    /// Points VOD chat at a replay session and a playback position. Chat already downloaded for the
    /// same session is kept, so this is cheap to call again after a seek.
    /// </summary>
    private void StartVodChat(ReplaySessionInfo replay, TimeSpan position)
    {
        if (Target.IsOfflineVod)
        {
            QueueReplayChatStatus("Offline playback: chat is not included in the VOD download.");
            return;
        }
        if (currentSettings is not { } settings || !replay.IsAvailable)
        {
            return;
        }

        vodChat.Start(replay, settings, position, () => GetCurrentReplayDuration(replay),
            isGrowing: !Target.IsExplicitVod);
    }

    private void StopVodChat()
    {
        vodChat.Stop();
        QueueReplayChatStatus("");
    }

    /// <summary>
    /// Publishes the VOD chat playback has just reached through the same append path the live feed
    /// uses, and returns how many messages were published.
    /// </summary>
    private int PumpVodChat(
        TimeSpan position,
        long? expectedSeekOperationVersion = null,
        long? expectedPlaybackStateVersion = null)
    {
        if (expectedSeekOperationVersion is { } expectedVersion &&
            !IsReplayClockSampleCurrent(expectedVersion, expectedPlaybackStateVersion))
        {
            return 0;
        }

        var due = vodChat.TakeMessagesDueAt(position, MaxChatMessages);
        foreach (var message in due)
        {
            AddChatMessage(message, isRememberedDockedLocalEcho: false);
        }

        // Video DVR availability does not imply chat history exists. Future captured messages
        // must not hide that limitation at the current playback position.
        QueueReplayChatStatus(replaySession is { } replay && IsCurrentLiveDvrReplay(replay) &&
            !vodChat.HasMessagesAtOrBefore(position)
                ? "Twitch hasn't published chat history for this broadcast. " +
                    "Captured messages will appear when playback reaches them."
                : "", expectedSeekOperationVersion ?? Volatile.Read(ref replaySeekOperationVersion));

        if (vodChat.TryTakeNotice(out var notice) &&
            Target.IsExplicitVod &&
            !vodChat.HasMessages)
        {
            AddSystemMessage(notice);
        }

        return due.Count;
    }

    private void QueueReplayChatStatus(string text, long? expectedSeekOperationVersion = null)
    {
        long epoch;
        lock (chatMessageUiGate)
        {
            if (expectedSeekOperationVersion is { } version && !IsLatestReplaySeekOperation(version)) return;
            if (requestedReplayChatStatus == text) return;
            requestedReplayChatStatus = text;
            epoch = chatEpoch;
        }

        dispatch(() =>
        {
            lock (chatMessageUiGate)
            {
                if (disposed || epoch != chatEpoch || requestedReplayChatStatus != text) return;
            }
            ApplyReplayChatStatus(text);
        });
    }

    private void ApplyReplayChatStatus(string text)
    {
        if (!SetProperty(ref replayChatStatusText, text, nameof(ReplayChatStatusText))) return;
        replayChatStatusMessage = text.Length == 0 ? null : new ChatMessage(
            Target.Platform, Target.Channel, "system", text, DateTimeOffset.UtcNow, "#A6E3A1");
        OnPropertyChanged(nameof(HasReplayChatStatus));
        InvalidateNativeReplayOverlayFrame();
    }

    /// <summary>
    /// Empties the visible chat so a seek does not leave the previous position's messages on
    /// screen, and blanks the native overlay in case nothing replaces them.
    /// </summary>
    private void ClearChatForVodChatSeek()
    {
        lock (chatMessageUiGate)
        {
            pendingChatMessages.Clear();
            chatEpoch++;
            requestedReplayChatStatus = "";
        }

        nativeOverlay.InvalidatePendingFrame();
        CancelNativeReplayOverlayAnimationState();
        dispatch(() =>
        {
            ChatMessages.Clear();
            DockedChatMessages.Clear();
            DockedChatFeedItems.Clear();
            ApplyReplayChatStatus("");
            activeTwitchPredictionFeedItem = null;
            StopTwitchPredictionClock();
            // Seeking backwards legitimately re-shows messages, so the live dedupe ring has to
            // forget them or they would be silently dropped.
            recentChatMessageIds.Clear();
            recentChatMessageIdSet.Clear();
            MarkNativeReplayOverlayRefreshPendingAfterSeek();
        });
        ClearNativeReplayOverlayForEmptyReplayWindowInBackground();
    }

    /// <summary>
    /// Whether the in-app chat client must stay connected even when the VLC overlay renders chat by
    /// itself. Its messages are what feeds VOD chat once a live stream is watched behind the live
    /// edge, including Twitch DVR windows that do not yet have a published VOD id.
    /// </summary>
    private bool ShouldKeepChatClientForVodChatCapture(AppSettings settings)
    {
        return !Target.IsExplicitVod &&
            Target.Platform is PlatformKind.Twitch or PlatformKind.Kick &&
            replayResolver is not null &&
            settings.Replay.Enabled &&
            settings.Chat.ConnectAutomatically &&
            IsChatVisible;
    }

    private void QueueReplayClockUiApply(
        ReplayClockSnapshot clock,
        long sampledSeekOperationVersion,
        long sampledPlaybackStateVersion)
    {
        lock (replayClockUiGate)
        {
            pendingReplayClockUiSample = new ReplayClockUiUpdate(
                clock,
                sampledSeekOperationVersion,
                sampledPlaybackStateVersion);
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

            if (IsReplayClockSampleCurrent(update.Value.ReplaySeekOperationVersion, update.Value.PlaybackStateVersion))
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

    private ReplayClockSnapshot ResolveReplayClock(ReplaySessionInfo replay, long sampledSeekOperationVersion, bool sampleBeganDuringSeek, long? sampledPlaybackStateVersion = null) => replayClock.ResolveReplayClock(replay, sampledSeekOperationVersion, sampleBeganDuringSeek, sampledPlaybackStateVersion);

    private void SetReplayClockAnchor(TimeSpan offset, TimeSpan duration, long seekGeneration, bool awaitingSeekConfirmation, DateTimeOffset? observedAtUtc = null) => replayClock.SetReplayClockAnchor(offset, duration, seekGeneration, awaitingSeekConfirmation, observedAtUtc);

    private void ClearReplayClockAnchor() => replayClock.ClearReplayClockAnchor();

    private readonly record struct ReplayClockUiUpdate(
        ReplayClockSnapshot Clock,
        long ReplaySeekOperationVersion,
        long PlaybackStateVersion);

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

        CancelCancellationSource(cancellation);
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
        // A seek re-anchors chat and resolves playback using its captured replay. Keep
        // promotion in the same transition gate so it cannot replace that source midway.
        await replayPlaybackTransitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed || replaySession is not { IsAvailable: true } currentReplay ||
                !IsCurrentLiveDvrReplay(currentReplay) || !IsSameReplayStream(currentReplay, promotedReplay))
            {
                return;
            }

            replaySession = promotedReplay;
            var duration = GetCurrentReplayDuration(promotedReplay);
            var offset = GetCurrentReplayStepOffset();
            QueueReplayPlaybackUrlResolution(promotedReplay, settings);
            // Same broadcast, new id: content offsets are unchanged, so everything captured so far
            // stays valid and only the source of future fetches moves to the published VOD.
            vodChat.Promote(promotedReplay);
            QueueReplayChatStatus("");

            dispatch(() =>
            {
                ApplyReplayClock(IsReplayMode ? offset : duration, duration, isSeekable: true);
                ApplyReplaySeekToolTipForCurrentReadiness();
            });

            logger.Write(AppLogLevel.Info, "Replay", $"Twitch current-live DVR replay for {Target.DisplayName} was promoted to VOD {promotedReplay.ReplayId}.");
        }
        finally
        {
            replayPlaybackTransitionGate.Release();
        }
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

    private void LogReplayFirstSeekStage(string stage, TimeSpan elapsed)
    {
        logger.Write(
            AppLogLevel.Info,
            "Replay",
            $"Replay first-seek {stage} took {elapsed.TotalMilliseconds:0} ms for {Target.DisplayName}.");
    }

    private TimeSpan GetCurrentReplayDuration(ReplaySessionInfo replay) => replayClock.GetCurrentReplayDuration(replay);

    private static TimeSpan ClampReplayOffset(TimeSpan value, TimeSpan duration) => ReplayClockState.ClampReplayOffset(value, duration);

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

        StartPolling(ref viewerCountPollingCancellation, ref viewerCountPollingTask,
            token => PollViewerCountAsync(settings, token), "Viewers",
            $"Viewer count polling cleanup failed for {Target.DisplayName}.");
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

        ResetVideoAspectRatioPollingBackoff();
        StartPolling(ref videoAspectRatioPollingCancellation, ref videoAspectRatioPollingTask,
            PollVideoAspectRatioAsync, "Playback", $"Video aspect ratio polling cleanup failed for {Target.DisplayName}.");
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

    private void RefreshNativeReplayOverlayForVideoSize(int width, int height) => nativeOverlay.RefreshNativeReplayOverlayForVideoSize(width, height);

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
                cancellationToken.ThrowIfCancellationRequested();
                DispatchViewerCountUpdate(() => ApplyViewerCountResult(result), cancellationToken);
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
                DispatchViewerCountUpdate(() => ApplyViewerCount("N/A", $"Viewer count unavailable: {ex.Message}"), cancellationToken);
                delay = ViewerCountRetryDelay;
            }
        }
    }

    private void DispatchViewerCountUpdate(Action update, CancellationToken cancellationToken)
    {
        dispatch(() =>
        {
            if (!disposed && !cancellationToken.IsCancellationRequested) update();
        });
    }

    private void ApplyViewerCountResult(ViewerCountResult result)
    {
        var updatedAt = DateTimeOffset.Now;

        // Only an Available poll carries authoritative category data. On any other state the
        // platform told us nothing usable, so keep the last known category rather than blanking it.
        if (result.State == ViewerCountState.Available)
        {
            hasPolledCategory = true;
            SetCategoryName(result.CategoryName);
            SetStreamTitle(result.StreamTitle);
        }

        switch (result.State)
        {
            case ViewerCountState.Available when result.ViewerCount is { } viewerCount:
                ApplyViewerCount(
                    FormatViewerCount(viewerCount),
                    $"{viewerCount.ToString("N0", CultureInfo.CurrentCulture)} viewers. Updated {updatedAt:t}.");
                break;
            case ViewerCountState.Offline:
                ApplyViewerCount("--", $"{result.Message} Updated {updatedAt:t}.");
                break;
            case ViewerCountState.NotConfigured:
                ApplyViewerCount("Auth", result.Message);
                break;
            default:
                ApplyViewerCount("N/A", result.Message);
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

    private void SetViewerCount(string text, string toolTip) => dispatch(() => ApplyViewerCount(text, toolTip));

    private void ApplyViewerCount(string text, string toolTip)
    {
        ViewerCountText = text;
        ViewerCountToolTip = toolTip;
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
        CategoryName = normalized;
    }

    private void SetStreamTitle(string value)
    {
        var normalized = value?.Trim() ?? "";
        if (string.Equals(StreamTitle, normalized, StringComparison.Ordinal))
        {
            return;
        }

        StreamTitle = normalized;
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
        StopLivePlaybackMonitoring();
        livePlaybackConnectionSuspended = false;
        replayClock.ClearResumeHold();
        await StopVideoAspectRatioPollingAsync();
        await StopReplayClockPollingAsync();
        await StopNativeReplayOverlayEventHostAsync();
        await StopNativeOverlayChatAsync(clearOverlay: false);

        await StopVodResumeTrackingAsync();
        await StopStreamSessionAsync();

        var engine = playbackEngine;
        playbackEngine = null;
        OnPropertyChanged(nameof(CanChangePlaybackRate));
        isDirectExplicitVodReplayPlayback = false;
        explicitVodPlaybackUri = null;
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

    private async Task DisposeUnclaimedStreamSessionAsync(
        Task<IStreamTransportSession> streamSessionTask,
        CancellationTokenSource? cancellation)
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
        finally
        {
            // A late producer may still use its token after Stop has returned.
            cancellation?.Dispose();
        }
    }

    private void StreamSessionOnLogLineReceived(object? sender, string line)
    {
        logBuffer.Enqueue(line);
    }

    private void ChatClientOnMessageReceived(object? sender, ChatMessage message)
    {
        if (Target.IsExplicitVod || !ReferenceEquals(sender, chatClient))
        {
            return;
        }

        var sampledSeekOperationVersion = Volatile.Read(ref replaySeekOperationVersion);
        var seekWasInProgress = IsReplaySeekInProgress;

        // Every live message is filed on the VOD timeline at its broadcast offset, so seeking
        // back later has chat to replay. This is lock-protected and cheap enough for the read loop.
        var alreadyDue = vodChat.CaptureLiveMessage(message);

        // The first seek clears chat before opening replay media, but commits IsReplayMode /
        // IsBehindLive only after that open succeeds. IRC must keep capturing throughout the
        // transition without leaking a burst of live messages into the historical chat window.
        if (seekWasInProgress || !IsReplayClockSampleCurrent(sampledSeekOperationVersion))
        {
            return;
        }

        // Behind the live edge, playback position decides when a message becomes visible, so it is
        // only queued for display while the tab is actually watching live. AddChatMessage performs
        // the single coalesced dispatcher hop that owns the WPF-bound collections, rather than
        // scheduling one dispatcher callback per message before that batching can take effect.
        if (!IsBehindLive && !IsReplayMode)
        {
            AddChatMessage(message, isRememberedDockedLocalEcho: false,
                expectedLiveSeekOperationVersion: sampledSeekOperationVersion);
            return;
        }

        // A captured message that playback has already passed belongs on screen now; waiting for the
        // next clock tick would make chat visibly lag the video.
        if (alreadyDue)
        {
            PumpVodChat(vodChat.Position, sampledSeekOperationVersion);
        }
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

    private void AddChatMessage(
        ChatMessage message,
        bool isRememberedDockedLocalEcho,
        long? expectedLiveSeekOperationVersion = null)
    {
        if (disposed)
        {
            return;
        }

        var shouldDispatch = false;
        lock (chatMessageUiGate)
        {
            // Recheck alongside the queue/epoch update: a seek can begin after the receive
            // callback's first check. Captured messages are delivered by the replay clock later.
            if (expectedLiveSeekOperationVersion is { } version &&
                (!IsReplayClockSampleCurrent(version) || IsBehindLive || IsReplayMode))
            {
                return;
            }

            pendingChatMessages.Enqueue(new PendingChatMessage(
                message,
                isRememberedDockedLocalEcho,
                chatEpoch));
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
        long currentEpoch;
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
                currentEpoch = chatEpoch;
            }

            foreach (var pending in batch)
            {
                if (pending.Epoch != currentEpoch)
                {
                    continue;
                }

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

    private Task<bool> StartNativeOverlayChatTrackedAsync(AppSettings settings, CancellationToken cancellationToken, bool startCaptureChatClient = false) => nativeOverlay.StartNativeOverlayChatTrackedAsync(settings, cancellationToken, startCaptureChatClient);

    private void StartNativeOverlayChatInBackground(AppSettings settings, CancellationToken cancellationToken, bool startCaptureChatClient = false) => nativeOverlay.StartNativeOverlayChatInBackground(settings, cancellationToken, startCaptureChatClient);

    private Task StopNativeOverlayChatAsync(bool clearOverlay = false) => nativeOverlay.StopNativeOverlayChatAsync(clearOverlay);

    private DetachedNativeOverlayChat? TryDetachNativeOverlayChatForReplayTransition() => nativeOverlay.TryDetachNativeOverlayChatForReplayTransition();

    private Task BlankNativeOverlayAsync(string? pipeName = null, CancellationToken cancellationToken = default) => nativeOverlay.BlankNativeOverlayAsync(pipeName, cancellationToken);

    private void ClearNativeReplayOverlayForReplayTransition(ReplaySessionInfo replay, bool targetWindowHasReplayMessages) => nativeOverlay.ClearNativeReplayOverlayForReplayTransition(replay, targetWindowHasReplayMessages);

    private void ClearNativeReplayOverlayForEmptyReplayWindowInBackground() => nativeOverlay.ClearNativeReplayOverlayForEmptyReplayWindowInBackground();

    public Task<bool> TryReleaseNativeOverlayChatInputFocusAsync() => nativeOverlay.TryReleaseNativeOverlayChatInputFocusAsync();

    private void ApplyAudio()
    {
        if (playbackEngine is null)
        {
            return;
        }

        playbackEngine.SetAudioState(Volume, CurrentAudioState);
        AudioStateApplied?.Invoke(this, EventArgs.Empty);
    }

    private PlaybackAudioState CurrentAudioState => NeverMute
        ? PlaybackAudioState.Audible
        : IsMuted
            ? PlaybackAudioState.HardMuted
            : isSelectedForAudio
                ? PlaybackAudioState.Audible
                : PlaybackAudioState.Muted;

    internal static int NormalizeVolume(int value)
    {
        return Math.Clamp(value, VolumeLimits.Min, VolumeLimits.Max);
    }

    private static string BuildNativeOverlayPositionStatePath(StreamTarget target) => NativeChatOverlayController.BuildNativeOverlayPositionStatePath(target);

    private NativeOverlayPlaybackSnapshot? CaptureNativeOverlayPlayback()
    {
        var engine = playbackEngine;
        if (engine is null) return null;
        engine.TryGetVideoSize(out var width, out var height);
        return new(engine, engine.UsesNativeOverlay, engine.NativeOverlayPipeName,
            engine.NativeOverlayPositionStatePath, engine.NativeOverlayDirectory, width, height);
    }

    private NativeOverlayChatSnapshot CaptureNativeOverlayChat() => new(
        IsReplayMode, IsBehindLive, IsChatVisible, IsDockedChatOverrideActive, replaySession,
        replayChatStatusMessage, chatSettings is null ? null :
            new NativeOverlayChatOptions(chatSettings.Layout, chatSettings.DockWidth, chatSettings.VlcOverlayFontSize));

    private static string NormalizeOutgoingMessage(string message)
    {
        return ChatTextNormalizer.NormalizeSingleLine(message, 500);
    }

    private void QueueNativeChatOverlayUpdateAfterReplayWindowApply() => nativeOverlay.QueueNativeChatOverlayUpdateAfterReplayWindowApply();

    private void MarkNativeReplayOverlayRefreshPendingAfterSeek() => nativeOverlay.MarkNativeReplayOverlayRefreshPendingAfterSeek();

    private void FlushNativeReplayOverlayRefreshAfterSeek() => nativeOverlay.FlushNativeReplayOverlayRefreshAfterSeek();

    private void UpdateNativeChatOverlay() => nativeOverlay.UpdateNativeChatOverlay();

    private void ResetNativeReplayOverlayScrollState() => nativeOverlay.ResetNativeReplayOverlayScrollState();

    private void SuspendNativeReplayOverlayResizePersistence() => nativeOverlay.SuspendNativeReplayOverlayResizePersistence();

    private void StopNativeReplayOverlayEventHost() => nativeOverlay.StopNativeReplayOverlayEventHost();

    private Task StopNativeReplayOverlayEventHostAsync() => nativeOverlay.StopNativeReplayOverlayEventHostAsync();

    private void InvalidateNativeReplayOverlayFrame() => nativeOverlay.InvalidateNativeReplayOverlayFrame();

    private void CancelNativeReplayOverlayAnimationState() => nativeOverlay.CancelNativeReplayOverlayAnimationState();

    internal static TimeSpan CalculateNativeReplayOverlayAnimationDelay(TimeSpan animationClock, TimeSpan? nextAnimationFrameDelay, TimeSpan currentAnimationClock) => NativeChatOverlayController.CalculateNativeReplayOverlayAnimationDelay(animationClock, nextAnimationFrameDelay, currentAnimationClock);

    private bool ShouldUseNativeOverlayController(AppSettings settings) => !Target.IsOfflineVod && nativeOverlay.ShouldUseNativeOverlayController(settings);

    private Uri GetOfflinePlaybackUri()
    {
        if (!Path.IsPathFullyQualified(Target.LocalMediaPath) ||
            !Uri.TryCreate(Target.LocalMediaPath, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc)
            throw new InvalidDataException("Offline VOD playback requires a local media file, not a network URL or share.");
        if (!File.Exists(uri.LocalPath)) throw new FileNotFoundException("The downloaded VOD files are missing. Download this VOD again.", uri.LocalPath);
        return uri;
    }

    private bool IsDockedChatModeActive => chatSettings?.Layout == ChatLayout.Docked || IsDockedChatOverrideActive;

    private bool IsNativeOverlayChatCurrent(AppSettings settings) => nativeOverlay.IsNativeOverlayChatCurrent(settings);

    private static bool ShouldRequestNativeOverlay(ChatSettings settings)
    {
        return settings.Layout == ChatLayout.Overlay;
    }

    private static string? ResolveVlcOverlayDirectory(ChatSettings settings)
    {
        return VlcOverlayDirectoryResolver.TryResolve(settings.VlcOverlayDirectory);
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

    private readonly record struct PendingChatMessage(
        ChatMessage Message,
        bool IsRememberedDockedLocalEcho,
        long Epoch);

    private sealed record DockedLocalEcho(
        PlatformKind Platform,
        string Channel,
        string Username,
        string Message,
        DateTimeOffset Timestamp);

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
