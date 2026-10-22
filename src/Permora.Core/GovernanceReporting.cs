namespace Permora.Core;

public sealed record GovernanceCounts(int Assignments, int ExpiredAssignments,
    int UnverifiableAssignments, int GroupSeparationRevocations, int OpenReviews,
    int PendingDecisions, int OverdueDecisions);
public sealed record GovernanceNotice(string Key, string Code, string Severity,
    int Count, DateTimeOffset? DueAt, Guid? ReviewId);
public sealed record GovernanceReport(Guid ApplicationId, long ApplicationRevision,
    DateTimeOffset AsOf, GovernanceCounts Counts, int TotalNotices,
    IReadOnlyList<GovernanceNotice> Notices, int? NextOffset);

/// <summary>
/// Read-only obligations from persisted portal state, not a live Keycloak compliance
/// attestation. Notices are derived on demand; no delivery or automatic escalation
/// to another person is implied. Application/reviews share the worker's row lock.
/// </summary>
public sealed class GovernanceReporting(IPortalStore store, IReadOnlySet<string> administrators, TimeProvider clock)
{
    public Task<GovernanceReport> ReadAsync(Guid appId, Actor actor, int offset, int limit, CancellationToken ct)
        => store.InApplicationAsync(appId, async tx =>
        {
            // Check the current mandate while the team row is locked, before any
            // directory/review access. Report visibility equals application visibility.
            Policy.RequireVisible(actor, tx.Team, administrators);
            if (offset is < 0 or > 1_000_000 || limit is < 1 or > 50)
                throw new DomainException("invalid_page", "Use offset 0–1000000 and limit 1–50.");
            var app = tx.Application;
            var now = clock.GetUtcNow();
            var directory = await store.DirectoryAsync(app.RealmKey, ct);
            var principals = directory.Where(p => p.RealmKey == app.RealmKey)
                .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.ToArray());
            var unverifiable = app.Assignments.Count(a =>
                !principals.TryGetValue(a.PrincipalId, out var matches) || matches.Length != 1
                || !Enum.IsDefined(matches[0].Kind) || string.IsNullOrWhiteSpace(matches[0].KeycloakId)
                || !matches[0].Enabled || matches[0].Protected
                || matches[0].SyncedAt > now.AddMinutes(1) || now - matches[0].SyncedAt > TimeSpan.FromMinutes(15)
                || (matches[0].Kind == PrincipalKind.Group && !matches[0].MembershipComplete));
            var expired = app.Assignments.Count(a => a.ExpiresAt <= now);
            var groupConflicts = DirectoryGovernance.GroupRevocationsForSeparation(app, directory, now).Count;
            var reviews = (await tx.ReviewsAsync(ct)).Where(r => !r.Closed && r.ApplicationId == appId).ToArray();
            var notices = new List<GovernanceNotice>();
            var pending = 0;
            var overdue = 0;
            foreach (var review in reviews)
            {
                var count = review.Items.Count(i => i.Decision == "Pending");
                pending += count;
                if (review.DueAt <= now) overdue += count;
                if (count == 0) continue;
                var code = review.DueAt <= now ? "review_overdue"
                    : review.DueAt <= now.AddHours(48) ? "review_due_soon" : "review_open";
                notices.Add(new($"review:{review.Id}", code,
                    code == "review_overdue" ? "critical" : code == "review_due_soon" ? "warning" : "info",
                    count, review.DueAt, review.Id));
            }
            if (expired > 0) notices.Add(new("expired", "expired_assignments", "critical", expired, null, null));
            if (unverifiable > 0) notices.Add(new("directory", "directory_unverifiable", "critical", unverifiable, null, null));
            if (groupConflicts > 0) notices.Add(new("sod", "group_separation_conflict", "critical", groupConflicts, null, null));
            var schedule = await tx.ReviewScheduleAsync(ct);
            if (schedule is { Enabled: true } && schedule.NextStartAt <= now)
                notices.Add(new("schedule", "review_schedule_due", "warning", 1, schedule.NextStartAt, null));
            var ordered = notices.OrderBy(n => n.Severity == "critical" ? 0 : n.Severity == "warning" ? 1 : 2)
                .ThenBy(n => n.DueAt ?? DateTimeOffset.MinValue).ThenBy(n => n.Key, StringComparer.Ordinal).ToArray();
            var page = ordered.Skip(offset).Take(limit).ToArray();
            return new GovernanceReport(appId, app.Revision, now,
                new(app.Assignments.Count, expired, unverifiable, groupConflicts, reviews.Length, pending, overdue),
                ordered.Length, page, offset + page.Length < ordered.Length ? offset + page.Length : null);
        }, ct);
}
