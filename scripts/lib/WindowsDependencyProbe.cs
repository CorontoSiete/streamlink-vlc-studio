// This source is compiled by the application, Setup, and Windows PowerShell 5.1.
// Keep it compatible with the .NET Framework C# compiler used by Add-Type.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
#if NET5_0_OR_GREATER
using System.Runtime.Versioning;
using StreamlinkVlcStudio.Infrastructure.Processes;
#endif

namespace StreamStudio.Installation
{
#if NET5_0_OR_GREATER
    [SupportedOSPlatform("windows")]
#endif
    public static class WindowsDependencyProbe
    {
        private const string WebView2Key = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        private static readonly string[] RequiredVlcPlugins = {
            @"access\libfilesystem_plugin.dll", @"access\libhttp_plugin.dll", @"access\libhttps_plugin.dll",
            @"codec\libavcodec_plugin.dll", @"demux\libadaptive_plugin.dll", @"demux\libmp4_plugin.dll",
            @"demux\libts_plugin.dll", @"audio_output\libdirectsound_plugin.dll",
            @"misc\libgnutls_plugin.dll", @"video_chroma\libswscale_plugin.dll",
            @"video_filter\libadjust_plugin.dll", @"video_output\libdirect3d11_plugin.dll",
            @"video_output\libwingdi_plugin.dll"
        };

        public static string NormalizeVersion(string value)
        {
            var match = Regex.Match(value ?? string.Empty, @"^v?(\d+(?:\.\d+){1,3})(?:-\d+)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) return string.Empty;
            try
            {
                var version = Version.Parse(match.Groups[1].Value);
                return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)).ToString();
            }
            catch (ArgumentException) { return string.Empty; }
            catch (FormatException) { return string.Empty; }
            catch (OverflowException) { return string.Empty; }
        }

