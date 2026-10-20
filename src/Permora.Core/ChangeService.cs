namespace Permora.Core;

public sealed class ChangeService(IPortalStore store, IReadOnlySet<string> administrators, TimeProvider clock,
    IGroupGrantPolicy? groupGrants = null)
{
    public async Task<Operation> RequestAsync(Guid appId, Actor actor, Change change, long expectedRevision,
        string idempotencyKey, CancellationToken ct)
    {
        var initial = await store.ApplicationAsync(appId, ct) ?? throw new DomainException("not_found", "Application not found.", 404);
        var directory = await store.DirectoryAsync(initial.RealmKey, ct);
        var hash = Wire.Hash(Wire.Serialize(new { change, expectedRevision }));
        return await store.InApplicationAsync(appId, async tx =>
        {
            Policy.RequireVisible(actor, tx.Team, administrators);
            Policy.Text(idempotencyKey, "Idempotency-Key", 8, 128);
            var existing = await tx.IdempotentAsync(actor.Key, idempotencyKey, ct);
            if (existing is not null)
            {
                if (existing.RequestHash != hash) throw new DomainException("idempotency_conflict", "Key reused with a different request.", 409);
                return existing;
            }
            if (change.Kind is not (ChangeKind.Reconcile or ChangeKind.RevokeAccess)
                && (await tx.UnresolvedMutationsAsync(ct)).Count > 0)
                throw new DomainException("ambiguous_previous_change", "Resolve or reconcile a previous failed write before requesting new changes.", 409);
            if (tx.Application.Revision != expectedRevision)
                throw new DomainException("revision_conflict", "Reload the application before editing.", 412);
            var now = clock.GetUtcNow();
            var normalized = change.Kind is ChangeKind.CreatePermission or ChangeKind.CreateRole or ChangeKind.GrantAccess
                ? change with { TargetId = Guid.NewGuid() } : change;
            var plan = Policy.Plan(tx.Application, tx.Team, actor, normalized, directory, administrators, now);
            // Reject prohibited group grants before an operation is queued or an approval
            // is requested. The worker's independent Keycloak connector retains its
            // fail-closed enforcement if a config changes after request submission.
            if (normalized.Kind == ChangeKind.GrantAccess)
            {
                var principal = directory.SingleOrDefault(p => p.Id == normalized.PrincipalId
                    && p.RealmKey == tx.Application.RealmKey);
                if (principal?.Kind == PrincipalKind.Group
                    && groupGrants?.Allows(tx.Application.RealmKey, principal.KeycloakId) != true)
                    throw new DomainException("group_mapping_disabled",
                        "Delegated group grants require central IAM approval of Keycloak Groups.manage exposure.", 403);
            }
            var operation = new Operation(Guid.NewGuid(), appId, actor, normalized, expectedRevision, idempotencyKey,
                hash, plan.ApprovalRequired ? OperationStatus.AwaitingApproval : OperationStatus.Queued, now, now, 0);
            await tx.SaveOperationAsync(operation, ct);
            await tx.AppendAuditAsync(actor, "change.requested", AuditDetails.Requested(operation), now, ct);
            return operation;
        }, ct);
    }

    public Task<Operation> DecideAsync(Guid appId, Guid operationId, Actor actor, bool approve, string reason, CancellationToken ct)
    {
        return store.InApplicationAsync(appId, async tx =>
        {
            Policy.RequireVisible(actor, tx.Team, administrators);
            Policy.Text(reason, "Decision reason", 3, 1000);
            var operation = await tx.OperationAsync(operationId, ct) ?? throw new DomainException("not_found", "Request not found.", 404);
            Policy.RequireApproval(actor, operation.RequestedBy, tx.Team, administrators);
            if (operation.Status != OperationStatus.AwaitingApproval)
                throw new DomainException("invalid_state", "This request is no longer awaiting approval.", 409);
            if (approve && (operation.ExpectedRevision != tx.Application.Revision || clock.GetUtcNow() - operation.CreatedAt > TimeSpan.FromDays(7)))
                throw new DomainException("stale_approval", "This request must be resubmitted against the current state.", 409);
            var updated = operation with
            {
                Status = approve ? OperationStatus.Queued : OperationStatus.Rejected,
                ApprovedBy = approve ? actor : null,
                ApprovedAt = approve ? clock.GetUtcNow() : null,
                DecisionReason = reason.Trim()
            };
            await tx.SaveOperationAsync(updated, ct);
            await tx.AppendAuditAsync(actor, approve ? "change.approved" : "change.rejected", AuditDetails.Decision(operationId, reason), clock.GetUtcNow(), ct);
            return updated;
        }, ct);
    }

    public Task<AccessReview> StartReviewAsync(Guid appId, Actor actor, string name, DateTimeOffset dueAt, CancellationToken ct)
    {
        return store.InApplicationAsync(appId, async tx =>
        {
            Policy.RequireVisible(actor, tx.Team, administrators);
            if (Policy.Role(actor, tx.Team, administrators) != ManagementRole.Owner)
                throw new DomainException("forbidden", "Only application owners may start a review.", 403);
            Policy.Text(name, "Review name", 3, 120);
            if (dueAt <= clock.GetUtcNow() || dueAt > clock.GetUtcNow().AddDays(90))
                throw new DomainException("invalid_due_date", "Review due date must be within the next 90 days.");
            var review = new AccessReview(Guid.NewGuid(), appId, name, dueAt,
                tx.Application.Assignments.Select(a => new ReviewItem(a.Id, "Pending")).ToArray());
            await tx.SaveReviewAsync(review, ct);
            await tx.AppendAuditAsync(actor, "review.started", review.Id.ToString(), clock.GetUtcNow(), ct);
            return review;
        }, ct);
    }

    public async Task<AccessReview> ReviewAsync(Guid appId, Guid reviewId, Guid assignmentId, Actor actor, bool retain, CancellationToken ct)
    {
        var app = await store.ApplicationAsync(appId, ct) ?? throw new DomainException("not_found", "Application not found.", 404);
        var directory = await store.DirectoryAsync(app.RealmKey, ct);
        return await store.InApplicationAsync(appId, async tx =>
        {
            Policy.RequireVisible(actor, tx.Team, administrators);
            if (Policy.Role(actor, tx.Team, administrators) is not (ManagementRole.Owner or ManagementRole.Approver))
                throw new DomainException("forbidden", "Review is not permitted.", 403);
            var review = (await tx.ReviewsAsync(ct)).SingleOrDefault(r => r.Id == reviewId)
                ?? throw new DomainException("not_found", "Review not found.", 404);
            if (review.Closed || review.DueAt <= clock.GetUtcNow()) throw new DomainException("review_closed", "The review is closed.", 409);
            if (!review.Items.Any(i => i.AssignmentId == assignmentId && i.Decision == "Pending"))
                throw new DomainException("not_found", "Pending review item not found.", 404);
            var assignment = tx.Application.Assignments.SingleOrDefault(a => a.Id == assignmentId);
            if (assignment is not null && retain)
            {
                // Retention is a new authorization decision. Never certify a
                // guessed identity, an ambiguous lookup or a foreign realm entry.
                var matches = directory.Where(p => p.Id == assignment.PrincipalId
                    && p.RealmKey == app.RealmKey).Take(2).ToArray();
                if (matches.Length == 0)
                    throw new DomainException("identity_missing", "The identity must be refreshed before certification.", 409);
                if (matches.Length != 1)
                    throw new DomainException("ambiguous_directory_identity",
                        "Directory identities conflict. Refresh and reconcile before certification.", 409);
                var principal = matches[0];
                if (!Enum.IsDefined(principal.Kind) || string.IsNullOrWhiteSpace(principal.KeycloakId))
                    throw new DomainException("invalid_directory_identity",
                        "Invalid directory principal cannot be certified.", 409);
                Policy.Fresh(principal, clock.GetUtcNow());
                if (principal.Kind == PrincipalKind.Group && !principal.MembershipComplete)
                    throw new DomainException("incomplete_group", "Group membership must be verified before certification.", 409);
                if (!principal.Enabled || principal.Protected || principal.SubjectKey == actor.Key || principal.EffectiveMemberKeys.Contains(actor.Key))
                    throw new DomainException("self_certification", "You cannot certify your own access or an ineligible identity.", 403);
            }
            if (!retain && assignment is not null) await QueueRevocationAsync(tx, assignmentId, "Access review rejected", clock.GetUtcNow(), ct);
            var items = review.Items.Select(i => i.AssignmentId == assignmentId ? i with
                { Decision = assignment is null ? "AlreadyRevoked" : retain ? "Retained" : "RevocationQueued", ReviewedBy = actor.Key } : i).ToArray();
            var updated = review with { Items = items, Closed = items.All(i => i.Decision != "Pending") };
            await tx.SaveReviewAsync(updated, ct);
            await tx.AppendAuditAsync(actor, "review.decided", $"{reviewId}/{assignmentId}: {(retain ? "retain" : "revoke")}", clock.GetUtcNow(), ct);
            return updated;
        }, ct);
    }

    public static async Task QueueRevocationAsync(IApplicationTransaction tx, Guid assignmentId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var key = $"revoke:{assignmentId}";
        if (await tx.IdempotentAsync("system", key, ct) is not null) return;
        var change = new Change { Kind = ChangeKind.RevokeAccess, TargetId = assignmentId, Reason = reason };
        var operation = new Operation(Guid.NewGuid(), tx.Application.Id, new Actor("system", "Lifecycle worker"), change,
            tx.Application.Revision, key, Wire.Hash(Wire.Serialize(change)), OperationStatus.Queued, now, now, 0, SystemRevocation: true);
        await tx.SaveOperationAsync(operation, ct);
        await tx.AppendAuditAsync(operation.RequestedBy, "revocation.queued", assignmentId.ToString(), now, ct);
    }
}
