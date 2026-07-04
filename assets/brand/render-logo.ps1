# DeltaT brand renderer -- "Signal and Soot"
# Draws the mark (amber delta raised above an off-white datum line) natively at
# every size: 1024 master PNG, specimen plate PNG, and a multi-size .ico.
Add-Type -AssemblyName System.Drawing

# Paths derive from this script's location (assets/brand/) so it runs anywhere.
$brand  = $PSScriptRoot
$repo   = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$appres = Join-Path $repo "src\DeltaT.App\Assets"

$soot      = [System.Drawing.Color]::FromArgb(255, 0x13, 0x11, 0x10)
$amber     = [System.Drawing.Color]::FromArgb(255, 0xE2, 0xA1, 0x44)
$paper     = [System.Drawing.Color]::FromArgb(255, 0xE9, 0xE1, 0xD2)
$faint     = [System.Drawing.Color]::FromArgb(255, 0x6A, 0x61, 0x52)
$hairline  = [System.Drawing.Color]::FromArgb(255, 0x3A, 0x34, 0x2C)

# Non-ASCII glyphs built from code points so the source file stays pure ASCII
# (Windows PowerShell 5.1 reads .ps1 as ANSI and would otherwise mangle them).
$DELTA = [char]0x0394
$DASH  = [char]0x2014

