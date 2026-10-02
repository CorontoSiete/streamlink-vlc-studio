function Read-WindowsDependencyManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string[]]$RequiredDependencies = @('streamlink', 'vlc', 'webview2'))

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

    foreach ($name in $RequiredDependencies) {
        if ($null -eq $manifest.dependencies.PSObject.Properties[$name]) {
            throw "Windows dependency manifest omits required dependency '$name': $fullPath"
        }
    }

    foreach ($property in $dependencies) {
        if ($property.Name -cnotin @('streamlink', 'vlc', 'webview2')) {
            throw "Windows dependency '$($property.Name)' is not supported by this installer."
        }
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
        $minimum = Get-DependencyMinimumVersion $entry
        $pinned = ConvertTo-DependencyVersion ([string]$entry.version)
        if ($null -eq $pinned -or $pinned -le [version]'0.0.0.0' -or
            (ConvertTo-DependencyVersion $minimum) -le [version]'0.0.0.0' -or
            (ConvertTo-DependencyVersion $minimum) -gt $pinned) {
            throw "Windows dependency '$($property.Name)' has an invalid version or minimumVersion."
        }
        $downloadUri = $null
        if (-not [Uri]::TryCreate([string]$entry.url, [UriKind]::Absolute, [ref]$downloadUri) -or
            $downloadUri.Scheme -cne 'https' -or -not [string]::IsNullOrEmpty($downloadUri.UserInfo) -or
            [string]$entry.fileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.exe$' -or
            [string]$entry.authenticode -cnotin @('Valid', 'NotSigned')) {
            throw "Windows dependency '$($property.Name)' has an unsafe download URL, file name, or Authenticode policy."
        }
        if ([string]$entry.authenticode -ceq 'Valid' -and
            ([string]::IsNullOrWhiteSpace([string]$entry.expectedPublisher) -or
             ([string]$entry.expectedSignerThumbprint) -notmatch '^[0-9a-fA-F]{40}$')) {
            throw "Windows dependency '$($property.Name)' omits its expected signer."
        }
    }

    $manifest
}

function Assert-DependencyManifestsMatch {
    param(
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)]$Actual)

    $minimums = [ordered]@{}
    foreach ($entry in $Expected.dependencies.PSObject.Properties) {
        $minimums[$entry.Name] = Get-DependencyMinimumVersion $entry.Value
    }
    Assert-DependencyMinimums ([pscustomobject]$minimums) $Actual
    foreach ($entry in $Expected.dependencies.PSObject.Properties) {
        $other = $Actual.dependencies.PSObject.Properties[$entry.Name].Value
        foreach ($field in @('version', 'fileName', 'url', 'length', 'sha256', 'authenticode',
                'expectedPublisher', 'expectedSignerThumbprint', 'expectedProductName', 'sdkVersion')) {
            $expectedField = $entry.Value.PSObject.Properties[$field]
            $actualField = $other.PSObject.Properties[$field]
            $expectedValue = if ($null -eq $expectedField) { '' } else { [string]$expectedField.Value }
            $actualValue = if ($null -eq $actualField) { '' } else { [string]$actualField.Value }
            if ($expectedValue -cne $actualValue) {
                throw "The release payload's '$($entry.Name)' $field does not match the installer dependency manifest."
            }
        }
    }
}

function Assert-DependencyMinimums {
    param(
        [Parameter(Mandatory = $true)]$SignedMinimums,
        [Parameter(Mandatory = $true)]$DependencyManifest)

    $entries = @($DependencyManifest.dependencies.PSObject.Properties)
    $minimums = @($SignedMinimums.PSObject.Properties)
    if ($entries.Count -ne $minimums.Count) {
        throw 'Signed dependency minimums do not describe every dependency in the release payload.'
    }
    foreach ($entry in $entries) {
        $signed = $SignedMinimums.PSObject.Properties[$entry.Name]
        if ($null -eq $signed -or [string]$signed.Value -cne (Get-DependencyMinimumVersion $entry.Value)) {
            throw "Signed dependency minimum for '$($entry.Name)' does not match the release payload."
        }
    }
}

function Get-DependencyMinimumVersion {
    param([Parameter(Mandatory = $true)]$Dependency)

    $property = $Dependency.PSObject.Properties['minimumVersion']
    $minimum = if ($null -eq $property) { [string]$Dependency.version } else { [string]$property.Value }
    $parsed = ConvertTo-DependencyVersion $minimum
    if ($minimum -notmatch '^\d+(?:\.\d+){1,3}(?:-\d+)?$' -or
        $null -eq $parsed -or $parsed -le [version]'0.0.0.0') {
        throw "Windows dependency has an invalid minimum version: '$minimum'."
    }
    $minimum
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
    if ($normalized -notmatch '^(?<version>\d+(?:\.\d+){1,3})(?:-\d+)?$') {
        return $null
    }

    try {
        $version = [version]$Matches.version
        [version]::new($version.Major, $version.Minor, [Math]::Max(0, $version.Build), [Math]::Max(0, $version.Revision))
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
