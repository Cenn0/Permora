using Npgsql;

namespace Permora.Infrastructure;

/// <summary>Aggregated operational state without principal IDs, secrets or realm names.</summary>
public sealed record OperationsHealthSnapshot(long Queued, long AwaitingApproval, long Failed, long Conflict,
    long DueRevocations, double OldestDueRevocationSeconds);

public static class OperationsHealth
{
    public static async Task<OperationsHealthSnapshot> ReadAsync(NpgsqlDataSource database,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT
                count(*) FILTER (WHERE status = 'Queued'),
                count(*) FILTER (WHERE status = 'AwaitingApproval'),
                count(*) FILTER (WHERE status = 'Failed'),
                count(*) FILTER (WHERE status = 'Conflict'),
                count(*) FILTER (WHERE status = 'Queued' AND actor_key = 'system' AND next_at <= @now),
                coalesce(extract(epoch FROM (@now - min(created_at)
                    FILTER (WHERE status = 'Queued' AND actor_key = 'system' AND next_at <= @now))), 0)::double precision
            FROM operations
            """, connection);
        command.Parameters.AddWithValue("now", now);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidDataException("Operational health query returned no result.");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetDouble(5));
    }
}
