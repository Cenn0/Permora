namespace Permora.Tests;

public sealed class GovernanceReportingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ManagementRole.Owner)]
    [InlineData(ManagementRole.Approver)]
    [InlineData(ManagementRole.Auditor)]
    [InlineData(ManagementRole.Maintainer)]
    [InlineData(ManagementRole.AccessManager)]
    public async Task Every_visible_mandate_can_read_without_mutating_review_or_application(ManagementRole role)
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var assignment = f.Assignment();
        store.App = f.App with { Assignments = [assignment] };
        var review = new AccessReview(Guid.NewGuid(), f.App.Id, "Check", f.Clock.Now,
            [new(assignment.Id, "Pending"), new(Guid.NewGuid(), "Retain")]);
        store.Reviews.Add(review.Id, review);
        var before = Wire.Serialize(store.App);
        var actor = store.Team.Members.Single(m => m.Role == role);
        var report = await new GovernanceReporting(store, f.Admins, f.Clock)
            .ReadAsync(f.App.Id, new(actor.SubjectKey, actor.Name), 0, 25, Ct);
        Assert.Equal(1, report.Counts.PendingDecisions);
        Assert.Equal(1, report.Counts.OverdueDecisions);
        Assert.Equal("review_overdue", Assert.Single(report.Notices).Code);
        Assert.Equal(before, Wire.Serialize(store.App));
        Assert.Equal(review, Assert.Single(store.Reviews.Values));
        Assert.Empty(store.Operations);
        Assert.Empty(store.Audit);
    }

    [Fact]
    public async Task Removed_or_foreign_mandate_is_denied_even_with_invalid_page()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        store.Team = store.Team with { Members = [] };
        var service = new GovernanceReporting(store, f.Admins, f.Clock);
        foreach (var actor in new[] { f.Owner, f.Outsider })
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => service.ReadAsync(f.App.Id, actor, -1, 100, Ct));
            Assert.Equal(404, error.Status);
        }
        var admin = await new GovernanceReporting(store, new HashSet<string> { f.Owner.Key }, f.Clock)
            .ReadAsync(f.App.Id, f.Owner, 0, 25, Ct);
        Assert.Empty(admin.Notices);
    }

    [Theory]
    [InlineData(-1, 25)]
    [InlineData(1000001, 25)]
    [InlineData(0, 0)]
    [InlineData(0, 51)]
    public async Task Invalid_pages_are_rejected(int offset, int limit)
    {
        var f = new Fixture();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            new GovernanceReporting(new MemoryStore(f), f.Admins, f.Clock).ReadAsync(f.App.Id, f.Owner, offset, limit, Ct));
        Assert.Equal("invalid_page", error.Code);
    }

    [Fact]
    public async Task All_open_reviews_are_counted_and_stably_paged_beyond_200_without_closed_or_foreign_rows()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        for (var i = 0; i < 211; i++)
        {
            var review = new AccessReview(Guid.NewGuid(), f.App.Id, "Review", f.Clock.Now.AddHours(1), [new(Guid.NewGuid(), "Pending")]);
            store.Reviews.Add(review.Id, review);
        }
        var closed = new AccessReview(Guid.NewGuid(), f.App.Id, "Closed", f.Clock.Now.AddDays(-1), [new(Guid.NewGuid(), "Pending")], true);
        var foreign = closed with { Id = Guid.NewGuid(), ApplicationId = Guid.NewGuid(), Closed = false };
        store.Reviews.Add(closed.Id, closed); store.Reviews.Add(foreign.Id, foreign);
        var service = new GovernanceReporting(store, f.Admins, f.Clock);
        var keys = new List<string>();
        int? offset = 0;
        while (offset is { } next)
        {
            var page = await service.ReadAsync(f.App.Id, f.Auditor, next, 50, Ct);
            Assert.Equal(211, page.Counts.OpenReviews);
            Assert.Equal(211, page.Counts.PendingDecisions);
            Assert.Equal(211, page.TotalNotices);
            Assert.InRange(page.Notices.Count, 1, 50);
            keys.AddRange(page.Notices.Select(n => n.Key));
            offset = page.NextOffset;
        }
        Assert.Equal(211, keys.Distinct().Count());
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Empty((await service.ReadAsync(f.App.Id, f.Owner, 500, 50, Ct)).Notices);
    }

    [Fact]
    public async Task Reminder_and_escalation_thresholds_use_server_time_and_disappear_after_closure()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var review = new AccessReview(Guid.NewGuid(), f.App.Id, "Review", f.Clock.Now.AddHours(49), [new(Guid.NewGuid(), "Pending")]);
        store.Reviews.Add(review.Id, review);
        var service = new GovernanceReporting(store, f.Admins, f.Clock);
        async Task<string> Code() => Assert.Single((await service.ReadAsync(f.App.Id, f.Owner, 0, 25, Ct)).Notices).Code;
        Assert.Equal("review_open", await Code());
        f.Clock.Now = f.Clock.Now.AddHours(1);
        Assert.Equal("review_due_soon", await Code());
        f.Clock.Now = review.DueAt;
        Assert.Equal("review_overdue", await Code());
        store.Reviews[review.Id] = review with { Closed = true };
        Assert.Empty((await service.ReadAsync(f.App.Id, f.Owner, 0, 25, Ct)).Notices);
    }

    [Fact]
    public async Task Missing_ambiguous_stale_disabled_and_incomplete_directory_is_not_reported_as_verified()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var principals = new[] {
            f.Employee with { Id = Guid.NewGuid(), SyncedAt = f.Clock.Now.AddMinutes(-16) },
            f.Employee with { Id = Guid.NewGuid(), Enabled = false },
            f.Employee with { Id = Guid.NewGuid(), Kind = PrincipalKind.Group, MembershipComplete = false },
            f.Employee with { Id = Guid.NewGuid(), SyncedAt = f.Clock.Now.AddMinutes(2) },
            f.Employee with { Id = Guid.NewGuid(), Kind = (PrincipalKind)99 },
            f.Employee with { Id = Guid.NewGuid(), Protected = true },
            f.Employee with { Id = Guid.NewGuid(), KeycloakId = "" }
        };
        store.Directory = [.. principals, f.Employee, f.Employee];
        store.App = f.App with { Assignments = [.. principals.Select(p => f.Assignment(principal: p.Id)),
            f.Assignment(), f.Assignment(principal: Guid.NewGuid())] };
        var report = await new GovernanceReporting(store, f.Admins, f.Clock).ReadAsync(f.App.Id, f.Owner, 0, 25, Ct);
        Assert.Equal(9, report.Counts.UnverifiableAssignments);
        Assert.Equal(9, Assert.Single(report.Notices).Count);
        Assert.Equal("directory_unverifiable", report.Notices[0].Code);
        var json = Wire.Serialize(report);
        Assert.DoesNotContain(f.Employee.SubjectKey, json);
        Assert.DoesNotContain(f.Employee.KeycloakId, json);
    }

    [Fact]
    public async Task Expiration_and_group_conflict_match_worker_detection_without_queuing_writes()
    {
        var f = new Fixture();
        var store = new MemoryStore(f);
        var group = f.Employee with { Id = Guid.NewGuid(), Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Employee.SubjectKey] };
        store.Directory = [f.Employee, group];
        store.App = f.App with {
            Policy = new(true, 90, [new(f.Reader.Id, f.Supervisor.Id, "Separate")]),
            Assignments = [f.Assignment() with { ExpiresAt = f.Clock.Now }, f.Assignment(f.Supervisor.Id, group.Id)]
        };
        store.ReviewSchedule = new("Plan", 30, 7, f.Clock.Now, true, 1);
        var report = await new GovernanceReporting(store, f.Admins, f.Clock).ReadAsync(f.App.Id, f.Owner, 0, 25, Ct);
        Assert.Equal(1, report.Counts.ExpiredAssignments);
        Assert.Equal(1, report.Counts.GroupSeparationRevocations);
        Assert.Contains(report.Notices, n => n.Code == "review_schedule_due");
        Assert.Empty(store.Operations);
        store.ReviewSchedule = store.ReviewSchedule with { Enabled = false };
        report = await new GovernanceReporting(store, f.Admins, f.Clock).ReadAsync(f.App.Id, f.Owner, 0, 25, Ct);
        Assert.DoesNotContain(report.Notices, n => n.Code == "review_schedule_due");
    }
}
