using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Threading;
using StreamlinkVlcStudio.Infrastructure.Vod;
using static StreamlinkVlcStudio.Infrastructure.LocalWeb.LocalWebRequestValues;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public sealed record LocalWebDependencies
{
    public required Func<CancellationToken, Task<AppSettings>> GetSettingsAsync { get; init; }
    public required Func<Action<AppSettings>, CancellationToken, Task> ChangeSettingsAsync { get; init; }
    public required IStreamlinkService Streamlink { get; init; }
    public required IStreamSearchService Search { get; init; }
    public required IFollowedStreamsService Following { get; init; }
    public required IBrowseService Browse { get; init; }
    public required ITwitchVodService TwitchVods { get; init; }
    public required IKickVodService KickVods { get; init; }
    public required Func<AppSettings, IChatClientFactory> ChatFactory { get; init; }
    public required IVodDownloadService Downloads { get; init; }
    public required IVodPlaybackHistory History { get; init; }
    public required IVodChatProvider VodChat { get; init; }
    public required IReplayResolver Replay { get; init; }
    public required ITwitchSubOnlyVodResolver SubOnlyVods { get; init; }
    public required Func<StreamTarget, CancellationToken, Task<Uri?>> CreateClipAsync { get; init; }
    public required Func<StreamTarget, CancellationToken, Task<ChannelFollowResult?>> GetFollowStatusAsync { get; init; }
    public required Func<StreamTarget, bool, CancellationToken, Task<ChannelFollowResult?>> FollowAsync { get; init; }
    public Func<StreamTarget, CancellationToken, Task<StreamTarget>>? ResolveKickVodAsync { get; init; }
    public ILocalWebChatPresentation? ChatPresentation { get; init; }
    public LocalWebChannelProfileClient ChannelProfiles { get; init; } = new();
    public LocalWebCategoryArtwork CategoryArtwork { get; init; } = new();
    public SemaphoreSlim PlaybackSlots { get; } = new(32, 32);
}

/// <summary>A device owns its players/chat. Desktop account credentials never enter a response.</summary>
public sealed partial class LocalWebWorkspace(LocalWebDependencies services) : ILocalWebWorkspace
{
    private readonly ConcurrentDictionary<string, Player> players = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> categoryArtworkUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamTarget> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> targetIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncOperationGate categoryViewerCountSlots = new(4);
    private readonly AsyncOperationGate followingHistorySlot = new();
    private FollowedChannel[] offlineFollowing = [];
    private readonly object playerGate = new();
    private bool disposed;
    private Task? disposalTask;
    public static IReadOnlyList<string> Qualities { get; } = Array.AsReadOnly(QualityOption.Defaults.Select(quality => quality.Id).ToArray());

