using System.Globalization;

internal static class DependencyFreeTestRunner
{
    private static readonly TimeSpan DefaultTestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumConfiguredTimeout = TimeSpan.FromDays(1);
    // The bounded runner allows two five-second cleanup waits after `timeout + IsolatedProcessGrace`,
    // so the outer race must leave room for both. Otherwise a merely slow isolated test
    // trips the outer abort first and stops the whole remaining suite while the child is still
    // running, instead of failing that one test.
    private static readonly TimeSpan IsolatedProcessGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IsolatedProcessTimeoutMargin = TimeSpan.FromSeconds(20);
    private static readonly HashSet<string> FreshProcessTests = new(StringComparer.Ordinal)
    {
        AppLifecycleTestCatalog.ExitSignalTestName,
        "inactive window first click focuses docked chat input and accepts typing",
        "theatre chat input stays above the taskbar and accepts physical typing",
        "docked and theatre chat release native overlay keyboard capture before typing",
        "native overlay chat clears stale shift state after shifted symbol input",
        "native video double click exits theatre mode when the first click activates the window"
    };

    public static async Task<int> RunAsync(IReadOnlyList<(string Name, Func<Task> Run)> tests)
    {
        ArgumentNullException.ThrowIfNull(tests);

        var filter = Environment.GetEnvironmentVariable("SVS_TEST_FILTER");
        var selected = string.IsNullOrWhiteSpace(filter)
            ? tests.ToArray()
            : tests
                .Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (selected.Length == 0)
        {
            Console.Error.WriteLine($"FAIL no tests matched SVS_TEST_FILTER '{filter}'.");
            return 1;
        }

        var timeout = ReadPositiveSeconds("SVS_TEST_TIMEOUT_SECONDS", DefaultTestTimeout);
        var drainTimeout = ReadPositiveSeconds("SVS_TEST_DRAIN_TIMEOUT_SECONDS", DefaultDrainTimeout);
        var expectedMaximumSkips = ReadNonNegativeInteger("SVS_EXPECTED_MAX_SKIPS", int.MaxValue);
        var failed = 0;
        var passed = 0;
        var timedOut = 0;
        var skipped = 0;
        var executed = 0;
        var terminatedEarly = false;

        foreach (var test in selected)
        {
            executed++;
            var isolated = ShouldRunInFreshProcess(test.Name);
            var testTimeout = isolated ? timeout + IsolatedProcessTimeoutMargin : timeout;
            Task runTask;
            try
            {
                runTask = isolated
                    ? RunInFreshProcessAsync(test.Name, timeout)
                    : test.Run() ?? Task.FromException(
                        new InvalidOperationException($"Test '{test.Name}' returned a null task."));
            }
            catch (Exception ex)
            {
                runTask = Task.FromException(ex);
            }

            using var timeoutCancellation = new CancellationTokenSource();
            var timeoutTask = Task.Delay(testTimeout, timeoutCancellation.Token);
            var completed = await Task.WhenAny(runTask, timeoutTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, runTask))
            {
                await timeoutCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    // Awaiting the actual task here deliberately treats TimeoutException thrown by
                    // product/test code as an ordinary failure, not a runner timeout.
                    await runTask.ConfigureAwait(false);
                    passed++;
                    Console.WriteLine($"PASS {test.Name}");
                }
                catch (InteractiveDesktopTestSkippedException ex)
                {
                    skipped++;
                    Console.WriteLine($"SKIP {test.Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"FAIL {test.Name}: {ex}");
                }

                continue;
            }

            timedOut++;
            Console.WriteLine($"TIMEOUT {test.Name} after {testTimeout.TotalSeconds:0.#} seconds; draining before stopping.");

            if (!runTask.IsCompleted)
            {
                using var drainCancellation = new CancellationTokenSource();
                var drainTask = Task.Delay(drainTimeout, drainCancellation.Token);
                completed = await Task.WhenAny(runTask, drainTask).ConfigureAwait(false);
                if (ReferenceEquals(completed, runTask))
                {
                    await drainCancellation.CancelAsync().ConfigureAwait(false);
                }
            }

            if (ReferenceEquals(completed, runTask) || runTask.IsCompleted)
            {
                Observe(runTask);
                continue;
            }

            terminatedEarly = true;
            Console.Error.WriteLine(
                $"FATAL test '{test.Name}' did not finish during the {drainTimeout.TotalSeconds:0.#}-second drain; " +
                "terminating the run without starting more tests.");
            ObserveWhenComplete(runTask);
            break;
        }

