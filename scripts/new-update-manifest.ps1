[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][string]$SetupPath,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
    [string]$ContractPath,
    [string]$PublicKeyPath,
    [string]$Repository = 'CorontoSiete/streamlink-vlc-studio',
    [string]$OutputDirectory = 'artifacts'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\release-contract.ps1')
$contractFile = if ([string]::IsNullOrWhiteSpace($ContractPath)) {
    Join-Path $repoRoot 'shared\release-contract.json'
} else {
    [IO.Path]::GetFullPath($ContractPath)
}
$contract = Read-ReleaseContract $contractFile
$identity = Get-StableReleaseIdentity -Tag $Tag -MinimumVersion ([string]$contract.release.minimumVersion)
if ($identity.VersionText -cne $Version) { throw "Tag must exactly match version: v$Version" }
if ($Commit -cne $Commit.ToLowerInvariant()) { throw 'Commit must use lowercase hexadecimal.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "Repository must be an owner/name pair: $Repository" }
if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) {
    $contractDirectory = Split-Path -Parent $contractFile
    $contractRoot = if ((Split-Path -Leaf $contractDirectory) -ieq 'shared') { Split-Path -Parent $contractDirectory } else { $contractDirectory }
    $PublicKeyPath = Join-Path $contractRoot ([string]$contract.release.manifestSignature.publicKey)
}
foreach ($path in @($SetupPath, $ZipPath, $PrivateKeyPath, $PublicKeyPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required update-manifest input is missing: $path" }
}
$dependencyManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'dependencies\windows-installers.json') -Raw | ConvertFrom-Json
if ($dependencyManifest.schemaVersion -ne 1 -or
    [string]::IsNullOrWhiteSpace([string]$dependencyManifest.dependencies.streamlink.version) -or
    [string]::IsNullOrWhiteSpace([string]$dependencyManifest.dependencies.vlc.version)) {
    throw 'The locked Windows dependency manifest is incomplete.'
}
$setup = Get-Item -LiteralPath $SetupPath
$zip = Get-Item -LiteralPath $ZipPath
if ($setup.Name -cne 'StreamlinkVlcStudio-Setup.exe' -or $zip.Name -cne 'StreamlinkVlcStudio-release.zip') {
    throw 'Update package names do not match the updater protocol.'
}

$manifest = [ordered]@{
    schemaVersion = 1
    protocolVersion = [int]$contract.release.updaterProtocolVersion
    channel = 'stable'
    prerelease = $false
    version = $Version
    tag = $Tag
    commit = $Commit.ToLowerInvariant()
    repository = $Repository
    releasePage = "https://github.com/$Repository/releases/tag/$Tag"
    keyId = [string]$contract.release.manifestSignature.keyId
    dependencyMinimums = [ordered]@{
        streamlink = [string]$dependencyManifest.dependencies.streamlink.version
        vlc = [string]$dependencyManifest.dependencies.vlc.version
    }
    setup = [ordered]@{
        name = $setup.Name
        length = $setup.Length
        sha256 = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    zip = [ordered]@{
        name = $zip.Name
        length = $zip.Length
        sha256 = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$manifestPath = Join-Path $output 'UPDATE-MANIFEST.json'
$signaturePath = Join-Path $output 'UPDATE-MANIFEST.sig'
$utf8 = [Text.UTF8Encoding]::new($false)
$bytes = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 8 -Compress))
[IO.File]::WriteAllBytes($manifestPath, $bytes)

$rsa = [Security.Cryptography.RSA]::Create()
try {
    $pem = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $PrivateKeyPath))
    $rsa.ImportFromPem($pem)
    if ($rsa.KeySize -ne [int]$contract.release.manifestSignature.keyBits) { throw "Release signing key must be exactly $($contract.release.manifestSignature.keyBits) bits; found $($rsa.KeySize)." }
    $privateKeyId = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    if ($privateKeyId -cne [string]$contract.release.manifestSignature.keyId -or
        $privateKeyId -cne (Get-ReleasePublicKeyId $PublicKeyPath)) {
        throw "Protected release private key does not match the committed update trust root. Expected $($contract.release.manifestSignature.keyId); found $privateKeyId."
    }
    $signature = $rsa.SignData(
        $bytes,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllBytes($signaturePath, $signature)
    if (-not $rsa.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw 'The generated update-manifest signature did not verify.'
    }
} finally {
    $rsa.Dispose()
}

& (Join-Path $scriptRoot 'verify-update-manifest.ps1') `
    -ManifestPath $manifestPath `
    -SignaturePath $signaturePath `
    -ContractPath $contractFile `
    -PublicKeyPath $PublicKeyPath `
    -SetupPath $SetupPath `
    -ZipPath $ZipPath `
    -ExpectedVersion $Version `
    -ExpectedTag $Tag `
    -ExpectedCommit $Commit `
    -ExpectedRepository $Repository

Write-Output $manifestPath
Write-Output $signaturePath
