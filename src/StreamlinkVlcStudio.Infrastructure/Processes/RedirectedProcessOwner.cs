using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StreamlinkVlcStudio.Infrastructure.Processes;

/// <summary>Owns redirected pipes and the lifetime of a command's descendants, including after its launcher exits.</summary>
internal sealed class RedirectedProcessOwner : IDisposable
{
    private SafeFileHandle? job;

    private RedirectedProcessOwner(Process process, StreamReader output, StreamReader error, SafeFileHandle? job)
    {
        Process = process;
        StandardOutput = output;
        StandardError = error;
        this.job = job;
    }

    internal Process Process { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    internal static RedirectedProcessOwner Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute || !startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError ||
            startInfo.RedirectStandardInput)
            throw new ArgumentException("The process must redirect stdout and stderr without shell execution or redirected stdin.", nameof(startInfo));

        if (OperatingSystem.IsWindows()) return StartWindows(startInfo);

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Process '{startInfo.FileName}' could not be started.");
            return new RedirectedProcessOwner(process, process.StandardOutput, process.StandardError, null);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    // This handle is deliberately non-inheritable. Closing it kills the whole job,
    // even when the launcher has already exited or the owner itself is terminated.
    internal void Terminate()
    {
        if (OperatingSystem.IsWindows())
        {
            Interlocked.Exchange(ref job, null)?.Dispose();
        }
        else
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
        }
    }

    internal async Task StopAsync(TimeSpan timeout)
    {
        Terminate();
        try
        {
            using var budget = new CancellationTokenSource(timeout);
            await Process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception) { }
    }

    public void Dispose()
    {
        Terminate();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }

    private static unsafe RedirectedProcessOwner StartWindows(ProcessStartInfo startInfo)
    {
        if (!string.IsNullOrEmpty(startInfo.UserName))
            throw new NotSupportedException("Owned commands run with the current user's credentials.");
        if (startInfo.ArgumentList.Count > 0 && !string.IsNullOrEmpty(startInfo.Arguments))
            throw new ArgumentException("Specify either ArgumentList or Arguments, not both.", nameof(startInfo));

        SafeFileHandle? job = null;
        Process? process = null;
        StreamReader? output = null;
        StreamReader? error = null;
        var attributes = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var attributesInitialized = false;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var limits = new JobLimits { Basic = new BasicJobLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            using var outputRead = CreateOutputPipe(ref security, out var outputWriteHandle);
            using var outputWrite = outputWriteHandle;
            using var errorRead = CreateOutputPipe(ref security, out var errorWriteHandle);
            using var errorWrite = errorWriteHandle;
            using var input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
            if (input.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());

            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            attributesInitialized = true;

            var jobHandle = job.DangerousGetHandle();
            // Assignment is part of CreateProcess, before any child thread can run.
            // A suspended-then-AssignProcessToJobObject sequence would leave an orphan window.
            if (!UpdateProcThreadAttribute(attributes, 0, 0x2000D, &jobHandle, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            IntPtr* handles = stackalloc IntPtr[] { input.DangerousGetHandle(), outputWrite.DangerousGetHandle(), errorWrite.DangerousGetHandle() };
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100, // STARTF_USESTDHANDLES
                    StandardInput = handles[0],
                    StandardOutput = handles[1],
                    StandardError = handles[2]
                },
                AttributeList = attributes
            };
            var command = new StringBuilder(QuoteArgument(startInfo.FileName));
            if (startInfo.ArgumentList.Count > 0)
            {
                foreach (var argument in startInfo.ArgumentList) command.Append(' ').Append(QuoteArgument(argument));
            }
            else if (!string.IsNullOrEmpty(startInfo.Arguments)) command.Append(' ').Append(startInfo.Arguments);
            var environmentBlock = string.Join('\0', startInfo.Environment
                .Where(pair => pair.Value is not null)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
            environment = Marshal.StringToHGlobalUni(environmentBlock);
            // Suspension only lets us retain a managed Process handle before a very short
            // launcher exits. The job already owns the suspended process atomically.
            const uint flags = 0x00080000 | 0x00000400 | 0x00000004; // EXTENDED_STARTUPINFO, UNICODE_ENVIRONMENT, SUSPENDED
            if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true,
                    flags | (startInfo.CreateNoWindow ? 0x08000000u : 0), environment,
                    string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                    ref startup, out var created))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            using var processHandle = new SafeProcessHandle(created.Process, ownsHandle: true);
            using var threadHandle = new SafeFileHandle(created.Thread, ownsHandle: true);
            process = Process.GetProcessById(created.ProcessId);
            _ = process.SafeHandle;
            output = CreateReader(outputRead, startInfo.StandardOutputEncoding);
            error = CreateReader(errorRead, startInfo.StandardErrorEncoding);
            if (ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var owner = new RedirectedProcessOwner(process, output, error, job);
            process = null;
            output = error = null;
            job = null;
            return owner;
        }
        finally
        {
            // On any failure, close the job before releasing the process or pipes.
            job?.Dispose();
            process?.Dispose();
            output?.Dispose();
            error?.Dispose();
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(environment);
        }
    }

    private static SafeFileHandle CreateOutputPipe(ref SecurityAttributes security, out SafeFileHandle write)
    {
        if (!CreatePipe(out var read, out write, ref security, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error);
        }
        if (SetHandleInformation(read, 1, 0)) return read;
        var lastError = Marshal.GetLastPInvokeError();
        read.Dispose();
        write.Dispose();
        throw new Win32Exception(lastError);
    }

    private static StreamReader CreateReader(SafeFileHandle handle, Encoding? encoding)
    {
        // Duplicate so the temporary creation handles always have simple, scoped ownership.
        var current = GetCurrentProcess();
        if (!DuplicateHandle(current, handle, current, out var copy, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var codePage = GetConsoleOutputCP();
            encoding ??= CodePagesEncodingProvider.Instance.GetEncoding((int)(codePage == 0 ? GetACP() : codePage)) ?? Encoding.UTF8;
            return new StreamReader(new FileStream(copy, FileAccess.Read), encoding, detectEncodingFromByteOrderMarks: true);
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    // Windows CommandLineToArgv/CRT quoting: escape quotes and double trailing backslashes.
    private static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicJobLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, void* value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
