using StreamlinkVlcStudio.Core.Models;
using StreamlinkVlcStudio.Core.Parsing;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class FollowedChannelsSettings : NotifyPropertyChangedObject
{
    private List<string> kickChannelSlugs = [];
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
