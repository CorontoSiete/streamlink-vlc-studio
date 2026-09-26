using System.Diagnostics;
using System.Runtime.InteropServices;
using StreamlinkVlcStudio.Core.Logging;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

public sealed partial class LibVlcPlaybackEngine
{
    private ReplayPreparation? replayPreparation;

    public Task PrepareReplayAsync(Uri mediaUri, CancellationToken cancellationToken = default)
    {
        lock (nativeGate)
        {
            if (disposed || cancellationToken.IsCancellationRequested || player == IntPtr.Zero || preserveReplayPause || !replayPausePluginAvailable ||
                libVlcVersion is not { Major: 3, Minor: 0, Build: 23 } || !HlsReplayTimeline.IsPlaylist(mediaUri))
                return Task.CompletedTask;
            if (replayPreparation is { } existing && existing.Uri == mediaUri && !existing.Cancellation.IsCancellationRequested)
                return existing.Task.WaitAsync(cancellationToken);

            CancelReplayPreparationCore();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // The background worker may outlive Stop/Dispose. Its own instance reference
            // keeps native creation/cleanup valid without holding the active player's lock.
            var retainedInstance = instance;
            var preparedHandle = videoHandle;
            LibVlcNative.libvlc_retain(retainedInstance);
            replayPreparation = new ReplayPreparation(mediaUri, cancellation,
                Task.Run(() => PrepareReplayInputAsync(retainedInstance, preparedHandle, mediaUri, cancellation.Token)));
            var preparation = replayPreparation;
            preparation.Registration = cancellationToken.Register(() =>
            {
                _ = Task.Run(() =>
                {
                    lock (nativeGate)
                        if (ReferenceEquals(replayPreparation, preparation)) CancelReplayPreparationCore();
                });
            });
            return replayPreparation.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task<PreparedReplayInput?> PrepareReplayInputAsync(IntPtr retainedInstance, IntPtr preparedHandle, Uri uri, CancellationToken cancellationToken)
    {
        PreparedReplayInput? input = null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            var token = budget.Token;
            input = new PreparedReplayInput(await PrepareMediaSourceAsync(uri, token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            input.Media = LibVlcNative.libvlc_media_new_location(retainedInstance, input.Source!.PlaybackUri.ToString());
            if (input.Media == IntPtr.Zero) throw new InvalidOperationException("Could not prepare replay media.");
            // No video decoder or HWND is created during preparation. Audio establishes
            // the demux timestamp reference silently, then the entire input is paused.
            LibVlcNative.libvlc_media_add_option(input.Media, ":no-video");
            LibVlcNative.libvlc_media_add_option(input.Media, ":demux-filter=studio_replay_pause");
            var eventName = $"Local\\StreamStudio.ReplayPause.{Guid.NewGuid():N}";
            input.PauseReady = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            LibVlcNative.libvlc_media_add_option(input.Media, $":studio-replay-pause-ready={eventName}");
            ConfigureReplayDecoder(input.Media, UsesNativeOverlay, Environment.ProcessorCount);
            input.Player = LibVlcNative.libvlc_media_player_new_from_media(input.Media);
            if (input.Player == IntPtr.Zero) throw new InvalidOperationException("Could not prepare a replay player.");
            input.VideoHandle = preparedHandle;
            LibVlcVideoOutputBinding.Bind(input.Player, preparedHandle, RendererMode, libVlcVersion, UsesNativeOverlay, hardwareOverlayComposition);
            LibVlcNative.libvlc_audio_set_volume(input.Player, 0);
            LibVlcNative.libvlc_audio_set_mute(input.Player, 1);
            if (LibVlcNative.libvlc_media_player_play(input.Player) != 0)
                throw new InvalidOperationException("Could not start replay preparation.");

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var state = LibVlcNative.libvlc_media_player_get_state(input.Player);
                if (state is LibVlcNative.MediaPlayerState.Error or LibVlcNative.MediaPlayerState.Ended or LibVlcNative.MediaPlayerState.Stopped)
                    throw new InvalidOperationException("The replay ended during preparation.");
                if (state == LibVlcNative.MediaPlayerState.Playing && input.PauseReady.WaitOne(0) &&
                    LibVlcNative.libvlc_media_player_get_time(input.Player) > 0 &&
                    LibVlcNative.libvlc_media_player_get_length(input.Player) > 0 &&
                    LibVlcNative.libvlc_media_player_is_seekable(input.Player) != 0 &&
                    LibVlcNative.libvlc_media_get_stats(input.Media, out var stats) != 0 && stats.DecodedAudio > 0)
                {
                    if (stats.DecodedVideo != 0 || stats.DisplayedPictures != 0)
                        throw new InvalidOperationException("Replay preparation unexpectedly enabled video.");
                    input.VideoTrack = FindPreparedVideoTrack(input.Player);
                    LibVlcNative.libvlc_media_player_set_pause(input.Player, 1);
                    break;
                }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
            while (LibVlcNative.libvlc_media_player_get_state(input.Player) != LibVlcNative.MediaPlayerState.Paused)
                await Task.Delay(25, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            logger.Write(AppLogLevel.Debug, "Replay", "Replay input prepared and paused without video output.");
            var ready = input;
            input = null;
            return ready;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Info, "Replay", "Background replay preparation was unavailable; the seek will open it normally.", ex);
            return null;
        }
        finally
        {
            input?.Dispose();
            LibVlcNative.libvlc_release(retainedInstance);
        }
    }

    private static int FindPreparedVideoTrack(IntPtr preparedPlayer)
    {
        var head = LibVlcNative.libvlc_video_get_track_description(preparedPlayer);
        try
        {
            for (var next = head; next != IntPtr.Zero;)
            {
                var track = Marshal.PtrToStructure<LibVlcNative.TrackDescription>(next);
                if (track.Id >= 0) return track.Id;
                next = track.Next;
            }
            throw new InvalidOperationException("The prepared replay has no video track.");
        }
        finally { if (head != IntPtr.Zero) LibVlcNative.libvlc_track_description_list_release(head); }
    }

    private async Task<bool> TryPlayPreparedReplayAsync(Uri uri, TimeSpan position, CancellationToken cancellationToken)
    {
        long generation = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            await RunBlockingNativeAsync(() =>
            {
                lock (nativeGate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // libVLC's public length remains frozen while paused. The adaptive
                    // playlist still refreshes, so newly appended positions are valid.
                    // Confirm the requested clock after unpausing instead of rejecting
                    // them using that stale length.
                    if (disposed || replayPreparation is not { } preparation || preparation.Cancellation.IsCancellationRequested || preparation.Uri != uri ||
                        !preparation.Task.IsCompletedSuccessfully || preparation.Task.Result is not { } input ||
                        position >= HlsReplayTimeline.RebaseThreshold ||
                        LibVlcNative.libvlc_media_player_get_time(input.Player) >= HlsReplayTimeline.RebaseThreshold.TotalMilliseconds ||
                        LibVlcNative.libvlc_media_player_get_state(input.Player) != LibVlcNative.MediaPlayerState.Paused ||
                        videoHandle == IntPtr.Zero)
                        return;

                    replayPreparation = null; // Transfer ownership before stopping the live player.
                    preparation.Registration.Dispose();
                    preparation.Cancellation.Dispose();
                    using var transferredInput = input;
                    StopCurrentCore();
                    player = input.Player; input.Player = IntPtr.Zero;
                    media = input.Media; input.Media = IntPtr.Zero;
                    currentMediaSource = input.Source; input.Source = null;
                    replayPauseReady = input.PauseReady; input.PauseReady = null;
                    currentMediaUri = currentMediaSource!.PlaybackUri;
                    originalMediaUri = uri;
                    preserveReplayPause = true;
                    desiredPaused = false;
                    replayOutputPending = true;
                    replayOpeningPosition = position;
                    generation = Interlocked.Increment(ref playerGeneration);
                    if (input.VideoHandle != videoHandle)
                        LibVlcVideoOutputBinding.Bind(player, videoHandle, RendererMode, libVlcVersion, UsesNativeOverlay, hardwareOverlayComposition);
                    GateReplayVideoCore();
                    _ = ApplyAudioCore();
                    LibVlcVideoOutputBinding.EnablePreparedVideo(player);
                    if (LibVlcNative.libvlc_video_set_track(player, input.VideoTrack) != 0)
                        throw new InvalidOperationException("Could not select the prepared replay video.");
                    LibVlcNative.libvlc_media_player_set_pause(player, 0);
                    LibVlcNative.libvlc_media_player_set_time(player, (long)Math.Round(position.TotalMilliseconds));
                }
            }, cancellationToken).ConfigureAwait(false);
            if (generation == 0) return false;
            await SeekCoreAsync(position, generation, cancellationToken, openingAtPosition: true, seekAlreadySubmitted: true).ConfigureAwait(false);
            lock (nativeGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (disposed || generation != playerGeneration)
                    throw new OperationCanceledException("The media changed while activating the prepared replay.");
                ReleaseReplayOutputCore();
            }
            logger.Write(AppLogLevel.Info, "Replay", $"Prepared replay seek confirmed in {watch.ElapsedMilliseconds} ms.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (nativeGate)
            {
                if (disposed || (generation != 0 && generation != playerGeneration))
                    throw new OperationCanceledException("The media changed while activating the prepared replay.", ex);
                if (generation != 0) StopCurrentCore();
            }
            logger.Write(AppLogLevel.Info, "Replay", "The prepared replay could not be activated; opening a fresh input.", ex);
            return false;
        }
    }

    // Called under nativeGate. Joining a canceled native input stays off that lock
    // and off the UI thread; each preparation owns its cleanup and runtime reference.
    private void CancelReplayPreparationCore()
    {
        var preparation = replayPreparation;
        replayPreparation = null;
        if (preparation is null) return;
        preparation.Registration.Dispose();
        preparation.Cancellation.Cancel();
        _ = preparation.Task.ContinueWith(completed =>
        {
            if (completed.IsCompletedSuccessfully) completed.Result?.Dispose();
            preparation.Cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private sealed record ReplayPreparation(Uri Uri, CancellationTokenSource Cancellation, Task<PreparedReplayInput?> Task)
    {
        internal CancellationTokenRegistration Registration;
    }

    private sealed class PreparedReplayInput(PlaybackMediaSource source) : IDisposable
    {
        internal IntPtr Player;
        internal IntPtr Media;
        internal IntPtr VideoHandle;
        internal PlaybackMediaSource? Source = source;
        internal EventWaitHandle? PauseReady;
        internal int VideoTrack;

        public void Dispose()
        {
            if (Player != IntPtr.Zero)
            {
                LibVlcNative.libvlc_media_player_stop(Player);
                LibVlcNative.libvlc_media_player_release(Player);
                Player = IntPtr.Zero;
            }
            if (Media != IntPtr.Zero) { LibVlcNative.libvlc_media_release(Media); Media = IntPtr.Zero; }
            PauseReady?.Dispose(); PauseReady = null;
            Source?.Dispose(); Source = null;
        }
    }
}
