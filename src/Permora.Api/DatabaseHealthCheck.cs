using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Permora.Infrastructure;

namespace Permora.Api;

public sealed class DatabaseHealthCheck(NpgsqlDataSource source) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM schema_versions WHERE version=@version)", connection);
            command.Parameters.AddWithValue("version", PostgresStore.SchemaVersion);
            return await command.ExecuteScalarAsync(cancellationToken) is true
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database migration required.");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        { return HealthCheckResult.Unhealthy("Database unavailable or not migrated."); }
    }
}
