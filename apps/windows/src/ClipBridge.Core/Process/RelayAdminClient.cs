using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ClipBridge.Core.Logging;

namespace ClipBridge.Core.Process;

/// <summary>Read-only health/shutdown client for the relay (loopback only).</summary>
public interface IRelayAdminClient
{
    /// <summary>GET /api/v1/health → true when the relay answers ok.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct);

    /// <summary>POST /api/v1/admin/shutdown (auth) → graceful stop of the relay process.</summary>
    Task<bool> RequestShutdownAsync(CancellationToken ct);
}

/// <summary>Real implementation; talks to http://127.0.0.1:&lt;port&gt; only.</summary>
public sealed class HttpRelayAdminClient : IRelayAdminClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _healthUrl;
    private readonly string _shutdownUrl;
    private readonly object _lock = new();

    public HttpRelayAdminClient(int port, string? token = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _healthUrl = $"http://127.0.0.1:{port}/api/v1/health";
        _shutdownUrl = $"http://127.0.0.1:{port}/api/v1/admin/shutdown";
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(_healthUrl, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> RequestShutdownAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _shutdownUrl);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
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
}
