namespace ClipBridge.Core.Config;

/// <summary>
/// Optional import of a Milestone 2 relay `.env` token.
///
/// Scope is deliberately narrow: the import only happens when the running binary sits
/// inside a real ClipBridge source checkout (a directory that has both
/// services/relay/.env and the Windows project or .git). A build handed to another person
/// is never inside a checkout, so it can never inherit a developer token from a folder
/// that happens to be nearby. Anything else gets a freshly generated token instead.
/// </summary>
public static class ConfigMigration
{
    private const int MaxWalkUp = 6;

    /// <summary>Finds the legacy relay .env of the checkout containing the app, if any.</summary>
    public static string? FindLegacyRelayEnv(string? startDirectory = null)
    {
        var start = startDirectory ?? AppContext.BaseDirectory;
        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(Path.GetFullPath(start));
        }
        catch
        {
            return null;
        }

        // Only a developer build (running out of apps\windows\...) may inherit a relay .env.
        // A packaged artifact is never there, so shipping ClipBridge inside a folder that
        // happens to contain somebody's checkout cannot leak that person's token.
        if (!IsDevLayout(dir.FullName))
        {
            return null;
        }

        for (var i = 0; i < MaxWalkUp && dir is not null; i++)
        {
            if (IsCheckoutRoot(dir.FullName))
            {
                return Path.Combine(dir.FullName, "services", "relay", ".env");
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static bool IsDevLayout(string directory)
        => directory.Replace('/', '\\')
            .Contains(@"\apps\windows\", StringComparison.OrdinalIgnoreCase);

    private static bool IsCheckoutRoot(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "services", "relay", ".env")))
        {
            return false;
        }
        return Directory.Exists(Path.Combine(directory, ".git"))
            || File.Exists(Path.Combine(directory, "apps", "windows", "src", "ClipBridge.Win", "ClipBridge.Win.csproj"));
    }

    /// <summary>Reads CLIPBRIDGE_TOKEN from a legacy relay .env (never logs it).</summary>
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

    /// <summary>Locates a legacy token without writing anything.</summary>
    public static bool TryReadLegacyToken(out string? token, out string? envPath, string? startDirectory = null)
    {
        token = null;
        envPath = FindLegacyRelayEnv(startDirectory);
        if (envPath is null)
        {
            return false;
        }
        token = ReadLegacyToken(envPath);
        return token is not null;
    }

    /// <summary>
    /// Legacy entry point (Milestone 3): seed a config from the checkout's relay .env.
    /// Kept for compatibility; new code goes through <see cref="ConfigBootstrap"/>.
    /// </summary>
    public static bool TryMigrateFromLegacyEnv(string? configPath = null)
    {
        var path = configPath ?? AppPaths.ConfigFile;
        if (File.Exists(path))
        {
            return false; // an existing config always wins; never touch a configured token
        }
        if (!TryReadLegacyToken(out var token, out _) || token is null)
        {
            return false;
        }

        ConfigStore.Write(new ClientConfig { DeviceToken = token }, path);
        return true;
    }
}
