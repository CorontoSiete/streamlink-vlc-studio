using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
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

    public IPlaybackEngine Create(string vlcDirectory, bool enableNativeOverlay = true, string? nativeOverlayPositionStatePath = null) =>
        new LibVlcPlaybackEngine(vlcDirectory, chatSettings, logger, enableNativeOverlay, nativeOverlayPositionStatePath);
}

public sealed class LibVlcPlaybackEngine : IPlaybackEngine
{
    private const int NativeOverlayShowPlaceholder = 0;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
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
    private readonly object nativeGate = new();
    private IntPtr instance;
    private IntPtr player;
    private IntPtr media;
    private IntPtr videoHandle;
    private Uri? currentMediaUri;
    private int desiredVolume = 80;
    private int desiredAudioState = (int)PlaybackAudioState.Audible;
    private bool desiredPaused;
    private int? lastEnabledAudioTrackId;
    private int audioStateVersion;
    private int videoOutputVersion;
    private bool audioTrackSelectionUnavailable;
    private bool audioMuteUnavailable;
    private bool audioTrackDisabledByEngine;
    private bool? lastNativeMuteState;
    private string overlayText = "";
    private bool overlayVisible;
    private double overlayOpacity = 0.92;
    private double overlayFontSize = 13;
    private bool overlayUnavailable;
    private bool disposed;

