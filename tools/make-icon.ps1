# Draws TriRecTray\trirec.ico: three monitors and a red recording dot, at 16-256 px.
# Run again after changing the drawing; the build picks the file up.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\TriRecTray\trirec.ico'
$sizes = 16, 24, 32, 48, 64, 256

function Draw([int]$n) {
    $bmp = New-Object Drawing.Bitmap $n, $n
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)
    $u = $n / 32.0
    function Box($x, $y, $w, $h) { New-Object Drawing.RectangleF ($x * $u), ($y * $u), ($w * $u), ($h * $u) }
    # Rounded dark tile
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $r = 7 * $u; $t = Box 0.5 0.5 31 31
    $path.AddArc($t.X, $t.Y, $r, $r, 180, 90); $path.AddArc($t.Right - $r, $t.Y, $r, $r, 270, 90)
    $path.AddArc($t.Right - $r, $t.Bottom - $r, $r, $r, 0, 90); $path.AddArc($t.X, $t.Bottom - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(36, 38, 44))), $path)
    # Three screens: big left, two stacked right (the real layout)
    $screen = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(150, 190, 235))
    $g.FillRectangle($screen, (Box 4 6 13 11))
    $g.FillRectangle($screen, (Box 18.5 6 9.5 5))
    $g.FillRectangle($screen, (Box 18.5 12 9.5 5))
    # Red dot
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(230, 45, 45))), (Box 11 18.5 10 10))
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

# ICO file: header, one directory entry per size, then the PNG images.
$imgs = foreach ($s in $sizes) { , (Draw $s) }
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i] % 256
    $w.Write([byte]$s); $w.Write([byte]$s); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$imgs[$i].Length); $w.Write([uint32]$offset)
    $offset += $imgs[$i].Length
}
foreach ($img in $imgs) { $w.Write($img) }
$w.Dispose()
"wrote $out ($((Get-Item $out).Length) bytes)"
