[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RootDirectory,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$ProjectAssetsPath,
    [string]$PublishedDepsPath,
    [string]$DependencyManifestPath,
    [string]$NativeManifestPath,
    [string]$DocumentNamespace = "https://github.com/CorontoSiete/streamlink-vlc-studio/sbom/local",
    [switch]$Verify
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$root = [IO.Path]::GetFullPath($RootDirectory)
$output = [IO.Path]::GetFullPath($OutputPath)
if ([string]::IsNullOrWhiteSpace($DependencyManifestPath)) {
    $DependencyManifestPath = Join-Path $repoRoot "dependencies\windows-installers.json"
}
if ([string]::IsNullOrWhiteSpace($NativeManifestPath)) {
    $NativeManifestPath = Join-Path $repoRoot "dependencies\native-overlay.json"
}
if ([string]::IsNullOrWhiteSpace($PublishedDepsPath)) {
    $PublishedDepsPath = Join-Path $repoRoot "src\StreamlinkVlcStudio.App.Wpf\bin\Release\net10.0-windows10.0.19041.0\win-x64\StreamlinkVlcStudio.App.Wpf.deps.json"
}
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "SBOM root directory missing: $root"
}

function Get-RelativeSlashPath([string]$Base, [string]$Path) {
    $baseFull = [IO.Path]::GetFullPath($Base).TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($baseFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the SBOM root: $pathFull"
    }
    $pathFull.Substring($baseFull.Length).Replace('\', '/')
}

function Get-StringSha1([string]$Value) {
    $algorithm = [Security.Cryptography.SHA1]::Create()
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes($Value)
        ([BitConverter]::ToString($algorithm.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    } finally {
        $algorithm.Dispose()
    }
}

function Get-PackageVerificationCode([string[]]$Sha1Values) {
    if ($Sha1Values.Count -eq 0) {
        throw "An analyzed SPDX package must contain at least one file."
    }
    Get-StringSha1 (($Sha1Values | Sort-Object) -join "")
}

function Get-RootFiles {
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $directories = [Collections.Generic.Stack[string]]::new()
    $directories.Push($root)
    while ($directories.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "SBOM roots cannot contain symbolic links or junctions: $($item.FullName)"
            }

            if ($item.PSIsContainer) {
                $directories.Push($item.FullName)
            } elseif (-not [string]::Equals(
                    $item.FullName,
                    $output,
                    [StringComparison]::OrdinalIgnoreCase)) {
                $files.Add($item)
            }
        }
    }

    @($files | Sort-Object FullName)
}

