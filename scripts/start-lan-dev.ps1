# Start the ClipBridge relay + Windows client for LAN iPhone/iPad testing (Milestone 2).
#
# What it does:
#   1. checks prerequisites (relay venv, relay .env with CLIPBRIDGE_TOKEN)
#   2. starts the relay bound to 0.0.0.0 (LAN mode) in this session's environment
#   3. builds & starts the Windows tray client pointing at the relay (localhost)
#   4. prints the suggested LAN URL(s) + security notice + firewall hint
#
# What it NEVER does:
#   - modifies the Windows firewall / Defender / router / Wi-Fi
#   - prints the bearer token (read it from services\relay\.env yourself)
#   - overwrites your .env or %APPDATA%\ClipBridge\config.json
#   - exposes the relay to the Internet
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/start-lan-dev.ps1 [-Port 8787]
# Stop:   Ctrl+C (stops relay and client started by this script)

param(
    [int]$Port = 8787,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$relayDir = Join-Path $root 'services\relay'
$winDir = Join-Path $root 'apps\windows'
Import-Module (Join-Path $PSScriptRoot 'lib\LanSetup.psm1') -Force

# ---------------------------------------------------------------- preflight
$python = Join-Path $relayDir '.venv\Scripts\python.exe'
if (-not (Test-Path $python)) {
    Write-Host "Relay venv missing: $python"
    Write-Host 'Create it first:'
    Write-Host "  cd $relayDir"
    Write-Host '  python -m venv .venv'
    Write-Host '  .venv\Scripts\pip install -r requirements.txt'
    exit 1
}

$envFile = Join-Path $relayDir '.env'
if (-not (Test-Path $envFile)) {
    Write-Host 'No services\relay\.env found. The relay refuses to start without a token.'
    Write-Host 'Create it (this gives you the current local secret to type into the Shortcuts):'
    Write-Host "  cd $relayDir; Copy-Item .env.example .env"
    Write-Host '  then set CLIPBRIDGE_TOKEN=<random> and CLIPBRIDGE_BIND_HOST=0.0.0.0'
    Write-Host '  token generator: python -c "import secrets; print(secrets.token_hex(32))"'
    exit 1
}
$hasToken = (Select-String -Path $envFile -Pattern '^CLIPBRIDGE_TOKEN=.' -Quiet)
if (-not $hasToken) {
    Write-Host 'services\relay\.env exists but CLIPBRIDGE_TOKEN is empty.'
    Write-Host 'Set CLIPBRIDGE_TOKEN=<random> in that file first. (The token is never printed.)'
    exit 1
}

$dotnet = $null
$sdkVersion = $null
foreach ($candidate in @("$env:USERPROFILE\.dotnet\dotnet.exe", (Get-Command dotnet -ErrorAction SilentlyContinue).Source)) {
    if (-not $candidate -or -not (Test-Path $candidate)) { continue }
    if ($sdkVersion) { continue }
    # Probe from the repo root so the SDK resolver honours global.json (.NET 10 pin).
    Push-Location (Join-Path $PSScriptRoot '..')
    $version = try { (& $candidate --version 2>$null | Select-Object -First 1) } catch { $null }
    Pop-Location
    if ($version) { $dotnet = $candidate; $sdkVersion = $version }
}
if (-not $dotnet) { Write-Host 'No .NET 10 SDK matching global.json found'; exit 1 }

# ---------------------------------------------------------------- build client
if (-not $NoBuild) {
    & $dotnet build (Join-Path $winDir 'ClipBridge.sln') -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host 'client build failed'; exit $LASTEXITCODE }
}
$clientExe = Join-Path $winDir 'src\ClipBridge.Win\bin\Debug\net10.0-windows\ClipBridge.exe'
if (-not (Test-Path $clientExe)) { Write-Host "Client exe missing: $clientExe"; exit 1 }

