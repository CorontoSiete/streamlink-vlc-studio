namespace StreamlinkVlcStudio.Core.Models;

public enum PlaybackEngineState
{
    Idle, Opening, Buffering, Playing, Paused, Stopped, Ended, Error
}

/// <summary>A sample from the decoder, independent of the UI's requested playback state.</summary>
public readonly record struct PlaybackHealth(
    long Generation, PlaybackEngineState State, long PositionMilliseconds,
    int DecodedVideo, int DisplayedPictures, int DecodedAudio);
