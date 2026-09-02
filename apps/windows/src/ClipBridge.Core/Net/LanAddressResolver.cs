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
/// (Milestone 3 .NET port of the Milestone 2 PowerShell heuristics in scripts/lib/LanSetup.psm1):
/// - private RFC-1918 unicast IPv4 only (no loopback/APIPA/public/multicast)
/// - excludes VPN tunnels and virtual adapters by description/name keywords and OS virtual flag
/// - real (physical) adapters are preferred; candidates ordered by priority
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
    /// Input shape (mirrors the M2 PowerShell module):
    ///   Name, Description, IsUp(bool), IsVirtual(bool), IPv4Addresses(IEnumerable&lt;string&gt;)
    /// Returns candidates sorted real-first, then by name, then address.
    /// </summary>
    public static IReadOnlyList<LanCandidate> SelectCandidates(
        IEnumerable<AdapterInfo> adapters, int port = 8787)
    {
        var result = new List<LanCandidate>();
        foreach (var adapter in adapters)
        {
            if (!adapter.IsUp) continue;
            foreach (var raw in adapter.IPv4Addresses.Distinct())
            {
                var ip = raw.Trim();
                if (!IsPrivateIpv4(ip)) continue;
                if (IsLikelyVirtualOrVpn(adapter.Description, adapter.Name, adapter.IsVirtual)) continue;
                result.Add(new LanCandidate(adapter.Name, adapter.Description, ip, adapter.IsVirtual, port));
            }
        }
        return result
            .OrderBy(c => c.IsVirtual)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.IpAddress, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Enumerate the real Windows interfaces and run the same filter.</summary>
    public static IReadOnlyList<LanCandidate> DiscoverCandidates(int port = 8787)
    {
        var adapters = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue; // loopback + tunnel interfaces are never LAN candidates
            }
            var isUp = nic.OperationalStatus == OperationalStatus.Up;
            var addresses = nic.GetIPProperties().UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address.ToString());
            adapters.Add(new AdapterInfo(
                nic.Name,
                nic.Description,
                isUp,
                IsLikelyVirtualOrVpn(nic.Description, nic.Name, IsVirtualType(nic)),
                addresses));
        }
        return SelectCandidates(adapters, port);
    }

    private static bool IsVirtualType(NetworkInterface nic)
        => nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel;

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
    IEnumerable<string> IPv4Addresses);
