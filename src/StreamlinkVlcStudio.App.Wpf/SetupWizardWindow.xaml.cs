using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using StreamlinkVlcStudio.App.Wpf.Services;
using StreamlinkVlcStudio.Core.Logging;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Core.Settings;
using StreamlinkVlcStudio.Infrastructure.Chat;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class SetupWizardWindow : Window, INotifyPropertyChanged
{
    private readonly ISettingsService settingsService;
    private readonly IAppLogger logger;
    private readonly ClipboardService clipboardService = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly object operationGate = new();
    private Task<bool>? finishOperation;
    private int activeOperationCount;
    private bool closeRequested;
    private bool handlersDetached;
    private bool cancellationDisposed;
    private bool dialogResultAssigned;
    private int currentStep;
    private bool isBusy;
    private string statusMessage = "Use Next to connect the platforms you want to use.";

    public SetupWizardWindow(AppSettings settings, ISettingsService settingsService, IAppLogger logger)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();
        DataContext = this;
        Settings.Chat.PropertyChanged += ChatSettingsPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppSettings Settings { get; }

    public int CurrentStep
    {
        get => currentStep;
        private set
        {
            if (currentStep == value)
            {
                return;
            }

            currentStep = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(IsWelcomeVisible));
            OnPropertyChanged(nameof(IsTwitchVisible));
            OnPropertyChanged(nameof(IsKickVisible));
            OnPropertyChanged(nameof(IsCompleteVisible));
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    public string ProgressText => CurrentStep switch
    {
        0 => "Step 1 of 4",
        1 => "Step 2 of 4",
        2 => "Step 3 of 4",
        _ => "Step 4 of 4"
    };

    public bool IsWelcomeVisible => CurrentStep == 0;

    public bool IsTwitchVisible => CurrentStep == 1;

    public bool IsKickVisible => CurrentStep == 2;

    public bool IsCompleteVisible => CurrentStep == 3;

    public bool CanGoBack => CurrentStep > 0 && !IsBusy;

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (isBusy == value)
            {
                return;
            }

            isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanConnectTwitch));
            OnPropertyChanged(nameof(CanConnectKick));
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string StatusMessage
    {
        get => statusMessage;
        private set
        {
            if (string.Equals(statusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            statusMessage = value;
            OnPropertyChanged();
        }
    }

    public bool CanConnectTwitch => !IsBusy && !string.IsNullOrWhiteSpace(Settings.Chat.TwitchClientId);

    public bool CanConnectKick => !IsBusy &&
        !string.IsNullOrWhiteSpace(Settings.Chat.KickClientId) &&
        !string.IsNullOrWhiteSpace(Settings.Chat.KickClientSecret);

    public string TwitchAccountStatus => HasTwitchConnection
        ? string.IsNullOrWhiteSpace(Settings.Chat.TwitchUsername)
            ? "Twitch account connected."
            : $"Connected as {Settings.Chat.TwitchUsername}."
        : "Not connected yet.";

    public string KickAccountStatus => HasKickConnection
        ? string.IsNullOrWhiteSpace(Settings.Chat.KickUsername)
            ? "Kick account connected."
            : $"Connected as {Settings.Chat.KickUsername}."
        : "Not connected yet.";

    public string DependencyStatus
    {
        get
        {
            var streamlinkReady = !string.IsNullOrWhiteSpace(Settings.StreamlinkPath) &&
                File.Exists(Settings.StreamlinkPath);
            var vlcReady = !string.IsNullOrWhiteSpace(Settings.VlcDirectory) &&
                File.Exists(Path.Combine(Settings.VlcDirectory, "libvlc.dll"));

            return streamlinkReady && vlcReady
                ? "Streamlink and VLC are ready."
                : "The app could not find Streamlink or VLC yet. Finish setup, then choose their paths in Settings if needed.";
        }
    }

    private bool HasTwitchConnection => !string.IsNullOrWhiteSpace(Settings.Chat.TwitchOAuthToken);

    private bool HasKickConnection => !string.IsNullOrWhiteSpace(Settings.Chat.KickOAuthToken) ||
        !string.IsNullOrWhiteSpace(Settings.Chat.KickRefreshToken);

    private void SetupWizardWindowLoaded(object sender, RoutedEventArgs e)
    {
        KickClientSecretBox.Password = Settings.Chat.KickClientSecret;
        OnPropertyChanged(nameof(CanConnectTwitch));
        OnPropertyChanged(nameof(CanConnectKick));
        OnPropertyChanged(nameof(TwitchAccountStatus));
        OnPropertyChanged(nameof(KickAccountStatus));
        OnPropertyChanged(nameof(DependencyStatus));
    }

    private void SetupWizardWindowClosing(object? sender, CancelEventArgs e)
    {
        lock (operationGate)
        {
            closeRequested = true;
        }
        lifetimeCancellation.Cancel();
        DetachHandlersOnce();
    }

    private void SetupWizardWindowClosed(object? sender, EventArgs e)
    {
        DisposeCancellationWhenIdle();
    }

    private void NextButtonClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        CurrentStep = Math.Min(CurrentStep + 1, 3);
    }

    private void BackButtonClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        CurrentStep = Math.Max(CurrentStep - 1, 0);
    }

    private async void CopyRedirectButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string redirectUri })
        {
            return;
        }

        try
        {
            var result = await clipboardService.TrySetTextAsync(redirectUri, lifetimeCancellation.Token);
            if (result.Succeeded)
            {
                StatusMessage = $"Copied {redirectUri}";
                return;
            }

            StatusMessage = "The clipboard is busy. Select and copy the redirect URL manually.";
            logger.Write(AppLogLevel.Info, "Setup", "Could not copy the OAuth redirect URL to the clipboard after three attempts.", result.Error);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private void OpenUrlButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            StatusMessage = "Opened the developer page in your browser.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not open the developer page. Copy the URL from the button and open it manually.";
            logger.Write(AppLogLevel.Warning, "Setup", "Could not open an OAuth developer page.", ex);
        }
    }

    private void KickClientSecretBoxPasswordChanged(object sender, RoutedEventArgs e)
    {
        Settings.Chat.KickClientSecret = KickClientSecretBox.Password;
        OnPropertyChanged(nameof(CanConnectKick));
    }

    private async void ConnectTwitchButtonClick(object sender, RoutedEventArgs e)
    {
        if (!CanConnectTwitch)
        {
            StatusMessage = "Enter the Twitch Client ID first.";
            return;
        }

        if (!TryBeginOperation())
        {
            return;
        }

        SetBusy(true, "Waiting for Twitch authorization in your browser...");
        try
        {
            var token = await TwitchOAuthService.AuthorizeUserTokenAsync(
                Settings.Chat,
                lifetimeCancellation.Token);
            TwitchOAuthService.ApplyTokenResult(Settings.Chat, token);
            await settingsService.SaveAsync(Settings, lifetimeCancellation.Token);
            StatusMessage = string.IsNullOrWhiteSpace(Settings.Chat.TwitchUsername)
                ? "Twitch connected."
                : $"Twitch connected as {Settings.Chat.TwitchUsername}.";
            OnPropertyChanged(nameof(TwitchAccountStatus));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Setup", "Twitch authorization failed during first-run setup.", ex);
        }
        catch (OperationCanceledException)
        {
            if (!IsCloseRequested())
            {
                StatusMessage = "Twitch authorization canceled.";
            }
        }
        finally
        {
            SetBusy(false, StatusMessage);
            EndOperation();
        }
    }

    private async void ConnectKickButtonClick(object sender, RoutedEventArgs e)
    {
        if (!CanConnectKick)
        {
            StatusMessage = "Enter the Kick Client ID and Client Secret first.";
            return;
        }

        if (!TryBeginOperation())
        {
            return;
        }

        SetBusy(true, "Waiting for Kick authorization in your browser...");
        try
        {
            var token = await KickOAuthService.AuthorizeUserTokenAsync(
                Settings.Chat,
                lifetimeCancellation.Token);
            KickOAuthService.ApplyTokenResult(Settings.Chat, token);

            if (string.IsNullOrWhiteSpace(Settings.Chat.KickUsername))
            {
                try
                {
                    var username = await KickOAuthService.TryGetCurrentUsernameAsync(
                        token.AccessToken,
                        lifetimeCancellation.Token);
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        Settings.Chat.KickUsername = username;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Write(AppLogLevel.Warning, "Setup", "Could not resolve the authorized Kick username.", ex);
                }
            }

            await settingsService.SaveAsync(Settings, lifetimeCancellation.Token);
            StatusMessage = string.IsNullOrWhiteSpace(Settings.Chat.KickUsername)
                ? "Kick connected."
                : $"Kick connected as {Settings.Chat.KickUsername}.";
            OnPropertyChanged(nameof(KickAccountStatus));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusMessage = ex.Message;
            logger.Write(AppLogLevel.Warning, "Setup", "Kick authorization failed during first-run setup.", ex);
        }
        catch (OperationCanceledException)
        {
            if (!IsCloseRequested())
            {
                StatusMessage = "Kick authorization canceled.";
            }
        }
        finally
        {
            SetBusy(false, StatusMessage);
            EndOperation();
        }
    }

    private async void FinishButtonClick(object sender, RoutedEventArgs e)
    {
        await FinishSetupAsync();
    }

    internal Task<bool> FinishSetupAsync()
    {
        lock (operationGate)
        {
            if (finishOperation is not null)
            {
                return finishOperation;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            finishOperation = completion.Task;
            _ = CompleteSetupAsync(completion);
            return finishOperation;
        }
    }

    private async Task CompleteSetupAsync(TaskCompletionSource<bool> completion)
    {
        var succeeded = false;
        var operationStarted = false;
        var previousSetupCompleted = Settings.SetupCompleted;
        var ownsSetupMutation = false;
        try
        {
            if (!TryBeginOperation())
            {
                completion.TrySetResult(false);
                return;
            }

            operationStarted = true;
            SetBusy(true, "Saving setup...");
            Settings.SetupCompleted = true;
            ownsSetupMutation = !previousSetupCompleted;
            await settingsService.SaveAsync(Settings, lifetimeCancellation.Token);
            lifetimeCancellation.Token.ThrowIfCancellationRequested();
            lock (operationGate)
            {
                if (closeRequested)
                {
                    throw new OperationCanceledException(lifetimeCancellation.Token);
                }
            }

            succeeded = true;
            if (!dialogResultAssigned)
            {
                dialogResultAssigned = true;
                DialogResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (ownsSetupMutation && Settings.SetupCompleted)
            {
                Settings.SetupCompleted = previousSetupCompleted;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ownsSetupMutation && Settings.SetupCompleted)
            {
                Settings.SetupCompleted = previousSetupCompleted;
            }
            StatusMessage = "Could not save setup. Check that the settings folder is writable, then try again.";
            logger.Write(AppLogLevel.Error, "Setup", "Could not save first-run setup.", ex);
        }
        finally
        {
            if (operationStarted)
            {
                SetBusy(false, StatusMessage);
                EndOperation();
            }

            if (!succeeded)
            {
                lock (operationGate)
                {
                    if (ReferenceEquals(finishOperation, completion.Task))
                    {
                        finishOperation = null;
                    }
                }
            }
            completion.TrySetResult(succeeded);
        }
    }

    private void ChatSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CanConnectTwitch));
        OnPropertyChanged(nameof(CanConnectKick));
        OnPropertyChanged(nameof(TwitchAccountStatus));
        OnPropertyChanged(nameof(KickAccountStatus));
        OnPropertyChanged(nameof(DependencyStatus));
    }

    private void SetBusy(bool value, string message)
    {
        IsBusy = value;
        StatusMessage = message;
    }

    private bool TryBeginOperation()
    {
        lock (operationGate)
        {
            if (closeRequested || cancellationDisposed || activeOperationCount != 0)
            {
                return false;
            }

            activeOperationCount++;
            return true;
        }
    }

    private bool IsCloseRequested()
    {
        lock (operationGate)
        {
            return closeRequested;
        }
    }

    private void EndOperation()
    {
        var shouldDispose = false;
        lock (operationGate)
        {
            if (activeOperationCount > 0)
            {
                activeOperationCount--;
            }
            shouldDispose = closeRequested && activeOperationCount == 0 && !cancellationDisposed;
            if (shouldDispose)
            {
                cancellationDisposed = true;
            }
        }
        if (shouldDispose)
        {
            lifetimeCancellation.Dispose();
        }
    }

    private void DisposeCancellationWhenIdle()
    {
        var shouldDispose = false;
        lock (operationGate)
        {
            shouldDispose = activeOperationCount == 0 && !cancellationDisposed;
            if (shouldDispose)
            {
                cancellationDisposed = true;
            }
        }
        if (shouldDispose)
        {
            lifetimeCancellation.Dispose();
        }
    }

    private void DetachHandlersOnce()
    {
        lock (operationGate)
        {
            if (handlersDetached)
            {
                return;
            }
            handlersDetached = true;
        }
        Settings.Chat.PropertyChanged -= ChatSettingsPropertyChanged;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
