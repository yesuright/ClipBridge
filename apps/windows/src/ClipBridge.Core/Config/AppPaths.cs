namespace ClipBridge.Core.Config;

/// <summary>
/// Process-wide ClipBridge locations. The actual layout is resolved once from
/// <see cref="AppPathLayout.FromEnvironment"/> (standard Windows per-user folders, or an
/// explicit override) so every component — config, logs, database, relay launch — agrees.
///
/// No path here is derived from the install directory, the developer account, or the
/// current working directory.
/// </summary>
public static class AppPaths
{
    private static AppPathLayout _current = AppPathLayout.FromEnvironment();

    /// <summary>Active layout. Settable for tests/hosts that resolve it explicitly.</summary>
    public static AppPathLayout Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static string ConfigDir => Current.ConfigDir;

    public static string ConfigFile => Current.ConfigFile;

    public static string DataDir => Current.DataDir;

    public static string LogsDir => Current.LogsDir;

    public static string DatabaseFile => Current.DatabaseFile;

    /// <summary>Creates all runtime directories (idempotent).</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
