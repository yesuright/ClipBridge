# LanSetup.psm1 - testable helpers for discovering the right LAN address for
# iPhone/iPad Shortcuts configuration.
#
# Design rules (Milestone 2):
#  - The relay address is NEVER hardcoded anywhere; this module only *suggests*
#    a URL for the current machine. The user decides which interface to use.
#  - The logic ("which IPs are usable") lives in pure functions that take
#    adapter-like objects, so scripts/tests/test-lan-setup.ps1 can unit-test
#    them without touching the network.
#  - This module NEVER reads or prints the bearer token.

$script:VpnKeywords = @(
    'vpn', 'tun', 'tap', 'wireguard', 'wintun', 'openvpn', 'sstp', 'l2tp', 'pptp',
    'ikev2', 'ipsec', 'fortinet', 'globalprotect', 'anyconnect', 'tailscale',
    'zerotier', 'hamachi', 'nordvpn', 'expressvpn', 'surfshark', 'mullvad', 'private internet access',
    'softether', 'teredo', 'isatap', '6to4', 'cisco secure', 'cisco anyconnect'
)

$script:VirtualKeywords = @(
    'vEthernet', 'Hyper-V', 'VirtualBox', 'VMware', 'Docker', 'WSL', 'Loopback Adapter',
    'Bluetooth Network Connection', 'TAP-Windows', 'WireGuard', 'Wintun'
)

function Test-IsPrivateIpv4 {
    <#
    .SYNOPSIS
    True when the address is a usable private (RFC 1918) unicast IPv4 address.
    Loopback (127.*), link-local/APIPA (169.254.*), multicast and public ranges
    are rejected.
    #>
    [CmdletBinding()]
    param([Parameter()][AllowEmptyString()][string]$IpAddress)

    $ip = $IpAddress.Trim()
    if ($ip.Length -eq 0) { return $false }
    if ($ip -notmatch '^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$') { return $false }
    $parts = $ip.Split('.')
    if ($parts.Length -ne 4) { return $false }
    $octets = @()
    foreach ($p in $parts) {
        $n = 0
        if (-not [int]::TryParse($p, [ref]$n) -or $n -lt 0 -or $n -gt 255) { return $false }
        $octets += $n
    }
    $a, $b = $octets[0], $octets[1]
    if ($a -eq 127) { return $false }                                  # loopback
    if ($a -eq 169 -and $b -eq 254) { return $false }                  # APIPA / link-local
    if ($a -ge 224) { return $false }                                  # multicast / reserved
    if ($a -eq 10) { return $true }                                    # 10.0.0.0/8
    if ($a -eq 172 -and $b -ge 16 -and $b -le 31) { return $true }     # 172.16.0.0/12
    if ($a -eq 192 -and $b -eq 168) { return $true }                   # 192.168.0.0/16
    return $false
}

function Test-IsLikelyVirtualOrVpn {
    <#
    .SYNOPSIS
    Heuristic: does this adapter look like a VPN tunnel or a virtual adapter?
    Used to *filter* candidates, not to decide - the user always sees the full
    list when multiple real interfaces exist.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InterfaceDescription,
        [string]$InterfaceName = '',
        [switch]$Virtual
    )

    if ($Virtual) { return $true }
    $haystack = "$InterfaceDescription $InterfaceName".ToLowerInvariant()
    foreach ($k in $script:VpnKeywords) {
        if ($haystack.Contains($k)) { return $true }
    }
    foreach ($k in $script:VirtualKeywords) {
        if ($haystack -match [regex]::Escape($k.ToLowerInvariant())) { return $true }
    }
    return $false
}

function Format-RelayUrl {
    <#
    .SYNOPSIS
    Build the suggested relay URL for a LAN address, e.g.
    Format-RelayUrl '192.168.1.123' 8787 -> http://192.168.1.123:8787
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$IpAddress,
        [Parameter()][int]$Port = 8787
    )
    return "http://{0}:{1}" -f $IpAddress, $Port
}

function Select-ClipBridgeLanAddresses {
    <#
    .SYNOPSIS
    Pure filter over adapter-like objects. Each input object must have:
      Name                  (string)  interface alias (e.g. 'Wi-Fi')
      InterfaceDescription  (string)  underlying device description
      Status                (string)  Up/Down (case-insensitive)
      IPv4Address           (string[]) candidate IPv4 addresses (may be empty)
      Virtual               (bool)    adapter marked virtual by the OS
    Returns candidate entries, one per usable address, sorted with real
    (non-virtual) adapters first. Never touches the network.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Interfaces,
        [Parameter()][int]$Port = 8787
    )

    $candidates = @()
    foreach ($iface in $Interfaces) {
        $status = "$($iface.Status)".Trim()
        if ($status -notin @('Up', 'Unknown')) { continue }
        $addresses = @($iface.IPv4Address)
        if ($addresses.Count -eq 0) { continue }
        foreach ($ip in ($addresses | ForEach-Object { "$_".Trim() } | Select-Object -Unique)) {
            if (-not (Test-IsPrivateIpv4 $ip)) { continue }
            if (Test-IsLikelyVirtualOrVpn $iface.InterfaceDescription $iface.Name -Virtual:([bool]$iface.Virtual)) { continue }
            $candidates += [pscustomobject]@{
                Name                 = "$($iface.Name)"
                InterfaceDescription = "$($iface.InterfaceDescription)"
                IPv4Address          = $ip
                Ip                   = $ip
                Port                 = $Port
                SuggestedUrl         = Format-RelayUrl $ip $Port
                IsVirtual            = [bool]$iface.Virtual
            }
        }
    }
    $candidates = $candidates | Sort-Object -Property @{ Expression = { $_.IsVirtual } }, Name
    return , $candidates
}

