namespace StreamlinkVlcStudio.Infrastructure.Vlc;

/// <summary>
/// Gives media a chance to be adapted before libVLC opens it, for sources a particular libVLC
/// release cannot play reliably as published (for example Twitch VODs with muted segments).
/// </summary>
internal interface IPlaybackMediaSourceGateway
{
    /// <summary>
    /// Returns the source libVLC should open for <paramref name="mediaUri"/>. Implementations fall
    /// back to the original URI instead of failing, and only throw for caller cancellation.
    /// </summary>
    /// <param name="libVlcVersion">
    /// The libVLC release that will open the media, or <see langword="null"/> when it is unknown.
    /// </param>
    /// <param name="preferFastReplay">Opt into a supported completed-replay demuxer for a timestamped open.</param>
    Task<PlaybackMediaSource> PrepareAsync(Uri mediaUri, Version? libVlcVersion, CancellationToken cancellationToken,
        bool preferFastReplay = false);
}

/// <summary>
/// The URI libVLC opens plus whatever keeps that URI alive. The playback engine owns the source
/// for as long as the media may be (re)opened and disposes it when the media is released.
/// </summary>
internal sealed class PlaybackMediaSource : IDisposable
{
    private IDisposable? lease;

    internal PlaybackMediaSource(Uri playbackUri, IDisposable? lease, TimeSpan timelineOffset = default,
        bool useAvformatDemuxer = false, TimeSpan replaySeekPreroll = default)
    {
        PlaybackUri = playbackUri ?? throw new ArgumentNullException(nameof(playbackUri));
        this.lease = lease;
        TimelineOffset = timelineOffset;
        UseAvformatDemuxer = useAvformatDemuxer;
        ReplaySeekPreroll = replaySeekPreroll;
    }

    internal Uri PlaybackUri { get; }
    internal TimeSpan TimelineOffset { get; }
    // Only a validated local transport may opt in; its lease also interrupts nested
    // FFmpeg reads during teardown. Preroll covers the playlist's longest segment.
    internal bool UseAvformatDemuxer { get; }
    internal TimeSpan ReplaySeekPreroll { get; }

    /// <summary>A source that plays <paramref name="mediaUri"/> exactly as given.</summary>
    internal static PlaybackMediaSource Direct(Uri mediaUri) => new(mediaUri, lease: null);

    public void Dispose() => Interlocked.Exchange(ref lease, null)?.Dispose();
}
