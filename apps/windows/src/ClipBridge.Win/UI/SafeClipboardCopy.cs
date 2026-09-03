using ClipBridge.Core.Sync;
using System.Runtime.InteropServices;

namespace ClipBridge.Win.UI;

/// <summary>UI writes use M3's one-shot suppression. All entry points run on the host STA.</summary>
public sealed class SafeClipboardCopy(SyncEngine engine, IClipboardService clipboard, Func<uint>? sequence = null)
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    private readonly Func<uint> _sequence = sequence ?? GetClipboardSequenceNumber;
    private uint? _copySequence;

    public bool TryCopy(string text)
    {
        Interlocked.Increment(ref engine.SuppressNextLocalEvents);
        try { clipboard.SetText(text); _copySequence = _sequence(); return true; }
        catch
        {
            ConsumeOne(); // a failed write will not produce its expected notification
            return false;
        }
    }

    public Task OnClipboardChangedAsync(string? text)
    {
        if (_copySequence is { } written)
        {
            if (_sequence() == written)
            {
                // Windows can deliver multiple notifications for the SAME write.
                // Reuse the engine's existing suppression for each such notification;
                // a fresh user write has a new OS sequence even if its text is equal.
                Interlocked.CompareExchange(ref engine.SuppressNextLocalEvents, 1, 0);
            }
            else
            {
                _copySequence = null;
                Interlocked.Exchange(ref engine.SuppressNextLocalEvents, 0);
            }
        }
        // M3 returns early while paused, before consuming host suppression. Consume
        // the UI notification here so the next normal copy after Resume is unaffected.
        if (engine.Paused) ConsumeOne();
        return engine.OnClipboardChangedAsync(text);
    }

    private void ConsumeOne()
    {
        var count = engine.SuppressNextLocalEvents;
        while (count > 0)
        {
            var previous = Interlocked.CompareExchange(ref engine.SuppressNextLocalEvents, count - 1, count);
            if (previous == count) return;
            count = previous;
        }
    }
}
