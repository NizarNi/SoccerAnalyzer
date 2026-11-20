# Draws the SoccerAnalyzer logo (own artwork) and writes Assets\logo.png + Assets\SoccerAnalyzer.ico (16..256 px).
param([string]$OutDir = (Join-Path $PSScriptRoot '..\Assets'))
Add-Type -AssemblyName System.Drawing
$null = New-Item -ItemType Directory -Force $OutDir

function New-Logo([int]$S) {
  $bmp = New-Object System.Drawing.Bitmap $S, $S
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $k = $S / 256.0
  function P($v) { [single]($v * $k) }

  # rounded-square pitch background
  $r = P 52
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($S - $r, 0, $r, $r, 270, 90)
  $path.AddArc($S - $r, $S - $r, $r, $r, 0, 90); $path.AddArc(0, $S - $r, $r, $r, 90, 90); $path.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $S)), ([System.Drawing.Color]::FromArgb(255, 14, 52, 32)), ([System.Drawing.Color]::FromArgb(255, 32, 120, 62))
  $g.FillPath($bg, $path)
  # pitch stripes
  $stripe = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(22, 255, 255, 255))
  $g.SetClip($path)
  foreach ($i in 0..3) { $g.FillRectangle($stripe, 0, (P (20 + $i * 64)), $S, (P 32)) }
  $g.ResetClip()

  # spotlight beam fading upward + ground ring
  $cx = P 128; $baseY = P 196
  $beamPts = @([System.Drawing.PointF]::new($cx - (P 62), $baseY), [System.Drawing.PointF]::new($cx + (P 62), $baseY), [System.Drawing.PointF]::new($cx + (P 30), (P 14)), [System.Drawing.PointF]::new($cx - (P 30), (P 14)))
  $beam = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, $baseY)), ([System.Drawing.PointF]::new(0, (P 14))), ([System.Drawing.Color]::FromArgb(170, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
  $g.FillPolygon($beam, $beamPts)
  $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 255, 255, 255)), (P 7)
  $g.DrawEllipse($ringPen, $cx - (P 82), $baseY - (P 26), (P 164), (P 52))
  $ringPen2 = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)), (P 4)
  $g.DrawEllipse($ringPen2, $cx - (P 56), $baseY - (P 18), (P 112), (P 36))

  # soccer ball standing in the ring
  $bx = $cx; $by = P 132; $br = P 56
  $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 0, 0, 0))
  $g.FillEllipse($shadow, $bx - $br * 0.8, $baseY - (P 12), $br * 1.6, (P 20))
  $gp = New-Object System.Drawing.Drawing2D.GraphicsPath; $gp.AddEllipse($bx - $br, $by - $br, 2 * $br, 2 * $br)
  $ball = New-Object System.Drawing.Drawing2D.PathGradientBrush $gp
  $ball.CenterPoint = [System.Drawing.PointF]::new($bx - $br * 0.3, $by - $br * 0.35)
  $ball.CenterColor = [System.Drawing.Color]::White; $ball.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 205, 210, 215))
  $g.FillEllipse($ball, $bx - $br, $by - $br, 2 * $br, 2 * $br)
  $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 25, 28, 32))
  function Pent($cx0, $cy0, $rad, $rot) { $pts = @(); foreach ($i in 0..4) { $a = ($rot + 72 * $i) * [Math]::PI / 180; $pts += [System.Drawing.PointF]::new([single]($cx0 + $rad * [Math]::Cos($a)), [single]($cy0 + $rad * [Math]::Sin($a))) }; ,$pts }
  $g.FillPolygon($dark, (Pent $bx $by ($br * 0.36) (-90)))
  $seam = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 25, 28, 32)), ([Math]::Max(1, (P 3)))
  foreach ($i in 0..4) {
    $a = (-90 + 72 * $i) * [Math]::PI / 180
    $x1 = $bx + $br * 0.36 * [Math]::Cos($a); $y1 = $by + $br * 0.36 * [Math]::Sin($a)
    $x2 = $bx + $br * 0.70 * [Math]::Cos($a); $y2 = $by + $br * 0.70 * [Math]::Sin($a)
    $g.DrawLine($seam, [single]$x1, [single]$y1, [single]$x2, [single]$y2)
    $g.FillPolygon($dark, (Pent ($bx + $br * 0.93 * [Math]::Cos($a)) ($by + $br * 0.93 * [Math]::Sin($a)) ($br * 0.24) (90 + 72 * $i + 180)))
  }
  $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 60, 66, 72)), ([Math]::Max(1, (P 3)))
  $g.DrawEllipse($rim, $bx - $br, $by - $br, 2 * $br, 2 * $br)
  $g.Dispose()
  return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) { $b = New-Logo $s; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs[$s] = $ms.ToArray(); $b.Dispose() }
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'logo.png'), $pngs[256])

$ico = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
  $dim = if ($s -ge 256) { 0 } else { $s }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$s].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $w.Write($pngs[$s]) }
$w.Flush(); [System.IO.File]::WriteAllBytes((Join-Path $OutDir 'SoccerAnalyzer.ico'), $ico.ToArray())
"wrote $((Get-Item (Join-Path $OutDir 'SoccerAnalyzer.ico')).Length) bytes ico, $($pngs[256].Length) bytes png"
