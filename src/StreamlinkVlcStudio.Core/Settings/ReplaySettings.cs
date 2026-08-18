namespace StreamlinkVlcStudio.Core.Settings;

public sealed class ReplaySettings : NotifyPropertyChangedObject
{
    private bool enabled = true;
    private bool attemptPrivateKickReplayResolution = true;

    public bool Enabled
    {
        get => enabled;
        set => SetProperty(ref enabled, value);
    }

    public bool AttemptPrivateKickReplayResolution
    {
        get => attemptPrivateKickReplayResolution;
        set => SetProperty(ref attemptPrivateKickReplayResolution, value);
    }
}
