using System.Runtime.InteropServices;
using ClipBridge.Core.Sync;

namespace ClipBridge.Win;

/// <summary>
/// WinForms clipboard adapter (STA).
/// - TryGetText: safe read, fails soft when the clipboard is busy (no crash).
/// - SetText: marshalled to the STA UI thread (hidden form Invoke) so OLE clipboard
///   access always happens on the correct thread.
/// </summary>
public sealed class WinFormsClipboardService : IClipboardService
{
    private readonly Control _invoker;

    public WinFormsClipboardService(Control invoker) => _invoker = invoker;

    public string? TryGetText()
    {
        try
        {
            return Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText)
                : null;
        }
        catch (ExternalException)
        {
            // Clipboard locked by another process: report "no text" and keep running.
            return null;
        }
        catch (ThreadStateException)
        {
            // Not on an STA thread: skip (host always calls from the UI thread).
            return null;
        }
    }

    public void SetText(string text)
    {
        if (_invoker.InvokeRequired)
        {
            _invoker.Invoke(() => SetText(text));
            return;
        }
        Clipboard.SetText(text, TextDataFormat.UnicodeText);
    }
}
