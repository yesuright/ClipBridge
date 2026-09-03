using System.Text.Json;

namespace ClipBridge.Core.Config;

/// <summary>
/// Client configuration. Precedence: environment variables &gt; JSON config file &gt; defaults.
/// Never commit real tokens: config.json lives in %APPDATA%\ClipBridge and is gitignored.
///
/// Milestone 3 additions: relay management (the tray app can own the relay child process),
/// LAN hostname for the .local advertisement. Milestone 2 keys keep working unchanged.
/// </summary>
public sealed record ClientConfig
{
    public string RelayUrl { get; init; } = "http://127.0.0.1:8787";
    public string DeviceId { get; init; } = "windows-main";
    public string DeviceToken { get; init; } = "";
    public int PollIntervalMs { get; init; } = 2000;
    public int HttpTimeoutSeconds { get; init; } = 20;

    /// <summary>Suppression window for a remotely-applied clip before a user re-copy re-uploads.</summary>
    public int SuppressWindowSeconds { get; init; } = 30;

    /// <summary>How long to remember our own uploads when filtering poll results.</summary>
    public int UploadMemorySeconds { get; init; } = 900;

    /// <summary>
    /// Bounded upload retry policy: the SAME clip (same clip_id, idempotent at the relay)
    /// is re-posted up to <see cref="UploadMaxAttempts"/> times on transient failures,
    /// with <see cref="UploadRetryBaseMs"/> * (attempt-1) backoff. A permanent protocol
    /// error (4xx) is never retried, and there is no queue: after the bound the event
    /// is dropped (as before) but only after three genuine attempts instead of one.
    /// </summary>
    public int UploadMaxAttempts { get; init; } = 3;

    /// <summary>Base backoff (ms) between upload attempts: attempt 2 = base, attempt 3 = 2*base.</summary>
    public int UploadRetryBaseMs { get; init; } = 500;

    // ---------------------------------------------------------------- M3: relay management

    /// <summary>
    /// True (default): the tray app starts/stops a local relay child process.
    /// Development can run the relay separately (CLIPBRIDGE_MANAGE_RELAY=0 or a dev build
    /// without a packaged relay next to the exe falls back to "external relay" mode).
    /// </summary>
    public bool ManageRelay { get; init; } = true;

    /// <summary>Hostname advertised on the LAN via mDNS (e.g. "clipbridge" → clipbridge.local).</summary>
    public string LanHostname { get; init; } = "clipbridge";

    /// <summary>Port the managed relay listens on. Must match the port of RelayUrl.</summary>
    public int RelayPort { get; init; } = 8787;

    /// <summary>Show the hidden app icon in the tray (default true; false only affects nothing else).</summary>
    public bool ShowTrayIcon { get; init; } = true;

    public static string DefaultConfigPath => AppPaths.ConfigFile;

    public bool HasValidToken => !string.IsNullOrWhiteSpace(DeviceToken);

    /// <summary>Base address for the /api/v1 endpoints (trailing slash required by HttpClient).</summary>
    public Uri ApiBaseUri
    {
        get
        {
            var trimmed = RelayUrl.TrimEnd('/');
            var baseUrl = trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : trimmed + "/api/v1";
            return new Uri(baseUrl + "/");
        }
    }

    public static ClientConfig Load(string? path = null)
    {
        var config = new ClientConfig();
        var filePath = path ?? DefaultConfigPath;
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var fromFile = JsonSerializer.Deserialize<ClientConfig>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (fromFile is not null)
                {
                    config = fromFile with { };
                }
            }
            catch (Exception)
            {
                // Malformed config: fall back to defaults + env; do not crash at load time.
            }
        }

        Func<string, string?> env = name => Environment.GetEnvironmentVariable(name);
        return config with
        {
            RelayUrl = env("CLIPBRIDGE_RELAY_URL") ?? config.RelayUrl,
            DeviceId = env("CLIPBRIDGE_DEVICE_ID") ?? config.DeviceId,
            DeviceToken = env("CLIPBRIDGE_DEVICE_TOKEN") ?? config.DeviceToken,
            PollIntervalMs = ParseInt(env("CLIPBRIDGE_POLL_INTERVAL_MS"), config.PollIntervalMs, 500, 60000),
            HttpTimeoutSeconds = ParseInt(env("CLIPBRIDGE_HTTP_TIMEOUT_SECONDS"), config.HttpTimeoutSeconds, 5, 300),
            SuppressWindowSeconds = ParseInt(env("CLIPBRIDGE_SUPPRESS_WINDOW_SECONDS"), config.SuppressWindowSeconds, 1, 3600),
            UploadMemorySeconds = ParseInt(env("CLIPBRIDGE_UPLOAD_MEMORY_SECONDS"), config.UploadMemorySeconds, 60, 86400),
            UploadMaxAttempts = ParseInt(env("CLIPBRIDGE_UPLOAD_MAX_ATTEMPTS"), config.UploadMaxAttempts, 1, 5),
            UploadRetryBaseMs = ParseInt(env("CLIPBRIDGE_UPLOAD_RETRY_BASE_MS"), config.UploadRetryBaseMs, 100, 5000),
            ManageRelay = ParseBool(env("CLIPBRIDGE_MANAGE_RELAY"), config.ManageRelay),
            LanHostname = NormalizeHostname(env("CLIPBRIDGE_LAN_HOSTNAME") ?? config.LanHostname),
            RelayPort = ParseInt(env("CLIPBRIDGE_RELAY_PORT"), config.RelayPort, 1, 65535),
            ShowTrayIcon = ParseBool(env("CLIPBRIDGE_SHOW_TRAY_ICON"), config.ShowTrayIcon),
        };
    }

    private static int ParseInt(string? value, int fallback, int min, int max)
        => int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static bool ParseBool(string? value, bool fallback)
        => value is not null && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static string NormalizeHostname(string raw)
    {
        // mDNS hostnames: single label (letters, digits, hyphens), no dots.
        // "clipbridge.local" / "clipbridge." inputs reduce to the label "clipbridge".
        var name = raw.Trim().TrimEnd('.').ToLowerInvariant();
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            name = name[..dot];
        }
        if (name.Length == 0 || name.Length > 63) return "clipbridge";
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return "clipbridge";
        if (name[0] == '-' || name[^1] == '-') return "clipbridge";
        return name;
    }
}
