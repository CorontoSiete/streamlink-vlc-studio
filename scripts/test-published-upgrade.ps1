[CmdletBinding()]
param(
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$SourceTag = 'v1.8.3',
    [ValidatePattern('^v\d+\.\d+\.\d+$')][string]$TargetTag = 'v1.8.4',
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
. "$PSScriptRoot/lib/runtime-dependencies.ps1"
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

function Assert-InstalledRuntimeDependencies([string]$Phase) {
    $metadataPath = Join-Path (Split-Path $installed -Parent) 'release-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $capability = $metadata.PSObject.Properties['dependencyVerificationProtocol']
    if ($null -eq $capability -or [int]$capability.Value -ne 1) {
        if (-not [string]::IsNullOrWhiteSpace($TargetReleaseDirectory)) {
            throw 'The new target payload does not declare native dependency verification support.'
        }
        Write-Host "Legacy published target has no dependency maintenance command ($Phase)."
        return
    }
    $streamlink = Join-Path ([StreamStudio.Installation.WindowsDependencyProbe]::GetProgramFiles64Directory()) 'Streamlink\bin\streamlink.exe'
    $vlc = [StreamStudio.Installation.WindowsDependencyProbe]::FindMachineVlcDirectory()
    $health = [StreamStudio.Installation.WindowsDependencyProbe]::VerifyApplication($installed, $streamlink, $vlc)
    $health | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logs ($Phase + '-dependencies.json')) -Encoding utf8
    if ($health.ExitCode -ne 0 -or $health.Truncated) {
        throw "The installed target cannot use its runtime dependencies after $Phase. $($health.Error)"
    }
    Write-Host "PASS installed app initialized Streamlink, VLC, WebView2, Skia, and HarfBuzz after $Phase."
}

