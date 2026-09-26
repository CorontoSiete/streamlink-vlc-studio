# GitHub publication helpers. Load common.ps1 and release-contract.ps1 first.
function Invoke-ReleaseGitHub {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& gh @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub command '$($Arguments[0]) $($Arguments[1])' failed with exit code $LASTEXITCODE."
    }
    $output -join "`n"
}

function Get-ReleaseGitHubPages {
    param([Parameter(Mandatory = $true)][string]$Endpoint)

    $json = Invoke-ReleaseGitHub @('api', $Endpoint, '--paginate', '--slurp')
    if ([string]::IsNullOrWhiteSpace($json)) { throw 'GitHub returned an empty response.' }
    $pages = ConvertFrom-Json -InputObject $json -NoEnumerate
    if ($pages -isnot [Array]) { throw 'GitHub returned an invalid paginated response.' }
    foreach ($page in $pages) {
        if ($page -isnot [Array]) { throw 'GitHub returned an invalid response page.' }
        foreach ($item in $page) { $item }
    }
}

function Get-ReleaseDraftMarker {
    param([string]$Repository, [string]$Tag, [string]$Commit)

    "<!-- stream-studio-release: $Repository $Tag $Commit -->"
}

function Assert-ReleasePublicationState {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Commit,
        [Parameter(Mandatory = $true)]$Identity)

    $releases = @(Get-ReleaseGitHubPages "repos/$Repository/releases?per_page=100")
    foreach ($release in $releases) {
        if ([bool]$release.draft -or [bool]$release.prerelease) { continue }
        try {
            $other = Get-StableReleaseIdentity -Tag ([string]$release.tag_name) -MinimumVersion '0.0.0'
        } catch { continue }
        if ($other.Version -ge $Identity.Version) {
            throw "A final release at or above $Tag already exists: $($release.tag_name)."
        }
    }

    $existing = @($releases | Where-Object { [string]$_.tag_name -ceq $Tag })
    if ($existing.Count -gt 1) { throw "Multiple GitHub releases exist for $Tag." }
    if ($existing.Count -eq 1) {
        $draft = $existing[0]
        $marker = Get-ReleaseDraftMarker $Repository $Tag $Commit
        if (-not [bool]$draft.draft -or [bool]$draft.prerelease -or
            [string]$draft.target_commitish -cne $Commit -or
            ([string]$draft.body -split '\r?\n') -cnotcontains $marker) {
            throw "Release $Tag is not a resumable workflow draft for commit $Commit. It was left unchanged."
        }
        if ([long]$draft.id -le 0) { throw 'GitHub returned an invalid draft ID.' }
    }

    # The commits endpoint resolves both annotated and lightweight tags.
    $remoteCommit = Invoke-ReleaseGitHub @('api', "repos/$Repository/commits/$Tag") | ConvertFrom-Json
    if ([string]$remoteCommit.sha -cne $Commit) {
        throw "Remote tag $Tag does not resolve to source commit $Commit."
    }
    if ($existing.Count -eq 1) { $existing[0] }
}

function Get-ReleaseRemoteAssets {
    param([string]$Repository, [long]$ReleaseId)

    Get-ReleaseGitHubPages "repos/$Repository/releases/$ReleaseId/assets?per_page=100"
}

function Assert-ReleaseRemoteAssetNames {
    param([AllowEmptyCollection()][object[]]$Assets, [object[]]$Files)

    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($asset in $Assets) {
        if ([string]$asset.name -cnotin @($Files.Name) -or -not $seen.Add([string]$asset.name)) {
            throw "Unexpected or duplicate draft asset: $($asset.name). The draft was left unpublished."
        }
    }
}

function Test-ReleaseRemoteAsset {
    param($Asset, $File)

    $digest = if ($null -ne $Asset.PSObject.Properties['digest']) { [string]$Asset.digest } else { '' }
    [string]$Asset.name -ceq $File.Name -and
        [string]$Asset.state -ceq 'uploaded' -and
        [long]$Asset.size -eq $File.Length -and
        $digest -ceq "sha256:$($File.Sha256)"
}

function Assert-ReleaseRemoteAssets {
    param([AllowEmptyCollection()][object[]]$Assets, [object[]]$Files)

    Assert-ReleaseRemoteAssetNames -Assets $Assets -Files $Files
    foreach ($file in $Files) {
        $matching = @($Assets | Where-Object { [string]$_.name -ceq $file.Name })
        if ($matching.Count -ne 1 -or -not (Test-ReleaseRemoteAsset -Asset $matching[0] -File $file)) {
            throw "Uploaded asset does not match the reviewed file (name, state, length, SHA-256): $($file.Name). The draft was left unpublished."
        }
    }
}
