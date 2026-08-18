using System.Text.Json;

namespace StreamlinkVlcStudio.Infrastructure.Http;

/// <summary>Reads the first useful error message from either batched or single GraphQL responses.</summary>
internal static class GraphQlErrorReader
{
    internal static string Extract(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var message = Extract(item);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }

            return "";
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return errors
            .EnumerateArray()
            .Select(ReadStringMessage)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "";
    }

    internal static string ExtractResponseMessage(string responseBody, int maximumFallbackCharacters = 240)
    {
        var normalizedBody = (responseBody ?? "").Trim();
        if (normalizedBody.Length == 0)
        {
            return "";
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFallbackCharacters, 1);
        try
        {
            using var document = JsonDocument.Parse(normalizedBody);
            var graphQlError = Extract(document.RootElement);
            if (!string.IsNullOrWhiteSpace(graphQlError))
            {
                return graphQlError;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var message = ReadStringMessage(document.RootElement);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }
        catch (JsonException)
        {
        }

        return normalizedBody.Length <= maximumFallbackCharacters
            ? normalizedBody
            : normalizedBody[..maximumFallbackCharacters];
    }

    private static string ReadStringMessage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return message.GetString()?.Trim() ?? "";
    }
}
