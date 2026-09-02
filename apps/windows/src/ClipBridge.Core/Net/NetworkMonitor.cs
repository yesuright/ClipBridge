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
/// Event-driven LAN availability watcher (Milestone 3). Subscribes to
/// <see cref="NetworkChange.NetworkAddressChanged"/> / <see cref="NetworkChange.NetworkAvailabilityChanged"/>
/// — never enumerates adapters on a timer. <see cref="ForceRefresh"/> is called on resume
/// (sleep/wake) and after a manual "check now".
/// </summary>
public interface INetworkMonitor : IDisposable
{
    event Action<NetworkChangedEventArgs>? Changed;
    NetworkStatus Current { get; }
    void ForceRefresh();
}

public sealed class NetworkMonitor : INetworkMonitor
{
    private readonly object _lock = new();
    private NetworkStatus _current = new(Array.Empty<LanCandidate>(), null);
    private bool _disposed;

    public event Action<NetworkChangedEventArgs>? Changed;

    public NetworkMonitor(int port = 8787)
    {
        Port = port;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        // Initial evaluation (cheap, one-shot at startup).
        ForceRefresh();
    }

    public int Port { get; }

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

    public void ForceRefresh()
    {
        var old = Current;
        var candidates = LanAddressResolver.DiscoverCandidates(Port);
        var primary = LanAddressResolver.Primary(candidates);
        var next = new NetworkStatus(candidates, primary?.Url);

        bool changed;
        lock (_lock)
        {
            changed = !EqualsString(old.PrimaryUrl, next.PrimaryUrl);
            _current = next;
        }
        if (changed)
        {
            Changed?.Invoke(new NetworkChangedEventArgs(old.PrimaryUrl, next.PrimaryUrl, next.LanAvailable));
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
