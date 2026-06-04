namespace StreamlinkVlcStudio.Core.Models;

public sealed record QualityOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;

    public static IReadOnlyList<QualityOption> Defaults { get; } =
    [
        new("best", "Best"),
        new("source", "Source"),
        new("1080p60", "1080p60"),
        new("1080p", "1080p"),
        new("720p60", "720p60"),
        new("720p", "720p"),
        new("480p", "480p"),
        new("audio_only", "Audio only"),
        new("worst", "Worst")
    ];
}
