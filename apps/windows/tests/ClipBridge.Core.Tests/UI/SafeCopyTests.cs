using ClipBridge.Core.Config;
using ClipBridge.Core.Sync;
using ClipBridge.Core.Tests.Sync;
using ClipBridge.Win.UI;
using Xunit;

namespace ClipBridge.Core.Tests.UI;

public class SafeCopyTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AppCopyAndDuplicateNotificationsNeverUpload_AndNextUserCopyStillWorks(bool paused)
    {
        var clipboard = new FakeClipboard(); var relay = new FakeRelay(); var logger = new CapturingLogger();
        using var engine = new SyncEngine(clipboard, relay, new ClientConfig { DeviceToken = "test-token", DeviceId = "windows-main" }, logger);
        engine.Paused = paused;
        uint sequence = 5;
        var copy = new SafeClipboardCopy(engine, clipboard, () => sequence);
        Assert.True(copy.TryCopy("sensitive original\r\n中文😀\ttext"));
        await copy.OnClipboardChangedAsync(clipboard.Text);
        await copy.OnClipboardChangedAsync(clipboard.Text);
        Assert.Empty(relay.Uploaded);
        Assert.Equal(0, engine.SuppressNextLocalEvents);
        engine.Paused = false; sequence++;
        await copy.OnClipboardChangedAsync("sensitive original\r\n中文😀\ttext");
        Assert.Single(relay.Uploaded); // deliberate recopy of equal text is a fresh OS write
        Assert.DoesNotContain(logger.Lines, line => line.Contains("sensitive original"));
    }

    [Fact]
    public async Task FailedCopyDoesNotSwallowTheNextUserCopy()
    {
        var clipboard = new FakeClipboard { SetFailuresRemaining = 1 }; var relay = new FakeRelay();
        using var engine = new SyncEngine(clipboard, relay, new ClientConfig { DeviceToken = "test-token" });
        var copy = new SafeClipboardCopy(engine, clipboard, () => 10);
        Assert.False(copy.TryCopy("address")); Assert.Equal(0, engine.SuppressNextLocalEvents);
        await copy.OnClipboardChangedAsync("new user text"); Assert.Single(relay.Uploaded);
    }

    [Fact]
    public async Task MissingAppNotificationDoesNotSwallowNewSequence()
    {
        var clipboard = new FakeClipboard(); var relay = new FakeRelay();
        using var engine = new SyncEngine(clipboard, relay, new ClientConfig { DeviceToken = "test-token" });
        uint sequence = 8; var copy = new SafeClipboardCopy(engine, clipboard, () => sequence);
        copy.TryCopy("address"); sequence++;
        await copy.OnClipboardChangedAsync("new user text"); Assert.Single(relay.Uploaded);
    }
}
