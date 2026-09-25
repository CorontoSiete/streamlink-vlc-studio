[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$ProbePath,
    [ValidateSet('', 'Managed', 'Zip', 'Unmanaged')][string]$ExpectedInstallKind = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$info = [Diagnostics.ProcessStartInfo]::new($executable)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
# Scope the startup hook to this one child. It exits before app startup or any installation.
$info.Environment['DOTNET_STARTUP_HOOKS'] = (Resolve-Path -LiteralPath $ProbePath).Path
$process = [Diagnostics.Process]::Start($info)
try {
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Packaged updater probe timed out.' }
    $reportText = $output.GetAwaiter().GetResult()
    $errorText = $errors.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Packaged updater probe failed: $errorText" }
    $report = $reportText | ConvertFrom-Json
    if ($report.ProcessPath -ine $executable -or
        [IO.Path]::TrimEndingDirectorySeparator($report.ConfiguredDirectory) -ine (Split-Path $executable -Parent)) {
        throw "The packaged updater is using the extraction cache instead of its apphost directory: $reportText"
    }
    if ($ExpectedInstallKind -ne '' -and $report.InstallKind -cne $ExpectedInstallKind) {
        throw "Packaged updater ownership mismatch. Expected $ExpectedInstallKind; got $($report.InstallKind)."
    }
    Write-Host "PASS packaged updater uses its executable directory; installation kind: $($report.InstallKind)."
    Write-Host $reportText
} finally {
    $process.Dispose()
}
