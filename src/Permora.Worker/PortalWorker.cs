using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Permora.Core;
using Permora.Infrastructure;

namespace Permora.Worker;

public sealed class PortalWorker(IServiceProvider services, KeycloakOptions options, ILogger<PortalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSync = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                using var scope = services.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IPortalStore>();
                if (DateTimeOffset.UtcNow >= nextSync)
                {
                    foreach (var realm in options.Realms.Keys)
                    {
                        try
                        {
                            using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            budget.CancelAfter(TimeSpan.FromMinutes(3));
                            var snapshot = await scope.ServiceProvider.GetRequiredService<IIdentityConnector>().ReadDirectoryAsync(realm, budget.Token);
                            await store.ReplaceDirectoryAsync(realm, snapshot, budget.Token);
                            var disabled = (await store.DirectoryAsync(realm, budget.Token)).Where(p => !p.Enabled).Select(p => p.Id).ToHashSet();
                            foreach (var app in (await store.ApplicationsAsync([], true, budget.Token)).Where(a => a.RealmKey == realm))
                                await store.InApplicationAsync(app.Id, async tx =>
                                {
                                    var now = DateTimeOffset.UtcNow;
                                    foreach (var assignment in tx.Application.Assignments.Where(a => disabled.Contains(a.PrincipalId)))
                                        await ChangeService.QueueRevocationAsync(tx, assignment.Id, "Identity explicitly disabled in the source", now, budget.Token);
                                    // External Keycloak/LDAP group membership can change after an
                                    // approval. Revoke affected group grants, not unrelated direct
                                    // individual assignments, when current complete membership
                                    // makes a newly conflicting SoD pair effective.
                                    foreach (var assignmentId in DirectoryGovernance.GroupRevocationsForSeparation(tx.Application, snapshot))
                                        await ChangeService.QueueRevocationAsync(tx, assignmentId, "External group membership violates separation of duties", now, budget.Token);
                                    return true;
                                }, budget.Token);
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        { logger.LogWarning("Directory synchronization failed for {Realm}: {Type}", realm, ex.GetType().Name); }
                    }
                    // Missing or incomplete snapshots do not replace the last complete snapshot.
                    nextSync = DateTimeOffset.UtcNow.AddMinutes(5);
                    await CleanupSessionsAsync(scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>(), stoppingToken);
                }
                foreach (var id in await store.DueApplicationsAsync(DateTimeOffset.UtcNow, 100, stoppingToken))
                {
                    try
                    {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        budget.CancelAfter(TimeSpan.FromSeconds(60));
                        var processor = scope.ServiceProvider.GetRequiredService<OperationProcessor>();
                        await processor.SweepAsync(id, budget.Token);
                        await processor.ProcessAsync(id, budget.Token);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    { logger.LogWarning("Operation batch for {ApplicationId} could not complete: {Type}", id, ex.GetType().Name); }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            { logger.LogError("Worker iteration failed: {Type}", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    private static async Task CleanupSessionsAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("DELETE FROM sessions WHERE expires_at < now()", connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
