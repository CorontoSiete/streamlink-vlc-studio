using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Infrastructure.Vlc;

internal sealed class LibVlcAudioStateController
{
    private AudioStateSnapshot snapshot = new(80, PlaybackAudioState.Audible, 0);

    public AudioStateSnapshot Snapshot => Volatile.Read(ref snapshot);

    public AudioStateSnapshot Update(int volume, PlaybackAudioState audioState)
    {
        var normalizedVolume = Math.Clamp(volume, VolumeLimits.Min, VolumeLimits.Max);
        var normalizedState = Normalize(audioState);

        while (true)
        {
            var current = Snapshot;
            var updated = new AudioStateSnapshot(
                normalizedVolume,
                normalizedState,
                unchecked(current.Version + 1));
            if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, updated, current), current))
            {
                return updated;
            }
        }
    }

    public AudioStateSnapshot Invalidate()
    {
        while (true)
        {
            var current = Snapshot;
            var updated = current with { Version = unchecked(current.Version + 1) };
            if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, updated, current), current))
            {
                return updated;
            }
        }
    }

    public bool IsCurrent(int version, PlaybackAudioState audioState)
    {
        var current = Snapshot;
        return current.Version == version && current.AudioState == audioState;
    }

    private static PlaybackAudioState Normalize(PlaybackAudioState audioState)
    {
        return Enum.IsDefined(audioState) ? audioState : PlaybackAudioState.Audible;
    }

    internal sealed record AudioStateSnapshot(int Volume, PlaybackAudioState AudioState, int Version);
}