    public async Task<object?> InvokeAsync(string action, JsonElement input, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        var settings = await services.GetSettingsAsync(token).ConfigureAwait(false);
        switch (action)
        {
            case "settings":
                return new
                {
                    settings.DefaultQuality,
                    settings.LowLatency,
                    theme = settings.Theme.ToString(),
                    qualities = Qualities,
                    accounts = new { twitch = !string.IsNullOrEmpty(settings.Chat.TwitchOAuthToken), kick = !string.IsNullOrEmpty(settings.Chat.KickOAuthToken) }
                };
            case "following":
                var following = await services.Following.GetLiveFollowedStreamsAsync(settings, token).ConfigureAwait(false);
                Volatile.Write(ref offlineFollowing, (following.OfflineChannels ?? []).ToArray());
                return new
                {
                    items = following.Streams.Select(s => Card(s.Target, s.DisplayName, s.Title, s.ThumbnailUrl, "Live", s.ViewerCount)).ToArray(),
                    offline = (following.OfflineChannels ?? []).Select(s => Card(s.Target, s.DisplayName, "", s.ProfileImageUrl, "Offline", lastLiveAtUtc: s.LastLiveAtUtc)).ToArray(),
                    message = string.Join(" ", following.Messages),
                };
            case "following/history":
                // Only enrich the server's status snapshot. This independent read never registers
                // targets or accepts client-supplied channel names, and cannot block playback/chat.
                using (await followingHistorySlot.EnterAsync(token).ConfigureAwait(false))
                {
                    var snapshot = Volatile.Read(ref offlineFollowing);
                    var enriched = await services.Following.EnrichOfflineChannelsAsync(snapshot, token).ConfigureAwait(false);
                    return new
                    {
                        items = enriched.Select(channel => new
                        {
                            platform = channel.Platform.ToString(),
                            channel = channel.Channel,
                            lastLiveAtUtc = channel.LastLiveAtUtc
                        }).ToArray()
                    };
                }
            case "recent":
                return new { items = settings.RecentStreams.Select(s => Card(StreamInputParser.FromChannel(s.Platform, s.Channel) with { ProfileImageUrl = s.ProfileImageUrl }, s.DisplayName, s.CategoryName, s.ThumbnailUrl, "Recent")).ToArray() };
            case "recent/remove":
                var recent = Target(input);
                await services.ChangeSettingsAsync(s => s.RecentStreams = s.RecentStreams.Where(r => r.Platform != recent.Platform || !r.Channel.Equals(recent.Channel, StringComparison.OrdinalIgnoreCase)).ToList(), token).ConfigureAwait(false);
                return new { removed = true };
            case "started":
                var playing = GetPlayer(input);
                if (!playing.Target.IsExplicitVod && !playing.RecordedRecent)
                {
                    var target = playing.Target;
                    await services.ChangeSettingsAsync(s => s.RecentStreams = s.RecentStreams
                        .Where(r => r.Platform != target.Platform || !r.Channel.Equals(target.Channel, StringComparison.OrdinalIgnoreCase))
                        .Prepend(new RecentStreamSettings
                        {
                            Platform = target.Platform,
                            Channel = target.Channel,
                            Url = target.Url,
                            DisplayName = target.Channel,
                            ProfileImageUrl = playing.ProfileImageUrl,
                            LastQuality = playing.Quality,
                            LastWatchedAtUtc = DateTimeOffset.UtcNow
                        }).Take(100).ToList(), token).ConfigureAwait(false);
                    playing.RecordedRecent = true;
                }
                return new { recorded = true };
            case "search":
                var search = await services.Search.SearchAsync(new(RequiredText(input, "query", 250), Quality(input, settings), 30,
                    StreamSearchMode.ChannelDiscovery, Platform(input)), settings, token).ConfigureAwait(false);
                return new { items = search.Channels.Select(s => Card(s.Target, s.DisplayName, s.Title, s.ThumbnailUrl, s.State.ToString(), s.ViewerCount)).ToArray(), search.Message };
            case "categories":
                var categories = await services.Browse.GetCategoriesAsync(new(Platform(input), Optional(input, "query", 100), Optional(input, "cursor", 2048)), settings, token).ConfigureAwait(false);
                return new { items = categories.Items.Select(c => new { c.Id, c.Name, thumbnailUrl = CategoryArtworkUrl(c), c.ViewerCount }).ToArray(), categories.NextCursor, categories.Message };
            case "categories/viewers":
                if (Platform(input) != PlatformKind.Twitch ||
                    !input.TryGetProperty("categoryIds", out var ids) || ids.ValueKind != JsonValueKind.Array ||
                    ids.GetArrayLength() is < 1 or > 20)
                    throw new ArgumentException("Request between 1 and 20 Twitch category IDs.");
                var categoryIds = ids.EnumerateArray().Select(id =>
                {
                    if (id.ValueKind != JsonValueKind.String || id.GetString()?.Trim() is not { Length: > 0 and <= 128 } value || value.Any(char.IsControl))
                        throw new ArgumentException("Invalid category ID.");
                    return value;
                }).Distinct(StringComparer.Ordinal).ToArray();
                using (await categoryViewerCountSlots.EnterAsync(token).ConfigureAwait(false))
                {
                    var counts = await services.Browse.GetCategoryViewerCountsAsync(new(PlatformKind.Twitch, categoryIds), settings, token).ConfigureAwait(false);
                    return new { available = counts.IsAvailable, items = counts.IsAvailable ? counts.Items : [] };
                }
            case "streams":
                var streams = await services.Browse.GetStreamsAsync(new(Platform(input), Optional(input, "categoryId", 128), Optional(input, "categoryName", 100), Optional(input, "cursor", 2048)), settings, token).ConfigureAwait(false);
                return new { items = streams.Items.Select(s => Card(s.Target, s.DisplayName, s.Title, s.ThumbnailUrl, "Live", s.ViewerCount)).ToArray(), streams.NextCursor, streams.Message };
            case "vods":
                return await VodsAsync(input, settings, token).ConfigureAwait(false);
            case "downloads":
                return await DownloadsAsync(settings, token).ConfigureAwait(false);
            case "downloads/add":
                var downloadTarget = TargetOrInput(input);
                if (!downloadTarget.IsExplicitVod) throw new ArgumentException("Select a VOD to download.");
                var queued = await services.Downloads.EnqueueAsync(new(downloadTarget, Quality(input, settings), DownloadOptions(settings)), token).ConfigureAwait(false);
                return new { queued.Id };
            case "downloads/cancel": await services.Downloads.CancelAsync(Id(input), token).ConfigureAwait(false); return new { canceled = true };
            case "downloads/retry": await services.Downloads.RetryAsync(Id(input), DownloadOptions(settings), token).ConfigureAwait(false); return new { retried = true };
            case "downloads/remove": await services.Downloads.RemoveAsync(Id(input), token).ConfigureAwait(false); return new { removed = true };
            case "open":
                var replacement = Optional(input, "replacePlayer", 64);
                if (replacement.Length > 0 && players.TryRemove(replacement, out var replaced)) await replaced.DisposeAsync().ConfigureAwait(false);
                return await OpenAsync(input, settings, token).ConfigureAwait(false);
            case "channel/profile":
                var profilePlayer = GetPlayer(input);
                var profile = await services.ChannelProfiles.GetAsync(profilePlayer.Target, token).ConfigureAwait(false);
                if (profile.ProfileImageUrl.Length > 0 && profilePlayer.ProfileImageUrl != profile.ProfileImageUrl &&
                    !LocalWebProfileImages.HasCachedImage(profilePlayer.Target))
                {
                    profilePlayer.ProfileImageUrl = profile.ProfileImageUrl;
                    if (settings.RecentStreams.Any(s => s.Platform == profilePlayer.Target.Platform &&
                        s.Channel.Equals(profilePlayer.Target.Channel, StringComparison.OrdinalIgnoreCase) && s.ProfileImageUrl != profile.ProfileImageUrl))
                        await services.ChangeSettingsAsync(s =>
                        {
                            foreach (var recentStream in s.RecentStreams.Where(r => r.Platform == profilePlayer.Target.Platform &&
                                r.Channel.Equals(profilePlayer.Target.Channel, StringComparison.OrdinalIgnoreCase))) recentStream.ProfileImageUrl = profile.ProfileImageUrl;
                        }, token).ConfigureAwait(false);
                }
                return profile with { ProfileImageUrl = profilePlayer.ProfileImageUrl };
            case "close":
                if (players.TryRemove(RequiredText(input, "player", 64), out var closed)) await closed.DisposeAsync().ConfigureAwait(false);
                return new { closed = true };
            case "chat":
                return await ReadChatAsync(input, settings, token).ConfigureAwait(false);
            case "chat/presentation":
                var chatPlayer = GetPlayer(input);
                // A failed catalog load does not change Version. Keep its bounded retry
                // coordinator moving even when a quiet channel has no new messages.
                foreach (var message in chatPlayer.PresentationMessages.Values.DistinctBy(m => (m.Platform, m.Channel, m.RoomId)))
                    services.ChatPresentation?.EnsureLoaded(message, settings.Chat);
                var version = services.ChatPresentation?.Version ?? 0;
                var knownVersion = Integer(input, "version", -1);
                return new
                {
                    version,
                    messages = knownVersion == version ? [] :
                    chatPlayer.PresentationMessages.Values.Select(m => PresentChat(chatPlayer, m, settings, remember: false)).ToArray()
                };
            case "chat/send":
                var sender = GetPlayer(input);
                if (sender.Target.IsExplicitVod || sender.Chat is null) throw new ArgumentException("Chat sending is available for live streams.");
                if (DateTimeOffset.UtcNow - sender.LastSent < TimeSpan.FromSeconds(1)) throw new ArgumentException("Wait before sending again.");
                sender.LastSent = DateTimeOffset.UtcNow;
                await sender.Chat.SendMessageAsync(RequiredText(input, "message", 500), token).ConfigureAwait(false);
                return new { sent = true };
            case "progress":
                var player = GetPlayer(input);
                if (player.Target.IsExplicitVod)
                {
                    var position = Seconds(input, "position");
                    var duration = Seconds(input, "duration");
                    var ended = Boolean(input, "ended", false);
                    if (duration > 0 && position <= duration + 5)
                    {
                        services.History.Remember(player.Target, new(TimeSpan.FromSeconds(Math.Min(position, duration)), TimeSpan.FromSeconds(duration), DateTimeOffset.UtcNow, ended));
                        await services.History.SaveAsync(token).ConfigureAwait(false);
                    }
                }
                return new { saved = true };
            case "replay":
                var livePlayer = GetPlayer(input);
                if (livePlayer.Target.IsExplicitVod) throw new ArgumentException("Already playing an archive.");
                var replay = await services.Replay.ResolveCurrentReplayAsync(livePlayer.Target, livePlayer.Quality, settings, token).ConfigureAwait(false);
                if (!replay.IsAvailable) return new { available = false, message = "The provider has no seekable replay for this stream." };
                var replayTarget = livePlayer.Target with
                {
                    Kind = replay.Platform == PlatformKind.Twitch ? StreamTargetKind.TwitchVod : StreamTargetKind.KickVod,
                    Url = replay.ReplayUrl,
                    MediaId = replay.ReplayId,
                    MediaDuration = replay.Duration,
                    MediaStartedAtUtc = replay.StreamStartedAtUtc,
                    ChatRoomId = replay.ChatRoomId,
                    DisplayTitle = livePlayer.Target.Channel + " replay"
                };
                return new { available = true, key = Register(replayTarget) };
            case "clip":
                var clipTarget = GetPlayer(input).Target;
                if (clipTarget.IsExplicitVod) throw new ArgumentException("Clips require live playback.");
                var clip = await services.CreateClipAsync(clipTarget, token).ConfigureAwait(false);
                return new { url = clip?.AbsoluteUri, message = clip is null ? "Clip was not confirmed. Check the desktop account connection." : "Clip published." };
            case "follow/status":
                var statusPlayer = GetPlayer(input);
                if (statusPlayer.Target.IsExplicitVod) throw new ArgumentException("Follow status requires a live channel.");
                // Website checks can outlast a request. Poll a player-owned task without holding the command gate.
                statusPlayer.FollowStatus ??= CheckFollowStatusAsync(statusPlayer);
                return statusPlayer.FollowStatus.IsCompleted ? await statusPlayer.FollowStatus.ConfigureAwait(false) : new FollowState(null, Pending: true);
            case "follow":
                var followTarget = GetPlayer(input).Target;
                var follow = await services.FollowAsync(followTarget, true, token).ConfigureAwait(false);
                ConfirmFollowState(followTarget, follow, true);
                return new { following = follow?.IsFollowing, message = follow is null ? "Connect the website account in the desktop app, then retry." : "Following confirmed." };
            case "unfollow":
                var unfollowTarget = GetPlayer(input).Target;
                var unfollow = await services.FollowAsync(unfollowTarget, false, token).ConfigureAwait(false);
                ConfirmFollowState(unfollowTarget, unfollow, false);
                return new { following = unfollow?.IsFollowing, message = unfollow is null ? "Unfollow was not confirmed." : "Unfollow confirmed." };
            case "prediction":
                var predictionPlayer = GetPlayer(input);
                if (predictionPlayer.Chat is not ITwitchPredictionClient predictions) throw new ArgumentException("Predictions require Twitch chat.");
                return new { prediction = predictionPlayer.Prediction, access = predictions.PredictionAccess };
            case "prediction/create":
                if (GetPlayer(input).Chat is not ITwitchPredictionClient creator) throw new ArgumentException();
                if (!input.TryGetProperty("outcomes", out var outcomeValues) || outcomeValues.ValueKind != JsonValueKind.Array ||
                    outcomeValues.GetArrayLength() != 2 || outcomeValues.EnumerateArray().Any(o => o.ValueKind != JsonValueKind.String))
                    throw new ArgumentException("Provide two prediction outcomes.");
                var outcomes = outcomeValues.EnumerateArray().Select(o => o.GetString()!).ToArray();
                if (outcomes.Any(o => o.Length is < 1 or > 25)) throw new ArgumentException("Provide two prediction outcomes.");
                return await creator.CreatePredictionAsync(new(RequiredText(input, "title", 45), outcomes, (int)Math.Clamp(Seconds(input, "window"), 30, 1800)), token).ConfigureAwait(false);
            case "prediction/action":
                if (GetPlayer(input).Chat is not ITwitchPredictionClient manager) throw new ArgumentException();
                var predictionId = RequiredText(input, "predictionId", 128);
                return RequiredText(input, "operation", 16) switch
                {
                    "lock" => await manager.LockPredictionAsync(predictionId, token).ConfigureAwait(false),
                    "cancel" => await manager.CancelPredictionAsync(predictionId, token).ConfigureAwait(false),
                    "resolve" => await manager.ResolvePredictionAsync(predictionId, RequiredText(input, "outcomeId", 128), token).ConfigureAwait(false),
                    _ => throw new ArgumentException()
                };
            default: throw new ArgumentException("Unknown action.");
        }
    }

