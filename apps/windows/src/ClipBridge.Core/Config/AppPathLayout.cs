namespace ClipBridge.Core.Config;

/// <summary>
/// Resolved, per-user locations for ClipBridge. Windows conventions are the default:
///
///   config  %APPDATA%\ClipBridge\config.json      (roaming, small, user-owned settings)
///   data    %LOCALAPPDATA%\ClipBridge\             (machine-local: logs, SQLite, runtime state)
///
/// Nothing here depends on the install directory, the developer account, or the current
/// working directory — the app can live in Program Files, on a USB stick, or under a path
/// with spaces/Unicode and still use the same per-user locations.
///
/// Overrides exist for three real use cases (all optional, unset by default):
///   CLIPBRIDGE_HOME        → one root: &lt;home&gt;\config + &lt;home&gt;\data
///   CLIPBRIDGE_CONFIG_DIR  → explicit config directory
///   CLIPBRIDGE_DATA_DIR    → explicit data directory
/// They let tests and the portability harness simulate a different Windows account without
/// touching the real profile, and let advanced users relocate data deliberately.
/// </summary>
public sealed record AppPathLayout(string ConfigDir, string DataDir, string Source)
{
    public const string HomeEnvVar = "CLIPBRIDGE_HOME";
    public const string ConfigDirEnvVar = "CLIPBRIDGE_CONFIG_DIR";
    public const string DataDirEnvVar = "CLIPBRIDGE_DATA_DIR";

    public string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string DatabaseFile => Path.Combine(DataDir, "clipbridge.db");

    /// <summary>Human-readable, path-only description (never contains secrets).</summary>
    public string Describe() => $"config={ConfigDir} data={DataDir} source={Source}";

    /// <summary>
    /// Pure resolver. <paramref name="readEnvironment"/> and the folder roots are injectable
    /// so every branch is unit-testable without changing the real process environment.
    /// </summary>
    public static AppPathLayout FromEnvironment(
        Func<string, string?>? readEnvironment = null,
        Func<Environment.SpecialFolder, string>? folderPath = null)
    {
        var env = readEnvironment ?? Environment.GetEnvironmentVariable;
        var folders = folderPath ?? Environment.GetFolderPath;

        var home = Normalize(env(HomeEnvVar));
        if (home is not null)
        {
            var root = TryFullPath(home);
            if (root is not null)
            {
                return new AppPathLayout(
                    Path.Combine(root, "config"),
                    Path.Combine(root, "data"),
                    HomeEnvVar);
            }
        }

        var configDir = TryFullPath(Normalize(env(ConfigDirEnvVar)));
        var dataDir = TryFullPath(Normalize(env(DataDirEnvVar)));

        var configSource = configDir is null ? "default" : ConfigDirEnvVar;
        var dataSource = dataDir is null ? "default" : DataDirEnvVar;

        configDir ??= Path.Combine(RoamingRoot(folders), "ClipBridge");
        dataDir ??= Path.Combine(LocalRoot(folders), "ClipBridge");

        var source = configSource == "default" && dataSource == "default"
            ? "default"
            : $"{configSource}/{dataSource}";
        return new AppPathLayout(configDir, dataDir, source);
    }

    /// <summary>Explicit single-root layout (&lt;home&gt;\config, &lt;home&gt;\data).</summary>
    public static AppPathLayout ForHome(string home)
    {
        var root = TryFullPath(home)
                   ?? throw new ArgumentException("Home directory is not a valid path.", nameof(home));
        return new AppPathLayout(Path.Combine(root, "config"), Path.Combine(root, "data"), HomeEnvVar);
    }

    private static string RoamingRoot(Func<Environment.SpecialFolder, string> folders)
        => FirstNonEmpty(
            folders(Environment.SpecialFolder.ApplicationData),
            CombineProfile(folders, "AppData", "Roaming"),
            folders(Environment.SpecialFolder.LocalApplicationData));

    private static string LocalRoot(Func<Environment.SpecialFolder, string> folders)
        => FirstNonEmpty(
            folders(Environment.SpecialFolder.LocalApplicationData),
            folders(Environment.SpecialFolder.ApplicationData),
            CombineProfile(folders, "AppData", "Local"));

    private static string CombineProfile(
        Func<Environment.SpecialFolder, string> folders, params string[] parts)
    {
        var profile = folders(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? "" : Path.Combine(new[] { profile }.Concat(parts).ToArray());
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate!;
            }
        }
        // Last resort: never return a relative path that silently depends on the CWD.
        return Path.Combine(Path.GetTempPath(), "ClipBridge");
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return raw.Trim().Trim('"');
    }

    private static string? TryFullPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        // A relative override would silently depend on the current working directory —
        // exactly the coupling this class exists to remove. Reject it.
        try
        {
            if (!Path.IsPathFullyQualified(raw))
            {
                return null;
            }
            return Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
