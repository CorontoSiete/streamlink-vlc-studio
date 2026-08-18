param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OverlaySource,
    [string]$OutputRoot,
    [string]$ReleaseZip,
    [string]$SetupFileName = "StreamlinkVlcStudio-Setup.msi",
    [string]$BootstrapperFileName = "StreamlinkVlcStudio-Setup.exe",
    [string]$DependencyManifest,
    [ValidateRange(5, 600)]
    [int]$HttpTimeoutSeconds = 60,
    [Parameter(Mandatory = $true)]
    [string]$ProductVersion,
    [string]$WixVersion = "6.0.2",
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
. (Join-Path $scriptRoot "lib\common.ps1")
. (Join-Path $scriptRoot "lib\dependency-manifest.ps1")
. (Join-Path $scriptRoot "lib\release-contract.ps1")
$releaseContract = Read-ReleaseContract (Join-Path $repoRoot "shared\release-contract.json")

& (Join-Path $scriptRoot "generate-browser-route-policy.ps1") -RepositoryRoot $repoRoot -Check
if (-not $?) {
    throw "Generated browser route policy validation failed."
}

function Test-ThreePartProductVersion([string]$Version) {
    if ($Version -notmatch "^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$") {
        return $false
    }

    foreach ($name in @("major", "minor", "patch")) {
        [int]$part = 0
        if (-not [int]::TryParse(
                $Matches[$name],
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$part) -or
            $part -gt 255) {
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
            --version $Version | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "WiX tool installation failed with exit code $LASTEXITCODE."
        }
    }

    if (-not (Test-Path -LiteralPath $wixPath -PathType Leaf)) {
        throw "WiX executable was not created: $wixPath"
    }

    $installedVersion = [string](& $wixPath --version 2>$null | Select-Object -First 1)
    if ((ConvertTo-DependencyVersion $installedVersion) -ne (ConvertTo-DependencyVersion $Version)) {
        throw "WiX version mismatch. Expected $Version, found '$installedVersion'."
    }

    $requiredExtensions = @(
        "WixToolset.UI.wixext",
        "WixToolset.BootstrapperApplications.wixext",
        "WixToolset.Util.wixext"
    )
    foreach ($extension in $requiredExtensions) {
        $extensionList = (& $wixPath extension list 2>$null | Out-String)
        $installedExtensionVersion = @(
            [regex]::Matches($extensionList, '(?m)^\s*' + [regex]::Escape($extension) + '\s+(?<version>\d+(?:\.\d+){1,3})\s*$') |
                ForEach-Object { ConvertTo-DependencyVersion $_.Groups['version'].Value }
        ) | Where-Object { $_ -eq (ConvertTo-DependencyVersion $Version) } | Select-Object -First 1
        if ($null -ne $installedExtensionVersion) {
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
        [Parameter(Mandatory = $true)]$Dependency)

    Write-Info "Downloading $Uri..."
    $headers = @{
        "User-Agent" = "StreamlinkVlcStudioInstallerBuilder/1.0 (+https://github.com/CorontoSiete/streamlink-vlc-studio)"
    }
    $dependencyToValidate = $Dependency
    $result = Save-HttpFileAtomically `
        -Uri $Uri `
        -DestinationPath $DestinationPath `
        -Headers $headers `
        -TimeoutSeconds $HttpTimeoutSeconds `
        -MaximumBytes ([long]$Dependency.length) `
        -ValidationScript {
            param($DownloadedPath)
            Assert-PinnedInstallerDependency -Path $DownloadedPath -Dependency $dependencyToValidate
        }

    Write-Info "Downloaded $([System.IO.Path]::GetFileName($DestinationPath)) ($($result.Length) bytes, SHA-256 $($result.Sha256))."
}

if (-not [string]::Equals($Runtime, "win-x64", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The MSI payload is x64-only. Runtime must be win-x64, not '$Runtime'."
}

$dependencyManifestPath = if ([string]::IsNullOrWhiteSpace($DependencyManifest)) {
    Join-Path $repoRoot "dependencies\windows-installers.json"
} else {
    [IO.Path]::GetFullPath($DependencyManifest)
}
if (-not (Test-Path -LiteralPath $dependencyManifestPath -PathType Leaf)) {
    throw "Locked installer dependency manifest missing: $dependencyManifestPath"
}
$dependencyManifestData = Read-WindowsDependencyManifest $dependencyManifestPath
if ($dependencyManifestData.schemaVersion -ne 1 -or
    $null -eq $dependencyManifestData.dependencies.streamlink -or
    $null -eq $dependencyManifestData.dependencies.vlc) {
    throw "Unsupported or incomplete installer dependency manifest: $dependencyManifestPath"
}

$maximumInstallerDependencyBytes = 536870912
$dependencyFileNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($dependencyProperty in $dependencyManifestData.dependencies.PSObject.Properties) {
    $dependency = $dependencyProperty.Value
    $fileName = [string]$dependency.fileName
    $dependencyUri = $null
    [long]$dependencyLength = 0
    if (-not (Test-SafeWindowsPathSegment $fileName) -or
        -not $dependencyFileNames.Add($fileName)) {
        throw "Locked dependency fileName must be a safe, unique leaf name: $fileName"
    }
    if (-not [Uri]::TryCreate([string]$dependency.url, [UriKind]::Absolute, [ref]$dependencyUri) -or
        -not [string]::Equals($dependencyUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Locked dependency URL must be an absolute HTTPS URL: $($dependency.url)"
    }
    if (-not [long]::TryParse(
            [Convert]::ToString($dependency.length, [Globalization.CultureInfo]::InvariantCulture),
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$dependencyLength) -or
        $dependencyLength -le 0 -or
        $dependencyLength -gt $maximumInstallerDependencyBytes) {
        throw "Locked dependency length must be an integer from 1 through $maximumInstallerDependencyBytes bytes: $($dependency.length)"
    }
    if (([string]$dependency.sha256).Trim() -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]::IsNullOrWhiteSpace([string]$dependency.authenticode)) {
        throw "Locked dependency integrity metadata is incomplete: $($dependencyProperty.Name)"
    }
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

if (-not (Test-SafeWindowsPathSegment $SetupFileName) -or
    -not [string]::Equals([System.IO.Path]::GetExtension($SetupFileName), ".msi", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "SetupFileName must be a leaf .msi file name, not a path: $SetupFileName"
}

if (-not (Test-SafeWindowsPathSegment $BootstrapperFileName) -or
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
    $stagedSetupPath = Join-Path $buildRoot $SetupFileName
    $stagedBootstrapperPath = Join-Path $buildRoot $BootstrapperFileName
    $payloadRoot = Join-Path $buildRoot "payload"
    Write-Info "Extracting release payload..."
    Expand-ValidatedZipArchive -ArchivePath $releaseZipPath -DestinationDirectory $payloadRoot

    $payloadRoot = Resolve-ReleasePayloadRoot -ExtractedRoot $payloadRoot -Contract $releaseContract

    $wixPath = Ensure-WixTool $WixVersion
    $wixSource = Join-Path $repoRoot "scripts\installer\StreamlinkVlcStudio.wxs"
    if (-not (Test-Path -LiteralPath $wixSource -PathType Leaf)) {
        throw "WiX source file was not found: $wixSource"
    }

    Write-Info "Building native Windows Installer package..."
    $wixArguments = @(
        "build",
        "-arch", "x64",
        "-ext", "WixToolset.UI.wixext",
        "-d", ("PayloadDir=" + $payloadRoot),
        "-d", ("ProductVersion=" + $ProductVersion),
        "-pdbtype", "none",
        "-o", $stagedSetupPath,
        $wixSource
    )
    & $wixPath @wixArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "WiX MSI build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $stagedSetupPath -PathType Leaf) -or
        (Get-Item -LiteralPath $stagedSetupPath).Length -eq 0) {
        throw "MSI installer was not created: $stagedSetupPath"
    }

    Write-Info "Validating MSI database..."
    & $wixPath msi validate $stagedSetupPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "WiX MSI validation failed with exit code $LASTEXITCODE."
    }

    $dependencyRoot = Join-Path $buildRoot "dependencies"
    New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null

    Write-Info "Downloading the locked Streamlink Windows dependency..."
    $streamlinkInfo = $dependencyManifestData.dependencies.streamlink
    $streamlinkMinimumVersion = ([string]$streamlinkInfo.version).Trim()
    if ($streamlinkMinimumVersion -notmatch '^\d+(?:\.\d+){1,3}(?:-[0-9A-Za-z.-]+)?$') {
        throw "The locked Streamlink version is not a valid Burn version: $streamlinkMinimumVersion"
    }
    $streamlinkInstallerPath = Join-Path $dependencyRoot ([string]$streamlinkInfo.fileName)
    Save-DependencyFile `
        -Uri $streamlinkInfo.url `
        -DestinationPath $streamlinkInstallerPath `
        -Dependency $streamlinkInfo

    Write-Info "Downloading the locked VLC Windows x64 dependency..."
    $vlcInfo = $dependencyManifestData.dependencies.vlc
    $vlcMsiPath = Join-Path $dependencyRoot ([string]$vlcInfo.fileName)
    Save-DependencyFile `
        -Uri $vlcInfo.url `
        -DestinationPath $vlcMsiPath `
        -Dependency $vlcInfo

    $bundleSource = Join-Path $repoRoot "scripts\installer\StreamlinkVlcStudio.Bundle.wxs"
    $appIcon = Join-Path $repoRoot "src\StreamlinkVlcStudio.App.Wpf\Assets\Twitch.ico"
    if (-not (Test-Path -LiteralPath $bundleSource -PathType Leaf)) {
        throw "WiX bundle source file was not found: $bundleSource"
    }
    if (-not (Test-Path -LiteralPath $appIcon -PathType Leaf)) {
        throw "Bundle icon file was not found: $appIcon"
    }

    Write-Info "Building full dependency bootstrapper..."
    $bundleArguments = @(
        "build",
        "-arch", "x64",
        "-ext", "WixToolset.BootstrapperApplications.wixext",
        "-ext", "WixToolset.Util.wixext",
        "-d", ("AppMsi=" + $stagedSetupPath),
        "-d", ("AppIcon=" + $appIcon),
        "-d", ("BundleVersion=" + $ProductVersion + ".0"),
        "-d", ("StreamlinkInstaller=" + $streamlinkInstallerPath),
        "-d", ("StreamlinkMinimumVersion=" + $streamlinkMinimumVersion),
        "-d", ("VlcMsi=" + $vlcMsiPath),
        "-pdbtype", "none",
        "-o", $stagedBootstrapperPath,
        $bundleSource
    )
    & $wixPath @bundleArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "WiX bootstrapper build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $stagedBootstrapperPath -PathType Leaf) -or
        (Get-Item -LiteralPath $stagedBootstrapperPath).Length -eq 0) {
        throw "Bootstrapper installer was not created: $stagedBootstrapperPath"
    }

    Promote-ValidatedFileSetAtomically @(
        [pscustomobject]@{ Source = $stagedSetupPath; Destination = $setupPath },
        [pscustomobject]@{ Source = $stagedBootstrapperPath; Destination = $bootstrapperPath }
    )
} finally {
    Remove-DirectoryIfExists $buildRoot $outputRootPath
}

Write-Info "MSI installer: $setupPath"
Write-Info "Full installer: $bootstrapperPath"
Write-Output $setupPath
Write-Output $bootstrapperPath
