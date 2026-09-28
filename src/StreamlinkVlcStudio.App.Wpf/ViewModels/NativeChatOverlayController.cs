using System.Collections.Immutable;
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
using StreamlinkVlcStudio.App.Wpf.Services;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Infrastructure.Chat;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using StreamlinkVlcStudio.Infrastructure.Vlc;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class NativeChatOverlayController : IAsyncDisposable
{
    private readonly StreamTarget Target;
    private readonly IAppLogger logger;
    private readonly Action<Action> dispatch;
    private readonly Action<string> AddSystemMessage;
    private readonly Func<NativeOverlayPlaybackSnapshot?> capturePlayback;
    private readonly Func<NativeOverlayChatSnapshot> captureChat;
    private readonly Func<AppSettings?> getSettings;
    private readonly Func<ImmutableArray<ChatMessage>> captureMessages;
    private readonly Action<Uri>? openChatLink;
    private readonly Func<CancellationToken, Task> StartChatAsync;
    private readonly Func<CancellationToken, Task> EnsureChatClientConnectedAsync;
    private readonly Func<AppSettings, bool> ShouldKeepChatClientForVodChatCapture;
    private readonly CancellationTokenSource lifetimeCancellation;
    private readonly ClipboardService clipboardService = new();
    private bool disposed;
    private Task? disposalTask;
    private int nativeReplayOverlayTextSelectionAvailable;
    private int nativeReplayOverlaySelectionStartValue;
    private int nativeReplayOverlaySelectionStartKnown;
    private int nativeReplayOverlayTextSelectionGeneration;
    private NativeOverlayPlaybackSnapshot? playbackEngine => capturePlayback();
    private NativeOverlayChatSnapshot Chat => captureChat();
    private ChatSettings? chatSettings => Chat.Options?.ToSettings();
    private AppSettings? currentSettings => getSettings();
    private ReplaySessionInfo? replaySession => Chat.Replay;
    private bool IsReplayMode => Chat.IsReplayMode;
    private bool IsBehindLive => Chat.IsBehindLive;
    private bool IsChatVisible => Chat.IsVisible;
    private bool IsDockedChatOverrideActive => Chat.IsDockedOverrideActive;
    private ChatMessage? replayChatStatusMessage => Chat.StatusMessage;
    private ImmutableArray<ChatMessage> ChatMessages => captureMessages();

    internal NativeChatOverlayController(StreamTarget target, IAppLogger logger, Action<Action> dispatch,
        Action<string> addSystemMessage, Func<NativeOverlayPlaybackSnapshot?> capturePlayback,
        Func<NativeOverlayChatSnapshot> captureChat, Func<ImmutableArray<ChatMessage>> captureMessages, Func<AppSettings?> getSettings,
        Func<CancellationToken, Task> startChat, Func<CancellationToken, Task> ensureChatConnected,
        Func<AppSettings, bool> shouldCaptureChat, Action<Uri>? openChatLink, CancellationToken cancellationToken)
    {
        Target = target;
        this.logger = logger;
        this.dispatch = dispatch;
        AddSystemMessage = addSystemMessage;
        this.capturePlayback = capturePlayback;
        this.captureChat = captureChat;
        this.captureMessages = captureMessages;
        this.getSettings = getSettings;
        this.openChatLink = openChatLink;
        StartChatAsync = startChat;
        EnsureChatClientConnectedAsync = ensureChatConnected;
        ShouldKeepChatClientForVodChatCapture = shouldCaptureChat;
        lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        nativeReplayOverlayEventHost = new NativeOverlayReplayEventHost(
            logger,
            this.dispatch,
            InvalidateNativeReplayOverlayFrame,
            GetNativeReplayOverlayVideoHeight,
            replayScrolled: ScrollNativeReplayOverlay,
            replayScrollPositionChanged: SetNativeReplayOverlayScrollPosition,
            uiScaleChanged: OnNativeReplayOverlayUiScaleChanged,
            textSelectionEvent: OnNativeReplayOverlayTextSelectionEvent);
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
    }

    internal bool TryGetLastVideoSize(out int width, out int height)
    {
        lock (nativeReplayOverlayRefreshGate)
        {
            width = nativeReplayOverlayVideoWidth;
            height = nativeReplayOverlayVideoHeight;
        }
        return width > 0 && height > 0;
    }
    internal void InvalidatePendingFrame()
    {
        nativeReplayOverlayRenderState.InvalidateFrameKey();
        nativeReplayOverlayFrameWriteGate.Invalidate();
        nativeReplayOverlayFrameScheduler?.CancelPending();
    }

    public ValueTask DisposeAsync()
    {
        if (disposalTask is not null) return new(disposalTask);
        disposed = true;
        lifetimeCancellation.Cancel();
        AnimatedEmoteImage.ImageCacheEntryCompleted -= OnAnimatedEmoteImageCacheEntryCompleted;
        DockedChatBadgeCatalog.Shared.CatalogChanged -= OnChatRenderCatalogChanged;
        DockedChatEmoteCatalog.Shared.CatalogChanged -= OnChatRenderCatalogChanged;
        CancelNativeReplayOverlayAnimationState();
        CancelNativeReplayOverlayWarmupRefresh();
        disposalTask = DisposeCoreAsync();
        return new(disposalTask);
    }
    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopNativeOverlayChatAsync(clearOverlay: true).ConfigureAwait(false);
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

        }
        finally
        {
            await nativeReplayOverlayFrameWriteGate.DisposeAsync();
            lifetimeCancellation.Dispose();
        }
    }
    internal static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan NativeOverlayGracefulStopTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan NativeOverlayShutdownRequestTimeout = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan NativeOverlayInputFocusReleaseTimeout = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan NativeOverlayClearTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan NativeReplayOverlayFrameWriteTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan NativeOverlayPipeConnectTimeout = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan ReplayDiagnosticsSlowThreshold = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan NativeReplayOverlayRefreshDelay = TimeSpan.FromMilliseconds(16);
    internal static readonly TimeSpan NativeReplayOverlayDefaultAnimationDelay = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan[] NativeReplayOverlayWarmupRefreshDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromSeconds(3)
    ];
    internal const int NativeReplayOverlayMessagesPerScrollNotch = 3;
    internal const string NativeOverlayFontSizeArgument = "--font-size";
    private readonly NativeOverlayCapabilityProbe nativeOverlayCapabilityProbe = new();
    private readonly object nativeOverlayStartupGate = new();
    private readonly object nativeReplayOverlayRefreshGate = new();
    private readonly object nativeReplayOverlayFrameSchedulerGate = new();
    private readonly object nativeReplayOverlayAnimationGate = new();
    private readonly object nativeReplayOverlayScrollGate = new();
    private readonly object nativeOverlayStopNoticeGate = new();
    private readonly NativeOverlayReplayEventHost nativeReplayOverlayEventHost;
    private readonly NativeReplayOverlayFrameWriteGate nativeReplayOverlayFrameWriteGate;
    private readonly NativeReplayOverlayRenderState nativeReplayOverlayRenderState = new();
    private NativeReplayOverlayFrameScheduler? nativeReplayOverlayFrameScheduler;
    private Task<NativeReplayOverlayFrameScheduler>? nativeReplayOverlayFrameSchedulerCreationTask;
    private readonly SemaphoreSlim nativeOverlayProcessGate = new(1, 1);
    private Process? nativeOverlayProcess;
    private Task? nativeOverlayStartupTask;
    private CancellationTokenSource? nativeOverlayStartupCancellation;
    private long nativeOverlayStartupVersion;
    private bool nativeReplayOverlayRefreshQueued;
    private bool nativeReplayOverlayRefreshPendingAfterSeek;
    private int nativeReplayOverlayVideoWidth;
    private int nativeReplayOverlayVideoHeight;
    private int nativeReplayOverlayUiScaleHeight;
    private string nativeReplayOverlayWarmupSessionKey = "";
    private long nativeReplayOverlayWarmupVersion;
    private CancellationTokenSource? nativeReplayOverlayWarmupCancellation;
    private long nativeReplayOverlayResizePersistenceResumeAfterVersion;
    private int nativeReplayOverlayMessageOffset;
    private int nativeReplayOverlayMaximumMessageOffset;
    private ChatMessage? nativeReplayOverlayAnchorMessage;
    private string nativeReplayOverlayScrollSessionKey = "";
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
    private KickOverlayChannelInfo? resolvedKickOverlayChannelInfo;
    private string? resolvedTwitchOverlayRoomId;
    internal bool IsNativeReplayOverlayEventHostRunning => nativeReplayOverlayEventHost.IsRunning;
    internal string? NativeReplayOverlayEventHostPipeName => nativeReplayOverlayEventHost.PipeName;
    internal bool HasNativeReplayOverlayTextSelection =>
        nativeReplayOverlayEventHost.IsRunning &&
        Volatile.Read(ref nativeReplayOverlayTextSelectionAvailable) != 0;
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

    internal void OnChatRenderCatalogChanged(object? sender, EventArgs e)
    {
        if (e is CatalogChangedEventArgs changes && !changes.MayAffect(Target))
        {
            return;
        }

        Interlocked.Increment(ref nativeReplayOverlayRenderContentVersion);
        dispatch(InvalidateNativeReplayOverlayFrame);
    }

    internal void OnAnimatedEmoteImageCacheEntryCompleted(object? sender, AnimatedEmoteImageCacheCompletedEventArgs e)
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

    internal async Task StopNativeOverlayChatAfterReplayTransitionAsync()
    {
        await StopNativeOverlayChatAsync(clearOverlay: false).ConfigureAwait(false);
    }

    internal async Task StopDetachedNativeOverlayChatAfterReplayTransitionAsync(DetachedNativeOverlayChat detached)
    {
        SuppressNativeOverlayStoppedNotice(detached.Process);
        await StopDetachedNativeOverlayChatAsync(detached, clearOverlay: false).ConfigureAwait(false);
    }

    internal void SuppressNativeOverlayStoppedNotice(Process? process)
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

    internal void OnNativeOverlayProcessExited(Process process)
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

    internal static void CancelCancellationSource(CancellationTokenSource? cancellation)
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

    internal void RefreshNativeReplayOverlayForVideoSize(int width, int height)
    {
        if (!RecordNativeReplayOverlayVideoSize(width, height) ||
            !ShouldRefreshNativeReplayOverlayForVideoSize())
        {
            return;
        }

        dispatch(InvalidateNativeReplayOverlayFrameIfReplayChatVisible);
    }

    internal bool RecordNativeReplayOverlayVideoSize(int width, int height)
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

    internal bool ShouldRefreshNativeReplayOverlayForVideoSize()
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

    internal async Task<bool> StartNativeOverlayChatAsync(
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

    internal Task<bool> StartNativeOverlayChatTrackedAsync(
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

    internal async Task<bool> RunNativeOverlayChatStartupWhenReadyAsync(
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

    internal async Task<bool> RunNativeOverlayChatStartupAsync(
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
                ShouldKeepChatClientForVodChatCapture(settings))
            {
                await StartChatAsync(operationCancellation.Token).ConfigureAwait(false);
                return true;
            }

            if (!started &&
                startCaptureChatClient &&
                ShouldKeepChatClientForVodChatCapture(settings))
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

    internal void StartNativeOverlayChatInBackground(
        AppSettings settings,
        CancellationToken cancellationToken,
        bool startCaptureChatClient = false)
    {
        _ = StartNativeOverlayChatTrackedAsync(
            settings,
            cancellationToken,
            startCaptureChatClient);
    }

    internal async Task<bool> TryStartNativeOverlayChatAsync(
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

    internal bool IsCurrentNativeOverlayStartup(
        long? operationVersion,
        CancellationToken cancellationToken,
        NativeOverlayPlaybackSnapshot? expectedEngine = null)
    {
        if (disposed || Target.IsExplicitVod || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (expectedEngine is not null && !ReferenceEquals(expectedEngine.Identity, playbackEngine?.Identity))
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

    internal async Task StopNativeOverlayChatAsync(bool clearOverlay = false)
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

    internal async Task StopNativeOverlayStartupAsync()
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

    internal DetachedNativeOverlayChat? TryDetachNativeOverlayChatForReplayTransition()
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

    internal DetachedNativeOverlayChat? DetachNativeOverlayChatCore(bool includePipeOnly)
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

    internal async Task StopDetachedNativeOverlayChatAsync(DetachedNativeOverlayChat detached, bool clearOverlay)
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

    internal static bool IsProcessRunning(Process? process)
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

    internal static void DisposeFailedNativeOverlayProcess(Process process)
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

    internal static void TryDeleteNativeOverlayTokenFile(string? tokenFile)
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

    internal async Task<bool> RequestNativeOverlayShutdownAsync(string pipeName, CancellationToken cancellationToken = default)
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

    internal async Task<bool> WaitForNativeOverlayProcessExitAsync(Process process, TimeSpan timeout, string timeoutMessage)
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

    internal async Task BlankNativeOverlayAsync(string? pipeName = null, CancellationToken cancellationToken = default)
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

    internal void ClearNativeReplayOverlayForReplayTransition(
        ReplaySessionInfo replay,
        bool targetWindowHasReplayMessages)
    {
        if (!replay.IsAvailable || targetWindowHasReplayMessages)
        {
            return;
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
            return;
        }

        CancelNativeReplayOverlayAnimationState();
        QueueCriticalNativeReplayOverlayFrameWrite(
            engine.NativeOverlayPipeName!,
            BuildTransparentNativeReplayOverlayFrameMessage(engine, settings));
    }

    internal void ClearNativeReplayOverlayForEmptyReplayWindowInBackground()
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

    internal void QueueCriticalNativeReplayOverlayFrameWrite(string pipeName, byte[] message)
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

    internal byte[] BuildTransparentNativeReplayOverlayFrameMessage(NativeOverlayPlaybackSnapshot engine, ChatSettings settings)
    {
        return NativeOverlayChatFrameRenderer.BuildTransparentFrameMessage(
            CloneChatSettingsForNativeReplayRender(settings),
            GetNativeReplayOverlayVideoHeight(),
            engine.NativeOverlayPositionStatePath,
            out _,
            out _);
    }

    internal async Task<(bool Sent, Exception? LastException)> TryWriteNativeOverlayMessageAsync(
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

    internal static bool TryWriteNativeOverlayEventSynchronously(
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

    internal void LogNativeOverlayLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        logger.Write(AppLogLevel.Info, "ChatOverlay", line);
    }

    internal static string? WriteOverlayTokenFile(string platform, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.ProductDirectoryName,
            "overlay-tokens");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{platform}-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, token ?? "", new UTF8Encoding(false));
        return path;
    }

    internal static string? FindTwitchBadgeManifestPath()
    {
        return BundledBadgeAssets.FindTwitchBadgeManifestPath();
    }

    internal static string? FindKickBadgeManifestPath()
    {
        return BundledBadgeAssets.FindKickBadgeManifestPath();
    }

    internal string BuildNativeOverlayLaunchKey(
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

    internal async Task<string?> ResolveTwitchOverlayRoomIdAsync(ChatSettings settings, CancellationToken cancellationToken)
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
            request.Headers.UserAgent.ParseAdd("StreamStudio/0.1");

            using var response = await BoundedHttpResponseSender.SendAsync(httpClient, request, cancellationToken);
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
            if (TwitchUserPayloadReader.TryRead(document.RootElement, Target.Channel, out var user))
            {
                var roomId = GetOptionalString(user, "id");
                if (IsAsciiDigits(roomId)) return CacheResolvedTwitchOverlayRoomId(roomId);
            }

            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Twitch room ID lookup did not return a user ID for {Target.Channel}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Twitch room ID lookup failed for {Target.Channel}.", ex);
        }

        return null;
    }

    internal string CacheResolvedTwitchOverlayRoomId(string roomId)
    {
        resolvedTwitchOverlayRoomId = roomId;
        return roomId;
    }

    internal static bool IsAsciiDigits(string value)
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

    internal async Task<string?> ResolveKickOverlayTokenAsync(ChatSettings settings, CancellationToken cancellationToken)
    {
        return await KickOAuthService.GetUsableAccessTokenAsync(
            settings,
            ApplyKickTokenResultOnUiThreadAsync,
            logger,
            cancellationToken);
    }

    internal async Task ApplyKickTokenResultOnUiThreadAsync(
        ChatSettings settings,
        KickOAuthTokenResult token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var credentials = KickCredentialSnapshot.Capture(settings);
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
                if (completion.Task.IsCompleted || cancellationToken.IsCancellationRequested || !credentials.Matches(settings))
                {
                    completion.TrySetCanceled();
                    return;
                }

                KickOAuthService.ApplyTokenResult(settings, token);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally
        {
            // A dispatcher callback can run after its caller's timeout or cancellation.
            completion.TrySetCanceled();
        }
    }

    internal string GetConfiguredKickSetting(
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

    internal static string BuildNativeOverlayPositionStatePath(StreamTarget target)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.ProductDirectoryName,
            "vlc-overlays",
            "streams");
        Directory.CreateDirectory(directory);

        var key = target.StateKey;
        var slug = BuildFileSlug(key);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..12];
        return Path.Combine(directory, $"{slug}-{hash}.txt");
    }

    internal static string BuildFileSlug(string value)
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

    internal static string TokenFingerprint(string token)
    {
        var normalized = token.Trim();
        if (normalized.Length == 0)
        {
            return "";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    internal static string FileFingerprint(string? path)
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

    internal async Task<KickOverlayChannelInfo> ResolveKickOverlayChannelInfoAsync(ChatSettings settings, bool sendAsBot, CancellationToken cancellationToken)
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

    internal KickOverlayChannelInfo CacheResolvedKickOverlayChannelInfo(KickOverlayChannelInfo info)
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

    internal async Task<KickOverlayChannelInfo> TryResolveKickChannelMetadataAsync(string channel, CancellationToken cancellationToken)
    {
        using var httpClient = HttpClientFactory.Create(TimeSpan.FromSeconds(18));
        var reader = new KickWebsiteJsonReader(httpClient, logger, "ChatOverlay", TimeSpan.FromSeconds(18));
        var escapedChannel = Uri.EscapeDataString(channel);
        var url = $"https://kick.com/api/v2/channels/{escapedChannel}";
        var referrer = $"https://kick.com/{escapedChannel}";
        try
        {
            var direct = await reader.ReadDirectAsync(url, referrer, cancellationToken);
            if (ReadKickOverlayChannelInfo(direct.Body) is { ChatroomId: not null } metadata)
                return metadata;

            var fallback = await reader.ReadFallbackAsync(url, referrer, cancellationToken);
            if (ReadKickOverlayChannelInfo(fallback) is { ChatroomId: not null } fallbackMetadata)
            {
                AddSystemMessage("Resolved Kick chatroom ID with curl fallback.");
                return fallbackMetadata;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Write(AppLogLevel.Warning, "ChatOverlay", $"Kick metadata lookup failed for {channel}.", ex);
        }

        return new KickOverlayChannelInfo(null, null);
    }

    internal static KickOverlayChannelInfo? ReadKickOverlayChannelInfo(string? body)
    {
        if (body is null) return null;
        using var document = JsonDocument.Parse(body);
        var info = KickChannelInfoJson.Read(document.RootElement);
        return new KickOverlayChannelInfo(info.ChatroomId, info.BroadcasterUserId);
    }

    internal void QueueNativeChatOverlayUpdateAfterReplayWindowApply()
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

    internal void MarkNativeReplayOverlayRefreshPendingAfterSeek()
    {
        lock (nativeReplayOverlayRefreshGate)
        {
            nativeReplayOverlayRefreshPendingAfterSeek = true;
        }
    }

    internal void FlushNativeReplayOverlayRefreshAfterSeek()
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

    internal async Task RunQueuedNativeReplayOverlayRefreshAsync()
    {
        await Task.Delay(NativeReplayOverlayRefreshDelay).ConfigureAwait(false);
        dispatch(ApplyQueuedNativeReplayOverlayRefresh);
    }

    internal void ApplyQueuedNativeReplayOverlayRefresh()
    {
        lock (nativeReplayOverlayRefreshGate)
        {
            nativeReplayOverlayRefreshQueued = false;
        }

        UpdateNativeChatOverlay();
    }

    internal void UpdateNativeChatOverlay()
    {
        // The custom VLC plugin overlay renders chat. The replay frame pipeline resets its own
        // state when the plugin overlay is unavailable, so there is no fallback path to drive here.
        UpdateNativeReplayChatOverlay();
    }

    internal void UpdateNativeReplayChatOverlay()
    {
        UpdateNativeReplayChatOverlay(
            forceAnimationRepaint: false,
            GetNativeReplayOverlayAnimationClock());
    }

    internal void UpdateNativeReplayChatOverlay(bool forceAnimationRepaint, TimeSpan animationClock)
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

        videoHeight = GetNativeReplayOverlayVideoHeight();
        var sourceSize = GetNativeReplayOverlaySourceSize();
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
            replaySessionKey,
            sourceSize);
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
            RenderContentVersion: renderContentVersion,
            SourceSize: sourceSize);
        _ = QueueNativeReplayOverlayFrameAsync(request);
    }

    internal ChatMessage[] GetNativeReplayOverlayMessages()
    {
        if (replayChatStatusMessage is { } status) return [status];
        return ChatMessages
            .Where(ShouldRenderNativeReplayOverlayMessage)
            .ToArray();
    }

    internal void ScrollNativeReplayOverlay(int wheelNotches)
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

    internal void SetNativeReplayOverlayScrollPosition(int messageOffset)
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

    internal void EnsureNativeReplayOverlayScrollSession(string replaySessionKey)
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

    internal int ResolveNativeReplayOverlayMessageOffset(IReadOnlyList<ChatMessage> messages)
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

    internal void ApplyNativeReplayOverlayRenderedSelection(
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

    internal void ResetNativeReplayOverlayScrollState()
    {
        lock (nativeReplayOverlayScrollGate)
        {
            ResetNativeReplayOverlayScrollStateLocked("");
        }
    }

    internal void ResetNativeReplayOverlayScrollStateLocked(string replaySessionKey)
    {
        nativeReplayOverlayMessageOffset = 0;
        nativeReplayOverlayMaximumMessageOffset = 0;
        nativeReplayOverlayAnchorMessage = null;
        nativeReplayOverlayScrollSessionKey = replaySessionKey;
    }

    internal static bool IsSameNativeReplayOverlayMessage(ChatMessage candidate, ChatMessage anchor)
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

    internal void StartNativeReplayOverlayEventHost(string pipeName, string positionStatePath)
    {
        var isReconnect = !nativeReplayOverlayEventHost.IsRunning ||
            !string.Equals(nativeReplayOverlayEventHost.PipeName, pipeName, StringComparison.Ordinal);
        nativeReplayOverlayEventHost.Start(pipeName, positionStatePath);
        if (isReconnect)
        {
            nativeReplayOverlayFrameWriteGate.NotifyReconnected(pipeName);
        }
    }

    internal void SuspendNativeReplayOverlayResizePersistence()
    {
        Volatile.Write(
            ref nativeReplayOverlayResizePersistenceResumeAfterVersion,
            nativeReplayOverlayRenderState.Version);
        nativeReplayOverlayEventHost.SuspendResizePersistence();
    }

    internal void ResumeNativeReplayOverlayResizePersistence(NativeReplayOverlayFrameWriteRequest request)
    {
        if (request.Version > Volatile.Read(ref nativeReplayOverlayResizePersistenceResumeAfterVersion) &&
            !string.Equals(request.WriteKind, "critical-clear", StringComparison.Ordinal))
        {
            nativeReplayOverlayEventHost.ResumeResizePersistence();
        }
    }

    internal void StopNativeReplayOverlayEventHost()
    {
        ResetNativeReplayOverlayFrameState();
        nativeReplayOverlayEventHost.Stop();
    }

    internal Task StopNativeReplayOverlayEventHostAsync()
    {
        ResetNativeReplayOverlayFrameState();
        return nativeReplayOverlayEventHost.StopAsync();
    }

    internal void InvalidateNativeReplayOverlayFrame()
    {
        nativeReplayOverlayRenderState.InvalidateFrameKey();
        UpdateNativeReplayChatOverlay();
    }

    internal void InvalidateNativeReplayOverlayFrameIfReplayChatVisible()
    {
        if (!HasNativeReplayOverlayRenderableMessages())
        {
            return;
        }

        InvalidateNativeReplayOverlayFrame();
    }

    internal bool HasNativeReplayOverlayRenderableMessages()
    {
        return replayChatStatusMessage is not null || ChatMessages.Any(ShouldRenderNativeReplayOverlayMessage);
    }

    internal void ScheduleNativeReplayOverlayWarmupRefresh(
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

        CancelCancellationSource(previousCancellation);
        _ = RunNativeReplayOverlayWarmupRefreshAsync(cancellation, warmupVersion);
    }

    internal string BuildNativeReplayOverlayWarmupSessionKey(string pipeName, string? positionStatePath)
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

    internal string GetNativeReplayOverlaySessionKey()
    {
        var replay = replaySession;
        return string.Join(
            ":",
            replay?.Platform.ToString() ?? Target.Platform.ToString(),
            replay?.Channel ?? Target.Channel,
            replay?.ReplayId ?? Target.Url,
            IsReplayMode || IsBehindLive ? "replay" : "live");
    }

    internal async Task RunNativeReplayOverlayWarmupRefreshAsync(
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

    internal bool IsNativeReplayOverlayWarmupCurrent(
        CancellationTokenSource cancellation,
        long warmupVersion)
    {
        lock (nativeReplayOverlayRefreshGate)
        {
            return ReferenceEquals(nativeReplayOverlayWarmupCancellation, cancellation) &&
                nativeReplayOverlayWarmupVersion == warmupVersion;
        }
    }

    internal void CancelNativeReplayOverlayWarmupRefresh()
    {
        CancellationTokenSource? cancellation;
        lock (nativeReplayOverlayRefreshGate)
        {
            cancellation = nativeReplayOverlayWarmupCancellation;
            nativeReplayOverlayWarmupCancellation = null;
            nativeReplayOverlayWarmupSessionKey = "";
            nativeReplayOverlayWarmupVersion++;
        }

        CancelCancellationSource(cancellation);
    }

    internal void ResetNativeReplayOverlayFrameState()
    {
        Interlocked.Increment(ref nativeReplayOverlayTextSelectionGeneration);
        Interlocked.Exchange(ref nativeReplayOverlayTextSelectionAvailable, 0);
        Interlocked.Exchange(ref nativeReplayOverlaySelectionStartKnown, 0);
        if (nativeReplayOverlayFrameScheduler is { } scheduler)
        {
            _ = scheduler.HandleTextSelectionEventAsync(
                NativeOverlayProtocolCodec.TextSelectionCancelEventType,
                0);
        }

        lock (nativeReplayOverlayRefreshGate)
        {
            nativeReplayOverlayRefreshPendingAfterSeek = false;
            nativeReplayOverlayVideoWidth = 0;
            nativeReplayOverlayVideoHeight = 0;
            nativeReplayOverlayUiScaleHeight = 0;
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

    internal async Task QueueNativeReplayOverlayFrameAsync(NativeReplayOverlayFrameRequest request)
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

    internal async Task<NativeReplayOverlayFrameScheduler> GetNativeReplayOverlayFrameSchedulerAsync()
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

    internal void OnNativeReplayOverlayUiScaleChanged(int height)
    {
        if (Interlocked.Exchange(ref nativeReplayOverlayUiScaleHeight, height) == height) return;
        SuspendNativeReplayOverlayResizePersistence();
        InvalidateNativeReplayOverlayFrameIfReplayChatVisible();
    }

    internal async Task CopyNativeReplayOverlayTextSelectionAsync()
    {
        var scheduler = nativeReplayOverlayFrameScheduler;
        if (scheduler is null)
        {
            return;
        }

        try
        {
            var text = await scheduler.GetSelectedMessageBodyTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var result = await clipboardService.TrySetTextAsync(text);
            if (!result.Succeeded && result.Error is not null)
            {
                logger.Write(AppLogLevel.Debug, "ChatOverlay", "Could not copy selected replay-overlay chat text.", result.Error);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Write(AppLogLevel.Debug, "ChatOverlay", "Could not copy selected replay-overlay chat text.", ex);
        }
    }

    private void OnNativeReplayOverlayTextSelectionEvent(uint eventType, int packedPoint)
    {
        var selectionGeneration = Interlocked.Increment(ref nativeReplayOverlayTextSelectionGeneration);
        if (eventType == NativeOverlayProtocolCodec.TextClickEventType)
        {
            Interlocked.Exchange(ref nativeReplayOverlaySelectionStartKnown, 0);
            Interlocked.Exchange(ref nativeReplayOverlayTextSelectionAvailable, 0);
        }
        else if (eventType == NativeOverlayProtocolCodec.TextSelectionBeginEventType)
        {
            Interlocked.Exchange(ref nativeReplayOverlaySelectionStartValue, packedPoint);
            Interlocked.Exchange(ref nativeReplayOverlaySelectionStartKnown, 1);
            Interlocked.Exchange(ref nativeReplayOverlayTextSelectionAvailable, 0);
        }
        else if (eventType == NativeOverlayProtocolCodec.TextSelectionCancelEventType)
        {
            Interlocked.Exchange(ref nativeReplayOverlaySelectionStartKnown, 0);
            Interlocked.Exchange(ref nativeReplayOverlayTextSelectionAvailable, 0);
        }
        else if ((eventType == NativeOverlayProtocolCodec.TextSelectionUpdateEventType ||
                  eventType == NativeOverlayProtocolCodec.TextSelectionEndEventType) &&
                 Volatile.Read(ref nativeReplayOverlaySelectionStartKnown) != 0 &&
                 Volatile.Read(ref nativeReplayOverlaySelectionStartValue) != packedPoint)
        {
            // Keep Ctrl+C available immediately after mouse-up while the render-thread
            // endpoint update is still queued.
            Interlocked.Exchange(ref nativeReplayOverlayTextSelectionAvailable, 1);
        }

        var scheduler = nativeReplayOverlayFrameScheduler;
        if (scheduler is null)
        {
            return;
        }

        if (eventType == NativeOverlayProtocolCodec.TextClickEventType)
        {
            _ = ApplyNativeReplayOverlayTextClickEventAsync(
                scheduler,
                packedPoint,
                selectionGeneration);
            return;
        }

        _ = ApplyNativeReplayOverlayTextSelectionEventAsync(
            scheduler,
            eventType,
            packedPoint,
            selectionGeneration);
    }

    private async Task ApplyNativeReplayOverlayTextSelectionEventAsync(
        NativeReplayOverlayFrameScheduler scheduler,
        uint eventType,
        int packedPoint,
        int selectionGeneration)
    {
        try
        {
            var hasSelection = await scheduler
                .HandleTextSelectionEventAsync(eventType, packedPoint)
                .ConfigureAwait(false);
            dispatch(() =>
            {
                if (!ReferenceEquals(scheduler, nativeReplayOverlayFrameScheduler))
                {
                    return;
                }

                if (!nativeReplayOverlayEventHost.IsRunning ||
                    Volatile.Read(ref nativeReplayOverlayTextSelectionGeneration) != selectionGeneration)
                {
                    return;
                }

                if (eventType == NativeOverlayProtocolCodec.TextSelectionEndEventType)
                {
                    Interlocked.Exchange(
                        ref nativeReplayOverlayTextSelectionAvailable,
                        hasSelection ? 1 : 0);
                    Interlocked.Exchange(ref nativeReplayOverlaySelectionStartKnown, 0);
                }
                else if (eventType == NativeOverlayProtocolCodec.TextSelectionCancelEventType)
                {
                    Interlocked.Exchange(ref nativeReplayOverlayTextSelectionAvailable, 0);
                    Interlocked.Exchange(ref nativeReplayOverlaySelectionStartKnown, 0);
                }

                InvalidateNativeReplayOverlayFrame();
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Write(AppLogLevel.Debug, "ChatOverlay", "Could not update replay-overlay chat text selection.", ex);
        }
    }

    private async Task ApplyNativeReplayOverlayTextClickEventAsync(
        NativeReplayOverlayFrameScheduler scheduler,
        int packedPoint,
        int selectionGeneration)
    {
        try
        {
            var uri = await scheduler
                .HandleTextClickEventAsync(packedPoint)
                .ConfigureAwait(false);
            dispatch(() =>
            {
                if (!ReferenceEquals(scheduler, nativeReplayOverlayFrameScheduler) ||
                    !nativeReplayOverlayEventHost.IsRunning ||
                    Volatile.Read(ref nativeReplayOverlayTextSelectionGeneration) != selectionGeneration)
                {
                    return;
                }

                Interlocked.Exchange(ref nativeReplayOverlayTextSelectionAvailable, 0);
                Interlocked.Exchange(ref nativeReplayOverlaySelectionStartKnown, 0);
                if (uri is not null && ChatLinkParser.IsSupportedWebUri(uri))
                {
                    try
                    {
                        openChatLink?.Invoke(uri);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        logger.Write(AppLogLevel.Warning, "ChatOverlay", "Could not open a link from replay chat.", ex);
                    }
                }

                InvalidateNativeReplayOverlayFrame();
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.Write(AppLogLevel.Debug, "ChatOverlay", "Could not resolve a clicked replay-overlay chat link.", ex);
        }
    }

    internal NativeOverlaySourceSize? GetNativeReplayOverlaySourceSize() =>
        Volatile.Read(ref nativeReplayOverlayUiScaleHeight) > 0 &&
        playbackEngine?.TryGetVideoSize(out var width, out var height) == true && width > 0 && height > 0
            ? new NativeOverlaySourceSize(width, height)
            : null;

    internal int GetNativeReplayOverlayVideoHeight()
    {
        var scaleHeight = Volatile.Read(ref nativeReplayOverlayUiScaleHeight);
        if (scaleHeight > 0) return scaleHeight;
        return playbackEngine?.TryGetVideoSize(out _, out var height) == true && height > 0
            ? height
            : 0;
    }

    internal async Task<NativeReplayOverlayFrameWriteResult> WriteNativeReplayOverlayFrameMessageAsync(
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

    internal void OnNativeReplayOverlayFrameWriteFailed(Exception exception)
    {
        nativeReplayOverlayRenderState.InvalidateFrameKey();
        logger.Write(AppLogLevel.Warning, "ChatOverlay", "Could not update the native VLC replay chat overlay.", exception);
        if (IsReplayMode || IsBehindLive)
        {
            dispatch(InvalidateNativeReplayOverlayFrame);
        }
    }

    internal void OnNativeReplayOverlayFrameRendered(NativeReplayOverlayFrameResult result)
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
            result.Request.Messages.All(IsSystemChatMessage);
        var writeKind = result.Request.Messages.Count == 0
            ? "blank-frame"
            : result.Request.Messages.All(IsSystemChatMessage)
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

    internal void OnNativeReplayOverlayFrameWriteSucceeded(NativeReplayOverlayFrameWriteRequest request)
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

    internal void TrackNativeReplayOverlayPendingImageLoads(
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

    internal void CancelNativeReplayOverlayAnimationState()
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

    internal void CancelNativeReplayOverlayAnimationTimer()
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

    internal void ScheduleNativeReplayOverlayAnimationFrame(
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

    internal async Task RunNativeReplayOverlayAnimationTimerAsync(
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

    internal TimeSpan GetNativeReplayOverlayAnimationClock()
    {
        var epoch = Volatile.Read(ref nativeReplayOverlayAnimationEpochTimestamp);
        var now = Stopwatch.GetTimestamp();
        return now > epoch
            ? Stopwatch.GetElapsedTime(epoch, now)
            : TimeSpan.Zero;
    }

    internal bool TryAdoptNativeReplayOverlayImageCachePins(object? imageCachePinOwner, long version)
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

    internal static void ReleaseNativeReplayOverlayImageCachePins(object? imageCachePinOwner)
    {
        if (imageCachePinOwner is not null)
        {
            AnimatedEmoteImage.ClearCachePins(imageCachePinOwner);
        }
    }

    internal static ChatSettings CloneChatSettingsForNativeReplayRender(ChatSettings settings)
    {
        return new ChatSettings
        {
            DockWidth = settings.DockWidth
        };
    }

    internal static string BuildNativeReplayOverlayFrameKey(
        string pipeName,
        string? positionStatePath,
        ChatSettings settings,
        double overlayFontSize,
        int videoHeight,
        IReadOnlyList<ChatMessage> messages,
        int messageOffset,
        string replaySessionKey,
        NativeOverlaySourceSize? sourceSize = null)
    {
        var layout = NativeOverlayChatFrameRenderer.ResolveReplayOverlayLayout(
            settings,
            overlayFontSize,
            videoHeight,
            positionStatePath,
            sourceSize);
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

    internal bool ShouldUseNativeOverlayController(AppSettings settings)
    {
        return !Target.IsExplicitVod && !IsReplayMode && !IsBehindLive &&
            settings.Chat.ConnectAutomatically &&
            settings.Chat.Layout == ChatLayout.Overlay &&
            !IsDockedChatOverrideActive &&
            IsChatVisible &&
            playbackEngine?.UsesNativeOverlay == true;
    }

    internal bool IsNativeOverlayChatCurrent(AppSettings settings)
    {
        return playbackEngine is { UsesNativeOverlay: true } engine &&
            !string.IsNullOrWhiteSpace(engine.NativeOverlayPipeName) &&
            IsProcessRunning(nativeOverlayProcess) &&
            string.Equals(nativeOverlayPipeName, engine.NativeOverlayPipeName, StringComparison.Ordinal) &&
            string.Equals(nativeOverlayLaunchKey, BuildNativeOverlayLaunchKey(settings), StringComparison.Ordinal);
    }

    internal int GetNativeOverlayFontSize(AppSettings settings)
    {
        var value = settings.StreamVlcOverlayFontSizes.TryGetValue(Target.StateKey, out var savedFontSize)
            ? savedFontSize
            : settings.Chat.VlcOverlayFontSize;
        return (int)Math.Round(Math.Clamp(
            value,
            ChatSettings.MinimumFontSize,
            ChatSettings.MaximumFontSize));
    }

    internal static string? ResolveVlcOverlayDirectory(ChatSettings settings)
    {
        return VlcOverlayDirectoryResolver.TryResolve(settings.VlcOverlayDirectory);
    }

    internal string? ResolveActiveNativeOverlayDirectory(ChatSettings settings)
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

    internal static string? ResolveNativeOverlayControllerDirectory(NativeOverlayPlaybackSnapshot engine, ChatSettings settings)
    {
        var engineDirectory = VlcOverlayDirectoryResolver.NormalizeDirectory(engine.NativeOverlayDirectory);
        return string.IsNullOrWhiteSpace(engineDirectory)
            ? ResolveVlcOverlayDirectory(settings)
            : engineDirectory;
    }

    internal static string GetConfiguredNativeOverlayControllerPath(ChatSettings settings)
    {
        var configuredDirectory = VlcOverlayDirectoryResolver.NormalizeDirectory(settings.VlcOverlayDirectory);
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            configuredDirectory = VlcOverlayDirectoryResolver.GetBundledOverlayDirectory();
        }

        return VlcOverlayDirectoryResolver.GetControllerPath(configuredDirectory);
    }

    internal static bool IsSystemChatMessage(ChatMessage message)
    {
        return string.Equals(message.Username, "system", StringComparison.OrdinalIgnoreCase);
    }

    internal bool ShouldRenderNativeReplayOverlayMessage(ChatMessage message)
    {
        // System notices are normally chrome the overlay leaves out, but on a VOD they carry the
        // only explanation of why chat is missing, so they earn a line there.
        return !IsSystemChatMessage(message) || Target.IsExplicitVod;
    }
}

internal sealed record DetachedNativeOverlayChat(Process? Process, string? PipeName, string? TokenFile);

internal sealed record KickOverlayChannelInfo(string? ChatroomId, long? BroadcasterUserId);
