<#
.SYNOPSIS
Installs Streamlink VLC Studio and its Windows runtime dependencies.

.DESCRIPTION
Installs only checksummed release assets and dependencies pinned in the
checked-in dependency manifest. GitHub Actions artifacts are available only
through the explicit developer-only mode with a trusted-main commit check.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\StreamlinkVlcStudio"),
    [ValidatePattern("^[^/\s]+/[^/\s]+$")]
    [string]$GitHubRepository = "CorontoSiete/streamlink-vlc-studio",
    [string[]]$AppAssetPatterns = @(
        "^StreamlinkVlcStudio-release\.zip$"
    ),
    [ValidateSet("Auto", "Release", "Artifact", "GitHub", "Local")]
    [string]$AppSource = "Auto",
    [switch]$DeveloperArtifact,
    [ValidatePattern("^[0-9a-fA-F]{40}$")]
    [string]$ExpectedCommit,
    [ValidatePattern("^\.github/workflows/[A-Za-z0-9._/-]+\.ya?ml$")]
    [string]$ExpectedWorkflowPath = ".github/workflows/build.yml",
    [string]$DependencyManifest,
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
. (Join-Path $script:ScriptDirectory "lib\common.ps1")
. (Join-Path $script:ScriptDirectory "lib\install-state.ps1")
. (Join-Path $script:ScriptDirectory "lib\dependency-manifest.ps1")
. (Join-Path $script:ScriptDirectory "lib\release-contract.ps1")
$releaseContractCandidates = @(
    (Join-Path $script:ScriptDirectory "release-contract.json"),
    (Join-Path $script:ScriptDirectory "..\shared\release-contract.json")
)
$releaseContractPath = $releaseContractCandidates |
    ForEach-Object { [IO.Path]::GetFullPath($_) } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($releaseContractPath)) {
    throw "Release contract was not found beside the installer or in the source tree."
}
$script:ReleaseContract = Read-ReleaseContract $releaseContractPath
$script:UserAgent = "StreamlinkVlcStudioInstaller/1.0 (+https://github.com/$GitHubRepository)"
$script:TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("StreamlinkVlcStudio-installer-" + [Guid]::NewGuid().ToString("N"))
$script:RebootRequired = $false
$script:MaximumDownloadBytes = 512MB
$script:MaximumChecksumBytes = 1MB