        public static bool IsX64PortableExecutable(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
                    stream.Position = 0x3c;
                    var offset = reader.ReadInt32();
                    if (offset < 64 || offset > stream.Length - 26) return false;
                    stream.Position = offset;
                    if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664) return false;
                    stream.Position = offset + 24;
                    return reader.ReadUInt16() == 0x20b;
                }
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) return false;
                throw;
            }
        }

        public static string ReadStreamlinkVersion(string executable, int timeoutMilliseconds)
        {
            if (!IsX64PortableExecutable(executable)) return string.Empty;
            var deadline = Stopwatch.StartNew();
            var result = RunCommand(executable, "--no-config --version", timeoutMilliseconds);
            if (result.ExitCode != 0 || result.Truncated) return string.Empty;
            var match = Regex.Match(result.Output.Trim(), @"^streamlink\s+(v?\d+(?:\.\d+){1,3}(?:-\d+)?)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) return string.Empty;
            var version = NormalizeVersion(match.Groups[1].Value);
            if (version.Length == 0) return string.Empty;
            // --version exits before importing provider plugins. Resolve both
            // providers offline to prove their Python modules and imports work.
            // Ignore personal configuration and never follow HTTP redirects.
            foreach (var url in new[] {
                "https://www.twitch.tv/streamstudio_dependency_check",
                "https://kick.com/streamstudio_dependency_check" })
            {
                var remaining = timeoutMilliseconds - (int)Math.Min(int.MaxValue, deadline.ElapsedMilliseconds);
                if (remaining <= 0) throw new TimeoutException("The Streamlink dependency check timed out.");
                var plugin = RunCommand(executable, "--no-config --can-handle-url-no-redirect " + url, remaining);
                if (plugin.ExitCode != 0 || plugin.Truncated) return string.Empty;
            }
            return version;
        }

        public static CommandResult VerifyApplication(string executable, string streamlink, string vlc)
        {
            return RunCommand(executable, "--maintenance-verify-dependencies --streamlink-path " + QuoteArgument(streamlink) +
                " --vlc-directory " + QuoteArgument(vlc), 60000);
        }

        private static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        public sealed class CommandResult
        {
            public int ExitCode;
            public string Output = string.Empty;
            public string Error = string.Empty;
            public bool Truncated;
        }

        private static CommandResult RunCommand(string executable, string arguments, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
#if NET5_0_OR_GREATER
            var start = BoundedProcessRunner.CreateRedirectedStartInfo(executable, new string[0]);
            start.Arguments = arguments;
            start.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? string.Empty;
            var captured = new BoundedProcessRunner().RunAsync(start, TimeSpan.FromMilliseconds(timeoutMilliseconds)).GetAwaiter().GetResult();
            if (captured.TimedOut) throw new TimeoutException("The dependency command timed out.");
            return new CommandResult
            {
                ExitCode = captured.ExitCode,
                Output = captured.StandardOutput,
                Error = captured.StandardError,
                Truncated = captured.OutputWasTruncated
            };
#else
            var info = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? string.Empty
            };
            using (var command = FrameworkCommand.Start(info))
            {
                var process = command.Process;
                try
                {
                    var output = ReadBoundedOutputAsync(command.Output);
                    var error = ReadBoundedOutputAsync(command.Error);
                    try
                    {
                        if (!process.WaitForExit(timeoutMilliseconds)) throw new TimeoutException("The dependency command timed out.");
                        // End descendants before draining: inherited pipes must not keep setup waiting.
                        command.Terminate();
                        if (!Task.WaitAll(new Task[] { output, error }, 2000))
                            throw new TimeoutException("The dependency command did not close its output pipes.");
                        return new CommandResult
                        {
                            ExitCode = process.ExitCode,
                            Output = output.Result.Text,
                            Error = error.Result.Text,
                            Truncated = output.Result.Truncated || error.Result.Truncated
                        };
                    }
                    finally
                    {
                        command.Terminate();
                        command.Output.Dispose();
                        command.Error.Dispose();
                        Task.WhenAll(output, error).ContinueWith(completed =>
                        {
                            var failure = completed.Exception;
                            if (failure != null) failure.Handle(ignored => true);
                        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
                finally
                {
                    command.Terminate();
                    if (!process.HasExited)
                    {
                        try { process.Kill(); process.WaitForExit(1000); }
                        catch (InvalidOperationException) { }
                        catch (Win32Exception) { }
                    }
                }
            }
#endif
        }

        public static string ReadVlcVersion(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return string.Empty;
            var library = Path.Combine(directory, "libvlc.dll");
            if (!IsX64PortableExecutable(library) ||
                !IsX64PortableExecutable(Path.Combine(directory, "libvlccore.dll")) ||
                !Directory.Exists(Path.Combine(directory, "plugins"))) return string.Empty;
            foreach (var plugin in RequiredVlcPlugins)
            {
                if (!IsX64PortableExecutable(Path.Combine(directory, "plugins", plugin))) return string.Empty;
            }
            var version = NormalizeVersion(FileVersionInfo.GetVersionInfo(library).FileVersion ?? string.Empty);
            return version.Length > 0 && CanLoadVlc(directory, version) ? version : string.Empty;
        }

        private static bool CanLoadVlc(string directory, string expectedVersion)
        {
            // Resolve dependent DLLs beside VLC and in the standard Windows locations.
            const uint search = 0x00000100 | 0x00001000;
            var core = LoadLibraryEx(Path.GetFullPath(Path.Combine(directory, "libvlccore.dll")), IntPtr.Zero, search);
            if (core == IntPtr.Zero) return false;
            try
            {
                var library = LoadLibraryEx(Path.GetFullPath(Path.Combine(directory, "libvlc.dll")), IntPtr.Zero, search);
                if (library == IntPtr.Zero) return false;
                try
                {
                    var address = GetProcAddress(library, "libvlc_get_version");
                    if (address == IntPtr.Zero || GetProcAddress(library, "libvlc_new") == IntPtr.Zero ||
                        GetProcAddress(library, "libvlc_release") == IntPtr.Zero) return false;
                    var getVersion = (GetVlcVersion)Marshal.GetDelegateForFunctionPointer(address, typeof(GetVlcVersion));
                    var reported = Marshal.PtrToStringAnsi(getVersion()) ?? string.Empty;
                    var match = Regex.Match(reported, @"^(\d+(?:\.\d+){1,3})(?:\s|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (!match.Success || NormalizeVersion(match.Groups[1].Value) != expectedVersion) return false;
                    foreach (var plugin in RequiredVlcPlugins)
                    {
                        var handle = LoadLibraryEx(Path.GetFullPath(Path.Combine(directory, "plugins", plugin)), IntPtr.Zero, search);
                        if (handle == IntPtr.Zero) return false;
                        try
                        {
                            // The application and its embedded plugins use VLC 3's
                            // reviewed module ABI. A loadable unrelated DLL is not a plugin.
                            if (GetProcAddress(handle, "vlc_entry__3_0_0f") == IntPtr.Zero) return false;
                        }
                        finally { FreeLibrary(handle); }
                    }
                    return true;
                }
                finally { FreeLibrary(library); }
            }
            finally { FreeLibrary(core); }
        }

        public static string GetProgramFiles64Directory()
        {
            var directory = Environment.GetEnvironmentVariable("ProgramW6432");
            return string.IsNullOrWhiteSpace(directory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) : directory;
        }

        public static string FindMachineVlcDirectory()
        {
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = root.OpenSubKey(@"SOFTWARE\VideoLAN\VLC"))
            {
                var directory = key == null ? string.Empty : Convert.ToString(key.GetValue("InstallDir")) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(directory) && Path.IsPathRooted(directory)) return directory;
            }
            return Path.Combine(GetProgramFiles64Directory(), "VideoLAN", "VLC");
        }

        public static string ReadWebView2Version(bool machineOnly)
        {
            var machine = ReadWebView2Registration(RegistryHive.LocalMachine, RegistryView.Registry32,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            if (machineOnly) return machine;
            var user = ReadWebView2Registration(RegistryHive.CurrentUser, RegistryView.Default,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (machine.Length == 0) return user;
            if (user.Length == 0) return machine;
            return Version.Parse(user) > Version.Parse(machine) ? user : machine;
        }

        private static string ReadWebView2Registration(RegistryHive hive, RegistryView view, string defaultRoot)
        {
            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var key = root.OpenSubKey(WebView2Key))
            {
                if (key == null) return string.Empty;
                var registered = NormalizeVersion(Convert.ToString(key.GetValue("pv")) ?? string.Empty);
                if (registered.Length == 0 || registered == "0.0.0.0") return string.Empty;
                var location = Convert.ToString(key.GetValue("location")) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(location)) location = Path.Combine(defaultRoot, "Microsoft", "EdgeWebView", "Application");
                return ReadWebView2Installation(location, registered);
            }
        }

        // Public so detection of stale registration and wrong architecture is testable without changing the registry.
        public static string ReadWebView2Installation(string location, string registeredVersion)
        {
            if (string.IsNullOrWhiteSpace(location) || !Path.IsPathRooted(location)) return string.Empty;
            var registered = NormalizeVersion(registeredVersion);
            if (registered.Length == 0 || registered == "0.0.0.0") return string.Empty;
            var executable = Path.Combine(location, registered, "msedgewebview2.exe");
            if (!IsX64PortableExecutable(executable)) return string.Empty;
            var core = Path.Combine(location, registered, "msedge.dll");
            if (!IsX64PortableExecutable(core) ||
                NormalizeVersion(FileVersionInfo.GetVersionInfo(core).FileVersion ?? string.Empty) != registered) return string.Empty;
            var actual = NormalizeVersion(FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty);
            return actual == registered ? actual : string.Empty;
        }

#if !NET5_0_OR_GREATER
        private static async Task<ProbeOutput> ReadBoundedOutputAsync(StreamReader reader)
        {
            const int limit = 16 * 1024;
            var text = new StringBuilder();
            var buffer = new char[2048];
            var truncated = false;
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                var kept = Math.Min(count, limit - text.Length);
                text.Append(buffer, 0, kept);
                truncated |= kept != count;
            }
            return new ProbeOutput { Text = text.ToString(), Truncated = truncated };
        }

        private sealed class ProbeOutput
        {
            public string Text = string.Empty;
            public bool Truncated;
        }
#endif

        private sealed class ProcessJob : SafeHandleZeroOrMinusOneIsInvalid
        {
            private ProcessJob() : base(true) { }

            public static ProcessJob Create()
            {
                var job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
                if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new ExtendedJobLimits { Basic = new BasicJobLimits { LimitFlags = 0x2000 } };
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedJobLimits))))
                {
                    var error = Marshal.GetLastWin32Error();
                    job.Dispose();
                    throw new Win32Exception(error);
                }
                return job;
            }

            protected override bool ReleaseHandle() { return CloseHandle(handle); }
        }

