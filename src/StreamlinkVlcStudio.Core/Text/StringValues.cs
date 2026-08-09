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
}
