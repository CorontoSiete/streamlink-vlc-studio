function Read-NativeOverlayManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Native dependency manifest missing: $fullPath"
    }

    $manifest = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.component -ne 'bundled-vlc-chat-overlay' -or
        @($manifest.files).Count -eq 0) {
        throw "Unsupported or empty native dependency manifest: $fullPath"
    }
    if ($manifest.provenance.classification -ne 'opaque-third-party-input' -or
        [string]::IsNullOrWhiteSpace([string]$manifest.provenance.sourceAvailability) -or
        [string]::IsNullOrWhiteSpace([string]$manifest.provenance.notes)) {
        throw "Native dependency provenance metadata is incomplete: $fullPath"
    }

    $reviewedUtc = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$manifest.provenance.reviewedUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$reviewedUtc)) {
        throw "Native dependency provenance review timestamp is invalid: $fullPath"
    }

    $manifest
}

function Get-NativeOverlayRelativeManifestPath {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    $normalized = $ManifestPath.Replace('\', '/')
    $marker = '/BundledOverlay/'
    $index = $normalized.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase)
    if ($index -lt 0) {
        throw "Native dependency path is not rooted beneath BundledOverlay: $ManifestPath"
    }

    $relative = $normalized.Substring($index + $marker.Length)
    if ([string]::IsNullOrWhiteSpace($relative) -or
        [IO.Path]::IsPathRooted($relative) -or
        $relative -match '(^|/)\.\.(/|$)') {
        throw "Unsafe native dependency path in manifest: '$ManifestPath'"
    }
    $relative
}

function Get-NativeOverlayFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        } finally {
            $algorithm.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Assert-NativeOverlaySource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$OverlaySource,
        [Parameter(Mandatory = $true)][string]$ManifestPath)

    $sourceRoot = [IO.Path]::GetFullPath($OverlaySource)
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Native overlay source directory missing: $sourceRoot"
    }
    Assert-NoReparsePointInExistingPath -Path $sourceRoot

    $sourceItem = Get-Item -LiteralPath $sourceRoot -Force
    if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Native overlay source cannot be a symbolic link or junction: $sourceRoot"
    }

    $manifest = Read-NativeOverlayManifest $ManifestPath
    $expected = @{}
    foreach ($entry in @($manifest.files)) {
        $relative = Get-NativeOverlayRelativeManifestPath ([string]$entry.path)
        if ($expected.ContainsKey($relative)) {
            throw "Duplicate native dependency path in manifest: $relative"
        }
        if ([int64]$entry.length -le 0 -or
            ([string]$entry.sha256).Trim() -notmatch '^[0-9a-fA-F]{64}$' -or
            [string]$entry.authenticode -notin @('Valid', 'NotSigned')) {
            throw "Invalid native dependency integrity metadata for $relative."
        }
        $expected[$relative] = $entry
    }

    $actual = @{}
    foreach ($item in @(Get-ChildItem -LiteralPath $sourceRoot -Force -Recurse)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Native overlay source contains a symbolic link or junction: $($item.FullName)"
        }
        if ($item.PSIsContainer) {
            continue
        }

        $relative = $item.FullName.Substring($sourceRoot.Length).TrimStart('\', '/').Replace('\', '/')
        if ($actual.ContainsKey($relative)) {
            throw "Native overlay source contains duplicate canonical path: $relative"
        }
        $actual[$relative] = $item
    }

    $missing = @($expected.Keys | Where-Object { -not $actual.ContainsKey($_) } | Sort-Object)
    $unlisted = @($actual.Keys | Where-Object { -not $expected.ContainsKey($_) } | Sort-Object)
    if ($missing.Count -gt 0 -or $unlisted.Count -gt 0) {
        throw "Native overlay manifest/file set mismatch. Missing: $($missing -join ', '); unlisted: $($unlisted -join ', ')"
    }

    $verified = [Collections.Generic.List[object]]::new()
    foreach ($relative in @($expected.Keys | Sort-Object)) {
        $entry = $expected[$relative]
        $file = $actual[$relative]
        if ($file.Length -ne [int64]$entry.length) {
            throw "Native dependency length mismatch for $relative. Expected $($entry.length), found $($file.Length)."
        }

        $actualHash = Get-NativeOverlayFileSha256 $file.FullName
        $expectedHash = ([string]$entry.sha256).Trim().ToLowerInvariant()
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::Ordinal)) {
            throw "Native dependency SHA-256 mismatch for $relative. Expected $expectedHash, found $actualHash."
        }

        if ($IsWindows -or $env:OS -eq 'Windows_NT') {
            $signatureStatus = (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status.ToString()
            if (-not [string]::Equals($signatureStatus, [string]$entry.authenticode, [StringComparison]::Ordinal)) {
                throw "Native dependency signature status mismatch for $relative. Expected $($entry.authenticode), found $signatureStatus."
            }
        }

        $verified.Add([pscustomobject]@{
            RelativePath = $relative
            SourcePath = $file.FullName
            Length = $file.Length
            Sha256 = $actualHash
            Authenticode = [string]$entry.authenticode
        })
    }

    $verified.ToArray()
}

function Copy-VerifiedNativeOverlay {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$VerifiedFiles,
        [Parameter(Mandatory = $true)][string]$Destination)

    $destinationRoot = [IO.Path]::GetFullPath($Destination)
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    foreach ($file in $VerifiedFiles) {
        $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot ([string]$file.RelativePath)))
        if (-not (Test-PathIsSameOrUnderDirectory -ChildPath $target -ParentPath $destinationRoot)) {
            throw "Native overlay destination escapes its staging root: $($file.RelativePath)"
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath ([string]$file.SourcePath) -Destination $target -Force
    }
}
