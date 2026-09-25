internal static class AppLifecycleTestCatalog
{
    internal const string ExitSignalTestName = "app lifecycle: exiting an instance does not signal another instance";

    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        (ExitSignalTestName, ExitDoesNotSignalAnotherInstanceAsync)
    ];

    private static Task ExitDoesNotSignalAnotherInstanceAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new ExitTestApp();
                // Separate handles to the same named events model two app instances without
                // touching the real app's events or opening a window.
                var prefix = $"Local\\StreamStudio-test-{Guid.NewGuid():N}";
                using var primaryActivation = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-activate");
                using var primaryShutdown = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "-shutdown");
                typeof(App).GetField("activationEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(app, EventWaitHandle.OpenExisting(prefix + "-activate"));
                typeof(App).GetField("maintenanceShutdownEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(app, EventWaitHandle.OpenExisting(prefix + "-shutdown"));

                app.ExitForTest();
                Assert.Equal(false, primaryShutdown.WaitOne(0));
                Assert.Equal(false, primaryActivation.WaitOne(0));
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class ExitTestApp : App
    {
        internal void ExitForTest()
        {
            var args = (ExitEventArgs)Activator.CreateInstance(
                typeof(ExitEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [0], culture: null)!;
            OnExit(args);
        }
    }
}
