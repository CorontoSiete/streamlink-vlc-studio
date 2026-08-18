using System.Buffers.Binary;
using System.Globalization;
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

    /// <summary>Creates a stable cache key without retaining UTF-8 copies of credential material.</summary>
    internal static string CreateCredentialFingerprint(params string?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, bytes.Length);
                hash.AppendData(lengthPrefix);
                hash.AppendData(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        var digest = hash.GetHashAndReset();
        try
        {
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
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
        return TryParseQueryString(query, out var values)
            ? values
            : throw new FormatException("The OAuth callback query string is malformed.");
    }

    internal static bool TryParseQueryString(
        string query,
        out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length > 16_384)
        {
            return false;
        }

        var trimmed = query.StartsWith('?') ? query[1..] : query;
        var parts = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 128)
        {
            return false;
        }

        try
        {
            foreach (var part in parts)
            {
                var separator = part.IndexOf('=');
                var encodedName = separator >= 0 ? part[..separator] : part;
                var encodedValue = separator >= 0 ? part[(separator + 1)..] : "";
                if (encodedName.Length == 0 ||
                    !HasValidPercentEncoding(encodedName) ||
                    !HasValidPercentEncoding(encodedValue))
                {
                    return false;
                }

                var name = Uri.UnescapeDataString(encodedName.Replace('+', ' '));
                var value = Uri.UnescapeDataString(encodedValue.Replace('+', ' '));
                if (name.Length == 0 || name.Length > 256 || value.Length > 8_192 ||
                    !values.TryAdd(name, value))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            values.Clear();
            return false;
        }
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) ||
                !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    /// <summary>
    /// Converts an OAuth "expires_in" value (a number or numeric string, in seconds) into an absolute
    /// UTC expiry, or null when the value is invalid, non-positive, or outside the DateTimeOffset range.
    /// </summary>
    public static DateTimeOffset? TryGetExpiresAt(JsonElement expiresInElement)
    {
        return expiresInElement.ValueKind switch
        {
            JsonValueKind.Number when expiresInElement.TryGetInt64(out var numericValue) => TryGetExpiresAt(numericValue),
            JsonValueKind.String => TryGetExpiresAt(expiresInElement.GetString()),
            _ => null
        };
    }

    public static DateTimeOffset? TryGetExpiresAt(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var expiresInElement)
            ? TryGetExpiresAt(expiresInElement)
            : null;
    }

    public static DateTimeOffset? TryGetExpiresAt(string? expiresIn)
    {
        return long.TryParse(
            expiresIn,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var seconds)
            ? TryGetExpiresAt(seconds)
            : null;
    }

    private static DateTimeOffset? TryGetExpiresAt(long seconds)
    {
        if (seconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads OAuth scopes from <paramref name="propertyName"/> on <paramref name="root"/>, accepting
    /// either a single space-delimited string or an array of strings. Returns a case-insensitive set.
    /// </summary>
    public static HashSet<string> ReadScopes(JsonElement root, string propertyName = "scope")
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetNonEmptyString(root, propertyName, out var scopeValue))
        {
            foreach (var scope in scopeValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                scopes.Add(scope);
            }

            return scopes;
        }

        if (!TryGetArray(root, propertyName, out var scopeArray))
        {
            return scopes;
        }

        foreach (var item in scopeArray.EnumerateArray())
        {
            var scope = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(scope))
            {
                scopes.Add(scope);
            }
        }

        return scopes;
    }
}