#if !NET5_0_OR_GREATER
        // Windows PowerShell 5.1 cannot load the app's .NET 10 process runner.
        // Keep its equivalent native startup compatible with the Framework compiler.
        // The job and inherited pipes are assigned atomically by CreateProcess;
        // a fast launcher cannot escape ownership before a later job assignment.
        private sealed class FrameworkCommand : IDisposable
        {
            public Process Process;
            public StreamReader Output, Error;
            private ProcessJob job;

            public void Terminate() { job.Dispose(); }
            public void Dispose()
            {
                Terminate();
                Output.Dispose(); Error.Dispose(); Process.Dispose();
            }

            public static FrameworkCommand Start(ProcessStartInfo info)
            {
                var job = ProcessJob.Create();
                Process process = null;
                StreamReader output = null, error = null;
                SafeFileHandle outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, input = null;
                var attributes = IntPtr.Zero;
                var values = IntPtr.Zero;
                var initialized = false;
                try
                {
                    var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = true };
                    CreateOutputPipe(ref security, out outputRead, out outputWrite);
                    CreateOutputPipe(ref security, out errorRead, out errorWrite);
                    input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
                    if (input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                    attributes = Marshal.AllocHGlobal(size);
                    if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    initialized = true;
                    values = Marshal.AllocHGlobal(4 * IntPtr.Size);
                    Marshal.WriteIntPtr(values, 0, job.DangerousGetHandle());
                    Marshal.WriteIntPtr(values, IntPtr.Size, input.DangerousGetHandle());
                    Marshal.WriteIntPtr(values, 2 * IntPtr.Size, outputWrite.DangerousGetHandle());
                    Marshal.WriteIntPtr(values, 3 * IntPtr.Size, errorWrite.DangerousGetHandle());
                    if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x2000D), values, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero) ||
                        !UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), IntPtr.Add(values, IntPtr.Size), new IntPtr(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var startup = new StartupInfoEx
                    {
                        StartupInfo = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfoEx)), Flags = 0x100,
                            StandardInput = input.DangerousGetHandle(), StandardOutput = outputWrite.DangerousGetHandle(), StandardError = errorWrite.DangerousGetHandle() },
                        AttributeList = attributes
                    };
                    ProcessInformation created;
                    var command = new StringBuilder(QuoteArgument(info.FileName)).Append(' ').Append(info.Arguments);
                    // The job already owns this suspended process. Retain its managed
                    // handle and readers before allowing the launcher to execute.
                    if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080004,
                        IntPtr.Zero, info.WorkingDirectory, ref startup, out created)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    using (var processHandle = new SafeFileHandle(created.Process, true))
                    using (var threadHandle = new SafeFileHandle(created.Thread, true))
                    {
                        process = Process.GetProcessById(created.ProcessId);
                        var retained = process.Handle;
                        output = new StreamReader(new FileStream(outputRead, FileAccess.Read), Encoding.UTF8, true);
                        outputRead = null;
                        error = new StreamReader(new FileStream(errorRead, FileAccess.Read), Encoding.UTF8, true);
                        errorRead = null;
                        if (ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    var owner = new FrameworkCommand { Process = process, Output = output, Error = error, job = job };
                    process = null; output = error = null; job = null;
                    return owner;
                }
                finally
                {
                    if (job != null) job.Dispose();
                    if (process != null) process.Dispose();
                    if (output != null) output.Dispose();
                    if (error != null) error.Dispose();
                    if (outputRead != null) outputRead.Dispose();
                    if (outputWrite != null) outputWrite.Dispose();
                    if (errorRead != null) errorRead.Dispose();
                    if (errorWrite != null) errorWrite.Dispose();
                    if (input != null) input.Dispose();
                    if (initialized) DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(values);
                }
            }

            private static void CreateOutputPipe(ref SecurityAttributes security, out SafeFileHandle read, out SafeFileHandle write)
            {
                if (!CreatePipe(out read, out write, ref security, 0) || !SetHandleInformation(read, 1, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
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
        private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(SafeFileHandle thread);
#endif

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetVlcVersion();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicJobLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedJobLimits
        {
            public BasicJobLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ProcessJob CreateJobObject(IntPtr security, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(ProcessJob job, int infoClass, ref ExtendedJobLimits limits, uint length);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