# ---------------------------------------------------------------- candidates
$candidates = Get-LiveClipBridgeLanCandidates -Port $Port
if ($candidates.Count -eq 0) {
    Write-Host 'WARNING: no usable private-LAN IPv4 detected - iPhone/iPad may not reach the relay.'
    Write-Host '         (run scripts/show-lan-setup.ps1 for details)' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- start relay
# LAN mode: bind 0.0.0.0. These env vars are scoped to this script session only,
# the user's environment is not modified.
$env:CLIPBRIDGE_BIND_HOST = '0.0.0.0'
$env:CLIPBRIDGE_PORT = "$Port"

Write-Host ''
Write-Host '=== Starting ClipBridge relay in LAN mode (0.0.0.0) ===' -ForegroundColor Cyan
$relayProc = Start-Process -FilePath $python -ArgumentList 'run.py' `
    -WorkingDirectory $relayDir -PassThru -WindowStyle Minimized

# wait for health via loopback (always reachable from this machine)
$healthy = $false
for ($i = 0; $i -lt 60; $i++) {
    if ($relayProc.HasExited) { break }
    try {
        $h = Invoke-RestMethod "http://127.0.0.1:$Port/api/v1/health" -TimeoutSec 2
        if ($h.status -eq 'ok') { $healthy = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 500
}
if (-not $healthy) {
    Write-Host "Relay did not become healthy (process exited? port $Port busy?)." -ForegroundColor Red
    if (-not $relayProc.HasExited) { Stop-Process -Id $relayProc.Id -Force }
    exit 1
}
Write-Host "Relay is up on http://0.0.0.0:$Port (health OK)." -ForegroundColor Green

# ---------------------------------------------------------------- start client
Write-Host ''
Write-Host '=== Starting Windows client (tray) ===' -ForegroundColor Cyan
$env:CLIPBRIDGE_RELAY_URL = "http://127.0.0.1:$Port"
$clientProc = Start-Process -FilePath $clientExe -PassThru
Write-Host "Client started (PID $($clientProc.Id)). Copying text now will sync;"
Write-Host 'remote posts from iPhone/iPad will land in the clipboard within ~2 s.'

# ---------------------------------------------------------------- summary
Write-Host ''
Write-Host '--- Use these URLs in the Shortcuts ---' -ForegroundColor Green
if ($candidates.Count -eq 0) {
    Write-Host '  (none detected - see warning above)'
} else {
    foreach ($c in $candidates) {
        Write-Host ("  {0}   (interface: {1})" -f $c.SuggestedUrl, $c.Name)
    }
}
Write-Host ''
Write-Host '--- SECURITY (please read) ---' -ForegroundColor Yellow
Write-Host '  * LAN mode = plain HTTP. The bearer token travels UNENCRYPTED on your network.'
Write-Host '  * Use only on a trusted private Wi-Fi. NEVER port-forward this port.'
Write-Host '  * If the iPhone cannot connect, the Windows firewall is the likely cause:'
Write-Host '    run scripts/show-lan-setup.ps1 then apply the firewall step manually'
Write-Host '    (as Administrator, Private profile only). ClipBridge never changes'
Write-Host '    firewall settings by itself.'
Write-Host ''
Write-Host 'Your token: read CLIPBRIDGE_TOKEN from services\relay\.env and paste it into'
Write-Host 'the Shortcut configuration once. This script never prints it.'
Write-Host ''
Write-Host 'Press Ctrl+C here to stop relay + client.' -ForegroundColor Cyan

try {
    while ($true) {
        Start-Sleep -Seconds 5
        if ($relayProc.HasExited) {
            Write-Host 'Relay exited unexpectedly.' -ForegroundColor Red
            break
        }
    }
} finally {
    if ($clientProc -and -not $clientProc.HasExited) {
        Stop-Process -Id $clientProc.Id -Force -ErrorAction SilentlyContinue
    }
    if ($relayProc -and -not $relayProc.HasExited) {
        Stop-Process -Id $relayProc.Id -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'Stopped relay and client.'
}
