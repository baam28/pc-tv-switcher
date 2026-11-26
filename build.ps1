# Builds TVMode.exe using the C# compiler that ships with Windows (.NET Framework 4.x). No installs needed.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (.NET Framework 4.x is required).' }

$root = $PSScriptRoot
$src = Join-Path $root 'src'
$ico = Join-Path $src 'app.ico'

# --- App icon: drawn here at every size Windows asks for, so it is crisp in Explorer, the taskbar and the tray.
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
        $r = [single]($s * 0.22)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $w = [single]($s - 1)
        $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
        $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90); $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
        $path.CloseFigure()
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(86, 156, 255)), ([System.Drawing.Color]::FromArgb(52, 98, 230))
        $g.FillPath($brush, $path)
        # white TV: rounded screen outline + stand
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.2, $s * 0.075))
        $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $x = $s * 0.2; $y = $s * 0.24; $tw = $s * 0.6; $th = $s * 0.4
        $g.DrawRectangle($pen, [single]$x, [single]$y, [single]$tw, [single]$th)
        $g.DrawLine($pen, [single]($s * 0.36), [single]($s * 0.78), [single]($s * 0.64), [single]($s * 0.78))
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        , $ms.ToArray()
    }
    # ICO container with PNG-compressed images
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $pngs[$i].Length
        $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
        $offset += $len
    }
    foreach ($p in $pngs) { $bw.Write($p) }
    [System.IO.File]::WriteAllBytes($ico, $out.ToArray())
    Write-Host "Created $ico"
}

$sources = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    /out:"$root\TVMode.exe" `
    /win32manifest:"$src\app.manifest" /win32icon:"$ico" /resource:"$ico",TVMode.app.ico `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Built $root\TVMode.exe"
