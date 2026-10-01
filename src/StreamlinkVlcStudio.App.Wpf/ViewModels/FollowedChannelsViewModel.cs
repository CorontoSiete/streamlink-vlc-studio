using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using StreamlinkVlcStudio.App.Wpf.Notifications;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

internal sealed class FollowedChannelsViewModel : HomeFeatureViewModel
{
    private readonly Func<LiveStreamCardViewModel, bool, Task> openStream;
    private readonly Func<FollowedChannel, Task> openOfflineChannel;
    internal FollowedChannelsViewModel(MainViewModelDependencies dependencies, Action<string> setStatus,
        Func<LiveStreamCardViewModel, bool, Task> openStream,
        Func<FollowedChannel, Task> openOfflineChannel) : base(dependencies, setStatus)
    {
        this.openStream = openStream;
        this.openOfflineChannel = openOfflineChannel;
        settingsService = dependencies.SettingsService;
        followedStreamsService = dependencies.FollowedStreamsService;
        liveNotificationService = dependencies.LiveNotificationService;
        kickFollowedChannelsImporter = dependencies.KickFollowedChannelsImporter;
        followedChannelsRefreshInterval = dependencies.FollowedChannelsRefreshInterval ?? DefaultFollowedChannelsRefreshInterval;
        kickFollowedChannelsText = FormatKickFollowedChannelsText(Settings.FollowedChannels.KickChannelSlugs);
        RefreshFollowedChannelsCommand = CreateCommand(RefreshFollowedChannelsAsync, () => followedStreamsService is not null);
        ImportKickFollowsCommand = CreateCommand(ImportKickFollowsAsync,
            () => kickFollowedChannelsImporter is not null && Volatile.Read(ref kickFollowImportBusy) == 0);
        ClearImportedKickFollowsCommand = CreateCommand(ClearImportedKickFollowsAsync,
            () => Volatile.Read(ref kickFollowImportBusy) == 0);
        LiveFollowedChannels.CollectionChanged += LiveFollowedChannelsOnCollectionChanged;
        OfflineFollowedChannels.CollectionChanged += OfflineFollowedChannelsOnCollectionChanged;
        Settings.PropertyChanged += SettingsOnPropertyChanged;
        ObserveFollowedChannelsSettings(Settings.FollowedChannels);
    }

