param([string]$Branding = $PSScriptRoot, [string]$OutDir = (Join-Path $PSScriptRoot '..\src\Assets\portal'))
# Renders branding/render-portal.html with headless Edge (transparent) and crops the phone-portal images:
# icon-192.png, icon-512.png, icon-maskable-512.png, apple-touch-icon.png, lockup.png (all embedded in the exe).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$sheet = Join-Path $env:TEMP 'maison-portal-sheet.png'
Remove-Item $sheet -ErrorAction SilentlyContinue
$url = 'file:///' + (Join-Path $Branding 'render-portal.html').Replace('\', '/')
$p = Start-Process $edge -PassThru -ArgumentList '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
    '--default-background-color=00000000', '--run-all-compositor-stages-before-draw', '--virtual-time-budget=5000',
    "--user-data-dir=$env:TEMP\maison-edge", "--screenshot=$sheet", '--window-size=1500,740', $url
$p.WaitForExit(60000) | Out-Null
if (-not (Test-Path $sheet)) { throw 'Edge did not produce the sheet' }

$src = [System.Drawing.Bitmap]::FromFile($sheet)
function Save([int]$x, [int]$y, [int]$w, [int]$h, [string]$name) {
    $b = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $w, $h), (New-Object System.Drawing.Rectangle $x, $y, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $b.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}
Save 0 0 192 192 'icon-192.png'
Save 200 0 512 512 'icon-512.png'
Save 720 0 512 512 'icon-maskable-512.png'
Save 1240 0 180 180 'apple-touch-icon.png'
Save 0 540 600 180 'lockup.png'
$src.Dispose()
Get-ChildItem $OutDir -Filter *.png | Select-Object Name, Length
