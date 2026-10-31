using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

/// <summary>Scopes web-only preferences to an account while leaving host services shared.</summary>
internal sealed class LocalWebUserWorkspace(ILocalWebWorkspace inner, LocalWebUserStore users, string userId) : ILocalWebWorkspace
{
    private readonly ConcurrentDictionary<string, string> playerKeys = new(StringComparer.Ordinal);

    public async Task<object?> InvokeAsync(string action, JsonElement input, CancellationToken token)
    {
        switch (action)
        {
            case "settings":
                {
                    var host = JsonSerializer.SerializeToNode(await inner.InvokeAsync(action, input, token).ConfigureAwait(false),
                        LocalWebUserStore.JsonOptions) as JsonObject ?? throw new InvalidDataException("Invalid host settings.");
                    var preferences = users.PreferencesFor(userId);
                    host["defaultQuality"] = preferences.Quality;
                    host["lowLatency"] = preferences.LowLatency;
                    host["preferences"] = JsonSerializer.SerializeToNode(preferences, LocalWebUserStore.JsonOptions);
                    return host;
                }
            case "settings/save":
                await users.SavePreferencesAsync(userId, input, token).ConfigureAwait(false);
                return new { saved = true };
            case "chat/layout":
                {
                    var key = PlayerKey(input);
                    await users.SaveChatLayoutAsync(userId, key, LocalWebPreferences.ReadLayout(input), token).ConfigureAwait(false);
                    return new { saved = true };
                }
            case "volume/save":
                {
                    var key = PlayerKey(input);
                    if (!input.TryGetProperty("volume", out var value) || value.ValueKind != JsonValueKind.Number ||
                        !value.TryGetDouble(out var volume)) throw new ArgumentException("Invalid volume.");
                    await users.SaveVolumeAsync(userId, key, volume, token).ConfigureAwait(false);
                    return new { saved = true };
                }
            case "open":
                {
                    var preferences = users.PreferencesFor(userId);
                    var copy = JsonNode.Parse(input.GetRawText())!.AsObject();
                    if (!input.TryGetProperty("quality", out _)) copy["quality"] = preferences.Quality;
                    copy["lowLatency"] = preferences.LowLatency;
                    var request = JsonSerializer.SerializeToElement(copy, LocalWebUserStore.JsonOptions);
                    var result = JsonSerializer.SerializeToNode(await inner.InvokeAsync(action, request, token).ConfigureAwait(false),
                        LocalWebUserStore.JsonOptions) as JsonObject ?? throw new InvalidDataException("Invalid player response.");
                    var id = result["id"]?.GetValue<string>() ?? throw new InvalidDataException("Invalid player response.");
                    var platform = result["platform"]?.GetValue<string>() ?? throw new InvalidDataException("Invalid player response.");
                    var channel = result["channel"]?.GetValue<string>() ?? throw new InvalidDataException("Invalid player response.");
                    var key = StreamKey(platform, channel);
                    playerKeys[id] = key;
                    if (input.TryGetProperty("replacePlayer", out var replaced) && replaced.ValueKind == JsonValueKind.String)
                        playerKeys.TryRemove(replaced.GetString()!, out _);
                    result["chatLayout"] = JsonSerializer.SerializeToNode(users.PreferencesFor(userId).ChatLayouts.GetValueOrDefault(key),
                        LocalWebUserStore.JsonOptions);
                    return result;
                }
            case "close":
                {
                    var result = await inner.InvokeAsync(action, input, token).ConfigureAwait(false);
                    if (input.TryGetProperty("player", out var player) && player.ValueKind == JsonValueKind.String)
                        playerKeys.TryRemove(player.GetString()!, out _);
                    return result;
                }
            default: return await inner.InvokeAsync(action, input, token).ConfigureAwait(false);
        }
    }

    private string PlayerKey(JsonElement input)
    {
        if (!input.TryGetProperty("player", out var player) || player.ValueKind != JsonValueKind.String ||
            !playerKeys.TryGetValue(player.GetString() ?? "", out var key)) throw new ArgumentException("Player not found.");
        return key;
    }

    internal static string StreamKey(string platform, string channel)
    {
        if (platform is not ("Twitch" or "Kick") || channel.Trim() is not { Length: > 0 and <= 100 } name ||
            name.Any(char.IsControl)) throw new InvalidDataException("Invalid player response.");
        return platform + ":" + name.ToLowerInvariant();
    }

    public Task ServeMediaAsync(HttpContext context, string playbackId, string resourceId) =>
        inner.ServeMediaAsync(context, playbackId, resourceId);
    public Task ServeCategoryArtworkAsync(HttpContext context, string assetId) => inner.ServeCategoryArtworkAsync(context, assetId);
    public Task ServeChatAssetAsync(HttpContext context, string assetId) => inner.ServeChatAssetAsync(context, assetId);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