function Get-CanonicalDependencyDescriptors {
    $descriptors = @{}
    function Add-Descriptor(
        [string]$Kind,
        [string]$Name,
        [string]$Version,
        [string]$DownloadLocation,
        [string]$Sha256,
        [string]$Detail) {
        if ([string]::IsNullOrWhiteSpace($Name) -or [string]::IsNullOrWhiteSpace($Version)) {
            throw "SBOM dependency source contains a blank name or version ($Kind)."
        }
        $key = "$Kind|$Name|$Version"
        if ($descriptors.ContainsKey($key)) {
            return
        }
        $descriptors[$key] = [pscustomobject]@{
            Key = $key
            Kind = $Kind
            Name = $Name
            Version = $Version
            DownloadLocation = if ([string]::IsNullOrWhiteSpace($DownloadLocation)) { 'NOASSERTION' } else { $DownloadLocation }
            Sha256 = $Sha256
            Detail = $Detail
        }
    }

    $assets = Get-Content -LiteralPath $ProjectAssetsPath -Raw | ConvertFrom-Json
    foreach ($library in @($assets.libraries.PSObject.Properties | Sort-Object Name)) {
        if ($library.Value.type -ne 'package') { continue }
        $separator = $library.Name.LastIndexOf('/')
        if ($separator -le 0) { continue }
        $name = $library.Name.Substring(0, $separator)
        $version = $library.Name.Substring($separator + 1)
        Add-Descriptor 'nuget' $name $version "https://www.nuget.org/packages/$name/$version" '' 'Resolved from project.assets.json.'
    }

    $publishedDeps = Get-Content -LiteralPath $PublishedDepsPath -Raw | ConvertFrom-Json
    foreach ($library in @($publishedDeps.libraries.PSObject.Properties | Sort-Object Name)) {
        if ($library.Value.type -notin @('package', 'runtimepack')) { continue }
        $separator = $library.Name.LastIndexOf('/')
        if ($separator -le 0) { continue }
        $name = $library.Name.Substring(0, $separator)
        $version = $library.Name.Substring($separator + 1)
        if ($library.Value.type -eq 'package') {
            Add-Descriptor 'nuget' $name $version "https://www.nuget.org/packages/$name/$version" '' 'Resolved from the publish .deps.json and project assets.'
        } else {
            Add-Descriptor 'runtimepack' $name $version 'NOASSERTION' '' 'Resolved from the publish .deps.json.'
        }
    }

    foreach ($framework in @($assets.project.frameworks.PSObject.Properties)) {
        foreach ($dependency in @($framework.Value.downloadDependencies)) {
            $versionRange = [string]$dependency.version
            if ($versionRange -notmatch '^\[(?<version>[^,\]]+),\s*\k<version>\]$') {
                throw "Runtime-pack dependency does not use an exact version: $($dependency.name) $versionRange"
            }
            Add-Descriptor `
                'runtimepack' `
                ("runtimepack." + [string]$dependency.name) `
                $Matches.version `
                'NOASSERTION' `
                '' `
                'Resolved from project.assets.json downloadDependencies.'
        }
    }

    $dependencyManifest = Get-Content -LiteralPath $DependencyManifestPath -Raw | ConvertFrom-Json
    if ($dependencyManifest.schemaVersion -ne 1 -or @($dependencyManifest.dependencies.PSObject.Properties).Count -eq 0) {
        throw "Unsupported or empty Windows dependency manifest: $DependencyManifestPath"
    }
    foreach ($property in @($dependencyManifest.dependencies.PSObject.Properties | Sort-Object Name)) {
        $dependency = $property.Value
        if ([string]$dependency.version -notmatch '^[0-9A-Za-z.+-]+$' -or
            [string]$dependency.url -notmatch '^https://' -or
            [int64]$dependency.length -le 0 -or
            [string]$dependency.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "Invalid locked Windows dependency: $($property.Name)"
        }
        Add-Descriptor `
            'windows-installer' `
            ([string]$property.Name) `
            ([string]$dependency.version) `
            ([string]$dependency.url) `
            (([string]$dependency.sha256).ToLowerInvariant()) `
            "Locked installer $($dependency.fileName), $($dependency.length) bytes; Authenticode $($dependency.authenticode); expected publisher $($dependency.expectedPublisher)."
    }

    $nativeManifest = Get-Content -LiteralPath $NativeManifestPath -Raw | ConvertFrom-Json
    if ($nativeManifest.schemaVersion -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$nativeManifest.component) -or
        @($nativeManifest.files).Count -eq 0) {
        throw "Unsupported or empty native dependency manifest: $NativeManifestPath"
    }
    $nativeHashes = @($nativeManifest.files | Sort-Object path | ForEach-Object { ([string]$_.sha256).ToLowerInvariant() })
    if (@($nativeHashes | Where-Object { $_ -notmatch '^[0-9a-f]{64}$' }).Count -gt 0) {
        throw "Native dependency manifest contains an invalid SHA-256 value."
    }
    $nativeVersion = "pinned-$((Get-StringSha1 ($nativeHashes -join '')).Substring(0, 12))"
    Add-Descriptor `
        'native-overlay' `
        ([string]$nativeManifest.component) `
        $nativeVersion `
        'NOASSERTION' `
        '' `
        "Classification: $($nativeManifest.provenance.classification). Source availability: $($nativeManifest.provenance.sourceAvailability). $($nativeManifest.provenance.notes)"

    @($descriptors.Values | Sort-Object Key)
}

foreach ($requiredPath in @($ProjectAssetsPath, $PublishedDepsPath, $DependencyManifestPath, $NativeManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "SBOM input file missing: $requiredPath"
    }
}

