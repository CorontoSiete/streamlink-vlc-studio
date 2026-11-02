using System.Collections.Concurrent;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Infrastructure.Http;
using StreamlinkVlcStudio.Infrastructure.Twitch;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;
using static StreamlinkVlcStudio.Infrastructure.LocalWeb.LocalWebProfileImages;

namespace StreamlinkVlcStudio.Infrastructure.LocalWeb;

public sealed record LocalWebChannelLink(string Title, string Url);
public sealed record LocalWebChannelPanel(string Title, string Description, string ImageUrl, string LinkUrl);
public sealed record LocalWebChannelProfile(string DisplayName, string Description, string ProfileImageUrl,
    long? Followers, bool IsPartner, string Title, string Category, long? Viewers, string StartedAt,
    IReadOnlyList<string> Tags, IReadOnlyList<LocalWebChannelLink> Links, IReadOnlyList<LocalWebChannelPanel> Panels,
    bool HasExtensions = false);

/// <summary>Public channel content only. Provider credentials and HTML never enter the response.</summary>
public sealed class LocalWebChannelProfileClient
{
    private static readonly HttpClient SharedClient = HttpClientFactory.Create(TimeSpan.FromSeconds(10),
        includeUserAgent: true, acceptJson: true, allowAutoRedirect: false);
    private readonly HttpClient client;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, LocalWebChannelProfile Profile)> cache = new();

    public LocalWebChannelProfileClient(HttpClient? client = null) => this.client = client ?? SharedClient;

    public async Task<LocalWebChannelProfile> GetAsync(StreamTarget target, CancellationToken token)
    {
        var key = target.Platform + ":" + target.Channel.ToLowerInvariant();
        if (cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Profile;
        var profile = target.Platform == PlatformKind.Twitch
            ? await TwitchAsync(target.Channel, token).ConfigureAwait(false)
            : await KickAsync(target.Channel, token).ConfigureAwait(false);
        // Bound the shared desktop cache even when many browser devices browse different channels.
        if (cache.Count >= 256)
            foreach (var old in cache.OrderBy(entry => entry.Value.Expires).Take(64)) cache.TryRemove(old.Key, out _);
        cache[key] = (DateTimeOffset.UtcNow.AddSeconds(45), profile);
        return profile;
    }

    private async Task<LocalWebChannelProfile> TwitchAsync(string login, CancellationToken token)
    {
        var transport = new TwitchGraphQlTransport(client);
        var payload = JsonSerializer.Serialize(new
        {
            query = """
                query StreamStudioChannelProfile($login: String!) {
                  user(login: $login) {
                    displayName description profileImageURL(width: 150) followers { totalCount } roles { isPartner }
                    broadcastSettings { title game { name } }
                    stream { title viewersCount createdAt game { name } freeformTags { name } }
                    panels { type ... on DefaultPanel { title description imageURL linkURL } }
                  }
                }
                """,
            variables = new { login }
        });
        using var document = await transport.SendAsync(payload, TwitchGraphQlTransport.PublicClientId,
            TwitchGraphQlTransport.CreateDeviceId(), token).ConfigureAwait(false);
        var user = Child(Child(document.RootElement, "data"), "user");
        if (user.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Channel profile is unavailable.");
        var stream = Child(user, "stream");
        var broadcast = stream.ValueKind == JsonValueKind.Object ? stream : Child(user, "broadcastSettings");
        var allPanels = Array(user, "panels").ToArray();
        var panels = allPanels.Where(p => GetOptionalString(p, "type") == "DEFAULT").Take(100)
            .Select(p => new LocalWebChannelPanel(GetOptionalString(p, "title"), GetOptionalString(p, "description"),
                PublicUrl(GetOptionalString(p, "imageURL")), Link(GetOptionalString(p, "linkURL")))).ToArray();
        var links = new List<LocalWebChannelLink>();
        // Twitch can independently refuse social links. Keep the bio and panels usable in that case.
        try
        {
            using var social = await transport.SendAsync(JsonSerializer.Serialize(new
            {
                query = "query StreamStudioSocialLinks($login: String!) { user(login: $login) { channel { socialMedias { title name url } } } }",
                variables = new { login }
            }), TwitchGraphQlTransport.PublicClientId, TwitchGraphQlTransport.CreateDeviceId(), token).ConfigureAwait(false);
            foreach (var item in Array(Child(Child(Child(social.RootElement, "data"), "user"), "channel"), "socialMedias"))
            {
                var url = Link(GetOptionalString(item, "url"));
                if (url.Length > 0) links.Add(new(First(GetOptionalString(item, "title"), GetOptionalString(item, "name")), url));
            }
        }
        catch (Exception error) when (error is TwitchGraphQlRejectedException or TwitchGraphQlHttpException or
            HttpRequestException or JsonException or InvalidDataException)
        { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        if (links.Count == 0)
            foreach (var panel in panels)
                if (Uri.TryCreate(panel.LinkUrl, UriKind.Absolute, out var uri) && SocialTitle(uri.Host) is { } title &&
                    !links.Any(link => link.Url == uri.AbsoluteUri)) links.Add(new(title, uri.AbsoluteUri));
        return new(GetOptionalString(user, "displayName"), GetOptionalString(user, "description"),
            PublicUrl(GetOptionalString(user, "profileImageURL")), TryGetInt64(Child(user, "followers"), "totalCount"),
            Child(Child(user, "roles"), "isPartner").ValueKind == JsonValueKind.True,
            GetOptionalString(broadcast, "title"), GetOptionalString(Child(broadcast, "game"), "name"),
            TryGetInt64(stream, "viewersCount"), GetOptionalString(stream, "createdAt"),
            Array(stream, "freeformTags").Select(tag => GetOptionalString(tag, "name")).Where(tag => tag.Length > 0).Take(30).ToArray(),
            links, panels, allPanels.Any(panel => GetOptionalString(panel, "type") == "EXTENSION"));
    }

    private async Task<LocalWebChannelProfile> KickAsync(string channel, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://kick.com/api/v2/channels/" + Uri.EscapeDataString(channel));
        using var response = await BoundedHttpResponseSender.SendAsync(client, request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await BoundedHttpContentReader.ReadJsonAsync(response.Content, token).ConfigureAwait(false));
        var root = document.RootElement;
        var user = Child(root, "user"); var stream = Child(root, "livestream");
        var category = Array(stream, "categories").FirstOrDefault();
        var links = new List<LocalWebChannelLink>();
        foreach (var (field, title, prefix) in new[] { ("twitter", "Twitter", "https://twitter.com/"),
            ("instagram", "Instagram", "https://instagram.com/"), ("youtube", "YouTube", "https://youtube.com/"),
            ("discord", "Discord", "https://discord.gg/") })
        {
            var value = GetOptionalString(user, field);
            if (value.Length == 0) continue;
            var url = Link(value.Contains("://", StringComparison.Ordinal) ? value : prefix + value.TrimStart('@', '/'));
            if (url.Length > 0) links.Add(new(title, url));
        }
        return new(First(GetOptionalString(user, "username"), channel), GetOptionalString(user, "bio"),
            PublicUrl(GetOptionalString(user, "profile_pic")), TryGetInt64(root, "followers_count"),
            Child(root, "verified").ValueKind == JsonValueKind.True || Child(root, "verified").ValueKind == JsonValueKind.Object,
            GetOptionalString(stream, "session_title"), GetOptionalString(category, "name"), TryGetInt64(stream, "viewer_count"),
            GetOptionalString(stream, "created_at"), Array(stream, "tags").Select(tag => tag.ValueKind == JsonValueKind.String
                ? tag.GetString()! : GetOptionalString(tag, "name")).ToArray(), links, []);
    }

    private static JsonElement Child(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => Child(value, name) is { ValueKind: JsonValueKind.Array } items
        ? items.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string First(string value, string fallback) => value.Length > 0 ? value : fallback;
    private static string Link(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : "";
    private static string? SocialTitle(string host) => host.ToLowerInvariant() switch
    {
        "twitter.com" or "www.twitter.com" or "x.com" or "www.x.com" => "Twitter",
        "youtube.com" or "www.youtube.com" or "youtu.be" => "YouTube",
        "instagram.com" or "www.instagram.com" => "Instagram",
        "discord.com" or "discord.gg" => "Discord",
        _ => null
    };
}
