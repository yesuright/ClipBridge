namespace ClipBridge.Win;

/// <summary>
/// Hidden message window that surfaces WM_CLIPBOARDUPDATE and WM_POWERBROADCAST to the
/// tray context. Invisible (opacity 0, not in taskbar, off-screen). Requires a message
/// loop, which Application.Run on the STA main thread provides.
/// </summary>
public sealed class HiddenWindow : Form
{
    private readonly TrayContext _owner;

    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_APMRESUMESUSPEND = 0x0007;

    public HiddenWindow(TrayContext owner)
    {
        _owner = owner;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = new Size(1, 1);
        WindowState = FormWindowState.Minimized;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Text = "ClipBridge";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            _owner.OnClipboardUpdate();
            return;
        }
        if (message.Msg == WM_POWERBROADCAST)
        {
            var wParam = unchecked((int)message.WParam.ToInt64());
            if (wParam == PBT_APMRESUMEAUTOMATIC || wParam == PBT_APMRESUMESUSPEND)
            {
                _owner.OnResumeFromSleep();
            }
            return;
        }
        base.WndProc(ref message);
    }

    protected override void SetVisibleCore(bool value)
        => base.SetVisibleCore(false); // never show, even via Show()
}
