using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class FollowedChannelsSettings : NotifyPropertyChangedObject
{
    private List<string> kickChannelSlugs = [];
    private List<string> kickImportedChannelSlugs = [];
    private List<string> pinnedOfflineChannelKeys = [];
    private DateTimeOffset? kickFollowsImportedAtUtc;
    private bool notifyWhenLive = true;

    public List<string> KickChannelSlugs
    {
        get => kickChannelSlugs;
        set => SetProperty(ref kickChannelSlugs, NormalizeChannelSlugs(value));
    }

    public bool NotifyWhenLive
    {
        get => notifyWhenLive;
        set => SetProperty(ref notifyWhenLive, value);
    }

    public List<string> KickImportedChannelSlugs
    {
        get => kickImportedChannelSlugs;
        set => SetProperty(ref kickImportedChannelSlugs, NormalizeChannelSlugs(value));
    }

    public DateTimeOffset? KickFollowsImportedAtUtc
    {
        get => kickFollowsImportedAtUtc;
        set => SetProperty(ref kickFollowsImportedAtUtc, value);
    }

    public List<string> PinnedOfflineChannelKeys
    {
        get => pinnedOfflineChannelKeys;
        set => SetProperty(ref pinnedOfflineChannelKeys, NormalizeChannelKeys(value));
    }

    private static List<string> NormalizeChannelKeys(IEnumerable<string>? values)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var separator = value.IndexOf(':');
            if (separator <= 0 ||
                !Enum.TryParse<PlatformKind>(value[..separator].Trim(), ignoreCase: true, out var platform) ||
                !StreamInputParser.TryFromChannel(platform, value[(separator + 1)..], out var target))
            {
                continue;
            }

            if (seen.Add(target.StateKey)) keys.Add(target.StateKey);
        }

        return keys;
    }

    private static List<string> NormalizeChannelSlugs(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        var slugs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            IReadOnlyList<StreamTarget> candidates;
            try
            {
                candidates = StreamInputParser.ParseCandidates(value ?? "");
            }
            catch (ArgumentException)
            {
                continue;
            }

            var slug = candidates.FirstOrDefault(target => target.Platform == PlatformKind.Kick)?.Channel;
            if (slug is null)
            {
                continue;
            }

            if (!seen.Add(slug))
            {
                continue;
            }

            slugs.Add(slug);
        }

        return slugs;
    }
}
