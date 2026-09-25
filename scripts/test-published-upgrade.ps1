[CmdletBinding()]
param(
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$SourceTag = 'v1.7.0',
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$TargetTag = 'v1.7.2',
    [ValidateSet('source', 'target')][string]$HelperSource = 'source',
    [string]$TargetReleaseDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' -or
    $env:GITHUB_REPOSITORY -ne 'CorontoSiete/streamlink-vlc-studio') {
    throw 'This installation test is restricted to the repository disposable Windows Actions runner.'
}
. "$PSScriptRoot/lib/release-contract.ps1"
$sourceIdentity = Get-StableReleaseIdentity $SourceTag
$targetIdentity = Get-StableReleaseIdentity $TargetTag
if ($targetIdentity.Version -le $sourceIdentity.Version) { throw 'The target must be newer than the source.' }
$root = Join-Path $env:RUNNER_TEMP ('published-upgrade-' + [Guid]::NewGuid().ToString('N'))
$logs = [IO.Path]::GetFullPath('artifacts/upgrade-smoke')
New-Item -ItemType Directory -Path $root, $logs -Force | Out-Null
$installed = Join-Path $env:ProgramFiles 'Streamlink VLC Studio/StreamlinkVlcStudio.exe'
$updateRoot = Join-Path $env:LOCALAPPDATA 'StreamlinkVlcStudio/Updates'
$helper = $null

function Assert-InstalledVersion([string]$Version) {
    if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) { throw "Installed app missing: $installed" }
    Assert-WindowsFileVersion $installed 'Installed application' $Version
}

function Get-VerifiedSetup([string]$Tag, [string]$Destination, [string]$LocalReleaseDirectory = '') {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    if ([string]::IsNullOrWhiteSpace($LocalReleaseDirectory)) {
        & gh release download $Tag --repo $env:GITHUB_REPOSITORY --dir $Destination `
            --pattern StreamlinkVlcStudio-Setup.exe --pattern StreamlinkVlcStudio-release.zip `
            --pattern UPDATE-MANIFEST.json --pattern UPDATE-MANIFEST.sig
        if ($LASTEXITCODE -ne 0) { throw "Could not download $Tag." }
    } else {
        foreach ($name in @('StreamlinkVlcStudio-Setup.exe', 'StreamlinkVlcStudio-release.zip', 'UPDATE-MANIFEST.json', 'UPDATE-MANIFEST.sig')) {
            Copy-Item -LiteralPath (Join-Path $LocalReleaseDirectory $name) -Destination $Destination
        }
    }
    $setup = Join-Path $Destination 'StreamlinkVlcStudio-Setup.exe'
    & "$PSScriptRoot/verify-update-manifest.ps1" `
        -ManifestPath (Join-Path $Destination 'UPDATE-MANIFEST.json') `
        -SignaturePath (Join-Path $Destination 'UPDATE-MANIFEST.sig') `
        -SetupPath $setup -ZipPath (Join-Path $Destination 'StreamlinkVlcStudio-release.zip') `
        -ExpectedTag $Tag -ExpectedVersion $Tag.Substring(1) | Out-Host
    return $setup
}

