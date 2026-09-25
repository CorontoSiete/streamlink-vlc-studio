using System.IO;
using System.Text.Json;

namespace StreamlinkVlcStudio.App.Wpf.Twitch;

internal static class TwitchBonusScript
{
    private static readonly string Script = ReadScript();

    internal static string Build(string channel) =>
        $"({Script})({JsonSerializer.Serialize(channel)})";

    private static string ReadScript()
    {
        using var stream = typeof(TwitchBonusScript).Assembly.GetManifestResourceStream(
            "StreamlinkVlcStudio.App.Wpf.Twitch.ClaimChannelPoints.js")
            ?? throw new InvalidOperationException("The Twitch bonus script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
