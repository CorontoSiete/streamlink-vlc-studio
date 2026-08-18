function Test-SafeContractRelativePath {
    param([AllowNull()][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        [IO.Path]::IsPathRooted($Path)) {
        return $false
    }

    $normalized = $Path.Replace('\', '/')
    if ([IO.Path]::IsPathRooted($normalized)) {
        return $false
    }
    foreach ($segment in @($normalized -split '/')) {
        if (-not (Test-SafeWindowsPathSegment $segment)) {
            return $false
        }
    }

    $true
}

function Read-ReleaseContract {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Release contract missing: $fullPath"
    }
    $contract = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ($contract.schemaVersion -ne 1 -or
        $null -eq $contract.payload -or
        $null -eq $contract.outputs -or
        @($contract.releaseSet).Count -eq 0) {
        throw "Unsupported or incomplete release contract: $fullPath"
    }

    $required = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in @($contract.payload.requiredFiles)) {
        $value = ([string]$relative).Replace('\', '/')
        if (-not (Test-SafeContractRelativePath $value) -or -not $required.Add($value)) {
            throw "Release contract contains an unsafe or duplicate payload path: '$relative'."
        }
    }
    if (-not $required.Contains(([string]$contract.payload.executable).Replace('\', '/'))) {
        throw "Release contract payload does not require its executable."
    }

    $browserRuntime = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in @($contract.payload.browserExtensionRuntime)) {
        $value = ([string]$relative).Replace('\', '/')
        if (-not (Test-SafeContractRelativePath $value) -or -not $browserRuntime.Add($value) -or
            -not $required.Contains("browser-extension/$value")) {
            throw "Release contract contains an invalid browser extension runtime path: '$relative'."
        }
    }

    $assetNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $outputKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in @($contract.releaseSet)) {
        $name = [string]$asset.name
        $key = [string]$asset.output
        $outputProperty = $contract.outputs.PSObject.Properties[$key]
        if (-not (Test-SafeWindowsPathSegment $name) -or
            -not $assetNames.Add($name) -or
            $asset.checksummed -isnot [bool] -or
            [string]::IsNullOrWhiteSpace($key) -or
            $null -eq $outputProperty -or
            -not $outputKeys.Add($key) -or
            -not (Test-SafeContractRelativePath ([string]$outputProperty.Value)) -or
            -not [string]::Equals([IO.Path]::GetFileName([string]$outputProperty.Value), $name, [StringComparison]::Ordinal)) {
            throw "Release contract contains an invalid or duplicate release-set entry: '$name'."
        }
    }
    if (@($contract.releaseSet | Where-Object { -not $_.checksummed }).Count -ne 1) {
        throw "Release contract must contain exactly one non-checksummed manifest asset."
    }

    $contract
}

function Get-ReleaseContractOutputPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Contract,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Key)

    $property = $Contract.outputs.PSObject.Properties[$Key]
    if ($null -eq $property) {
        throw "Release contract output key is unknown: $Key"
    }
    [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ([string]$property.Value)))
}

function Resolve-ReleasePayloadRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ExtractedRoot,
        [Parameter(Mandatory = $true)]$Contract,
        [switch]$AllowNone)

    $root = [IO.Path]::GetFullPath($ExtractedRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Extracted release root is missing: $root"
    }
    Assert-NoReparsePointInExistingPath -Path $root
    $executableName = [string]$Contract.payload.executable
    $matches = @(Get-ChildItem -LiteralPath $root -Force -Recurse -File -Filter $executableName)
    if ($matches.Count -eq 0) {
        if ($AllowNone) { return $null }
        throw "Release payload does not contain $executableName."
    }
    if ($matches.Count -ne 1) {
        throw "Release payload must contain exactly one $executableName; found $($matches.Count)."
    }

    $payloadRoot = [IO.Path]::GetFullPath((Split-Path -Parent $matches[0].FullName))
    Assert-ReleasePayload -PayloadRoot $payloadRoot -Contract $Contract
    $payloadRoot
}

