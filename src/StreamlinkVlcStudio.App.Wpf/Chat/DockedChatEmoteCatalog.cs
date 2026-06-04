using System.Net.Http;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal sealed class DockedChatEmoteCatalog
{
    private const int MaxJsonBytes = 4 * 1024 * 1024;
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly object sync = new();
    private readonly Dictionary<string, DockedChatEmoteImage> emotes = new(StringComparer.Ordinal);
    private readonly HashSet<string> channelLoadsStarted = new(StringComparer.OrdinalIgnoreCase);
    private int catalogChangedQueued;
    private bool globalLoadStarted;

    public static DockedChatEmoteCatalog Shared { get; } = new();

    public event EventHandler? CatalogChanged;

    public bool TryGet(string code, out DockedChatEmoteImage emote)
    {
        lock (sync)
        {
            return emotes.TryGetValue(code, out emote!);
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
                    changed |= AddEmote(emote.Code, emote.ImageUrl, 28, 28);
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
        lock (sync)
        {
            if (globalLoadStarted)
            {
                return;
            }

            globalLoadStarted = true;
        }

        _ = Task.Run(LoadGlobalAsync);
    }

    private void EnsureTwitchChannelLoaded(string roomId, string channel)
    {
        var normalizedRoomId = roomId.Trim();
        if (normalizedRoomId.Length == 0)
        {
            return;
        }

        lock (sync)
        {
            if (!channelLoadsStarted.Add($"{normalizedRoomId}|{channel}"))
            {
                return;
            }
        }

        _ = Task.Run(() => LoadTwitchChannelAsync(normalizedRoomId, channel));
    }

    private async Task LoadGlobalAsync()
    {
        var changed = AddBuiltinFallbacks();
        changed |= await LoadBttvAsync("https://api.betterttv.net/3/cached/emotes/global").ConfigureAwait(false);
        changed |= await LoadFfzAsync("https://api.frankerfacez.com/v1/set/global").ConfigureAwait(false);
        changed |= await LoadSevenTvAsync("https://7tv.io/v3/emote-sets/global").ConfigureAwait(false);
        changed |= await LoadTwitchFallbackAsync("https://emotes.crippled.dev/v1/global/twitch").ConfigureAwait(false);

        if (changed)
        {
            QueueCatalogChanged();
        }
    }

    private async Task LoadTwitchChannelAsync(string roomId, string channel)
    {
        var escapedRoomId = Uri.EscapeDataString(roomId);
        var escapedChannel = Uri.EscapeDataString(channel);
        var changed = await LoadBttvAsync($"https://api.betterttv.net/3/cached/users/twitch/{escapedRoomId}").ConfigureAwait(false);
        changed |= await LoadFfzAsync($"https://api.frankerfacez.com/v1/room/id/{escapedRoomId}").ConfigureAwait(false);
        changed |= await LoadSevenTvAsync($"https://7tv.io/v3/users/twitch/{escapedRoomId}").ConfigureAwait(false);
        changed |= await LoadTwitchFallbackAsync($"https://emotes.crippled.dev/v1/channel/{escapedChannel}/twitch").ConfigureAwait(false);

        if (changed)
        {
            QueueCatalogChanged();
        }
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
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private async Task<bool> LoadBttvAsync(string url)
    {
        using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
        if (document is null)
        {
            return false;
        }

        var changed = false;
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            if (!TryGetString(item, "id", out var id) ||
                !TryGetString(item, "code", out var code) ||
                TryGetBool(item, "modifier", false))
            {
                continue;
            }

            var imageType = TryGetString(item, "imageType", out var parsedImageType) &&
                !string.IsNullOrWhiteSpace(parsedImageType)
                    ? parsedImageType
                    : "png";

            changed |= AddEmote(
                code,
                $"https://cdn.betterttv.net/emote/{Uri.EscapeDataString(id)}/2x.{imageType}",
                TryGetInt(item, "width", 0),
                TryGetInt(item, "height", 0));
        }

        return changed;
    }

    private async Task<bool> LoadFfzAsync(string url)
    {
        using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
        if (document is null)
        {
            return false;
        }

        var changed = false;
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            if (!TryGetString(item, "name", out var code) ||
                !item.TryGetProperty("urls", out var urls) ||
                urls.ValueKind != JsonValueKind.Object ||
                !(TryGetString(urls, "2", out var imageUrl) || TryGetString(urls, "1", out imageUrl)) ||
                TryGetBool(item, "modifier", false))
            {
                continue;
            }

            changed |= AddEmote(
                code,
                imageUrl,
                TryGetInt(item, "width", 0),
                TryGetInt(item, "height", 0));
        }

        return changed;
    }

    private async Task<bool> LoadSevenTvAsync(string url)
    {
        using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
        if (document is null)
        {
            return false;
        }

        var changed = false;
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            if (!TryGetString(item, "name", out var code) ||
                !TryGetSevenTvHost(item, out var host) ||
                !TryGetString(host, "url", out var baseUrl) ||
                !TryGetSevenTvFileName(host, out var fileName))
            {
                continue;
            }

            changed |= AddEmote(code, $"{baseUrl.TrimEnd('/')}/{fileName}", 0, 0);
        }

        return changed;
    }

    private async Task<bool> LoadTwitchFallbackAsync(string url)
    {
        using var document = await TryGetJsonAsync(url).ConfigureAwait(false);
        if (document is null)
        {
            return false;
        }

        var changed = false;
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            if (TryGetInt(item, "provider", -1) != 0 ||
                TryGetBool(item, "zero_width", false) ||
                !TryGetString(item, "code", out var code) ||
                !TryFindString(item, "url", out var imageUrl))
            {
                continue;
            }

            changed |= AddEmote(code, imageUrl, 28, 28);
        }

        return changed;
    }

    private bool AddBuiltinFallbacks()
    {
        var changed = false;
        changed |= AddEmote("RespectfullyNo", "https://cdn.7tv.app/emote/01K65KFQ64QEPPWVW055JMNBWY/2x.gif", 32, 32);
        changed |= AddEmote("RespectfullyNO", "https://cdn.7tv.app/emote/01K7SRC7DHWSB0AGB81DM10T26/2x.gif", 32, 32);
        changed |= AddEmote("respectfullyno", "https://cdn.7tv.app/emote/01K6N6FWRAEPB4BBNNFYV1G5MJ/2x.gif", 32, 32);
        changed |= AddEmote("Uppies", "https://cdn.betterttv.net/emote/61818ec01f8ff7628e6c1e0b/2x.gif", 28, 28);
        changed |= AddEmote("Yo", "https://cdn.7tv.app/emote/01GKFRT59000047SF1NR3YD3WA/2x.gif", 32, 32);
        changed |= AddEmote("nickmercsW", "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_61905b27c9b649e8af5c92e1a5c3cd64/default/light/2.0", 28, 28);
        return changed;
    }

    private bool AddEmote(string code, string imageUrl, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            code.Length >= 96 ||
            string.IsNullOrWhiteSpace(imageUrl) ||
            !TryNormalizeHttpsUrl(imageUrl, out var normalizedUrl))
        {
            return false;
        }

        var emote = new DockedChatEmoteImage(code, PreferSharperEmoteUrl(normalizedUrl), width, height);
        lock (sync)
        {
            if (emotes.TryGetValue(code, out var existing) && existing.Equals(emote))
            {
                return false;
            }

            emotes[code] = emote;
            return true;
        }
    }

    private static async Task<JsonDocument?> TryGetJsonAsync(string url)
    {
        try
        {
            using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length > MaxJsonBytes)
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
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamlinkVlcStudio/0.1");
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

    private static bool TryGetSevenTvFileName(JsonElement host, out string fileName)
    {
        fileName = "";
        if (!host.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var preferred in new[] { "2x.gif", "2x.png", "1x.gif", "1x.png" })
        {
            foreach (var file in files.EnumerateArray())
            {
                if (TryGetString(file, "name", out var candidate) &&
                    string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    fileName = candidate;
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

    private static bool TryGetString(JsonElement item, string propertyName, out string value)
    {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return value.Length > 0;
        }

        value = "";
        return false;
    }

    private static int TryGetInt(JsonElement item, string propertyName, int fallback)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => fallback
        };
    }

    private static bool TryGetBool(JsonElement item, string propertyName, bool fallback)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(propertyName, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => fallback
        };
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
                .Replace("/1x.webp", "/2x.webp", StringComparison.OrdinalIgnoreCase);
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
