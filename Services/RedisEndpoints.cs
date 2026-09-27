using System.Net;

namespace RedisMCPSharp.Services;

/// <summary>
/// Renders a Redis endpoint the same way whatever kind it is.
/// </summary>
/// <remarks>
/// <para>
/// <c>IConnectionMultiplexer.GetEndPoints()</c> returns a mixture. The seed endpoint is built
/// from the configured connection string and is a <see cref="DnsEndPoint"/>; endpoints found
/// through cluster topology are <see cref="IPEndPoint"/>. Their <c>ToString()</c> implementations
/// disagree: <see cref="IPEndPoint"/> renders <c>address:port</c>, while <see cref="DnsEndPoint"/>
/// prefixes its <see cref="System.Net.Sockets.AddressFamily"/> - which is <c>Unspecified</c> when
/// the host was given as a name - producing <c>Unspecified/host:port</c>.
/// </para>
/// <para>
/// So one cluster reported its nodes in two formats, and the odd one read like an error at exactly
/// the moment someone is looking at a cluster problem. Nothing was wrong with the data.
/// </para>
/// </remarks>
internal static class RedisEndpoints
{
    /// <summary>Formats an endpoint as <c>host:port</c>, whatever concrete type it is.</summary>
    /// <remarks>
    /// Only <see cref="DnsEndPoint"/> is special-cased, because only it is wrong. A first version
    /// of this also rebuilt <see cref="IPEndPoint"/> as <c>$"{ip.Address}:{ip.Port}"</c>, which
    /// looks equivalent and is not: <see cref="IPEndPoint.ToString"/> brackets an IPv6 address,
    /// and without that <c>2001:db8::1:6379</c> has no way to tell the port from the address.
    /// </remarks>
    public static string Describe(EndPoint? endpoint) => endpoint switch
    {
        null => "unknown",
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        // Everything else already renders sensibly and keeps its own formatting rather than
        // being guessed at; better an unusual string than a wrong one.
        _ => endpoint.ToString() ?? "unknown",
    };
}
