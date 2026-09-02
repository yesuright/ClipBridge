using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ClipBridge.Core.Net;

/// <summary>
/// In-app mDNS responder (Milestone 3) — advertises <c>&lt;hostname&gt;.local</c> on the LAN
/// so the iPhone Shortcuts can use a stable URL like http://clipbridge.local:8787 instead of a
/// DHCP-dependent IP. Pure application-level implementation:
/// - binds UDP 5353, joins 224.0.0.251, answers A/ANY queries for one hostname
/// - announces on start, sends TTL-0 goodbye on stop, re-announces when the IP changes
/// - startup probe detects another host already claiming the name (conflict → stop advertising)
/// No system service is installed; nothing outside the app process is touched.
///
/// NOTE (verified limitation): this only advertises. Whether iOS/Windows actually resolve the
/// name depends on the network stack (broadcast/multicast on the current Wi-Fi, VPN routing)
/// — see docs/LAN_HOSTNAME.md / DAILY_USE.md.
/// </summary>
public sealed class MdnsResponder : IDisposable
{
    private const int MdnsPort = 5353;
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    private readonly object _lock = new();
    private readonly Func<IReadOnlyList<LanCandidate>> _addressSource;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _conflicted;
    private bool _disposed;

    public event Action<MdnsResponderEvent>? Event;

    public MdnsResponder(string hostname, Func<IReadOnlyList<LanCandidate>> addressSource)
    {
        Hostname = NormalizeHostname(hostname);
        _addressSource = addressSource;
    }

    public string Hostname { get; }

    /// <summary>Full advertised name, e.g. clipbridge.local.</summary>
    public string FullName => Hostname + ".local";

