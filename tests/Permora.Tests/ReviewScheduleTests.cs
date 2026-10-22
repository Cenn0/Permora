using System.Text.Json;

namespace Permora.Tests;

public sealed class ReviewScheduleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessReviewSchedule Schedule(Fixture f) =>
        new("Quarterly access review", 90, 14, f.Clock.Now.AddDays(1), true, 999);

    [Fact]
    public async Task Configuring_a_schedule_uses_its_own_revision_and_preserves_application_revision()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var service = new ChangeService(store, f.Admins, f.Clock);
        Assert.Null(await store.ReviewScheduleAsync(f.App.Id, Ct));

        var created = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner, Schedule(f), 0, Ct);
        Assert.Equal(1, created.Revision);
        Assert.Equal(f.App.Revision, store.App.Revision);
        Assert.Equal(created, await store.ReviewScheduleAsync(f.App.Id, Ct));

        var updated = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            created with { IntervalDays = 30, Revision = long.MaxValue }, created.Revision, Ct);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(30, updated.IntervalDays);
        Assert.Equal(f.App.Revision, store.App.Revision);
        Assert.Equal(2, store.Audit.Count(a => a.Action == "review.schedule_updated"));
        Assert.True(AuditChain.Verify(store.Audit));
    }

    [Theory]
    [InlineData(ManagementRole.Maintainer)]
    [InlineData(ManagementRole.AccessManager)]
    [InlineData(ManagementRole.Approver)]
    [InlineData(ManagementRole.Auditor)]
    public async Task Only_owners_can_configure_periodic_reviews(ManagementRole role)
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var actor = new Actor(f.Team.Members.Single(m => m.Role == role).SubjectKey, "Reviewer");
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new ChangeService(store, f.Admins, f.Clock).ConfigureReviewScheduleAsync(
                f.App.Id, actor, Schedule(f), 0, Ct));
        Assert.Equal(403, error.Status);
        Assert.Null(store.ReviewSchedule);
        Assert.Empty(store.Audit);
    }

    [Fact]
    public async Task Foreign_team_is_denied_before_schedule_validation_or_revision_disclosure()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new ChangeService(store, f.Admins, f.Clock).ConfigureReviewScheduleAsync(f.App.Id,
                f.Outsider, new("x", 0, 999, DateTimeOffset.MinValue, true, -1), -1, Ct));
        Assert.Equal(404, error.Status);
        Assert.Equal("not_found", error.Code);
        Assert.Null(store.ReviewSchedule);
        Assert.Empty(store.Audit);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(366, 1)]
    [InlineData(30, 0)]
    [InlineData(100, 91)]
    [InlineData(7, 8)]
    public async Task Invalid_intervals_and_review_windows_are_rejected_atomically(int interval, int window)
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new ChangeService(store, f.Admins, f.Clock).ConfigureReviewScheduleAsync(f.App.Id,
                f.Owner, Schedule(f) with { IntervalDays = interval, ReviewWindowDays = window }, 0, Ct));
        Assert.Equal(400, error.Status);
        Assert.Null(store.ReviewSchedule);
        Assert.Empty(store.Audit);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(121)]
    public async Task Invalid_schedule_names_are_rejected(int length)
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new ChangeService(store, f.Admins, f.Clock).ConfigureReviewScheduleAsync(f.App.Id,
                f.Owner, Schedule(f) with { Name = new string('x', length) }, 0, Ct));
        Assert.Equal(400, error.Status);
        Assert.Null(store.ReviewSchedule);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task Enabled_schedule_requires_a_future_start_within_one_year(int days)
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new ChangeService(store, f.Admins, f.Clock).ConfigureReviewScheduleAsync(f.App.Id,
                f.Owner, Schedule(f) with { NextStartAt = f.Clock.Now.AddDays(days) }, 0, Ct));
        Assert.Equal(400, error.Status);
        Assert.Null(store.ReviewSchedule);
    }

    [Fact]
    public async Task Concurrent_configuration_updates_cannot_overwrite_a_newer_revision()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var service = new ChangeService(store, f.Admins, f.Clock);
        var schedule = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner, Schedule(f), 0, Ct);
        var results = await Task.WhenAll(new[] { "First update", "Second update" }.Select(name =>
            Record.ExceptionAsync(() => service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
                schedule with { Name = name }, schedule.Revision, Ct)).AsTask()));
        Assert.Single(results, e => e is null);
        Assert.Equal(412, Assert.IsType<DomainException>(Assert.Single(results, e => e is not null)).Status);
        Assert.Equal(2, store.ReviewSchedule!.Revision);
        Assert.Equal(f.App.Revision, store.App.Revision);
    }

    [Fact]
    public async Task Repeated_sweeps_create_one_campaign_and_advance_only_the_schedule_revision()
    {
        var f = new Fixture();
        var assignment = f.Assignment() with { ExpiresAt = null };
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var schedule = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner, Schedule(f), 0, Ct);
        f.Clock.Now = schedule.NextStartAt;
        var processor = new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock);
        await processor.SweepAsync(f.App.Id, Ct);
        await processor.SweepAsync(f.App.Id, Ct);

        var review = Assert.Single(store.Reviews.Values);
        Assert.Equal(schedule.NextStartAt, review.ScheduledFor);
        Assert.Equal(schedule.NextStartAt.AddDays(schedule.ReviewWindowDays), review.DueAt);
        Assert.Equal(assignment.Id, Assert.Single(review.Items).AssignmentId);
        Assert.False(review.Closed);
        Assert.Equal(schedule.NextStartAt.AddDays(90), store.ReviewSchedule!.NextStartAt);
        Assert.Equal(2, store.ReviewSchedule.Revision);
        Assert.Equal(f.App.Revision, store.App.Revision);
        Assert.Single(store.Audit, a => a.Action == "review.scheduled");
        Assert.Empty(store.Operations);
    }

    [Fact]
    public async Task Long_downtime_materializes_at_most_one_overdue_and_one_current_cycle_with_distinct_snapshots()
    {
        var f = new Fixture();
        var firstStart = f.Clock.Now.AddDays(1);
        var original = f.Assignment() with { ExpiresAt = null };
        var intermediate = f.Assignment() with { GrantedAt = firstStart.AddDays(50), ExpiresAt = null };
        var current = f.Assignment() with { GrantedAt = firstStart.AddDays(97), ExpiresAt = null };
        var later = f.Assignment() with { GrantedAt = firstStart.AddDays(98).AddHours(1), ExpiresAt = null };
        var store = new MemoryStore(f) { App = f.App with { Assignments = [original, intermediate, current, later] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            new("Weekly certification", 7, 2, firstStart, true, 0), 0, Ct);
        f.Clock.Now = firstStart.AddDays(99);
        var processor = new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock);
        await processor.SweepAsync(f.App.Id, Ct);
        await processor.SweepAsync(f.App.Id, Ct);

        Assert.Equal(2, store.Reviews.Count);
        var overdue = Assert.Single(store.Reviews.Values, review => review.Closed);
        Assert.Equal(firstStart.AddDays(91), overdue.ScheduledFor);
        Assert.Equal(firstStart.AddDays(93), overdue.DueAt);
        Assert.Equal(2, overdue.Items.Count);
        Assert.All(overdue.Items, item => Assert.Equal("RevocationQueued", item.Decision));
        Assert.Contains(overdue.Items, item => item.AssignmentId == original.Id);
        Assert.Contains(overdue.Items, item => item.AssignmentId == intermediate.Id);
        var open = Assert.Single(store.Reviews.Values, review => !review.Closed);
        Assert.Equal(firstStart.AddDays(98), open.ScheduledFor);
        Assert.Equal(firstStart.AddDays(100), open.DueAt);
        Assert.Equal(current.Id, Assert.Single(open.Items).AssignmentId);
        Assert.Equal("Pending", open.Items[0].Decision);
        Assert.DoesNotContain(store.Reviews.Values.SelectMany(review => review.Items), item => item.AssignmentId == later.Id);
        Assert.Equal(2, store.Operations.Count);
        Assert.All(store.Operations.Values, op => Assert.Contains(op.Change.TargetId, new[] { original.Id, intermediate.Id }));
        Assert.Equal(firstStart.AddDays(105), store.ReviewSchedule!.NextStartAt);
        Assert.Equal(2, store.ReviewSchedule.Revision);
        Assert.Equal(f.App.Revision, store.App.Revision);
        Assert.Single(store.Audit, a => a.Action == "review.overdue");
        var scheduled = store.Audit.Where(a => a.Action == "review.scheduled").ToArray();
        Assert.Equal(2, scheduled.Length);
        foreach (var audit in scheduled)
        {
            using var detail = JsonDocument.Parse(audit.Detail);
            Assert.Equal(firstStart, detail.RootElement.GetProperty("earliestMissedStart").GetDateTimeOffset());
            Assert.Equal(13, detail.RootElement.GetProperty("skippedCycles").GetInt64());
        }
        Assert.True(AuditChain.Verify(store.Audit));
    }

    [Fact]
    public async Task Catchup_between_review_windows_revokes_grants_from_later_missed_cycles_without_a_retroactive_deadline()
    {
        var f = new Fixture();
        var firstStart = f.Clock.Now.AddDays(1);
        var original = f.Assignment() with { ExpiresAt = null };
        var intermediate = f.Assignment() with { GrantedAt = firstStart.AddDays(1), ExpiresAt = null };
        var later = f.Assignment() with { GrantedAt = firstStart.AddDays(14).AddHours(1), ExpiresAt = null };
        var store = new MemoryStore(f) { App = f.App with { Assignments = [original, intermediate, later] } };
        await new ChangeService(store, f.Admins, f.Clock).ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            new("Weekly review after outage", 7, 1, firstStart, true, 0), 0, Ct);
        f.Clock.Now = firstStart.AddDays(16);
        var processor = new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock);
        await processor.SweepAsync(f.App.Id, Ct);

        var overdue = Assert.Single(store.Reviews.Values);
        Assert.Equal(firstStart.AddDays(14), overdue.ScheduledFor);
        Assert.Equal(firstStart.AddDays(15), overdue.DueAt);
        Assert.True(overdue.Closed);
        Assert.Equal(2, overdue.Items.Count);
        Assert.Contains(overdue.Items, item => item.AssignmentId == intermediate.Id);
        Assert.DoesNotContain(overdue.Items, item => item.AssignmentId == later.Id);
        Assert.Equal(2, store.Operations.Count);
        Assert.Equal(firstStart.AddDays(21), store.ReviewSchedule!.NextStartAt);
        using var detail = JsonDocument.Parse(Assert.Single(store.Audit, a => a.Action == "review.scheduled").Detail);
        Assert.Equal(firstStart, detail.RootElement.GetProperty("earliestMissedStart").GetDateTimeOffset());
        Assert.Equal(2, detail.RootElement.GetProperty("skippedCycles").GetInt64());
        await processor.ProcessAsync(f.App.Id, Ct);
        await processor.ProcessAsync(f.App.Id, Ct);
        Assert.Equal(later.Id, Assert.Single(store.App.Assignments).Id);
    }

    [Fact]
    public async Task Empty_scheduled_and_manual_campaigns_are_immediately_closed()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var service = new ChangeService(store, f.Admins, f.Clock);
        var manual = await service.StartReviewAsync(f.App.Id, f.Owner, "No assignments", f.Clock.Now.AddDays(2), Ct);
        Assert.True(manual.Closed);
        Assert.Empty(manual.Items);
        Assert.Null(manual.ScheduledFor);
        var schedule = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner, Schedule(f), 0, Ct);
        f.Clock.Now = schedule.NextStartAt;
        await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock).SweepAsync(f.App.Id, Ct);
        var scheduled = Assert.Single(store.Reviews.Values, r => r.ScheduledFor is not null);
        Assert.True(scheduled.Closed);
        Assert.Empty(scheduled.Items);
        Assert.Empty(store.Operations);
    }

    [Fact]
    public async Task Disabling_a_schedule_accepts_its_past_start_but_does_not_change_existing_deadlines()
    {
        var f = new Fixture();
        var assignment = f.Assignment() with { ExpiresAt = null };
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var schedule = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner, Schedule(f), 0, Ct);
        var review = await service.StartReviewAsync(f.App.Id, f.Owner, "Existing campaign", f.Clock.Now.AddHours(2), Ct);
        await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            schedule with { Enabled = false, NextStartAt = f.Clock.Now.AddDays(-1) }, schedule.Revision, Ct);
        f.Clock.Now = review.DueAt.AddSeconds(1);
        await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock).SweepAsync(f.App.Id, Ct);

        var closed = Assert.Single(store.Reviews.Values);
        Assert.Equal(review.DueAt, closed.DueAt);
        Assert.True(closed.Closed);
        Assert.Equal("RevocationQueued", Assert.Single(closed.Items).Decision);
        Assert.Single(store.Operations);
        Assert.False(store.ReviewSchedule!.Enabled);
        Assert.Equal(2, store.ReviewSchedule.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Due_schedule_cannot_be_disabled_or_postponed_until_the_worker_materializes_it(bool disable)
    {
        var f = new Fixture();
        var store = new MemoryStore(f) { App = f.App with { Assignments = [f.Assignment() with { ExpiresAt = null }] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var schedule = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            Schedule(f) with { NextStartAt = f.Clock.Now.AddMinutes(1) }, 0, Ct);
        f.Clock.Now = schedule.NextStartAt;
        var auditBefore = store.Audit.ToArray();
        var requested = schedule with { Name = "Changed cadence", Enabled = !disable,
            NextStartAt = disable ? schedule.NextStartAt : f.Clock.Now.AddDays(2) };

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner, requested, schedule.Revision, Ct));
        Assert.Equal(409, error.Status);
        Assert.Equal("review_schedule_due", error.Code);
        Assert.Equal(schedule, store.ReviewSchedule);
        Assert.Equal(auditBefore, store.Audit);
        Assert.Empty(store.Reviews);
        Assert.Empty(store.Operations);

        await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock).SweepAsync(f.App.Id, Ct);
        var materialized = Assert.Single(store.Reviews.Values);
        var advanced = store.ReviewSchedule!;
        Assert.Equal(schedule.NextStartAt, materialized.ScheduledFor);
        Assert.Equal(schedule.NextStartAt.AddDays(schedule.ReviewWindowDays), materialized.DueAt);
        var updated = await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            requested, advanced.Revision, Ct);
        Assert.Equal(advanced.Revision + 1, updated.Revision);
        Assert.Equal(!disable, updated.Enabled);
        Assert.Equal(requested.NextStartAt, updated.NextStartAt);
        Assert.Equal(materialized, Assert.Single(store.Reviews.Values));
        Assert.Equal(f.App.Revision, store.App.Revision);
        Assert.Equal(2, store.Audit.Count(a => a.Action == "review.schedule_updated"));
    }

    [Fact]
    public async Task Overdue_missing_assignment_is_reported_as_already_revoked_without_an_operation()
    {
        var f = new Fixture();
        var store = new MemoryStore(f) { App = f.App with { Assignments = [f.Assignment()] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var review = await service.StartReviewAsync(f.App.Id, f.Owner, "Remove old access", f.Clock.Now.AddHours(1), Ct);
        store.App = store.App with { Assignments = [], Revision = store.App.Revision + 1 };
        f.Clock.Now = review.DueAt;
        var processor = new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock);
        await processor.SweepAsync(f.App.Id, Ct);
        await processor.SweepAsync(f.App.Id, Ct);
        Assert.True(store.Reviews[review.Id].Closed);
        Assert.Equal("AlreadyRevoked", Assert.Single(store.Reviews[review.Id].Items).Decision);
        Assert.Empty(store.Operations);
        Assert.Single(store.Audit, a => a.Action == "review.overdue");
    }

    [Fact]
    public async Task Expired_access_cannot_be_certified_before_the_worker_has_revoked_it()
    {
        var f = new Fixture();
        var assignment = f.Assignment() with { ExpiresAt = f.Clock.Now };
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var review = await service.StartReviewAsync(f.App.Id, f.Owner, "Expired access", f.Clock.Now.AddHours(1), Ct);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.ReviewAsync(f.App.Id, review.Id, assignment.Id, f.Owner, true, Ct));
        Assert.Equal("assignment_expired", error.Code);
        Assert.Equal("Pending", Assert.Single(store.Reviews[review.Id].Items).Decision);
        Assert.Empty(store.Operations);
    }

    [Theory]
    [InlineData(OperationStatus.Queued, true)]
    [InlineData(OperationStatus.Failed, true)]
    [InlineData(OperationStatus.Conflict, true)]
    [InlineData(OperationStatus.Applied, true)]
    [InlineData(OperationStatus.Queued, false)]
    [InlineData(OperationStatus.Failed, false)]
    [InlineData(OperationStatus.Conflict, false)]
    [InlineData(OperationStatus.Applied, false)]
    public async Task Existing_revocation_cannot_be_overridden_by_retaining_another_review(OperationStatus status, bool system)
    {
        var f = new Fixture();
        var assignment = f.Assignment();
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var review = await service.StartReviewAsync(f.App.Id, f.Owner, "Concurrent review", f.Clock.Now.AddHours(1), Ct);
        await store.InApplicationAsync(f.App.Id, async tx =>
        {
            await ChangeService.QueueRevocationAsync(tx, assignment.Id, "Earlier review rejected", f.Clock.Now, Ct);
            return true;
        }, Ct);
        var operation = Assert.Single(store.Operations.Values);
        store.Operations[operation.Id] = operation with { Status = status, SystemRevocation = system,
            RequestedBy = system ? operation.RequestedBy : f.Owner,
            IdempotencyKey = system ? operation.IdempotencyKey : "manual-revocation-request" };
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.ReviewAsync(f.App.Id, review.Id, assignment.Id, f.Owner, true, Ct));
        Assert.Equal("revocation_pending", error.Code);
        Assert.Equal("Pending", Assert.Single(store.Reviews[review.Id].Items).Decision);
        Assert.Single(store.Operations);
    }

    [Fact]
    public async Task Rejected_revocation_does_not_block_an_independent_valid_certification()
    {
        var f = new Fixture();
        var assignment = f.Assignment();
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var review = await service.StartReviewAsync(f.App.Id, f.Owner, "Recheck current access", f.Clock.Now.AddHours(1), Ct);
        var rejected = new Operation(Guid.NewGuid(), f.App.Id, f.Owner,
            new Change { Kind = ChangeKind.RevokeAccess, TargetId = assignment.Id, Reason = "Rejected proposal" },
            store.App.Revision, "rejected-manual-revoke", "test-hash", OperationStatus.Rejected,
            f.Clock.Now, f.Clock.Now, 0);
        store.Operations[rejected.Id] = rejected;
        var certified = await service.ReviewAsync(f.App.Id, review.Id, assignment.Id, f.Approver, true, Ct);
        Assert.True(certified.Closed);
        Assert.Equal("Retained", Assert.Single(certified.Items).Decision);
        Assert.Single(store.Operations);
    }

    [Fact]
    public async Task Another_overdue_campaign_blocks_certification_even_before_its_revocation_is_queued()
    {
        var f = new Fixture();
        var assignment = f.Assignment() with { ExpiresAt = null };
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var overdue = await service.StartReviewAsync(f.App.Id, f.Owner, "Earlier deadline", f.Clock.Now.AddHours(1), Ct);
        var current = await service.StartReviewAsync(f.App.Id, f.Owner, "Later deadline", f.Clock.Now.AddHours(3), Ct);
        f.Clock.Now = overdue.DueAt;
        store.Directory = f.Directory.Select(p => p with { SyncedAt = f.Clock.Now }).ToArray();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.ReviewAsync(f.App.Id, current.Id, assignment.Id, f.Owner, true, Ct));
        Assert.Equal("revocation_pending", error.Code);
        Assert.Equal("Pending", Assert.Single(store.Reviews[current.Id].Items).Decision);
        Assert.Equal("Pending", Assert.Single(store.Reviews[overdue.Id].Items).Decision);
        Assert.Empty(store.Operations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missed_unmaterialized_schedule_blocks_only_assignments_existing_at_its_scheduled_start(bool grantedAfterStart)
    {
        var f = new Fixture();
        var start = f.Clock.Now.AddHours(1);
        var assignment = f.Assignment() with { ExpiresAt = null,
            GrantedAt = grantedAfterStart ? start.AddHours(1) : f.Clock.Now };
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            new("Missed weekly review", 7, 1, start, true, 0), 0, Ct);
        var current = await service.StartReviewAsync(f.App.Id, f.Owner, "Current manual review", f.Clock.Now.AddDays(3), Ct);
        f.Clock.Now = start.AddDays(1);
        store.Directory = f.Directory.Select(p => p with { SyncedAt = f.Clock.Now }).ToArray();

        if (grantedAfterStart)
        {
            var result = await service.ReviewAsync(f.App.Id, current.Id, assignment.Id, f.Owner, true, Ct);
            Assert.Equal("Retained", Assert.Single(result.Items).Decision);
            Assert.True(result.Closed);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                service.ReviewAsync(f.App.Id, current.Id, assignment.Id, f.Owner, true, Ct));
            Assert.Equal("revocation_pending", error.Code);
            Assert.Equal("Pending", Assert.Single(store.Reviews[current.Id].Items).Decision);
        }
        Assert.Single(store.Reviews);
        Assert.Empty(store.Operations);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(15, true)]
    public async Task Certification_before_catchup_respects_the_latest_missed_deadline_including_intermediate_grants(int grantDay, bool canRetain)
    {
        var f = new Fixture();
        var firstStart = f.Clock.Now.AddDays(1);
        var assignment = f.Assignment() with { GrantedAt = firstStart.AddDays(grantDay), ExpiresAt = null };
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        await service.ConfigureReviewScheduleAsync(f.App.Id, f.Owner,
            new("Pending missed cycles", 7, 1, firstStart, true, 0), 0, Ct);
        var manual = await service.StartReviewAsync(f.App.Id, f.Owner, "Independent campaign", firstStart.AddDays(20), Ct);
        f.Clock.Now = firstStart.AddDays(16);
        store.Directory = f.Directory.Select(p => p with { SyncedAt = f.Clock.Now }).ToArray();

        if (canRetain)
        {
            var retained = await service.ReviewAsync(f.App.Id, manual.Id, assignment.Id, f.Owner, true, Ct);
            Assert.Equal("Retained", Assert.Single(retained.Items).Decision);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                service.ReviewAsync(f.App.Id, manual.Id, assignment.Id, f.Owner, true, Ct));
            Assert.Equal("revocation_pending", error.Code);
            Assert.Equal("Pending", Assert.Single(store.Reviews[manual.Id].Items).Decision);
        }
        Assert.Single(store.Reviews);
        Assert.Empty(store.Operations);
    }

    [Fact]
    public async Task New_review_deadline_requeues_an_earlier_revocation_abandoned_after_reconciliation()
    {
        var f = new Fixture();
        var assignment = f.Assignment();
        var store = new MemoryStore(f) { App = f.App with { Assignments = [assignment] } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        await store.InApplicationAsync(f.App.Id, async tx =>
        {
            await ChangeService.QueueRevocationAsync(tx, assignment.Id, "Earlier campaign rejected access", f.Clock.Now, Ct);
            return true;
        }, Ct);
        var earlier = Assert.Single(store.Operations.Values);
        store.Operations[earlier.Id] = earlier with { Status = OperationStatus.Failed, Attempts = 6,
            ErrorCode = "connector_unavailable" };
        await service.RequestAsync(f.App.Id, f.Owner,
            new Change { Kind = ChangeKind.Reconcile, Reason = "Remote write did not take effect" },
            store.App.Revision, "reconcile-earlier-revocation", Ct);
        var processor = new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock);
        await processor.ProcessAsync(f.App.Id, Ct);
        Assert.Equal(OperationStatus.Rejected, store.Operations[earlier.Id].Status);
        Assert.Equal("reconciled_not_applied", store.Operations[earlier.Id].ErrorCode);

        var review = await service.StartReviewAsync(f.App.Id, f.Owner, "New mandatory review", f.Clock.Now.AddMinutes(1), Ct);
        f.Clock.Now = review.DueAt;
        await processor.SweepAsync(f.App.Id, Ct);
        Assert.True(store.Reviews[review.Id].Closed);
        Assert.Equal("RevocationQueued", Assert.Single(store.Reviews[review.Id].Items).Decision);
        Assert.Single(store.Operations.Values, op => op.Change.Kind == ChangeKind.RevokeAccess
            && op.Change.TargetId == assignment.Id && op.Status == OperationStatus.Queued);
        await processor.ProcessAsync(f.App.Id, Ct);
        Assert.Empty(store.App.Assignments);
    }

    [Fact]
    public void Existing_review_documents_without_schedule_metadata_remain_readable()
    {
        var f = new Fixture();
        var legacy = Wire.Serialize(new { id = Guid.NewGuid(), applicationId = f.App.Id,
            name = "Legacy review", dueAt = f.Clock.Now.AddDays(1),
            items = new[] { new ReviewItem(Guid.NewGuid(), "Pending") }, closed = false });
        var review = Wire.Deserialize<AccessReview>(legacy);
        Assert.Null(review.ScheduledFor);
        Assert.Equal("Legacy review", review.Name);
        Assert.Equal("Pending", Assert.Single(review.Items).Decision);
    }
}
