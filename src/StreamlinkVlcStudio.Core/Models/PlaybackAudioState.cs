namespace StreamlinkVlcStudio.Core.Models;

public enum PlaybackAudioState
{
    Audible,
    // Automatic background-tab mute: keep the decoder running for fast switching.
    Muted,
    // Explicit user mute: the audio track may be disabled.
    HardMuted
}
