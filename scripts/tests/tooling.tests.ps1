[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
. (Join-Path $scriptRoot 'lib\common.ps1')
. (Join-Path $scriptRoot 'lib\dependency-manifest.ps1')
. (Join-Path $scriptRoot 'lib\native-overlay.ps1')
. (Join-Path $scriptRoot 'lib\release-contract.ps1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try {
        & $Action
    } catch {
        if ($_.Exception.Message -notmatch $Pattern) {
            throw "Expected failure matching '$Pattern', got: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected an exception matching '$Pattern'."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('StreamlinkVlcStudio-tooling-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $manifest = Read-WindowsDependencyManifest (Join-Path $repoRoot 'dependencies\windows-installers.json')
    Assert-True ([int64]$manifest.dependencies.streamlink.length -gt 0) 'Canonical dependency length was not read.'
    $obsoleteManifestPath = Join-Path $testRoot 'obsolete-manifest.json'
    $obsolete = Get-Content -LiteralPath (Join-Path $repoRoot 'dependencies\windows-installers.json') -Raw | ConvertFrom-Json
    $obsolete.dependencies.streamlink | Add-Member -NotePropertyName expectedLength -NotePropertyValue 1
    $obsolete | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $obsoleteManifestPath -Encoding UTF8
    Assert-Throws { Read-WindowsDependencyManifest $obsoleteManifestPath | Out-Null } 'expectedLength'
    Write-Host 'PASS tooling: dependency manifests use canonical length'

    $candidateRoot = Join-Path $testRoot 'candidates'
    New-Item -ItemType Directory -Path $candidateRoot | Out-Null
    $old = Join-Path $candidateRoot 'old.exe'
    $new = Join-Path $candidateRoot 'new.exe'
    [IO.File]::WriteAllText($old, 'old')
    [IO.File]::WriteAllText($new, 'new')
    $versions = @{ $old = '7.9.0'; $new = '8.6.1' }
    $selected = Select-CompatibleDependencyCandidate `
        -CandidatePaths @($old, $new) `
        -MinimumVersion '8.5.0-1' `
        -VersionReader { param($path) $versions[$path] } `
        -Description 'stub dependency'
    Assert-True ([string]::Equals($selected.Path, $new, [StringComparison]::OrdinalIgnoreCase)) 'Compatible candidate selection chose the wrong executable.'
    Assert-Throws {
        Select-CompatibleDependencyCandidate `
            -CandidatePaths @($old) `
            -MinimumVersion '8.5.0' `
            -VersionReader { param($path) $versions[$path] } `
            -Description 'stub dependency' | Out-Null
    } 'below 8\.5\.0'
    Write-Host 'PASS tooling: compatible installed dependency selection'

    Assert-Throws {
        Save-HttpFileAtomically `
            -Uri 'http://example.invalid/dependency.exe' `
            -DestinationPath (Join-Path $testRoot 'insecure-download.exe')
    } 'absolute HTTPS'
    Write-Host 'PASS tooling: shared downloads require HTTPS'

    Assert-True (Test-SafeWindowsPathSegment 'release-package.zip') 'A canonical Windows file name was rejected.'
    $superscriptOne = [char]0x00B9
    $superscriptTwo = [char]0x00B2
    foreach ($unsafeName in @(
            'CON',
            'CONIN$.txt',
            ('COM{0}.log' -f $superscriptOne),
            ('LPT{0}' -f $superscriptTwo),
            'payload.',
            'payload:stream',
            '..\payload')) {
        Assert-True (-not (Test-SafeWindowsPathSegment $unsafeName)) "Unsafe Windows file name was accepted: $unsafeName"
    }
    foreach ($unsafeRelativePath in @('payload/./app.exe', 'payload//app.exe', 'payload/app.exe.', 'payload/app.exe:stream')) {
        Assert-True (-not (Test-SafeContractRelativePath $unsafeRelativePath)) "Unsafe contract path was accepted: $unsafeRelativePath"
    }
    Write-Host 'PASS tooling: Windows leaf names and contract paths are canonical'

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unsafeArchivePath = Join-Path $testRoot 'unsafe.zip'
    $unsafeArchive = [IO.Compression.ZipFile]::Open(
        $unsafeArchivePath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $unsafeArchive.CreateEntry('../outside.txt')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write('escape attempt')
        } finally {
            $writer.Dispose()
        }
    } finally {
        $unsafeArchive.Dispose()
    }
    $unsafeDestination = Join-Path $testRoot 'unsafe-extract'
    Assert-Throws {
        Expand-ValidatedZipArchive $unsafeArchivePath $unsafeDestination
    } 'unsafe path segment'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot 'outside.txt'))) 'Unsafe ZIP entry escaped its extraction root.'

    $validArchivePath = Join-Path $testRoot 'valid.zip'
    $validArchive = [IO.Compression.ZipFile]::Open(
        $validArchivePath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $validArchive.CreateEntry('payload/app.txt')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write('validated payload')
        } finally {
            $writer.Dispose()
        }
        $entry = $validArchive.CreateEntry('payload\windows-path.txt')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write('windows archive path')
        } finally {
            $writer.Dispose()
        }
    } finally {
        $validArchive.Dispose()
    }
    $validDestination = Join-Path $testRoot 'valid-extract'
    Expand-ValidatedZipArchive $validArchivePath $validDestination
    Assert-True (
        (Get-Content -LiteralPath (Join-Path $validDestination 'payload\app.txt') -Raw) -eq 'validated payload') `
        'Validated ZIP extraction did not preserve the payload.'
    Assert-True (
        (Get-Content -LiteralPath (Join-Path $validDestination 'payload\windows-path.txt') -Raw) -eq 'windows archive path') `
        'Validated ZIP extraction did not accept Windows archive separators.'
    Write-Host 'PASS tooling: ZIP extraction is bounded, canonical, and traversal-safe'

    $junctionTarget = Join-Path $testRoot 'junction-target'
    $junctionPath = Join-Path $testRoot 'junction-parent'
    New-Item -ItemType Directory -Path $junctionTarget | Out-Null
    New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget | Out-Null
    Assert-Throws {
        Promote-ValidatedFileSetAtomically @(
            [pscustomobject]@{
                Source = $validArchivePath
                Destination = Join-Path $junctionPath 'created\promoted.zip'
            }
        )
    } 'junction|symbolic link'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $junctionTarget 'created'))) `
        'A rejected promotion created a directory through a junction before validating its parent.'
    Assert-Throws {
        Expand-ValidatedZipArchive `
            $validArchivePath `
            (Join-Path $junctionPath 'extract-created\payload')
    } 'junction|symbolic link'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $junctionTarget 'extract-created'))) `
        'A rejected ZIP extraction created a directory through a junction before validating its parent.'
    Write-Host 'PASS tooling: parent junctions are rejected before promotion writes'

    $promotionRoot = Join-Path $testRoot 'promotion-rollback'
    New-Item -ItemType Directory -Path $promotionRoot | Out-Null
    $firstSource = Join-Path $promotionRoot 'first-source.bin'
    $secondSource = Join-Path $promotionRoot 'second-source.bin'
    $firstDestination = Join-Path $promotionRoot 'first-output.bin'
    $blockedDestination = Join-Path $promotionRoot 'blocked-output.bin'
    [IO.File]::WriteAllText($firstSource, 'new first')
    [IO.File]::WriteAllText($secondSource, 'new second')
    [IO.File]::WriteAllText($firstDestination, 'previous first')
    New-Item -ItemType Directory -Path $blockedDestination | Out-Null
    Assert-Throws {
        Promote-ValidatedFileSetAtomically @(
            [pscustomobject]@{ Source = $firstSource; Destination = $firstDestination },
            [pscustomobject]@{ Source = $secondSource; Destination = $blockedDestination }
        )
    } 'promotion failed'
    Assert-True ((Get-Content -LiteralPath $firstDestination -Raw) -eq 'previous first') `
        'A failed multi-file promotion did not restore the previous output.'
    Assert-True (@(Get-ChildItem -LiteralPath $promotionRoot -Force | Where-Object {
        $_.Name -match '\.(?:backup|promote)-'
    }).Count -eq 0) 'A failed multi-file promotion left temporary or backup files behind.'
    Write-Host 'PASS tooling: multi-file promotion rolls back earlier replacements'

    $overlaySource = Join-Path $testRoot 'overlay'
    New-Item -ItemType Directory -Path (Join-Path $overlaySource 'build') -Force | Out-Null
    $canonicalBuild = Join-Path $repoRoot 'src\StreamlinkVlcStudio.Infrastructure\Vlc\BundledOverlay\build'
    Copy-Item -LiteralPath (Join-Path $canonicalBuild 'libmyoverlay_plugin.dll') -Destination (Join-Path $overlaySource 'build\libmyoverlay_plugin.dll')
    Copy-Item -LiteralPath (Join-Path $canonicalBuild 'vlc_chat_overlay.exe') -Destination (Join-Path $overlaySource 'build\vlc_chat_overlay.exe')
    $nativeManifest = Join-Path $repoRoot 'dependencies\native-overlay.json'
    Assert-True (@(Assert-NativeOverlaySource $overlaySource $nativeManifest).Count -eq 2) 'Canonical alternate overlay was not accepted.'
    [IO.File]::AppendAllText((Join-Path $overlaySource 'build\libmyoverlay_plugin.dll'), 'altered')
    Assert-Throws { Assert-NativeOverlaySource $overlaySource $nativeManifest | Out-Null } 'length mismatch'
    Copy-Item -LiteralPath (Join-Path $canonicalBuild 'libmyoverlay_plugin.dll') -Destination (Join-Path $overlaySource 'build\libmyoverlay_plugin.dll') -Force
    [IO.File]::WriteAllText((Join-Path $overlaySource 'build\.hidden'), 'unexpected')
    Assert-Throws { Assert-NativeOverlaySource $overlaySource $nativeManifest | Out-Null } 'unlisted: build/\.hidden'
    Write-Host 'PASS tooling: selected native overlay provenance is closed and pinned'

    $contract = Read-ReleaseContract (Join-Path $repoRoot 'shared\release-contract.json')
    Assert-Throws {
        Resolve-ReleasePayloadRoot $junctionPath $contract -AllowNone | Out-Null
    } 'junction|symbolic link'

    $duplicateContractPath = Join-Path $testRoot 'case-duplicate-contract.json'
    $duplicateContract = Get-Content -LiteralPath (Join-Path $repoRoot 'shared\release-contract.json') -Raw | ConvertFrom-Json
    $originalAsset = @($duplicateContract.releaseSet | Where-Object { [bool]$_.checksummed })[0]
    $duplicateName = ([string]$originalAsset.name).ToUpperInvariant()
    if ([string]::Equals($duplicateName, [string]$originalAsset.name, [StringComparison]::Ordinal)) {
        $duplicateName = ([string]$originalAsset.name).ToLowerInvariant()
    }
    $originalOutput = [string]$duplicateContract.outputs.PSObject.Properties[[string]$originalAsset.output].Value
    $normalizedOutput = $originalOutput.Replace('\', '/')
    $duplicateOutput = $normalizedOutput.Substring(0, $normalizedOutput.LastIndexOf('/') + 1) + $duplicateName
    $duplicateContract.outputs | Add-Member -NotePropertyName 'caseDuplicateForTest' -NotePropertyValue $duplicateOutput
    $duplicateContract.releaseSet = @($duplicateContract.releaseSet) + [pscustomobject]@{
        name = $duplicateName
        output = 'caseDuplicateForTest'
        checksummed = $true
    }
    $duplicateContract | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $duplicateContractPath -Encoding UTF8
    Assert-Throws { Read-ReleaseContract $duplicateContractPath | Out-Null } 'invalid or duplicate release-set entry'

    $ambiguous = Join-Path $testRoot 'ambiguous-payload'
    New-Item -ItemType Directory -Path (Join-Path $ambiguous 'one') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $ambiguous 'two') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $ambiguous 'one\StreamlinkVlcStudio.exe'), 'one')
    [IO.File]::WriteAllText((Join-Path $ambiguous 'two\StreamlinkVlcStudio.exe'), 'two')
    Assert-Throws { Resolve-ReleasePayloadRoot $ambiguous $contract | Out-Null } 'exactly one'
    Write-Host 'PASS tooling: ambiguous payload roots are rejected'

    $routeRoot = Join-Path $testRoot 'routes'
    New-Item -ItemType Directory -Path (Join-Path $routeRoot 'shared') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $routeRoot 'browser-extension') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'shared\platform-routes.json') -Destination (Join-Path $routeRoot 'shared\platform-routes.json')
    & (Join-Path $scriptRoot 'generate-browser-route-policy.ps1') -RepositoryRoot $routeRoot
    & (Join-Path $scriptRoot 'generate-browser-route-policy.ps1') -RepositoryRoot $routeRoot -Check
    [IO.File]::AppendAllText((Join-Path $routeRoot 'browser-extension\platform-routes.generated.js'), "`n// stale")
    Assert-Throws {
        & (Join-Path $scriptRoot 'generate-browser-route-policy.ps1') -RepositoryRoot $routeRoot -Check
    } 'stale'
    Write-Host 'PASS tooling: stale generated browser routes are rejected'

    $releaseRepo = Join-Path $testRoot 'release-repo'
    New-Item -ItemType Directory -Path $releaseRepo | Out-Null
    foreach ($entry in @($contract.releaseSet | Where-Object { [bool]$_.checksummed })) {
        $path = Get-ReleaseContractOutputPath $contract $releaseRepo ([string]$entry.output)
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, "stub $($entry.name)")
    }
    Assert-Throws {
        New-VerifiedReleaseSet `
            $contract `
            $releaseRepo `
            (Join-Path $junctionPath 'release-created\set') | Out-Null
    } 'junction|symbolic link'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $junctionTarget 'release-created'))) `
        'A rejected release-set promotion created a directory through a junction before validating its parent.'
    $releaseSetRoot = Join-Path $releaseRepo 'artifacts\release-set'
    $releaseFiles = @(New-VerifiedReleaseSet $contract $releaseRepo $releaseSetRoot)
    Assert-True ($releaseFiles.Count -eq @($contract.releaseSet).Count) 'Atomic release set has the wrong asset count.'
    [IO.File]::WriteAllText((Join-Path $releaseSetRoot 'unexpected.txt'), 'unexpected')
    Assert-Throws { Test-VerifiedReleaseSet $contract $releaseSetRoot | Out-Null } 'Unexpected files'
    Remove-Item -LiteralPath (Join-Path $releaseSetRoot 'unexpected.txt') -Force
    Test-VerifiedReleaseSet $contract $releaseSetRoot | Out-Null
    $unexpectedDirectory = Join-Path $releaseSetRoot 'nested'
    New-Item -ItemType Directory -Path $unexpectedDirectory | Out-Null
    Assert-Throws { Test-VerifiedReleaseSet $contract $releaseSetRoot | Out-Null } 'Unexpected directories'
    Remove-DirectoryTreeSafely $unexpectedDirectory
    Test-VerifiedReleaseSet $contract $releaseSetRoot | Out-Null
    Write-Host 'PASS tooling: exact release sets are atomically staged and independently verified'

    $sbomRoot = Join-Path $testRoot 'sbom-root'
    New-Item -ItemType Directory -Path $sbomRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $sbomRoot 'app.exe'), 'stub app')
    $sbomPath = Join-Path $testRoot 'test.spdx.json'
    $assetsPath = Join-Path $repoRoot 'src\StreamlinkVlcStudio.App.Wpf\obj\project.assets.json'
    $publishedDepsPath = Join-Path $testRoot 'published.deps.json'
    $publishedDeps = [ordered]@{
        runtimeTarget = [ordered]@{
            name = '.NETCoreApp,Version=v10.0/win-x64'
            signature = ''
        }
        targets = [ordered]@{}
        libraries = [ordered]@{
            'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.10' = [ordered]@{
                type = 'runtimepack'
                serviceable = $false
                sha512 = ''
            }
            'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.10' = [ordered]@{
                type = 'runtimepack'
                serviceable = $false
                sha512 = ''
            }
            'runtimepack.Microsoft.Windows.SDK.NET.Ref/10.0.19041.57' = [ordered]@{
                type = 'runtimepack'
                serviceable = $false
                sha512 = ''
            }
        }
    }
    [IO.File]::WriteAllText(
        $publishedDepsPath,
        ($publishedDeps | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    & (Join-Path $scriptRoot 'generate-sbom.ps1') `
        -RootDirectory $sbomRoot `
        -OutputPath $sbomPath `
        -ProjectAssetsPath $assetsPath `
        -PublishedDepsPath $publishedDepsPath `
        -DocumentNamespace 'https://example.invalid/test-sbom'
    & (Join-Path $scriptRoot 'generate-sbom.ps1') `
        -RootDirectory $sbomRoot `
        -OutputPath $sbomPath `
        -ProjectAssetsPath $assetsPath `
        -PublishedDepsPath $publishedDepsPath `
        -Verify
    $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
    $runtimePackage = @($sbom.packages | Where-Object { ([string]$_.comment).StartsWith('SVS-Dependency-Key:runtimepack|') })[0]
    $sbom.packages = @($sbom.packages | Where-Object { $_.SPDXID -ne $runtimePackage.SPDXID })
    $sbom.relationships = @($sbom.relationships | Where-Object { $_.relatedSpdxElement -ne $runtimePackage.SPDXID })
    $sbom | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $sbomPath -Encoding UTF8
    Assert-Throws {
        & (Join-Path $scriptRoot 'generate-sbom.ps1') `
            -RootDirectory $sbomRoot `
            -OutputPath $sbomPath `
            -ProjectAssetsPath $assetsPath `
            -PublishedDepsPath $publishedDepsPath `
            -Verify
    } 'dependency count|omits canonical dependency'
    Write-Host 'PASS tooling: SBOM verification reconstructs runtime-pack dependencies'

    $version = Get-MsiProductVersion -RunNumber 123 -RunAttempt 2
    Assert-True ($version.Ordinal -eq 12302) 'MSI build ordinal calculation changed.'
    Assert-True ($version.ProductVersion -match '^\d+\.\d+\.\d+$') 'MSI semantic product version is invalid.'
    Write-Host 'PASS tooling: MSI versions use the shared semantic calculation'
} finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-DirectoryTreeSafely $testRoot
    }
}
