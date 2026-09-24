function Find-SignTool {
    [CmdletBinding()]
    param()

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Filter 'signtool.exe' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    throw 'Authenticode signing is configured, but signtool.exe was not found.'
}

function Get-AuthenticodeSigningConfiguration {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$CertificateThumbprint = '',
        [AllowEmptyString()][string]$TimestampUrl = '')

    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $CertificateThumbprint = [string]$env:SVS_AUTHENTICODE_CERTIFICATE_THUMBPRINT
    }
    if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $TimestampUrl = [string]$env:SVS_AUTHENTICODE_TIMESTAMP_URL
    }

    $hasThumbprint = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
    $hasTimestamp = -not [string]::IsNullOrWhiteSpace($TimestampUrl)
    if (-not $hasThumbprint -and -not $hasTimestamp) {
        return [pscustomobject]@{
            Enabled = $false
            CertificateThumbprint = ''
            TimestampUrl = ''
            SignTool = ''
        }
    }
    if (-not $hasThumbprint -or -not $hasTimestamp) {
        throw 'Authenticode configuration is partial. Configure both SVS_AUTHENTICODE_CERTIFICATE_THUMBPRINT and SVS_AUTHENTICODE_TIMESTAMP_URL, or neither.'
    }

    $thumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
        throw "Authenticode certificate thumbprint is not a SHA-1 thumbprint: '$CertificateThumbprint'."
    }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        -not [string]::Equals($timestampUri.Scheme, [Uri]::UriSchemeHttps, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Authenticode RFC3161 timestamp URL must be absolute HTTPS: '$TimestampUrl'."
    }

    $certificatePath = "Cert:\CurrentUser\My\$thumbprint"
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
        throw "Authenticode certificate is not installed in CurrentUser\\My: $thumbprint"
    }
    $certificate = Get-Item -LiteralPath $certificatePath
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        throw "Authenticode certificate is expired or has no private key: $thumbprint"
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    if (@($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq $codeSigningOid }).Count -eq 0) {
        throw "Authenticode certificate does not permit code signing: $thumbprint"
    }

    [pscustomobject]@{
        Enabled = $true
        CertificateThumbprint = $thumbprint
        TimestampUrl = $timestampUri.AbsoluteUri
        SignTool = Find-SignTool
    }
}

function Assert-AuthenticodeSignature {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Configuration)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Authenticode output is missing: $fullPath"
    }
    if (-not [bool]$Configuration.Enabled) {
        throw 'Authenticode verification was requested without an enabled signing configuration.'
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $fullPath
    if ([string]$signature.Status -cne 'Valid' -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -cne [string]$Configuration.CertificateThumbprint -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "Authenticode verification failed for $fullPath. Status: $($signature.Status); signer: $($signature.SignerCertificate.Thumbprint); timestamp present: $($null -ne $signature.TimeStamperCertificate)."
    }

    & $Configuration.SignTool verify /pa /all /v $fullPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verification failed for $fullPath with exit code $LASTEXITCODE."
    }
    $signature
}

function Invoke-AuthenticodeSigning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$Path,
        [Parameter(Mandatory = $true)]$Configuration)

    if (-not [bool]$Configuration.Enabled) {
        return
    }
    foreach ($item in $Path) {
        $fullPath = [IO.Path]::GetFullPath($item)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Authenticode input is missing: $fullPath"
        }
        $existing = Get-AuthenticodeSignature -LiteralPath $fullPath
        if ($existing.Status -eq 'Valid' -and
            $null -ne $existing.SignerCertificate -and
            $existing.SignerCertificate.Thumbprint -ceq [string]$Configuration.CertificateThumbprint -and
            $null -ne $existing.TimeStamperCertificate) {
            Assert-AuthenticodeSignature -Path $fullPath -Configuration $Configuration | Out-Null
            continue
        }
        & $Configuration.SignTool sign `
            /sha1 $Configuration.CertificateThumbprint `
            /s My `
            /fd SHA256 `
            /tr $Configuration.TimestampUrl `
            /td SHA256 `
            /v `
            $fullPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode signing failed for $fullPath with exit code $LASTEXITCODE."
        }
        Assert-AuthenticodeSignature -Path $fullPath -Configuration $Configuration | Out-Null
    }
}
