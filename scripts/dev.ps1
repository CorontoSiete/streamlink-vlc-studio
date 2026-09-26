<#
.SYNOPSIS
Build, test, check, or run Stream Studio with the SDK pinned in global.json.
.DESCRIPTION
Defaults to a Release build and the headless-safe test suite. Check also verifies
formatting, PowerShell syntax/tooling contracts, and bundled native dependencies.
Environment changes apply only to this invocation and its child processes.
.EXAMPLE
.\scripts\dev.ps1 Test -Filter 'stream open workflow:'
.EXAMPLE
.\scripts\dev.ps1 Test -Filter 'stream open workflow:' -NoBuild
.EXAMPLE
.\scripts\dev.ps1 Check
.EXAMPLE
.\scripts\dev.ps1 Run -Configuration Debug
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Build', 'Test', 'Check', 'Run')]
    [string]$Task = 'Test',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Filter = '',
    [switch]$NoBuild,
    [switch]$NoRestore,
    [switch]$Interactive,
    [ValidateRange(0, 2147483647)]
    [int]$ExpectedMaxSkips = 246,
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
# Check native exit codes explicitly on both Windows PowerShell and PowerShell 7.
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

if ($PSBoundParameters.ContainsKey('Filter') -and $Task -ne 'Test') {
    throw '-Filter is only supported for Test. Check always runs the complete suite.'
}
if ($NoBuild -and $Task -notin @('Test', 'Run')) {
    throw '-NoBuild is only supported for Test and Run.'
}
if (($Interactive -or $PSBoundParameters.ContainsKey('ExpectedMaxSkips')) -and $Task -notin @('Test', 'Check')) {
    throw '-Interactive and -ExpectedMaxSkips are only supported for Test and Check.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [string]::IsNullOrWhiteSpace($DotNetPath)) {
    # Resolve relative overrides before changing to the repository directory.
    $DotNetPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DotNetPath)
}

function Find-DevelopmentDotNet {
    param([string]$RequiredVersion, [string]$OverridePath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($OverridePath)) {
        $candidates = @($OverridePath)
    } else {
        $candidates += Join-Path $repoRoot '.dotnet-sdk\dotnet.exe'
        foreach ($root in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) {
            if (-not [string]::IsNullOrWhiteSpace($root)) {
                $candidates += Join-Path $root 'dotnet.exe'
            }
        }
        $candidates += @(Get-Command dotnet.exe -CommandType Application -All -ErrorAction SilentlyContinue |
            ForEach-Object Source)
        if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
            $candidates += Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
        }
        if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
            $candidates += Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        }
    }

    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        try {
            # Run from the repo so global.json, including rollForward, is honored.
            $versionOutput = @(& $candidate --version 2>&1)
            if ($LASTEXITCODE -eq 0 -and ($versionOutput -join "`n").Trim() -ceq $RequiredVersion) {
                return [IO.Path]::GetFullPath($candidate)
            }
        } catch {
            Write-Verbose "SDK candidate unavailable: $candidate"
        }
    }

    $checked = $candidates -join ', '
    throw "Could not find .NET SDK $RequiredVersion required by global.json. Install that x64 SDK, or pass -DotNetPath 'C:\path\to\dotnet.exe'. Checked: $checked"
}

