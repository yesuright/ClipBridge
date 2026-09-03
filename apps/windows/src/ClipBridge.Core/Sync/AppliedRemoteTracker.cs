using ClipBridge.Core.Caching;

namespace ClipBridge.Core.Sync;

/// <summary>
/// Tracks clips this client applied to its own clipboard from the relay.
/// Used by the clipboard-change listener to suppress the "echo" event that our own
/// SetText produces — the core loop-prevention layer.
/// </summary>
public sealed class AppliedRemoteTracker
{
    private readonly RecentHashCache _recentHashes;   // suppress upload of applied content (TTL)
    private readonly RecentHashCache _recentClipIds;  // avoid re-applying the same clip (session)

    public AppliedRemoteTracker(TimeSpan window, Func<DateTime>? clock = null)
    {
        _recentHashes = new RecentHashCache(window, capacity: 512, clock: clock);
        // Clip ids are remembered for the WHOLE session (capacity-bounded), not for the
        // suppression window: the same remote clip must never be re-applied after its
        // suppression window expires (the old behaviour re-wrote the clipboard every ~30 s,
        // clobbering the user's newest copy). The user's own deliberate re-copy of the same
        // CONTENT is still allowed: that check is hash-based and stays TTL-bounded.
        _recentClipIds = new RecentHashCache(ttl: null, capacity: 512, clock: clock);
    }

    /// <summary>Record a successful remote apply (must be called BEFORE SetText).</summary>
    public void MarkApplied(string clipId, string contentHash)
    {
        _recentClipIds.Add(clipId);
        _recentHashes.Add(contentHash);
    }

    /// <summary>True when this content was just applied by us — suppress the echo upload.</summary>
    public bool IsRecentlyApplied(string contentHash) => _recentHashes.Contains(contentHash);

    /// <summary>True when this remote clip was already applied within the window.</summary>
    public bool WasApplied(string clipId) => _recentClipIds.Contains(clipId);
}
