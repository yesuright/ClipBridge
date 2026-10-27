using System.Net.NetworkInformation;

namespace ClipBridge.Core.Net;

/// <summary>Result of a network re-evaluation.</summary>
public sealed record NetworkStatus(
    IReadOnlyList<LanCandidate> Candidates,
    string? PrimaryUrl)
{
    public bool LanAvailable => PrimaryUrl is not null;
}

public sealed record NetworkChangedEventArgs(string? OldPrimaryUrl, string? NewPrimaryUrl, bool LanAvailable);

/// <summary>
/// Event-driven LAN availability watcher. Subscribes to
/// <see cref="NetworkChange.NetworkAddressChanged"/> / <see cref="NetworkChange.NetworkAvailabilityChanged"/>
/// — never enumerates adapters on a timer. <see cref="ForceRefresh"/> is called on resume
/// (sleep/wake), after a manual "check now", and after a relay port fallback.
///
/// Portability notes: the port is retargetable (the relay can move when the configured
/// port is taken), an explicit IP preference is honoured when that address still exists,
/// and evaluation never throws — Windows raises these events from arbitrary threads and
/// an exception there would take the process down.
/// </summary>
public interface INetworkMonitor : IDisposable
{
    event Action<NetworkChangedEventArgs>? Changed;
    NetworkStatus Current { get; }
    void ForceRefresh();
    void UpdatePort(int port);
}

public sealed class NetworkMonitor : INetworkMonitor
{
    private readonly object _lock = new();
    private NetworkStatus _current = new(Array.Empty<LanCandidate>(), null);
    private bool _disposed;
    private int _port;
    private string? _preferredIp;

    public event Action<NetworkChangedEventArgs>? Changed;

    public NetworkMonitor(int port = 8787, string? preferredIp = null)
    {
        _port = port;
        _preferredIp = string.IsNullOrWhiteSpace(preferredIp) ? null : preferredIp.Trim();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        // Initial evaluation (cheap, one-shot at startup).
        ForceRefresh();
    }

    public int Port
    {
        get { lock (_lock) { return _port; } }
    }

    public NetworkStatus Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Follows a relay port change so every displayed URL stays correct.</summary>
    public void UpdatePort(int port)
    {
        lock (_lock)
        {
            if (_port == port)
            {
                return;
            }
            _port = port;
        }
        ForceRefresh();
    }

    public void SetPreferredIp(string? ip)
    {
        lock (_lock)
        {
            _preferredIp = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
        }
        ForceRefresh();
    }

    public void ForceRefresh()
    {
        NetworkStatus next;
        try
        {
            int port;
            string? preferred;
            lock (_lock)
            {
                port = _port;
                preferred = _preferredIp;
            }
            var candidates = LanAddressResolver.PreferIp(
                LanAddressResolver.DiscoverCandidates(port), preferred);
            var primary = LanAddressResolver.Primary(candidates);
            next = new NetworkStatus(candidates, primary?.Url);
        }
        catch
        {
            // Never let adapter enumeration break the app; "no LAN" is a valid state.
            next = new NetworkStatus(Array.Empty<LanCandidate>(), null);
        }

        var old = Current;
        bool changed;
        lock (_lock)
        {
            changed = !EqualsString(old.PrimaryUrl, next.PrimaryUrl);
            _current = next;
        }
        if (!changed)
        {
            return;
        }
        try
        {
            Changed?.Invoke(new NetworkChangedEventArgs(old.PrimaryUrl, next.PrimaryUrl, next.LanAvailable));
        }
        catch
        {
            // A subscriber failing must not kill the network-change thread.
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ForceRefresh();

    private static bool EqualsString(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
    }
}
