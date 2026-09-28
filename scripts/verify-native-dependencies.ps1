[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$ManifestPath,
    [string]$OverlaySource,
    [switch]$SkipAuthenticodeWhenUnavailable
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
} else {
    [IO.Path]::GetFullPath($RepositoryRoot)
}
$manifest = if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    Join-Path $repoRoot 'dependencies\native-overlay.json'
} else {
    [IO.Path]::GetFullPath($ManifestPath)
}
$source = if ([string]::IsNullOrWhiteSpace($OverlaySource)) {
    Join-Path $repoRoot 'src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledOverlay'
} else {
    [IO.Path]::GetFullPath($OverlaySource)
}

. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\native-overlay.ps1')

$verified = @(Assert-NativeOverlaySource `
    -OverlaySource $source `
    -ManifestPath $manifest `
    -SkipAuthenticodeWhenUnavailable:$SkipAuthenticodeWhenUnavailable)
Write-Host "Verified $($verified.Count) pinned native overlay inputs from $source."

$coreRoot = Join-Path $repoRoot 'native\vlc-core'
$coreProvenancePath = Join-Path $coreRoot 'provenance.json'
$coreProvenance = Get-Content -LiteralPath $coreProvenancePath -Raw | ConvertFrom-Json
if ($coreProvenance.schemaVersion -ne 1 -or
    $coreProvenance.referenceVlcVersion -cne '3.0.23' -or
    $coreProvenance.license -cne 'LGPL-3.0-or-later' -or
    $coreProvenance.vlcSourceLicense -cne 'LGPL-2.1-or-later' -or
    @($coreProvenance.buildInputs.contribPackages).Count -ne 5) {
    throw 'Bundled VLC core provenance is missing its supported version or license.'
}
$coreRuntimePath = Join-Path $repoRoot 'src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledVlcCoreRuntime.cs'
$coreRuntimeSource = Get-Content -LiteralPath $coreRuntimePath -Raw
$runtimeHashMatch = [regex]::Match(
    $coreRuntimeSource,
    'private\s+const\s+string\s+BundledCoreSha256\s*=\s*"(?<hash>[0-9a-f]{64})"\s*;')
if (-not $runtimeHashMatch.Success -or
    $runtimeHashMatch.Groups['hash'].Value -cne [string]$coreProvenance.binarySha256) {
    throw 'Bundled VLC core runtime hash does not match the pinned binary provenance.'
}
foreach ($relativePath in (@(
        'COPYING.LIB',
        'README.md',
        'core-config.h',
        'windows-address-waits.patch',
        'provenance.json') + @($coreProvenance.licenseFiles))) {
    if (-not (Test-Path -LiteralPath (Join-Path $coreRoot $relativePath) -PathType Leaf)) {
        throw "Bundled VLC core source material is missing: $relativePath"
    }
}
$coreRelativePath = ([string]$coreProvenance.binary).Replace('/', [IO.Path]::DirectorySeparatorChar)
$coreBinaryPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $coreRelativePath))
if (-not (Test-Path -LiteralPath $coreBinaryPath -PathType Leaf)) {
    throw "Bundled VLC core binary is missing: $coreBinaryPath"
}
$coreHashes = @(
        [string]$coreProvenance.upstreamSha256,
        [string]$coreProvenance.referenceVlcSha256,
        [string]$coreProvenance.referenceVlcCoreSha256,
        [string]$coreProvenance.binarySha256,
        [string]$coreProvenance.buildInputs.toolchain.archiveSha256,
        [string]$coreProvenance.buildInputs.toolchain.compilerBinarySha256)
foreach ($package in $coreProvenance.buildInputs.contribPackages) {
    $coreHashes += [string]$package.archiveSha256
    $coreHashes += [string]$package.librarySha256
}
foreach ($runtimeLibrary in $coreProvenance.buildInputs.toolchain.linkedRuntimeLibraries) {
    $coreHashes += [string]$runtimeLibrary.sha256
}
foreach ($hash in $coreHashes) {
    if ($hash -notmatch '^[0-9a-f]{64}$') {
        throw 'Bundled VLC core provenance contains an invalid SHA-256 value.'
    }
}
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $coreStream = [IO.File]::OpenRead($coreBinaryPath)
    try {
        $actualCoreHash = [BitConverter]::ToString($sha256.ComputeHash($coreStream)).Replace('-', '').ToLowerInvariant()
    } finally {
        $coreStream.Dispose()
    }
} finally {
    $sha256.Dispose()
}
if ($actualCoreHash -cne [string]$coreProvenance.binarySha256) {
    throw "Bundled VLC core SHA-256 mismatch: expected $($coreProvenance.binarySha256), got $actualCoreHash."
}
Write-Host "Verified bundled VLC 3.0.23 condition-wait core ($actualCoreHash)."
