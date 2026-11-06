#Requires -Version 5.1
<#
  Repair-Ollama.ps1 - fixes a broken Ollama install (e.g. "llama-server binary not found"
  after a failed auto-update). Safe to run any time; your downloaded models are untouched
  (they live in %USERPROFILE%\.ollama\models, not in the program folder).

  Steps:
    1. Stop all Ollama / llama-server processes.
    2. Rename any runtime file that is still locked (antivirus scans etc.) so the
       installer can write a fresh copy - this is what made the previous update fail.
    3. Reinstall the current Ollama release with winget (--force).
    4. Verify llama-server.exe exists, then start the Ollama tray app.
#>
param([switch]$NoPause)

$ErrorActionPreference = 'Continue'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\Ollama'
$lib = Join-Path $dir 'lib\ollama'
$stamp = Get-Date -Format 'yyyyMMddHHmmss'

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Good($m) { Write-Host "    OK  $m" -ForegroundColor Green }
function Bad($m)  { Write-Host "    !!  $m" -ForegroundColor Red }

Step 'Stopping Ollama processes'
Get-Process 'ollama*', 'llama*' -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Good 'stopped'

Step 'Moving aside locked runtime files'
$moved = 0
if (Test-Path $lib) {
    Get-ChildItem $lib -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*.old-*' } | ForEach-Object {
        try { $s = [IO.File]::Open($_.FullName, 'Open', 'ReadWrite', 'None'); $s.Close() }
        catch {
            try { Rename-Item -LiteralPath $_.FullName -NewName "$($_.Name).old-$stamp" -ErrorAction Stop; $moved++; Write-Host "    renamed locked file: $($_.Name)" }
            catch { Bad "could not move $($_.FullName): $($_.Exception.Message)" }
        }
    }
}
Good "$moved locked file(s) moved aside"

Step 'Reinstalling Ollama with winget'
winget install --id Ollama.Ollama -e --force --silent --accept-source-agreements --accept-package-agreements
if ($LASTEXITCODE -ne 0) { Bad "winget exited with code $LASTEXITCODE" }

Step 'Verifying'
if (Test-Path (Join-Path $lib 'llama-server.exe')) { Good 'llama-server.exe present' } else { Bad 'llama-server.exe still missing - see the winget log above' }

Step 'Cleaning up old renamed files (ones still locked are removed after a reboot)'
Get-ChildItem $lib -Recurse -File -Filter '*.old-*' -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop } catch { Write-Host "    left for later: $($_.Name)" }
}

Step 'Starting Ollama'
$app = Join-Path $dir 'ollama app.exe'
if (Test-Path $app) { Start-Process $app } else { Start-Process (Join-Path $dir 'ollama.exe') -ArgumentList 'serve' -WindowStyle Hidden }
$up = $false
foreach ($i in 1..20) {
    try { $v = Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 2; $up = $true; break } catch { Start-Sleep -Milliseconds 750 }
}
if ($up) { Good "server running, version $($v.version)" } else { Bad 'server did not come up within 15s' }

if (-not $NoPause) { Write-Host ''; Read-Host 'Done. Press Enter to close' }
