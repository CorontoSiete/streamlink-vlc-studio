[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$InternalMsiPath,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$SbomPath,
    [Parameter(Mandatory = $true)][string]$MetadataPath,
    [Parameter(Mandatory = $true)][string]$UpdateManifestPath,
    [Parameter(Mandatory = $true)][string]$UpdateSignaturePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\release-contract.ps1')
. (Join-Path $scriptRoot 'lib\authenticode.ps1')
$contract = Read-ReleaseContract (Join-Path $repoRoot 'shared\release-contract.json')
$identity = Get-StableReleaseIdentity -Tag $Tag -MinimumVersion ([string]$contract.release.minimumVersion)
if ($identity.VersionText -cne $Version) {
    throw "Release version $Version does not match stable tag $Tag."
}

& (Join-Path $scriptRoot 'verify-update-manifest.ps1') `
    -ManifestPath $UpdateManifestPath `
    -SignaturePath $UpdateSignaturePath `
    -SetupPath $SetupPath `
    -ZipPath $ZipPath `
    -ExpectedVersion $Version `
    -ExpectedTag $Tag `
    -ExpectedCommit $Commit `
    -ExpectedRepository $Repository

foreach ($path in @($SetupPath, $InternalMsiPath, $ZipPath, $SbomPath, $MetadataPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) {
        throw "Release artifact is missing or empty: $path"
    }
}

Assert-WindowsFileVersion $SetupPath 'Burn bundle' $Version
Assert-ManagedUpdateCompatibility $InternalMsiPath
$msiVersion = Get-MsiPropertyValue -Path $InternalMsiPath -Property 'ProductVersion'
if ($msiVersion -cne $Version) {
    throw "Internal MSI ProductVersion mismatch. Expected $Version; found $msiVersion."
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('StreamlinkVlcStudio-release-audit-' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-ValidatedZipArchive -ArchivePath $ZipPath -DestinationDirectory $temporaryRoot
    $payloadRoot = Resolve-ReleasePayloadRoot -ExtractedRoot $temporaryRoot -Contract $contract
    Assert-WindowsFileVersion (Join-Path $payloadRoot 'StreamStudio.exe') 'Application' $Version
    Assert-WindowsFileVersion (Join-Path $payloadRoot 'Uninstall.exe') 'ZIP maintenance helper' $Version

    $zipMetadata = Get-Content -LiteralPath (Join-Path $payloadRoot 'release-metadata.json') -Raw | ConvertFrom-Json
    if ($zipMetadata.schemaVersion -ne 1 -or
        [string]$zipMetadata.artifactKind -cne 'zip' -or
        [string]$zipMetadata.version -cne $Version -or
        [string]$zipMetadata.tag -cne $Tag -or
        [string]$zipMetadata.commit -cne $Commit -or
        [string]$zipMetadata.repository -cne $Repository -or
        [int]$zipMetadata.updaterProtocolVersion -ne [int]$contract.release.updaterProtocolVersion -or
        [string]$zipMetadata.updateSigningKeyId -cne [string]$contract.release.manifestSignature.keyId) {
        throw 'ZIP release metadata does not exactly match the stable release identity.'
    }

    $authenticode = Get-AuthenticodeSigningConfiguration
    if ($authenticode.Enabled) {
        foreach ($signedPath in @(
                $SetupPath,
                $InternalMsiPath,
                (Join-Path $payloadRoot 'StreamStudio.exe'),
                (Join-Path $payloadRoot 'Uninstall.exe'))) {
            Assert-AuthenticodeSignature -Path $signedPath -Configuration $authenticode | Out-Null
        }
    }
    if ([bool]$zipMetadata.authenticodeSigned -ne [bool]$authenticode.Enabled) {
        throw 'ZIP Authenticode metadata does not match release signing configuration.'
    }
} finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-DirectoryTreeSafely $temporaryRoot
    }
}

$sbom = Get-Content -LiteralPath $SbomPath -Raw | ConvertFrom-Json
$rootPackages = @($sbom.packages | Where-Object { [string]$_.SPDXID -ceq 'SPDXRef-Package-StreamStudio' })
if ($rootPackages.Count -ne 1 -or [string]$rootPackages[0].versionInfo -cne $Version) {
    throw "SBOM application version does not exactly match $Version."
}

$metadata = Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json
if ($metadata.schemaVersion -ne 2 -or
    [string]$metadata.version -cne $Version -or
    [string]$metadata.tag -cne $Tag -or
    [string]$metadata.commit -cne $Commit -or
    [string]$metadata.repository -cne $Repository -or
    [string]$metadata.msiProductVersion -cne $Version -or
    [string]$metadata.bundleVersion -cne "$Version.0" -or
    [int]$metadata.updaterProtocolVersion -ne [int]$contract.release.updaterProtocolVersion -or
    [string]$metadata.updateSigning.keyId -cne [string]$contract.release.manifestSignature.keyId) {
    throw 'Top-level release metadata does not exactly match the stable release identity.'
}

foreach ($entry in @(
        [pscustomobject]@{ Record = $metadata.artifacts.setup; Path = $SetupPath; Name = 'StreamlinkVlcStudio-Setup.exe' },
        [pscustomobject]@{ Record = $metadata.artifacts.zip; Path = $ZipPath; Name = 'StreamlinkVlcStudio-release.zip' },
        [pscustomobject]@{ Record = $metadata.artifacts.sbom; Path = $SbomPath; Name = 'StreamStudio.spdx.json' },
        [pscustomobject]@{ Record = $metadata.artifacts.updateManifest; Path = $UpdateManifestPath; Name = 'UPDATE-MANIFEST.json' },
        [pscustomobject]@{ Record = $metadata.artifacts.updateSignature; Path = $UpdateSignaturePath; Name = 'UPDATE-MANIFEST.sig' })) {
    $file = Get-Item -LiteralPath $entry.Path
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$entry.Record.name -cne $entry.Name -or
        [long]$entry.Record.length -ne $file.Length -or
        [string]$entry.Record.sha256 -cne $hash) {
        throw "Top-level release metadata has stale asset data for $($entry.Name)."
    }
}

Write-Host "Verified exact cross-artifact release version $Version and tag $Tag."
