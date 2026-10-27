using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClipBridge.Core.Config;
using ClipBridge.Core.Models;

namespace ClipBridge.Core.Api;

public sealed class RelayApiException : Exception
{
    public int StatusCode { get; }
    public string? ErrorCode { get; }
    public string? ResponseBody { get; }

    /// <summary>
    /// Request path that produced the error (no token, no content). A 404 on our own relay
    /// is otherwise indistinguishable from "wrong base address" in a log line.
    /// </summary>
    public string? RequestPath { get; }

    public RelayApiException(
        int statusCode, string? errorCode, string? responseBody, string? message = null,
        string? requestPath = null)
        : base(message ?? $"relay returned HTTP {statusCode} ({errorCode ?? "unknown"})")
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        ResponseBody = responseBody;
        RequestPath = requestPath;
    }
}

public interface IRelayClient
{
    Task<ClipRecord> PostClipAsync(ClipRecord clip, CancellationToken ct);
    Task<ClipRecord?> GetLatestAsync(CancellationToken ct);
    Task<IReadOnlyList<ClipRecord>> GetRecentAsync(int limit, CancellationToken ct);
}

/// <summary>
/// HTTP client for the ClipBridge relay. Always sends
/// `Authorization: Bearer &lt;token&gt;`; never logs content or tokens.
/// </summary>
public sealed class RelayClient : IRelayClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ClientConfig _config;

    public RelayClient(ClientConfig config, HttpMessageHandler? handler = null)
    {
        _config = config;
        // Proxy behaviour is intentionally the .NET default here: the user runs ClipBridge
        // with a system proxy and asked for that to stay unchanged. A proxy that intercepts
        // loopback is a known open item (see docs/PORTABILITY.md, finding P0-2).
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = config.ApiBaseUri;
        _http.Timeout = TimeSpan.FromSeconds(config.HttpTimeoutSeconds);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.DeviceToken);
    }

    public async Task<ClipRecord> PostClipAsync(ClipRecord clip, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            "clips",
            new
            {
                clip_id = clip.ClipId,
                device_id = clip.DeviceId,
                content_type = clip.ContentType,
                content = clip.Content,
                content_hash = clip.ContentHash,
                created_at = clip.CreatedAt,
                origin = clip.Origin,
            },
            ClipJson.Options,
            ct);
        return await ReadRecordAsync(response, expected: HttpStatusCode.Created, ct);
    }

    public async Task<ClipRecord?> GetLatestAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("clips/latest", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        return await ReadRecordAsync(response, expected: HttpStatusCode.OK, ct);
    }

    public async Task<IReadOnlyList<ClipRecord>> GetRecentAsync(int limit, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"clips?limit={limit}", ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await ToExceptionAsync(response, ct);
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            var envelope = JsonSerializer.Deserialize<ClipResponseEnvelope>(body, ClipJson.Options);
            return envelope?.Clips ?? Array.Empty<ClipRecord>();
        }
        catch (JsonException)
        {
            // Malformed body: treat as "nothing new" — poller must keep running.
            return Array.Empty<ClipRecord>();
        }
    }

    private static async Task<ClipRecord> ReadRecordAsync(
        HttpResponseMessage response, HttpStatusCode expected, CancellationToken ct)
    {
        if (response.StatusCode != expected)
        {
            throw await ToExceptionAsync(response, ct);
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ClipRecord>(body, ClipJson.Options)
               ?? throw new RelayApiException((int)response.StatusCode, "invalid_response", body);
    }

    private static async Task<RelayApiException> ToExceptionAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        string? errorCode = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                errorCode = err.GetString();
            }
        }
        catch (JsonException)
        {
            // non-JSON error body — keep raw text only for diagnostics
        }
        return new RelayApiException((int)response.StatusCode, errorCode, body,
            requestPath: response.RequestMessage?.RequestUri?.PathAndQuery);
    }

    public void Dispose() => _http.Dispose();
}
