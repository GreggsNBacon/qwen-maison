param([string]$Branding, [string]$OutDir)
# Renders branding/render-assets.html with headless Edge (transparent), crops the icon sizes and lockups,
# and writes maison.ico (PNG-compressed entries), maison-256.png, logo-dark.png, logo-light.png.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$sheet = Join-Path $env:TEMP 'maison-sheet.png'
Remove-Item $sheet -ErrorAction SilentlyContinue
$url = 'file:///' + (Join-Path $Branding 'render-assets.html').Replace('\', '/')
$p = Start-Process $edge -PassThru -ArgumentList '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
    '--default-background-color=00000000', '--run-all-compositor-stages-before-draw', '--virtual-time-budget=5000',
    "--user-data-dir=$env:TEMP\maison-edge", "--screenshot=$sheet", '--window-size=1500,480', $url
$p.WaitForExit(60000) | Out-Null
if (-not (Test-Path $sheet)) { throw 'Edge did not produce the sheet' }

$src = [System.Drawing.Bitmap]::FromFile($sheet)
function Crop([int]$x, [int]$y, [int]$w, [int]$h) {
    $b = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $w, $h), (New-Object System.Drawing.Rectangle $x, $y, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    return $b
}
function PngBytes($bmp) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return , $ms.ToArray() }

$sizes = 256, 128, 64, 48, 40, 32, 24, 20, 16
$x = 0
$entries = @()
foreach ($s in $sizes) {
    $bmp = Crop $x 0 $s $s
    $entries += [pscustomobject]@{ Size = $s; Png = (PngBytes $bmp) }
    if ($s -eq 256) { $bmp.Save((Join-Path $OutDir 'maison-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($s -eq 32) { $bmp.Save((Join-Path $OutDir 'maison-32.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    $x += $s + 8
}
(Crop 0 272 740 200).Save((Join-Path $OutDir 'logo-dark.png'), [System.Drawing.Imaging.ImageFormat]::Png)
(Crop 760 272 740 200).Save((Join-Path $OutDir 'logo-light.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$src.Dispose()

# ICO: ICONDIR + ICONDIRENTRY[n] + PNG blobs (PNG entries are valid for every size since Windows Vista).
$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$e.Png.Length); $w.Write([uint32]$offset)
    $offset += $e.Png.Length
}
foreach ($e in $entries) { $w.Write($e.Png) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'maison.ico'), $ms.ToArray())
Get-ChildItem $OutDir | Select-Object Name, Length
