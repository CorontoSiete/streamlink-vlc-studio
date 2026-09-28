using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

/// <summary>Opt-in benchmark counters, restricted to this test process and its descendants.</summary>
internal sealed class MultistreamProcessMetrics : IDisposable
{
    private readonly Dictionary<int, TrackedProcess> processes = [];
    private readonly IntPtr gpuQuery;
    private readonly IntPtr gpuCounter;
    private readonly IntPtr gpuDedicatedMemoryCounter;
    private readonly IntPtr gpuSharedMemoryCounter;
    private readonly int consoleHostPid;
    internal string? GpuError { get; private set; }

    internal MultistreamProcessMetrics()
    {
        var console = GetConsoleWindow();
        if (console != IntPtr.Zero) _ = GetWindowThreadProcessId(console, out consoleHostPid);
        Track(Environment.ProcessId, Path.GetFileNameWithoutExtension(Environment.ProcessPath!), 0, 0);
        var status = PdhOpenQueryW(null, UIntPtr.Zero, out gpuQuery);
        if (status == 0) status = PdhAddEnglishCounterW(gpuQuery, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out gpuCounter);
        if (status == 0) status = PdhAddEnglishCounterW(gpuQuery, @"\GPU Process Memory(*)\Dedicated Usage", UIntPtr.Zero, out gpuDedicatedMemoryCounter);
        if (status == 0) status = PdhAddEnglishCounterW(gpuQuery, @"\GPU Process Memory(*)\Shared Usage", UIntPtr.Zero, out gpuSharedMemoryCounter);
        if (status == 0) status = PdhCollectQueryData(gpuQuery);
        if (status != 0) GpuError = $"PDH initialization: 0x{status:x8}";
    }

