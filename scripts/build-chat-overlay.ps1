param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repositoryRoot 'native/chat-overlay'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'src/StreamlinkVlcStudio.Infrastructure/Vlc/BundledOverlay/build'
}
if ((& $Gcc -dumpmachine) -ne 'x86_64-w64-mingw32') {
    throw 'Use an x64 MinGW-w64 GCC targeting MSVCRT for VLC 3.0.'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$qualitySource = Join-Path $source 'quality-gdi'
$objectsDirectory = Join-Path $repositoryRoot ('.tmp/chat-overlay-objects/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $objectsDirectory | Out-Null
$gxx = Join-Path (Split-Path -Parent $Gcc) 'g++.exe'
$objects = @()
foreach ($name in @('wingdi.c', 'common.c', 'events.c', 'win32touch.c', 'sensors.cpp')) {
    $objectPath = Join-Path $objectsDirectory ($name + '.o')
    $compiler = if ($name.EndsWith('.cpp')) { $gxx } else { $Gcc }
    # VLC 3's own output implementation uses its deprecated manage callback.
    & $compiler -c -O2 -Wall -Wextra -Werror -Wno-deprecated-declarations -fno-strict-aliasing "-ffile-prefix-map=$repositoryRoot=." `
        -D__PLUGIN__ -DPIC -DNDEBUG -DWIN32 -D_WIN32_WINNT=0x0601 -DMODULE_STRING='"myoverlay"' `
        -include (Join-Path $qualitySource 'build-config.h') -I $VlcIncludeDirectory `
        (Join-Path $qualitySource $name) -o $objectPath
    if ($LASTEXITCODE -ne 0) { throw "GDI output build failed for $name ($LASTEXITCODE)." }
    $objects += $objectPath
}
& $Gcc -shared -O2 -Wall -Wextra -Werror -fno-strict-aliasing -static -static-libgcc "-ffile-prefix-map=$repositoryRoot=." `
    -D__PLUGIN__ -DPIC -DWIN32 -D_WIN32_WINNT=0x0601 `
    -I $VlcIncludeDirectory -L $VlcLibraryDirectory `
    (Join-Path $source 'myoverlay.c') `
    @objects -o (Join-Path $OutputDirectory 'libmyoverlay_plugin.dll') -lvlccore `
    -lgdi32 -lmsimg32 -luser32 -lole32 -luuid -lpropsys -lstdc++ -lwinpthread `
    '-Wl,--no-insert-timestamp,--image-base,0x180000000'
if ($LASTEXITCODE -ne 0) { throw "Overlay plugin build failed ($LASTEXITCODE)." }
foreach ($objectPath in $objects) { Remove-Item -LiteralPath $objectPath }
Remove-Item -LiteralPath $objectsDirectory
& $Gcc -O2 -Wall -Wextra -Werror -static-libgcc "-ffile-prefix-map=$repositoryRoot=." `
    (Join-Path $source 'vlc_chat_overlay.c') (Join-Path $source 'tls.c') `
    -o (Join-Path $OutputDirectory 'vlc_chat_overlay.exe') `
    -lws2_32 -lsecur32 -lcrypt32 -lgdi32 -luser32 -lwinhttp -lole32 -lgdiplus -ld2d1 -ldwrite -luuid `
    '-Wl,--no-insert-timestamp'
if ($LASTEXITCODE -ne 0) { throw "Overlay controller build failed ($LASTEXITCODE)." }
$objdump = Join-Path (Split-Path -Parent $Gcc) 'objdump.exe'
foreach ($name in @('libmyoverlay_plugin.dll', 'vlc_chat_overlay.exe')) {
    $binary = Join-Path $OutputDirectory $name
    $imports = (& $objdump -p $binary) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $imports -notmatch 'DLL Name:\s+msvcrt.dll' -or
        $imports -match 'DLL Name:\s+(?:ucrtbase|api-ms-win-crt|libgcc|libstdc\+\+|libwinpthread)') {
        throw "Unexpected compiler runtime dependency in $name."
    }
    Get-FileHash -LiteralPath $binary -Algorithm SHA256
}
