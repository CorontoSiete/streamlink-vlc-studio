using System.Diagnostics;
using System.Text;

namespace StreamlinkVlcStudio.Maintenance;

internal static class StageCleanup
{
    private const string Prefix = "StreamStudio-Maintenance-Stage-";

    internal static bool IsSafeStageDirectory(string directory)
    {
        var path = PathSafety.Normalize(directory);
        var name = Path.GetFileName(path);
        return Path.GetDirectoryName(path) is { } parent && PathSafety.PathsEqual(parent, Path.GetTempPath()) &&
               name.StartsWith(Prefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(name[Prefix.Length..], "N", out _) &&
               !PathSafety.ContainsReparsePoint(path);
    }

    internal static bool TryLaunch(string stageDirectory, int parentProcessId, MaintenanceLog log)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(stageDirectory, parentProcessId));
            if (process is null) return false;
            log.Write($"Scheduled removal of temporary maintenance files after exit: {stageDirectory}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Write($"Could not start temporary maintenance cleanup: {exception.Message}");
            return false;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string stageDirectory, int parentProcessId)
    {
        if (!IsSafeStageDirectory(stageDirectory) || parentProcessId <= 0)
            throw new InvalidDataException("Temporary maintenance cleanup requires a validated stage and parent process.");

        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = PathSafety.Normalize(Path.GetTempPath())
        };
        // Paths are passed as data, never interpolated into executable PowerShell text.
        info.Environment["STREAMSTUDIO_CLEANUP_STAGE"] = PathSafety.Normalize(stageDirectory);
        info.Environment["STREAMSTUDIO_CLEANUP_PARENT"] = parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                     "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(CleanupScript)) })
            info.ArgumentList.Add(argument);
        return info;
    }

    // No recursive deletion: remove only the two files created by the handoff, then
    // the empty directory. Recheck the path before retries, including all ancestors.
    private const string CleanupScript = """
        $ErrorActionPreference = 'Stop'
        try {
            $stage = [IO.Path]::GetFullPath($env:STREAMSTUDIO_CLEANUP_STAGE).TrimEnd('\')
            $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
            if ([IO.Path]::GetDirectoryName($stage) -ine $temporaryRoot -or
                [IO.Path]::GetFileName($stage) -cnotmatch '^StreamStudio-Maintenance-Stage-[0-9a-f]{32}$') { exit 1 }
            $parentId = [int]$env:STREAMSTUDIO_CLEANUP_PARENT
            if ($parentId -le 0) { exit 1 }
            $parent = Get-Process -Id $parentId -ErrorAction SilentlyContinue
            if ($null -ne $parent) {
                try { if (-not $parent.WaitForExit(30000)) { exit 1 } }
                finally { $parent.Dispose() }
            }
            for ($attempt = 0; $attempt -lt 10; $attempt++) {
                try {
                    for ($ancestor = $stage; $null -ne $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
                        if (Test-Path -LiteralPath $ancestor) {
                            $attributes = [IO.File]::GetAttributes($ancestor)
                            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
                        }
                    }
                    foreach ($name in @('StreamStudio.Maintenance.exe', '.stage-token')) {
                        $file = Join-Path $stage $name
                        if (Test-Path -LiteralPath $file) {
                            $attributes = [IO.File]::GetAttributes($file)
                            if (($attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -ne 0) { exit 1 }
                            Remove-Item -LiteralPath $file -Force
                        }
                    }
                    if (Test-Path -LiteralPath $stage) { [IO.Directory]::Delete($stage, $false) }
                    exit 0
                } catch {
                    if ($attempt -lt 9) { Start-Sleep -Milliseconds 300 }
                }
            }
        } catch { }
        exit 1
        """;
}
