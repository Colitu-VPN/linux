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
        // DHCPv6 only from the privileged client port, never "any host, port 547".
        script.Should().Contain("udp sport 546 udp dport 547 accept").And.NotContain("    udp dport 547 accept");
    }

    [Fact]
    public void Rules_OutliveTheApp_FailClosed()
    {
        var script = Script();
        // No watcher that deletes the table when the app dies: a crash must not open the internet.
        script.Should().NotContain("kill -0").And.NotContain("setsid");
        script.Should().NotContain("nft delete table");
        // An old (≤ 1.1.2) watcher is stopped so it cannot remove the new rules either.
        script.Should().Contain("/run/colitu-killswitch.watch");
    }

    [Fact]
    public void Rules_ReplaceTheTableInOneTransaction()
    {
        var script = Script();
        script.Should().Contain("table inet colitu_killswitch\ndelete table inet colitu_killswitch\ntable inet colitu_killswitch {");
    }

    [Fact]
    public void Rules_LeaveAMarkerForTheNextStart()
    {
        var script = Script();
        script.Should().Contain("printf '%s %s\\n' \"${SUDO_UID:-0}\" 4242 > /run/colitu-killswitch.active");
        script.Should().Contain("chmod 0644 /run/colitu-killswitch.active");
    }

    [Fact]
    public void Release_RemovesTableMarkerAndOldWatcher()
    {
        var script = ColituKillSwitch.ReleaseScript;
        script.Should().Contain("nft delete table inet colitu_killswitch 2>/dev/null || true");
        script.Should().Contain("rm -f /run/colitu-killswitch.active");
        script.Should().Contain("/run/colitu-killswitch.watch");
        script.Should().NotContain("\r");
    }

    [Theory]
    [InlineData("1000 4242\n", 4242)]
    [InlineData("0 17", 17)]
    [InlineData("1000", null)]
    [InlineData("1000 abc", null)]
    [InlineData("1000 -5", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Marker_NamesTheAppThatInstalledTheRules(string? text, int? expected)
    {
        ColituKillSwitch.ParseMarkerPid(text).Should().Be(expected);
    }

    [Fact]
    public void OrphanCores_AreMatchedOnTheirExecutablePath()
    {
        var script = ColituKillSwitch.BuildStopOrphanCoresScript("/opt/colitu-vpn/bin", "/home/u/.local/share/ColituVPN/bin/", "/opt/colitu-vpn/bin/");
        script.Should().Contain("readlink \"$p/exe\"");
        script.Should().Contain("'/opt/colitu-vpn/bin/'*) ;;");
        script.Should().Contain("'/home/u/.local/share/ColituVPN/bin/'*) ;;");
        script.Should().Contain("*) continue ;;");
        script.Split("/opt/colitu-vpn/bin/").Length.Should().Be(2, "duplicates are dropped");
        script.Should().NotContain("\r");
    }

    [Fact]
    public void OrphanCores_OnlyThoseThisUserStartedThroughSudo()
    {
        // colitud (systemd) and other users' TUN sessions run the same /opt/colitu-vpn/bin cores.
        var script = ColituKillSwitch.BuildStopOrphanCoresScript("/opt/colitu-vpn/bin");
        script.Should().Contain("uid=\"${SUDO_UID:-}\"");
        script.Should().Contain("case \"$uid\" in ''|*[!0-9]*) exit 0 ;; esac", "no caller uid: nothing is killed");
        script.Should().Contain("tr '\\0' '\\n' < \"$p/environ\" 2>/dev/null | grep -qx \"SUDO_UID=$uid\" || continue");
        script.IndexOf("grep -qx", StringComparison.Ordinal).Should().BeLessThan(script.IndexOf("kill -9", StringComparison.Ordinal));
    }

    [Fact]
    public void OrphanCores_PathIsQuotedForTheShell()
    {
        ColituKillSwitch.BuildStopOrphanCoresScript("/home/o'neil/$(evil)/bin/")
            .Should().Contain("'/home/o'\\''neil/$(evil)/bin/'*)");
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

    [Fact]
    public void LeftoverRules_AreNeverReportedOutsideLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            ColituKillSwitch.HasLeftoverRules().Should().BeFalse();
        }
    }
}