if ($Verify) {
    if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
        throw "SBOM missing: $output"
    }

    $document = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    $namespaceUri = $null
    $created = [DateTimeOffset]::MinValue
    if ($document.spdxVersion -ne "SPDX-2.3" -or
        $document.dataLicense -ne "CC0-1.0" -or
        $document.SPDXID -ne "SPDXRef-DOCUMENT" -or
        [string]::IsNullOrWhiteSpace([string]$document.name) -or
        -not [Uri]::TryCreate([string]$document.documentNamespace, [UriKind]::Absolute, [ref]$namespaceUri) -or
        @($document.creationInfo.creators).Count -eq 0 -or
        -not [DateTimeOffset]::TryParse(
            [string]$document.creationInfo.created,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$created)) {
        throw "SBOM is missing required SPDX 2.3 document fields: $output"
    }

    $packages = @($document.packages)
    $files = @($document.files)
    $relationships = @($document.relationships)
    if ($packages.Count -eq 0 -or $files.Count -eq 0 -or $relationships.Count -eq 0) {
        throw "SBOM must contain packages, files, and relationships: $output"
    }

    $packageIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($package in $packages) {
        $packageId = [string]$package.SPDXID
        if ($packageId -notmatch '^SPDXRef-[A-Za-z0-9.-]+$' -or
            -not $packageIds.Add($packageId) -or
            [string]::IsNullOrWhiteSpace([string]$package.name) -or
            [string]::IsNullOrWhiteSpace([string]$package.downloadLocation) -or
            $null -eq $package.filesAnalyzed -or
            [string]::IsNullOrWhiteSpace([string]$package.licenseConcluded) -or
            [string]::IsNullOrWhiteSpace([string]$package.licenseDeclared) -or
            [string]::IsNullOrWhiteSpace([string]$package.copyrightText)) {
            throw "SBOM contains an invalid or duplicate package entry: $packageId"
        }
    }

    $rootPackages = @($packages | Where-Object SPDXID -eq "SPDXRef-Package-StreamlinkVlcStudio")
    if ($rootPackages.Count -ne 1 -or $rootPackages[0].filesAnalyzed -ne $true) {
        throw "SBOM must contain exactly one analyzed Streamlink VLC Studio root package."
    }

    $expectedDependencies = @(Get-CanonicalDependencyDescriptors)
    $dependencyPackages = @($packages | Where-Object { $_.SPDXID -ne 'SPDXRef-Package-StreamlinkVlcStudio' })
    if ($dependencyPackages.Count -ne $expectedDependencies.Count) {
        throw "SBOM dependency count does not match canonical inputs. SBOM: $($dependencyPackages.Count); expected: $($expectedDependencies.Count)."
    }
    $actualDependencyKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($package in $dependencyPackages) {
        $comment = [string]$package.comment
        if ($comment -notmatch '^SVS-Dependency-Key:(?<key>\S+)') {
            throw "SBOM dependency package is missing its canonical key: $($package.SPDXID)"
        }
        $key = $Matches.key
        if (-not $actualDependencyKeys.Add($key)) {
            throw "SBOM contains duplicate canonical dependency key: $key"
        }
        $expected = @($expectedDependencies | Where-Object { $_.Key -ceq $key })
        if ($expected.Count -ne 1 -or
            [string]$package.name -cne [string]$expected[0].Name -or
            [string]$package.versionInfo -cne [string]$expected[0].Version -or
            [string]$package.downloadLocation -cne [string]$expected[0].DownloadLocation) {
            throw "SBOM dependency metadata does not match canonical input: $key"
        }
        if (-not [string]::IsNullOrWhiteSpace($expected[0].Sha256)) {
            $hashes = @($package.checksums | Where-Object algorithm -eq 'SHA256')
            if ($hashes.Count -ne 1 -or
                ([string]$hashes[0].checksumValue).ToLowerInvariant() -cne $expected[0].Sha256) {
                throw "SBOM dependency checksum does not match canonical input: $key"
            }
        }
        $dependencyRelationships = @($relationships | Where-Object {
            $_.spdxElementId -eq 'SPDXRef-Package-StreamlinkVlcStudio' -and
            $_.relationshipType -eq 'DEPENDS_ON' -and
            $_.relatedSpdxElement -eq $package.SPDXID
        })
        if ($dependencyRelationships.Count -ne 1) {
            throw "SBOM must relate canonical dependency exactly once: $key"
        }
    }
    foreach ($expected in $expectedDependencies) {
        if (-not $actualDependencyKeys.Contains($expected.Key)) {
            throw "SBOM omits canonical dependency: $($expected.Key)"
        }
    }
    $verificationCode = [string]$rootPackages[0].packageVerificationCode.packageVerificationCodeValue
    if ($verificationCode -notmatch '^[0-9a-f]{40}$') {
        throw "SBOM root package is missing a valid package verification code."
    }

    $describes = @(
        $relationships | Where-Object {
            $_.spdxElementId -eq "SPDXRef-DOCUMENT" -and
            $_.relationshipType -eq "DESCRIBES" -and
            $_.relatedSpdxElement -eq "SPDXRef-Package-StreamlinkVlcStudio"
        }
    )
    if ($describes.Count -ne 1) {
        throw "SBOM must describe the root package exactly once."
    }

    $actualFiles = Get-RootFiles
    $actualPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($actualFile in $actualFiles) {
        $null = $actualPaths.Add((Get-RelativeSlashPath $root $actualFile.FullName))
    }
    if ($files.Count -ne $actualPaths.Count) {
        throw "SBOM file count does not match its root. SBOM: $($files.Count); root: $($actualPaths.Count)."
    }

    $fileIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $sha1Values = [Collections.Generic.List[string]]::new()
    $rootPrefix = $root.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    foreach ($entry in $files) {
        $relativePath = [string]$entry.fileName
        $fileId = [string]$entry.SPDXID
        if ($fileId -notmatch '^SPDXRef-[A-Za-z0-9.-]+$' -or -not $fileIds.Add($fileId)) {
            throw "SBOM contains an invalid or duplicate file SPDX identifier: $fileId"
        }
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains('\') -or
            $relativePath -match '(^|/)\.\.(/|$)') {
            throw "SBOM contains an unsafe or non-canonical file path: $relativePath"
        }

        $filePath = [IO.Path]::GetFullPath((Join-Path $root $relativePath))
        if (-not $filePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $filePath -PathType Leaf) -or
            -not $actualPaths.Remove($relativePath)) {
            throw "SBOM file is missing, duplicated, or outside its root: $relativePath"
        }
        if (-not [string]::Equals(
                $relativePath,
                (Get-RelativeSlashPath $root $filePath),
                [StringComparison]::Ordinal)) {
            throw "SBOM file path does not use the root's canonical casing: $relativePath"
        }
        if ([string]::IsNullOrWhiteSpace([string]$entry.licenseConcluded) -or
            @($entry.licenseInfoInFiles).Count -eq 0 -or
            [string]::IsNullOrWhiteSpace([string]$entry.copyrightText)) {
            throw "SBOM file is missing required license/copyright fields: $relativePath"
        }

        $sha1Entries = @($entry.checksums | Where-Object algorithm -eq "SHA1")
        $sha256Entries = @($entry.checksums | Where-Object algorithm -eq "SHA256")
        if ($sha1Entries.Count -ne 1 -or $sha256Entries.Count -ne 1) {
            throw "SBOM file must contain exactly one SHA1 and one SHA256 checksum: $relativePath"
        }
        $expectedSha1 = ([string]$sha1Entries[0].checksumValue).ToLowerInvariant()
        $expectedSha256 = ([string]$sha256Entries[0].checksumValue).ToLowerInvariant()
        if ($expectedSha1 -notmatch '^[0-9a-f]{40}$' -or $expectedSha256 -notmatch '^[0-9a-f]{64}$' -or
            (Get-FileHash -LiteralPath $filePath -Algorithm SHA1).Hash.ToLowerInvariant() -ne $expectedSha1 -or
            (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedSha256) {
            throw "SBOM checksum mismatch for $relativePath."
        }
        $sha1Values.Add($expectedSha1)

        $contains = @(
            $relationships | Where-Object {
                $_.spdxElementId -eq "SPDXRef-Package-StreamlinkVlcStudio" -and
                $_.relationshipType -eq "CONTAINS" -and
                $_.relatedSpdxElement -eq $fileId
            }
        )
        if ($contains.Count -ne 1) {
            throw "SBOM root package must contain file $fileId exactly once."
        }
    }
    if ($actualPaths.Count -ne 0) {
        throw "SBOM omits files from its root: $(@($actualPaths) -join ', ')"
    }

    $actualVerificationCode = Get-PackageVerificationCode ($sha1Values.ToArray())
    if (-not [string]::Equals($verificationCode, $actualVerificationCode, [StringComparison]::Ordinal)) {
        throw "SBOM package verification code mismatch."
    }

    Write-Host "Verified SPDX 2.3 SBOM with $($files.Count) files and $($packages.Count) packages."
    return
}

