# The same PE, registry, and bounded process probes are used by Setup and the app.
if ($null -eq ('StreamStudio.Installation.WindowsDependencyProbe' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'WindowsDependencyProbe.cs')
}

function Get-WebView2Version {
    [StreamStudio.Installation.WindowsDependencyProbe]::ReadWebView2Version($false)
}
