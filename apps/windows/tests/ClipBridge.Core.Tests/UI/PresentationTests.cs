using ClipBridge.Core.Models;
using ClipBridge.Core.Status;
using ClipBridge.Win.UI;
using Xunit;

namespace ClipBridge.Core.Tests.UI;

public class PresentationTests
{
    internal static AppStatusSnapshot Healthy => new() { App = AppState.Running, Relay = RelayState.Running,
        Sync = SyncState.Active, Network = NetworkState.LanAvailable, LanUrl = "http://192.168.1.14:8787" };

    [Theory]
    [InlineData(RelayState.Starting, SyncState.Active, "Starting…")]
    [InlineData(RelayState.Restarting, SyncState.Active, "Relay restarting…")]
    [InlineData(RelayState.Failed, SyncState.Active, "Offline")]
    [InlineData(RelayState.Running, SyncState.Offline, "Offline")]
    [InlineData(RelayState.Running, SyncState.Paused, "Sync paused")]
    [InlineData(RelayState.Running, SyncState.Active, "Connected")]
    public void StatusMatchesObservedState(RelayState relay, SyncState sync, string title)
    {
        var view = StatusPresentation.From(Healthy with { Relay = relay, Sync = sync, LastError = "private internal exception" });
        Assert.Equal(title, view.Title);
        Assert.DoesNotContain("private", view.Explanation);
        Assert.DoesNotContain("iPhone Connected", view.Title);
    }
    [Fact] public void MissingLanNeverClaimsConnection() => Assert.False(StatusPresentation.From(Healthy with { LanUrl = null }).Connected);
    [Fact] public void NoLanHasActionableLabel() => Assert.Equal("No local network", StatusPresentation.From(Healthy with { Network = NetworkState.NoLan }).Title);
    [Fact] public void MdnsConflictDoesNotInvalidateWorkingIp() => Assert.True(StatusPresentation.From(Healthy with { Mdns = MdnsState.Conflict }).Connected);
    [Fact] public void StartupCannotClaimActive() => Assert.Equal("Starting…", StatusPresentation.From(new()).Title);

    [Fact]
    public void PreviewIsBoundedAndUnicodeSafe()
    {
        var preview = HistoryPresentation.Preview(new string('x', 109) + "😀\r\n" + new string('z', 2 * 1024 * 1024));
        Assert.EndsWith("😀…", preview); Assert.True(preview.Length <= 112);
        Assert.Equal("one two three", HistoryPresentation.Preview("one\r\n\t two\nthree"));
        Assert.Equal("Whitespace only", HistoryPresentation.Preview("\r\n\t"));
    }
    [Fact]
    public void StoredHistoryDoesNotImplyWindowsDelivery()
    {
        Assert.Equal("iPhone → Relay", HistoryPresentation.Route(Clip("private content"), "windows-main"));
        Assert.Equal("other-device", HistoryPresentation.Source("other-device", "windows-main"));
        Assert.Equal("Windows", HistoryPresentation.Source("custom-pc", "custom-pc"));
    }
    internal static ClipRecord Clip(string text) => new() { ClipId = Guid.NewGuid().ToString(), DeviceId = "iphone",
        ContentType = "text/plain", Content = text, ContentHash = "test", CreatedAt = "2026-09-03T08:00:00Z",
        ReceivedAt = "2026-09-03T08:00:00Z", Origin = "shortcut", SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text) };

    [Theory] [InlineData(ThemeChoice.System)] [InlineData(ThemeChoice.Dark)] [InlineData(ThemeChoice.Light)]
    public void AppearanceRoundTripsSeparatelyFromCoreConfiguration(ThemeChoice theme)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cb-theme-{Guid.NewGuid():N}.json");
        try { var store = new AppearanceStore(path); Assert.Equal(ThemeChoice.System, store.Load()); store.Save(theme); Assert.Equal(theme, store.Load()); }
        finally { File.Delete(path); }
    }
    [Fact]
    public void InvalidAppearanceFallsBackToSystem()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cb-theme-{Guid.NewGuid():N}.json");
        try { File.WriteAllText(path, "{\"Theme\":\"900\"}"); Assert.Equal(ThemeChoice.System, new AppearanceStore(path).Load()); }
        finally { File.Delete(path); }
    }
}
