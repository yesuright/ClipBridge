using ClipBridge.Core.Status;
using Xunit;

namespace ClipBridge.Core.Tests.Status;

public class AppStatusModelTests
{
    [Fact]
    public void Snapshot_DefaultsToStarting()
    {
        var model = new AppStatusModel();
        var s = model.Snapshot;
        Assert.Equal(AppState.Starting, s.App);
        Assert.Equal(RelayState.Stopped, s.Relay);
        Assert.Null(s.LanUrl);
    }

    [Fact]
    public void Update_FiresChangedEvent_OnlyOnActualChange()
    {
        var model = new AppStatusModel();
        var count = 0;
        model.Changed += _ => count++;

        model.Patch(s => s.With(AppState.Running).WithRelay(RelayState.Running));
        Assert.Equal(1, count); // one patch = one changed snapshot

        model.Patch(s => s.With(AppState.Running).WithRelay(RelayState.Running));
        Assert.Equal(1, count); // no change → no event
    }

    [Fact]
    public void Patch_ModifiesOnlyGivenFields()
    {
        var model = new AppStatusModel();
        model.Patch(s => s.With(AppState.Running).WithLanUrl("http://192.168.1.14:8787"));
        model.Patch(s => s.WithSync(SyncState.Paused));

        var s = model.Snapshot;
        Assert.Equal(AppState.Running, s.App);
        Assert.Equal("http://192.168.1.14:8787", s.LanUrl);
        Assert.Equal(SyncState.Paused, s.Sync);
        Assert.Equal(RelayState.Stopped, s.Relay); // untouched
    }

    [Fact]
    public void LastSync_RecordsDirectionSize()
    {
        var model = new AppStatusModel();
        var at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        model.Patch(s => s.WithLastSync(at, "iphone", 1234, "apply"));
        var s = model.Snapshot;
        Assert.Equal(at, s.LastSyncAtUtc);
        Assert.Equal("iphone", s.LastSyncDeviceId);
        Assert.Equal(1234, s.LastSyncBytes);
        Assert.Equal("apply", s.LastSyncDirection);
    }

    [Fact]
    public void Snapshot_IsImmutableRecords()
    {
        var model = new AppStatusModel();
        var before = model.Snapshot;
        model.Patch(s => s.With(AppState.Running));
        Assert.Equal(AppState.Starting, before.App); // previous snapshot unchanged
    }
}
