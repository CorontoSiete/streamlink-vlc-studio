using System.Text.Json.Serialization;

namespace StreamlinkVlcStudio.Core.Settings;

public sealed class DownloadSettings : NotifyPropertyChangedObject
{
    private string quality = "best";
    private double bandwidthLimitMegabytesPerSecond;
    private string? directory;
    private List<string> previousDirectories = [];

    public string Quality
    {
        get => quality;
        set => SetProperty(ref quality, string.IsNullOrWhiteSpace(value) ? "best" : value.Trim());
    }

    public double BandwidthLimitMegabytesPerSecond
    {
        get => bandwidthLimitMegabytesPerSecond;
        set => SetProperty(ref bandwidthLimitMegabytesPerSecond, double.IsFinite(value) && value >= 0 ? value : 0);
    }

    [JsonIgnore]
    public long BandwidthLimitBytesPerSecond => BandwidthLimitMegabytesPerSecond >= long.MaxValue / 1_000_000d
        ? long.MaxValue : (long)Math.Ceiling(BandwidthLimitMegabytesPerSecond * 1_000_000);

    public string? Directory
    {
        get => directory;
        set => SetProperty(ref directory, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public List<string> PreviousDirectories
    {
        get => previousDirectories;
        set => SetProperty(ref previousDirectories, (value ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
