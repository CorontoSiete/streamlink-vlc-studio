namespace StreamlinkVlcStudio.Core.Models;

public sealed record AppThemeOption(AppTheme Value, string DisplayName)
{
    public override string ToString() => DisplayName;

    public static IReadOnlyList<AppThemeOption> All { get; } =
        Array.AsReadOnly<AppThemeOption>(
        [
            new(AppTheme.Dark, "Dark"),
            new(AppTheme.Light, "Light"),
            new(AppTheme.MidnightBlue, "Midnight Blue"),
            new(AppTheme.Dracula, "Dracula"),
            new(AppTheme.Nord, "Nord"),
            new(AppTheme.Solarized, "Solarized"),
            new(AppTheme.Monokai, "Monokai"),
            new(AppTheme.Cyberpunk, "Cyberpunk")
        ]);
}
