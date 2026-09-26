param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.tmp/chat-overlay-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
foreach ($name in @('tls-handshake', 'network', 'readability')) {
    $binary = Join-Path $OutputDirectory ($name + '.exe')
    $sources = @((Join-Path $repositoryRoot "native/chat-overlay/tests/$name.c"))
    if ($name -ne 'tls-handshake') { $sources += Join-Path $repositoryRoot 'native/chat-overlay/tls.c' }
    & $Gcc -O2 -Wall -Wextra -Werror -static-libgcc @sources -o $binary `
        -lws2_32 -lsecur32 -lcrypt32 -lgdi32 -luser32 -lwinhttp -lole32 -lgdiplus -ld2d1 -ldwrite -luuid
    if ($LASTEXITCODE -ne 0) { throw "Native $name test build failed ($LASTEXITCODE)." }
    & $binary $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Native $name tests failed ($LASTEXITCODE)." }
}
