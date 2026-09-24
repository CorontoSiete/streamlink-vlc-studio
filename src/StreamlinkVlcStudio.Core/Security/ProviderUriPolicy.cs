using StreamlinkVlcStudio.Core.Models;

namespace StreamlinkVlcStudio.Core.Security;

/// <summary>Exact host and transport policy for media URLs that may be handed to VLC.</summary>
public static class ProviderUriPolicy
{
    public static bool IsApprovedReplayUri(Uri? uri, PlatformKind platform)
    {
        if (uri is null ||
            !uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            return false;
        }

        return IsApprovedReplayHost(uri.IdnHost, platform);
    }

    private static bool IsApprovedReplayHost(string? host, PlatformKind platform)
    {
        var normalized = (host ?? "").Trim().TrimEnd('.');
        if (normalized.Length == 0 || normalized.Any(char.IsControl))
        {
            return false;
        }

        return platform switch
        {
            PlatformKind.Twitch => IsHostOrSubdomain(normalized, "twitch.tv") ||
                IsHostOrSubdomain(normalized, "ttvnw.net") ||
                IsHostOrSubdomain(normalized, "jtvnw.net") ||
                IsCloudFrontDistributionHost(normalized),
            PlatformKind.Kick => IsHostOrSubdomain(normalized, "kick.com"),
            _ => false
        };
    }

    public static bool TryResolveReplayUri(
        string? value,
        Uri baseUri,
        PlatformKind platform,
        out Uri uri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        uri = null!;
        var candidate = (value ?? "").Trim();
        if (candidate.Length == 0 || candidate.Any(char.IsControl))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, candidate, out var resolved) ||
            !IsApprovedReplayUri(resolved, platform))
        {
            return false;
        }

        uri = resolved;
        return true;
    }

    private static bool IsHostOrSubdomain(string host, string expectedHost) =>
        string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith($".{expectedHost}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Twitch serves VOD segments from CloudFront distribution domains such as
    /// <c>d2e2de1etea730.cloudfront.net</c>. CloudFront is a multi-tenant CDN, so the whole
    /// <c>cloudfront.net</c> suffix cannot be trusted: accept only a single distribution label
    /// (the shape Twitch actually uses) and never a nested, attacker-chosen subdomain.
    /// </summary>
    private static bool IsCloudFrontDistributionHost(string host)
    {
        const string suffix = ".cloudfront.net";
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var distribution = host[..^suffix.Length];
        return distribution.Length > 0 && distribution.All(char.IsAsciiLetterOrDigit);
    }
}
