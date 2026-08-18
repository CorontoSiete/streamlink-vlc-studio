<#
Shared PowerShell helpers for the packaging / installer scripts.

Dot-source this file:

    . "$PSScriptRoot\lib\common.ps1"

All functions treat every path as a full path and normalize trailing
separators so `-LiteralPath` comparisons behave the same on any drive layout.
#>

$script:PathSeparators = [char[]]@('\', '/')

function Write-Info {
    <#
    .SYNOPSIS
    Writes an informational message unless `$Quiet` is truthy in the caller's
    scope chain.

    .DESCRIPTION
    Each packaging script exposes a `[switch]$Quiet` parameter. Because this
    file is dot-sourced, PowerShell resolves the bare `$Quiet` reference here
    against the caller's dynamic scope chain, so no threading through helpers
    is required.
    #>
    param([string]$Message)

    if ($Quiet) { return }
    Write-Host $Message
}

function Get-FullPathNormalized {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::Equals($fullPath, $rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $rootPath
    }

    $fullPath.TrimEnd($script:PathSeparators)
}

function Add-TrailingDirectorySeparator {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = Get-FullPathNormalized $Path
    if ($fullPath.EndsWith([System.IO.Path]::DirectorySeparatorChar) -or
        $fullPath.EndsWith([System.IO.Path]::AltDirectorySeparatorChar)) {
        return $fullPath
    }

    $fullPath + [System.IO.Path]::DirectorySeparatorChar
}

function Test-PathIsSameOrUnderDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$ChildPath,
        [Parameter(Mandatory = $true)][string]$ParentPath)

    $childFull = Get-FullPathNormalized $ChildPath
    $parentFull = Get-FullPathNormalized $ParentPath
    [string]::Equals($childFull, $parentFull, [System.StringComparison]::OrdinalIgnoreCase) -or
        $childFull.StartsWith(
            (Add-TrailingDirectorySeparator $parentFull),
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-SafeWindowsPathSegment {
    <#
    .SYNOPSIS
    Returns true only for a single, canonical Windows file-name segment.

    .DESCRIPTION
    Centralizes the Windows name rules shared by archive, download, and
    installer-output validation. In particular, it rejects alternate data
    streams, trailing dots/spaces, and reserved DOS device names.
    #>
    param([AllowNull()][string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name) -or
        $Name -in @('.', '..') -or
        $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        -not [string]::Equals(
            $Name.TrimEnd([char[]]@(' ', '.')),
            $Name,
            [StringComparison]::Ordinal)) {
        return $false
    }

    $deviceName = ($Name -split '\.', 2)[0].TrimEnd(' ')
    # Windows also treats the superscript 1/2/3 suffixes as digits for COM/LPT,
    # and maps its console aliases to the device namespace.
    $superscriptDigits = [string]([char[]]@(0x00B9, 0x00B2, 0x00B3))
    $devicePattern = '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9' +
        $superscriptDigits + ']|LPT[1-9' + $superscriptDigits + '])$'
    $deviceName -notmatch $devicePattern
}

function Assert-NoReparsePointInExistingPath {
    <#
    .SYNOPSIS
    Rejects paths whose existing components include a symbolic link or junction.

    .DESCRIPTION
    Lexical containment checks do not prevent a path from escaping through an
    ancestor junction. This helper walks the existing path components from the
    filesystem root so callers can guard recursive writes and deletions.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$ExcludeLeaf)

    $fullPath = Get-FullPathNormalized $Path
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    $relativePath = $fullPath.Substring($rootPath.Length).Trim($script:PathSeparators)
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        return
    }

    $segments = @($relativePath -split '[\\/]')
    $segmentCount = if ($ExcludeLeaf) { $segments.Count - 1 } else { $segments.Count }
    $currentPath = $rootPath
    for ($index = 0; $index -lt $segmentCount; $index++) {
        $currentPath = Join-Path $currentPath $segments[$index]
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }

        $item = Get-Item -LiteralPath $currentPath -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to use a path below a junction or symbolic link: $currentPath"
        }
    }
}

