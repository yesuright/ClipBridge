using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipBridge.Core.Config;

/// <summary>
/// Client configuration. Precedence: environment variables &gt; JSON config file &gt; defaults.
/// Never commit real tokens: config.json lives in %APPDATA%\ClipBridge and is gitignored.
///
/// Portability rules encoded here:
/// - Every value has a safe default, so a missing key or an older config file still loads.
/// - <see cref="Normalize"/> turns user-edited, partially wrong values (a URL without a
///   scheme, a device id the relay would reject, a port outside 1-65535) into something the
///   app can actually run with, and reports what it changed instead of crashing at startup.
/// - <see cref="ApiBaseUri"/> never throws.
/// </summary>
public sealed record ClientConfig
{
    /// <summary>Schema version written by this build. 0/absent = pre-versioning config (valid).</summary>
    public int ConfigVersion { get; init; } = ConfigStore.CurrentSchemaVersion;

    public string RelayUrl { get; init; } = DefaultRelayUrl;
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

    /// <summary>
    /// Optional manual LAN address for the iPhone URL. Empty = automatic selection.
    /// Use it when a machine has several plausible adapters (VPN, docking station, Hyper-V)
    /// and the automatic pick is not the network the phone is on.
    /// </summary>
    public string LanIpOverride { get; init; } = "";

    /// <summary>Show the hidden app icon in the tray (default true; false only affects nothing else).</summary>
    public bool ShowTrayIcon { get; init; } = true;

    public const string DefaultRelayUrl = "http://127.0.0.1:8787";

    public static string DefaultConfigPath => AppPaths.ConfigFile;

    public bool HasValidToken => !string.IsNullOrWhiteSpace(DeviceToken);

    /// <summary>Base address for the /api/v1 endpoints (trailing slash required by HttpClient).</summary>
    public Uri ApiBaseUri => BuildApiBaseUri(out _);

    /// <summary>Same as <see cref="ApiBaseUri"/> but explains a fallback instead of hiding it.</summary>
    public Uri BuildApiBaseUri(out string? note)
    {
        if (TryBuildApiBaseUri(RelayUrl, out var uri, out var error))
        {
            note = null;
            return uri;
        }
        note = $"RelayUrl '{RelayUrl}' is not a usable http(s) address ({error}); using {DefaultRelayUrl}.";
        TryBuildApiBaseUri(DefaultRelayUrl, out var fallback, out _);
        return fallback;
    }

    public static bool TryBuildApiBaseUri(string? relayUrl, out Uri uri, out string? error)
    {
        uri = new Uri(DefaultRelayUrl + "/api/v1/");
        error = null;
        if (string.IsNullOrWhiteSpace(relayUrl))
        {
            error = "empty";
            return false;
        }
        var trimmed = relayUrl.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            // A very common hand-edit: "192.168.1.14:8787" without a scheme.
            trimmed = "http://" + trimmed;
        }
        var separator = trimmed.IndexOf("://", StringComparison.Ordinal);
        var scheme = trimmed[..separator];
        // Only the path may be trimmed: "http://" must not degrade into the host "http".
        var rest = trimmed[(separator + 3)..].TrimEnd('/');
        if (rest.Length == 0 || rest.Trim('/').Length == 0)
        {
            error = "missing host";
            return false;
        }
        trimmed = $"{scheme}://{rest}";
        if (!trimmed.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/api/v1";
        }
        if (!Uri.TryCreate(trimmed + "/", UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
        {
            error = "not an absolute http(s) URL";
            return false;
        }
        uri = parsed;
        return true;
    }

    public static ClientConfig Load(string? path = null)
    {
        var config = ReadFile(path);
        return ApplyEnvironment(config).Normalize(out _);
    }

    /// <summary>Reads the JSON file only (no env, no normalization). Used by ConfigStore.</summary>
    internal static ClientConfig ReadFile(string? path)
    {
        var filePath = path ?? DefaultConfigPath;
        if (!File.Exists(filePath))
        {
            return new ClientConfig();
        }
        try
        {
            var json = File.ReadAllText(filePath);
            var fromFile = JsonSerializer.Deserialize<ClientConfig>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return fromFile ?? new ClientConfig();
        }
        catch (Exception)
        {
            // Malformed config: ConfigStore backs up the file and provisions a valid one.
            // Loading itself must never crash the app.
            return new ClientConfig();
        }
    }

    /// <summary>Applies CLIPBRIDGE_* environment overrides on top of a loaded config.</summary>
    public static ClientConfig ApplyEnvironment(ClientConfig config)
    {
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
            LanHostname = env("CLIPBRIDGE_LAN_HOSTNAME") ?? config.LanHostname,
            RelayPort = ParseInt(env("CLIPBRIDGE_RELAY_PORT"), config.RelayPort, 1, 65535),
            LanIpOverride = env("CLIPBRIDGE_LAN_IP") ?? config.LanIpOverride,
            ShowTrayIcon = ParseBool(env("CLIPBRIDGE_SHOW_TRAY_ICON"), config.ShowTrayIcon),
        };
    }

