using System.Collections.ObjectModel;
using System.Collections.Specialized;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Text;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class StreamSearchViewModel : HomeFeatureViewModel
{
    private readonly Func<string> selectedQuality;
    private readonly Func<bool> canShowSearch;
    private readonly Func<StreamSearchResultViewModel, bool, Task> openSearchResult;
    internal int CurrentGeneration => streamSearchController.CurrentGeneration;

    internal StreamSearchViewModel(MainViewModelDependencies dependencies, Action<string> setStatus,
        Func<string> selectedQuality, Func<bool> canShowSearch, Func<StreamSearchResultViewModel, bool, Task> openSearchResult)
        : base(dependencies, setStatus)
    {
        this.selectedQuality = selectedQuality;
        this.canShowSearch = canShowSearch;
        this.openSearchResult = openSearchResult;
        streamSearchService = dependencies.StreamSearchService;
        streamlinkService = dependencies.StreamlinkService;
        streamMetadataService = dependencies.StreamMetadataService;
        twitchVodService = dependencies.TwitchVodService;
        viewerCountService = dependencies.ViewerCountService;
        streamSearchDebounceInterval = dependencies.StreamSearchDebounceInterval ?? DefaultStreamSearchDebounceInterval;
        AddAndPlayCommand = CreateCommand(AddAndPlayAsync, () => HasNewStreamSearchText);
        StreamSearchResults.CollectionChanged += StreamSearchResultsOnCollectionChanged;
    }

    protected override void StopOperations()
    {
        StreamSearchResults.CollectionChanged -= StreamSearchResultsOnCollectionChanged;
        streamSearchController.Dispose();
    }

    protected override Task WaitForOperationsAsync() => streamSearchController.DrainAsync(Timeout.InfiniteTimeSpan);
    private static readonly TimeSpan DefaultStreamSearchDebounceInterval = TimeSpan.FromMilliseconds(250);
    private const int StreamSearchViewerCountConcurrency = 4;
    private readonly IStreamlinkService streamlinkService;
    private readonly IViewerCountService? viewerCountService;
    private readonly IStreamMetadataService? streamMetadataService;
    private readonly ITwitchVodService? twitchVodService;
    private readonly IStreamSearchService? streamSearchService;
    private readonly TimeSpan streamSearchDebounceInterval;
    private readonly StreamSearchController streamSearchController = new();
    private string newStreamText = "";
    private string streamSearchStatus = "";
    private bool isStreamSearchRunning;
    private bool hasStreamSearchCompleted;
    private Task? activeStreamSearchTask;
    private int activeStreamSearchGeneration;
    private string activeStreamSearchQuality = "";
    private bool isStreamSearchDropdownOpen;
    private long streamSearchScheduleVersion;
    public ObservableCollection<StreamSearchResultViewModel> StreamSearchResults { get; } = [];

    public AsyncRelayCommand AddAndPlayCommand { get; }

    public string NewStreamText
    {
        get => newStreamText;
        set
        {
            if (SetProperty(ref newStreamText, value ?? ""))
            {
                streamSearchController.AdvanceGeneration();
                CancelStreamSearchDebounce();
                CancelActiveStreamSearch();
                IsStreamSearchRunning = false;
                ClearStreamSearchResults();
                OnPropertyChanged(nameof(HasNewStreamSearchText));
                OnPropertyChanged(nameof(IsNewStreamSearchPlaceholderVisible));
                AddAndPlayCommand.RaiseCanExecuteChanged();
                ScheduleAutomaticStreamSearch();
            }
        }
    }

    public bool HasNewStreamSearchText => !string.IsNullOrWhiteSpace(NewStreamText);

    public bool IsNewStreamSearchPlaceholderVisible => !HasNewStreamSearchText;

    public bool HasStreamSearchResults => StreamSearchResults.Count > 0;

    public bool IsStreamSearchPanelVisible => canShowSearch() && isStreamSearchDropdownOpen && (IsStreamSearchRunning ||
        hasStreamSearchCompleted ||
        HasStreamSearchResults);

    public bool IsStreamSearchResultsVisible => HasStreamSearchResults;

    public bool IsStreamSearchEmptyVisible => hasStreamSearchCompleted &&
        !IsStreamSearchRunning &&
        !HasStreamSearchResults;

    public string StreamSearchResultsTitle => StreamSearchResults.Count switch
    {
        0 => "Search results",
        1 => "1 search result",
        _ => $"{StreamSearchResults.Count} search results"
    };

    public string StreamSearchStatus
    {
        get => streamSearchStatus;
        internal set
        {
            if (SetProperty(ref streamSearchStatus, value ?? ""))
            {
                OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
                OnPropertyChanged(nameof(IsStreamSearchEmptyVisible));
            }
        }
    }

    public bool IsStreamSearchRunning
    {
        get => isStreamSearchRunning;
        internal set
        {
            if (SetProperty(ref isStreamSearchRunning, value))
            {
                OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
                OnPropertyChanged(nameof(IsStreamSearchEmptyVisible));
            }
        }
    }

    public void ShowStreamSearchDropdown()
    {
        if (HasNewStreamSearchText &&
            (IsStreamSearchRunning || hasStreamSearchCompleted || HasStreamSearchResults))
        {
            SetStreamSearchDropdownOpen(true);
        }
    }

    public void DismissStreamSearchDropdown()
    {
        CancelStreamSearchDebounce();
        SetStreamSearchDropdownOpen(false);
    }

    internal Task AddAndPlayAsync()
    {
        var query = NewStreamText.Trim();
        CancelStreamSearchDebounce();
        if (activeStreamSearchTask is { IsCompleted: false } &&
            activeStreamSearchGeneration == streamSearchController.CurrentGeneration &&
            string.Equals(activeStreamSearchQuality, selectedQuality(), StringComparison.Ordinal))
        {
            SetStreamSearchDropdownOpen(true);
            return activeStreamSearchTask;
        }

        return StartStreamSearchAsync(query, streamSearchController.AdvanceGeneration());
    }

    internal Task StartStreamSearchAsync(string query, int searchGeneration)
    {
        activeStreamSearchGeneration = searchGeneration;
        activeStreamSearchQuality = selectedQuality();
        activeStreamSearchTask = RunStreamSearchAsync(query, searchGeneration);
        return activeStreamSearchTask;
    }

    internal async Task RunStreamSearchAsync(string query, int searchGeneration)
    {
        if (string.IsNullOrWhiteSpace(query) || !IsCurrentStreamSearch(searchGeneration, query))
        {
            return;
        }

        var searchCancellation = ReplaceStreamSearchCancellation();
        StreamSearchResults.Clear();
        StreamSearchStatus = "";
        SetStreamSearchCompleted(false);
        SetStreamSearchDropdownOpen(true);
        IsStreamSearchRunning = true;

        try
        {
            var probes = await SearchStreamCandidatesAsync(query, searchCancellation.Token);
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            var enrichedProbes = await LoadStreamSearchResultMetadataAsync(
                probes,
                searchCancellation.Token);
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            var displayProbes = OrderStreamSearchProbesForDisplay(enrichedProbes);
            ReplaceStreamSearchResults(displayProbes);
            SetStreamSearchCompleted(true);
            StreamSearchStatus = FormatStreamSearchResult(query, displayProbes);
            StatusMessage = StreamSearchStatus;
            IsStreamSearchRunning = false;

            var viewerCountProbes = await LoadStreamSearchResultViewerCountsAsync(
                enrichedProbes,
                searchCancellation.Token);
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            if (!viewerCountProbes.SequenceEqual(enrichedProbes))
            {
                displayProbes = OrderStreamSearchProbesForDisplay(viewerCountProbes);
                UpdateStreamSearchViewerCounts(displayProbes);
                StreamSearchStatus = FormatStreamSearchResult(query, displayProbes);
                StatusMessage = StreamSearchStatus;
            }
        }
        catch (OperationCanceledException) when (searchCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentStreamSearch(searchGeneration, query))
            {
                return;
            }

            SetStreamSearchCompleted(true);
            StreamSearchStatus = ex.Message;
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "UI", "Stream search failed.", ex);
        }
        finally
        {
            if (IsCurrentStreamSearch(searchGeneration, query))
            {
                IsStreamSearchRunning = false;
            }

            DisposeStreamSearchCancellation(searchCancellation);
        }
    }

    internal async Task<IReadOnlyList<StreamCandidateProbe>> SearchStreamCandidatesAsync(
        string query,
        CancellationToken cancellationToken)
    {
        if (StreamInputParser.TryParseTwitchVodUrl(query, out var vodTarget) && vodTarget is not null)
        {
            return [new StreamCandidateProbe(vodTarget, new StreamlinkProbeResult(true, "Twitch VOD"))];
        }

        if (streamSearchService is not null)
        {
            var serviceMessage = $"Searching Twitch and Kick for {query}";
            StatusMessage = serviceMessage;
            StreamSearchStatus = serviceMessage;
            var result = await streamSearchService.SearchAsync(
                new StreamSearchRequest(query, selectedQuality(), 10),
                Settings,
                cancellationToken);
            return result.Channels
                .Select(channel => new StreamCandidateProbe(
                    channel.Target,
                    new StreamlinkProbeResult(channel.CanPlay, channel.StatusMessage),
                    Channel: channel,
                    ViewerCount: channel.ViewerCount))
                .ToArray();
        }

        var candidates = StreamInputParser.ParseCandidates(query);
        if (candidates.Count == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(Settings.StreamlinkPath))
        {
            throw new InvalidOperationException("Configure the Streamlink executable path in Settings.");
        }

        var message = candidates.Count == 1
            ? $"Searching {candidates[0].DisplayName}"
            : $"Searching Twitch and Kick for {candidates[0].Channel}";
        StatusMessage = message;
        StreamSearchStatus = message;
        var customArguments = CommandLineTokenizer.Tokenize(Settings.CustomStreamlinkArguments);
        return await ProbeCandidatesAsync(
            candidates,
            customArguments,
            cancellationToken);
    }

    internal async Task<IReadOnlyList<StreamCandidateProbe>> LoadStreamSearchResultMetadataAsync(
        IReadOnlyList<StreamCandidateProbe> probes,
        CancellationToken cancellationToken)
    {
        if ((streamMetadataService is null && twitchVodService is null) || probes.Count == 0)
        {
            return probes;
        }

        return await Task.WhenAll(probes.Select(probe => LoadStreamSearchResultMetadataAsync(
            probe,
            cancellationToken)));
    }

    internal async Task<StreamCandidateProbe> LoadStreamSearchResultMetadataAsync(
        StreamCandidateProbe probe,
        CancellationToken cancellationToken)
    {
        if (probe.Target.IsExplicitTwitchVod)
        {
            return await LoadTwitchVodMetadataAsync(probe, cancellationToken);
        }

        if (probe.Channel is not null || probe.Target.Kind != StreamTargetKind.Live)
        {
            return probe;
        }

        var metadataService = streamMetadataService;
        if (metadataService is null)
        {
            return probe;
        }

        try
        {
            var metadata = await metadataService.GetLiveStreamMetadataAsync(
                probe.Target,
                Settings,
                cancellationToken);
            return metadata.State == StreamMetadataState.Available
                ? probe with { Metadata = metadata }
                : probe;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Failed to load metadata for {probe.Target.DisplayName}.", ex);
            return probe;
        }
    }

    private async Task<StreamCandidateProbe> LoadTwitchVodMetadataAsync(
        StreamCandidateProbe probe,
        CancellationToken cancellationToken)
    {
        if (twitchVodService is null)
        {
            return probe;
        }

        try
        {
            var video = await twitchVodService.GetVideoAsync(probe.Target.MediaId, cancellationToken);
            if (video is null)
            {
                return probe with
                {
                    Result = new StreamlinkProbeResult(false, "This Twitch VOD was not found or is no longer available.")
                };
            }

            return probe with
            {
                Target = probe.Target with
                {
                    Channel = video.ChannelLogin,
                    DisplayTitle = FirstNonEmpty(video.Title, video.ChannelDisplayName),
                    BroadcasterId = video.BroadcasterId,
                    MediaDuration = video.Duration,
                    MediaStartedAtUtc = video.CreatedAtUtc,
                    CategoryName = video.CategoryName,
                    ProfileImageUrl = video.ProfileImageUrl
                },
                Metadata = new StreamMetadataResult(
                    StreamMetadataState.Available,
                    video.ThumbnailUrl,
                    video.ChannelDisplayName,
                    "Twitch VOD metadata loaded.",
                    video.CategoryName,
                    video.ProfileImageUrl)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Failed to load metadata for {probe.Target.DisplayName}.", ex);
            return probe with
            {
                Result = probe.Result with { Message = "Twitch VOD. Title and avatar could not be loaded. Try searching again." }
            };
        }
    }

    internal async Task<IReadOnlyList<StreamCandidateProbe>> LoadStreamSearchResultViewerCountsAsync(
        IReadOnlyList<StreamCandidateProbe> probes,
        CancellationToken cancellationToken)
    {
        if (viewerCountService is null ||
            !probes.Any(probe => IsLiveStreamSearchProbe(probe) && probe.Target.Kind == StreamTargetKind.Live && probe.ViewerCount is null))
        {
            return probes;
        }

        using var throttle = new SemaphoreSlim(StreamSearchViewerCountConcurrency);
        var tasks = probes.Select(async probe =>
        {
            if (!IsLiveStreamSearchProbe(probe) || probe.Target.Kind != StreamTargetKind.Live || probe.ViewerCount is not null)
            {
                return probe;
            }

            await throttle.WaitAsync(cancellationToken);
            try
            {
                return await LoadStreamSearchResultViewerCountAsync(probe, cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    internal void ReplaceStreamSearchResults(IReadOnlyList<StreamCandidateProbe> probes)
    {
        StreamSearchResults.Clear();
        foreach (var probe in probes)
        {
            StreamSearchResults.Add(probe.Channel is { } channel
                ? new StreamSearchResultViewModel(channel, openSearchResult, probe.ViewerCount)
                : new StreamSearchResultViewModel(
                    probe.Target,
                    probe.Result,
                    probe.Metadata,
                    openSearchResult,
                    probe.ViewerCount));
        }
    }

    internal void UpdateStreamSearchViewerCounts(IReadOnlyList<StreamCandidateProbe> probes)
    {
        // Enrichment only changes counts and ordering. Keep the existing rows so
        // WPF retains their controls, focus, and any in-flight Open command.
        for (var index = 0; index < probes.Count; index++)
        {
            var probe = probes[index];
            var identity = probe.Target.TabIdentityKey;
            for (var currentIndex = index; currentIndex < StreamSearchResults.Count; currentIndex++)
            {
                var result = StreamSearchResults[currentIndex];
                if (!string.Equals(result.Target.TabIdentityKey, identity, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.UpdateViewerCount(probe.ViewerCount);
                if (currentIndex != index)
                {
                    StreamSearchResults.Move(currentIndex, index);
                }
                break;
            }
        }
    }

    internal async Task<StreamCandidateProbe> LoadStreamSearchResultViewerCountAsync(
        StreamCandidateProbe probe,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await viewerCountService!.GetViewerCountAsync(
                probe.Target,
                Settings,
                cancellationToken);
            return result.State == ViewerCountState.Available && result.ViewerCount is { } viewerCount
                ? probe with { ViewerCount = Math.Max(0, viewerCount) }
                : probe;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Failed to load viewer count for {probe.Target.DisplayName}.", ex);
            return probe;
        }
    }

    internal bool IsCurrentStreamSearch(int searchGeneration, string query)
    {
        return !disposed &&
            streamSearchController.IsCurrent(
                searchGeneration,
                query,
                () => NewStreamText,
                () => disposed);
    }

    internal void ClearStreamSearchResults()
    {
        StreamSearchResults.Clear();
        StreamSearchStatus = "";
        SetStreamSearchCompleted(false);
        if (!HasNewStreamSearchText)
        {
            SetStreamSearchDropdownOpen(false);
        }
    }

    internal void ScheduleAutomaticStreamSearch()
    {
        if (disposed)
        {
            return;
        }

        var query = NewStreamText.Trim();
        var searchGeneration = streamSearchController.CurrentGeneration;
        var scheduleVersion = Interlocked.Increment(ref streamSearchScheduleVersion);
        if (string.IsNullOrWhiteSpace(query))
        {
            SetStreamSearchDropdownOpen(false);
            return;
        }

        void StartSearchIfCurrent()
        {
            // Cancellation can occur after the timer has already posted to the UI.
            // Recheck there so a dismissed search cannot reopen the popup later.
            if (scheduleVersion == Volatile.Read(ref streamSearchScheduleVersion))
            {
                _ = RunAutomaticStreamSearchAsync(query, searchGeneration);
            }
        }

        if (streamSearchDebounceInterval <= TimeSpan.Zero)
        {
            dispatch(StartSearchIfCurrent);
            return;
        }

        streamSearchController.Schedule(
            streamSearchDebounceInterval,
            () => dispatch(StartSearchIfCurrent),
            ReportDebouncedCallbackFailure);
    }

    internal async Task RunAutomaticStreamSearchAsync(string query, int searchGeneration)
    {
        if (disposed || !IsCurrentStreamSearch(searchGeneration, query))
        {
            return;
        }

        await StartStreamSearchAsync(query, searchGeneration);
    }

    internal void CancelStreamSearchDebounce()
    {
        Interlocked.Increment(ref streamSearchScheduleVersion);
        streamSearchController.CancelScheduled();
    }

    internal CancellationTokenSource ReplaceStreamSearchCancellation()
    {
        return streamSearchController.BeginOperation(lifetimeCancellation.Token);
    }

    internal void CancelActiveStreamSearch()
    {
        streamSearchController.CancelActive();
    }

    internal void DisposeStreamSearchCancellation(CancellationTokenSource cancellation)
    {
        streamSearchController.Complete(cancellation);
    }

    internal void SetStreamSearchDropdownOpen(bool value)
    {
        SetProperty(ref isStreamSearchDropdownOpen, value && canShowSearch(),
            nameof(IsStreamSearchPanelVisible));
    }

    internal void SetStreamSearchCompleted(bool value)
    {
        if (hasStreamSearchCompleted == value)
        {
            return;
        }

        hasStreamSearchCompleted = value;
        OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
        OnPropertyChanged(nameof(IsStreamSearchEmptyVisible));
    }

    internal static string FormatStreamSearchResult(string query, IReadOnlyList<StreamCandidateProbe> probes)
    {
        if (probes.Count == 1 && probes[0].Target.IsExplicitTwitchVod)
        {
            var video = probes[0];
            return video.Metadata is not null
                ? $"Twitch VOD found: {video.Target.DisplayTitle}"
                : video.Result.Message;
        }

        if (probes.Count == 0)
        {
            return $"No Twitch or Kick channels found for {query}.";
        }

        var live = probes.Count(IsLiveStreamSearchProbe);
        var offline = probes.Count(probe =>
            !IsLiveStreamSearchProbe(probe) &&
            probe.Channel?.State == StreamSearchChannelState.Offline);
        var unavailable = probes.Count - live - offline;
        return StreamSearchSummary.Format(query, live, offline, unavailable);
    }

    internal static IReadOnlyList<StreamCandidateProbe> OrderStreamSearchProbesForDisplay(
        IReadOnlyList<StreamCandidateProbe> probes)
    {
        return probes
            .Select((probe, index) => new { Probe = probe, Index = index })
            .OrderBy(item => IsLiveStreamSearchProbe(item.Probe) ? 0 : 1)
            .ThenBy(item => item.Probe.ViewerCount is null ? 1 : 0)
            .ThenByDescending(item => item.Probe.ViewerCount ?? 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Probe)
            .ToArray();
    }

    internal static bool IsLiveStreamSearchProbe(StreamCandidateProbe probe)
    {
        return probe.Target.Kind == StreamTargetKind.Live &&
            (probe.Channel?.IsLive ?? probe.Result.HasPlayableStream);
    }

    internal async Task<IReadOnlyList<StreamCandidateProbe>> ProbeCandidatesAsync(
        IReadOnlyList<StreamTarget> candidates,
        IReadOnlyList<string> customArguments,
        CancellationToken cancellationToken)
    {
        return await Task.WhenAll(candidates.Select(target => ProbeCandidateAsync(
            target,
            customArguments,
            cancellationToken)));
    }

    internal async Task<StreamCandidateProbe> ProbeCandidateAsync(
        StreamTarget target,
        IReadOnlyList<string> customArguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new StreamTransportRequest(
                target,
                selectedQuality(),
                Settings.StreamlinkPath!,
                Settings.LowLatency,
                customArguments);
            var result = await streamlinkService.ProbeStreamsAsync(request, cancellationToken);
            return new StreamCandidateProbe(target, result);
        }
        catch (OperationCanceledException)
        {
            return new StreamCandidateProbe(target, new StreamlinkProbeResult(false, "Canceled."));
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Search", $"Streamlink probe failed for {target.DisplayName}.", ex);
            return new StreamCandidateProbe(target, new StreamlinkProbeResult(false, ex.Message));
        }
    }

    internal void StreamSearchResultsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasStreamSearchResults));
        OnPropertyChanged(nameof(IsStreamSearchPanelVisible));
        OnPropertyChanged(nameof(IsStreamSearchResultsVisible));
        OnPropertyChanged(nameof(IsStreamSearchEmptyVisible));
        OnPropertyChanged(nameof(StreamSearchResultsTitle));
    }
}
