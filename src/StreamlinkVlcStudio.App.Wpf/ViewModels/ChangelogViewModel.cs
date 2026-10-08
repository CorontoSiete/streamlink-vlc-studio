using StreamlinkVlcStudio.App.Wpf.Services;
using StreamlinkVlcStudio.Core.Commands;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.App.Wpf.ViewModels;

public sealed class ChangelogViewModel : ObservableObject
{
    private readonly ReleaseNotesCatalog catalog;
    private readonly AppSettings settings;
    private readonly bool setupWasCompleted;
    private ReleaseNotes? selectedRelease;

    internal ChangelogViewModel(ReleaseNotesCatalog catalog, AppSettings settings)
    {
        this.catalog = catalog;
        this.settings = settings;
        setupWasCompleted = settings.SetupCompleted;
        selectedRelease = catalog.InstalledRelease;
        ShowNewerReleaseCommand = new RelayCommand(() => MoveSelection(-1), () => CanMoveSelection(-1));
        ShowOlderReleaseCommand = new RelayCommand(() => MoveSelection(1), () => CanMoveSelection(1));
        ShowInstalledReleaseCommand = new RelayCommand(SelectInstalledRelease, () => CanSelectInstalledRelease);
    }

    public IReadOnlyList<ReleaseNotes> Releases => catalog.Releases;
    public string InstalledVersionText => $"Installed version {catalog.InstalledVersion.ToString(3)}";
    public RelayCommand ShowNewerReleaseCommand { get; }
    public RelayCommand ShowOlderReleaseCommand { get; }
    public RelayCommand ShowInstalledReleaseCommand { get; }
    public bool IsInstalledReleaseSelected => SelectedRelease?.Version == catalog.InstalledVersion;
    public bool CanSelectInstalledRelease => catalog.InstalledRelease is not null && !IsInstalledReleaseSelected;
    public string SelectedReleaseLabel => IsInstalledReleaseSelected ? "INSTALLED VERSION" : "EARLIER RELEASE";
    public string ReleasePositionText => HasSelectedRelease ? $"{SelectedReleaseIndex + 1} of {Releases.Count} releases" : "";

    public ReleaseNotes? SelectedRelease
    {
        get => selectedRelease;
        set
        {
            if (value is not null && !Releases.Contains(value)) return;
            if (!SetProperty(ref selectedRelease, value)) return;
            OnPropertyChanged(nameof(HasSelectedRelease));
            OnPropertyChanged(nameof(IsInstalledReleaseSelected));
            OnPropertyChanged(nameof(CanSelectInstalledRelease));
            OnPropertyChanged(nameof(SelectedReleaseLabel));
            OnPropertyChanged(nameof(ReleasePositionText));
            ShowNewerReleaseCommand.RaiseCanExecuteChanged();
            ShowOlderReleaseCommand.RaiseCanExecuteChanged();
            ShowInstalledReleaseCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelectedRelease => SelectedRelease is not null;

    internal void SelectInstalledRelease() => SelectedRelease = catalog.InstalledRelease;

    private int SelectedReleaseIndex
    {
        get
        {
            for (var index = 0; index < Releases.Count; index++)
                if (Releases[index] == SelectedRelease) return index;
            return -1;
        }
    }

    private bool CanMoveSelection(int offset) => SelectedReleaseIndex >= 0 &&
        SelectedReleaseIndex + offset >= 0 && SelectedReleaseIndex + offset < Releases.Count;

    private void MoveSelection(int offset)
    {
        if (CanMoveSelection(offset)) SelectedRelease = Releases[SelectedReleaseIndex + offset];
    }

    internal bool ShouldOpenAfterStartup(AppUpdateCompletion? completion, Version? pendingRepairVersion = null)
    {
        if (catalog.InstalledRelease is null) return false;
        var completedInstalledUpdate = completion is
        { Outcome: AppUpdateCompletionOutcome.Succeeded or AppUpdateCompletionOutcome.SucceededRebootRequired } &&
            (completion.TargetVersion is null || ReleaseNotesCatalog.Normalize(completion.TargetVersion) == catalog.InstalledVersion);
        // A successful installer result remains authoritative when a stale repair
        // notice is locked against deletion by antivirus or another process.
        if (!completedInstalledUpdate && pendingRepairVersion is not null &&
            ReleaseNotesCatalog.Normalize(pendingRepairVersion) >= catalog.InstalledVersion)
            return false;

        // Setup can replace the executable before a later package fails. Wait for a
        // successful repair instead of presenting that incomplete install as an update.
        if (completion is not null &&
            (completion.TargetVersion is null || ReleaseNotesCatalog.Normalize(completion.TargetVersion) >= catalog.InstalledVersion) &&
            completion.Outcome is AppUpdateCompletionOutcome.Failed or AppUpdateCompletionOutcome.Canceled)
            return false;

        if (completion?.TargetVersion is { } target && ReleaseNotesCatalog.Normalize(target) > catalog.InstalledVersion)
            return false;

        if (Version.TryParse(settings.Updates.LastSeenChangelogVersion, out var seen))
            return ReleaseNotesCatalog.Normalize(seen) < catalog.InstalledVersion;

        // Existing users have no marker when this feature first arrives. A fresh
        // installation establishes a baseline without interrupting the setup wizard.
        if (!setupWasCompleted && completion?.Outcome is not
            (AppUpdateCompletionOutcome.Succeeded or AppUpdateCompletionOutcome.SucceededRebootRequired))
        {
            MarkInstalledReleaseSeen();
            return false;
        }

        return true;
    }

    internal void MarkPresented()
    {
        if (SelectedRelease?.Version == catalog.InstalledVersion) MarkInstalledReleaseSeen();
    }

    private void MarkInstalledReleaseSeen()
    {
        if (Version.TryParse(settings.Updates.LastSeenChangelogVersion, out var seen) &&
            ReleaseNotesCatalog.Normalize(seen) >= catalog.InstalledVersion) return;
        settings.Updates.LastSeenChangelogVersion = catalog.InstalledVersion.ToString(3);
    }
}