    internal IReadOnlyList<ProcessCounterSample> Sample()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var parents = new Dictionary<int, (int Parent, string Name)>();
        if (Process32FirstW(snapshot, ref entry))
        {
            do { parents[entry.ProcessId] = (entry.ParentProcessId, entry.Executable); } while (Process32NextW(snapshot, ref entry));
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var (pid, item) in parents)
                if (!processes.ContainsKey(pid) && processes.TryGetValue(item.Parent, out var owner) && !HasExited(owner))
                    changed |= Track(pid, Path.GetFileNameWithoutExtension(item.Name), item.Parent, owner.Created);
        } while (changed);
        var samples = new List<ProcessCounterSample>();
        foreach (var tracked in processes.Values)
        {
            if (HasExited(tracked)) continue;
            if (!GetProcessTimes(tracked.Handle, out _, out _, out var kernel, out var user))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var memory = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
            if (!K32GetProcessMemoryInfo(tracked.Handle, ref memory, memory.Size))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!GetProcessIoCounters(tracked.Handle, out var io)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            samples.Add(new ProcessCounterSample(tracked.Pid, tracked.Name, (kernel + user) / 10_000_000d,
                (long)memory.PrivateBytes, (long)memory.WorkingSetBytes, io.ReadTransferCount, io.WriteTransferCount,
                tracked.Parent, DateTime.FromFileTimeUtc(tracked.Created)));
        }
        return samples;
    }

    // Retained handles keep final CPU counters readable after a short-lived
    // replay/probe process exits. Memory samples still contain only live children.
    internal double TotalCpuSeconds
    {
        get
        {
            long total = 0;
            foreach (var tracked in processes.Values)
            {
                if (!GetProcessTimes(tracked.Handle, out _, out _, out var kernel, out var user))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                total += kernel + user;
            }
            return total / 10_000_000d;
        }
    }

    private bool Track(int pid, string name, int parent, long parentCreated)
    {
        // Process.SafeHandle requests all access. Counters need only query access;
        // also reject old processes whose former parent's PID has since been reused.
        var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error == 87) return false; // The snapshot's process has exited.
            throw new Win32Exception(error, $"Cannot query process {pid} (reported parent {parent}).");
        }
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (created < parentCreated) return false;
            processes.Add(pid, new TrackedProcess(pid, name, handle, parent, created));
            handle = null!; // The retained query handle also prevents PID reuse.
            return true;
        }
        finally { handle?.Dispose(); }
    }

    private static bool HasExited(TrackedProcess process)
    {
        if (!GetExitCodeProcess(process.Handle, out var code)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return code != 259; // STILL_ACTIVE
    }

    internal IReadOnlyDictionary<string, double> SampleGpu(IEnumerable<int> processIds)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (GpuError is not null) return result;
        var status = PdhCollectQueryData(gpuQuery);
        uint bytes = 0, count = 0;
        const uint format = 0x200 | 0x8000; // DOUBLE | NOCAP100; sum only this tree's instances by engine type.
        if (status == 0) status = PdhGetFormattedCounterArrayW(gpuCounter, format, ref bytes, ref count, IntPtr.Zero);
        if (status != 0x800007D2) { GpuError = $"PDH size: 0x{status:x8}"; return result; }
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            status = PdhGetFormattedCounterArrayW(gpuCounter, format, ref bytes, ref count, buffer);
            if (status != 0) { GpuError = $"PDH data: 0x{status:x8}"; return result; }
            var ids = processIds.ToHashSet();
            var itemSize = Marshal.SizeOf<GpuCounterItem>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<GpuCounterItem>(buffer + index * itemSize);
                if (item.Status > 1 || !double.IsFinite(item.Value)) continue;
                var name = Marshal.PtrToStringUni(item.Name)!;
                var pieces = name.Split('_');
                if (pieces.Length < 3 || pieces[0] != "pid" || !int.TryParse(pieces[1], out var pid) || !ids.Contains(pid)) continue;
                var engine = name.IndexOf("engtype_", StringComparison.Ordinal);
                var key = engine >= 0 ? name[(engine + 8)..] : "unknown";
                result[key] = result.GetValueOrDefault(key) + item.Value;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    internal async Task AssertChildrenExitedAsync()
    {
        foreach (var process in processes.Values.Where(process => process.Pid != Environment.ProcessId))
        {
            // An attached console belongs to the benchmark host's lifetime and
            // exits with that host, after this assertion. Keep its counters in
            // the measurement; only exempt the OS-confirmed console from teardown.
            if (process.Pid == consoleHostPid && process.Parent == Environment.ProcessId && process.Name == "conhost") continue;
            await TestWait.UntilAsync(() => HasExited(process), TimeSpan.FromSeconds(8),
                $"Benchmark child {process.Name} ({process.Pid}) did not exit.");
        }
    }

    // Read the same PDH collection as SampleGpu so engine and memory observations
    // share an interval. These are per-process GPU allocations, not host RAM.
    internal GpuMemorySample SampleGpuMemory(IEnumerable<int> processIds)
    {
        var ids = processIds.ToHashSet();
        return new GpuMemorySample(Read(gpuDedicatedMemoryCounter), Read(gpuSharedMemoryCounter));

        long Read(IntPtr counter)
        {
            if (GpuError is not null) return 0;
            uint bytes = 0, count = 0;
            var status = PdhGetFormattedCounterArrayW(counter, 0x200 | 0x8000, ref bytes, ref count, IntPtr.Zero);
            if (status != 0x800007D2) { GpuError = $"PDH GPU memory size: 0x{status:x8}"; return 0; }
            var buffer = Marshal.AllocHGlobal(checked((int)bytes));
            try
            {
                status = PdhGetFormattedCounterArrayW(counter, 0x200 | 0x8000, ref bytes, ref count, buffer);
                if (status != 0) { GpuError = $"PDH GPU memory data: 0x{status:x8}"; return 0; }
                long total = 0;
                var itemSize = Marshal.SizeOf<GpuCounterItem>();
                for (var index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<GpuCounterItem>(buffer + index * itemSize);
                    if (item.Status > 1 || !double.IsFinite(item.Value) || item.Value < 0) continue;
                    var name = Marshal.PtrToStringUni(item.Name)!.AsSpan();
                    if (!name.StartsWith("pid_", StringComparison.Ordinal)) continue;
                    var end = name[4..].IndexOf('_');
                    if (end >= 0 && int.TryParse(name.Slice(4, end), out var pid) && ids.Contains(pid))
                        total += checked((long)item.Value);
                }
                return total;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    internal sealed record GpuMemorySample(long DedicatedBytes, long SharedBytes);

    public void Dispose()
    {
        if (gpuQuery != IntPtr.Zero) _ = PdhCloseQuery(gpuQuery);
        foreach (var process in processes.Values) process.Handle.Dispose();
    }

    private sealed record TrackedProcess(int Pid, string Name, SafeProcessHandle Handle, int Parent, long Created);

    internal sealed record ProcessCounterSample(int Pid, string Name, double CpuSeconds, long PrivateBytes, long WorkingSetBytes,
        ulong ReadBytes, ulong WriteBytes, int ParentPid, DateTime CreatedUtc);

    [DllImport("kernel32")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage;
        public int ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads;
        public int ParentProcessId, Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetBytes, WorkingSetBytes, QuotaPeakPagedBytes, QuotaPagedBytes,
            QuotaPeakNonPagedBytes, QuotaNonPagedBytes, PageFileBytes, PeakPageFileBytes, PrivateBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuCounterItem
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, UIntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
