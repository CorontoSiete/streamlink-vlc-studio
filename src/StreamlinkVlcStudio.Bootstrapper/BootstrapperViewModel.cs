using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace StreamlinkVlcStudio.Bootstrapper;

internal sealed class BootstrapperViewModel : INotifyPropertyChanged
{
    private BootstrapperPage page = BootstrapperPage.Loading;
    private string statusText = "Checking your system…";
    private string operationTitle = "Preparing setup";
    private string resultTitle = string.Empty;
    private string resultMessage = string.Empty;
    private string streamlinkStatus = "Checking…";
    private string vlcStatus = "Checking…";
    private int progress;
    private bool purgeUserData;
    private bool cancelRequested;
    private bool resultSucceeded;
    private bool resultWarning;
    private bool canLaunch;
    private bool canOpenLog;

    public BootstrapperViewModel(StudioBootstrapperApplication application)
    {
        Version = application.BundleVersion;
        StreamlinkVersion = application.StreamlinkVersion;
        VlcVersion = application.VlcVersion;
        InstallCommand = new RelayCommand(application.Install, () => Page == BootstrapperPage.Install);
        RepairCommand = new RelayCommand(application.Repair, () => Page == BootstrapperPage.Maintenance);
        UninstallCommand = new RelayCommand(application.Uninstall, () => Page == BootstrapperPage.Maintenance);
        CancelCommand = new RelayCommand(application.RequestCancel, () => Page == BootstrapperPage.Progress && !CancelRequested);
        CloseCommand = new RelayCommand(application.Close, () => Page != BootstrapperPage.Progress);
        OpenLogCommand = new RelayCommand(application.OpenLog, () => CanOpenLog);
        LaunchCommand = new RelayCommand(application.LaunchApplication, () => CanLaunch);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Version { get; }

    public string StreamlinkVersion { get; }

    public string VlcVersion { get; }

    public ICommand InstallCommand { get; }

    public ICommand RepairCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand OpenLogCommand { get; }

    public ICommand LaunchCommand { get; }

    public BootstrapperPage Page
    {
        get => page;
        set
        {
            if (Set(ref page, value))
            {
                OnPropertyChanged(nameof(IsLoadingPage));
                OnPropertyChanged(nameof(IsInstallPage));
                OnPropertyChanged(nameof(IsMaintenancePage));
                OnPropertyChanged(nameof(IsProgressPage));
                OnPropertyChanged(nameof(IsResultPage));
                RaiseCommandStates();
            }
        }
    }

    public bool IsLoadingPage => Page == BootstrapperPage.Loading;

    public bool IsInstallPage => Page == BootstrapperPage.Install;

    public bool IsMaintenancePage => Page == BootstrapperPage.Maintenance;

    public bool IsProgressPage => Page == BootstrapperPage.Progress;

    public bool IsResultPage => Page == BootstrapperPage.Result;

    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }

    public string OperationTitle
    {
        get => operationTitle;
        set => Set(ref operationTitle, value);
    }

    public string ResultTitle
    {
        get => resultTitle;
        set => Set(ref resultTitle, value);
    }

    public string ResultMessage
    {
        get => resultMessage;
        set => Set(ref resultMessage, value);
    }

    public string StreamlinkStatus
    {
        get => streamlinkStatus;
        set => Set(ref streamlinkStatus, value);
    }

    public string VlcStatus
    {
        get => vlcStatus;
        set => Set(ref vlcStatus, value);
    }

    public int Progress
    {
        get => progress;
        set => Set(ref progress, Math.Clamp(value, 0, 100));
    }

    public bool PurgeUserData
    {
        get => purgeUserData;
        set => Set(ref purgeUserData, value);
    }

    public bool CancelRequested
    {
        get => cancelRequested;
        set
        {
            if (Set(ref cancelRequested, value))
            {
                ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool ResultSucceeded
    {
        get => resultSucceeded;
        set => Set(ref resultSucceeded, value);
    }

    public bool ResultWarning
    {
        get => resultWarning;
        set => Set(ref resultWarning, value);
    }

    public bool CanLaunch
    {
        get => canLaunch;
        set
        {
            if (Set(ref canLaunch, value))
            {
                ((RelayCommand)LaunchCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanOpenLog
    {
        get => canOpenLog;
        set
        {
            if (Set(ref canOpenLog, value))
            {
                ((RelayCommand)OpenLogCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)InstallCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RepairCommand).RaiseCanExecuteChanged();
        ((RelayCommand)UninstallCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CloseCommand).RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