    public bool IsAdvertising => _loop is { IsCompleted: false } && !_conflicted;

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MdnsResponder));
        }
        if (_loop is { IsCompleted: false })
        {
            return;
        }
        _conflicted = false;
        try
        {
            // SO_REUSEADDR before bind: the mDNS port 5353 is commonly shared with other
            // responders (e.g. Edge, Bonjour, Docker) — Windows delivers multicast to all
            // sockets in the group, so coexistence is the correct mDNS behaviour.
            // NOTE: do not set ReceiveTimeout here — the async ReceiveAsync path on
            // Windows busy-loops when combined with it.
            _client = new UdpClient();
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            // NEVER loop our own datagrams back: our responses echo the question section,
            // and an echo can make us answer our own answers in a self-amplifying
            // ping-pong loop (observed as 100 % of one core).
            _client.MulticastLoopback = false;
            JoinGroupOnAllInterfaces();
        }
        catch (Exception)
        {
            // Port 5353 unavailable (hard block by another app): never crash the host —
            // advertising is a best-effort feature; the app keeps running with IP URLs.
            RaiseEvent(MdnsResponderEvent.Error);
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            // Startup probe: is the name already claimed by another host?
            var conflict = await ProbeForConflictAsync(ct).ConfigureAwait(false);
            if (conflict)
            {
                lock (_lock)
                {
                    _conflicted = true;
                }
                RaiseEvent(MdnsResponderEvent.Conflict);
                return;
            }
            await AnnounceAsync(ttl: 120, ct).ConfigureAwait(false);
            RaiseEvent(MdnsResponderEvent.Started);

            var buffer = new byte[1500];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _client!.ReceiveAsync(ct).ConfigureAwait(false);
                    if (result.Buffer.Length > 0)
                    {
                        HandleQuery(result.Buffer);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Transient multicast read error — ventilate: a bare loop without a
                    // delay turns the retry into a 100 %-core exception storm.
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Unexpected receive error (e.g. access/port reuse quirks on Windows):
                    // keep the responder alive but throttle, never spin.
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                _ = buffer; // receive buffer reused by ReceiveAsync internally
            }
        }
        catch (Exception ex)
        {
            RaiseEvent(MdnsResponderEvent.Error);
            _ = ex;
        }
        finally
        {
            try
            {
                if (!_conflicted)
                {
                    await AnnounceAsync(ttl: 0, ct: CancellationToken.None, timeoutMs: 500).ConfigureAwait(false);
                }
                _client?.Client.Close();
            }
            catch
            {
                // best effort goodbye
            }
        }
    }

    /// <summary>Re-announce with the current addresses (e.g. after the LAN IP changed).</summary>
    public void Reannounce()
    {
        if (!IsAdvertising)
        {
            if (_conflicted || _loop is null)
            {
                return;
            }
        }
        RaiseEvent(MdnsResponderEvent.Reannounced);
        if (_loop is { IsCompleted: false } && !_conflicted)
        {
            // Fire-and-forget announcement from a fresh socket; the main loop socket
            // is busy reading, and sending through it is still fine (UDP is half-duplex
            // from our side), so just send on the shared client.
            _ = AnnounceAsync(ttl: 120, CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------- internals

    /// <summary>
    /// Join the mDNS multicast group on EVERY connected interface. The Windows DNS client
    /// picks an arbitrary (often virtual, e.g. WSL/Hyper-V) interface for its .local query;
    /// a default-membership-only socket silently misses queries arriving on other adapters.
    /// </summary>
    private void JoinGroupOnAllInterfaces()
    {
        var joined = false;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                foreach (var uni in nic.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        continue;
                    }
                    try
                    {
                        _client!.JoinMulticastGroup(MdnsGroup, uni.Address);
                        joined = true;
                        break; // one membership per interface is enough
                    }
                    catch
                    {
                        // some adapters reject multicast joins — try the next one
                    }
                }
            }
        }
        catch
        {
            // enumeration failure — fall back to the default membership
        }
        if (!joined)
        {
            try
            {
                _client!.JoinMulticastGroup(MdnsGroup);
            }
            catch
            {
                // start() already wraps this in try — port level problem → Error event
                throw;
            }
        }
    }

    private IEnumerable<IPAddress> CurrentAddresses()
    {
        var seen = new HashSet<string>();
        foreach (var candidate in _addressSource())
        {
            if (seen.Add(candidate.IpAddress) && IPAddress.TryParse(candidate.IpAddress, out var ip))
            {
                yield return ip;
            }
        }
    }

    private static string NormalizeHostname(string raw)
    {
        var name = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0 || name.Length > 63 || name.StartsWith('-') || name.EndsWith('-'))
        {
            return "clipbridge";
        }
        return name;
    }

    private async Task<bool> ProbeForConflictAsync(CancellationToken ct)
    {
        // Send a query for our name, then listen briefly for someone else's A answer.
        try
        {
            await SendMulticastAsync(BuildQueryMessage(), ct).ConfigureAwait(false);
        }
        catch
        {
            return false; // probing failed — treat as no conflict, advertise anyway
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
        var own = new HashSet<string>(CurrentAddresses().Select(a => a.ToString()));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                {
                    break;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(remaining);
                var result = await _client!.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                foreach (var ip in MdnsCodec.ParseARecords(result.Buffer))
                {
                    if (!own.Contains(ip.ToString()))
                    {
                        // Another host (or a stale record from an old app instance on a
                        // different address) claims the name → conflict.
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
        }
        return false;
    }

    private byte[] BuildQueryMessage()
    {
        // id=0, flags=0 (query), QDCOUNT=1, question: FullName A IN
        var name = MdnsCodec.EncodeName(FullName);
        var message = new byte[12 + name.Length + 4];
        message[4] = 0; // QDCOUNT high
        message[5] = 1;
        name.CopyTo(message, 12);
        var pos = 12 + name.Length;
        message[pos] = 0x00;
        message[pos + 1] = (byte)MdnsCodec.TypeA;
        message[pos + 2] = 0x00;
        message[pos + 3] = (byte)MdnsCodec.ClassIn;
        return message;
    }

    private void HandleQuery(byte[] message)
    {
        // mDNS rule: respond to QUERIES only. A response also carries a question section
        // (we echo it), so without this check we would answer our own answers forever.
        if (message.Length < 12 || (message[2] & 0x80) != 0)
        {
            return;
        }
        IReadOnlyList<MdnsCodec.Query> queries;
        try
        {
            queries = MdnsCodec.ParseQueries(message);
        }
        catch (MdnsCodec.MalformedMessageException)
        {
            return;
        }
        var matched = false;
        foreach (var query in queries)
        {
            if (!query.Name.Equals(FullName, StringComparison.OrdinalIgnoreCase))
            {
                continue; // not for us
            }
            if (query.Type != MdnsCodec.TypeA && query.Type != MdnsCodec.TypeAny)
            {
                continue;
            }
            matched = true;
            var response = MdnsCodec.BuildAResponse(0, query, CurrentAddresses());
            Send(response);
        }
        if (matched)
        {
            RaiseEvent(MdnsResponderEvent.Answered);
        }
    }

    private async Task AnnounceAsync(uint ttl, CancellationToken ct, int timeoutMs = 1500)
    {
        var addresses = CurrentAddresses().ToList();
        if (addresses.Count == 0)
        {
            return;
        }
        try
        {
            var response = MdnsCodec.BuildAResponse(0, new MdnsCodec.Query(FullName, MdnsCodec.TypeA, MdnsCodec.ClassIn), addresses, ttl);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            await SendMulticastAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // announcements are best effort
        }
    }

    private async Task SendMulticastAsync(byte[] message, CancellationToken ct)
    {
        await _client!.SendAsync(message, new IPEndPoint(MdnsGroup, MdnsPort), ct).ConfigureAwait(false);
    }

    private void Send(byte[] message)
    {
        try
        {
            var task = _client?.SendAsync(message, message.Length, new IPEndPoint(MdnsGroup, MdnsPort));
            if (task is null)
            {
                return;
            }
            _ = task.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        RaiseEvent(MdnsResponderEvent.SendFailed);
                    }
                }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception)
        {
            RaiseEvent(MdnsResponderEvent.SendFailed);
        }
    }

    private void RaiseEvent(MdnsResponderEvent kind) => Event?.Invoke(kind);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _cts?.Cancel();
            _client?.Client.Close();
        }
        catch
        {
            // closing already-closed socket is fine
        }
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // shutdown race — acceptable
        }
        _cts?.Dispose();
        _client?.Dispose();
    }
}

public enum MdnsResponderEvent
{
    Started,
    Answered,
    Conflict,
    Error,
    Reannounced,
    SendFailed,
}
