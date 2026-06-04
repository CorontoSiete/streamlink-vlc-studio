using System.Globalization;

namespace StreamlinkVlcStudio.Core.Text;

public static class ChatTextNormalizer
{
    public static string NormalizeSingleLine(string? value, int maxTextElements = int.MaxValue)
    {
        var normalized = (value ?? "")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return maxTextElements == int.MaxValue
            ? normalized
            : TruncateTextElements(normalized, maxTextElements);
    }

    public static string TruncateTextElements(string value, int maxTextElements)
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