    private sealed record FollowState(bool? Following, string Message = "", bool Pending = false);

    private async Task<FollowState> CheckFollowStatusAsync(Player player)
    {
        try
        {
            var result = await services.GetFollowStatusAsync(player.Target, player.Lifetime.Token).ConfigureAwait(false);
            player.Lifetime.Token.ThrowIfCancellationRequested();
            if (result is null) return new(null, "Follow status is unavailable. Connect the website account in the desktop app, then reopen the channel.");
            ValidateFollowTarget(player.Target, result);
            return new(result.IsFollowing);
        }
        catch (Exception)
        {
            return new(null, "Could not verify follow status. Reopen the channel to retry.");
        }
    }

    private static void ValidateFollowTarget(StreamTarget target, ChannelFollowResult result)
    {
        if (target.Platform != result.Platform || !target.Channel.Equals(result.Channel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The follow result did not match the selected broadcaster.");
    }

    private void ConfirmFollowState(StreamTarget target, ChannelFollowResult? result, bool desired)
    {
        if (result is null) return;
        ValidateFollowTarget(target, result);
        if (result.IsFollowing != desired) throw new InvalidOperationException("The requested follow state was not confirmed.");
        foreach (var player in players.Values.Where(p => p.Target.Platform == target.Platform &&
            p.Target.Channel.Equals(target.Channel, StringComparison.OrdinalIgnoreCase)))
            // Replace pending reads too: a late status response cannot overwrite a confirmed mutation.
            player.FollowStatus = Task.FromResult(new FollowState(result.IsFollowing));
    }

    private async Task<object> VodsAsync(JsonElement input, AppSettings settings, CancellationToken token)
    {
        var channel = StreamInputParser.FromChannel(Platform(input), RequiredText(input, "channel", 100));
        var cursor = Optional(input, "cursor", 2048);
        if (channel.Platform == PlatformKind.Twitch)
        {
            var type = Optional(input, "type", 16) switch { "highlight" => TwitchVodTypeFilter.Highlight, "upload" => TwitchVodTypeFilter.Upload, "all" => TwitchVodTypeFilter.All, _ => TwitchVodTypeFilter.Archive };
            var result = await services.TwitchVods.SearchAsync(new(channel.Channel, type, cursor, 50), settings, token).ConfigureAwait(false);
            var cards = new List<object>();
            foreach (var v in result.Videos.OrderByDescending(v => v.PublishedAtUtc ?? v.CreatedAtUtc))
            {
                var target = new StreamTarget(PlatformKind.Twitch, v.ChannelLogin, v.Url, StreamTargetKind.TwitchVod, v.Id, v.Title, v.BroadcasterId, v.Duration, v.CreatedAtUtc, ProfileImageUrl: v.ProfileImageUrl);
                cards.Add(await VodCardAsync(target, v.ThumbnailUrl, v.PublishedAtUtc ?? v.CreatedAtUtc, token).ConfigureAwait(false));
            }
            return new { items = cards, result.NextCursor, result.Message };
        }
        else
        {
            var result = await services.KickVods.SearchAsync(new(channel.Channel, cursor), settings, token).ConfigureAwait(false);
            var cards = new List<object>();
            foreach (var v in result.Videos.OrderByDescending(v => v.CreatedAtUtc ?? v.StartedAtUtc))
            {
                var target = new StreamTarget(PlatformKind.Kick, v.ChannelSlug, v.Source, StreamTargetKind.KickVod, v.Id, v.Title, v.ChannelId, v.Duration, v.StartedAtUtc, v.ChannelId, ProfileImageUrl: v.ProfileImageUrl);
                cards.Add(await VodCardAsync(target, v.ThumbnailUrl, v.CreatedAtUtc ?? v.StartedAtUtc, token).ConfigureAwait(false));
            }
            return new { items = cards, result.NextCursor, result.Message };
        }
    }

    private async Task<object> VodCardAsync(StreamTarget target, string thumbnail, DateTimeOffset? published, CancellationToken token)
    {
        thumbnail = LocalWebProfileImages.PublicUrl(thumbnail);
        target = target with { MediaThumbnailUrl = thumbnail, MediaPublishedAtUtc = published };
        RememberVodMetadata(target, new(thumbnail, published));
        var bookmark = await services.History.GetAsync(target, token).ConfigureAwait(false);
        return new
        {
            key = Register(target),
            title = target.DisplayTitle,
            channel = target.Channel,
            platform = target.Platform.ToString(),
            profileImageUrl = LocalWebProfileImages.PublicUrl(target.ProfileImageUrl),
            thumbnailUrl = thumbnail,
            duration = target.MediaDuration.TotalSeconds,
            published,
            resume = bookmark is { Completed: false } ? bookmark.Position.TotalSeconds : 0,
            watched = bookmark?.HasBeenWatched == true || bookmark?.Completed == true,
            state = "VOD"
        };
    }

    private async Task<object> OpenAsync(JsonElement input, AppSettings settings, CancellationToken token)
    {
        if (players.Count >= 16 || !await services.PlaybackSlots.WaitAsync(0, token).ConfigureAwait(false))
            throw new ArgumentException("The browser player limit has been reached. Close a player first.");
        Player? player = null;
        IVodDownloadPlayback? offline = null;
        var transferred = false;
        try
        {
            StreamTarget target;
            if (input.TryGetProperty("downloadId", out _))
            {
                offline = await services.Downloads.AcquireOfflinePlaybackAsync(Id(input, "downloadId"), token).ConfigureAwait(false);
                target = offline.Target;
            }
            else target = TargetOrInput(input);
            if (target.IsExplicitKickVod && !target.IsOfflineVod &&
                !new Uri(target.Url).AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                target = services.ResolveKickVodAsync is { } resolveKick
                    ? await resolveKick(target, token).ConfigureAwait(false)
                    : throw new ArgumentException("Choose a listed Kick video.");
            var quality = Quality(input, settings);
            var id = LocalWebAuthentication.NewToken();
            LocalWebMedia media;
            if (offline is not null)
            {
                var directory = Path.GetDirectoryName(target.LocalMediaPath)!;
                var package = await OfflineVodPackage.ValidateAsync(directory, token).ConfigureAwait(false);
                media = new LocalWebMedia(id, target.LocalMediaPath, package.Files.Select(f => Path.Combine(directory, f.Name)));
            }
            else
            {
                var lowLatency = Boolean(input, "lowLatency", settings.LowLatency);
                var resolved = await ResolveAsync(target, quality, lowLatency, settings, token).ConfigureAwait(false);
                media = resolved.TrustedLocal
                    ? await LocalWebMedia.FromTrustedPlaylistAsync(id, resolved.Uri, token).ConfigureAwait(false)
                    : new LocalWebMedia(id, resolved.Uri);
            }
            player = new(id, target, quality, media, offline, services.PlaybackSlots, lifetime.Token);
            player.ProfileImageUrl = target.IsOfflineVod && LocalWebProfileImages.HasCachedImage(target)
                ? $"/media/{id}/profile" : LocalWebProfileImages.PublicUrl(target.ProfileImageUrl);
            offline = null;
            if (!target.IsExplicitVod)
            {
                var chat = services.ChatFactory(settings).Create(target.Platform);
                player.Chat = chat;
                chat.MessageReceived += (_, message) => player.AddChat(message);
                chat.StatusChanged += (_, _) => player.ChatStatus = "Chat connection status changed.";
                if (chat is ITwitchPredictionClient predictions)
                    predictions.PredictionReceived += (_, prediction) => player.Prediction = prediction;
                player.ChatConnection = ConnectChatAsync(player);
            }
            var bookmark = target.IsExplicitVod ? await services.History.GetAsync(target, token).ConfigureAwait(false) : null;
            // Registration can reject a full session library. Keep the player owned by
            // this method until every fallible setup step has completed.
            var key = Register(target);
            token.ThrowIfCancellationRequested();
            lock (playerGate)
            {
                if (disposed) throw new OperationCanceledException(token);
                players[id] = player;
            }
            transferred = true;
            return new
            {
                id,
                key,
                title = target.TabTitle,
                channel = target.Channel,
                platform = target.Platform.ToString(),
                profileImageUrl = player.ProfileImageUrl,
                chatLayout = (WebChatOverlayLayout?)null,
                url = media.PathFor(media.Root),
                isVod = target.IsExplicitVod,
                isMp4 = target.LocalMediaPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase),
                resume = bookmark is { Completed: false } ? bookmark.Position.TotalSeconds : 0,
                duration = target.MediaDuration.TotalSeconds,
                quality
            };
        }
        finally
        {
            if (!transferred)
            {
                if (player is not null) await player.DisposeAsync().ConfigureAwait(false);
                else { if (offline is not null) await offline.DisposeAsync().ConfigureAwait(false); services.PlaybackSlots.Release(); }
            }
        }
    }

    private async Task<(Uri Uri, bool TrustedLocal)> ResolveAsync(StreamTarget target, string quality, bool lowLatency, AppSettings settings, CancellationToken token)
    {
        if (target.IsExplicitKickVod) return (new Uri(target.Url), false);
        if (target.IsExplicitVod && Uri.TryCreate(target.Url, UriKind.Absolute, out var direct) &&
            direct.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return (direct, false);
        try
        {
            var result = await services.Streamlink.ResolveStreamUrlAsync(new(target, quality, settings.StreamlinkPath ?? "", lowLatency,
                CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments)), token).ConfigureAwait(false);
            return (result.StreamUri, false);
        }
        catch (Exception error) when (target.IsExplicitTwitchVod && error is not OperationCanceledException)
        {
            var fallback = (await services.SubOnlyVods.ResolveAsync(new(target.MediaId, quality), token).ConfigureAwait(false)).PlaybackUri;
            return (fallback, fallback.IsFile);
        }
    }

    private static async Task ConnectChatAsync(Player player)
    {
        try
        {
            await player.Chat!.ConnectAsync(player.Target, player.Lifetime.Token).ConfigureAwait(false);
            player.ChatStatus = "Connected";
        }
        catch (OperationCanceledException) when (player.Lifetime.IsCancellationRequested) { }
        catch { player.ChatStatus = "Chat unavailable. Check the desktop account connection."; }
    }

    private async Task<object> ReadChatAsync(JsonElement input, AppSettings settings, CancellationToken token)
    {
        var player = GetPlayer(input);
        if (player.Target.IsExplicitVod)
        {
            var from = Seconds(input, "from");
            var target = player.Target;
            var replay = new ReplaySessionInfo(target.Platform, target.Channel, target.Url, target.MediaId, target.MediaStartedAtUtc,
                target.MediaDuration, true, "", player.Quality, ChatRoomId: target.ChatRoomId);
            var result = await services.VodChat.FetchAsync(replay, settings, TimeSpan.FromSeconds(from), token).ConfigureAwait(false);
            var status = result.Outcome switch
            {
                VodChatFetchOutcome.Unsupported => result.Reason,
                VodChatFetchOutcome.Failed => result.Reason + " Retrying…",
                VodChatFetchOutcome.Completed when result.Messages.Count == 0 => "No more archived chat at this position.",
                _ => "Replay chat"
            };
            return new
            {
                messages = result.Messages.Select(m => new { offset = m.Offset.TotalSeconds, message = PresentChat(player, m.Message, settings) }).ToArray(),
                through = result.CoveredThroughOffset.TotalSeconds,
                outcome = result.Outcome.ToString(),
                status,
                reason = result.Reason
            };
        }
        var after = Integer(input, "after", 0);
        return new
        {
            messages = player.ReadChat(after).Select(m => new { sequence = m.Sequence, message = PresentChat(player, m.Message, settings) }).ToArray(),
            status = player.ChatStatus,
            username = player.Chat?.CurrentUsername
        };
    }

    private ChatMessage PresentChat(Player player, ChatMessage message, AppSettings settings, bool remember = true)
    {
        if (string.IsNullOrEmpty(message.MessageId))
            message = message with { MessageId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(message))) };
        if (remember)
        {
            if (!player.PresentationMessages.ContainsKey(message.MessageId)) player.PresentationOrder.Enqueue(message.MessageId);
            player.PresentationMessages[message.MessageId] = message;
            while (player.PresentationMessages.Count > 2000) player.PresentationMessages.Remove(player.PresentationOrder.Dequeue());
        }
        return services.ChatPresentation?.Present(message, settings.Chat) ?? message;
    }

