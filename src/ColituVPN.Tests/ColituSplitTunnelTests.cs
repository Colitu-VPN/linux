using System.Net;
using AwesomeAssertions;
using ServiceLib;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

public class ColituSplitTunnelTests
{
    private static ColituVpnPreferences Prefs(string mode, bool tun, string[]? apps = null, string[]? domains = null, string[]? ips = null) => new ColituVpnPreferences(
        ConnectionMode: tun ? ColituConnectionModes.Tun : ColituConnectionModes.Proxy,
        SplitTunnelMode: mode,
        SplitTunnelApps: apps?.ToList(),
        SplitTunnelDomains: domains?.ToList(),
        SplitTunnelIps: ips?.ToList()).Normalize();

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  https://Mail.Example.COM/inbox?x=1 ", "mail.example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("user@example.com:8443", "example.com")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    public void Domains_AreNormalized(string input, string expected)
    {
        ColituSplitTunnel.TryNormalizeDomain(input, out var domain).Should().BeTrue();
        domain.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("203.0.113.7")]
    [InlineData("exa mple.com")]
    [InlineData("-bad.com")]
    [InlineData("example.123")]
    [InlineData("a;rm -rf /.com")]
    public void Domains_RejectNonsense(string input)
    {
        ColituSplitTunnel.TryNormalizeDomain(input, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData("2001:db8::1/32", "2001:db8::/32")]
    [InlineData(" 192.168.1.0/24 ", "192.168.1.0/24")]
    [InlineData("203.0.113.7/32", "203.0.113.7")]
    public void Networks_AreNormalized(string input, string expected)
    {
        ColituSplitTunnel.TryNormalizeNetwork(input, out var network).Should().BeTrue();
        network.Should().Be(expected);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("1.2.3")]
    [InlineData("example.com")]
    [InlineData("10.0.0.1; nft flush ruleset")]
    [InlineData("fe80::1%eth0")]
    public void Networks_RejectNonsense(string input)
    {
        ColituSplitTunnel.TryNormalizeNetwork(input, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("firefox", true)]
    [InlineData("telegram-desktop", true)]
    [InlineData("/usr/lib/firefox/firefox", true)]
    [InlineData("/opt/My App/app", true)]
    [InlineData("fire fox", false)]
    [InlineData("../x", false)]
    [InlineData("/usr/lib/../bin/sh", false)]
    [InlineData("/usr/lib/", false)]
    [InlineData("a\"b", false)]
    public void Apps_AreValidated(string input, bool ok)
    {
        ColituSplitTunnel.TryNormalizeApp(input, out _).Should().Be(ok);
    }

    [Fact]
    public void Preferences_DropInvalidAndDuplicateEntries()
    {
        var prefs = Prefs("exclude", tun: true, apps: ["firefox", "firefox", "bad app"], domains: ["Example.com", "example.com", "x"], ips: ["10.0.0.0/8", "nope"]);
        prefs.SplitTunnelApps.Should().Equal("firefox");
        prefs.SplitTunnelDomains.Should().Equal("example.com");
        prefs.SplitTunnelIps.Should().Equal("10.0.0.0/8");
    }

    [Fact]
    public void IsOffByDefault()
    {
        new ColituVpnPreferences().Normalize().SplitTunnelMode.Should().Be(ColituSplitTunnelModes.Off);
        new ColituVpnPreferences(SplitTunnelMode: "nonsense").Normalize().SplitTunnelMode.Should().Be(ColituSplitTunnelModes.Off);
        new ColituVpnPreferences(SplitTunnelMode: " Include ").Normalize().SplitTunnelMode.Should().Be(ColituSplitTunnelModes.Include);
    }

    [Fact]
    public void Off_AddsNoRules()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(Prefs("off", tun: true, apps: ["firefox"], domains: ["example.com"]));
        rules.Should().NotContain(r => r.Id.StartsWith("colitu-split"));
    }

    [Fact]
    public void Exclude_Tun_SendsAppsSitesAndAddressesDirect_BeforeTheRegionalRules()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(Prefs("exclude", tun: true, apps: ["firefox"], domains: ["example.com"], ips: ["203.0.113.0/24"]));
        var domains = rules.Single(r => r.Id == "colitu-split-domains");
        domains.OutboundTag.Should().Be(Global.DirectTag);
        domains.Domain.Should().Equal("domain:example.com");
        rules.Single(r => r.Id == "colitu-split-ips").Ip.Should().Equal("203.0.113.0/24");
        var apps = rules.Single(r => r.Id == "colitu-split-apps");
        apps.Process.Should().Equal("firefox");
        apps.InboundTag.Should().Equal("tun");
        rules.FindIndex(r => r.Id == "colitu-split-domains").Should().BeLessThan(rules.FindIndex(r => r.Id == "colitu-ru-direct-domain"));
        rules.Should().NotContain(r => r.Id == "colitu-split-rest");
        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeTrue();
    }

