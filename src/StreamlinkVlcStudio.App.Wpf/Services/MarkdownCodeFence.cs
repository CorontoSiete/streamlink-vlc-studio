namespace StreamlinkVlcStudio.App.Wpf.Services;

/// <summary>Tracks fenced Markdown blocks shared by release-note splitting and rendering.</summary>
internal sealed class MarkdownCodeFence
{
    private char marker;
    private int markerLength;
    private int indentation;

    internal bool IsOpen => markerLength > 0;

    internal bool TryProcessDelimiter(string line)
    {
        var start = 0;
        while (start < line.Length && line[start] == ' ' && start < 4) start++;
        if (start > 3 || start == line.Length) return false;

        var nextMarker = line[start];
        if (nextMarker is not ('`' or '~') || (IsOpen && nextMarker != marker)) return false;

        var end = start;
        while (end < line.Length && line[end] == nextMarker) end++;
        if (end - start < (IsOpen ? markerLength : 3)) return false;

        var remainder = line.AsSpan(end);
        if (IsOpen)
        {
            foreach (var character in remainder)
            {
                if (character is not (' ' or '\t')) return false;
            }
            markerLength = 0;
            return true;
        }

        if (nextMarker == '`' && remainder.Contains('`')) return false;
        marker = nextMarker;
        markerLength = end - start;
        indentation = start;
        return true;
    }

    internal string RemoveIndentation(string line)
    {
        var start = 0;
        while (start < indentation && start < line.Length && line[start] == ' ') start++;
        return line[start..];
    }
}
