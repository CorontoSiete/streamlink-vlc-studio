using System.Net;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Infrastructure.Http;

namespace StreamlinkVlcStudio.Infrastructure.Twitch;

internal sealed class TwitchGraphQlTransport(HttpClient httpClient)
{
    internal const string Endpoint = "https://gql.twitch.tv/gql";

    internal async Task<JsonDocument> SendAsync(
        string payload,
        string clientId,
        string deviceId,
        CancellationToken cancellationToken,
        string? oauthToken = null,
        string mediaType = "text/plain")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("en-US");
        request.Headers.Referrer = new Uri("https://www.twitch.tv/");
        request.Headers.TryAddWithoutValidation("Client-Id", clientId.Trim());
        request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId.Trim());
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        if (!string.IsNullOrWhiteSpace(oauthToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {oauthToken.Trim()}");
        }

        request.Content = new StringContent(payload, Encoding.UTF8, mediaType);
        using var response = await BoundedHttpResponseSender
            .SendAsync(httpClient, request, cancellationToken)
            .ConfigureAwait(false);
        var responseBody = await BoundedHttpContentReader
            .ReadJsonAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new TwitchGraphQlHttpException(
                response.StatusCode,
                response.ReasonPhrase,
                responseBody);
        }

        var document = JsonDocument.Parse(responseBody);
        var graphQlError = GraphQlErrorReader.Extract(document.RootElement);
        if (!string.IsNullOrWhiteSpace(graphQlError))
        {
            document.Dispose();
            throw new TwitchGraphQlRejectedException(graphQlError);
        }

        return document;
    }
}

internal sealed class TwitchGraphQlHttpException(
    HttpStatusCode statusCode,
    string? reasonPhrase,
    string responseBody)
    : InvalidOperationException($"Twitch GraphQL returned {(int)statusCode} {reasonPhrase}.")
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
    internal string ReasonPhrase { get; } = reasonPhrase ?? "";
    internal string ResponseBody { get; } = responseBody;
}

internal sealed class TwitchGraphQlRejectedException(string graphQlMessage)
    : InvalidOperationException(graphQlMessage)
{
    internal string GraphQlMessage { get; } = graphQlMessage;
}
