namespace StreamlinkVlcStudio.Core.Models;

public sealed record BrowseLanguageOption(string Code, string Name);

/// <summary>Shared stream-language choices for desktop and browser discovery.</summary>
public static class BrowseLanguages
{
    public static IReadOnlyList<BrowseLanguageOption> Options { get; } = Array.AsReadOnly<BrowseLanguageOption>(
    [
        new("", "All languages"),
        new("ar", "Arabic"),
        new("bg", "Bulgarian"),
        new("ca", "Catalan"),
        new("zh", "Chinese"),
        new("cs", "Czech"),
        new("da", "Danish"),
        new("nl", "Dutch"),
        new("en", "English"),
        new("fi", "Finnish"),
        new("fr", "French"),
        new("de", "German"),
        new("el", "Greek"),
        new("hi", "Hindi"),
        new("hu", "Hungarian"),
        new("id", "Indonesian"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("ms", "Malay"),
        new("no", "Norwegian"),
        new("pl", "Polish"),
        new("pt", "Portuguese"),
        new("ro", "Romanian"),
        new("ru", "Russian"),
        new("sk", "Slovak"),
        new("es", "Spanish"),
        new("sv", "Swedish"),
        new("tl", "Tagalog"),
        new("th", "Thai"),
        new("tr", "Turkish"),
        new("uk", "Ukrainian"),
        new("vi", "Vietnamese")
    ]);

    public static string Normalize(string? language)
    {
        var code = (language ?? "").Trim().ToLowerInvariant();
        if (!Options.Any(option => option.Code == code))
            throw new ArgumentException("Choose a language from the discovery language list.", nameof(language));
        return code;
    }
}
