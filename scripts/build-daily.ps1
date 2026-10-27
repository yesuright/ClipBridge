# Build the daily-use ClipBridge artifact (Milestone 4).
#
# Produces:  artifacts\ClipBridge\
#   ClipBridge.exe            <- Windows tray client (self-contained .NET, no runtime needed)
#   ClipBridge.exe.config / dlls...
#   relay\ClipBridgeRelay.exe <- packaged relay (PyInstaller onedir, no Python needed)
#   relay\_internal\...
#   README.txt                <- quick start for a non-developer
#   DAILY_USE.md              <- full plain-language guide
#
# No installer, no admin, no firewall/router changes. Double-click ClipBridge.exe and it
# runs the whole system (relay + tray client) from one process tree.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\build-daily.ps1
# Requires: .NET 8 SDK (repo scripts prefer %USERPROFILE%\.dotnet), relay venv with
# pyinstaller installed (pip install pyinstaller).

param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    # Default keeps the documented artifacts\ClipBridge location. Pass another folder to
    # build and test a second copy while the installed one is running (the running app
    # holds file locks inside artifacts\ClipBridge\relay and cannot be replaced in place).
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactDisk = Join-Path $root 'build\dist-daily'
$artifact = if ($OutputDir) { [IO.Path]::GetFullPath($OutputDir) } else { Join-Path $root 'artifacts\ClipBridge' }
# Never clean a computed path unless it resolves inside the canonical workspace.
if (-not [IO.Path]::GetFullPath($artifact).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Artifact path is outside the ClipBridge workspace.'
}

Write-Host '=== build-daily: ClipBridge daily-use artifact ===' -ForegroundColor Cyan

# ------------------------------------------------------------------ locate tools
$dotnet = $null
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dotnet) { Write-Host '.NET 8 SDK not found'; exit 1 }

$relayDir = Join-Path $root 'services\relay'
$pyInstaller = Join-Path $relayDir '.venv\Scripts\pyinstaller.exe'
if (-not (Test-Path $pyInstaller)) {
    Write-Host "PyInstaller missing: $pyInstaller"
    Write-Host 'Install it into the relay venv:  '
    Write-Host "  cd $relayDir"
    Write-Host '  .venv\Scripts\pip install pyinstaller'
    exit 1
}

# ------------------------------------------------------------------ 1) Windows client (self-contained)
Write-Host ''
Write-Host '[1/3] Publishing Windows client (self-contained, no .NET runtime required)...' -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'apps\windows\src\ClipBridge.Win\ClipBridge.Win.csproj') `
    -c $Configuration -r $Runtime --self-contained true -o $artifactDisk --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host 'client publish failed'; exit $LASTEXITCODE }

# ------------------------------------------------------------------ 2) packaged relay (PyInstaller onedir)
Write-Host ''
Write-Host '[2/3] Building packaged relay (PyInstaller onedir, no Python required)...' -ForegroundColor Cyan
$buildDir = Join-Path $root 'build'
& $pyInstaller --noconfirm --onedir --name ClipBridgeRelay `
    (Join-Path $relayDir 'run.py') `
    --distpath (Join-Path $buildDir 'dist-pyinstaller') `
    --workpath (Join-Path $buildDir 'work-pyinstaller') `
    --specpath (Join-Path $buildDir 'spec-pyinstaller') `
    --paths $relayDir `
    --hidden-import uvicorn.loops.auto `
    --hidden-import uvicorn.loops.asyncio `
    --hidden-import uvicorn.protocols.http.auto `
    --hidden-import uvicorn.protocols.http.h11_impl `
    --hidden-import uvicorn.protocols.websockets.auto `
    --hidden-import uvicorn.lifespan.on `
    --hidden-import uvicorn.logging `
    --collect-submodules uvicorn
if ($LASTEXITCODE -ne 0) { Write-Host 'relay packaging failed'; exit $LASTEXITCODE }

# ------------------------------------------------------------------ 3) assemble artifact
Write-Host ''
Write-Host '[3/3] Assembling artifacts\ClipBridge\...' -ForegroundColor Cyan
if (Test-Path $artifact) { Remove-Item -LiteralPath $artifact -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifact | Out-Null

# client (published)
Get-ChildItem -Path $artifactDisk -Force | Copy-Item -Destination $artifact -Recurse -Force

# relay (packaged) → artifact\relay\
$relayDist = Join-Path $buildDir 'dist-pyinstaller\ClipBridgeRelay'
New-Item -ItemType Directory -Force -Path (Join-Path $artifact 'relay') | Out-Null
Get-ChildItem -Path $relayDist -Force | Copy-Item -Destination (Join-Path $artifact 'relay') -Recurse -Force

# docs inside the artifact (best effort — the artifact works without them)
if (Test-Path (Join-Path $root 'docs\DAILY_USE.md')) {
    Copy-Item (Join-Path $root 'docs\DAILY_USE.md') (Join-Path $artifact 'DAILY_USE.md') -Force
}
@"
ClipBridge - daily use
======================

Start:   double-click ClipBridge.exe (nothing else to run or install)
Setup:   on first start ClipBridge creates its own access token and shows a
         "Connect your iPhone" window with the address + token for both Shortcuts.
         Open it again any time: tray icon -> Connection setup...
Open:    click the tray icon (Home / History / Settings)
Hide:    close the window; sync keeps running in the tray
Stop:    right-click the tray icon -> Exit ClipBridge
Logs:    %LOCALAPPDATA%\ClipBridge\logs\
Data:    %LOCALAPPDATA%\ClipBridge\clipbridge.db
Config:  %APPDATA%\ClipBridge\config.json
Doctor:  ClipBridge.exe --doctor      (paths, config state, relay, LAN URL, autostart)
LAN URL: Home -> iPhone connection -> Copy address
         Use the current PC LAN IP, e.g. http://192.168.1.14:8787
Theme:   Settings -> Appearance -> System / Light / Dark

Portable/test: set CLIPBRIDGE_HOME to a folder to keep config + data there.

iPhone: see DAILY_USE.md next to this file. Never forward port 8787 on your router.
If Windows Firewall asks about ClipBridgeRelay.exe, allow it on private networks only.
"@ | Set-Content -Path (Join-Path $artifact 'README.txt') -Encoding UTF8

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "Artifact: $artifact" -ForegroundColor Green
Write-Host "  client:  $artifact\ClipBridge.exe"
Write-Host "  relay:   $artifact\relay\ClipBridgeRelay.exe"
Write-Host 'Runtime dependencies: NONE (self-contained). No Python, no .NET install, no admin.'
