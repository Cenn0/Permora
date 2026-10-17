using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using Permora.Api;
using Permora.Core;
using Permora.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
ConfigurationChecks.AddLocalFile(builder.Configuration);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 262144);
var options = ConfigurationChecks.ReadPortal(builder.Configuration, builder.Environment.IsDevelopment());
var admins = ConfigurationChecks.Administrators(options);
var connectionString = ConfigurationChecks.ReadPortalConnectionString(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IReadOnlySet<string>>(admins);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<PostgresStore>();
builder.Services.AddSingleton<IPortalStore>(p => p.GetRequiredService<PostgresStore>());
builder.Services.AddScoped<ChangeService>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddPermoraHostingSecurity(builder.Configuration, options, builder.Environment.IsDevelopment());
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-XSRF-TOKEN"; o.Cookie.Name = "Permora.Antiforgery"; o.Cookie.SecurePolicy = options.AllowHttpInDevelopment && builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; });
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    o.SerializerOptions.MaxDepth = 32;
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
});
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
if (options.EnableDevelopmentLogin)
{
    builder.Services.AddAuthentication("Development").AddScheme<AuthenticationSchemeOptions, DevelopmentAuthentication>("Development", _ => { });
}
else
{
    builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = "Permora.Session"; o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() && options.AllowHttpInDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(30); o.SlidingExpiration = false;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    })
    .AddOpenIdConnect(o =>
    {
        o.Authority = options.Authority; o.ClientId = options.ClientId;
        o.ClientSecret = File.ReadAllText(options.ClientSecretFile).Trim();
        o.ResponseType = "code"; o.UsePkce = true; o.SaveTokens = false; o.MapInboundClaims = false;
        // Preserve the signed ID-token ACR after ASP.NET Core's default claim processing.
        o.ClaimActions.Remove("acr");
        o.RequireHttpsMetadata = !(builder.Environment.IsDevelopment() && options.AllowHttpInDevelopment);
        o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile");
        o.TokenValidationParameters.NameClaimType = "name";
        o.TokenValidationParameters.ValidateIssuer = true;
        o.TokenValidationParameters.ValidIssuer = options.Authority.TrimEnd('/');
        o.TokenValidationParameters.ValidateAudience = true;
        o.TokenValidationParameters.ValidAudience = options.ClientId;
        o.Events.OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is ClaimsIdentity identity)
            {
                var actor = CurrentActor(context.Principal, options);
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, actor.Key));
            }
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToIdentityProvider = context =>
        {
            context.ProtocolMessage.RedirectUri = options.PublicOrigin.TrimEnd('/') + "/signin-oidc";
            if (context.Properties.Items.ContainsKey(SessionAssurance.StepUpChallengeFlag))
            {
                context.ProtocolMessage.SetParameter("claims", SessionAssurance.RequiredIdTokenClaims(options.PrivilegedAcr));
                context.ProtocolMessage.SetParameter("max_age", "0");
                context.ProtocolMessage.Prompt = "login";
            }
            if (context.Request.Path.StartsWithSegments("/api"))
            { context.Response.StatusCode = 401; context.HandleResponse(); }
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.PostLogoutRedirectUri = options.PublicOrigin.TrimEnd('/') + "/signout-callback-oidc";
            return Task.CompletedTask;
        };
    });
    builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
        .Configure<SessionStore>((cookie, store) => cookie.SessionStore = store);
}
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy("PlatformAdmin", policy => policy.RequireAssertion(c =>
        c.User.Identity?.IsAuthenticated == true && admins.Contains(CurrentActor(c.User, options).Key)));
});

var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/bff"))
        context.Response.Headers.CacheControl = "no-store";
    if (HostingSecurity.RequiresHttpsRejection(context, app.Environment.IsDevelopment()))
    {
        await Results.Problem(statusCode: 400, title: "https_required", detail: "HTTPS or a configured trusted TLS proxy is required.").ExecuteAsync(context);
        return;
    }
    try { await next(context); }
    catch (DomainException ex)
    { await Results.Problem(statusCode: ex.Status, title: ex.Code, detail: ex.Message, extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context); }
    catch (BadHttpRequestException)
    { await Results.Problem(statusCode: 400, title: "invalid_request", detail: "The request does not match the API contract.").ExecuteAsync(context); }
    catch (AntiforgeryValidationException)
    { await Results.Problem(statusCode: 400, title: "invalid_csrf", detail: "Refresh the session before retrying.").ExecuteAsync(context); }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure)
    { await Results.Problem(statusCode: 409, title: "concurrent_change", detail: "The application is being updated. Retry after reloading.").ExecuteAsync(context); }
    catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError("Request {TraceId} failed with {ExceptionType}", context.TraceIdentifier, ex.GetType().Name);
        await Results.Problem(statusCode: 500, title: "internal_error", detail: "The request could not be completed.", extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
    }
});
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var protectedPath = context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/bff");
    var mutating = HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
        || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method);
    if (protectedPath && mutating)
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    if (context.Request.Path.StartsWithSegments("/api/v1") && mutating && !options.EnableDevelopmentLogin
        && !SessionAssurance.IsPrivileged(context.User, options, DateTimeOffset.UtcNow))
        throw new DomainException("step_up_required", "A fresh Keycloak MFA verification is required before changing access. Open /bff/step-up and retry.", 403);
    await next(context);
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapOpenApi().RequireAuthorization("PlatformAdmin");
app.MapGet("/bff/session", (HttpContext context, IAntiforgery antiforgery) =>
{
    var authenticated = context.User.Identity?.IsAuthenticated == true;
    var tokens = antiforgery.GetAndStoreTokens(context);
    context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
    {
        HttpOnly = false, Secure = !app.Environment.IsDevelopment() || !options.AllowHttpInDevelopment,
        SameSite = SameSiteMode.Strict, Path = "/"
    });
    return Results.Ok(new
    {
        authenticated, name = authenticated ? CurrentActor(context.User, options).Name : "",
        subjectKey = authenticated ? CurrentActor(context.User, options).Key : "",
        administrator = authenticated && admins.Contains(CurrentActor(context.User, options).Key),
        development = options.EnableDevelopmentLogin,
        strongAuthentication = options.EnableDevelopmentLogin || SessionAssurance.IsPrivileged(context.User, options, DateTimeOffset.UtcNow)
    });
}).AllowAnonymous();
app.MapGet("/bff/login", () => options.EnableDevelopmentLogin ? Results.Redirect("/")
    : Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous();
app.MapGet("/bff/step-up", () =>
{
    if (options.EnableDevelopmentLogin) return (IResult)Results.Redirect("/");
    var properties = new AuthenticationProperties { RedirectUri = "/" };
    properties.Items[SessionAssurance.StepUpChallengeFlag] = "true";
    return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
}).RequireAuthorization();
app.MapPost("/bff/logout", async (HttpContext context) =>
{
    // Local logout intentionally leaves the shared Keycloak SSO session alone.
    if (!options.EnableDevelopmentLogin) await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});
ApiEndpoints.Map(app, options, admins);
app.MapFallback("/api/{**path}", () => Results.NotFound()).RequireAuthorization();
app.MapFallback("/bff/{**path}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();
app.Run();

static Actor CurrentActor(ClaimsPrincipal user, PortalOptions portal) => ApiEndpoints.Actor(user, portal);
public partial class Program { }
