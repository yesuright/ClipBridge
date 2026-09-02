using System.Text.Json;

namespace ClipBridge.Core.Config;

/// <summary>
/// Client configuration. Precedence: environment variables &gt; JSON config file &gt; defaults.
/// Never commit real tokens: config.json lives in %APPDATA%\ClipBridge and is gitignored.
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

    public static string DefaultConfigPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "ClipBridge", "config.json");

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
        };
    }

    private static int ParseInt(string? value, int fallback, int min, int max)
        => int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;
}
