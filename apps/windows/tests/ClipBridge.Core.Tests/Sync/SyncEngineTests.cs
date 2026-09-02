using System.Net;
using System.Runtime.InteropServices;
using ClipBridge.Core.Api;
using ClipBridge.Core.Config;
using ClipBridge.Core.Hashing;
using ClipBridge.Core.Logging;
using ClipBridge.Core.Models;
using ClipBridge.Core.Sync;
using Xunit;

namespace ClipBridge.Core.Tests.Sync;

public sealed class FakeClipboard : IClipboardService
{
    public string? Text;
    public int SetCount;
    public int SetFailuresRemaining;
    public Exception? SetFailure;

    public string? TryGetText() => Text;

    public void SetText(string text)
    {
        if (SetFailuresRemaining > 0)
        {
            SetFailuresRemaining--;
            throw SetFailure ?? new ExternalException("OpenClipboard failed");
        }
        Text = text;
        SetCount++;
    }
}

public sealed class FakeRelay : IRelayClient
{
    public readonly List<ClipRecord> Uploaded = new();
    public List<ClipRecord> History = new();
    public bool FailNextPost;
    public HttpStatusCode FailWith = HttpStatusCode.InternalServerError;
    public int PostCount;
    public int RecentCount;

    public Task<ClipRecord> PostClipAsync(ClipRecord clip, CancellationToken ct)
    {
        PostCount++;
        if (FailNextPost)
        {
            FailNextPost = false;
            throw new HttpRequestException("simulated network failure");
        }
        Uploaded.Add(clip);
        return Task.FromResult(clip);
    }

    public Task<ClipRecord?> GetLatestAsync(CancellationToken ct)
        => Task.FromResult(History.LastOrDefault());

    public Task<IReadOnlyList<ClipRecord>> GetRecentAsync(int limit, CancellationToken ct)
    {
        RecentCount++;
        return Task.FromResult<IReadOnlyList<ClipRecord>>(History.Take(limit).ToList());
    }
}

public sealed class CapturingLogger : IClientLogger
{
    public readonly List<string> Lines = new();
    public void Info(string m) => Lines.Add($"INFO {m}");
    public void Warn(string m) => Lines.Add($"WARN {m}");
    public void Error(string m, Exception? x = null) => Lines.Add($"ERROR {m}");
    public void Debug(string m) => Lines.Add($"DEBUG {m}");
}

public class SyncEngineTests : IDisposable
{
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeRelay _relay = new();
    private readonly CapturingLogger _logger = new();
    private readonly ClientConfig _config = new()
    {
        RelayUrl = "http://127.0.0.1:8787",
        DeviceId = "windows-main",
        DeviceToken = "test-token",
        PollIntervalMs = 2000,
        SuppressWindowSeconds = 30,
        UploadMemorySeconds = 900,
    };
    private readonly SyncEngine _engine;

    public SyncEngineTests()
    {
        _engine = new SyncEngine(_clipboard, _relay, _config, _logger);
    }

    private static ClipRecord RemoteClip(string content, string clipId, string device = "iphone")
        => new()
        {
            ClipId = clipId,
            DeviceId = device,
            ContentType = ClipContentType.TextPlain,
            Content = content,
            ContentHash = ContentHasher.Sha256Hex(content),
            CreatedAt = "2025-06-01T12:00:00.000Z",
            ReceivedAt = "2025-06-01T12:00:00.000Z",
            Origin = "local",
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(content),
        };

    public void Dispose() => _engine.Dispose();

    // ---------------------------------------------------------------- upload

    [Fact]
    public async Task UserCopy_UploadsWithProtocolFields()
    {
        await _engine.OnClipboardChangedAsync("Hello from ClipBridge");

        Assert.Single(_relay.Uploaded);
        var clip = _relay.Uploaded[0];
        Assert.Equal("Hello from ClipBridge", clip.Content);
        Assert.Equal("windows-main", clip.DeviceId);
        Assert.Equal(ClipContentType.TextPlain, clip.ContentType);
        Assert.Equal(ContentHasher.Sha256Hex("Hello from ClipBridge"), clip.ContentHash);
        Assert.Equal(21, clip.SizeBytes);
        Assert.Equal("local", clip.Origin);
        Assert.True(Guid.TryParse(clip.ClipId, out _), "clip_id must be a generated UUID");
        Assert.EndsWith("Z", clip.CreatedAt);
        Assert.True(DateTimeOffset.TryParse(clip.CreatedAt, out _));
    }

