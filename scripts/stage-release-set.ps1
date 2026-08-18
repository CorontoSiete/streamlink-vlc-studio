[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$ContractPath,
    [string]$Destination,
    [string]$CheckRoot,
    [switch]$Check,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
} else {
    [IO.Path]::GetFullPath($RepositoryRoot)
}
$contractFile = if ([string]::IsNullOrWhiteSpace($ContractPath)) {
    Join-Path $repoRoot 'shared\release-contract.json'
} else {
    [IO.Path]::GetFullPath($ContractPath)
}

. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\release-contract.ps1')
$contract = Read-ReleaseContract $contractFile

if ($Check) {
    $root = if ([string]::IsNullOrWhiteSpace($CheckRoot)) {
        Join-Path $repoRoot 'artifacts\release-set'
    } else {
        [IO.Path]::GetFullPath($CheckRoot)
    }
    $files = @(Test-VerifiedReleaseSet -Contract $contract -Root $root)
    Write-Info "Verified closed release set with $($files.Count) assets: $root"
    return
}

$destinationRoot = if ([string]::IsNullOrWhiteSpace($Destination)) {
    Join-Path $repoRoot 'artifacts\release-set'
} else {
    [IO.Path]::GetFullPath($Destination)
}
$files = @(New-VerifiedReleaseSet -Contract $contract -RepositoryRoot $repoRoot -Destination $destinationRoot)
Write-Info "Promoted closed release set with $($files.Count) assets: $destinationRoot"
