using ClipBridge.Core.Api;
using ClipBridge.Core.Config;
using ClipBridge.Core.Logging;
using ClipBridge.Core.Models;
using ClipBridge.Core.Sync;

namespace ClipBridge.Win;

/// <summary>
/// Minimal functional tray shell for Milestone 1:
/// hidden window + clipboard listener + 2s polling + tray menu
/// (toggle sync, check now, open log folder, exit).
/// Deliberately no custom visuals — that is Codex's future scope.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly ClientConfig _config;
    private readonly IClientLogger _log;
    private readonly HiddenWindow _hidden;
    private readonly WinFormsClipboardService _clipboard;
    private readonly SyncEngine _engine;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _statusItem;

    public TrayContext(ClientConfig config, IClientLogger logger)
    {
        _config = config;
        _log = logger;

        _hidden = new HiddenWindow(this);
        // Force native window handle creation so AddClipboardFormatListener is
        // actually registered (accessing Handle creates the message window).
        _ = _hidden.Handle;
        _clipboard = new WinFormsClipboardService(_hidden);
        _engine = new SyncEngine(_clipboard,
                                 new RelayClient(config),
                                 config,
                                 logger);
        _engine.ClipApplied += clip => Notify($"♪ Received from {clip.DeviceId} ({clip.SizeBytes} bytes)");
        _engine.ClipUploaded += clip => Notify($"♪ Copied to relay ({clip.SizeBytes} bytes)");

        _statusItem = new ToolStripMenuItem($"ClipBridge — syncing every {config.PollIntervalMs} ms");
        _toggleItem = new ToolStripMenuItem("Sync enabled", null, (_, _) => ToggleSync())
        {
            Checked = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Check now", null, (_, _) => CheckNow()));
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _trayIcon = new NotifyIcon
        {
            // System icon only — custom icon design is Codex's future scope.
            Icon = SystemIcons.Application,
            Text = "ClipBridge",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _log.Info($"started device={config.DeviceId} relay={config.RelayUrl}");
        _engine.StartPolling();
    }

    /// <summary>Called from the hidden window's WndProc (STA UI thread).</summary>
    public void OnClipboardUpdate()
    {
        var text = _clipboard.TryGetText();
        _ = _engine.OnClipboardChangedAsync(text);
    }

    private void ToggleSync()
    {
        _engine.Paused = !_engine.Paused;
        _toggleItem.Checked = !_engine.Paused;
        _statusItem.Text = _engine.Paused
            ? "ClipBridge — sync paused"
            : $"ClipBridge — syncing every {_config.PollIntervalMs} ms";
        _log.Info(_engine.Paused ? "sync_paused" : "sync_resumed");
    }

    private void CheckNow()
    {
        _ = _engine.CheckNowAsync();
    }

    private static void OpenLogFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               "ClipBridge");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void Notify(string text)
    {
        try
        {
            _trayIcon.ShowBalloonTip(1500, "ClipBridge", text, ToolTipIcon.Info);
        }
        catch
        {
            // Balloon tips are best-effort; never let them take the app down.
        }
    }

    protected override void ExitThreadCore()
    {
        _log.Info("stopping");
        _engine.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _hidden.Dispose();
        base.ExitThreadCore();
    }
}