function Invoke-DevelopmentDotNet {
    param([string[]]$Arguments)

    Write-Host ("dotnet " + ($Arguments -join ' '))
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

$environmentNames = @(
    'PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH', 'DOTNET_CLI_HOME',
    'DOTNET_NOLOGO', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_CLI_TELEMETRY_OPTOUT',
    'TEMP', 'TMP', 'SVS_TEST_FILTER', 'SVS_SKIP_INTERACTIVE_WINDOW_TESTS',
    'SVS_EXPECTED_MAX_SKIPS', 'SVS_TEST_ISOLATED_CHILD'
)
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

Push-Location -LiteralPath $repoRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
    $env:TEMP = Join-Path $repoRoot '.tmp'
    $env:TMP = $env:TEMP
    $env:DOTNET_NOLOGO = 'true'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 'true'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'
    New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME, $env:TEMP -Force | Out-Null

    $requiredVersion = [string](Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $dotnet = Find-DevelopmentDotNet -RequiredVersion $requiredVersion -OverridePath $DotNetPath
    $dotnetRoot = Split-Path -Parent $dotnet
    $env:PATH = $dotnetRoot + [IO.Path]::PathSeparator + $env:PATH
    $env:DOTNET_ROOT = $dotnetRoot
    $env:DOTNET_ROOT_X64 = $dotnetRoot
    $env:DOTNET_HOST_PATH = $dotnet
    Write-Host "Using .NET SDK $requiredVersion at $dotnet"

    $solution = 'StreamlinkVlcStudio.sln'
    $appProject = 'src\StreamlinkVlcStudio.App.Wpf\StreamlinkVlcStudio.App.Wpf.csproj'
    $testProject = 'tests\StreamlinkVlcStudio.Tests\StreamlinkVlcStudio.Tests.csproj'
    $buildTarget = if ($Task -eq 'Run') { $appProject } else { $solution }

    if (-not $NoBuild -and -not $NoRestore) {
        Invoke-DevelopmentDotNet @('restore', $buildTarget, '--locked-mode')
    }

    if ($Task -eq 'Check') {
        Write-Host 'Checking PowerShell syntax...'
        $syntaxErrors = @()
        Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.ps1 -File -Recurse | ForEach-Object {
            $tokens = $null
            $parseErrors = $null
            [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
            foreach ($parseError in $parseErrors) {
                $syntaxErrors += "$($_.FullName):$($parseError.Extent.StartLineNumber): $($parseError.Message)"
            }
        }
        if ($syntaxErrors.Count -gt 0) { throw ($syntaxErrors -join "`n") }
        Invoke-DevelopmentDotNet @('format', $solution, '--verify-no-changes', '--no-restore')
        & {
            # Existing packaging helpers use optional variables in caller scope.
            Set-StrictMode -Off
            & (Join-Path $PSScriptRoot 'tests\tooling.tests.ps1')
            & (Join-Path $PSScriptRoot 'verify-native-dependencies.ps1')
        }
    }

    if (-not $NoBuild) {
        Invoke-DevelopmentDotNet @('build', $buildTarget, '--configuration', $Configuration, '--no-restore', '-warnaserror')
    } else {
        Write-Host "Reusing existing $Configuration output (-NoBuild); source changes are not rebuilt."
    }

    if ($Task -in @('Test', 'Check')) {
        # Explicit options win over stale filters/modes left in a developer shell.
        $env:SVS_TEST_FILTER = $Filter
        $env:SVS_SKIP_INTERACTIVE_WINDOW_TESTS = if ($Interactive) { 'false' } else { 'true' }
        $env:SVS_TEST_ISOLATED_CHILD = $null
        $env:SVS_EXPECTED_MAX_SKIPS = if ($Interactive -and -not $PSBoundParameters.ContainsKey('ExpectedMaxSkips')) {
            '0'
        } else {
            [string]$ExpectedMaxSkips
        }
        $selection = if ([string]::IsNullOrWhiteSpace($Filter)) { 'all tests' } else { "filter '$Filter'" }
        $mode = if ($Interactive) { 'interactive desktop' } else { 'headless-safe' }
        Write-Host "Running $selection ($mode; maximum skips: $env:SVS_EXPECTED_MAX_SKIPS)."
        Invoke-DevelopmentDotNet @('test', $testProject, '--configuration', $Configuration, '--no-restore', '--no-build')
    } elseif ($Task -eq 'Run') {
        Invoke-DevelopmentDotNet @('run', '--project', $appProject, '--configuration', $Configuration, '--no-restore', '--no-build')
    }

    Write-Host "$Task completed successfully."
} finally {
    foreach ($name in $environmentNames) {
        if ($null -eq $savedEnvironment[$name]) {
            # PowerShell otherwise converts null to an empty string; modern .NET
            # distinguishes an absent environment variable from an empty one.
            [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
    Pop-Location
}
