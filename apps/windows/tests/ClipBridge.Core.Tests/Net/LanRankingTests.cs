using ClipBridge.Core.Net;
using ClipBridge.Core.Process;
using Xunit;

namespace ClipBridge.Core.Tests.Net;

/// <summary>
/// Multi-homed machines (VPN + Wi-Fi + Hyper-V + dock) are the norm, so the "first adapter"
/// assumption is wrong. These tests pin the ranking rules and the explicit override.
/// </summary>
public class LanRankingTests
{
    [Fact]
    public void AdapterWithDefaultGateway_RanksAboveAnIsolatedOne()
    {
        var candidates = LanAddressResolver.SelectCandidates(new[]
        {
            // Alphabetically first, but this is an isolated lab network with no gateway.
            new AdapterInfo("LAN-Adapter", "Intel Ethernet", true, false, new[] { "192.168.99.5" }, HasGateway: false, TypeRank: 0),
            new AdapterInfo("Wi-Fi", "Intel Wireless", true, false, new[] { "192.168.1.20" }, HasGateway: true, TypeRank: 0),
        });

        Assert.Equal("192.168.1.20", candidates[0].IpAddress);
    }

    [Fact]
    public void EthernetAndWifi_RankAboveExoticInterfaceTypes()
    {
        var candidates = LanAddressResolver.SelectCandidates(new[]
        {
            new AdapterInfo("PPP", "Point to Point", true, false, new[] { "10.1.1.1" }, HasGateway: true, TypeRank: 1),
            new AdapterInfo("Wi-Fi", "Wireless", true, false, new[] { "10.1.1.2" }, HasGateway: true, TypeRank: 0),
        });

        Assert.Equal("10.1.1.2", candidates[0].IpAddress);
    }

    [Fact]
    public void PreferIp_MovesTheConfiguredAddressFirst()
    {
        var candidates = LanAddressResolver.SelectCandidates(new[]
        {
            new AdapterInfo("Wi-Fi", "Wireless", true, false, new[] { "192.168.1.20" }, true, 0),
            new AdapterInfo("Ethernet", "Intel", true, false, new[] { "192.168.1.21" }, true, 0),
        });

        var preferred = LanAddressResolver.PreferIp(candidates, "192.168.1.21");
        Assert.Equal("192.168.1.21", preferred[0].IpAddress);
    }

    [Fact]
    public void PreferIp_IsIgnored_WhenTheAddressIsGone()
    {
        var candidates = LanAddressResolver.SelectCandidates(new[]
        {
            new AdapterInfo("Wi-Fi", "Wireless", true, false, new[] { "192.168.1.20" }, true, 0),
        });

        var preferred = LanAddressResolver.PreferIp(candidates, "192.168.1.99");
        Assert.Equal("192.168.1.20", preferred[0].IpAddress);
        Assert.Single(preferred);
    }

    [Fact]
    public void PreferIp_HandlesEmptyInput()
    {
        Assert.Empty(LanAddressResolver.PreferIp(Array.Empty<LanCandidate>(), "192.168.1.5"));
        Assert.Empty(LanAddressResolver.PreferIp(Array.Empty<LanCandidate>(), null));
    }

    [Fact]
    public void DiscoverCandidates_NeverThrows()
    {
        var exception = Record.Exception(() => LanAddressResolver.DiscoverCandidates(8787));
        Assert.Null(exception);
    }

    // ---------------------------------------------------------------- port resolution

    [Fact]
    public void PortResolution_KeepsAFreePort()
    {
        var result = RelayPortResolution.Resolve(8787, _ => true, _ => false);
        Assert.Equal(8787, result.Port);
        Assert.False(result.Changed);
        Assert.True(result.Resolved);
    }

    [Fact]
    public void PortResolution_KeepsAPortHeldByAnExistingClipBridgeRelay()
    {
        var result = RelayPortResolution.Resolve(8787, p => p != 8787, p => p == 8787);
        Assert.Equal(8787, result.Port);
        Assert.False(result.Changed);
        Assert.True(result.Resolved);
    }

    [Fact]
    public void PortResolution_MovesWhenAnotherProgramHoldsThePort()
    {
        var result = RelayPortResolution.Resolve(
            8787,
            p => p is not (8787 or 8788),
            _ => false);

        Assert.Equal(8789, result.Port);
        Assert.True(result.Changed);
        Assert.True(result.Resolved);
        Assert.Contains("8787", result.Reason);
    }

    [Fact]
    public void PortResolution_ReportsFailure_WhenNothingIsFree()
    {
        var result = RelayPortResolution.Resolve(8787, _ => false, _ => false, scanAttempts: 3);
        Assert.False(result.Resolved);
        Assert.False(result.Changed);
        Assert.Contains("no free port", result.Reason);
    }

    [Fact]
    public void PortResolution_RejectsInvalidPorts()
    {
        Assert.False(RelayPortResolution.Resolve(0, _ => true, _ => false).Resolved);
        Assert.False(RelayPortResolution.Resolve(70000, _ => true, _ => false).Resolved);
    }

    [Fact]
    public void PortResolution_DoesNotWrapPast65535()
    {
        var result = RelayPortResolution.Resolve(65535, _ => false, _ => false, scanAttempts: 5);
        Assert.False(result.Resolved);
    }

    [Fact]
    public void IsBindable_DetectsARealListener()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(PortProbe.IsBindable(port));
        Assert.True(PortProbe.IsLoopbackPortOpen(port));
    }
}
