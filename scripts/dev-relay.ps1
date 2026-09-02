# Start the ClipBridge relay for local development.
# Usage: powershell -ExecutionPolicy Bypass -File scripts/dev-relay.ps1
# Requires: services/relay/.venv (python -m venv + pip install -r requirements.txt)
param(
    [int]$Port = 8787,
    [string]$Token = $env:CLIPBRIDGE_TOKEN
)

$relayDir = Join-Path $PSScriptRoot '..\services\relay'
$python = Join-Path $relayDir '.venv\Scripts\python.exe'
if (-not (Test-Path $python)) {
    Write-Host 'Relay venv missing. Create it first:'
    Write-Host "  cd $relayDir"
    Write-Host '  python -m venv .venv'
    Write-Host '  .venv\Scripts\pip install -r requirements.txt'
    exit 1
}

if (-not $Token) {
    $envFile = Join-Path $relayDir '.env'
    if (Test-Path $envFile) {
        Write-Host "Using token from $envFile"
    } else {
        # Convenience for dev only: generate an ephemeral dev token and print it.
        $Token = 'dev-' + [guid]::NewGuid().ToString('N')
        $env:CLIPBRIDGE_TOKEN = $Token
        Write-Host "No .env found — generated ephemeral dev token (set CLIPBRIDGE_TOKEN=$Token to reuse):"
        Write-Host "  $Token"
    }
}
$env:CLIPBRIDGE_PORT = "$Port"

Set-Location $relayDir
& $python run.py
