using System.IO;
using System.Text;

namespace StreamlinkVlcStudio.App.Wpf.Services;

public sealed record ReleaseNotes(Version Version, string Markdown)
{
    public string DisplayName => $"Version {Version.ToString(3)}";
    public string ChangesMarkdown => SplitNotes().Changes;
    public string UpdatingMarkdown => SplitNotes().Updating;
    public bool HasUpdatingInstructions => !string.IsNullOrWhiteSpace(UpdatingMarkdown);
    public string HeaderMarkdown => SplitPresentation().Header;
    public string BodyMarkdown => SplitPresentation().Body;

    private (string Header, string Body) SplitPresentation()
    {
        var changes = ChangesMarkdown;
        var lines = changes.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (start < 0 || !lines[start].Trim().StartsWith("# ", StringComparison.Ordinal))
            return ($"# Stream Studio {Version.ToString(3)}", changes);

        // Keep the authored title and introduction together. Lists, subsequent
        // headings, quotes and fenced samples belong to the changes below it.
        var end = start + 1;
        var codeFence = new MarkdownCodeFence();
        for (; end < lines.Length; end++)
        {
            var line = lines[end].Trim();
            if (line.StartsWith('#') || line.StartsWith('>') ||
                codeFence.TryProcessDelimiter(lines[end]) ||
                line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ||
                line.StartsWith("+ ", StringComparison.Ordinal) || IsNumberedItem(line)) break;
        }

        return (JoinSection(lines, start, end), JoinSection(lines, end, lines.Length));
    }

    private static bool IsNumberedItem(string line)
    {
        var digits = line.TakeWhile(char.IsAsciiDigit).Count();
        return digits > 0 && line.Length > digits + 1 && line[digits] is '.' or ')' && line[digits + 1] == ' ';
    }

    private (string Changes, string Updating) SplitNotes()
    {
        var lines = Markdown.ReplaceLineEndings("\n").Split('\n');
        var codeFence = new MarkdownCodeFence();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (codeFence.TryProcessDelimiter(lines[index]) || codeFence.IsOpen ||
                !line.Equals("## Updating", StringComparison.OrdinalIgnoreCase)) continue;

            var updating = JoinSection(lines, index + 1, lines.Length);
            if (updating.Length > 0)
                return (string.Join('\n', lines[..index]).TrimEnd(), updating);
        }
        return (Markdown, "");
    }

    private static string JoinSection(string[] lines, int start, int end)
    {
        // Trim surrounding blank lines, preserving indentation and blanks inside an unclosed sample.
        while (start < end && string.IsNullOrWhiteSpace(lines[start])) start++;
        var codeFence = new MarkdownCodeFence();
        var contentEnd = start;
        for (var index = start; index < end; index++)
        {
            codeFence.TryProcessDelimiter(lines[index]);
            if (codeFence.IsOpen || !string.IsNullOrWhiteSpace(lines[index])) contentEnd = index + 1;
        }
        return string.Join('\n', lines[start..contentEnd]);
    }
}

/// <summary>Reads the same versioned notes used by release publication, including in single-file builds.</summary>
internal sealed class ReleaseNotesCatalog
{
    private const string ResourcePrefix = "StreamStudio.ReleaseNotes.";

    internal ReleaseNotesCatalog(Version installedVersion, IEnumerable<ReleaseNotes> releases)
    {
        InstalledVersion = Normalize(installedVersion);
        Releases = releases
            .Where(release => release.Version <= InstalledVersion)
            .OrderByDescending(release => release.Version)
            .ToArray();
        InstalledRelease = Releases.SingleOrDefault(release => release.Version == InstalledVersion);
    }

    internal Version InstalledVersion { get; }
    internal IReadOnlyList<ReleaseNotes> Releases { get; }
    internal ReleaseNotes? InstalledRelease { get; }

    internal static ReleaseNotesCatalog LoadEmbedded()
    {
        var assembly = typeof(ReleaseNotesCatalog).Assembly;
        var version = assembly.GetName().Version
            ?? throw new InvalidDataException("The installed application version is unavailable.");
        var installedVersion = Normalize(version);
        var releases = new List<ReleaseNotes>();
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var fileName = resourceName[ResourcePrefix.Length..];
            if (!fileName.StartsWith('v') || !fileName.EndsWith(".md", StringComparison.Ordinal) ||
                !Version.TryParse(fileName[1..^3], out var releaseVersion) ||
                releaseVersion.Build < 0 || releaseVersion.Revision >= 0)
                throw new InvalidDataException($"The bundled release notes have an invalid version: {fileName}.");

            if (releaseVersion > installedVersion) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"The bundled release notes are missing: {fileName}.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var markdown = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(markdown))
                throw new InvalidDataException($"The bundled release notes are empty: {fileName}.");
            releases.Add(new ReleaseNotes(releaseVersion, markdown));
        }

        return new ReleaseNotesCatalog(installedVersion, releases);
    }

    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));
}
