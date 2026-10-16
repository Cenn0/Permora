using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Permora.Infrastructure;
using Permora.Core;

namespace Permora.Api;

// Explicit opt-in and loopback only. This scheme is never registered in a production host.
public sealed class DevelopmentAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, PortalOptions portal)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address))
            return Task.FromResult(AuthenticateResult.Fail("Development authentication is loopback-only."));
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", portal.DevelopmentSubject), new Claim("iss", portal.Authority),
            new Claim("name", "Demo-Team"), new Claim(ClaimTypes.NameIdentifier, Wire.Subject(portal.Authority, portal.DevelopmentSubject))
        }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
