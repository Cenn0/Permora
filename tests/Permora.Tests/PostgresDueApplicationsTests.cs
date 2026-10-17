using Npgsql;
using Permora.Core;
using Permora.Infrastructure;

namespace Permora.Tests;

/// <summary>
/// Regression for a permanently busy subset of applications exhausting the per-tick
/// selection limit. A rotating cursor must reach all due application UUIDs.
/// </summary>
public sealed class PostgresDueApplicationsTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Due_queue_round_robin_visits_all_hot_applications_and_wraps()
    {
        var value = Environment.GetEnvironmentVariable("PERMORA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(value))
            Assert.Skip("Set PERMORA_TEST_POSTGRES to an isolated permora_test_* PostgreSQL database.");
        var cs = new NpgsqlConnectionStringBuilder(value);
        Assert.StartsWith("permora_test_", cs.Database ?? "");
        var schema = "test_" + Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;
        await using var admin = NpgsqlDataSource.Create(value!);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}"))
            await create.ExecuteNonQueryAsync(ct);
        try
        {
            cs.SearchPath = schema;
            await using var db = NpgsqlDataSource.Create(cs.ConnectionString);
            var store = new PostgresStore(db);
            var fixture = new Fixture();
            await store.MigrateAsync(ct);
            await store.SaveTeamAsync(fixture.Team, null, fixture.Owner, ct);

            var ids = new List<Guid>();
            for (var i = 0; i < 7; i++)
            {
                var app = fixture.App with { Id = Guid.NewGuid(), Slug = $"fairness-app-{i}",
                    Name = $"Fairness {i}" };
                await store.CreateApplicationAsync(app, fixture.Owner, ct);
                ids.Add(app.Id);
                await store.InApplicationAsync(app.Id, async tx =>
                {
                    await tx.SaveOperationAsync(new Operation(Guid.NewGuid(), app.Id, fixture.Owner,
                        new Change { Kind = ChangeKind.Reconcile, Reason = "Remain due in every round" },
                        app.Revision, $"test-queue-{app.Id:N}", "test-hash",
                        OperationStatus.Queued, fixture.Clock.Now, fixture.Clock.Now, 0), ct);
                    return true;
                }, ct);
            }

            var cursor = Guid.Empty;
            var visited = new List<Guid>();
            for (var tick = 0; tick < 4; tick++)
            {
                var due = await store.DueApplicationsAsync(fixture.Clock.Now, 2, cursor, ct);
                Assert.Equal(2, due.Count);
                Assert.Equal(2, due.Distinct().Count());
                visited.AddRange(due);
                cursor = due[^1];
            }

            Assert.True(ids.ToHashSet().IsSubsetOf(visited.ToHashSet()),
                "Every continuously due application must eventually be scheduled");
            Assert.Equal(7, visited.Take(7).Distinct().Count());
            var continuation = await store.DueApplicationsAsync(fixture.Clock.Now, 2, cursor, ct);
            Assert.Equal(2, continuation.Count);
            Assert.All(continuation, id => Assert.Contains(id, ids));
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync(ct);
        }
    }
}
