param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [string]$OverlaySource,
    [string]$OutputRoot,
    [string]$PublishedAppDirectory,
    [string]$Version = '1.7.4',
    [string]$Tag = '',
    [string]$Commit = '',
    [string]$Repository = 'CorontoSiete/streamlink-vlc-studio',
    [switch]$KeepStaging,
    [switch]$SkipAuthenticodeWhenUnavailable,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
. (Join-Path $scriptRoot "lib\common.ps1")
. (Join-Path $scriptRoot "lib\install-state.ps1")
. (Join-Path $scriptRoot "lib\native-overlay.ps1")
. (Join-Path $scriptRoot "lib\release-contract.ps1")
. (Join-Path $scriptRoot "lib\authenticode.ps1")

$releaseContractPath = Join-Path $repoRoot "shared\release-contract.json"
$releaseContract = Read-ReleaseContract $releaseContractPath
$releaseVersion = ConvertTo-StableReleaseVersion $Version
if ($releaseVersion -lt (ConvertTo-StableReleaseVersion ([string]$releaseContract.release.minimumVersion))) {
    throw "Package version $Version is below the supported release floor $($releaseContract.release.minimumVersion)."
}
if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $releaseIdentity = Get-StableReleaseIdentity -Tag $Tag -MinimumVersion ([string]$releaseContract.release.minimumVersion)
    if ($releaseIdentity.VersionText -cne $Version) {
        throw "Package tag $Tag does not exactly match version $Version."
    }
}
if (-not [string]::IsNullOrWhiteSpace($Commit) -and $Commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Package commit must be 40 lowercase hexadecimal characters when supplied.'
}
if (-not [string]::IsNullOrWhiteSpace($Tag) -and [string]::IsNullOrWhiteSpace($Commit)) {
    throw 'A stable-tag package requires its source commit.'
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repository must be an owner/name pair: $Repository"
}
$authenticode = Get-AuthenticodeSigningConfiguration

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
$stageDir = Join-Path $outputRootPath "StreamStudio"
$zipPath = Join-Path $outputRootPath "StreamlinkVlcStudio-release.zip"
$publishDir = if ([string]::IsNullOrWhiteSpace($PublishedAppDirectory)) {
    Join-Path $repoRoot "artifacts\publish\StreamStudio"
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
    Get-ChildItem -LiteralPath $sourceFull -Force | ForEach-Object {
        if (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Package sources cannot contain symbolic links or directory junctions: $($_.FullName)"
        }

        $targetPath = Join-Path $destinationFull $_.Name
        if ($_.PSIsContainer) {
            Copy-DirectoryFiltered $_.FullName $targetPath
        } else {
            $extension = $_.Extension.ToLowerInvariant()
            $isEnvironmentFile = $extension -eq '.env' -or
                $_.Name.StartsWith('.env.', [StringComparison]::OrdinalIgnoreCase)
            $looksLikeSecret = $isEnvironmentFile -or
                ($_.BaseName -match '(?i)(token|secret|credential|cookie)' -and
                    $extension -in @(".config", ".json", ".txt", ".xml", ".yaml", ".yml"))
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

$nativeManifestPath = Join-Path $repoRoot "dependencies\native-overlay.json"
$verifiedOverlayFiles = @(
    Assert-NativeOverlaySource `
        -OverlaySource $overlaySourcePath `
        -ManifestPath $nativeManifestPath `
        -SkipAuthenticodeWhenUnavailable:$SkipAuthenticodeWhenUnavailable
)

$requiredDocumentationFiles = @(
    "README.md",
    "install.txt"
)
foreach ($relativePath in $requiredDocumentationFiles) {
    $required = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required documentation file missing: $required"
    }
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null
Assert-NoReparsePointInExistingPath -Path $outputRootPath

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

    Write-Info "Publishing Stream Studio..."
    & $dotnet restore $project `
        -r $Runtime `
        --locked-mode `
        -p:SelfContained=$selfContained `
        -p:NuGetAudit=true `
        -p:NuGetAuditMode=all `
        -warnaserror:NU1801,NU1900,NU1901,NU1902,NU1903,NU1904 `
        -s "https://api.nuget.org/v3/index.json"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }

    & $dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --no-restore `
        --self-contained $selfContained `
        -p:PublishSingleFile=$publishSingleFile `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -p:VersionPrefix=$Version `
        -p:AssemblyVersion="${Version}.0" `
        -p:FileVersion="${Version}.0" `
        -p:InformationalVersion=$Version `
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
$friendlyExe = Join-Path $stageDir "StreamStudio.exe"
if ((Test-Path -LiteralPath $publishedExe -PathType Leaf) -and -not (Test-Path -LiteralPath $friendlyExe -PathType Leaf)) {
    Rename-Item -LiteralPath $publishedExe -NewName "StreamStudio.exe"
}

if (-not (Test-Path -LiteralPath $friendlyExe -PathType Leaf)) {
    throw "Staged app executable missing: $friendlyExe"
}

$uninstallerScript = Join-Path $scriptRoot "build-uninstaller.ps1"
$uninstallerTarget = Join-Path $stageDir "Uninstall.exe"
if (-not (Test-Path -LiteralPath $uninstallerScript -PathType Leaf)) {
    throw "Uninstaller builder missing: $uninstallerScript"
}

Write-Info "Building uninstaller..."
& powershell -NoProfile -ExecutionPolicy Bypass -File $uninstallerScript `
    -OutputPath $uninstallerTarget `
    -Configuration $Configuration `
    -Version $Version `
    -Quiet | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Uninstaller build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $uninstallerTarget -PathType Leaf)) {
    throw "Staged uninstaller missing: $uninstallerTarget"
}

Write-Info "Staging VLC overlay..."
$overlayStage = Join-Path $stageDir "vlc-overlay"
Copy-VerifiedNativeOverlay -VerifiedFiles $verifiedOverlayFiles -Destination $overlayStage
$overlayBuildStage = Join-Path $overlayStage "build"
foreach ($name in @("libmyoverlay_plugin.dll", "vlc_chat_overlay.exe")) {
    $stagedOverlayFile = Join-Path $overlayBuildStage $name
    if (-not (Test-Path -LiteralPath $stagedOverlayFile -PathType Leaf)) {
        throw "Staged overlay binary missing: $stagedOverlayFile"
    }
}

$installerSource = Join-Path $repoRoot "scripts\install.ps1"
if (-not (Test-Path -LiteralPath $installerSource -PathType Leaf)) {
    throw "Installer script missing: $installerSource"
}
Copy-Item -LiteralPath $installerSource -Destination (Join-Path $stageDir "install.ps1") -Force
$installerCommonSource = Join-Path $scriptRoot "lib\common.ps1"
if (-not (Test-Path -LiteralPath $installerCommonSource -PathType Leaf)) {
    throw "Shared installer helper file missing: $installerCommonSource"
}
$installerCommonStage = Join-Path $stageDir "lib"
New-Item -ItemType Directory -Path $installerCommonStage -Force | Out-Null
Copy-Item -LiteralPath $installerCommonSource -Destination (Join-Path $installerCommonStage "common.ps1") -Force
$installerStateSource = Join-Path $scriptRoot "lib\install-state.ps1"
if (-not (Test-Path -LiteralPath $installerStateSource -PathType Leaf)) {
    throw "Shared install-state helper file missing: $installerStateSource"
}
Copy-Item -LiteralPath $installerStateSource -Destination (Join-Path $installerCommonStage "install-state.ps1") -Force
$installerDependencyManifestLibrary = Join-Path $scriptRoot "lib\dependency-manifest.ps1"
if (-not (Test-Path -LiteralPath $installerDependencyManifestLibrary -PathType Leaf)) {
    throw "Shared dependency-manifest helper file missing: $installerDependencyManifestLibrary"
}
Copy-Item `
    -LiteralPath $installerDependencyManifestLibrary `
    -Destination (Join-Path $installerCommonStage "dependency-manifest.ps1") `
    -Force
$releaseContractLibrary = Join-Path $scriptRoot "lib\release-contract.ps1"
Copy-Item `
    -LiteralPath $releaseContractLibrary `
    -Destination (Join-Path $installerCommonStage "release-contract.ps1") `
    -Force
Copy-Item `
    -LiteralPath $releaseContractPath `
    -Destination (Join-Path $stageDir "release-contract.json") `
    -Force
$releasePublicKeySource = Join-Path $repoRoot ([string]$releaseContract.release.manifestSignature.publicKey)
$releasePublicKeyStage = Join-Path $stageDir ([string]$releaseContract.release.manifestSignature.publicKey)
New-Item -ItemType Directory -Path (Split-Path -Parent $releasePublicKeyStage) -Force | Out-Null
Copy-Item -LiteralPath $releasePublicKeySource -Destination $releasePublicKeyStage -Force

Write-Info "Staging installation and usage documentation..."
foreach ($relativePath in $requiredDocumentationFiles) {
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot $relativePath) `
        -Destination (Join-Path $stageDir $relativePath) `
        -Force
}

$thirdPartyNotices = Join-Path $repoRoot "THIRD-PARTY-NOTICES.md"
if (-not (Test-Path -LiteralPath $thirdPartyNotices -PathType Leaf)) {
    throw "Third-party notices file missing: $thirdPartyNotices"
}
Copy-Item -LiteralPath $thirdPartyNotices -Destination (Join-Path $stageDir "THIRD-PARTY-NOTICES.md") -Force
$nativeProvenance = $nativeManifestPath
if (-not (Test-Path -LiteralPath $nativeProvenance -PathType Leaf)) {
    throw "Native overlay provenance manifest missing: $nativeProvenance"
}
Copy-Item -LiteralPath $nativeProvenance -Destination (Join-Path $stageDir "native-overlay-provenance.json") -Force

$dependencyManifest = Join-Path $repoRoot "dependencies\windows-installers.json"
if (-not (Test-Path -LiteralPath $dependencyManifest -PathType Leaf)) {
    throw "Locked Windows dependency manifest missing: $dependencyManifest"
}
$dependencyStage = Join-Path $stageDir "dependencies"
New-Item -ItemType Directory -Path $dependencyStage -Force | Out-Null
Copy-Item -LiteralPath $dependencyManifest -Destination (Join-Path $dependencyStage "windows-installers.json") -Force

if ($authenticode.Enabled) {
    Write-Info 'Authenticode-signing staged application binaries...'
    Invoke-AuthenticodeSigning `
        -Path @($friendlyExe, $uninstallerTarget) `
        -Configuration $authenticode
}

Write-Info 'Writing ZIP release metadata...'
$zipReleaseMetadata = [ordered]@{
    schemaVersion = 1
    artifactKind = 'zip'
    version = $Version
    tag = if ([string]::IsNullOrWhiteSpace($Tag)) { $null } else { $Tag }
    commit = if ([string]::IsNullOrWhiteSpace($Commit)) { $null } else { $Commit }
    repository = $Repository
    updaterProtocolVersion = [int]$releaseContract.release.updaterProtocolVersion
    updateSigningKeyId = [string]$releaseContract.release.manifestSignature.keyId
    authenticodeSigned = [bool]$authenticode.Enabled
}
[IO.File]::WriteAllText(
    (Join-Path $stageDir 'release-metadata.json'),
    ($zipReleaseMetadata | ConvertTo-Json -Depth 5 -Compress),
    [Text.UTF8Encoding]::new($false))

Write-Info "Writing installation ownership marker and managed-file manifest..."
Write-InstallOwnershipState -Directory $stageDir -InstallId "release-payload" | Out-Null
Assert-ReleasePayload -PayloadRoot $stageDir -Contract $releaseContract

Write-Info "Creating zip..."
$archiveItems = @(Get-ChildItem -LiteralPath $stageDir -Force)
if ($archiveItems.Count -eq 0) {
    throw "Package staging directory is empty: $stageDir"
}
$temporaryZipPath = Join-Path $outputRootPath (
    ".StreamlinkVlcStudio-release.{0}.tmp.zip" -f [Guid]::NewGuid().ToString("N"))
try {
    Compress-Archive -LiteralPath $archiveItems.FullName -DestinationPath $temporaryZipPath
    if (-not (Test-Path -LiteralPath $temporaryZipPath -PathType Leaf) -or
        (Get-Item -LiteralPath $temporaryZipPath).Length -eq 0) {
        throw "Release zip was not created: $temporaryZipPath"
    }
    Promote-ValidatedFileSetAtomically @(
        [pscustomobject]@{ Source = $temporaryZipPath; Destination = $zipPath }
    )
} finally {
    if (Test-Path -LiteralPath $temporaryZipPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryZipPath -Force
    }
}

if (-not $KeepStaging) {
    Remove-DirectoryIfExists $stageDir $outputRootPath
}

Write-Info "Release zip: $zipPath"
Write-Output $zipPath
