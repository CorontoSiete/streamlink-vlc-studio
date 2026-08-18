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
        // Publish the activation endpoint before claiming the mutex. A second
        // launch can otherwise observe the mutex during this short window and
        // have nowhere to signal the primary instance.
        activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isPrimaryInstance);
        if (!isPrimaryInstance)
        {
            SignalPrimaryInstance();
            Shutdown();
            return;
        }

        activationSignalCancellation = new CancellationTokenSource();
        activationSignalTask = Task.Run(() => WatchActivationSignals(activationSignalCancellation.Token));

        base.OnStartup(e);

        var setupRequested = e.Args.Any(argument =>
            string.Equals(argument, "--setup", StringComparison.OrdinalIgnoreCase));
        var mainWindow = new MainWindow(setupRequested);
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
        while (!cancellationToken.IsCancellationRequested)
        {
            int signaledHandle;
            try
            {
                signaledHandle = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (signaledHandle != 0 ||
                cancellationToken.IsCancellationRequested ||
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(ActivateMainWindow);
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.ShowInTaskbar = true;
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
