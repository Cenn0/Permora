using Npgsql;
using Permora.Core;
using Permora.Infrastructure;

namespace Permora.Tests;

/// <summary>
/// Actual PostgreSQL retention coverage: directory cleanup must never remove
/// an identity still needed for grants, delegated group membership, team
/// ownership or a pending access decision. No audit events are ever pruned.
/// </summary>
public sealed class PostgresPrivacyRetentionTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Stale_unreferenced_directory_identity_can_be_pruned_without_removing_authorization_subjects()
    {
        var connection = Environment.GetEnvironmentVariable("PERMORA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Skip("Requires isolated permora_test_* PostgreSQL.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        Assert.StartsWith("permora_test_", settings.Database ?? "");
        var schema = "test_" + Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;
        await using var admin = NpgsqlDataSource.Create(connection!);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}"))
            await create.ExecuteNonQueryAsync(ct);
        try
        {
            settings.SearchPath = schema;
            await using var source = NpgsqlDataSource.Create(settings.ConnectionString);
            var store = new PostgresStore(source);
            var fixture = new Fixture();
            var now = DateTimeOffset.UtcNow;
            var old = now.AddDays(-150);
            DirectoryPrincipal Principal(string name, DateTimeOffset stamp) =>
                fixture.Employee with
                {
                    Id = Guid.NewGuid(), KeycloakId = name,
                    SubjectKey = Wire.Subject(Fixture.Issuer, name),
                    Name = name, SyncedAt = stamp
                };

            var obsolete = Principal("obsolete-no-grants", old);
            var referenced = Principal("assigned-user", old);
            var pending = Principal("pending-approval", old);
            var member = Principal("assigned-group-member", old);
            var group = Principal("assigned-group", now) with
            {
                Kind = PrincipalKind.Group, MembershipComplete = true,
                EffectiveMemberKeys = [member.SubjectKey]
            };
            var recent = Principal("recent-unreferenced", now);
            var teamOwner = fixture.OwnerPrincipal with { SyncedAt = old };
            var otherRealm = Principal("other-realm", old) with { RealmKey = "other" };

            var app = fixture.App with
            {
                Assignments =
                [
                    fixture.Assignment(principal: referenced.Id),
                    fixture.Assignment(principal: group.Id)
                ]
            };
            await store.MigrateAsync(ct);
            await store.SaveTeamAsync(fixture.Team, null, fixture.Owner, ct);
            await store.CreateApplicationAsync(app, fixture.Owner, ct);
            await store.ReplaceDirectoryAsync("enterprise",
                [obsolete, referenced, pending, member, group, recent, teamOwner], ct);
            await store.ReplaceDirectoryAsync("other", [otherRealm], ct);

            await store.InApplicationAsync(app.Id, async tx =>
            {
                await tx.SaveOperationAsync(new Operation(
                    Guid.NewGuid(), app.Id, fixture.Owner,
                    new Change
                    {
                        Kind = ChangeKind.GrantAccess, PrincipalId = pending.Id,
                        RoleId = fixture.Reader.Id, ExpiresAt = now.AddDays(5),
                        Reason = "Pending human approval"
                    }, app.Revision, "privacy-pending-approval", "payload-hash",
                    OperationStatus.AwaitingApproval, now, now, 0), ct);
                return true;
            }, ct);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.PruneUnreferencedDirectoryAsync("enterprise", now.AddDays(-1), 100, ct));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.PruneUnreferencedDirectoryAsync("enterprise", now.AddDays(-90), 1001, ct));

            var removed = await store.PruneUnreferencedDirectoryAsync(
                "enterprise", now.AddDays(-90), 100, ct);
            Assert.Equal(1, removed);
            var survivors = await store.DirectoryAsync("enterprise", ct);
            Assert.DoesNotContain(survivors, p => p.Id == obsolete.Id);
            Assert.Contains(survivors, p => p.Id == referenced.Id);
            Assert.Contains(survivors, p => p.Id == pending.Id);
            Assert.Contains(survivors, p => p.Id == member.Id);
            Assert.Contains(survivors, p => p.Id == group.Id);
            Assert.Contains(survivors, p => p.Id == recent.Id);
            Assert.Contains(survivors, p => p.Id == teamOwner.Id);
            Assert.Single(await store.DirectoryAsync("other", ct));
            Assert.Equal(0, await store.PruneUnreferencedDirectoryAsync(
                "enterprise", now.AddDays(-90), 100, ct));
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync(ct);
        }
    }
}