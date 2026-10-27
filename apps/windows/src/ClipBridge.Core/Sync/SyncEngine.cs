using System.Text;
using ClipBridge.Core.Api;
using ClipBridge.Core.Config;
using ClipBridge.Core.Hashing;
using ClipBridge.Core.Logging;
using ClipBridge.Core.Models;
using ClipBridge.Core.Time;

namespace ClipBridge.Core.Sync;

/// <summary>
/// Core sync orchestrator, platform-neutral and fully unit-testable.
///
/// Responsibilities:
/// - upload local clipboard text (from the host's clipboard listener)
/// - poll the relay for remote clips and apply the newest applicable one
/// - prevent the clipboard loop at the architecture level:
///   1) AppliedRemoteTracker suppresses the echo event our own SetText generates
///   2) UploadedEchoTracker + own-device filter skip our own uploads on poll
///   3) the relay additionally rejects duplicate content within its dedup window
///
/// Reliability: bounded retries for clipboard writes, per-operation exception
/// containment, no tight retry loops (poll interval is fixed and bounded),
/// network failures never crash the engine.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly IClipboardService _clipboard;
    private readonly IRelayClient _relay;
    private readonly ClientConfig _config;
    private readonly IClientLogger _log;
    private readonly AppliedRemoteTracker _applied;
    private readonly UploadedEchoTracker _uploaded;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<DateTime> _clock;
    private Task? _pollTask;
    private bool _disposed;

    /// <summary>
    /// Identical consecutive poll failures are logged once when they appear and then at
    /// most once per this interval (with a repeat count) — an offline relay used to write
    /// one identical `poll_failed` line every poll period forever, which buried the real
    /// lifecycle lines. Recovery is always logged.
    /// </summary>
    private static readonly TimeSpan PollFailureRepeatInterval = TimeSpan.FromMinutes(1);

    private string? _loggedPollError;
    private DateTime _lastPollErrorLoggedAtUtc = DateTime.MinValue;
    private long _pollFailureCount;

    /// <summary>
    /// Server timestamp of the newest remote clip applied in this session.
    /// Older remote clips are never re-applied — the client syncs "up to" the
    /// newest remote clip it has seen, even across restarts with a full history.
    /// </summary>
    private string? _lastAppliedReceivedAt;

    /// <summary>Set by the host (tray) to pause syncing without stopping the process.</summary>
    public volatile bool Paused;

    /// <summary>
    /// One-shot suppression of the NEXT local clipboard event (e.g. the tray "Copy LAN URL"
    /// action must not upload its own clipboard write). Thread-safe via Interlocked.
    /// </summary>
    public volatile int SuppressNextLocalEvents;

    /// <summary>Last non-fatal error message (safe to show in UI; never contains content).</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Raised whenever the sync health changes: LastError transitioned null↔non-null.
    /// The host maps it to SyncState.Active/Offline (Paused is host-controlled).
    /// </summary>
    public event Action<bool>? SyncHealthChanged; // parameter: healthy

    public event Action<ClipRecord>? ClipUploaded;
    public event Action<ClipRecord>? ClipApplied;

    public SyncEngine(
        IClipboardService clipboard,
        IRelayClient relay,
        ClientConfig config,
        IClientLogger? log = null,
        Func<DateTime>? clock = null)
    {
        _clipboard = clipboard;
        _relay = relay;
        _config = config;
        _log = log ?? NullClientLogger.Instance;
        _clock = clock ?? (() => DateTime.UtcNow);
        _applied = new AppliedRemoteTracker(
            TimeSpan.FromSeconds(config.SuppressWindowSeconds), clock);
        _uploaded = new UploadedEchoTracker(
            TimeSpan.FromSeconds(config.UploadMemorySeconds), clock);
    }

    /// <summary>Start the background polling loop (interval from config, 0.5s – 60s).</summary>
    public void StartPolling()
    {
        if (_pollTask is not null)
        {
            return;
        }
        _pollTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await CheckForRemoteAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                try
                {
                    await Task.Delay(_config.PollIntervalMs, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    /// <summary>
    /// Handle a clipboard change reported by the host listener.
    /// <paramref name="text"/> is null when the clipboard holds no text (images, files, …) —
    /// those are safely ignored per Milestone 1.
    /// </summary>
    public async Task OnClipboardChangedAsync(string? text, CancellationToken ct = default)
    {
        if (Paused || text is null)
        {
            return;
        }
        // One-shot host suppression (e.g. "Copy LAN URL"): skip exactly this event.
        if (Interlocked.CompareExchange(ref SuppressNextLocalEvents, 0, 0) > 0)
        {
            Interlocked.Decrement(ref SuppressNextLocalEvents);
            _log.Debug("local_event_suppressed (host one-shot suppression)");
            return;
        }

        var sizeBytes = Encoding.UTF8.GetByteCount(text);
        if (sizeBytes > ClipContentType.MaxContentBytes)
        {
            _log.Warn($"clip_too_large size={sizeBytes} max={ClipContentType.MaxContentBytes} skip");
            return;
        }

        var hash = ContentHasher.Sha256Hex(text);

        // LOOP PREVENTION: this is the echo of our own remote write. Do not re-upload.
        if (_applied.IsRecentlyApplied(hash))
        {
            _log.Debug($"suppressed echo hash={ContentHasher.HashPrefix(hash)}");
            return;
        }

        // Repeated notifications for a copy we just uploaded (the relay would reject
        // the same content inside its dedup window anyway): skip the round-trip.
        if (_uploaded.IsRecentUpload(hash))
        {
            _log.Debug($"suppressed recent upload hash={ContentHasher.HashPrefix(hash)}");
            return;
        }

        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Paused)
            {
                return;
            }
            // Re-check the loop-prevention caches AFTER acquiring the lock: duplicate
            // notifications for the same copy can arrive while the winning event is
            // still uploading (previously these were posted again and relay-rejected
            // with 409 duplicate_content_in_window — wasted round trips for the same event).
            if (_applied.IsRecentlyApplied(hash) || _uploaded.IsRecentUpload(hash))
            {
                return;
            }
            var clip = BuildLocalClip(text);
            var created = await PostWithRetryAsync(clip, ct).ConfigureAwait(false);
            _uploaded.MarkUploaded(created.ClipId, created.ContentHash);
            UpdateHealth(healthy: true);
            LastError = null;
            _log.Info(
                $"clip_uploaded clip_id={created.ClipId} device={created.DeviceId} " +
                $"size={created.SizeBytes} hash={ContentHasher.HashPrefix(created.ContentHash)}");
            ClipUploaded?.Invoke(created);
        }
        catch (RelayApiException ex) when (ex.StatusCode == 409)
        {
            // Already known to the relay (idempotent retry or duplicate-in-window):
            // treat as uploaded so polls skip our own content and repeated
            // notifications for the same copy are ignored.
            _uploaded.MarkUploaded(clipIdFrom409(ex) ?? Guid.NewGuid().ToString(), hash);
            _log.Warn($"clip_upload_duplicate error={ex.ErrorCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested)
        {
            // graceful shutdown in progress
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or RelayApiException or System.Text.Json.JsonException)
        {
            UpdateHealth(healthy: false);
            LastError = SafeExceptionMessage(ex);
            _log.Warn($"clip_upload_failed error={LastError}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// POST the clip with BOUNDED retries on transient failures only (network errors,
    /// timeouts, 5xx). The same clip is reposted under the SAME clip_id every attempt:
    /// the relay's primary key makes the retry idempotent — a lost response followed by
    /// a retry returns 409 duplicate_clip_id, which the caller treats as uploaded, so no
    /// duplicate history entry is ever created. Permanent protocol errors (4xx) are never
    /// retried. Cancellation-aware; no queue — the event is dropped after the bound
    /// (three genuine attempts instead of one).
    /// </summary>
    private async Task<ClipRecord> PostWithRetryAsync(ClipRecord clip, CancellationToken ct)
    {
        var attempts = Math.Max(1, _config.UploadMaxAttempts);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                var delayMs = Math.Max(1, _config.UploadRetryBaseMs) * (attempt - 1);
                _log.Warn(
                    $"upload_retry attempt={attempt}/{attempts} clip_id={clip.ClipId} " +
                    $"size={clip.SizeBytes} delay_ms={delayMs} error={SafeExceptionMessage(lastError!)}");
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
            try
            {
                var created = await _relay.PostClipAsync(clip, ct).ConfigureAwait(false);
                if (attempt > 1)
                {
                    _log.Info($"upload_retry_success attempt={attempt} clip_id={created.ClipId}");
                }
                return created;
            }
            catch (Exception ex) when (IsTransientUploadFailure(ex)
                                       && !ct.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                lastError = ex;
            }
        }
        throw lastError!;
    }

    /// <summary>Transient = worth retrying: transport failure, timeout, JSON decode, server 5xx.</summary>
    private static bool IsTransientUploadFailure(Exception ex) => ex switch
    {
        RelayApiException api => api.StatusCode >= 500,
        _ => ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException,
    };

    private void UpdateHealth(bool healthy)
    {
        var wasHealthy = LastError is null;
        if (healthy == wasHealthy)
        {
            return;
        }
        if (healthy)
        {
            LastError = null;
        }
        SyncHealthChanged?.Invoke(healthy);
    }

    /// <summary>
    /// Poll the relay and apply the newest remote clip that is not our own echo and has
    /// not already been applied. Returns true when a clip was applied.
    /// </summary>
    public async Task<bool> CheckForRemoteAsync(CancellationToken ct = default)
    {
        if (Paused)
        {
            return false;
        }
        try
        {
            var recent = await _relay.GetRecentAsync(20, ct).ConfigureAwait(false);
            foreach (var clip in recent)
            {
                if (clip.DeviceId == _config.DeviceId)
                {
                    continue; // our own device (typically our own uploads)
                }
                if (_uploaded.IsOwnEcho(clip.ClipId))
                {
                    continue; // defensive: exact echo of an upload from this session
                }
                if (_applied.WasApplied(clip.ClipId))
                {
                    continue; // already applied within the suppression window
                }
                // Once we have applied the newest remote clip, never walk back into
                // older history — prevents cascading re-applies of stale clips.
                // (Strictly old only: equal server timestamps still count as candidates.)
                if (_lastAppliedReceivedAt is not null
                    && string.CompareOrdinal(clip.ReceivedAt, _lastAppliedReceivedAt) < 0)
                {
                    continue;
                }
                if (clip.ContentType != ClipContentType.TextPlain)
                {
                    continue; // Milestone 1: text/plain only
                }
                if (!HashMatches(clip))
                {
                    continue; // integrity check failed — never apply corrupt content
                }
                // Do not clobber a newer local (or identical remote) clipboard state.
                var current = _clipboard.TryGetText();
                if (current is not null && string.Equals(current, clip.Content, StringComparison.Ordinal))
                {
                    continue;
                }
                // The relay answered, so the transport is healthy again regardless of
                // whether the clipboard write itself succeeded.
                var applied = await ApplyRemoteAsync(clip, ct).ConfigureAwait(false);
                NotePollRecovered();
                return applied;
            }
            NotePollRecovered();
            UpdateHealth(healthy: true);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or RelayApiException or System.Text.Json.JsonException)
        {
            UpdateHealth(healthy: false);
            LastError = SafeExceptionMessage(ex);
            LogPollFailure(LastError);
            return false;
        }
    }

    /// <summary>First occurrence of an error is a WARN; identical repeats are rate-limited.</summary>
    private void LogPollFailure(string message)
    {
        var now = _clock();
        if (!string.Equals(message, _loggedPollError, StringComparison.Ordinal))
        {
            _loggedPollError = message;
            _pollFailureCount = 1;
            _lastPollErrorLoggedAtUtc = now;
            _log.Warn($"poll_failed error={message}");
            return;
        }
        _pollFailureCount++;
        if (now - _lastPollErrorLoggedAtUtc >= PollFailureRepeatInterval)
        {
            _lastPollErrorLoggedAtUtc = now;
            _log.Warn($"poll_failed error={message} repeats={_pollFailureCount} (still offline)");
        }
    }

    private void NotePollRecovered()
    {
        if (_loggedPollError is null)
        {
            return;
        }
        _log.Info($"poll_recovered after_error={_loggedPollError} failed_polls={_pollFailureCount}");
        _loggedPollError = null;
        _pollFailureCount = 0;
    }

    /// <summary>Manual "check now" (tray action).</summary>
    public Task<bool> CheckNowAsync(CancellationToken ct = default) => CheckForRemoteAsync(ct);

    // ---------------------------------------------------------------- internals

    private bool HashMatches(ClipRecord clip)
    {
        var expected = ContentHasher.Sha256Hex(clip.Content);
        if (!string.Equals(expected, clip.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"clip_hash_mismatch clip_id={clip.ClipId} skip");
            return false;
        }
        if (clip.SizeBytes != Encoding.UTF8.GetByteCount(clip.Content))
        {
            _log.Warn($"clip_size_mismatch clip_id={clip.ClipId} skip");
            return false;
        }
        return true;
    }

    private async Task<bool> ApplyRemoteAsync(ClipRecord clip, CancellationToken ct)
    {
        await _operationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Paused)
            {
                return false;
            }
            // Mark BEFORE SetText: the WM_CLIPBOARDUPDATE echo can arrive on the UI
            // thread while we are still here — the suppression must already be armed.
            _applied.MarkApplied(clip.ClipId, clip.ContentHash);
            _lastAppliedReceivedAt = clip.ReceivedAt;

            Exception? lastError = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    _clipboard.SetText(clip.Content);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 5)
                    {
                        _log.Debug($"clipboard_busy retry={attempt}");
                        await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
                    }
                }
            }

            if (lastError is not null)
            {
                _log.Error($"clipboard_apply_failed clip_id={clip.ClipId}", lastError);
                return false;
            }

            _log.Info(
                $"clip_applied clip_id={clip.ClipId} device={clip.DeviceId} " +
                $"size={clip.SizeBytes} hash={ContentHasher.HashPrefix(clip.ContentHash)}");
            ClipApplied?.Invoke(clip);
            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private ClipRecord BuildLocalClip(string text)
    {
        var now = Timestamps.UtcNowIso();
        return new ClipRecord
        {
            ClipId = Guid.NewGuid().ToString(),
            DeviceId = _config.DeviceId,
            ContentType = ClipContentType.TextPlain,
            Content = text,
            ContentHash = ContentHasher.Sha256Hex(text),
            CreatedAt = now,
            ReceivedAt = now, // server overrides; client value unused on upload
            Origin = "local",
            SizeBytes = Encoding.UTF8.GetByteCount(text),
        };
    }

    private static string? clipIdFrom409(RelayApiException ex)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ex.ResponseBody ?? "");
            return doc.RootElement.TryGetProperty("clip_id", out var id) ? id.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string SafeExceptionMessage(Exception ex)
    {
        var message = ex switch
        {
            HttpRequestException http => $"http_error:{(int)(http.StatusCode ?? 0)}",
            TaskCanceledException => "timeout",
            RelayApiException api => $"relay_{api.StatusCode}:{api.ErrorCode} path={api.RequestPath}",
            _ => ex.Message,
        };
        return message.Replace("\r", " ").Replace("\n", " ");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        try
        {
            _pollTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // shutdown races are non-fatal
        }
        _cts.Dispose();
        _operationLock.Dispose();
    }
}
