param(
    [Parameter(Mandatory = $true)][string]$BaselineTests,
    [Parameter(Mandatory = $true)][string]$UpdatedTests,
    [Parameter(Mandatory = $true)][string]$HlsDirectory,
    [Parameter(Mandatory = $true)][string]$NativeRenderer,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$DotNetPath = "$env:USERPROFILE\.dotnet\dotnet.exe",
    [ValidateSet(4, 8)][int[]]$Streams = @(4, 8),
    [ValidateSet('quiet', 'busy', 'animated')][string[]]$Chat = @('quiet', 'busy', 'animated'),
    [ValidateRange(1, 10)][int]$Trials = 3,
    [ValidateRange(0, 300)][int]$WarmupSeconds = 10,
    [ValidateRange(1, 300)][int]$Seconds = 30,
    [switch]$VlcDiagnostics,
    [string]$StopFile = ''
)
$ErrorActionPreference = 'Stop'
$dotnet = (Resolve-Path -LiteralPath $DotNetPath).Path
$binaries = @{
    before = (Resolve-Path -LiteralPath $BaselineTests).Path
    after = (Resolve-Path -LiteralPath $UpdatedTests).Path
}
$fixture = (Resolve-Path -LiteralPath $HlsDirectory).Path
$renderer = (Resolve-Path -LiteralPath $NativeRenderer).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$configuration = @{
    DOTNET_ROOT = (Split-Path -Parent $dotnet)
    SVS_TEST_FILTER = 'multistream application benchmark:'
    SVS_TEST_TIMEOUT_SECONDS = '240'
    SVS_SKIP_INTERACTIVE_WINDOW_TESTS = 'false'
    SVS_EXPECTED_MAX_SKIPS = '0'
    SVS_BENCHMARK_MULTISTREAM = '1'
    SVS_BENCHMARK_WARMUP = [string]$WarmupSeconds
    SVS_BENCHMARK_SECONDS = [string]$Seconds
    SVS_TEST_START_WATCH_HLS_DIRECTORY = $fixture
    SVS_BENCHMARK_NATIVE_RENDERER = $renderer
    SVS_BENCHMARK_VLC_DIAGNOSTICS = $(if ($VlcDiagnostics) { '1' } else { '0' })
    SVS_BENCHMARK_STREAMS = ''
    SVS_BENCHMARK_CHAT = ''
    SVS_BENCHMARK_VERSION = ''
    SVS_BENCHMARK_OUTPUT = ''
}
$saved = @{}
foreach ($key in $configuration.Keys) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $configuration[$key], 'Process')
}
try {
    foreach ($count in $Streams) {
        foreach ($mode in $Chat) {
            for ($trial = 1; $trial -le $Trials; $trial++) {
                $versions = if ($trial % 2 -eq 1) { @('before', 'after') } else { @('after', 'before') }
                foreach ($version in $versions) {
                    if ($StopFile -and (Test-Path -LiteralPath $StopFile)) {
                        Write-Host 'Stopped between completed trials.'
                        return
                    }
                    $name = "$count-$mode-$trial-$version"
                    if (Test-Path -LiteralPath (Join-Path $outputRoot "$name.json")) {
                        throw "Trial $name already exists. Choose a fresh output directory to preserve raw measurements."
                    }
                    $env:SVS_BENCHMARK_STREAMS = [string]$count
                    $env:SVS_BENCHMARK_CHAT = $mode
                    $env:SVS_BENCHMARK_VERSION = $version
                    $env:SVS_BENCHMARK_OUTPUT = Join-Path $outputRoot "$name.json"
                    Write-Host "Measuring $name ($WarmupSeconds seconds warmup, $Seconds seconds sample)..."
                    & $dotnet $binaries[$version] *> (Join-Path $outputRoot "$name.log")
                    if ($LASTEXITCODE -ne 0) { throw "Trial $name failed. Inspect its log and raw JSON before continuing." }
                    Get-Content -LiteralPath (Join-Path $outputRoot "$name.log") | Select-String 'streams,|All .* tests passed'
                }
            }
        }
    }
} finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
}
