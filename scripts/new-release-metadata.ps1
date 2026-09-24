[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$SourceRef,
    [Parameter(Mandatory = $true)][string]$WorkflowRef,
    [Parameter(Mandatory = $true)][long]$WorkflowRunId,
    [Parameter(Mandatory = $true)][long]$WorkflowRunNumber,
    [Parameter(Mandatory = $true)][long]$WorkflowRunAttempt,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$InternalMsiPath,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$SbomPath,
    [Parameter(Mandatory = $true)][string]$UpdateManifestPath,
    [Parameter(Mandatory = $true)][string]$UpdateSignaturePath,
    [string]$OutputPath = 'artifacts\RELEASE-METADATA.json'
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
    throw "Release metadata version $Version does not match tag $Tag."
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repository must be an owner/name pair: $Repository"
}

function Get-ArtifactRecord([string]$Path, [string]$ExpectedName) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release metadata input is missing: $Path"
    }
    $file = Get-Item -LiteralPath $Path
    if ($file.Name -cne $ExpectedName -or $file.Length -le 0) {
        throw "Release metadata input must be a non-empty $ExpectedName file: $Path"
    }
    [ordered]@{
        name = $file.Name
        length = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$authenticode = Get-AuthenticodeSigningConfiguration
$metadata = [ordered]@{
    schemaVersion = 2
    version = $Version
    tag = $Tag
    repository = $Repository
    commit = $Commit
    sourceRef = $SourceRef
    workflowRef = $WorkflowRef
    workflowRunId = $WorkflowRunId
    workflowRunNumber = $WorkflowRunNumber
    workflowRunAttempt = $WorkflowRunAttempt
    msiProductVersion = (Get-MsiPropertyValue -Path $InternalMsiPath -Property 'ProductVersion')
    bundleVersion = $identity.FourPartVersion
    dotnetSdkVersion = '10.0.302'
    selfContainedRuntimeVersion = '10.0.10'
    updaterProtocolVersion = [int]$contract.release.updaterProtocolVersion
    updateSigning = [ordered]@{
        algorithm = [string]$contract.release.manifestSignature.algorithm
        keyId = [string]$contract.release.manifestSignature.keyId
    }
    authenticode = [ordered]@{
        enabled = [bool]$authenticode.Enabled
        timestampProtocol = if ($authenticode.Enabled) { 'RFC3161' } else { $null }
        timestampUrl = if ($authenticode.Enabled) { [string]$authenticode.TimestampUrl } else { $null }
        signerThumbprint = if ($authenticode.Enabled) { [string]$authenticode.CertificateThumbprint } else { $null }
    }
    artifacts = [ordered]@{
        setup = (Get-ArtifactRecord $SetupPath 'StreamlinkVlcStudio-Setup.exe')
        zip = (Get-ArtifactRecord $ZipPath 'StreamlinkVlcStudio-release.zip')
        sbom = (Get-ArtifactRecord $SbomPath 'StreamlinkVlcStudio.spdx.json')
        updateManifest = (Get-ArtifactRecord $UpdateManifestPath 'UPDATE-MANIFEST.json')
        updateSignature = (Get-ArtifactRecord $UpdateSignaturePath 'UPDATE-MANIFEST.sig')
    }
    internalMsi = [ordered]@{
        public = $false
        productVersion = (Get-MsiPropertyValue -Path $InternalMsiPath -Property 'ProductVersion')
    }
    nativeInputManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $repoRoot 'dependencies\native-overlay.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    createdUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}

$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$parent = Split-Path -Parent $fullOutputPath
[IO.Directory]::CreateDirectory($parent) | Out-Null
$temporary = "$fullOutputPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText(
        $temporary,
        ($metadata | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $fullOutputPath -Force
} finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Remove-Item -LiteralPath $temporary -Force
    }
}
Write-Output $fullOutputPath
