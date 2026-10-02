<#
.SYNOPSIS
Verifies an application against real runtime payloads and damaged private copies.
.DESCRIPTION
Never installs a program or changes registry entries, settings, or account data.
Use runtime directories extracted from the reviewed, hash-verified installers.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ApplicationExecutable,
    [Parameter(Mandatory = $true)][string]$StreamlinkDirectory,
    [Parameter(Mandatory = $true)][string]$VlcDirectory,
    [string]$WebView2ExecutableFolder,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'This runtime verification requires 64-bit PowerShell.' }
. (Join-Path $PSScriptRoot 'lib\common.ps1')
. (Join-Path $PSScriptRoot 'lib\runtime-dependencies.ps1')
$ApplicationExecutable = [IO.Path]::GetFullPath($ApplicationExecutable)
$StreamlinkDirectory = [IO.Path]::GetFullPath($StreamlinkDirectory)
$VlcDirectory = [IO.Path]::GetFullPath($VlcDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($source in @($ApplicationExecutable, $StreamlinkDirectory, $VlcDirectory)) {
    Assert-NoReparsePointInExistingPath $source
    if (-not (Test-Path -LiteralPath $source)) { throw "Runtime verification input is missing: $source" }
}
Assert-NoReparsePointInExistingPath $OutputDirectory
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryBase ('StreamStudio-runtime-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$savedWebView2Folder = [Environment]::GetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER', 'Process')
$savedAppData = [Environment]::GetEnvironmentVariable('APPDATA', 'Process')
$results = [Collections.Generic.List[object]]::new()

function Copy-RuntimeDirectory([string]$Source, [string]$Destination) {
    Assert-UnderDirectory -ChildPath $Destination -ParentPath $testRoot
    if ((Test-PathIsSameOrUnderDirectory -ChildPath $Source -ParentPath $Destination) -or
        (Test-PathIsSameOrUnderDirectory -ChildPath $Destination -ParentPath $Source)) {
        throw 'Runtime verification sources and private copies must not contain one another.'
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Runtime verification inputs cannot contain junctions or symbolic links: $($item.FullName)"
        }
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) { Copy-RuntimeDirectory $item.FullName $target }
        else { Copy-Item -LiteralPath $item.FullName -Destination $target }
    }
}

function Verify-Health([string]$Name, [string]$Streamlink, [string]$Vlc, [string]$ExpectedFailure = '') {
    $health = [StreamStudio.Installation.WindowsDependencyProbe]::VerifyApplication($ApplicationExecutable, $Streamlink, $Vlc)
    $health | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory ($Name + '.json')) -Encoding UTF8
    if ($health.Truncated) { throw "The runtime health check truncated its output: $Name" }
    if ([string]::IsNullOrEmpty($ExpectedFailure)) {
        if ($health.ExitCode -ne 0) { throw "Runtime health check failed ($Name): $($health.Error)" }
    } elseif ($health.ExitCode -eq 0 -or $health.Error -notmatch $ExpectedFailure) {
        throw "Runtime health check did not reject $Name as expected: $($health.Error)"
    }
    $results.Add([pscustomobject]@{ Test = $Name; ExitCode = $health.ExitCode; Passed = $true })
    Write-Host "PASS real runtime dependencies: $Name"
}

