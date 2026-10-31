using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.LocalWeb;

namespace StreamlinkVlcStudio.App.Wpf.Chat;

// Reuse the desktop's provider catalogs without sending credentials or file paths to a device.
internal sealed class LocalWebChatPresentation : ILocalWebChatPresentation, IDisposable
{
    internal static LocalWebChatPresentation Shared { get; } = new(DockedChatBadgeCatalog.Shared, DockedChatEmoteCatalog.Shared);
    private readonly DockedChatBadgeCatalog badges;
    private readonly DockedChatEmoteCatalog emotes;
    private readonly ConcurrentDictionary<string, string> assets = new(StringComparer.Ordinal);
    private long version;
    public long Version => Interlocked.Read(ref version);

    internal LocalWebChatPresentation(DockedChatBadgeCatalog badges, DockedChatEmoteCatalog emotes)
    {
        this.badges = badges;
        this.emotes = emotes;
        badges.CatalogChanged += CatalogChanged;
        emotes.CatalogChanged += CatalogChanged;
    }

    private void CatalogChanged(object? sender, EventArgs e) => Interlocked.Increment(ref version);

    public void Dispose()
    {
        badges.CatalogChanged -= CatalogChanged;
        emotes.CatalogChanged -= CatalogChanged;
    }

    public void EnsureLoaded(ChatMessage message, ChatSettings settings)
    {
        badges.ConfigureTwitchCredentials(settings.TwitchClientId, settings.TwitchOAuthToken);
        badges.EnsureForMessage(message);
        emotes.EnsureForMessage(message);
        if (message.Platform == PlatformKind.Kick)
            emotes.EnsureKickLoaded(message.Channel, badges.GetKickBroadcasterId(message.Channel));
    }

    public ChatMessage Present(ChatMessage message, ChatSettings settings)
    {
        EnsureLoaded(message, settings);
        var resolvedBadges = message.Badges?.Select(badge => badges.TryGet(message, badge, out var image)
            ? badge with { Title = image.Code, ImageUrl = BrowserUrl(image.ImageUrl) }
            : badge with { ImageUrl = null }).ToArray();
        var resolvedEmotes = new List<ChatEmote>();
        var position = 0;
        foreach (var emote in (message.Emotes ?? []).OrderBy(e => e.StartIndex))
        {
            if (emote.StartIndex < position || emote.EndIndex <= emote.StartIndex || emote.EndIndex > message.Message.Length) continue;
            AddCatalogEmotes(position, emote.StartIndex);
            var imageUrl = emote.ImageUrl;
            if (string.IsNullOrWhiteSpace(imageUrl) && emotes.TryGet(message, emote.Code, out var image)) imageUrl = image.ImageUrl;
            resolvedEmotes.Add(emote with { ImageUrl = BrowserUrl(imageUrl) });
            position = emote.EndIndex;
        }
        AddCatalogEmotes(position, message.Message.Length);
        return message with { Badges = resolvedBadges, Emotes = resolvedEmotes };

        void AddCatalogEmotes(int start, int end)
        {
            foreach (Match match in Regex.Matches(message.Message[start..end], @"\S+"))
                if (emotes.TryGet(message, match.Value, out var image))
                    resolvedEmotes.Add(new ChatEmote(start + match.Index, start + match.Index + match.Length, match.Value, BrowserUrl(image.ImageUrl)));
        }
    }

    private string? BrowserUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            // Twitch's default format negotiates animated emotes; static still works for ordinary ones.
            return uri.Host.Equals("static-cdn.jtvnw.net", StringComparison.OrdinalIgnoreCase)
                ? uri.AbsoluteUri.Replace("/static/", "/default/", StringComparison.Ordinal)
                : uri.AbsoluteUri;
        }
        if (!uri.IsFile) return null;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsBundledBadge(path)) return null;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        assets.TryAdd(id, path);
        return "/chat-assets/" + id;
    }

    private static bool IsBundledBadge(string path)
    {
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var manifest in new[] { BundledBadgeAssets.FindTwitchBadgeManifestPath(), BundledBadgeAssets.FindKickBadgeManifestPath() })
            if (manifest is not null && path.StartsWith(Path.GetDirectoryName(Path.GetFullPath(manifest)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task ServeAssetAsync(HttpContext context, string assetId)
    {
        if (!assets.TryGetValue(assetId, out var path) || !IsBundledBadge(path)) { context.Response.StatusCode = 404; return; }
        await Results.File(path, "image/png").ExecuteAsync(context).ConfigureAwait(false);
    }
}
