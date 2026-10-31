using System.Net.Http.Json;
using System.Text.Json;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>Negotiates the public Pusher application assigned to a channel's chat feed.</summary>
internal sealed record KickPusherConnection(string AppKey, string Cluster)
{
    public Uri WebSocketUri => new($"wss://ws-{Cluster}.pusher.com/app/{AppKey}?protocol=7&client=dotnet&version=0.1&flash=false");

    public static async Task<KickPusherConnection> ResolveAsync(
        string? channelId, string clientId, CancellationToken cancellationToken)
    {
        // This anonymous endpoint must not inherit account headers, cookies or redirects.
        using var http = HttpClientFactory.Create(TimeSpan.FromSeconds(10), acceptJson: true, allowAutoRedirect: false);
        KickHttpHeaders.Configure(http);
        return await ResolveAsync(http, channelId, clientId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<KickPusherConnection> ResolveAsync(
        HttpClient http, string? channelId, string clientId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (KickChannelInfoJson.NormalizeNumericId(channelId) is not { Length: <= 63 } normalizedId)
            throw new InvalidOperationException("Kick channel ID could not be resolved for chat connection setup.");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://web.kick.com/api/v1/realtime/channels/{normalizedId}/chat/connection");
        request.Headers.Add("Origin", "https://kick.com");
        request.Headers.Add("x-app-platform", "web");
        request.Content = JsonContent.Create(new
        {
            client = new { id = clientId, type = "web" },
            capabilities = new { accepted_providers = new[] { new { provider = "pusher" } } }
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await BoundedHttpContentReader.ReadStringAsync(response.Content, 64 * 1024, cancellationToken)
            .ConfigureAwait(false);
        return Parse(body);
    }

    internal static KickPusherConnection Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String &&
            mode.GetString() == "websocket" &&
            data.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
        {
            foreach (var connection in connections.EnumerateArray())
            {
                if (connection.ValueKind != JsonValueKind.Object ||
                    !connection.TryGetProperty("provider", out var provider) || provider.ValueKind != JsonValueKind.String ||
                    provider.GetString() != "pusher" ||
                    !connection.TryGetProperty("credentials", out var credentials) || credentials.ValueKind != JsonValueKind.Object)
                    continue;

                if (ReadIdentifier(credentials, "app_key", 128, allowHyphen: false) is { } key &&
                    ReadIdentifier(credentials, "cluster", 32, allowHyphen: true) is { } cluster)
                    return new KickPusherConnection(key, cluster);
            }
        }

        throw new InvalidDataException("Kick did not supply a supported Pusher chat connection.");
    }

    private static string? ReadIdentifier(JsonElement credentials, string name, int maximumLength, bool allowHyphen)
    {
        if (!credentials.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var value = element.GetString();
        return value is { Length: > 0 } && value.Length <= maximumLength &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || (allowHyphen && character == '-')) &&
            value[0] != '-' && value[^1] != '-' ? value : null;
    }
}
