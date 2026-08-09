<#
.SYNOPSIS
Installs Streamlink VLC Studio and its Windows runtime dependencies.

.DESCRIPTION
The app and Streamlink are resolved through GitHub's latest release API at
install time, so this script does not need a hard-coded app or Streamlink
version. VLC is resolved from VideoLAN's official latest Windows x64 installer.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\StreamlinkVlcStudio"),
    [ValidatePattern("^[^/\s]+/[^/\s]+$")]
    [string]$GitHubRepository = "CorontoSiete/streamlink-vlc-studio",
    [ValidatePattern("^[^/\s]+/[^/\s]+$")]
    [string]$StreamlinkGitHubRepository = "streamlink/windows-builds",
    [string[]]$AppAssetPatterns = @(
        "^StreamlinkVlcStudio-release\.zip$",
        "^StreamlinkVlcStudio\.zip$",
        "^StreamlinkVlcStudio\.exe$",
        "^StreamlinkVlcStudio.*\.zip$"
    ),
    [string[]]$AppArtifactNamePatterns = @(
        "^StreamlinkVlcStudio-release$",
        "^StreamlinkVlcStudio-exe$",
        "StreamlinkVlcStudio"
    ),
    [ValidateSet("Auto", "GitHub", "Local")]
    [string]$AppSource = "Auto",
    [switch]$SkipApp,
    [switch]$SkipStreamlink,
    [switch]$SkipVlc,
    [switch]$SkipShortcut,
    [switch]$ForceDependencyUpdate,
    [switch]$ForceStopApp,
    [switch]$Launch,
    [ValidateRange(5, 600)]
    [int]$HttpTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

if ([Net.ServicePointManager]::SecurityProtocol -band [Net.SecurityProtocolType]::Tls12) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} else {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

$script:ScriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
$script:UserAgent = "StreamlinkVlcStudioInstaller/1.0 (+https://github.com/$GitHubRepository)"
$script:TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("StreamlinkVlcStudio-installer-" + [Guid]::NewGuid().ToString("N"))

function Normalize-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        return $rootPath
    }

    $fullPath.TrimEnd("\", "/")
}

function Assert-NoReparsePointInExistingPath([string]$Path) {
    $fullPath = Normalize-FullPath $Path
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    $relativePath = $fullPath.Substring($rootPath.Length).Trim([char[]]@('\', '/'))
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return
    }

    $currentPath = $rootPath
    foreach ($segment in ($relativePath -split '[\\/]')) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "InstallDir cannot be inside a symbolic link or directory junction: $currentPath"
        }
    }
}

function Assert-NoReparsePointInDirectoryTree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    Get-ChildItem -LiteralPath $Path -Force | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "InstallDir cannot contain a symbolic link or directory junction: $($_.FullName)"
        }

        if ($_.PSIsContainer) {
            Assert-NoReparsePointInDirectoryTree $_.FullName
        }
    }
}

