namespace StreamlinkVlcStudio.Core.Models;

public sealed record PlaybackClock(TimeSpan Position, TimeSpan? Duration, bool IsSeekable);
