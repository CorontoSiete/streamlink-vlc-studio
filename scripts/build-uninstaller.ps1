param(
    [string]$OutputPath,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))

function Write-Info([string]$Message) {
    if (-not $Quiet) {
        Write-Host $Message
    }
}

function Assert-UnderDirectory([string]$ChildPath, [string]$ParentPath) {
    $childFull = [System.IO.Path]::GetFullPath($ChildPath)
    $parentFull = [System.IO.Path]::GetFullPath($ParentPath).TrimEnd([char[]](92, 47))
    if (-not $childFull.StartsWith($parentFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($childFull, $parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside expected directory. Path: $childFull Parent: $parentFull"
    }
}

function Remove-DirectoryIfExists([string]$Path, [string]$ParentPath) {
    $full = [System.IO.Path]::GetFullPath($Path)
    Assert-UnderDirectory $full $ParentPath
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

function New-UninstallBootstrapScript([string]$Path) {
    @'
[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StreamlinkVlcStudio"

function Write-Step([string]$Message) {
    if (-not $Quiet) {
        Write-Host ""
        Write-Host "==> $Message" -ForegroundColor Cyan
    }
}

function Normalize-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]](92, 47))
}

function Test-SafeDeleteDirectory([string]$Directory) {
    $full = Normalize-FullPath $Directory
    if ([string]::IsNullOrWhiteSpace($full) -or -not (Test-Path -LiteralPath $full -PathType Container)) {
        return $false
    }

    $blocked = @(
        [System.IO.Path]::GetPathRoot($full),
        $env:USERPROFILE,
        $env:APPDATA,
        $env:LOCALAPPDATA,
        $env:ProgramData,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:windir
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Normalize-FullPath $_ }

    if ($blocked | Where-Object { [string]::Equals($_, $full, [StringComparison]::OrdinalIgnoreCase) }) {
        return $false
    }

    (Test-Path -LiteralPath (Join-Path $full "StreamlinkVlcStudio.exe") -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $full "Uninstall.exe") -PathType Leaf)
}

function Get-ParentProcessInfo {
    try {
        $current = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
        if ($null -eq $current -or $null -eq $current.ParentProcessId) {
            return $null
        }

        Get-CimInstance Win32_Process -Filter "ProcessId=$($current.ParentProcessId)"
    } catch {
        $null
    }
}

function Resolve-InstallDirectory {
    try {
        $properties = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction Stop
        if (-not [string]::IsNullOrWhiteSpace($properties.InstallLocation)) {
            return Normalize-FullPath $properties.InstallLocation
        }
    } catch {
    }

    $parent = Get-ParentProcessInfo
    if ($parent -and -not [string]::IsNullOrWhiteSpace($parent.ExecutablePath)) {
        return Normalize-FullPath (Split-Path -Parent $parent.ExecutablePath)
    }

    ""
}

function Stop-AppProcesses {
    $running = @(Get-Process -Name "StreamlinkVlcStudio", "StreamlinkVlcStudio.App.Wpf", "vlc_chat_overlay" -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    Write-Step "Stopping Streamlink VLC Studio"
    $ids = @($running | ForEach-Object { $_.Id })
    $running | Stop-Process -Force
    try {
        Wait-Process -Id $ids -Timeout 10 -ErrorAction SilentlyContinue
    } catch {
    }
}

function Remove-ShortcutIfTargetMatches([string]$ShortcutPath, [string]$InstallDirectory) {
    try {
        if (-not (Test-Path -LiteralPath $ShortcutPath -PathType Leaf)) {
            return
        }

        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $target = Normalize-FullPath $shortcut.TargetPath
        $install = Normalize-FullPath $InstallDirectory
        if ([string]::IsNullOrWhiteSpace($target) -or
            $target.StartsWith($install + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals((Split-Path -Leaf $ShortcutPath), "Streamlink VLC Studio.lnk", [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals((Split-Path -Leaf $ShortcutPath), "StreamlinkVlcStudio.lnk", [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $ShortcutPath -Force
        }
    } catch {
    }
}

function Remove-Shortcuts([string]$InstallDirectory) {
    Write-Step "Removing shortcuts"
    $shortcutRoots = @(
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)) "Programs"),
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory))
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Container) }

    foreach ($root in $shortcutRoots) {
        Get-ChildItem -LiteralPath $root -Recurse -Filter "*.lnk" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "*Streamlink*VLC*Studio*" -or $_.Name -like "*StreamlinkVlcStudio*" } |
            ForEach-Object { Remove-ShortcutIfTargetMatches $_.FullName $InstallDirectory }
    }
}

