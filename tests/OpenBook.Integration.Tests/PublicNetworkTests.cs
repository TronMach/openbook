using System.Net;
using OpenBook.Infrastructure.Net;

namespace OpenBook.Integration.Tests;

/// <summary>Outbound calls to addresses people type in stay off private networks.</summary>
public sealed class PublicNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    public void Private_and_reserved_addresses_are_refused(string address) =>
        Assert.False(PublicNetwork.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:1.1.1.1")]
    public void Public_addresses_are_allowed(string address) =>
        Assert.True(PublicNetwork.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://127.0.0.1:9/hook")]
    [InlineData("http://localhost:9/hook")]
    [InlineData("http://[::1]:9/hook")]
    public async Task Connections_to_private_hosts_are_refused_before_anything_is_sent(string url)
    {
        using var client = new HttpClient(PublicNetwork.Handler(new OutboundPolicy()));
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.PostAsync(url, new StringContent("{}")));
        Assert.True(ex is PrivateNetworkException || ex.InnerException is PrivateNetworkException, ex.ToString());
    }

    [Fact]
    public async Task An_administrator_can_allow_private_networks()
    {
        using var client = new HttpClient(PublicNetwork.Handler(new OutboundPolicy(AllowPrivateNetworks: true))) { Timeout = TimeSpan.FromSeconds(10) };
        // Nothing listens there, so the connection itself fails, but not because the address was refused.
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.PostAsync("http://127.0.0.1:9/hook", new StringContent("{}")));
        Assert.False(ex is PrivateNetworkException || ex.InnerException is PrivateNetworkException);
    }
}