function Get-LiveClipBridgeLanCandidates {
    <#
    .SYNOPSIS
    Enumerate the actual Windows interfaces and run the same filter.
    Returns the same shape as Select-ClipBridgeLanAddresses.
    #>
    [CmdletBinding()]
    param([Parameter()][int]$Port = 8787)

    $adapters = @{}
    try {
        $netAdapters = Get-NetAdapter -ErrorAction Stop
        foreach ($na in $netAdapters) {
            $adapters[[int]$na.ifIndex] = [pscustomobject]@{
                Name                 = $na.Name
                InterfaceDescription = $na.InterfaceDescription
                Status               = "$($na.Status)"
                Virtual              = [bool]$na.Virtual
            }
        }
    } catch {
        $netAdapters = @()
    }

    $interfaces = @()
    if (Get-Command Get-NetIPConfiguration -ErrorAction SilentlyContinue) {
        foreach ($cfg in (Get-NetIPConfiguration -ErrorAction SilentlyContinue)) {
            $idx = [int]$cfg.InterfaceIndex
            $addr = @($cfg.IPv4Address.IPAddress)
            if ($addr.Count -eq 0) { continue }
            $adapter = $adapters[$idx]
            $interfaces += [pscustomobject]@{
                InterfaceIndex       = $idx
                Name                 = if ($adapter) { $adapter.Name } else { [string]$cfg.InterfaceAlias }
                InterfaceDescription = if ($adapter) { $adapter.InterfaceDescription } else { [string]$cfg.InterfaceAlias }
                Status               = if ($adapter) { "$($adapter.Status)" } else { 'Up' }
                IPv4Address          = $addr
                Virtual              = if ($adapter) { [bool]$adapter.Virtual } else { $false }
            }
        }
    } else {
        # Fallback (no NetAdapter module): use CIM, still filter the same way.
        Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled = TRUE' -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress } |
            ForEach-Object {
                $interfaces += [pscustomobject]@{
                    InterfaceIndex       = [int]$_.InterfaceIndex
                    Name                 = [string]$_.Description
                    InterfaceDescription = [string]$_.Description
                    Status               = 'Up'
                    IPv4Address          = @($_.IPAddress | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' })
                    Virtual              = $false
                }
            }
    }

    return Select-ClipBridgeLanAddresses -Interfaces $interfaces -Port $Port
}

function Test-RelayListening {
    <#
    .SYNOPSIS
    Read-only check: which address is the relay port actually bound to?
    Returns an object with BoundAddresses (string[]) and IsListening (bool).
    Never modifies firewall or anything else.
    #>
    [CmdletBinding()]
    param([Parameter()][int]$Port = 8787)

    $bound = @()
    $listening = $false
    try {
        $conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        if ($conns) {
            $listening = $true
            $bound = @($conns | ForEach-Object { "$($_.LocalAddress)" } | Sort-Object -Unique)
        }
    } catch {
        # access denied for unprivileged listing is fine - report as unknown
    }
    return [pscustomobject]@{
        IsListening    = $listening
        BoundAddresses = $bound
        AllLoopbackOnly = ($bound.Count -gt 0 -and ($bound | Where-Object { $_ -ne '127.0.0.1' -and $_ -ne '::1' }).Count -eq 0)
    }
}

function Test-FirewallMayBlockInbound {
    <#
    .SYNOPSIS
    Read-only heuristic: is an inbound firewall rule for this port likely missing?
    Returns [pscustomobject]@{ ProfilesChecked; InboundAllowed; ExistingRules }
    Does NOT create, modify or remove any rule - the user does that manually.
    #>
    [CmdletBinding()]
    param([Parameter()][int]$Port = 8787)

    $inboundAllowed = $false
    $checked = @()
    $existing = @()
    try {
        foreach ($profile in (Get-NetFirewallProfile -ErrorAction Stop)) {
            $checked += $profile.Name
            if ($profile.Enabled -and $profile.DefaultInboundAction -ne 'Block') {
                $inboundAllowed = $true
            }
        }
        $existing = @(Get-NetFirewallRule -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -match 'ClipBridge|8787' } |
            Select-Object -ExpandProperty DisplayName)
    } catch {
        # non-elevated can still read profiles usually; on failure degrade gracefully
    }
    return [pscustomobject]@{
        ProfilesChecked = $checked
        InboundAllowed  = $inboundAllowed
        ExistingRules   = $existing
    }
}

Export-ModuleMember -Function @(
    'Test-IsPrivateIpv4',
    'Test-IsLikelyVirtualOrVpn',
    'Format-RelayUrl',
    'Select-ClipBridgeLanAddresses',
    'Get-LiveClipBridgeLanCandidates',
    'Test-RelayListening',
    'Test-FirewallMayBlockInbound'
)
