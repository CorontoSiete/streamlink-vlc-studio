param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [string]$OverlaySource = "C:\Users\ComputerGuy\Downloads\vlc-overlay",
    [string]$OutputRoot,
    [string]$PublishedAppDirectory,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
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

function Write-Info([string]$Message) {
    if (-not $Quiet) {
        Write-Host $Message
    }
}

function Assert-UnderDirectory([string]$ChildPath, [string]$ParentPath) {
    $childFull = [System.IO.Path]::GetFullPath($ChildPath)
    $parentFull = [System.IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/')
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

function Get-RelativePathCompat([string]$BasePath, [string]$FullPath) {
    $baseFull = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $pathFull = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $pathFull.StartsWith($baseFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$pathFull' is not under '$baseFull'."
    }

    return $pathFull.Substring($baseFull.Length)
}

function Copy-DirectoryFiltered([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Source directory not found: $Source"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Recurse -File | Where-Object {
        $_.Name -notlike "*.log" -and
        $_.Name -notlike "*.tmp" -and
        $_.Name -notlike "*.user" -and
        $_.Name -ne "settings.json" -and
        $_.Name -notlike "*token*"
    } | ForEach-Object {
        $relativePath = Get-RelativePathCompat $Source $_.FullName
        $targetPath = Join-Path $Destination $relativePath
        $targetDirectory = Split-Path -Parent $targetPath
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
    }
}

$overlayBuildSource = Join-Path $OverlaySource "build"
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
    "content.js",
    "README.md"
)
foreach ($relativePath in $requiredBrowserExtensionFiles) {
    $required = Join-Path $browserExtensionSource $relativePath
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required browser extension file missing: $required"
    }
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($PublishedAppDirectory)) {
    Remove-DirectoryIfExists $publishDir (Join-Path $repoRoot "artifacts")
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    $dotnet = Join-Path $repoRoot ".dotnet-sdk\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        $dotnet = "dotnet"
    }

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

Write-Info "Staging VLC overlay..."
$overlayBuildStage = Join-Path $stageDir "vlc-overlay\build"
Copy-DirectoryFiltered $overlayBuildSource $overlayBuildStage
foreach ($name in @("libmyoverlay_plugin.dll", "vlc_chat_overlay.exe")) {
    $stagedOverlayFile = Join-Path $overlayBuildStage $name
    if (-not (Test-Path -LiteralPath $stagedOverlayFile -PathType Leaf)) {
        throw "Staged overlay binary missing: $stagedOverlayFile"
    }
}

Write-Info "Staging Brave browser extension..."
$browserExtensionStage = Join-Path $stageDir "browser-extension"
New-Item -ItemType Directory -Path $browserExtensionStage -Force | Out-Null
foreach ($relativePath in $requiredBrowserExtensionFiles) {
    $sourceFile = Join-Path $browserExtensionSource $relativePath
    $targetFile = Join-Path $browserExtensionStage $relativePath
    Copy-Item -LiteralPath $sourceFile -Destination $targetFile -Force
}

$installSource = Join-Path $repoRoot "install.txt"
if (Test-Path -LiteralPath $installSource -PathType Leaf) {
    Copy-Item -LiteralPath $installSource -Destination (Join-Path $stageDir "install.txt") -Force
}

if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Info "Creating zip..."
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -Force

Write-Info "Release zip: $zipPath"
Write-Output $zipPath