function Remove-AppData([string]$InstallDirectory) {
    Write-Step "Removing app data"
    $install = Normalize-FullPath $InstallDirectory
    $paths = @(
        (Join-Path $env:APPDATA "StreamlinkVlcStudio"),
        (Join-Path $env:LOCALAPPDATA "StreamlinkVlcStudio")
    )

    foreach ($path in $paths) {
        $full = Normalize-FullPath $path
        if (-not [string]::IsNullOrWhiteSpace($install) -and
            [string]::Equals($full, $install, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if (Test-SafeDeleteDirectory $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        } elseif (Test-Path -LiteralPath $path -PathType Container) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    $tempRoot = [System.IO.Path]::GetTempPath()
    Get-ChildItem -LiteralPath $tempRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "StreamlinkVlcStudio-setup-*" -or $_.Name -like "StreamlinkVlcStudio-installer-*" -or $_.Name -like "StreamlinkVlcStudio-uninstall-*" } |
        ForEach-Object {
            try {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            } catch {
            }
        }
}

function Remove-UninstallRegistryEntry {
    Write-Step "Removing uninstall registration"
    if (Test-Path -LiteralPath $uninstallKey) {
        Remove-Item -LiteralPath $uninstallKey -Recurse -Force
    }
}

function Start-InstallDirectoryCleanup([string]$InstallDirectory) {
    if (-not (Test-SafeDeleteDirectory $InstallDirectory)) {
        throw "Refusing to delete unsafe install directory: $InstallDirectory"
    }

    $parent = Get-ParentProcessInfo
    $waitIds = @($PID)
    if ($parent -and $parent.ProcessId) {
        $waitIds += [int]$parent.ProcessId
    }

    $cleanupScript = Join-Path ([System.IO.Path]::GetTempPath()) ("StreamlinkVlcStudio-uninstall-cleanup-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    @(
        'param('
        '    [Parameter(Mandatory = $true)]'
        '    [string]$InstallDirectory,'
        '    [string]$WaitProcessIds = ""'
        ')'
        ''
        '$ErrorActionPreference = "SilentlyContinue"'
        ''
        'foreach ($idText in ($WaitProcessIds -split ",")) {'
        '    $id = 0'
        '    if ([int]::TryParse($idText, [ref]$id) -and $id -gt 0) {'
        '        Wait-Process -Id $id -Timeout 30 -ErrorAction SilentlyContinue'
        '    }'
        '}'
        ''
        'for ($attempt = 0; $attempt -lt 30; $attempt++) {'
        '    Remove-Item -LiteralPath $InstallDirectory -Recurse -Force -ErrorAction SilentlyContinue'
        '    if (-not (Test-Path -LiteralPath $InstallDirectory)) {'
        '        break'
        '    }'
        ''
        '    Start-Sleep -Milliseconds 500'
        '}'
        ''
        'Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue'
    ) | Set-Content -LiteralPath $cleanupScript -Encoding ASCII

    Start-Process -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $cleanupScript,
        "-InstallDirectory",
        $InstallDirectory,
        "-WaitProcessIds",
        ($waitIds -join ",")
    ) -WindowStyle Hidden
}

$installDirectory = Resolve-InstallDirectory
if ([string]::IsNullOrWhiteSpace($installDirectory)) {
    throw "Could not resolve the Streamlink VLC Studio install directory."
}

Stop-AppProcesses
Remove-Shortcuts $installDirectory
Remove-AppData $installDirectory
Remove-UninstallRegistryEntry
Write-Step "Removing installed files"
Start-InstallDirectoryCleanup $installDirectory
Write-Step "Uninstall complete"
'@ | Set-Content -LiteralPath $Path -Encoding ASCII
}

function New-IExpressSed(
    [string]$Path,
    [string]$SourceDirectory,
    [string]$TargetPath) {
    $sourceDirectoryFull = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd([char[]](92, 47)) + [System.IO.Path]::DirectorySeparatorChar
    $targetPathFull = [System.IO.Path]::GetFullPath($TargetPath)

    @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$targetPathFull
FriendlyName=Streamlink VLC Studio Uninstall
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Uninstall-StreamlinkVlcStudio-Bootstrap.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Uninstall-StreamlinkVlcStudio-Bootstrap.ps1 -Quiet
UserQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Uninstall-StreamlinkVlcStudio-Bootstrap.ps1 -Quiet
FILE0="Uninstall-StreamlinkVlcStudio-Bootstrap.ps1"
[SourceFiles]
SourceFiles0=$sourceDirectoryFull
[SourceFiles0]
%FILE0%=
"@ | Set-Content -LiteralPath $Path -Encoding ASCII
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "release\Uninstall.exe"
}

$outputPathFull = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputPathFull
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$iexpress = Get-Command "iexpress.exe" -ErrorAction SilentlyContinue
if ($null -eq $iexpress) {
    throw "IExpress was not found. This uninstaller builder requires Windows iexpress.exe."
}

$buildRoot = Join-Path $outputDirectory ".uninstaller-build"
Remove-DirectoryIfExists $buildRoot $outputDirectory
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

try {
    $bootstrapPath = Join-Path $buildRoot "Uninstall-StreamlinkVlcStudio-Bootstrap.ps1"
    New-UninstallBootstrapScript $bootstrapPath

    $sedPath = Join-Path $buildRoot "Uninstall.sed"
    New-IExpressSed $sedPath $buildRoot $outputPathFull

    if (Test-Path -LiteralPath $outputPathFull -PathType Leaf) {
        Remove-Item -LiteralPath $outputPathFull -Force
    }

    Write-Info "Building uninstall executable..."
    $process = Start-Process -FilePath $iexpress.Source -ArgumentList @("/N", "/Q", $sedPath) -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "IExpress failed with exit code $($process.ExitCode)."
    }

    if (-not (Test-Path -LiteralPath $outputPathFull -PathType Leaf) -or
        (Get-Item -LiteralPath $outputPathFull).Length -eq 0) {
        throw "Uninstall executable was not created: $outputPathFull"
    }
} finally {
    Remove-DirectoryIfExists $buildRoot $outputDirectory
}

Write-Info "Uninstall executable: $outputPathFull"
Write-Output $outputPathFull
