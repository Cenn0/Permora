using Npgsql;
using Permora.Infrastructure;

namespace Permora.Tests;

public sealed class PostgresTests
{
    [Fact][Trait("Category", "PostgresIntegration")]
    public async Task Migration_atomicity_audit_directory_and_dashboard_use_real_postgresql()
    {
        var value = Environment.GetEnvironmentVariable("PERMORA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(value)) Assert.Skip("Set PERMORA_TEST_POSTGRES to an isolated permora_test_* PostgreSQL database.");
        var options = new NpgsqlConnectionStringBuilder(value);
        Assert.StartsWith("permora_test_", options.Database ?? "");
        var schema = "test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(value!);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            options.SearchPath = schema;
            await using var source = NpgsqlDataSource.Create(options.ConnectionString); var store = new PostgresStore(source); var f = new Fixture();
            await store.MigrateAsync(TestContext.Current.CancellationToken); await store.MigrateAsync(TestContext.Current.CancellationToken);
            await store.SaveTeamAsync(f.Team, null, f.Owner, TestContext.Current.CancellationToken);
            await store.CreateApplicationAsync(f.App, f.Owner, TestContext.Current.CancellationToken);
            await store.ReplaceDirectoryAsync("enterprise", f.Directory, TestContext.Current.CancellationToken);
            await store.ReplaceDirectoryAsync("enterprise", [], TestContext.Current.CancellationToken);
            var retained = await store.DirectoryAsync("enterprise", TestContext.Current.CancellationToken);
            Assert.Equal(f.Directory.Count, retained.Count); Assert.All(retained, p => Assert.True(p.Enabled));
            Assert.Equal(f.Employee.SyncedAt, retained.Single(p => p.Id == f.Employee.Id).SyncedAt);
            // Missing from a filtered listing is not deletion; a concrete disabled record is authoritative.
            await store.ReplaceDirectoryAsync("enterprise", [f.Employee with { Enabled = false }], TestContext.Current.CancellationToken);
            Assert.False((await store.DirectoryAsync("enterprise", TestContext.Current.CancellationToken)).Single(p => p.Id == f.Employee.Id).Enabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InApplicationAsync<bool>(f.App.Id, async tx =>
            {
                tx.Application = tx.Application with { Name = "Must roll back", Revision = 2 };
                await tx.AppendAuditAsync(f.Owner, "must.rollback", "Test", f.Clock.Now, TestContext.Current.CancellationToken);
                throw new InvalidOperationException("Rollback test");
            }, TestContext.Current.CancellationToken));
            Assert.Equal(f.App.Name, (await store.ApplicationAsync(f.App.Id, TestContext.Current.CancellationToken))!.Name);
            Assert.DoesNotContain(await store.AuditAsync(f.App.Id, 0, 100, TestContext.Current.CancellationToken), a => a.Action == "must.rollback");
            // More than a page of requests: the count must not be calculated from a truncated feed.
            await store.InApplicationAsync(f.App.Id, async tx =>
            {
                for (var i = 0; i < 35; i++) await tx.SaveOperationAsync(new(Guid.NewGuid(), f.App.Id, f.Owner, f.Grant(), 1,
                    "request-" + i, "hash", OperationStatus.AwaitingApproval, f.Clock.Now, f.Clock.Now, 0), TestContext.Current.CancellationToken);
                return true;
            }, TestContext.Current.CancellationToken);
            var dashboard = await store.DashboardAsync([f.App.Id], TestContext.Current.CancellationToken);
            Assert.Equal(35, dashboard.AwaitingApproval); Assert.Equal(20, dashboard.Requests.Count);
            Assert.Equal(0, (await store.DashboardAsync([], TestContext.Current.CancellationToken)).AwaitingApproval);
            var audit = await store.AuditAsync(f.App.Id, 0, 100, TestContext.Current.CancellationToken); Assert.True(AuditChain.Verify(audit));
            await using var mutate = source.CreateCommand("UPDATE audit_events SET sequence=sequence+1");
            await Assert.ThrowsAsync<PostgresException>(() => mutate.ExecuteNonQueryAsync());
        }
        finally { await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE"); await drop.ExecuteNonQueryAsync(); }
    }
}
