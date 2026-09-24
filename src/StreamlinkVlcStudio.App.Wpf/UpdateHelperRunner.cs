using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using StreamlinkVlcStudio.Core.Services;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class UpdateHelperRunner
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains("--update-helper", StringComparer.OrdinalIgnoreCase)) return false;
        exitCode = RunAsync(args).GetAwaiter().GetResult();
        return true;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var operationId = Guid.Parse(Required(args, "--update-helper"));
        var parentId = int.Parse(Required(args, "--parent-pid"), System.Globalization.CultureInfo.InvariantCulture);
        var setup = Path.GetFullPath(Required(args, "--setup"));
        var setupLength = long.Parse(Required(args, "--setup-length"), System.Globalization.CultureInfo.InvariantCulture);
        var setupSha256 = Required(args, "--setup-sha256");
        var targetVersion = Version.Parse(Required(args, "--target-version"));
        var installDirectory = Path.GetFullPath(Required(args, "--install-dir"));
        var resultPath = Path.GetFullPath(Required(args, "--result"));
        var logPath = Path.GetFullPath(Required(args, "--log"));
        ValidatePathsAndPackage(setup, setupLength, setupSha256, resultPath, logPath);
        AppUpdateCompletion completion;
        try
        {
            await WaitForParentAsync(parentId).ConfigureAwait(false);
            var info = new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setup)! };
            foreach (var argument in new[] { "/passive", "/norestart", "/log", logPath }) info.ArgumentList.Add(argument);
            using var installer = Process.Start(info) ?? throw new InvalidOperationException("Setup did not start.");
            await installer.WaitForExitAsync().ConfigureAwait(false);
            var code = installer.ExitCode;
            var outcome = MapExitCode(code);
            if (outcome is AppUpdateCompletionOutcome.Succeeded or AppUpdateCompletionOutcome.SucceededRebootRequired)
            {
                var installedTarget = Path.Combine(installDirectory, "StreamlinkVlcStudio.exe");
                var actual = ReadVersion(installedTarget);
                if (actual is null || Normalize(actual) != Normalize(targetVersion))
                {
                    outcome = AppUpdateCompletionOutcome.Failed;
                    code = -2;
                }
            }

            completion = new(operationId, outcome, code, logPath, Message(outcome, code), DateTimeOffset.UtcNow);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            completion = new(
                operationId,
                AppUpdateCompletionOutcome.Canceled,
                1223,
                logPath,
                "The update was canceled.",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            completion = new(operationId, AppUpdateCompletionOutcome.Failed, -1, logPath, $"Update failed. {ex.Message}", DateTimeOffset.UtcNow);
        }

        await WriteAtomicAsync(resultPath, completion).ConfigureAwait(false);
        var installed = Path.Combine(installDirectory, "StreamlinkVlcStudio.exe");
        if (File.Exists(installed))
        {
            Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true, WorkingDirectory = installDirectory })?.Dispose();
        }
        return completion.Outcome is AppUpdateCompletionOutcome.Succeeded or AppUpdateCompletionOutcome.SucceededRebootRequired ? 0 : 1;
    }

    internal static AppUpdateCompletionOutcome MapExitCode(int code) => code switch
    {
        0 => AppUpdateCompletionOutcome.Succeeded,
        3010 => AppUpdateCompletionOutcome.SucceededRebootRequired,
        1602 or 1223 => AppUpdateCompletionOutcome.Canceled,
        _ => AppUpdateCompletionOutcome.Failed
    };

    private static string Message(AppUpdateCompletionOutcome outcome, int code) => outcome switch
    {
        AppUpdateCompletionOutcome.Succeeded => "The update was installed successfully.",
        AppUpdateCompletionOutcome.SucceededRebootRequired => "The update was installed. Windows must restart to finish.",
        AppUpdateCompletionOutcome.Canceled => "The update was canceled.",
        _ => $"Setup failed with exit code {code}."
    };

    private static async Task WaitForParentAsync(int id)
    {
        try { using var process = Process.GetProcessById(id); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static Version? ReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        var value = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0];
        return Version.TryParse(value, out var version) ? version : null;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static void ValidatePathsAndPackage(
        string setup,
        long expectedLength,
        string expectedSha256,
        string resultPath,
        string logPath)
    {
        var helperDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (!string.Equals(Path.GetDirectoryName(setup), helperDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(setup), "StreamlinkVlcStudio-Setup.exe", StringComparison.Ordinal) ||
            expectedLength is <= 0 or > 1024L * 1024L * 1024L ||
            expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The staged update helper paths or package metadata are invalid.");
        }

        var updateRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamlinkVlcStudio",
            "Updates"));
        var rootPrefix = Path.TrimEndingDirectorySeparator(updateRoot) + Path.DirectorySeparatorChar;
        if (!resultPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !logPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update result and log must remain inside the update root.");
        }

        var info = new FileInfo(setup);
        if (!info.Exists || info.Length != expectedLength)
        {
            throw new InvalidDataException("The staged Setup package length changed before launch.");
        }
        using var stream = info.OpenRead();
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The staged Setup package hash changed before launch.");
        }
    }

    private static string Required(string[] args, string option)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing {option} value.");
        return args[index + 1];
    }

    private static async Task WriteAtomicAsync(string path, AppUpdateCompletion result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) await JsonSerializer.SerializeAsync(stream, result).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }
}
