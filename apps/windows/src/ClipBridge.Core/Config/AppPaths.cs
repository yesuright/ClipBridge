namespace ClipBridge.Core.Config;

/// <summary>
/// Well-known ClipBridge locations, following Windows conventions:
/// - Settings (roaming): %APPDATA%\ClipBridge\config.json — same file as Milestone 2
///   (source compatibility: an existing M2 config is read as-is, no migration needed).
/// - Machine-local runtime data (logs, database): %LOCALAPPDATA%\ClipBridge\
///   Logs and the SQLite database are regenerable/big — they must not roam.
/// - Secrets live in config.json (same OS-account trust as before; never in the repo).
/// </summary>
public static class AppPaths
{
    public static string ConfigDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBridge");

    public static string ConfigFile { get; } = Path.Combine(ConfigDir, "config.json");

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBridge");

    public static string LogsDir { get; } = Path.Combine(DataDir, "logs");

    public static string DatabaseFile { get; } = Path.Combine(DataDir, "clipbridge.db");

    /// <summary>Creates all runtime directories (idempotent).</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
    }
}
