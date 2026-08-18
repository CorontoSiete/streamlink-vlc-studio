namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Coordinates Home VOD and Browse loading lifetimes. It keeps each paged
/// surface's generation independent while sharing the same safe cancellation
/// and disposal behavior.
/// </summary>
internal sealed class VodBrowseController : IDisposable
{
    private readonly CancellationDebounceCoordinator twitchVod = new();
    private readonly CancellationDebounceCoordinator browseCategories = new();
    private readonly CancellationDebounceCoordinator browseStreams = new();
    private int twitchVodGeneration;
    private int browseCategoryGeneration;
    private int browseCategoryViewerCountGeneration;
    private int browseStreamGeneration;

    public int CurrentTwitchVodGeneration => Volatile.Read(ref twitchVodGeneration);
    public int CurrentBrowseCategoryGeneration => Volatile.Read(ref browseCategoryGeneration);
    public int CurrentBrowseCategoryViewerCountGeneration => Volatile.Read(ref browseCategoryViewerCountGeneration);
    public int CurrentBrowseStreamGeneration => Volatile.Read(ref browseStreamGeneration);

    public int AdvanceTwitchVodGeneration() => Interlocked.Increment(ref twitchVodGeneration);
    public int AdvanceBrowseCategoryGeneration() => Interlocked.Increment(ref browseCategoryGeneration);
    public int AdvanceBrowseCategoryViewerCountGeneration() => Interlocked.Increment(ref browseCategoryViewerCountGeneration);
    public int AdvanceBrowseStreamGeneration() => Interlocked.Increment(ref browseStreamGeneration);

    public bool IsCurrentTwitchVodGeneration(int expected) => CurrentTwitchVodGeneration == expected;
    public bool IsCurrentBrowseCategoryGeneration(int expected) => CurrentBrowseCategoryGeneration == expected;
    public bool IsCurrentBrowseCategoryViewerCountGeneration(int expected) => CurrentBrowseCategoryViewerCountGeneration == expected;
    public bool IsCurrentBrowseStreamGeneration(int expected) => CurrentBrowseStreamGeneration == expected;

    public void ScheduleTwitchVod(
        TimeSpan delay,
        Action callback,
        Action<Exception>? callbackErrorHandler = null) =>
        twitchVod.Schedule(delay, callback, callbackErrorHandler);
    public void CancelScheduledTwitchVod() => twitchVod.CancelScheduled();
    public CancellationTokenSource BeginTwitchVodOperation(CancellationToken lifetimeToken) => twitchVod.BeginOperation(lifetimeToken);
    public void CancelTwitchVodOperation() => twitchVod.CancelActive();
    public void CompleteTwitchVodOperation(CancellationTokenSource operation) => twitchVod.Complete(operation);

    public void ScheduleBrowseCategory(
        TimeSpan delay,
        Action callback,
        Action<Exception>? callbackErrorHandler = null) =>
        browseCategories.Schedule(delay, callback, callbackErrorHandler);
    public void CancelScheduledBrowseCategory() => browseCategories.CancelScheduled();
    public CancellationTokenSource BeginBrowseCategoryOperation(CancellationToken lifetimeToken) => browseCategories.BeginOperation(lifetimeToken);
    public void CancelBrowseCategoryOperation() => browseCategories.CancelActive();
    public void CompleteBrowseCategoryOperation(CancellationTokenSource operation) => browseCategories.Complete(operation);

    public CancellationTokenSource BeginBrowseStreamOperation(CancellationToken lifetimeToken) => browseStreams.BeginOperation(lifetimeToken);
    public void CancelBrowseStreamOperation() => browseStreams.CancelActive();
    public void CompleteBrowseStreamOperation(CancellationTokenSource operation) => browseStreams.Complete(operation);

    public async Task DrainAsync(TimeSpan timeout)
    {
        await Task.WhenAll(
            twitchVod.DrainAsync(timeout),
            browseCategories.DrainAsync(timeout),
            browseStreams.DrainAsync(timeout));
    }

    public void Dispose()
    {
        twitchVod.Dispose();
        browseCategories.Dispose();
        browseStreams.Dispose();
    }
}
