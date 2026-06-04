using System.Windows;

namespace StreamlinkVlcStudio.App.Wpf;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Local\\StreamlinkVlcStudio.App.SingleInstance";
    private const string ActivationEventName = "Local\\StreamlinkVlcStudio.App.Activate";
    private Mutex? singleInstanceMutex;
    private EventWaitHandle? activationEvent;
    private CancellationTokenSource? activationSignalCancellation;
    private Task? activationSignalTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isPrimaryInstance);
        if (!isPrimaryInstance)
        {
            SignalPrimaryInstance();
            Shutdown();
            return;
        }

        activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        activationSignalCancellation = new CancellationTokenSource();
        activationSignalTask = Task.Run(() => WatchActivationSignals(activationSignalCancellation.Token));

        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        activationSignalCancellation?.Cancel();
        activationEvent?.Set();

        try
        {
            activationSignalTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
        }

        activationSignalCancellation?.Dispose();
        activationEvent?.Dispose();

        if (singleInstanceMutex is not null)
        {
            try
            {
                singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private static void SignalPrimaryInstance()
    {
        try
        {
            using var existingActivationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            existingActivationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WatchActivationSignals(CancellationToken cancellationToken)
    {
        if (activationEvent is null)
        {
            return;
        }

        WaitHandle[] handles = [activationEvent, cancellationToken.WaitHandle];
        while (WaitHandle.WaitAny(handles) == 0)
        {
            Dispatcher.BeginInvoke(ActivateMainWindow);
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        if (!MainWindow.IsVisible)
        {
            MainWindow.Show();
        }

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }
}
