[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'scripts\lib\common.ps1')
. (Join-Path $repoRoot 'scripts\lib\install-state.ps1')

function Assert-Lifecycle([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

# Load only filesystem helpers. Network, dependency installation, registry,
# shortcuts, settings, and process termination are never invoked by these fixtures.
$tokens = $null
$parseErrors = $null
$installer = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'scripts\install.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert-Lifecycle ($parseErrors.Count -eq 0) 'Installer PowerShell syntax is invalid.'
$functionNames = @('Install-AppPayloadAtomically', 'Install-AppPayloadCore', 'Copy-DirectoryContents', 'Remove-InstallWorkingDirectory', 'Remove-SearchIconCache')
foreach ($definition in $installer.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $functionNames
        }, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
function Assert-AppPayload([string]$Path) {
    Assert-Lifecycle (Test-Path -LiteralPath (Join-Path $Path 'StreamStudio.exe') -PathType Leaf) 'Fixture payload is missing its executable.'
}
function Write-Detail([string]$Message) { }
function Stop-AppIfNeeded {
    # Model a final user-file save during application shutdown.
    [IO.File]::WriteAllText((Join-Path $InstallDir 'notes.txt'), 'saved during shutdown')
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('StreamStudio-install-lifecycle-' + [Guid]::NewGuid().ToString('N'))
$failures = [Collections.Generic.List[string]]::new()
function Invoke-LifecycleTest([string]$Name, [scriptblock]$Test) {
    try {
        & $Test
        Write-Host "PASS installer lifecycle: $Name"
    } catch {
        $failures.Add("${Name}: $($_.Exception.Message)")
        Write-Host "FAIL installer lifecycle: ${Name}: $($_.Exception.Message)"
    }
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Invoke-LifecycleTest 'installation lease matches the native uninstaller and normalizes path aliases' {
        $expected = 'Local\StreamStudio.Installation.25549A2C6E0AAF49EA87B4987F6EAC72914EA313C86CB5E31C88CBCFE33BFB02'
        foreach ($directory in @('C:\Stream Studio\Install\', 'C:/stream studio/install/../install')) {
            Assert-Lifecycle ((Get-InstallOperationMutexName $directory) -ceq $expected) 'The installer and native uninstaller mutex identities differ.'
        }
        $outer = Enter-InstallOperation $testRoot
        try {
            $nested = Enter-InstallOperation $testRoot
            Exit-InstallOperation $nested
        } finally {
            Exit-InstallOperation $outer
        }
    }

    Invoke-LifecycleTest 'a competing process prevents replacement and allows retry after releasing its lease' {
        $InstallDir = Join-Path $testRoot 'concurrent-install'
        $source = Join-Path $testRoot 'concurrent-payload'
        [IO.Directory]::CreateDirectory($InstallDir) | Out-Null
        [IO.Directory]::CreateDirectory($source) | Out-Null
        [IO.File]::WriteAllText((Join-Path $InstallDir 'StreamStudio.exe'), 'old app')
        [IO.File]::WriteAllText((Join-Path $source 'StreamStudio.exe'), 'new app')
        Write-InstallOwnershipState -Directory $InstallDir | Out-Null
        Write-InstallOwnershipState -Directory $source | Out-Null
        [IO.File]::WriteAllText((Join-Path $InstallDir 'notes.txt'), 'before shutdown')
        $readyPath = Join-Path $testRoot 'mutex-holder-ready'
        $releaseName = 'Local\StreamStudio.Install.Test.' + [Guid]::NewGuid().ToString('N')
        $release = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $releaseName)
        $command = @'
$ErrorActionPreference = 'Stop'
$mutex = [Threading.Mutex]::new($false, $env:SVS_LIFECYCLE_MUTEX_NAME)
$release = [Threading.EventWaitHandle]::OpenExisting($env:SVS_LIFECYCLE_RELEASE_EVENT)
try {
    if (-not $mutex.WaitOne(0)) { throw 'Could not acquire fixture mutex.' }
    try {
        [IO.File]::WriteAllText($env:SVS_LIFECYCLE_READY_PATH, 'ready')
        if (-not $release.WaitOne(10000)) { throw 'Fixture was not released.' }
    } finally { $mutex.ReleaseMutex() }
} finally { $release.Dispose(); $mutex.Dispose() }
'@
        $info = [Diagnostics.ProcessStartInfo]::new()
        $info.FileName = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
        $info.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $info.EnvironmentVariables['SVS_LIFECYCLE_MUTEX_NAME'] = Get-InstallOperationMutexName $InstallDir
        $info.EnvironmentVariables['SVS_LIFECYCLE_RELEASE_EVENT'] = $releaseName
        $info.EnvironmentVariables['SVS_LIFECYCLE_READY_PATH'] = $readyPath
        $child = [Diagnostics.Process]::Start($info)
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            while (-not (Test-Path -LiteralPath $readyPath) -and -not $child.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 20
            }
            Assert-Lifecycle (Test-Path -LiteralPath $readyPath) 'The competing process did not acquire the installation lease.'
            $blocked = $false
            try { Install-AppPayloadAtomically $source 'concurrent fixture' | Out-Null } catch {
                $blocked = $true
                Assert-Lifecycle ($_.Exception.Message -match 'Another installation or uninstall') 'A competing operation was not identified.'
            }
            Assert-Lifecycle $blocked 'A competing process did not prevent replacement.'
            Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $InstallDir 'StreamStudio.exe')) -ceq 'old app') 'The installed app changed while another operation owned it.'
            Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $InstallDir 'notes.txt')) -ceq 'before shutdown') 'Application shutdown ran before the lease was acquired.'
            Assert-Lifecycle (@(Get-ChildItem -LiteralPath $testRoot -Force | Where-Object { $_.Name -match '^\.concurrent-install\.(stage|backup)-' }).Count -eq 0) 'A blocked operation left transaction directories.'
            $release.Set() | Out-Null
            Assert-Lifecycle ($child.WaitForExit(5000) -and $child.ExitCode -eq 0) 'The competing process did not release its lease.'
            Install-AppPayloadAtomically $source 'retry fixture' | Out-Null
            Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $InstallDir 'StreamStudio.exe')) -ceq 'new app') 'The installer could not retry after the competing operation finished.'
        } finally {
            $release.Set() | Out-Null
            if (-not $child.WaitForExit(5000)) { $child.Kill(); $child.WaitForExit(2000) | Out-Null }
            $child.Dispose()
            $release.Dispose()
        }
    }

    Invoke-LifecycleTest 'search icon cleanup preserves unrelated apps at every display scale' {
        $localApplicationData = Join-Path $testRoot 'search-cache'
        $cache = Join-Path $localApplicationData 'Packages\Microsoft.Windows.Search_cw5n1h2txyewy\LocalState\AppIconCache'
        foreach ($scale in @('100', '150')) {
            $directory = Join-Path $cache $scale
            [IO.Directory]::CreateDirectory($directory) | Out-Null
            [IO.File]::WriteAllText((Join-Path $directory '{6D809377-6AF0-444B-8957-A3773F02200E}_Streamlink VLC Studio_StreamlinkVlcStudio_exe'), 'old icon')
            [IO.File]::WriteAllText((Join-Path $directory 'C__Users_test_AppData_Local_Programs_StreamStudio_StreamStudio_exe'), 'old icon')
            [IO.File]::WriteAllText((Join-Path $directory 'OtherApp_exe'), 'unrelated icon')
            [IO.File]::WriteAllText((Join-Path $directory 'StreamStudioTools_exe'), 'unrelated icon')
        }
        Assert-Lifecycle ((Remove-SearchIconCache $localApplicationData) -eq 4) 'The old Stream Studio search bitmaps remained.'
        foreach ($scale in @('100', '150')) {
            $remaining = @(Get-ChildItem -LiteralPath (Join-Path $cache $scale) -File)
            Assert-Lifecycle ($remaining.Count -eq 2) 'Unrelated application icons were removed.'
            foreach ($file in $remaining) {
                Assert-Lifecycle ([IO.File]::ReadAllText($file.FullName) -ceq 'unrelated icon') 'An unrelated icon changed.'
            }
        }
        Assert-Lifecycle ((Remove-SearchIconCache $localApplicationData) -eq 0) 'Icon cleanup is not repeatable.'
    }

    Invoke-LifecycleTest 'upgrade preserves empty user directories and removes obsolete managed directories' {
        $existing = Join-Path $testRoot 'existing'
        $stage = Join-Path $testRoot 'stage'
        foreach ($directory in @($existing, $stage, (Join-Path $existing 'old\runtime'))) {
            [IO.Directory]::CreateDirectory($directory) | Out-Null
        }
        [IO.File]::WriteAllText((Join-Path $existing 'old\runtime\managed.dll'), 'old payload')
        [IO.File]::WriteAllText((Join-Path $existing 'converted-file'), 'old managed file')
        Write-InstallOwnershipState -Directory $existing | Out-Null
        [IO.File]::Delete((Join-Path $existing 'converted-file'))
        [IO.Directory]::CreateDirectory((Join-Path $existing 'converted-file')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $existing 'user\empty')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $existing 'old\user-empty')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $existing 'notes.txt'), 'user file')
        Copy-UnmanagedInstallFiles -ExistingDirectory $existing -StagingDirectory $stage -ExistingState (Read-InstallOwnershipState $existing)
        Assert-Lifecycle (Test-Path -LiteralPath (Join-Path $stage 'user\empty') -PathType Container) 'An empty user directory was lost.'
        Assert-Lifecycle (Test-Path -LiteralPath (Join-Path $stage 'old\user-empty') -PathType Container) 'A user directory below a managed parent was lost.'
        Assert-Lifecycle (Test-Path -LiteralPath (Join-Path $stage 'converted-file') -PathType Container) 'A user directory replacing an old managed file was lost.'
        Assert-Lifecycle (-not (Test-Path -LiteralPath (Join-Path $stage 'old\runtime'))) 'An obsolete managed directory survived.'
        Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $stage 'notes.txt')) -ceq 'user file') 'An unmanaged file changed.'
    }

    Invoke-LifecycleTest 'upgrade snapshots user files after shutdown and leaves no transaction directories' {
        $InstallDir = Join-Path $testRoot 'installed'
        $source = Join-Path $testRoot 'payload'
        [IO.Directory]::CreateDirectory($InstallDir) | Out-Null
        [IO.Directory]::CreateDirectory($source) | Out-Null
        [IO.File]::WriteAllText((Join-Path $InstallDir 'StreamStudio.exe'), 'old app')
        [IO.File]::WriteAllText((Join-Path $InstallDir 'obsolete.dll'), 'old runtime')
        Write-InstallOwnershipState -Directory $InstallDir | Out-Null
        [IO.File]::WriteAllText((Join-Path $InstallDir 'notes.txt'), 'before shutdown')
        [IO.File]::WriteAllText((Join-Path $source 'StreamStudio.exe'), 'new app')
        Write-InstallOwnershipState -Directory $source | Out-Null
        Install-AppPayloadAtomically -PayloadRoot $source -SourceDescription 'local fixture' | Out-Null
        Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $InstallDir 'notes.txt')) -ceq 'saved during shutdown') 'The upgrade lost the final saved user file.'
        Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $InstallDir 'StreamStudio.exe')) -ceq 'new app') 'The new application was not installed.'
        Assert-Lifecycle (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'obsolete.dll'))) 'An obsolete managed file survived.'
        Assert-Lifecycle (-not (Read-InstallOwnershipState $InstallDir).Paths.Contains('notes.txt')) 'The installer claimed ownership of a user file.'
        Assert-Lifecycle (@(Get-ChildItem -LiteralPath $testRoot -Force | Where-Object { $_.Name -match '^\.installed\.(stage|backup)-' }).Count -eq 0) 'Transaction directories remained after success.'
    }

    Invoke-LifecycleTest 'a payload conflict preserves the existing installation and its user directories' {
        $InstallDir = Join-Path $testRoot 'conflict-install'
        $source = Join-Path $testRoot 'conflict-payload'
        [IO.Directory]::CreateDirectory($InstallDir) | Out-Null
        [IO.Directory]::CreateDirectory($source) | Out-Null
        [IO.File]::WriteAllText((Join-Path $InstallDir 'StreamStudio.exe'), 'old app')
        Write-InstallOwnershipState -Directory $InstallDir | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $InstallDir 'user-folder')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $source 'StreamStudio.exe'), 'new app')
        [IO.File]::WriteAllText((Join-Path $source 'user-folder'), 'conflicting managed file')
        Write-InstallOwnershipState -Directory $source | Out-Null
        $failed = $false
        try { Install-AppPayloadAtomically -PayloadRoot $source -SourceDescription 'conflicting fixture' | Out-Null } catch {
            $failed = $true
            Assert-Lifecycle ($_.Exception.Message -match 'conflicts with user-created directory') 'The conflict was not identified.'
        }
        Assert-Lifecycle $failed 'The upgrade overwrote a user directory.'
        Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $InstallDir 'StreamStudio.exe')) -ceq 'old app') 'The existing application changed after a conflict.'
        Assert-Lifecycle (Test-Path -LiteralPath (Join-Path $InstallDir 'user-folder') -PathType Container) 'The existing user directory was removed.'
        Assert-Lifecycle (@(Get-ChildItem -LiteralPath $testRoot -Force | Where-Object { $_.Name -match '^\.conflict-install\.(stage|backup)-' }).Count -eq 0) 'An aborted upgrade left transaction directories.'
        Read-InstallOwnershipState $InstallDir | Out-Null
    }

    Invoke-LifecycleTest 'cleanup removes unlocked siblings and retries read-only directories' {
        $cleanup = Join-Path $testRoot 'cleanup'
        [IO.Directory]::CreateDirectory($cleanup) | Out-Null
        $lockedPath = Join-Path $cleanup 'a-locked.bin'
        $siblingPath = Join-Path $cleanup 'z-removable.bin'
        [IO.File]::WriteAllText($lockedPath, 'locked')
        [IO.File]::WriteAllText($siblingPath, 'remove')
        [IO.File]::SetAttributes($siblingPath, [IO.FileAttributes]::ReadOnly)
        [IO.File]::SetAttributes($cleanup, [IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReadOnly)
        try {
            $handle = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
            try {
                try { Remove-DirectoryTreeSafely $cleanup } catch { }
                Assert-Lifecycle (-not (Test-Path -LiteralPath $siblingPath)) 'A locked file prevented sibling cleanup.'
                Assert-Lifecycle (Test-Path -LiteralPath $lockedPath) 'The locked file was not retained for retry.'
            } finally { $handle.Dispose() }
            Remove-DirectoryTreeSafely $cleanup
            Assert-Lifecycle (-not (Test-Path -LiteralPath $cleanup)) 'A read-only cleanup directory remained after retry.'
        } finally {
            foreach ($path in @($cleanup, $siblingPath)) {
                if (Test-Path -LiteralPath $path) { [IO.File]::SetAttributes($path, [IO.FileAttributes]::Normal) }
            }
        }
    }

    Invoke-LifecycleTest 'cleanup rejects ancestor junctions and unlinks direct junctions without traversing targets' {
        $target = Join-Path $testRoot 'junction-target'
        $link = Join-Path $testRoot 'junction-link'
        [IO.Directory]::CreateDirectory($target) | Out-Null
        [IO.File]::WriteAllText((Join-Path $target 'keep.txt'), 'unrelated data')
        New-Item -ItemType Junction -Path $link -Target $target | Out-Null
        try {
            $rejected = $false
            try { Remove-DirectoryTreeSafely (Join-Path $link 'keep.txt') } catch { $rejected = $true }
            Assert-Lifecycle $rejected 'Cleanup followed an ancestor junction.'
            Remove-DirectoryTreeSafely $link
            Assert-Lifecycle (-not (Test-Path -LiteralPath $link)) 'The direct junction was not removed.'
            Assert-Lifecycle ([IO.File]::ReadAllText((Join-Path $target 'keep.txt')) -ceq 'unrelated data') 'Cleanup changed the junction target.'
        } finally {
            if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }
        }
    }

    Invoke-LifecycleTest 'the installed app stays registered when a dependency fails' {
        $entryPoint = $installer.EndBlock.Statements[-1]
        Assert-Lifecycle ($entryPoint -is [Management.Automation.Language.TryStatementAst]) 'The installer entry point changed.'
        foreach ($failingDependency in @('Streamlink', 'VLC')) {
            $InstallDir = Join-Path $testRoot 'registered-install'
            $steps = [Collections.Generic.List[string]]::new()
            $SkipApp = $false
            $SkipStreamlink = $false
            $SkipVlc = $false
            $Launch = $true
            function Install-App { $steps.Add('app'); return (Join-Path $testRoot 'registered-install\StreamStudio.exe') }
            function New-StartMenuShortcut { param($AppExe) $steps.Add('shortcut') }
            function Register-AppUninstallEntry { param($AppExe) $steps.Add('registration') }
            function Ensure-LockedStreamlink {
                $steps.Add('Streamlink')
                if ($failingDependency -eq 'Streamlink') { throw 'Simulated dependency failure.' }
                return 'streamlink.exe'
            }
            function Ensure-LockedVlc {
                $steps.Add('VLC')
                if ($failingDependency -eq 'VLC') { throw 'Simulated dependency failure.' }
                return 'vlc'
            }
            function Update-AppSettings { param($StreamlinkPath, $VlcDirectory) $steps.Add('settings') }
            function Start-Process { param($FilePath) $steps.Add('launch') }
            function Remove-TempRoot { $steps.Add('cleanup') }
            $installOperationMutex = Enter-InstallOperation $InstallDir
            $failed = $false
            try { & ([scriptblock]::Create($entryPoint.Extent.Text)) } catch {
                $failed = $true
                Assert-Lifecycle ($_.Exception.Message -match 'Simulated dependency failure') 'The dependency failure was hidden.'
            }
            Assert-Lifecycle $failed 'The simulated dependency did not fail.'
            Assert-Lifecycle ($steps.Contains('registration')) "A $failingDependency failure stranded an unregistered application."
            Assert-Lifecycle ($steps.IndexOf('registration') -lt $steps.IndexOf('Streamlink')) 'Registration was delayed until after dependency installation.'
            Assert-Lifecycle ($steps.Contains('shortcut')) 'The installed application has no Start Menu entry.'
            Assert-Lifecycle ($steps.Contains('cleanup')) 'Dependency failure bypassed temporary-file cleanup.'
            Assert-Lifecycle (-not $steps.Contains('settings') -and -not $steps.Contains('launch')) 'Incomplete dependencies were saved or the app was launched.'
        }
    }

    Invoke-LifecycleTest 'an unsuccessful forced shutdown aborts before installation can proceed' {
        # Import the real process policy into this scenario's scope and simulate
        # Windows refusing to stop a process. No process on this PC is terminated.
        $shutdown = $installer.Find({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-AppIfNeeded'
            }, $false)
        . ([scriptblock]::Create($shutdown.Extent.Text))
        $InstallDir = Join-Path $testRoot 'running-install'
        $candidate = [pscustomobject]@{ Name = 'StreamStudio.exe'; ExecutablePath = (Join-Path $InstallDir 'StreamStudio.exe'); ProcessId = 123456 }
        $requestedStops = [Collections.Generic.List[int]]::new()
        $isStillRunning = $true
        function Get-CimInstance { param($ClassName, $ErrorAction) $candidate }
        function Stop-Process { param($Id, [switch]$Force, $ErrorAction) $requestedStops.Add([int]$Id) }
        function Wait-Process { param($Id, $Timeout, $ErrorAction) }
        function Get-Process { param($Id, $ErrorAction) if ($isStillRunning) { $candidate } }
        $ForceStopApp = $false
        $refused = $false
        try { Stop-AppIfNeeded } catch { $refused = $true }
        Assert-Lifecycle $refused 'An unrequested forced shutdown was allowed.'
        Assert-Lifecycle ($requestedStops.Count -eq 0) 'The app was stopped without ForceStopApp.'
        $ForceStopApp = $true
        $refused = $false
        try { Stop-AppIfNeeded } catch {
            $refused = $true
            Assert-Lifecycle ($_.Exception.Message -match 'still running') 'An unsuccessful shutdown was not identified.'
        }
        Assert-Lifecycle $refused 'The installer continued while the app was still running.'
        Assert-Lifecycle ($requestedStops.Count -eq 1) 'The expected stop attempt did not occur.'
        $isStillRunning = $false
        Stop-AppIfNeeded
    }
} finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    Assert-UnderDirectory -ChildPath $resolvedRoot -ParentPath ([IO.Path]::GetTempPath())
    Assert-Lifecycle ([IO.Path]::GetFileName($resolvedRoot) -cmatch '^StreamStudio-install-lifecycle-[0-9a-f]{32}$') 'Unsafe test cleanup path.'
    Remove-DirectoryTreeSafely $resolvedRoot
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
