param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [string]$OutputDirectory = '',
    [string]$BaselineSource = '',
    [ValidateSet('', 'quiet', 'busy', 'animated')][string]$Benchmark = '',
    [ValidateRange(1, 100000)][int]$Frames = 400,
    [ValidateRange(0, 32767)][int]$CatalogEntries = 4000,
    [ValidateSet('', 'references', 'capacity', 'pipe')][string]$Filter = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.tmp/chat-render-resources' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$binary = Join-Path $OutputDirectory 'render-resources.exe'
$compilerOptions = @()
if ($BaselineSource) {
    $baseline = (Resolve-Path -LiteralPath $BaselineSource).Path.Replace('\', '/')
    $baselineHeader = Join-Path $OutputDirectory 'baseline-source.h'
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($baselineHeader),
        "#define RESOURCE_BASELINE`n" + '#define OVERLAY_RENDERER_SOURCE "' + $baseline + "`"`n",
        [Text.UTF8Encoding]::new($false))
    $compilerOptions += @('-include', $baselineHeader)
}
& $Gcc -O2 -Wall -Wextra -Werror -fmax-errors=4 -static-libgcc @compilerOptions `
    (Join-Path $repositoryRoot 'native/chat-overlay/tests/render-resources.c') `
    (Join-Path $repositoryRoot 'native/chat-overlay/tls.c') -o $binary `
    -lws2_32 -lsecur32 -lcrypt32 -lgdi32 -luser32 -lwinhttp -lole32 -lgdiplus -ld2d1 -ldwrite -luuid -lpsapi
if ($LASTEXITCODE -ne 0) { throw "Native render resource test build failed ($LASTEXITCODE)." }
if ($Benchmark) { & $binary --benchmark $Benchmark $Frames $CatalogEntries }
elseif ($Filter) { & $binary "--$Filter" }
else { & $binary }
if ($LASTEXITCODE -ne 0) { throw "Native render resource tests failed ($LASTEXITCODE)." }
