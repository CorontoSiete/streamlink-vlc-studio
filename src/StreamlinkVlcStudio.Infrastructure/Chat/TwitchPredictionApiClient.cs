using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

public sealed class TwitchPredictionApiClient
{
    public const int MaxTitleLength = 45;
    public const int MaxOutcomeTitleLength = 25;
    public const int MinOutcomeCount = 2;
    public const int MaxOutcomeCount = 10;
    public const int MinPredictionWindowSeconds = 30;
    public const int MaxPredictionWindowSeconds = 1800;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    public TwitchPredictionApiClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<TwitchUserInfo?> ResolveUserByLoginAsync(
        string login,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login.Trim())}",
            accessToken,
            clientId);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException("Resolve Twitch channel", response, responseBody);
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            return new TwitchUserInfo(
                TwitchPredictionJson.GetOptionalString(item, "id"),
                TwitchPredictionJson.GetOptionalString(item, "login"),
                TwitchPredictionJson.GetOptionalString(item, "display_name"));
        }

        return null;
    }

    public async Task<TwitchPrediction?> GetLatestPredictionAsync(
        string broadcasterId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        RequireNonEmpty(broadcasterId, "Broadcaster ID");
        using var request = CreateRequest(
            HttpMethod.Get,
            $"https://api.twitch.tv/helix/predictions?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&first=1",
            accessToken,
            clientId);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException("Get Twitch prediction", response, responseBody);
        }

        using var document = JsonDocument.Parse(responseBody);
        return TwitchPredictionJson.ReadFirstPrediction(document.RootElement);
    }

    public Task<TwitchPrediction> CreatePredictionAsync(
        string broadcasterId,
        TwitchPredictionCreateRequest request,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        RequireNonEmpty(broadcasterId, "Broadcaster ID");
        var normalized = ValidateCreateRequest(request);
        var payload = new
        {
            broadcaster_id = broadcasterId,
            title = normalized.Title,
            outcomes = normalized.Outcomes.Select(title => new { title }).ToArray(),
            prediction_window = normalized.PredictionWindowSeconds
        };

        return SendPredictionBodyAsync(
            HttpMethod.Post,
            "https://api.twitch.tv/helix/predictions",
            payload,
            "Create Twitch prediction",
            accessToken,
            clientId,
            cancellationToken);
    }

    public Task<TwitchPrediction> LockPredictionAsync(
        string broadcasterId,
        string predictionId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        return EndPredictionAsync(broadcasterId, predictionId, "LOCKED", null, accessToken, clientId, cancellationToken);
    }

    public Task<TwitchPrediction> CancelPredictionAsync(
        string broadcasterId,
        string predictionId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        return EndPredictionAsync(broadcasterId, predictionId, "CANCELED", null, accessToken, clientId, cancellationToken);
    }

    public Task<TwitchPrediction> ResolvePredictionAsync(
        string broadcasterId,
        string predictionId,
        string winningOutcomeId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        RequireNonEmpty(winningOutcomeId, "Winning outcome ID");
        return EndPredictionAsync(broadcasterId, predictionId, "RESOLVED", winningOutcomeId, accessToken, clientId, cancellationToken);
    }

    public async Task CreateEventSubWebSocketSubscriptionAsync(
        string subscriptionType,
        string broadcasterId,
        string sessionId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken = default)
    {
        RequireNonEmpty(subscriptionType, "EventSub subscription type");
        RequireNonEmpty(broadcasterId, "Broadcaster ID");
        RequireNonEmpty(sessionId, "EventSub WebSocket session ID");

        var payload = new
        {
            type = subscriptionType,
            version = "1",
            condition = new
            {
                broadcaster_user_id = broadcasterId
            },
            transport = new
            {
                method = "websocket",
                session_id = sessionId
            }
        };

        using var request = CreateRequest(
            HttpMethod.Post,
            "https://api.twitch.tv/helix/eventsub/subscriptions",
            accessToken,
            clientId);
        request.Content = CreateJsonContent(payload);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        if (response.StatusCode != HttpStatusCode.Accepted && !response.IsSuccessStatusCode)
        {
            throw CreateApiException($"Subscribe to {subscriptionType}", response, responseBody);
        }
    }

    public static TwitchPredictionCreateRequest ValidateCreateRequest(TwitchPredictionCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Outcomes is null)
        {
            throw new ArgumentException("Prediction outcomes are required.", nameof(request));
        }

        var title = (request.Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Prediction title is required.", nameof(request));
        }

        if (title.Length > MaxTitleLength)
        {
            throw new ArgumentException($"Prediction title must be {MaxTitleLength} characters or fewer.", nameof(request));
        }

        if (request.Outcomes.Count is < MinOutcomeCount or > MaxOutcomeCount)
        {
            throw new ArgumentException($"Predictions require {MinOutcomeCount} to {MaxOutcomeCount} outcomes.", nameof(request));
        }

        var outcomes = new List<string>();
        foreach (var outcome in request.Outcomes)
        {
            var normalizedOutcome = (outcome ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalizedOutcome))
            {
                throw new ArgumentException("Outcome titles are required.", nameof(request));
            }

            if (normalizedOutcome.Length > MaxOutcomeTitleLength)
            {
                throw new ArgumentException($"Outcome titles must be {MaxOutcomeTitleLength} characters or fewer.", nameof(request));
            }

            outcomes.Add(normalizedOutcome);
        }

        if (request.PredictionWindowSeconds is < MinPredictionWindowSeconds or > MaxPredictionWindowSeconds)
        {
            throw new ArgumentException(
                $"Prediction duration must be between {MinPredictionWindowSeconds} and {MaxPredictionWindowSeconds} seconds.",
                nameof(request));
        }

        return new TwitchPredictionCreateRequest(title, outcomes, request.PredictionWindowSeconds);
    }

    private Task<TwitchPrediction> EndPredictionAsync(
        string broadcasterId,
        string predictionId,
        string status,
        string? winningOutcomeId,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        RequireNonEmpty(broadcasterId, "Broadcaster ID");
        RequireNonEmpty(predictionId, "Prediction ID");
        var payload = winningOutcomeId is null
            ? new Dictionary<string, string>
            {
                ["broadcaster_id"] = broadcasterId,
                ["id"] = predictionId,
                ["status"] = status
            }
            : new Dictionary<string, string>
            {
                ["broadcaster_id"] = broadcasterId,
                ["id"] = predictionId,
                ["status"] = status,
                ["winning_outcome_id"] = winningOutcomeId
            };

        return SendPredictionBodyAsync(
            HttpMethod.Patch,
            "https://api.twitch.tv/helix/predictions",
            payload,
            "End Twitch prediction",
            accessToken,
            clientId,
            cancellationToken);
    }

    private async Task<TwitchPrediction> SendPredictionBodyAsync(
        HttpMethod method,
        string url,
        object payload,
        string operation,
        string accessToken,
        string clientId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, url, accessToken, clientId);
        request.Content = CreateJsonContent(payload);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(operation, response, responseBody);
        }

        using var document = JsonDocument.Parse(responseBody);
        return TwitchPredictionJson.ReadFirstPrediction(document.RootElement) ??
            throw new InvalidOperationException($"{operation} response did not include prediction data.");
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string accessToken,
        string clientId)
    {
        RequireNonEmpty(accessToken, "Twitch OAuth token");
        RequireNonEmpty(clientId, "Twitch Client ID");
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TwitchOAuthService.NormalizeOAuthToken(accessToken));
        request.Headers.TryAddWithoutValidation("Client-Id", clientId.Trim());
        return request;
    }

    private static StringContent CreateJsonContent(object payload)
    {
        return new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
    }

    private static InvalidOperationException CreateApiException(string operation, HttpResponseMessage response, string responseBody)
    {
        var message = ExtractApiMessage(responseBody);
        var detail = string.IsNullOrWhiteSpace(message)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
            : $"{(int)response.StatusCode} {response.ReasonPhrase}. {message}";
        return new InvalidOperationException($"{operation} failed: {detail}");
    }

    private static string ExtractApiMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "";
            }

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Trim();
    }

    private static void RequireNonEmpty(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required.");
        }
    }
}

public sealed record TwitchUserInfo(string Id, string Login, string DisplayName);