function Remove-DirectoryTreeSafely([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        if ($item.PSIsContainer) {
            [IO.Directory]::Delete($item.FullName)
        } else {
            [IO.File]::Delete($item.FullName)
        }
        return
    }

    if (-not $item.PSIsContainer) {
        Remove-Item -LiteralPath $item.FullName -Force
        return
    }

    Get-ChildItem -LiteralPath $item.FullName -Force | ForEach-Object {
        if ($_.PSIsContainer -or (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            Remove-DirectoryTreeSafely $_.FullName
        } else {
            Remove-Item -LiteralPath $_.FullName -Force
        }
    }
    [IO.Directory]::Delete($item.FullName)
}

function Assert-SafeInstallDirectory([string]$Directory) {
    $fullPath = Normalize-FullPath $Directory
    if ([string]::IsNullOrWhiteSpace($fullPath)) {
        throw "InstallDir cannot be empty."
    }

    $blockedPaths = @(
        [IO.Path]::GetPathRoot($fullPath),
        $env:USERPROFILE,
        $env:APPDATA,
        $env:LOCALAPPDATA,
        $env:ProgramData,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:windir
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Normalize-FullPath $_ }

    if ($blockedPaths | Where-Object { [string]::Equals($_, $fullPath, [StringComparison]::OrdinalIgnoreCase) }) {
        throw "InstallDir must be a dedicated application subdirectory, not a drive or profile/system root: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        throw "InstallDir points to an existing file: $fullPath"
    }

    Assert-NoReparsePointInExistingPath $fullPath
    Assert-NoReparsePointInDirectoryTree $fullPath

    $fullPath
}

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Detail([string]$Message) {
    Write-Host "    $Message"
}

function Get-TempDownloadPath([string]$FileName) {
    if ([string]::IsNullOrWhiteSpace($FileName) -or
        -not [string]::Equals([IO.Path]::GetFileName($FileName), $FileName, [StringComparison]::Ordinal) -or
        $FileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        -not [string]::Equals($FileName.TrimEnd([char[]]@(' ', '.')), $FileName, [StringComparison]::Ordinal) -or
        $FileName -in @(".", "..")) {
        throw "Download file name must be a non-empty leaf name: $FileName"
    }

    $deviceName = ($FileName -split '\.', 2)[0].TrimEnd(' ')
    if ($deviceName -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
        throw "Download file name cannot be a reserved Windows device name: $FileName"
    }

    Join-Path $script:TempRoot $FileName
}

function Get-HttpHeaders([switch]$GitHub, [switch]$GitHubAsset) {
    $headers = @{
        "User-Agent" = $script:UserAgent
    }

    if ($GitHub) {
        $headers["Accept"] = "application/vnd.github+json"
        $headers["X-GitHub-Api-Version"] = "2022-11-28"
        if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
            $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN"
        }
    }

    if ($GitHubAsset) {
        $headers["Accept"] = "application/octet-stream"
        if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
            $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN"
        }
    }

    return $headers
}

function Invoke-WebRequestCompat([hashtable]$Parameters) {
    $command = Get-Command Invoke-WebRequest
    if ($command.Parameters.ContainsKey("UseBasicParsing")) {
        $Parameters["UseBasicParsing"] = $true
    }

    if (-not $Parameters.ContainsKey("TimeoutSec")) {
        $Parameters["TimeoutSec"] = $HttpTimeoutSeconds
    }

    Invoke-WebRequest @Parameters
}

function Invoke-GitHubApi([string]$Uri) {
    try {
        Invoke-RestMethod -Uri $Uri -Headers (Get-HttpHeaders -GitHub) -TimeoutSec $HttpTimeoutSeconds -ErrorAction Stop
    } catch {
        throw "GitHub API request failed: $Uri. $($_.Exception.Message)"
    }
}

function Get-GitHubLatestRelease([string]$Repository) {
    Invoke-GitHubApi "https://api.github.com/repos/$Repository/releases/latest"
}

function Get-GitHubArtifacts([string]$Repository) {
    Invoke-GitHubApi "https://api.github.com/repos/$Repository/actions/artifacts?per_page=100"
}

function Select-ReleaseAsset($Release, [string[]]$Patterns, [string]$Description) {
    $assets = @($Release.assets)
    foreach ($pattern in $Patterns) {
        $asset = $assets |
            Where-Object { $_.name -match $pattern } |
            Sort-Object name |
            Select-Object -First 1
        if ($null -ne $asset) {
            return $asset
        }
    }

    $available = ($assets | ForEach-Object { $_.name }) -join ", "
    if ([string]::IsNullOrWhiteSpace($available)) {
        $available = "(none)"
    }

    throw "No $Description asset matched '$($Patterns -join "', '")'. Available assets: $available"
}

function Select-AppArtifact($ArtifactsResponse, [string[]]$Patterns) {
    $artifacts = @($ArtifactsResponse.artifacts) |
        Where-Object { -not $_.expired } |
        Sort-Object created_at -Descending

    foreach ($pattern in $Patterns) {
        $artifact = $artifacts |
            Where-Object { $_.name -match $pattern } |
            Select-Object -First 1
        if ($null -ne $artifact) {
            return $artifact
        }
    }

    $available = ($artifacts | ForEach-Object { $_.name }) -join ", "
    if ([string]::IsNullOrWhiteSpace($available)) {
        $available = "(none)"
    }

    throw "No non-expired Streamlink VLC Studio GitHub Actions artifact matched '$($Patterns -join "', '")'. Available artifacts: $available"
}

function Save-GitHubAsset($Asset, [string]$DestinationPath) {
    Write-Detail "Downloading $($Asset.name)"
    Save-GitHubDownloadUrl $Asset.url $DestinationPath
}

