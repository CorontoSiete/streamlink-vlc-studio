<#
.SYNOPSIS
Publish a verified release set through a resumable GitHub draft.
.DESCRIPTION
Preflight is read-only. Publication resumes only a draft marked by this workflow
for the same repository, tag, and source commit. Uploaded GitHub asset digests
must match every local file before the release becomes public.
#>
#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Publish')]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory = $true, ParameterSetName = 'Preflight')][switch]$Preflight,
    [Parameter(ParameterSetName = 'Publish')][string]$ReleaseDirectory = 'artifacts\release-set',
    [Parameter(Mandatory = $true, ParameterSetName = 'Publish')][string]$NotesPath,
    [string]$ContractPath = (Join-Path $PSScriptRoot '..\shared\release-contract.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'lib\common.ps1')
. (Join-Path $PSScriptRoot 'lib\release-contract.ps1')
. (Join-Path $PSScriptRoot 'lib\github-release.ps1')
$contract = Read-ReleaseContract $ContractPath
$identity = Get-StableReleaseIdentity -Tag $Tag -MinimumVersion ([string]$contract.release.minimumVersion)

if ($Preflight) {
    Assert-ReleasePublicationState -Repository $Repository -Tag $Tag -Commit $Commit -Identity $identity | Out-Null
    Write-Host "Release preflight passed for $Tag at $Commit."
    return
}

# Validate all local inputs before making any remote changes.
$notes = [IO.File]::ReadAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($NotesPath))
$releaseRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReleaseDirectory)
$verified = @(Test-VerifiedReleaseSet -Contract $contract -Root $releaseRoot)
$metadata = Get-Content -LiteralPath (Join-Path $releaseRoot 'RELEASE-METADATA.json') -Raw | ConvertFrom-Json
if ($metadata.schemaVersion -ne 2 -or [string]$metadata.version -cne $identity.VersionText -or
    [string]$metadata.tag -cne $Tag -or [string]$metadata.commit -cne $Commit -or
    [string]$metadata.repository -cne $Repository) {
    throw 'Release metadata does not match the requested publication identity.'
}
& (Join-Path $PSScriptRoot 'verify-update-manifest.ps1') `
    -ContractPath $ContractPath `
    -ManifestPath (Join-Path $releaseRoot 'UPDATE-MANIFEST.json') `
    -SignaturePath (Join-Path $releaseRoot 'UPDATE-MANIFEST.sig') `
    -SetupPath (Join-Path $releaseRoot 'StreamlinkVlcStudio-Setup.exe') `
    -ZipPath (Join-Path $releaseRoot 'StreamlinkVlcStudio-release.zip') `
    -ExpectedVersion $identity.VersionText -ExpectedTag $Tag -ExpectedCommit $Commit -ExpectedRepository $Repository
$files = @($verified | ForEach-Object {
    [pscustomobject]@{
        Name = $_.File.Name
        Path = $_.File.FullName
        Length = $_.File.Length
        Sha256 = Get-FileSha256 $_.File.FullName
    }
})

$draft = Assert-ReleasePublicationState -Repository $Repository -Tag $Tag -Commit $Commit -Identity $identity
$temporaryNotes = [IO.Path]::GetTempFileName()
try {
    $marker = Get-ReleaseDraftMarker $Repository $Tag $Commit
    [IO.File]::WriteAllText($temporaryNotes, "$notes`n`n$marker`n", [Text.UTF8Encoding]::new($false))
    if ($null -eq $draft) {
        Invoke-ReleaseGitHub @('release', 'create', $Tag, '--repo', $Repository, '--verify-tag',
            '--target', $Commit, '--title', "Stream Studio $Tag", '--notes-file', $temporaryNotes, '--draft') | Out-Null
        $draft = Assert-ReleasePublicationState -Repository $Repository -Tag $Tag -Commit $Commit -Identity $identity
        if ($null -eq $draft) { throw "Created draft $Tag could not be found." }
    }

    $assets = @(Get-ReleaseRemoteAssets -Repository $Repository -ReleaseId $draft.id)
    Assert-ReleaseRemoteAssetNames -Assets $assets -Files $files
    $pending = @($files | Where-Object {
        $file = $_
        $matching = @($assets | Where-Object { [string]$_.name -ceq $file.Name })
        $matching.Count -ne 1 -or -not (Test-ReleaseRemoteAsset -Asset $matching[0] -File $file)
    })
    if ($pending.Count -gt 0) {
        Write-Host "Uploading $($pending.Count) of $($files.Count) reviewed assets to draft $Tag."
        Invoke-ReleaseGitHub (@('release', 'upload', $Tag, '--repo', $Repository, '--clobber') + @($pending.Path)) | Out-Null
    }

    # Recheck after the upload: another release or a moved tag must not be ignored.
    $current = Assert-ReleasePublicationState -Repository $Repository -Tag $Tag -Commit $Commit -Identity $identity
    if ($null -eq $current -or [long]$current.id -ne [long]$draft.id) { throw 'The release draft changed during publication.' }
    $uploaded = @(Get-ReleaseRemoteAssets -Repository $Repository -ReleaseId $draft.id)
    Assert-ReleaseRemoteAssets -Assets $uploaded -Files $files
    Invoke-ReleaseGitHub @('release', 'edit', $Tag, '--repo', $Repository, '--verify-tag',
        '--notes-file', $temporaryNotes, '--draft=false', '--prerelease=false', '--latest') | Out-Null
    Write-Host "Published $Tag with $($files.Count) verified assets."
} catch {
    throw "Release publication stopped. Rerun the protected workflow to retry its matching draft. $($_.Exception.Message)"
} finally {
    Remove-Item -LiteralPath $temporaryNotes -Force
}
