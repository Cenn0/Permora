using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

namespace Permora.Infrastructure;

/// <summary>Fail-closed server-side assurance of a trusted ID-token authentication context.</summary>
public static class SessionAssurance
{
    public const string StepUpChallengeFlag = "permora.step_up";

    public static bool IsPrivileged(ClaimsPrincipal? principal, PortalOptions options, DateTimeOffset now)
    {
        if (principal?.Identity?.IsAuthenticated != true
            || string.IsNullOrWhiteSpace(options.PrivilegedAcr)
            || options.PrivilegedAuthenticationMaxAgeMinutes is < 1 or > 60)
            return false;

        // Never use an unsigned UI assertion, access-token freshness, cookie renewal time,
        // or a mutable client-side claim as a substitute for an IDP-authenticated ID token.
        var acr = principal.FindAll("acr").Select(c => c.Value).ToArray();
        var authTimes = principal.FindAll("auth_time").Select(c => c.Value).ToArray();
        if (acr.Length != 1 || !string.Equals(acr[0], options.PrivilegedAcr, StringComparison.Ordinal)
            || authTimes.Length != 1
            || !long.TryParse(authTimes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return false;

        try
        {
            var age = now.ToUniversalTime() - DateTimeOffset.FromUnixTimeSeconds(seconds);
            return age >= TimeSpan.FromSeconds(-30)
                && age <= TimeSpan.FromMinutes(options.PrivilegedAuthenticationMaxAgeMinutes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static string RequiredIdTokenClaims(string acr)
    {
        // OIDC 'essential' requests higher assurance from Keycloak; the API independently
        // checks the returned signed acr/auth_time and never trusts the request alone.
        return JsonSerializer.Serialize(new
        {
            id_token = new
            {
                acr = new { essential = true, values = new[] { acr } },
                auth_time = new { essential = true }
            }
        });
    }
}
