internal static class TestRunnerValidationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("test runner never treats a failed child process as an interactive skip", FailedChildIsNotSkipped),
        ("test runner rejects successful exits without the requested test result", MissingResultIsNotSuccess),
        ("test runner rejects conflicting or duplicate child results", AmbiguousResultIsNotSuccess)
    ];

    private static Task FailedChildIsNotSkipped()
    {
        const string output = "SKIP fixture: interactive desktop unavailable\r\nAll 0 tests passed; 1 skipped.\r\n";
        Assert.Throws<InvalidOperationException>(() =>
            DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 1, output, "shutdown failed"));

        Assert.Throws<InteractiveDesktopTestSkippedException>(() =>
            DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 0, output, ""));
        DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 0, "PASS fixture\r\n", "");
        return Task.CompletedTask;
    }

    private static Task MissingResultIsNotSuccess()
    {
        foreach (var output in new[] { "", "PASS fixture extended\r\n", "SKIP fixture extended: different test\r\n" })
        {
            Assert.Throws<InvalidOperationException>(() =>
                DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 0, output, ""));
        }
        return Task.CompletedTask;
    }

    private static Task AmbiguousResultIsNotSuccess()
    {
        foreach (var output in new[] { "PASS fixture\nSKIP fixture: conflicting result\n", "PASS fixture\nPASS fixture\n" })
        {
            Assert.Throws<InvalidOperationException>(() =>
                DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 0, output, ""));
        }
        return Task.CompletedTask;
    }
}
