function Invoke-NativeTestCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $command = Get-Command -Name $FilePath -CommandType Application -ErrorAction Stop
    # Windows PowerShell wraps redirected stderr in error records, even when
    # the executable succeeded. Keep diagnostics and judge native tests by their
    # exit status. These preferences are local to this function.
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    & $command @Arguments 2>&1 | ForEach-Object {
        if ($_ -is [Management.Automation.ErrorRecord]) {
            if ($_.FullyQualifiedErrorId -notin @('NativeCommandError', 'NativeCommandErrorMessage')) {
                throw $_
            }
            $_.Exception.Message
        } else {
            $_.ToString()
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage ($LASTEXITCODE)." }
}
