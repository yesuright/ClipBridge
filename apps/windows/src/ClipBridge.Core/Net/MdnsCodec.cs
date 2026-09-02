using System.Buffers.Binary;
using System.Net;

namespace ClipBridge.Core.Net;

/// <summary>
/// Minimal DNS wire codec for the ClipBridge mDNS responder (Milestone 3).
/// Only what is needed: parse a query (questions: name + QTYPE + QCLASS) and build a
/// response with A records. Pure functions — fully unit-testable without sockets.
/// </summary>
public static class MdnsCodec
{
    public const ushort TypeA = 1;
    public const ushort TypeAny = 255;
    public const ushort ClassIn = 1;

    /// <summary>A single question extracted from a DNS message.</summary>
    public readonly record struct Query(string Name, ushort Type, ushort Class);

    public sealed class MalformedMessageException : Exception
    {
        public MalformedMessageException(string message) : base(message) { }
    }

    /// <summary>Parse all questions from a DNS message (header + question section).</summary>
    public static IReadOnlyList<Query> ParseQueries(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12)
        {
            throw new MalformedMessageException("message too short");
        }
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(4, 2)); // QDCOUNT is at offset 4
        var offset = 12;
        var queries = new List<Query>();
        for (var i = 0; i < qdCount; i++)
        {
            var (name, next) = ParseName(message, offset);
            if (next + 4 > message.Length)
            {
                throw new MalformedMessageException("question truncated");
            }
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(next, 2));
            var cls = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(next + 2, 2));
            queries.Add(new Query(name, type, cls));
            offset = next + 4;
        }
        return queries;
    }

    /// <summary>Parse a DNS name from a message; returns (name, offset after name).</summary>
    public static (string Name, int NextOffset) ParseName(ReadOnlySpan<byte> message, int offset)
    {
        var labels = new List<string>();
        var pos = offset;
        while (true)
        {
            if (pos >= message.Length)
            {
                throw new MalformedMessageException("name exceeds message");
            }
            var len = message[pos];
            pos++;
            if (len == 0)
            {
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                // Compression pointer (used by queries rarely, but be safe).
                if (pos >= message.Length)
                {
                    throw new MalformedMessageException("truncated pointer");
                }
                var pointer = ((len & 0x3F) << 8) | message[pos];
                pos++;
                var (pointed, _) = ParseName(message, pointer);
                labels.Add(pointed);
                break;
            }
            if ((len & 0xC0) != 0)
            {
                throw new MalformedMessageException("unsupported label type");
            }
            if (pos + len > message.Length)
            {
                throw new MalformedMessageException("label exceeds message");
            }
            labels.Add(System.Text.Encoding.ASCII.GetString(message.Slice(pos, len)));
            pos += len;
        }
        return (string.Join(".", labels), pos);
    }

    /// <summary>Encode a DNS name (labels) with length prefixes.</summary>
    public static byte[] EncodeName(string name)
    {
        var parts = name.TrimEnd('.').Split('.');
        var buffer = new List<byte>();
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 63)
            {
                throw new ArgumentException("invalid DNS label", nameof(name));
            }
            buffer.Add((byte)part.Length);
            buffer.AddRange(System.Text.Encoding.ASCII.GetBytes(part));
        }
        buffer.Add(0);
        return buffer.ToArray();
    }

    /// <summary>
    /// Build a DNS response for the given question with A records.
    /// flags: standard response + authoritative (0x8400); answers use cache-flush (0x8001)
    /// as recommended for mDNS shared records.
    /// </summary>
    public static byte[] BuildAResponse(ushort id, Query question, IEnumerable<IPAddress> addresses, uint ttlSeconds = 120)
    {
        var nameBytes = EncodeName(question.Name);
        var answers = new List<byte[]>();
        foreach (var address in addresses)
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                continue;
            }
            var record = new byte[12 + 4];
            // NAME: compression pointer to offset 12 (start of the question name)
            record[0] = 0xC0;
            record[1] = 12;
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2, 2), TypeA);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(4, 2), (ushort)(ClassIn | 0x8000)); // cache-flush
            BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(6, 4), ttlSeconds);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(10, 2), 4); // RDLENGTH
            address.GetAddressBytes().CopyTo(record, 12);
            answers.Add(record);
        }

        var buffer = new byte[12 + nameBytes.Length + 4 + answers.Sum(a => a.Length)];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, id);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), 0x8400); // QR + AA
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4, 2), 1);      // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(6, 2), (ushort)answers.Count); // ANCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(8, 2), 0);      // NSCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(10, 2), 0);     // ARCOUNT
        var pos = 12;
        nameBytes.AsSpan().CopyTo(span.Slice(pos, nameBytes.Length));
        pos += nameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(pos, 2), question.Type);
        BinaryPrimitives.WriteUInt16BigEndian(span.Slice(pos + 2, 2), question.Class);
        pos += 4;
        foreach (var answer in answers)
        {
            answer.AsSpan().CopyTo(span.Slice(pos, answer.Length));
            pos += answer.Length;
        }
        return buffer;
    }

    /// <summary>Extract A-record IPv4 addresses from a DNS response message (for conflict detection / self-check).</summary>
    public static IReadOnlyList<IPAddress> ParseARecords(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12)
        {
            return Array.Empty<IPAddress>();
        }
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(6, 2));
        var offset = 12;
        try
        {
            for (var q = 0; q < BinaryPrimitives.ReadUInt16BigEndian(message.Slice(4, 2)); q++)
            {
                (_, offset) = ParseName(message, offset);
                offset += 4;
            }
        }
        catch (MalformedMessageException)
        {
            return Array.Empty<IPAddress>();
        }

        var ips = new List<IPAddress>();
        try
        {
            for (var i = 0; i < anCount; i++)
            {
                var (_, afterName) = ParseName(message, offset);
                offset = afterName;
                if (offset + 10 > message.Length)
                {
                    break;
                }
                var type = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
                var rdLength = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset + 8, 2));
                offset += 10;
                if (type == TypeA && rdLength == 4 && offset + 4 <= message.Length)
                {
                    ips.Add(new IPAddress(message.Slice(offset, 4)));
                }
                offset += rdLength;
            }
        }
        catch (MalformedMessageException)
        {
            // partial records are fine for the conflict check
        }
        return ips;
    }
}
