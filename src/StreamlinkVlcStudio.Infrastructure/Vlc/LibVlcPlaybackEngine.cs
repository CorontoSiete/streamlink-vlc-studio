using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

public sealed class LibVlcPlaybackEngineFactory : IPlaybackEngineFactory
{
    private readonly IAppLogger logger;
    private readonly ChatSettings chatSettings;

    public LibVlcPlaybackEngineFactory(IAppLogger logger, ChatSettings chatSettings)
    {
        this.logger = logger;
        this.chatSettings = chatSettings;
    }

    public async Task<IPlaybackEngine> CreateAsync(
        string vlcDirectory,
        bool enableNativeOverlay = true,
        string? nativeOverlayPositionStatePath = null,
        CancellationToken cancellationToken = default,
        VideoRendererMode rendererMode = VideoRendererMode.Automatic)
    {
        return await LibVlcPlaybackEngine.CreateAsync(
                vlcDirectory,
                chatSettings,
                logger,
                enableNativeOverlay,
                nativeOverlayPositionStatePath,
                cancellationToken,
                rendererMode)
            .ConfigureAwait(false);
    }
}

public sealed class LibVlcPlaybackEngine : IPlaybackEngine
{
    private const int NativeOverlayShowPlaceholder = 0;
    private static readonly TimeSpan VideoOutputRebindReadinessTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VideoOutputRebindPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan[] AudioStateConvergenceDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12)
    ];

    private readonly IAppLogger logger;
    private readonly string vlcDirectory;
    private readonly object nativeGate = new();
    private readonly LibVlcAudioStateController audioStateController = new();
    private readonly SemaphoreSlim audioApplySignal = new(0, 1);
    private readonly CancellationTokenSource audioApplyCancellation = new();
    private readonly Task audioApplyTask;
    private RuntimeLease? runtimeLease;
    private IntPtr instance;
    private IntPtr player;
    private IntPtr media;
    private IntPtr videoHandle;
    private Uri? currentMediaUri;
    private bool desiredPaused;
    private int? lastEnabledAudioTrackId;
    private int videoOutputVersion;
    private long playerGeneration;
    private bool audioTrackSelectionUnavailable;
    private bool audioMuteUnavailable;
    private bool audioTrackDisabledByEngine;
    private bool? lastNativeMuteState;
    private bool disposed;

    private LibVlcPlaybackEngine(
        string vlcDirectory,
        IAppLogger logger,
        VlcOverlayPluginRuntime? nativeOverlay,
        string? nativeOverlayPositionStatePath,
        VideoRendererMode rendererMode)
    {
        this.logger = logger;

        if (string.IsNullOrWhiteSpace(vlcDirectory))
        {
            throw new FileNotFoundException(
                "libvlc.dll was not found. Configure the VLC installation directory.",
                Path.Combine(vlcDirectory ?? "", "libvlc.dll"));
        }

        this.vlcDirectory = Path.GetFullPath(vlcDirectory.Trim());
        if (!File.Exists(Path.Combine(this.vlcDirectory, "libvlc.dll")))
        {
            throw new FileNotFoundException(
                "libvlc.dll was not found. Configure the VLC installation directory.",
                Path.Combine(this.vlcDirectory, "libvlc.dll"));
        }

        if (nativeOverlay is not null)
        {
            UsesNativeOverlay = true;
            NativeOverlayDirectory = nativeOverlay.OverlayDirectory;
            NativeOverlayPipeName = $"svs_{Guid.NewGuid():N}";
            var positionStatePath = string.IsNullOrWhiteSpace(nativeOverlayPositionStatePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "StreamlinkVlcStudio",
                    "vlc-overlays",
                    $"{NativeOverlayPipeName}.txt")
                : nativeOverlayPositionStatePath;
            NativeOverlayPositionStatePath = Path.GetFullPath(positionStatePath);

            var positionStateDirectory = Path.GetDirectoryName(NativeOverlayPositionStatePath);
            if (!string.IsNullOrWhiteSpace(positionStateDirectory))
            {
                Directory.CreateDirectory(positionStateDirectory);
            }
            SetVlcEnvironmentVariable(
                "VLC_PLUGIN_PATH",
                BuildPluginPath(this.vlcDirectory, nativeOverlay.PluginRoot, Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")));
            logger.Write(
                AppLogLevel.Info,
                "VlcOverlay",
                $"Native VLC overlay startup plugin={nativeOverlay.PluginPath} pluginSha256={nativeOverlay.PluginSha256} controller={nativeOverlay.ControllerPath} controllerSha256={nativeOverlay.ControllerSha256} pipe={NativeOverlayPipeName} show-placeholder={NativeOverlayShowPlaceholder} pluginRoot={nativeOverlay.PluginRoot} overlay={nativeOverlay.OverlayDirectory}.");
        }

        LibVlcNative.SetDllDirectory(this.vlcDirectory);
        var selectedRenderer = LibVlcRendererSelection.Resolve(
            this.vlcDirectory,
            rendererMode,
            UsesNativeOverlay);
        if (UsesNativeOverlay && rendererMode != VideoRendererMode.Gdi)
        {
            logger.Write(
                AppLogLevel.Info,
                "libVLC",
                "Native overlay compatibility selected the GDI video renderer.");
        }

        try
        {
            runtimeLease = AcquireRuntime(selectedRenderer);
        }
        catch (Exception ex) when (selectedRenderer != VideoRendererMode.Gdi)
        {
            logger.Write(
                AppLogLevel.Warning,
                "libVLC",
                "Direct3D11 initialization failed; falling back to the GDI video renderer.",
                ex);
            selectedRenderer = VideoRendererMode.Gdi;
            runtimeLease = AcquireRuntime(selectedRenderer);
        }

        instance = runtimeLease.Instance;
        RendererMode = selectedRenderer;
        logger.Write(
            AppLogLevel.Info,
            "libVLC",
            $"Using {RendererMode} video renderer with automatic hardware decoding.");
        audioApplyTask = Task.Run(ApplyAudioStateLoopAsync);
    }

    internal static async Task<LibVlcPlaybackEngine> CreateAsync(
        string vlcDirectory,
        ChatSettings chatSettings,
        IAppLogger logger,
        bool enableNativeOverlay,
        string? nativeOverlayPositionStatePath,
        CancellationToken cancellationToken,
        VideoRendererMode rendererMode)
    {
        var nativeOverlay = enableNativeOverlay
            ? await VlcOverlayPluginRuntimeFactory.TryPrepareAsync(
                    vlcDirectory,
                    chatSettings.VlcOverlayDirectory,
                    logger,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false)
            : null;
        return new LibVlcPlaybackEngine(
            vlcDirectory,
            logger,
            nativeOverlay,
            nativeOverlayPositionStatePath,
            rendererMode);
    }

    public bool UsesNativeOverlay { get; }
    public VideoRendererMode RendererMode { get; private set; }
    public string? NativeOverlayPipeName { get; }
    public string? NativeOverlayPositionStatePath { get; }
    public string? NativeOverlayDirectory { get; }
    public event EventHandler? VideoOutputRebound;
    public event EventHandler? AudioStateReapplied;

    public void SetVideoHandle(IntPtr handle)
    {
        var previousHandle = videoHandle;
        videoHandle = handle;
        var handleChanged = previousHandle != handle;
        var rebindVersion = handleChanged ? Interlocked.Increment(ref videoOutputVersion) : Volatile.Read(ref videoOutputVersion);
        var shouldRebindVideoOutput = ShouldRebindVideoOutput(
            previousHandle,
            videoHandle,
            player != IntPtr.Zero,
            currentMediaUri is not null);

        if (shouldRebindVideoOutput)
        {
            // A running Win32 vout owns a nested child-window tree. Moving that tree with
            // SetParent can succeed but leaves the GDI output periodically painting black.
            // Recreate it so libvlc_media_player_set_hwnd runs before playback starts.
            logger.Write(
                AppLogLevel.Info,
                "libVLC",
                "Recreating libVLC video output for the new host surface.");
            ScheduleVideoOutputRebind(rebindVersion);
            return;
        }

        if (disposed || player == IntPtr.Zero || !Monitor.TryEnter(nativeGate))
        {
            return;
        }

        try
        {
            if (!disposed && player != IntPtr.Zero)
            {
                LibVlcNative.libvlc_media_player_set_hwnd(player, videoHandle);
            }
        }
        finally
        {
            Monitor.Exit(nativeGate);
        }
    }

    private static bool ShouldRebindVideoOutput(
        IntPtr previousHandle,
        IntPtr currentHandle,
        bool hasActivePlayer,
        bool hasCurrentMedia)
    {
        return previousHandle != currentHandle &&
            currentHandle != IntPtr.Zero &&
            hasActivePlayer &&
            hasCurrentMedia;
    }

    public Task PlayAsync(Uri mediaUri, int volume, PlaybackAudioState audioState, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        audioStateController.Update(volume, audioState);
        return RunBlockingNativeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (nativeGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                StopCurrentCore();
                currentMediaUri = mediaUri;
                desiredPaused = false;

                if (videoHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("The video surface is not ready yet.");
                }

                CreatePlayerCore(mediaUri);
                _ = ApplyAudioCore();

                var result = LibVlcNative.libvlc_media_player_play(player);
                if (result != 0 &&
                    RendererMode == VideoRendererMode.Direct3D11 &&
                    !UsesNativeOverlay)
                {
                    logger.Write(
                        AppLogLevel.Warning,
                        "libVLC",
                        "Direct3D11 could not start the video output; retrying with GDI.");
                    SwitchToGdiCore(mediaUri);
                    result = LibVlcNative.libvlc_media_player_play(player);
                }

                if (result != 0)
                {
                    StopCurrentCore();
                    throw new InvalidOperationException("libVLC failed to start playback.");
                }

                logger.Write(AppLogLevel.Info, "libVLC", $"Playing {mediaUri}");
                if (!ApplyAudioCore())
                {
                    ScheduleAudioStateConvergence();
                }
            }
        }, cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return RunBlockingNativeAsync(() =>
        {
            lock (nativeGate)
            {
                if (!disposed && player != IntPtr.Zero)
                {
                    desiredPaused = true;
                    LibVlcNative.libvlc_media_player_set_pause(player, 1);
                }
            }
        }, cancellationToken);
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return RunBlockingNativeAsync(() =>
        {
            lock (nativeGate)
            {
                if (!disposed && player != IntPtr.Zero)
                {
                    desiredPaused = false;
                    LibVlcNative.libvlc_media_player_set_pause(player, 0);
                }
            }
        }, cancellationToken);
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return RunBlockingNativeAsync(() =>
        {
            lock (nativeGate)
            {
                if (disposed || player == IntPtr.Zero)
                {
                    return;
                }

                if (LibVlcNative.libvlc_media_player_is_seekable(player) == 0)
                {
                    throw new InvalidOperationException("The current media is not seekable.");
                }

                var length = LibVlcNative.libvlc_media_player_get_length(player);
                var targetMilliseconds = Math.Max(0, (long)Math.Round(position.TotalMilliseconds));
                if (length > 0)
                {
                    targetMilliseconds = Math.Min(targetMilliseconds, length);
                }

                LibVlcNative.libvlc_media_player_set_time(player, targetMilliseconds);
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return RunBlockingNativeAsync(() =>
        {
            lock (nativeGate)
            {
                if (!disposed)
                {
                    StopCurrentCore();
                }
            }
        }, cancellationToken);
    }

    public bool TryGetPlaybackClock(out PlaybackClock clock)
    {
        clock = new PlaybackClock(TimeSpan.Zero, null, false);
        if (disposed || player == IntPtr.Zero || !Monitor.TryEnter(nativeGate))
        {
            return false;
        }

        try
        {
            if (disposed || player == IntPtr.Zero)
            {
                return false;
            }

            var time = LibVlcNative.libvlc_media_player_get_time(player);
            var length = LibVlcNative.libvlc_media_player_get_length(player);
            var isSeekable = LibVlcNative.libvlc_media_player_is_seekable(player) != 0;
            if (!TryCreateTimeSpanFromMilliseconds(time, out var position))
            {
                return false;
            }

            TimeSpan? duration = null;
            if (length > 0)
            {
                if (!TryCreateTimeSpanFromMilliseconds(length, out var mediaDuration))
                {
                    return false;
                }

                duration = mediaDuration;
            }

            clock = new PlaybackClock(position, duration, isSeekable);
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException or OverflowException)
        {
            return false;
        }
        finally
        {
            Monitor.Exit(nativeGate);
        }
    }

    private static bool TryCreateTimeSpanFromMilliseconds(long milliseconds, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        var maximumMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds < 0 || milliseconds > maximumMilliseconds)
        {
            return false;
        }

        value = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
        return true;
    }

    public bool TryGetVideoSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (disposed || player == IntPtr.Zero || !Monitor.TryEnter(nativeGate))
        {
            return false;
        }

        try
        {
            if (disposed || player == IntPtr.Zero)
            {
                return false;
            }

            var result = LibVlcNative.libvlc_video_get_size(player, 0, out var nativeWidth, out var nativeHeight);
            if (result != 0 || nativeWidth == 0 || nativeHeight == 0)
            {
                return false;
            }

            width = checked((int)nativeWidth);
            height = checked((int)nativeHeight);
            return true;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException or OverflowException)
        {
            return false;
        }
        finally
        {
            Monitor.Exit(nativeGate);
        }
    }

    public bool TryGetVideoCursor(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (disposed || player == IntPtr.Zero || !Monitor.TryEnter(nativeGate))
        {
            return false;
        }

        try
        {
            if (disposed || player == IntPtr.Zero)
            {
                return false;
            }

            return LibVlcNative.libvlc_video_get_cursor(player, 0, out x, out y) == 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return false;
        }
        finally
        {
            Monitor.Exit(nativeGate);
        }
    }

    public void SetAudioState(int volume, PlaybackAudioState audioState)
    {
        audioStateController.Update(volume, audioState);
        SignalAudioWorker();
    }

    private async Task ApplyAudioStateLoopAsync()
    {
        try
        {
            while (true)
            {
                await audioApplySignal.WaitAsync(audioApplyCancellation.Token).ConfigureAwait(false);
                while (audioApplySignal.Wait(0))
                {
                }

                var request = audioStateController.Snapshot;
                var requestVersion = request.Version;
                var audioState = request.AudioState;
                var scheduleConvergence = false;
                try
                {
                    lock (nativeGate)
                    {
                        if (!disposed &&
                            player != IntPtr.Zero &&
                            audioStateController.IsCurrent(requestVersion, audioState))
                        {
                            var applied = ApplyImmediateAudioCore(audioState, requestVersion);
                            scheduleConvergence =
                                audioStateController.IsCurrent(requestVersion, audioState) &&
                                ShouldScheduleAudioStateConvergence(audioState, applied);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Write(AppLogLevel.Warning, "libVLC", "Applying audio state failed.", ex);
                    continue;
                }

                if (scheduleConvergence)
                {
                    ScheduleAudioStateConvergence(requestVersion);
                }
            }
        }
        catch (OperationCanceledException) when (audioApplyCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "libVLC", "The audio state worker failed.", ex);
        }
    }

    private void SignalAudioWorker()
    {
        try
        {
            audioApplySignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake-up already represents the latest desired state.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        lock (nativeGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            StopCurrentCore();
            if (runtimeLease is not null)
            {
                runtimeLease.Dispose();
                runtimeLease = null;
                instance = IntPtr.Zero;
            }
        }

        audioApplyCancellation.Cancel();
        SignalAudioWorker();
        _ = audioApplyTask.ContinueWith(
            _ =>
            {
                audioApplyCancellation.Dispose();
                audioApplySignal.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StopCurrentCore(bool clearCurrentMedia = true)
    {
        audioStateController.Invalidate();
        lastEnabledAudioTrackId = null;
        lastNativeMuteState = null;
        audioTrackDisabledByEngine = false;
        if (clearCurrentMedia)
        {
            currentMediaUri = null;
            desiredPaused = false;
            Interlocked.Increment(ref videoOutputVersion);
        }

        if (player != IntPtr.Zero)
        {
            LibVlcNative.libvlc_media_player_stop(player);
            LibVlcNative.libvlc_media_player_release(player);
            player = IntPtr.Zero;
            Interlocked.Increment(ref playerGeneration);
        }

        if (media != IntPtr.Zero)
        {
            LibVlcNative.libvlc_media_release(media);
            media = IntPtr.Zero;
        }
    }

    private void CreatePlayerCore(Uri mediaUri)
    {
        media = LibVlcNative.libvlc_media_new_location(instance, mediaUri.ToString());
        if (media == IntPtr.Zero)
        {
            throw new InvalidOperationException($"libVLC could not create media for {mediaUri}.");
        }

        try
        {
            player = LibVlcNative.libvlc_media_player_new_from_media(media);
            if (player == IntPtr.Zero)
            {
                throw new InvalidOperationException("libVLC could not create a media player.");
            }

            LibVlcNative.libvlc_media_player_set_hwnd(player, videoHandle);
            lastNativeMuteState = null;
            audioTrackDisabledByEngine = false;
            Interlocked.Increment(ref playerGeneration);
        }
        catch
        {
            if (player != IntPtr.Zero)
            {
                LibVlcNative.libvlc_media_player_release(player);
                player = IntPtr.Zero;
            }

            if (media != IntPtr.Zero)
            {
                LibVlcNative.libvlc_media_release(media);
                media = IntPtr.Zero;
            }

            throw;
        }
    }

    private RuntimeLease AcquireRuntime(VideoRendererMode rendererMode)
    {
        var options = BuildLibVlcOptionsForRenderer(rendererMode);
        if (UsesNativeOverlay)
        {
            // VLC 3.x does not instantiate this subpicture source from a media option;
            // it must be present when the dedicated libVLC instance is created.
            options.Add(BuildOverlaySubSourceOption(NativeOverlayPipeName!, NativeOverlayPositionStatePath!));
        }

        return LibVlcRuntime.Acquire(
            vlcDirectory,
            rendererMode,
            options,
            share: LibVlcRuntime.ShouldShareRuntime(UsesNativeOverlay),
            compatibilityKey: Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH"));
    }

    private void SwitchToGdiCore(Uri mediaUri)
    {
        StopCurrentCore();
        runtimeLease?.Dispose();
        runtimeLease = null;
        instance = IntPtr.Zero;

        runtimeLease = AcquireRuntime(VideoRendererMode.Gdi);
        instance = runtimeLease.Instance;
        RendererMode = VideoRendererMode.Gdi;
        currentMediaUri = mediaUri;
        desiredPaused = false;
        CreatePlayerCore(mediaUri);
        _ = ApplyAudioCore();
    }

    private void ScheduleVideoOutputRebind(int version)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(75).ConfigureAwait(false);
                await RebindVideoOutputForVersionAsync(version).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "libVLC", "Failed to recreate libVLC video output for a moved video surface.", ex);
            }
        });
    }

    private async Task RebindVideoOutputForVersionAsync(int version)
    {
        Uri mediaUri;
        IntPtr reboundPlayer;
        long reboundPlayerGeneration;
        long restorePositionMilliseconds = 0;
        var restorePosition = false;
        lock (nativeGate)
        {
            if (disposed ||
                player == IntPtr.Zero ||
                videoHandle == IntPtr.Zero ||
                currentMediaUri is not { } activeMediaUri ||
                version != Volatile.Read(ref videoOutputVersion))
            {
                return;
            }

            mediaUri = activeMediaUri;

            restorePosition = LibVlcNative.libvlc_media_player_is_seekable(player) != 0;
            if (restorePosition)
            {
                restorePositionMilliseconds = Math.Max(0, LibVlcNative.libvlc_media_player_get_time(player));
            }

            StopCurrentCore(clearCurrentMedia: false);
            CreatePlayerCore(mediaUri);
            _ = ApplyAudioCore();

            var result = LibVlcNative.libvlc_media_player_play(player);
            if (result != 0)
            {
                StopCurrentCore();
                throw new InvalidOperationException("libVLC failed to restart playback after the video surface moved.");
            }

            LibVlcNative.libvlc_media_player_set_pause(player, desiredPaused ? 1 : 0);

            if (!ApplyAudioCore())
            {
                ScheduleAudioStateConvergence();
            }

            reboundPlayer = player;
            reboundPlayerGeneration = Volatile.Read(ref playerGeneration);
        }

        if (restorePosition)
        {
            var deadline = Stopwatch.StartNew();
            var restored = false;
            while (deadline.Elapsed < VideoOutputRebindReadinessTimeout)
            {
                await Task.Delay(VideoOutputRebindPollInterval).ConfigureAwait(false);
                lock (nativeGate)
                {
                    if (disposed ||
                        version != Volatile.Read(ref videoOutputVersion) ||
                        player != reboundPlayer ||
                        reboundPlayerGeneration != Volatile.Read(ref playerGeneration) ||
                        !Equals(currentMediaUri, mediaUri))
                    {
                        return;
                    }

                    if (LibVlcNative.libvlc_media_player_is_seekable(player) == 0)
                    {
                        continue;
                    }

                    var length = LibVlcNative.libvlc_media_player_get_length(player);
                    var target = length > 0
                        ? Math.Min(restorePositionMilliseconds, length)
                        : restorePositionMilliseconds;
                    LibVlcNative.libvlc_media_player_set_time(player, target);
                    LibVlcNative.libvlc_media_player_set_pause(player, desiredPaused ? 1 : 0);
                    if (!ApplyAudioCore())
                    {
                        ScheduleAudioStateConvergence();
                    }
                    restored = true;
                }

                if (restored)
                {
                    break;
                }
            }

            if (!restored)
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "libVLC",
                    "The rebound video output did not become seekable within five seconds; playback continued without a position restore.");
            }
        }

        lock (nativeGate)
        {
            if (disposed ||
                version != Volatile.Read(ref videoOutputVersion) ||
                player != reboundPlayer ||
                reboundPlayerGeneration != Volatile.Read(ref playerGeneration) ||
                !Equals(currentMediaUri, mediaUri))
            {
                return;
            }

            // Pause and audio requests made during readiness polling take precedence over the
            // state captured before the player was recreated.
            LibVlcNative.libvlc_media_player_set_pause(player, desiredPaused ? 1 : 0);
            if (!ApplyAudioCore())
            {
                ScheduleAudioStateConvergence();
            }
        }

        VideoOutputRebound?.Invoke(this, EventArgs.Empty);
    }

    private bool ApplyAudioCore()
    {
        if (player == IntPtr.Zero)
        {
            return true;
        }

        var request = audioStateController.Snapshot;
        var audioState = request.AudioState;
        var version = request.Version;
        if (audioTrackSelectionUnavailable)
        {
            return ApplyVolumeOnlyAudioFallbackCore(audioState, version);
        }

        try
        {
            return ApplyAudioTrackCore(audioState, version);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            audioTrackSelectionUnavailable = true;
            logger.Write(AppLogLevel.Warning, "libVLC", "libVLC audio track selection is unavailable; falling back to volume-only audio control.", ex);
            return ApplyVolumeOnlyAudioFallbackCore(audioState, version);
        }
    }

    private bool ApplyImmediateAudioCore(PlaybackAudioState audioState, int version)
    {
        if (player == IntPtr.Zero)
        {
            return true;
        }

        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        if (audioTrackSelectionUnavailable)
        {
            return ApplyVolumeOnlyAudioFallbackCore(audioState, version);
        }

        try
        {
            return audioState switch
            {
                PlaybackAudioState.Audible => ApplyAudibleImmediateCore(version),
                PlaybackAudioState.Muted => ApplyMutedAudioTrackCore(version, PlaybackAudioState.Muted),
                PlaybackAudioState.HardMuted => ApplyMutedAudioTrackCore(version, PlaybackAudioState.HardMuted),
                _ => ApplyAudibleImmediateCore(version)
            };
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            audioTrackSelectionUnavailable = true;
            logger.Write(AppLogLevel.Warning, "libVLC", "libVLC audio track selection is unavailable; falling back to volume-only audio control.", ex);
            return ApplyVolumeOnlyAudioFallbackCore(audioState, version);
        }
    }

    private bool ApplyAudioTrackCore(PlaybackAudioState audioState, int version)
    {
        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        return audioState switch
        {
            PlaybackAudioState.Audible => ApplyAudibleAudioTrackCore(version),
            PlaybackAudioState.Muted => ApplyMutedAudioTrackCore(version, PlaybackAudioState.Muted),
            PlaybackAudioState.HardMuted => ApplyMutedAudioTrackCore(version, PlaybackAudioState.HardMuted),
            _ => ApplyAudibleAudioTrackCore(version)
        };
    }

    private bool ApplyMutedAudioTrackCore(int version, PlaybackAudioState audioState)
    {
        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        var volumeZeroApplied = TrySetVolumeCore(0);
        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        var nativeMuteApplied = TrySetNativeMuteCore(muted: true, force: true);
        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        var audioTrackDisabled = EnsureAudioTrackDisabledCore(version, audioState);
        return volumeZeroApplied && nativeMuteApplied && audioTrackDisabled;
    }

    private bool EnsureAudioTrackDisabledCore(int version, PlaybackAudioState audioState)
    {
        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        var currentTrack = LibVlcNative.libvlc_audio_get_track(player);

        if (currentTrack >= 0)
        {
            lastEnabledAudioTrackId = currentTrack;
            if (!IsAudioRequestCurrent(version, audioState))
            {
                return false;
            }

            var audioTrackDisabled = LibVlcNative.libvlc_audio_set_track(player, -1) == 0;
            if (audioTrackDisabled)
            {
                audioTrackDisabledByEngine = true;
            }

            return audioTrackDisabled;
        }

        var audioTrackExists = GetFirstAudioTrackIdCore(player) is not null;
        if (audioTrackExists && IsAudioRequestCurrent(version, audioState))
        {
            audioTrackDisabledByEngine = true;
        }

        return audioTrackExists;
    }

    private bool ApplyAudibleImmediateCore(int version)
    {
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        var targetVolume = audioStateController.Snapshot.Volume;
        var nativeMuteCleared = TrySetNativeMuteCore(muted: false, force: true);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        var volumeRestored = TrySetVolumeCore(targetVolume);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        if (!audioTrackDisabledByEngine)
        {
            return nativeMuteCleared && volumeRestored;
        }

        var audioTrackEnabled = EnsureAudioTrackEnabledCore(version);
        if (audioTrackEnabled)
        {
            nativeMuteCleared = TrySetNativeMuteCore(muted: false, force: true);
            volumeRestored = TrySetVolumeCore(targetVolume);
        }

        return nativeMuteCleared && volumeRestored && audioTrackEnabled;
    }

    private bool ApplyAudibleAudioTrackCore(int version)
    {
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        var targetVolume = audioStateController.Snapshot.Volume;
        var nativeMuteCleared = TrySetNativeMuteCore(muted: false, force: true);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        var volumeRestored = TrySetVolumeCore(targetVolume);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        var audioTrackEnabled = EnsureAudioTrackEnabledCore(version);
        if (audioTrackEnabled)
        {
            nativeMuteCleared = TrySetNativeMuteCore(muted: false, force: true);
            volumeRestored = TrySetVolumeCore(targetVolume);
        }

        return nativeMuteCleared && volumeRestored && audioTrackEnabled;
    }

    private bool ApplyVolumeOnlyAudioFallbackCore(PlaybackAudioState audioState, int version)
    {
        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        return audioState switch
        {
            PlaybackAudioState.Audible => ApplyAudibleVolumeOnlyFallbackCore(version),
            PlaybackAudioState.Muted => ApplyMutedVolumeOnlyFallbackCore(version, PlaybackAudioState.Muted),
            PlaybackAudioState.HardMuted => ApplyMutedVolumeOnlyFallbackCore(version, PlaybackAudioState.HardMuted),
            _ => ApplyAudibleVolumeOnlyFallbackCore(version)
        };
    }

    private bool ApplyAudibleVolumeOnlyFallbackCore(int version)
    {
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        var nativeMuteCleared = TrySetNativeMuteCore(muted: false, force: true);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        var volumeRestored = TrySetVolumeCore(audioStateController.Snapshot.Volume);
        return nativeMuteCleared && volumeRestored;
    }

    private bool ApplyMutedVolumeOnlyFallbackCore(int version, PlaybackAudioState audioState)
    {
        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        var volumeZeroApplied = TrySetVolumeCore(0);
        if (!IsAudioRequestCurrent(version, audioState))
        {
            return false;
        }

        var nativeMuteApplied = TrySetNativeMuteCore(muted: true, force: true);
        return volumeZeroApplied && nativeMuteApplied;
    }

    private bool TrySetVolumeCore(int volume)
    {
        return LibVlcNative.libvlc_audio_set_volume(player, volume) == 0;
    }

    private bool TrySetNativeMuteCore(bool muted, bool force = false)
    {
        if (audioMuteUnavailable)
        {
            return true;
        }

        if (!force && lastNativeMuteState == muted)
        {
            return true;
        }

        try
        {
            LibVlcNative.libvlc_audio_set_mute(player, muted ? 1 : 0);
            lastNativeMuteState = muted;
            return true;
        }
        catch (EntryPointNotFoundException ex)
        {
            audioMuteUnavailable = true;
            logger.Write(AppLogLevel.Warning, "libVLC", "libVLC native mute is unavailable; relying on volume and audio-track selection for mute.", ex);
            return true;
        }
    }

    private bool IsAudioRequestCurrent(int version, PlaybackAudioState audioState)
    {
        return audioStateController.IsCurrent(version, audioState);
    }

    private static bool ShouldScheduleAudioStateConvergence(PlaybackAudioState audioState, bool applied)
    {
        return !applied || audioState == PlaybackAudioState.Audible;
    }

    private bool EnsureAudioTrackEnabledCore(int version)
    {
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        var currentTrack = LibVlcNative.libvlc_audio_get_track(player);
        if (currentTrack >= 0)
        {
            lastEnabledAudioTrackId = currentTrack;
            audioTrackDisabledByEngine = false;
            return true;
        }

        audioTrackDisabledByEngine = true;
        var targetTrack = lastEnabledAudioTrackId ?? GetFirstAudioTrackIdCore(player);
        if (targetTrack is null)
        {
            return false;
        }

        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Audible))
        {
            return false;
        }

        if (LibVlcNative.libvlc_audio_set_track(player, targetTrack.Value) == 0)
        {
            lastEnabledAudioTrackId = targetTrack.Value;
            var enabled = LibVlcNative.libvlc_audio_get_track(player) >= 0;
            if (enabled)
            {
                audioTrackDisabledByEngine = false;
            }

            return enabled;
        }

        return false;
    }

    private static int? GetFirstAudioTrackIdCore(IntPtr player)
    {
        var description = LibVlcNative.libvlc_audio_get_track_description(player);
        if (description == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var current = description;
            while (current != IntPtr.Zero)
            {
                var track = Marshal.PtrToStructure<LibVlcNative.TrackDescription>(current);
                if (track.Id >= 0)
                {
                    return track.Id;
                }

                current = track.Next;
            }

            return null;
        }
        finally
        {
            LibVlcNative.libvlc_track_description_list_release(description);
        }
    }

    private static Task RunBlockingNativeAsync(Action action, CancellationToken cancellationToken = default)
    {
        return Task.Factory.StartNew(
            action,
            cancellationToken,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    internal static List<string> BuildLibVlcOptions()
    {
        return BuildLibVlcOptionsForRenderer(VideoRendererMode.Gdi);
    }

    internal static List<string> BuildLibVlcOptionsForRenderer(VideoRendererMode rendererMode)
    {
        return
        [
            "--no-video-title-show",
            "--quiet",
            $"--vout={LibVlcRendererSelection.GetVoutOption(rendererMode)}",
            "--avcodec-hw=any",
            "--network-caching=500",
            "--live-caching=300",
            "--drop-late-frames",
            "--skip-frames"
        ];
    }

    private void ScheduleAudioStateConvergence()
    {
        ScheduleAudioStateConvergence(audioStateController.Snapshot.Version);
    }

    private void ScheduleAudioStateConvergence(int version)
    {
        var audioState = audioStateController.Snapshot.AudioState;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delay in AudioStateConvergenceDelays)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    var result = TryApplyAudioForVersion(version, audioState);
                    if (result != AudioApplyResult.Stale)
                    {
                        AudioStateReapplied?.Invoke(this, EventArgs.Empty);
                    }

                    if (ShouldStopAudioStateConvergence(audioState, result))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Warning, "libVLC", "Audio state convergence failed.", ex);
            }
        });
    }

    private AudioApplyResult TryApplyAudioForVersion(int version, PlaybackAudioState audioState)
    {
        lock (nativeGate)
        {
            if (disposed ||
                player == IntPtr.Zero ||
                !audioStateController.IsCurrent(version, audioState))
            {
                return AudioApplyResult.Stale;
            }

            var applied = ApplyAudioCore();
            if (!IsAudioRequestCurrent(version, audioState))
            {
                return AudioApplyResult.Stale;
            }

            return applied ? AudioApplyResult.Converged : AudioApplyResult.Pending;
        }
    }

    internal static bool ShouldStopAudioStateConvergence(PlaybackAudioState audioState, AudioApplyResult result)
    {
        return result == AudioApplyResult.Stale ||
            (result == AudioApplyResult.Converged && audioState != PlaybackAudioState.Audible);
    }

    internal enum AudioApplyResult
    {
        Pending,
        Converged,
        Stale
    }

    private static string BuildPluginPath(string vlcDirectory, string overlayPluginRoot, string? existingPluginPath)
    {
        var paths = new List<string>
        {
            overlayPluginRoot
        };

        if (!string.IsNullOrWhiteSpace(existingPluginPath))
        {
            paths.AddRange(existingPluginPath.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // VLC_PLUGIN_PATH replaces VLC's default search path. Keep the bundled overlay
        // directory first so its module wins, but retain the installed VLC plugins as
        // well; the sub-source loader and the selected video output may depend on the
        // normal module tree being present in the same libVLC instance.
        paths.Add(Path.Combine(vlcDirectory, "plugins"));
        return string.Join(Path.PathSeparator, paths.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildOverlaySubSourceOption(string pipeName, string positionStatePath)
    {
        var optionPositionStatePath = positionStatePath.Replace('\\', '/');
        return $"--sub-source=myoverlay{{pipe={pipeName},position-state-path={optionPositionStatePath},show-placeholder={NativeOverlayShowPlaceholder}}}";
    }

    private void SetVlcEnvironmentVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);

        var result = LibVlcNative.putenv_s(name, value);
        if (result != 0)
        {
            logger.Write(AppLogLevel.Warning, "libVLC", $"Failed to set VLC C runtime environment variable {name}; native VLC plugins may not load.");
        }
    }

}
