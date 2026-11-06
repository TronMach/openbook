using System.Net;
using System.Net.Sockets;

namespace OpenBook.Infrastructure.Net;

/// <summary>Whether outbound calls to addresses people type in (webhooks, bank connections) may reach private networks.</summary>
/// <param name="AllowPrivateNetworks">From <c>OpenBook:Outbound:AllowPrivateNetworks</c>; off by default.</param>
public sealed record OutboundPolicy(bool AllowPrivateNetworks = false);

/// <summary>Refused because the address is on a private network (see <see cref="PublicNetwork"/>).</summary>
public sealed class PrivateNetworkException(string host)
    : HttpRequestException($"{host} is on a private or local network, so OpenBook won't connect to it. An administrator can allow it with OpenBook:Outbound:AllowPrivateNetworks.")
{
    public string Host { get; } = host;
}

/// <summary>
/// Keeps calls to addresses people type in from reaching the server's own network (loopback, private ranges, link-local
/// cloud metadata and so on). The check runs on the address actually connected to, after DNS, so a name that resolves to
/// a private address, or changes what it resolves to between checks, is refused too.
/// </summary>
public static class PublicNetwork
{
    /// <summary>A handler that only connects to public addresses, unless the policy allows private ones.</summary>
    public static SocketsHttpHandler Handler(OutboundPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // No proxy: what's checked below must be what's connected to, and through a proxy it would be the proxy.
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseProxy = false };
        if (!policy.AllowPrivateNetworks)
            handler.ConnectCallback = ConnectPublicAsync;
        return handler;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(IsPublic).ToList();
        if (allowed.Count == 0 || allowed.Count != addresses.Length)
            throw new PrivateNetworkException(host);

        // Each in turn, as an ordinary client does: a host's first address may not answer.
        SocketException? failed = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                failed = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw failed!;
    }

    /// <summary>
    /// False for loopback, unspecified, private (10/8, 172.16/12, 192.168/16, fc00::/7), shared (100.64/10), link-local
    /// (169.254/16, fe80::/10), multicast, broadcast and reserved ranges; IPv4 inside IPv6 is judged as IPv4.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || b[0] == 100 && b[1] >= 64 && b[1] <= 127
                || b[0] == 169 && b[1] == 254
                || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 192 && b[1] == 0 && b[2] == 0
                || b[0] == 198 && (b[1] == 18 || b[1] == 19));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            // NAT64 (64:ff9b::/96) carries an IPv4 address in its last four bytes.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
                return IsPublic(new IPAddress(b[12..]));
            return !(address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xfe) == 0xfc);
        }

        return false;
    }
}
