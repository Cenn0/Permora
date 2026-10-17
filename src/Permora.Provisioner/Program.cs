using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Permora.Core;
using Permora.Infrastructure;

// A separate executable with separate credentials. No HTTP endpoint and no arbitrary admin proxy.
var builder = Host.CreateApplicationBuilder(args);
ConfigurationChecks.AddLocalFile(builder.Configuration);
var options = builder.Configuration.GetSection("Provisioning").Get<ProvisioningOptions>() ?? new ProvisioningOptions();
foreach (var realm in options.Realms.Values)
    ConfigurationChecks.ValidateUrl(realm.BaseUrl, builder.Environment.IsDevelopment() && options.AllowHttpInDevelopment);
var connectionString = ConfigurationChecks.ReadPortalConnectionString(builder.Configuration);
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IPortalStore, PostgresStore>();
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<ClientPreparer>(http => http.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<ProvisioningWorker>();
using var host = builder.Build();
await host.RunAsync();

public sealed record ProvisioningRealm(string BaseUrl, string Realm, string ClientId, string SecretFile);
public sealed class ProvisioningOptions
{
    public Dictionary<string, ProvisioningRealm> Realms { get; init; } = [];
    public bool AllowHttpInDevelopment { get; init; }
}
public sealed class ProvisioningWorker(IServiceProvider services, ProvisioningOptions options, ILogger<ProvisioningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IPortalStore>();
                foreach (var app in (await store.ApplicationsAsync([], true, stoppingToken))
                    .Where(a => a.Status == ApplicationStatus.AwaitingIam && a.KeycloakClientUuid is null && options.Realms.ContainsKey(a.RealmKey)).Take(20))
                {
                    try
                    {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        budget.CancelAfter(TimeSpan.FromSeconds(50));
                        await store.InApplicationAsync(app.Id, async tx =>
                        {
                            if (tx.Application.KeycloakClientUuid is not null || tx.Application.Status != ApplicationStatus.AwaitingIam) return false;
                            var prepared = await scope.ServiceProvider.GetRequiredService<ClientPreparer>().PrepareAsync(tx.Application, budget.Token);
                            tx.Application = tx.Application with { KeycloakClientUuid = prepared.Id, KeycloakClientId = prepared.ClientId,
                                Revision = tx.Application.Revision + 1 };
                            await tx.AppendAuditAsync(new Actor("system:provisioner", "Isolated provisioner"), "client.prepared_disabled",
                                prepared.ClientId, DateTimeOffset.UtcNow, budget.Token);
                            // FGAP policy activation remains an explicit central IAM gate; do NOT activate here.
                            return true;
                        }, budget.Token);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning("Client preparation for {Application} requires attention: {Type}", app.Id, ex.GetType().Name); }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { logger.LogError("Provisioning iteration failed: {Type}", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
public sealed class ClientPreparer(HttpClient http, ProvisioningOptions options)
{
    public async Task<PreparedClient> PrepareAsync(ApplicationDocument app, CancellationToken ct)
    {
        if (!options.Realms.TryGetValue(app.RealmKey, out var realm)) throw new InvalidOperationException("Provisioning realm is not configured.");
        Policy.Key(app.Slug);
        if (app.Environment is not ("Development" or "Test" or "Production")) throw new InvalidOperationException("Invalid environment.");
        var clientId = $"app-{app.Slug}-{app.Environment.ToLowerInvariant()}-api";
        var secret = (await File.ReadAllTextAsync(realm.SecretFile, ct)).Trim();
        if (secret.Length < 16) throw new InvalidOperationException("Provisioning credential is missing or too short.");
        var root = realm.BaseUrl.TrimEnd('/');
        using var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["client_id"] = realm.ClientId, ["client_secret"] = secret, ["grant_type"] = "client_credentials" });
        using var tokenResponse = await http.PostAsync($"{root}/realms/{Uri.EscapeDataString(realm.Realm)}/protocol/openid-connect/token", tokenBody, ct);
        tokenResponse.EnsureSuccessStatusCode();
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        var token = tokenJson.RootElement.GetProperty("access_token").GetString() ?? throw new InvalidDataException("Missing service token.");
        var clientsPath = $"{root}/admin/realms/{Uri.EscapeDataString(realm.Realm)}/clients";
        async Task<ClientDocument[]> Lookup()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, clientsPath + "?clientId=" + Uri.EscapeDataString(clientId) + "&max=2");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ClientDocument[]>(cancellationToken: ct) ?? throw new InvalidDataException("Empty client lookup.");
        }
        var existing = await Lookup();
        if (existing.Length == 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, clientsPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new
            {
                clientId, name = app.Name, protocol = "openid-connect", enabled = false,
                bearerOnly = true, publicClient = false, standardFlowEnabled = false,
                implicitFlowEnabled = false, directAccessGrantsEnabled = false,
                serviceAccountsEnabled = false, fullScopeAllowed = false,
                attributes = new Dictionary<string, string> { ["permora.application"] = app.Id.ToString(), ["permora.lifecycle"] = "awaiting-iam" }
            });
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Conflict) response.EnsureSuccessStatusCode();
            existing = await Lookup();
        }
        if (existing.Length != 1) throw new InvalidDataException("Client preparation was not uniquely confirmed.");
        var client = existing[0];
        if (client.ClientId != clientId || client.Attributes?.GetValueOrDefault("permora.application") != app.Id.ToString()
            || client.Enabled != false || client.FullScopeAllowed != false || client.ServiceAccountsEnabled != false
            || client.DirectAccessGrantsEnabled != false || client.ImplicitFlowEnabled != false
            || client.StandardFlowEnabled != false || client.Protocol != "openid-connect")
            throw new DomainException("provisioning_ownership_conflict", "Never adopt an existing foreign or already-enabled client.", 409);
        if (!Guid.TryParse(client.Id, out _)) throw new InvalidDataException("Invalid client UUID.");
        return new PreparedClient(client.Id, client.ClientId);
    }
    public sealed record PreparedClient(string Id, string ClientId);
    private sealed record ClientDocument(string Id, string ClientId, bool? Enabled, bool? FullScopeAllowed,
        bool? ServiceAccountsEnabled, bool? DirectAccessGrantsEnabled, bool? ImplicitFlowEnabled,
        bool? StandardFlowEnabled, string? Protocol, Dictionary<string, string>? Attributes);
}
