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

namespace ClipBridge.Win;

/// <summary>
/// Functional tray shell (Milestone 3 — product lifecycle, deliberately minimal visuals):
/// hidden window + clipboard listener + polling sync engine + managed relay child +
/// network monitoring + mDNS hostname advertisement + startup registration + status model.
/// Every status the user sees flows through <see cref="AppStatusModel"/>; UI never parses logs.
/// Visual design (icons, layout, colors) is explicitly deferred to a later milestone.
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
    private readonly ToolStripMenuItem _relayStatusItem;
    private readonly ToolStripMenuItem _lanStatusItem;
    private readonly ToolStripMenuItem _lastSyncItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _copyLanItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _restartRelayItem;
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
        _appStatusItem = new ToolStripMenuItem("ClipBridge — starting\u2026");
        _relayStatusItem = new ToolStripMenuItem("Relay: —");
        _lanStatusItem = new ToolStripMenuItem("LAN: —");
        _lastSyncItem = new ToolStripMenuItem("Last sync: —");
        _toggleItem = new ToolStripMenuItem("Pause Sync", null, (_, _) => ToggleSync()) { Checked = false };
        _copyLanItem = new ToolStripMenuItem("Copy LAN URL", null, (_, _) => CopyLanUrl());
        _autostartItem = new ToolStripMenuItem("Start ClipBridge when I sign in", null, (_, _) => ToggleAutostart())
        {
            Checked = _startup.IsEnabled(),
            ToolTipText = "Registers HKCU Run — user-level, no admin needed",
        };
        _restartRelayItem = new ToolStripMenuItem("Restart Relay", null, (_, _) => _relayManager?.Restart())
        {
            Enabled = false,
            ToolTipText = "Restart the relay after a failure (port 8787 is never changed silently)",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_appStatusItem);
        menu.Items.Add(_relayStatusItem);
        menu.Items.Add(_lanStatusItem);
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Check Now", null, (_, _) => CheckNow()));
        menu.Items.Add(_copyLanItem);
        menu.Items.Add(_restartRelayItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Logs", null, (_, _) => OpenLogs()));
        menu.Items.Add(new ToolStripMenuItem("Open Data Folder", null, (_, _) => OpenDataFolder()));
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => RequestExit()));

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // system icon — custom art is Codex's future scope
            Text = "ClipBridge",
            ContextMenuStrip = menu,
            Visible = config.ShowTrayIcon,
        };

        // ------------------------------------------------------------- status wiring
        _status.Changed += OnStatusChanged;
        guard.Activated += () => MarshalToUi(() =>
            Notify("ClipBridge is already running.", ToolTipIcon.Info));
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
    }

    // ---------------------------------------------------------------- events from hosts

    private void OnRelayStateChanged(RelayState state, string? reason)
    {
        var keepLastError = _status.Snapshot.LastError;
        _status.Patch(s => s.WithRelay(state).WithLastError(
            state == RelayState.Failed ? (reason ?? "relay failed") : keepLastError));
        switch (state)
        {
            case RelayState.Failed:
                MarshalToUi(() => Notify(
                    "Relay failed: " + (reason ?? "unknown") + ". Check the log folder.",
                    ToolTipIcon.Warning));
                break;
            case RelayState.Running:
                _log.Info($"relay_ready reason={reason ?? "ok"}");
                break;
            case RelayState.Restarting:
                MarshalToUi(() => Notify("Relay restarted unexpectedly — recovering\u2026", ToolTipIcon.Warning));
                break;
        }
    }

    private void OnNetworkChanged(NetworkChangedEventArgs e)
    {
        _status.Patch(s =>
        {
            s.WithNetwork(e.LanAvailable ? NetworkState.LanAvailable : NetworkState.NoLan);
            s.WithLanUrl(e.NewPrimaryUrl);
            // mDNS re-announces with the new IP automatically
        });        if (e.OldPrimaryUrl is not null && e.NewPrimaryUrl is not null
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
        _status.Patch(s => s.WithSync(_engine.Paused ? SyncState.Paused : SyncState.Active));
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
        // The copy must not be uploaded back to the relay (one-shot suppression).
        Interlocked.Increment(ref _engine.SuppressNextLocalEvents);
        try
        {
            _clipboard.SetText(url);
        }
        catch
        {
            Notify("Could not write the clipboard.", ToolTipIcon.Warning);
        }
        Notify($"Copied {url}", ToolTipIcon.Info);
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
        var text = _clipboard.TryGetText();
        _ = _engine.OnClipboardChangedAsync(text);
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
        var syncLabel = s.Sync switch
        {
            SyncState.Paused => "Paused",
            SyncState.Offline => "Offline",
            _ => "Active",
        };
        var relayLabel = s.Relay switch
        {
            RelayState.Disabled => "External (dev mode)",
            RelayState.Starting => "Starting\u2026",
            RelayState.Restarting => "Recovering\u2026",
            RelayState.Failed => "Failed",
            RelayState.Running => "Running",
            _ => "Stopped",
        };
        var netLabel = s.Network switch
        {
            NetworkState.LanAvailable => s.LanUrl ?? "LAN available",
            NetworkState.NoLan => "No LAN",
            NetworkState.IpChanged => "IP changed (check Shortcut)",
            _ => "Unknown",
        };
        var last = s.LastSyncAtUtc is null
            ? "Last sync: —"
            : $"Last sync: {s.LastSyncAtUtc:HH:mm:ss} from {s.LastSyncDeviceId} ({s.LastSyncBytes} bytes)";

        MarshalToUi(() =>
        {
            _appStatusItem.Text = $"ClipBridge — Sync: {syncLabel}";
            _relayStatusItem.Text = $"Relay: {relayLabel}";
            _lanStatusItem.Text = $"LAN: {netLabel}";
            _lastSyncItem.Text = last;
            _toggleItem.Text = s.Sync == SyncState.Paused ? "Resume Sync" : "Pause Sync";
            _toggleItem.Checked = s.Sync == SyncState.Paused;
            _copyLanItem.Enabled = s.LanUrl is not null;
            _restartRelayItem.Enabled = s.Relay == RelayState.Failed;
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

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
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
