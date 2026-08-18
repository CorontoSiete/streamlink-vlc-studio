using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Parsing;

public static partial class StreamInputParser
{
    public static IReadOnlyList<StreamTarget> ParseCandidates(string input)
    {
        var parsed = ParseInput(input);
        if (parsed.VodId is not null)
        {
            return [FromTwitchVod(parsed.VodId)];
        }

        if (parsed.Platform is not null)
        {
            return [FromChannel(parsed.Platform.Value, parsed.Channel)];
        }

        var normalized = NormalizeChannel(parsed.Channel);
        var candidates = new List<StreamTarget>(2);
        if (TryFromChannel(PlatformKind.Twitch, normalized, out var twitchTarget))
        {
            candidates.Add(twitchTarget!);
        }

        if (TryFromChannel(PlatformKind.Kick, normalized, out var kickTarget))
        {
            candidates.Add(kickTarget!);
        }

        if (candidates.Count == 0)
        {
            throw new ArgumentException("The channel name is not valid for Twitch or Kick.", nameof(input));
        }

        return candidates;
    }

    public static StreamTarget Parse(string input, PlatformKind defaultPlatform)
    {
        var parsed = ParseInput(input);
        return parsed.VodId is not null
            ? FromTwitchVod(parsed.VodId)
            : FromChannel(parsed.Platform ?? defaultPlatform, parsed.Channel);
    }

    public static bool TryParsePlatformUrl(string input, out StreamTarget? target)
    {
        target = null;

        try
        {
            var parsed = ParseInput(input);
            if (parsed.VodId is not null)
            {
                target = FromTwitchVod(parsed.VodId);
                return true;
            }

            if (parsed.Platform is null)
            {
                return false;
            }

            target = FromChannel(parsed.Platform.Value, parsed.Channel);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool TryParseTwitchVodUrl(string input, out StreamTarget? target)
    {
        target = null;

        try
        {
            var parsed = ParseInput(input);
            if (parsed.VodId is null)
            {
                return false;
            }

            target = FromTwitchVod(parsed.VodId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static StreamTarget FromTwitchVod(string vodId)
    {
        return new StreamTarget(
            PlatformKind.Twitch,
            vodId,
            $"https://www.twitch.tv/videos/{vodId}",
            StreamTargetKind.TwitchVod,
            vodId,
            $"VOD {vodId}");
    }

    private static ParsedInput ParseInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Enter a Twitch or Kick channel URL/name.", nameof(input));
        }

        var trimmed = input.Trim();
        var urlCandidate = trimmed;
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                urlCandidate = "https:" + trimmed;
            }
            else if (!StartsWithKnownPlatformHost(trimmed))
            {
                return new ParsedInput(null, trimmed);
            }
            else
            {
                urlCandidate = "https://" + trimmed;
            }
        }

        if (!Uri.TryCreate(urlCandidate, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The stream URL is not valid.", nameof(input));
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only HTTP and HTTPS stream URLs are supported.", nameof(input));
        }

        var host = NormalizeHost(uri.Host);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            throw new ArgumentException("The stream URL does not include a channel name.", nameof(input));
        }

        var platform = host.ToLowerInvariant() switch
        {
            "twitch.tv" => PlatformKind.Twitch,
            "kick.com" => PlatformKind.Kick,
            _ => throw new ArgumentException("Only Twitch and Kick URLs are supported.", nameof(input))
        };

        if (platform == PlatformKind.Twitch &&
            segments.Length == 2 &&
            segments[0].Equals("videos", StringComparison.OrdinalIgnoreCase) &&
            TwitchVodIdPattern().IsMatch(segments[1]))
        {
            return new ParsedInput(platform, "", segments[1]);
        }

        if (segments.Length != 1)
        {
            throw new ArgumentException("The stream URL must point directly to a channel.", nameof(input));
        }

        return new ParsedInput(platform, NormalizeChannel(segments[0]));
    }

    public static StreamTarget FromChannel(PlatformKind platform, string? channel)
    {
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "The stream platform is not supported.");
        }

        var normalized = NormalizeChannel(channel);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Channel name cannot be empty.", nameof(channel));
        }

        if (normalized is "." or ".." || IsKnownNonChannelPath(platform, normalized))
        {
            throw new ArgumentException("The channel name points to a platform page, not a channel.", nameof(channel));
        }

        if (!TryNormalizeChannelSlug(platform, normalized, out normalized))
        {
            var message = platform == PlatformKind.Twitch
                ? "Twitch channel names must contain 3 to 25 letters, numbers, or underscores."
                : "Kick channel names may contain letters, numbers, underscores, hyphens, and dots.";
            throw new ArgumentException(message, nameof(channel));
        }

        var url = platform == PlatformKind.Twitch
            ? $"https://www.twitch.tv/{normalized}"
            : $"https://kick.com/{normalized}";

        return new StreamTarget(platform, normalized, url);
    }

    public static bool TryFromChannel(
        PlatformKind platform,
        string? channel,
        out StreamTarget? target)
    {
        try
        {
            target = FromChannel(platform, channel);
            return true;
        }
        catch (ArgumentException)
        {
            target = null;
            return false;
        }
    }

    public static bool TryNormalizeChannelSlug(
        PlatformKind platform,
        string? channel,
        out string normalized)
    {
        normalized = NormalizeChannel(channel);
        if (!Enum.IsDefined(platform) ||
            normalized.Length == 0 ||
            normalized is "." or ".." ||
            IsKnownNonChannelPath(platform, normalized))
        {
            normalized = "";
            return false;
        }

        var isValid = platform == PlatformKind.Twitch
            ? TwitchChannelPattern().IsMatch(normalized)
            : ChannelPattern().IsMatch(normalized);
        if (!isValid)
        {
            normalized = "";
        }

        return isValid;
    }

    private static string NormalizeChannel(string? channel)
    {
        return (channel ?? "").Trim().Trim('@', '/');
    }

    private static string NormalizeHost(string host)
    {
        var normalized = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host;

        return normalized.Equals("m.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("m.kick.com", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..]
            : normalized;
    }

    private static bool StartsWithKnownPlatformHost(string input)
    {
        return Uri.TryCreate("https://" + input, UriKind.Absolute, out var uri) &&
            IsKnownPlatformHost(NormalizeHost(uri.Host));
    }

    private static bool IsKnownPlatformHost(string host)
    {
        return host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("kick.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownNonChannelPath(PlatformKind platform, string channel)
    {
        return PlatformRoutePolicy.IsNonChannelRoute(platform, channel);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex("^[A-Za-z0-9_]{3,25}$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchChannelPattern();

    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchVodIdPattern();

    private sealed record ParsedInput(PlatformKind? Platform, string Channel, string? VodId = null);
}
