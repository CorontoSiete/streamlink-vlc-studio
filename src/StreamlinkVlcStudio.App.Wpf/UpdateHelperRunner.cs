using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using StreamlinkVlcStudio.Core;
using StreamlinkVlcStudio.Core.Services;
using StreamlinkVlcStudio.Infrastructure.Io;

namespace StreamlinkVlcStudio.App.Wpf;

internal static class UpdateHelperRunner
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains("--update-helper", StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            exitCode = RunAsync(args).GetAwaiter().GetResult();
        }
        catch
        {
            // Invalid helper arguments must not fall through into normal application startup.
            exitCode = 1;
        }
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
        ValidateResultPaths(operationId, resultPath, logPath);
        AppUpdateCompletion completion;
        try
        {
            await WaitForParentAsync(parentId).ConfigureAwait(false);
            ValidatePathsAndPackage(setup, setupLength, setupSha256, resultPath, logPath);
            // Keep the verified bytes locked against replacement until Setup has exited.
            using var packageLock = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(packageLock)), setupSha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("The staged Setup package changed before launch.");
            var info = new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setup)! };
            foreach (var argument in new[] { "/passive", "/norestart", "/log", logPath }) info.ArgumentList.Add(argument);
            using var installer = Process.Start(info) ?? throw new InvalidOperationException("Setup did not start.");
            await installer.WaitForExitAsync().ConfigureAwait(false);
            var code = installer.ExitCode;
            var outcome = MapExitCode(code);
            if (outcome == AppUpdateCompletionOutcome.Succeeded)
            {
                var installedTarget = Path.Combine(installDirectory, AppIdentity.ManagedExecutableName);
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
        var installed = Path.Combine(installDirectory, AppIdentity.ManagedExecutableName);
        if (File.Exists(installed))
        {
            try
            {
                Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true, WorkingDirectory = installDirectory })?.Dispose();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Preserve the install result for the next manual launch even if relaunch fails.
            }
        }
        return completion.Outcome is AppUpdateCompletionOutcome.Succeeded or AppUpdateCompletionOutcome.SucceededRebootRequired ? 0 : 1;
    }

    internal static AppUpdateCompletionOutcome MapExitCode(int code)
    {
        if ((code & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)) code &= 0xFFFF;
        return code switch
        {
            0 => AppUpdateCompletionOutcome.Succeeded,
            3010 or 1641 => AppUpdateCompletionOutcome.SucceededRebootRequired,
            1602 or 1223 => AppUpdateCompletionOutcome.Canceled,
            _ => AppUpdateCompletionOutcome.Failed
        };
    }

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

    private static void ValidateResultPaths(Guid operationId, string resultPath, string logPath)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.UpdateDirectoryName, "Updates");
        if (operationId == Guid.Empty ||
            !string.Equals(resultPath, Path.Combine(root, "results", operationId.ToString("N") + ".json"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(logPath, Path.Combine(root, "logs", operationId.ToString("N") + ".log"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update result and log do not match the operation.");
        foreach (var path in new[] { resultPath, logPath })
        {
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Update result paths cannot contain symbolic links or junctions.");
            }
        }
    }

    private static void ValidatePathsAndPackage(
        string setup,
        long expectedLength,
        string expectedSha256,
        string resultPath,
        string logPath)
    {
        var helperDirectory = Path.TrimEndingDirectorySeparator(AppIdentity.ExecutableDirectory);
        if (!string.Equals(Path.GetDirectoryName(setup), helperDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(setup), AppIdentity.SetupAssetName, StringComparison.Ordinal) ||
            expectedLength is <= 0 or > 1024L * 1024L * 1024L ||
            expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The staged update helper paths or package metadata are invalid.");
        }

        var updateRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.UpdateDirectoryName,
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

    private static Task WriteAtomicAsync(string path, AppUpdateCompletion result) =>
        AtomicFile.WriteAsync(path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, result, cancellationToken: token),
            CancellationToken.None);
}
