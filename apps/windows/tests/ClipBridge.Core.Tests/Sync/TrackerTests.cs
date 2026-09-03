using ClipBridge.Core.Caching;
using ClipBridge.Core.Sync;
using Xunit;

namespace ClipBridge.Core.Tests;

public sealed class MutableClock
{
    public DateTime Now { get; set; } = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public DateTime UtcNow() => Now;
    public void Advance(TimeSpan span) => Now = Now.Add(span);
}

public class RecentHashCacheTests
{
    [Fact]
    public void AddAndContains()
    {
        var cache = new RecentHashCache(TimeSpan.FromMinutes(5));
        cache.Add("a");
        Assert.True(cache.Contains("a"));
        Assert.False(cache.Contains("b"));
    }

    [Fact]
    public void ExpiresAfterTtl()
    {
        var clock = new MutableClock();
        var cache = new RecentHashCache(TimeSpan.FromSeconds(30), clock: clock.UtcNow);
        cache.Add("key");
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(cache.Contains("key"));
    }

    [Fact]
    public void NotExpiredWithinTtl()
    {
        var clock = new MutableClock();
        var cache = new RecentHashCache(TimeSpan.FromSeconds(30), clock: clock.UtcNow);
        cache.Add("key");
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(cache.Contains("key"));
    }

    [Fact]
    public void EvictsOldestBeyondCapacity()
    {
        var cache = new RecentHashCache(TimeSpan.FromMinutes(10), capacity: 2);
        cache.Add("a");
        cache.Add("b");
        cache.Add("c");
        Assert.False(cache.Contains("a"));
        Assert.True(cache.Contains("b"));
        Assert.True(cache.Contains("c"));
    }
}

public class AppliedRemoteTrackerTests
{
    [Fact]
    public void SuppressesAppliedHashWithinWindow()
    {
        var tracker = new AppliedRemoteTracker(TimeSpan.FromSeconds(30));
        tracker.MarkApplied("clip-1", "hash-1");
        Assert.True(tracker.IsRecentlyApplied("hash-1"));
        Assert.True(tracker.WasApplied("clip-1"));
        Assert.False(tracker.IsRecentlyApplied("hash-2"));
        Assert.False(tracker.WasApplied("clip-2"));
    }

    [Fact]
    public void HashSuppressionExpiresAfterWindow_ButClipIdIsSessionLong()
    {
        // The user's deliberate re-copy of the same CONTENT is allowed after the window
        // (hash check), but the SAME remote clip must never be re-applied this session
        // (clip-id check) — the old TTL-bounded clip-id memory re-wrote the clipboard
        // every ~30 s and clobbered the user's newest copy.
        var clock = new MutableClock();
        var tracker = new AppliedRemoteTracker(TimeSpan.FromSeconds(30), clock.UtcNow);
        tracker.MarkApplied("clip-1", "hash-1");
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(tracker.IsRecentlyApplied("hash-1"));   // re-copy allowed
        Assert.True(tracker.WasApplied("clip-1"));           // same remote clip: still applied
    }

    [Fact]
    public void WasApplied_StillTrueLongAfterWindow()
    {
        var clock = new MutableClock();
        var tracker = new AppliedRemoteTracker(TimeSpan.FromSeconds(30), clock.UtcNow);
        tracker.MarkApplied("clip-old", "hash-old");
        clock.Advance(TimeSpan.FromHours(6));
        Assert.True(tracker.WasApplied("clip-old")); // regression: stale re-apply every 30s
    }

    [Fact]
    public void DistinctClipIds_AreIndependent()
    {
        var tracker = new AppliedRemoteTracker(TimeSpan.FromSeconds(30));
        tracker.MarkApplied("clip-a", "hash-x");
        Assert.False(tracker.WasApplied("clip-b"));
        Assert.False(tracker.IsRecentlyApplied("hash-y"));
    }
}

public class UploadedEchoTrackerTests
{
    [Fact]
    public void DetectsOwnEchoByClipId()
    {
        var tracker = new UploadedEchoTracker(TimeSpan.FromMinutes(15));
        tracker.MarkUploaded("clip-42");
        Assert.True(tracker.IsOwnEcho("clip-42"));
        Assert.False(tracker.IsOwnEcho("clip-43"));
    }

    [Fact]
    public void RemembersNothingBeforeMark()
    {
        var tracker = new UploadedEchoTracker(TimeSpan.FromMinutes(15));
        Assert.False(tracker.IsOwnEcho("clip-42"));
    }

    [Fact]
    public void TracksRecentUploadHash()
    {
        var tracker = new UploadedEchoTracker(TimeSpan.FromMinutes(15));
        tracker.MarkUploaded("clip-42", "hash-A");
        Assert.True(tracker.IsRecentUpload("hash-A"));
        Assert.False(tracker.IsRecentUpload("hash-B"));
    }
}
