[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$previousExitCode = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$hadExitCode = $null -ne $previousExitCode
$savedExitCode = if ($hadExitCode) { $previousExitCode.Value } else { $null }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'scripts\lib\common.ps1')
. (Join-Path $repoRoot 'scripts\lib\dependency-manifest.ps1')
. (Join-Path $repoRoot 'scripts\lib\runtime-dependencies.ps1')

function Assert-DependencyTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-DependencyFailure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        Assert-DependencyTest ($_.Exception.Message -match $Pattern) "Unexpected failure: $($_.Exception.Message)"
        return
    }
    throw "Expected failure matching '$Pattern'."
}

# Execute the actual dependency orchestration with isolated installer/download
# boundaries. No registry, shortcuts, installed programs, or settings are changed.
$tokens = $null
$parseErrors = $null
$installer = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'scripts\install.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert-DependencyTest ($parseErrors.Count -eq 0) 'Installer syntax is invalid.'
$functionNames = @('Ensure-LockedStreamlink', 'Ensure-LockedVlc', 'Ensure-LockedWebView2',
    'Assert-InstalledDependencies', 'Use-AppPayloadDependencyManifest', 'Get-BoundedDownloadLength', 'Get-TempDownloadPath')
foreach ($definition in $installer.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $functionNames
        }, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}

