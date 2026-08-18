namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

/// <summary>
/// Keeps transient recent-stream hints and live-status state separate from the
/// persisted settings list. All access is serialized because metadata refresh
/// and playback completion can arrive on different background operations.
/// </summary>
internal sealed class RecentStreamController
{
    private readonly object gate = new();
    private readonly Dictionary<string, RecentStreamHint> hints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecentStreamLiveStatus> liveStatuses = new(StringComparer.OrdinalIgnoreCase);

    public void SetHint(string stateKey, RecentStreamHint hint)
    {
        lock (gate)
        {
            hints[stateKey] = hint;
        }
    }

    public RecentStreamHint? TakeHint(string stateKey)
    {
        lock (gate)
        {
            return hints.Remove(stateKey, out var hint) ? hint : null;
        }
    }

    public bool RemoveLiveStatus(string stateKey)
    {
        lock (gate)
        {
            return liveStatuses.Remove(stateKey);
        }
    }

    public bool TryGetLiveStatus(string stateKey, out RecentStreamLiveStatus status)
    {
        lock (gate)
        {
            return liveStatuses.TryGetValue(stateKey, out status!);
        }
    }

    public bool SetLiveStatus(string stateKey, RecentStreamLiveStatus status)
    {
        lock (gate)
        {
            if (liveStatuses.TryGetValue(stateKey, out var current) && current == status)
            {
                return false;
            }

            liveStatuses[stateKey] = status;
            return true;
        }
    }
}

internal sealed record RecentStreamHint(string ThumbnailUrl, string DisplayName, string CategoryName);
