namespace ClipBridge.Core.Status;

/// <summary>
/// Shared JSON options for status snapshots: enum values as names ("Running") so CLI
/// output and future UI get readable values, single-line for the named-pipe protocol.
/// </summary>
public static class StatusJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>
/// Domain status model (Milestone 3). The UI layer (tray today, a real settings/status
/// window later) consumes <see cref="AppStatusModel.Snapshot"/> and the
/// <see cref="AppStatusModel.Changed"/> event — it never has to parse log lines.
/// All enum values are UI-neutral strings; a future UI maps them to labels/colors.
/// </summary>
public enum RelayState
{
    /// <summary>Relay management is disabled or no packaged relay is present (dev mode).</summary>
    Disabled,
    Starting,
    Running,
    Restarting,
    Failed,
    Stopped,
}

public enum SyncState
{
    Active,
    Paused,
    /// <summary>Polls are failing (relay unreachable, no network).</summary>
    Offline,
}

public enum NetworkState
{
    Unknown,
    LanAvailable,
    NoLan,
    /// <summary>The preferred LAN address changed since the last check (Shortcut URL may need updating).</summary>
    IpChanged,
}

public enum AppState
{
    Starting,
    Running,
    Stopping,
    Stopped,
    /// <summary>Missing/invalid config — the app is not syncing.</summary>
    Misconfigured,
}

public enum MdnsState
{
    /// <summary>Advertising is not in use (disabled or unsupported).</summary>
    Off,
    Advertising,
    /// <summary>Another host answered for the same .local name — advertising stopped.</summary>
    Conflict,
    Errored,
}

/// <summary>Immutable snapshot of the whole app status. Safe to serialize for future UI.</summary>
public sealed record AppStatusSnapshot
{
    public AppState App { get; init; } = AppState.Starting;
    public RelayState Relay { get; init; } = RelayState.Stopped;
    public SyncState Sync { get; init; } = SyncState.Active;
    public NetworkState Network { get; init; } = NetworkState.Unknown;
    public MdnsState Mdns { get; init; } = MdnsState.Off;

    /// <summary>Current primary LAN URL for Shortcuts, e.g. http://192.168.1.14:8787; null = no LAN.</summary>
    public string? LanUrl { get; init; }

    /// <summary>The advertised .local name, e.g. clipbridge.local; null = not advertising.</summary>
    public string? LanHostname { get; init; }

    /// <summary>When true the LAN address changed since startup (Shortcut may need an update).</summary>
    public bool LanIpChanged { get; init; }

    public string? LastError { get; init; }

    public DateTime? LastSyncAtUtc { get; init; }
    public string? LastSyncDeviceId { get; init; }
    public long? LastSyncBytes { get; init; }
    public string? LastSyncDirection { get; init; } // "upload" | "apply"

    /// <summary>Startup registration state (Start with Windows).</summary>
    public bool StartupEnabled { get; init; }
}

/// <summary>
/// Thread-safe observable status container. Producers (relay manager, network monitor,
/// sync engine, tray) post updates; the UI observes. Events always fire on the posting
/// thread; the tray subscribes and marshals to the UI thread.
/// </summary>
public sealed class AppStatusModel
{
    private readonly object _lock = new();
    private AppStatusSnapshot _current = new() { App = AppState.Starting };

    public event Action<AppStatusSnapshot>? Changed;

    public AppStatusSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Update(AppStatusSnapshot next)
    {
        bool changed;
        lock (_lock)
        {
            changed = !_current.Equals(next);
            _current = next;
        }
        if (changed)
        {
            Changed?.Invoke(next);
        }
    }

    /// <summary>Convenience update that copies the current snapshot and replaces one field.</summary>
    public void Patch(Action<AppStatusSnapshotBuilder> patch)
    {
        AppStatusSnapshot current;
        lock (_lock)
        {
            current = _current;
        }
        var builder = new AppStatusSnapshotBuilder(current);
        patch(builder);
        Update(builder.Build());
    }
}

/// <summary>Small immutable-builder for patching snapshots without copying every field.</summary>
public sealed class AppStatusSnapshotBuilder
{
    private AppStatusSnapshot _s;

    public AppStatusSnapshotBuilder(AppStatusSnapshot from) => _s = from;

    public AppStatusSnapshotBuilder With(AppState v) { _s = _s with { App = v }; return this; }
    public AppStatusSnapshotBuilder With(RelayState v) { _s = _s with { Relay = v }; return this; }
    public AppStatusSnapshotBuilder With(SyncState v) { _s = _s with { Sync = v }; return this; }
    public AppStatusSnapshotBuilder With(NetworkState v) { _s = _s with { Network = v }; return this; }
    public AppStatusSnapshotBuilder With(MdnsState v) { _s = _s with { Mdns = v }; return this; }

    // Fluent aliases used by the host components.
    public AppStatusSnapshotBuilder WithRelay(RelayState v) => With(v);
    public AppStatusSnapshotBuilder WithSync(SyncState v) => With(v);
    public AppStatusSnapshotBuilder WithNetwork(NetworkState v) => With(v);
    public AppStatusSnapshotBuilder WithMdns(MdnsState v) => With(v);
    public AppStatusSnapshotBuilder WithLanUrl(string? v) { _s = _s with { LanUrl = v }; return this; }
    public AppStatusSnapshotBuilder WithLanHostname(string? v) { _s = _s with { LanHostname = v }; return this; }
    public AppStatusSnapshotBuilder WithLanIpChanged(bool v) { _s = _s with { LanIpChanged = v }; return this; }
    public AppStatusSnapshotBuilder WithLastError(string? v) { _s = _s with { LastError = v }; return this; }
    public AppStatusSnapshotBuilder WithStartupEnabled(bool v) { _s = _s with { StartupEnabled = v }; return this; }

    public AppStatusSnapshotBuilder WithLastSync(DateTime? atUtc, string? deviceId, long? bytes, string? direction)
    {
        _s = _s with { LastSyncAtUtc = atUtc, LastSyncDeviceId = deviceId, LastSyncBytes = bytes, LastSyncDirection = direction };
        return this;
    }

    public AppStatusSnapshot Build() => _s;
}