function New-Canvas([int]$px) {
    $bmp = New-Object System.Drawing.Bitmap($px, $px, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return @($bmp, $g)
}

function Get-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# The mark: outlined delta over a datum bar. Geometry scales with box size.
function Draw-Mark($g, [float]$cx, [float]$top, [float]$W) {
    $s   = [Math]::Max(2.0, $W * 0.17)     # stroke weight
    $H   = $W * 0.866                      # equilateral height
    $gap = [Math]::Max(1.0, $W * 0.10)     # the measured interval

    $apex = New-Object System.Drawing.PointF(($cx), ($top))
    $bl   = New-Object System.Drawing.PointF(($cx - $W / 2), ($top + $H))
    $br   = New-Object System.Drawing.PointF(($cx + $W / 2), ($top + $H))

    $amberBrush = New-Object System.Drawing.SolidBrush($script:amber)
    $paperBrush = New-Object System.Drawing.SolidBrush($script:paper)

    # Outer triangle, then carve the inner hole (uniform inset) unless too small.
    $outer = New-Object System.Drawing.Drawing2D.GraphicsPath
    $outer.AddPolygon([System.Drawing.PointF[]]@($apex, $br, $bl))
    $inr = $H / 3.0
    $k = 1.0 - ($s / $inr)
    if ($k -gt 0.22) {
        $ccx = $cx; $ccy = $top + 2.0 * $H / 3.0
        $ip = @()
        foreach ($pt in @($apex, $br, $bl)) {
            $ip += New-Object System.Drawing.PointF(($ccx + $k * ($pt.X - $ccx)), ($ccy + $k * ($pt.Y - $ccy)))
        }
        $outer.AddPolygon([System.Drawing.PointF[]]$ip)
        $outer.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
    }
    $g.FillPath($amberBrush, $outer)

    # Datum bar (the ambient reference the delta rises over).
    $barY = $top + $H + $gap
    $g.FillRectangle($paperBrush, ([float]($cx - $W / 2)), ([float]$barY), ([float]$W), ([float]$s))

    $amberBrush.Dispose(); $paperBrush.Dispose(); $outer.Dispose()
    return @($barY, $s, $H)
}

# One icon frame: soot tile + mark (matches the tray tile language).
function Draw-Tile([int]$px) {
    $c = New-Canvas $px
    $bmp = $c[0]; $g = $c[1]
    $r = [Math]::Max(2.0, $px * 0.19)
    $bw = [Math]::Max(1.0, $px / 32.0)
    $inset = $bw / 2.0
    $tile = Get-RoundRect $inset $inset ($px - $bw) ($px - $bw) $r
    $bg = New-Object System.Drawing.SolidBrush($script:soot)
    $g.FillPath($bg, $tile)
    $pen = New-Object System.Drawing.Pen($script:hairline, ([float]$bw))
    $g.DrawPath($pen, $tile)

    $W = $px * 0.56
    $H = $W * 0.866
    $s = [Math]::Max(2.0, $W * 0.17)
    $gap = [Math]::Max(1.0, $W * 0.10)
    $total = $H + $gap + $s
    $top = ($px - $total) / 2.0
    [void](Draw-Mark $g $($px / 2.0) $top $W)

    $bg.Dispose(); $pen.Dispose(); $tile.Dispose(); $g.Dispose()
    return $bmp
}

function Get-MonoFont([float]$sizePx, [System.Drawing.FontStyle]$style) {
    try {
        $fam = New-Object System.Drawing.FontFamily("Cascadia Mono")
    } catch {
        $fam = New-Object System.Drawing.FontFamily("Consolas")
    }
    return New-Object System.Drawing.Font($fam, $sizePx, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

# ============================================================ master 1024
$c = New-Canvas 1024
$master = $c[0]; $g = $c[1]
$bgBrush = New-Object System.Drawing.SolidBrush($soot)
$g.FillRectangle($bgBrush, 0, 0, 1024, 1024)
$W = 560.0; $H = $W * 0.866; $s = $W * 0.17; $gap = $W * 0.10
$total = $H + $gap + $s
$top = (1024.0 - $total) / 2.0 - 8.0
[void](Draw-Mark $g 512.0 $top $W)
$g.Dispose()
$master.Save((Join-Path $brand "deltat-logo-1024.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()

# ============================================================ specimen plate
$c = New-Canvas 1024
$plate = $c[0]; $g = $c[1]
$g.FillRectangle($bgBrush, 0, 0, 1024, 1024)

$W = 430.0; $H = $W * 0.866; $s = $W * 0.17; $gap = $W * 0.10
$total = $H + $gap + $s
$top = (1024.0 - $total) / 2.0 - 30.0
$apexY = $top
$res = Draw-Mark $g 512.0 $top $W
$barY = $res[0]; $barH = $res[1]
$datumY = [float]($barY + $barH / 2.0)

# Datum line continues across the plate, dashed, behind nothing (bar already drawn).
$dashPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(110, 0x6A, 0x61, 0x52), 2.0)
$dashPen.DashPattern = [float[]]@(1.5, 3.5)
$g.DrawLine($dashPen, 96.0, $datumY, ([float](512.0 - $W / 2 - 26)), $datumY)
$g.DrawLine($dashPen, ([float](512.0 + $W / 2 + 26)), $datumY, 928.0, $datumY)
# Measured level through the apex.
$g.DrawLine($dashPen, 96.0, ([float]$apexY), ([float](512.0 - 40)), ([float]$apexY))
$g.DrawLine($dashPen, ([float](512.0 + 40)), ([float]$apexY), 928.0, ([float]$apexY))

# Dimension callout on the right: the interval IS the subject.
$dimX = 862.0
$dimPen = New-Object System.Drawing.Pen($faint, 1.5)
$g.DrawLine($dimPen, $dimX, ([float]$apexY), $dimX, $datumY)
$g.DrawLine($dimPen, ($dimX - 7.0), ([float]$apexY), ($dimX + 7.0), ([float]$apexY))
$g.DrawLine($dimPen, ($dimX - 7.0), $datumY, ($dimX + 7.0), $datumY)

$fontS = Get-MonoFont 17 ([System.Drawing.FontStyle]::Regular)
$fontD = Get-MonoFont 24 ([System.Drawing.FontStyle]::Bold)
$faintBrush = New-Object System.Drawing.SolidBrush($faint)
$amberBrush = New-Object System.Drawing.SolidBrush($amber)

$sfC = New-Object System.Drawing.StringFormat
$sfC.Alignment = [System.Drawing.StringAlignment]::Center
$g.DrawString(($DELTA.ToString() + "T"), $fontD, $amberBrush, ($dimX + 12.0), (($apexY + $datumY) / 2.0 - 15.0))

# Corner annotations, etched small.
$g.DrawString(("SIGNAL & SOOT " + $DASH + " PLATE 01"), $fontS, $faintBrush, 96.0, 84.0)
$sfR = New-Object System.Drawing.StringFormat
$sfR.Alignment = [System.Drawing.StringAlignment]::Far
$g.DrawString("DELTA-T", $fontS, $faintBrush, (New-Object System.Drawing.RectangleF(96.0, 84.0, 832.0, 30.0)), $sfR)

# Bottom ruler: patient ticks, every fourth taller.
$tickPen = New-Object System.Drawing.Pen($faint, 1.0)
$ry = 918.0
for ($i = 0; $i -le 104; $i++) {
    $x = [float](96.0 + $i * 8.0)
    $h = 6.0
    if ($i % 4 -eq 0) { $h = 11.0 }
    $g.DrawLine($tickPen, $x, $ry, $x, ([float]($ry - $h)))
}
$g.DrawLine($tickPen, 96.0, $ry, 928.0, $ry)
$g.DrawString(("RISE OVER OUTSIDE AMBIENT " + $DASH + " CALIBRATED ONCE, TRUSTED AFTER"), $fontS, $faintBrush, (New-Object System.Drawing.RectangleF(96.0, ($ry + 14.0), 832.0, 30.0)), $sfC)

$g.Dispose()
$plate.Save((Join-Path $brand "deltat-brand-plate.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$plate.Dispose()

# ============================================================ multi-size .ico
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($px in $sizes) {
    $bmp = Draw-Tile $px
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@($px, $ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$icoPath = Join-Path $appres "deltat.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $px = $f[0]; $bytes = $f[1]
    $dim = $px
    if ($px -ge 256) { $dim = 0 }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$bytes.Length); $bw.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f[1]) }
$bw.Flush(); $bw.Close()

$bgBrush.Dispose()
"master : $(Join-Path $brand 'deltat-logo-1024.png')"
"plate  : $(Join-Path $brand 'deltat-brand-plate.png')"
"ico    : $icoPath ($([Math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB, $($frames.Count) frames)"
