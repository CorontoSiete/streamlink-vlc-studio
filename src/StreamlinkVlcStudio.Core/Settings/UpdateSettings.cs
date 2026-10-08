namespace StreamlinkVlcStudio.Core.Settings;

public sealed class UpdateSettings : NotifyPropertyChangedObject
{
    private bool automaticChecksEnabled = true;
    private bool automaticDownloadsEnabled;
    private string snoozedVersion = "";
    private DateTimeOffset? snoozedUntilUtc;
    private string lastSeenChangelogVersion = "";

    public bool AutomaticChecksEnabled
    {
        get => automaticChecksEnabled;
        set => SetProperty(ref automaticChecksEnabled, value);
    }

    public bool AutomaticDownloadsEnabled
    {
        get => automaticDownloadsEnabled;
        set => SetProperty(ref automaticDownloadsEnabled, value);
    }

    public string SnoozedVersion
    {
        get => snoozedVersion;
        set => SetProperty(ref snoozedVersion, value?.Trim() ?? "");
    }

    public DateTimeOffset? SnoozedUntilUtc
    {
        get => snoozedUntilUtc;
        set => SetProperty(ref snoozedUntilUtc, value);
    }

    public string LastSeenChangelogVersion
    {
        get => lastSeenChangelogVersion;
        set => SetProperty(ref lastSeenChangelogVersion, value?.Trim() ?? "");
    }

    public bool IsSnoozed(Version version, DateTimeOffset now) =>
        string.Equals(SnoozedVersion, version.ToString(3), StringComparison.Ordinal) &&
        SnoozedUntilUtc is { } until &&
        until > now;
}