    /// <summary>
    /// Makes an arbitrary (possibly hand-edited) config internally consistent.
    /// Notes describe every change, in plain language, for the log and the setup dialog.
    /// </summary>
    public ClientConfig Normalize(out IReadOnlyList<string> notes)
    {
        var list = new List<string>();
        var port = RelayPort is >= 1 and <= 65535 ? RelayPort : 8787;
        if (port != RelayPort)
        {
            list.Add($"RelayPort {RelayPort} is outside 1-65535; using {port}.");
        }

        var rawUrl = (RelayUrl ?? "").Trim();
        var effectiveUrl = rawUrl;
        if (effectiveUrl.Length == 0)
        {
            effectiveUrl = $"http://127.0.0.1:{port}";
            list.Add($"RelayUrl was empty; using {effectiveUrl}.");
        }
        else if (TryBuildApiBaseUri(effectiveUrl, out var parsed, out var urlError))
        {
            var baseUri = new Uri(parsed.GetLeftPart(UriPartial.Authority));
            var normalized = baseUri.ToString().TrimEnd('/');
            if (!string.Equals(normalized, effectiveUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                list.Add($"RelayUrl '{rawUrl}' normalized to '{normalized}'.");
            }
            effectiveUrl = normalized;

            // Managed relay: the local process binds RelayPort, so a *loopback* URL must use
            // that port. A non-loopback URL is a deliberate external/remote relay and is kept
            // exactly as configured (compatibility with existing split setups).
            var isLoopback = baseUri.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
            if (ManageRelay && isLoopback && baseUri.Port != port)
            {
                effectiveUrl = $"http://127.0.0.1:{port}";
                list.Add($"RelayUrl port {baseUri.Port} did not match RelayPort {port}; using {effectiveUrl}.");
            }
        }
        else
        {
            effectiveUrl = $"http://127.0.0.1:{port}";
            list.Add($"RelayUrl '{rawUrl}' is not a valid address ({urlError}); using {effectiveUrl}.");
        }

        var deviceId = NormalizeDeviceId(DeviceId, out var deviceIdChanged);
        if (deviceIdChanged)
        {
            list.Add($"DeviceId was adjusted to '{deviceId}' (letters, digits, '-' and '_' only).");
        }

        var hostname = NormalizeHostname(LanHostname);
        if (!string.Equals(hostname, LanHostname, StringComparison.Ordinal))
        {
            list.Add($"LanHostname normalized to '{hostname}'.");
        }

        var lanIp = NormalizeLanIp(LanIpOverride, out var lanIpChanged);
        if (lanIpChanged)
        {
            list.Add($"LanIpOverride '{LanIpOverride}' is not a valid IPv4 address; automatic selection is used.");
        }

        notes = list;
        return this with
        {
            RelayUrl = effectiveUrl,
            RelayPort = port,
            DeviceId = deviceId,
            DeviceToken = (DeviceToken ?? "").Trim(),
            LanHostname = hostname,
            LanIpOverride = lanIp,
        };
    }

    private static int ParseInt(string? value, int fallback, int min, int max)
        => int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static bool ParseBool(string? value, bool fallback)
        => value is not null && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static readonly Regex DeviceIdPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);

    /// <summary>
    /// The relay rejects device ids outside ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$ with 422 on every
    /// upload. A hand-edited config must not be able to break sync silently.
    /// </summary>
    public static string NormalizeDeviceId(string? raw, out bool changed)
    {
        var value = (raw ?? "").Trim();
        changed = false;
        if (DeviceIdPattern.IsMatch(value))
        {
            return value;
        }
        changed = true;
        var cleaned = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        cleaned = cleaned.TrimStart('-', '_');
        if (cleaned.Length > 64)
        {
            cleaned = cleaned[..64];
        }
        return cleaned.Length == 0 ? "windows-main" : cleaned;
    }

    public static string NormalizeHostname(string? raw)
    {
        // mDNS hostnames: single label (letters, digits, hyphens), no dots.
        // "clipbridge.local" / "clipbridge." inputs reduce to the label "clipbridge".
        var name = (raw ?? "").Trim().TrimEnd('.').ToLowerInvariant();
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

    private static string NormalizeLanIp(string? raw, out bool changed)
    {
        changed = false;
        var value = (raw ?? "").Trim();
        if (value.Length == 0)
        {
            return "";
        }
        if (System.Net.IPAddress.TryParse(value, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return ip.ToString();
        }
        changed = true;
        return "";
    }
}
