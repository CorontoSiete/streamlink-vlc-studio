namespace StreamlinkVlcStudio.Core.Settings;

public sealed class UpdateSettings : NotifyPropertyChangedObject
{
    private bool automaticChecksEnabled = true;
    private string snoozedVersion = "";
    private DateTimeOffset? snoozedUntilUtc;

    public bool AutomaticChecksEnabled
    {
        get => automaticChecksEnabled;
        set => SetProperty(ref automaticChecksEnabled, value);
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

    public bool IsSnoozed(Version version, DateTimeOffset now) =>
        string.Equals(SnoozedVersion, version.ToString(3), StringComparison.Ordinal) &&
        SnoozedUntilUtc is { } until &&
        until > now;
}
