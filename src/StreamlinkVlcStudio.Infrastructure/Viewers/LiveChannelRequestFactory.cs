using System.Net.Http.Headers;

namespace StreamlinkVlcStudio.Infrastructure.Viewers;

/// <summary>Builds the shared live-channel API requests used by viewer counts and metadata.</summary>
internal static class LiveChannelRequestFactory
{
    public static HttpRequestMessage CreateTwitchStreamsRequest(
        string channel,
        string token,
        string clientId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.twitch.tv/helix/streams?user_login={Uri.EscapeDataString(channel)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", clientId);
        return request;
    }

    public static HttpRequestMessage CreateKickChannelsRequest(
        string channel,
        string accessToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.kick.com/public/v1/channels?slug={Uri.EscapeDataString(channel)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}