function Assert-ReleasePayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PayloadRoot,
        [Parameter(Mandatory = $true)]$Contract)

    $root = [IO.Path]::GetFullPath($PayloadRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Release payload directory is missing: $root"
    }
    Assert-NoReparsePointInExistingPath -Path $root
    $rootWithSeparator = $root.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    foreach ($relative in @($Contract.payload.requiredFiles)) {
        $path = [IO.Path]::GetFullPath((Join-Path $root ([string]$relative)))
        if (-not $path.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release payload is missing required file: $relative"
        }
    }

    $reparse = @(Get-ChildItem -LiteralPath $root -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($reparse.Count -gt 0) {
        throw "Release payload contains a symbolic link or junction: $($reparse[0].FullName)"
    }
}

function Get-ReleaseSetFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Contract)

    $rootFull = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) {
        throw "Release-set directory is missing: $rootFull"
    }
    Assert-NoReparsePointInExistingPath -Path $rootFull
    $allItems = @(Get-ChildItem -LiteralPath $rootFull -Force)
    $reparse = @($allItems | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    })
    if ($reparse.Count -gt 0) {
        throw "Release set contains a symbolic link or reparse point: $($reparse[0].FullName)"
    }
    $directories = @($allItems | Where-Object { $_.PSIsContainer })
    if ($directories.Count -gt 0) {
        throw "Unexpected directories in closed release set: $($directories.FullName -join ', ')"
    }
    $allFiles = @($allItems | Where-Object { -not $_.PSIsContainer })
    $expectedNames = @($Contract.releaseSet | ForEach-Object { [string]$_.name })
    $result = [Collections.Generic.List[object]]::new()
    foreach ($asset in @($Contract.releaseSet)) {
        $matches = @($allFiles | Where-Object { $_.Name -ceq [string]$asset.name })
        if ($matches.Count -ne 1) {
            throw "Release set must contain exactly one $($asset.name); found $($matches.Count)."
        }
        if ($matches[0].Length -le 0) {
            throw "Release asset is empty: $($matches[0].FullName)"
        }
        $result.Add([pscustomobject]@{ Entry = $asset; File = $matches[0] })
    }

    $unexpected = @($allFiles | Where-Object { $_.Name -cnotin $expectedNames })
    if ($unexpected.Count -gt 0) {
        throw "Unexpected files in closed release set: $($unexpected.FullName -join ', ')"
    }
    $result.ToArray()
}

function Get-MsiProductVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][long]$RunNumber,
        [Parameter(Mandatory = $true)][long]$RunAttempt)

    if ($RunNumber -lt 0 -or $RunAttempt -lt 1 -or $RunAttempt -gt 99) {
        throw "Unsupported workflow run number/attempt for MSI versioning: $RunNumber/$RunAttempt"
    }
    if ($RunNumber -gt [Math]::Floor(([long]::MaxValue - $RunAttempt) / 100)) {
        throw "Workflow run number exceeds the supported build ordinal range: $RunNumber"
    }
    $ordinal = $RunNumber * 100 + $RunAttempt
    $major = 1 + [int64][Math]::Floor($ordinal / 65536)
    if ($major -gt 255) {
        throw "Workflow run number exceeds MSI ProductVersion capacity: $RunNumber"
    }
    [pscustomobject]@{
        Ordinal = $ordinal
        ProductVersion = '{0}.{1}.{2}' -f $major, ([int64][Math]::Floor(($ordinal % 65536) / 256)), ($ordinal % 256)
    }
}

function Assert-ReleaseChecksums {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$ReleaseSetFiles,
        [Parameter(Mandatory = $true)][string]$ChecksumPath)

    $covered = @($ReleaseSetFiles | Where-Object { [bool]$_.Entry.checksummed })
    $expectedNames = @($covered | ForEach-Object { [string]$_.Entry.name })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in @(Get-Content -LiteralPath $ChecksumPath)) {
        if ($line -notmatch '^(?<hash>[0-9a-f]{64}) \*(?<name>[^\\/]+)$' -or
            $Matches.name -cnotin $expectedNames -or
            -not $seen.Add($Matches.name)) {
            throw "Malformed, unexpected, or duplicate checksum line: $line"
        }
        $asset = @($covered | Where-Object { $_.Entry.name -ceq $Matches.name })
        if ($asset.Count -ne 1 -or
            (Get-FileHash -LiteralPath $asset[0].File.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Matches.hash) {
            throw "Release checksum mismatch: $($Matches.name)"
        }
    }
    if ($seen.Count -ne $expectedNames.Count) {
        throw 'Release checksum manifest does not cover the complete release set.'
    }
}

