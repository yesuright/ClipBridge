# Build and run the ClipBridge Windows client (tray app) for local development.
# Usage: powershell -ExecutionPolicy Bypass -File scripts/dev-client.ps1
# Configuration precedence: environment variables > %APPDATA%\ClipBridge\config.json > defaults
param(
    [string]$RelayUrl = $env:CLIPBRIDGE_RELAY_URL,
    [string]$DeviceId = $env:CLIPBRIDGE_DEVICE_ID,
    [string]$DeviceToken = $env:CLIPBRIDGE_DEVICE_TOKEN,
    [int]$PollIntervalMs = 2000,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$winDir = Join-Path $PSScriptRoot '..\apps\windows'

$dotnet = $null
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { $dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe" }
if (-not $dotnet) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dotnet) { Write-Host '.NET 8 SDK not found'; exit 1 }

if (-not $NoBuild) {
    & $dotnet build (Join-Path $winDir 'ClipBridge.sln') -c Debug --nologo -v m
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($RelayUrl)    { $env:CLIPBRIDGE_RELAY_URL = $RelayUrl }
if ($DeviceId)    { $env:CLIPBRIDGE_DEVICE_ID = $DeviceId }
if ($DeviceToken) { $env:CLIPBRIDGE_DEVICE_TOKEN = $DeviceToken }
$env:CLIPBRIDGE_POLL_INTERVAL_MS = "$PollIntervalMs"

$exe = Join-Path $winDir 'src\ClipBridge.Win\bin\Debug\net8.0-windows\ClipBridge.exe'
if (-not (Test-Path $exe)) { Write-Host "Client exe missing: $exe"; exit 1 }
Write-Host "Starting ClipBridge client: $exe"
& $exe