    internal void Initialize()
    {
        if (disposed || followedStreamsService is null) return;
        EnsureFollowedChannelsRefreshTimerStarted();
        _ = RefreshFollowedChannelsAsync();
    }
    private void SettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!disposed && e.PropertyName == nameof(AppSettings.FollowedChannels))
            ObserveFollowedChannelsSettings(Settings.FollowedChannels);
    }
    protected override void StopOperations()
    {
        followedChannelsRefreshCancellation.Cancel();
        lock (followedChannelsRefreshTimerGate)
        {
            followedChannelsRefreshTimer?.Dispose();
            followedChannelsRefreshTimer = null;
        }
        LiveFollowedChannels.CollectionChanged -= LiveFollowedChannelsOnCollectionChanged;
        OfflineFollowedChannels.CollectionChanged -= OfflineFollowedChannelsOnCollectionChanged;
        Settings.PropertyChanged -= SettingsOnPropertyChanged;
        if (observedFollowedChannelsSettings is not null)
        {
            observedFollowedChannelsSettings.PropertyChanged -= FollowedChannelsSettingsOnPropertyChanged;
            observedFollowedChannelsSettings = null;
        }
    }
    protected override void ReleaseResources()
    {
        followedChannelsRefreshCancellation.Dispose();
        followedChannelsRefreshGate.Dispose();
    }
    private static readonly TimeSpan DefaultFollowedChannelsRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly ISettingsService settingsService;
    private readonly IFollowedStreamsService? followedStreamsService;
    private readonly IKickFollowedChannelsImporter? kickFollowedChannelsImporter;
    private string kickFollowImportStatus = "";
    private int kickFollowImportBusy;
    private readonly ILiveNotificationService? liveNotificationService;
    private FollowedChannelsSettings? observedFollowedChannelsSettings;
    private HashSet<string>? previousLiveFollowedKeys;
    private readonly HashSet<PlatformKind> baselinedLivePlatforms = [];
    private readonly TimeSpan followedChannelsRefreshInterval;
    private readonly object followedChannelsRefreshTimerGate = new();
    private readonly object followedChannelsRefreshTaskGate = new();
    private readonly SemaphoreSlim followedChannelsRefreshGate = new(1, 1);
    private readonly CancellationTokenSource followedChannelsRefreshCancellation = new();
    private string followedChannelsStatus = "Live followed channels are not loaded";
    private string offlineFollowedChannelsStatus = "Offline followed channels are not loaded.";
    private string kickFollowedChannelsText;
    private DateTimeOffset? followedChannelsLastUpdatedAt;
    private bool isFollowedChannelsRefreshing;
    private Task? activeFollowedChannelsRefreshTask;
    private string activeFollowedChannelsRefreshKey = "";
    private int followedChannelsRefreshGeneration;
    private int followedChannelsAutomaticRefreshActive;
    private System.Threading.Timer? followedChannelsRefreshTimer;
    public ObservableCollection<LiveStreamCardViewModel> LiveFollowedChannels { get; } = [];
    public ObservableCollection<OfflineFollowedChannelViewModel> OfflineFollowedChannels { get; } = [];
    public AsyncRelayCommand RefreshFollowedChannelsCommand { get; }
    public AsyncRelayCommand ImportKickFollowsCommand { get; }
    public AsyncRelayCommand ClearImportedKickFollowsCommand { get; }

    public string FollowedChannelsStatus
    {
        get => followedChannelsStatus;
        internal set => SetProperty(ref followedChannelsStatus, value);
    }

    public string OfflineFollowedChannelsStatus
    {
        get => offlineFollowedChannelsStatus;
        private set => SetProperty(ref offlineFollowedChannelsStatus, value);
    }

    public bool IsFollowedChannelsRefreshing
    {
        get => isFollowedChannelsRefreshing;
        internal set
        {
            if (SetProperty(ref isFollowedChannelsRefreshing, value))
            {
                OnPropertyChanged(nameof(IsFollowedChannelsEmptyVisible));
                OnPropertyChanged(nameof(IsOfflineFollowedChannelsEmptyVisible));
            }
        }
    }

    public bool HasLiveFollowedChannels => LiveFollowedChannels.Count > 0;

    public bool IsFollowedChannelsEmptyVisible => !IsFollowedChannelsRefreshing && LiveFollowedChannels.Count == 0;

    public bool HasOfflineFollowedChannels => OfflineFollowedChannels.Count > 0;

    public bool IsOfflineFollowedChannelsEmptyVisible => !IsFollowedChannelsRefreshing && !HasOfflineFollowedChannels;

    public string OfflineFollowedChannelsCountText => $"{OfflineFollowedChannels.Count} offline";

    public string FollowedChannelsLastUpdatedText => followedChannelsLastUpdatedAt is { } updatedAt
        ? $"Updated {updatedAt.ToLocalTime():g}"
        : "";

    public string KickFollowedChannelsText
    {
        get => kickFollowedChannelsText;
        set
        {
            if (SetProperty(ref kickFollowedChannelsText, value ?? ""))
            {
                Settings.FollowedChannels.KickChannelSlugs = ParseKickFollowedChannelSlugs(
                    kickFollowedChannelsText, skipInvalidEntries: true, out _);
            }
        }
    }

    public string KickFollowImportStatus
    {
        get => kickFollowImportStatus;
        internal set => SetProperty(ref kickFollowImportStatus, value);
    }

    public string KickImportedFollowsSummary => Settings.FollowedChannels.KickFollowsImportedAtUtc is { } importedAt
        ? $"{Settings.FollowedChannels.KickImportedChannelSlugs.Count} Kick follows imported • {importedAt.ToLocalTime():g}"
        : "No Kick follows imported yet.";

    internal Task RefreshFollowedChannelsAsync()
    {
        lock (followedChannelsRefreshTaskGate)
        {
            if (disposed) return Task.CompletedTask;

            var chat = Settings.Chat;
            var key = OAuthTokenHelpers.CreateCredentialFingerprint(
                KickFollowedChannelsText, string.Join('\n', Settings.FollowedChannels.KickImportedChannelSlugs),
                Settings.FollowedChannels.KickFollowsImportedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                chat.TwitchOAuthToken, chat.TwitchClientId,
                chat.KickOAuthToken, chat.KickRefreshToken, chat.KickClientId, chat.KickClientSecret,
                chat.KickTokenExpiresAtUtc?.ToString("O", CultureInfo.InvariantCulture));
            if (activeFollowedChannelsRefreshTask is { IsCompleted: false } active &&
                activeFollowedChannelsRefreshKey == key)
            {
                return active;
            }

            var generation = Interlocked.Increment(ref followedChannelsRefreshGeneration);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Publish before starting: synchronous providers and property-change callbacks
            // can reenter refresh while the operation is being started.
            activeFollowedChannelsRefreshTask = completion.Task;
            activeFollowedChannelsRefreshKey = key;
            backgroundOperationController.Track(completion.Task);
            _ = CompleteFollowedChannelsRefreshAsync(completion, generation, followedChannelsRefreshCancellation.Token);
            return completion.Task;
        }
    }

    internal async Task CompleteFollowedChannelsRefreshAsync(
        TaskCompletionSource completion, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshFollowedChannelsCoreAsync(generation, cancellationToken);
            completion.TrySetResult();
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    internal bool IsCurrentFollowedRefresh(int generation) =>
        !disposed && generation == Volatile.Read(ref followedChannelsRefreshGeneration);

    internal async Task RefreshFollowedChannelsCoreAsync(int generation, CancellationToken cancellationToken)
    {
        if (followedStreamsService is null)
        {
            FollowedChannelsStatus = "Live followed channels are not available.";
            OfflineFollowedChannelsStatus = "Offline followed channels are not available.";
            return;
        }

        var enteredRefreshGate = false;
        try
        {
            await followedChannelsRefreshGate.WaitAsync(cancellationToken);
            enteredRefreshGate = true;

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentFollowedRefresh(generation)) return;
            Settings.FollowedChannels.KickChannelSlugs = ParseKickFollowedChannelSlugs(
                KickFollowedChannelsText,
                skipInvalidEntries: true,
                out var invalidKickFollowedEntries);
            IsFollowedChannelsRefreshing = true;
            FollowedChannelsStatus = "Refreshing live followed channels";
            OfflineFollowedChannelsStatus = "Refreshing offline followed channels";

            var result = await followedStreamsService.GetLiveFollowedStreamsAsync(Settings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentFollowedRefresh(generation))
            {
                return;
            }

            var thumbnailCacheVersion = LiveThumbnailCacheVersion.Next();
            UpdateLiveStreamCards(LiveFollowedChannels,
                result.Streams.Select(LiveStreamCardData.FromFollowedStream), thumbnailCacheVersion);
            var liveKeys = result.Streams.Select(stream => stream.Target.StateKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            PagedResultTracker.ApplyItems(OfflineFollowedChannels,
                (result.OfflineChannels ?? []).Where(channel => !liveKeys.Contains(channel.Target.StateKey)),
                card => card.Target.StateKey, channel => channel.Target.StateKey,
                channel => new OfflineFollowedChannelViewModel(channel, OpenOfflineChannelAsync, () => !disposed),
                (card, channel) => card.Update(channel), reset: true);

            ProcessFollowedChannelLiveNotifications(result);

            followedChannelsLastUpdatedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(FollowedChannelsLastUpdatedText));

            var statusPrefix = LiveFollowedChannels.Count switch
            {
                0 => "No followed channels are live.",
                1 => "1 followed channel is live.",
                _ => $"{LiveFollowedChannels.Count} followed channels are live."
            };
            var messages = result.Messages.ToList();
            if (invalidKickFollowedEntries.Count > 0)
            {
                var invalidMessage = FormatInvalidKickFollowedChannelsMessage(invalidKickFollowedEntries.Count);
                messages.Add(invalidMessage);
                logger.Write(
                    AppLogLevel.Warning,
                    "Followed",
                    $"{invalidMessage} Entries: {string.Join(", ", invalidKickFollowedEntries)}");
            }

            FollowedChannelsStatus = messages.Count == 0
                ? statusPrefix
                : $"{statusPrefix} {string.Join(' ', messages)}";

            var offlineMessages = (result.OfflineMessages ?? result.Messages).ToList();
            if (invalidKickFollowedEntries.Count > 0)
                offlineMessages.Add(FormatInvalidKickFollowedChannelsMessage(invalidKickFollowedEntries.Count));
            var offlineStatusPrefix = OfflineFollowedChannels.Count switch
            {
                0 when offlineMessages.Count > 0 => "Offline followed channels could not be fully loaded.",
                0 => "No offline followed channels found.",
                1 => "1 followed channel is offline.",
                _ => $"{OfflineFollowedChannels.Count} followed channels are offline."
            };
            OfflineFollowedChannelsStatus = offlineMessages.Count == 0
                ? offlineStatusPrefix
                : $"{offlineStatusPrefix} {string.Join(' ', offlineMessages)}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || disposed)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsCurrentFollowedRefresh(generation))
            {
                FollowedChannelsStatus = ex.Message;
                OfflineFollowedChannelsStatus = ex.Message;
            }
            logger.Write(AppLogLevel.Warning, "Followed", "Failed to refresh live followed channels.", ex);
        }
        finally
        {
            if (enteredRefreshGate && IsCurrentFollowedRefresh(generation) && !cancellationToken.IsCancellationRequested)
            {
                IsFollowedChannelsRefreshing = false;
            }

            if (enteredRefreshGate)
            {
                followedChannelsRefreshGate.Release();
            }
        }
    }

    internal void UpdateLiveStreamCards(ObservableCollection<LiveStreamCardViewModel> cards,
        IEnumerable<LiveStreamCardData> streams, long thumbnailCacheVersion)
    {
        PagedResultTracker.ApplyItems(cards, streams,
            card => card.Target.StateKey, data => data.Target.StateKey,
            data => new LiveStreamCardViewModel(data, openStream, thumbnailCacheVersion),
            (card, data) => card.Update(data, thumbnailCacheVersion), reset: true);
    }

    private async Task OpenOfflineChannelAsync(FollowedChannel channel)
    {
        if (disposed) return;
        try
        {
            await Track(openOfflineChannel(channel));
        }
        catch (Exception ex)
        {
            if (disposed || ex is OperationCanceledException) return;
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Error, "Followed", $"Failed to browse videos for {channel.Target.DisplayName}.", ex);
        }
    }

    internal void ProcessFollowedChannelLiveNotifications(FollowedLiveStreamsResult result)
    {
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in result.Streams)
        {
            currentKeys.Add(stream.Target.StateKey);
        }

        var previousKeys = previousLiveFollowedKeys ?? [];

        if (liveNotificationService is not null && Settings.FollowedChannels.NotifyWhenLive)
        {
            foreach (var stream in result.Streams)
            {
                // Only platforms that already completed a refresh may toast: each platform's
                // first successful round seeds its baseline silently, so neither app startup nor
                // a platform that was failing when the app started announces channels that were
                // already live before we could observe them.
                if (baselinedLivePlatforms.Contains(stream.Platform) &&
                    !previousKeys.Contains(stream.Target.StateKey))
                {
                    NotifyChannelLive(stream);
                }
            }
        }

        if (result.SucceededPlatforms is null)
        {
            // Services that do not report platform health (e.g. test stubs) baseline everything.
            baselinedLivePlatforms.UnionWith(Enum.GetValues<PlatformKind>());
        }
        else
        {
            baselinedLivePlatforms.UnionWith(result.SucceededPlatforms);
        }

        previousLiveFollowedKeys = BuildNextLiveFollowedKeys(previousKeys, currentKeys, result.SucceededPlatforms);
    }

    internal static HashSet<string> BuildNextLiveFollowedKeys(
        HashSet<string> previousKeys,
        HashSet<string> currentKeys,
        IReadOnlyList<PlatformKind>? succeededPlatforms)
    {
        var nextKeys = new HashSet<string>(currentKeys, StringComparer.OrdinalIgnoreCase);

        // When the service does not report platform health (e.g. in tests), trust every
        // platform so genuinely-offline channels are pruned normally.
        if (succeededPlatforms is null)
        {
            return nextKeys;
        }

        var healthyPlatforms = new HashSet<PlatformKind>(succeededPlatforms);

        // Carry over channels that belong to a platform that failed this round. A transient
        // API error must not drop a still-live channel, otherwise it would be re-announced
        // as "live" the moment the platform recovers.
        foreach (var key in previousKeys)
        {
            if (!IsKeyForHealthyPlatform(key, healthyPlatforms))
            {
                nextKeys.Add(key);
            }
        }

        return nextKeys;
    }

    internal static bool IsKeyForHealthyPlatform(string stateKey, HashSet<PlatformKind> healthyPlatforms)
    {
        var separatorIndex = stateKey.IndexOf(':');
        if (separatorIndex > 0 &&
            Enum.TryParse<PlatformKind>(stateKey[..separatorIndex], ignoreCase: true, out var platform))
        {
            return healthyPlatforms.Contains(platform);
        }

        // Unknown key shape: treat as healthy so the carry-over set cannot grow without bound.
        return true;
    }

    internal void NotifyChannelLive(FollowedLiveStream stream)
    {
        if (liveNotificationService is null)
        {
            return;
        }

        try
        {
            var displayName = string.IsNullOrWhiteSpace(stream.DisplayName) ? stream.Channel : stream.DisplayName;
            liveNotificationService.NotifyChannelLive(new LiveChannelNotification(
                stream.Platform,
                stream.Channel,
                displayName,
                stream.Title,
                stream.CategoryName,
                stream.ViewerCount,
                stream.ThumbnailUrl));
            logger.Write(AppLogLevel.Info, "Followed", $"Live notification sent for {displayName} ({stream.Platform}).");
        }
        catch (Exception ex)
        {
            logger.Write(AppLogLevel.Warning, "Followed", $"Failed to notify that {stream.Channel} is live.", ex);
        }
    }

    internal void EnsureFollowedChannelsRefreshTimerStarted()
    {
        if (followedStreamsService is null ||
            followedChannelsRefreshInterval <= TimeSpan.Zero ||
            disposed)
        {
            return;
        }

        lock (followedChannelsRefreshTimerGate)
        {
            if (followedChannelsRefreshTimer is not null || disposed)
            {
                return;
            }

            followedChannelsRefreshTimer = new System.Threading.Timer(
                _ => RefreshFollowedChannelsOnUi(),
                null,
                followedChannelsRefreshInterval,
                followedChannelsRefreshInterval);
        }
    }

    internal void RefreshFollowedChannelsOnUi()
    {
        if (disposed || followedStreamsService is null || followedChannelsRefreshCancellation.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref followedChannelsAutomaticRefreshActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            dispatch(() =>
            {
                if (disposed ||
                    followedStreamsService is null ||
                    followedChannelsRefreshCancellation.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref followedChannelsAutomaticRefreshActive, 0);
                    return;
                }

                var refreshTask = RefreshFollowedChannelsAsync();
                backgroundOperationController.Track(ReleaseFollowedChannelsAutomaticRefreshAfterAsync(refreshTask));
            });
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref followedChannelsAutomaticRefreshActive, 0);
            logger.Write(AppLogLevel.Warning, "Followed", "Failed to schedule live followed channels refresh.", ex);
        }
    }

    internal async Task ReleaseFollowedChannelsAutomaticRefreshAfterAsync(Task refreshTask)
    {
        try
        {
            await refreshTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (followedChannelsRefreshCancellation.IsCancellationRequested || disposed)
        {
        }
        finally
        {
            Interlocked.Exchange(ref followedChannelsAutomaticRefreshActive, 0);
        }
    }

    internal async Task ImportKickFollowsAsync()
    {
        if (kickFollowedChannelsImporter is null) return;
        if (Interlocked.CompareExchange(ref kickFollowImportBusy, 1, 0) != 0) return;
        var token = lifetimeCancellation.Token;
        try
        {
            ClearImportedKickFollowsCommand.RaiseCanExecuteChanged();
            KickFollowImportStatus = "Sign in to Kick and import your follows in the opened window.";
            var channels = await kickFollowedChannelsImporter.ImportAsync(token);
            token.ThrowIfCancellationRequested();
            if (channels is null)
            {
                KickFollowImportStatus = "Detection canceled. Your saved follows were kept.";
                return;
            }
            await SaveImportedKickFollowsAsync(channels, DateTimeOffset.UtcNow, token);
            token.ThrowIfCancellationRequested();
            KickFollowImportStatus = $"Imported {channels.Count} Kick follows. Detect again after following or unfollowing channels.";
            await RefreshFollowedChannelsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (!disposed)
        {
            KickFollowImportStatus = $"Kick follows could not be imported: {ex.Message}";
        }
        finally
        {
            Volatile.Write(ref kickFollowImportBusy, 0);
            ClearImportedKickFollowsCommand.RaiseCanExecuteChanged();
        }
    }

    internal async Task ClearImportedKickFollowsAsync()
    {
        if (Interlocked.CompareExchange(ref kickFollowImportBusy, 1, 0) != 0) return;
        var token = lifetimeCancellation.Token;
        try
        {
            ImportKickFollowsCommand.RaiseCanExecuteChanged();
            await SaveImportedKickFollowsAsync([], null, token);
            token.ThrowIfCancellationRequested();
            KickFollowImportStatus = "Imported Kick follows cleared.";
            await RefreshFollowedChannelsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (!disposed) { KickFollowImportStatus = ex.Message; }
        finally
        {
            Volatile.Write(ref kickFollowImportBusy, 0);
            ImportKickFollowsCommand.RaiseCanExecuteChanged();
        }
    }

    internal async Task SaveImportedKickFollowsAsync(
        IReadOnlyList<string> channels, DateTimeOffset? importedAt, CancellationToken token)
    {
        var settings = Settings.FollowedChannels;
        var previousChannels = settings.KickImportedChannelSlugs;
        var previousTime = settings.KickFollowsImportedAtUtc;
        settings.KickImportedChannelSlugs = channels.ToList();
        settings.KickFollowsImportedAtUtc = importedAt;
        try { await settingsService.SaveAsync(Settings, token); }
        catch
        {
            settings.KickImportedChannelSlugs = previousChannels;
            settings.KickFollowsImportedAtUtc = previousTime;
            throw;
        }
        OnPropertyChanged(nameof(KickImportedFollowsSummary));
    }

    internal void ObserveFollowedChannelsSettings(FollowedChannelsSettings settings)
    {
        if (!ReferenceEquals(observedFollowedChannelsSettings, settings))
        {
            if (observedFollowedChannelsSettings is not null)
            {
                observedFollowedChannelsSettings.PropertyChanged -= FollowedChannelsSettingsOnPropertyChanged;
            }

            observedFollowedChannelsSettings = settings;
            observedFollowedChannelsSettings.PropertyChanged += FollowedChannelsSettingsOnPropertyChanged;
        }

        ApplyLiveNotificationSetting();
    }

    internal void FollowedChannelsSettingsOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FollowedChannelsSettings.NotifyWhenLive))
        {
            ApplyLiveNotificationSetting();
        }
    }

    internal void ApplyLiveNotificationSetting()
    {
        if (liveNotificationService is not null)
        {
            liveNotificationService.IsEnabled = Settings.FollowedChannels.NotifyWhenLive;
        }
    }

    internal static string FormatKickFollowedChannelsText(IEnumerable<string> slugs)
    {
        return string.Join(Environment.NewLine, slugs);
    }

    internal static List<string> ParseKickFollowedChannelSlugs(string text)
    {
        return ParseKickFollowedChannelSlugs(text, skipInvalidEntries: false, out _);
    }

    internal static List<string> ParseKickFollowedChannelSlugs(
        string text,
        bool skipInvalidEntries,
        out IReadOnlyList<string> invalidEntries)
    {
        var slugs = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = (text ?? "").Split(
            ['\r', '\n', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            var normalized = entry.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!TryParseKickFollowedChannelEntry(normalized, out var target, out var errorMessage))
            {
                if (skipInvalidEntries)
                {
                    invalid.Add(normalized);
                    continue;
                }

                throw new FormatException(errorMessage);
            }

            if (seen.Add(target!.Channel))
            {
                slugs.Add(target.Channel);
            }
        }

        invalidEntries = invalid;
        return slugs;
    }

    internal static bool TryParseKickFollowedChannelEntry(
        string value,
        out StreamTarget? target,
        out string errorMessage)
    {
        if (StreamInputParser.TryParsePlatformUrl(value, out var parsedTarget) && parsedTarget is not null)
        {
            if (parsedTarget.Platform != PlatformKind.Kick)
            {
                target = null;
                errorMessage = $"Kick followed channels only accept Kick channel URLs or slugs: {value}";
                return false;
            }

            target = parsedTarget;
            errorMessage = "";
            return true;
        }

        try
        {
            target = StreamInputParser.FromChannel(PlatformKind.Kick, value);
            errorMessage = "";
            return true;
        }
        catch (ArgumentException ex)
        {
            target = null;
            errorMessage = ex.Message;
            return false;
        }
    }

    internal static string FormatInvalidKickFollowedChannelsMessage(int count)
    {
        return count == 1
            ? "1 invalid Kick followed channel entry was skipped."
            : $"{count} invalid Kick followed channel entries were skipped.";
    }

    internal void LiveFollowedChannelsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasLiveFollowedChannels));
        OnPropertyChanged(nameof(IsFollowedChannelsEmptyVisible));
    }

    private void OfflineFollowedChannelsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasOfflineFollowedChannels));
        OnPropertyChanged(nameof(IsOfflineFollowedChannelsEmptyVisible));
        OnPropertyChanged(nameof(OfflineFollowedChannelsCountText));
    }
}