function Remove-DirectoryTreeSafely {
    <#
    .SYNOPSIS
    Removes a directory tree without traversing any reparse points it contains.
    #>
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        # Windows PowerShell 5.1 can throw a NullReferenceException when
        # Remove-Item unlinks a directory junction. The .NET delete APIs
        # remove the link itself without traversing into its target.
        if ($item.PSIsContainer) {
            [System.IO.Directory]::Delete($item.FullName)
        } else {
            [System.IO.File]::Delete($item.FullName)
        }
        return
    }

    if (-not $item.PSIsContainer) {
        Remove-Item -LiteralPath $item.FullName -Force
        return
    }

    Get-ChildItem -LiteralPath $item.FullName -Force | ForEach-Object {
        if ($_.PSIsContainer -or
            (($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
            Remove-DirectoryTreeSafely $_.FullName
        } else {
            Remove-Item -LiteralPath $_.FullName -Force
        }
    }
    [System.IO.Directory]::Delete($item.FullName)
}

function Assert-UnderDirectory {
    <#
    .SYNOPSIS
    Throws unless $ChildPath is equal to or nested under $ParentPath.

    .DESCRIPTION
    Used as a guard rail before recursive file operations so the packaging
    scripts cannot accidentally remove anything outside the intended output
    directory even if callers pass unexpected input.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$ChildPath,
        [Parameter(Mandatory = $true)][string]$ParentPath)

    $childFull = Get-FullPathNormalized $ChildPath
    $parentFull = Get-FullPathNormalized $ParentPath

    if (-not (Test-PathIsSameOrUnderDirectory -ChildPath $childFull -ParentPath $parentFull)) {
        throw "Refusing to modify path outside expected directory. Path: $childFull Parent: $parentFull"
    }
}

function Remove-DirectoryIfExists {
    <#
    .SYNOPSIS
    Recursively removes $Path if it exists, but only when it is under $ParentPath.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ParentPath)

    $full = Get-FullPathNormalized $Path
    $parentFull = Get-FullPathNormalized $ParentPath
    Assert-UnderDirectory -ChildPath $full -ParentPath $ParentPath
    if ([string]::Equals($full, $parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to recursively remove the guard directory itself: $full"
    }

    # The leaf is handled separately below so deleting a leaf junction removes
    # only the junction itself. Every existing ancestor, including the caller's
    # guard directory, must be a real directory.
    Assert-NoReparsePointInExistingPath -Path $full -ExcludeLeaf

    Remove-DirectoryTreeSafely $full
}

