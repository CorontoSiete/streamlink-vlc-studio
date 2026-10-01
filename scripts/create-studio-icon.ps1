<#
.SYNOPSIS
Render the Studio vector mark into the multi-resolution Windows application icon.
.EXAMPLE
powershell.exe -NoProfile -STA -File .\scripts\create-studio-icon.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this renderer in an STA PowerShell process, as shown in the example.'
}
Add-Type -AssemblyName PresentationCore, WindowsBase
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetDirectory = Join-Path $repoRoot 'src\StreamlinkVlcStudio.App.Wpf\Assets'
[xml]$source = Get-Content -LiteralPath (Join-Path $assetDirectory 'Studio.svg') -Raw
$iconFrames = [Collections.Generic.List[byte[]]]::new()
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

foreach ($size in $iconSizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try {
        $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size / 256.0, $size / 256.0))
        foreach ($rectangle in $source.DocumentElement.ChildNodes) {
            if ($rectangle.LocalName -ne 'rect') { continue }
            $fill = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($rectangle.fill))
            $pen = $null
            if ($rectangle.HasAttribute('stroke')) {
                $stroke = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($rectangle.stroke))
                $pen = [Windows.Media.Pen]::new($stroke, [double]$rectangle.GetAttribute('stroke-width'))
            }
            $bounds = [Windows.Rect]::new([double]$rectangle.x, [double]$rectangle.y, [double]$rectangle.width, [double]$rectangle.height)
            $drawing.DrawRoundedRectangle($fill, $pen, $bounds, [double]$rectangle.rx, [double]$rectangle.rx)
        }
        $drawing.Pop()
    } finally {
        $drawing.Close()
    }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        $iconFrames.Add($stream.ToArray())
    } finally {
        $stream.Dispose()
    }
}

$iconPath = Join-Path $assetDirectory 'Studio.ico'
$writer = [IO.BinaryWriter]::new([IO.File]::Create($iconPath))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$iconFrames.Count)
    $offset = 6 + 16 * $iconFrames.Count
    for ($index = 0; $index -lt $iconSizes.Count; $index++) {
        $dimension = if ($iconSizes[$index] -eq 256) { 0 } else { $iconSizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$iconFrames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $iconFrames[$index].Length
    }
    foreach ($frame in $iconFrames) { $writer.Write([byte[]]$frame) }
} finally {
    $writer.Dispose()
}
Write-Output "Rendered $($iconFrames.Count) icon sizes to $iconPath"
