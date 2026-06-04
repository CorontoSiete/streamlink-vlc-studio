using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Parsing;

public static partial class StreamInputParser
{
    private static readonly HashSet<string> TwitchNonChannelPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "clips",
        "directory",
        "downloads",
        "drops",
        "inventory",
        "jobs",
        "moderator",
        "p",
        "popout",
        "search",
        "settings",
        "subscriptions",
        "turbo",
        "videos",
        "wallet"
    };

    private static readonly HashSet<string> KickNonChannelPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "categories",
        "community-guidelines",
        "dashboard",
        "following",
        "privacy",
        "search",
        "terms-of-service",
        "video",
        "videos"
    };

    public static IReadOnlyList<StreamTarget> ParseCandidates(string input)
    {
        var parsed = ParseInput(input);
        if (parsed.Platform is not null)
        {
            return [FromChannel(parsed.Platform.Value, parsed.Channel)];
        }

        var normalized = NormalizeChannel(parsed.Channel);
        if (!CouldBeTwitchChannel(normalized))
        {
            return [FromChannel(PlatformKind.Kick, normalized)];
        }

        return
        [
            FromChannel(PlatformKind.Twitch, normalized),
            FromChannel(PlatformKind.Kick, normalized)
        ];
    }

    public static StreamTarget Parse(string input, PlatformKind defaultPlatform)
    {
        var parsed = ParseInput(input);
        return FromChannel(parsed.Platform ?? defaultPlatform, parsed.Channel);
    }

    public static bool TryParsePlatformUrl(string input, out StreamTarget? target)
    {
        target = null;

        try
        {
            var parsed = ParseInput(input);
            if (parsed.Platform is null || IsKnownNonChannelPath(parsed.Platform.Value, parsed.Channel))
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
            if (!StartsWithKnownPlatformHost(trimmed))
            {
                return new ParsedInput(null, trimmed);
            }

            urlCandidate = "https://" + trimmed;
        }

        if (!Uri.TryCreate(urlCandidate, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("The stream URL is not valid.", nameof(input));
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

        var channel = NormalizeChannel(segments[0]);
        if (IsKnownNonChannelPath(platform, channel))
        {
            throw new ArgumentException("The stream URL points to a platform page, not a channel.", nameof(input));
        }

        return new ParsedInput(platform, channel);
    }

    public static StreamTarget FromChannel(PlatformKind platform, string channel)
    {
        var normalized = NormalizeChannel(channel);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Channel name cannot be empty.", nameof(channel));
        }

        if (!ChannelPattern().IsMatch(normalized))
        {
            throw new ArgumentException("Channel names may contain letters, numbers, underscores, hyphens, and dots.", nameof(channel));
        }

        var url = platform == PlatformKind.Twitch
            ? $"https://www.twitch.tv/{normalized}"
            : $"https://kick.com/{normalized}";

        return new StreamTarget(platform, normalized, url);
    }

    private static string NormalizeChannel(string channel)
    {
        return channel.Trim().TrimStart('@').Trim('/');
    }

    private static bool CouldBeTwitchChannel(string channel)
    {
        return TwitchChannelPattern().IsMatch(channel);
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
        var separatorIndex = input.IndexOfAny(['/', '?', '#']);
        var host = separatorIndex >= 0 ? input[..separatorIndex] : input;
        return IsKnownPlatformHost(NormalizeHost(host));
    }

    private static bool IsKnownPlatformHost(string host)
    {
        return host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("kick.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownNonChannelPath(PlatformKind platform, string channel)
    {
        return platform switch
        {
            PlatformKind.Twitch => TwitchNonChannelPaths.Contains(channel),
            PlatformKind.Kick => KickNonChannelPaths.Contains(channel),
            _ => false
        };
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex("^[A-Za-z0-9_]{3,25}$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchChannelPattern();

    private sealed record ParsedInput(PlatformKind? Platform, string Channel);
}
