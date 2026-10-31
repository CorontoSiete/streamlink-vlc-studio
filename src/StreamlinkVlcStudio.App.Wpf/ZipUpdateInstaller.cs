using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Infrastructure.Processes;

namespace StreamlinkVlcStudio.App.Wpf;

/// <summary>Installs the authenticated ZIP with its own atomic, ownership-aware installer.</summary>
internal static partial class ZipUpdateInstaller
{
    private const int MaximumEntries = 10_000;
    private const long MaximumEntryBytes = 1024L * 1024 * 1024;
    private const long MaximumTotalBytes = 2 * MaximumEntryBytes;
    private static readonly TimeSpan InstallationTimeout = TimeSpan.FromMinutes(30);

    // Dot-source the authenticated installer so even existing releases report a
    // dependency's reboot request. Paths are separate -File arguments, never code.
    private const string InstallerWrapper = """
        param(
            [Parameter(Mandatory = $true)][string]$InstallerPath,
            [Parameter(Mandatory = $true)][string]$Destination
        )
        $ErrorActionPreference = 'Stop'
        try {
            . $InstallerPath -AppSource Local -InstallDir $Destination
            if ($script:RebootRequired) { exit 3010 }
            exit 0
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            $failure = $_.Exception
            while ($null -ne $failure) {
                if ($failure -is [ComponentModel.Win32Exception] -and $failure.NativeErrorCode -eq 1223) {
                    exit 1223
                }
                $failure = $failure.InnerException
            }
            exit 1
        }
        """;

    internal static async Task<int> InstallAsync(
        Stream verifiedArchive,
        string operationDirectory,
        string installDirectory,
        Version targetVersion,
        string logPath,
        Func<ProcessStartInfo, TimeSpan, Task<ProcessExecutionResult>>? run = null)
    {
        AssertNoReparsePoints(operationDirectory);
        AssertNoReparsePoints(installDirectory);
        var payloadDirectory = Path.Combine(Path.GetFullPath(operationDirectory), "payload-" + Guid.NewGuid().ToString("N"));
        var wrapperPath = Path.Combine(Path.GetFullPath(operationDirectory), "install-" + Guid.NewGuid().ToString("N") + ".ps1");
        var locks = new List<FileStream>();
        try
        {
            var payload = ExtractPayload(verifiedArchive, payloadDirectory);
            // Hold the extracted authenticated bytes against modification until
            // the installer has finished copying and verifying its local payload.
            foreach (var path in Directory.EnumerateFiles(payloadDirectory, "*", SearchOption.AllDirectories))
                locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            var actualVersion = UpdateHelperRunner.ReadVersion(Path.Combine(payload, AppIdentity.ZipExecutableName));
            if (actualVersion is null || new Version(actualVersion.Major, actualVersion.Minor, Math.Max(0, actualVersion.Build)) !=
                new Version(targetVersion.Major, targetVersion.Minor, Math.Max(0, targetVersion.Build)))
                throw new InvalidDataException("The ZIP application version does not match the signed update.");

            using (var wrapper = new FileStream(wrapperPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(InstallerWrapper);
                wrapper.Write(bytes);
                wrapper.Flush(flushToDisk: true);
            }
            locks.Add(new FileStream(wrapperPath, FileMode.Open, FileAccess.Read, FileShare.Read));
            var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = BoundedProcessRunner.CreateRedirectedStartInfo(powerShell,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapperPath,
                    "-InstallerPath", Path.Combine(payload, "install.ps1"), "-Destination", Path.GetFullPath(installDirectory)]);
            info.WorkingDirectory = payload;
            var result = run is null
                ? await new BoundedProcessRunner().RunAsync(info, InstallationTimeout).ConfigureAwait(false)
                : await run(info, InstallationTimeout).ConfigureAwait(false);
            await File.AppendAllTextAsync(logPath, result.StandardOutput + Environment.NewLine + result.StandardError).ConfigureAwait(false);
            if (result.TimedOut) throw new TimeoutException("The ZIP update installer did not finish within 30 minutes.");
            if (result.OutputWasTruncated) throw new InvalidDataException("The ZIP update installer produced an incomplete diagnostic log.");
            return result.ExitCode;
        }
        finally
        {
            foreach (var handle in locks) handle.Dispose();
            TryDeletePayload(payloadDirectory);
            try { File.Delete(wrapperPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string ExtractPayload(Stream archiveStream, string destination)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (Directory.Exists(root) || File.Exists(root))
            throw new InvalidDataException("The ZIP extraction destination must be new.");
        AssertNoReparsePoints(root);
        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is <= 0 or > MaximumEntries)
            throw new InvalidDataException("The ZIP update has an invalid number of entries.");
        var prefix = root + Path.DirectorySeparatorChar;
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Target, bool Directory)>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var relative = directory ? name[..^1] : name;
            if (Path.IsPathRooted(name) || relative.Split('/').Any(segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.TrimEnd(' ', '.') != segment ||
                ReservedDeviceName().IsMatch(segment.Split('.')[0].TrimEnd(' '))))
                throw new InvalidDataException("The ZIP update contains an unsafe Windows path.");
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !paths.TryAdd(target, directory))
                throw new InvalidDataException("The ZIP update contains a duplicate or escaping path.");
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The ZIP update contains a symbolic link or reparse point.");
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes || entry.Length > MaximumTotalBytes - total ||
                (directory && entry.Length != 0))
                throw new InvalidDataException("The ZIP update exceeds its extraction limits.");
            total += entry.Length;
            entries.Add((entry, target, directory));
        }
        foreach (var (_, target, _) in entries)
        {
            for (var parent = Path.GetDirectoryName(target); parent is not null && parent != root; parent = Path.GetDirectoryName(parent))
                if (paths.TryGetValue(parent, out var isDirectory) && !isDirectory)
                    throw new InvalidDataException("The ZIP update uses a file as a parent directory.");
        }

        try
        {
            Directory.CreateDirectory(root);
            var buffer = new byte[128 * 1024];
            foreach (var (entry, target, directory) in entries)
            {
                if (directory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                AssertNoReparsePoints(target);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long written = 0;
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    written += read;
                    if (written > entry.Length) throw new InvalidDataException("A ZIP entry exceeds its declared length.");
                    output.Write(buffer, 0, read);
                }
                if (written != entry.Length) throw new InvalidDataException("A ZIP entry is incomplete.");
            }
            var candidates = new[] { root }.Concat(Directory.EnumerateDirectories(root))
                .Where(path => File.Exists(Path.Combine(path, AppIdentity.ZipExecutableName)) &&
                    File.Exists(Path.Combine(path, "install.ps1"))).ToArray();
            return candidates is [var payload] ? payload :
                throw new InvalidDataException("The ZIP update must contain exactly one complete application payload.");
        }
        catch
        {
            TryDeletePayload(root);
            throw;
        }
    }

    internal static void AssertNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP update paths cannot contain symbolic links or junctions.");
    }

    private static void TryDeletePayload(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            AssertTreeNoReparsePoints(path);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static void AssertTreeNoReparsePoints(string path)
    {
        AssertNoReparsePoints(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP update cleanup cannot follow a symbolic link or junction.");
            if (Directory.Exists(child)) AssertTreeNoReparsePoints(child);
        }
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9\u00B9\u00B2\u00B3]|LPT[1-9\u00B9\u00B2\u00B3])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceName();
}
