using System.Collections.Concurrent;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Core.Text.StringValues;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

public sealed partial class FollowedStreamsService
{
    private static readonly TimeSpan OfflineMetadataLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OfflineMetadataRetryDelay = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, OfflineMetadata> offlineMetadata = new(StringComparer.OrdinalIgnoreCase);
    internal TimeProvider MetadataTimeProvider { get; init; } = TimeProvider.System;
    internal TimeSpan OfflineMetadataTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<FollowedChannel>> EnrichOfflineChannelsAsync(
        IReadOnlyList<FollowedChannel> channels, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loads = channels.GroupBy(channel => channel.Platform).Select(async group =>
        {
            var enriched = group.ToList();
            await EnrichOfflineMetadataAsync(group.Key, enriched, cancellationToken).ConfigureAwait(false);
            return enriched;
        });
        var results = await Task.WhenAll(loads).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var byKey = results.SelectMany(result => result).ToDictionary(channel => channel.Target.StateKey,
            StringComparer.OrdinalIgnoreCase);
        return channels.Select(channel => byKey[channel.Target.StateKey]).ToArray();
    }

    private void PrepareOfflineMetadata(PlatformKind platform, IReadOnlyList<FollowedLiveStream> streams,
        List<FollowedChannel> channels)
    {
        var now = MetadataTimeProvider.GetUtcNow();
        var activeKeys = channels.Select(channel => channel.Target.StateKey)
            .Concat(streams.Select(stream => stream.Target.StateKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in offlineMetadata.Keys)
        {
            if (key.StartsWith(platform + ":", StringComparison.OrdinalIgnoreCase) && !activeKeys.Contains(key))
                offlineMetadata.TryRemove(key, out _);
        }

        foreach (var stream in streams)
        {
            // A newly ended broadcast must bypass an older offline cache entry. Its real start
            // time is also useful when the broadcaster does not save public recordings.
            offlineMetadata.AddOrUpdate(stream.Target.StateKey,
                _ => new(null, ValidBroadcastTime(stream.StartedAtUtc, now), DateTimeOffset.MinValue),
                (_, previous) => previous with
                {
                    LastLiveAtUtc = Latest(previous.LastLiveAtUtc, ValidBroadcastTime(stream.StartedAtUtc, now)),
                    ExpiresAtUtc = DateTimeOffset.MinValue
                });
        }

        if (platform == PlatformKind.Twitch)
        {
            // Keep the official offline image separate from the cached website fallback.
            // Otherwise the displayed fallback could mask a changed banner during enrichment.
            foreach (var channel in channels)
                offlineMetadata.AddOrUpdate(channel.Target.StateKey,
                    _ => new(null, null, DateTimeOffset.MinValue, channel.BannerImageUrl),
                    (_, previous) => previous with { TwitchOfflineImageUrl = channel.BannerImageUrl });
        }

        ApplyCachedOfflineMetadata(platform, channels);
    }

    private async Task EnrichOfflineMetadataAsync(PlatformKind platform, List<FollowedChannel> channels,
        CancellationToken cancellationToken)
    {
        var now = MetadataTimeProvider.GetUtcNow();
        var pending = channels.Where(channel => !offlineMetadata.TryGetValue(channel.Target.StateKey, out var cached) ||
            cached.ExpiresAtUtc <= now).ToArray();
        if (pending.Length > 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(OfflineMetadataTimeout);
            try
            {
                if (platform == PlatformKind.Twitch)
                    await LoadTwitchOfflineMetadataAsync(pending, deadline.Token).ConfigureAwait(false);
                else
                    await LoadKickOfflineMetadataAsync(pending, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Write(AppLogLevel.Info, "Followed", $"{platform} offline banner or broadcast history unavailable.", ex);
            }

            // Missing users, failed requests, and timeouts retry soon without flooding either
            // platform. Previously confirmed metadata remains usable during a transient failure.
            foreach (var channel in pending)
            {
                offlineMetadata.AddOrUpdate(channel.Target.StateKey,
                    _ => new(null, null, now + OfflineMetadataRetryDelay),
                    (_, previous) => previous.ExpiresAtUtc > now ? previous
                        : previous with { ExpiresAtUtc = now + OfflineMetadataRetryDelay });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ApplyCachedOfflineMetadata(platform, channels);
    }

    private void ApplyCachedOfflineMetadata(PlatformKind platform, List<FollowedChannel> channels)
    {
        for (var index = 0; index < channels.Count; index++)
        {
            var channel = channels[index];
            if (offlineMetadata.TryGetValue(channel.Target.StateKey, out var cached))
                channels[index] = channel with
                {
                    BannerImageUrl = platform == PlatformKind.Kick
                        ? cached.BannerImageUrl ?? channel.BannerImageUrl
                        : FirstNonEmpty(cached.TwitchOfflineImageUrl ?? channel.BannerImageUrl, cached.BannerImageUrl),
                    LastLiveAtUtc = cached.LastLiveAtUtc
                };
        }
    }

    private async Task LoadTwitchOfflineMetadataAsync(FollowedChannel[] channels, CancellationToken cancellationToken)
    {
        var transport = new TwitchGraphQlTransport(httpClient);
        foreach (var batch in channels.Chunk(100))
        {
            // Verified against Twitch's live web endpoint. lastBroadcast remains available
            // without a public VOD; a video's upload/creation time is not a last-live time.
            var payload = JsonSerializer.Serialize(new
            {
                operationName = "OfflineFollowedChannels",
                variables = new { logins = batch.Select(channel => channel.Channel).ToArray() },
                query = "query OfflineFollowedChannels($logins: [String!]!) { users(logins: $logins) { id login offlineImageURL bannerImageURL lastBroadcast { startedAt } } }"
            });
            using var document = await transport.SendAsync(payload, TwitchGraphQlTransport.PublicClientId,
                null, cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data) || !TryGetArray(data, "users", out var users))
                throw new JsonException("Twitch did not return offline channel metadata.");

            var requested = batch.ToDictionary(channel => channel.Channel, StringComparer.OrdinalIgnoreCase);
            var now = MetadataTimeProvider.GetUtcNow();
            foreach (var user in users.EnumerateArray())
            {
                if (!TryGetNonEmptyString(user, "id", out _) ||
                    !TryGetNonEmptyString(user, "login", out var login) || !requested.Remove(login, out var channel))
                    continue;
                var lastLive = user.TryGetProperty("lastBroadcast", out var broadcast)
                    ? ValidBroadcastTime(TryGetDateTimeOffset(broadcast, "startedAt"), now) : null;
                StoreOfflineMetadata(channel, FirstNonEmpty(
                    NormalizeImageUrl(GetOptionalString(user, "offlineImageURL")),
                    NormalizeImageUrl(GetOptionalString(user, "bannerImageURL"))), lastLive, now);
            }
        }
    }

    private async Task LoadKickOfflineMetadataAsync(FollowedChannel[] channels, CancellationToken cancellationToken)
    {
        var reader = new KickWebsiteJsonReader(httpClient, logger, "Followed", TimeSpan.FromSeconds(5));
        await Parallel.ForEachAsync(channels,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (channel, token) =>
            {
                string? banner = null;
                DateTimeOffset? lastLive = null;
                var bannerLoaded = false;
                var historyLoaded = false;
                try
                {
                    try
                    {
                        banner = await LoadKickOfflineBannerAsync(reader, channel, token).ConfigureAwait(false);
                        bannerLoaded = true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.Write(AppLogLevel.Info, "Followed", $"Kick offline banner unavailable for {channel.Channel}.", ex);
                    }

                    try
                    {
                        var body = await reader.ReadAsync(
                            $"https://kick.com/api/v2/channels/{Uri.EscapeDataString(channel.Channel)}/videos?limit=1&page=1",
                            channel.Url, token).ConfigureAwait(false);
                        if (body is null) return;
                        using var document = JsonDocument.Parse(body);
                        // Kick's channel API has no offline last-live field. The website's verified
                        // videos response supplies actual broadcast start_time values in UTC.
                        if (document.RootElement.ValueKind != JsonValueKind.Array)
                            throw new JsonException("Kick did not return a broadcast history array.");
                        var now = MetadataTimeProvider.GetUtcNow();
                        lastLive = document.RootElement.EnumerateArray()
                            .Where(video => TryGetBool(video, "is_live") != true)
                            .Select(video => ValidBroadcastTime(TryGetDateTimeOffset(video, "start_time"), now))
                            .Max();
                        historyLoaded = true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        logger.Write(AppLogLevel.Info, "Followed", $"Kick broadcast history unavailable for {channel.Channel}.", ex);
                    }
                }
                finally
                {
                    // Artwork and history are independent. Even a history deadline must retain
                    // a banner already loaded, while partial failures still retry after 30 seconds.
                    StoreOfflineMetadata(channel, banner, lastLive, MetadataTimeProvider.GetUtcNow(), bannerLoaded && historyLoaded);
                }
            }).ConfigureAwait(false);
    }

    private void StoreOfflineMetadata(FollowedChannel channel, string? banner, DateTimeOffset? lastLive,
        DateTimeOffset now, bool complete = true) =>
        offlineMetadata.AddOrUpdate(channel.Target.StateKey,
            _ => new(banner, lastLive, now + (complete ? OfflineMetadataLifetime : OfflineMetadataRetryDelay)),
            (_, previous) => new(banner ?? previous.BannerImageUrl, Latest(previous.LastLiveAtUtc, lastLive),
                now + (complete ? OfflineMetadataLifetime : OfflineMetadataRetryDelay), previous.TwitchOfflineImageUrl));

    private static DateTimeOffset? ValidBroadcastTime(DateTimeOffset? value, DateTimeOffset now) =>
        value is { } timestamp && timestamp > DateTimeOffset.UnixEpoch && timestamp <= now ? timestamp.ToUniversalTime() : null;

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is { } timestamp && (second is null || timestamp > second) ? first : second;

    // null means the banner lookup failed; empty means the website confirms no artwork.
    private sealed record OfflineMetadata(string? BannerImageUrl, DateTimeOffset? LastLiveAtUtc, DateTimeOffset ExpiresAtUtc,
        string? TwitchOfflineImageUrl = null);
}
