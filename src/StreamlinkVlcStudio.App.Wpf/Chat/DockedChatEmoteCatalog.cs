using System.Net.Http;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class DockedChatEmoteCatalog
{
    private const int MaxJsonBytes = 4 * 1024 * 1024;
    private const int MaxMessageSuppliedEmotes = 4_096;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly object sync = new();
    private readonly Dictionary<string, DockedChatEmoteImage> emotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> catalogScopeByEmoteKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> emoteKeysByCatalogScope = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkedListNode<string>> messageEmoteNodes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> messageEmoteLru = [];
    private readonly CatalogLoadCoordinator loadCoordinator;
    private int catalogChangedQueued;

    public static DockedChatEmoteCatalog Shared { get; } = new();

    internal DockedChatEmoteCatalog()
    {
        loadCoordinator = new CatalogLoadCoordinator(scopeEvicted: EvictCatalogScope);
    }

    public event EventHandler? CatalogChanged;

    public bool TryGet(ChatMessage message, string code, out DockedChatEmoteImage emote)
    {
        lock (sync)
        {
            var channelKey = MakeEmoteKey(message.Platform, message.Channel, code);
            if (emotes.TryGetValue(channelKey, out emote!))
            {
                TouchMessageEmoteLocked(channelKey);
                return true;
            }

            var globalKey = MakeEmoteKey(message.Platform, "", code);
            if (emotes.TryGetValue(globalKey, out emote!))
            {
                TouchMessageEmoteLocked(globalKey);
                return true;
            }

            return false;
        }
    }

    public void EnsureForMessage(ChatMessage message)
    {
        if (message.Emotes is { Count: > 0 })
        {
            var changed = false;
            foreach (var emote in message.Emotes)
            {
                if (!string.IsNullOrWhiteSpace(emote.ImageUrl))
                {
                    changed |= AddMessageEmote(
                        message.Platform,
                        message.Channel,
                        emote.Code,
                        emote.ImageUrl,
                        28,
                        28);
                }
            }

            if (changed)
            {
                QueueCatalogChanged();
            }
        }

        if (message.Platform != PlatformKind.Twitch)
        {
            return;
        }

        EnsureGlobalLoaded();
        if (!string.IsNullOrWhiteSpace(message.RoomId))
        {
            EnsureTwitchChannelLoaded(message.RoomId, message.Channel);
        }
    }

    public void EnsureGlobalLoaded()
    {
        loadCoordinator.Ensure(
            "twitch:global",
            LoadGlobalAsync,
            QueueCatalogChanged,
            preserveFromEviction: true);
    }

    private void EnsureTwitchChannelLoaded(string roomId, string channel)
    {
        var normalizedRoomId = roomId.Trim();
        if (normalizedRoomId.Length == 0)
        {
            return;
        }

        var normalizedChannel = channel.Trim().ToLowerInvariant();
        loadCoordinator.Ensure(
            $"twitch:{normalizedRoomId}:{normalizedChannel}",
            () => LoadTwitchChannelAsync(normalizedRoomId, normalizedChannel),
            QueueCatalogChanged);
    }

    private async Task<CatalogLoadResult> LoadGlobalAsync()
    {
        const string scope = "twitch:global";
        var changed = AddBuiltinFallbacks(PlatformKind.Twitch, "", scope);
        var succeeded = false;
        var result = await LoadBttvAsync(
            "https://api.betterttv.net/3/cached/emotes/global",
            PlatformKind.Twitch,
            "",
            scope).ConfigureAwait(false);
        succeeded |= result.Succeeded;
        changed |= result.Changed;
        result = await LoadFfzAsync(
            "https://api.frankerfacez.com/v1/set/global",
            PlatformKind.Twitch,
            "",
            scope).ConfigureAwait(false);
        succeeded |= result.Succeeded;
        changed |= result.Changed;
        result = await LoadSevenTvAsync(
            "https://7tv.io/v3/emote-sets/global",
            PlatformKind.Twitch,
            "",
            scope).ConfigureAwait(false);
        succeeded |= result.Succeeded;
        changed |= result.Changed;
        result = await LoadTwitchFallbackAsync(
            "https://emotes.crippled.dev/v1/global/twitch",
            PlatformKind.Twitch,
            "",
            scope).ConfigureAwait(false);
        succeeded |= result.Succeeded;
        changed |= result.Changed;
        return new CatalogLoadResult(succeeded, changed);
    }

    private async Task<CatalogLoadResult> LoadTwitchChannelAsync(string roomId, string channel)
    {
        var escapedRoomId = Uri.EscapeDataString(roomId);
        var escapedChannel = Uri.EscapeDataString(channel);
        var scope = $"twitch:{roomId}:{channel}";
        var result = await LoadBttvAsync(
            $"https://api.betterttv.net/3/cached/users/twitch/{escapedRoomId}",
            PlatformKind.Twitch,
            channel,
            scope).ConfigureAwait(false);
        var succeeded = result.Succeeded;
        var changed = result.Changed;
        result = await LoadFfzAsync(
            $"https://api.frankerfacez.com/v1/room/id/{escapedRoomId}",
            PlatformKind.Twitch,
            channel,
            scope).ConfigureAwait(false);
        succeeded |= result.Succeeded;
        changed |= result.Changed;
        result = await LoadSevenTvAsync(
            $"https://7tv.io/v3/users/twitch/{escapedRoomId}",
            PlatformKind.Twitch,
            channel,
            scope).ConfigureAwait(false);
        succeeded |= result.Succeeded;
        changed |= result.Changed;
        result = await LoadTwitchFallbackAsync(
            $"https://emotes.crippled.dev/v1/channel/{escapedChannel}/twitch",
            PlatformKind.Twitch,
            channel,
            scope).ConfigureAwait(false);
        succeeded |= result.Succeeded;
        changed |= result.Changed;
        return new CatalogLoadResult(succeeded, changed);
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

    private async Task<CatalogLoadResult> LoadBttvAsync(
        string url,
        PlatformKind platform,
        string channel,
        string scope)
    {
        using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
        if (document is null)
        {
            return CatalogLoadResult.Failed();
        }

        var changed = false;
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            if (!TryGetNonEmptyString(item, "id", out var id) ||
                !TryGetNonEmptyString(item, "code", out var code) ||
                (TryGetBool(item, "modifier") ?? false))
            {
                continue;
            }

            var imageType = TryGetNonEmptyString(item, "imageType", out var parsedImageType) &&
                !string.IsNullOrWhiteSpace(parsedImageType)
                    ? parsedImageType
                    : "png";

            changed |= AddCatalogEmote(
                platform,
                channel,
                code,
                $"https://cdn.betterttv.net/emote/{Uri.EscapeDataString(id)}/2x.{imageType}",
                TryGetInt32(item, "width") ?? 0,
                TryGetInt32(item, "height") ?? 0,
                scope);
        }

        return CatalogLoadResult.Successful(changed);
    }

    private async Task<CatalogLoadResult> LoadFfzAsync(
        string url,
        PlatformKind platform,
        string channel,
        string scope)
    {
        using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
        if (document is null)
        {
            return CatalogLoadResult.Failed();
        }

        var changed = false;
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            if (!TryGetNonEmptyString(item, "name", out var code) ||
                !item.TryGetProperty("urls", out var urls) ||
                urls.ValueKind != JsonValueKind.Object ||
                !(TryGetNonEmptyString(urls, "2", out var imageUrl) || TryGetNonEmptyString(urls, "1", out imageUrl)) ||
                (TryGetBool(item, "modifier") ?? false))
            {
                continue;
            }

            changed |= AddCatalogEmote(
                platform,
                channel,
                code,
                imageUrl,
                TryGetInt32(item, "width") ?? 0,
                TryGetInt32(item, "height") ?? 0,
                scope);
        }

        return CatalogLoadResult.Successful(changed);
    }

    private async Task<CatalogLoadResult> LoadSevenTvAsync(
        string url,
        PlatformKind platform,
        string channel,
        string scope)
    {
        using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
        if (document is null)
        {
            return CatalogLoadResult.Failed();
        }

        var changed = false;
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            if (!TryGetNonEmptyString(item, "name", out var code) ||
                !TryGetSevenTvHost(item, out var host) ||
                !TryGetNonEmptyString(host, "url", out var baseUrl) ||
                !TryGetSevenTvFileName(host, out var fileName, out var width, out var height))
            {
                continue;
            }

            changed |= AddCatalogEmote(
                platform,
                channel,
                code,
                $"{baseUrl.TrimEnd('/')}/{fileName}",
                width,
                height,
                scope);
        }

        return CatalogLoadResult.Successful(changed);
    }

    private async Task<CatalogLoadResult> LoadTwitchFallbackAsync(
        string url,
        PlatformKind platform,
        string channel,
        string scope)
    {
        using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
        if (document is null)
        {
            return CatalogLoadResult.Failed();
        }

        var changed = false;
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            if ((TryGetInt32(item, "provider") ?? -1) != 0 ||
                (TryGetBool(item, "zero_width") ?? false) ||
                !TryGetNonEmptyString(item, "code", out var code) ||
                !TryFindString(item, "url", out var imageUrl))
            {
                continue;
            }

            changed |= AddCatalogEmote(platform, channel, code, imageUrl, 28, 28, scope);
        }

        return CatalogLoadResult.Successful(changed);
    }

    private bool AddBuiltinFallbacks(PlatformKind platform, string channel, string scope)
    {
        var changed = false;
        changed |= AddCatalogEmote(platform, channel, "RespectfullyNo", "https://cdn.7tv.app/emote/01K65KFQ64QEPPWVW055JMNBWY/2x.gif", 32, 32, scope);
        changed |= AddCatalogEmote(platform, channel, "RespectfullyNO", "https://cdn.7tv.app/emote/01K7SRC7DHWSB0AGB81DM10T26/2x.gif", 32, 32, scope);
        changed |= AddCatalogEmote(platform, channel, "respectfullyno", "https://cdn.7tv.app/emote/01K6N6FWRAEPB4BBNNFYV1G5MJ/2x.gif", 32, 32, scope);
        changed |= AddCatalogEmote(platform, channel, "Uppies", "https://cdn.betterttv.net/emote/61818ec01f8ff7628e6c1e0b/2x.gif", 28, 28, scope);
        changed |= AddCatalogEmote(platform, channel, "Yo", "https://cdn.7tv.app/emote/01GKFRT59000047SF1NR3YD3WA/2x.gif", 32, 32, scope);
        changed |= AddCatalogEmote(platform, channel, "nickmercsW", "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_61905b27c9b649e8af5c92e1a5c3cd64/static/light/2.0", 28, 28, scope);
        return changed;
    }

    internal bool AddEmote(
        PlatformKind platform,
        string channel,
        string code,
        string imageUrl,
        int width,
        int height) => AddEmoteCore(platform, channel, code, imageUrl, width, height, null, false);

    private bool AddMessageEmote(
        PlatformKind platform,
        string channel,
        string code,
        string imageUrl,
        int width,
        int height) => AddEmoteCore(platform, channel, code, imageUrl, width, height, null, true);

    private bool AddCatalogEmote(
        PlatformKind platform,
        string channel,
        string code,
        string imageUrl,
        int width,
        int height,
        string scope) => AddEmoteCore(platform, channel, code, imageUrl, width, height, scope, false);

    private bool AddEmoteCore(
        PlatformKind platform,
        string channel,
        string code,
        string imageUrl,
        int width,
        int height,
        string? catalogScope,
        bool messageSupplied)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.Length >= 96 ||
            string.IsNullOrWhiteSpace(imageUrl) ||
            !TryNormalizeHttpsUrl(imageUrl, out var normalizedUrl))
        {
            return false;
        }

        var emote = new DockedChatEmoteImage(code, PreferSharperEmoteUrl(normalizedUrl), width, height);
        var key = MakeEmoteKey(platform, channel, code);
        lock (sync)
        {
            var changed = !emotes.TryGetValue(key, out var existing) || !existing.Equals(emote);
            emotes[key] = emote;
            if (messageSupplied)
            {
                UntrackCatalogEmoteLocked(key);
                TouchOrAddMessageEmoteLocked(key);
            }
            else
            {
                RemoveMessageEmoteTrackingLocked(key);
                UntrackCatalogEmoteLocked(key);
                if (catalogScope is not null)
                {
                    catalogScopeByEmoteKey[key] = catalogScope;
                    if (!emoteKeysByCatalogScope.TryGetValue(catalogScope, out var keys))
                    {
                        keys = new HashSet<string>(StringComparer.Ordinal);
                        emoteKeysByCatalogScope[catalogScope] = keys;
                    }

                    keys.Add(key);
                }
            }

            return changed;
        }
    }

    internal int MessageSuppliedEmoteCountForTest
    {
        get
        {
            lock (sync)
            {
                return messageEmoteNodes.Count;
            }
        }
    }

    private void EvictCatalogScope(string scope)
    {
        var changed = false;
        lock (sync)
        {
            if (!emoteKeysByCatalogScope.Remove(scope, out var keys))
            {
                return;
            }

            foreach (var key in keys)
            {
                if (catalogScopeByEmoteKey.TryGetValue(key, out var owner) &&
                    string.Equals(owner, scope, StringComparison.OrdinalIgnoreCase))
                {
                    catalogScopeByEmoteKey.Remove(key);
                    changed |= emotes.Remove(key);
                }
            }
        }

        if (changed)
        {
            QueueCatalogChanged();
        }
    }

    private void TouchOrAddMessageEmoteLocked(string key)
    {
        if (messageEmoteNodes.TryGetValue(key, out var existing))
        {
            messageEmoteLru.Remove(existing);
            messageEmoteLru.AddLast(existing);
        }
        else
        {
            messageEmoteNodes[key] = messageEmoteLru.AddLast(key);
        }

        while (messageEmoteNodes.Count > MaxMessageSuppliedEmotes)
        {
            var oldest = messageEmoteLru.First!;
            messageEmoteLru.RemoveFirst();
            messageEmoteNodes.Remove(oldest.Value);
            emotes.Remove(oldest.Value);
        }
    }

    private void TouchMessageEmoteLocked(string key)
    {
        if (messageEmoteNodes.TryGetValue(key, out var node))
        {
            messageEmoteLru.Remove(node);
            messageEmoteLru.AddLast(node);
        }
    }

    private void RemoveMessageEmoteTrackingLocked(string key)
    {
        if (messageEmoteNodes.Remove(key, out var node))
        {
            messageEmoteLru.Remove(node);
        }
    }

    private void UntrackCatalogEmoteLocked(string key)
    {
        if (!catalogScopeByEmoteKey.Remove(key, out var scope) ||
            !emoteKeysByCatalogScope.TryGetValue(scope, out var keys))
        {
            return;
        }

        keys.Remove(key);
        if (keys.Count == 0)
        {
            emoteKeysByCatalogScope.Remove(scope);
        }
    }

    private static string MakeEmoteKey(PlatformKind platform, string? channel, string code) =>
        string.Join(
            '|',
            platform.ToString(),
            (channel ?? "").Trim().ToLowerInvariant(),
            code.Trim());

    private static async Task<JsonDocument?> TryGetJsonAsync(string url)
    {
        try
        {
            using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
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

    private static HttpClient CreateHttpClient()
    {
        var client = HttpClientFactory.Create(
            TimeSpan.FromSeconds(8),
            includeUserAgent: true,
            acceptJson: true);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var child in EnumerateObjects(property.Value))
                {
                    yield return child;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in EnumerateObjects(item))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool TryGetSevenTvHost(JsonElement item, out JsonElement host)
    {
        if (item.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("host", out host) &&
            host.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (item.TryGetProperty("host", out host) && host.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        host = default;
        return false;
    }

    private static bool TryGetSevenTvFileName(
        JsonElement host,
        out string fileName,
        out int width,
        out int height)
    {
        fileName = "";
        width = 0;
        height = 0;
        if (!host.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var preferred in new[]
                 {
                     "2x.gif", "2x.webp", "2x.png", "2x.avif",
                     "1x.gif", "1x.webp", "1x.png", "1x.avif"
                 })
        {
            foreach (var file in files.EnumerateArray())
            {
                if (TryGetNonEmptyString(file, "name", out var candidate) &&
                    string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = candidate;
                    width = TryGetInt32(file, "width") ?? 0;
                    height = TryGetInt32(file, "height") ?? 0;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindString(JsonElement item, string propertyName, out string value)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString() ?? "";
                    return value.Length > 0;
                }

                if (TryFindString(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (item.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in item.EnumerateArray())
            {
                if (TryFindString(child, propertyName, out value))
                {
                    return true;
                }
            }
        }

        value = "";
        return false;
    }


    private static bool TryNormalizeHttpsUrl(string url, out string normalized)
    {
        normalized = url.Trim();
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = "https:" + normalized;
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static string PreferSharperEmoteUrl(string imageUrl)
    {
        if (imageUrl.Contains("cdn.betterttv.net/emote/", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("cdn.7tv.app/emote/", StringComparison.OrdinalIgnoreCase))
        {
            return imageUrl
                .Replace("/1x.gif", "/2x.gif", StringComparison.OrdinalIgnoreCase)
                .Replace("/1x.png", "/2x.png", StringComparison.OrdinalIgnoreCase)
                .Replace("/1x.webp", "/2x.webp", StringComparison.OrdinalIgnoreCase)
                .Replace("/1x.avif", "/2x.avif", StringComparison.OrdinalIgnoreCase);
        }

        if (imageUrl.Contains("static-cdn.jtvnw.net/emoticons/", StringComparison.OrdinalIgnoreCase) &&
            imageUrl.Contains("/default/", StringComparison.OrdinalIgnoreCase))
        {
            imageUrl = imageUrl.Replace(
                "/default/",
                "/static/",
                StringComparison.OrdinalIgnoreCase);
        }

        if (imageUrl.Contains("static-cdn.jtvnw.net/emoticons/", StringComparison.OrdinalIgnoreCase) &&
            imageUrl.EndsWith("/1.0", StringComparison.OrdinalIgnoreCase))
        {
            return imageUrl[..^4] + "/2.0";
        }

        return imageUrl;
    }
}

internal sealed record DockedChatEmoteImage(string Code, string ImageUrl, int Width, int Height);
