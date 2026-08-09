param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [string]$OverlaySource,
    [string]$OutputRoot,
    [string]$PublishedAppDirectory,
    [switch]$SkipUninstaller,
    [switch]$KeepStaging,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
. (Join-Path $scriptRoot "lib\common.ps1")

$overlaySourcePath = if ([string]::IsNullOrWhiteSpace($OverlaySource)) {
    Join-Path $repoRoot "src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledOverlay"
} else {
    $OverlaySource
}
$overlaySourcePath = [System.IO.Path]::GetFullPath($overlaySourcePath)
$outputRootPath = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "release"
} else {
    $OutputRoot
}
$outputRootPath = [System.IO.Path]::GetFullPath($outputRootPath)
$stageDir = Join-Path $outputRootPath "StreamlinkVlcStudio"
$zipPath = Join-Path $outputRootPath "StreamlinkVlcStudio-release.zip"
$publishDir = if ([string]::IsNullOrWhiteSpace($PublishedAppDirectory)) {
    Join-Path $repoRoot "artifacts\publish\StreamlinkVlcStudio"
} else {
    [System.IO.Path]::GetFullPath($PublishedAppDirectory)
}

Assert-NoReparsePointInExistingPath -Path $outputRootPath

function Copy-DirectoryFiltered([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Source directory not found: $Source"
    }

    $sourceFull = [System.IO.Path]::GetFullPath($Source)
    $destinationFull = [System.IO.Path]::GetFullPath($Destination)
    if ((Test-PathIsSameOrUnderDirectory -ChildPath $destinationFull -ParentPath $sourceFull) -or
        (Test-PathIsSameOrUnderDirectory -ChildPath $sourceFull -ParentPath $destinationFull)) {
        throw "Source and destination directories must not contain each other. Source: $sourceFull Destination: $destinationFull"
    }

    $sourceItem = Get-Item -LiteralPath $sourceFull -Force
    if (($sourceItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Package sources cannot be symbolic links or directory junctions: $sourceFull"
    }

    New-Item -ItemType Directory -Path $destinationFull -Force | Out-Null
    Get-ChildItem -LiteralPath $sourceFull | ForEach-Object {
        if (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Package sources cannot contain symbolic links or directory junctions: $($_.FullName)"
        }

        $targetPath = Join-Path $destinationFull $_.Name
        if ($_.PSIsContainer) {
            Copy-DirectoryFiltered $_.FullName $targetPath
        } else {
            $extension = $_.Extension.ToLowerInvariant()
            $looksLikeSecret = $_.BaseName -match '(?i)(token|secret|credential|cookie)' -and
                $extension -in @(".config", ".env", ".json", ".txt", ".xml", ".yaml", ".yml")
            if ($_.Name -notlike "*.log" -and
                $_.Name -notlike "*.tmp" -and
                $_.Name -notlike "*.user" -and
                $_.Name -ne "settings.json" -and
                -not $looksLikeSecret) {
                Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
            }
        }
    }
}

$overlayBuildSource = Join-Path $overlaySourcePath "build"
$requiredOverlayFiles = @(
    (Join-Path $overlayBuildSource "libmyoverlay_plugin.dll"),
    (Join-Path $overlayBuildSource "vlc_chat_overlay.exe")
)
foreach ($required in $requiredOverlayFiles) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required overlay binary missing: $required"
    }
}

$browserExtensionSource = Join-Path $repoRoot "browser-extension"
$requiredBrowserExtensionFiles = @(
    "manifest.json",
    "background.js",
    "content-core.js",
    "content.js"
)
foreach ($relativePath in $requiredBrowserExtensionFiles) {
    $required = Join-Path $browserExtensionSource $relativePath
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required browser extension file missing: $required"
    }
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

if ((Test-PathIsSameOrUnderDirectory -ChildPath $stageDir -ParentPath $publishDir) -or
    (Test-PathIsSameOrUnderDirectory -ChildPath $publishDir -ParentPath $stageDir)) {
    throw "PublishedAppDirectory and the package staging directory must not contain each other. Published: $publishDir Staging: $stageDir"
}

