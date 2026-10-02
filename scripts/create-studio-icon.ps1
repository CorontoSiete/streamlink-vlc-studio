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
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

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
    # Use standard 32-bit bitmap frames. PNG-only ICOs fail in some larger-icon
    # readers used by Windows Search and older System.Drawing implementations.
    $converted = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $size * 4
    $pixels = [byte[]]::new($stride * $size)
    $converted.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0)) * 4
    $mask = [byte[]]::new($maskStride * $size)
    for ($y = 0; $y -lt $size; $y++) {
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) {
                $maskIndex = ($size - 1 - $y) * $maskStride + [int][Math]::Floor($x / 8.0)
                $mask[$maskIndex] = [byte]($mask[$maskIndex] -bor (0x80 -shr ($x % 8)))
            }
        }
    }
    $stream = [IO.MemoryStream]::new()
    $frameWriter = [IO.BinaryWriter]::new($stream)
    try {
        $frameWriter.Write([uint32]40) # BITMAPINFOHEADER
        $frameWriter.Write([int32]$size)
        $frameWriter.Write([int32]($size * 2)) # Color bitmap plus transparency mask.
        $frameWriter.Write([uint16]1)
        $frameWriter.Write([uint16]32)
        $frameWriter.Write([uint32]0) # BI_RGB
        $frameWriter.Write([uint32]$pixels.Length)
        $frameWriter.Write([int32]0)
        $frameWriter.Write([int32]0)
        $frameWriter.Write([uint32]0)
        $frameWriter.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            $frameWriter.Write($pixels, $y * $stride, $stride)
        }
        $frameWriter.Write($mask)
        $iconFrames.Add($stream.ToArray())
    } finally {
        $frameWriter.Dispose()
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