try {
    if (Test-Path -LiteralPath $installed) { throw 'The runner must start without this application installed.' }
    $sourceSetup = Get-VerifiedSetup $SourceTag (Join-Path $root 'source')
    $targetSetup = Get-VerifiedSetup $TargetTag (Join-Path $root 'target') $TargetReleaseDirectory
    $initialLog = Join-Path $logs 'initial-install.log'
    $initial = Start-Process -FilePath $sourceSetup -WindowStyle Hidden -PassThru `
        -ArgumentList @('/passive', '/norestart', '/log', ('"' + $initialLog + '"'))
    if (-not $initial.WaitForExit(600000)) { throw 'The initial installation timed out.' }
    if ($initial.ExitCode -ne 0) { throw "Initial installation returned $($initial.ExitCode)." }
    Assert-InstalledVersion $sourceIdentity.VersionText
    Write-Host "PASS installed published $SourceTag."

    $dataRoot = Join-Path $env:APPDATA 'StreamlinkVlcStudio'
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $sentinel = Join-Path $dataRoot 'upgrade-smoke-sentinel.txt'
    $sentinelValue = [Guid]::NewGuid().ToString('D')
    [IO.File]::WriteAllText($sentinel, $sentinelValue)

    $operationId = [Guid]::NewGuid()
    $operation = Join-Path $updateRoot ('operations/' + $operationId.ToString('N'))
    $results = Join-Path $updateRoot 'results'
    $updateLogs = Join-Path $updateRoot 'logs'
    New-Item -ItemType Directory -Path $operation, $results, $updateLogs -Force | Out-Null
    $stagedSetup = Join-Path $operation 'StreamlinkVlcStudio-Setup.exe'
    $stagedHelper = Join-Path $operation 'StreamlinkVlcStudio.exe'
    Copy-Item -LiteralPath $targetSetup -Destination $stagedSetup
    if ($HelperSource -eq 'target') {
        # Clients through 1.7.1 use the extraction cache as their helper directory.
        # Exercise the fixed signed helper against the older installed application.
        $targetPayload = Join-Path $root 'target-payload'
        Expand-Archive -LiteralPath (Join-Path $root 'target/StreamlinkVlcStudio-release.zip') -DestinationPath $targetPayload
        $helpers = @(Get-ChildItem -LiteralPath $targetPayload -Filter StreamStudio.exe -File -Recurse)
        if ($helpers.Count -ne 1) { throw 'The signed target ZIP must contain exactly one app executable.' }
        Copy-Item -LiteralPath $helpers[0].FullName -Destination $stagedHelper
        Assert-WindowsFileVersion $stagedHelper 'Published update helper' $targetIdentity.VersionText
    } else {
        Copy-Item -LiteralPath $installed -Destination $stagedHelper
        Assert-WindowsFileVersion $stagedHelper 'Published update helper' $sourceIdentity.VersionText
    }
    $resultPath = Join-Path $results ($operationId.ToString('N') + '.json')
    $updateLog = Join-Path $updateLogs ($operationId.ToString('N') + '.log')
    $verifiedManifest = Get-Content -LiteralPath (Join-Path $root 'target/UPDATE-MANIFEST.json') -Raw | ConvertFrom-Json
    $length = [long]$verifiedManifest.setup.length
    $hash = [string]$verifiedManifest.setup.sha256

    # Model an app that has already exited, so the published helper can install immediately.
    $parent = Start-Process -FilePath $env:ComSpec -ArgumentList '/c exit 0' -WindowStyle Hidden -PassThru
    $parent.WaitForExit()
    $helperArguments = @(
        '--update-helper', $operationId.ToString('D'), '--parent-pid', $parent.Id,
        '--setup', ('"' + $stagedSetup + '"'), '--setup-length', $length, '--setup-sha256', $hash,
        '--target-version', $targetIdentity.VersionText,
        '--install-dir', ('"' + (Split-Path $installed -Parent) + '"'),
        '--result', ('"' + $resultPath + '"'), '--log', ('"' + $updateLog + '"'))
    $helper = Start-Process -FilePath $stagedHelper -WorkingDirectory $operation `
        -ArgumentList $helperArguments -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $logs 'helper-stderr.txt') `
        -RedirectStandardOutput (Join-Path $logs 'helper-stdout.txt')
    # Wait only for the helper; Start-Process -Wait would also wait for the relaunched app.
    if (-not $helper.WaitForExit(600000)) { throw 'The published update helper timed out.' }
    if ($helper.ExitCode -ne 0) { throw "The published update helper returned $($helper.ExitCode)." }
    Assert-InstalledVersion $targetIdentity.VersionText
    if (-not (Test-Path -LiteralPath $sentinel) -or [IO.File]::ReadAllText($sentinel) -cne $sentinelValue) {
        throw 'The upgrade removed or changed existing user data.'
    }
    $restartDeadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $restarted = @(Get-Process -Name StreamlinkVlcStudio -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $installed })
        if ($restarted.Count -gt 0) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $restartDeadline)
    if ($restarted.Count -eq 0) { throw 'The published update helper did not restart the installed application.' }
    Start-Sleep -Seconds 5
    if (@($restarted | Where-Object { -not $_.HasExited }).Count -eq 0) { throw 'The restarted application exited during startup.' }
    Write-Host 'PASS actual update helper installed the target and restarted the app.'
    $repairLog = Join-Path $logs 'quiet-repair.log'
    $repair = Start-Process -FilePath $targetSetup -WindowStyle Hidden -PassThru `
        -ArgumentList @('/repair', '/quiet', '/norestart', '/log', ('"' + $repairLog + '"'))
    if (-not $repair.WaitForExit(600000)) { throw 'Quiet repair timed out.' }
    if ($repair.ExitCode -ne 0) { throw "Quiet repair returned $($repair.ExitCode)." }
    Assert-InstalledVersion $targetIdentity.VersionText
    & "$PSScriptRoot/test-packaged-updater.ps1" -ExecutablePath $installed -ExpectedInstallKind Managed `
        -ProbePath 'tests/StreamlinkVlcStudio.UpdateProbe/bin/Release/net10.0/StreamlinkVlcStudio.UpdateProbe.dll'
    $summary = "PASS: $SourceTag -> $TargetTag using the $HelperSource release helper; exact installed version, retained user data, app restart, and quiet repair verified."
    Write-Host $summary
    [IO.File]::WriteAllText((Join-Path $logs 'result.txt'), $summary)
} finally {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = [DateTime]::Now.AddMinutes(-20) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting') } |
        Select-Object TimeCreated, ProviderName, Id, Message |
        Format-List | Out-File (Join-Path $logs 'application-events.txt') -Encoding utf8
    if (Test-Path -LiteralPath $updateRoot) {
        Get-ChildItem -LiteralPath $updateRoot -Filter *.log -File -Recurse |
            Copy-Item -Destination $logs -Force
        Get-ChildItem -LiteralPath (Join-Path $updateRoot 'results') -Filter *.json -File -ErrorAction SilentlyContinue |
            Copy-Item -Destination $logs -Force
    }
    foreach ($process in @(Get-Process -Name StreamlinkVlcStudio -ErrorAction SilentlyContinue)) {
        $processPath = $process.Path
        if (-not [string]::IsNullOrWhiteSpace($processPath) -and
            ($processPath -eq $installed -or $processPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
            $processPath.StartsWith($updateRoot, [StringComparison]::OrdinalIgnoreCase))) {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        }
    }
}
