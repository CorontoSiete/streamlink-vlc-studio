using System.Text.Json;

namespace StreamlinkVlcStudio.App.Wpf.Twitch;

internal static class TwitchBonusClaimResponse
{
    internal static bool IsGraphQlEndpoint(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "gql.twitch.tv" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == "/gql";

    // Verified against Twitch's own ClaimCommunityPoints mutation and success handler:
    // https://assets.twitch.tv/assets/59186-419334d6a6edad3df7ed.js (2026-09-24).
    // Unknown shapes fail closed. Neither a button click nor a changing balance is a claim.
    internal static IReadOnlyList<string> ReadConfirmedClaims(string requestJson, string responseJson)
    {
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var request = JsonDocument.Parse(requestJson, new JsonDocumentOptions { MaxDepth = 32 });
            using var document = JsonDocument.Parse(responseJson, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && request.RootElement.ValueKind == JsonValueKind.Array &&
                root.GetArrayLength() == request.RootElement.GetArrayLength())
            {
                for (var i = 0; i < root.GetArrayLength(); i++) ReadResult(request.RootElement[i], root[i], confirmed);
            }
            else ReadResult(request.RootElement, root, confirmed);
        }
        catch (JsonException) { }
        return confirmed.ToArray();
    }

    private static void ReadResult(JsonElement request, JsonElement result, HashSet<string> confirmed)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("operationName", out var operation) || operation.ValueKind != JsonValueKind.String ||
            operation.GetString() != "ClaimCommunityPoints" ||
            !TryObject(request, "variables", out var variables) || !TryObject(variables, "input", out var input) ||
            !input.TryGetProperty("claimID", out var requestedId) || requestedId.ValueKind != JsonValueKind.String ||
            result.ValueKind != JsonValueKind.Object ||
            (result.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null &&
             !(errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() == 0)) ||
            !TryObject(result, "data", out var data) ||
            !TryObject(data, "claimCommunityPoints", out var payload) ||
            !payload.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Null ||
            !TryObject(payload, "claim", out var claim) ||
            !claim.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !claim.TryGetProperty("pointsEarnedTotal", out var points) ||
            points.ValueKind != JsonValueKind.Number || !points.TryGetInt64(out var earned) || earned <= 0) return;
        var claimId = id.GetString();
        if (!string.IsNullOrWhiteSpace(claimId) && claimId.Length <= 256 && claimId == requestedId.GetString())
            confirmed.Add(claimId);
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
}