function Expand-ValidatedZipArchive {
    <#
    .SYNOPSIS
    Validates and extracts a ZIP archive into a new destination directory.

    .DESCRIPTION
    Rejects path traversal, alternate data streams, duplicate Windows paths,
    reparse-point entries, and archive bombs before extracting into a temporary
    sibling directory. The completed directory is promoted only after the
    extraction succeeds, so callers never observe a partial destination.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [ValidateRange(1, 100000)][int]$MaximumEntries = 10000,
        [ValidateRange(1, [long]::MaxValue)][long]$MaximumEntryBytes = 1GB,
        [ValidateRange(1, [long]::MaxValue)][long]$MaximumTotalBytes = 2GB)

    $archiveFull = [IO.Path]::GetFullPath($ArchivePath)
    if (-not (Test-Path -LiteralPath $archiveFull -PathType Leaf)) {
        throw "ZIP archive was not found: $archiveFull"
    }
    $archiveItem = Get-Item -LiteralPath $archiveFull -Force
    if (($archiveItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ZIP archive cannot be a symbolic link or reparse point: $archiveFull"
    }

    $destinationFull = Get-FullPathNormalized $DestinationDirectory
    if (Test-Path -LiteralPath $destinationFull) {
        throw "ZIP extraction destination must not already exist: $destinationFull"
    }
    $parent = Split-Path -Parent $destinationFull
    if ([string]::IsNullOrWhiteSpace($parent) -or
        [string]::Equals($parent, $destinationFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ZIP extraction destination must be a dedicated child directory: $destinationFull"
    }
    Assert-NoReparsePointInExistingPath -Path $parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Assert-NoReparsePointInExistingPath -Path $parent

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($archiveFull)
    try {
        if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt $MaximumEntries) {
            throw "ZIP archive must contain 1 through $MaximumEntries entries; found $($archive.Entries.Count)."
        }

        $seenTargets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$totalLength = 0
        $destinationPrefix = Add-TrailingDirectorySeparator $destinationFull
        foreach ($entry in $archive.Entries) {
            $entryName = [string]$entry.FullName
            $normalizedEntryName = $entryName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($entryName) -or
                [IO.Path]::IsPathRooted($entryName) -or
                [IO.Path]::IsPathRooted($normalizedEntryName)) {
                throw "ZIP archive contains an unsafe or non-canonical path: $entryName"
            }

            $trimmedName = $normalizedEntryName.TrimEnd('/')
            $segments = @($trimmedName -split '/')
            foreach ($segment in $segments) {
                if (-not (Test-SafeWindowsPathSegment $segment)) {
                    throw "ZIP archive contains an unsafe path segment: $entryName"
                }
            }

            $target = [IO.Path]::GetFullPath((Join-Path $destinationFull $trimmedName))
            if (-not $target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not $seenTargets.Add($target)) {
                throw "ZIP archive contains an unsafe or duplicate path: $entryName"
            }

            $windowsAttributes = [int]$entry.ExternalAttributes -band 0xFFFF
            $unixFileType = ([int]$entry.ExternalAttributes -shr 16) -band 0xF000
            if (($windowsAttributes -band [int][IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $unixFileType -eq 0xA000) {
                throw "ZIP archive contains a symbolic-link or reparse-point entry: $entryName"
            }

            [long]$entryLength = $entry.Length
            if ($entryLength -lt 0 -or $entryLength -gt $MaximumEntryBytes -or
                $totalLength -gt ($MaximumTotalBytes - $entryLength)) {
                throw "ZIP archive exceeds extraction limits."
            }
            $totalLength += $entryLength
        }
    } finally {
        $archive.Dispose()
    }

    $leaf = Split-Path -Leaf $destinationFull
    $stage = Join-Path $parent (".$leaf.extract-" + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $stage | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory($archiveFull, $stage)
        Assert-NoReparsePointInExistingPath -Path $stage
        $reparseEntry = Get-ChildItem -LiteralPath $stage -Force -Recurse |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
            Select-Object -First 1
        if ($null -ne $reparseEntry) {
            throw "ZIP archive extracted a symbolic link or reparse point: $($reparseEntry.FullName)"
        }
        [IO.Directory]::Move($stage, $destinationFull)
    } finally {
        if (Test-Path -LiteralPath $stage) {
            Remove-DirectoryTreeSafely $stage
        }
    }
}

function Save-HttpFileAtomically {
    <#
    .SYNOPSIS
    Downloads a file with a deadline and promotes it only after optional validation succeeds.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][uri]$Uri,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [hashtable]$Headers = @{},
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 60,
        [ValidateRange(1, [long]::MaxValue)][long]$MaximumBytes = 536870912,
        [scriptblock]$ValidationScript)

    if (-not $Uri.IsAbsoluteUri -or
        -not [string]::Equals($Uri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Download URL must be absolute HTTPS: $Uri"
    }

    $destinationFull = [System.IO.Path]::GetFullPath($DestinationPath)
    $destinationDirectory = Split-Path -Parent $destinationFull
    if ([string]::IsNullOrWhiteSpace($destinationDirectory)) {
        throw "A destination directory is required: $DestinationPath"
    }

    Assert-NoReparsePointInExistingPath -Path $destinationDirectory
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Assert-NoReparsePointInExistingPath -Path $destinationDirectory
    $extension = [System.IO.Path]::GetExtension($destinationFull)
    $temporaryPath = Join-Path $destinationDirectory (
        ".{0}.{1}{2}" -f
            [System.IO.Path]::GetFileNameWithoutExtension($destinationFull),
            [Guid]::NewGuid().ToString("N"),
            $extension)

    try {
        Add-Type -AssemblyName System.Net.Http
        $handler = [Net.Http.HttpClientHandler]::new()
        $client = [Net.Http.HttpClient]::new($handler)
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
        $response = $null
        $input = $null
        $output = $null
        $deadline = [Threading.CancellationTokenSource]::new()
        try {
            $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
            $deadline.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds))
            foreach ($header in $Headers.GetEnumerator()) {
                if (-not $request.Headers.TryAddWithoutValidation([string]$header.Key, [string]$header.Value)) {
                    throw "Unsupported HTTP request header: $($header.Key)"
                }
            }

            $response = $client.SendAsync(
                $request,
                [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
                $deadline.Token).GetAwaiter().GetResult()
            $finalUri = $response.RequestMessage.RequestUri
            if ($null -eq $finalUri -or
                -not $finalUri.IsAbsoluteUri -or
                -not [string]::Equals($finalUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Dependency download redirected to a non-HTTPS URL: $finalUri"
            }
            if (-not $response.IsSuccessStatusCode) {
                throw "Dependency download failed with HTTP status $([int]$response.StatusCode) ($($response.ReasonPhrase)): $Uri"
            }
            $contentLength = $response.Content.Headers.ContentLength
            if ($null -ne $contentLength -and [long]$contentLength -gt $MaximumBytes) {
                throw "Dependency download exceeds the $MaximumBytes-byte limit: $Uri"
            }

            $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output = [IO.File]::Open(
                $temporaryPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            $buffer = [byte[]]::new(81920)
            [long]$totalBytes = 0
            while (($read = $input.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                $totalBytes += $read
                if ($totalBytes -gt $MaximumBytes) {
                    throw "Dependency download exceeds the $MaximumBytes-byte limit: $Uri"
                }
                $output.Write($buffer, 0, $read)
            }
        } finally {
            if ($null -ne $output) { $output.Dispose() }
            if ($null -ne $input) { $input.Dispose() }
            if ($null -ne $response) { $response.Dispose() }
            $request.Dispose()
            $client.Dispose()
            $handler.Dispose()
            $deadline.Dispose()
        }

        if (-not (Test-Path -LiteralPath $temporaryPath -PathType Leaf) -or
            (Get-Item -LiteralPath $temporaryPath -Force).Length -eq 0) {
            throw "Dependency download produced no data: $Uri"
        }

        $validationResult = if ($null -ne $ValidationScript) {
            & $ValidationScript $temporaryPath
        } else {
            $null
        }

        if (Test-Path -LiteralPath $destinationFull -PathType Leaf) {
            $destinationItem = Get-Item -LiteralPath $destinationFull -Force
            if (($destinationItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to replace a dependency through a symbolic link: $destinationFull"
            }
            $backupPath = $temporaryPath + ".backup"
            try {
                [System.IO.File]::Replace($temporaryPath, $destinationFull, $backupPath, $true)
            } finally {
                if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                    Remove-Item -LiteralPath $backupPath -Force
                }
            }
        } else {
            [System.IO.File]::Move($temporaryPath, $destinationFull)
        }

        $validationResult
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Assert-PinnedInstallerDependency {
    <#
    .SYNOPSIS
    Validates every integrity and publisher field in a locked installer dependency entry.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Dependency)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Dependency file missing: $Path"
    }

    if ([int64]$Dependency.length -le 0 -or
        ([string]$Dependency.sha256).Trim() -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]::IsNullOrWhiteSpace([string]$Dependency.authenticode)) {
        throw "Dependency manifest entry is incomplete or invalid for $Path."
    }

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -ne [int64]$Dependency.length) {
        throw "Dependency length mismatch for $Path. Expected $($Dependency.length), found $($item.Length)."
    }

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = ([string]$Dependency.sha256).Trim().ToLowerInvariant()
    if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::Ordinal)) {
        throw "Dependency SHA-256 mismatch for $Path. Expected $expectedHash, found $actualHash."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $expectedStatus = [string]$Dependency.authenticode
    if (-not [string]::Equals($signature.Status.ToString(), $expectedStatus, [StringComparison]::Ordinal)) {
        throw "Dependency Authenticode status mismatch for $Path. Expected $expectedStatus, found $($signature.Status)."
    }

    $expectedThumbprint = [string]$Dependency.expectedSignerThumbprint
    if (-not [string]::IsNullOrWhiteSpace($expectedThumbprint)) {
        $actualThumbprint = if ($signature.SignerCertificate) { $signature.SignerCertificate.Thumbprint } else { "" }
        if (-not [string]::Equals($actualThumbprint, $expectedThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Dependency signer mismatch for $Path. Expected thumbprint $expectedThumbprint, found $actualThumbprint."
        }
    }

    $expectedPublisher = [string]$Dependency.expectedPublisher
    if ($expectedStatus -eq "Valid" -and -not [string]::IsNullOrWhiteSpace($expectedPublisher)) {
        $actualPublisher = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { "" }
        if (-not [string]::Equals($actualPublisher, $expectedPublisher, [StringComparison]::Ordinal)) {
            throw "Dependency publisher mismatch for $Path. Expected '$expectedPublisher', found '$actualPublisher'."
        }
    }

    $expectedProduct = [string]$Dependency.expectedProductName
    if (-not [string]::IsNullOrWhiteSpace($expectedProduct)) {
        $actualProduct = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductName
        if (-not [string]::Equals($actualProduct, $expectedProduct, [StringComparison]::Ordinal)) {
            throw "Dependency product-name mismatch for $Path. Expected '$expectedProduct', found '$actualProduct'."
        }
    }

    [pscustomobject]@{
        Length = $item.Length
        Sha256 = $actualHash
        Authenticode = $signature.Status.ToString()
    }
}

function Promote-ValidatedFileSetAtomically {
    <#
    .SYNOPSIS
    Promotes an already validated group of files with rollback if any replacement fails.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object[]]$Files)

    if ($Files.Count -eq 0) {
        throw "At least one validated file is required for promotion."
    }
    $operationId = [Guid]::NewGuid().ToString('N')
    $prepared = [Collections.Generic.List[object]]::new()
    $destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($mapping in $Files) {
            $source = [IO.Path]::GetFullPath([string]$mapping.Source)
            $destination = [IO.Path]::GetFullPath([string]$mapping.Destination)
            if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
                (Get-Item -LiteralPath $source -Force).Length -le 0) {
                throw "Validated promotion source is missing or empty: $source"
            }
            if (-not $destinations.Add($destination)) {
                throw "Validated file set contains duplicate destination: $destination"
            }
            $parent = Split-Path -Parent $destination
            Assert-NoReparsePointInExistingPath -Path $parent
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            Assert-NoReparsePointInExistingPath -Path $parent
            $leaf = Split-Path -Leaf $destination
            $temporary = Join-Path $parent (".$leaf.promote-$operationId")
            $backup = Join-Path $parent (".$leaf.backup-$operationId")
            Copy-Item -LiteralPath $source -Destination $temporary
            $prepared.Add([pscustomobject]@{
                Destination = $destination
                Temporary = $temporary
                Backup = $backup
                HadExisting = $false
                Promoted = $false
            })
        }

        foreach ($item in $prepared) {
            if (Test-Path -LiteralPath $item.Destination -PathType Leaf) {
                $existing = Get-Item -LiteralPath $item.Destination -Force
                if (($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Refusing to replace release output through a symbolic link: $($item.Destination)"
                }
                [IO.File]::Move($item.Destination, $item.Backup)
                $item.HadExisting = $true
            }
        }
        foreach ($item in $prepared) {
            [IO.File]::Move($item.Temporary, $item.Destination)
            $item.Promoted = $true
        }
    } catch {
        $failure = $_
        foreach ($item in @($prepared | Sort-Object Destination -Descending)) {
            if ($item.Promoted -and (Test-Path -LiteralPath $item.Destination -PathType Leaf)) {
                [IO.File]::Delete($item.Destination)
            }
            if ($item.HadExisting -and (Test-Path -LiteralPath $item.Backup -PathType Leaf)) {
                [IO.File]::Move($item.Backup, $item.Destination)
            }
        }
        throw "Validated file-set promotion failed; previous outputs were restored when possible. $($failure.Exception.Message)"
    } finally {
        foreach ($item in $prepared) {
            if (Test-Path -LiteralPath $item.Temporary -PathType Leaf) {
                [IO.File]::Delete($item.Temporary)
            }
        }
    }

    foreach ($item in $prepared) {
        if (Test-Path -LiteralPath $item.Backup -PathType Leaf) {
            [IO.File]::Delete($item.Backup)
        }
    }
}

function Invoke-IExpress {
    <#
    .SYNOPSIS
    Runs Windows `iexpress.exe /N /Q` against the supplied SED path.

    .PARAMETER SedPath
    Path to the SED (self-extracting definition) file to compile.

    .PARAMETER WorkingDirectory
    Directory that iexpress uses to resolve relative source files.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$SedPath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory)

    $iexpress = Get-Command "iexpress.exe" -ErrorAction SilentlyContinue
    if ($null -eq $iexpress) {
        throw "IExpress was not found. This packaging step requires Windows iexpress.exe."
    }

    $process = Start-Process `
        -FilePath $iexpress.Source `
        -WorkingDirectory $WorkingDirectory `
        -ArgumentList @("/N", "/Q", (Split-Path -Leaf $SedPath)) `
        -Wait `
        -PassThru `
        -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "IExpress failed with exit code $($process.ExitCode)."
    }
}
