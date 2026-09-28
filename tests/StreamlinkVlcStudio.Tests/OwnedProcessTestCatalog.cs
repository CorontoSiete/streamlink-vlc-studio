using System.ComponentModel;

internal static class OwnedProcessTestCatalog
{
    internal static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        ("owned process: stop after launcher exit kills children and grandchildren", SessionLauncherExitAsync),
        ("owned process: independent sessions and unrelated processes survive another stop", IndependentSessionsAsync),
        ("owned process: probe completion closes descendants holding output pipes", ProbeLauncherExitAsync),
        ("owned process: cancellation terminates a ready tree and drains output", CancellationAsync),
        ("owned process: timeout terminates children and grandchildren", TimeoutAsync),
        ("owned process: abrupt owner exit closes a non-inherited job", AbruptOwnerExitAsync),
        ("owned process: failed transport startup terminates its tree", FailedTransportStartupAsync),
        ("owned process: failed creation releases handles", FailedCreationAsync),
        ("owned process: argument quoting environment working directory and encodings", ArgumentsAsync)
    ];

    private static async Task SessionLauncherExitAsync()
    {
        await using var tree = new TestTree();
        await using var session = await StartSessionAsync(tree, "transport");
        var processes = await tree.ObserveAsync("launcher", "child", "grandchild");
        await File.WriteAllTextAsync(Path.Combine(tree.Directory, "exit"), "exit");
        await processes[0].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(false, processes[1].HasExited);
        Assert.Equal(false, processes[2].HasExited);
        await session.DisposeAsync();
        await AssertExitedAsync(processes);
    }

    private static async Task IndependentSessionsAsync()
    {
        await using var first = new TestTree();
        await using var second = new TestTree();
        await using var unrelated = new TestTree();
        using var other = Process.Start(OwnedProcessTestHost.StartInfo("unrelated", unrelated.Directory))!;
        var sessions = await Task.WhenAll(StartSessionAsync(first, "transport"), StartSessionAsync(second, "transport"));
        await using var a = sessions[0];
        await using var b = sessions[1];
        var firstProcesses = await first.ObserveAsync("launcher", "child", "grandchild");
        var secondProcesses = await second.ObserveAsync("launcher", "child", "grandchild");
        await unrelated.ObserveAsync("unrelated");
        await a.DisposeAsync();
        await AssertExitedAsync(firstProcesses);
        Assert.True(secondProcesses.All(process => !process.HasExited));
        Assert.Equal(false, other.HasExited);
        await b.DisposeAsync();
        await AssertExitedAsync(secondProcesses);
        Assert.Equal(false, other.HasExited);
    }

    private static async Task ProbeLauncherExitAsync()
    {
        await using var tree = new TestTree();
        var running = new BoundedProcessRunner().RunAsync(OwnedProcessTestHost.StartInfo("exit", tree.Directory), TimeSpan.FromSeconds(15));
        var processes = await tree.ObserveAsync("launcher", "child", "grandchild");
        await File.WriteAllTextAsync(Path.Combine(tree.Directory, "exit"), "exit");
        var result = await running.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(false, result.TimedOut);
        Assert.Equal(false, result.OutputWasTruncated);
        Assert.Contains("tree ready", result.StandardOutput);
        Assert.Contains("tree error", result.StandardError);
        await AssertExitedAsync(processes);
    }

    private static async Task CancellationAsync()
    {
        await using var tree = new TestTree();
        using var cancellation = new CancellationTokenSource();
        var running = new BoundedProcessRunner().RunAsync(OwnedProcessTestHost.StartInfo("wait", tree.Directory),
            TimeSpan.FromSeconds(15), cancellation.Token);
        var processes = await tree.ObserveAsync("launcher", "child", "grandchild");
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        await AssertExitedAsync(processes);

        await using var transport = new TestTree();
        using var startupCancellation = new CancellationTokenSource();
        var startup = StartSessionAsync(transport, "wait", startupCancellation.Token);
        var startupProcesses = await transport.ObserveAsync("launcher", "child", "grandchild");
        startupCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => startup);
        await AssertExitedAsync(startupProcesses);
    }

    private static async Task TimeoutAsync()
    {
        await using var tree = new TestTree();
        var running = new BoundedProcessRunner().RunAsync(OwnedProcessTestHost.StartInfo("wait", tree.Directory), TimeSpan.FromSeconds(4));
        var processes = await tree.ObserveAsync("launcher", "child", "grandchild");
        var result = await running;
        Assert.True(result.TimedOut);
        await AssertExitedAsync(processes);
    }

    private static async Task AbruptOwnerExitAsync()
    {
        await using var tree = new TestTree();
        // This host is deliberately launched without our owner. Kill only the host,
        // so success proves kill-on-close rather than Process.Kill(entireProcessTree).
        using var host = Process.Start(OwnedProcessTestHost.StartInfo("owner", tree.Directory))!;
        try
        {
            var processes = await tree.ObserveAsync("launcher", "child", "grandchild");
            host.Kill();
            await host.WaitForExitAsync();
            await AssertExitedAsync(processes);
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
        }
    }

    private static async Task FailedTransportStartupAsync()
    {
        await using var tree = new TestTree();
        var startup = StartSessionAsync(tree, "fail");
        var processes = await tree.ObserveAsync("launcher", "child", "grandchild");
        await File.WriteAllTextAsync(Path.Combine(tree.Directory, "exit"), "exit");
        await Assert.ThrowsAsync<InvalidOperationException>(() => startup);
        await AssertExitedAsync(processes);
    }

    private static Task FailedCreationAsync()
    {
        using var process = Process.GetCurrentProcess();
        var before = process.HandleCount;
        for (var index = 0; index < 30; index++)
        {
            var missing = BoundedProcessRunner.CreateRedirectedStartInfo(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"), []);
            try
            {
                using var owner = RedirectedProcessOwner.Start(missing);
                throw new InvalidOperationException("Missing executable unexpectedly started.");
            }
            catch (Win32Exception) { }
        }
        process.Refresh();
        Assert.True(process.HandleCount <= before + 8, $"Failed starts leaked handles: {before} -> {process.HandleCount}.");
        return Task.CompletedTask;
    }

    private static async Task ArgumentsAsync()
    {
        await using var tree = new TestTree();
        var values = new[] { "", "plain", "two words", "embedded\"quote", @"trailing space\", "雪 café", @"\\server\share\" };
        var info = OwnedProcessTestHost.StartInfo("echo", tree.Directory);
        foreach (var value in values) info.ArgumentList.Add(value);
        info.Environment["SVS_OWNED_FIXTURE_VALUE"] = "unicode 雪 value";
        info.WorkingDirectory = tree.Directory;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.Unicode;
        var result = await new BoundedProcessRunner().RunAsync(info, TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.ExitCode);
        var echo = JsonSerializer.Deserialize<string[]>(result.StandardOutput)!;
        Assert.True(values.SequenceEqual(echo.Take(values.Length)));
        Assert.Equal("unicode 雪 value", echo[^2]);
        Assert.Equal(tree.Directory, echo[^1]);
        Assert.Equal("error 雪", result.StandardError.Trim('\uFEFF'));
    }

    private static Task<IStreamTransportSession> StartSessionAsync(TestTree tree, string mode, CancellationToken cancellation = default) =>
        new StreamlinkService(new MemoryLogger()).StartExternalHttpAsync(new StreamTransportRequest(
            StreamInputParser.FromChannel(PlatformKind.Twitch, "fixture"), "best", OwnedProcessTestHost.Executable,
            false, ["--owned-process-fixture", mode, tree.Directory]), cancellation);

    private static async Task AssertExitedAsync(IEnumerable<Process> processes)
    {
        foreach (var process in processes) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TestTree : IAsyncDisposable
    {
        private readonly List<Process> observed = [];
        internal string Directory { get; } = System.IO.Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"svs-owned-{Guid.NewGuid():N}")).FullName;

        internal async Task<Process[]> ObserveAsync(params string[] names)
        {
            var result = new List<Process>();
            foreach (var name in names)
            {
                var path = Path.Combine(Directory, name + ".pid");
                await TestWait.UntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(8));
                var process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(path), CultureInfo.InvariantCulture));
                _ = process.SafeHandle;
                observed.Add(process);
                result.Add(process);
            }
            return result.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var process in observed)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                finally { process.Dispose(); }
            }
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

