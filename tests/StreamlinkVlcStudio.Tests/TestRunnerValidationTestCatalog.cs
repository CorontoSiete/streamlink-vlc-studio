internal static class TestRunnerValidationTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("test runner never treats a failed child process as an interactive skip", FailedChildIsNotSkipped)
    ];

    private static Task FailedChildIsNotSkipped()
    {
        const string output = "SKIP fixture: interactive desktop unavailable\r\nAll 0 tests passed; 1 skipped.\r\n";
        Assert.Throws<InvalidOperationException>(() =>
            DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 1, output, "shutdown failed"));

        Assert.Throws<InteractiveDesktopTestSkippedException>(() =>
            DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 0, output, ""));
        DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 0, "PASS fixture\r\n", "");
        DependencyFreeTestRunner.ValidateIsolatedResult("fixture", 0, "SKIP fixture extended: different test\r\n", "");
        return Task.CompletedTask;
    }
}
