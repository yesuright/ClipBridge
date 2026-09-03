using ClipBridge.Core.Sync;
using Xunit;

namespace ClipBridge.Core.Tests.Sync;

public class ClipboardReadGuardTests
{
    private sealed class RecordedClipboard : IClipboardService
    {
        public string? Text = "start";
        public string? TryGetText() => Text;
        public void SetText(string text) => Text = text;
    }

    private sealed class ImmediateScheduler
    {
        public readonly List<Action> Pending = new();
        public TimeSpan LastGrace;
        public void Schedule(Action callback, TimeSpan grace)
        {
            Pending.Add(callback);
            LastGrace = grace;
        }

        public void FireAll()
        {
            var all = Pending.ToArray();
            Pending.Clear();
            foreach (var a in all) a();
        }
    }

    private static (ClipboardReadGuard Guard, RecordedClipboard Clip, ImmediateScheduler Scheduler,
        List<string> Delivered, List<string> Skipped) Create()
    {
        var clip = new RecordedClipboard();
        var scheduler = new ImmediateScheduler();
        var delivered = new List<string>();
        var skipped = new List<string>();
        var guard = new ClipboardReadGuard(clip, delivered.Add, skipped.Add, scheduler.Schedule);
        return (guard, clip, scheduler, delivered, skipped);
    }

    [Fact]
    public void ConfidentRead_IsDeliveredImmediately()
    {
        var (guard, clip, _, delivered, skipped) = Create();
        clip.Text = "real user copy";
        guard.OnClipboardEvent(TimeSpan.FromMilliseconds(300));

        Assert.Single(delivered);
        Assert.Equal("real user copy", delivered[0]);
        Assert.Empty(skipped);
    }

    [Fact]
    public void DeferredReadThatFindsContent_IsDelivered()
    {
        // delayed-rendering source: first read empty, payload renders 250 ms later
        var (guard, clip, scheduler, delivered, skipped) = Create();
        clip.Text = "";
        guard.OnClipboardEvent(TimeSpan.FromMilliseconds(300));
        Assert.Empty(delivered); // never publishes the unverified empty read

        clip.Text = "payload now available";
        scheduler.FireAll();
        Assert.Single(delivered);
        Assert.Equal("payload now available", delivered[0]);
        Assert.Empty(skipped);
        Assert.Equal(TimeSpan.FromMilliseconds(300), scheduler.LastGrace);
    }

    [Fact]
    public void DeferredReadStillEmpty_IsSkippedWithReason_NeverPublished()
    {
        var (guard, clip, scheduler, delivered, skipped) = Create();
        clip.Text = "";
        guard.OnClipboardEvent(TimeSpan.FromMilliseconds(300));
        scheduler.FireAll();

        Assert.Empty(delivered);
        Assert.Single(skipped);
        Assert.Equal(ClipboardReadGuard.ReasonEmptyRead, skipped[0]);
    }

    [Fact]
    public void DeferredReadStillNull_IsSkippedAsUnreadable()
    {
        var (guard, clip, scheduler, _, skipped) = Create();
        clip.Text = null; // clipboard busy / no text
        guard.OnClipboardEvent(TimeSpan.FromMilliseconds(300));
        scheduler.FireAll();

        Assert.Single(skipped);
        Assert.Equal(ClipboardReadGuard.ReasonUnreadable, skipped[0]);
    }

    [Fact]
    public void BurstOfUncertainEvents_SchedulesExactlyOneDeferredRead()
    {
        // Windows can deliver several notifications for one delayed write; all of them
        // must coalesce into a SINGLE bounded re-read (no re-read per straggler).
        var (guard, clip, scheduler, delivered, skipped) = Create();
        clip.Text = "";
        guard.OnClipboardEvent(TimeSpan.FromMilliseconds(300));
        guard.OnClipboardEvent(TimeSpan.FromMilliseconds(300));
        guard.OnClipboardEvent(TimeSpan.FromMilliseconds(300));
        Assert.Single(scheduler.Pending);

        clip.Text = "finally rendered";
        scheduler.FireAll();
        Assert.Single(delivered);
        Assert.Empty(skipped);
    }

    [Fact]
    public void ConfidentEventAfterDeferred_ServesTheNewestContent()
    {
        // A genuinely new copy made while the deferred re-read was pending must win:
        // the guard always re-reads the CURRENT clipboard at delivery time.
        var (guard, clip, scheduler, delivered, _) = Create();
        clip.Text = "";
        guard.OnClipboardEvent(TimeSpan.FromMilliseconds(300));
        clip.Text = "the newest copy wins";
        scheduler.FireAll();

        Assert.Single(delivered);
        Assert.Equal("the newest copy wins", delivered[0]);
    }
}
