using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Parsing;

public static class VodDownloadUrlParser
{
    public static StreamTarget Parse(string input)
    {
        var value = input?.Trim() ?? "";
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Enter a Twitch or Kick VOD page URL.", nameof(input));

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if ((uri.IdnHost.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) ||
             uri.IdnHost.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase)) &&
            parts.Length == 2 && parts[0] == "videos" && parts[1].Length is > 0 and <= 32 &&
            parts[1].All(char.IsAsciiDigit))
            return new StreamTarget(PlatformKind.Twitch, parts[1], $"https://www.twitch.tv/videos/{parts[1]}",
                StreamTargetKind.TwitchVod, parts[1], $"VOD {parts[1]}");

        if ((uri.IdnHost.Equals("kick.com", StringComparison.OrdinalIgnoreCase) ||
             uri.IdnHost.Equals("www.kick.com", StringComparison.OrdinalIgnoreCase)) &&
            parts.Length == 3 && parts[1] == "videos" &&
            parts[2].Length is > 0 and <= 128 && parts[2].All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
        {
            var channel = StreamInputParser.FromChannel(PlatformKind.Kick, parts[0]);
            return channel with
            {
                Url = $"https://kick.com/{channel.Channel}/videos/{parts[2]}",
                Kind = StreamTargetKind.KickVod,
                MediaId = parts[2],
                DisplayTitle = $"{channel.Channel} VOD {parts[2]}"
            };
        }

        throw new ArgumentException("Only Twitch /videos/{id} and Kick /{channel}/videos/{id} VOD URLs can be downloaded. Live streams and clips are not VODs.", nameof(input));
    }
}
