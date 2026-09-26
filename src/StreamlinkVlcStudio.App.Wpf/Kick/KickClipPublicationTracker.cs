using System.Text.Json;
using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf.Kick;

/// <summary>Observes the website's two verified clip contracts; never sends a publishing request.</summary>
internal sealed class KickClipPublicationTracker
{
    internal const int MaximumResponseBytes = 256 * 1024;
    private readonly HashSet<string> drafts = new(StringComparer.Ordinal);
    private readonly string channel;

    internal KickClipPublicationTracker(StreamTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        channel = target.Channel.Trim().ToLowerInvariant();
        if (target.Platform != PlatformKind.Kick || target.Kind != StreamTargetKind.Live || !IsIdentifier(channel))
            throw new InvalidOperationException("Select a live Kick channel to create a clip.");
    }

    internal Uri ChannelUri => new($"https://kick.com/{channel}");
    internal bool HasDraft => drafts.Count != 0;

    internal bool IsChannelPage(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && IsOrigin(uri, "kick.com") &&
        uri.AbsolutePath.TrimEnd('/').Equals($"/{channel}", StringComparison.OrdinalIgnoreCase);

    internal void Reset() => drafts.Clear();

    internal static bool IsClipRequest(string method, string address) =>
        TryReadRoute(method, address, out _, out _, out _);

    internal KickClipResult? Observe(string method, string address, int status, string body)
    {
        if (!TryReadRoute(method, address, out var route, out var requestedId, out var web)) return null;
        if (status is < 200 or >= 300)
            throw new InvalidOperationException(status switch
            {
                401 => "Sign in to Kick using Detect Kick follows in Settings, then click Clip again.",
                403 => "Kick refused clipping. Check your sign-in using Detect Kick follows in Settings and any channel restrictions.",
                429 => "Kick is limiting clip creation. Wait before trying again.",
                _ => $"Kick clip creation failed (HTTP {status}). Check your channel clips before retrying."
            });
        if (body.Length > MaximumResponseBytes)
            throw new InvalidOperationException("Kick's clip response was too large to verify.");

        try
        {
            using var document = JsonDocument.Parse(body);
            var clip = document.RootElement;
            if (web && clip.ValueKind == JsonValueKind.Object && clip.TryGetProperty("data", out var data)) clip = data;
            if (clip.ValueKind != JsonValueKind.Object || !clip.TryGetProperty("id", out var value) ||
                value.ValueKind != JsonValueKind.String || !IsIdentifier(value.GetString()))
                throw new InvalidOperationException("Kick did not return a valid clip ID. Publication could not be verified.");
            var id = value.GetString()!;
            if (requestedId is null)
            {
                if (drafts.Count >= 32)
                    throw new InvalidOperationException("Kick created too many clip drafts. Try again later.");
                drafts.Add(route + "/" + id);
                return null;
            }

            // Correlate the request with the draft, but use the final response's public ID,
            // as Kick's editor does. The website does not require the two IDs to be identical.
            if (!drafts.Remove(route + "/" + requestedId))
                throw new InvalidOperationException("Kick's published clip did not match the draft created for this request.");
            return new KickClipResult(id, new Uri($"https://kick.com/{channel}/clips/{id}"));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Kick returned an unreadable clip response. Publication could not be verified.", ex);
        }
    }

    private static bool TryReadRoute(string method, string address, out string route, out string? clipId, out bool web)
    {
        route = "";
        clipId = null;
        web = false;
        if (method != "POST" || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var parts = uri.AbsolutePath.Split('/');
        int draftLength;
        if (IsOrigin(uri, "web.kick.com") && parts.Length >= 4 &&
            parts[1] == "api" && parts[2] == "v1" && parts[3] == "clips")
        {
            web = true;
            draftLength = 4;
        }
        else if (IsOrigin(uri, "kick.com") && parts.Length >= 7 &&
                 parts[1] == "api" && parts[2] == "internal" && parts[3] == "v1" &&
                 parts[4] == "livestreams" && IsIdentifier(parts[5]) && parts[6] == "clips")
        {
            draftLength = 7;
        }
        else return false;

        if (parts.Length != draftLength)
        {
            if (parts.Length != draftLength + 2 || !IsIdentifier(parts[draftLength]) || parts[^1] != "finalize") return false;
            clipId = parts[draftLength];
        }
        route = uri.GetLeftPart(UriPartial.Authority) + string.Join('/', parts.Take(draftLength));
        return true;
    }

    private static bool IsOrigin(Uri uri, string host) => uri.Scheme == "https" && uri.Host == host &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0;

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
