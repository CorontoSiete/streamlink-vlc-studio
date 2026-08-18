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

    (Test-Path -LiteralPath (Join-Path $full ".streamlink-vlc-studio-owner.json") -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $full ".streamlink-vlc-studio-files.json") -PathType Leaf)
}

function Assert-NoReparsePointInTree([string]$Directory) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push((Normalize-FullPath $Directory))
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to uninstall through a symbolic link or junction: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            }
        }
    }
}

function Get-ValidatedOwnershipState([string]$Directory, [switch]$VerifyManagedFiles) {
    $root = Normalize-FullPath $Directory
    if (-not (Test-SafeDeleteDirectory $root)) {
        throw "Refusing to delete unsafe install directory: $root"
    }
    Assert-NoReparsePointInTree $root

    $ownerPath = Join-Path $root ".streamlink-vlc-studio-owner.json"
    $manifestPath = Join-Path $root ".streamlink-vlc-studio-files.json"
    try {
        $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        throw "Installation ownership state is corrupt; no files or registration were removed. $($_.Exception.Message)"
    }

    if ($owner.schemaVersion -ne 1 -or $manifest.schemaVersion -ne 1 -or
        $owner.product -ne "streamlink-vlc-studio" -or $manifest.product -ne "streamlink-vlc-studio" -or
        [string]::IsNullOrWhiteSpace([string]$owner.installId) -or
        $owner.installId -ne $manifest.installId) {
        throw "Invalid Streamlink VLC Studio ownership state; no files or registration were removed."
    }

    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($manifestHash, [string]$owner.manifestSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installation ownership manifest was modified; no files or registration were removed."
    }

    $rootPrefix = $root.TrimEnd([char[]]@(92, 47)) + [IO.Path]::DirectorySeparatorChar
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative.IndexOf([char]0) -ge 0 -or $relative -match '(^|[\/])\.\.([\/]|$)' -or
            -not $seen.Add($relative)) {
            throw "Unsafe or duplicate managed path in installation manifest: $relative"
        }

        $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Managed path escapes the installation: $relative"
        }
        if (-not (Test-Path -LiteralPath $target)) {
            continue
        }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "Managed installation path is not a file: $relative"
        }

        $item = Get-Item -LiteralPath $target -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to remove a managed reparse point: $target"
        }
        if ($VerifyManagedFiles) {
            $actualHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($item.Length -ne [int64]$entry.length -or
                -not [string]::Equals($actualHash, [string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Managed installation file was modified; no files or registration were removed: $relative"
            }
        }
    }

    [pscustomobject]@{
        Root = $root
        OwnerPath = $ownerPath
        ManifestPath = $manifestPath
        Manifest = $manifest
    }
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

function Stop-AppProcesses([string]$InstallDirectory) {
    $install = Normalize-FullPath $InstallDirectory
    $running = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -in @("StreamlinkVlcStudio.exe", "StreamlinkVlcStudio.App.Wpf.exe", "vlc_chat_overlay.exe") -and
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            (Test-PathIsSameOrUnderDirectory $_.ExecutablePath $install)
        })
    if ($running.Count -eq 0) {
        return
    }

    Write-Step "Stopping Streamlink VLC Studio"
    $ids = @($running | ForEach-Object { [int]$_.ProcessId })
    $ids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
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
        'function Test-SafeDeleteDirectory {'
        (Get-Command Test-SafeDeleteDirectory).Definition
        '}'
        ''
        'function Assert-NoReparsePointInTree {'
        (Get-Command Assert-NoReparsePointInTree).Definition
        '}'
        ''
        'function Get-ValidatedOwnershipState {'
        (Get-Command Get-ValidatedOwnershipState).Definition
        '}'
        ''
        'function Write-Step {'
        (Get-Command Write-Step).Definition
        '}'
        ''
        'function Test-PathIsSameOrUnderDirectory {'
        (Get-Command Test-PathIsSameOrUnderDirectory).Definition
        '}'
        ''
        'function Remove-ShortcutIfTargetMatches {'
        (Get-Command Remove-ShortcutIfTargetMatches).Definition
        '}'
        ''
        'function Remove-Shortcuts {'
        (Get-Command Remove-Shortcuts).Definition
        '}'
        ''
        'function Remove-UninstallRegistryEntry {'
        (Get-Command Remove-UninstallRegistryEntry).Definition
        '}'
        ''
        '$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StreamlinkVlcStudio"'
        '$ErrorActionPreference = "Stop"'
        ''
        'try {'
        '    foreach ($idText in ($WaitProcessIds -split ",")) {'
        '        $id = 0'
        '        if ([int]::TryParse($idText, [ref]$id) -and $id -gt 0) {'
        '            Wait-Process -Id $id -Timeout 30 -ErrorAction SilentlyContinue'
        '        }'
        '    }'
        ''
        '    $state = Get-ValidatedOwnershipState $InstallDirectory -VerifyManagedFiles'
        '    $root = $state.Root'
        '    $ownerPath = $state.OwnerPath'
        '    $manifestPath = $state.ManifestPath'
        '    $manifest = $state.Manifest'
        '    foreach ($entry in @($manifest.files)) {'
        '        $relative = [string]$entry.path'
        '        $target = [IO.Path]::GetFullPath((Join-Path $root $relative))'
        '        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {'
        '            continue'
        '        }'
        '        for ($attempt = 0; $attempt -lt 30 -and (Test-Path -LiteralPath $target); $attempt++) {'
        '            Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue'
        '            if (Test-Path -LiteralPath $target) { Start-Sleep -Milliseconds 500 }'
        '        }'
        '        if (Test-Path -LiteralPath $target) {'
        '            throw "Could not remove managed installation file; uninstall registration was preserved: $relative"'
        '        }'
        '    }'
        ''
        '    Remove-Item -LiteralPath $ownerPath -Force -ErrorAction SilentlyContinue'
        '    Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue'
        '    Get-ChildItem -LiteralPath $root -Directory -Recurse -Force -ErrorAction SilentlyContinue |'
        '        Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {'
        '            if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and'
        '                @(Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue).Count -eq 0) {'
        '                [IO.Directory]::Delete($_.FullName)'
        '            }'
        '        }'
        '    if ((Test-Path -LiteralPath $root -PathType Container) -and'
        '        @(Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue).Count -eq 0) {'
        '        [IO.Directory]::Delete($root)'
        '    }'
        '    Remove-Shortcuts $root'
        '    Remove-UninstallRegistryEntry'
        '} finally {'
        '    Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue'
        '}'
    ) | Set-Content -LiteralPath $cleanupScript -Encoding ASCII

    # Trailing separators must go: a path ending in "\" would escape the closing quote of the
    # argument below, swallowing -WaitProcessIds into the -InstallDirectory value.
    $cleanupTarget = $InstallDirectory.TrimEnd([char[]]@('\', '/'))
    $cleanupArguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallDirectory "{1}" -WaitProcessIds "{2}"' -f
        $cleanupScript.TrimEnd([char[]]@('\', '/')),
        $cleanupTarget,
        ($waitIds -join ",")
    $cleanupProcess = Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList $cleanupArguments `
        -WindowStyle Hidden `
        -PassThru `
        -ErrorAction Stop
    if ($null -eq $cleanupProcess) {
        throw "The uninstall cleanup process could not be started. Registration was preserved."
    }
}

$installDirectory = Resolve-InstallDirectory
if ([string]::IsNullOrWhiteSpace($installDirectory)) {
    throw "Could not resolve the Streamlink VLC Studio install directory."
}

$ownershipState = Get-ValidatedOwnershipState $installDirectory -VerifyManagedFiles
$installDirectory = $ownershipState.Root
Stop-AppProcesses $installDirectory
Write-Step "Removing installed files"
Start-InstallDirectoryCleanup $installDirectory
Write-Step "Uninstall cleanup started"
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

$outputLeaf = Split-Path -Leaf $OutputPath
if (-not (Test-SafeWindowsPathSegment $outputLeaf) -or
    -not [string]::Equals([IO.Path]::GetExtension($outputLeaf), '.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must end in a safe .exe file name: $OutputPath"
}

$outputPathFull = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputPathFull
Assert-NoReparsePointInExistingPath -Path $outputDirectory
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$buildRoot = Join-Path $outputDirectory ".uninstaller-build"
if (Test-PathIsSameOrUnderDirectory -ChildPath $outputPathFull -ParentPath $buildRoot) {
    throw "OutputPath cannot be inside the temporary uninstaller build directory: $outputPathFull"
}
Remove-DirectoryIfExists $buildRoot $outputDirectory
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

try {
    $bootstrapPath = Join-Path $buildRoot "Uninstall-StreamlinkVlcStudio-Bootstrap.ps1"
    New-UninstallBootstrapScript $bootstrapPath

    $stagedOutputPath = Join-Path $buildRoot "Uninstall-built.exe"
    $sedPath = Join-Path $buildRoot "Uninstall.sed"
    New-IExpressSed $sedPath $buildRoot $stagedOutputPath

    Write-Info "Building uninstall executable..."
    Invoke-IExpress -SedPath $sedPath -WorkingDirectory $buildRoot

    if (-not (Test-Path -LiteralPath $stagedOutputPath -PathType Leaf) -or
        (Get-Item -LiteralPath $stagedOutputPath).Length -eq 0) {
        throw "Uninstall executable was not created: $stagedOutputPath"
    }
    Promote-ValidatedFileSetAtomically @(
        [pscustomobject]@{ Source = $stagedOutputPath; Destination = $outputPathFull }
    )
} finally {
    Remove-DirectoryIfExists $buildRoot $outputDirectory
}

Write-Info "Uninstall executable: $outputPathFull"
Write-Output $outputPathFull
