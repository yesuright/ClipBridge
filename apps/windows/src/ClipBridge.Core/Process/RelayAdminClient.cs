using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace ClipBridge.Core.Process;

/// <summary>Read-only health/shutdown client for the relay (loopback only).</summary>
public interface IRelayAdminClient
{
    /// <summary>GET /api/v1/health → true when a ClipBridge relay answers.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct);

    /// <summary>POST /api/v1/admin/shutdown (auth) → graceful stop of the relay process.</summary>
    Task<bool> RequestShutdownAsync(CancellationToken ct);
}

/// <summary>
/// Real implementation; talks to http://127.0.0.1:&lt;port&gt; only.
///
/// Two portability details:
/// - The port is retargetable: when 8787 is taken and the app moves its relay, every
///   subsequent health/shutdown call must follow, or the manager would keep probing a
///   port that now belongs to somebody else.
/// - "Healthy" means a ClipBridge relay, not merely "something answered 200". Port 8787 is
///   a popular port; a random local service answering /api/v1/health must be treated as a
///   conflict, never as "our relay is already running".
/// </summary>
public sealed class HttpRelayAdminClient : IRelayAdminClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private string _healthUrl;
    private string _shutdownUrl;
    private bool _disposed;

    public HttpRelayAdminClient(int port, string? token = null)
    {
        // Default .NET proxy handling, unchanged (see docs/PORTABILITY.md, finding P0-2).
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        Port = port;
        (_healthUrl, _shutdownUrl) = Urls(port);
    }

    public int Port { get; private set; }

    private static (string Health, string Shutdown) Urls(int port)
        => ($"http://127.0.0.1:{port}/api/v1/health",
            $"http://127.0.0.1:{port}/api/v1/admin/shutdown");

    /// <summary>Point the admin client at a different local port (after a port fallback).</summary>
    public void UpdatePort(int port)
    {
        lock (_lock)
        {
            if (Port == port)
            {
                return;
            }
            Port = port;
            (_healthUrl, _shutdownUrl) = Urls(port);
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        string url;
        lock (_lock)
        {
            url = _healthUrl;
        }
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return LooksLikeClipBridge(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a health body is a ClipBridge relay. Tolerates older relays that only
    /// returned {"status":"ok"} but requires the shape to be JSON.
    /// </summary>
    public static bool LooksLikeClipBridge(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (doc.RootElement.TryGetProperty("service", out var service)
                && service.ValueKind == JsonValueKind.String)
            {
                return string.Equals(service.GetString(), "clipbridge-relay", StringComparison.OrdinalIgnoreCase);
            }
            return doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<bool> RequestShutdownAsync(CancellationToken ct)
    {
        string url;
        lock (_lock)
        {
            url = _shutdownUrl;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _http.Dispose();
    }
}

/// <summary>Loopback TCP probe used to detect "port already in use" before starting.</summary>
public static class PortProbe
{
    /// <summary>
    /// Is anything currently accepting TCP on 127.0.0.1:port?
    /// </summary>
    public static bool IsLoopbackPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(IPAddress.Loopback, port);
            if (!task.Wait(300))
            {
                return false;
            }
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Can this process bind 0.0.0.0:port right now? Unlike <see cref="IsLoopbackPortOpen"/>
    /// this catches a listener on a specific LAN interface too, which is exactly the case
    /// that makes the relay fail to start.
    /// </summary>
    public static bool IsBindable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try
            {
                listener?.Stop();
            }
            catch
            {
                // best effort
            }
        }
    }
}