$canonicalPath = Join-Path $repoRoot 'dependencies\windows-installers.json'
$canonicalJson = Get-Content -LiteralPath $canonicalPath -Raw
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('StreamStudio-dependency-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:TempRoot = Join-Path $testRoot 'downloads'
$script:MaximumDownloadBytes = 512MB
$script:streamlinkFixture = Join-Path $testRoot 'streamlink.exe'
$script:vlcFixture = Join-Path $testRoot 'vlc'
[IO.File]::WriteAllText($script:streamlinkFixture, 'candidate')
[IO.Directory]::CreateDirectory($script:vlcFixture) | Out-Null
$script:events = [Collections.Generic.List[string]]::new()
$failures = [Collections.Generic.List[string]]::new()

function Write-Step([string]$Message) { }
function Write-Detail([string]$Message) { }
function Get-StreamlinkCandidatePaths { $script:streamlinkFixture }
function Get-VlcCandidateDirectories { $script:vlcFixture }
function Get-StreamlinkVersion([string]$Path) { $script:streamlinkVersion }
function Get-VlcVersion([string]$Path) { $script:vlcVersion }
function Get-WebView2Version { $script:webViewVersion }
function Save-Uri([string]$Uri, [string]$Path, [long]$ExpectedBytes, [long]$MaximumBytes) {
    Assert-DependencyTest ($Uri.StartsWith('https://') -and $ExpectedBytes -gt 0 -and $ExpectedBytes -eq $MaximumBytes) 'Download was not bounded by its reviewed length.'
    $script:events.Add('download')
    [IO.File]::WriteAllText($Path, 'download fixture')
}
function Assert-DownloadedDependency([string]$Path, $Dependency) {
    $script:events.Add('verify')
    if ($script:rejectDownload) { throw 'Fixture hash/signature validation failed.' }
}
function Start-Installer([string]$Path, [string[]]$ArgumentList, [string]$Name) {
    $script:events.Add('install')
    if ($Name -eq 'Streamlink') {
        Assert-DependencyTest (($ArgumentList -join ' ') -ceq '/S') 'Streamlink silent arguments changed.'
        $script:streamlinkVersion = $script:installedVersion
    } elseif ($Name -eq 'VLC') {
        Assert-DependencyTest (($ArgumentList -join ' ') -ceq '/L=1033 /S') 'VLC silent arguments changed.'
        $script:vlcVersion = $script:installedVersion
    } else {
        Assert-DependencyTest (($ArgumentList -join ' ') -ceq '/silent /install') 'WebView2 offline silent arguments changed.'
        $script:webViewVersion = $script:installedVersion
    }
}

function Invoke-DependencyTest([string]$TestName, [scriptblock]$Test) {
    $script:events.Clear()
    $script:DependencyManifest = $canonicalJson | ConvertFrom-Json
    $script:InstallerDependencyManifest = $canonicalJson | ConvertFrom-Json
    $script:DependencyManifestWasOverridden = $false
    $script:VerifyInstalledAppDependencies = $false
    $script:RebootRequired = $false
    $script:ForceDependencyUpdate = $false
    $script:streamlinkVersion = '8.5.0'
    $script:vlcVersion = '3.0.23'
    $script:webViewVersion = '154.0.4258.48'
    $script:rejectDownload = $false
    try {
        & $Test
        Write-Host "PASS installer dependencies: $TestName"
    } catch {
        $failures.Add("${TestName}: $($_.Exception.Message)")
        Write-Host "FAIL installer dependencies: ${TestName}: $($_.Exception.Message)"
    }
}

try {
    Invoke-DependencyTest 'compatible runtimes are retained without downloading or downgrading' {
        $script:streamlinkVersion = '9.0.0'
        $script:vlcVersion = '4.0.0'
        Assert-DependencyTest ((Ensure-LockedStreamlink) -eq $script:streamlinkFixture) 'Streamlink candidate was lost.'
        Assert-DependencyTest ((Ensure-LockedVlc) -eq $script:vlcFixture) 'VLC candidate was lost.'
        Assert-DependencyTest ((Ensure-LockedWebView2) -eq '154.0.4258.48') 'A compatible WebView2 Runtime was replaced.'
        Assert-DependencyTest ($script:events.Count -eq 0) 'A compatible dependency was downloaded.'
        $script:ForceDependencyUpdate = $true
        $script:webViewVersion = '155.0.0.1'
        Ensure-LockedWebView2 | Out-Null
        Assert-DependencyTest ($script:events.Count -eq 0) 'ForceDependencyUpdate downgraded the shared Evergreen Runtime.'
    }
    foreach ($name in @('Streamlink', 'Vlc', 'WebView2')) {
        Invoke-DependencyTest "$name installs only verified bytes and rechecks the resulting runtime" {
            $script:streamlinkVersion = ''
            $script:vlcVersion = ''
            $script:webViewVersion = ''
            $script:installedVersion = if ($name -eq 'Streamlink') { '8.5.0' } elseif ($name -eq 'Vlc') { '3.0.23' } else { '154.0.4258.53' }
            & ("Ensure-Locked" + $name) | Out-Null
            Assert-DependencyTest (($script:events -join ',') -ceq 'download,verify,install') 'The installer ran before checksum/signature verification.'
        }
        Invoke-DependencyTest "$name rejects a successful installer that leaves the runtime missing or obsolete" {
            $script:streamlinkVersion = ''
            $script:vlcVersion = ''
            $script:webViewVersion = ''
            $script:installedVersion = '1.0.0'
            Assert-DependencyFailure { & ("Ensure-Locked" + $name) | Out-Null } 'No compatible|usable x64 runtime'
        }
        Invoke-DependencyTest "$name does not execute a rejected download" {
            $script:streamlinkVersion = ''
            $script:vlcVersion = ''
            $script:webViewVersion = ''
            $script:rejectDownload = $true
            Assert-DependencyFailure { & ("Ensure-Locked" + $name) | Out-Null } 'validation failed'
            Assert-DependencyTest (($script:events -join ',') -ceq 'download,verify') 'Rejected bytes were executed.'
        }
    }
    Invoke-DependencyTest 'final verification refuses missing dependencies even when their installation was skipped' {
        $script:streamlinkVersion = '7.9.0'
        Assert-DependencyFailure { Assert-InstalledDependencies '' $script:streamlinkFixture $script:vlcFixture } 'No compatible Streamlink'
        $script:streamlinkVersion = '8.5.0'
        $script:vlcVersion = ''
        Assert-DependencyFailure { Assert-InstalledDependencies '' $script:streamlinkFixture $script:vlcFixture } 'No compatible VLC'
        $script:vlcVersion = '3.0.23'
        $script:webViewVersion = '120.0.0.0'
        Assert-DependencyFailure { Assert-InstalledDependencies '' $script:streamlinkFixture $script:vlcFixture } 'supported x64 WebView2'
    }
    Invoke-DependencyTest 'authenticated payload minima replace the older install script manifest' {
        $payload = Join-Path $testRoot 'new-payload'
        [IO.Directory]::CreateDirectory((Join-Path $payload 'dependencies')) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $payload 'lib')) | Out-Null
        $updated = $canonicalJson | ConvertFrom-Json
        $updated.dependencies.streamlink.version = '9.0.0-1'
        $updated | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $payload 'dependencies\windows-installers.json') -Encoding UTF8
        [IO.File]::WriteAllText((Join-Path $payload 'lib\WindowsDependencyProbe.cs'), 'capability fixture')
        $minimums = [pscustomobject]@{ streamlink = '9.0.0-1'; vlc = '3.0.23'; webview2 = '152.0.4191.53' }
        Use-AppPayloadDependencyManifest $payload $minimums
        Assert-DependencyTest ($script:DependencyManifest.dependencies.streamlink.version -ceq '9.0.0-1') 'The old script pinned the new payload to an obsolete runtime.'
        Assert-DependencyTest $script:VerifyInstalledAppDependencies 'Native verification capability was not retained.'
        $minimums.webview2 = '140.0.0.0'
        Assert-DependencyFailure { Use-AppPayloadDependencyManifest $payload $minimums } 'webview2.*does not match'
        $script:DependencyManifestWasOverridden = $true
        Assert-DependencyFailure { Use-AppPayloadDependencyManifest $payload } 'explicit dependency manifest.*streamlink'
    }
    Invoke-DependencyTest 'legacy signed releases keep their declared minima and gain the reviewed WebView2 Runtime' {
        $payload = Join-Path $testRoot 'legacy-payload'
        [IO.Directory]::CreateDirectory((Join-Path $payload 'dependencies')) | Out-Null
        $legacy = $canonicalJson | ConvertFrom-Json
        $legacy.dependencies.PSObject.Properties.Remove('webview2')
        $legacy | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $payload 'dependencies\windows-installers.json') -Encoding UTF8
        Use-AppPayloadDependencyManifest $payload ([pscustomobject]@{ streamlink = '8.5.0-1'; vlc = '3.0.23' })
        Assert-DependencyTest ($script:DependencyManifest.dependencies.webview2.minimumVersion -ceq '152.0.4191.53') 'Legacy installation omitted WebView2.'
        Assert-DependencyTest (-not $script:VerifyInstalledAppDependencies) 'An unsupported command would be sent to a legacy application.'
    }
    Invoke-DependencyTest 'dependency-only repair follows the installed payload rather than the older script minima' {
        $InstallDir = Join-Path $testRoot 'dependency-only-repair'
        [IO.Directory]::CreateDirectory((Join-Path $InstallDir 'dependencies')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $InstallDir 'StreamStudio.exe'), 'fixture application')
        $updated = $canonicalJson | ConvertFrom-Json
        $updated.dependencies.streamlink.version = '9.0.0-1'
        $updated | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $InstallDir 'dependencies\windows-installers.json') -Encoding UTF8
        $script:streamlinkVersion = '9.0.0'
        $SkipApp = $true
        $SkipStreamlink = $false
        $SkipVlc = $false
        $Launch = $false
        function Install-App { throw 'SkipApp attempted to replace the application.' }
        function Register-AppUninstallEntry { param($AppExe) $script:events.Add('registration') }
        function New-StartMenuShortcut { param($AppExe) $script:events.Add('shortcut') }
        function Update-AppSettings { param($StreamlinkPath, $VlcDirectory) $script:events.Add('settings') }
        function Remove-TempRoot { $script:events.Add('cleanup') }
        & ([scriptblock]::Create($installer.EndBlock.Statements[-1].Extent.Text))
        Assert-DependencyTest ($script:DependencyManifest.dependencies.streamlink.version -ceq '9.0.0-1') 'Dependency-only repair used obsolete installer minima.'
        Assert-DependencyTest (($script:events -join ',') -ceq 'registration,shortcut,settings,cleanup') 'Dependency-only repair did not retain the existing compatible runtime.'
    }
    Invoke-DependencyTest 'manifest and signing checks reject omissions invalid minima and unsafe download locations' {
        $minimums = [pscustomobject]@{ streamlink = '8.5.0-1'; vlc = '3.0.23'; webview2 = '152.0.4191.53' }
        Assert-DependencyMinimums $minimums $script:DependencyManifest
        $minimums.PSObject.Properties.Remove('webview2')
        Assert-DependencyFailure { Assert-DependencyMinimums $minimums $script:DependencyManifest } 'every dependency'
        foreach ($mutation in @(
                { param($data) $data.dependencies.webview2.minimumVersion = '999.0.0.0' },
                { param($data) $data.dependencies.webview2.minimumVersion = '0.0.0.0' },
                { param($data) $data.dependencies.webview2.fileName = '..\escape.exe' },
                { param($data) $data.dependencies.webview2.url = 'http://example.invalid/runtime.exe' },
                { param($data) $data.dependencies.webview2.expectedSignerThumbprint = '' },
                { param($data) $data.dependencies | Add-Member -NotePropertyName unknown -NotePropertyValue $data.dependencies.vlc })) {
            $data = $canonicalJson | ConvertFrom-Json
            & $mutation $data
            $path = Join-Path $testRoot 'invalid-manifest.json'
            $data | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
            Assert-DependencyFailure { Read-WindowsDependencyManifest $path | Out-Null } 'invalid|unsafe|expected signer|not supported'
        }
        $changedPayload = $canonicalJson | ConvertFrom-Json
        $changedPayload.dependencies.vlc.sha256 = '0' * 64
        Assert-DependencyFailure { Assert-DependencyManifestsMatch $script:DependencyManifest $changedPayload } 'vlc.*sha256.*does not match'
    }

    # Compile a real x64 .NET Framework executable using the compiler available on
    # supported Windows versions, including when this suite is run from PowerShell 7.
    $source = Join-Path $testRoot 'ProbeFixture.cs'
    $compiler = Join-Path $testRoot 'compile-fixture.ps1'
    @'
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
public static class ProbeFixture {
    public static int Main(string[] args) {
        string executable = Assembly.GetExecutingAssembly().Location;
        string mode = Path.GetFileNameWithoutExtension(executable);
        if (mode == "args") return args.Length == 5 && args[0] == "--maintenance-verify-dependencies" &&
            args[1] == "--streamlink-path" && args[2] == "C:\\fixture path\\ending\\" &&
            args[3] == "--vlc-directory" && args[4] == "C:\\another \"quoted\"\\" ? 0 : 6;
        if (mode == "missing-plugin" && Array.IndexOf(args, "https://kick.com/streamstudio_dependency_check") >= 0) return 9;
        if (mode == "plugin-stall" && Array.IndexOf(args, "--can-handle-url-no-redirect") >= 0) { Thread.Sleep(30000); return 0; }
        if (mode == "no-config" && (args.Length == 0 || args[0] != "--no-config")) return 8;
        if (mode == "stall" || mode == "child") { Thread.Sleep(30000); return 0; }
        if (mode == "flood") { Console.WriteLine(new string('x', 40000)); Console.Error.WriteLine(new string('e', 40000)); return 0; }
        if (mode == "descendants") {
            Process child = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(executable), "child.exe")) { UseShellExecute = false, CreateNoWindow = true });
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(executable), "child.pid"), child.Id.ToString());
            Thread.Sleep(50);
        }
        Console.WriteLine("streamlink 8.5.0");
        return mode == "error" ? 7 : 0;
    }
}
'@ | Set-Content -LiteralPath $source -Encoding UTF8
    @'
