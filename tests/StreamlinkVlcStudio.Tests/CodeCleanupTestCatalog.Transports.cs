internal static partial class CodeCleanupTestCatalog
{
    private static async Task TransportShutdownLogReentryAsync(bool failLogging)
    {
        const string failure = "Injected transport log failure.";
        using var owner = RedirectedProcessOwner.Start(ProcessExtensions.CreateRedirectedStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), ["/d", "/c", "exit 0"]));
        var logger = new MemoryLogger();
        var session = new StreamlinkExternalHttpSession(owner, logger);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AttachOutputPumps(release.Task, release.Task);
        // Force the process-error diagnostic synchronously, before output draining.
        owner.Process.Dispose();
        var gate = typeof(StreamlinkExternalHttpSession)
            .GetField("disposeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var callbackEntered = false;
        var locked = false;
        Task? reentered = null;
        logger.EntryWritten += (_, _) =>
        {
            if (callbackEntered) return;
            callbackEntered = true;
            locked = Monitor.IsEntered(gate);
            reentered = session.DisposeAsync().AsTask();
            if (failLogging) throw new InvalidOperationException(failure);
        };
        try
        {
            var closing = session.DisposeAsync().AsTask();
            Assert.True(callbackEntered, "The process-error diagnostic did not invoke the subscriber.");
            Assert.True(!closing.IsCompleted, "Transport shutdown did not wait for its output pumps.");
            Assert.True(!locked, "Transport shutdown invoked a logging callback while holding its state lock.");
            Assert.True(ReferenceEquals(closing, reentered), "Reentrant transport shutdown received a different task.");
            release.TrySetResult();
            if (failLogging)
                await Assert.ThrowsAsync<InvalidOperationException>(() => closing.WaitAsync(TimeSpan.FromSeconds(5)));
            else
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ReferenceEquals(closing, session.DisposeAsync().AsTask()));
            Assert.Throws<ObjectDisposedException>(() => owner.StandardOutput.Peek());
            Assert.Throws<ObjectDisposedException>(() => owner.StandardError.Peek());
        }
        finally
        {
            release.TrySetResult();
            try { await Task.WhenAll(session.DisposeAsync().AsTask(), reentered ?? Task.CompletedTask); }
            catch (InvalidOperationException ex) when (failLogging && ex.Message == failure) { }
        }
    }
}
