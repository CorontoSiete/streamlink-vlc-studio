param(
    [string]$OutputPath,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Version = "1.7.0",
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
. (Join-Path $scriptRoot "lib\common.ps1")

if ($Version -notmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
    throw "Version must be a canonical three-part numeric version: $Version"
}
$versionParts = @($Version -split '\.' | ForEach-Object { [uint64]$_ })
if (@($versionParts | Where-Object { $_ -gt 65535 }).Count -gt 0) {
    throw "Version components must be between 0 and 65535: $Version"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "release\Uninstall.exe"
}

$outputLeaf = Split-Path -Leaf $OutputPath
if (-not (Test-SafeWindowsPathSegment $outputLeaf) -or
    -not [string]::Equals([IO.Path]::GetExtension($outputLeaf), ".exe", [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must end in a safe .exe file name: $OutputPath"
}

$outputPathFull = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputPathFull
Assert-NoReparsePointInExistingPath -Path $outputDirectory
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Assert-NoReparsePointInExistingPath -Path $outputDirectory

$project = Join-Path $repoRoot "src\StreamlinkVlcStudio.Maintenance\StreamlinkVlcStudio.Maintenance.csproj"
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "NativeAOT maintenance project missing: $project"
}

$repoDotNet = Join-Path $repoRoot ".dotnet-sdk\dotnet.exe"
$dotnet = if (Test-Path -LiteralPath $repoDotNet -PathType Leaf) {
    $repoDotNet
} else {
    $command = Get-Command "dotnet.exe" -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "dotnet was not found. Install the SDK selected by global.json before packaging."
    }
    $command.Source
}

$buildRoot = Join-Path $outputDirectory (".uninstaller-build-" + [Guid]::NewGuid().ToString("N"))
if (Test-PathIsSameOrUnderDirectory -ChildPath $outputPathFull -ParentPath $buildRoot) {
    throw "OutputPath cannot be inside the temporary uninstaller build directory: $outputPathFull"
}
New-Item -ItemType Directory -Path $buildRoot | Out-Null

try {
    Write-Info "Publishing x64 NativeAOT maintenance executable..."
    & $dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        --nologo `
        -p:PublishAot=true `
        -p:DebugType=none `
        -p:DebugSymbols=false `
        -p:StripSymbols=true `
        -p:Version=$Version `
        -p:VersionPrefix=$Version `
        -p:AssemblyVersion="${Version}.0" `
        -p:FileVersion="${Version}.0" `
        -p:InformationalVersion=$Version `
        -o $buildRoot
    if ($LASTEXITCODE -ne 0) {
        throw "NativeAOT maintenance publish failed with exit code $LASTEXITCODE."
    }

    $publishedExecutable = Join-Path $buildRoot "StreamlinkVlcStudio.Maintenance.exe"
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf) -or
        (Get-Item -LiteralPath $publishedExecutable).Length -le 0) {
        throw "NativeAOT maintenance executable was not created: $publishedExecutable"
    }

    $stream = [IO.File]::OpenRead($publishedExecutable)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "Maintenance output is not a Windows PE executable."
            }
            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 6)) {
                throw "Maintenance output has an invalid PE header offset."
            }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
                throw "Maintenance output is not an x64 Windows PE executable."
            }
        } finally {
            $reader.Dispose()
        }
    } finally {
        $stream.Dispose()
    }

    Promote-ValidatedFileSetAtomically @(
        [pscustomobject]@{ Source = $publishedExecutable; Destination = $outputPathFull }
    )
} finally {
    Remove-DirectoryIfExists $buildRoot $outputDirectory
}

Write-Info "NativeAOT uninstall executable: $outputPathFull"
Write-Output $outputPathFull
