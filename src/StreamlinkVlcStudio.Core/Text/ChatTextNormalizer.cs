using System.Globalization;

namespace StreamlinkVlcStudio.Core.Text;

public static class ChatTextNormalizer
{
    private const int MaximumBadgeTitleTextElements = 128;

    public static string NormalizeSingleLine(string? value, int maxTextElements = int.MaxValue)
    {
        var input = value ?? "";
        var builder = new System.Text.StringBuilder(input.Length);
        foreach (var character in input)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var normalized = builder.ToString().Trim();

        return maxTextElements == int.MaxValue
            ? normalized
            : TruncateTextElements(normalized, maxTextElements);
    }

    public static string NormalizeBadgeTitle(string? value, string? fallback = null)
    {
        var normalized = NormalizeSingleLine(value, MaximumBadgeTitleTextElements);
        return normalized.Length > 0
            ? normalized
            : NormalizeSingleLine(fallback, MaximumBadgeTitleTextElements);
    }

    private static string TruncateTextElements(string value, int maxTextElements)
    {
        if (string.IsNullOrEmpty(value) || maxTextElements < 0)
        {
            return "";
        }

        if (maxTextElements == 0)
        {
            return "";
        }

        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            if (count == maxTextElements)
            {
                return value[..enumerator.ElementIndex];
            }

            count++;
        }

        return value;
    }
}
