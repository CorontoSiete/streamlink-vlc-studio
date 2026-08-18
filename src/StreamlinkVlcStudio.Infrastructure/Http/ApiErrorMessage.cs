using System.Text.Json;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.Infrastructure.Http;

internal static class ApiErrorMessage
{
    private static readonly string[] MessagePropertyNames =
    [
        "message",
        "error_description",
        "error"
    ];

    public static string Extract(string? responseBody, bool includeBodyFallback = true)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            foreach (var propertyName in MessagePropertyNames)
            {
                var value = GetOptionalString(document.RootElement, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return includeBodyFallback
            ? responseBody.Length <= 240 ? responseBody : responseBody[..240]
            : "";
    }
}
