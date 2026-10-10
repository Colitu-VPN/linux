using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Fixes ported from the Windows security audit (2026-10-10): links-only config import.</summary>
public class ColituWindowsPortedAuditTests
{
    [Theory]
    [InlineData("vless://a@h:443?security=reality#x")]
    [InlineData("vless://a@h:443#x\ntrojan://p@h:443#y\r\n\r\n  hysteria2://p@h:443#z\nss://YWVzOnB3@h:1#s")]
    [InlineData("VLESS://a@h:443#upper")]
    public void LinksOnly_AcceptsTheShareLinksTheAppBuilds(string raw)
    {
        ColituVpnService.IsLinksOnly(raw).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("{\"outbounds\":[]}")]
    [InlineData("vless://a@h:443#x\n{\"inbounds\":[]}")]
    [InlineData("http://evil.example/sub")]
    [InlineData("vmess://abc")]
    [InlineData("vless://a@h:443#x\nsocks://u@h:1")]
    public void LinksOnly_RefusesAnythingElse(string? raw)
    {
        ColituVpnService.IsLinksOnly(raw).Should().BeFalse();
    }
}