function Save-GitHubDownloadUrl([string]$Uri, [string]$DestinationPath) {
    $parameters = @{
        Uri = $Uri
        OutFile = $DestinationPath
        Headers = (Get-HttpHeaders -GitHubAsset)
        ErrorAction = "Stop"
    }
    Invoke-WebRequestCompat $parameters | Out-Null

    if (-not (Test-Path -LiteralPath $DestinationPath -PathType Leaf) -or
        (Get-Item -LiteralPath $DestinationPath).Length -eq 0) {
        throw "Download failed or produced an empty file: $DestinationPath"
    }
}

function Save-Uri([string]$Uri, [string]$DestinationPath) {
    Write-Detail "Downloading $Uri"
    $parameters = @{
        Uri = $Uri
        OutFile = $DestinationPath
        Headers = (Get-HttpHeaders)
        ErrorAction = "Stop"
    }
    Invoke-WebRequestCompat $parameters | Out-Null

    if (-not (Test-Path -LiteralPath $DestinationPath -PathType Leaf) -or
        (Get-Item -LiteralPath $DestinationPath).Length -eq 0) {
        throw "Download failed or produced an empty file: $DestinationPath"
    }
}

function Get-WebContent([string]$Uri) {
    $parameters = @{
        Uri = $Uri
        Headers = (Get-HttpHeaders)
        ErrorAction = "Stop"
    }
    (Invoke-WebRequestCompat $parameters).Content
}

function Normalize-Version([string]$Version) {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        return ""
    }

    $normalized = $Version.Trim()
    if ($normalized.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    $normalized
}

function Test-VersionMatch([string]$InstalledVersion, [string]$TargetVersion) {
    $installed = Normalize-Version $InstalledVersion
    $target = Normalize-Version $TargetVersion
    if ([string]::IsNullOrWhiteSpace($installed) -or [string]::IsNullOrWhiteSpace($target)) {
        return $false
    }

    [string]::Equals($installed, $target, [StringComparison]::OrdinalIgnoreCase) -or
        $installed.StartsWith($target + ".", [StringComparison]::OrdinalIgnoreCase)
}

function Get-StreamlinkVersionFromWindowsBuildTag([string]$TagName) {
    $normalized = Normalize-Version $TagName
    if ($normalized -match "^([0-9]+(?:\.[0-9]+)+)") {
        return $Matches[1]
    }

    $normalized
}

