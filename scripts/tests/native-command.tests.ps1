[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\native-test.ps1')
$nativeTestExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$nativeTestHadExitCode = $null -ne $nativeTestExitCode
$nativeTestSavedExitCode = if ($nativeTestHadExitCode) { $nativeTestExitCode.Value } else { $null }
try {
    $hostExecutable = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $output = @(Invoke-NativeTestCommand -FilePath $hostExecutable -FailureMessage 'Fixture failed' -Arguments @(
        '-NoProfile', '-NonInteractive', '-Command',
        "[Console]::Out.WriteLine('native output'); [Console]::Error.WriteLine('native diagnostic'); [Console]::Error.WriteLine(); exit 0"
    ))
    if ($output -notcontains 'native output' -or $output -notcontains 'native diagnostic') {
        throw 'A successful native command lost its stdout or stderr.'
    }
    if ($output -notcontains '') { throw 'An empty stderr line was replaced by a PowerShell exception type.' }
    if ($ErrorActionPreference -ne 'Stop') { throw 'Native command handling changed the caller error policy.' }
    Write-Host 'PASS native commands: successful stderr diagnostics survive redirected output'

    $failed = $false
    try {
        Invoke-NativeTestCommand -FilePath $hostExecutable -FailureMessage 'Expected native failure' -Arguments @(
            '-NoProfile', '-NonInteractive', '-Command', "[Console]::Error.WriteLine('failure diagnostic'); exit 17"
        ) | Out-Null
    } catch {
        if ($_.Exception.Message -notmatch 'Expected native failure \(17\)') { throw }
        $failed = $true
    }
    if (-not $failed) { throw 'A nonzero native exit code was accepted.' }
    Write-Host 'PASS native commands: failing exit codes retain the command failure and diagnostics'

    $failed = $false
    try {
        Invoke-NativeTestCommand -FilePath (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.exe')) `
            -FailureMessage 'Missing executable' | Out-Null
    } catch [Management.Automation.CommandNotFoundException] { $failed = $true }
    if (-not $failed) { throw 'A missing executable was accepted using a stale exit code.' }
    Write-Host 'PASS native commands: missing executables fail before stale exit codes are examined'
} finally {
    if ($nativeTestHadExitCode) {
        Set-Variable -Name LASTEXITCODE -Value $nativeTestSavedExitCode -Scope Global
    } else {
        Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
}
