namespace StreamlinkVlcStudio.Infrastructure.Limits;

/// <summary>Central limits for data received from external processes and networks.</summary>
internal static class PayloadLimits
{
    internal const int WebSocketTextBytes = 1 * 1024 * 1024;
    internal const int HttpJsonBytes = 2 * 1024 * 1024;
    internal const int PlaylistBytes = 4 * 1024 * 1024;
    internal const int RangeProbeBytes = 64 * 1024;
    internal const int ProcessOutputBytes = 4 * 1024 * 1024;
    internal const int ProcessLineBytes = 64 * 1024;
    internal const int ReplayChatCacheBytes = 64 * 1024 * 1024;
    internal const int TwitchInboundIrcBytes = 16 * 1024;
    internal const int TwitchOutboundIrcBytes = 512;

    internal const int ImageMaximumDimension = 4096;
    internal const int ImageMaximumPixels = 16_000_000;
    internal const int ImageMaximumFrames = 300;
    internal const int ImageMaximumDecodedBytes = 64 * 1024 * 1024;
}
