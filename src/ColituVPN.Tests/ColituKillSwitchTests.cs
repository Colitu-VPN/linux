using System.Net;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

public class ColituKillSwitchTests
{
    private static string Script() => ColituKillSwitch.BuildEngageScript(
        [IPAddress.Parse("203.0.113.7"), IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::5"), IPAddress.Parse("fe80::1")],
        4242);

    [Fact]
    public void Rules_DropEverythingElseByDefault()
    {
        var script = Script();
        script.Should().Contain("type filter hook output priority 0; policy drop;");
        script.Should().Contain("oifname \"lo\" accept");
        script.Should().Contain("oifname { \"xray_tun\", \"singbox_tun\" } accept");
    }

    [Fact]
    public void Rules_LetTheServersAndTheLocalNetworkThrough()
    {
        var script = Script();
        script.Should().Contain("ip daddr { 203.0.113.7 } accept", "duplicates are removed");
        script.Should().Contain("ip6 daddr { 2001:db8::5 } accept", "link-local addresses are covered by fe80::/10 already");
        script.Should().Contain("192.168.0.0/16").And.Contain("10.0.0.0/8").And.Contain("udp sport 68 udp dport 67 accept");
    }

    [Fact]
    public void Rules_NeverOutliveTheApp()
    {
        var script = Script();
        script.Should().Contain("while kill -0 4242").And.Contain("nft delete table inet colitu_killswitch");
        script.Should().Contain("setsid");
    }

    [Fact]
    public void Rules_UseUnixLineEndings()
    {
        Script().Should().NotContain("\r");
    }

    [Fact]
    public void Rules_HaveNoAddressesWhenNoneAreKnown()
    {
        var script = ColituKillSwitch.BuildEngageScript([], 1);
        script.Should().NotContain("ip daddr { } accept");
    }
}
