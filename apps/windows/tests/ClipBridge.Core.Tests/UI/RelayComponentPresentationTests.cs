using ClipBridge.Core.Status;
using ClipBridge.Win.UI;
using Xunit;

namespace ClipBridge.Core.Tests.UI;

public class RelayComponentPresentationTests
{
    [Fact]
    public void MissingRelayComponent_IsNotPresentedAsConnected()
    {
        var snapshot = new AppStatusSnapshot
        {
            App = AppState.Running,
            Relay = RelayState.Disabled,
            Sync = SyncState.Offline,
            Network = NetworkState.LanAvailable,
            LanUrl = "http://192.168.1.14:8787",
            LastError = "relay_component_missing",
        };

        var presentation = StatusPresentation.From(snapshot);

        Assert.False(presentation.Connected);
        Assert.Equal("Relay component missing", presentation.Title);
        Assert.Contains("relay", presentation.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthySnapshot_IsStillPresentedAsConnected()
    {
        var presentation = StatusPresentation.From(new AppStatusSnapshot
        {
            App = AppState.Running,
            Relay = RelayState.Running,
            Sync = SyncState.Active,
            Network = NetworkState.LanAvailable,
            LanUrl = "http://192.168.1.14:8787",
        });

        Assert.True(presentation.Connected);
        Assert.Equal("Connected", presentation.Title);
    }
}
