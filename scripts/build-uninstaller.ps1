param(
    [string]$OutputPath,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
. (Join-Path $scriptRoot "lib\common.ps1")

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

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        return $rootPath
    }

    $fullPath.TrimEnd([char[]](92, 47))
}

function Test-PathContainsReparsePoint([string]$Path) {
    $fullPath = Normalize-FullPath $Path
    if ([string]::IsNullOrWhiteSpace($fullPath)) {
        return $false
    }

    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    $relativePath = $fullPath.Substring($rootPath.Length).Trim([char[]](92, 47))
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return $false
    }

    $currentPath = $rootPath
    foreach ($segment in ($relativePath -split '[\\/]')) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            return $true
        }
    }

    $false
}

function Remove-ReparsePoint([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Path is not a symbolic link or directory junction: $Path"
    }

    # Windows PowerShell 5.1 can throw a NullReferenceException when
    # Remove-Item unlinks a directory junction. These APIs remove only the link.
    if ($item.PSIsContainer) {
        [System.IO.Directory]::Delete($item.FullName)
    } else {
        [System.IO.File]::Delete($item.FullName)
    }
}

function Remove-DirectoryTreeSafely([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Remove-ReparsePoint $item.FullName
        return
    }

    if (-not $item.PSIsContainer) {
        Remove-Item -LiteralPath $item.FullName -Force
        return
    }

    Get-ChildItem -LiteralPath $item.FullName -Force | ForEach-Object {
        if ($_.PSIsContainer -or
            (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            Remove-DirectoryTreeSafely $_.FullName
        } else {
            Remove-Item -LiteralPath $_.FullName -Force
        }
    }
    [System.IO.Directory]::Delete($item.FullName)
}

function Test-SafeDeleteDirectory([string]$Directory) {
    $full = Normalize-FullPath $Directory
    if ([string]::IsNullOrWhiteSpace($full) -or -not (Test-Path -LiteralPath $full -PathType Container)) {
        return $false
    }

    if (Test-PathContainsReparsePoint $full) {
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

    (Test-Path -LiteralPath (Join-Path $full "StreamlinkVlcStudio.exe") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $full "Uninstall.exe") -PathType Leaf)
}

function Test-PathIsSameOrUnderDirectory([string]$ChildPath, [string]$ParentPath) {
    $child = Normalize-FullPath $ChildPath
    $parent = Normalize-FullPath $ParentPath
    $parentPrefix = if ($parent.EndsWith([System.IO.Path]::DirectorySeparatorChar) -or
        $parent.EndsWith([System.IO.Path]::AltDirectorySeparatorChar)) {
        $parent
    } else {
        $parent + [System.IO.Path]::DirectorySeparatorChar
    }
    [string]::Equals($child, $parent, [StringComparison]::OrdinalIgnoreCase) -or
        $child.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase)
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
        if (-not [string]::IsNullOrWhiteSpace($target) -and
            (Test-PathIsSameOrUnderDirectory $target $install)) {
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
            (Test-PathIsSameOrUnderDirectory $install $full)) {
            continue
        }

        if (Test-Path -LiteralPath $path -PathType Container) {
            $parentPath = Split-Path -Parent $full
            if (-not (Test-PathContainsReparsePoint $parentPath)) {
                Remove-DirectoryTreeSafely $path
            }
        }
    }

    $tempRoot = [System.IO.Path]::GetTempPath()
    Get-ChildItem -LiteralPath $tempRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "StreamlinkVlcStudio-setup-*" -or $_.Name -like "StreamlinkVlcStudio-installer-*" -or $_.Name -like "StreamlinkVlcStudio-uninstall-*" } |
        ForEach-Object {
            try {
                if (-not (Test-PathContainsReparsePoint (Split-Path -Parent $_.FullName))) {
                    Remove-DirectoryTreeSafely $_.FullName
                }
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
        'function Normalize-FullPath {'
        (Get-Command Normalize-FullPath).Definition
        '}'
        ''
        'function Test-PathContainsReparsePoint {'
        (Get-Command Test-PathContainsReparsePoint).Definition
        '}'
        ''
        'function Remove-ReparsePoint {'
        (Get-Command Remove-ReparsePoint).Definition
        '}'
        ''
        'function Remove-DirectoryTreeSafely {'
        (Get-Command Remove-DirectoryTreeSafely).Definition
        '}'
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
        '    if (Test-PathContainsReparsePoint (Split-Path -Parent $InstallDirectory)) {'
        '        break'
        '    }'
        ''
        '    Remove-DirectoryTreeSafely $InstallDirectory'
        '    if (-not (Test-Path -LiteralPath $InstallDirectory)) {'
        '        break'
        '    }'
        ''
        '    Start-Sleep -Milliseconds 500'
        '}'
        ''
        'Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue'
    ) | Set-Content -LiteralPath $cleanupScript -Encoding ASCII

    # Trailing separators must go: a path ending in "\" would escape the closing quote of the
    # argument below, swallowing -WaitProcessIds into the -InstallDirectory value.
    $cleanupTarget = $InstallDirectory.TrimEnd([char[]]@('\', '/'))
    $cleanupArguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallDirectory "{1}" -WaitProcessIds "{2}"' -f
        $cleanupScript.TrimEnd([char[]]@('\', '/')),
        $cleanupTarget,
        ($waitIds -join ",")
    Start-Process -FilePath "powershell.exe" -ArgumentList $cleanupArguments -WindowStyle Hidden
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
Assert-NoReparsePointInExistingPath -Path $outputDirectory
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

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
    Invoke-IExpress -SedPath $sedPath -WorkingDirectory $buildRoot

    if (-not (Test-Path -LiteralPath $outputPathFull -PathType Leaf) -or
        (Get-Item -LiteralPath $outputPathFull).Length -eq 0) {
        throw "Uninstall executable was not created: $outputPathFull"
    }
} finally {
    Remove-DirectoryIfExists $buildRoot $outputDirectory
}

Write-Info "Uninstall executable: $outputPathFull"
Write-Output $outputPathFull