internal static class OwnedProcessTestHost
{
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "StreamlinkVlcStudio.Tests.exe");

    internal static ProcessStartInfo StartInfo(string mode, string directory) =>
        BoundedProcessRunner.CreateRedirectedStartInfo(Executable, ["--owned-process-fixture", mode, directory]);

    internal static async Task<int> RunAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--owned-process-fixture");
        var mode = args[index + 1];
        var directory = args[index + 2];
        if (mode == "echo")
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Write(JsonSerializer.Serialize(args.Skip(index + 3)
                .Concat([Environment.GetEnvironmentVariable("SVS_OWNED_FIXTURE_VALUE")!, Environment.CurrentDirectory])));
            await Console.OpenStandardError().WriteAsync(Encoding.Unicode.GetBytes("error 雪"));
            return 0;
        }
        if (mode == "owner")
        {
            using var owner = RedirectedProcessOwner.Start(StartInfo("wait", directory));
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        var name = mode is "child" or "grandchild" or "unrelated" ? mode : "launcher";
        var pidPath = Path.Combine(directory, name + ".pid");
        await File.WriteAllTextAsync(pidPath + ".tmp", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        File.Move(pidPath + ".tmp", pidPath);
        if (name is "grandchild" or "unrelated")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        var childInfo = StartInfo(name == "launcher" ? "child" : "grandchild", directory);
        childInfo.RedirectStandardOutput = childInfo.RedirectStandardError = false;
        using var child = Process.Start(childInfo)!;
        await TestWait.UntilAsync(() => File.Exists(Path.Combine(directory, "grandchild.pid")), TimeSpan.FromSeconds(6));
        Console.WriteLine("tree ready");
        Console.Error.WriteLine("tree error");
        if (mode == "transport") Console.WriteLine("http://127.0.0.1:12345/");
        if (mode is "transport" or "exit" or "fail")
        {
            while (!File.Exists(Path.Combine(directory, "exit"))) await Task.Delay(10);
            return mode == "fail" ? 19 : 0;
        }
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}
