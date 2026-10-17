using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Permora.Infrastructure;

namespace Permora.Tests;

public sealed class SessionAssuranceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly PortalOptions Policy = new()
    {
        PrivilegedAcr = "2",
        PrivilegedAuthenticationMaxAgeMinutes = 10
    };

    private static ClaimsPrincipal Principal(string? acr = "2", string? authTime = null, bool authenticated = true,
        bool duplicateAcr = false, bool duplicateTime = false)
    {
        var claims = new List<Claim> { new("sub", "approver") };
        if (acr is not null) claims.Add(new Claim("acr", acr));
        if (authTime is not null) claims.Add(new Claim("auth_time", authTime));
        if (duplicateAcr) claims.Add(new Claim("acr", acr ?? "2"));
        if (duplicateTime) claims.Add(new Claim("auth_time", authTime ?? "1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "oidc" : null));
    }

    private static string Seconds(DateTimeOffset value) => value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void Fresh_high_assurance_authentication_allows_privileged_write()
        => Assert.True(SessionAssurance.IsPrivileged(Principal(authTime: Seconds(Now.AddMinutes(-2))), Policy, Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("gold")]
    public void Absent_or_untrusted_acr_never_grants_step_up(string? acr)
        => Assert.False(SessionAssurance.IsPrivileged(Principal(acr, Seconds(Now)), Policy, Now));

    [Fact]
    public void Missing_authentication_time_is_rejected()
        => Assert.False(SessionAssurance.IsPrivileged(Principal(), Policy, Now));

    [Theory]
    [InlineData("not-a-timestamp")]
    [InlineData("-1")]
    [InlineData("999999999999999999")]
    public void Invalid_signed_authentication_time_is_rejected(string time)
        => Assert.False(SessionAssurance.IsPrivileged(Principal(authTime: time), Policy, Now));

    [Fact]
    public void Expired_authentication_requires_a_new_step_up()
        => Assert.False(SessionAssurance.IsPrivileged(Principal(authTime: Seconds(Now.AddMinutes(-11))), Policy, Now));

    [Fact]
    public void Future_authentication_time_fails_closed()
        => Assert.False(SessionAssurance.IsPrivileged(Principal(authTime: Seconds(Now.AddMinutes(2))), Policy, Now));

    [Fact]
    public void Unauthenticated_identity_cannot_claim_mfa()
        => Assert.False(SessionAssurance.IsPrivileged(Principal(authTime: Seconds(Now), authenticated: false), Policy, Now));

    [Fact]
    public void Ambiguous_acr_or_auth_time_is_rejected()
    {
        Assert.False(SessionAssurance.IsPrivileged(Principal(authTime: Seconds(Now), duplicateAcr: true), Policy, Now));
        Assert.False(SessionAssurance.IsPrivileged(Principal(authTime: Seconds(Now), duplicateTime: true), Policy, Now));
    }

    [Fact]
    public void Step_up_request_requires_essential_id_token_acr_and_auth_time()
    {
        using var json = JsonDocument.Parse(SessionAssurance.RequiredIdTokenClaims("2"));
        var token = json.RootElement.GetProperty("id_token");
        Assert.True(token.GetProperty("acr").GetProperty("essential").GetBoolean());
        Assert.Equal("2", token.GetProperty("acr").GetProperty("values")[0].GetString());
        Assert.True(token.GetProperty("auth_time").GetProperty("essential").GetBoolean());
    }
}
