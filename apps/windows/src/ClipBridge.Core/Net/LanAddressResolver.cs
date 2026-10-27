using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ClipBridge.Core.Net;

/// <summary>An interface candidate for the LAN relay URL.</summary>
public sealed record LanCandidate(string Name, string Description, string IpAddress, bool IsVirtual, int Port)
{
    public string Url => $"http://{IpAddress}:{Port}";
}

/// <summary>
/// Pure, unit-testable selection of the best LAN IPv4 for iPhone/iPad Shortcuts
/// (.NET port of the Milestone 2 PowerShell heuristics in scripts/lib/LanSetup.psm1):
/// - private RFC-1918 unicast IPv4 only (no loopback/APIPA/public/multicast)
/// - excludes VPN tunnels and virtual adapters by description/name keywords and OS virtual flag
/// - ranks real adapters with a default gateway above isolated/virtual ones, and
///   Ethernet/Wi-Fi above exotic interface types, instead of assuming "first adapter wins"
/// - never throws: one broken adapter must not take the whole app down
/// </summary>
public static class LanAddressResolver
{
    private static readonly string[] VpnKeywords =
    {
        "vpn", "tun", "tap", "wireguard", "wintun", "openvpn", "sstp", "l2tp", "pptp",
        "ikev2", "ipsec", "fortinet", "globalprotect", "anyconnect", "tailscale",
        "zerotier", "hamachi", "nordvpn", "expressvpn", "surfshark", "mullvad", "private internet access",
        "softether", "teredo", "isatap", "6to4", "cisco secure", "cisco anyconnect",
    };

    private static readonly string[] VirtualKeywords =
    {
        "virtual", "vehternet", "hyper-v", "hyperv", "virtualbox", "vmware", "docker", "wsl",
        "loopback adapter", "bluetooth network connection", "tap-windows", "wireguard", "wintun",
    };

    public static bool IsPrivateIpv4(string ip)
    {
        if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = parsed.GetAddressBytes();
        var a = bytes[0];
        var b = bytes[1];
        if (a == 127) return false;                                  // loopback
        if (a == 169 && b == 254) return false;                      // APIPA / link-local
        if (a >= 224) return false;                                  // multicast / reserved
        if (a == 10) return true;                                    // 10.0.0.0/8
        if (a == 172 && b is >= 16 and <= 31) return true;           // 172.16.0.0/12
        if (a == 192 && b == 168) return true;                       // 192.168.0.0/16
        return false;
    }

    public static bool IsLikelyVirtualOrVpn(string interfaceDescription, string interfaceName = "", bool isVirtual = false)
    {
        if (isVirtual) return true;
        var haystack = $"{interfaceDescription} {interfaceName}".ToLowerInvariant();
        foreach (var k in VpnKeywords)
        {
            if (haystack.Contains(k)) return true;
        }
        foreach (var k in VirtualKeywords)
        {
            if (haystack.Contains(k)) return true;
        }
        return false;
    }

