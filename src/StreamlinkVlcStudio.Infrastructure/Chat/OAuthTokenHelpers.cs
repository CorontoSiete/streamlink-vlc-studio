using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static StreamlinkVlcStudio.Core.Json.JsonElementReader;

namespace StreamlinkVlcStudio.Infrastructure.Chat;

/// <summary>
/// Shared OAuth token helpers used by the Twitch and Kick authorization flows. Consolidates
/// token normalization, PKCE/secret generation, and required-field extraction that were
/// previously duplicated across the two OAuth services.
/// </summary>
public static class OAuthTokenHelpers
{
    /// <summary>
    /// Strips a leading "oauth:", "oauth ", or "Bearer " prefix (case-insensitive) and trims.
    /// </summary>
    public static string NormalizeBearerToken(string token)
    {
        var normalized = token.Trim();
        if (normalized.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["oauth:".Length..];
        }
        else if (normalized.StartsWith("oauth ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["oauth ".Length..];
        }
        else if (normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Bearer ".Length..];
        }

        return normalized.Trim();
    }

    /// <summary>Generates a URL-safe base64 secret from <paramref name="byteCount"/> random bytes.</summary>
    public static string CreateBase64UrlSecret(int byteCount)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Base64UrlEncode(bytes);
    }

    /// <summary>Computes the PKCE S256 code challenge for <paramref name="codeVerifier"/>.</summary>
    public static string CreateCodeChallenge(string codeVerifier)
    {
        return Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
    }

    /// <summary>Encodes bytes as URL-safe base64 (no padding, '+' to '-', '/' to '_').</summary>
    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Reads a required string property, throwing <see cref="InvalidOperationException"/> with
    /// <paramref name="errorMessage"/> when it is missing or blank.
    /// </summary>
    public static string GetRequiredString(JsonElement root, string propertyName, string errorMessage)
    {
        var value = GetOptionalString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(errorMessage);
        }

        return value;
    }

    /// <summary>
    /// Parses a URL query string (with or without a leading '?') into a case-sensitive dictionary,
    /// decoding percent-escapes and treating '+' as a space.
    /// </summary>
    public static Dictionary<string, string> ParseQueryString(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = separator >= 0 ? part[..separator] : part;
            var value = separator >= 0 ? part[(separator + 1)..] : "";
            values[Uri.UnescapeDataString(name.Replace('+', ' '))] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    /// <summary>
    /// Converts an OAuth "expires_in" value (a number or numeric string, in seconds) into an absolute
    /// UTC expiry, or null when the value is non-positive.
    /// </summary>
    public static DateTimeOffset? TryGetExpiresAt(JsonElement expiresInElement)
    {
        long seconds = expiresInElement.ValueKind switch
        {
            JsonValueKind.Number when expiresInElement.TryGetInt64(out var numericValue) => numericValue,
            JsonValueKind.String when long.TryParse(expiresInElement.GetString(), out var stringValue) => stringValue,
            _ => 0
        };

        return seconds <= 0 ? null : DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    /// <summary>
    /// Reads OAuth scopes from <paramref name="propertyName"/> on <paramref name="root"/>, accepting
    /// either a single space-delimited string or an array of strings. Returns a case-insensitive set.
    /// </summary>
    public static HashSet<string> ReadScopes(JsonElement root, string propertyName = "scope")
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(propertyName, out var scopeProperty))
        {
            return scopes;
        }

        if (scopeProperty.ValueKind == JsonValueKind.String)
        {
            foreach (var scope in (scopeProperty.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                scopes.Add(scope);
            }

            return scopes;
        }

        if (scopeProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in scopeProperty.EnumerateArray())
            {
                var scope = item.GetString();
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    scopes.Add(scope);
                }
            }
        }

        return scopes;
    }
}
