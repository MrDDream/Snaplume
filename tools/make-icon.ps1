# Generates src/Snaplume/Assets/app.ico (PNG-compressed entries, 16 to 256 px)
# from the Snaplume logo (docs/branding/snaplume.svg), drawn in its 120x120 design space.
param([string]$Out = (Join-Path $PSScriptRoot "..\src\Snaplume\Assets\app.ico"))

Add-Type -AssemblyName PresentationCore, WindowsBase

$sparkle = 'M0 -26 C2 -8 8 -2 26 0 C8 2 2 8 0 26 C-2 8 -8 2 -26 0 C-8 -2 -2 -8 0 -26 Z'

function New-Brush([string]$hex) {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $b.Freeze(); return $b
}

function Draw-Sparkle($dc, [double]$cx, [double]$cy, [double]$k, $brush) {
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform $cx, $cy))
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $k, $k))
    $dc.DrawGeometry($brush, $null, [System.Windows.Media.Geometry]::Parse($sparkle))
    $dc.Pop(); $dc.Pop()
}

function New-IconPng([int]$s) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($s / 120.0), ($s / 120.0)))

    # Dark rounded tile
    $dc.DrawRoundedRectangle((New-Brush '#1E1B2E'), $null, (New-Object System.Windows.Rect 0, 0, 120, 120), 26, 26)

    # Two crop corners (top-left, bottom-right)
    $pen = New-Object System.Windows.Media.Pen (New-Brush '#A78BFA'), 9
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    $dc.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse('M26 58 V26 H58'))
    $dc.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse('M94 62 V94 H62'))

    # Main sparkle, plus the small one only where it stays legible
    Draw-Sparkle $dc 74 46 1.05 (New-Brush '#FBBF24')
    if ($s -ge 32) { Draw-Sparkle $dc 44 76 0.38 (New-Brush '#FDE68A') }

    $dc.Pop(); $dc.Close()
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($visual)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 64, 128, 256
$images = @($sizes | ForEach-Object { , (New-IconPng $_) })

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Output "Icon written to $Out"
