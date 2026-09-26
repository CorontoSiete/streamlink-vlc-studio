param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [Parameter(Mandatory = $true)][string]$VlcDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.tmp/chat-compositor-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$binary = Join-Path $OutputDirectory 'compositor.exe'
& $Gcc -O2 -Wall -Wextra -Werror -static-libgcc -D__PLUGIN__ -DWIN32 -D_WIN32_WINNT=0x0601 `
    -include (Join-Path $repositoryRoot 'native/chat-overlay/quality-gdi/build-config.h') `
    -I $VlcIncludeDirectory -L $VlcLibraryDirectory -L $VlcDirectory `
    (Join-Path $repositoryRoot 'native/chat-overlay/tests/compositor.c') -o $binary `
    '-l:libvlc.dll' -lvlccore -lgdi32 -lmsimg32
if ($LASTEXITCODE -ne 0) { throw "Compositor test build failed ($LASTEXITCODE)." }
$previousPath = $env:PATH
try {
    $env:PATH = $VlcDirectory + ';' + $previousPath
    & $binary
    if ($LASTEXITCODE -ne 0) { throw "Compositor tests failed ($LASTEXITCODE)." }
} finally { $env:PATH = $previousPath }
