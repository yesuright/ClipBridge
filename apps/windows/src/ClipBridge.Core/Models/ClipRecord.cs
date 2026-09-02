namespace ClipBridge.Core.Models;

/// <summary>Content types supported by the protocol. Milestone 1: text/plain only.</summary>
public static class ClipContentType
{
    public const string TextPlain = "text/plain";

    /// <summary>Protocol-level limit: 2 MiB of UTF-8 text per clip (matches the relay).</summary>
    public const int MaxContentBytes = 2 * 1024 * 1024;
}

/// <summary>
/// A clipboard record as exchanged with the relay.
/// All timestamps are UTC ISO 8601 (RFC 3339), e.g. 2025-06-01T12:00:00.000Z.
/// </summary>
public sealed record ClipRecord
{
    public required string ClipId { get; init; }
    public required string DeviceId { get; init; }
    public required string ContentType { get; init; }
    public required string Content { get; init; }
    public required string ContentHash { get; init; }
    public required string CreatedAt { get; init; }
    public required string ReceivedAt { get; init; }
    public required string Origin { get; init; }
    public required int SizeBytes { get; init; }
}

/// <summary>Optional protocol fields carried in a create request.</summary>
public sealed record ClipResponseEnvelope
{
    public int Count { get; init; }
    public required IReadOnlyList<ClipRecord> Clips { get; init; }
}
