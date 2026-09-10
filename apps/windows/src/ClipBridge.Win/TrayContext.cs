using System.Text.Json;
using ClipBridge.Core.Api;
using ClipBridge.Core.Config;
using ClipBridge.Core.Logging;
using ClipBridge.Core.Models;
using ClipBridge.Core.Net;
using ClipBridge.Core.Process;
using ClipBridge.Core.Startup;
using ClipBridge.Core.Status;
using ClipBridge.Core.Sync;
using ClipBridge.Win.UI;
using Appearance = ClipBridge.Win.UI.Appearance;

namespace ClipBridge.Win;

/// <summary>
/// Tray lifecycle host with an isolated native presentation layer:
/// hidden window + clipboard listener + polling sync engine + managed relay child +
/// network monitoring + mDNS hostname advertisement + startup registration + status model.
/// Every status the user sees flows through <see cref="AppStatusModel"/>; UI never parses logs.
/// The main window is an owned view, not the application lifetime owner.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly ClientConfig _config;
    private readonly IClientLogger _log;
    private readonly AppStatusModel _status;
    private readonly HiddenWindow _hidden;
    private readonly WinFormsClipboardService _clipboard;
    private readonly SyncEngine _engine;
    private readonly HttpRelayAdminClient _relayAdmin;
    private readonly NetworkMonitor _network;
    private readonly MdnsResponder? _mdns;
    private readonly RelayManager? _relayManager;
    private readonly WindowsStartupRegistration _startup;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _appStatusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _copyLanItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly SafeClipboardCopy _safeCopy;
    private readonly ClipboardReadGuard _clipboardGuard;
    private readonly Appearance _appearance;
    private readonly RelayClient _historyClient;
    private readonly Icon _appIcon;
    private System.Windows.Forms.Timer? _deferredClipboardRead;
    private MainWindow? _window;
    private bool _exiting;

    public TrayContext(ClientConfig config, IClientLogger logger, AppStatusModel status, SingleInstanceGuard guard)
    {
        _config = config;
        _log = logger;
        _status = status;

        AppPaths.EnsureDirectories();

        _hidden = new HiddenWindow(this);
        _ = _hidden.Handle; // force handle creation so AddClipboardFormatListener registers
        _clipboard = new WinFormsClipboardService(_hidden);
        _engine = new SyncEngine(_clipboard, new RelayClient(config), config, logger);
        _safeCopy = new SafeClipboardCopy(_engine, _clipboard);
        // Uncertain clipboard reads (delayed-rendering sources) get ONE bounded deferred
        // re-read before publishing: an unverified empty read must never become an empty
        // clip that shadows the real content on the relay (see M4.1 reliability fix).
        _clipboardGuard = new ClipboardReadGuard(
            _clipboard,
            text => _ = _safeCopy.OnClipboardChangedAsync(text),
            reason => _log.Info(
                $"clipboard_skipped reason={reason} sequence={NativeMethods.GetClipboardSequenceNumber()}"),
            ScheduleDeferredClipboardRead);
        _engine.ClipApplied += clip => OnSyncEvent("apply", clip);
        _engine.ClipUploaded += clip => OnSyncEvent("upload", clip);
        _engine.SyncHealthChanged += healthy => _status.Patch(s => s.WithSync(
            _engine.Paused ? SyncState.Paused : (healthy ? SyncState.Active : SyncState.Offline)));

        _relayAdmin = new HttpRelayAdminClient(config.RelayPort, config.DeviceToken);
        _startup = new WindowsStartupRegistration();

        // ------------------------------------------------------------- relay child
        var relayExe = RelayPaths.FindRelayExecutable();
        if (config.ManageRelay && relayExe is not null)
        {
            var spec = new RelayLaunchSpec(
                relayExe,
                config.RelayPort,
                config.DeviceToken,
                AppPaths.DatabaseFile,
                AppPaths.LogsDir);
            _relayManager = new RelayManager(
                spec,
                new DefaultRelayProcessFactory(line => WriteRelayOutputLine(line)),
                _relayAdmin,
                logger);
            _relayManager.StateChanged += OnRelayStateChanged;
        }
        else
        {
            if (config.ManageRelay && relayExe is null)
            {
                logger.Info("relay_external_mode no packaged relay found (dev build?) — using an externally started relay");
            }
            else
            {
                logger.Info("relay_management_disabled configured off — using an externally started relay");
            }
            _relayManager = null;
        }

        // ------------------------------------------------------------- network + mDNS
        _network = new NetworkMonitor(config.RelayPort);
        _network.Changed += OnNetworkChanged;
        if (config.ManageRelay || relayExe is not null)
        {
            _mdns = new MdnsResponder(config.LanHostname, () => _network.Current.Candidates);
            _mdns.Event += OnMdnsEvent;
        }

        // ------------------------------------------------------------- tray menu
        _appearance = new Appearance();
        _historyClient = new RelayClient(config);
        using (var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("ClipBridge.Win.Assets.ClipBridge.ico")!)
            _appIcon = new Icon(stream, new Size(32, 32));
        _appStatusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Pause Sync", null, (_, _) => ToggleSync()) { Checked = false };
        _copyLanItem = new ToolStripMenuItem("Copy iPhone Address", null, (_, _) => CopyLanUrl());
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = _startup.IsEnabled(),
            ToolTipText = "Run quietly when you sign in",
        };

        var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 10), ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(new ToolStripMenuItem("Open ClipBridge", null, (_, _) => OpenWindow()) { Font = new Font("Segoe UI Semibold", 10) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_appStatusItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Check Now", null, (_, _) => CheckNow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_copyLanItem);
        menu.Items.Add(new ToolStripMenuItem("Open Logs", null, (_, _) => OpenLogs()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit ClipBridge", null, (_, _) => RequestExit()));
        foreach (ToolStripItem item in menu.Items) if (item is ToolStripMenuItem) item.Padding = new Padding(6, 5, 12, 5);
        menu.Opening += (_, _) => RefreshStartup();

        _trayIcon = new NotifyIcon
        {
            Icon = new Icon(_appIcon, SystemInformation.SmallIconSize),
            Text = "ClipBridge",
            ContextMenuStrip = menu,
            Visible = config.ShowTrayIcon,
        };
        _trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenWindow(); };
        _trayIcon.DoubleClick += (_, _) => OpenWindow();
        _appearance.Changed += OnAppearanceChanged;
        OnAppearanceChanged();

        // ------------------------------------------------------------- status wiring
        _status.Changed += OnStatusChanged;
        guard.Activated += () => MarshalToUi(OpenWindow);
        guard.ExitRequested += RequestExit;
        guard.CommandHandler = _ => JsonSerializer.Serialize(_status.Snapshot, StatusJson.Options);
        SeedNetworkStatus();

        // ------------------------------------------------------------- start
        // Component isolation: an optional component (mDNS, network, relay) must never
        // take the daily-use app down. Each start is individually guarded.
        try { _engine.StartPolling(); } catch (Exception ex) { logger.Error("engine_start_failed", ex); }
        try { _relayManager?.Start(); } catch (Exception ex) { logger.Error("relay_manager_start_failed", ex); }
        try { _mdns?.Start(); } catch (Exception ex) { logger.Error("mdns_start_failed", ex); }
        RefreshMenuItem();
        _status.Patch(s => s
            .With(AppState.Running)
            .WithStartupEnabled(_startup.IsEnabled()));
        _log.Info($"started device={config.DeviceId} relay={config.RelayUrl} manageRelay={_relayManager is not null}");
        Notify("ClipBridge is running. ", ToolTipIcon.Info);
        if (!Environment.GetCommandLineArgs().Contains("--tray-autostart", StringComparer.OrdinalIgnoreCase))
            MarshalToUi(OpenWindow);
    }

    // ---------------------------------------------------------------- events from hosts

    private void OnRelayStateChanged(RelayState state, string? reason)
    {
        var keepLastError = _status.Snapshot.LastError;
        _status.Patch(s => s.WithRelay(state).WithLastError(
            state == RelayState.Failed ? (reason ?? "relay failed")
            : state == RelayState.Running ? null   // recovered: never keep a stale failure
            : keepLastError));
        switch (state)
        {
            case RelayState.Failed:
                // Balloon once per failure episode. The manager now keeps retrying on its
                // own with an escalating cool-down, so warning on every retry would nag the
                // user forever; later rounds are still visible in the log and in the UI.
                if (_relayManager is null || _relayManager.RecoveryRounds <= 1)
                {
                    MarshalToUi(() => Notify(
                        "Relay failed: " + (reason ?? "unknown") + ". Retrying automatically — check the log folder if it stays offline.",
                        ToolTipIcon.Warning));
                }
                _status.Patch(s => s.WithSync(_engine.Paused ? SyncState.Paused : SyncState.Offline));
                break;
            case RelayState.Running:
                _log.Info($"relay_ready reason={reason ?? "ok"}");
                // Readiness: "Sync: Active" must mean "the relay is up AND answering".
                // Trigger one immediate health probe instead of waiting up to a poll
                // period; the engine flips Active only when this request succeeds.
                _ = ConfirmSyncReadyAsync();
                break;
            case RelayState.Starting:
            case RelayState.Restarting:
            case RelayState.Stopped:
                // A managed relay that is not Running cannot serve uploads/polls.
                // Never leave the UI on a stale "Active" during these transitions.
                _status.Patch(s => s.WithSync(_engine.Paused ? SyncState.Paused : SyncState.Offline));
                if (state == RelayState.Restarting)
                {
                    MarshalToUi(() => Notify("Relay restarted unexpectedly — recovering\u2026", ToolTipIcon.Warning));
                }
                break;
        }
    }

    /// <summary>
    /// Confirm that the relay really answers and publish the resulting sync state.
    /// The engine only raises <c>SyncHealthChanged</c> on a null↔non-null LastError
    /// transition, so a relay that comes up before the poll loop ever fails (or that
    /// recovers faster than one poll period) left the UI stuck on the "Offline" that the
    /// Starting/Restarting handlers publish — while sync was in fact working. Probing
    /// here and publishing "Active" on success makes readiness independent of the
    /// transition history.
    /// </summary>
    private async Task ConfirmSyncReadyAsync()
    {
        try
        {
            await _engine.CheckNowAsync().ConfigureAwait(false);
        }
        catch
        {
            // the engine owns failure handling; lastError stays authoritative
        }
        if (!_engine.Paused && _engine.LastError is null)
        {
            _status.Patch(s => s.WithSync(SyncState.Active));
        }
    }

    private void OnNetworkChanged(NetworkChangedEventArgs e)
    {
        _status.Patch(s =>
        {
            s.WithNetwork(e.LanAvailable ? NetworkState.LanAvailable : NetworkState.NoLan);
            s.WithLanUrl(e.NewPrimaryUrl);
            // mDNS re-announces with the new IP automatically
        });
        if (e.OldPrimaryUrl is not null && e.NewPrimaryUrl is not null
            && !e.OldPrimaryUrl.Equals(e.NewPrimaryUrl, StringComparison.Ordinal))
        {
            _status.Patch(s => s.WithNetwork(NetworkState.IpChanged).WithLanIpChanged(true));
            MarshalToUi(() => Notify(
                $"Network changed — LAN URL is now {e.NewPrimaryUrl} (update the Shortcut if it uses the old IP).",
                ToolTipIcon.Warning));
            _log.Warn($"network_ip_changed old={e.OldPrimaryUrl} new={e.NewPrimaryUrl}");
        }
        else if (!e.LanAvailable)
        {
            _log.Warn("network_lan_lost no private LAN address available");
        }
        else
        {
            _log.Info($"network_ok lan={e.NewPrimaryUrl}");
        }
        _mdns?.Reannounce();
        _ = _engine.CheckNowAsync();
    }

    private void OnMdnsEvent(MdnsResponderEvent kind)
    {
        switch (kind)
        {
            case MdnsResponderEvent.Started:
                _status.Patch(s => s.WithMdns(MdnsState.Advertising).WithLanHostname(_mdns?.FullName));
                _log.Info($"mdns_advertising name={_mdns?.FullName}");
                break;
            case MdnsResponderEvent.Conflict:
                _status.Patch(s => s.WithMdns(MdnsState.Conflict).WithLanHostname(null));
                MarshalToUi(() => Notify(
                    $"Another device already uses {_mdns?.FullName} — IP addresses stay active, try a different hostname in settings.",
                    ToolTipIcon.Warning));
                _log.Warn("mdns_conflict name already in use, advertising stopped");
                break;
            case MdnsResponderEvent.Error:
                _status.Patch(s => s.WithMdns(MdnsState.Errored));
                _log.Warn("mdns_errored responder stopped");
                break;
            case MdnsResponderEvent.Answered:
                _log.Debug("mdns_answered query for the advertised name");
                break;
            case MdnsResponderEvent.SendFailed:
                _log.Warn("mdns_send_failed responder could not deliver an answer");
                break;
        }
    }

    private void SeedNetworkStatus()
    {
        // The network monitor's initial evaluation happens in its constructor, before we
        // subscribe — seed the status model from its current state so the snapshot
        // always has a LAN URL even when no network event arrives afterwards.
        var current = _network.Current;
        _status.Patch(s => s
            .WithNetwork(current.LanAvailable ? NetworkState.LanAvailable : NetworkState.NoLan)
            .WithLanUrl(current.PrimaryUrl));
    }

    private void OnSyncEvent(string direction, ClipRecord clip)
    {
        _status.Patch(s => s.WithLastSync(DateTime.UtcNow, clip.DeviceId, clip.SizeBytes, direction));
        MarshalToUi(() => Notify(
            direction == "apply"
                ? $"Received from {clip.DeviceId} ({clip.SizeBytes} bytes)"
                : $"Copied to relay ({clip.SizeBytes} bytes)",
            ToolTipIcon.Info));
        RefreshMenuItem();
    }

    private void OnStatusChanged(AppStatusSnapshot snapshot) => RefreshMenuItem();

    // ---------------------------------------------------------------- tray actions

    private void ToggleSync()
    {
        _engine.Paused = !_engine.Paused;
        // Resuming must not claim "Active" while the managed relay cannot serve requests:
        // a paused→resumed app whose relay is Failed/Stopped is still Offline. Without this
        // the resume path silently overwrote the Offline state M4.1 sets for a dead relay.
        var relayDown = _relayManager is not null
            && _status.Snapshot.Relay is RelayState.Failed or RelayState.Stopped;
        _status.Patch(s => s.WithSync(
            _engine.Paused ? SyncState.Paused : relayDown ? SyncState.Offline : SyncState.Active));
        _log.Info(_engine.Paused ? "sync_paused" : "sync_resumed");
        Notify(_engine.Paused ? "Sync paused" : "Sync resumed", ToolTipIcon.Info);
    }

    private void CheckNow()
    {
        _ = _engine.CheckNowAsync();
        _log.Debug("check_now manual");
    }

    private void CopyLanUrl()
    {
        var url = _status.Snapshot.LanUrl;
        if (url is null)
        {
            Notify("No LAN address available right now.", ToolTipIcon.Warning);
            return;
        }
        Notify(_safeCopy.TryCopy(url) ? "iPhone address copied." : "Clipboard is busy. Please try again.", ToolTipIcon.Info);
    }

    private void ToggleAutostart()
    {
        var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var wasEnabled = _startup.IsEnabled();
        if (wasEnabled)
        {
            _startup.Disable();
        }
        else
        {
            _startup.Enable(StartupManager.BuildCommandLine(exe));
        }
        _autostartItem.Checked = !wasEnabled;
        _status.Patch(s => s.WithStartupEnabled(_startup.IsEnabled()));
        _log.Info(wasEnabled ? "startup_disabled" : "startup_enabled");
    }

    private void RefreshStartup() => _status.Patch(s => s.WithStartupEnabled(_startup.IsEnabled()));

    private void OpenWindow()
    {
        if (_exiting) return;
        _window ??= new MainWindow(_status.Snapshot, _appearance, _historyClient,
            new UiActions(ToggleSync, () => _engine.CheckNowAsync(), _safeCopy.TryCopy, ToggleAutostart,
                RefreshStartup, OpenLogs, OpenDataFolder, () => _relayManager?.Restart()), _config.DeviceId, _appIcon);
        // The same application menu is also keyboard-accessible from the window
        // (Shift+F10), even when Windows puts the notification icon in its overflow.
        _window.ContextMenuStrip = _trayIcon.ContextMenuStrip;
        _window.OpenAndFocus();
    }

    private void OnAppearanceChanged() => MarshalToUi(() =>
    {
        if (!_exiting) _trayIcon.ContextMenuStrip!.Renderer = new TrayRenderer(_appearance.Colors);
    });

    private static void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        LaunchExplorer(AppPaths.LogsDir);
    }

    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        LaunchExplorer(AppPaths.DataDir);
    }

    private static void LaunchExplorer(string dir)
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void RequestExit()
    {
        MarshalToUi(() =>
        {
            _log.Info("exit_requested");
            ExitThread();
        });
    }

    /// <summary>Called from the hidden window's WndProc (STA UI thread).</summary>
    public void OnClipboardUpdate()
    {
        _log.Debug($"clipboard_event sequence={NativeMethods.GetClipboardSequenceNumber()}");
        _clipboardGuard.OnClipboardEvent(DeferredClipboardReadGrace);
    }

    /// <summary>
    /// One bounded deferred re-read for an uncertain (empty/busy) clipboard read.
    /// The timer is STA (WinForms) because the deferred re-read touches the clipboard.
    /// Multiple uncertain notifications coalesce into a single re-read.
    /// </summary>
    private readonly TimeSpan DeferredClipboardReadGrace = TimeSpan.FromMilliseconds(300);

    private void ScheduleDeferredClipboardRead(Action callback, TimeSpan grace)
    {
        if (_deferredClipboardRead is null)
        {
            _deferredClipboardRead = new System.Windows.Forms.Timer();
            _deferredClipboardRead.Tick += (_, _) =>
            {
                _deferredClipboardRead!.Stop();
                callback(); // ClipboardReadGuard re-reads and delivers/skips (STA here)
            };
        }
        _deferredClipboardRead.Interval = (int)Math.Max(50, grace.TotalMilliseconds);
        _deferredClipboardRead.Stop();
        _deferredClipboardRead.Start();
    }

    /// <summary>WM_POWERBROADCAST resume (sleep→wake): re-check the network and sync immediately.</summary>
    public void OnResumeFromSleep()
    {
        _log.Info("resume_from_sleep detected — refreshing network and sync");
        _network.ForceRefresh();
        _ = _engine.CheckNowAsync();
    }

    private void WriteRelayOutputLine(string line)
    {
        // Relay console output goes to its own file (no console window in daily mode).
        // Never contains the token or clipboard content (relay logging guarantees).
        try
        {
            System.IO.File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "relay-console.log"),
                $"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}] {line}\n");
        }
        catch
        {
            // best effort
        }
    }

    private void RefreshMenuItem()
    {
        var s = _status.Snapshot;
        MarshalToUi(() =>
        {
            if (_exiting) return;
            var presentation = StatusPresentation.From(s);
            _appStatusItem.Text = presentation.Connected ? "Sync: Active" : presentation.Title;
            _trayIcon.Text = "ClipBridge · " + presentation.Title;
            _toggleItem.Text = s.Sync == SyncState.Paused ? "Resume Sync" : "Pause Sync";
            _toggleItem.Checked = s.Sync == SyncState.Paused;
            _copyLanItem.Enabled = s.LanUrl is not null;
            _autostartItem.Checked = s.StartupEnabled;
            _window?.Present(s);
        });
    }

    private void Notify(string text, ToolTipIcon icon)
    {
        try
        {
            _trayIcon.ShowBalloonTip(2500, "ClipBridge", text, icon);
        }
        catch
        {
            // balloon tips are best effort; never take the app down
        }
    }

    private void MarshalToUi(Action action)
    {
        try
        {
            if (_hidden.IsHandleCreated && !_hidden.Disposing)
            {
                _hidden.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch
        {
            // UI races during shutdown are non-fatal
        }
    }

    protected override void ExitThreadCore()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        _log.Info("stopping");

        // Ordered, graceful: sync engine first (stop polling/uploads), then relay
        // (admin shutdown → wait → job kills), then network/mdns, then UI.
        try { _engine.Dispose(); } catch { }
        _deferredClipboardRead?.Dispose();
        _deferredClipboardRead = null;
        try
        {
            // Graceful relay stop first (never leave a stale process).
            _relayManager?.Stop(graceful: true);
            // Wait briefly for the admin shutdown to take effect.
            var started = DateTime.UtcNow;
            while (_relayManager?.RelayPid is { } pid
                   && DateTime.UtcNow - started < TimeSpan.FromSeconds(10))
            {
                if (!IsPidAlive(pid))
                {
                    break;
                }
                Thread.Sleep(200);
            }
            _relayManager?.Dispose(); // job object close = guaranteed kill-on-close
        }
        catch { }

        try { _mdns?.Dispose(); } catch { }
        try { _network.Dispose(); } catch { }
        try { _relayAdmin.Dispose(); } catch { }
        _status.Changed -= OnStatusChanged;
        _appearance.Changed -= OnAppearanceChanged;
        _window?.Dispose();
        _historyClient.Dispose();
        _appearance.Dispose();

        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Icon?.Dispose();
        _trayIcon.Dispose();
        _appIcon.Dispose();
        _hidden.Dispose();

        _log.Info("stopped");
        base.ExitThreadCore();
    }

    private static bool IsPidAlive(int? pid)
    {
        if (pid is null)
        {
            return false;
        }
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid.Value);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
