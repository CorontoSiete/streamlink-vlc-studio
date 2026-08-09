using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class DockedChatBadgeCatalog
{
    private const int MaxJsonBytes = 2 * 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly object sync = new();
    private readonly Dictionary<string, DockedChatEmoteImage> badges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<int>> twitchNumericBadgeVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<int>> kickNumericBadgeVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> channelLoadsStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> kickChannelLoadsStarted = new(StringComparer.OrdinalIgnoreCase);
    private string twitchClientId = "";
    private string twitchOAuthToken = "";
    private bool globalLoadStarted;
    private bool kickGlobalLoadStarted;

    public static DockedChatBadgeCatalog Shared { get; } = new();

    public event EventHandler? CatalogChanged;

    public void ConfigureTwitchCredentials(string? clientId, string? oauthToken)
    {
        var normalizedClientId = NormalizeCredential(clientId);
        var normalizedOAuthToken = NormalizeOAuthToken(oauthToken);
        lock (sync)
        {
            if (string.Equals(twitchClientId, normalizedClientId, StringComparison.Ordinal) &&
                string.Equals(twitchOAuthToken, normalizedOAuthToken, StringComparison.Ordinal))
            {
                return;
            }

            twitchClientId = normalizedClientId;
            twitchOAuthToken = normalizedOAuthToken;
            globalLoadStarted = false;
            channelLoadsStarted.Clear();
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
        lock (sync)
        {
            if (globalLoadStarted)
            {
                return;
            }

            globalLoadStarted = true;
        }

        _ = LoadBundledTwitchBadges();
        _ = Task.Run(() => LoadTwitchBadgesAsync("https://badges.twitch.tv/v1/badges/global/display"));
    }

    private void EnsureKickGlobalLoaded()
    {
        lock (sync)
        {
            if (kickGlobalLoadStarted)
            {
                return;
            }

            kickGlobalLoadStarted = true;
        }

        if (LoadBundledKickBadges())
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnsureChannelLoaded(string roomId)
    {
        var normalizedRoomId = roomId.Trim();
        if (normalizedRoomId.Length == 0)
        {
            return;
        }

        lock (sync)
        {
            if (!channelLoadsStarted.Add(normalizedRoomId))
            {
                return;
            }
        }

        var escapedRoomId = Uri.EscapeDataString(normalizedRoomId);
        _ = Task.Run(() => LoadTwitchBadgesAsync($"https://badges.twitch.tv/v1/badges/channels/{escapedRoomId}/display", normalizedRoomId));
    }

    private void EnsureKickChannelLoaded(string channel)
    {
        var normalizedChannel = NormalizePart(channel);
        if (normalizedChannel.Length == 0)
        {
            return;
        }

        lock (sync)
        {
            if (!kickChannelLoadsStarted.Add(normalizedChannel))
            {
                return;
            }
        }

        _ = Task.Run(() => LoadKickChannelBadgesAsync(normalizedChannel));
    }

    private async Task LoadTwitchBadgesAsync(string url, string? roomId = null)
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
            if (document is not null &&
                document.RootElement.TryGetProperty("badge_sets", out var badgeSets) &&
                badgeSets.ValueKind == JsonValueKind.Object)
            {
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
        }

        if (changed)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
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

    private async Task LoadKickChannelBadgesAsync(string channel)
    {
        var escapedChannel = Uri.EscapeDataString(channel);
        using var httpDocument = await TryGetJsonAsync(
            $"https://kick.com/api/v2/channels/{escapedChannel}",
            $"https://kick.com/{escapedChannel}").ConfigureAwait(false);
        using var curlDocument = httpDocument is null
            ? await TryGetKickChannelJsonWithCurlAsync(channel).ConfigureAwait(false)
            : null;
        var document = httpDocument ?? curlDocument;
        if (document is null ||
            !document.RootElement.TryGetProperty("subscriber_badges", out var subscriberBadges) ||
            subscriberBadges.ValueKind != JsonValueKind.Array)
        {
            return;
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

        if (changed)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool LoadBundledTwitchBadges()
    {
        return LoadBundledBadges(BundledBadgeAssets.FindTwitchBadgeManifestPath(), AddTwitchBadge);
    }

    private bool LoadBundledKickBadges()
    {
        return LoadBundledBadges(BundledBadgeAssets.FindKickBadgeManifestPath(), AddKickBadge);
    }

    private bool LoadBundledBadges(
        string? manifestPath,
        Func<string?, string, string, string, string, bool> addBadge)
    {
        if (manifestPath is null)
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
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
            string.IsNullOrWhiteSpace(title) ? key : title.Trim(),
            normalizedUrl,
            18,
            18);

        lock (sync)
        {
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
        var normalizedId = NormalizeKickBadgeId(id);
        var normalizedVersion = NormalizePart(version);
        if (normalizedId.Length == 0 ||
            normalizedVersion.Length == 0 ||
            !TryNormalizeKickBadgeCatalogUrl(imageUrl, out var normalizedUrl))
        {
            return false;
        }

        var key = MakeKickBadgeKey(normalizedChannel, normalizedId, normalizedVersion);
        var badge = new DockedChatEmoteImage(
            string.IsNullOrWhiteSpace(title) ? key : title.Trim(),
            normalizedUrl,
            36,
            36);

        lock (sync)
        {
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

    private static IEnumerable<string> CandidateKickBadgeIds(string? id)
    {
        var normalized = NormalizeKickBadgeId(id);
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
            NormalizeKickBadgeId(normalized)
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

    private static string NormalizeKickBadgeId(string? id)
    {
        var normalized = NormalizePart(id).Replace('-', '_');
        return normalized switch
        {
            "gift_sub" => "sub_gifter",
            "gift_subs" => "sub_gifter",
            "gift_subscriber" => "sub_gifter",
            "gift_subscription" => "sub_gifter",
            "gifted_sub" => "sub_gifter",
            "gifted_subs" => "sub_gifter",
            "gifted_subscriber" => "sub_gifter",
            "gifted_subscription" => "sub_gifter",
            "gifter" => "sub_gifter",
            "sub" => "subscriber",
            "subscription" => "subscriber",
            "subscriptions" => "subscriber",
            "subgift" => "sub_gifter",
            "sub_gift" => "sub_gifter",
            "subgifter" => "sub_gifter",
            "sub_gifter_badge" => "sub_gifter",
            "sub_gifts" => "sub_gifter",
            "subscriber_gifter" => "sub_gifter",
            "subscription_gift" => "sub_gifter",
            "subscription_gifts" => "sub_gifter",
            _ => normalized
        };
    }

    private static string MakeKickBadgeKey(string? channel, string id, string version)
    {
        return $"kick/{MakeKickBadgeScope(channel)}/{NormalizeKickBadgeId(id)}/{NormalizePart(version)}";
    }

    private static string MakeKickBadgeVersionKey(string? channel, string id)
    {
        return $"kick/{MakeKickBadgeScope(channel)}/{NormalizeKickBadgeId(id)}";
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
        var title = string.IsNullOrWhiteSpace(badge.Title) ? badge.Id : badge.Title;
        return string.IsNullOrWhiteSpace(badge.Version) ||
            title.Contains(badge.Version.Trim(), StringComparison.OrdinalIgnoreCase)
                ? title
                : $"{title} ({badge.Version})";
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

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxJsonBytes)
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

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxJsonBytes)
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
        var curlPath = ResolveCurlPath();
        if (string.IsNullOrWhiteSpace(curlPath))
        {
            return null;
        }

        var escapedChannel = Uri.EscapeDataString(channel);
        var startInfo = new ProcessStartInfo
        {
            FileName = curlPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in BuildKickMetadataCurlArguments(escapedChannel))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }

                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 ||
                string.IsNullOrWhiteSpace(stdout) ||
                stdout.Length > MaxJsonBytes)
            {
                return null;
            }

            return JsonDocument.Parse(stdout);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Win32Exception or IOException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ResolveCurlPath()
    {
        var configured = Environment.GetEnvironmentVariable("STREAMLINK_KICK_CURL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        return "curl.exe";
    }

    private static IEnumerable<string> BuildKickMetadataCurlArguments(string escapedChannel)
    {
        yield return "--location";
        yield return "--silent";
        yield return "--show-error";
        yield return "--fail";
        yield return "--compressed";
        yield return "--max-time";
        yield return "15";
        yield return "--user-agent";
        yield return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
        yield return "--header";
        yield return "Accept: application/json,text/plain,*/*";
        yield return "--header";
        yield return "Accept-Language: *";
        yield return "--referer";
        yield return $"https://kick.com/{escapedChannel}";
        yield return $"https://kick.com/api/v2/channels/{escapedChannel}";
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
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }
}
