using ClipBridge.Core.Api;
using ClipBridge.Core.Config;
using ClipBridge.Core.Models;
using ClipBridge.Core.Sync;
using Xunit;

namespace ClipBridge.Core.Tests.Sync;

/// <summary>
/// The real 2026-09-10 log had ~15 identical `WARN poll_failed error=http_error:0` lines
/// every minute for as long as the relay was down (tens of thousands of lines in total),
/// which buried every lifecycle line that actually explained the outage. Identical
/// consecutive poll failures are now logged once and then rate-limited; recovery is
/// always logged, and a *different* error is still reported immediately.
/// </summary>
public class PollFailureLoggingTests
{
    private sealed class PollFailingRelay : IRelayClient
    {
        /// <summary>Exception the next poll should throw (null = succeed).</summary>
        public Func<Exception>? Failure;

        public Task<ClipRecord> PostClipAsync(ClipRecord clip, CancellationToken ct)
            => Task.FromResult(clip);

        public Task<ClipRecord?> GetLatestAsync(CancellationToken ct) => Task.FromResult<ClipRecord?>(null);

        public Task<IReadOnlyList<ClipRecord>> GetRecentAsync(int limit, CancellationToken ct)
        {
            var failure = Failure;
            return failure is null
                ? Task.FromResult<IReadOnlyList<ClipRecord>>(Array.Empty<ClipRecord>())
                : Task.FromException<IReadOnlyList<ClipRecord>>(failure());
        }
    }

    private static ClientConfig Config() => new()
    {
        RelayUrl = "http://127.0.0.1:8787",
        DeviceId = "windows-main",
        DeviceToken = "test-token",
        PollIntervalMs = 2000,
    };

    private static IEnumerable<string> PollLines(CapturingLogger log)
        => log.Lines.Where(l => l.Contains("poll_failed") || l.Contains("poll_recovered"));

    [Fact]
    public async Task IdenticalRepeatedPollFailures_AreLoggedOnceThenRateLimited()
    {
        var relay = new PollFailingRelay { Failure = () => new HttpRequestException("connection refused") };
        var log = new CapturingLogger();
        var now = new DateTime(2026, 9, 10, 7, 51, 28, DateTimeKind.Utc);
        using var engine = new SyncEngine(new FakeClipboard(), relay, Config(), log, () => now);

        for (var i = 0; i < 15; i++) // 15 polls inside the same minute (the real cadence)
        {
            await engine.CheckForRemoteAsync();
        }

        var lines = PollLines(log).ToList();
        Assert.Single(lines);
        Assert.Contains("http_error:0", lines[0]);
        Assert.StartsWith("WARN", lines[0]);

        // Still the same error one minute later: one rate-limited heartbeat, not 15 lines.
        now = now.AddSeconds(61);
        for (var i = 0; i < 15; i++)
        {
            await engine.CheckForRemoteAsync();
        }
        lines = PollLines(log).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("repeats=", lines[1]);
    }

    [Fact]
    public async Task ADifferentPollFailure_IsReportedImmediately()
    {
        var relay = new PollFailingRelay { Failure = () => new HttpRequestException("connection refused") };
        var log = new CapturingLogger();
        var now = new DateTime(2026, 9, 10, 7, 51, 28, DateTimeKind.Utc);
        using var engine = new SyncEngine(new FakeClipboard(), relay, Config(), log, () => now);

        await engine.CheckForRemoteAsync();
        await engine.CheckForRemoteAsync();

        relay.Failure = () => new RelayApiException(502, null, "<html>bad gateway</html>");
        await engine.CheckForRemoteAsync();

        var lines = PollLines(log).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("http_error:0", lines[0]);
        Assert.Contains("relay_502", lines[1]);
    }

    [Fact]
    public async Task RecoveryAfterPollFailures_IsLoggedExactlyOnce()
    {
        var relay = new PollFailingRelay { Failure = () => new HttpRequestException("connection refused") };
        var log = new CapturingLogger();
        var now = new DateTime(2026, 9, 10, 7, 51, 28, DateTimeKind.Utc);
        using var engine = new SyncEngine(new FakeClipboard(), relay, Config(), log, () => now);

        for (var i = 0; i < 5; i++)
        {
            await engine.CheckForRemoteAsync();
        }
        Assert.Single(PollLines(log));

        relay.Failure = null; // the relay came back
        await engine.CheckForRemoteAsync();
        await engine.CheckForRemoteAsync();
        await engine.CheckForRemoteAsync();

        var lines = PollLines(log).ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("INFO", lines[1]);
        Assert.Contains("poll_recovered", lines[1]);
        Assert.Contains("failed_polls=5", lines[1]);
    }

    [Fact]
    public async Task HealthyPolling_LogsNothing()
    {
        var relay = new PollFailingRelay();
        var log = new CapturingLogger();
        using var engine = new SyncEngine(new FakeClipboard(), relay, Config(), log, () => DateTime.UtcNow);

        for (var i = 0; i < 10; i++)
        {
            await engine.CheckForRemoteAsync();
        }

        Assert.Empty(PollLines(log));
    }
}
