using System.Net;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("127.5.6.7")]      // 127/8
    [InlineData("10.0.0.1")]       // приватная 10/8
    [InlineData("172.16.0.1")]     // приватная 172.16/12
    [InlineData("172.31.255.254")] // граница 172.16/12
    [InlineData("192.168.1.1")]    // приватная 192.168/16
    [InlineData("169.254.169.254")]// link-local / cloud metadata
    [InlineData("100.64.0.1")]     // CGNAT 100.64/10
    [InlineData("0.0.0.0")]        // 0/8
    public void IsPublic_PrivateOrLocalIPv4_False(string ip)
    {
        SsrfGuard.IsPublic(IPAddress.Parse(ip)).Should().BeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]   // сразу за приватным 172.16/12
    [InlineData("192.169.0.1")]  // сразу за 192.168/16
    [InlineData("100.128.0.1")]  // за CGNAT
    public void IsPublic_PublicIPv4_True(string ip)
    {
        SsrfGuard.IsPublic(IPAddress.Parse(ip)).Should().BeTrue();
    }

    [Theory]
    [InlineData("::1")]              // loopback
    [InlineData("fe80::1")]          // link-local
    [InlineData("fc00::1")]          // unique local
    [InlineData("fd12:3456::1")]     // unique local
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.1")]  // IPv4-mapped приватный
    public void IsPublic_PrivateOrLocalIPv6_False(string ip)
    {
        SsrfGuard.IsPublic(IPAddress.Parse(ip)).Should().BeFalse();
    }

    [Fact]
    public void IsPublic_PublicIPv6_True()
    {
        SsrfGuard.IsPublic(IPAddress.Parse("2606:4700:4700::1111")).Should().BeTrue();
    }

    [Fact]
    public async Task IsPubliclyRoutable_LocalhostLiteral_False()
    {
        var uri = new Uri("http://127.0.0.1:8080/x");
        (await SsrfGuard.IsPubliclyRoutableAsync(uri, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsPubliclyRoutable_MetadataLiteral_False()
    {
        var uri = new Uri("http://169.254.169.254/latest/meta-data/");
        (await SsrfGuard.IsPubliclyRoutableAsync(uri, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsPubliclyRoutable_UnresolvableHost_False()
    {
        var uri = new Uri("http://nonexistent.invalid/");
        (await SsrfGuard.IsPubliclyRoutableAsync(uri, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("224.0.0.1")]      // multicast 224/4
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]      // reserved 240/4
    [InlineData("255.255.255.255")] // broadcast
    [InlineData("198.18.0.1")]     // benchmark 198.18/15
    [InlineData("198.19.255.255")]
    public void IsPublic_ДопДиапазоныIPv4_False(string ip)
    {
        SsrfGuard.IsPublic(IPAddress.Parse(ip)).Should().BeFalse();
    }

    [Theory]
    [InlineData("198.17.255.255")] // сразу перед benchmark
    [InlineData("198.20.0.1")]     // сразу за benchmark
    [InlineData("223.255.255.255")] // сразу перед multicast
    public void IsPublic_ДопДиапазоныГраницыIPv4_True(string ip)
    {
        SsrfGuard.IsPublic(IPAddress.Parse(ip)).Should().BeTrue();
    }

    [Fact]
    public void IsPublic_NAT64ЗавёрнутыйLoopback_False()
    {
        // 64:ff9b::7f00:1 — NAT64-представление 127.0.0.1
        SsrfGuard.IsPublic(IPAddress.Parse("64:ff9b::7f00:1")).Should().BeFalse();
    }

    [Fact]
    public void IsPublic_NAT64ЗавёрнутыйПубличныйАдрес_True()
    {
        // 64:ff9b::0808:0808 — NAT64-представление 8.8.8.8
        SsrfGuard.IsPublic(IPAddress.Parse("64:ff9b::808:808")).Should().BeTrue();
    }

    [Fact]
    public async Task Check_ПриватныйЛитерал_Private()
    {
        var uri = new Uri("http://127.0.0.1/");
        (await SsrfGuard.CheckAsync(uri, CancellationToken.None)).Should().Be(SsrfGuard.AddressCheck.Private);
    }

    [Fact]
    public async Task Check_НерезолвящийсяХост_DnsFailed()
    {
        var uri = new Uri("http://nonexistent.invalid/");
        (await SsrfGuard.CheckAsync(uri, CancellationToken.None)).Should().Be(SsrfGuard.AddressCheck.DnsFailed);
    }

    [Fact]
    public async Task Check_ПубличныйЛитерал_Public()
    {
        var uri = new Uri("http://8.8.8.8/");
        (await SsrfGuard.CheckAsync(uri, CancellationToken.None)).Should().Be(SsrfGuard.AddressCheck.Public);
    }
}
