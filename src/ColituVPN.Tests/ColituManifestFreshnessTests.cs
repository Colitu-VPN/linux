using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>The signed update manifest carries issued_at / expires_at: stale, expired or undated ones are not offered.</summary>
public class ColituManifestFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private static ColituLinuxVersionPayload Manifest(DateTimeOffset? issued, DateTimeOffset? expires) => new()
    {
        LatestVersionCode = 150,
        VersionName = "1.5.0",
        Deb = new ColituLinuxPackage { Url = "https://colitu.com/downloads/linux/colitu-vpn_1.5.0_amd64.deb", Sha256 = new string('a', 64) },
        Rpm = new ColituLinuxPackage { Url = "https://colitu.com/downloads/linux/colitu-vpn-1.5.0-1.x86_64.rpm", Sha256 = new string('b', 64) },
        IssuedAt = issued == null ? null : Iso(issued.Value),
        ExpiresAt = expires == null ? null : Iso(expires.Value)
    };

    [Fact]
    public void FreshManifest_IsAccepted()
    {
        ColituUpdateSignature.IsFresh(Manifest(Now.AddDays(-1), Now.AddDays(29)), Now, out var reason).Should().BeTrue();
        reason.Should().BeEmpty();
        ColituUpdateSignature.IsFresh(Manifest(Now, Now.AddDays(30)), Now, out _).Should().BeTrue();
    }

    [Fact]
    public void ManifestIssuedMoreThan30DaysAgo_IsRejected()
    {
        ColituUpdateSignature.IsFresh(Manifest(Now.AddDays(-31), Now.AddDays(10)), Now, out var reason).Should().BeFalse();
        reason.Should().Contain("30 days");
        ColituUpdateSignature.IsFresh(Manifest(Now.AddDays(-30), Now.AddDays(10)), Now, out _).Should().BeTrue();
    }

    [Fact]
    public void ExpiredManifest_IsRejected()
    {
        ColituUpdateSignature.IsFresh(Manifest(Now.AddDays(-2), Now.AddSeconds(-1)), Now, out var reason).Should().BeFalse();
        reason.Should().Contain("expires_at");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ManifestWithoutDates_IsRejected(bool hasIssued, bool hasExpires)
    {
        var manifest = Manifest(hasIssued ? Now : null, hasExpires ? Now.AddDays(30) : null);
        ColituUpdateSignature.IsFresh(manifest, Now, out _).Should().BeFalse();
        manifest.IssuedAt = "not a date";
        manifest.ExpiresAt = "";
        ColituUpdateSignature.IsFresh(manifest, Now, out _).Should().BeFalse();
    }

    private static string Sign(ECDsa key, string message) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));

    [Fact]
    public void SignatureV1_KeepsTheOldMessage_SoOldClientsStillVerify()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var payload = Manifest(Now, Now.AddDays(30));
        // The v1 text is exactly the pre-freshness format: eight lines, no dates.
        var expectedV1 = string.Join("\n", "colitu-linux-update-v1", "150", "1.5.0",
            payload.Deb!.Url, new string('a', 64), payload.Rpm!.Url, new string('b', 64), "false");
        ColituUpdateSignature.Message(payload).Should().Be(expectedV1);

        // A signature made the old way verifies with the old logic, whatever the dates say.
        payload.Signature = Sign(key, expectedV1);
        ColituUpdateSignature.Verify(payload, pem).Should().BeTrue();
        payload.IssuedAt = null;
        payload.ExpiresAt = null;
        ColituUpdateSignature.Verify(payload, pem).Should().BeTrue();
    }

    [Fact]
    public void SignatureV2_CoversTheV1TextAndTheDates()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var payload = Manifest(Now, Now.AddDays(30));
        var expectedV2 = ColituUpdateSignature.Message(payload) + "\n" + Iso(Now) + "\n" + Iso(Now.AddDays(30));
        ColituUpdateSignature.MessageV2(payload).Should().Be(expectedV2);

        payload.SignatureV2 = Sign(key, expectedV2);
        ColituUpdateSignature.VerifyV2(payload, pem).Should().BeTrue();
    }

    [Fact]
    public void ChangedDates_BreakSignatureV2()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var payload = Manifest(Now, Now.AddDays(30));
        payload.SignatureV2 = Sign(key, ColituUpdateSignature.MessageV2(payload));
        ColituUpdateSignature.VerifyV2(payload, pem).Should().BeTrue();

        payload.ExpiresAt = Iso(Now.AddDays(300));
        ColituUpdateSignature.VerifyV2(payload, pem).Should().BeFalse();
        payload.ExpiresAt = Iso(Now.AddDays(30));
        payload.IssuedAt = Iso(Now.AddDays(1));
        ColituUpdateSignature.VerifyV2(payload, pem).Should().BeFalse();
        payload.IssuedAt = null;
        payload.ExpiresAt = null;
        ColituUpdateSignature.VerifyV2(payload, pem).Should().BeFalse();
    }

    [Fact]
    public void ManifestWithoutSignatureV2_IsRejected_EvenWithAValidV1Signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var payload = Manifest(Now, Now.AddDays(30));
        payload.Signature = Sign(key, ColituUpdateSignature.Message(payload));

        ColituUpdateSignature.Verify(payload, pem).Should().BeTrue();
        ColituUpdateSignature.VerifyV2(payload, pem).Should().BeFalse();
        payload.SignatureV2 = "";
        ColituUpdateSignature.VerifyV2(payload, pem).Should().BeFalse();
        // The v1 signature cannot stand in for v2: it does not cover the dates.
        payload.SignatureV2 = payload.Signature;
        ColituUpdateSignature.VerifyV2(payload, pem).Should().BeFalse();
    }

    [Fact]
    public void ManifestJson_ReadsSnakeCaseDates()
    {
        const string json = """{"latestVersionCode":150,"versionName":"1.5.0","deb":{"url":"https://colitu.com/x.deb","sha256":"aa"},"rpm":{"url":"https://colitu.com/x.rpm","sha256":"bb"},"forceUpdate":false,"issued_at":"2026-10-10T12:00:00Z","expires_at":"2026-11-09T12:00:00Z","signature":"x","signature_v2":"y"}""";
        var payload = JsonSerializer.Deserialize<ColituLinuxVersionPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        payload.IssuedAt.Should().Be("2026-10-10T12:00:00Z");
        payload.ExpiresAt.Should().Be("2026-11-09T12:00:00Z");
        payload.SignatureV2.Should().Be("y");
        ColituUpdateSignature.IsFresh(payload, Now, out _).Should().BeTrue();
    }
}