function Normalize-PathCandidate([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $Path.Trim().Trim('"')
}

function Find-OnPath([string]$FileName) {
    foreach ($directory in ($env:PATH -split [IO.Path]::PathSeparator)) {
        $candidateDirectory = Normalize-PathCandidate $directory
        if ([string]::IsNullOrWhiteSpace($candidateDirectory)) {
            continue
        }

        $candidate = Join-Path $candidateDirectory $FileName
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    $null
}

function Find-Streamlink {
    $candidatePaths = @(
        (Normalize-PathCandidate $env:STREAMLINK_PATH),
        (Find-OnPath "streamlink.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Streamlink\bin\streamlink.exe"),
        (Join-Path $env:ProgramFiles "Streamlink\bin\streamlink.exe")
    )

    foreach ($candidate in $candidatePaths) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    $null
}

function Get-StreamlinkVersion([string]$StreamlinkPath) {
    if ([string]::IsNullOrWhiteSpace($StreamlinkPath) -or
        -not (Test-Path -LiteralPath $StreamlinkPath -PathType Leaf)) {
        return ""
    }

    try {
        $output = & $StreamlinkPath --version 2>$null | Select-Object -First 1
        if ($output -match "streamlink\s+v?([0-9][0-9A-Za-z\.\-\+]*)") {
            return $Matches[1]
        }
    } catch {
        return ""
    }

    ""
}

function Find-VlcDirectory {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    $candidateDirectories = @(
        (Normalize-PathCandidate $env:VLC_PLUGIN_PATH),
        (Join-Path $env:ProgramFiles "VideoLAN\VLC"),
        $(if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) { Join-Path $programFilesX86 "VideoLAN\VLC" }),
        $(try { (Get-ItemProperty -Path "HKLM:\SOFTWARE\VideoLAN\VLC" -ErrorAction Stop).InstallDir } catch { $null }),
        $(try { (Get-ItemProperty -Path "HKCU:\SOFTWARE\VideoLAN\VLC" -ErrorAction Stop).InstallDir } catch { $null }),
        $(try { (Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\VideoLAN\VLC" -ErrorAction Stop).InstallDir } catch { $null })
    )

    foreach ($candidate in $candidateDirectories) {
        $directory = Normalize-PathCandidate $candidate
        if ([string]::IsNullOrWhiteSpace($directory)) {
            continue
        }

        if (Test-Path -LiteralPath (Join-Path $directory "libvlc.dll") -PathType Leaf) {
            return [IO.Path]::GetFullPath($directory)
        }

        $parent = Split-Path -Parent $directory
        if (-not [string]::IsNullOrWhiteSpace($parent) -and
            (Test-Path -LiteralPath (Join-Path $parent "libvlc.dll") -PathType Leaf)) {
            return [IO.Path]::GetFullPath($parent)
        }
    }

    $null
}

function Get-VlcVersion([string]$VlcDirectory) {
    $vlcExe = Join-Path $VlcDirectory "vlc.exe"
    if (-not (Test-Path -LiteralPath $vlcExe -PathType Leaf)) {
        return ""
    }

    try {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($vlcExe).ProductVersion
        if ($null -eq $version) {
            $version = ""
        }

        $version = $version.Replace(",", ".")
        if ($version -match "([0-9]+(?:\.[0-9]+)+)") {
            return $Matches[1]
        }
    } catch {
        return ""
    }

    ""
}

function Get-LatestVlcInstallerInfo {
    $baseUri = "https://get.videolan.org/vlc/last/win64/"
    $content = Get-WebContent $baseUri
    $matches = [regex]::Matches($content, 'href="(?<name>vlc-(?<version>[0-9][^"]*)-win64\.exe)"')
    if ($matches.Count -eq 0) {
        throw "Could not find a VLC win64 installer at $baseUri"
    }

    $fileName = $matches[0].Groups["name"].Value
    [pscustomobject]@{
        Version = $matches[0].Groups["version"].Value
        FileName = $fileName
        Uri = ([Uri]::new([Uri]$baseUri, $fileName)).AbsoluteUri
    }
}

function Start-Installer([string]$FilePath, [string[]]$ArgumentList, [string]$Name) {
    Write-Detail "Running $Name installer"
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$Name installer failed with exit code $($process.ExitCode). Installer path: $FilePath"
    }
}

function Stop-AppIfNeeded {
    $running = @(Get-Process -Name "StreamlinkVlcStudio", "StreamlinkVlcStudio.App.Wpf" -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    if (-not $ForceStopApp) {
        $names = ($running | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ", "
        throw "Streamlink VLC Studio is running: $names. Close it and rerun the installer, or rerun with -ForceStopApp."
    }

    Write-Detail "Stopping running app process before update"
    $ids = @($running | ForEach-Object { $_.Id })
    $running | Stop-Process -Force
    try {
        Wait-Process -Id $ids -Timeout 10 -ErrorAction SilentlyContinue
    } catch {
    }
}

function Copy-DirectoryContents([string]$SourceDirectory, [string]$DestinationDirectory) {
    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $SourceDirectory -Force | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "App packages cannot contain symbolic links or directory junctions: $($_.FullName)"
        }

        $destination = Join-Path $DestinationDirectory $_.Name
        if ($_.PSIsContainer) {
            Copy-DirectoryContents $_.FullName $destination
        } else {
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
    }
}

function Test-PathIsSameOrUnderDirectory([string]$ChildPath, [string]$ParentPath) {
    $childFull = [IO.Path]::GetFullPath($ChildPath).TrimEnd("\", "/")
    $parentFull = [IO.Path]::GetFullPath($ParentPath).TrimEnd("\", "/")
    [string]::Equals($childFull, $parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        $childFull.StartsWith($parentFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-AppPayloadRoot([string]$ExtractDirectory, [int]$NestedArchiveDepth = 0) {
    if (Test-Path -LiteralPath (Join-Path $ExtractDirectory "StreamlinkVlcStudio.exe") -PathType Leaf) {
        return $ExtractDirectory
    }

    foreach ($directory in (Get-ChildItem -LiteralPath $ExtractDirectory -Directory)) {
        if (Test-Path -LiteralPath (Join-Path $directory.FullName "StreamlinkVlcStudio.exe") -PathType Leaf) {
            return $directory.FullName
        }
    }

    $exe = Get-ChildItem -LiteralPath $ExtractDirectory -Recurse -Filter "StreamlinkVlcStudio.exe" -File |
        Select-Object -First 1
    if ($null -ne $exe) {
        return (Split-Path -Parent $exe.FullName)
    }

    # GitHub Actions wraps uploaded files in an artifact archive. The current CI
    # artifact contains the distributable release zip rather than a loose app exe.
    if ($NestedArchiveDepth -eq 0) {
        $nestedReleaseZip = Get-ChildItem -LiteralPath $ExtractDirectory -Recurse -File |
            Where-Object {
                $_.Name -eq "StreamlinkVlcStudio-release.zip" -or
                    $_.Name -eq "StreamlinkVlcStudio.zip"
            } |
            Select-Object -First 1
        if ($null -ne $nestedReleaseZip) {
            $nestedExtractDirectory = Join-Path $ExtractDirectory ("nested-release-" + [Guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Path $nestedExtractDirectory -Force | Out-Null
            Expand-Archive -LiteralPath $nestedReleaseZip.FullName -DestinationPath $nestedExtractDirectory -Force
            return Resolve-AppPayloadRoot $nestedExtractDirectory ($NestedArchiveDepth + 1)
        }
    }

    throw "The downloaded app package does not contain StreamlinkVlcStudio.exe."
}

function Find-LocalAppPayloadRoot {
    $candidateDirectories = @(
        $script:ScriptDirectory,
        (Join-Path $script:ScriptDirectory "StreamlinkVlcStudio")
    )

    foreach ($candidateDirectory in $candidateDirectories) {
        if (-not [string]::IsNullOrWhiteSpace($candidateDirectory) -and
            (Test-Path -LiteralPath (Join-Path $candidateDirectory "StreamlinkVlcStudio.exe") -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidateDirectory)
        }
    }

    $null
}

function Install-AppFromLocalPayload {
    Write-Step "Installing Streamlink VLC Studio from local package"
    $payloadRoot = Find-LocalAppPayloadRoot
    if ([string]::IsNullOrWhiteSpace($payloadRoot)) {
        throw "No local StreamlinkVlcStudio.exe was found beside install.ps1. Run from the extracted release zip, publish a GitHub release, or rerun with -SkipApp to install dependencies only."
    }

    $installDirFull = [IO.Path]::GetFullPath($InstallDir)
    $payloadRootFull = [IO.Path]::GetFullPath($payloadRoot)
    New-Item -ItemType Directory -Path $installDirFull -Force | Out-Null
    Stop-AppIfNeeded
    if ([string]::Equals($payloadRootFull.TrimEnd("\", "/"), $installDirFull.TrimEnd("\", "/"), [StringComparison]::OrdinalIgnoreCase)) {
        Write-Detail "Using app in place at $installDirFull"
    } elseif (Test-PathIsSameOrUnderDirectory $installDirFull $payloadRootFull) {
        throw "InstallDir cannot be inside the local package folder because that would recursively copy the package into itself. Choose a folder outside '$payloadRootFull', or run from the final app folder with -InstallDir '$payloadRootFull'."
    } else {
        Copy-DirectoryContents $payloadRootFull $installDirFull
    }

    $appExe = Join-Path $installDirFull "StreamlinkVlcStudio.exe"
    if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
        throw "Installed app executable missing: $appExe"
    }

    Write-Detail "App installed from local package to $installDirFull"
    $appExe
}

function Install-AppFromPackageFile([string]$PackagePath, [string]$SourceDescription) {
    $installDirFull = [IO.Path]::GetFullPath($InstallDir)
    New-Item -ItemType Directory -Path $installDirFull -Force | Out-Null
    Stop-AppIfNeeded

    $extension = [IO.Path]::GetExtension($PackagePath)
    if ([string]::Equals($extension, ".zip", [StringComparison]::OrdinalIgnoreCase)) {
        $extractDirectory = Join-Path $script:TempRoot ("app-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
        Expand-Archive -LiteralPath $PackagePath -DestinationPath $extractDirectory -Force
        $payloadRoot = Resolve-AppPayloadRoot $extractDirectory
        Copy-DirectoryContents $payloadRoot $installDirFull
    } elseif ([string]::Equals($extension, ".exe", [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $PackagePath -Destination (Join-Path $installDirFull "StreamlinkVlcStudio.exe") -Force
    } else {
        throw "Unsupported app package type: $PackagePath"
    }

    $appExe = Join-Path $installDirFull "StreamlinkVlcStudio.exe"
    if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
        throw "Installed app executable missing: $appExe"
    }

    Write-Detail "App installed from $SourceDescription to $installDirFull"
    $appExe
}

function Install-AppFromGitHubRelease {
    Write-Step "Installing Streamlink VLC Studio from GitHub release"
    try {
        $release = Get-GitHubLatestRelease $GitHubRepository
    } catch {
        throw "Could not read the latest GitHub release for $GitHubRepository. Original error: $($_.Exception.Message)"
    }

    $asset = Select-ReleaseAsset $release $AppAssetPatterns "Streamlink VLC Studio app"
    $downloadPath = Get-TempDownloadPath $asset.name

    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    Save-GitHubAsset $asset $downloadPath
    Install-AppFromPackageFile $downloadPath "GitHub release $($release.tag_name)"
}

function Install-AppFromGitHubArtifact {
    Write-Step "Installing Streamlink VLC Studio from GitHub Actions artifact"
    try {
        $artifactsResponse = Get-GitHubArtifacts $GitHubRepository
    } catch {
        throw "Could not read GitHub Actions artifacts for $GitHubRepository. Original error: $($_.Exception.Message)"
    }

    $artifact = Select-AppArtifact $artifactsResponse $AppArtifactNamePatterns
    $downloadPath = Get-TempDownloadPath ($artifact.name + ".zip")

    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    Write-Detail "Downloading artifact $($artifact.name)"
    Save-GitHubDownloadUrl $artifact.archive_download_url $downloadPath
    Install-AppFromPackageFile $downloadPath "GitHub Actions artifact $($artifact.name)"
}

function Install-AppFromGitHub {
    $releaseError = $null
    try {
        return Install-AppFromGitHubRelease
    } catch {
        $releaseError = $_.Exception.Message
        Write-Detail "GitHub release app install failed; trying latest Actions artifact. $releaseError"
    }

    try {
        return Install-AppFromGitHubArtifact
    } catch {
        throw "Could not install the app from GitHub release or GitHub Actions artifacts. Release error: $releaseError Artifact error: $($_.Exception.Message) Make sure $GitHubRepository is public, or set GITHUB_TOKEN to a token with repo/actions read access for a private repository."
    }
}

function Install-App {
    if ([string]::Equals($AppSource, "Local", [StringComparison]::OrdinalIgnoreCase)) {
        return Install-AppFromLocalPayload
    }

    if ([string]::Equals($AppSource, "GitHub", [StringComparison]::OrdinalIgnoreCase)) {
        return Install-AppFromGitHub
    }

    try {
        return Install-AppFromGitHub
    } catch {
        $githubError = $_.Exception.Message
        $localPayloadRoot = Find-LocalAppPayloadRoot
        if (-not [string]::IsNullOrWhiteSpace($localPayloadRoot)) {
            Write-Detail "GitHub latest app install failed; using local package instead. $githubError"
            return Install-AppFromLocalPayload
        }

        throw "$githubError No local app payload was found beside install.ps1. If you are installing from an extracted release zip, make sure install.ps1 is in the same folder as StreamlinkVlcStudio.exe. To install dependencies only, rerun with -SkipApp."
    }
}

function Ensure-LatestStreamlink {
    Write-Step "Checking Streamlink"
    $release = Get-GitHubLatestRelease $StreamlinkGitHubRepository
    $targetVersion = Get-StreamlinkVersionFromWindowsBuildTag $release.tag_name
    $currentPath = Find-Streamlink
    $currentVersion = if ($currentPath) { Get-StreamlinkVersion $currentPath } else { "" }

    if (-not $ForceDependencyUpdate -and
        $currentPath -and
        (Test-VersionMatch $currentVersion $targetVersion)) {
        Write-Detail "Streamlink $currentVersion found at $currentPath"
        return $currentPath
    }

    $asset = Select-ReleaseAsset $release @(
        "streamlink.*(x86_64|amd64|win64|windows).*\.exe$",
        "streamlink.*\.exe$"
    ) "Streamlink Windows installer"

    $downloadPath = Get-TempDownloadPath $asset.name
    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    Save-GitHubAsset $asset $downloadPath
    Start-Installer $downloadPath @("/S") "Streamlink"

    $installedPath = Find-Streamlink
    if (-not $installedPath) {
        throw "Streamlink installer completed, but streamlink.exe was not found."
    }

    $installedVersion = Get-StreamlinkVersion $installedPath
    Write-Detail "Streamlink $installedVersion ready at $installedPath"
    $installedPath
}

function Ensure-LatestVlc {
    Write-Step "Checking VLC"
    $latest = Get-LatestVlcInstallerInfo
    $currentDirectory = Find-VlcDirectory
    $currentVersion = if ($currentDirectory) { Get-VlcVersion $currentDirectory } else { "" }

    if (-not $ForceDependencyUpdate -and
        $currentDirectory -and
        (Test-VersionMatch $currentVersion $latest.Version)) {
        Write-Detail "VLC $currentVersion found at $currentDirectory"
        return $currentDirectory
    }

    $downloadPath = Get-TempDownloadPath $latest.FileName
    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    Save-Uri $latest.Uri $downloadPath
    Start-Installer $downloadPath @("/S") "VLC"

    $installedDirectory = Find-VlcDirectory
    if (-not $installedDirectory) {
        throw "VLC installer completed, but libvlc.dll was not found."
    }

    $installedVersion = Get-VlcVersion $installedDirectory
    Write-Detail "VLC $installedVersion ready at $installedDirectory"
    $installedDirectory
}

function Set-ObjectProperty([object]$Target, [string]$Name, $Value) {
    $property = $Target.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Target | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    } else {
        $property.Value = $Value
    }
}

function Update-AppSettings([string]$StreamlinkPath, [string]$VlcDirectory) {
    if ([string]::IsNullOrWhiteSpace($StreamlinkPath) -and [string]::IsNullOrWhiteSpace($VlcDirectory)) {
        return
    }

    Write-Step "Updating app settings"
    $settingsDirectory = Join-Path $env:APPDATA "StreamlinkVlcStudio"
    $settingsPath = Join-Path $settingsDirectory "settings.json"
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null

    $settings = [pscustomobject]@{}
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try {
            $loaded = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            if ($null -ne $loaded) {
                $settings = $loaded
            }
        } catch {
            $backupPath = Join-Path $settingsDirectory ("settings.json.invalid-" + [DateTimeOffset]::UtcNow.ToString("yyyyMMddHHmmss"))
            Move-Item -LiteralPath $settingsPath -Destination $backupPath -Force
            Write-Detail "Existing settings JSON was invalid and was moved to $backupPath"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($StreamlinkPath)) {
        Set-ObjectProperty $settings "StreamlinkPath" $StreamlinkPath
        Write-Detail "StreamlinkPath = $StreamlinkPath"
    }

    if (-not [string]::IsNullOrWhiteSpace($VlcDirectory)) {
        Set-ObjectProperty $settings "VlcDirectory" $VlcDirectory
        Write-Detail "VlcDirectory = $VlcDirectory"
    }

    $temporarySettingsPath = Join-Path $settingsDirectory ("settings.json.tmp-" + [Guid]::NewGuid().ToString("N"))
    try {
        $settings | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $temporarySettingsPath -Encoding UTF8
        if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
            $backupPath = Join-Path $settingsDirectory ("settings.json.backup-" + [Guid]::NewGuid().ToString("N"))
            try {
                [IO.File]::Replace($temporarySettingsPath, $settingsPath, $backupPath, $true)
            } finally {
                if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                    Remove-Item -LiteralPath $backupPath -Force
                }
            }
        } else {
            Move-Item -LiteralPath $temporarySettingsPath -Destination $settingsPath
        }
    } finally {
        if (Test-Path -LiteralPath $temporarySettingsPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporarySettingsPath -Force
        }
    }
}

function New-StartMenuShortcut([string]$AppExe) {
    if ($SkipShortcut -or [string]::IsNullOrWhiteSpace($AppExe) -or
        -not (Test-Path -LiteralPath $AppExe -PathType Leaf)) {
        return
    }

    Write-Step "Creating shortcut"
    $programsDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)) "Programs"
    New-Item -ItemType Directory -Path $programsDirectory -Force | Out-Null

    $shortcutPath = Join-Path $programsDirectory "Streamlink VLC Studio.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $AppExe
    $shortcut.WorkingDirectory = Split-Path -Parent $AppExe
    $shortcut.IconLocation = $AppExe
    $shortcut.Save()
    Write-Detail $shortcutPath
}

function Get-DirectorySizeKilobytes([string]$Directory) {
    if ([string]::IsNullOrWhiteSpace($Directory) -or
        -not (Test-Path -LiteralPath $Directory -PathType Container)) {
        return 0
    }

    $bytes = 0L
    Get-ChildItem -LiteralPath $Directory -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        $bytes += $_.Length
    }

    [Math]::Max(1, [int][Math]::Ceiling($bytes / 1KB))
}

function Get-AppDisplayVersion([string]$AppExe) {
    try {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($AppExe).ProductVersion
        if (-not [string]::IsNullOrWhiteSpace($version)) {
            return $version
        }
    } catch {
    }

    "1.0.0"
}

function Register-AppUninstallEntry([string]$AppExe) {
    if ([string]::IsNullOrWhiteSpace($AppExe) -or
        -not (Test-Path -LiteralPath $AppExe -PathType Leaf)) {
        return
    }

    $installDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $AppExe))
    $uninstallExe = Join-Path $installDirectory "Uninstall.exe"
    if (-not (Test-Path -LiteralPath $uninstallExe -PathType Leaf)) {
        Write-Detail "Uninstall.exe was not found; Control Panel uninstall registration was skipped."
        return
    }

    Write-Step "Registering uninstaller"
    $registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StreamlinkVlcStudio"
    New-Item -Path $registryPath -Force | Out-Null

    $uninstallCommand = '"' + $uninstallExe + '"'
    $quietUninstallCommand = '"' + $uninstallExe + '" /Q'
    $estimatedSize = Get-DirectorySizeKilobytes $installDirectory
    $displayVersion = Get-AppDisplayVersion $AppExe

    New-ItemProperty -Path $registryPath -Name "DisplayName" -Value "Streamlink VLC Studio" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "DisplayVersion" -Value $displayVersion -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "Publisher" -Value "Streamlink VLC Studio" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "InstallLocation" -Value $installDirectory -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "DisplayIcon" -Value $AppExe -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "UninstallString" -Value $uninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "QuietUninstallString" -Value $quietUninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "URLInfoAbout" -Value "https://github.com/$GitHubRepository" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "NoModify" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "NoRepair" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "EstimatedSize" -Value $estimatedSize -PropertyType DWord -Force | Out-Null

    Write-Detail "Uninstall.exe registered for Control Panel at $uninstallExe"
}

function Remove-TempRoot {
    if ([string]::IsNullOrWhiteSpace($script:TempRoot) -or
        -not (Test-Path -LiteralPath $script:TempRoot)) {
        return
    }

    $tempRootFull = [IO.Path]::GetFullPath($script:TempRoot)
    $systemTempFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd("\", "/")
    if ($tempRootFull.StartsWith($systemTempFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Assert-NoReparsePointInExistingPath (Split-Path -Parent $tempRootFull)
        Remove-DirectoryTreeSafely $tempRootFull
    }
}

$InstallDir = Assert-SafeInstallDirectory $InstallDir

try {
    $appExe = if ($SkipApp) {
        $existingApp = Join-Path ([IO.Path]::GetFullPath($InstallDir)) "StreamlinkVlcStudio.exe"
        if (Test-Path -LiteralPath $existingApp -PathType Leaf) { $existingApp } else { "" }
    } else {
        Install-App
    }

    $streamlinkPath = if ($SkipStreamlink) { Find-Streamlink } else { Ensure-LatestStreamlink }
    $vlcDirectory = if ($SkipVlc) { Find-VlcDirectory } else { Ensure-LatestVlc }

    Update-AppSettings $streamlinkPath $vlcDirectory
    New-StartMenuShortcut $appExe
    Register-AppUninstallEntry $appExe

    Write-Step "Done"
    if (-not [string]::IsNullOrWhiteSpace($appExe)) {
        Write-Detail "App: $appExe"
    }
    if (-not [string]::IsNullOrWhiteSpace($streamlinkPath)) {
        Write-Detail "Streamlink: $streamlinkPath"
    }
    if (-not [string]::IsNullOrWhiteSpace($vlcDirectory)) {
        Write-Detail "VLC: $vlcDirectory"
    }

    if ($Launch -and -not [string]::IsNullOrWhiteSpace($appExe)) {
        Start-Process -FilePath $appExe
    }
} finally {
    Remove-TempRoot
}