    [Fact]
    public async Task IdenticalRecopyWithinRelayWindow_IsSkippedClientSide()
    {
        // The relay would reject the same identical content within its own dedup
        // window anyway (409), so the client skips the redundant round-trip.
        await _engine.OnClipboardChangedAsync("copy once");
        Assert.Equal(1, _relay.PostCount);

        await _engine.OnClipboardChangedAsync("copy once");
        Assert.Equal(1, _relay.PostCount);
        Assert.Contains(_logger.Lines, l => l.Contains("suppressed recent upload"));
    }

    [Fact]
    public async Task NullText_IsIgnored()
    {
        await _engine.OnClipboardChangedAsync(null);
        Assert.Empty(_relay.Uploaded);
    }

    [Fact]
    public async Task EmptyText_IsUploaded()
    {
        await _engine.OnClipboardChangedAsync("");
        Assert.Single(_relay.Uploaded);
    }

    [Fact]
    public async Task TooLargeContent_IsSkippedWithoutCrash()
    {
        var huge = new string('x', ClipContentType.MaxContentBytes + 1);
        await _engine.OnClipboardChangedAsync(huge);
        Assert.Empty(_relay.Uploaded);
        Assert.Contains(_logger.Lines, l => l.Contains("clip_too_large"));
    }

    [Fact]
    public async Task UserCopiesSameTextTwice_TwoDistinctEvents()
    {
        // Deliberate re-copy must NOT be swallowed by dedup.
        await _engine.OnClipboardChangedAsync("repeat me");

        var clock = new MutableClock();
        var engine2 = new SyncEngine(_clipboard, _relay, _config, _logger, clock.UtcNow);
        clock.Advance(TimeSpan.FromSeconds(31)); // outside the suppression window
        await engine2.OnClipboardChangedAsync("repeat me");

        Assert.Equal(2, _relay.Uploaded.Count);
        Assert.NotEqual(_relay.Uploaded[0].ClipId, _relay.Uploaded[1].ClipId);
        engine2.Dispose();
    }

    [Fact]
    public async Task NetworkFailure_DoesNotCrash_AndRecovers()
    {
        _relay.FailNextPost = true;
        await _engine.OnClipboardChangedAsync("A");
        Assert.Empty(_relay.Uploaded);
        Assert.NotNull(_engine.LastError);

        await _engine.OnClipboardChangedAsync("B");
        Assert.Single(_relay.Uploaded);
        Assert.Equal("B", _relay.Uploaded[0].Content);
    }

    [Fact]
    public async Task Paused_DoesNothing()
    {
        _engine.Paused = true;
        await _engine.OnClipboardChangedAsync("A");
        Assert.Empty(_relay.Uploaded);

        _relay.History = new List<ClipRecord> { RemoteClip("R", "clip-r") };
        var applied = await _engine.CheckForRemoteAsync();
        Assert.False(applied);
        Assert.Equal(0, _clipboard.SetCount);
    }

    // ---------------------------------------------------------------- receive

    [Fact]
    public async Task RemoteClip_IsWrittenToClipboard()
    {
        _relay.History = new List<ClipRecord> { RemoteClip("Hello from iPhone", "clip-remote-1") };
        var applied = await _engine.CheckForRemoteAsync();

        Assert.True(applied);
        Assert.Equal("Hello from iPhone", _clipboard.Text);
        Assert.Equal(1, _clipboard.SetCount);
    }

    [Fact]
    public async Task RemoteFromOwnDevice_IsSkipped()
    {
        _relay.History = new List<ClipRecord>
        {
            RemoteClip("A", "clip-w1", device: "windows-main"),
        };
        var applied = await _engine.CheckForRemoteAsync();
        Assert.False(applied);
        Assert.Equal(0, _clipboard.SetCount);
    }

    // ------------------------------------------------- Milestone 2: device routing

    [Fact]
    public async Task IphonePost_IsAppliedToWindowsClipboard()
    {
        _relay.History = new List<ClipRecord> { RemoteClip("Hello from iPhone", "clip-ip-1", device: "iphone") };
        var applied = await _engine.CheckForRemoteAsync();
        Assert.True(applied);
        Assert.Equal("Hello from iPhone", _clipboard.Text);
        Assert.Equal(1, _clipboard.SetCount);
    }

    [Fact]
    public async Task IpadPost_IsAppliedToWindowsClipboard()
    {
        _relay.History = new List<ClipRecord> { RemoteClip("Hello from iPad", "clip-pad-1", device: "ipad") };
        var applied = await _engine.CheckForRemoteAsync();
        Assert.True(applied);
        Assert.Equal("Hello from iPad", _clipboard.Text);
        Assert.Equal(1, _clipboard.SetCount);
    }

