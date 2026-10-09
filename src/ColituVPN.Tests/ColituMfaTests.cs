using System.Net;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

[Collection("Language")]
public class ColituMfaTests
{
    [Fact]
    public void Challenge_IsReadFromA403WithMfaRequired()
    {
        var challenge = ColituAuthService.ParseMfaChallenge(HttpStatusCode.Forbidden,
            """{"error":{"code":"MFA_REQUIRED","message":"Two-factor code required"},"mfa_token":"tok_123","mfa_expires_in":300}""");
        challenge.Should().NotBeNull();
        challenge!.Token.Should().Be("tok_123");
        challenge.ExpiresInSeconds.Should().Be(300);
    }

    [Theory]
    [InlineData("""{"error":{"code":"MFA_REQUIRED"},"mfa_token":"t","mfa_method":"email","mfa_expires_in":600}""", "email", 600)]
    [InlineData("""{"error":{"code":"MFA_REQUIRED"},"mfa_token":"t","mfa_method":"totp"}""", "totp", 300)]
    [InlineData("""{"error":{"code":"MFA_REQUIRED"},"mfa_token":"t"}""", "totp", 300)]
    public void Challenge_ReadsTheMethod(string body, string method, int expires)
    {
        var challenge = ColituAuthService.ParseMfaChallenge(HttpStatusCode.Forbidden, body);
        challenge.Should().NotBeNull();
        challenge!.Method.Should().Be(method);
        challenge.ExpiresInSeconds.Should().Be(expires);
    }

    [Fact]
    public void Challenge_DefaultsToFiveMinutesWithoutExpiry()
    {
        ColituAuthService.ParseMfaChallenge(HttpStatusCode.Forbidden, """{"error":{"code":"MFA_REQUIRED"},"mfa_token":"t"}""")!
            .ExpiresInSeconds.Should().Be(300);
    }

    [Fact]
    public void Challenge_AcceptsAStringErrorCode()
    {
        ColituAuthService.ParseMfaChallenge(HttpStatusCode.Forbidden, """{"error":"MFA_REQUIRED","mfa_token":"t","mfa_expires_in":120}""")!
            .ExpiresInSeconds.Should().Be(120);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"MFA_REQUIRED_UPDATE_APP"}}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"MFA_REQUIRED"}}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"MFA_REQUIRED"},"mfa_token":""}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"REGION_NOT_SUPPORTED"},"mfa_token":"t"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"MFA_REQUIRED"},"mfa_token":"t"}""")]
    [InlineData(HttpStatusCode.Forbidden, "<html>Forbidden</html>")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    public void Challenge_IsNullForAnythingElse(HttpStatusCode status, string body)
    {
        ColituAuthService.ParseMfaChallenge(status, body).Should().BeNull();
    }

    [Theory]
    [InlineData("MFA_INVALID_CODE", "mfa.err.invalid")]
    [InlineData("MFA_TOKEN_EXPIRED", "mfa.err.expired")]
    [InlineData("MFA_REQUIRED", "mfa.err.required")]
    [InlineData("MFA_REQUIRED_UPDATE_APP", "mfa.err.updateApp")]
    [InlineData("RATE_LIMITED", "err.rateLimited")]
    public void ErrorCodes_GetFriendlyMessages(string code, string key)
    {
        ColituAuthService.FriendlyMessage(code, "raw server text", HttpStatusCode.Unauthorized).Should().Be(Loc.I[key]);
    }

    [Fact]
    public void LoginAnnouncesTheMfaFeature()
    {
        ColituAuthService.FeaturesHeader.Should().Be("X-Colitu-Features");
        ColituAuthService.FeaturesValue.Should().Be("mfa");
    }
}
