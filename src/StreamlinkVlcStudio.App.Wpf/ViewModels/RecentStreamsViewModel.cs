using System.Collections.ObjectModel;
using System.Collections.Specialized;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class RecentStreamsViewModel : HomeFeatureViewModel
{
    private readonly Func<RecentStreamViewModel, bool, Task> openStream;
    private readonly Func<bool> isVisible;
    internal RecentStreamsViewModel(MainViewModelDependencies dependencies, Action<string> setStatus,
        Func<RecentStreamViewModel, bool, Task> openStream, Func<bool> isVisible) : base(dependencies, setStatus)
    {
        this.openStream = openStream;
        this.isVisible = isVisible;
        settingsService = dependencies.SettingsService;
        streamMetadataService = dependencies.StreamMetadataService;
        recentThumbnailRefreshInterval = dependencies.RecentThumbnailRefreshInterval ?? DefaultRecentThumbnailRefreshInterval;
        RebuildRecentStreams();
        RecentStreams.CollectionChanged += RecentStreamsOnCollectionChanged;
    }

    internal Task RefreshAsync(bool force = false) => disposed ? Task.CompletedTask :
        RefreshRecentThumbnailsAsync(recentThumbnailRefreshCancellation.Token, force);

    protected override void StopOperations()
    {
        CancelOperation(recentThumbnailRefreshCancellation);
        StopRefreshTimer(recentThumbnailRefreshTimerGate, ref recentThumbnailRefreshTimer);
        RecentStreams.CollectionChanged -= RecentStreamsOnCollectionChanged;
    }
    protected override void ReleaseResources()
    {
        recentThumbnailRefreshCancellation.Dispose();
        recentStreamsGate.Dispose();
        recentThumbnailRefreshGate.Dispose();
    }
    private static readonly TimeSpan DefaultRecentThumbnailRefreshInterval = TimeSpan.FromMinutes(5);
    private const int RecentMetadataConcurrency = 4;
    private readonly ISettingsService settingsService;
    private readonly IStreamMetadataService? streamMetadataService;
    private readonly TimeSpan recentThumbnailRefreshInterval;
    private readonly object recentThumbnailRefreshTimerGate = new();
    private readonly SemaphoreSlim recentStreamsGate = new(1, 1);
    private readonly SemaphoreSlim recentThumbnailRefreshGate = new(1, 1);
    private readonly CancellationTokenSource recentThumbnailRefreshCancellation = new();
    private readonly RecentStreamController recentStreamController = new();
    private Dictionary<string, StreamMetadataResult>? pendingRecentMetadata;
    private System.Threading.Timer? recentThumbnailRefreshTimer;
    public ObservableCollection<RecentStreamViewModel> RecentStreams { get; } = [];

    public bool HasRecentStreams => RecentStreams.Count > 0;

    public bool IsRecentStreamsEmptyVisible => RecentStreams.Count == 0;

    public string RecentStreamsStatus => RecentStreams.Count switch
    {
        0 => "No recent streams yet.",
        1 => "1 recent stream.",
        _ => $"{RecentStreams.Count} recent streams."
    };

    internal Task DeleteRecentStreamAsync(RecentStreamViewModel stream) => disposed ? Task.CompletedTask : Track(DeleteRecentStreamAsyncCore(stream));

    private async Task DeleteRecentStreamAsyncCore(RecentStreamViewModel stream)
    {
        if (disposed)
        {
            return;
        }

        var target = stream.Target;
        try
        {
            await recentStreamsGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                if (!Settings.RecentStreams.Any(recentStream => IsSameRecentStream(recentStream, target)))
                {
                    return;
                }

                Settings.RecentStreams = Settings.RecentStreams
                    .Where(recentStream => !IsSameRecentStream(recentStream, target))
                    .ToList();
                recentStreamController.RemoveLiveStatus(target.StateKey);
                recentStreamController.TakeHint(target.StateKey);
                pendingRecentMetadata?.Remove(target.StateKey);

                RebuildRecentStreams();
                StatusMessage = $"{target.DisplayName} removed from recent streams";
                await SaveRecentStreamRemovalAsync(target);
            }
            finally
            {
                recentStreamsGate.Release();
            }
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to remove {target.DisplayName} from recent streams.", ex);
        }
    }

    internal void SetRecentStreamHint(
        StreamTarget target,
        string thumbnailUrl,
        string displayName,
        string categoryName)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl) &&
            string.IsNullOrWhiteSpace(displayName) &&
            string.IsNullOrWhiteSpace(categoryName))
        {
            return;
        }

        recentStreamController.SetHint(
            target.StateKey,
            new RecentStreamHint(
                thumbnailUrl?.Trim() ?? "",
                displayName?.Trim() ?? "",
                categoryName?.Trim() ?? ""));
    }

    internal RecentStreamHint? TakeRecentStreamHint(StreamTarget target)
    {
        return recentStreamController.TakeHint(target.StateKey);
    }


    internal void EnsureRecentThumbnailRefreshTimerStarted()
    {
        if (streamMetadataService is not null)
            EnsureRefreshTimerStarted(recentThumbnailRefreshTimerGate, ref recentThumbnailRefreshTimer,
                recentThumbnailRefreshInterval, RefreshRecentThumbnailsOnUiIfVisible);
    }

    internal void RefreshRecentThumbnailsOnUiIfVisible()
    {
        if (disposed || streamMetadataService is null)
        {
            return;
        }

        dispatch(() =>
        {
            if (disposed || !isVisible())
            {
                return;
            }

            // Navigation can reuse fresh results; periodic polling retains its cadence.
            backgroundOperationController.Track(RefreshRecentThumbnailsAsync(recentThumbnailRefreshCancellation.Token, force: true));
        });
    }

    internal void RefreshRecentThumbnailsInBackground()
    {
        if (disposed || streamMetadataService is null)
        {
            return;
        }

        backgroundOperationController.Track(RefreshRecentThumbnailsAsync(recentThumbnailRefreshCancellation.Token));
    }

    internal Task RefreshRecentThumbnailsAsync(CancellationToken cancellationToken, bool force = false) => disposed ? Task.CompletedTask : Track(RefreshRecentThumbnailsAsyncCore(cancellationToken, force));

    private async Task RefreshRecentThumbnailsAsyncCore(CancellationToken cancellationToken, bool force = false)
    {
        if (streamMetadataService is null || Settings.RecentStreams.Count == 0)
        {
            return;
        }

        try
        {
            if (!await recentThumbnailRefreshGate.WaitAsync(0, cancellationToken))
            {
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = Settings.RecentStreams
                .Select(stream => new StreamTarget(stream.Platform, stream.Channel, stream.Url, CategoryName: stream.CategoryName))
                .DistinctBy(target => target.StateKey, StringComparer.OrdinalIgnoreCase)
                .Where(target => force || !recentStreamController.IsMetadataFresh(target.StateKey, now, recentThumbnailRefreshInterval))
                .ToArray();
            if (snapshot.Length == 0)
            {
                return;
            }

            var metadataByStream = new Dictionary<string, StreamMetadataResult>(StringComparer.OrdinalIgnoreCase);
            pendingRecentMetadata = metadataByStream;
            await MarkRecentStreamsCheckingAsync(snapshot, cancellationToken);

            // A fixed worker count bounds both provider requests and queued tasks
            // even when the user's Recent history has grown large.
            var nextIndex = -1;
            await Task.WhenAll(Enumerable.Range(0, Math.Min(RecentMetadataConcurrency, snapshot.Length)).Select(async _ =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= snapshot.Length)
                    {
                        return;
                    }

                    var metadata = await GetRecentStreamMetadataAsync(snapshot[index], cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (metadata is not null)
                    {
                        await ApplyRecentStreamMetadataAsync(snapshot[index], metadata, cancellationToken);
                    }
                }
            }));
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed || metadataByStream.Count == 0)
            {
                return;
            }

            await recentStreamsGate.WaitAsync(cancellationToken);
            try
            {
                if (disposed) return;
                var settingsChanged = false;
                var updated = new List<RecentStreamSettings>(Settings.RecentStreams.Count);
                foreach (var stream in Settings.RecentStreams)
                {
                    var key = new StreamTarget(stream.Platform, stream.Channel, stream.Url).StateKey;
                    var next = metadataByStream.TryGetValue(key, out var metadata)
                        ? MergeRecentStreamMetadata(stream, metadata) : stream;
                    updated.Add(next);
                    settingsChanged |= !ReferenceEquals(stream, next);
                }
                // Publish a new settings list once, keeping a concurrent save's snapshot
                // stable while individual results are already visible in the cards.
                if (settingsChanged)
                {
                    Settings.RecentStreams = updated;
                    RebuildRecentStreams();
                    await SaveRecentThumbnailSettingsAsync(cancellationToken);
                }
            }
            finally
            {
                recentStreamsGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", "Failed to refresh recent stream thumbnails.", ex);
        }
        finally
        {
            pendingRecentMetadata = null;
            recentThumbnailRefreshGate.Release();
        }
    }

    internal async Task MarkRecentStreamsCheckingAsync(
        IReadOnlyList<StreamTarget> targets,
        CancellationToken cancellationToken)
    {
        await recentStreamsGate.WaitAsync(cancellationToken);
        try
        {
            var changed = false;
            var status = new RecentStreamLiveStatus(
                RecentStreamLiveState.Checking,
                null,
                "Checking live status from the platform.");

            foreach (var target in targets)
            {
                if (!recentStreamController.TryGetLiveStatus(target.StateKey, out var current) ||
                    current.State != RecentStreamLiveState.Checking)
                {
                    changed |= recentStreamController.SetLiveStatus(target.StateKey, status);
                }
            }

            if (changed)
            {
                RebuildRecentStreams();
            }
        }
        finally
        {
            recentStreamsGate.Release();
        }
    }

    internal async Task ApplyRecentStreamMetadataAsync(
        StreamTarget target,
        StreamMetadataResult metadata,
        CancellationToken cancellationToken)
    {
        await recentStreamsGate.WaitAsync(cancellationToken);
        try
        {
            if (disposed)
            {
                return;
            }

            // A request can finish after its row was deleted. It must not restore either
            // the row or its transient freshness/status entry.
            var current = FindRecentStream(target);
            if (current is null) return;
            pendingRecentMetadata![target.StateKey] = metadata;
            var checkedAtUtc = DateTimeOffset.UtcNow;
            var status = CreateRecentStreamLiveStatus(metadata, checkedAtUtc);
            recentStreamController.SetLiveStatus(target.StateKey, status);
            recentStreamController.RecordMetadataRefresh(target.StateKey, checkedAtUtc,
                metadata.State is StreamMetadataState.Available or StreamMetadataState.Offline);
            foreach (var card in RecentStreams)
            {
                if (card.Platform == target.Platform &&
                    string.Equals(card.Channel, target.Channel, StringComparison.OrdinalIgnoreCase))
                    card.Update(MergeRecentStreamMetadata(current, metadata), status);
            }
        }
        finally { recentStreamsGate.Release(); }
    }

    internal static RecentStreamSettings MergeRecentStreamMetadata(RecentStreamSettings stream, StreamMetadataResult metadata)
    {
        if (metadata.State != StreamMetadataState.Available) return stream;
        var displayName = FirstNonEmpty(metadata.DisplayName, stream.DisplayName, stream.Channel);
        var thumbnailUrl = NormalizeImageUrl(FirstNonEmpty(metadata.ThumbnailUrl, stream.ThumbnailUrl));
        var categoryName = FirstNonEmpty(metadata.CategoryName, stream.CategoryName);
        if (displayName == stream.DisplayName && thumbnailUrl == stream.ThumbnailUrl && categoryName == stream.CategoryName)
            return stream;

        // Merge into the latest settings, preserving watch order, quality and timestamps
        // that playback or a user action may have changed while the request was running.
        return new RecentStreamSettings
        {
            Platform = stream.Platform,
            Channel = stream.Channel,
            Url = stream.Url,
            DisplayName = displayName,
            CategoryName = categoryName,
            ThumbnailUrl = thumbnailUrl,
            LastQuality = stream.LastQuality,
            LastWatchedAtUtc = stream.LastWatchedAtUtc
        };
    }

    internal static RecentStreamLiveStatus CreateRecentStreamLiveStatus(
        StreamMetadataResult metadata,
        DateTimeOffset checkedAtUtc)
    {
        return metadata.State switch
        {
            StreamMetadataState.Available => new RecentStreamLiveStatus(
                RecentStreamLiveState.Live,
                checkedAtUtc,
                FirstNonEmpty(metadata.Message, "The platform reports this stream is live.")),
            StreamMetadataState.Offline => new RecentStreamLiveStatus(
                RecentStreamLiveState.Offline,
                checkedAtUtc,
                FirstNonEmpty(metadata.Message, "The platform reports this stream is offline.")),
            _ => new RecentStreamLiveStatus(
                RecentStreamLiveState.Unknown,
                checkedAtUtc,
                FirstNonEmpty(metadata.Message, "The platform did not return a usable live status."))
        };
    }

    internal Task RememberRecentStreamAsync(StreamTabViewModel tab, Task<StreamMetadataResult?>? openingMetadata = null) => disposed ? Task.CompletedTask : Track(RememberRecentStreamAsyncCore(tab, openingMetadata));

    private async Task RememberRecentStreamAsyncCore(
        StreamTabViewModel tab,
        Task<StreamMetadataResult?>? openingMetadata = null)
    {
        if (disposed)
        {
            return;
        }

        var target = tab.Target;
        try
        {
            var hint = TakeRecentStreamHint(target);
            var watchedAtUtc = DateTimeOffset.UtcNow;
            var needsMetadata = false;

            await recentStreamsGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                var existing = FindRecentStream(target);
                var recentStream = CreateRecentStreamSettings(
                    target,
                    tab.Quality,
                    watchedAtUtc,
                    hint,
                    existing,
                    metadata: null);

                Settings.RecentStreams = Settings.RecentStreams
                    .Where(stream => !IsSameRecentStream(stream, target))
                    .Prepend(recentStream)
                    .ToList();
                recentStreamController.SetLiveStatus(target.StateKey, new RecentStreamLiveStatus(
                    RecentStreamLiveState.Live,
                    watchedAtUtc,
                    "Playback started successfully."));
                RebuildRecentStreams();
                await SaveRecentStreamSettingsAsync(target);
                needsMetadata = openingMetadata is not null ||
                    (streamMetadataService is not null && string.IsNullOrWhiteSpace(recentStream.ThumbnailUrl));
            }
            finally
            {
                recentStreamsGate.Release();
            }

            if (!needsMetadata)
            {
                return;
            }

            // Reuse the opening lookup; playback and the initial Recent card never wait for it.
            var metadata = openingMetadata is not null
                ? await openingMetadata
                : await TryGetRecentStreamMetadataAsync(target, lifetimeCancellation.Token);
            if (disposed || metadata?.State != StreamMetadataState.Available ||
                (string.IsNullOrWhiteSpace(metadata.ThumbnailUrl) &&
                    string.IsNullOrWhiteSpace(metadata.DisplayName) &&
                    string.IsNullOrWhiteSpace(metadata.CategoryName)))
            {
                return;
            }

            await recentStreamsGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                var existing = FindRecentStream(target);
                if (existing is null)
                {
                    return;
                }

                var recentStream = CreateRecentStreamSettings(
                    target,
                    existing.LastQuality,
                    existing.LastWatchedAtUtc,
                    hint: null,
                    existing,
                    metadata);

                Settings.RecentStreams = Settings.RecentStreams
                    .Select(stream => IsSameRecentStream(stream, target) ? recentStream : stream)
                    .ToList();
                var checkedAtUtc = DateTimeOffset.UtcNow;
                recentStreamController.SetLiveStatus(target.StateKey, CreateRecentStreamLiveStatus(metadata, checkedAtUtc));
                recentStreamController.RecordMetadataRefresh(target.StateKey, checkedAtUtc, succeeded: true);
                RebuildRecentStreams();
                await SaveRecentStreamSettingsAsync(target);
            }
            finally
            {
                recentStreamsGate.Release();
            }
        }
        catch (OperationCanceledException) when (disposed || lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to update recent stream {target.DisplayName}.", ex);
        }
    }

    internal RecentStreamSettings? FindRecentStream(StreamTarget target)
    {
        return Settings.RecentStreams.FirstOrDefault(stream => IsSameRecentStream(stream, target));
    }

    internal static RecentStreamSettings CreateRecentStreamSettings(
        StreamTarget target,
        string quality,
        DateTimeOffset lastWatchedAtUtc,
        RecentStreamHint? hint,
        RecentStreamSettings? existing,
        StreamMetadataResult? metadata)
    {
        return new RecentStreamSettings
        {
            Platform = target.Platform,
            Channel = target.Channel,
            Url = target.Url,
            DisplayName = FirstNonEmpty(
                hint?.DisplayName,
                metadata?.DisplayName,
                existing?.DisplayName,
                target.Channel),
            ThumbnailUrl = FirstNonEmpty(
                hint?.ThumbnailUrl,
                metadata?.ThumbnailUrl,
                existing?.ThumbnailUrl),
            CategoryName = FirstNonEmpty(
                target.CategoryName,
                hint?.CategoryName,
                metadata?.CategoryName,
                existing?.CategoryName),
            LastQuality = quality,
            LastWatchedAtUtc = lastWatchedAtUtc
        };
    }

    internal async Task<StreamMetadataResult?> TryGetRecentStreamMetadataAsync(
        StreamTarget target,
        CancellationToken cancellationToken = default)
    {
        var result = await GetRecentStreamMetadataAsync(target, cancellationToken);
        return result?.State == StreamMetadataState.Available ? result : null;
    }

    internal async Task<StreamMetadataResult?> GetRecentStreamMetadataAsync(
        StreamTarget target,
        CancellationToken cancellationToken = default)
    {
        if (streamMetadataService is null)
        {
            return null;
        }

        try
        {
            return await streamMetadataService.GetLiveStreamMetadataAsync(target, Settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to load metadata for {target.DisplayName}.", ex);
            return new StreamMetadataResult(
                StreamMetadataState.Unavailable,
                "",
                "",
                "The platform metadata request failed.");
        }
    }

    internal async Task SaveRecentThumbnailSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await settingsService.SaveAsync(Settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", "Failed to save refreshed recent stream thumbnails.", ex);
        }
    }

    internal async Task SaveRecentStreamRemovalAsync(StreamTarget target)
    {
        try
        {
            await settingsService.SaveAsync(Settings);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to save recent stream removal for {target.DisplayName}.", ex);
        }
    }

    internal async Task SaveRecentStreamSettingsAsync(StreamTarget target)
    {
        try
        {
            await settingsService.SaveAsync(Settings);
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Recent", $"Failed to save recent stream {target.DisplayName}.", ex);
        }
    }

    internal static bool IsSameRecentStream(RecentStreamSettings stream, StreamTarget target)
    {
        return stream.Platform == target.Platform &&
            string.Equals(stream.Channel, target.Channel, StringComparison.OrdinalIgnoreCase);
    }

    internal void RecentStreamsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRecentStreams));
        OnPropertyChanged(nameof(IsRecentStreamsEmptyVisible));
        OnPropertyChanged(nameof(RecentStreamsStatus));
    }

    internal void RebuildRecentStreams()
    {
        var rows = Settings.RecentStreams.Select(stream =>
        {
            var target = new StreamTarget(stream.Platform, stream.Channel, stream.Url, CategoryName: stream.CategoryName);
            var liveStatus = recentStreamController.TryGetLiveStatus(target.StateKey, out var status)
                ? status : RecentStreamLiveStatus.Unknown;
            var displayed = pendingRecentMetadata is not null && pendingRecentMetadata.TryGetValue(target.StateKey, out var metadata)
                ? MergeRecentStreamMetadata(stream, metadata) : stream;
            return (Key: target.StateKey, Stream: displayed, Status: liveStatus);
        });
        PagedResultTracker.ApplyItems(RecentStreams, rows,
            card => card.Target.StateKey, row => row.Key,
            row => new RecentStreamViewModel(row.Stream, openStream, DeleteRecentStreamAsync, row.Status),
            (card, row) => card.Update(row.Stream, row.Status), reset: true);
    }
}