    [Fact]
    public void Proxy_LeavesAppsOut()
    {
        var prefs = Prefs("exclude", tun: false, apps: ["firefox"], domains: ["example.com"]);
        ColituVpnService.BuildColituRoutingRules(prefs).Should().NotContain(r => r.Id == "colitu-split-apps");
        ColituSplitTunnel.EffectiveCount(prefs).Should().Be(1);
        ColituSplitTunnel.IsActive(Prefs("exclude", tun: false, apps: ["firefox"])).Should().BeFalse("apps alone do nothing in proxy mode");
    }

    [Fact]
    public void Include_SendsSelectedThroughTheVpnAndTheRestDirect()
    {
        var rules = ColituVpnService.BuildColituRoutingRules(Prefs("include", tun: true, apps: ["firefox"], domains: ["example.com"]), "DE");
        rules.Single(r => r.Id == "colitu-split-apps").OutboundTag.Should().Be(Global.ProxyTag);
        var rest = rules.Last();
        rest.Id.Should().Be("colitu-split-rest");
        rest.OutboundTag.Should().Be(Global.DirectTag);
        rest.Port.Should().Be("1-65535");
        rest.InboundTag.Should().Equal("tun");
        rules.Single(r => r.Id == "colitu-ru-direct-domain").Enabled.Should().BeFalse("selected apps keep their Russian sites in the tunnel");

        var proxyRest = ColituVpnService.BuildColituRoutingRules(Prefs("include", tun: false, domains: ["example.com"])).Last();
        proxyRest.Id.Should().Be("colitu-split-rest");
        proxyRest.InboundTag.Should().BeNull();
    }

    [Fact]
    public void ProxyMode_Exclude_AddsSitesToTheDesktopBypassList()
    {
        ColituSplitTunnel.BuildProxyExceptions(Global.SystemProxyExceptionsLinux, Prefs("exclude", tun: false, domains: ["example.com"], ips: ["10.0.0.0/8"]))
            .Should().Be("localhost,127.0.0.0/8,::1,example.com,*.example.com,10.0.0.0/8");
        ColituSplitTunnel.BuildProxyExceptions(Global.SystemProxyExceptionsLinux, Prefs("include", tun: false, domains: ["example.com"]))
            .Should().Be(Global.SystemProxyExceptionsLinux);
        ColituSplitTunnel.BuildProxyExceptions(Global.SystemProxyExceptionsLinux, Prefs("exclude", tun: true, domains: ["example.com"]))
            .Should().Be(Global.SystemProxyExceptionsLinux, "TUN mode needs no desktop proxy");
    }

    [Fact]
    public void KillSwitch_KeepsExcludedAddressesAndMarkedTrafficReachable()
    {
        var prefs = Prefs("exclude", tun: true, ips: ["203.0.113.0/24", "2001:db8::/32"]);
        var script = ColituKillSwitch.BuildEngageScript([IPAddress.Parse("198.51.100.1")], 1, ColituSplitTunnel.KillSwitchBypassNetworks(prefs));
        script.Should().Contain("ip daddr { 198.51.100.1, 203.0.113.0/24 } accept");
        script.Should().Contain("ip6 daddr { 2001:db8::/32 } accept");
        script.Should().Contain($"meta mark 0x{Global.LinuxTunDirectRoutingMark:x} accept");
        ColituSplitTunnel.KillSwitchBypassNetworks(Prefs("include", tun: true, ips: ["203.0.113.0/24"])).Should().BeEmpty();
    }

    [Fact]
    public void KillSwitch_NeverWritesUnvalidatedText()
    {
        var script = ColituKillSwitch.BuildEngageScript([], 1, ["10.0.0.0/8 } accept; flush ruleset", "0.0.0.0/0", "192.0.2.1"]);
        script.Should().NotContain("flush").And.NotContain("0.0.0.0/0");
        script.Should().Contain("ip daddr { 192.0.2.1 } accept");
    }

    [Fact]
    public void ChangingTheLists_NeedsAReconnect()
    {
        var a = Prefs("exclude", tun: true, domains: ["example.com"]);
        a.SameSplitTunnel(a with { }).Should().BeTrue();
        a.SameSplitTunnel(a with { SplitTunnelDomains = ["example.org"] }).Should().BeFalse();
        a.SameSplitTunnel(a with { SplitTunnelMode = ColituSplitTunnelModes.Include }).Should().BeFalse();
    }
}
