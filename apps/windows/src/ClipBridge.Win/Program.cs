using System.Text.Json;
using ClipBridge.Core.Config;
using ClipBridge.Core.Logging;
using ClipBridge.Core.Net;
using ClipBridge.Core.Process;
using ClipBridge.Core.Startup;
using ClipBridge.Core.Status;

namespace ClipBridge.Win;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var wantsStatus = args.Contains("--status", StringComparer.OrdinalIgnoreCase);
        var wantsExit = args.Contains("--exit", StringComparer.OrdinalIgnoreCase);
        var wantsProbe = args.Contains("--probe-clipboard", StringComparer.OrdinalIgnoreCase);
        var wantsDoctor = args.Contains("--doctor", StringComparer.OrdinalIgnoreCase);

        // Debug/introspection helper: print what THIS process reads from the clipboard,
        // as codepoints/hex — used by tests to verify the exact read path (no secrets).
        if (wantsProbe)
        {
            RunClipboardProbe();
            return;
        }

        // Instance id: default "ClipBridge". Overridable (CLIPBRIDGE_INSTANCE_ID) so two
        // copies on one machine (e.g. an automated verify run next to the real tray app)
        // never fight over the single-instance mutex/pipe.
        var instanceId = Environment.GetEnvironmentVariable("CLIPBRIDGE_INSTANCE_ID") ?? "ClipBridge";

        // ------------------------------------------------------------- diagnostics
        // Read-only, no lock, no writes: prints the resolved layout and config state so a
        // support report from another machine can be answered without guessing.
        if (wantsDoctor)
        {
            PrintDoctor();
            return;
        }

        // ------------------------------------------------------------- CLI control commands
        // These are standalone (they talk to the registry / the running instance), so they
        // never need the single-instance lock.
        if (args.Any(a => a.Equals("--autostart-status", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--enable-autostart", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--disable-autostart", StringComparison.OrdinalIgnoreCase)))
        {
            RunAutostartCommand(args);
            return;
        }

        // ------------------------------------------------------------- single instance
        // The guard is acquired on every full start. A second instance either reports
        // status (--status), requests a clean exit (--exit), or activates the running tray.
        // Control commands (--status/--exit) never BECOME the primary instance.
        if (wantsStatus || wantsExit)
        {
            using var control = new SingleInstanceGuard(instanceId);
            var runningReply = control.RequestStatus();
            if (runningReply is not null)
            {
                if (wantsStatus)
                {
                    Console.WriteLine(runningReply);
                }
                else
                {
                    control.Signal("exit");
                }
            }
            else if (wantsStatus)
            {
                Console.WriteLine("{\"error\":\"not_running\"}");
            }
            return;
        }

        var guard = new SingleInstanceGuard(instanceId);
        if (!guard.TryAcquire())
        {
            guard.Signal("activate");
            return;
        }

        // ------------------------------------------------------------- config + first run
        // A stranger's PC has no config file. Instead of asking them to hand-edit JSON
        // (which previously made the app unusable on a fresh machine), the app provisions
        // a token/device identity itself and shows the values it needs for the Shortcut.
        var bootstrap = ConfigBootstrap.Ensure();
        var config = bootstrap.Config;
        if (!bootstrap.IsUsable)
        {
            MessageBox.Show(
                "ClipBridge cannot save its configuration, so it cannot run yet.\n\n" +
                $"{bootstrap.Error}\n\n" +
                "Configuration file:\n" + bootstrap.ConfigPath + "\n\n" +
                "Fix the folder permissions (or the drive) and start ClipBridge again. " +
                "Existing files are never deleted or overwritten while they cannot be read.",
                "ClipBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var logger = CreateLogger(out var loggingWarning);
        using var loggerLifetime = logger as IDisposable;
        logger.Info($"layout {AppPaths.Current.Describe()}");
        logger.Info($"config {bootstrap.Describe()}");
        foreach (var note in bootstrap.Notes)
        {
            logger.Info($"config_note {note}");
        }
        if (loggingWarning is not null)
        {
            logger.Warn($"logging_fallback {loggingWarning}");
        }

        var status = new AppStatusModel();

        using var trayGuard = guard;
        trayGuard.CommandHandler = _ => JsonSerializer.Serialize(status.Snapshot, StatusJson.Options);

        Application.Run(new TrayContext(
            config, logger, status, trayGuard,
            showSetupOnStart: bootstrap.Outcome is ConfigBootstrapOutcome.Provisioned
                or ConfigBootstrapOutcome.Recovered
                or ConfigBootstrapOutcome.Migrated));
    }

    private static void RunClipboardProbe()
    {
        try
        {
            var uni = System.Windows.Forms.Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? System.Windows.Forms.Clipboard.GetText(TextDataFormat.UnicodeText)
                : null;
            var ansi = System.Windows.Forms.Clipboard.ContainsText()
                ? System.Windows.Forms.Clipboard.GetText()
                : null;
            Console.WriteLine("unicode: " + (uni is null ? "<none>" : string.Join(",", uni.Select(c => ((int)c).ToString("X4")))));
            Console.WriteLine("ansi:    " + (ansi is null ? "<none>" : string.Join(",", ansi.Select(c => ((int)c).ToString("X4")))));
        }
        catch (Exception ex)
        {
            Console.WriteLine("probe failed: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Logging must never be the reason the app does not start. If the per-user log
    /// directory is not writable (locked-down profile, redirected folder), fall back to
    /// %TEMP% and finally to a no-op logger, and report which happened.
    /// </summary>
    private static IClientLogger CreateLogger(out string? warning)
    {
        warning = null;
        try
        {
            AppPaths.EnsureDirectories();
            return new FileClientLogger(AppPaths.LogsDir);
        }
        catch (Exception ex)
        {
            warning = $"primary log directory unavailable ({ex.GetType().Name}); ";
        }
        try
        {
            var fallback = Path.Combine(Path.GetTempPath(), "ClipBridge", "logs");
            var logger = new FileClientLogger(fallback);
            warning += $"using {fallback}";
            return logger;
        }
        catch (Exception ex)
        {
            warning += $"temporary log directory unavailable too ({ex.GetType().Name}); logging disabled";
            return NullClientLogger.Instance;
        }
    }

    /// <summary>
    /// Support/diagnostic snapshot: resolved paths, config state, relay location, LAN URL,
    /// startup registration. Never prints the token — only a short non-reversible fingerprint.
    /// </summary>
    private static void PrintDoctor()
    {
        var layout = AppPaths.Current;
        var read = ConfigStore.Read(layout.ConfigFile);
        var config = read.Config;
        string? lanUrl = null;
        try
        {
            lanUrl = LanAddressResolver.DiscoverPrimary(config.RelayPort)?.Url;
        }
        catch
        {
            // diagnostics are best-effort
        }
        var relayExe = RelayPaths.FindRelayExecutable();
        var startup = new WindowsStartupRegistration();
        string? startupCommand = null;
        try
        {
            startupCommand = startup.GetCommand();
        }
        catch
        {
            // registry unavailable
        }

        var snapshot = new
        {
            app_version = typeof(Program).Assembly.GetName().Version?.ToString(),
            os = Environment.OSVersion.VersionString,
            user = Environment.UserName,
            config_dir = layout.ConfigDir,
            data_dir = layout.DataDir,
            path_source = layout.Source,
            config_state = read.State.ToString(),
            config_note = read.Note,
            backup = read.BackupPath,
            device_id = config.DeviceId,
            token = config.HasValidToken ? "configured" : "missing",
            token_fingerprint = TokenGenerator.Fingerprint(config.DeviceToken),
            relay_url = config.RelayUrl,
            relay_port = config.RelayPort,
            manage_relay = config.ManageRelay,
            relay_exe = relayExe,
            relay_exe_found = relayExe is not null,
            lan_url = lanUrl,
            lan_hostname = config.LanHostname,
            lan_ip_override = config.LanIpOverride,
            port_bindable = PortProbe.IsBindable(config.RelayPort),
            startup_enabled = startupCommand is not null,
            startup_command = startupCommand,
            startup_stale = startupCommand is not null && StartupManager.IsStale(startupCommand, CurrentExecutablePath()),
        };
        Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Standalone startup-registration commands: --autostart-status (print true/false),
    /// --enable-autostart, --disable-autostart. User-level HKCU Run only; needs no elevation.
    /// </summary>
    private static void RunAutostartCommand(string[] args)
    {
        var registration = new WindowsStartupRegistration();
        if (args.Contains("--autostart-status", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(registration.IsEnabled() ? "true" : "false");
            return;
        }
        if (args.Contains("--enable-autostart", StringComparer.OrdinalIgnoreCase))
        {
            var exe = CurrentExecutablePath();
            registration.Enable(StartupManager.BuildCommandLine(exe));
            Console.WriteLine("true");
            return;
        }
        if (args.Contains("--disable-autostart", StringComparer.OrdinalIgnoreCase))
        {
            registration.Disable();
            Console.WriteLine("false");
            return;
        }
        Console.WriteLine("unknown autostart command");
    }

    /// <summary>
    /// The real exe path, also correct under `dotnet run` (where ProcessPath is the host).
    /// </summary>
    internal static string CurrentExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath)
            && Path.GetFileNameWithoutExtension(processPath).Equals("ClipBridge", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }
        var assembly = typeof(Program).Assembly.Location;
        if (!string.IsNullOrEmpty(assembly))
        {
            return Path.ChangeExtension(assembly, ".exe");
        }
        return processPath ?? AppContext.BaseDirectory;
    }
}
