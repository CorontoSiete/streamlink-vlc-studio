[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [AllowEmptyString()][string]$Tag = '',
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$InternalMsiPath,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$SbomPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\release-contract.ps1')
$contract = Read-ReleaseContract (Join-Path $repoRoot 'shared\release-contract.json')
$parsedVersion = ConvertTo-StableReleaseVersion $Version
if ($parsedVersion -lt (ConvertTo-StableReleaseVersion ([string]$contract.release.minimumVersion))) {
    throw "Build version $Version is below the supported floor $($contract.release.minimumVersion)."
}
if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $identity = Get-StableReleaseIdentity -Tag $Tag -MinimumVersion ([string]$contract.release.minimumVersion)
    if ($identity.VersionText -cne $Version) { throw "Build tag $Tag does not match version $Version." }
}

Assert-WindowsFileVersion $SetupPath 'Burn bundle' $Version
Assert-ManagedUpdateCompatibility $InternalMsiPath
$msiVersion = Get-MsiPropertyValue -Path $InternalMsiPath -Property ProductVersion
if ($msiVersion -cne $Version) { throw "Internal MSI ProductVersion mismatch. Expected $Version; found $msiVersion." }

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('StreamStudio-build-version-' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-ValidatedZipArchive $ZipPath $temporaryRoot
    $payloadRoot = Resolve-ReleasePayloadRoot $temporaryRoot $contract
    Assert-WindowsFileVersion (Join-Path $payloadRoot 'StreamStudio.exe') 'Application' $Version
    Assert-WindowsFileVersion (Join-Path $payloadRoot 'Uninstall.exe') 'ZIP maintenance helper' $Version
    $zipMetadata = Get-Content -LiteralPath (Join-Path $payloadRoot 'release-metadata.json') -Raw | ConvertFrom-Json
    $expectedTag = if ([string]::IsNullOrWhiteSpace($Tag)) { '' } else { $Tag }
    if ($zipMetadata.schemaVersion -ne 1 -or
        [string]$zipMetadata.version -cne $Version -or
        [string]$zipMetadata.tag -cne $expectedTag -or
        [string]$zipMetadata.commit -cne $Commit -or
        [string]$zipMetadata.repository -cne $Repository) {
        throw 'ZIP metadata does not exactly match the requested build identity.'
    }
} finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) { Remove-DirectoryTreeSafely $temporaryRoot }
}

$sbom = Get-Content -LiteralPath $SbomPath -Raw | ConvertFrom-Json
$rootPackage = @($sbom.packages | Where-Object { [string]$_.SPDXID -ceq 'SPDXRef-Package-StreamStudio' })
if ($rootPackage.Count -ne 1 -or [string]$rootPackage[0].versionInfo -cne $Version) {
    throw "SBOM application version does not exactly match $Version."
}
Write-Host "Verified build-wide version $Version."