    /// <summary>
    /// Pure filter over adapter-like objects.
    /// Input shape: Name, Description, IsUp, IsVirtual, IPv4Addresses, HasGateway, TypeRank.
    /// Returns candidates best-first: physical before virtual, routed before isolated,
    /// Ethernet/Wi-Fi before exotic types, then by name and address (stable output).
    /// </summary>
    public static IReadOnlyList<LanCandidate> SelectCandidates(
        IEnumerable<AdapterInfo> adapters, int port = 8787)
    {
        var list = adapters as IReadOnlyList<AdapterInfo> ?? adapters.ToList();
        var result = new List<LanCandidate>();
        foreach (var adapter in list)
        {
            if (!adapter.IsUp) continue;
            bool virtualOrVpn;
            try
            {
                virtualOrVpn = adapter.IsVirtual
                    || IsLikelyVirtualOrVpn(adapter.Description, adapter.Name, adapter.IsVirtual);
            }
            catch
            {
                virtualOrVpn = true; // a broken adapter is never the best answer
            }
            foreach (var raw in adapter.IPv4Addresses.Distinct())
            {
                var ip = raw.Trim();
                if (!IsPrivateIpv4(ip)) continue;
                if (virtualOrVpn) continue;
                result.Add(new LanCandidate(adapter.Name, adapter.Description, ip, adapter.IsVirtual, port));
            }
        }

        return result
            .OrderBy(c => c.IsVirtual)
            .ThenBy(c => GatewayRank(list, c))
            .ThenBy(c => TypeRank(list, c))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.IpAddress, StringComparer.Ordinal)
            .ToList();
    }

    private static int GatewayRank(IEnumerable<AdapterInfo> adapters, LanCandidate candidate)
    {
        foreach (var adapter in adapters)
        {
            if (string.Equals(adapter.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                return adapter.HasGateway ? 0 : 1;
            }
        }
        return 1;
    }

    private static int TypeRank(IEnumerable<AdapterInfo> adapters, LanCandidate candidate)
    {
        foreach (var adapter in adapters)
        {
            if (string.Equals(adapter.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Math.Clamp(adapter.TypeRank, 0, 9);
            }
        }
        return 2;
    }

    /// <summary>
    /// Puts an explicitly configured IP first when it is still present on this machine.
    /// Returns the input order unchanged when the preference is absent (the adapter is
    /// unplugged, DHCP gave it a different address, …) — a stale override must never
    /// hide a working address.
    /// </summary>
    public static IReadOnlyList<LanCandidate> PreferIp(IReadOnlyList<LanCandidate> candidates, string? preferredIp)
    {
        if (candidates.Count <= 1 || string.IsNullOrWhiteSpace(preferredIp))
        {
            return candidates;
        }
        var preferred = candidates.FirstOrDefault(
            c => string.Equals(c.IpAddress, preferredIp.Trim(), StringComparison.Ordinal));
        if (preferred is null)
        {
            return candidates;
        }
        var reordered = new List<LanCandidate>(candidates.Count) { preferred };
        reordered.AddRange(candidates.Where(c => !ReferenceEquals(c, preferred)));
        return reordered;
    }

    /// <summary>Enumerate the real Windows interfaces and run the same filter.</summary>
    public static IReadOnlyList<LanCandidate> DiscoverCandidates(int port = 8787)
    {
        var adapters = new List<AdapterInfo>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return Array.Empty<LanCandidate>(); // no usable network stack — treated as "no LAN"
        }

        foreach (var nic in nics)
        {
            try
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue; // loopback + tunnel interfaces are never LAN candidates
                }
                var isUp = nic.OperationalStatus == OperationalStatus.Up;
                var properties = nic.GetIPProperties();
                var addresses = properties.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => u.Address.ToString())
                    .ToList();
                if (addresses.Count == 0)
                {
                    continue;
                }
                var hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any)
                    && !g.Address.Equals(IPAddress.None));
                adapters.Add(new AdapterInfo(
                    nic.Name,
                    nic.Description,
                    isUp,
                    IsLikelyVirtualOrVpn(nic.Description, nic.Name, IsTypeVirtual(nic)),
                    addresses,
                    hasGateway,
                    RankOf(nic.NetworkInterfaceType)));
            }
            catch
            {
                // A single flaky/disconnected adapter (common with VPN clients and docking
                // stations) must not abort discovery or crash the app at startup.
            }
        }
        return SelectCandidates(adapters, port);
    }

    private static bool IsTypeVirtual(NetworkInterface nic)
        => nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel;

    /// <summary>Lower is better: the two interface types people actually use come first.</summary>
    private static int RankOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Wireless80211 => 0,
        NetworkInterfaceType.Unknown => 2,
        _ => 1,
    };

    /// <summary>First usable candidate, or null when the machine has no LAN address.</summary>
    public static LanCandidate? Primary(in IReadOnlyList<LanCandidate> candidates)
        => candidates.Count > 0 ? candidates[0] : null;

    public static LanCandidate? DiscoverPrimary(int port = 8787)
        => Primary(DiscoverCandidates(port));
}

/// <summary>Plain adapter snapshot (input to the pure selector, OS-boundaries provide it).</summary>
public sealed record AdapterInfo(
    string Name,
    string Description,
    bool IsUp,
    bool IsVirtual,
    IEnumerable<string> IPv4Addresses,
    bool HasGateway = false,
    int TypeRank = 1);
