namespace ClipBridge.Core.Sync;

/// <summary>
/// Guard for WM_CLIPBOARDUPDATE reads (reliability fix, M4.1).
///
/// Windows can deliver the clipboard-update notification BEFORE the data of a
/// delayed-rendering source (browsers, Electron apps, terminals, apps that keep an
/// OLE data object alive) is actually readable. In that window the first read is
/// either:
///   - empty   (the UnicodeText format is advertised but the payload is not rendered
///              yet — WinForms GetText then returns ""), or
///   - null    (the clipboard is still owned/flushed by the copying app and the read
///              fails with a transient ExternalException).
/// Publishing an empty clip makes it the newest record and poisons
/// `GET /clips/latest` for the iPhone until the real text arrives, and dropping a
/// busy read silently loses the user's copy. The real logs showed exactly this:
/// `clip_uploaded ... size=0 hash=e3b0c442` minutes before the actual size-N upload.
///
/// The guard therefore introduces ONE bounded deferred re-read per event burst
/// (host schedules it after a small grace period; the host timer/thread must be the
/// STA thread that can touch the clipboard):
///   - confident read (non-null, non-empty)             -> delivered immediately
///   - empty/none read                                  -> one deferred re-read
///   - deferred re-read becomes confident                -> delivered (dedup-safe: the
///     engine suppresses the identical hash if the strays also deliver it)
///   - deferred re-read STILL empty/none                 -> skipped with an explicit
///     reason (`clipboard_skipped reason=empty_read|unreadable`) — never published,
///     so an empty clip can no longer shadow the real content
///
/// Loop prevention is untouched: this guard only decides WHAT text is handed to the
/// engine, and the engine's hash/clip-id suppression already deduplicates strays.
/// </summary>
public sealed class ClipboardReadGuard
{
    /// <summary>Skip reason: a text format was advertised but the payload never rendered.</summary>
    public const string ReasonEmptyRead = "empty_read";

    /// <summary>Skip reason: the clipboard was busy/empty at read time and stayed so.</summary>
    public const string ReasonUnreadable = "unreadable";

    private readonly IClipboardService _clipboard;
    private readonly Action<string> _deliver;
    private readonly Action<string> _skip;
    private readonly Action<Action, TimeSpan> _schedule;
    private int _deferredScheduled;

    public ClipboardReadGuard(
        IClipboardService clipboard,
        Action<string> deliver,
        Action<string> skip,
        Action<Action, TimeSpan> schedule)
    {
        _clipboard = clipboard;
        _deliver = deliver;
        _skip = skip;
        _schedule = schedule;
    }

    /// <summary>A read we trust: non-null and non-empty.</summary>
    public static bool IsConfident(string? text) => text is { Length: > 0 };

    /// <summary>
    /// Process one clipboard-update notification: read the current text and either
    /// deliver it, or schedule the single deferred re-read that covers this event burst.
    /// </summary>
    public void OnClipboardEvent(TimeSpan grace)
    {
        var text = _clipboard.TryGetText();
        if (IsConfident(text))
        {
            _deliver(text!);
            return;
        }
        ScheduleDeferred(grace);
    }

    private void ScheduleDeferred(TimeSpan grace)
    {
        if (Interlocked.CompareExchange(ref _deferredScheduled, 1, 0) != 0)
        {
            return; // one deferred re-read is already pending for this burst
        }
        _schedule(DeferredRead, grace);
    }

    private void DeferredRead()
    {
        Interlocked.Exchange(ref _deferredScheduled, 0);
        var text = _clipboard.TryGetText();
        if (IsConfident(text))
        {
            _deliver(text!);
            return;
        }
        // Never publish a read we could not verify: an empty clip would shadow the
        // real content on `GET /clips/latest` for the iPhone.
        _skip(text is null ? ReasonUnreadable : ReasonEmptyRead);
    }
}
