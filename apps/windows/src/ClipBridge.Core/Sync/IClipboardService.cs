namespace ClipBridge.Core.Sync;

/// <summary>
/// Platform-neutral clipboard access. The Windows host implements this on top of
/// the WinForms Clipboard API (STA + hidden-window marshalling).
/// </summary>
public interface IClipboardService
{
    /// <summary>Current clipboard text, or null when it holds no text / is unavailable.</summary>
    string? TryGetText();

    /// <summary>Set the clipboard to the given text. Implementations retry bounded times on busy.</summary>
    void SetText(string text);
}