$files = @()
$relationships = @()
$sha1Values = [Collections.Generic.List[string]]::new()
$index = 0
foreach ($file in Get-RootFiles) {
    $index++
    $spdxId = "SPDXRef-File-$index"
    $sha1 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA1).Hash.ToLowerInvariant()
    $sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sha1Values.Add($sha1)
    $files += [ordered]@{
        SPDXID = $spdxId
        fileName = Get-RelativeSlashPath $root $file.FullName
        checksums = @(
            [ordered]@{ algorithm = "SHA1"; checksumValue = $sha1 },
            [ordered]@{ algorithm = "SHA256"; checksumValue = $sha256 }
        )
        licenseConcluded = "NOASSERTION"
        licenseInfoInFiles = @("NOASSERTION")
        copyrightText = "NOASSERTION"
    }
    $relationships += [ordered]@{
        spdxElementId = "SPDXRef-Package-StreamlinkVlcStudio"
        relationshipType = "CONTAINS"
        relatedSpdxElement = $spdxId
    }
}

$packages = @([ordered]@{
    SPDXID = "SPDXRef-Package-StreamlinkVlcStudio"
    name = "Streamlink VLC Studio"
    versionInfo = "build"
    downloadLocation = "NOASSERTION"
    filesAnalyzed = $true
    packageVerificationCode = [ordered]@{
        packageVerificationCodeValue = Get-PackageVerificationCode ($sha1Values.ToArray())
    }
    licenseConcluded = "NOASSERTION"
    licenseDeclared = "NOASSERTION"
    copyrightText = "NOASSERTION"
})