function Invoke-PublishedUpdaterProbe([string]$Phase, [switch]$Download) {
    $reportPath = Join-Path $logs ($Phase + '.json')
    $probeEnvironment = @{
        DOTNET_STARTUP_HOOKS = (Resolve-Path -LiteralPath 'tests/StreamlinkVlcStudio.UpdateProbe/bin/Release/net10.0/StreamlinkVlcStudio.UpdateProbe.dll').Path
        SVS_UPDATE_PROBE_RESTART_EXECUTABLE = $installed
        SVS_UPDATE_PROBE_RESTART_REPORT = $reportPath
        SVS_UPDATE_PROBE_EXPECTED_TAG = $TargetTag
        SVS_UPDATE_PROBE_DOWNLOAD = $Download.IsPresent.ToString().ToLowerInvariant()
    }
    $probe = Start-Process -FilePath $installed -WorkingDirectory (Split-Path $installed -Parent) `
        -WindowStyle Hidden -PassThru -Environment $probeEnvironment `
        -RedirectStandardError (Join-Path $logs ($Phase + '-stderr.txt')) `
        -RedirectStandardOutput (Join-Path $logs ($Phase + '-stdout.txt'))
    try {
        if (-not $probe.WaitForExit(660000)) { $probe.Kill(); throw "The published updater probe timed out ($Phase)." }
        if ($probe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath)) {
            $probeError = Get-Content -LiteralPath (Join-Path $logs ($Phase + '-stderr.txt')) -Raw
            throw "The published updater probe failed ($Phase), exit $($probe.ExitCode). $probeError"
        }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if ($report.ProcessPath -ine $installed -or $report.InstallKind -cne 'Managed' -or
            $report.ConfiguredDirectory -ine (Split-Path $installed -Parent)) {
            throw 'The published check did not run inside the installed managed application.'
        }
        Write-Host "PASS published app's real signed GitHub updater: $Phase ($($report.LiveUpdate.Phase))."
        return $report
    } finally { $probe.Dispose() }
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

    $sentinels = @(foreach ($dataDirectory in @('StreamStudio', 'StreamlinkVlcStudio')) {
        $dataRoot = Join-Path $env:APPDATA $dataDirectory
        New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
        $sentinel = Join-Path $dataRoot 'upgrade-smoke-sentinel.txt'
        $sentinelValue = [Guid]::NewGuid().ToString('D')
        [IO.File]::WriteAllText($sentinel, $sentinelValue)
        [pscustomobject]@{ Path = $sentinel; Value = $sentinelValue }
    })

    $publishedDownload = $null
    if ([string]::IsNullOrWhiteSpace($TargetReleaseDirectory) -and $HelperSource -eq 'source') {
        $latestTag = & gh api "repos/$env:GITHUB_REPOSITORY/releases/latest" --jq '.tag_name'
        if ($LASTEXITCODE -ne 0) { throw 'Could not determine the current published updater target.' }
        if ($latestTag.Trim() -ceq $TargetTag) {
            # Exercise the source release's actual CheckAsync and DownloadAsync
            # over HTTPS. Install the exact package it staged below.
            $publishedDownload = Invoke-PublishedUpdaterProbe 'published-update-download' -Download
        } else {
            Write-Host "Historical target $TargetTag uses installer/helper verification; the updater currently targets $latestTag."
        }
    }
    $operationId = if ($null -ne $publishedDownload) {
        [Guid]::Parse([string]$publishedDownload.LiveUpdate.PreparedUpdate.OperationId)
    } else { [Guid]::NewGuid() }
    $operation = Join-Path $updateRoot ('operations/' + $operationId.ToString('N'))
    $results = Join-Path $updateRoot 'results'
    $updateLogs = Join-Path $updateRoot 'logs'
    New-Item -ItemType Directory -Path $operation, $results, $updateLogs -Force | Out-Null
    $stagedSetup = Join-Path $operation 'StreamlinkVlcStudio-Setup.exe'
    $stagedHelper = Join-Path $operation 'StreamlinkVlcStudio.exe'
    if ($null -ne $publishedDownload) {
        $prepared = $publishedDownload.LiveUpdate.PreparedUpdate
        if ($prepared.OperationDirectory -ine $operation -or $prepared.SetupPath -ine $stagedSetup -or
            $prepared.HelperPath -ine $stagedHelper -or -not (Test-Path -LiteralPath $stagedSetup) -or
            -not (Test-Path -LiteralPath $stagedHelper)) {
            throw 'The installed updater did not stage the expected managed operation and helper.'
        }
    } else { Copy-Item -LiteralPath $targetSetup -Destination $stagedSetup }
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
        if ($null -eq $publishedDownload) { Copy-Item -LiteralPath $installed -Destination $stagedHelper }
        Assert-WindowsFileVersion $stagedHelper 'Published update helper' $sourceIdentity.VersionText
    }
    $resultPath = Join-Path $results ($operationId.ToString('N') + '.json')
    $updateLog = Join-Path $updateLogs ($operationId.ToString('N') + '.log')
    $verifiedManifest = Get-Content -LiteralPath (Join-Path $root 'target/UPDATE-MANIFEST.json') -Raw | ConvertFrom-Json
    $length = [long]$verifiedManifest.setup.length
    $hash = [string]$verifiedManifest.setup.sha256
    if ((Get-Item -LiteralPath $stagedSetup).Length -ne $length -or
        (Get-FileHash -LiteralPath $stagedSetup -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) {
        throw 'The staged updater download does not match the separately verified published manifest.'
    }

    # Model an app that has already exited, so the published helper can install immediately.
    $parent = Start-Process -FilePath $env:ComSpec -ArgumentList '/c exit 0' -WindowStyle Hidden -PassThru
    $parent.WaitForExit()
    $helperArguments = @(
        '--update-helper', $operationId.ToString('D'), '--parent-pid', $parent.Id,
        '--setup', ('"' + $stagedSetup + '"'), '--setup-length', $length, '--setup-sha256', $hash,
        '--target-version', $targetIdentity.VersionText,
        '--install-dir', ('"' + (Split-Path $installed -Parent) + '"'),
        '--result', ('"' + $resultPath + '"'), '--log', ('"' + $updateLog + '"'))
    # Actions has no interactive desktop. Inspect the actual relaunched executable
    # before WPF opens its custom chrome, then exit so repair can replace its files.
    $restartReportPath = Join-Path $logs 'restarted-updater.json'
    $probePath = (Resolve-Path -LiteralPath 'tests/StreamlinkVlcStudio.UpdateProbe/bin/Release/net10.0/StreamlinkVlcStudio.UpdateProbe.dll').Path
    $probeEnvironment = @{
        DOTNET_STARTUP_HOOKS = $probePath
        SVS_UPDATE_PROBE_RESTART_EXECUTABLE = $installed
        SVS_UPDATE_PROBE_RESTART_REPORT = $restartReportPath
    }
    $helper = Start-Process -FilePath $stagedHelper -WorkingDirectory $operation `
        -ArgumentList $helperArguments -WindowStyle Hidden -PassThru `
        -Environment $probeEnvironment `
        -RedirectStandardError (Join-Path $logs 'helper-stderr.txt') `
        -RedirectStandardOutput (Join-Path $logs 'helper-stdout.txt')
    # Wait only for the helper; Start-Process -Wait would also wait for the relaunched app.
    if (-not $helper.WaitForExit(600000)) { throw 'The published update helper timed out.' }
    if ($helper.ExitCode -ne 0) { throw "The published update helper returned $($helper.ExitCode)." }
    Assert-InstalledVersion $targetIdentity.VersionText
    foreach ($sentinelRecord in $sentinels) {
        if (-not (Test-Path -LiteralPath $sentinelRecord.Path) -or
            [IO.File]::ReadAllText($sentinelRecord.Path) -cne $sentinelRecord.Value) {
            throw "The upgrade removed or changed existing user data: $($sentinelRecord.Path)"
        }
    }
    $restartDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $restartReportPath)) {
        if ([DateTime]::UtcNow -ge $restartDeadline) { throw 'The helper did not launch the installed application with its updater probe.' }
        Start-Sleep -Milliseconds 200
    }
    $restartReport = Get-Content -LiteralPath $restartReportPath -Raw | ConvertFrom-Json
    if ($restartReport.ProcessPath -ine $installed -or
        $restartReport.ConfiguredDirectory -ine (Split-Path $installed -Parent) -or
        $restartReport.InstallKind -cne 'Managed') { throw 'The relaunched application did not configure its managed updater correctly.' }
    foreach ($process in @(Get-Process -Name StreamlinkVlcStudio -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installed })) {
        if (-not $process.WaitForExit(10000)) { throw 'The restarted probe did not exit before repair.' }
    }
    Write-Host 'PASS actual update helper installed the target and relaunched its managed updater (desktop UI is outside this headless test).'
    Assert-InstalledRuntimeDependencies 'upgrade'
    if ($null -ne $publishedDownload) {
        Invoke-PublishedUpdaterProbe 'published-current-version-check' | Out-Host
    }
    $installedMetadata = Get-Content -LiteralPath (Join-Path (Split-Path $installed -Parent) 'release-metadata.json') -Raw | ConvertFrom-Json
    $dependencyCapability = $installedMetadata.PSObject.Properties['dependencyVerificationProtocol']
    if ($null -ne $dependencyCapability -and [int]$dependencyCapability.Value -eq 1) {
        # Exercise the exact /passive invocation used by older update helpers on
        # a registered target bundle. It must repair missing MSI payload files.
        $overlay = Join-Path (Split-Path $installed -Parent) 'vlc-overlay\build\libmyoverlay_plugin.dll'
        $installPrefix = [IO.Path]::GetFullPath((Split-Path $installed -Parent)).TrimEnd('\') + '\'
        $overlay = [IO.Path]::GetFullPath($overlay)
        if (-not $overlay.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $overlay -PathType Leaf)) {
            throw 'The passive repair fixture is not an installed app payload file.'
        }
        $overlayHash = (Get-FileHash -LiteralPath $overlay -Algorithm SHA256).Hash
        Remove-Item -LiteralPath $overlay -Force
        $passiveRepairLog = Join-Path $logs 'passive-current-version-repair.log'
        $passiveRepair = Start-Process -FilePath $targetSetup -WindowStyle Hidden -PassThru `
            -ArgumentList @('/passive', '/norestart', '/log', ('"' + $passiveRepairLog + '"'))
        if (-not $passiveRepair.WaitForExit(600000)) { throw 'Passive same-version repair timed out.' }
        if ($passiveRepair.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $overlay -PathType Leaf) -or
            (Get-FileHash -LiteralPath $overlay -Algorithm SHA256).Hash -cne $overlayHash) {
            throw "Passive same-version Setup did not restore the exact missing app payload; exit $($passiveRepair.ExitCode)."
        }
        Assert-InstalledRuntimeDependencies 'passive-current-version-repair'
        Write-Host 'PASS the legacy updater invocation repaired a missing MSI payload in the registered target version.'
    }
    $repairLog = Join-Path $logs 'quiet-repair.log'
    $repair = Start-Process -FilePath $targetSetup -WindowStyle Hidden -PassThru `
        -ArgumentList @('/repair', '/quiet', '/norestart', '/log', ('"' + $repairLog + '"'))
    if (-not $repair.WaitForExit(600000)) { throw 'Quiet repair timed out.' }
    if ($repair.ExitCode -notin @(0, 3010)) { throw "Quiet repair returned $($repair.ExitCode)." }
    Write-Host "PASS quiet repair; restart required: $($repair.ExitCode -eq 3010)."
    Assert-InstalledVersion $targetIdentity.VersionText
    if ($repair.ExitCode -eq 0) { Assert-InstalledRuntimeDependencies 'repair' }
    & "$PSScriptRoot/test-packaged-updater.ps1" -ExecutablePath $installed -ExpectedInstallKind Managed `
        -ProbePath 'tests/StreamlinkVlcStudio.UpdateProbe/bin/Release/net10.0/StreamlinkVlcStudio.UpdateProbe.dll'
    $summary = "PASS: $SourceTag -> $TargetTag using the $HelperSource release helper; exact installed version, retained user data, app restart, and quiet repair verified."
    if ($null -ne $publishedDownload) {
        $summary += ' The installed source app detected and downloaded the signed GitHub release; the upgraded app confirmed it was current.'
    }
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
