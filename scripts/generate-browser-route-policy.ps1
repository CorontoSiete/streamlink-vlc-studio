[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$Check
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
} else {
    [IO.Path]::GetFullPath($RepositoryRoot)
}
$sourcePath = Join-Path $repoRoot "shared\platform-routes.json"
$destinationPath = Join-Path $repoRoot "browser-extension\platform-routes.generated.js"
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Shared platform route policy is missing: $sourcePath"
}
$routes = Get-Content -LiteralPath $sourcePath -Raw | ConvertFrom-Json

function Format-JavaScriptArray([object[]]$Values) {
    $routes = @($Values | ForEach-Object { [string]$_ })
    $uniqueRoutes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($routes.Count -eq 0 -or
        @($routes | Where-Object {
            $_ -notmatch '^[a-z0-9][a-z0-9-]*$' -or
            -not $uniqueRoutes.Add($_)
        }).Count -gt 0) {
        throw "Platform route lists must be non-empty and contain unique lowercase route segments."
    }

    ($routes | ForEach-Object { '      ' + ($_ | ConvertTo-Json -Compress) }) -join ",`r`n"
}

$twitch = Format-JavaScriptArray @($routes.twitch)
$kick = Format-JavaScriptArray @($routes.kick)
$content = @"
// Generated from shared/platform-routes.json. Run scripts/generate-browser-route-policy.ps1 after editing it.
(function (global) {
  "use strict";

  global.StreamlinkVlcStudioPlatformRoutes = Object.freeze({
    twitch: Object.freeze([
$twitch
    ]),
    kick: Object.freeze([
$kick
    ])
  });
})(globalThis);
"@
$normalizedContent = $content.Replace("`r`n", "`n")
if ($Check) {
    if (-not (Test-Path -LiteralPath $destinationPath -PathType Leaf)) {
        throw "Generated browser route policy is missing: $destinationPath"
    }
    $current = [IO.File]::ReadAllText($destinationPath)
    if (-not [string]::Equals($current.Replace("`r`n", "`n"), $normalizedContent, [StringComparison]::Ordinal)) {
        throw "Generated browser route policy is stale. Run scripts/generate-browser-route-policy.ps1."
    }
    Write-Host "Verified generated browser route policy."
    return
}
$temporaryPath = $destinationPath + "." + [Guid]::NewGuid().ToString("N") + ".tmp"
try {
    [IO.File]::WriteAllText($temporaryPath, $normalizedContent, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
        $backupPath = $temporaryPath + ".backup"
        try {
            [IO.File]::Replace($temporaryPath, $destinationPath, $backupPath, $true)
        } finally {
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                Remove-Item -LiteralPath $backupPath -Force
            }
        }
    } else {
        [IO.File]::Move($temporaryPath, $destinationPath)
    }
} finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}
