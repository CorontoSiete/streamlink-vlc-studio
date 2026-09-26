#requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repoRoot 'scripts\lib\common.ps1')
. (Join-Path $repoRoot 'scripts\lib\release-contract.ps1')
$publisher = Join-Path $repoRoot 'scripts\publish-release.ps1'
$fixtureParent = [IO.Path]::GetTempPath()
$fixtureRoot = Join-Path $fixtureParent ('StreamStudio-publication-tests-' + [Guid]::NewGuid().ToString('N'))
$releaseRoot = Join-Path $fixtureRoot 'release files'
$contractPath = Join-Path $fixtureRoot 'shared\release-contract.json'
$commit = 'a' * 40
$tag = 'v1.7.6'
$repository = 'fixture/stream-studio'
$exitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$hadExitCode = $null -ne $exitCodeVariable
$savedExitCode = if ($hadExitCode) { $exitCodeVariable.Value } else { $null }

function Assert-Publication([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-PublicationFailure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        return
    }
    throw "Expected publication failure matching '$Pattern'."
}

function New-PublicationScenario {
    @{
        Releases = @(); Assets = @(); Calls = [Collections.Generic.List[object]]::new()
        RemoteCommit = $commit; FailApi = $false; FailUpload = $false; FailPublish = $false
        AfterUpload = ''; LaterPage = $false; Notes = ''; AssetReads = 0; ListResponse = ''
    }
}

function Get-FakeOption([object[]]$Arguments, [string]$Name) {
    $index = [Array]::IndexOf($Arguments, $Name)
    if ($index -lt 0) { throw "Missing fake CLI option $Name." }
    $Arguments[$index + 1]
}

# This function shadows the CLI for every scenario, including error paths.
# No GitHub credentials, installed gh executable, or network are used.
function gh {
    $arguments = @($args)
    $publicationScenario.Calls.Add($arguments)
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'api') {
        if ($publicationScenario.FailApi) { $global:LASTEXITCODE = 17; return }
        if ($publicationScenario.ListResponse) { $publicationScenario.ListResponse; return }
        $endpoint = [string]$arguments[1]
        if ($endpoint -like '*/commits/*') {
            @{ sha = $publicationScenario.RemoteCommit } | ConvertTo-Json -Compress
            return
        }
        Assert-Publication ($arguments -contains '--paginate' -and $arguments -contains '--slurp') 'GitHub list did not request every page.'
        if ($endpoint -like '*/assets?*') {
            $publicationScenario.AssetReads++
            $records = @($publicationScenario.Assets)
            if ($publicationScenario.AssetReads -gt 1) {
                switch ($publicationScenario.AfterUpload) {
                    'hash' { $records[0].digest = 'sha256:' + ('0' * 64) }
                    'length' { $records[0].size++ }
                    'state' { $records[0].state = 'starter' }
                    'missing' { $records = @($records | Select-Object -Skip 1) }
                    'digest' { $records[0].digest = $null }
                    'extra' { $records += @{ name = 'internal.msi'; state = 'uploaded'; size = 1; digest = '' } }
                }
            }
        } elseif ($endpoint -like '*/releases?*') {
            $records = @($publicationScenario.Releases)
        } else { throw "Unexpected fake API endpoint: $endpoint" }
        $json = ConvertTo-Json -InputObject $records -Depth 12 -Compress
        if ($publicationScenario.LaterPage) { '[[],' + $json + ']' } else { '[' + $json + ']' }
        return
    }

    Assert-Publication ($arguments[0] -eq 'release') 'Unexpected GitHub command.'
    switch ($arguments[1]) {
        'create' {
            Assert-Publication ($arguments -contains '--draft' -and $arguments -contains '--verify-tag') 'Release was created publicly or without tag verification.'
            $publicationScenario.Notes = [IO.File]::ReadAllText((Get-FakeOption $arguments '--notes-file'))
            $publicationScenario.Releases += [pscustomobject]@{
                id = 42; tag_name = $tag; draft = $true; prerelease = $false
                target_commitish = Get-FakeOption $arguments '--target'; body = $publicationScenario.Notes
            }
        }
        'upload' {
            Assert-Publication ($publicationScenario.Releases[-1].draft) 'Upload changed a published release.'
            Assert-Publication ($arguments -contains '--clobber') 'Interrupted uploads cannot be replaced.'
            $uploadedCount = 0
            foreach ($path in @($arguments | Select-Object -Skip 6)) {
                $file = Get-Item -LiteralPath $path
                $publicationScenario.Assets = @($publicationScenario.Assets | Where-Object { $_.name -cne $file.Name })
                $publicationScenario.Assets += [pscustomobject]@{
                    name = $file.Name; state = 'uploaded'; size = $file.Length; digest = 'sha256:' + (Get-FileSha256 $path)
                }
                $uploadedCount++
                if ($publicationScenario.FailUpload -and $uploadedCount -eq 2) { $global:LASTEXITCODE = 19; return }
            }
            if ($publicationScenario.AfterUpload -eq 'tag') { $publicationScenario.RemoteCommit = 'b' * 40 }
            if ($publicationScenario.AfterUpload -eq 'newer') {
                $publicationScenario.Releases += [pscustomobject]@{ id = 99; tag_name = 'v1.7.7'; draft = $false; prerelease = $false }
            }
        }
        'edit' {
            Assert-Publication ($publicationScenario.AssetReads -ge 2) 'Publication preceded remote asset verification.'
            Assert-Publication ($arguments -contains '--draft=false' -and $arguments -contains '--latest' -and
                $arguments -contains '--prerelease=false' -and $arguments -contains '--verify-tag') 'Final release flags were incomplete.'
            if ($publicationScenario.FailPublish) { $global:LASTEXITCODE = 23; return }
            $publicationScenario.Releases[-1].draft = $false
        }
        default { throw "Unexpected fake release command: $($arguments[1])" }
    }
}

