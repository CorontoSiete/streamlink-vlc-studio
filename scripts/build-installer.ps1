param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OverlaySource,
    [string]$OutputRoot,
    [string]$ReleaseZip,
    [string]$SetupFileName = "StreamlinkVlcStudio-Setup.msi",
    [string]$BootstrapperFileName = "StreamlinkVlcStudio-Setup.exe",
    [ValidatePattern("^[^/\s]+/[^/\s]+$")]
    [string]$StreamlinkGitHubRepository = "streamlink/windows-builds",
    [string]$VlcDownloadRoot = "https://get.videolan.org/vlc/last/win64/",
    [string]$ProductVersion = "1.0.0",
    [string]$WixVersion = "6.0.2",
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
. (Join-Path $scriptRoot "lib\common.ps1")

function Test-ThreePartProductVersion([string]$Version) {
    if ($Version -notmatch "^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$") {
        return $false
    }

    foreach ($name in @("major", "minor", "patch")) {
        if ([int64]$Matches[$name] -gt 255) {
            return $false
        }
    }

    $true
}

function Ensure-WixTool {
    param([Parameter(Mandatory = $true)][string]$Version)

    if ($Version -notmatch "^\d+\.\d+\.\d+$") {
        throw "WixVersion must be a three-part version: $Version"
    }

    $toolDirectory = Join-Path $repoRoot (".tools\wix-" + $Version)
    $wixPath = Join-Path $toolDirectory "wix.exe"
    if (-not (Test-Path -LiteralPath $wixPath -PathType Leaf)) {
        $dotnetCommand = Get-Command "dotnet" -ErrorAction SilentlyContinue
        if ($null -eq $dotnetCommand) {
            throw "dotnet was not found on PATH. WiX $Version is required to build the MSI."
        }

        Write-Info "Installing WiX $Version command-line tool..."
        & $dotnetCommand.Source tool install `
            --tool-path $toolDirectory `
            wix `
            --version $Version `
            --ignore-failed-sources | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "WiX tool installation failed with exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $wixPath -PathType Leaf)) {
        throw "WiX executable was not created: $wixPath"
    }

    $installedVersion = (& $wixPath --version 2>$null | Select-Object -First 1)
    if ($installedVersion -notmatch [regex]::Escape($Version)) {
        throw "WiX version mismatch. Expected $Version, found '$installedVersion'."
    }

    $requiredExtensions = @(
        "WixToolset.UI.wixext",
        "WixToolset.BootstrapperApplications.wixext",
        "WixToolset.Util.wixext"
    )
    foreach ($extension in $requiredExtensions) {
        $extensionList = (& $wixPath extension list 2>$null | Out-String)
        if ($extensionList -match ([regex]::Escape($extension) + "\s+" + [regex]::Escape($Version))) {
            continue
        }

        Write-Info "Installing WiX $extension $Version..."
        & $wixPath extension add ("{0}/{1}" -f $extension, $Version) | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "WiX $extension installation failed with exit code $LASTEXITCODE."
        }
    }

    $wixPath
}

