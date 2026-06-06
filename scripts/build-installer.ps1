param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OverlaySource,
    [string]$OutputRoot,
    [string]$ReleaseZip,
    [string]$SetupFileName = "StreamlinkVlcStudio-Setup.exe",
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$overlaySourcePath = if ([string]::IsNullOrWhiteSpace($OverlaySource)) {
    Join-Path $repoRoot "src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledOverlay"
} else {
    $OverlaySource
}
$overlaySourcePath = [System.IO.Path]::GetFullPath($overlaySourcePath)

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

function New-BootstrapScript([string]$Path) {
    @'
[CmdletBinding()]
param(
    [switch]$Quiet,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
    if (-not $Quiet) {
        Write-Host ""
        Write-Host "==> $Message" -ForegroundColor Cyan
    }
}

$sourceDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}

$releaseZip = Join-Path $sourceDirectory "StreamlinkVlcStudio-release.zip"
if (-not (Test-Path -LiteralPath $releaseZip -PathType Leaf)) {
    throw "Installer payload is missing: $releaseZip"
}

$payloadRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("StreamlinkVlcStudio-setup-" + [Guid]::NewGuid().ToString("N"))
try {
    Write-Step "Extracting Streamlink VLC Studio"
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
    Expand-Archive -LiteralPath $releaseZip -DestinationPath $payloadRoot -Force

    $installScript = Join-Path $payloadRoot "install.ps1"
    if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
        $installScript = Get-ChildItem -LiteralPath $payloadRoot -Recurse -Filter "install.ps1" -File |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if ([string]::IsNullOrWhiteSpace($installScript) -or
        -not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
        throw "Installer payload does not contain install.ps1."
    }

    $installArguments = @("-AppSource", "Local", "-ForceStopApp")
    if (-not $NoLaunch -and -not $Quiet) {
        $installArguments += "-Launch"
    }

    Write-Step "Installing Streamlink VLC Studio"
    Push-Location (Split-Path -Parent $installScript)
    try {
        & $installScript @installArguments
    } finally {
        Pop-Location
    }
} finally {
    if (Test-Path -LiteralPath $payloadRoot) {
        Remove-Item -LiteralPath $payloadRoot -Recurse -Force
    }
}
'@ | Set-Content -LiteralPath $Path -Encoding ASCII
}

function New-IExpressSed(
    [string]$Path,
    [string]$SourceDirectory,
    [string]$TargetPath) {
    $sourceDirectoryFull = [System.IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $targetPathFull = [System.IO.Path]::GetFullPath($TargetPath)

    @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
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
FinishMessage=Streamlink VLC Studio setup is complete.
TargetName=$targetPathFull
FriendlyName=Streamlink VLC Studio Setup
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-StreamlinkVlcStudio-Bootstrap.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-StreamlinkVlcStudio-Bootstrap.ps1 -Quiet -NoLaunch
UserQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -File Install-StreamlinkVlcStudio-Bootstrap.ps1 -Quiet -NoLaunch
FILE0="Install-StreamlinkVlcStudio-Bootstrap.ps1"
FILE1="StreamlinkVlcStudio-release.zip"
[SourceFiles]
SourceFiles0=$sourceDirectoryFull
[SourceFiles0]
%FILE0%=
%FILE1%=
"@ | Set-Content -LiteralPath $Path -Encoding ASCII
}

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

$iexpress = Get-Command "iexpress.exe" -ErrorAction SilentlyContinue
if ($null -eq $iexpress) {
    throw "IExpress was not found. This installer builder requires Windows iexpress.exe."
}

$setupPath = Join-Path $outputRootPath $SetupFileName
$buildRoot = Join-Path $outputRootPath ".installer-build"
Remove-DirectoryIfExists $buildRoot $outputRootPath
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

try {
    Copy-Item -LiteralPath $releaseZipPath -Destination (Join-Path $buildRoot "StreamlinkVlcStudio-release.zip") -Force
    $bootstrapPath = Join-Path $buildRoot "Install-StreamlinkVlcStudio-Bootstrap.ps1"
    New-BootstrapScript $bootstrapPath

    $sedPath = Join-Path $buildRoot "StreamlinkVlcStudio-Setup.sed"
    New-IExpressSed $sedPath $buildRoot $setupPath

    if (Test-Path -LiteralPath $setupPath -PathType Leaf) {
        Remove-Item -LiteralPath $setupPath -Force
    }

    Write-Info "Building setup installer..."
    $process = Start-Process -FilePath $iexpress.Source -ArgumentList @("/N", "/Q", $sedPath) -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "IExpress failed with exit code $($process.ExitCode)."
    }

    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf) -or
        (Get-Item -LiteralPath $setupPath).Length -eq 0) {
        throw "Installer was not created: $setupPath"
    }
} finally {
    Remove-DirectoryIfExists $buildRoot $outputRootPath
}

Write-Info "Setup installer: $setupPath"
Write-Output $setupPath
