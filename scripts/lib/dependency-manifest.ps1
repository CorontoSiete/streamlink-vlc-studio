function Read-WindowsDependencyManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Windows dependency manifest missing: $fullPath"
    }

    $manifest = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $null -eq $manifest.dependencies) {
        throw "Unsupported Windows dependency manifest: $fullPath"
    }

    $dependencies = @($manifest.dependencies.PSObject.Properties)
    if ($dependencies.Count -eq 0) {
        throw "Windows dependency manifest is empty: $fullPath"
    }

    foreach ($property in $dependencies) {
        $entry = $property.Value
        if ([string]::IsNullOrWhiteSpace([string]$entry.version) -or
            [string]::IsNullOrWhiteSpace([string]$entry.fileName) -or
            [string]::IsNullOrWhiteSpace([string]$entry.url) -or
            [int64]$entry.length -le 0 -or
            ([string]$entry.sha256).Trim() -notmatch '^[0-9a-fA-F]{64}$' -or
            [string]::IsNullOrWhiteSpace([string]$entry.authenticode)) {
            throw "Windows dependency '$($property.Name)' has an incomplete or invalid manifest entry."
        }

        if ($null -ne $entry.PSObject.Properties['expectedLength']) {
            throw "Windows dependency '$($property.Name)' uses obsolete 'expectedLength'; use canonical 'length'."
        }
    }

    $manifest
}

function ConvertTo-DependencyVersion {
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $normalized = $Value.Trim()
    if ($normalized.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }
    if ($normalized -notmatch '^(?<version>\d+(?:\.\d+){1,3})') {
        return $null
    }

    try {
        [version]$Matches.version
    } catch {
        $null
    }
}

function Select-CompatibleDependencyCandidate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$CandidatePaths,
        [Parameter(Mandatory = $true)][string]$MinimumVersion,
        [Parameter(Mandatory = $true)][scriptblock]$VersionReader,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$AllowNone)

    $minimum = ConvertTo-DependencyVersion $MinimumVersion
    if ($null -eq $minimum) {
        throw "Pinned $Description version is invalid: '$MinimumVersion'."
    }

    $diagnostics = [Collections.Generic.List[string]]::new()
    $compatible = [Collections.Generic.List[object]]::new()
    foreach ($rawPath in @($CandidatePaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $path = [IO.Path]::GetFullPath($rawPath)
        if (-not (Test-Path -LiteralPath $path)) {
            $diagnostics.Add("$path (missing)")
            continue
        }

        $reported = ''
        try {
            $reported = [string](& $VersionReader $path)
        } catch {
            $diagnostics.Add("$path (version probe failed: $($_.Exception.Message))")
            continue
        }

        $parsed = ConvertTo-DependencyVersion $reported
        if ($null -eq $parsed) {
            $diagnostics.Add("$path (unparseable version '$reported')")
            continue
        }
        if ($parsed -lt $minimum) {
            $diagnostics.Add("$path (version $reported is below $MinimumVersion)")
            continue
        }

        $compatible.Add([pscustomobject]@{
            Path = $path
            ReportedVersion = $reported
            ParsedVersion = $parsed
        })
    }

    $selected = $compatible |
        Sort-Object -Property @{ Expression = 'ParsedVersion'; Descending = $true }, @{ Expression = 'Path'; Descending = $false } |
        Select-Object -First 1
    if ($null -ne $selected) {
        return $selected
    }

    if ($AllowNone) {
        return $null
    }

    $detail = if ($diagnostics.Count -eq 0) { 'no candidates were discovered' } else { $diagnostics -join '; ' }
    throw "No compatible $Description installation was found (minimum $MinimumVersion): $detail."
}
