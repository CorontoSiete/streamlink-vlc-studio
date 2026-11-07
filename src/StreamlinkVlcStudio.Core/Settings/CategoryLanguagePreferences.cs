using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Settings;

public static class CategoryLanguagePreferences
{
    public static string Key(PlatformKind platform, string categoryId)
    {
        var id = categoryId.Trim();
        if (platform is not (PlatformKind.Twitch or PlatformKind.Kick) ||
            id.Length is < 1 or > 128 || id.Any(char.IsControl) || id.Contains(':'))
            throw new ArgumentException("Invalid category.");
        return $"{platform}:{id}";
    }

    public static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, language) in values ?? new Dictionary<string, string>())
        {
            var separator = key.IndexOf(':');
            if (separator < 1 || !Enum.TryParse<PlatformKind>(key[..separator], true, out var platform)) continue;
            try { result[Key(platform, key[(separator + 1)..])] = BrowseLanguages.Normalize(language); }
            catch (ArgumentException) { /* Ignore invalid entries from an older or edited settings file. */ }
        }
        return result;
    }
}
