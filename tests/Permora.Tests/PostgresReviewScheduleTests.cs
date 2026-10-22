using Npgsql;
using NpgsqlTypes;
using Permora.Infrastructure;

namespace Permora.Tests;

public sealed class PostgresReviewScheduleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Version_one_database_upgrades_without_losing_existing_review_or_application_data()
    {
        await using var db = await Database.OpenAsync(migrate: false);
        await using var stream = typeof(PostgresStore).Assembly.GetManifestResourceStream("Permora.Infrastructure.schema.sql")
            ?? throw new InvalidOperationException("Version-one schema missing.");
        using var reader = new StreamReader(stream);
        await using (var legacy = db.Source.CreateCommand(await reader.ReadToEndAsync(Ct)))
            await legacy.ExecuteNonQueryAsync(Ct);
        await using (var version = db.Source.CreateCommand("CREATE TABLE schema_versions(version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now()); INSERT INTO schema_versions(version) VALUES (1)"))
            await version.ExecuteNonQueryAsync(Ct);
        var f = db.Fixture;
        var assignment = f.Assignment();
        await db.SeedAsync(f.App with { Assignments = [assignment] });
        var reviewId = Guid.NewGuid();
        var dueAt = f.Clock.Now.AddDays(2);
        var legacyReview = Wire.Serialize(new { id = reviewId, applicationId = f.App.Id,
            name = "Existing campaign", dueAt, items = new[] { new ReviewItem(assignment.Id, "Pending") }, closed = false });
        await using (var insert = db.Source.CreateCommand("INSERT INTO access_reviews(id, application_id, closed, due_at, document) VALUES (@id, @app, false, @due, @document)"))
        {
            insert.Parameters.AddWithValue("id", reviewId);
            insert.Parameters.AddWithValue("app", f.App.Id);
            insert.Parameters.AddWithValue("due", dueAt);
            insert.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, legacyReview);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        var before = Wire.Serialize(await db.Store.ApplicationAsync(f.App.Id, Ct));
        var auditBefore = await db.Store.AuditAsync(f.App.Id, 0, 100, Ct);

        await db.Store.MigrateAsync(Ct);
        await db.Store.MigrateAsync(Ct);
        await using (var versions = db.Source.CreateCommand("SELECT array_agg(version ORDER BY version) FROM schema_versions"))
        {
            var applied = Assert.IsType<int[]>(await versions.ExecuteScalarAsync(Ct));
            Assert.Equal([1, 2], applied);
        }
        Assert.Equal(before, Wire.Serialize(await db.Store.ApplicationAsync(f.App.Id, Ct)));
        Assert.Equal(auditBefore, await db.Store.AuditAsync(f.App.Id, 0, 100, Ct));
        var migratedReview = Assert.Single(await db.Store.ReviewsAsync(f.App.Id, Ct));
        Assert.Equal(reviewId, migratedReview.Id);
        Assert.Equal(dueAt, migratedReview.DueAt);
        Assert.Null(migratedReview.ScheduledFor);
        Assert.Null(await db.Store.ReviewScheduleAsync(f.App.Id, Ct));

        var configured = await new ChangeService(db.Store, f.Admins, f.Clock).ConfigureReviewScheduleAsync(
            f.App.Id, f.Owner, new("After upgrade", 30, 7, f.Clock.Now.AddDays(1), true, 700), 0, Ct);
        Assert.Equal(1, configured.Revision);
        Assert.Equal(configured, await db.Store.ReviewScheduleAsync(f.App.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.InApplicationAsync<bool>(f.App.Id, async tx =>
        {
            await tx.SaveReviewScheduleAsync(configured with { Enabled = false, Revision = configured.Revision + 1 }, Ct);
            await tx.AppendAuditAsync(f.Owner, "schedule.must.rollback", "Atomicity regression", f.Clock.Now, Ct);
            throw new InvalidOperationException("Force transaction rollback.");
        }, Ct));
        Assert.Equal(configured, await db.Store.ReviewScheduleAsync(f.App.Id, Ct));
        Assert.DoesNotContain(await db.Store.AuditAsync(f.App.Id, 0, 100, Ct), a => a.Action == "schedule.must.rollback");
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Due_selector_finds_schedule_only_work_and_excludes_disabled_future_and_unconfigured_apps()
    {
        await using var db = await Database.OpenAsync();
        var f = db.Fixture;
        await db.SeedAsync(f.App);
        var future = f.App with { Id = Guid.NewGuid(), Slug = "future-review" };
        var disabled = f.App with { Id = Guid.NewGuid(), Slug = "disabled-review" };
        var unconfigured = f.App with { Id = Guid.NewGuid(), Slug = "no-review-plan" };
        await db.Store.CreateApplicationAsync(future, f.Owner, Ct);
        await db.Store.CreateApplicationAsync(disabled, f.Owner, Ct);
        await db.Store.CreateApplicationAsync(unconfigured, f.Owner, Ct);
        var service = new ChangeService(db.Store, f.Admins, f.Clock);
        var template = new AccessReviewSchedule("Weekly access review", 7, 1, f.Clock.Now.AddHours(1), true, 0);
        await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner, template, 0, Ct);
        await service.ConfigureReviewScheduleAsync(future.Id, f.Owner, template with { NextStartAt = f.Clock.Now.AddDays(5) }, 0, Ct);
        await service.ConfigureReviewScheduleAsync(disabled.Id, f.Owner,
            template with { Enabled = false, NextStartAt = f.Clock.Now.AddDays(-5) }, 0, Ct);
        f.Clock.Now = f.Clock.Now.AddHours(2);

        Assert.Equal(f.App.Id, Assert.Single(await db.Store.DueApplicationsAsync(f.Clock.Now, 100, Guid.Empty, Ct)));
        await new OperationProcessor(db.Store, new FakeConnector(), f.Admins, f.Clock).SweepAsync(f.App.Id, Ct);
        Assert.Empty(await db.Store.DueApplicationsAsync(f.Clock.Now, 100, Guid.Empty, Ct));
        Assert.Empty(await db.Store.ReviewsAsync(future.Id, Ct));
        Assert.Empty(await db.Store.ReviewsAsync(disabled.Id, Ct));
        Assert.True(Assert.Single(await db.Store.ReviewsAsync(f.App.Id, Ct)).Closed);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Two_independent_workers_create_one_campaign_and_one_deadline_revocation()
    {
        await using var db = await Database.OpenAsync();
        var f = db.Fixture;
        var assignment = f.Assignment() with { ExpiresAt = null };
        await db.SeedAsync(f.App with { Assignments = [assignment] });
        var schedule = await new ChangeService(db.Store, f.Admins, f.Clock).ConfigureReviewScheduleAsync(
            f.App.Id, f.Owner, new("Parallel worker review", 7, 1, f.Clock.Now.AddHours(1), true, 0), 0, Ct);
        await using var otherSource = NpgsqlDataSource.Create(db.ConnectionString);
        var otherStore = new PostgresStore(otherSource);
        var first = new OperationProcessor(db.Store, new FakeConnector(), f.Admins, f.Clock);
        var second = new OperationProcessor(otherStore, new FakeConnector(), f.Admins, f.Clock);
        f.Clock.Now = schedule.NextStartAt;
        await Task.WhenAll(first.SweepAsync(f.App.Id, Ct), second.SweepAsync(f.App.Id, Ct));

        var review = Assert.Single(await db.Store.ReviewsAsync(f.App.Id, Ct));
        Assert.Equal(assignment.Id, Assert.Single(review.Items).AssignmentId);
        Assert.Equal(schedule.NextStartAt, review.ScheduledFor);
        Assert.Equal(2, (await otherStore.ReviewScheduleAsync(f.App.Id, Ct))!.Revision);
        Assert.Equal(f.App.Revision, (await db.Store.ApplicationAsync(f.App.Id, Ct))!.Revision);
        Assert.Single(await db.Store.AuditAsync(f.App.Id, 0, 100, Ct), a => a.Action == "review.scheduled");

        f.Clock.Now = review.DueAt;
        await Task.WhenAll(first.SweepAsync(f.App.Id, Ct), second.SweepAsync(f.App.Id, Ct));
        var closed = Assert.Single(await db.Store.ReviewsAsync(f.App.Id, Ct));
        Assert.True(closed.Closed);
        Assert.Equal("RevocationQueued", Assert.Single(closed.Items).Decision);
        var revoke = Assert.Single(await db.Store.OperationsAsync(f.App.Id, 100, Ct));
        Assert.True(revoke.SystemRevocation);
        Assert.Equal(assignment.Id, revoke.Change.TargetId);
        var audit = await db.Store.AuditAsync(f.App.Id, 0, 100, Ct);
        Assert.Single(audit, a => a.Action == "review.overdue");
        Assert.True(AuditChain.Verify(audit));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Independent_configuration_clients_cannot_lose_each_others_update()
    {
        await using var db = await Database.OpenAsync();
        var f = db.Fixture;
        await db.SeedAsync(f.App);
        var first = new ChangeService(db.Store, f.Admins, f.Clock);
        var schedule = await first.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            new("Initial schedule", 30, 7, f.Clock.Now.AddDays(1), true, 0), 0, Ct);
        await using var otherSource = NpgsqlDataSource.Create(db.ConnectionString);
        var second = new ChangeService(new PostgresStore(otherSource), f.Admins, f.Clock);
        var attempts = await Task.WhenAll(
            Record.ExceptionAsync(() => first.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
                schedule with { Name = "First writer" }, schedule.Revision, Ct)).AsTask(),
            Record.ExceptionAsync(() => second.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
                schedule with { Name = "Second writer" }, schedule.Revision, Ct)).AsTask());
        Assert.Single(attempts, error => error is null);
        Assert.Equal(412, Assert.IsType<DomainException>(Assert.Single(attempts, error => error is not null)).Status);
        var persisted = (await db.Store.ReviewScheduleAsync(f.App.Id, Ct))!;
        Assert.Equal(2, persisted.Revision);
        Assert.Contains(persisted.Name, new[] { "First writer", "Second writer" });
        Assert.Equal(f.App.Revision, (await db.Store.ApplicationAsync(f.App.Id, Ct))!.Revision);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Starting_a_periodic_campaign_does_not_invalidate_an_approved_pending_grant()
    {
        await using var db = await Database.OpenAsync();
        var f = db.Fixture;
        await db.SeedAsync(f.App);
        await db.Store.ReplaceDirectoryAsync(f.App.RealmKey, f.Directory, Ct);
        var service = new ChangeService(db.Store, f.Admins, f.Clock);
        var schedule = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            new("Independent cadence", 7, 1, f.Clock.Now.AddMinutes(1), true, 0), 0, Ct);
        var grant = await service.RequestAsync(f.App.Id, f.Owner, f.Grant(), f.App.Revision, "scheduled-review-approved-grant", Ct);
        await service.DecideAsync(f.App.Id, grant.Id, f.Approver, true, "Approved independently", Ct);
        f.Clock.Now = schedule.NextStartAt;
        var connector = new FakeConnector();
        var processor = new OperationProcessor(db.Store, connector, f.Admins, f.Clock);
        await processor.SweepAsync(f.App.Id, Ct);
        Assert.Equal(f.App.Revision, (await db.Store.ApplicationAsync(f.App.Id, Ct))!.Revision);
        Assert.True(await processor.ProcessAsync(f.App.Id, Ct));
        Assert.Equal(OperationStatus.Applied, Assert.Single(await db.Store.OperationsAsync(f.App.Id, 100, Ct)).Status);
        Assert.Single((await db.Store.ApplicationAsync(f.App.Id, Ct))!.Assignments);
        Assert.Equal(1, connector.Writes);
        Assert.Equal(2, (await db.Store.ReviewScheduleAsync(f.App.Id, Ct))!.Revision);
    }

    [Theory]
    [Trait("Category", "PostgresIntegration")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Persistence_refuses_to_replace_a_schedule_with_an_equal_or_older_revision(long staleRevision)
    {
        await using var db = await Database.OpenAsync();
        var f = db.Fixture;
        await db.SeedAsync(f.App);
        var service = new ChangeService(db.Store, f.Admins, f.Clock);
        var initial = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            new("Initial configuration", 30, 7, f.Clock.Now.AddDays(1), true, 0), 0, Ct);
        var current = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            initial with { Name = "Current configuration" }, initial.Revision, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.InApplicationAsync(f.App.Id, async tx =>
        {
            await tx.SaveReviewScheduleAsync(current with { Name = "Stale overwrite", Revision = staleRevision }, Ct);
            return true;
        }, Ct));
        Assert.Equal(current, await db.Store.ReviewScheduleAsync(f.App.Id, Ct));
        Assert.Equal(f.App.Revision, (await db.Store.ApplicationAsync(f.App.Id, Ct))!.Revision);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task An_expired_grant_remains_due_after_an_earlier_system_revocation_was_rejected()
    {
        await using var db = await Database.OpenAsync();
        var f = db.Fixture;
        var assignment = f.Assignment() with { ExpiresAt = f.Clock.Now };
        await db.SeedAsync(f.App with { Assignments = [assignment] });
        await db.Store.InApplicationAsync(f.App.Id, async tx =>
        {
            await ChangeService.QueueRevocationAsync(tx, assignment.Id, "Previous unsuccessful revocation", f.Clock.Now.AddHours(-1), Ct);
            var previous = (await tx.IdempotentAsync("system", "revoke:" + assignment.Id, Ct))!;
            await tx.SaveOperationAsync(previous with { Status = OperationStatus.Rejected,
                ErrorCode = "reconciled_not_applied", Attempts = 6 }, Ct);
            return true;
        }, Ct);
        var original = Assert.Single(await db.Store.OperationsAsync(f.App.Id, 100, Ct));
        Assert.Equal(OperationStatus.Rejected, original.Status);
        Assert.Empty(await db.Store.ReviewsAsync(f.App.Id, Ct));
        Assert.Null(await db.Store.ReviewScheduleAsync(f.App.Id, Ct));

        Assert.Equal(f.App.Id, Assert.Single(await db.Store.DueApplicationsAsync(f.Clock.Now, 100, Guid.Empty, Ct)));
        var processor = new OperationProcessor(db.Store, new FakeConnector(), f.Admins, f.Clock);
        await processor.SweepAsync(f.App.Id, Ct);
        var requeued = Assert.Single(await db.Store.OperationsAsync(f.App.Id, 100, Ct));
        Assert.Equal(original.Id, requeued.Id);
        Assert.Equal(OperationStatus.Queued, requeued.Status);
        Assert.Equal(0, requeued.Attempts);
        Assert.Equal(f.Clock.Now, requeued.NextAttemptAt);
        Assert.Null(requeued.ErrorCode);
        Assert.Single(await db.Store.AuditAsync(f.App.Id, 0, 100, Ct), a => a.Action == "revocation.requeued");
        await processor.ProcessAsync(f.App.Id, Ct);
        Assert.Empty((await db.Store.ApplicationAsync(f.App.Id, Ct))!.Assignments);
        Assert.Empty(await db.Store.DueApplicationsAsync(f.Clock.Now, 100, Guid.Empty, Ct));
    }

    private sealed class Database(NpgsqlDataSource admin, NpgsqlDataSource source,
        string schema, string connectionString) : IAsyncDisposable
    {
        public NpgsqlDataSource Source { get; } = source;
        public PostgresStore Store { get; } = new(source);
        public Fixture Fixture { get; } = new();
        public string ConnectionString { get; } = connectionString;

        public static async Task<Database> OpenAsync(bool migrate = true)
        {
            var value = Environment.GetEnvironmentVariable("PERMORA_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(value))
                Assert.Skip("Set PERMORA_TEST_POSTGRES to an isolated permora_test_* PostgreSQL database.");
            var options = new NpgsqlConnectionStringBuilder(value);
            Assert.StartsWith("permora_test_", options.Database ?? "");
            var schema = "test_" + Guid.NewGuid().ToString("N");
            var admin = NpgsqlDataSource.Create(value!);
            await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}"))
                await create.ExecuteNonQueryAsync(Ct);
            options.SearchPath = schema;
            var source = NpgsqlDataSource.Create(options.ConnectionString);
            var db = new Database(admin, source, schema, options.ConnectionString);
            try
            {
                if (migrate) await db.Store.MigrateAsync(Ct);
                return db;
            }
            catch
            {
                await db.DisposeAsync();
                throw;
            }
        }

        public async Task SeedAsync(ApplicationDocument application)
        {
            await Store.SaveTeamAsync(Fixture.Team, null, Fixture.Owner, Ct);
            await Store.CreateApplicationAsync(application, Fixture.Owner, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            try
            {
                await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally { await admin.DisposeAsync(); }
        }
    }
}
