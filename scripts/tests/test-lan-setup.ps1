# Unit tests for the LAN helper logic (scripts/lib/LanSetup.psm1).
# No Pester dependency - plain PowerShell assertions, exits non-zero on failure.
# Usage: powershell -ExecutionPolicy Bypass -File scripts/tests/test-lan-setup.ps1

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\lib\LanSetup.psm1') -Force

$failures = 0
function Assert-True([bool]$condition, [string]$name, [string]$detail = '') {
    if ($condition) { Write-Host "[PASS] $name $detail" -ForegroundColor Green }
    else {
        $script:failures++
        Write-Host "[FAIL] $name $detail" -ForegroundColor Red
    }
}

function New-Adapter([string]$name, [string]$desc, [string]$status, [string[]]$ips, [bool]$virtual = $false) {
    [pscustomobject]@{
        Name                 = $name
        InterfaceDescription = $desc
        Status               = $status
        IPv4Address          = $ips
        Virtual              = $virtual
    }
}

Write-Host '=== Test-IsPrivateIpv4 ===' -ForegroundColor Cyan
Assert-True (Test-IsPrivateIpv4 '192.168.1.123') '192.168.x accepted'
Assert-True (Test-IsPrivateIpv4 '10.1.2.3') '10.x accepted'
Assert-True (Test-IsPrivateIpv4 '172.16.0.1') '172.16 accepted'
Assert-True (Test-IsPrivateIpv4 '172.31.255.254') '172.31 accepted'
Assert-True (-not (Test-IsPrivateIpv4 '127.0.0.1')) 'loopback rejected'
Assert-True (-not (Test-IsPrivateIpv4 '169.254.10.10')) 'APIPA rejected'
Assert-True (-not (Test-IsPrivateIpv4 '8.8.8.8')) 'public rejected'
Assert-True (-not (Test-IsPrivateIpv4 '224.0.0.1')) 'multicast rejected'
Assert-True (-not (Test-IsPrivateIpv4 '172.15.0.1')) '172.15 rejected (just below range)'
Assert-True (-not (Test-IsPrivateIpv4 '172.32.0.1')) '172.32 rejected (just above range)'
Assert-True (-not (Test-IsPrivateIpv4 '100.64.1.2')) 'CGNAT 100.64/10 rejected'
Assert-True (-not (Test-IsPrivateIpv4 '192.168.1.999')) 'invalid octet rejected'
Assert-True (-not (Test-IsPrivateIpv4 'not-an-ip')) 'garbage rejected'
Assert-True (-not (Test-IsPrivateIpv4 '')) 'empty rejected'

Write-Host '=== Test-IsLikelyVirtualOrVpn ===' -ForegroundColor Cyan
Assert-True (Test-IsLikelyVirtualOrVpn 'WireGuard Tunnel' 'WireGuard') 'WireGuard recognized'
Assert-True (Test-IsLikelyVirtualOrVpn 'TAP-Windows Adapter V9' 'TAP') 'OpenVPN TAP recognized'
Assert-True (Test-IsLikelyVirtualOrVpn 'Fortinet FSSO VPN' 'FortiVPN') 'Fortinet recognized'
Assert-True (Test-IsLikelyVirtualOrVpn 'Hyper-V Virtual Ethernet Adapter' 'vEthernet') 'Hyper-V vEthernet recognized'
Assert-True (-not (Test-IsLikelyVirtualOrVpn 'Intel(R) Wi-Fi 6 AX201 160MHz' 'Wi-Fi')) 'real Wi-Fi NOT flagged'
Assert-True (-not (Test-IsLikelyVirtualOrVpn 'Realtek PCIe GbE Family Controller' 'Ethernet')) 'real Ethernet NOT flagged'
Assert-True (Test-IsLikelyVirtualOrVpn 'any' 'any' -Virtual:$true) 'Virtual flag forces exclusion'
Assert-True (Test-IsLikelyVirtualOrVpn 'Tailscale' 'tailscale0') 'Tailscale recognized'

Write-Host '=== Format-RelayUrl ===' -ForegroundColor Cyan
Assert-True ((Format-RelayUrl '192.168.1.123' 8787) -eq 'http://192.168.1.123:8787') 'format default port'
Assert-True ((Format-RelayUrl '10.0.0.5' 9000) -eq 'http://10.0.0.5:9000') 'format custom port'