function Invoke-Publication {
    & $publisher -Tag $tag -Commit $commit -Repository $repository -ContractPath $contractPath `
        -ReleaseDirectory $releaseRoot -NotesPath (Join-Path $fixtureRoot 'notes.md')
}

function Write-FixtureChecksums {
    $lines = foreach ($entry in $contract.releaseSet | Where-Object checksummed) {
        (Get-FileSha256 (Join-Path $releaseRoot $entry.name)) + ' *' + $entry.name
    }
    [IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $lines)
}

try {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $contractPath)) | Out-Null
    [IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
    $contract = Get-Content -LiteralPath (Join-Path $repoRoot 'shared\release-contract.json') -Raw | ConvertFrom-Json
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    try {
        $privatePath = Join-Path $fixtureRoot 'fixture-private.pem'
        [IO.File]::WriteAllText($privatePath, $rsa.ExportPkcs8PrivateKeyPem())
        [IO.File]::WriteAllText((Join-Path $fixtureRoot 'shared\update-signing-public-key.pem'), $rsa.ExportSubjectPublicKeyInfoPem())
        $contract.release.manifestSignature.keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    } finally { $rsa.Dispose() }
    [IO.File]::WriteAllText($contractPath, ($contract | ConvertTo-Json -Depth 12))
    foreach ($name in @('StreamlinkVlcStudio-Setup.exe', 'StreamlinkVlcStudio-release.zip', 'StreamStudio.spdx.json')) {
        [IO.File]::WriteAllText((Join-Path $releaseRoot $name), "Fixture $name")
    }
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'notes.md'), 'Install `Setup.exe`; preserve $literal and backticks.')
    $metadata = @{ schemaVersion = 2; version = '1.7.6'; tag = $tag; commit = $commit; repository = $repository }
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'RELEASE-METADATA.json'), ($metadata | ConvertTo-Json))
    & (Join-Path $repoRoot 'scripts\new-update-manifest.ps1') -Version '1.7.6' -Tag $tag -Commit $commit `
        -Repository $repository -ContractPath $contractPath -PrivateKeyPath $privatePath `
        -SetupPath (Join-Path $releaseRoot 'StreamlinkVlcStudio-Setup.exe') `
        -ZipPath (Join-Path $releaseRoot 'StreamlinkVlcStudio-release.zip') -OutputDirectory $releaseRoot | Out-Null
    Write-FixtureChecksums

    $publicationScenario = New-PublicationScenario
    & $publisher -Preflight -Tag $tag -Commit $commit -Repository $repository -ContractPath $contractPath
    Assert-Publication (@($publicationScenario.Calls | Where-Object { $_[0] -ne 'api' }).Count -eq 0) 'Preflight mutated GitHub.'
    Write-Host 'PASS publication: read-only preflight'

    $publicationScenario = New-PublicationScenario
    Invoke-Publication
    Assert-Publication (-not $publicationScenario.Releases[0].draft -and $publicationScenario.Assets.Count -eq 7) 'Complete release was not published.'
    Assert-Publication ($publicationScenario.Notes.Contains('`Setup.exe`; preserve $literal')) 'Release note literals were altered.'
    Write-Host 'PASS publication: signed local set, private staging, remote verification, literal release notes'

    $publicationScenario = New-PublicationScenario
    $publicationScenario.FailUpload = $true
    Assert-PublicationFailure { Invoke-Publication } 'exit code 19'
    Assert-Publication ($publicationScenario.Releases[0].draft -and $publicationScenario.Assets.Count -eq 2) 'Failed upload did not remain a partial draft.'
    $publicationScenario.FailUpload = $false
    Invoke-Publication
    $uploads = @($publicationScenario.Calls | Where-Object { $_[0] -eq 'release' -and $_[1] -eq 'upload' })
    Assert-Publication ($uploads.Count -eq 2 -and $uploads[1].Count -eq 11) 'Retry did not upload exactly the five missing files.'
    Assert-Publication (@($publicationScenario.Calls | Where-Object { $_[1] -eq 'create' }).Count -eq 1) 'Retry created a second release.'
    Write-Host 'PASS publication: interrupted upload resumes and reuses matching assets'

    $publicationScenario = New-PublicationScenario
    $publicationScenario.FailPublish = $true
    Assert-PublicationFailure { Invoke-Publication } 'exit code 23'
    $publicationScenario.FailPublish = $false
    Invoke-Publication
    Assert-Publication (@($publicationScenario.Calls | Where-Object { $_[1] -eq 'upload' }).Count -eq 1) 'Publish retry uploaded already verified assets again.'
    Write-Host 'PASS publication: final publication retry avoids duplicate uploads'

    $publicationScenario = New-PublicationScenario
    $publicationScenario.FailPublish = $true
    Assert-PublicationFailure { Invoke-Publication } 'exit code 23'
    $publicationScenario.FailPublish = $false
    $publicationScenario.Assets[0].digest = 'sha256:' + ('0' * 64)
    $publicationScenario.Assets[1].state = 'starter'
    Invoke-Publication
    $uploads = @($publicationScenario.Calls | Where-Object { $_[1] -eq 'upload' })
    Assert-Publication ($uploads[1].Count -eq 8) 'Retry did not replace exactly the two invalid uploaded assets.'
    Write-Host 'PASS publication: retry replaces corrupt or unfinished draft assets'

    foreach ($failure in @('hash', 'length', 'state', 'missing', 'digest', 'extra', 'tag', 'newer')) {
        $publicationScenario = New-PublicationScenario
        $publicationScenario.AfterUpload = $failure
        Assert-PublicationFailure { Invoke-Publication } 'does not match|Unexpected|does not resolve|already exists'
        Assert-Publication ($publicationScenario.Releases[0].draft) "Failure '$failure' exposed the draft."
        Assert-Publication (@($publicationScenario.Calls | Where-Object { $_[1] -eq 'edit' }).Count -eq 0) "Failure '$failure' attempted publication."
    }
    Write-Host 'PASS publication: hash, size, state, completeness, digest, extra assets, tag and version changes block publication'

    foreach ($failure in @('foreign', 'commit', 'published', 'prerelease', 'extra', 'duplicate')) {
        $publicationScenario = New-PublicationScenario
        $publicationScenario.FailUpload = $true
        Assert-PublicationFailure { Invoke-Publication } 'exit code 19'
        $publicationScenario.Calls.Clear()
        switch ($failure) {
            'foreign' { $publicationScenario.Releases[0].body = 'Manually prepared draft' }
            'commit' { $publicationScenario.Releases[0].target_commitish = 'b' * 40 }
            'published' { $publicationScenario.Releases[0].draft = $false }
            'prerelease' { $publicationScenario.Releases[0].prerelease = $true }
            'extra' { $publicationScenario.Assets += [pscustomobject]@{ name = 'internal.msi' } }
            'duplicate' { $publicationScenario.Assets += $publicationScenario.Assets[0] }
        }
        Assert-PublicationFailure { Invoke-Publication } 'not a resumable|already exists|Unexpected or duplicate'
        Assert-Publication (@($publicationScenario.Calls | Where-Object { $_[0] -ne 'api' }).Count -eq 0) "Existing '$failure' release was changed."
    }
    Write-Host 'PASS publication: foreign drafts, different commits, published releases and unexpected assets stay unchanged'

    $publicationScenario = New-PublicationScenario
    $publicationScenario.LaterPage = $true
    $publicationScenario.Releases = @([pscustomobject]@{ tag_name = 'v1.8.0'; draft = $false; prerelease = $false })
    Assert-PublicationFailure { Invoke-Publication } 'already exists: v1.8.0'
    $publicationScenario = New-PublicationScenario
    $publicationScenario.FailApi = $true
    Assert-PublicationFailure { Invoke-Publication } 'exit code 17'
    Assert-Publication (@($publicationScenario.Calls | Where-Object { $_[0] -ne 'api' }).Count -eq 0) 'API failure was treated as a missing release.'
    Write-Host 'PASS publication: all release pages are checked and API errors stop publication'

    foreach ($response in @('null', '{}', '[{}]', 'not JSON')) {
        $publicationScenario = New-PublicationScenario
        $publicationScenario.ListResponse = $response
        Assert-PublicationFailure { Invoke-Publication } 'invalid|JSON'
        Assert-Publication (@($publicationScenario.Calls | Where-Object { $_[0] -ne 'api' }).Count -eq 0) 'Malformed API response was treated as a missing release.'
    }
    Write-Host 'PASS publication: malformed API responses cannot create a release'

    $publicationScenario = New-PublicationScenario
    $setupPath = Join-Path $releaseRoot 'StreamlinkVlcStudio-Setup.exe'
    $setupBytes = [IO.File]::ReadAllBytes($setupPath)
    [IO.File]::AppendAllText($setupPath, 'tampered')
    Assert-PublicationFailure { Invoke-Publication } 'checksum mismatch'
    [IO.File]::WriteAllBytes($setupPath, $setupBytes)
    [IO.File]::AppendAllText((Join-Path $releaseRoot 'UPDATE-MANIFEST.json'), ' ')
    Write-FixtureChecksums
    Assert-PublicationFailure { Invoke-Publication } 'signature is invalid'
    Assert-Publication ($publicationScenario.Calls.Count -eq 0) 'Invalid local files reached GitHub.'
    Write-Host 'PASS publication: corrupt checksums and invalid signatures fail before contacting GitHub'
} finally {
    if (-not $hadExitCode) {
        Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    } else {
        $global:LASTEXITCODE = $savedExitCode
    }
    if (Test-Path -LiteralPath $fixtureRoot) {
        Assert-UnderDirectory -ChildPath $fixtureRoot -ParentPath $fixtureParent
        Remove-DirectoryTreeSafely $fixtureRoot
    }
}
