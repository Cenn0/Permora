using Npgsql;
using Permora.Infrastructure;

namespace Permora.Tests;

/// <summary>
/// Safety invariant: an unconfirmed manual revoke may bypass grant backoff,
/// while ordinary writes must not bypass any queued revoke. These tests do not
/// claim that a simulated connector is a production Keycloak readback.
/// </summary>
public sealed class RevocationPriorityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Fixture F, MemoryStore Store, Assignment Existing, Operation Grant, Operation Revoke) Setup()
    {
        var f = new Fixture();
        var existing = f.Assignment();
        var store = new MemoryStore(f) { App = f.App with { Assignments = [existing], Revision = 3 } };
        var grant = new Operation(Guid.NewGuid(), f.App.Id, f.Owner, f.Grant(), 1,
            "earlier-grant", "grant-hash", OperationStatus.Queued, f.Clock.Now,
            f.Clock.Now, 0);
        var revoke = new Operation(Guid.NewGuid(), f.App.Id, f.Owner,
            new Change { Kind = ChangeKind.RevokeAccess, TargetId = existing.Id,
                Reason = "Emergency role revocation" }, 1,
            "manual-revoke", "revoke-hash", OperationStatus.Queued,
            f.Clock.Now.AddSeconds(1), f.Clock.Now, 0);
        store.Operations[grant.Id] = grant;
        store.Operations[revoke.Id] = revoke;
        return (f, store, existing, grant, revoke);
    }

    [Fact]
    public async Task Manual_revoke_preempts_older_queued_grant_and_stale_revision()
    {
        var (f, store, existing, grant, revoke) = Setup();
        var connector = new FakeConnector();

        Assert.True(await new OperationProcessor(store, connector, f.Admins, f.Clock)
            .ProcessAsync(f.App.Id, Ct));

        Assert.DoesNotContain(store.App.Assignments, a => a.Id == existing.Id);
        Assert.Equal(OperationStatus.Applied, store.Operations[revoke.Id].Status);
        Assert.Equal(OperationStatus.Queued, store.Operations[grant.Id].Status);
        Assert.Equal(4, store.App.Revision);
        Assert.Equal(1, connector.Writes);
    }

    [Fact]
    public async Task Backing_off_revoke_blocks_grants_until_remote_state_is_resolved()
    {
        var (f, store, existing, grant, revoke) = Setup();
        store.Operations[revoke.Id] = revoke with { NextAttemptAt = f.Clock.Now.AddMinutes(5), Attempts = 1 };
        var connector = new FakeConnector();

        Assert.False(await new OperationProcessor(store, connector, f.Admins, f.Clock)
            .ProcessAsync(f.App.Id, Ct));

        Assert.Equal(0, connector.Writes);
        Assert.Contains(store.App.Assignments, a => a.Id == existing.Id);
        Assert.Equal(OperationStatus.Queued, store.Operations[grant.Id].Status);
    }

    [Fact]
    public async Task Manual_revocation_does_not_expire_after_seven_days_of_queueing()
    {
        var (f, store, existing, _, revoke) = Setup();
        store.Operations.Remove(store.Operations.Keys.Single(id => id != revoke.Id));
        f.Clock.Now = f.Clock.Now.AddDays(8);
        store.Directory = f.Directory.Select(p => p with { SyncedAt = f.Clock.Now }).ToArray();

        Assert.True(await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock)
            .ProcessAsync(f.App.Id, Ct));
        Assert.Equal(OperationStatus.Applied, store.Operations[revoke.Id].Status);
        Assert.DoesNotContain(store.App.Assignments, a => a.Id == existing.Id);
    }

    [Fact]
    public async Task Manual_revocation_still_requires_current_owner_authority()
    {
        var (f, store, existing, grant, revoke) = Setup();
        store.Operations.Remove(grant.Id);
        store.Team = store.Team with
        {
            Members = store.Team.Members.Where(member => member.SubjectKey != f.Owner.Key).ToArray()
        };
        var connector = new FakeConnector();

        Assert.True(await new OperationProcessor(store, connector, f.Admins, f.Clock)
            .ProcessAsync(f.App.Id, Ct));
        Assert.Equal(OperationStatus.Conflict, store.Operations[revoke.Id].Status);
        Assert.Equal("not_found", store.Operations[revoke.Id].ErrorCode);
        Assert.Contains(store.App.Assignments, a => a.Id == existing.Id);
        Assert.Equal(0, connector.Writes);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Real_postgres_revocation_preempts_grant_and_prevents_backoff_bypass()
    {
        var value = Environment.GetEnvironmentVariable("PERMORA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(value))
            Assert.Skip("Set PERMORA_TEST_POSTGRES to an isolated permora_test_* PostgreSQL database.");
        var options = new NpgsqlConnectionStringBuilder(value);
        Assert.StartsWith("permora_test_", options.Database ?? "");
        var schema = "test_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(value!);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}"))
            await create.ExecuteNonQueryAsync(Ct);
        try
        {
            options.SearchPath = schema;
            await using var source = NpgsqlDataSource.Create(options.ConnectionString);
            var store = new PostgresStore(source);
            var f = new Fixture();
            var existing = f.Assignment();
            var app = f.App with { Revision = 3, Assignments = [existing] };
            await store.MigrateAsync(Ct);
            await store.SaveTeamAsync(f.Team, null, f.Owner, Ct);
            await store.CreateApplicationAsync(app, f.Owner, Ct);
            await store.ReplaceDirectoryAsync(app.RealmKey, f.Directory, Ct);

            var grant = new Operation(Guid.NewGuid(), app.Id, f.Owner, f.Grant(), 1,
                "pg-old-grant", "hash-grant", OperationStatus.Queued,
                f.Clock.Now, f.Clock.Now, 0);
            var revoke = new Operation(Guid.NewGuid(), app.Id, f.Owner,
                new Change { Kind = ChangeKind.RevokeAccess, TargetId = existing.Id,
                    Reason = "Postgres emergency revoke" }, 1,
                "pg-revoke", "hash-revoke", OperationStatus.Queued,
                f.Clock.Now.AddSeconds(1), f.Clock.Now.AddMinutes(5), 1);
            await store.InApplicationAsync(app.Id, async tx =>
            {
                await tx.SaveOperationAsync(grant, Ct);
                await tx.SaveOperationAsync(revoke, Ct);
                return true;
            }, Ct);

            // An older, immediately due grant must not pass a queued revoke
            // that is currently in retry/backoff.
            var blocked = await store.InApplicationAsync(app.Id, tx => tx.NextOperationAsync(f.Clock.Now, Ct), Ct);
            Assert.Null(blocked);

            await store.InApplicationAsync(app.Id, async tx =>
            {
                await tx.SaveOperationAsync(revoke with { NextAttemptAt = f.Clock.Now }, Ct);
                return true;
            }, Ct);

            var next = await store.InApplicationAsync(app.Id, tx => tx.NextOperationAsync(f.Clock.Now, Ct), Ct);
            Assert.Equal(revoke.Id, next?.Id);

            var connector = new FakeConnector();
            var processor = new OperationProcessor(store, connector, f.Admins, f.Clock);
            Assert.True(await processor.ProcessAsync(app.Id, Ct));
            Assert.Equal(1, connector.Writes);
            var after = (await store.ApplicationAsync(app.Id, Ct))!;
            Assert.Empty(after.Assignments);
            Assert.Equal(app.Revision + 1, after.Revision);
            var operations = await store.OperationsAsync(app.Id, 20, Ct);
            Assert.Equal(OperationStatus.Applied, operations.Single(o => o.Id == revoke.Id).Status);
            Assert.Equal(OperationStatus.Queued, operations.Single(o => o.Id == grant.Id).Status);

            // The queued grant is now stale and must fail closed; it cannot
            // resurrect a right after emergency revocation.
            Assert.True(await processor.ProcessAsync(app.Id, Ct));
            Assert.Equal(1, connector.Writes);
            var final = await store.OperationsAsync(app.Id, 20, Ct);
            Assert.Equal("revision_conflict", final.Single(o => o.Id == grant.Id).ErrorCode);
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync(Ct);
        }
    }
}
