using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed partial class StreamTabViewModel
{
    private readonly LivePlaybackHealthMonitor liveHealthMonitor = new();
    private System.Threading.Timer? liveHealthTimer;
    private CancellationTokenSource? liveRecoveryCancellation;
    private StreamTransportRequest? liveRecoveryRequest;
    private long liveHealthVersion;
    private int liveHealthTickQueued;
    private int liveRecoveryFailures;
    private long nextLiveRecoveryAt;
    private bool isRecoveringLivePlayback;
    private bool liveRecoveryInterrupted;
    private bool liveRecoveryPending;
    private long? liveRecoveryStableSince;

    public bool IsRecoveringLivePlayback
    {
        get => isRecoveringLivePlayback;
        private set => SetProperty(ref isRecoveringLivePlayback, value);
    }

    private void StartLivePlaybackMonitoring()
    {
        if (Target.Kind != StreamTargetKind.Live || liveHealthTimer is not null) return;
        var version = ++liveHealthVersion;
        liveHealthTimer = new System.Threading.Timer(ignored =>
        {
            if (Interlocked.CompareExchange(ref liveHealthTickQueued, 1, 0) != 0) return;
            try { dispatch(() => _ = ObserveLivePlaybackAsync(version, Environment.TickCount64)); }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref liveHealthTickQueued, 0);
                logger.Write(AppLogLevel.Warning, "Playback", $"Could not check playback for {Target.DisplayName}.", ex);
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopLivePlaybackMonitoring()
    {
        ++liveHealthVersion;
        liveHealthTimer?.Dispose();
        liveHealthTimer = null;
        CancelLivePlaybackRecovery();
        liveRecoveryRequest = null;
        liveHealthMonitor.Reset();
        liveRecoveryFailures = 0;
        liveRecoveryStableSince = null;
        nextLiveRecoveryAt = 0;
        IsRecoveringLivePlayback = false;
        liveRecoveryPending = false;
    }

    private void CancelLivePlaybackRecovery()
    {
        liveRecoveryInterrupted = true;
        liveRecoveryCancellation?.Cancel();
    }

    // Also used by deterministic tests to sample the real tab lifecycle without sleeping.
    internal Task CheckLivePlaybackHealthAsync(long now) => ObserveLivePlaybackAsync(liveHealthVersion, now);

    private bool ShouldMonitorLivePlayback => !disposed && !IsReplayMode && !IsBehindLive &&
        !IsBackgroundResourceServicesSuspended && !livePlaybackConnectionSuspended &&
        liveRecoveryRequest is not null && playbackEngine is not null &&
        (Status == PlaybackStatus.Playing || (IsRecoveringLivePlayback && Status == PlaybackStatus.Starting));

    private async Task ObserveLivePlaybackAsync(long version, long now)
    {
        var lifecycleAcquired = false;
        var transitionAcquired = false;
        try
        {
            if (version != liveHealthVersion || !ShouldMonitorLivePlayback || IsBusy)
            {
                liveHealthMonitor.Reset();
                return;
            }
            string? reason = null;
            if (!liveRecoveryPending)
            {
                if (!playbackEngine!.TryGetPlaybackHealth(out var sample)) return;
                reason = liveHealthMonitor.Observe(sample, now, IsAudioOnlyQuality);
                if (reason is null)
                {
                    if (liveRecoveryStableSince is { } since && now - since >= 60_000)
                    {
                        liveRecoveryFailures = 0;
                        liveRecoveryStableSince = null;
                    }
                    return;
                }
            }
            if (now < nextLiveRecoveryAt) return;

            // Respect start/stop/seek/pause operations; never queue recovery behind an old state.
            lifecycleAcquired = await lifecycleGate.WaitAsync(0);
            if (!lifecycleAcquired) return;
            transitionAcquired = await playbackTransitionGate.WaitAsync(0);
            if (!transitionAcquired || version != liveHealthVersion || !ShouldMonitorLivePlayback) return;

            await RecoverLivePlaybackAsync(reason ?? "retrying interrupted live playback", now);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Error, "Playback", $"Playback health check failed for {Target.DisplayName}.", ex);
        }
        finally
        {
            if (transitionAcquired) playbackTransitionGate.Release();
            if (lifecycleAcquired) lifecycleGate.Release();
            Interlocked.Exchange(ref liveHealthTickQueued, 0);
        }
    }

    private bool IsAudioOnlyQuality => string.Equals(Quality, "audio_only", StringComparison.OrdinalIgnoreCase);

    private async Task RecoverLivePlaybackAsync(string reason, long now)
    {
        var engine = playbackEngine!;
        var request = liveRecoveryRequest!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        liveRecoveryCancellation = cancellation;
        liveRecoveryInterrupted = false;
        liveRecoveryStableSince = null;
        cancellation.CancelAfter(TimeSpan.FromSeconds(75));
        IsRecoveringLivePlayback = true;
        liveRecoveryPending = true;
        IsBusy = true;
        Status = PlaybackStatus.Starting;
        ErrorMessage = "Live playback was interrupted. Reconnecting…";
        logger.Write(AppLogLevel.Warning, "Playback", $"Recovering {Target.DisplayName}: {reason}.");
        try
        {
            // Resolve again, since the old process may still be listening with an expired
            // playlist or may have exited. Keep the tab, chat, volume and mute preferences.
            await StopStreamSessionAsync();
            CancelReplayInputPreparation();
            await engine.StopAsync(cancellation.Token);
            var session = await streamlinkService.StartExternalHttpAsync(request, cancellation.Token);
            streamSession = session;
            session.LogLineReceived += StreamSessionOnLogLineReceived;
            cancellation.Token.ThrowIfCancellationRequested();
            await engine.PlayAsync(session.PlaybackUri, Volume, CurrentAudioState, cancellation.Token);
            await WaitForLiveOutputAsync(engine, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            ApplyAudio();
            ErrorMessage = "";
            Status = PlaybackStatus.Playing;
            IsRecoveringLivePlayback = false;
            liveRecoveryPending = false;
            QueueCachedReplayInputPreparation();
            liveHealthMonitor.Reset();
            // A few frames followed by another EOF must still back off. Reset the failure
            // history only after a later period of stable output.
            liveRecoveryFailures++;
            liveRecoveryStableSince = Environment.TickCount64;
            nextLiveRecoveryAt = Environment.TickCount64 + (long)LivePlaybackHealthMonitor.RetryDelay(liveRecoveryFailures).TotalMilliseconds;
            logger.Write(AppLogLevel.Info, "Playback", $"Recovered {Target.DisplayName}; VLC output is advancing.");
        }
        catch (Exception ex)
        {
            await StopStreamSessionAsync();
            await engine.StopAsync(CancellationToken.None);
            if (liveRecoveryInterrupted || lifetimeCancellation.IsCancellationRequested)
            {
                // A user transition can now acquire the gate and pause/stop/replace the tab.
                Status = PlaybackStatus.Playing;
                ErrorMessage = "";
                IsRecoveringLivePlayback = false;
                liveHealthMonitor.Reset();
                return;
            }
            liveRecoveryFailures++;
            var delay = LivePlaybackHealthMonitor.RetryDelay(liveRecoveryFailures);
            nextLiveRecoveryAt = Math.Max(now, Environment.TickCount64) + (long)delay.TotalMilliseconds;
            ErrorMessage = $"Live playback is unavailable. Retrying in {delay.TotalSeconds:0} seconds…";
            logger.Write(AppLogLevel.Warning, "Playback", $"Recovery failed for {Target.DisplayName}; retry in {delay.TotalSeconds:0}s.", ex);
        }
        finally
        {
            liveRecoveryCancellation = null;
            IsBusy = false;
        }
    }

    private async Task WaitForLiveOutputAsync(StreamlinkVlcStudio.Core.Services.IPlaybackEngine engine, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 30_000;
        PlaybackHealth? previous = null;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.TryGetPlaybackHealth(out var sample))
            {
                if (sample.State is PlaybackEngineState.Ended or PlaybackEngineState.Error or PlaybackEngineState.Stopped)
                    throw new InvalidOperationException($"VLC entered {sample.State} before live output resumed.");
                if (previous is { } before && LivePlaybackHealthMonitor.HasOutputProgress(before, sample, IsAudioOnlyQuality)) return;
                previous = sample;
            }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("VLC did not produce advancing live output within 30 seconds.");
    }
}
