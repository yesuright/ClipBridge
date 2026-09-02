using ClipBridge.Core.Config;
using ClipBridge.Core.Sync;
using Xunit;

namespace ClipBridge.Core.Tests.Sync;

public class SyncEnginePauseAndSuppressionTests
{
    private static SyncEngine CreateEngine(FakeClipboard clipboard, FakeRelay relay, ClientConfig config)
        => new(clipboard, relay, config);

    private static ClientConfig Config() => new()
    {
        RelayUrl = "http://127.0.0.1:8787",
        DeviceId = "windows-main",
        DeviceToken = "test-token",
        PollIntervalMs = 2000,
        SuppressWindowSeconds = 30,
        UploadMemorySeconds = 900,
    };

    [Fact]
    public async Task OneShotSuppression_SkipsExactlyNextLocalEvent()
    {
        var clipboard = new FakeClipboard();
        var relay = new FakeRelay();
        using var engine = CreateEngine(clipboard, relay, Config());

        engine.SuppressNextLocalEvents = 1;
        await engine.OnClipboardChangedAsync("the LAN url copy");

        Assert.Equal(0, relay.PostCount); // suppressed, not uploaded

        await engine.OnClipboardChangedAsync("real user copy");
        Assert.Equal(1, relay.PostCount); // next event flows normally
        Assert.Equal("real user copy", relay.Uploaded[0].Content);
    }

    [Fact]
    public async Task Paused_DoesNotUpload_OrApplyRemote()
    {
        var clipboard = new FakeClipboard();
        var relay = new FakeRelay();
        var config = Config();
        using var engine = CreateEngine(clipboard, relay, config);

        engine.Paused = true;

        await engine.OnClipboardChangedAsync("while paused");
        Assert.Equal(0, relay.PostCount);

        var applied = await engine.CheckForRemoteAsync();
        Assert.False(applied);
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task Resume_WorksAfterPause()
    {
        var clipboard = new FakeClipboard();
        var relay = new FakeRelay();
        using var engine = CreateEngine(clipboard, relay, Config());

        engine.Paused = true;
        await engine.OnClipboardChangedAsync("paused copy");
        engine.Paused = false;
        await engine.OnClipboardChangedAsync("after resume");

        Assert.Single(relay.Uploaded);
        Assert.Equal("after resume", relay.Uploaded[0].Content);
    }
}
