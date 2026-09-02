using ClipBridge.Core.Net;
using Xunit;

namespace ClipBridge.Core.Tests.Net;

public class LanAddressResolverTests
{
    // ---------------------------------------------------------------- private IP logic

    [Theory]
    [InlineData("192.168.1.14", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]     // outside 172.16/12
    [InlineData("127.0.0.1", false)]      // loopback
    [InlineData("169.254.10.20", false)]  // APIPA/link-local
    [InlineData("224.0.0.251", false)]    // multicast
    [InlineData("8.8.8.8", false)]        // public
    [InlineData("999.1.1.1", false)]      // malformed
    [InlineData("", false)]
    [InlineData("192.168.1.14:8787", false)] // not an IP
    public void IsPrivateIpv4_MatchesRfc1918(string ip, bool expected)
        => Assert.Equal(expected, LanAddressResolver.IsPrivateIpv4(ip));

    // ---------------------------------------------------------------- virtual/vpn heuristic

    [Theory]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter 2", "Wi-Fi", false, true)]
    [InlineData("Realtek 8821CE Wireless LAN 802.11ac PCI-E NIC", "WLAN", false, false)]
    [InlineData("TAP-Windows Adapter V9", "Ethernet", false, true)]
    [InlineData("Fortinet Virtual Adapter (WireGuard)", "VPN", false, true)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", "vEthernet", false, true)]
    [InlineData("Intel(R) Ethernet Connection (2)", "Ethernet", false, false)]
    [InlineData("", "Wi-Fi", true, true)]   // OS marks it virtual
    public void IsLikelyVirtualOrVpn_Heuristic(string description, string name, bool isVirtual, bool expected)
        => Assert.Equal(expected, LanAddressResolver.IsLikelyVirtualOrVpn(description, name, isVirtual));

    // ---------------------------------------------------------------- selection

    private static AdapterInfo Adapter(string name, string desc, bool up, bool virtual_, params string[] ips)
        => new(name, desc, up, virtual_, ips);

    [Fact]
    public void SelectCandidates_KeepsOnlyUpPrivatePhysical()
    {
        var candidates = LanAddressResolver.SelectCandidates(new[]
        {
            Adapter("WLAN", "Realtek 8821CE Wireless LAN", true, false, "192.168.1.14"),
            Adapter("WLAN", "Realtek 8821CE Wireless LAN", true, false, "8.8.8.8"),        // public → dropped
            Adapter("Ethernet", "Intel Ethernet", false, false, "192.168.1.22"),           // down → dropped
            Adapter("VPN", "WireGuard", true, false, "10.66.0.2"),                          // vpn → dropped
            Adapter("vEthernet", "Hyper-V Virtual Ethernet Adapter", true, false, "172.20.0.7"), // virtual → dropped
            Adapter("APIPA", "Intel Ethernet", true, false, "169.254.1.2"),                // apipa → dropped
        });
        var only = Assert.Single(candidates);
        Assert.Equal("192.168.1.14", only.IpAddress);
        Assert.Equal("http://192.168.1.14:8787", only.Url);
    }

    [Fact]
    public void SelectCandidates_MultiHomed_ReturnsMultipleSorted()
    {
        var candidates = LanAddressResolver.SelectCandidates(new[]
        {
            Adapter("Ethernet", "Intel Ethernet", true, false, "192.168.1.22"),
            Adapter("WLAN", "Realtek Wireless", true, false, "192.168.1.14", "10.13.37.5"),
        });
        // sorted by name (Ethernet < WLAN), then address lexicographically within an adapter
        Assert.Equal(3, candidates.Count);
        Assert.Equal("Ethernet", candidates[0].Name);
        Assert.Equal("192.168.1.22", candidates[0].IpAddress);
        Assert.Equal("WLAN", candidates[1].Name);
        Assert.Equal("10.13.37.5", candidates[1].IpAddress);
        Assert.Equal("WLAN", candidates[2].Name);
        Assert.Equal("192.168.1.14", candidates[2].IpAddress);
    }

    [Fact]
    public void SelectCandidates_Empty_WhenNoLan()
        => Assert.Empty(LanAddressResolver.SelectCandidates(new[]
        {
            Adapter("VPN", "WireGuard Tunnel", true, false, "10.66.0.2"),
            Adapter("WLAN", "Realtek Wireless", true, false, "169.254.1.2"),
        }));

    [Fact]
    public void DiscoverCandidates_LiveSmoke_DoesNotThrow()
    {
        // Live enumeration must never throw and must only return well-formed URLs.
        var candidates = LanAddressResolver.DiscoverCandidates();
        foreach (var c in candidates)
        {
            Assert.Contains("http://", c.Url);
            Assert.True(LanAddressResolver.IsPrivateIpv4(c.IpAddress));
        }
    }
}