if ([string]::Equals($AppSource, "Artifact", [StringComparison]::OrdinalIgnoreCase)) {
    if (-not $DeveloperArtifact -or [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
        throw "AppSource Artifact is developer-only and requires both -DeveloperArtifact and -ExpectedCommit <40-hex trusted-main commit>."
    }
} elseif ($DeveloperArtifact) {
    throw "-DeveloperArtifact is valid only with -AppSource Artifact."
}

$dependencyManifestCandidates = if ([string]::IsNullOrWhiteSpace($DependencyManifest)) {
    @(
        (Join-Path $script:ScriptDirectory "dependencies\windows-installers.json"),
        (Join-Path $script:ScriptDirectory "..\dependencies\windows-installers.json")
    )
} else {
    @($DependencyManifest)
}
$dependencyManifestPath = $dependencyManifestCandidates |
    ForEach-Object { [IO.Path]::GetFullPath($_) } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($dependencyManifestPath)) {
    throw "Locked dependency manifest was not found. Supply -DependencyManifest or use a complete release package."
}
$script:DependencyManifest = Read-WindowsDependencyManifest $dependencyManifestPath
if ($script:DependencyManifest.schemaVersion -ne 1 -or
    $null -eq $script:DependencyManifest.dependencies.streamlink -or
    $null -eq $script:DependencyManifest.dependencies.vlc) {
    throw "Unsupported or incomplete dependency manifest: $dependencyManifestPath"
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

function Assert-SafeInstallDirectory([string]$Directory) {
    if ([string]::IsNullOrWhiteSpace($Directory)) {
        throw "InstallDir cannot be empty."
    }

    $fullPath = Get-FullPathNormalized $Directory

    $blockedPaths = @(
        [IO.Path]::GetPathRoot($fullPath),
        $env:USERPROFILE,
        $env:APPDATA,
        $env:LOCALAPPDATA,
        $env:ProgramData,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:windir
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Get-FullPathNormalized $_ }

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
    if (-not (Test-SafeWindowsPathSegment $FileName)) {
        throw "Download file name must be a non-empty leaf name: $FileName"
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

function Get-GitHubWorkflowRun([string]$Repository, [int64]$RunId) {
    Invoke-GitHubApi "https://api.github.com/repos/$Repository/actions/runs/$RunId"
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

function Select-AppArtifact($ArtifactsResponse, [string]$Commit) {
    $expectedName = "StreamlinkVlcStudio-release-$Commit"
    $artifacts = @($ArtifactsResponse.artifacts) |
        Where-Object {
            -not $_.expired -and
            [string]::Equals([string]$_.name, $expectedName, [StringComparison]::Ordinal) -and
            $_.workflow_run.head_branch -eq "main" -and
            [string]::Equals([string]$_.workflow_run.head_sha, $Commit, [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object created_at -Descending

    $artifact = $artifacts | Select-Object -First 1
    if ($null -ne $artifact) {
        return $artifact
    }

    $available = ($artifacts | ForEach-Object { $_.name }) -join ", "
    if ([string]::IsNullOrWhiteSpace($available)) {
        $available = "(none)"
    }

    throw "No non-expired trusted-main artifact named '$expectedName' for commit $Commit was found. Available artifacts: $available"
}

function Get-BoundedDownloadLength($Value, [string]$Description, [long]$MaximumBytes) {
    [long]$length = 0
    if (-not [long]::TryParse(
            ([string]$Value),
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$length) -or
        $length -le 0 -or
        $length -gt $MaximumBytes) {
        throw "$Description must declare an integer size from 1 through $MaximumBytes bytes."
    }

    $length
}

function Save-GitHubAsset($Asset, [string]$DestinationPath, [long]$MaximumBytes = $script:MaximumDownloadBytes) {
    Write-Detail "Downloading $($Asset.name)"
    $expectedBytes = Get-BoundedDownloadLength $Asset.size "GitHub asset '$($Asset.name)'" $MaximumBytes
    Save-GitHubDownloadUrl $Asset.url $DestinationPath $expectedBytes $expectedBytes
}

function Save-BoundedDownload(
    [string]$Uri,
    [string]$DestinationPath,
    [hashtable]$Headers,
    [long]$MaximumBytes = $script:MaximumDownloadBytes,
    [long]$ExpectedBytes = 0) {
    [uri]$downloadUri = $null
    if (-not [uri]::TryCreate($Uri, [UriKind]::Absolute, [ref]$downloadUri) -or
        -not [string]::Equals($downloadUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Download URL must be absolute HTTPS: $Uri"
    }

    $validationScript = $null
    if ($ExpectedBytes -gt 0) {
        $expectedLength = $ExpectedBytes
        $sourceUri = $Uri
        $validationScript = {
            param([string]$Path)
            if ((Get-Item -LiteralPath $Path -Force).Length -ne $expectedLength) {
                throw "Download size mismatch for $sourceUri. Expected $expectedLength bytes."
            }
        }.GetNewClosure()
    }

    Save-HttpFileAtomically `
        -Uri $downloadUri `
        -DestinationPath $DestinationPath `
        -Headers $Headers `
        -TimeoutSeconds $HttpTimeoutSeconds `
        -MaximumBytes $MaximumBytes `
        -ValidationScript $validationScript | Out-Null

    if (-not (Test-Path -LiteralPath $DestinationPath -PathType Leaf) -or
        (Get-Item -LiteralPath $DestinationPath).Length -eq 0) {
        throw "Download failed or produced an empty file: $DestinationPath"
    }
}

function Save-GitHubDownloadUrl(
    [string]$Uri,
    [string]$DestinationPath,
    [long]$MaximumBytes = $script:MaximumDownloadBytes,
    [long]$ExpectedBytes = 0) {
    Save-BoundedDownload `
        $Uri `
        $DestinationPath `
        (Get-HttpHeaders -GitHubAsset) `
        $MaximumBytes `
        $ExpectedBytes
}

function Read-ChecksumManifest([string]$Path) {
    $checksums = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64}) \*(?<name>[^\\/]+)$') {
            throw "Malformed SHA256SUMS line: $line"
        }
        $name = $Matches.name
        if ($checksums.ContainsKey($name)) {
            throw "Duplicate checksum entry: $name"
        }
        $checksums[$name] = $Matches.hash.ToLowerInvariant()
    }
    if ($checksums.Count -eq 0) {
        throw "Checksum manifest is empty: $Path"
    }
    $checksums
}

function Assert-FileChecksum([string]$Path, [hashtable]$Checksums) {
    $name = [IO.Path]::GetFileName($Path)
    if (-not $Checksums.ContainsKey($name)) {
        throw "Release checksum manifest does not contain '$name'."
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actual, [string]$Checksums[$name], [StringComparison]::Ordinal)) {
        throw "Release checksum mismatch for '$name'. Expected $($Checksums[$name]), found $actual."
    }
    Write-Detail "Verified SHA-256 $actual for $name"
}

function Save-Uri(
    [string]$Uri,
    [string]$DestinationPath,
    [long]$MaximumBytes = $script:MaximumDownloadBytes,
    [long]$ExpectedBytes = 0) {
    Write-Detail "Downloading $Uri"
    Save-BoundedDownload $Uri $DestinationPath (Get-HttpHeaders) $MaximumBytes $ExpectedBytes
}

function Normalize-PathCandidate([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }
    $candidate = $Path.Trim()
    if ($candidate.Length -ge 2 -and $candidate[0] -eq '"' -and $candidate[$candidate.Length - 1] -eq '"') {
        $candidate = $candidate.Substring(1, $candidate.Length - 2)
    }
    if ([string]::IsNullOrWhiteSpace($candidate) -or
        $candidate.Contains('"') -or
        $candidate.IndexOfAny([char[]](0..31)) -ge 0 -or
        -not [IO.Path]::IsPathRooted($candidate)) {
        return $null
    }
    try {
        [IO.Path]::GetFullPath($candidate)
    } catch {
        $null
    }
}

function Find-OnPath([string]$FileName) {
    foreach ($directory in ($env:PATH -split [IO.Path]::PathSeparator)) {
        $candidateDirectory = Normalize-PathCandidate $directory
        if ([string]::IsNullOrWhiteSpace($candidateDirectory)) {
            continue
        }

        if (-not (Test-Path -LiteralPath $candidateDirectory -PathType Container)) {
            continue
        }

        $candidate = Join-Path $candidateDirectory $FileName
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    $null
}

function Get-StreamlinkCandidatePaths {
    $candidatePaths = @(
        (Normalize-PathCandidate $env:STREAMLINK_PATH),
        (Find-OnPath "streamlink.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Streamlink\bin\streamlink.exe"),
        (Join-Path $env:ProgramFiles "Streamlink\bin\streamlink.exe")
    )

    foreach ($candidate in $candidatePaths) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            [IO.Path]::GetFullPath($candidate)
        }
    }
}

function Find-Streamlink {
    @(Get-StreamlinkCandidatePaths) | Select-Object -First 1
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

function Get-VlcCandidateDirectories {
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
            [IO.Path]::GetFullPath($directory)
            continue
        }

        $parent = Split-Path -Parent $directory
        if (-not [string]::IsNullOrWhiteSpace($parent) -and
            (Test-Path -LiteralPath (Join-Path $parent "libvlc.dll") -PathType Leaf)) {
            [IO.Path]::GetFullPath($parent)
        }
    }
}

function Find-VlcDirectory {
    @(Get-VlcCandidateDirectories | Select-Object -Unique) | Select-Object -First 1
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

function Assert-DownloadedDependency([string]$Path, $Dependency) {
    $result = Assert-PinnedInstallerDependency -Path $Path -Dependency $Dependency
    Write-Detail "Verified $($Dependency.fileName): $($Dependency.version), SHA-256 $($result.Sha256), Authenticode $($result.Authenticode)"
}

function Start-Installer([string]$FilePath, [string[]]$ArgumentList, [string]$Name) {
    Write-Detail "Running $Name installer"
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -Wait -PassThru
    if ($process.ExitCode -notin @(0, 1641, 3010)) {
        throw "$Name installer failed with exit code $($process.ExitCode). Installer path: $FilePath"
    }
    if ($process.ExitCode -in @(1641, 3010)) {
        $script:RebootRequired = $true
        Write-Detail "$Name completed successfully and requested a reboot (exit code $($process.ExitCode))."
    }
}

function Stop-AppIfNeeded {
    $installRoot = [IO.Path]::GetFullPath($InstallDir)
    $running = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -in @("StreamlinkVlcStudio.exe", "StreamlinkVlcStudio.App.Wpf.exe", "vlc_chat_overlay.exe") -and
            -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
            (Test-PathIsSameOrUnderDirectory ([IO.Path]::GetFullPath($_.ExecutablePath)) $installRoot)
        })
    if ($running.Count -eq 0) {
        return
    }

    if (-not $ForceStopApp) {
        $names = ($running | ForEach-Object { "$($_.Name)($($_.ProcessId))" }) -join ", "
        throw "Streamlink VLC Studio is running: $names. Close it and rerun the installer, or rerun with -ForceStopApp."
    }

    Write-Detail "Stopping running app process before update"
    $ids = @($running | ForEach-Object { [int]$_.ProcessId })
    $ids | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
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

function Assert-AppPayload([string]$PayloadRoot) {
    Assert-ReleasePayload -PayloadRoot $PayloadRoot -Contract $script:ReleaseContract
}

function Install-AppPayloadAtomically([string]$PayloadRoot, [string]$SourceDescription) {
    $source = [IO.Path]::GetFullPath($PayloadRoot).TrimEnd([char[]]@('\', '/'))
    $destination = [IO.Path]::GetFullPath($InstallDir).TrimEnd([char[]]@('\', '/'))
    Assert-AppPayload $source

    if ([string]::Equals($source, $destination, [StringComparison]::OrdinalIgnoreCase)) {
        Assert-OwnedOrEmptyInstallDestination $destination | Out-Null
        Write-Detail "Using owned app in place at $destination"
        return (Join-Path $destination "StreamlinkVlcStudio.exe")
    }
    if ((Test-PathIsSameOrUnderDirectory $destination $source) -or
        (Test-PathIsSameOrUnderDirectory $source $destination)) {
        throw "App payload and InstallDir cannot contain one another. Source: $source Destination: $destination"
    }

    $existingState = Assert-OwnedOrEmptyInstallDestination $destination
    $parent = Split-Path -Parent $destination
    Assert-NoReparsePointInExistingPath $parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Assert-NoReparsePointInExistingPath $parent
    $leaf = Split-Path -Leaf $destination
    $operationId = [Guid]::NewGuid().ToString("N")
    $stage = Join-Path $parent (".$leaf.stage-$operationId")
    $backup = Join-Path $parent (".$leaf.backup-$operationId")
    Assert-UnderDirectory -ChildPath $stage -ParentPath $parent
    Assert-UnderDirectory -ChildPath $backup -ParentPath $parent

    $movedExisting = $false
    $installedStage = $false
    try {
        New-Item -ItemType Directory -Path $stage | Out-Null
        Copy-DirectoryContents $source $stage
        Assert-AppPayload $stage
        $managedPaths = @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force | ForEach-Object {
            Get-InstallRelativePath $stage $_.FullName
        } | Where-Object { $_ -notin @($script:InstallOwnerFileName, $script:InstallManifestFileName) })

        if ($null -ne $existingState) {
            Copy-UnmanagedInstallFiles `
                -ExistingDirectory $destination `
                -StagingDirectory $stage `
                -ExistingState $existingState
        }
        $installId = if ($null -ne $existingState) { [string]$existingState.Owner.installId } else { [Guid]::NewGuid().ToString("D") }
        Write-InstallOwnershipState -Directory $stage -InstallId $installId -ManagedRelativePaths $managedPaths | Out-Null
        Read-InstallOwnershipState $stage | Out-Null

        Stop-AppIfNeeded
        if (Test-Path -LiteralPath $destination -PathType Container) {
            [IO.Directory]::Move($destination, $backup)
            $movedExisting = $true
        }
        [IO.Directory]::Move($stage, $destination)
        $installedStage = $true
    } catch {
        $failure = $_
        if ($movedExisting -and -not (Test-Path -LiteralPath $destination) -and
            (Test-Path -LiteralPath $backup -PathType Container)) {
            [IO.Directory]::Move($backup, $destination)
            $movedExisting = $false
        }
        throw "Atomic app installation failed; the previous installation was restored when possible. $($failure.Exception.Message)"
    } finally {
        if (Test-Path -LiteralPath $stage -PathType Container) {
            Remove-DirectoryTreeSafely $stage
        }
    }

    if ($installedStage -and (Test-Path -LiteralPath $backup -PathType Container)) {
        try {
            Remove-DirectoryTreeSafely $backup
        } catch {
            Write-Warning "The upgrade succeeded, but its rollback backup could not be removed: $backup"
        }
    }

    $appExe = Join-Path $destination "StreamlinkVlcStudio.exe"
    Write-Detail "App installed from $SourceDescription to $destination"
    $appExe
}

function Resolve-AppPayloadRoot([string]$ExtractDirectory, [int]$NestedArchiveDepth = 0) {
    $payloadRoot = Resolve-ReleasePayloadRoot `
        -ExtractedRoot $ExtractDirectory `
        -Contract $script:ReleaseContract `
        -AllowNone
    if (-not [string]::IsNullOrWhiteSpace($payloadRoot)) {
        return $payloadRoot
    }

    # GitHub Actions wraps uploaded files in an artifact archive. The current CI
    # artifact contains the distributable release zip rather than a loose app exe.
    if ($NestedArchiveDepth -eq 0) {
        $nestedReleaseZips = @(Get-ChildItem -LiteralPath $ExtractDirectory -Force -Recurse -File |
            Where-Object {
                $_.Name -eq "StreamlinkVlcStudio-release.zip" -or
                    $_.Name -eq "StreamlinkVlcStudio.zip"
            })
        if ($nestedReleaseZips.Count -gt 1) {
            throw "The app package contains multiple nested release archives; refusing to guess."
        }
        if ($nestedReleaseZips.Count -eq 1) {
            $nestedReleaseZip = $nestedReleaseZips[0]
            $nestedExtractDirectory = Join-Path $ExtractDirectory ("nested-release-" + [Guid]::NewGuid().ToString("N"))
            Expand-ValidatedZipArchive $nestedReleaseZip.FullName $nestedExtractDirectory
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

    Install-AppPayloadAtomically $payloadRoot "local package"
}

function Install-AppFromPackageFile([string]$PackagePath, [string]$SourceDescription) {
    $extension = [IO.Path]::GetExtension($PackagePath)
    if ([string]::Equals($extension, ".zip", [StringComparison]::OrdinalIgnoreCase)) {
        $extractDirectory = Join-Path $script:TempRoot ("app-" + [Guid]::NewGuid().ToString("N"))
        Expand-ValidatedZipArchive $PackagePath $extractDirectory
        $payloadRoot = Resolve-AppPayloadRoot $extractDirectory
        return Install-AppPayloadAtomically $payloadRoot $SourceDescription
    } elseif ([string]::Equals($extension, ".exe", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Loose executable app packages are not accepted; use a checksummed complete release zip."
    } else {
        throw "Unsupported app package type: $PackagePath"
    }
}

function Install-AppFromGitHubRelease {
    Write-Step "Installing Streamlink VLC Studio from GitHub release"
    try {
        $release = Get-GitHubLatestRelease $GitHubRepository
    } catch {
        throw "Could not read the latest GitHub release for $GitHubRepository. Original error: $($_.Exception.Message)"
    }

    if ($release.draft -or $release.prerelease) {
        throw "Latest GitHub release is not a final published release: $($release.tag_name)"
    }
    $asset = Select-ReleaseAsset $release $AppAssetPatterns "Streamlink VLC Studio app"
    $checksumAsset = Select-ReleaseAsset $release @("^SHA256SUMS\.txt$") "release checksum manifest"
    $downloadPath = Get-TempDownloadPath $asset.name
    $checksumPath = Get-TempDownloadPath ("release-" + [Guid]::NewGuid().ToString("N") + "-SHA256SUMS.txt")

    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    Save-GitHubAsset $asset $downloadPath
    Save-GitHubAsset $checksumAsset $checksumPath $script:MaximumChecksumBytes
    if ((Get-Item -LiteralPath $checksumPath).Length -gt 1MB) {
        throw "Release checksum manifest is unexpectedly large."
    }
    Assert-FileChecksum $downloadPath (Read-ChecksumManifest $checksumPath)
    Install-AppFromPackageFile $downloadPath "GitHub release $($release.tag_name)"
}

function Install-AppFromGitHubArtifact {
    Write-Step "Installing Streamlink VLC Studio from GitHub Actions artifact"
    try {
        $artifactsResponse = Get-GitHubArtifacts $GitHubRepository
    } catch {
        throw "Could not read GitHub Actions artifacts for $GitHubRepository. Original error: $($_.Exception.Message)"
    }

    $artifact = Select-AppArtifact $artifactsResponse $ExpectedCommit
    $run = Get-GitHubWorkflowRun $GitHubRepository ([int64]$artifact.workflow_run.id)
    if ($run.status -ne "completed" -or $run.conclusion -ne "success" -or
        $run.head_branch -ne "main" -or
        -not [string]::Equals([string]$run.head_sha, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase) -or
        $run.event -notin @("push", "workflow_dispatch") -or
        -not [string]::Equals([string]$run.path, $ExpectedWorkflowPath, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$run.repository.full_name, $GitHubRepository, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact run $($run.id) is not a successful trusted-main run of $ExpectedWorkflowPath for expected commit $ExpectedCommit."
    }
    $downloadPath = Get-TempDownloadPath ($artifact.name + ".zip")

    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    Write-Detail "Downloading artifact $($artifact.name)"
    $artifactBytes = Get-BoundedDownloadLength `
        $artifact.size_in_bytes `
        "GitHub artifact '$($artifact.name)'" `
        $script:MaximumDownloadBytes
    Save-GitHubDownloadUrl $artifact.archive_download_url $downloadPath $artifactBytes $artifactBytes
    $artifactExtract = Join-Path $script:TempRoot ("artifact-" + [Guid]::NewGuid().ToString("N"))
    Expand-ValidatedZipArchive $downloadPath $artifactExtract
    $checksumMatches = @(Get-ChildItem -LiteralPath $artifactExtract -Recurse -File -Filter "SHA256SUMS.txt")
    $releaseMatches = @(Get-ChildItem -LiteralPath $artifactExtract -Recurse -File -Filter "StreamlinkVlcStudio-release.zip")
    if ($checksumMatches.Count -ne 1 -or $releaseMatches.Count -ne 1) {
        throw "Trusted developer artifact must contain exactly one release zip and one SHA256SUMS.txt."
    }
    Assert-FileChecksum $releaseMatches[0].FullName (Read-ChecksumManifest $checksumMatches[0].FullName)
    Install-AppFromPackageFile $releaseMatches[0].FullName "trusted-main artifact $($artifact.name) for $ExpectedCommit"
}

function Install-App {
    if ([string]::Equals($AppSource, "Local", [StringComparison]::OrdinalIgnoreCase)) {
        return Install-AppFromLocalPayload
    }

    if ([string]::Equals($AppSource, "Artifact", [StringComparison]::OrdinalIgnoreCase)) {
        return Install-AppFromGitHubArtifact
    }

    if ($AppSource -in @("Release", "GitHub")) {
        return Install-AppFromGitHubRelease
    }

    try {
        return Install-AppFromGitHubRelease
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

function Ensure-LockedStreamlink {
    Write-Step "Checking Streamlink"
    $dependency = $script:DependencyManifest.dependencies.streamlink
    $targetVersion = [string]$dependency.version
    $current = Select-CompatibleDependencyCandidate `
        -CandidatePaths @(Get-StreamlinkCandidatePaths) `
        -MinimumVersion $targetVersion `
        -VersionReader { param($path) Get-StreamlinkVersion $path } `
        -Description "Streamlink" `
        -AllowNone
    $currentPath = if ($null -ne $current) { [string]$current.Path } else { "" }
    $currentVersion = if ($null -ne $current) { [string]$current.ReportedVersion } else { "" }
    $currentComparable = if ($null -ne $current) { $current.ParsedVersion } else { $null }
    $targetComparable = ConvertTo-DependencyVersion $targetVersion

    if (-not $ForceDependencyUpdate -and $currentPath -and $null -ne $currentComparable -and
        $null -ne $targetComparable -and $currentComparable -ge $targetComparable) {
        $state = if ($currentComparable -gt $targetComparable) { "newer than locked $targetVersion" } else { "matches locked $targetVersion" }
        Write-Detail "Streamlink $currentVersion ($state) found at $currentPath"
        return $currentPath
    }
    if ($ForceDependencyUpdate -and $currentPath -and $null -ne $currentComparable -and
        $null -ne $targetComparable -and $currentComparable -gt $targetComparable) {
        Write-Detail "ForceDependencyUpdate explicitly permits downgrade from Streamlink $currentVersion to $targetVersion."
    }

    $downloadPath = Get-TempDownloadPath ([string]$dependency.fileName)
    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    $expectedBytes = Get-BoundedDownloadLength `
        $dependency.length `
        "Streamlink dependency" `
        $script:MaximumDownloadBytes
    Save-Uri ([string]$dependency.url) $downloadPath $expectedBytes $expectedBytes
    Assert-DownloadedDependency $downloadPath $dependency
    Start-Installer $downloadPath @("/S") "Streamlink"

    $installed = Select-CompatibleDependencyCandidate `
        -CandidatePaths @(Get-StreamlinkCandidatePaths) `
        -MinimumVersion $targetVersion `
        -VersionReader { param($path) Get-StreamlinkVersion $path } `
        -Description "Streamlink"
    Write-Detail "Streamlink $($installed.ReportedVersion) ready at $($installed.Path)"
    $installed.Path
}

function Ensure-LockedVlc {
    Write-Step "Checking VLC"
    $dependency = $script:DependencyManifest.dependencies.vlc
    $targetVersion = [string]$dependency.version
    $current = Select-CompatibleDependencyCandidate `
        -CandidatePaths @(Get-VlcCandidateDirectories | Select-Object -Unique) `
        -MinimumVersion $targetVersion `
        -VersionReader { param($path) Get-VlcVersion $path } `
        -Description "VLC" `
        -AllowNone
    $currentDirectory = if ($null -ne $current) { [string]$current.Path } else { "" }
    $currentVersion = if ($null -ne $current) { [string]$current.ReportedVersion } else { "" }
    $currentComparable = if ($null -ne $current) { $current.ParsedVersion } else { $null }
    $targetComparable = ConvertTo-DependencyVersion $targetVersion

    if (-not $ForceDependencyUpdate -and $currentDirectory -and $null -ne $currentComparable -and
        $null -ne $targetComparable -and $currentComparable -ge $targetComparable) {
        $state = if ($currentComparable -gt $targetComparable) { "newer than locked $targetVersion" } else { "matches locked $targetVersion" }
        Write-Detail "VLC $currentVersion ($state) found at $currentDirectory"
        return $currentDirectory
    }
    if ($ForceDependencyUpdate -and $currentDirectory -and $null -ne $currentComparable -and
        $null -ne $targetComparable -and $currentComparable -gt $targetComparable) {
        Write-Detail "ForceDependencyUpdate explicitly permits downgrade from VLC $currentVersion to $targetVersion."
    }

    $downloadPath = Get-TempDownloadPath ([string]$dependency.fileName)
    New-Item -ItemType Directory -Path $script:TempRoot -Force | Out-Null
    $expectedBytes = Get-BoundedDownloadLength `
        $dependency.length `
        "VLC dependency" `
        $script:MaximumDownloadBytes
    Save-Uri ([string]$dependency.url) $downloadPath $expectedBytes $expectedBytes
    Assert-DownloadedDependency $downloadPath $dependency
    $msiArguments = @("/i", ('"' + $downloadPath + '"'), "/qn", "/norestart")
    Start-Installer (Join-Path $env:SystemRoot "System32\msiexec.exe") $msiArguments "VLC"

    $installed = Select-CompatibleDependencyCandidate `
        -CandidatePaths @(Get-VlcCandidateDirectories | Select-Object -Unique) `
        -MinimumVersion $targetVersion `
        -VersionReader { param($path) Get-VlcVersion $path } `
        -Description "VLC"
    Write-Detail "VLC $($installed.ReportedVersion) ready at $($installed.Path)"
    $installed.Path
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
            if ($loaded -isnot [pscustomobject]) {
                throw "Settings JSON must contain an object at the document root."
            }
            $settings = $loaded
        } catch {
            $backupPath = Join-Path $settingsDirectory (
                "settings.json.invalid-{0}-{1}" -f
                    [DateTimeOffset]::UtcNow.ToString("yyyyMMddHHmmssfff"),
                    [Guid]::NewGuid().ToString("N"))
            Move-Item -LiteralPath $settingsPath -Destination $backupPath
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

    $streamlinkPath = if ($SkipStreamlink) { Find-Streamlink } else { Ensure-LockedStreamlink }
    $vlcDirectory = if ($SkipVlc) { Find-VlcDirectory } else { Ensure-LockedVlc }

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
    if ($script:RebootRequired) {
        Write-Detail "A dependency installer requested a reboot to complete installation."
    }

    if ($Launch -and -not [string]::IsNullOrWhiteSpace($appExe)) {
        Start-Process -FilePath $appExe
    }
} finally {
    Remove-TempRoot
}
