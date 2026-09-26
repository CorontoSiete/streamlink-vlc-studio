param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [Parameter(Mandatory = $true)][string]$VlcDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.tmp/chat-subpicture-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$binary = Join-Path $OutputDirectory 'subpictures.exe'
& $Gcc -O2 -Wall -Wextra -Werror -static-libgcc -D__PLUGIN__ -DWIN32 -D_WIN32_WINNT=0x0601 `
    -include (Join-Path $repositoryRoot 'native/chat-overlay/quality-gdi/build-config.h') `
    -I $VlcIncludeDirectory -L $VlcLibraryDirectory `
    (Join-Path $repositoryRoot 'native/chat-overlay/tests/subpictures.c') -o $binary `
    -lvlccore -luser32
if ($LASTEXITCODE -ne 0) { throw "Subpicture test build failed ($LASTEXITCODE)." }
$previousPath = $env:PATH
try {
    $env:PATH = $VlcDirectory + ';' + $previousPath
    & $binary
    if ($LASTEXITCODE -ne 0) { throw "Subpicture tests failed ($LASTEXITCODE)." }
} finally { $env:PATH = $previousPath }
