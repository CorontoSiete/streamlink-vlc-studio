[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$entryPoint = Join-Path $repoRoot 'scripts\dev.ps1'
. (Join-Path $repoRoot 'scripts\lib\common.ps1')
$exitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$hadExitCode = $null -ne $exitCodeVariable
$savedExitCode = if ($hadExitCode) { $exitCodeVariable.Value } else { $null }

function Assert-Development([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-DevelopmentFailure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw "Expected failure matching '$Pattern'."
}

$fixtureParent = [IO.Path]::GetTempPath()
$fixtureRoot = Join-Path $fixtureParent ('StreamStudio-development-tests-' + [Guid]::NewGuid().ToString('N'))
$environmentNames = @(
    'PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH', 'DOTNET_CLI_HOME',
    'DOTNET_NOLOGO', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_CLI_TELEMETRY_OPTOUT',
    'TEMP', 'TMP', 'SVS_TEST_FILTER', 'SVS_SKIP_INTERACTIVE_WINDOW_TESTS',
    'SVS_EXPECTED_MAX_SKIPS', 'SVS_TEST_ISOLATED_CHILD',
    'STUDIO_DEV_TEST_LOG', 'STUDIO_DEV_TEST_VERSION', 'STUDIO_DEV_TEST_FAIL'
)
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-DevelopmentScenario {
    param([hashtable]$Options)

    [IO.File]::WriteAllText($env:STUDIO_DEV_TEST_LOG, '')
    $before = @{}
    foreach ($name in $environmentNames) {
        $before[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    $previousLocation = (Get-Location).Path
    try {
        & $entryPoint @Options -DotNetPath '.\fake sdk\dotnet.ps1' *> (Join-Path $fixtureRoot 'output.log')
    } finally {
        Assert-Development ((Get-Location).Path -ceq $previousLocation) 'Developer command changed the caller directory.'
        foreach ($name in $environmentNames) {
            Assert-Development ([Environment]::GetEnvironmentVariable($name, 'Process') -ceq $before[$name]) "Developer command leaked $name."
        }
    }
    @(Get-Content -LiteralPath $env:STUDIO_DEV_TEST_LOG | ForEach-Object { $_ | ConvertFrom-Json })
}

New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'fake sdk') -Force | Out-Null
Push-Location -LiteralPath $fixtureRoot
try {
    # A host shim records exactly what a child process receives without a build,
    # desktop window, network access, or dependency on an installed SDK.
    $shim = @'
if ($args[0] -eq '--version') {
    $global:LASTEXITCODE = 0
    Write-Output $env:STUDIO_DEV_TEST_VERSION
    return
}
$record = [ordered]@{
    Arguments = @($args)
    Directory = (Get-Location).Path
    Path = $env:PATH
    DotNetRoot = $env:DOTNET_ROOT
    DotNetRootX64 = $env:DOTNET_ROOT_X64
    HostPath = $env:DOTNET_HOST_PATH
    Filter = $env:SVS_TEST_FILTER
    SkipInteractive = $env:SVS_SKIP_INTERACTIVE_WINDOW_TESTS
    MaximumSkips = $env:SVS_EXPECTED_MAX_SKIPS
    IsolatedChild = $env:SVS_TEST_ISOLATED_CHILD
}
[IO.File]::AppendAllText($env:STUDIO_DEV_TEST_LOG, ($record | ConvertTo-Json -Compress) + [Environment]::NewLine)
$global:LASTEXITCODE = if ($args[0] -eq $env:STUDIO_DEV_TEST_FAIL) { 19 } else { 0 }
'@
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'fake sdk\dotnet.ps1'), $shim)
    $env:STUDIO_DEV_TEST_LOG = Join-Path $fixtureRoot 'calls.jsonl'
    $env:STUDIO_DEV_TEST_VERSION = [string](Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $env:STUDIO_DEV_TEST_FAIL = ''
    $env:SVS_TEST_FILTER = 'stale shell filter'
    $env:SVS_SKIP_INTERACTIVE_WINDOW_TESTS = 'false'
    $env:SVS_EXPECTED_MAX_SKIPS = '999'
    $env:SVS_TEST_ISOLATED_CHILD = 'true'

    $filter = 'stream open workflow: spaces & literal $text'
    $calls = @(Invoke-DevelopmentScenario @{ Task = 'Test'; Filter = $filter })
    Assert-Development (($calls.Arguments | Where-Object { $_ -in @('restore', 'build', 'test') }) -join ',' -ceq 'restore,build,test') 'Default test run did not restore, build, then test.'
    Assert-Development ($calls[0].Arguments -contains '--locked-mode') 'Restore did not use locked dependencies.'
    Assert-Development ($calls[1].Arguments -contains '-warnaserror') 'Build allowed warnings.'
    Assert-Development ($calls[2].Arguments -contains '--no-build' -and $calls[2].Arguments -contains '--no-restore') 'Test could rebuild shared WPF output.'
    $child = $calls[2]
    $expectedRoot = Join-Path $fixtureRoot 'fake sdk'
    Assert-Development ($child.Directory -ceq $repoRoot) 'Child command did not run from the repository.'
    Assert-Development ($child.Path.StartsWith($expectedRoot + [IO.Path]::PathSeparator)) 'Child PATH did not prefer the selected SDK.'
    Assert-Development ($child.DotNetRoot -ceq $expectedRoot -and $child.DotNetRootX64 -ceq $expectedRoot) 'Child SDK roots differ from the selected host.'
    Assert-Development ($child.HostPath -ceq (Join-Path $expectedRoot 'dotnet.ps1')) 'Child host override differs from the selected SDK.'
    Assert-Development ($child.Filter -ceq $filter -and $child.SkipInteractive -ceq 'true' -and $child.MaximumSkips -ceq '246') 'Explicit test selection or headless defaults were lost.'
    Assert-Development ([string]::IsNullOrEmpty($child.IsolatedChild)) 'Stale child marker disabled test isolation.'
    Write-Host 'PASS development: SDK propagation, focused tests, build ordering, and shell restoration'

    $calls = @(Invoke-DevelopmentScenario @{ Task = 'Test'; NoBuild = $true; Interactive = $true; Configuration = 'Debug' })
    Assert-Development ($calls.Count -eq 1 -and $calls[0].Arguments[0] -ceq 'test') 'NoBuild performed restore/build work.'
    Assert-Development ($calls[0].Arguments -contains 'Debug') 'Requested configuration was lost.'
    Assert-Development ([string]::IsNullOrEmpty($calls[0].Filter)) 'Unfiltered test run inherited an old filter.'
    Assert-Development ($calls[0].SkipInteractive -ceq 'false' -and $calls[0].MaximumSkips -ceq '0') 'Interactive run permitted implicit skips.'
    Write-Host 'PASS development: fast reruns and explicit interactive mode'

    $calls = @(Invoke-DevelopmentScenario @{ Task = 'Build'; NoRestore = $true })
    Assert-Development ($calls.Count -eq 1 -and $calls[0].Arguments[0] -ceq 'build') 'NoRestore performed a restore or ran tests.'
    $calls = @(Invoke-DevelopmentScenario @{ Task = 'Run' })
    Assert-Development ($calls.Count -eq 3 -and $calls[0].Arguments[1] -like '*App.Wpf.csproj' -and $calls[2].Arguments[0] -ceq 'run') 'Run did not build and launch the app project.'
    Write-Host 'PASS development: build-only and application launch tasks'

    foreach ($stage in @('restore', 'build', 'test')) {
        $env:STUDIO_DEV_TEST_FAIL = $stage
        Assert-DevelopmentFailure { Invoke-DevelopmentScenario @{ Task = 'Test' } } "dotnet $stage failed with exit code 19"
        $failedCalls = @(Get-Content -LiteralPath $env:STUDIO_DEV_TEST_LOG | ForEach-Object { $_ | ConvertFrom-Json })
        Assert-Development ($failedCalls[-1].Arguments[0] -ceq $stage) "Work continued after $stage failed."
    }
    $env:STUDIO_DEV_TEST_FAIL = ''
    $env:STUDIO_DEV_TEST_VERSION = '0.0.0'
    Assert-DevelopmentFailure { Invoke-DevelopmentScenario @{ Task = 'Test' } } 'Could not find .NET SDK'
    Assert-Development ((Get-Item -LiteralPath $env:STUDIO_DEV_TEST_LOG).Length -eq 0) 'Wrong SDK was used for a build.'
    Assert-DevelopmentFailure { Invoke-DevelopmentScenario @{ Task = 'Check'; Filter = 'partial' } } 'Check always runs the complete suite'
    Assert-DevelopmentFailure { Invoke-DevelopmentScenario @{ Task = 'Check'; NoBuild = $true } } 'NoBuild is only supported'
    Write-Host 'PASS development: failures stop work, restore shell state, and reject incomplete checks'

    $env:DOTNET_HOST_PATH = Join-Path $fixtureRoot 'fake sdk\dotnet.ps1'
    $env:STUDIO_DEV_TEST_FAIL = 'publish'
    [IO.File]::WriteAllText($env:STUDIO_DEV_TEST_LOG, '')
    Assert-DevelopmentFailure {
        & (Join-Path $repoRoot 'scripts\build-uninstaller.ps1') -OutputPath (Join-Path $fixtureRoot 'Uninstall.exe') -Quiet
    } 'NativeAOT maintenance publish failed with exit code 19'
    $publish = Get-Content -LiteralPath $env:STUDIO_DEV_TEST_LOG | ConvertFrom-Json
    Assert-Development ($publish.Arguments[0] -ceq 'publish') 'Uninstaller ignored the selected SDK host.'

    # Load only the installer resolver, without downloading or running setup.
    $tokens = $null
    $parseErrors = $null
    $installerAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repoRoot 'scripts\build-installer.ps1'), [ref]$tokens, [ref]$parseErrors)
    $resolver = $installerAst.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-DotNetTool'
    }, $false)
    & {
        $repoRoot = $fixtureRoot
        New-Item -ItemType Directory -Path (Join-Path $repoRoot '.dotnet-sdk') | Out-Null
        [IO.File]::WriteAllText((Join-Path $repoRoot '.dotnet-sdk\dotnet.exe'), 'unused older SDK')
        . ([scriptblock]::Create($resolver.Extent.Text))
        Assert-Development ((Resolve-DotNetTool) -ceq $env:DOTNET_HOST_PATH) 'Installer preferred an older repository SDK over the selected host.'
        $env:DOTNET_HOST_PATH = Join-Path $fixtureRoot 'missing.exe'
        Assert-DevelopmentFailure { Resolve-DotNetTool } 'selected .NET host does not exist'
    }
    Write-Host 'PASS development: packaging scripts honor the selected SDK host'
} finally {
    # Expected native-command failures must not fail the caller's CI step.
    if ($hadExitCode) {
        $global:LASTEXITCODE = $savedExitCode
    } else {
        Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
    Pop-Location
    foreach ($name in $environmentNames) {
        if ($null -eq $savedEnvironment[$name]) {
            [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
    Assert-UnderDirectory -ChildPath $fixtureRoot -ParentPath $fixtureParent
    Remove-DirectoryTreeSafely $fixtureRoot
}
