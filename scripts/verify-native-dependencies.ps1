[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$ManifestPath,
    [string]$OverlaySource
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

$verified = @(Assert-NativeOverlaySource -OverlaySource $source -ManifestPath $manifest)
Write-Host "Verified $($verified.Count) pinned native overlay inputs from $source."
