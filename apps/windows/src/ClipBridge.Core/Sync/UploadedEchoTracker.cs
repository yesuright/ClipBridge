using ClipBridge.Core.Caching;

namespace ClipBridge.Core.Sync;

/// <summary>
/// Remembers clips this client uploaded, so poll results can exclude our own
/// echoes (a clip we uploaded comes back with OUR clip_id — a perfect discriminator)
/// and repeated clipboard notifications for the same copy can be ignored while the
/// relay's dedup window is still active.
/// </summary>
public sealed class UploadedEchoTracker
{
    private readonly RecentHashCache _clipIds;
    private readonly RecentHashCache _contentHashes;

    public UploadedEchoTracker(TimeSpan window, Func<DateTime>? clock = null)
    {
        _clipIds = new RecentHashCache(window, capacity: 1024, clock: clock);
        _contentHashes = new RecentHashCache(window, capacity: 1024, clock: clock);
    }

    public void MarkUploaded(string clipId) => MarkUploaded(clipId, null);

    public void MarkUploaded(string clipId, string? contentHash)
    {
        _clipIds.Add(clipId);
        if (contentHash is not null)
        {
            _contentHashes.Add(contentHash);
        }
    }

    public bool IsOwnEcho(string clipId) => _clipIds.Contains(clipId);

    /// <summary>
    /// True when this exact content was uploaded within the memory window.
    /// The relay rejects the same identical re-post inside its own dedup window,
    /// so skipping here loses no user-visible event.
    /// </summary>
    public bool IsRecentUpload(string contentHash) => _contentHashes.Contains(contentHash);
}
