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

function Assert-SignedReleaseSet([object[]]$Files) {
    $byName = @{}
    foreach ($item in $Files) { $byName[[string]$item.Entry.name] = $item.File.FullName }
    $metadata = Get-Content -LiteralPath $byName['RELEASE-METADATA.json'] -Raw | ConvertFrom-Json
    & (Join-Path $scriptRoot 'verify-update-manifest.ps1') `
        -ManifestPath $byName['UPDATE-MANIFEST.json'] `
        -SignaturePath $byName['UPDATE-MANIFEST.sig'] `
        -SetupPath $byName['StreamlinkVlcStudio-Setup.exe'] `
        -ZipPath $byName['StreamlinkVlcStudio-release.zip'] `
        -ExpectedVersion ([string]$metadata.version) `
        -ExpectedTag ([string]$metadata.tag) `
        -ExpectedCommit ([string]$metadata.commit) `
        -ExpectedRepository ([string]$metadata.repository)
}

if ($Check) {
    $root = if ([string]::IsNullOrWhiteSpace($CheckRoot)) {
        Join-Path $repoRoot 'artifacts\release-set'
    } else {
        [IO.Path]::GetFullPath($CheckRoot)
    }
    $files = @(Test-VerifiedReleaseSet -Contract $contract -Root $root)
    Assert-SignedReleaseSet $files
    Write-Info "Verified closed release set with $($files.Count) assets: $root"
    return
}

$destinationRoot = if ([string]::IsNullOrWhiteSpace($Destination)) {
    Join-Path $repoRoot 'artifacts\release-set'
} else {
    [IO.Path]::GetFullPath($Destination)
}
$files = @(New-VerifiedReleaseSet -Contract $contract -RepositoryRoot $repoRoot -Destination $destinationRoot)
Assert-SignedReleaseSet $files
Write-Info "Promoted closed release set with $($files.Count) assets: $destinationRoot"
