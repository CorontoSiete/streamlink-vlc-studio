namespace StreamlinkVlcStudio.Core.Text;

/// <summary>
/// Shared string-selection helpers. Consolidates the <c>FirstNonEmpty</c> helper that was
/// previously duplicated across many services and view models.
/// </summary>
public static class StringValues
{
    /// <summary>
    /// Returns the first value that is not null/blank, trimmed. Returns an empty string when
    /// every candidate is null or whitespace.
    /// </summary>
    public static string FirstNonEmpty(params string?[]? values)
    {
        if (values is null)
        {
            return "";
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    /// <summary>
    /// Returns the trimmed value, or <c>null</c> when it is null or whitespace.
    /// </summary>
    public static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Converts a hyphenated or underscored identifier into a display title.</summary>
    public static string HumanizeIdentifier(string? value)
    {
        return string.Join(
            ' ',
            (value ?? "")
                .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    /// <summary>
    /// Trims an image URL, upgrades protocol-relative URLs to HTTPS, and optionally replaces
    /// Twitch-style <c>{width}</c>/<c>{height}</c> placeholders.
    /// </summary>
    public static string NormalizeImageUrl(string? value, string? width = null, string? height = null)
    {
        var trimmed = (value ?? "").Trim();
        var normalized = trimmed.StartsWith("//", StringComparison.Ordinal)
            ? "https:" + trimmed
            : trimmed;
        if (!string.IsNullOrWhiteSpace(width))
        {
            normalized = normalized
                .Replace("%{width}", width, StringComparison.OrdinalIgnoreCase)
                .Replace("{width}", width, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(height))
        {
            normalized = normalized
                .Replace("%{height}", height, StringComparison.OrdinalIgnoreCase)
                .Replace("{height}", height, StringComparison.OrdinalIgnoreCase);
        }

        return normalized;
    }
}
