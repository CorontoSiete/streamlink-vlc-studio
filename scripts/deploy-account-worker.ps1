#requires -Version 7.0
param(
    [string]$SettingsPath = (Join-Path $env:APPDATA 'StreamStudio\settings.json'),
    [string]$VerifyServiceUrl
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This provisioning command reads this Windows user''s DPAPI-protected Kick app secret.' }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workerDirectory = Join-Path $repositoryRoot 'hosting\cloudflare'
$wranglerPath = Join-Path $workerDirectory 'node_modules\wrangler\bin\wrangler.js'
$nodePath = (Get-Command node.exe -ErrorAction Stop).Source
if (-not (Test-Path -LiteralPath $wranglerPath -PathType Leaf)) {
    throw 'Run npm ci in hosting/cloudflare first.'
}

function Invoke-AccountWrangler {
    param([string[]]$Arguments, [string]$InputJson)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $nodePath
    $start.WorkingDirectory = $workerDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['WRANGLER_SEND_METRICS'] = 'false'
    $start.Environment['CI'] = 'true'
    $start.ArgumentList.Add($wranglerPath)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not start Wrangler.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($InputJson) { $process.StandardInput.Write($InputJson) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            throw 'Wrangler exceeded its one-minute deadline. Check the Cloudflare dashboard before retrying.'
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            if ($InputJson) { throw 'Cloudflare secret provisioning failed. No secret value was written to disk or displayed.' }
            throw "Wrangler failed: $output"
        }
        return $output
    }
    finally { $process.Dispose() }
}

$publicPath = Join-Path $repositoryRoot 'shared\oauth-clients.json'
$public = Get-Content -Raw -LiteralPath $publicPath | ConvertFrom-Json
if (-not [string]::IsNullOrWhiteSpace($VerifyServiceUrl)) {
    $serviceUri = $null
    if (-not [Uri]::TryCreate($VerifyServiceUrl, [UriKind]::Absolute, [ref]$serviceUri) -or
        $serviceUri.Scheme -ne 'https' -or $serviceUri.Host -notmatch '^[a-z0-9-]+\.[a-z0-9-]+\.workers\.dev$' -or
        $serviceUri.AbsolutePath -ne '/' -or $serviceUri.Query -or $serviceUri.Fragment -or $serviceUri.UserInfo) {
        throw '-VerifyServiceUrl must be the exact HTTPS origin returned by the Cloudflare deployment.'
    }
    $serviceOrigin = $serviceUri.AbsoluteUri.TrimEnd('/')
} else {
$settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json
if ($settings.Chat.KickClientId -cne $public.KickClientId) {
    throw 'The saved Kick application does not match the app''s public registration. No secret was uploaded.'
}
if ($settings.ProtectedSecrets.Version -ne 1 -or $settings.ProtectedSecrets.Protection -ne 'DPAPI-CurrentUser') {
    throw 'The saved account secrets are not in the supported Windows-protected format.'
}

Write-Host 'Checking Cloudflare sign-in...'
$identity = Invoke-AccountWrangler -Arguments @('whoami')
if ($identity -match 'not authenticated') { throw 'Sign in first: cd hosting/cloudflare; npm run login -- --use-keyring --scopes account:read user:read workers:write workers_scripts:write' }
Write-Host 'Deploying the account service code...'
$deployment = Invoke-AccountWrangler -Arguments @('deploy')
$urlMatch = [regex]::Match($deployment, 'https://[a-z0-9-]+\.[a-z0-9-]+\.workers\.dev(?=\s|/|$)')
if (-not $urlMatch.Success) { throw 'Deployment did not return a workers.dev URL. The desktop configuration was not changed.' }
$serviceOrigin = $urlMatch.Value.TrimEnd('/')
Write-Host "Deployed Worker: $serviceOrigin"

Write-Host 'Provisioning the saved Kick app secret into Cloudflare...'
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$protected = [Convert]::FromBase64String($settings.ProtectedSecrets.Ciphertext)
$entropy = [Text.Encoding]::UTF8.GetBytes('StreamlinkVlcStudio.Settings.ProtectedSecrets.v1')
$plaintext = [Security.Cryptography.ProtectedData]::Unprotect($protected, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
try {
    $secrets = [Text.Encoding]::UTF8.GetString($plaintext) | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($secrets.KickClientSecret)) { throw 'The saved Kick client secret is missing.' }
    $secretInput = @{ KICK_CLIENT_SECRET = $secrets.KickClientSecret } | ConvertTo-Json -Compress
    # Private stdin pipe: never a command-line argument, transcript entry, or plaintext file.
    $null = Invoke-AccountWrangler -Arguments @('secret', 'bulk') -InputJson $secretInput
}
finally {
    [Array]::Clear($plaintext, 0, $plaintext.Length)
    $secretInput = $null
    $secrets = $null
}
}

