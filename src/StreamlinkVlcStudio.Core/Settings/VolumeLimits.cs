namespace StreamlinkVlcStudio.Core.Settings;

/// <summary>
/// Shared playback volume bounds. Consolidates the 0-125 range that was previously hardcoded
/// across the playback engine, settings normalization, and the volume UI.
/// </summary>
public static class VolumeLimits
{
    public const int Min = 0;

    /// <summary>Maximum volume percentage; values above 100 apply libVLC amplification.</summary>
    public const int Max = 125;
}
