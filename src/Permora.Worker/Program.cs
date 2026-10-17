using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Permora.Core;
using Permora.Infrastructure;
using Permora.Worker;

var builder = Host.CreateApplicationBuilder(args.Where(a => a is not "--migrate" and not "--seed-demo").ToArray());
ConfigurationChecks.AddLocalFile(builder.Configuration);
var portal = builder.Configuration.GetSection("Portal").Get<PortalOptions>() ?? new PortalOptions();
ConfigurationChecks.ValidateUrl(portal.Authority, builder.Environment.IsDevelopment() && portal.AllowHttpInDevelopment);
var keycloak = KeycloakConfiguration.Read(builder.Configuration);
foreach (var realm in keycloak.Realms.Values)
{
    ConfigurationChecks.ValidateUrl(realm.BaseUrl, builder.Environment.IsDevelopment() && keycloak.AllowHttpInDevelopment);
    ConfigurationChecks.ValidateUrl(realm.Issuer, builder.Environment.IsDevelopment() && keycloak.AllowHttpInDevelopment);
    if (realm.Issuer.TrimEnd('/') != portal.Authority.TrimEnd('/'))
        throw new InvalidOperationException("Cross-issuer delegation requires authoritative person correlation; it is not enabled in this release.");
}
var connection = ConfigurationChecks.ReadPortalConnectionString(builder.Configuration);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connection));
builder.Services.AddSingleton<PostgresStore>();
builder.Services.AddSingleton<IPortalStore>(p => p.GetRequiredService<PostgresStore>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IReadOnlySet<string>>(ConfigurationChecks.Administrators(portal));
builder.Services.AddSingleton(keycloak);
builder.Services.AddHttpClient<KeycloakConnector>(http => http.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<IAccessConnector>(p => p.GetRequiredService<KeycloakConnector>());
builder.Services.AddTransient<IIdentityConnector>(p => p.GetRequiredService<KeycloakConnector>());
builder.Services.AddTransient<OperationProcessor>();
builder.Services.AddHostedService<PortalWorker>();
using var host = builder.Build();
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await host.Services.GetRequiredService<PostgresStore>().MigrateAsync(CancellationToken.None);
    return;
}
if (args.Contains("--seed-demo", StringComparer.Ordinal))
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seeding is Development-only.");
    await DemoSeed.RunAsync(host.Services.GetRequiredService<IPortalStore>(), portal.Authority, CancellationToken.None);
    return;
}
if (!builder.Environment.IsDevelopment() && keycloak.Realms.Count == 0)
    throw new InvalidOperationException("A production worker requires at least one explicitly configured Keycloak realm.");
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Permora.Worker.Startup");
startupLogger.LogInformation("Worker configured realms: {RealmCount}; registered hosted services: {HostedServiceCount}",
    keycloak.Realms.Count, host.Services.GetServices<IHostedService>().Count());
await host.RunAsync();