Write-Host '=== Select-ClipBridgeLanAddresses (synthetic) ===' -ForegroundColor Cyan
$interfaces = @(
    (New-Adapter 'Wi-Fi' 'Intel(R) Wi-Fi 6 AX201' 'Up' @('192.168.1.123')),
    (New-Adapter 'Ethernet' 'Realtek PCIe GbE' 'Up' @('10.0.0.5')),
    (New-Adapter 'WireGuard' 'WireGuard Tunnel' 'Up' @('10.8.0.2')),            # VPN: private ip but excluded
    (New-Adapter 'vEthernet (Default Switch)' 'Hyper-V Virtual Ethernet Adapter' 'Up' @('172.19.1.2') -virtual $true),
    (New-Adapter 'Loopback' 'Microsoft Loopback Adapter' 'Up' @('127.0.0.1')),
    (New-Adapter 'Disconnected Wi-Fi' 'Intel(R) Wi-Fi 6 AX201' 'Disconnected' @('192.168.1.50')),
    (New-Adapter 'APIPA unit' 'Some Virtual NIC' 'Up' @('169.254.1.2')),
    (New-Adapter 'Public' 'whatever' 'Up' @('8.8.8.8')),
    (New-Adapter 'MultiNIC' 'Intel(R) Ethernet' 'Up' @('192.168.1.123', '192.168.1.124'))
)
$selected = Select-ClipBridgeLanAddresses -Interfaces $interfaces -Port 8787
# expect: Wi-Fi(1) + Ethernet(1) + MultiNIC(2) = 4 candidates
Assert-True ($selected.Count -eq 4) "4 candidates expected, got $($selected.Count)"
Assert-True (@($selected | Where-Object { $_.Name -eq 'Wi-Fi' }).Count -eq 1) 'Wi-Fi selected once'
Assert-True (@($selected | Where-Object { $_.Name -eq 'Ethernet' }).Count -eq 1) 'Ethernet selected'
Assert-True (($selected | Where-Object { $_.Name -eq 'MultiNIC' }).Count -eq 2) 'multi-homed NIC -> 2 candidates'
Assert-True (($selected | Where-Object { $_.Name -in @('WireGuard', 'vEthernet (Default Switch)', 'Loopback', 'Disconnected Wi-Fi', 'APIPA unit', 'Public') }).Count -eq 0) 'VPN/virtual/loopback/disconnected/APIPA/public all excluded'
Assert-True (($selected | Where-Object { $_.SuggestedUrl -eq 'http://192.168.1.123:8787' }).Count -ge 1) 'suggested URL well-formed'
Assert-True (($selected | Select-Object -First 1).Name -eq 'Ethernet') 'sorted by name (stable order)'
$multi = @($selected | Where-Object { $_.Name -eq 'MultiNIC' })
Assert-True ($multi.Count -eq 2) "multi-homed NIC -> 2 candidates, got $($multi.Count)"
Assert-True ($multi[0].Ip -ne $multi[1].Ip) 'multi entries have distinct IPs'

Write-Host '=== Live enumeration smoke ===' -ForegroundColor Cyan
$live = $null
try {
    $live = @(Get-LiveClipBridgeLanCandidates -Port 8787)
    Assert-True ($true) 'Get-LiveClipBridgeLanCandidates did not throw'
    $bad = @($live | Where-Object { -not (Test-IsPrivateIpv4 $_.Ip) -or $_.SuggestedUrl -notmatch '^http://' })
    Assert-True ($bad.Count -eq 0) 'every live candidate is private + well-formed URL'
} catch {
    Assert-True $false 'Get-LiveClipBridgeLanCandidates did not throw' $_.Exception.Message
}
if ($live.Count -gt 0) {
    Write-Host ("  live candidates: {0}" -f (($live | ForEach-Object { "$($_.SuggestedUrl) [$($_.Name)]" }) -join '; '))
} else {
    Write-Host '  (no live candidates on this machine right now - ok for a network-less environment)'
}

Write-Host '=== Test-RelayListening / Test-FirewallMayBlockInbound smoke ===' -ForegroundColor Cyan
$l = Test-RelayListening -Port 8787
Assert-True ($null -ne $l -and ($l.PSObject.Properties.Name -contains 'IsListening')) 'listener check returns object'
$f = Test-FirewallMayBlockInbound -Port 8787
Assert-True ($null -ne $f -and ($f.PSObject.Properties.Name -contains 'InboundAllowed')) 'firewall check returns object'

Write-Host ''
Write-Host '=== LAN HELPER TEST RESULTS ===' -ForegroundColor Cyan
if ($failures -eq 0) { Write-Host 'ALL LAN HELPER CHECKS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$failures LAN HELPER CHECK(S) FAILED" -ForegroundColor Red; exit 1