$packageIndex = 0
foreach ($dependency in @(Get-CanonicalDependencyDescriptors)) {
    $packageIndex++
    $packageId = "SPDXRef-Dependency-$packageIndex"
    $package = [ordered]@{
        SPDXID = $packageId
        name = $dependency.Name
        versionInfo = $dependency.Version
        downloadLocation = $dependency.DownloadLocation
        filesAnalyzed = $false
        licenseConcluded = "NOASSERTION"
        licenseDeclared = "NOASSERTION"
        copyrightText = "NOASSERTION"
        comment = "SVS-Dependency-Key:$($dependency.Key) $($dependency.Detail)"
        externalRefs = @([ordered]@{
            referenceCategory = "PACKAGE-MANAGER"
            referenceType = "purl"
            referenceLocator = if ($dependency.Kind -eq 'nuget') {
                "pkg:nuget/$([Uri]::EscapeDataString($dependency.Name))@$([Uri]::EscapeDataString($dependency.Version))"
            } else {
                "pkg:generic/$([Uri]::EscapeDataString($dependency.Name))@$([Uri]::EscapeDataString($dependency.Version))"
            }
        })
    }
    if (-not [string]::IsNullOrWhiteSpace($dependency.Sha256)) {
        $package.checksums = @([ordered]@{
            algorithm = 'SHA256'
            checksumValue = $dependency.Sha256
        })
    }
    $packages += $package
    $relationships += [ordered]@{
        spdxElementId = "SPDXRef-Package-StreamlinkVlcStudio"
        relationshipType = "DEPENDS_ON"
        relatedSpdxElement = $packageId
    }
}

$document = [ordered]@{
    spdxVersion = "SPDX-2.3"
    dataLicense = "CC0-1.0"
    SPDXID = "SPDXRef-DOCUMENT"
    name = "Streamlink VLC Studio release"
    documentNamespace = $DocumentNamespace.TrimEnd('/') + "/" + [Guid]::NewGuid().ToString("N")
    creationInfo = [ordered]@{
        created = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        creators = @("Tool: scripts/generate-sbom.ps1")
    }
    packages = $packages
    files = $files
    relationships = @(
        [ordered]@{
            spdxElementId = "SPDXRef-DOCUMENT"
            relationshipType = "DESCRIBES"
            relatedSpdxElement = "SPDXRef-Package-StreamlinkVlcStudio"
        }
    ) + $relationships
}

$parent = Split-Path -Parent $output
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$temporary = "$output.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText(
        $temporary,
        ($document | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $output -Force
} finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

Write-Host "Generated SPDX 2.3 SBOM with $($files.Count) files and $($packages.Count) packages: $output"
