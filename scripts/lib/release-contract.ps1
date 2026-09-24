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

function ConvertTo-StableReleaseVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Version)

    if ($Version -notmatch '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)$') {
        throw "Release version must be exactly MAJOR.MINOR.PATCH with no prerelease, metadata, or leading zeroes: '$Version'."
    }

    $parts = [Collections.Generic.List[int]]::new()
    foreach ($name in @('major', 'minor', 'patch')) {
        [int]$part = 0
        if (-not [int]::TryParse(
                $Matches[$name],
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$part) -or
            $part -gt 255) {
            throw "Release version component '$name' must fit the MSI range 0 through 255: '$Version'."
        }
        $parts.Add($part)
    }

    [Version]::new($parts[0], $parts[1], $parts[2])
}

function Get-StableReleaseIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [string]$MinimumVersion = '1.7.0')

    if ($Tag -notmatch '^v(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))$') {
        throw "Stable releases require an exact vMAJOR.MINOR.PATCH tag: '$Tag'."
    }
    $versionText = $Matches.version
    $version = ConvertTo-StableReleaseVersion $versionText
    $minimum = ConvertTo-StableReleaseVersion $MinimumVersion
    if ($version -lt $minimum) {
        throw "Release tag $Tag is below the supported release floor v$MinimumVersion."
    }

    [pscustomobject]@{
        Tag = $Tag
        Version = $version
        VersionText = $versionText
        FourPartVersion = "$versionText.0"
    }
}

function Get-PemSubjectPublicKeyInfoBytes {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Release public key is missing: $fullPath"
    }
    $pem = [IO.File]::ReadAllText($fullPath)
    if ($pem -notmatch '(?s)^\s*-----BEGIN PUBLIC KEY-----\s*(?<body>[A-Za-z0-9+/=\r\n]+?)\s*-----END PUBLIC KEY-----\s*$') {
        throw "Release public key must contain exactly one PEM SubjectPublicKeyInfo block: $fullPath"
    }
    try {
        $bytes = [Convert]::FromBase64String(($Matches.body -replace '\s', ''))
    } catch {
        throw "Release public key contains invalid base64: $fullPath"
    }
    if ($bytes.Length -lt 256) {
        throw "Release public key SubjectPublicKeyInfo is unexpectedly small: $fullPath"
    }
    $bytes
}

function Get-ReleasePublicKeyId {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($algorithm.ComputeHash((Get-PemSubjectPublicKeyInfoBytes $Path)))).Replace('-', '').ToLowerInvariant()
    } finally {
        $algorithm.Dispose()
    }
}

function Get-MsiPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')][string]$Property)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "MSI database is missing: $fullPath"
    }
    $installer = $null
    $database = $null
    $view = $null
    $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase',
            [Reflection.BindingFlags]::InvokeMethod,
            $null,
            $installer,
            @($fullPath, 0))
        $query = "SELECT `Value` FROM `Property` WHERE `Property`='$Property'"
        $view = $database.GetType().InvokeMember(
            'OpenView',
            [Reflection.BindingFlags]::InvokeMethod,
            $null,
            $database,
            @($query))
        $view.GetType().InvokeMember('Execute', [Reflection.BindingFlags]::InvokeMethod, $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', [Reflection.BindingFlags]::InvokeMethod, $null, $view, $null)
        if ($null -eq $record) {
            throw "MSI property is missing: $Property"
        }
        [string]$record.GetType().InvokeMember(
            'StringData',
            [Reflection.BindingFlags]::GetProperty,
            $null,
            $record,
            @(1))
    } finally {
        foreach ($comObject in @($record, $view, $database, $installer)) {
            if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject)
            }
        }
    }
}

function Read-ReleaseContract {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Release contract missing: $fullPath"
    }
    $contract = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ($contract.schemaVersion -ne 2 -or
        $null -eq $contract.release -or
        $null -eq $contract.release.manifestSignature -or
        $null -eq $contract.payload -or
        $null -eq $contract.outputs -or
        @($contract.releaseSet).Count -eq 0) {
        throw "Unsupported or incomplete release contract: $fullPath"
    }

    $minimumVersion = ConvertTo-StableReleaseVersion ([string]$contract.release.minimumVersion)
    if ($minimumVersion -lt [Version]::new(1, 7, 0) -or
        [string]$contract.release.stableTagPattern -cne '^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$' -or
        [int]$contract.release.updaterProtocolVersion -ne 1 -or
        [string]$contract.release.manifestSignature.algorithm -cne 'RSA-PSS-SHA256' -or
        [int]$contract.release.manifestSignature.keyBits -ne 3072 -or
        [string]$contract.release.manifestSignature.keyId -notmatch '^[0-9a-f]{64}$' -or
        -not (Test-SafeContractRelativePath ([string]$contract.release.manifestSignature.publicKey))) {
        throw "Release contract contains an invalid stable-release or manifest-signing policy: $fullPath"
    }
    $contractDirectory = Split-Path -Parent $fullPath
    $repositoryRoot = if ([string]::Equals(
            (Split-Path -Leaf $contractDirectory),
            'shared',
            [StringComparison]::OrdinalIgnoreCase)) {
        Split-Path -Parent $contractDirectory
    } else {
        $contractDirectory
    }
    $publicKeyPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ([string]$contract.release.manifestSignature.publicKey)))
    $actualKeyId = Get-ReleasePublicKeyId $publicKeyPath
    if ($actualKeyId -cne [string]$contract.release.manifestSignature.keyId) {
        throw "Release public-key ID mismatch. Contract: $($contract.release.manifestSignature.keyId); actual: $actualKeyId."
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

    $requiredReleaseAssets = @(
        'StreamlinkVlcStudio-Setup.exe',
        'StreamlinkVlcStudio-release.zip',
        'UPDATE-MANIFEST.json',
        'UPDATE-MANIFEST.sig',
        'SHA256SUMS.txt',
        'RELEASE-METADATA.json',
        'StreamlinkVlcStudio.spdx.json'
    )
    $actualReleaseAssets = @($contract.releaseSet | ForEach-Object { [string]$_.name })
    if ($actualReleaseAssets.Count -ne $requiredReleaseAssets.Count -or
        @($requiredReleaseAssets | Where-Object { $_ -cnotin $actualReleaseAssets }).Count -gt 0 -or
        @($actualReleaseAssets | Where-Object { [IO.Path]::GetExtension($_) -ieq '.msi' }).Count -gt 0) {
        throw 'Release contract must define the exact seven-asset public release set and must not expose the internal MSI.'
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

function Assert-WindowsFileVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$Version)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Description is missing: $Path" }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($Path))
    $accepted = @($Version, "$Version.0")
    if ([string]$info.FileVersion -cnotin $accepted -or [string]$info.ProductVersion -cnotin $accepted) {
        throw "$Description version mismatch. Expected $Version or $Version.0; FileVersion '$($info.FileVersion)'; ProductVersion '$($info.ProductVersion)'."
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