function Save-DependencyFile {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [int64]$ExpectedLength = 0,
        [string]$ExpectedSha256)

    Write-Info "Downloading $Uri..."
    $headers = @{
        "User-Agent" = "StreamlinkVlcStudioInstallerBuilder/1.0 (+https://github.com/CorontoSiete/streamlink-vlc-studio)"
    }
    Invoke-WebRequest `
        -Uri $Uri `
        -Headers $headers `
        -OutFile $DestinationPath `
        -UseBasicParsing `
        -ErrorAction Stop

    if (-not (Test-Path -LiteralPath $DestinationPath -PathType Leaf)) {
        throw "Dependency download did not create a file: $DestinationPath"
    }

    $item = Get-Item -LiteralPath $DestinationPath
    if ($item.Length -eq 0) {
        throw "Dependency download produced an empty file: $DestinationPath"
    }

    if ($ExpectedLength -gt 0 -and $item.Length -ne $ExpectedLength) {
        throw "Dependency download size mismatch for $DestinationPath. Expected $ExpectedLength bytes, found $($item.Length) bytes."
    }

    $hash = (Get-FileHash -LiteralPath $DestinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and
        -not [string]::Equals($hash, $ExpectedSha256.Trim().ToLowerInvariant(), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Dependency SHA-256 mismatch for $DestinationPath. Expected $ExpectedSha256, found $hash."
    }

    Write-Info "Downloaded $([System.IO.Path]::GetFileName($DestinationPath)) ($($item.Length) bytes, SHA-256 $hash)."
}

function Get-LatestStreamlinkInstallerInfo {
    $apiUri = "https://api.github.com/repos/$StreamlinkGitHubRepository/releases/latest"
    $headers = @{
        "Accept" = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
        "User-Agent" = "StreamlinkVlcStudioInstallerBuilder/1.0 (+https://github.com/CorontoSiete/streamlink-vlc-studio)"
    }

    try {
        $release = Invoke-RestMethod -Uri $apiUri -Headers $headers -ErrorAction Stop
    } catch {
        throw "Could not read the latest Streamlink Windows release from $apiUri. $($_.Exception.Message)"
    }

    $assets = @(@($release.assets) | Where-Object {
        $_.name -match "(?i)^streamlink-\d+(?:\.\d+)+-\d+-py\d+-x86_64\.exe$"
    })
    if ($assets.Count -ne 1) {
        $available = (@($release.assets) | ForEach-Object { $_.name }) -join ", "
        throw "Expected exactly one official x86_64 Streamlink installer in release '$($release.tag_name)', found $($assets.Count). Available assets: $available"
    }

    $asset = $assets[0]
    [pscustomobject]@{
        Version = [string]$release.tag_name
        FileName = [string]$asset.name
        Uri = [string]$asset.browser_download_url
        Length = [int64]$asset.size
        Sha256 = if ([string]$asset.digest -match "(?i)^sha256:(?<hash>[0-9a-f]{64})$") { $Matches["hash"] } else { "" }
    }
}

function Get-LatestVlcMsiInfo {
    $content = Invoke-WebRequest `
        -Uri $VlcDownloadRoot `
        -Headers @{ "User-Agent" = "StreamlinkVlcStudioInstallerBuilder/1.0" } `
        -UseBasicParsing `
        -ErrorAction Stop
    $matches = [regex]::Matches(
        $content.Content,
        'href="(?<name>vlc-(?<version>\d+(?:\.\d+)+)-win64\.msi)"',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($matches.Count -eq 0) {
        throw "Could not find an official VLC win64 MSI at $VlcDownloadRoot"
    }

    $candidates = @($matches | ForEach-Object {
        [pscustomobject]@{
            Version = [version]$_.Groups["version"].Value
            FileName = $_.Groups["name"].Value
        }
    } | Sort-Object Version -Descending)
    $selected = $candidates[0]
    [pscustomobject]@{
        Version = $selected.Version.ToString()
        FileName = $selected.FileName
        Uri = ([System.Uri]::new(([System.Uri]$VlcDownloadRoot), $selected.FileName)).AbsoluteUri
    }
}

function Get-PayloadRoot([string]$ExtractedRoot) {
    $directExecutable = Join-Path $ExtractedRoot "StreamlinkVlcStudio.exe"
    if (Test-Path -LiteralPath $directExecutable -PathType Leaf) {
        return [System.IO.Path]::GetFullPath($ExtractedRoot)
    }

    $matches = @(Get-ChildItem -LiteralPath $ExtractedRoot -Recurse -Filter "StreamlinkVlcStudio.exe" -File)
    if ($matches.Count -eq 1) {
        return [System.IO.Path]::GetFullPath((Split-Path -Parent $matches[0].FullName))
    }

    if ($matches.Count -eq 0) {
        throw "Release zip does not contain StreamlinkVlcStudio.exe."
    }

    throw "Release zip contains more than one StreamlinkVlcStudio.exe; refusing to guess which payload to install."
}

function Assert-Payload([string]$PayloadRoot) {
    $requiredFiles = @(
        "StreamlinkVlcStudio.exe",
        "THIRD-PARTY-NOTICES.md",
        "browser-extension\manifest.json",
        "browser-extension\background.js",
        "browser-extension\content-core.js",
        "browser-extension\content.js",
        "vlc-overlay\build\libmyoverlay_plugin.dll",
        "vlc-overlay\build\vlc_chat_overlay.exe"
    )

    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $PayloadRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release payload is missing required file: $relativePath"
        }
    }

    $reparsePoints = @(Get-ChildItem -LiteralPath $PayloadRoot -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($reparsePoints.Count -gt 0) {
        throw "Release payload contains a symbolic link or junction: $($reparsePoints[0].FullName)"
    }
}

if (-not [string]::Equals($Runtime, "win-x64", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The MSI payload is x64-only. Runtime must be win-x64, not '$Runtime'."
}

if (-not (Test-ThreePartProductVersion $ProductVersion)) {
    throw "ProductVersion must contain three numeric parts from 0 through 255, for example 1.0.0: $ProductVersion"
}

$overlaySourcePath = if ([string]::IsNullOrWhiteSpace($OverlaySource)) {
    Join-Path $repoRoot "src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledOverlay"
} else {
    $OverlaySource
}
$overlaySourcePath = [System.IO.Path]::GetFullPath($overlaySourcePath)

$outputRootPath = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    if ([string]::IsNullOrWhiteSpace($ReleaseZip)) {
        Join-Path $repoRoot "release"
    } else {
        Split-Path -Parent ([System.IO.Path]::GetFullPath($ReleaseZip))
    }
} else {
    $OutputRoot
}
$outputRootPath = [System.IO.Path]::GetFullPath($outputRootPath)
Assert-NoReparsePointInExistingPath -Path $outputRootPath
New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($ReleaseZip)) {
    $packageScript = Join-Path $scriptRoot "package-release.ps1"
    $packageArguments = @(
        "-Configuration", $Configuration,
        "-Runtime", $Runtime,
        "-OverlaySource", $overlaySourcePath,
        "-OutputRoot", $outputRootPath
    )
    if ($Quiet) {
        $packageArguments += "-Quiet"
    }

    Write-Info "Building release zip..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File $packageScript @packageArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Release package build failed with exit code $LASTEXITCODE."
    }

    $ReleaseZip = Join-Path $outputRootPath "StreamlinkVlcStudio-release.zip"
}

$releaseZipPath = [System.IO.Path]::GetFullPath($ReleaseZip)
if (-not (Test-Path -LiteralPath $releaseZipPath -PathType Leaf)) {
    throw "Release zip was not found: $releaseZipPath"
}

if ([string]::IsNullOrWhiteSpace($SetupFileName) -or
    -not [string]::Equals([System.IO.Path]::GetFileName($SetupFileName), $SetupFileName, [System.StringComparison]::Ordinal) -or
    -not [string]::Equals([System.IO.Path]::GetExtension($SetupFileName), ".msi", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "SetupFileName must be a leaf .msi file name, not a path: $SetupFileName"
}

if ([string]::IsNullOrWhiteSpace($BootstrapperFileName) -or
    -not [string]::Equals([System.IO.Path]::GetFileName($BootstrapperFileName), $BootstrapperFileName, [System.StringComparison]::Ordinal) -or
    -not [string]::Equals([System.IO.Path]::GetExtension($BootstrapperFileName), ".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "BootstrapperFileName must be a leaf .exe file name, not a path: $BootstrapperFileName"
}

$setupPath = Join-Path $outputRootPath $SetupFileName
$bootstrapperPath = Join-Path $outputRootPath $BootstrapperFileName
$buildRoot = Join-Path $outputRootPath ".installer-build"
Assert-UnderDirectory -ChildPath $setupPath -ParentPath $outputRootPath
Assert-UnderDirectory -ChildPath $bootstrapperPath -ParentPath $outputRootPath
if (Test-PathIsSameOrUnderDirectory -ChildPath $releaseZipPath -ParentPath $buildRoot) {
    throw "ReleaseZip cannot be inside the temporary installer build directory: $releaseZipPath"
}

Remove-DirectoryIfExists $buildRoot $outputRootPath
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

try {
    $payloadRoot = Join-Path $buildRoot "payload"
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
    Write-Info "Extracting release payload..."
    Expand-Archive -LiteralPath $releaseZipPath -DestinationPath $payloadRoot -Force

    $payloadRoot = Get-PayloadRoot $payloadRoot
    Assert-Payload $payloadRoot

    $wixPath = Ensure-WixTool $WixVersion
    $wixSource = Join-Path $repoRoot "scripts\installer\StreamlinkVlcStudio.wxs"
    if (-not (Test-Path -LiteralPath $wixSource -PathType Leaf)) {
        throw "WiX source file was not found: $wixSource"
    }

    if (Test-Path -LiteralPath $setupPath -PathType Leaf) {
        Remove-Item -LiteralPath $setupPath -Force
    }

    Write-Info "Building native Windows Installer package..."
    $wixArguments = @(
        "build",
        "-arch", "x64",
        "-ext", "WixToolset.UI.wixext",
        "-d", ("PayloadDir=" + $payloadRoot),
        "-d", ("ProductVersion=" + $ProductVersion),
        "-pdbtype", "none",
        "-o", $setupPath,
        $wixSource
    )
    & $wixPath @wixArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "WiX MSI build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf) -or
        (Get-Item -LiteralPath $setupPath).Length -eq 0) {
        throw "MSI installer was not created: $setupPath"
    }

    Write-Info "Validating MSI database..."
    & $wixPath msi validate $setupPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "WiX MSI validation failed with exit code $LASTEXITCODE."
    }

    $dependencyRoot = Join-Path $buildRoot "dependencies"
    New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null

    Write-Info "Resolving the official Streamlink Windows release..."
    $streamlinkInfo = Get-LatestStreamlinkInstallerInfo
    $streamlinkInstallerPath = Join-Path $dependencyRoot $streamlinkInfo.FileName
    Save-DependencyFile `
        -Uri $streamlinkInfo.Uri `
        -DestinationPath $streamlinkInstallerPath `
        -ExpectedLength $streamlinkInfo.Length `
        -ExpectedSha256 $streamlinkInfo.Sha256

    Write-Info "Resolving the official VLC Windows x64 MSI..."
    $vlcInfo = Get-LatestVlcMsiInfo
    $vlcMsiPath = Join-Path $dependencyRoot $vlcInfo.FileName
    Save-DependencyFile `
        -Uri $vlcInfo.Uri `
        -DestinationPath $vlcMsiPath

    $bundleSource = Join-Path $repoRoot "scripts\installer\StreamlinkVlcStudio.Bundle.wxs"
    $appIcon = Join-Path $repoRoot "src\StreamlinkVlcStudio.App.Wpf\Assets\Twitch.ico"
    if (-not (Test-Path -LiteralPath $bundleSource -PathType Leaf)) {
        throw "WiX bundle source file was not found: $bundleSource"
    }
    if (-not (Test-Path -LiteralPath $appIcon -PathType Leaf)) {
        throw "Bundle icon file was not found: $appIcon"
    }

    if (Test-Path -LiteralPath $bootstrapperPath -PathType Leaf) {
        Remove-Item -LiteralPath $bootstrapperPath -Force
    }

    Write-Info "Building full dependency bootstrapper..."
    $bundleArguments = @(
        "build",
        "-arch", "x64",
        "-ext", "WixToolset.BootstrapperApplications.wixext",
        "-ext", "WixToolset.Util.wixext",
        "-d", ("AppMsi=" + $setupPath),
        "-d", ("AppIcon=" + $appIcon),
        "-d", ("BundleVersion=" + $ProductVersion + ".0"),
        "-d", ("StreamlinkInstaller=" + $streamlinkInstallerPath),
        "-d", ("VlcMsi=" + $vlcMsiPath),
        "-pdbtype", "none",
        "-o", $bootstrapperPath,
        $bundleSource
    )
    & $wixPath @bundleArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "WiX bootstrapper build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $bootstrapperPath -PathType Leaf) -or
        (Get-Item -LiteralPath $bootstrapperPath).Length -eq 0) {
        throw "Bootstrapper installer was not created: $bootstrapperPath"
    }
} finally {
    Remove-DirectoryIfExists $buildRoot $outputRootPath
}

Write-Info "MSI installer: $setupPath"
Write-Info "Full installer: $bootstrapperPath"
Write-Output $setupPath
Write-Output $bootstrapperPath
