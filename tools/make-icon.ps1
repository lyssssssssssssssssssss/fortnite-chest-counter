# Generates src/ChestCounter.App/Assets/app.ico (16/24/32/48/64/128/256 px, PNG-compressed entries)
# and the dashboard favicon. Pure System.Drawing; run with Windows PowerShell.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\ChestCounter.App\Assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-ChestBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0
    function RR([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = [Math]::Max(1, $r * 2)
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure(); return $p
    }
    # tile
    $tile = RR (1*$s) (1*$s) (62*$s) (62*$s) (14*$s)
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 24, 24, 28))), $tile)
    $g.DrawPath((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 58, 58, 66), [Math]::Max(1, 1.5*$s))), $tile)
    $gold = [System.Drawing.Color]::FromArgb(255, 240, 180, 60)
    $goldDark = [System.Drawing.Color]::FromArgb(255, 176, 118, 26)
    # lid (rounded top)
    $lid = RR (12*$s) (14*$s) (40*$s) (16*$s) (7*$s)
    $g.FillPath((New-Object System.Drawing.SolidBrush($gold)), $lid)
    # body
    $body = RR (12*$s) (30*$s) (40*$s) (21*$s) (3*$s)
    $g.FillPath((New-Object System.Drawing.SolidBrush($goldDark)), $body)
    # band + lock
    $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 24, 24, 28))), (12*$s), (29*$s), (40*$s), [Math]::Max(1, 2.2*$s))
    $lock = RR (28*$s) (25*$s) (8*$s) (11*$s) (2*$s)
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 236, 190))), $lock)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) { $b = New-ChestBitmap $sz; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); ,$ms.ToArray() }

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app-256.png'), $pngs[-1])
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app-64.png'), $pngs[4])
"icon written to $OutDir"
