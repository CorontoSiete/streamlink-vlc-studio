namespace StreamlinkVlcStudio.App.Wpf.Chat;

internal readonly record struct ChatLinkMatch(int Start, int Length, Uri Uri);

internal static class ChatLinkParser
{
    internal static IEnumerable<ChatLinkMatch> FindLinks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (var index = 0; index < text.Length; index++)
        {
            if (!TryGetPrefixLength(text, index, out var prefixLength, out var needsHttpsPrefix))
            {
                continue;
            }

            var end = index + prefixLength;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && !char.IsControl(text[end]))
            {
                end++;
            }

            var linkEnd = TrimTrailingPunctuation(text, index, end);
            if (linkEnd <= index ||
                !TryCreateWebUri(text.AsSpan(index, linkEnd - index), needsHttpsPrefix, out var uri))
            {
                continue;
            }

            yield return new ChatLinkMatch(index, linkEnd - index, uri);
            index = linkEnd - 1;
        }
    }

    internal static bool IsSupportedWebUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static bool TryGetPrefixLength(
        string text,
        int index,
        out int prefixLength,
        out bool needsHttpsPrefix)
    {
        prefixLength = 0;
        needsHttpsPrefix = false;
        if (index > 0 && IsLinkWordCharacter(text[index - 1]))
        {
            return false;
        }

        if (StartsWith(text, index, "https://"))
        {
            prefixLength = "https://".Length;
            return true;
        }

        if (StartsWith(text, index, "http://"))
        {
            prefixLength = "http://".Length;
            return true;
        }

        if (StartsWith(text, index, "www."))
        {
            prefixLength = "www.".Length;
            needsHttpsPrefix = true;
            return true;
        }

        return false;
    }

    private static bool StartsWith(string text, int index, string value) =>
        index <= text.Length - value.Length &&
        string.Compare(text, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsLinkWordCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '@' or '.' or '+' or '-';

    private static int TrimTrailingPunctuation(string text, int start, int end)
    {
        // Count each delimiter once. Rescanning a long URL for every unmatched
        // closing character makes a message's trailing punctuation quadratic.
        var parentheses = 0;
        var brackets = 0;
        var braces = 0;
        for (var index = start; index < end; index++)
        {
            switch (text[index])
            {
                case '(': parentheses++; break;
                case ')': parentheses--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
                case '{': braces++; break;
                case '}': braces--; break;
            }
        }

        while (end > start)
        {
            var last = text[end - 1];
            if (last is '.' or ',' or '!' or '?' or ';' or ':' or '"' or '\'' or '>')
            {
                end--;
                continue;
            }

            switch (last)
            {
                case ')' when parentheses < 0: parentheses++; break;
                case ']' when brackets < 0: brackets++; break;
                case '}' when braces < 0: braces++; break;
                default: return end;
            }

            end--;
        }

        return end;
    }

    private static bool TryCreateWebUri(
        ReadOnlySpan<char> displayText,
        bool needsHttpsPrefix,
        out Uri uri)
    {
        uri = null!;
        var candidate = needsHttpsPrefix
            ? string.Concat("https://", displayText.ToString())
            : displayText.ToString();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) || parsed is null ||
            !IsSupportedWebUri(parsed))
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
