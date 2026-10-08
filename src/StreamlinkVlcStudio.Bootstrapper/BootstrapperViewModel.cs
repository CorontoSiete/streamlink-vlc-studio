using System.Windows.Input;
using StreamlinkVlcStudio.Core.Commands;
using StreamlinkVlcStudio.Core.Settings;

namespace StreamlinkVlcStudio.Bootstrapper;

internal sealed class BootstrapperViewModel : NotifyPropertyChangedObject
{
    private BootstrapperPage page = BootstrapperPage.Loading;
    private string statusText = "Checking your system…";
    private string operationTitle = "Preparing setup";
    private string resultTitle = string.Empty;
    private string resultMessage = string.Empty;
    private string streamlinkStatus = "Checking…";
    private string vlcStatus = "Checking…";
    private string webView2Status = "Checking…";
    private int progress;
    private bool purgeUserData;
    private bool cancelRequested;
    private bool isRollingBack;
    private bool isVerifyingDependencies;
    private bool canRetry;
    private bool resultSucceeded;
    private bool resultWarning;
    private bool canLaunch;
    private bool canOpenLog;

    public BootstrapperViewModel(StudioBootstrapperApplication application)
    {
        Version = application.BundleVersion;
        StreamlinkVersion = application.StreamlinkVersion;
        VlcVersion = application.VlcVersion;
        WebView2Version = application.WebView2Version;
        InstallCommand = new RelayCommand(application.Install, () => Page == BootstrapperPage.Install);
        RepairCommand = new RelayCommand(application.Repair, () => Page == BootstrapperPage.Maintenance);
        UninstallCommand = new RelayCommand(application.Uninstall, () => Page == BootstrapperPage.Maintenance);
        CancelCommand = new RelayCommand(application.RequestCancel, () => Page == BootstrapperPage.Progress && !CancelRequested && !IsRollingBack && !IsVerifyingDependencies);
        RetryCommand = new RelayCommand(application.Retry, () => Page == BootstrapperPage.Result && CanRetry);
        CloseCommand = new RelayCommand(application.Close, () => Page != BootstrapperPage.Progress);
        OpenLogCommand = new RelayCommand(application.OpenLog, () => CanOpenLog);
        LaunchCommand = new RelayCommand(application.LaunchApplication, () => CanLaunch);
    }

    public string Version { get; }

    public string StreamlinkVersion { get; }

    public string VlcVersion { get; }

    public string WebView2Version { get; }

    public ICommand InstallCommand { get; }

    public ICommand RepairCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RetryCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand OpenLogCommand { get; }

    public ICommand LaunchCommand { get; }

    public BootstrapperPage Page
    {
        get => page;
        set
        {
            if (SetProperty(ref page, value))
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
        set => SetProperty(ref statusText, value);
    }

    public string OperationTitle
    {
        get => operationTitle;
        set => SetProperty(ref operationTitle, value);
    }

    public string ResultTitle
    {
        get => resultTitle;
        set => SetProperty(ref resultTitle, value);
    }

    public string ResultMessage
    {
        get => resultMessage;
        set => SetProperty(ref resultMessage, value);
    }

    public string StreamlinkStatus
    {
        get => streamlinkStatus;
        set => SetProperty(ref streamlinkStatus, value);
    }

    public string VlcStatus
    {
        get => vlcStatus;
        set => SetProperty(ref vlcStatus, value);
    }

    public string WebView2Status
    {
        get => webView2Status;
        set => SetProperty(ref webView2Status, value);
    }

    public int Progress
    {
        get => progress;
        set => SetProperty(ref progress, Math.Clamp(value, 0, 100));
    }

    public bool PurgeUserData
    {
        get => purgeUserData;
        set => SetProperty(ref purgeUserData, value);
    }

    public bool CancelRequested
    {
        get => cancelRequested;
        set
        {
            if (SetProperty(ref cancelRequested, value))
            {
                ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRollingBack
    {
        get => isRollingBack;
        set
        {
            if (SetProperty(ref isRollingBack, value)) ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        }
    }

    public bool CanRetry
    {
        get => canRetry;
        set
        {
            if (SetProperty(ref canRetry, value)) ((RelayCommand)RetryCommand).RaiseCanExecuteChanged();
        }
    }

    public bool IsVerifyingDependencies
    {
        get => isVerifyingDependencies;
        set
        {
            if (SetProperty(ref isVerifyingDependencies, value)) ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        }
    }

    public bool ResultSucceeded
    {
        get => resultSucceeded;
        set => SetProperty(ref resultSucceeded, value);
    }

    public bool ResultWarning
    {
        get => resultWarning;
        set => SetProperty(ref resultWarning, value);
    }

    public bool CanLaunch
    {
        get => canLaunch;
        set
        {
            if (SetProperty(ref canLaunch, value))
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
            if (SetProperty(ref canOpenLog, value))
            {
                ((RelayCommand)OpenLogCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private void RaiseCommandStates()
    {
        ((RelayCommand)InstallCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RepairCommand).RaiseCanExecuteChanged();
        ((RelayCommand)UninstallCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RetryCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CloseCommand).RaiseCanExecuteChanged();
    }
}
