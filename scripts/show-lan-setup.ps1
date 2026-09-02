# Show LAN setup hints for iPhone/iPad Shortcuts (Milestone 2).
# Detects the machine's private LAN address(es), shows the suggested relay URL,
# and reports (read-only) whether the relay is listening from the LAN and
# whether the firewall may be blocking inbound connections.
#
# This script NEVER:
#   - modifies the firewall (no rule creation, no disabling)
#   - touches router / Wi-Fi / Defender / port forwarding
#   - prints the bearer token
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/show-lan-setup.ps1 [-Port 8787]

param(
    [int]$Port = 8787
)

$ErrorActionPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSScriptRoot 'lib\LanSetup.psm1') -Force

Write-Host ''
Write-Host '=== ClipBridge LAN setup helper ===' -ForegroundColor Cyan
Write-Host "Tip: run this on the PC that runs the relay. Port: $Port"
Write-Host ''

# ------------------------------------------------------------- 1. candidates
$candidates = Get-LiveClipBridgeLanCandidates -Port $Port
if ($candidates.Count -eq 0) {
    Write-Host 'No suitable private-LAN IPv4 address found.' -ForegroundColor Yellow
    Write-Host '  - Is the PC connected to your Wi-Fi / Ethernet?'
    Write-Host '  - VPN or virtual adapters are excluded by design.'
    Write-Host '  - Disconnected adapters are ignored.'
    Write-Host '  - If you truly want to use a VPN adapter, configure the relay URL'
    Write-Host '    with its address manually - but prefer a direct LAN connection.'
} else {
    Write-Host 'Suggested relay URL(s) for iPhone/iPad Shortcuts:' -ForegroundColor Green
    foreach ($c in $candidates) {
        $flags = @()
        if ($c.IsVirtual) { $flags += 'virtual' }
        if ($flags.Count -eq 0) { $flags += 'physical' }
        Write-Host ("  {0,-34}  {1,-14}  [{2}]" -f $c.SuggestedUrl, $c.Name, ($flags -join ', '))
    }
    Write-Host ''
    Write-Host 'If several URLs are listed, choose the one matching the PC''s current'
    Write-Host 'network (usually the one you see in Windows Settings > Network & Internet).'
}

# ----------------------------------------------------------------- 2. listener
Write-Host ''
Write-Host '--- Relay listener check ---' -ForegroundColor Cyan
$listen = Test-RelayListening -Port $Port
if (-not $listen.IsListening) {
    Write-Host "Nothing is listening on TCP port $Port right now." -ForegroundColor Yellow
    Write-Host '  Start the relay first, e.g.:'
    Write-Host '    powershell -ExecutionPolicy Bypass -File scripts/start-lan-dev.ps1'
    Write-Host "    (or:  `$env:CLIPBRIDGE_BIND_HOST='0.0.0.0'; cd services\relay; .\.venv\Scripts\python run.py)"
} elseif ($listen.AllLoopbackOnly) {
    Write-Host "Port $Port listens on loopback only (127.0.0.1 / ::1)." -ForegroundColor Yellow
    Write-Host '  iPhone/iPad cannot reach that address. Restart the relay in LAN mode:'
    Write-Host '    powershell -ExecutionPolicy Bypass -File scripts/start-lan-dev.ps1'
} else {
    Write-Host "Port $Port is listening on: $($listen.BoundAddresses -join ', ')" -ForegroundColor Green
    $needed = $candidates | Where-Object { $listen.BoundAddresses -contains $_.Ip -or $listen.BoundAddresses -contains '0.0.0.0' }
    if ($needed.Count -gt 0) {
        Write-Host "Good: this covers $($needed.SuggestedUrl -join ', ')." -ForegroundColor Green
    } else {
        Write-Host 'Note: no listed LAN address matches the bind address - the relay is'
        Write-Host 'bound to an interface that was not detected as a valid LAN candidate.' -ForegroundColor Yellow
    }
}

# ----------------------------------------------------------------- 3. firewall
Write-Host ''
Write-Host '--- Firewall check (read-only) ---' -ForegroundColor Cyan
$fw = Test-FirewallMayBlockInbound -Port $Port
Write-Host ('Profiles checked: {0}' -f ($fw.ProfilesChecked -join ', '))
if ($fw.InboundAllowed) {
    Write-Host 'Inbound is not hard-blocked by default on at least one enabled profile.' -ForegroundColor Green
} else {
    Write-Host 'An enabled firewall profile blocks inbound by default.' -ForegroundColor Yellow
    Write-Host '  -> If the iPhone/iPad test fails with "connection refused"/timeout,'
    Write-Host '     the Windows firewall is the most likely cause.'
}
Write-Host '  Manual step (run as Administrator, only if needed - ClipBridge never'
Write-Host '  changes firewall settings for you):'
Write-Host '    New-NetFirewallRule -DisplayName "ClipBridge relay" -Direction Inbound -Protocol TCP'
Write-Host "      -LocalPort $Port -Action Allow -Profile Private"
Write-Host '  (Windows network profile must be "Private", not "Public", for this rule to apply.)'
Write-Host ''

# ---------------------------------------------------------------- 4. security
Write-Host '--- Security ---' -ForegroundColor Cyan
Write-Host 'This LAN HTTP mode is for TRUSTED private networks only:'
Write-Host '  * The bearer token travels as plain text over HTTP - anyone on the same'
Write-Host '    Wi-Fi with a packet sniffer could read it and your clipboard text.'
Write-Host '  * Do NOT port-forward this port at the router. Do NOT use on public Wi-Fi.'
Write-Host '  * Production / remote use requires HTTPS or a secure tunnel.'
Write-Host ''
Write-Host 'Your token is where you put it (services\relay\.env or your shell); it is'
Write-Host 'never printed by this script. See docs/DEVICE_TEST.md for Shortcuts steps.'
Write-Host ''