param([string]$Source, [string]$Destination)
$ErrorActionPreference = 'Stop'
$options = [CodeDom.Compiler.CompilerParameters]::new()
$options.CompilerOptions = '/platform:x64'
$options.GenerateExecutable = $true
$options.OutputAssembly = $Destination
$options.ReferencedAssemblies.Add('System.dll') | Out-Null
$provider = [Microsoft.CSharp.CSharpCodeProvider]::new()
try {
    $result = $provider.CompileAssemblyFromFile($options, $Source)
    if ($result.Errors.HasErrors) { throw (($result.Errors | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine) }
} finally { $provider.Dispose() }
'@ | Set-Content -LiteralPath $compiler -Encoding UTF8
    $windowsPowerShell = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $compiler -Source $source -Destination (Join-Path $testRoot 'valid.exe')
    Assert-DependencyTest ($LASTEXITCODE -eq 0) 'The native process fixture did not compile.'
    foreach ($mode in @('error', 'flood', 'stall', 'child', 'descendants', 'args', 'missing-plugin', 'plugin-stall', 'no-config')) {
        Copy-Item -LiteralPath (Join-Path $testRoot 'valid.exe') -Destination (Join-Path $testRoot ($mode + '.exe'))
    }
    Invoke-DependencyTest 'bounded real Streamlink probes require x64 exact version output and successful exit' {
        Assert-DependencyTest ([StreamStudio.Installation.WindowsDependencyProbe]::ReadStreamlinkVersion((Join-Path $testRoot 'valid.exe'), 2000) -ceq '8.5.0.0') 'A real compatible executable was rejected.'
        foreach ($mode in @('error', 'flood')) {
            Assert-DependencyTest ([StreamStudio.Installation.WindowsDependencyProbe]::ReadStreamlinkVersion((Join-Path $testRoot ($mode + '.exe')), 2000) -ceq '') 'A failed or truncated version probe was accepted.'
        }
        $started = [Diagnostics.Stopwatch]::StartNew()
        Assert-DependencyFailure { [StreamStudio.Installation.WindowsDependencyProbe]::ReadStreamlinkVersion((Join-Path $testRoot 'stall.exe'), 150) } 'timed out'
        Assert-DependencyTest ($started.Elapsed.TotalSeconds -lt 5) 'A stalled dependency command kept Setup waiting.'
    }
    Invoke-DependencyTest 'dependency probes terminate descendants and preserve Windows argument quoting' {
        Assert-DependencyTest ([StreamStudio.Installation.WindowsDependencyProbe]::ReadStreamlinkVersion((Join-Path $testRoot 'descendants.exe'), 2000) -ceq '8.5.0.0') 'The child-process version probe failed.'
        $childId = [int][IO.File]::ReadAllText((Join-Path $testRoot 'child.pid'))
        $child = Get-Process -Id $childId -ErrorAction SilentlyContinue
        Assert-DependencyTest ($null -eq $child -or $child.HasExited) 'A dependency probe leaked a child process.'
        $result = [StreamStudio.Installation.WindowsDependencyProbe]::VerifyApplication(
            (Join-Path $testRoot 'args.exe'), 'C:\fixture path\ending\', 'C:\another "quoted"\')
        Assert-DependencyTest ($result.ExitCode -eq 0) 'Paths with spaces, quotes, or trailing backslashes were changed.'
    }
    Invoke-DependencyTest 'Streamlink must load both provider plugins offline and ignore personal configuration' {
        Assert-DependencyTest ([StreamStudio.Installation.WindowsDependencyProbe]::ReadStreamlinkVersion((Join-Path $testRoot 'missing-plugin.exe'), 2000) -ceq '') 'A working version command hid a broken Kick plugin.'
        Assert-DependencyTest ([StreamStudio.Installation.WindowsDependencyProbe]::ReadStreamlinkVersion((Join-Path $testRoot 'no-config.exe'), 2000) -ceq '8.5.0.0') 'The health check read personal Streamlink configuration.'
        $started = [Diagnostics.Stopwatch]::StartNew()
        Assert-DependencyFailure { [StreamStudio.Installation.WindowsDependencyProbe]::ReadStreamlinkVersion((Join-Path $testRoot 'plugin-stall.exe'), 400) } 'timed out'
        Assert-DependencyTest ($started.Elapsed.TotalSeconds -lt 5) 'A hung provider import bypassed the shared dependency timeout.'
    }
    Invoke-DependencyTest 'a 32-bit installer host fails before changing the application or shared runtimes' {
        $x86PowerShell = Join-Path $env:windir 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
        $errorPath = Join-Path $testRoot 'x86-installer-error.txt'
        $outputPath = Join-Path $testRoot 'x86-installer-output.txt'
        $process = Start-Process -FilePath $x86PowerShell -WindowStyle Hidden -PassThru `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + (Join-Path $repoRoot 'scripts\install.ps1') + '"'), '-SkipApp') `
            -RedirectStandardError $errorPath -RedirectStandardOutput $outputPath
        try {
            if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'The 32-bit installation guard did not finish.' }
            Assert-DependencyTest ($process.ExitCode -ne 0) 'An x86 process attempted x64 dependency detection.'
            Assert-DependencyTest ([IO.File]::ReadAllText($errorPath) -match '64-bit PowerShell') 'The x86 host failure did not identify the correct supported host.'
            Assert-DependencyTest ([IO.File]::ReadAllText($outputPath).Length -eq 0) 'The installation started before the process architecture was checked.'
        } finally { $process.Dispose() }
    }
} finally {
    # Only the temporary tree created above is eligible for recursive cleanup.
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    Assert-DependencyTest ($resolvedTestRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTestRoot).StartsWith('StreamStudio-dependency-tests-')) 'Unsafe test cleanup path.'
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @('stall', 'child', 'descendants', 'plugin-stall') })) {
        if ($process.Path -and $process.Path.StartsWith($resolvedTestRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $process.Kill()
            $process.WaitForExit(2000) | Out-Null
        }
    }
    Remove-DirectoryTreeSafely $resolvedTestRoot
    if ($hadExitCode) { $global:LASTEXITCODE = $savedExitCode }
    else { Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
