$script:InstallOwnerFileName = ".streamlink-vlc-studio-owner.json"
$script:InstallManifestFileName = ".streamlink-vlc-studio-files.json"
$script:InstallProductId = "streamlink-vlc-studio"

function Get-InstallRelativePath([string]$Root, [string]$Path) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
    $pathFull = [IO.Path]::GetFullPath($Path)
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside installation root. Root: $rootFull Path: $pathFull"
    }
    $pathFull.Substring($prefix.Length).Replace('\', '/')
}

function Test-SafeInstallRelativePath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.IndexOf([char]0) -ge 0 -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        return $false
    }
    $true
}

function Get-SafeInstallFiles([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Installation trees cannot contain symbolic links or junctions: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            } else {
                $files.Add($item)
            }
        }
    }

    @($files | Sort-Object FullName)
}

function Write-JsonAtomically([string]$Path, $Value) {
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = Join-Path $parent ("." + [IO.Path]::GetFileName($Path) + "." + [Guid]::NewGuid().ToString("N") + ".tmp")
    try {
        [IO.File]::WriteAllText(
            $temporary,
            ($Value | ConvertTo-Json -Depth 12),
            [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $backup = $temporary + ".backup"
            try {
                [IO.File]::Replace($temporary, $Path, $backup, $true)
            } finally {
                if (Test-Path -LiteralPath $backup -PathType Leaf) {
                    Remove-Item -LiteralPath $backup -Force
                }
            }
        } else {
            Move-Item -LiteralPath $temporary -Destination $Path
        }
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Write-InstallOwnershipState {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [string]$InstallId,
        [string[]]$ManagedRelativePaths
    )

    $root = [IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Cannot write ownership state for a missing directory: $root"
    }
    if ([string]::IsNullOrWhiteSpace($InstallId)) {
        $InstallId = [Guid]::NewGuid().ToString("D")
    }

    $candidateFiles = if ($null -eq $ManagedRelativePaths) {
        @(Get-SafeInstallFiles $root)
    } else {
        @($ManagedRelativePaths | Sort-Object -Unique | ForEach-Object {
            if (-not (Test-SafeInstallRelativePath $_)) {
                throw "Unsafe managed installation path: '$_'"
            }
            $managedPath = Join-Path $root $_
            if (-not (Test-Path -LiteralPath $managedPath -PathType Leaf)) {
                throw "Managed installation file is missing: $_"
            }
            Get-Item -LiteralPath $managedPath -Force
        })
    }

    $files = @()
    foreach ($file in $candidateFiles) {
        $relative = Get-InstallRelativePath $root $file.FullName
        if ($relative -in @($script:InstallOwnerFileName, $script:InstallManifestFileName)) {
            continue
        }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Installation payload contains a symbolic link or reparse point: $($file.FullName)"
        }
        $files += [ordered]@{
            path = $relative
            length = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        product = $script:InstallProductId
        installId = $InstallId
        generatedUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        files = $files
    }
    $manifestPath = Join-Path $root $script:InstallManifestFileName
    Write-JsonAtomically $manifestPath $manifest
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $owner = [ordered]@{
        schemaVersion = 1
        product = $script:InstallProductId
        installId = $InstallId
        createdUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        manifest = $script:InstallManifestFileName
        manifestSha256 = $manifestHash
    }
    Write-JsonAtomically (Join-Path $root $script:InstallOwnerFileName) $owner
    $owner
}

function Read-InstallOwnershipState([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    $ownerPath = Join-Path $root $script:InstallOwnerFileName
    $manifestPath = Join-Path $root $script:InstallManifestFileName
    if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return $null
    }

    try {
        $owner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    } catch {
        throw "Installation ownership state is corrupt in '$root'. $($_.Exception.Message)"
    }
    if ($owner.schemaVersion -ne 1 -or $manifest.schemaVersion -ne 1 -or
        $owner.product -ne $script:InstallProductId -or $manifest.product -ne $script:InstallProductId -or
        [string]::IsNullOrWhiteSpace([string]$owner.installId) -or
        $owner.installId -ne $manifest.installId) {
        throw "Installation ownership state does not identify Streamlink VLC Studio: $root"
    }
    $actualManifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($actualManifestHash, [string]$owner.manifestSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installation manifest hash does not match its ownership marker: $root"
    }

    $paths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.path
        if (-not (Test-SafeInstallRelativePath $relative) -or -not $paths.Add($relative)) {
            throw "Installation manifest contains an unsafe or duplicate path: '$relative'"
        }
    }
    [pscustomobject]@{ Owner = $owner; Manifest = $manifest; Paths = $paths }
}

function Test-ExactLegacyInstall([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath (Join-Path $root "StreamlinkVlcStudio.exe") -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $root "Uninstall.exe") -PathType Leaf)) {
        return $false
    }

    $allowedTopLevel = @(
        "StreamlinkVlcStudio.exe",
        "Uninstall.exe",
        "THIRD-PARTY-NOTICES.md",
        "install.ps1",
        "native-overlay-provenance.json",
        "browser-extension",
        "vlc-overlay",
        "lib",
        "dependencies"
    )
    foreach ($item in Get-ChildItem -LiteralPath $root -Force) {
        if ($item.Name -notin $allowedTopLevel -or
            (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            return $false
        }
    }
    try {
        Get-SafeInstallFiles $root | Out-Null
    } catch {
        return $false
    }
    $true
}

function Assert-OwnedOrEmptyInstallDestination([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    if (-not (Test-Path -LiteralPath $root)) {
        return $null
    }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Install destination is not a directory: $root"
    }
    $items = @(Get-ChildItem -LiteralPath $root -Force)
    if ($items.Count -eq 0) {
        return $null
    }

    $state = Read-InstallOwnershipState $root
    if ($null -ne $state) {
        return $state
    }
    if (Test-ExactLegacyInstall $root) {
        Write-Host "    Migrating exact legacy dedicated installation ownership state."
        Write-InstallOwnershipState -Directory $root | Out-Null
        return Read-InstallOwnershipState $root
    }
    throw "Refusing to install into nonempty unowned destination: $root"
}

function Copy-UnmanagedInstallFiles {
    param(
        [Parameter(Mandatory = $true)][string]$ExistingDirectory,
        [Parameter(Mandatory = $true)][string]$StagingDirectory,
        [Parameter(Mandatory = $true)]$ExistingState
    )

    foreach ($file in Get-SafeInstallFiles $ExistingDirectory) {
        $relative = Get-InstallRelativePath $ExistingDirectory $file.FullName
        if ($relative -in @($script:InstallOwnerFileName, $script:InstallManifestFileName) -or
            $ExistingState.Paths.Contains($relative)) {
            continue
        }
        $target = Join-Path $StagingDirectory $relative
        if (Test-Path -LiteralPath $target) {
            throw "Upgrade payload conflicts with user-created file '$relative'; the existing installation was not changed."
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