    [Fact]
    public async Task OwnDeviceClipInFront_RemoteStillSelected()
    {
        // Newest record is our own device (filtered), next newest is iphone -> iphone applies.
        var own = RemoteClip("own", "clip-own-1", device: "windows-main") with
        { ReceivedAt = "2025-06-01T12:00:20.000Z" };
        var iphone = RemoteClip("from iphone", "clip-ip-2", device: "iphone") with
        { ReceivedAt = "2025-06-01T12:00:10.000Z" };
        _relay.History = new List<ClipRecord> { own, iphone };

        var applied = await _engine.CheckForRemoteAsync();
        Assert.True(applied);
        Assert.Equal("from iphone", _clipboard.Text);
    }

    [Fact]
    public async Task NewestRemoteAmongSeveralDevices_Wins()
    {
        var ipad = RemoteClip("from ipad", "clip-pad-2", device: "ipad") with
        { ReceivedAt = "2025-06-01T12:00:15.000Z" };
        var iphone = RemoteClip("from iphone", "clip-ip-3", device: "iphone") with
        { ReceivedAt = "2025-06-01T12:00:10.000Z" };
        var own = RemoteClip("own", "clip-own-2", device: "windows-main") with
        { ReceivedAt = "2025-06-01T12:00:30.000Z" };
        _relay.History = new List<ClipRecord> { own, ipad, iphone };

        var applied = await _engine.CheckForRemoteAsync();
        Assert.True(applied);
        Assert.Equal("from ipad", _clipboard.Text); // newest remote, oldest listed last
    }

    [Fact]
    public async Task CustomDeviceIds_AreHandledWithoutHardcoding()
    {
        // Device ids are free-form; the engine must not assume "iphone"/"ipad" strings.
        var tony = RemoteClip("tony clipboard", "clip-tony-1", device: "tony-iphone");
        _relay.History = new List<ClipRecord> { tony };
        var applied = await _engine.CheckForRemoteAsync();
        Assert.True(applied);
        Assert.Equal("tony clipboard", _clipboard.Text);
        Assert.Equal("windows-main", _config.DeviceId);
    }

    [Fact]
    public async Task CorruptRemoteHash_IsSkipped()
    {
        var clip = RemoteClip("tampered", "clip-x") with { ContentHash = "0".PadLeft(64, '0') };
        _relay.History = new List<ClipRecord> { clip };
        var applied = await _engine.CheckForRemoteAsync();
        Assert.False(applied);
        Assert.Equal(0, _clipboard.SetCount);
    }

    [Fact]
    public async Task AlreadyApplied_IsNotAppliedTwice()
    {
        _relay.History = new List<ClipRecord> { RemoteClip("once", "clip-1") };
        Assert.True(await _engine.CheckForRemoteAsync());
        Assert.False(await _engine.CheckForRemoteAsync()); // same clip, already applied
        Assert.Equal(1, _clipboard.SetCount);
    }

    [Fact]
    public async Task ClipboardAlreadyHoldsRemoteText_IsNotReapplied()
    {
        _clipboard.Text = "same text";
        _relay.History = new List<ClipRecord> { RemoteClip("same text", "clip-2") };
        var applied = await _engine.CheckForRemoteAsync();
        Assert.False(applied);
        Assert.Equal(0, _clipboard.SetCount);
    }

    [Fact]
    public async Task ClipboardBusy_RetriesThenSucceeds()
    {
        _clipboard.SetFailuresRemaining = 2;
        _relay.History = new List<ClipRecord> { RemoteClip("retry me", "clip-3") };
        var applied = await _engine.CheckForRemoteAsync();
        Assert.True(applied);
        Assert.Equal("retry me", _clipboard.Text);
        Assert.Equal(1, _clipboard.SetCount);
    }

    [Fact]
    public async Task ClipboardBusy_KeepsFailing_NoCrash()
    {
        _clipboard.SetFailuresRemaining = int.MaxValue;
        _relay.History = new List<ClipRecord> { RemoteClip("never lands", "clip-4") };
        try
        {
            var applied = await _engine.CheckForRemoteAsync();
            Assert.False(applied);
            Assert.Contains(_logger.Lines, l => l.Contains("clipboard_apply_failed"));
        }
        finally
        {
            _clipboard.SetFailuresRemaining = 0;
        }
    }

    // ---------------------------------------------------------------- loop prevention

    [Fact]
    public async Task LoopPrevention_RemoteApplyEcho_IsNotReuploaded()
    {
        _relay.History = new List<ClipRecord> { RemoteClip("Hello from iPhone", "clip-remote-2") };
        await _engine.CheckForRemoteAsync();
        Assert.Empty(_relay.Uploaded);

        // Our own SetText fires the clipboard listener with the SAME text.
        // The echo must be suppressed — this is the core loop-prevention behavior.
        await _engine.OnClipboardChangedAsync("Hello from iPhone");

        Assert.Empty(_relay.Uploaded);
        Assert.Equal("Hello from iPhone", _clipboard.Text);
    }