Write-Host 'Checking the deployed HTTPS service...'
$ready = $false
$usePublicDns = $false
for ($attempt = 0; $attempt -lt 6; $attempt++) {
    try {
        $health = Invoke-RestMethod -Uri "$serviceOrigin/health" -TimeoutSec 10
        if ($health.status -eq 'ok') { $ready = $true; break }
    }
    catch {
        # A new workers.dev hostname can already be live while the local recursive
        # resolver still caches NXDOMAIN. This per-command DoH lookup preserves
        # normal HTTPS certificate checks and never changes Windows DNS settings.
        if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
            $healthOutput = @(& curl.exe --silent --show-error --connect-timeout 10 --max-time 20 `
                --doh-url 'https://cloudflare-dns.com/dns-query' --fail "$serviceOrigin/health" 2>$null)
            if ($LASTEXITCODE -eq 0) {
                try {
                    $health = ($healthOutput -join "`n") | ConvertFrom-Json
                    if ($health.status -eq 'ok') { $ready = $true; $usePublicDns = $true; break }
                } catch { }
            }
        }
        if ($attempt -eq 5) {
            throw "HTTPS health verification failed for $serviceOrigin. DNS or certificate activation may still be pending. No desktop configuration was changed. Resume without redeploying with -VerifyServiceUrl '$serviceOrigin'."
        }
    }
    Start-Sleep -Milliseconds 500
}
if (-not $ready) { throw 'The service is not ready. The desktop configuration was not changed.' }
if ($usePublicDns) {
    $probeOutput = @(& curl.exe --silent --show-error --connect-timeout 10 --max-time 20 --include `
        --doh-url 'https://cloudflare-dns.com/dns-query' --request POST `
        --header 'Content-Type: application/x-www-form-urlencoded' `
        --data-urlencode "client_id=$($public.KickClientId)" --data-urlencode 'grant_type=client_credentials' `
        "$serviceOrigin/v1/kick/token" 2>$null) -join "`n"
    $probeValid = $LASTEXITCODE -eq 0 -and $probeOutput -match '(?m)^HTTP/\S+ 400\b' -and
        $probeOutput -match '(?im)^Cache-Control:\s*no-store\s*$' -and $probeOutput -match '"error":"invalid_request"'
} else {
    $probe = Invoke-WebRequest -Method Post -Uri "$serviceOrigin/v1/kick/token" -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ client_id = $public.KickClientId; grant_type = 'client_credentials' } -SkipHttpErrorCheck -TimeoutSec 20
    $probeValid = $probe.StatusCode -eq 400 -and $probe.Headers['Cache-Control'] -contains 'no-store'
}
if (-not $probeValid) {
    throw 'The token endpoint did not meet the expected contract. The desktop configuration was not changed.'
}
$public.KickTokenServiceUrl = "$serviceOrigin/v1/kick/token"
[IO.File]::WriteAllText($publicPath, (($public | ConvertTo-Json) + "`n"), [Text.UTF8Encoding]::new($false))
Write-Host "Kick service is ready: $($public.KickTokenServiceUrl)"
Write-Host 'Rebuild the desktop app to include this verified endpoint. No user token was uploaded.'
if ($usePublicDns) { Write-Host 'Verified through public DNS over HTTPS. This PC''s resolver may need time to discover the newly created hostname.' }
