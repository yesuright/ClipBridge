namespace ClipBridge.Core.Process;

/// <summary>Locates the packaged relay executable for the managed-relay mode.</summary>
public static class RelayPaths
{
    /// <summary>
    /// Resolution order:
    /// 1. CLIPBRIDGE_RELAY_EXE env var (explicit override / testing)
    /// 2. &lt;appdir&gt;\relay\ClipBridgeRelay.exe  (daily-use artifact layout)
    /// Returns null when no packaged relay is present — the app then runs in external-relay mode.
    /// </summary>
    public static string? FindRelayExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CLIPBRIDGE_RELAY_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }
        var packaged = Path.Combine(AppContext.BaseDirectory, "relay", "ClipBridgeRelay.exe");
        return File.Exists(packaged) ? packaged : null;
    }
}