    public LibVlcPlaybackEngine(
        string vlcDirectory,
        ChatSettings chatSettings,
        IAppLogger logger,
        bool enableNativeOverlay = true,
        string? nativeOverlayPositionStatePath = null)
    {
        this.logger = logger;

        if (string.IsNullOrWhiteSpace(vlcDirectory) || !File.Exists(Path.Combine(vlcDirectory, "libvlc.dll")))
        {
            throw new FileNotFoundException("libvlc.dll was not found. Configure the VLC installation directory.", Path.Combine(vlcDirectory ?? "", "libvlc.dll"));
        }

        var nativeOverlay = enableNativeOverlay
            ? VlcOverlayPluginRuntimeFactory.TryPrepare(vlcDirectory, chatSettings.VlcOverlayDirectory, logger)
            : null;
        if (nativeOverlay is not null)
        {
            UsesNativeOverlay = true;
            NativeOverlayDirectory = nativeOverlay.OverlayDirectory;
            NativeOverlayPipeName = $"svs_{Guid.NewGuid():N}";
            NativeOverlayPositionStatePath = string.IsNullOrWhiteSpace(nativeOverlayPositionStatePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "StreamlinkVlcStudio",
                    "vlc-overlays",
                    $"{NativeOverlayPipeName}.txt")
                : nativeOverlayPositionStatePath;

            Directory.CreateDirectory(Path.GetDirectoryName(NativeOverlayPositionStatePath)!);
            SetVlcEnvironmentVariable(
                "VLC_PLUGIN_PATH",
                BuildPluginPath(vlcDirectory, nativeOverlay.PluginRoot, Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")));
            logger.Write(
                AppLogLevel.Info,
                "VlcOverlay",
                $"Native VLC overlay startup plugin={nativeOverlay.PluginPath} pluginSha256={nativeOverlay.PluginSha256} controller={nativeOverlay.ControllerPath} controllerSha256={nativeOverlay.ControllerSha256} pipe={NativeOverlayPipeName} show-placeholder={NativeOverlayShowPlaceholder} pluginRoot={nativeOverlay.PluginRoot} overlay={nativeOverlay.OverlayDirectory}.");
        }

        LibVlcNative.SetDllDirectory(vlcDirectory);
        var options = BuildLibVlcOptions(vlcDirectory, logger);

        if (UsesNativeOverlay)
        {
            options.Add(BuildOverlaySubSourceOption(NativeOverlayPipeName!, NativeOverlayPositionStatePath!));
        }

        instance = LibVlcNative.libvlc_new(options.Count, options.ToArray());
        if (instance == IntPtr.Zero)
        {
            throw new InvalidOperationException("libVLC failed to initialize.");
        }
    }

    public bool UsesNativeOverlay { get; }
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
        var shouldRebindVideoOutput = false;

        if (disposed || player == IntPtr.Zero || !Monitor.TryEnter(nativeGate))
        {
            return;
        }

        try
        {
            if (!disposed && player != IntPtr.Zero)
            {
                LibVlcNative.libvlc_media_player_set_hwnd(player, videoHandle);
                if (handleChanged &&
                    previousHandle != IntPtr.Zero &&
                    videoHandle != IntPtr.Zero)
                {
                    shouldRebindVideoOutput = MoveVideoOutputWindows(previousHandle, videoHandle) == VideoOutputWindowMoveResult.Failed &&
                        currentMediaUri is not null;
                }
            }
        }
        finally
        {
            Monitor.Exit(nativeGate);
        }

        if (shouldRebindVideoOutput)
        {
            logger.Write(
                AppLogLevel.Warning,
                "libVLC",
                "Falling back to recreating libVLC video output because the active video window could not be moved to the new surface.");
            ScheduleVideoOutputRebind(rebindVersion);
        }
    }

    public Task PlayAsync(Uri mediaUri, int volume, PlaybackAudioState audioState, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        StoreDesiredAudioState(volume, audioState);
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
                if (result != 0)
                {
                    throw new InvalidOperationException("libVLC failed to start playback.");
                }

                logger.Write(AppLogLevel.Info, "libVLC", $"Playing {mediaUri}");
                if (!ApplyAudioCore())
                {
                    ScheduleAudioStateConvergence();
                }

                ApplyOverlayCore();
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
        var requestVersion = StoreDesiredAudioState(volume, audioState);
        var scheduleConvergence = false;
        lock (nativeGate)
        {
            if (!disposed &&
                player != IntPtr.Zero &&
                IsAudioRequestCurrent(requestVersion, audioState))
            {
                var applied = ApplyImmediateAudioCore(audioState, requestVersion);
                scheduleConvergence =
                    IsAudioRequestCurrent(requestVersion, audioState) &&
                    ShouldScheduleAudioStateConvergence(audioState, applied);
            }
        }

        if (scheduleConvergence)
        {
            ScheduleAudioStateConvergence(requestVersion);
        }
    }

    public void SetOverlayText(string? text, bool visible, double opacity, double fontSize)
    {
        overlayText = text ?? "";
        overlayVisible = visible && !string.IsNullOrWhiteSpace(overlayText);
        overlayOpacity = Math.Clamp(opacity, 0, 1);
        overlayFontSize = Math.Clamp(fontSize, 8, 36);
        if (disposed || !Monitor.TryEnter(nativeGate))
        {
            return;
        }

        try
        {
            if (!disposed)
            {
                ApplyOverlayCore();
            }
        }
        finally
        {
            Monitor.Exit(nativeGate);
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
            if (instance != IntPtr.Zero)
            {
                LibVlcNative.libvlc_release(instance);
                instance = IntPtr.Zero;
            }
        }
    }

    private void StopCurrentCore(bool clearCurrentMedia = true)
    {
        Interlocked.Increment(ref audioStateVersion);
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
            DisableOverlayCore();
            LibVlcNative.libvlc_media_player_stop(player);
            LibVlcNative.libvlc_media_player_release(player);
            player = IntPtr.Zero;
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

        player = LibVlcNative.libvlc_media_player_new_from_media(media);
        if (player == IntPtr.Zero)
        {
            LibVlcNative.libvlc_media_release(media);
            media = IntPtr.Zero;
            throw new InvalidOperationException("libVLC could not create a media player.");
        }

        LibVlcNative.libvlc_media_player_set_hwnd(player, videoHandle);
        lastNativeMuteState = null;
        audioTrackDisabledByEngine = false;
    }

    private void ScheduleVideoOutputRebind(int version)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(75).ConfigureAwait(false);
                RebindVideoOutputForVersion(version);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write(AppLogLevel.Warning, "libVLC", "Failed to recreate libVLC video output for a moved video surface.", ex);
            }
        });
    }

    private void RebindVideoOutputForVersion(int version)
    {
        var rebound = false;
        lock (nativeGate)
        {
            if (disposed ||
                player == IntPtr.Zero ||
                videoHandle == IntPtr.Zero ||
                currentMediaUri is not { } mediaUri ||
                version != Volatile.Read(ref videoOutputVersion))
            {
                return;
            }

            var restorePaused = desiredPaused;
            StopCurrentCore(clearCurrentMedia: false);
            CreatePlayerCore(mediaUri);
            _ = ApplyAudioCore();

            var result = LibVlcNative.libvlc_media_player_play(player);
            if (result != 0)
            {
                throw new InvalidOperationException("libVLC failed to restart playback after the video surface moved.");
            }

            if (restorePaused)
            {
                LibVlcNative.libvlc_media_player_set_pause(player, 1);
            }

            if (!ApplyAudioCore())
            {
                ScheduleAudioStateConvergence();
            }

            ApplyOverlayCore();
            rebound = true;
        }

        if (rebound)
        {
            VideoOutputRebound?.Invoke(this, EventArgs.Empty);
        }
    }

    private VideoOutputWindowMoveResult MoveVideoOutputWindows(IntPtr sourceParent, IntPtr targetParent)
    {
        var children = GetDirectChildWindows(sourceParent);
        if (children.Count == 0)
        {
            return VideoOutputWindowMoveResult.NoWindows;
        }

        var movedCount = 0;
        var failedCount = 0;
        var targetWidth = 1;
        var targetHeight = 1;
        if (GetClientRect(targetParent, out var targetRect))
        {
            targetWidth = Math.Max(1, targetRect.Right - targetRect.Left);
            targetHeight = Math.Max(1, targetRect.Bottom - targetRect.Top);
        }

        foreach (var child in children)
        {
            _ = SetParent(child, targetParent);
            if (GetParent(child) != targetParent)
            {
                failedCount++;
                logger.Write(
                    AppLogLevel.Warning,
                    "libVLC",
                    $"Failed to move VLC video child window 0x{child.ToInt64():X} to the new video surface. Win32 error {Marshal.GetLastWin32Error()}.");
                continue;
            }

            if (!SetWindowPos(
                    child,
                    IntPtr.Zero,
                    0,
                    0,
                    targetWidth,
                    targetHeight,
                    SwpNoZOrder | SwpNoActivate | SwpShowWindow))
            {
                logger.Write(
                    AppLogLevel.Warning,
                    "libVLC",
                    $"Failed to resize moved VLC video child window 0x{child.ToInt64():X}. Win32 error {Marshal.GetLastWin32Error()}.");
            }

            movedCount++;
        }

        return movedCount > 0
            ? VideoOutputWindowMoveResult.Moved
            : failedCount > 0
                ? VideoOutputWindowMoveResult.Failed
                : VideoOutputWindowMoveResult.NoWindows;
    }

    private static List<IntPtr> GetDirectChildWindows(IntPtr parent)
    {
        var children = new List<IntPtr>();
        EnumChildWindows(
            parent,
            (child, _) =>
            {
                if (GetParent(child) == parent)
                {
                    children.Add(child);
                }

                return true;
            },
            IntPtr.Zero);
        return children;
    }

    private bool ApplyAudioCore()
    {
        if (player == IntPtr.Zero)
        {
            return true;
        }

        var audioState = GetDesiredAudioState();
        var version = Volatile.Read(ref audioStateVersion);
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
                PlaybackAudioState.HardMuted => ApplyHardMutedAudioTrackCore(version),
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
            PlaybackAudioState.HardMuted => ApplyHardMutedAudioTrackCore(version),
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

    private bool ApplyHardMutedAudioTrackCore(int version)
    {
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.HardMuted))
        {
            return false;
        }

        var volumeZeroApplied = TrySetVolumeCore(0);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.HardMuted))
        {
            return false;
        }

        var nativeMuteApplied = TrySetNativeMuteCore(muted: true, force: true);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.HardMuted))
        {
            return false;
        }

        var audioTrackDisabled = EnsureAudioTrackDisabledCore(version, PlaybackAudioState.HardMuted);
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

        var targetVolume = Volatile.Read(ref desiredVolume);
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

        var targetVolume = Volatile.Read(ref desiredVolume);
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

    private bool ApplyVolumeOnlyAudioFallbackCore()
    {
        var audioState = GetDesiredAudioState();
        var version = Volatile.Read(ref audioStateVersion);
        return ApplyVolumeOnlyAudioFallbackCore(audioState, version);
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
            PlaybackAudioState.Muted => ApplyMutedVolumeOnlyFallbackCore(version),
            PlaybackAudioState.HardMuted => ApplyHardMutedVolumeOnlyFallbackCore(version),
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

        var volumeRestored = TrySetVolumeCore(Volatile.Read(ref desiredVolume));
        return nativeMuteCleared && volumeRestored;
    }

    private bool ApplyMutedVolumeOnlyFallbackCore(int version)
    {
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Muted))
        {
            return false;
        }

        var volumeZeroApplied = TrySetVolumeCore(0);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.Muted))
        {
            return false;
        }

        var nativeMuteApplied = TrySetNativeMuteCore(muted: true, force: true);
        return volumeZeroApplied && nativeMuteApplied;
    }

    private bool ApplyHardMutedVolumeOnlyFallbackCore(int version)
    {
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.HardMuted))
        {
            return false;
        }

        var volumeZeroApplied = TrySetVolumeCore(0);
        if (!IsAudioRequestCurrent(version, PlaybackAudioState.HardMuted))
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

    private int StoreDesiredAudioState(int volume, PlaybackAudioState audioState)
    {
        Volatile.Write(ref desiredVolume, Math.Clamp(volume, 0, 125));
        Volatile.Write(ref desiredAudioState, (int)audioState);
        return Interlocked.Increment(ref audioStateVersion);
    }

    private PlaybackAudioState GetDesiredAudioState()
    {
        return (PlaybackAudioState)Volatile.Read(ref desiredAudioState);
    }

    private bool IsAudioRequestCurrent(int version, PlaybackAudioState audioState)
    {
        return version == Volatile.Read(ref audioStateVersion) &&
            GetDesiredAudioState() == audioState;
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

    private static List<string> BuildLibVlcOptions(string vlcDirectory, IAppLogger logger)
    {
        logger.Write(AppLogLevel.Info, "libVLC", "Using Windows GDI VLC video output with automatic hardware decoding.");
        return
        [
            "--no-video-title-show",
            "--quiet",
            "--vout=wingdi",
            "--avcodec-hw=any",
            "--network-caching=500",
            "--live-caching=300",
            "--drop-late-frames",
            "--skip-frames"
        ];
    }

    private void ScheduleAudioStateConvergence()
    {
        ScheduleAudioStateConvergence(Volatile.Read(ref audioStateVersion));
    }

    private void ScheduleAudioStateConvergence(int version)
    {
        var audioState = GetDesiredAudioState();
        _ = Task.Run(async () =>
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
        });
    }

    private AudioApplyResult TryApplyAudioForVersion(int version, PlaybackAudioState audioState)
    {
        lock (nativeGate)
        {
            if (disposed ||
                player == IntPtr.Zero ||
                version != Volatile.Read(ref audioStateVersion) ||
                GetDesiredAudioState() != audioState)
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

    private enum VideoOutputWindowMoveResult
    {
        NoWindows,
        Moved,
        Failed
    }

    private void ApplyOverlayCore()
    {
        if (player == IntPtr.Zero || overlayUnavailable)
        {
            return;
        }

        try
        {
            if (!overlayVisible)
            {
                DisableOverlayCore();
                return;
            }

            LibVlcNative.libvlc_video_set_marquee_string(player, LibVlcNative.MarqueeText, overlayText);
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeColor, 0xFFFFFF);
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeOpacity, (int)Math.Round(overlayOpacity * 255));
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueePosition, LibVlcNative.PositionTopRight);
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeRefresh, 1000);
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeSize, (int)Math.Round(overlayFontSize));
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeTimeout, 0);
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeX, 24);
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeY, 22);
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeEnable, 1);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            overlayUnavailable = true;
            logger.Write(AppLogLevel.Warning, "libVLC", "libVLC native text overlay is unavailable.", ex);
        }
    }

    private void DisableOverlayCore()
    {
        if (player == IntPtr.Zero || overlayUnavailable)
        {
            return;
        }

        try
        {
            LibVlcNative.libvlc_video_set_marquee_int(player, LibVlcNative.MarqueeEnable, 0);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            overlayUnavailable = true;
            logger.Write(AppLogLevel.Warning, "libVLC", "libVLC native text overlay is unavailable.", ex);
        }
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

        paths.RemoveAll(path => string.Equals(path, Path.Combine(vlcDirectory, "plugins"), StringComparison.OrdinalIgnoreCase));
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

    private delegate bool EnumChildWindowProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parentHandle, EnumChildWindowProc callback, IntPtr lParam);

    [DllImport("user32")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr childHandle, IntPtr newParentHandle);

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}
