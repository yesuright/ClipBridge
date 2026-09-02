using System.Text.Json;

namespace ClipBridge.Core.Config;

/// <summary>
/// Migrations from the Milestone 2 layout into the daily-use layout.
///
/// What changed in M3:
/// - The client config keeps its M2 location (%APPDATA%\ClipBridge\config.json) and keys —
///   an existing M2 config is already fully valid, no rewrite, no token rotation.
/// - The relay is now managed by the tray app (M3). In the old layout the relay token lived
///   in services\relay\.env; if a user had configured ONLY the relay .env (no client config),
///   this migration copies that token into the client config so the daily app "just works".
///   The token is never printed or logged.
/// - Logs/DB move to %LOCALAPPDATA%\ClipBridge (machine-local). Nothing to migrate: the M2
///   relay DB is kept where it is; the daily app starts its own store (history is best-effort,
///   the protocol treats it as a cache-like rolling buffer).
/// </summary>
public static class ConfigMigration
{
    /// <summary>
    /// Best-effort discovery of the legacy relay .env (repo checkout), walking up from the
    /// app base directory and the current directory. Returns null when not found.
    /// </summary>
    public static string? FindLegacyRelayEnv()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 6 && dir is not null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "services", "relay", ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
        }
        return null;
    }

    /// <summary>
    /// Reads CLIPBRIDGE_TOKEN from a legacy relay .env (never logs it).
    /// </summary>
    public static string? ReadLegacyToken(string envPath)
    {
        try
        {
            foreach (var raw in File.ReadLines(envPath))
            {
                var line = raw.Trim();
                if (line.StartsWith("CLIPBRIDGE_TOKEN=", StringComparison.Ordinal))
                {
                    var value = line["CLIPBRIDGE_TOKEN=".Length..].Trim().Trim('"', '\'');
                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }
        }
        catch
        {
            // unreadable .env — migration is best-effort
        }
        return null;
    }

    /// <summary>
    /// When no client config exists yet, seed one from the legacy relay .env token so the
    /// daily build can start without requiring the user to re-enter anything.
    /// Returns true when a config file was written.
    /// </summary>
    public static bool TryMigrateFromLegacyEnv(string? configPath = null)
    {
        var path = configPath ?? AppPaths.ConfigFile;
        if (File.Exists(path))
        {
            return false; // an existing config wins; never touch a configured token
        }
        var envPath = FindLegacyRelayEnv();
        if (envPath is null)
        {
            return false;
        }
        var token = ReadLegacyToken(envPath);
        if (token is null)
        {
            return false;
        }

        var config = new ClientConfig { DeviceToken = token };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return true;
    }
}
