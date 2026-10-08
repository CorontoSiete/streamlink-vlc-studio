param(
    [Parameter(Mandatory = $true)][string]$Gcc,
    [Parameter(Mandatory = $true)][string]$VlcIncludeDirectory,
    [Parameter(Mandatory = $true)][string]$VlcLibraryDirectory,
    [Parameter(Mandatory = $true)][string]$VlcDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/native-test.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot '.tmp/chat-subpicture-tests' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
foreach ($name in @('subpictures', 'received-frames')) {
    $binary = Join-Path $OutputDirectory ($name + '.exe')
    Invoke-NativeTestCommand -FilePath $Gcc -FailureMessage 'Subpicture test build failed' -Arguments @(
        '-O2', '-Wall', '-Wextra', '-Werror', '-static-libgcc', '-D__PLUGIN__', '-DWIN32', '-D_WIN32_WINNT=0x0601',
        '-include', (Join-Path $repositoryRoot 'native/chat-overlay/quality-gdi/build-config.h'),
        '-I', $VlcIncludeDirectory, '-L', $VlcLibraryDirectory,
        (Join-Path $repositoryRoot "native/chat-overlay/tests/$name.c"), '-o', $binary,
        '-lvlccore', '-luser32'
    )
    $previousPath = $env:PATH
    try {
        $env:PATH = $VlcDirectory + ';' + $previousPath
        Invoke-NativeTestCommand -FilePath $binary -FailureMessage 'Subpicture tests failed'
    } finally { $env:PATH = $previousPath }
}