    private string CategoryArtworkUrl(BrowseCategory category)
    {
        if (category.Platform != PlatformKind.Twitch || !LocalWebCategoryArtwork.IsArtworkUrl(category.ThumbnailUrl)) return category.ThumbnailUrl;
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(category.ThumbnailUrl)));
        if (categoryArtworkUrls.Count >= 4096 && !categoryArtworkUrls.ContainsKey(id)) return "";
        categoryArtworkUrls[id] = category.ThumbnailUrl;
        return "/category-artwork/" + id;
    }

    public Task ServeCategoryArtworkAsync(HttpContext context, string assetId)
    {
        if (categoryArtworkUrls.TryGetValue(assetId, out var url)) return services.CategoryArtwork.ServeAsync(context, url);
        context.Response.StatusCode = 404;
        return Task.CompletedTask;
    }

    public Task ServeChatAssetAsync(HttpContext context, string assetId)
    {
        if (services.ChatPresentation is { } presentation) return presentation.ServeAssetAsync(context, assetId);
        context.Response.StatusCode = 404;
        return Task.CompletedTask;
    }

    public async Task ServeMediaAsync(HttpContext context, string playbackId, string resourceId)
    {
        if (!players.TryGetValue(playbackId, out var player)) { context.Response.StatusCode = 404; return; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, player.Lifetime.Token);
        context.RequestAborted = linked.Token;
        if (resourceId == "profile")
        {
            await LocalWebProfileImages.ServeCachedAsync(context, player.Target).ConfigureAwait(false);
            return;
        }
        await player.Media.ServeAsync(context, resourceId).ConfigureAwait(false);
    }

    private object Card(StreamTarget target, string name, string title, string thumbnail, string state, int? viewers = null,
        DateTimeOffset? lastLiveAtUtc = null) =>
        new
        {
            key = Register(target),
            platform = target.Platform.ToString(),
            channel = target.Channel,
            displayName = name,
            title,
            categoryName = target.CategoryName,
            thumbnailUrl = thumbnail,
            profileImageUrl = LocalWebProfileImages.PublicUrl(target.ProfileImageUrl),
            state,
            viewers,
            lastLiveAtUtc
        };

    private string Register(StreamTarget target)
    {
        if (targetIds.TryGetValue(target.TabIdentityKey, out var key))
        {
            // Refresh playback URLs, duration and chat metadata even when the provider
            // omits the avatar this time. Keep the last known picture in that case.
            targets[key] = target.ProfileImageUrl.Length > 0 ? target : target with
            {
                ProfileImageUrl = targets[key].ProfileImageUrl
            };
            return key;
        }
        if (targets.Count >= 5000) throw new InvalidDataException("Library limit reached. Sign in again to start a fresh session.");
        key = LocalWebAuthentication.NewToken();
        targetIds.Add(target.TabIdentityKey, key);
        targets.Add(key, target);
        return key;
    }

    private StreamTarget Target(JsonElement input) => targets.TryGetValue(RequiredText(input, "key", 64), out var target) ? target : throw new ArgumentException("Select a listed stream or video.");
    private StreamTarget TargetOrInput(JsonElement input)
    {
        if (input.TryGetProperty("key", out _)) return Target(input);
        var value = RequiredText(input, "input", 250);
        if (value.Contains("/videos/", StringComparison.Ordinal)) return VodDownloadUrlParser.Parse(value);
        return StreamInputParser.Parse(value, Platform(input));
    }
    private Player GetPlayer(JsonElement input) => players.TryGetValue(RequiredText(input, "player", 64), out var player) ? player : throw new ArgumentException("Player not found.");
    private static PlatformKind Platform(JsonElement input) => Optional(input, "platform", 10).ToLowerInvariant() switch { "kick" => PlatformKind.Kick, "twitch" or "" => PlatformKind.Twitch, _ => throw new ArgumentException() };
    private static string Quality(JsonElement input, AppSettings settings)
    {
        var quality = Optional(input, "quality", 20);
        if (quality.Length == 0) quality = settings.DefaultQuality;
        return Qualities.Contains(quality, StringComparer.Ordinal) ? quality : throw new ArgumentException("Unsupported quality.");
    }
    private static VodDownloadOptions DownloadOptions(AppSettings settings) => new(settings.StreamlinkPath ?? "", CommandLineTokenizer.Tokenize(settings.CustomStreamlinkArguments));

    public ValueTask DisposeAsync() => new(AsyncDisposal.Begin(playerGate, ref disposed, ref disposalTask, DisposeCoreAsync));

    private async Task DisposeCoreAsync()
    {
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        finally
        {
            categoryViewerCountSlots.Dispose();
            followingHistorySlot.Dispose();
            var cleanup = new List<Task>();
            foreach (var (id, player) in players.ToArray())
                if (players.TryRemove(id, out _)) cleanup.Add(player.DisposeAsync().AsTask());
            await Task.WhenAll(cleanup).ConfigureAwait(false);
        }
    }

    private sealed class Player(string id, StreamTarget target, string quality, LocalWebMedia media,
        IVodDownloadPlayback? offline, SemaphoreSlim slots, CancellationToken token) : IAsyncDisposable
    {
        private readonly object chatGate = new();
        private readonly object disposalGate = new();
        private readonly Queue<(long Sequence, ChatMessage Message)> messages = new();
        private long sequence;
        private bool disposed;
        private Task? disposalTask;
        internal string Id { get; } = id;
        internal StreamTarget Target { get; } = target;
        internal string Quality { get; } = quality;
        internal string ProfileImageUrl { get; set; } = "";
        internal LocalWebMedia Media { get; } = media;
        internal IChatClient? Chat { get; set; }
        internal Dictionary<string, ChatMessage> PresentationMessages { get; } = new(StringComparer.Ordinal);
        internal Queue<string> PresentationOrder { get; } = new();
        internal string ChatStatus { get; set; } = "Connecting";
        internal DateTimeOffset LastSent { get; set; }
        internal bool RecordedRecent { get; set; }
        internal TwitchPrediction? Prediction { get; set; }
        internal Task? ChatConnection { get; set; }
        internal Task<FollowState>? FollowStatus { get; set; }
        internal CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal void AddChat(ChatMessage message)
        {
            lock (chatGate)
            {
                messages.Enqueue((++sequence, message));
                while (messages.Count > 200) messages.Dequeue();
            }
        }
        internal (long Sequence, ChatMessage Message)[] ReadChat(long after)
        {
            lock (chatGate) return messages.Where(m => m.Sequence > after).ToArray();
        }
        public ValueTask DisposeAsync() => new(AsyncDisposal.Begin(disposalGate, ref disposed, ref disposalTask, DisposeCoreAsync));

        private async Task DisposeCoreAsync()
        {
            try
            {
                await Lifetime.CancelAsync().ConfigureAwait(false);
                if (ChatConnection is not null) await ChatConnection.ConfigureAwait(false);
                if (Chat is not null) await Chat.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Media.Dispose();
                try { if (offline is not null) await offline.DisposeAsync().ConfigureAwait(false); }
                finally { slots.Release(); }
            }
        }
    }
}
