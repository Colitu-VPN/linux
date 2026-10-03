using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

public class ColituNetworkTests
{
    [Theory]
    [InlineData("198.18.0.36", true)]
    [InlineData("198.19.255.1", true)]
    [InlineData("198.17.0.1", false)]
    [InlineData("203.0.113.10", false)]
    [InlineData("192.168.0.1", false)]
    public void FakeIpPool_IsRecognised(string address, bool placeholder)
    {
        ColituNetwork.IsPlaceholder(IPAddress.Parse(address)).Should().Be(placeholder);
    }

    [Theory]
    [InlineData("tun0")]
    [InlineData("wg0")]
    [InlineData("tailscale0")]
    [InlineData("docker0")]
    [InlineData("br-1a2b3c")]
    [InlineData("veth12ab")]
    [InlineData("virbr0")]
    [InlineData("singbox_tun")]
    [InlineData("xray_tun")]
    [InlineData("lo")]
    public void VirtualInterfaces_AreNeverUsedForTheCore(string name)
    {
        ColituNetwork.IsVirtual(name).Should().BeTrue();
    }

    [Fact]
    public void PhysicalInterface_IsNeverAVirtualOne()
    {
        var name = ColituNetwork.PhysicalInterfaceName();
        if (name == null)
        {
            return; // no default route on this machine (CI, or not Linux)
        }
        ColituNetwork.IsVirtual(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("xray_tun", true)]
    [InlineData("singbox_tun", true)]
    [InlineData("tun0", false)]
    public void OwnTunnels_AreNotAnotherVpn(string name, bool own)
    {
        ColituNetwork.IsOwnTun(name).Should().Be(own);
    }

    [Fact]
    public async Task LiteralAddress_IsReturnedWithoutLookup()
    {
        (await ColituNetwork.ResolveServerAsync("203.0.113.10")).Should().Be(IPAddress.Parse("203.0.113.10"));
    }

    [Fact]
    public void ShareLink_UsesThePinnedAddressAndKeepsTheNameForTls()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"trojan","endpoint":{"host":"vpn.example.com","port":443},"credentials":{"password":"p"},"transport":{"type":"tcp"},"security":{"type":"tls","server_name":"vpn.example.com"}}""").RootElement.Clone();

        ColituShareLinkBuilder.HostOf(payload).Should().Be("vpn.example.com");
        var link = ColituShareLinkBuilder.Build(payload, "x", "203.0.113.7");

        link.Should().StartWith("trojan://p@203.0.113.7:443?").And.Contain("sni=vpn.example.com");
    }

    [Fact]
    public void ShareLink_WithoutPin_DialsTheName()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1,"protocol":"shadowsocks","endpoint":{"host":"vpn.example.com","port":8388},"credentials":{"method":"aes-128-gcm","password":"p"},"transport":{"type":"tcp"},"security":{"type":"none"}}""").RootElement.Clone();

        ColituShareLinkBuilder.Build(payload, "x").Should().Contain("@vpn.example.com:8388");
    }
}
