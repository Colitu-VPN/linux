using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

public class ColituSecurityTests
{
    private static ColituLinuxVersionPayload Manifest() => new()
    {
        LatestVersionCode = 110,
        VersionName = "1.1.0",
        Deb = new ColituLinuxPackage { Url = "https://colitu.com/downloads/linux/colitu-vpn_1.1.0_amd64.deb", Sha256 = "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789" },
        Rpm = new ColituLinuxPackage { Url = "https://colitu.com/downloads/linux/colitu-vpn-1.1.0-1.x86_64.rpm", Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" },
        ForceUpdate = false
    };

    private static (string PublicPem, ColituLinuxVersionPayload Payload) Signed()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Manifest();
        payload.Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(ColituUpdateSignature.Message(payload)), HashAlgorithmName.SHA256));
        return (key.ExportSubjectPublicKeyInfoPem(), payload);
    }

    [Fact]
    public void UpdateSignature_AcceptsTheSignedManifest()
    {
        var (pem, payload) = Signed();
        ColituUpdateSignature.Verify(payload, pem).Should().BeTrue();
    }

    [Fact]
    public void UpdateSignature_RejectsAnyChangedField()
    {
        var (pem, payload) = Signed();
        payload.Deb!.Url = "https://colitu.com/downloads/linux/other.deb";
        ColituUpdateSignature.Verify(payload, pem).Should().BeFalse();

        (pem, payload) = Signed();
        payload.Rpm!.Sha256 = new string('0', 64);
        ColituUpdateSignature.Verify(payload, pem).Should().BeFalse();

        (pem, payload) = Signed();
        payload.ForceUpdate = true;
        ColituUpdateSignature.Verify(payload, pem).Should().BeFalse();
    }

    [Fact]
    public void UpdateSignature_IsBoundToLinux()
    {
        // A Windows manifest signature (different first line) never verifies here.
        ColituUpdateSignature.Message(Manifest()).Should().StartWith("colitu-linux-update-v1\n");
    }

    [Fact]
    public void UpdateSignature_RejectsUnsignedOrForeignManifests()
    {
        ColituUpdateSignature.Verify(Manifest()).Should().BeFalse();

        // Signed with some other key: fails against the key built into the app.
        var (_, foreign) = Signed();
        ColituUpdateSignature.Verify(foreign).Should().BeFalse();
    }

    /// <summary>Release check: set COLITU_VERIFY_MANIFEST to a latest.json produced by scripts/sign-linux-manifest.py.</summary>
    [Fact]
    public void UpdateSignature_ReleaseManifestVerifiesWithTheBuiltInKey()
    {
        var path = Environment.GetEnvironmentVariable("COLITU_VERIFY_MANIFEST");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        var payload = JsonSerializer.Deserialize<ColituLinuxVersionPayload>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        ColituUpdateSignature.Verify(payload).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://pay.example.com/checkout?id=1&x=2", true)]
    [InlineData("mailto:support@colitu.com", true)]
    [InlineData("http://colitu.com", false)]
    [InlineData("file:///usr/bin/sh", false)]
    [InlineData("/usr/bin/sh", false)]
    [InlineData("smb://attacker/share/setup.sh", false)]
    [InlineData("https://user:pass@colitu.com", false)]
    [InlineData("", false)]
    public void Shell_OnlyOpensWebAndMailLinks(string url, bool allowed)
    {
        ColituShell.IsSafeLink(url).Should().Be(allowed);
    }

    [Fact]
    public void Shell_CheckoutLinksMayNotBeMail()
    {
        ColituShell.IsSafeLink("mailto:x@y.z", allowMail: false).Should().BeFalse();
    }

    [Theory]
    [InlineData("+0300 2026-09-25 01:18:22 INFO [4279361987 36ms] dns: exchanged A www.example.com. 5 IN A 93.184.216.34")]
    [InlineData("+0300 2026-09-25 01:18:22 DEBUG [1] router: match[0] => direct")]
    [InlineData("2026/09/25 01:18:22 [Info] app/dispatcher: sniffed domain: example.com")]
    [InlineData("2026/09/25 01:18:22 from 127.0.0.1:50000 accepted tcp:example.com:443 [socks >> proxy]")]
    public void LogPrivacy_DropsTrafficLines(string line)
    {
        ColituLogPrivacy.SanitizeCoreLine(line).Should().BeNull();
    }

    [Fact]
    public void LogPrivacy_MasksVisitedHostsButKeepsTheServer()
    {
        var line = "+0300 2026-09-25 01:18:22 ERROR [42 3s] connection: open outbound connection: dial tcp 93.184.216.34:443 via pro.colitu.test: www.example.com timeout";

        var kept = ColituLogPrivacy.SanitizeCoreLine(line, ["pro.colitu.test"]);

        kept.Should().NotBeNull();
        kept.Should().NotContain("example.com").And.NotContain("93.184.216.34");
        kept.Should().Contain("pro.colitu.test").And.Contain("01:18:22").And.Contain("ERROR");
    }

    [Theory]
    [InlineData("+0300 2026-10-02 00:00:38 ERROR [3351765230 17m3s] connection: connection download closed: close tcp 172.19.0.1:58466->1.2.3.4:443: endpoint not connected")]
    [InlineData("+0300 2026-10-02 00:04:33 ERROR [4276258550 6m7s] connection: connection upload closed: stream 8628 canceled by remote with error code 0")]
    [InlineData("+0300 2026-10-02 02:33:11 ERROR [1342569061 10.6s] connection: connection download closed: remote error: dial tcp4 1.2.3.4:443: i/o timeout")]
    public void LogPrivacy_DropsRoutineConnectionCloses(string line)
    {
        ColituLogPrivacy.SanitizeCoreLine(line).Should().BeNull();
    }

    [Theory]
    [InlineData("+0300 2026-10-03 14:44:42 ERROR [3542861300 189ms] connection: open connection to 1.2.3.4:443 using outbound/hysteria2[proxy]: authentication failed, status code: 404")]
    [InlineData("+0300 2026-10-02 17:17:28 ERROR [1503109085 2m7s] connection: report handshake success: connection timed out")]
    public void LogPrivacy_KeepsErrorsAboutTheTunnel(string line)
    {
        ColituLogPrivacy.SanitizeCoreLine(line).Should().NotBeNull();
    }

    [Fact]
    public void LogPrivacy_KeepsCoreFileNamesReadable()
    {
        var line = "FATAL[0000] start service: open /home/u/.local/share/ColituVPN/binConfigs/config.json: permission denied (run_as_sudo.sh)";
        ColituLogPrivacy.SanitizeCoreLine(line).Should().Be(line);
    }

    [Fact]
    public void Redact_RemovesBrowsingFromSupportLogs()
    {
        var log = string.Join('\n',
            "2026-09-25 01:18:22.7567-INFO ColituVpnService | core notify=False: +0300 2026-09-25 01:18:22 INFO [1 36ms] dns: exchanged A secret-site.example. 5 IN A 1.2.3.4",
            "2026-09-25 01:18:23.0000-INFO ColituVpnService | core notify=True: +0300 2026-09-25 01:18:23 ERROR [2] dial tcp other.example:443: i/o timeout",
            "2026-09-25 01:18:24.0000-INFO ColituVpnService | Connection failed: timeout");

        var redacted = ColituSupportService.Redact(log);

        redacted.Should().NotContain("secret-site").And.NotContain("other.example").And.NotContain("1.2.3.4");
        redacted.Should().Contain("Connection failed: timeout").And.Contain("ERROR");
    }

    [Theory]
    [InlineData("https://api.colitu.com/api/v1", true)]
    [InlineData("http://api.colitu.com/api/v1", false)]
    [InlineData("https://user:pw@api.colitu.com", false)]
    [InlineData("not a url", false)]
    public void ApiBaseUrl_MustBeHttps(string value, bool allowed)
    {
        ColituAuthService.IsAllowedApiBaseUrl(value).Should().Be(allowed);
    }

    [Fact]
    public void Secrets_RoundTripAndAreNotPlainText()
    {
        const string token = "eyJhbGciOiJIUzI1NiJ9.refresh-token-value";
        var sealedValue = ColituHardening.Protect(token);

        sealedValue.Should().NotContain("refresh-token-value");
        ColituHardening.Unprotect(sealedValue).Should().Be(token);
        ColituHardening.Protect(token).Should().NotBe(sealedValue, "every seal uses a fresh nonce");
    }

    [Fact]
    public void Secrets_RejectTampering()
    {
        var bytes = ColituHardening.ProtectBytes(Encoding.UTF8.GetBytes("credentials"));
        bytes[^1] ^= 0x01;

        var act = () => ColituHardening.UnprotectBytes(bytes);
        act.Should().Throw<CryptographicException>();
    }
}