try {
    # Streamlink's Windows launcher can write an exception log to APPDATA.
    # Keep diagnostics from deliberately damaged providers inside this test tree.
    $env:APPDATA = Join-Path $testRoot 'appdata'
    New-Item -ItemType Directory -Path $env:APPDATA | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($WebView2ExecutableFolder)) {
        $WebView2ExecutableFolder = [IO.Path]::GetFullPath($WebView2ExecutableFolder)
        Assert-NoReparsePointInExistingPath $WebView2ExecutableFolder
        if (-not [StreamStudio.Installation.WindowsDependencyProbe]::IsX64PortableExecutable((Join-Path $WebView2ExecutableFolder 'msedgewebview2.exe'))) {
            throw 'The isolated WebView2 test folder does not contain an x64 browser executable.'
        }
        $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER = $WebView2ExecutableFolder
    }
    $streamlink = Join-Path $StreamlinkDirectory 'bin\streamlink.exe'
    Verify-Health 'reviewed-runtime-payloads' $streamlink $VlcDirectory

    $streamlinkCopy = Join-Path $testRoot 'streamlink'
    $vlcCopy = Join-Path $testRoot 'vlc'
    Copy-RuntimeDirectory $StreamlinkDirectory $streamlinkCopy
    Copy-RuntimeDirectory $VlcDirectory $vlcCopy
    $copiedStreamlink = Join-Path $streamlinkCopy 'bin\streamlink.exe'
    Verify-Health 'private-runtime-copies' $copiedStreamlink $vlcCopy

    $kickPlugin = Join-Path $streamlinkCopy 'pkgs\streamlink\plugins\kick.py'
    if (-not (Test-Path -LiteralPath $kickPlugin -PathType Leaf)) { throw 'The reviewed Streamlink Kick plugin was not found.' }
    Assert-UnderDirectory -ChildPath $kickPlugin -ParentPath $testRoot
    [IO.File]::WriteAllText($kickPlugin, 'raise ImportError("Runtime regression fixture: broken Kick dependency")')
    Verify-Health 'broken-streamlink-provider-import' $copiedStreamlink $vlcCopy 'Streamlink:'
    Copy-Item -LiteralPath (Join-Path $StreamlinkDirectory 'pkgs\streamlink\plugins\kick.py') -Destination $kickPlugin -Force

    $audioPlugin = Join-Path $vlcCopy 'plugins\audio_output\libdirectsound_plugin.dll'
    Assert-UnderDirectory -ChildPath $audioPlugin -ParentPath $testRoot
    Remove-Item -LiteralPath $audioPlugin -Force
    Verify-Health 'missing-vlc-audio-plugin' $copiedStreamlink $vlcCopy 'VLC:'
    Copy-Item -LiteralPath (Join-Path $VlcDirectory 'plugins\audio_output\libdirectsound_plugin.dll') -Destination $audioPlugin

    $mp4Plugin = Join-Path $vlcCopy 'plugins\demux\libmp4_plugin.dll'
    Assert-UnderDirectory -ChildPath $mp4Plugin -ParentPath $testRoot
    # libvlc is a real loadable x64 DLL, but it exports no VLC module entry point.
    Copy-Item -LiteralPath (Join-Path $VlcDirectory 'libvlc.dll') -Destination $mp4Plugin -Force
    Verify-Health 'vlc-library-without-module-exports' $copiedStreamlink $vlcCopy 'VLC:'
    Copy-Item -LiteralPath (Join-Path $VlcDirectory 'plugins\demux\libmp4_plugin.dll') -Destination $mp4Plugin -Force

    # The replacement is a valid, loadable VLC DLL with the correct module ABI.
    # Only real module discovery can distinguish it from the required MP4 demuxer.
    Copy-Item -LiteralPath (Join-Path $VlcDirectory 'plugins\audio_output\libadummy_plugin.dll') -Destination $mp4Plugin -Force
    Verify-Health 'wrong-vlc-module-with-stale-cache' $copiedStreamlink $vlcCopy "required VLC module 'mp4'"
    Copy-Item -LiteralPath (Join-Path $VlcDirectory 'plugins\demux\libmp4_plugin.dll') -Destination $mp4Plugin -Force

    $library = Join-Path $vlcCopy 'libvlc.dll'
    Assert-UnderDirectory -ChildPath $library -ParentPath $testRoot
    $image = [IO.File]::ReadAllBytes($library)
    $peOffset = [BitConverter]::ToInt32($image, 0x3c)
    $image[$peOffset + 4] = 0x4c
    $image[$peOffset + 5] = 0x01
    [IO.File]::WriteAllBytes($library, $image)
    Verify-Health 'wrong-vlc-architecture' $copiedStreamlink $vlcCopy 'VLC:'
    Copy-Item -LiteralPath (Join-Path $VlcDirectory 'libvlc.dll') -Destination $library -Force

    if (-not [string]::IsNullOrWhiteSpace($WebView2ExecutableFolder)) {
        $incompleteBrowser = Join-Path $testRoot 'incomplete-webview2'
        New-Item -ItemType Directory -Path $incompleteBrowser | Out-Null
        # Preserve the genuine x64 browser and its version metadata, but omit
        # its runtime DLLs and data. The installed registry still looks healthy.
        Copy-Item -LiteralPath (Join-Path $WebView2ExecutableFolder 'msedgewebview2.exe') -Destination $incompleteBrowser
        $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER = $incompleteBrowser
        Verify-Health 'missing-webview2-runtime-files' $copiedStreamlink $vlcCopy 'WebView2:'
        $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER = $WebView2ExecutableFolder
    }
    Verify-Health 'recovered-runtime-copies' $copiedStreamlink $vlcCopy
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
} finally {
    [Environment]::SetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER', $savedWebView2Folder, 'Process')
    [Environment]::SetEnvironmentVariable('APPDATA', $savedAppData, 'Process')
    # Resolve and verify the only eligible cleanup tree before recursive removal.
    Assert-UnderDirectory -ChildPath $testRoot -ParentPath $temporaryBase
    if (-not [IO.Path]::GetFileName($testRoot).StartsWith('StreamStudio-runtime-tests-', [StringComparison]::Ordinal)) {
        throw 'Unsafe runtime test cleanup path.'
    }
    Remove-DirectoryTreeSafely $testRoot
}
