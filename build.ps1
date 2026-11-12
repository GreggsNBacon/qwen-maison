<#
.SYNOPSIS
    Builds maison into one self-contained exe: dist\maison-<version>.exe

.DESCRIPTION
    Works the same for people and for AI coding agents (Qwen Code, Claude Code, ...): one command, plain output,
    exit code 0 on success and non-zero on any failure.

    The version comes from <Version> in src\Maison.csproj.

.PARAMETER Test
    After building, run the app's built-in self-test (screenshots every page, checks health and the dashboard) with a
    throwaway settings folder, so your real settings are never touched. Takes about a minute and opens a window.

.PARAMETER OutDir
    Where to put the exe. Default: dist\ in the repository.

.EXAMPLE
    .\build.ps1
.EXAMPLE
    .\build.ps1 -Test
#>
[CmdletBinding()]
param(
    [switch]$Test,
    [string]$OutDir = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Maison.csproj'

function Fail($message) {
    Write-Host "BUILD FAILED: $message" -ForegroundColor Red
    exit 1
}

# 1. .NET 10 SDK
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Fail ".NET SDK not found. Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10" }
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 })) {
    Fail ".NET 10 SDK (or newer) not found (have: $($sdks -join ', ')). Install it: winget install Microsoft.DotNet.SDK.10"
}

# 2. Version
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { Fail "No <Version> in $project" }
$exe = Join-Path $OutDir "maison-$version.exe"
Write-Host "Building maison $version -> $exe"

# A running copy of the same exe can't be replaced.
$running = Get-Process maison* -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { Fail "$exe is running. Close it (or build to another -OutDir) and try again." }

# 3. Publish (restore + build + single-file publish; the project renames the exe after the version)
& dotnet publish $project -c Release -o $OutDir --nologo
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish exited with code $LASTEXITCODE (see the errors above)" }
if (-not (Test-Path $exe)) { Fail "dotnet publish succeeded but $exe is missing" }

$file = Get-Item $exe
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ""
Write-Host "BUILD OK" -ForegroundColor Green
Write-Host "  exe     : $exe"
Write-Host ("  size    : {0:N1} MB" -f ($file.Length / 1MB))
Write-Host "  version : $($file.VersionInfo.ProductVersion)"
Write-Host "  sha256  : $hash"

# 4. Optional self-test
if ($Test) {
    $testDir = Join-Path $OutDir 'selftest'
    $configDir = Join-Path $OutDir 'selftest-config'
    Remove-Item $testDir, $configDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $configDir | Out-Null
    Write-Host ""
    Write-Host "Running self-test (scratch settings in $configDir) ..."
    $previous = $env:MAISON_CONFIG_DIR
    $env:MAISON_CONFIG_DIR = $configDir
    try {
        $p = Start-Process $exe -ArgumentList '--selftest', $testDir -PassThru
        if (-not $p.WaitForExit(300000)) { $p.Kill(); Fail "self-test timed out after 5 minutes" }
    }
    finally { $env:MAISON_CONFIG_DIR = $previous }

    $report = Join-Path $testDir 'selftest.txt'
    if (-not (Test-Path $report)) { Fail "self-test crashed (no $report). Check Windows Event Viewer > Application for a .NET Runtime error." }
    $lines = Get-Content $report -Encoding UTF8
    $version_line = $lines | Where-Object { $_ -like 'VERSION:*' } | Select-Object -First 1
    if ($version_line -notlike "VERSION: $version*") { Fail "self-test reported '$version_line', expected VERSION: $version" }
    Write-Host "SELF-TEST OK" -ForegroundColor Green
    Write-Host "  report      : $report"
    Write-Host "  screenshots : $testDir\*.png"
    $lines | Where-Object { $_ -match '^\[(Ok|Warn|Bad|Info)\]' } | ForEach-Object { Write-Host "  $_" }
}
exit 0