if ([string]::IsNullOrWhiteSpace($PublishedAppDirectory)) {
    Remove-DirectoryIfExists $publishDir (Join-Path $repoRoot "artifacts")
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    $dotnetCommand = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw "dotnet was not found on PATH. Install the SDK selected by global.json before packaging."
    }
    $dotnet = $dotnetCommand.Source

    $project = Join-Path $repoRoot "src\StreamlinkVlcStudio.App.Wpf\StreamlinkVlcStudio.App.Wpf.csproj"
    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    $publishSingleFile = if ($FrameworkDependent) { "false" } else { "true" }

    Write-Info "Publishing StreamlinkVlcStudio..."
    & $dotnet restore $project -r $Runtime -s "https://api.nuget.org/v3/index.json"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }

    & $dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained $selfContained `
        -p:PublishSingleFile=$publishSingleFile `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -p:BundledVlcOverlayRoot=$overlaySourcePath `
        -p:RequireBundledVlcOverlay=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    throw "Published app directory not found: $publishDir"
}

Remove-DirectoryIfExists $stageDir $outputRootPath
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Write-Info "Staging app files..."
Copy-DirectoryFiltered $publishDir $stageDir

$publishedExe = Join-Path $stageDir "StreamlinkVlcStudio.App.Wpf.exe"
$friendlyExe = Join-Path $stageDir "StreamlinkVlcStudio.exe"
if ((Test-Path -LiteralPath $publishedExe -PathType Leaf) -and -not (Test-Path -LiteralPath $friendlyExe -PathType Leaf)) {
    Rename-Item -LiteralPath $publishedExe -NewName "StreamlinkVlcStudio.exe"
}

if (-not (Test-Path -LiteralPath $friendlyExe -PathType Leaf)) {
    throw "Staged app executable missing: $friendlyExe"
}

if (-not $SkipUninstaller) {
    $uninstallerScript = Join-Path $scriptRoot "build-uninstaller.ps1"
    $uninstallerTarget = Join-Path $stageDir "Uninstall.exe"
    if (-not (Test-Path -LiteralPath $uninstallerScript -PathType Leaf)) {
        throw "Uninstaller builder missing: $uninstallerScript"
    }

    Write-Info "Building uninstaller..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File $uninstallerScript -OutputPath $uninstallerTarget -Quiet | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Uninstaller build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $uninstallerTarget -PathType Leaf)) {
        throw "Staged uninstaller missing: $uninstallerTarget"
    }
}

Write-Info "Staging VLC overlay..."
$overlayBuildStage = Join-Path $stageDir "vlc-overlay\build"
Copy-DirectoryFiltered $overlayBuildSource $overlayBuildStage
foreach ($name in @("libmyoverlay_plugin.dll", "vlc_chat_overlay.exe")) {
    $stagedOverlayFile = Join-Path $overlayBuildStage $name
    if (-not (Test-Path -LiteralPath $stagedOverlayFile -PathType Leaf)) {
        throw "Staged overlay binary missing: $stagedOverlayFile"
    }
}

Write-Info "Staging Brave browser extension runtime files..."
$browserExtensionStage = Join-Path $stageDir "browser-extension"
New-Item -ItemType Directory -Path $browserExtensionStage -Force | Out-Null
foreach ($relativePath in $requiredBrowserExtensionFiles) {
    $sourceFile = Join-Path $browserExtensionSource $relativePath
    $targetFile = Join-Path $browserExtensionStage $relativePath
    Copy-Item -LiteralPath $sourceFile -Destination $targetFile -Force
}

$installerSource = Join-Path $repoRoot "scripts\install.ps1"
if (Test-Path -LiteralPath $installerSource -PathType Leaf) {
    Copy-Item -LiteralPath $installerSource -Destination (Join-Path $stageDir "install.ps1") -Force
}

$thirdPartyNotices = Join-Path $repoRoot "THIRD-PARTY-NOTICES.md"
if (-not (Test-Path -LiteralPath $thirdPartyNotices -PathType Leaf)) {
    throw "Third-party notices file missing: $thirdPartyNotices"
}
Copy-Item -LiteralPath $thirdPartyNotices -Destination (Join-Path $stageDir "THIRD-PARTY-NOTICES.md") -Force

if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Info "Creating zip..."
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -Force
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or
    (Get-Item -LiteralPath $zipPath).Length -eq 0) {
    throw "Release zip was not created: $zipPath"
}

if (-not $KeepStaging) {
    Remove-DirectoryIfExists $stageDir $outputRootPath
}

Write-Info "Release zip: $zipPath"
Write-Output $zipPath
