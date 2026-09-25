param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) {
    $OutputPath = Join-Path $repositoryRoot 'src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledReplayPause\libstudio_replay_pause_plugin.dll'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if ((& $Gcc -dumpmachine) -ne 'x86_64-w64-mingw32') {
    throw 'Use an x64 MinGW-w64 GCC targeting the MSVCRT runtime for VLC 3.0.'
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
& $Gcc -shared -O2 -Wall -Wextra -Werror -fno-strict-aliasing -static-libgcc `
    -D__PLUGIN__ -DPIC -DWIN32 -D_WIN32_WINNT=0x0601 `
    -I $VlcIncludeDirectory -L $VlcLibraryDirectory `
    (Join-Path $repositoryRoot 'native\replay-pause\replay_pause.c') `
    -o $OutputPath -lvlccore '-Wl,--no-insert-timestamp,--image-base,0x180000000,--strip-all'
if ($LASTEXITCODE -ne 0) { throw "Replay pause plugin build failed ($LASTEXITCODE)." }
$objdump = Join-Path (Split-Path -Parent $Gcc) 'objdump.exe'
$imports = (& $objdump -p $OutputPath) -join "`n"
if ($LASTEXITCODE -ne 0 -or $imports -notmatch 'DLL Name:\s+msvcrt.dll' -or
    $imports -match 'DLL Name:\s+(?:ucrtbase|api-ms-win-crt|libgcc|libwinpthread)') {
    throw 'The VLC 3.0 plugin must use MSVCRT without an additional compiler runtime DLL.'
}
Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
