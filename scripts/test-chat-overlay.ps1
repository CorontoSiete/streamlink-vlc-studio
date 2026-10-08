param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\native-test.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.tmp/chat-overlay-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
foreach ($name in @('tls-handshake', 'network', 'readability', 'render-resources')) {
    $binary = Join-Path $OutputDirectory ($name + '.exe')
    $sources = @((Join-Path $repositoryRoot "native/chat-overlay/tests/$name.c"))
    if ($name -ne 'tls-handshake') { $sources += Join-Path $repositoryRoot 'native/chat-overlay/tls.c' }
    Invoke-NativeTestCommand -FilePath $Gcc -FailureMessage "Native $name test build failed" -Arguments (
        @('-O2', '-Wall', '-Wextra', '-Werror', '-static', '-static-libgcc') + $sources + @('-o', $binary,
            '-lws2_32', '-lsecur32', '-lcrypt32', '-lgdi32', '-luser32', '-lwinhttp', '-lole32',
            '-lgdiplus', '-ld2d1', '-ldwrite', '-luuid', '-lpsapi'))
    Invoke-NativeTestCommand -FilePath $binary -Arguments @($OutputDirectory) -FailureMessage "Native $name tests failed"
}
