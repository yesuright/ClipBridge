using System.Text.Json;
using ClipBridge.Core.Config;
using ClipBridge.Core.Logging;
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

        // Debug/introspection helper: print what THIS process reads from the clipboard,
        // as codepoints/hex — used by tests to verify the exact read path (no secrets).
        if (wantsProbe)
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
            return;
        }

        // Instance id: default "ClipBridge". Overridable (CLIPBRIDGE_INSTANCE_ID) so two
        // copies on one machine (e.g. an automated verify run next to the real tray app)
        // never fight over the single-instance mutex/pipe.
        var instanceId = Environment.GetEnvironmentVariable("CLIPBRIDGE_INSTANCE_ID") ?? "ClipBridge";

        // ------------------------------------------------------------- CLI control commands
        // These are standalone (they talk to the registry / the running instance), so they
        // never need the single-instance lock.
        if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains("autostart", StringComparison.OrdinalIgnoreCase)))
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
        var config = ClientConfig.Load();
        if (!config.HasValidToken)
        {
            // Existing M2 users (relay .env configured, no client config yet): migrate once.
            if (ConfigMigration.TryMigrateFromLegacyEnv())
            {
                config = ClientConfig.Load();
            }
        }
        if (!config.HasValidToken)
        {
            MessageBox.Show(
                "ClipBridge is not configured yet.\n\n" +
                "Open this file and set \"DeviceToken\" (same token the relay uses),\n" +
                "or set the CLIPBRIDGE_DEVICE_TOKEN environment variable:\n\n" +
                $"{ClientConfig.DefaultConfigPath}\n\n" +
                "See docs/DAILY_USE.md for the plain-language setup steps.",
                "ClipBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AppPaths.EnsureDirectories();
        using var logger = new FileClientLogger(AppPaths.LogsDir);
        var status = new AppStatusModel();

        using var trayGuard = guard;
        trayGuard.CommandHandler = _ => JsonSerializer.Serialize(status.Snapshot, StatusJson.Options);

        Application.Run(new TrayContext(config, logger, status, trayGuard));
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
            var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
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
}
