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
    Task<PlaybackMediaSource> PrepareAsync(Uri mediaUri, Version? libVlcVersion, CancellationToken cancellationToken);
}

/// <summary>
/// The URI libVLC opens plus whatever keeps that URI alive. The playback engine owns the source
/// for as long as the media may be (re)opened and disposes it when the media is released.
/// </summary>
internal sealed class PlaybackMediaSource : IDisposable
{
    private IDisposable? lease;

    internal PlaybackMediaSource(Uri playbackUri, IDisposable? lease)
    {
        PlaybackUri = playbackUri ?? throw new ArgumentNullException(nameof(playbackUri));
        this.lease = lease;
    }

    internal Uri PlaybackUri { get; }

    /// <summary>A source that plays <paramref name="mediaUri"/> exactly as given.</summary>
    internal static PlaybackMediaSource Direct(Uri mediaUri) => new(mediaUri, lease: null);

    public void Dispose() => Interlocked.Exchange(ref lease, null)?.Dispose();
}
