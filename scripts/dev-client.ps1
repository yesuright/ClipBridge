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

if (-not $NoBuild) {
    & $dotnet build (Join-Path $winDir 'ClipBridge.sln') -c Debug --nologo -v m
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($RelayUrl)    { $env:CLIPBRIDGE_RELAY_URL = $RelayUrl }
if ($DeviceId)    { $env:CLIPBRIDGE_DEVICE_ID = $DeviceId }
if ($DeviceToken) { $env:CLIPBRIDGE_DEVICE_TOKEN = $DeviceToken }
$env:CLIPBRIDGE_POLL_INTERVAL_MS = "$PollIntervalMs"

$exe = Join-Path $winDir 'src\ClipBridge.Win\bin\Debug\net10.0-windows\ClipBridge.exe'
if (-not (Test-Path $exe)) { Write-Host "Client exe missing: $exe"; exit 1 }
Write-Host "Starting ClipBridge client: $exe"
& $exe
