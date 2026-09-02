using ClipBridge.Core.Net;
using Xunit;

namespace ClipBridge.Core.Tests.Net;

public class MdnsCodecTests
{
    [Fact]
    public void EncodeName_RoundTrips()
    {
        var encoded = MdnsCodec.EncodeName("clipbridge.local");
        Assert.Equal(new byte[] { 10, (byte)'c', (byte)'l', (byte)'i', (byte)'p', (byte)'b', (byte)'r', (byte)'i', (byte)'d', (byte)'g', (byte)'e', 5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0 }, encoded);
        var (name, next) = MdnsCodec.ParseName(encoded, 0);
        Assert.Equal("clipbridge.local", name);
        Assert.Equal(encoded.Length, next);
    }

    [Fact]
    public void ParseQueries_ExtractsNameTypeClass()
    {
        var name = MdnsCodec.EncodeName("clipbridge.local");
        var message = new byte[12 + name.Length + 4];
        // QDCOUNT = 1
        message[4] = 0;
        message[5] = 1;
        name.CopyTo(message, 12);
        var pos = 12 + name.Length;
        message[pos] = 0;
        message[pos + 1] = 1;       // QTYPE A
        message[pos + 2] = 0;
        message[pos + 3] = 1;       // QCLASS IN

        var queries = MdnsCodec.ParseQueries(message);
        var q = Assert.Single(queries);
        Assert.Equal("clipbridge.local", q.Name);
        Assert.Equal(MdnsCodec.TypeA, q.Type);
        Assert.Equal(MdnsCodec.ClassIn, q.Class);
    }

    [Fact]
    public void BuildAResponse_ContainsAnswerForQuery()
    {
        var query = new MdnsCodec.Query("clipbridge.local", MdnsCodec.TypeA, MdnsCodec.ClassIn);
        var response = MdnsCodec.BuildAResponse(0x1234, query, new[] { System.Net.IPAddress.Parse("192.168.1.14") });
        var ips = MdnsCodec.ParseARecords(response);
        var ip = Assert.Single(ips);
        Assert.Equal("192.168.1.14", ip.ToString());
    }

    [Fact]
    public void BuildAResponse_SkipsNonIPv4Addresses()
    {
        var query = new MdnsCodec.Query("clipbridge.local", MdnsCodec.TypeAny, MdnsCodec.ClassIn);
        var response = MdnsCodec.BuildAResponse(0, query, new[]
        {
            System.Net.IPAddress.Parse("192.168.1.14"),
            System.Net.IPAddress.Parse("fe80::1"),
        });
        var ips = MdnsCodec.ParseARecords(response);
        var ip = Assert.Single(ips); // exactly one A record; IPv6 never enters the answer
        Assert.Equal("192.168.1.14", ip.ToString());
    }

    [Fact]
    public void ParseQueries_RejectsTruncatedMessage()
        => Assert.Throws<MdnsCodec.MalformedMessageException>(() => MdnsCodec.ParseQueries(new byte[] { 0, 1 }));

    [Fact]
    public void ParseARecords_EmptyOnGarbage()
        => Assert.Empty(MdnsCodec.ParseARecords(new byte[] { 0, 1, 2, 3 }));
}
