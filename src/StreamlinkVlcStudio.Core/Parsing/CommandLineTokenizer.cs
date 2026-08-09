using System.Text;

namespace StreamlinkVlcStudio.Core.Parsing;

public static class CommandLineTokenizer
{
    public static IReadOnlyList<string> Tokenize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var tokenStarted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];

            if (ch == '\\')
            {
                tokenStarted = true;
                var slashStart = index;
                while (index < value.Length && value[index] == '\\')
                {
                    index++;
                }

                var slashCount = index - slashStart;
                if (index < value.Length && value[index] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                    }
                    else
                    {
                        current.Append('"');
                    }
                }
                else
                {
                    current.Append('\\', slashCount);
                    index--;
                }

                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                AddToken(result, current, ref tokenStarted);
                continue;
            }

            tokenStarted = true;
            current.Append(ch);
        }

        if (inQuotes)
        {
            throw new FormatException("Unclosed quote in command line arguments.");
        }

        AddToken(result, current, ref tokenStarted);
        return result;
    }

    private static void AddToken(List<string> result, StringBuilder current, ref bool tokenStarted)
    {
        if (!tokenStarted)
        {
            return;
        }

        result.Add(current.ToString());
        current.Clear();
        tokenStarted = false;
    }
}