    [Fact]
    public async Task LoopPrevention_OwnUploadEcho_IsNotApplied()
    {
        // 1. user copies text → engine uploads clip c1
        await _engine.OnClipboardChangedAsync("my text");
        Assert.Single(_relay.Uploaded);
        var c1 = _relay.Uploaded[0];

        // 2. the relay returns our own upload back in history (echo)
        _relay.History = new List<ClipRecord> { c1 };

        // 3. poll must NOT write it to the clipboard again
        var applied = await _engine.CheckForRemoteAsync();
        Assert.False(applied);
        Assert.Null(_clipboard.Text);
        Assert.False(_engine.Paused);
    }

    [Fact]
    public async Task LoopPrevention_SuppressionExpires_AllowsDeliberateRecopy()
    {
        var clock = new MutableClock();
        using var engine2 = new SyncEngine(_clipboard, _relay, _config, _logger, clock.UtcNow);

        _relay.History = new List<ClipRecord> { RemoteClip("re-copy me", "clip-5") };
        Assert.True(await engine2.CheckForRemoteAsync());
        Assert.Empty(_relay.Uploaded);

        // echo suppressed
        await engine2.OnClipboardChangedAsync("re-copy me");
        Assert.Empty(_relay.Uploaded);

        // 31s later the user deliberately re-copies the same text: it uploads again
        clock.Advance(TimeSpan.FromSeconds(31));
        await engine2.OnClipboardChangedAsync("re-copy me");
        Assert.Single(_relay.Uploaded);
    }

    [Fact]
    public async Task NewRemoteClip_AfterOldOne_IsApplied()
    {
        var first = RemoteClip("first", "clip-6a");
        var second = RemoteClip("second", "clip-6b");
        _relay.History = new List<ClipRecord> { first };
        Assert.True(await _engine.CheckForRemoteAsync());

        _relay.History = new List<ClipRecord> { second, first };
        Assert.True(await _engine.CheckForRemoteAsync());
        Assert.Equal("second", _clipboard.Text);
    }

    [Fact]
    public async Task OlderRemoteClips_AreNotAppliedAfterNewest()
    {
        var newest = RemoteClip("newest", "clip-9a") with { ReceivedAt = "2025-06-01T12:00:10.000Z" };
        var older = RemoteClip("older", "clip-9b") with { ReceivedAt = "2025-06-01T12:00:05.000Z" };
        _relay.History = new List<ClipRecord> { newest, older };

        Assert.True(await _engine.CheckForRemoteAsync());
        Assert.Equal("newest", _clipboard.Text);

        // Next poll with the same history: the older clip must NOT cascade into
        // the clipboard (we already synced up to the newest remote clip).
        Assert.False(await _engine.CheckForRemoteAsync());
        Assert.False(await _engine.CheckForRemoteAsync());
        Assert.Equal(1, _clipboard.SetCount);
        Assert.Equal("newest", _clipboard.Text);
    }

    [Fact]
    public async Task PollFailure_IsContained_AndRecovers()
    {
        var failing = new FailingGetRelay(_relay, failures: 1);
        using var engine2 = new SyncEngine(_clipboard, failing, _config, _logger);

        var applied = await engine2.CheckForRemoteAsync();
        Assert.False(applied);
        Assert.NotNull(engine2.LastError);

        // recovery on the same engine once the relay is healthy again
        _relay.History = new List<ClipRecord> { RemoteClip("after recovery", "clip-7") };
        var recovered = await engine2.CheckForRemoteAsync();
        Assert.True(recovered);
        Assert.Equal("after recovery", _clipboard.Text);
    }

    private sealed class FailingGetRelay : IRelayClient
    {
        private readonly IRelayClient _inner;
        private int _failuresRemaining;

        public FailingGetRelay(IRelayClient inner, int failures)
        {
            _inner = inner;
            _failuresRemaining = failures;
        }

        private void MaybeFail()
        {
            if (_failuresRemaining-- > 0)
            {
                throw new HttpRequestException("simulated GET failure");
            }
        }

        public Task<ClipRecord> PostClipAsync(ClipRecord clip, CancellationToken ct)
            => _inner.PostClipAsync(clip, ct);

        public Task<ClipRecord?> GetLatestAsync(CancellationToken ct)
        {
            MaybeFail();
            return _inner.GetLatestAsync(ct);
        }

        public Task<IReadOnlyList<ClipRecord>> GetRecentAsync(int limit, CancellationToken ct)
        {
            MaybeFail();
            return _inner.GetRecentAsync(limit, ct);
        }
    }
}