        if (skipped > expectedMaximumSkips)
        {
            failed++;
            Console.Error.WriteLine(
                $"FAIL skip ceiling exceeded: observed {skipped}, expected at most {expectedMaximumSkips}.");
        }

        var notRun = selected.Length - executed;
        var succeeded = failed == 0 && timedOut == 0 && !terminatedEarly;
        Console.WriteLine(succeeded
            ? $"All {passed} tests passed; {skipped} skipped."
            : $"{passed} passed, {failed} failed, {timedOut} timed out, {skipped} skipped, " +
              $"{notRun} not run out of {selected.Length} selected tests.");
        return succeeded ? 0 : 1;
    }

    private static bool ShouldRunInFreshProcess(string testName) =>
        !string.Equals(
            Environment.GetEnvironmentVariable("SVS_TEST_ISOLATED_CHILD"),
            "true",
            StringComparison.OrdinalIgnoreCase) &&
        FreshProcessTests.Contains(testName);

    private static async Task RunInFreshProcessAsync(string testName, TimeSpan timeout)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Could not locate the test process host for isolated execution.");
        }

        var startInfo = BoundedProcessRunner.CreateRedirectedStartInfo(processPath, []);
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "StreamlinkVlcStudio.Tests.dll"));
        }

        startInfo.Environment["SVS_TEST_FILTER"] = testName;
        startInfo.Environment["SVS_TEST_ISOLATED_CHILD"] = "true";
        startInfo.Environment["SVS_EXPECTED_MAX_SKIPS"] = int.MaxValue.ToString(CultureInfo.InvariantCulture);

        var result = await new BoundedProcessRunner()
            .RunAsync(startInfo, timeout + IsolatedProcessGrace)
            .ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new TimeoutException($"Isolated test '{testName}' exceeded its process timeout.");
        }
        if (result.OutputWasTruncated)
        {
            throw new InvalidOperationException($"Isolated test '{testName}' exceeded its output limit or left output pipes open.");
        }

        ValidateIsolatedResult(testName, result.ExitCode, result.StandardOutput, result.StandardError);
    }

    internal static void ValidateIsolatedResult(string testName, int exitCode, string output, string error)
    {
        // A child can print a skip before failing during shutdown. Its exit status must
        // take precedence so a failed process cannot silently become a permitted skip.
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Isolated test process exited with code {exitCode}. " +
                $"Output: {output.Trim()} Error: {error.Trim()}".Trim());
        }

        var skipPrefix = $"SKIP {testName}: ";
        var results = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Equals($"PASS {testName}", StringComparison.Ordinal) ||
                line.StartsWith(skipPrefix, StringComparison.Ordinal))
            .ToArray();
        // A zero exit code alone does not prove that the child ran the requested test.
        // Require one unambiguous result, including when a similar test name appears.
        if (results.Length != 1)
        {
            throw new InvalidOperationException(
                $"Isolated test '{testName}' produced {results.Length} matching results; expected exactly one.");
        }
        if (results[0].StartsWith(skipPrefix, StringComparison.Ordinal))
        {
            throw new InteractiveDesktopTestSkippedException(results[0][skipPrefix.Length..]);
        }
    }

    private static void Observe(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private static void ObserveWhenComplete(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static TimeSpan ReadPositiveSeconds(string name, TimeSpan fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= 0 ||
            !double.IsFinite(seconds) ||
            seconds > MaximumConfiguredTimeout.TotalSeconds)
        {
            return fallback;
        }

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            return fallback;
        }
    }

    private static int ReadNonNegativeInteger(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : fallback;
    }
}
