using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Services;

public interface IVodDownloadService : IAsyncDisposable
{
    string DownloadDirectory { get; }
    void SetBandwidthLimit(long bytesPerSecond);
    Task ChangeDownloadDirectoryAsync(string downloadDirectory, CancellationToken cancellationToken = default);
    event Action<VodDownloadItem>? DownloadChanged;
    Task<IReadOnlyList<VodDownloadItem>> GetDownloadsAsync(CancellationToken cancellationToken = default);
    Task<VodDownloadItem> EnqueueAsync(VodDownloadRequest request, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid id, CancellationToken cancellationToken = default);
    Task RetryAsync(Guid id, VodDownloadOptions options, CancellationToken cancellationToken = default);
    Task<StreamTarget> GetOfflineTargetAsync(Guid id, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}
