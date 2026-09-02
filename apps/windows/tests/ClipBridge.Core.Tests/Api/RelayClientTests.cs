using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClipBridge.Core.Api;
using ClipBridge.Core.Config;
using ClipBridge.Core.Hashing;
using ClipBridge.Core.Models;
using Xunit;

namespace ClipBridge.Core.Tests.Api;

public sealed class StubHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> OnRequest = _ => new(HttpStatusCode.OK);
    public HttpRequestMessage? LastRequest;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(OnRequest(request));
    }
}

public class RelayClientTests
{
    private static ClientConfig Config() => new()
    {
        RelayUrl = "http://relay.test",
        DeviceId = "windows-main",
        DeviceToken = "secret-device-token",
        PollIntervalMs = 2000,
    };

    private const string CreatedResponse =
        "{\"clip_id\":\"11111111-2222-3333-4444-555555555555\",\"device_id\":\"windows-main\"," +
        "\"content_type\":\"text/plain\",\"content\":\"Hello from ClipBridge\"," +
        "\"content_hash\":\"b1b7b647b087a114c80f9bbae38e04e7ce0bc0f7d24ff177565742fc04357a2e\"," +
        "\"created_at\":\"2025-06-01T12:00:00.000Z\",\"received_at\":\"2025-06-01T12:00:00.300Z\"," +
        "\"origin\":\"local\",\"size_bytes\":21}";

    private static ClipRecord SampleClip() => new()
    {
        ClipId = "11111111-2222-3333-4444-555555555555",
        DeviceId = "windows-main",
        ContentType = "text/plain",
        Content = "Hello from ClipBridge",
        ContentHash = "b1b7b647b087a114c80f9bbae38e04e7ce0bc0f7d24ff177565742fc04357a2e",
        CreatedAt = "2025-06-01T12:00:00.000Z",
        ReceivedAt = "2025-06-01T12:00:00.300Z",
        Origin = "local",
        SizeBytes = 21,
    };

    [Fact]
    public async Task SendsBearerToken()
    {
        var handler = new StubHandler { OnRequest = _ => new(HttpStatusCode.NotFound) };
        using var client = new RelayClient(Config(), handler);

        await client.GetLatestAsync(CancellationToken.None);

        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("secret-device-token", auth.Parameter);
        Assert.Equal("http://relay.test/api/v1/clips/latest", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task PostClip_SerializesSnakeCaseFieldNames()
    {
        var handler = new StubHandler
        {
            OnRequest = _ => new(HttpStatusCode.Created)
            {
                Content = new StringContent(CreatedResponse, Encoding.UTF8, "application/json"),
            },
        };
        using var client = new RelayClient(Config(), handler);
        var clip = SampleClip();

        var result = await client.PostClipAsync(clip, CancellationToken.None);

        Assert.Equal(clip.ClipId, result.ClipId);
        Assert.Equal(21, result.SizeBytes);
        var body = await handler.LastRequest!.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("clip_id", out _), "field clip_id missing");
        Assert.True(root.TryGetProperty("device_id", out _), "field device_id missing");
        Assert.True(root.TryGetProperty("content_hash", out _), "field content_hash missing");
        Assert.True(root.TryGetProperty("created_at", out _), "field created_at missing");
        Assert.Equal("Hello from ClipBridge", root.GetProperty("content").GetString());
    }

    [Fact]
    public async Task PostClip_UploadsUnicodeContent()
    {
        var handler = new StubHandler
        {
            OnRequest = req => new(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        clip_id = "11111111-2222-3333-4444-555555555555",
                        device_id = "windows-main",
                        content_type = "text/plain",
                        content = "你好，跨设备剪贴板 🐱🚀",
                        content_hash = ContentHasher.Sha256Hex("你好，跨设备剪贴板 🐱🚀"),
                        created_at = "2025-06-01T12:00:00.000Z",
                        received_at = "2025-06-01T12:00:00.300Z",
                        origin = "local",
                        size_bytes = 80,
                    }),
                    Encoding.UTF8, "application/json"),
            },
        };
        using var client = new RelayClient(Config(), handler);

        var content = "你好，跨设备剪贴板 🐱🚀";
        var result = await client.PostClipAsync(SampleClip() with
        {
            Content = content,
            ContentHash = ContentHasher.Sha256Hex(content),
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(content),
        }, CancellationToken.None);

        Assert.Equal(content, result.Content);
    }

    [Fact]
    public async Task GetLatest_404_ReturnsNull()
    {
        var handler = new StubHandler { OnRequest = _ => new(HttpStatusCode.NotFound) };
        using var client = new RelayClient(Config(), handler);
        Assert.Null(await client.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetRecent_ParsesEnvelope()
    {
        var handler = new StubHandler
        {
            OnRequest = _ => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"count\":2,\"clips\":[" +
                    "{\"clip_id\":\"aaaaaaaa-1111-2222-3333-444444444444\",\"device_id\":\"iphone\"," +
                    "\"content_type\":\"text/plain\",\"content\":\"remote one\"," +
                    "\"content_hash\":\"h1\",\"created_at\":\"2025-06-01T12:00:00.000Z\"," +
                    "\"received_at\":\"2025-06-01T12:00:00.000Z\",\"origin\":\"local\",\"size_bytes\":10}," +
                    "{\"clip_id\":\"bbbbbbbb-1111-2222-3333-444444444444\",\"device_id\":\"ipad\"," +
                    "\"content_type\":\"text/plain\",\"content\":\"remote two\",\"content_hash\":\"h2\"," +
                    "\"created_at\":\"2025-06-01T12:00:00.000Z\",\"received_at\":\"2025-06-01T12:00:00.000Z\"," +
                    "\"origin\":\"local\",\"size_bytes\":10}]}",
                    Encoding.UTF8, "application/json"),
            },
        };
        using var client = new RelayClient(Config(), handler);

        var clips = await client.GetRecentAsync(20, CancellationToken.None);

        Assert.Equal(2, clips.Count);
        Assert.Equal("remote one", clips[0].Content);
        Assert.Equal("iphone", clips[0].DeviceId);
    }

    [Fact]
    public async Task Unauthorized_ThrowsTypedException()
    {
        var handler = new StubHandler
        {
            OnRequest = _ => new(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"unauthorized\"}", Encoding.UTF8, "application/json"),
            },
        };
        using var client = new RelayClient(Config(), handler);

        var ex = await Assert.ThrowsAsync<RelayApiException>(
            () => client.GetLatestAsync(CancellationToken.None));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("unauthorized", ex.ErrorCode);
    }

    [Fact]
    public async Task Duplicate_ThrowsWithErrorCode()
    {
        var handler = new StubHandler
        {
            OnRequest = _ => new(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    "{\"error\":\"duplicate_clip_id\",\"clip_id\":\"11111111-2222-3333-4444-555555555555\"}",
                    Encoding.UTF8, "application/json"),
            },
        };
        using var client = new RelayClient(Config(), handler);

        var ex = await Assert.ThrowsAsync<RelayApiException>(
            () => client.PostClipAsync(SampleClip(), CancellationToken.None));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("duplicate_clip_id", ex.ErrorCode);
    }

    [Fact]
    public async Task GetRecent_MalformedResponse_DoesNotThrow_Unexpectedly()
    {
        var handler = new StubHandler
        {
            OnRequest = _ => new(HttpStatusCode.OK)
            {
                Content = new StringContent("not json at all", Encoding.UTF8, "application/json"),
            },
        };
        using var client = new RelayClient(Config(), handler);
        var clips = await client.GetRecentAsync(20, CancellationToken.None);
        Assert.Empty(clips);
    }
}
