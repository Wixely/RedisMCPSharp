using System.Net.Sockets;
using System.Net;
using RedisMCPSharp.Services;
using Xunit;

namespace RedisMCPSharp.Tests;

/// <summary>
/// Cover for issue #6: a cluster reported its nodes in two different formats.
/// </summary>
public sealed class RedisEndpointsTests
{
    [Fact]
    public void TheBuiltInFormattingIsWhatWasWrong()
    {
        // Before: the two endpoint kinds render differently, and only one of them is reasonable.
        // Pinning the cause here means the next reader does not have to rediscover why the helper
        // exists - and if a future runtime ever makes DnsEndPoint.ToString() sane, this fails and
        // tells us the helper can go.
        EndPoint seed = new DnsEndPoint("redis.example.internal", 6379);
        EndPoint discovered = new IPEndPoint(IPAddress.Parse("10.0.0.11"), 6379);

        Assert.Equal("Unspecified/redis.example.internal:6379", seed.ToString());
        Assert.Equal("10.0.0.11:6379", discovered.ToString());
    }

    [Fact]
    public void BothKindsNowRenderTheSameWay()
    {
        // After: one format, so a caller grouping on "endpoint" sees one spelling per node.
        EndPoint seed = new DnsEndPoint("redis.example.internal", 6379);
        EndPoint discovered = new IPEndPoint(IPAddress.Parse("10.0.0.11"), 6379);

        Assert.Equal("redis.example.internal:6379", RedisEndpoints.Describe(seed));
        Assert.Equal("10.0.0.11:6379", RedisEndpoints.Describe(discovered));
    }

    [Fact]
    public void TheAddressFamilyPrefixIsGoneWhateverTheHost()
    {
        // "Unspecified/" is the specific thing that reads like an error mid-incident.
        foreach (string host in new[] { "redis.example.internal", "cache-01", "localhost" })
        {
            string rendered = RedisEndpoints.Describe(new DnsEndPoint(host, 6379));

            Assert.DoesNotContain("Unspecified", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("/", rendered, StringComparison.Ordinal);
            Assert.Equal($"{host}:6379", rendered);
        }
    }

    [Fact]
    public void AnIPv6AddressKeepsItsBracketsSoThePortIsStillReadable()
    {
        // IPEndPoint already brackets IPv6, and the helper must not strip them. A first version
        // rebuilt the string as $"{ip.Address}:{ip.Port}", which dropped the brackets and gave
        // 2001:db8::1:6379 - no way to tell where the address ends. This test caught that.
        string rendered = RedisEndpoints.Describe(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6379));

        Assert.Equal("[2001:db8::1]:6379", rendered);
    }

    [Fact]
    public void ANullEndpointDoesNotThrow()
    {
        // GetEndPoints() has returned an empty set on a half-connected multiplexer before, and a
        // diagnostic tool is exactly the wrong place to throw.
        Assert.Equal("unknown", RedisEndpoints.Describe(null));
    }

    [Fact]
    public void AnUnrecognisedKindFallsBackRatherThanGuessing()
    {
        // Better an unusual string than a confidently wrong one.
        Assert.Equal(new UnixDomainSocketEndPoint("/tmp/redis.sock").ToString(), RedisEndpoints.Describe(new UnixDomainSocketEndPoint("/tmp/redis.sock")));
    }

    [Fact]
    public void EveryKindAgreesOnTheHostPortShape()
    {
        // The property that actually matters to a consumer: whatever the node, the last colon
        // separates a port it can parse.
        EndPoint[] endpoints =
        [
            new DnsEndPoint("redis.example.internal", 6379),
            new IPEndPoint(IPAddress.Parse("10.0.0.11"), 6380),
            new IPEndPoint(IPAddress.Parse("2001:db8::1"), 6381),
        ];

        int[] ports = [6379, 6380, 6381];

        for (int i = 0; i < endpoints.Length; i++)
        {
            string rendered = RedisEndpoints.Describe(endpoints[i]);
            string port = rendered[(rendered.LastIndexOf(':') + 1)..];

            Assert.Equal(ports[i], int.Parse(port, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