function New-VerifiedReleaseSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Contract,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Destination)

    $repository = [IO.Path]::GetFullPath($RepositoryRoot)
    $destinationRoot = [IO.Path]::GetFullPath($Destination)
    $parent = Split-Path -Parent $destinationRoot
    if ([string]::IsNullOrWhiteSpace($parent) -or
        [string]::Equals($parent, $destinationRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release-set destination must be a dedicated child directory: $destinationRoot"
    }
    Assert-NoReparsePointInExistingPath -Path $parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Assert-NoReparsePointInExistingPath -Path $parent

    $operationId = [Guid]::NewGuid().ToString('N')
    $leaf = Split-Path -Leaf $destinationRoot
    $stage = Join-Path $parent (".$leaf.stage-$operationId")
    $backup = Join-Path $parent (".$leaf.backup-$operationId")
    $promoted = $false
    $movedExisting = $false
    try {
        New-Item -ItemType Directory -Path $stage | Out-Null
        $checksumLines = [Collections.Generic.List[string]]::new()
        foreach ($entry in @($Contract.releaseSet | Where-Object { [bool]$_.checksummed })) {
            $source = Get-ReleaseContractOutputPath `
                -Contract $Contract `
                -RepositoryRoot $repository `
                -Key ([string]$entry.output)
            if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -le 0) {
                throw "Release input is missing or empty: $source"
            }
            $target = Join-Path $stage ([string]$entry.name)
            Copy-Item -LiteralPath $source -Destination $target
            $checksumLines.Add(('{0} *{1}' -f
                (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant(),
                [string]$entry.name))
        }

        $checksumEntry = @($Contract.releaseSet | Where-Object { -not [bool]$_.checksummed })
        if ($checksumEntry.Count -ne 1) {
            throw "Release contract must contain exactly one non-checksummed manifest asset."
        }
        $checksumPath = Join-Path $stage ([string]$checksumEntry[0].name)
        [IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

        $stagedSet = @(Get-ReleaseSetFiles -Root $stage -Contract $Contract)
        Assert-ReleaseChecksums -ReleaseSetFiles $stagedSet -ChecksumPath $checksumPath

        if (Test-Path -LiteralPath $destinationRoot -PathType Container) {
            [IO.Directory]::Move($destinationRoot, $backup)
            $movedExisting = $true
        }
        [IO.Directory]::Move($stage, $destinationRoot)
        $promoted = $true
    } catch {
        $failure = $_
        if ($movedExisting -and -not (Test-Path -LiteralPath $destinationRoot) -and
            (Test-Path -LiteralPath $backup -PathType Container)) {
            [IO.Directory]::Move($backup, $destinationRoot)
            $movedExisting = $false
        }
        throw "Atomic release-set promotion failed; the previous set was restored when possible. $($failure.Exception.Message)"
    } finally {
        if (Test-Path -LiteralPath $stage -PathType Container) {
            Remove-DirectoryTreeSafely $stage
        }
    }

    if ($promoted -and (Test-Path -LiteralPath $backup -PathType Container)) {
        Remove-DirectoryTreeSafely $backup
    }
    @(Get-ReleaseSetFiles -Root $destinationRoot -Contract $Contract)
}

function Test-VerifiedReleaseSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Contract,
        [Parameter(Mandatory = $true)][string]$Root)

    $files = @(Get-ReleaseSetFiles -Root $Root -Contract $Contract)
    $checksum = @($files | Where-Object { -not [bool]$_.Entry.checksummed })
    if ($checksum.Count -ne 1) {
        throw "Release set does not contain exactly one checksum manifest."
    }
    Assert-ReleaseChecksums -ReleaseSetFiles $files -ChecksumPath $checksum[0].File.FullName
    $files
}
