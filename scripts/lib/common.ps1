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
