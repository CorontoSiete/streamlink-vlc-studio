namespace StreamlinkVlcStudio.Core.Models;

public sealed record WindowCloseBehaviorOption(WindowCloseBehavior Value, string DisplayName)
{
    public override string ToString() => DisplayName;

    public static IReadOnlyList<WindowCloseBehaviorOption> All { get; } =
    [
        new(WindowCloseBehavior.MinimizeToTray, "Minimize to system tray"),
        new(WindowCloseBehavior.Exit, "Exit completely")
    ];
}
