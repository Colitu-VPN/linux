using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Regression tests for the fixes of the 2026-10 security audit.</summary>
public class ColituAuditFixTests
{
    private static JsonElement Payload(string host) => JsonDocument.Parse(
        """{"schema_version":1,"protocol":"shadowsocks","endpoint":{"host":""" + JsonSerializer.Serialize(host)
        + ""","port":8388},"credentials":{"method":"aes-128-gcm","password":"p"},"transport":{"type":"tcp"},"security":{"type":"none"}}""")
        .RootElement.Clone();

    [Theory]
    [InlineData("vpn.example.com")]
    [InlineData("203.0.113.9")]
    public void ShareLink_AcceptsHostNamesAndAddresses(string host)
    {
        ColituShareLinkBuilder.Build(Payload(host), "x").Should().Contain($"@{host}:8388");
    }

    [Theory]
    [InlineData("evil.example?allowInsecure=1")]
    [InlineData("a@b.example")]
    [InlineData("host.example#frag")]
    [InlineData("host.example\nline")]
    [InlineData("host/path")]
    public void ShareLink_RejectsHostsThatCouldInjectParameters(string host)
    {
        ColituShareLinkBuilder.Build(Payload(host), "x").Should().BeNull();
    }

    [Fact]
    public void ShareLink_RejectsANonIntegerSchemaVersion()
    {
        var payload = JsonDocument.Parse("""{"schema_version":1.5,"protocol":"shadowsocks"}""").RootElement.Clone();
        ColituShareLinkBuilder.Build(payload, "x").Should().BeNull();
    }

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    public void Latency_OnlyDialsPublicAddresses(string address, bool allowed)
    {
        ColituLatency.IsPublic(IPAddress.Parse(address)).Should().Be(allowed);
    }

    [Fact]
    public void KillSwitch_ReplacesTheTableInOneTransactionAndStopsTheOldWatcher()
    {
        var script = ColituKillSwitch.BuildEngageScript([IPAddress.Parse("203.0.113.7")], 4242);

        var stop = script.IndexOf("/run/colitu-killswitch.watch", StringComparison.Ordinal);
        var load = script.IndexOf("nft -f -", StringComparison.Ordinal);
        stop.Should().BeGreaterThan(-1).And.BeLessThan(load, "the old watcher must be gone before the table changes");
        script.Should().NotContain("nft delete table inet colitu_killswitch 2>/dev/null || true\nnft -f",
            "a separate delete would leave a moment without rules");
        script.Should().Contain("table inet colitu_killswitch\ndelete table inet colitu_killswitch\ntable inet colitu_killswitch {");
        script.Should().Contain("echo $! > /run/colitu-killswitch.watch");
    }

    [Fact]
    public void Redact_MasksQuerySecretsEmailsAndUrlCredentials()
    {
        var log = string.Join('\n',
            "GET https://api.example.com/x?token=tok123&lang=en",
            "vless://u@h:443?pbk=pbk456&sid=sid789&sni=www.example.com",
            "account mail@example.com signed in",
            "proxy http://user:pa55@10.0.0.1:8080",
            "socks://dXNlcjpwYXNz@h:1080",
            "{\"id\":\"0b2c-uuid\",\"auth\":\"hy-secret\",\"server\":\"h\"}");

        var redacted = ColituSupportService.Redact(log);

        foreach (var secret in new[] { "tok123", "pbk456", "sid789", "mail@example.com", "pa55", "dXNlcjpwYXNz", "0b2c-uuid", "hy-secret" })
        {
            redacted.Should().NotContain(secret);
        }
        redacted.Should().Contain("lang=en").And.Contain("sni=www.example.com").And.Contain("\"server\":\"h\"");
    }

    [Fact]
    public void SystemBinary_IsAlwaysAnAbsolutePath()
    {
        ColituShell.SystemBinary("sudo").Should().StartWith("/");
        ColituShell.SystemBinary("pkexec").Should().StartWith("/");
    }
}
