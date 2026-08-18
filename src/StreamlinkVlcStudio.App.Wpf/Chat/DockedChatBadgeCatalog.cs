using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;
using StreamlinkVlcStudio.Core.Text;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Processes;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class DockedChatBadgeCatalog
{
    private const int MaxJsonBytes = 2 * 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly BoundedProcessRunner ProcessRunner = new();

    private readonly object sync = new();
    private readonly Dictionary<string, DockedChatEmoteImage> badges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<int>> twitchNumericBadgeVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<int>> kickNumericBadgeVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> catalogScopeByBadgeKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> badgeKeysByCatalogScope = new(StringComparer.OrdinalIgnoreCase);
    private readonly CatalogLoadCoordinator loadCoordinator;
    private string twitchClientId = "";
    private string twitchOAuthToken = "";
    private int catalogChangedQueued;

    public static DockedChatBadgeCatalog Shared { get; } = new();

    internal DockedChatBadgeCatalog()
    {
        loadCoordinator = new CatalogLoadCoordinator(scopeEvicted: EvictCatalogScope);
    }

    public event EventHandler? CatalogChanged;

    public void ConfigureTwitchCredentials(string? clientId, string? oauthToken)
    {
        var normalizedClientId = NormalizeCredential(clientId);
        var normalizedOAuthToken = NormalizeOAuthToken(oauthToken);
        var changed = false;
        lock (sync)
        {
            if (string.Equals(twitchClientId, normalizedClientId, StringComparison.Ordinal) &&
                string.Equals(twitchOAuthToken, normalizedOAuthToken, StringComparison.Ordinal))
            {
                return;
            }

            twitchClientId = normalizedClientId;
            twitchOAuthToken = normalizedOAuthToken;
            changed = true;
        }

        if (changed)
        {
            loadCoordinator.InvalidateScopes("badges:twitch:");
        }
    }

    public bool TryGet(ChatMessage message, ChatBadge badge, out DockedChatEmoteImage image)
    {
        if (!string.IsNullOrWhiteSpace(badge.ImageUrl) &&
            TryNormalizeBadgeImageUrl(message.Platform, badge.ImageUrl, out var directUrl))
        {
            image = new DockedChatEmoteImage(GetBadgeToolTip(badge), directUrl, 18, 18);
            return true;
        }

        if (message.Platform == PlatformKind.Twitch)
        {
            if (TryGetTwitchBadge(message, badge, out image))
            {
                return true;
            }
        }
        else if (message.Platform == PlatformKind.Kick &&
            TryGetKickBadge(message, badge, out image))
        {
            return true;
        }

        image = null!;
        return false;
    }

    public void EnsureForMessage(ChatMessage message)
    {
        if (message.Platform == PlatformKind.Kick)
        {
            EnsureKickGlobalLoaded();
            EnsureKickChannelLoaded(message.Channel);
            return;
        }

        if (message.Platform != PlatformKind.Twitch)
        {
            return;
        }

        EnsureGlobalLoaded();
        if (!string.IsNullOrWhiteSpace(message.RoomId))
        {
            EnsureChannelLoaded(message.RoomId);
        }
    }

    private void EnsureGlobalLoaded()
    {
        loadCoordinator.Ensure(
            "badges:twitch:bundled",
            LoadBundledTwitchBadgesAsync,
            QueueCatalogChanged,
            preserveFromEviction: true);
        loadCoordinator.Ensure(
            "badges:twitch:global",
            () => LoadTwitchBadgesAsync("https://badges.twitch.tv/v1/badges/global/display"),
            QueueCatalogChanged,
            preserveFromEviction: true);
    }

    private void EnsureKickGlobalLoaded()
    {
        loadCoordinator.Ensure(
            "badges:kick:bundled",
            LoadBundledKickBadgesAsync,
            QueueCatalogChanged,
            preserveFromEviction: true);
    }

    private void EnsureChannelLoaded(string roomId)
    {
        var normalizedRoomId = roomId.Trim();
        if (normalizedRoomId.Length == 0)
        {
            return;
        }

        var escapedRoomId = Uri.EscapeDataString(normalizedRoomId);
        loadCoordinator.Ensure(
            $"badges:twitch:channel:{normalizedRoomId}",
            () => LoadTwitchBadgesAsync(
                $"https://badges.twitch.tv/v1/badges/channels/{escapedRoomId}/display",
                normalizedRoomId),
            QueueCatalogChanged);
    }

    private void EnsureKickChannelLoaded(string channel)
    {
        var normalizedChannel = NormalizePart(channel);
        if (normalizedChannel.Length == 0)
        {
            return;
        }

        loadCoordinator.Ensure(
            $"badges:kick:channel:{normalizedChannel}",
            () => LoadKickChannelBadgesAsync(normalizedChannel),
            QueueCatalogChanged);
    }

    private async Task<CatalogLoadResult> LoadTwitchBadgesAsync(string url, string? roomId = null)
    {
        var normalizedRoomId = NormalizePart(roomId);
        var changed = false;
        var helixChanged = await LoadTwitchHelixBadgesAsync(normalizedRoomId).ConfigureAwait(false);
        if (helixChanged.HasValue)
        {
            changed = helixChanged.Value;
        }
        else
        {
            using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
            if (document is null)
            {
                throw new HttpRequestException("Twitch badge catalog was unavailable.");
            }

            if (!document.RootElement.TryGetProperty("badge_sets", out var badgeSets) ||
                badgeSets.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Twitch badge catalog was malformed.");
            }

            foreach (var badgeSet in badgeSets.EnumerateObject())
            {
                if (badgeSet.Value.ValueKind != JsonValueKind.Object ||
                    !badgeSet.Value.TryGetProperty("versions", out var versions) ||
                    versions.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var version in versions.EnumerateObject())
                {
                    if (version.Value.ValueKind != JsonValueKind.Object ||
                        !TryGetPreferredBadgeImageUrl(version.Value, out var imageUrl))
                    {
                        continue;
                    }

                    var title = TryGetNonEmptyString(version.Value, "title", out var parsedTitle)
                        ? parsedTitle
                        : GetBadgeToolTip(new ChatBadge(badgeSet.Name, version.Name));
                    changed |= AddTwitchBadge(normalizedRoomId, badgeSet.Name, version.Name, title, imageUrl);
                }
            }
        }

        return CatalogLoadResult.Successful(changed);
    }

    private async Task<bool?> LoadTwitchHelixBadgesAsync(string roomId)
    {
        var path = roomId.Length > 0
            ? $"/helix/chat/badges?broadcaster_id={Uri.EscapeDataString(roomId)}"
            : "/helix/chat/badges/global";
        using var document = await TryGetTwitchHelixJsonAsync(path).ConfigureAwait(false);
        if (document is null ||
            !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return LoadTwitchHelixBadges(data, roomId);
    }

    private bool LoadTwitchHelixBadges(JsonElement data, string? roomId)
    {
        var changed = false;
        foreach (var badgeSet in data.EnumerateArray())
        {
            if (badgeSet.ValueKind != JsonValueKind.Object ||
                !TryGetNonEmptyString(badgeSet, "set_id", out var id) ||
                !badgeSet.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var version in versions.EnumerateArray())
            {
                if (version.ValueKind != JsonValueKind.Object ||
                    !TryGetNonEmptyString(version, "id", out var versionId) ||
                    !TryGetPreferredBadgeImageUrl(version, out var imageUrl))
                {
                    continue;
                }

                var title = TryGetNonEmptyString(version, "title", out var parsedTitle)
                    ? parsedTitle
                    : GetBadgeToolTip(new ChatBadge(id, versionId));
                if (string.IsNullOrWhiteSpace(roomId) && HasTwitchBadge(null, id, versionId))
                {
                    continue;
                }

                changed |= AddTwitchBadge(roomId, id, versionId, title, imageUrl);
            }
        }

        return changed;
    }

    private bool HasTwitchBadge(string? roomId, string id, string version)
    {
        var key = MakeTwitchBadgeKey(roomId, id, version);
        lock (sync)
        {
            return badges.ContainsKey(key);
        }
    }

    private async Task<CatalogLoadResult> LoadKickChannelBadgesAsync(string channel)
    {
        var escapedChannel = Uri.EscapeDataString(channel);
        using var httpDocument = await TryGetJsonAsync(
            $"https://kick.com/api/v2/channels/{escapedChannel}",
            $"https://kick.com/{escapedChannel}").ConfigureAwait(false);
        using var curlDocument = httpDocument is null
            ? await TryGetKickChannelJsonWithCurlAsync(channel).ConfigureAwait(false)
            : null;
        var document = httpDocument ?? curlDocument;
        if (document is null)
        {
            throw new HttpRequestException("Kick badge catalog was unavailable.");
        }

        if (!document.RootElement.TryGetProperty("subscriber_badges", out var subscriberBadges) ||
            subscriberBadges.ValueKind != JsonValueKind.Array)
        {
            return CatalogLoadResult.Successful();
        }

        var changed = false;
        foreach (var subscriberBadge in subscriberBadges.EnumerateArray())
        {
            var months = TryGetInt32(subscriberBadge, "months") ?? 0;
            if (months <= 0 ||
                !TryGetKickSubscriberBadgeImageUrl(subscriberBadge, out var imageUrl))
            {
                continue;
            }

            changed |= AddKickBadge(
                channel,
                "subscriber",
                months.ToString(CultureInfo.InvariantCulture),
                $"{months}-Month Subscriber",
                imageUrl);
        }

        return CatalogLoadResult.Successful(changed);
    }

    private async Task<CatalogLoadResult> LoadBundledTwitchBadgesAsync()
    {
        return CatalogLoadResult.Successful(
            await LoadBundledBadges(BundledBadgeAssets.FindTwitchBadgeManifestPath(), AddTwitchBadge).ConfigureAwait(false));
    }

    private async Task<CatalogLoadResult> LoadBundledKickBadgesAsync()
    {
        return CatalogLoadResult.Successful(
            await LoadBundledBadges(BundledBadgeAssets.FindKickBadgeManifestPath(), AddKickBadge).ConfigureAwait(false));
    }

    private void QueueCatalogChanged()
    {
        if (Interlocked.Exchange(ref catalogChangedQueued, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref catalogChangedQueued, 0);
            CatalogLoadCoordinator.RaiseSafely(CatalogChanged, this);
        });
    }

    private async Task<bool> LoadBundledBadges(
        string? manifestPath,
        Func<string?, string, string, string, string, bool> addBadge)
    {
        if (manifestPath is null)
        {
            return false;
        }

        try
        {
            var bytes = await BoundedByteReader.ReadFileAsync(manifestPath, MaxJsonBytes).ConfigureAwait(false);
            if (bytes is null)
            {
                return false;
            }

            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var root = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var changed = false;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!TryGetNonEmptyString(entry, "id", out var id) ||
                    !TryGetNonEmptyString(entry, "version", out var version) ||
                    !TryGetNonEmptyString(entry, "image", out var relativeImagePath))
                {
                    continue;
                }

                var imagePath = Path.GetFullPath(Path.Combine(
                    root,
                    relativeImagePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!imagePath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(imagePath))
                {
                    continue;
                }

                var title = TryGetNonEmptyString(entry, "title", out var parsedTitle)
                    ? parsedTitle
                    : GetBadgeToolTip(new ChatBadge(id, version));
                changed |= addBadge(null, id, version, title, new Uri(imagePath).AbsoluteUri);
            }

            return changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private bool AddTwitchBadge(string? roomId, string id, string version, string title, string imageUrl)
    {
        var normalizedRoomId = NormalizePart(roomId);
        var normalizedId = NormalizePart(id);
        var normalizedVersion = NormalizePart(version);
        if (normalizedId.Length == 0 ||
            normalizedVersion.Length == 0 ||
            !TryNormalizeBadgeAssetUrl(imageUrl, out var normalizedUrl))
        {
            return false;
        }

        var key = MakeTwitchBadgeKey(normalizedRoomId, normalizedId, normalizedVersion);
        var badge = new DockedChatEmoteImage(
            ChatTextNormalizer.NormalizeBadgeTitle(title, key),
            normalizedUrl,
            18,
            18);

        lock (sync)
        {
            TrackCatalogBadgeLocked(key, GetTwitchPayloadScope(normalizedRoomId));
            if (normalizedRoomId.Length > 0 &&
                IsTwitchSubscriberBadgeId(normalizedId) &&
                int.TryParse(normalizedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var versionNumber) &&
                versionNumber >= 0)
            {
                var versionKey = MakeTwitchBadgeVersionKey(normalizedRoomId, normalizedId);
                if (!twitchNumericBadgeVersions.TryGetValue(versionKey, out var versions))
                {
                    versions = [];
                    twitchNumericBadgeVersions[versionKey] = versions;
                }

                if (!versions.Contains(versionNumber))
                {
                    versions.Add(versionNumber);
                    versions.Sort();
                }
            }

            if (badges.TryGetValue(key, out var existing) && existing.Equals(badge))
            {
                return false;
            }

            badges[key] = badge;
            return true;
        }
    }

    private bool TryGetTwitchBadge(ChatMessage message, ChatBadge badge, out DockedChatEmoteImage image)
    {
        var ids = CandidateTwitchBadgeIds(badge.Id).ToArray();
        if (ids.Length == 0)
        {
            image = null!;
            return false;
        }

        var roomId = NormalizePart(message.RoomId);
        if (roomId.Length > 0 && TryGetTwitchBadgeFromScope(roomId, ids, badge.Version, out image))
        {
            return true;
        }

        if (IsTwitchChannelScopedBadgeId(badge.Id))
        {
            image = null!;
            return false;
        }

        return TryGetTwitchBadgeFromScope("", ids, badge.Version, out image);
    }

    private bool TryGetTwitchBadgeFromScope(string roomId, IReadOnlyList<string> ids, string? requestedVersion, out DockedChatEmoteImage image)
    {
        foreach (var id in ids)
        {
            foreach (var version in CandidateTwitchBadgeVersions(roomId, id, requestedVersion))
            {
                var key = MakeTwitchBadgeKey(roomId, id, version);
                lock (sync)
                {
                    if (badges.TryGetValue(key, out image!))
                    {
                        return true;
                    }
                }
            }
        }

        image = null!;
        return false;
    }

    private bool TryGetTwitchNumericBadgeVersion(string roomId, string id, string? requestedVersion, out string version)
    {
        var normalizedVersion = NormalizePart(requestedVersion);
        if (!IsTwitchSubscriberBadgeId(id) ||
            !int.TryParse(normalizedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) ||
            requested < 0)
        {
            version = "";
            return false;
        }

        var versionKey = MakeTwitchBadgeVersionKey(roomId, id);
        lock (sync)
        {
            if (!twitchNumericBadgeVersions.TryGetValue(versionKey, out var versions))
            {
                version = "";
                return false;
            }

            var selected = -1;
            foreach (var candidate in versions)
            {
                if (candidate > requested)
                {
                    break;
                }

                selected = candidate;
            }

            if (selected < 0)
            {
                version = "";
                return false;
            }

            version = selected.ToString(CultureInfo.InvariantCulture);
            return true;
        }
    }

    private bool AddKickBadge(string? channel, string id, string version, string title, string imageUrl)
    {
        var normalizedChannel = NormalizePart(channel);
        var normalizedId = KickBadgeIdNormalizer.Normalize(id);
        var normalizedVersion = NormalizePart(version);
        if (normalizedId.Length == 0 ||
            normalizedVersion.Length == 0 ||
            !TryNormalizeKickBadgeCatalogUrl(imageUrl, out var normalizedUrl))
        {
            return false;
        }

        var key = MakeKickBadgeKey(normalizedChannel, normalizedId, normalizedVersion);
        var badge = new DockedChatEmoteImage(
            ChatTextNormalizer.NormalizeBadgeTitle(title, key),
            normalizedUrl,
            36,
            36);

        lock (sync)
        {
            TrackCatalogBadgeLocked(key, GetKickPayloadScope(normalizedChannel));
            if (int.TryParse(normalizedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var versionNumber) &&
                versionNumber > 0)
            {
                var versionKey = MakeKickBadgeVersionKey(normalizedChannel, normalizedId);
                if (!kickNumericBadgeVersions.TryGetValue(versionKey, out var versions))
                {
                    versions = [];
                    kickNumericBadgeVersions[versionKey] = versions;
                }

                if (!versions.Contains(versionNumber))
                {
                    versions.Add(versionNumber);
                    versions.Sort();
                }
            }

            if (badges.TryGetValue(key, out var existing) && existing.Equals(badge))
            {
                return false;
            }

            badges[key] = badge;
            return true;
        }
    }

    private bool TryGetKickBadge(ChatMessage message, ChatBadge badge, out DockedChatEmoteImage image)
    {
        var channel = NormalizePart(message.Channel);
        if (channel.Length == 0)
        {
            image = null!;
            return false;
        }

        foreach (var id in CandidateKickBadgeIds(badge.Id))
        {
            foreach (var version in CandidateKickBadgeVersions(badge.Version))
            {
                var key = MakeKickBadgeKey(channel, id, version);
                lock (sync)
                {
                    if (badges.TryGetValue(key, out image!))
                    {
                        return true;
                    }
                }
            }

            foreach (var version in CandidateKickBadgeVersions(badge.Version))
            {
                var key = MakeKickBadgeKey(null, id, version);
                lock (sync)
                {
                    if (badges.TryGetValue(key, out image!))
                    {
                        return true;
                    }
                }
            }

            if (TryGetKickNumericBadge(channel, id, badge.Version, out image))
            {
                return true;
            }

            if (TryGetKickNumericBadge("", id, badge.Version, out image))
            {
                return true;
            }
        }

        image = null!;
        return false;
    }

    private bool TryGetKickNumericBadge(string channel, string id, string? requestedVersion, out DockedChatEmoteImage image)
    {
        var normalizedVersion = NormalizePart(requestedVersion);
        if (!int.TryParse(normalizedVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested) ||
            requested <= 0)
        {
            image = null!;
            return false;
        }

        var versionKey = MakeKickBadgeVersionKey(channel, id);
        lock (sync)
        {
            if (!kickNumericBadgeVersions.TryGetValue(versionKey, out var versions))
            {
                image = null!;
                return false;
            }

            for (var index = versions.Count - 1; index >= 0; index--)
            {
                var version = versions[index];
                if (version > requested)
                {
                    continue;
                }

                var key = MakeKickBadgeKey(channel, id, version.ToString(CultureInfo.InvariantCulture));
                if (badges.TryGetValue(key, out image!))
                {
                    return true;
                }
            }
        }

        image = null!;
        return false;
    }

    private static IEnumerable<string> CandidateTwitchBadgeIds(string? id)
    {
        var normalized = NormalizePart(id);
        if (normalized.Length == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[]
        {
            normalized,
            normalized.Replace('_', '-'),
            normalized.Replace('-', '_'),
            NormalizeTwitchBadgeAlias(normalized)
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<string> CandidateTwitchBadgeVersions(string roomId, string id, string? version)
    {
        var normalized = NormalizePart(version);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
        {
            yield return normalized;
        }

        if (roomId.Length > 0 &&
            TryGetTwitchNumericBadgeVersion(roomId, id, normalized, out var selectedVersion) &&
            seen.Add(selectedVersion))
        {
            yield return selectedVersion;
        }

        foreach (var candidate in new[] { "1", "0" })
        {
            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string NormalizeTwitchBadgeAlias(string id)
    {
        return id.Replace('_', '-') switch
        {
            "artist-badge" => "artist-badge",
            "bits-leader" => "bits-leader",
            "bot-badge" => "bot-badge",
            "clip-champ" => "clip-champ",
            "clips-leader" => "clips-leader",
            "game-developer" => "game-developer",
            "hype-train" => "hype-train",
            "sub-gift" => "sub-gifter",
            "sub-gift-leader" => "sub-gift-leader",
            "sub-gifter" => "sub-gifter",
            "sub-gifter-badge" => "sub-gifter",
            "subgifter" => "sub-gifter",
            "twitch-dj" => "twitch-dj",
            _ => id
        };
    }

    private static bool IsTwitchSubscriberBadgeId(string? id)
    {
        return NormalizePart(id).Replace('_', '-') is "subscriber" or "sub";
    }

    private static bool IsTwitchChannelScopedBadgeId(string? id)
    {
        return IsTwitchSubscriberBadgeId(id);
    }

    private static string MakeTwitchBadgeKey(string? roomId, string id, string version)
    {
        var normalizedRoomId = NormalizePart(roomId);
        var scope = normalizedRoomId.Length > 0
            ? $"channel/{normalizedRoomId}"
            : "global";
        return $"twitch/{scope}/{NormalizePart(id)}/{NormalizePart(version)}";
    }

    private static string MakeTwitchBadgeVersionKey(string roomId, string id)
    {
        return $"twitch/channel/{NormalizePart(roomId)}/{NormalizePart(id)}";
    }

    private static string GetTwitchPayloadScope(string? roomId)
    {
        var normalized = NormalizePart(roomId);
        return normalized.Length == 0
            ? "badges:twitch:global"
            : $"badges:twitch:channel:{normalized}";
    }

    private static IEnumerable<string> CandidateKickBadgeIds(string? id)
    {
        var normalized = KickBadgeIdNormalizer.Normalize(id);
        if (normalized.Length == 0)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[]
        {
            normalized,
            normalized.Replace('_', '-'),
            normalized.Replace('-', '_')
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> CandidateKickBadgeVersions(string? version)
    {
        var normalized = NormalizePart(version);
        if (normalized.Length > 0)
        {
            yield return normalized;
            yield break;
        }

        yield return "1";
        yield return "0";
    }

    private static string MakeKickBadgeKey(string? channel, string id, string version)
    {
        return $"kick/{MakeKickBadgeScope(channel)}/{KickBadgeIdNormalizer.Normalize(id)}/{NormalizePart(version)}";
    }

    private static string MakeKickBadgeVersionKey(string? channel, string id)
    {
        return $"kick/{MakeKickBadgeScope(channel)}/{KickBadgeIdNormalizer.Normalize(id)}";
    }

    private static string GetKickPayloadScope(string? channel)
    {
        var normalized = NormalizePart(channel);
        return normalized.Length == 0
            ? "badges:kick:global"
            : $"badges:kick:channel:{normalized}";
    }

    private void TrackCatalogBadgeLocked(string key, string scope)
    {
        if (catalogScopeByBadgeKey.Remove(key, out var previousScope) &&
            badgeKeysByCatalogScope.TryGetValue(previousScope, out var previousKeys))
        {
            previousKeys.Remove(key);
            if (previousKeys.Count == 0)
            {
                badgeKeysByCatalogScope.Remove(previousScope);
            }
        }

        catalogScopeByBadgeKey[key] = scope;
        if (!badgeKeysByCatalogScope.TryGetValue(scope, out var keys))
        {
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            badgeKeysByCatalogScope[scope] = keys;
        }

        keys.Add(key);
    }

    private void EvictCatalogScope(string scope)
    {
        var changed = false;
        lock (sync)
        {
            if (badgeKeysByCatalogScope.Remove(scope, out var keys))
            {
                foreach (var key in keys)
                {
                    if (catalogScopeByBadgeKey.TryGetValue(key, out var owner) &&
                        string.Equals(owner, scope, StringComparison.OrdinalIgnoreCase))
                    {
                        catalogScopeByBadgeKey.Remove(key);
                        changed |= badges.Remove(key);
                    }
                }
            }

            if (scope.StartsWith("badges:twitch:channel:", StringComparison.OrdinalIgnoreCase))
            {
                var roomId = scope["badges:twitch:channel:".Length..];
                RemoveVersionKeysLocked(twitchNumericBadgeVersions, $"twitch/channel/{roomId}/");
            }
            else if (scope.StartsWith("badges:kick:channel:", StringComparison.OrdinalIgnoreCase))
            {
                var channel = scope["badges:kick:channel:".Length..];
                RemoveVersionKeysLocked(kickNumericBadgeVersions, $"kick/channel/{channel}/");
            }
        }

        if (changed)
        {
            QueueCatalogChanged();
        }
    }

    private static void RemoveVersionKeysLocked(Dictionary<string, List<int>> versions, string prefix)
    {
        foreach (var key in versions.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            versions.Remove(key);
        }
    }

    private static string MakeKickBadgeScope(string? channel)
    {
        var normalizedChannel = NormalizePart(channel);
        return normalizedChannel.Length > 0 ? $"channel/{normalizedChannel}" : "global";
    }

    private static string NormalizePart(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
    }

    private static string NormalizeCredential(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static string NormalizeOAuthToken(string? value)
    {
        var token = NormalizeCredential(value);
        return token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)
            ? token[6..].Trim()
            : token;
    }

    private static string GetBadgeToolTip(ChatBadge badge)
    {
        var title = ChatTextNormalizer.NormalizeBadgeTitle(badge.Title, badge.Id);
        var version = ChatTextNormalizer.NormalizeSingleLine(badge.Version, 64);
        return version.Length == 0 ||
            title.Contains(version, StringComparison.OrdinalIgnoreCase)
                ? title
                : $"{title} ({version})";
    }

    private static bool TryGetPreferredBadgeImageUrl(JsonElement version, out string imageUrl)
    {
        return TryGetNonEmptyString(version, "image_url_2x", out imageUrl) ||
            TryGetNonEmptyString(version, "image_url_4x", out imageUrl) ||
            TryGetNonEmptyString(version, "image_url_1x", out imageUrl);
    }

    private static bool TryGetKickSubscriberBadgeImageUrl(JsonElement item, out string imageUrl)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty("badge_image", out var badgeImage) &&
                badgeImage.ValueKind == JsonValueKind.Object &&
                TryGetNonEmptyString(badgeImage, "src", out imageUrl))
            {
                return true;
            }

            if (TryGetNonEmptyString(item, "image_url", out imageUrl) ||
                TryGetNonEmptyString(item, "imageUrl", out imageUrl) ||
                TryGetNonEmptyString(item, "src", out imageUrl))
            {
                return true;
            }
        }

        imageUrl = "";
        return false;
    }

    private static async Task<JsonDocument?> TryGetJsonAsync(string url, string? referer = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(referer) &&
                Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                request.Headers.Referrer = refererUri;
            }

            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await BoundedByteReader.ReadAsync(response.Content, MaxJsonBytes).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            return JsonDocument.Parse(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<JsonDocument?> TryGetTwitchHelixJsonAsync(string path)
    {
        string clientId;
        string oauthToken;
        lock (sync)
        {
            clientId = twitchClientId;
            oauthToken = twitchOAuthToken;
        }

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(oauthToken) ||
            !Uri.TryCreate(new Uri("https://api.twitch.tv"), path, out var uri))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Client-Id", clientId);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {oauthToken}");
            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await BoundedByteReader.ReadAsync(response.Content, MaxJsonBytes).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            return JsonDocument.Parse(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<JsonDocument?> TryGetKickChannelJsonWithCurlAsync(string channel)
    {
        var curlPath = KickCurlArguments.ResolveCurlPath();

        var escapedChannel = Uri.EscapeDataString(channel);
        try
        {
            var startInfo = BoundedProcessRunner.CreateRedirectedStartInfo(
                curlPath,
                KickCurlArguments.BuildJsonRequest(
                    $"https://kick.com/api/v2/channels/{escapedChannel}",
                    $"https://kick.com/{escapedChannel}"));
            var result = await ProcessRunner.RunAsync(
                startInfo,
                TimeSpan.FromSeconds(18)).ConfigureAwait(false);
            if (result.TimedOut ||
                result.ExitCode != 0 ||
                result.OutputWasTruncated ||
                string.IsNullOrWhiteSpace(result.StandardOutput) ||
                result.StandardOutput.Length > MaxJsonBytes)
            {
                return null;
            }

            return JsonDocument.Parse(result.StandardOutput);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Win32Exception or IOException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryNormalizeBadgeAssetUrl(string url, out string normalized)
    {
        normalized = url.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = "https:" + normalized;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            normalized = uri.ToString();
            return true;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) && uri.IsFile)
        {
            normalized = uri.AbsoluteUri;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeKickBadgeCatalogUrl(string url, out string normalized)
    {
        if (TryNormalizeBadgeImageUrl(PlatformKind.Kick, url, out normalized))
        {
            return true;
        }

        normalized = url.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) &&
            uri.IsFile)
        {
            normalized = uri.AbsoluteUri;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeBadgeImageUrl(PlatformKind platform, string url, out string normalized)
    {
        normalized = url.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = "https:" + normalized;
        }
        else if (normalized.StartsWith("/", StringComparison.Ordinal) && platform == PlatformKind.Kick)
        {
            normalized = "https://kick.com" + normalized;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (platform == PlatformKind.Kick && !IsKickAssetHost(uri))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Length == 0 ||
            extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKickAssetHost(Uri uri)
    {
        return string.Equals(uri.Host, "kick.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".kick.com", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = HttpClientFactory.Create(
            TimeSpan.FromSeconds(8),
            includeUserAgent: true,
            acceptJson: true);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }
}
