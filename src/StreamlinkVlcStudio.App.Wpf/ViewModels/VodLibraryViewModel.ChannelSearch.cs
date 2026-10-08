using System.Collections.ObjectModel;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed partial class VodLibraryViewModel
{
    private readonly IStreamSearchService? vodChannelSearchService;
    private readonly StreamSearchController vodChannelSearchController = new();
    private Task? activeVodChannelSearchTask;
    private int activeVodChannelSearchGeneration;
    private PlatformKind activeVodChannelSearchPlatform;
    private string vodChannelSearchStatus = "";
    private bool isVodChannelSearchRunning;
    private bool hasVodChannelSearchCompleted;
    private bool isVodChannelSearchOpen;
    private bool vodStreamerResolved;
    private bool suppressAutomaticVodSearch;
    private long twitchVodScheduleVersion;

    public ObservableCollection<VodChannelSearchResultViewModel> VodChannelSearchResults { get; } = [];
    public bool HasVodChannelSearchResults => VodChannelSearchResults.Count > 0;
    public bool IsVodChannelSearchVisible => isVodChannelSearchOpen &&
        (IsVodChannelSearchRunning || hasVodChannelSearchCompleted || HasVodChannelSearchResults);
    public string VodChannelSearchResultsTitle => VodChannelSearchResults.Count switch
    {
        0 => $"{VodPlatformText} streamers",
        1 => $"1 {VodPlatformText} streamer",
        _ => $"{VodChannelSearchResults.Count} {VodPlatformText} streamers"
    };

    public string VodChannelSearchStatus
    {
        get => vodChannelSearchStatus;
        private set => SetProperty(ref vodChannelSearchStatus, value);
    }

    public bool IsVodChannelSearchRunning
    {
        get => isVodChannelSearchRunning;
        private set
        {
            if (SetProperty(ref isVodChannelSearchRunning, value))
                OnPropertyChanged(nameof(IsVodChannelSearchVisible));
        }
    }

    private bool CanLoadEnteredVodStreamer => vodChannelSearchService is null || vodStreamerResolved ||
        (StreamInputParser.TryParsePlatformUrl(TwitchVodSearchText.Trim(), out var target) &&
         target is { Kind: StreamTargetKind.Live } && target.Platform == SelectedVodPlatform);

    internal Task SearchVodStreamerAsync()
    {
        if (disposed) return Task.CompletedTask;
        CancelTwitchVodSearchDebounce();
        if (!HasTwitchVodSearchText || CanLoadEnteredVodStreamer)
            return SearchTwitchVodsAsync(reset: true);

        SetVodChannelSearchOpen(true);
        var generation = vodChannelSearchController.CurrentGeneration;
        if (activeVodChannelSearchTask is { IsCompleted: false } &&
            activeVodChannelSearchGeneration == generation &&
            activeVodChannelSearchPlatform == SelectedVodPlatform)
            return activeVodChannelSearchTask;

        activeVodChannelSearchGeneration = vodChannelSearchController.AdvanceGeneration();
        activeVodChannelSearchPlatform = SelectedVodPlatform;
        activeVodChannelSearchTask = RunVodChannelSearchAsync(TwitchVodSearchText.Trim(),
            SelectedVodPlatform, activeVodChannelSearchGeneration);
        return activeVodChannelSearchTask;
    }

    private async Task RunVodChannelSearchAsync(string query, PlatformKind platform, int generation)
    {
        var cancellation = vodChannelSearchController.BeginOperation(lifetimeCancellation.Token);
        VodChannelSearchResults.Clear();
        hasVodChannelSearchCompleted = false;
        RaiseVodChannelSearchResultsChanged();
        IsVodChannelSearchRunning = true;
        VodChannelSearchStatus = $"Searching {platform} streamers for {query}…";
        TwitchVodStatus = VodChannelSearchStatus;
        StatusMessage = VodChannelSearchStatus;
        try
        {
            var result = await vodChannelSearchService!.SearchAsync(
                new StreamSearchRequest(query, Mode: StreamSearchMode.ChannelDiscovery, Platform: platform),
                Settings, cancellation.Token);
            if (!IsCurrentVodChannelSearch(generation, query, platform) || cancellation.IsCancellationRequested) return;

            foreach (var channel in (result.IsAvailable ? result.Channels : [])
                .Where(channel => channel.Platform == platform &&
                    StreamInputParser.TryFromChannel(platform, channel.Channel, out _))
                .DistinctBy(channel => channel.Channel, StringComparer.OrdinalIgnoreCase))
            {
                VodChannelSearchResults.Add(new VodChannelSearchResultViewModel(channel,
                    item => CreateCommand(() => SelectVodChannelAsync(item),
                        () => !disposed && item.Platform == SelectedVodPlatform && VodChannelSearchResults.Contains(item))));
            }

            hasVodChannelSearchCompleted = true;
            VodChannelSearchStatus = result.Message;
            TwitchVodStatus = result.Message;
            StatusMessage = result.Message;
            RaiseVodChannelSearchResultsChanged();

            // An exact login can also be someone else's partial-name query ("timmy"
            // matches "iiTzTimmy"). Keep every choice visible when multiple rows match.
            var exact = VodChannelSearchResults.FirstOrDefault(channel =>
                string.Equals(channel.Channel, query.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
            if (VodChannelSearchResults.Count == 1 && exact is not null && isVodChannelSearchOpen)
                await SelectVodChannelAsync(exact);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentVodChannelSearch(generation, query, platform)) return;
            hasVodChannelSearchCompleted = true;
            VodChannelSearchStatus = $"{platform} streamer search failed. Try again.";
            TwitchVodStatus = VodChannelSearchStatus;
            StatusMessage = VodChannelSearchStatus;
            RaiseVodChannelSearchResultsChanged();
            logger.Write(AppLogLevel.Error, "VODs", $"{platform} streamer discovery failed.", ex);
        }
        finally
        {
            if (IsCurrentVodChannelSearch(generation, query, platform)) IsVodChannelSearchRunning = false;
            vodChannelSearchController.Complete(cancellation);
        }
    }

    private bool IsCurrentVodChannelSearch(int generation, string query, PlatformKind platform) =>
        !disposed && platform == SelectedVodPlatform &&
        vodChannelSearchController.IsCurrent(generation, query, () => TwitchVodSearchText, () => disposed);

    private Task SelectVodChannelAsync(VodChannelSearchResultViewModel channel)
    {
        if (disposed || channel.Platform != SelectedVodPlatform || !VodChannelSearchResults.Contains(channel))
            return Task.CompletedTask;
        return SearchChannelVodsAsync(channel.Platform, channel.Channel);
    }

    internal Task SearchChannelVodsAsync(PlatformKind platform, string channel)
    {
        if (disposed) return Task.CompletedTask;
        suppressAutomaticVodSearch = true;
        try
        {
            SelectVodPlatform(platform);
            TwitchVodSearchText = channel;
        }
        finally
        {
            suppressAutomaticVodSearch = false;
        }

        return SearchTwitchVodsAsync(reset: true);
    }

    internal void ShowVodChannelSearchResults()
    {
        if (!disposed && HasTwitchVodSearchText &&
            (IsVodChannelSearchRunning || hasVodChannelSearchCompleted || HasVodChannelSearchResults))
            SetVodChannelSearchOpen(true);
    }

    internal void DismissVodChannelSearchResults()
    {
        CancelTwitchVodSearchDebounce();
        SetVodChannelSearchOpen(false);
    }

    private void ResetVodChannelSearch()
    {
        CancelVodChannelSearch();
        vodStreamerResolved = false;
        VodChannelSearchResults.Clear();
        hasVodChannelSearchCompleted = false;
        VodChannelSearchStatus = "";
        SetVodChannelSearchOpen(false);
        RaiseVodChannelSearchResultsChanged();
    }

    private void CancelVodChannelSearch()
    {
        vodChannelSearchController.AdvanceGeneration();
        vodChannelSearchController.CancelActive();
        IsVodChannelSearchRunning = false;
    }

    private void SetVodChannelSearchOpen(bool value) =>
        SetProperty(ref isVodChannelSearchOpen, value, nameof(IsVodChannelSearchVisible));

    private void RaiseVodChannelSearchResultsChanged()
    {
        OnPropertyChanged(nameof(HasVodChannelSearchResults));
        OnPropertyChanged(nameof(IsVodChannelSearchVisible));
        OnPropertyChanged(nameof(VodChannelSearchResultsTitle));
    }
}
