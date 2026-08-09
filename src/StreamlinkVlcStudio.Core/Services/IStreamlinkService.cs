using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public interface IStreamlinkService
{
    Task<StreamlinkProbeResult> ProbeStreamsAsync(StreamTransportRequest request, CancellationToken cancellationToken = default);
    Task<StreamlinkResolvedUrl> ResolveStreamUrlAsync(StreamTransportRequest request, CancellationToken cancellationToken = default);
    Task<IStreamTransportSession> StartExternalHttpAsync(StreamTransportRequest request, CancellationToken cancellationToken = default);
}

public sealed record StreamlinkProbeResult(bool HasPlayableStream, string Message);

public sealed record StreamlinkResolvedUrl(Uri StreamUri, string Message);

public interface IStreamTransportSession : IAsyncDisposable
{
    Uri PlaybackUri { get; }
    event EventHandler<string>? LogLineReceived;
}
