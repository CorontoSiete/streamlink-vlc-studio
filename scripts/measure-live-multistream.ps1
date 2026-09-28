<#
.SYNOPSIS
Compare two Release test builds using currently live public Twitch/Kick streams.
.DESCRIPTION
Runs alternating fresh-process trials with production playback, chat, replay and
viewer services. Saves every raw counter sample, composed window and cleanup result.
Build both versions first; keep other builds and playback tests stopped while measuring.
#>
param(
    [Parameter(Mandatory = $true)][string]$BaselineTests,
    [Parameter(Mandatory = $true)][string]$UpdatedTests,
    [Parameter(Mandatory = $true)][string[]]$Urls,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$DotNetPath = "$env:USERPROFILE\.dotnet\dotnet.exe",
    [ValidateRange(1, 10)][int]$Trials = 3,
    [ValidateRange(0, 120)][int]$WarmupSeconds = 20,
    [ValidateRange(10, 300)][int]$Seconds = 60
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Urls.Count -notin @(4, 8)) { throw 'Supply four or eight live URLs, including both Twitch and Kick.' }
$dotnet = (Resolve-Path -LiteralPath $DotNetPath).Path
$binaries = @{
    before = (Resolve-Path -LiteralPath $BaselineTests).Path
    after = (Resolve-Path -LiteralPath $UpdatedTests).Path
}
if ((Get-FileHash -LiteralPath $binaries.before -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $binaries.after -Algorithm SHA256).Hash) {
    throw 'Use the same test assembly in both directories so the measurement harness is identical.'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$configuration = @{
    DOTNET_ROOT = (Split-Path -Parent $dotnet)
    SVS_TEST_FILTER = 'multistream live smoke:'
    SVS_TEST_TIMEOUT_SECONDS = [string]($WarmupSeconds + $Seconds + 180)
    SVS_SKIP_INTERACTIVE_WINDOW_TESTS = 'false'
    SVS_EXPECTED_MAX_SKIPS = '0'
    SVS_TEST_ISOLATED_CHILD = ''
    SVS_TEST_MULTISTREAM_LIVE_URLS = $Urls -join ','
    SVS_TEST_MULTISTREAM_LIVE_WARMUP = [string]$WarmupSeconds
    SVS_TEST_MULTISTREAM_LIVE_SECONDS = [string]$Seconds
    SVS_TEST_MULTISTREAM_LIVE_CYCLES = '1'
    SVS_TEST_MULTISTREAM_LIVE_OUTPUT = ''
    SVS_BENCHMARK_VERSION = ''
    # Verbose native logs are useful for diagnosis, but distort resource comparisons.
    SVS_BENCHMARK_VLC_DIAGNOSTICS = '0'
}
$saved = @{}
foreach ($key in $configuration.Keys) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $configuration[$key], 'Process')
}
try {
    for ($trial = 1; $trial -le $Trials; $trial++) {
        $versions = if ($trial % 2 -eq 1) { @('before', 'after') } else { @('after', 'before') }
        foreach ($version in $versions) {
            $name = "$($Urls.Count)-$trial-$version"
            $output = Join-Path $outputRoot $name
            $log = Join-Path $outputRoot "$name.log"
            if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $log)) {
                throw "Trial $name already exists. Use a fresh directory to preserve all observations."
            }
            $env:SVS_BENCHMARK_VERSION = $version
            $env:SVS_TEST_MULTISTREAM_LIVE_OUTPUT = $output
            Write-Host "Measuring $name ($WarmupSeconds seconds warmup, $Seconds seconds sample)..."
            & $dotnet $binaries[$version] *> $log
            if ($LASTEXITCODE -ne 0) { throw "Live trial $name failed. Inspect its log and raw results before continuing." }
            Get-Content -LiteralPath $log | Select-String 'CPU cores;|All .* tests passed'
        }
    }
} finally {
    foreach ($key in $saved.Keys) {
        if ($null -eq $saved[$key]) {
            [Environment]::SetEnvironmentVariable($key, [NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process')
        }
    }
}
