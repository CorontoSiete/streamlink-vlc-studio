namespace StreamlinkVlcStudio.Infrastructure.Hls;

internal readonly record struct HlsAttributeValue(string Value, bool IsQuoted);

/// <summary>Reads bounded HLS attribute lists without splitting commas inside quoted values.</summary>
internal static class HlsAttributeList
{
    private const int MaximumAttributes = 32;

    internal static bool TryParse(ReadOnlySpan<char> text, out Dictionary<string, HlsAttributeValue> attributes)
    {
        attributes = new(StringComparer.Ordinal);
        var position = 0;
        while (position < text.Length)
        {
            var equals = text[position..].IndexOf('=');
            if (equals <= 0 || attributes.Count >= MaximumAttributes) return false;
            var name = text.Slice(position, equals);
            foreach (var character in name)
                if (character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '-')) return false;
            position += equals + 1;
            if (position == text.Length) return false;

            var quoted = text[position] == '"';
            ReadOnlySpan<char> value;
            if (quoted)
            {
                position++;
                var end = text[position..].IndexOf('"');
                if (end < 0) return false;
                value = text.Slice(position, end);
                position += end + 1;
            }
            else
            {
                var end = text[position..].IndexOf(',');
                if (end < 0) end = text.Length - position;
                if (end == 0) return false;
                value = text.Slice(position, end);
                position += end;
            }

            foreach (var character in value)
                if (char.IsControl(character) || (!quoted && (character == '"' || char.IsWhiteSpace(character)))) return false;
            if (!attributes.TryAdd(name.ToString(), new(value.ToString(), quoted))) return false;
            if (position == text.Length) break;
            if (text[position++] != ',' || position == text.Length) return false;
        }

        return attributes.Count > 0;
    }
}
