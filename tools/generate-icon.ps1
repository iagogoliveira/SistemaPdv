<#
.SYNOPSIS
  Gera src/Pdv.Desktop/Assets/app.ico a partir do logotipo vetorial (LogoImage em Themes/Icons.xaml),
  para que o ícone do executável, do instalador e da interface sejam o mesmo desenho.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/generate-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$root = Split-Path $PSScriptRoot -Parent
$xamlPath = Join-Path $root 'src\Pdv.Desktop\Themes\Icons.xaml'
$output = Join-Path $root 'src\Pdv.Desktop\Assets\app.ico'

$stream = [IO.File]::OpenRead($xamlPath)
try { $dictionary = [Windows.Markup.XamlReader]::Load($stream) } finally { $stream.Dispose() }
$drawing = $dictionary['LogoImage'].Drawing

function Render-Png([System.Windows.Media.Drawing]$drawing, [int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $ctx = $visual.RenderOpen()
    $bounds = $drawing.Bounds
    $scale = $size / [Math]::Max($bounds.Width, $bounds.Height)
    $ctx.PushTransform((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
    $ctx.PushTransform((New-Object System.Windows.Media.TranslateTransform (-$bounds.X), (-$bounds.Y)))
    $ctx.DrawDrawing($drawing)
    $ctx.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object IO.MemoryStream
    $encoder.Save($ms)
    return ,$ms.ToArray()
}

# ICO com imagens PNG embutidas (suportado desde o Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(Render-Png $drawing $s) }

$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes($output, $out.ToArray())

# Prévia em PNG para documentação.
[IO.File]::WriteAllBytes((Join-Path $root 'src\Pdv.Desktop\Assets\logo-256.png'), $images[-1])
Write-Host "Ícone gerado: $output"
