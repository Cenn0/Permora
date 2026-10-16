namespace Permora.Core;

public sealed class OperationProcessor(IPortalStore store, IAccessConnector connector,
    IReadOnlySet<string> administrators, TimeProvider clock)
{
    public async Task<bool> ProcessAsync(Guid appId, CancellationToken ct)
    {
        var initial = await store.ApplicationAsync(appId, ct);
        if (initial is null) return false;
        var directory = await store.DirectoryAsync(initial.RealmKey, ct);
        // The PostgreSQL row lock is intentionally held across the bounded connector call.
        // A lost connection can still leave an ambiguous remote result; retries verify postconditions.
        return await store.InApplicationAsync(appId, async tx =>
        {
            var now = clock.GetUtcNow();
            var operation = await tx.NextOperationAsync(now, ct);
            if (operation is null) return false;
            var externalStarted = false;
            try
            {
                if (!operation.SystemRevocation && operation.ExpectedRevision != tx.Application.Revision)
                    throw new DomainException("revision_conflict", "Application changed after the request.", 409);
                if (!operation.SystemRevocation && now - operation.CreatedAt > TimeSpan.FromDays(7))
                    throw new DomainException("request_expired", "The change request expired.", 409);
                if (operation.Change.Kind is not (ChangeKind.Reconcile or ChangeKind.RevokeAccess)
                    && (await tx.UnresolvedMutationsAsync(ct)).Any(o => o.Id != operation.Id))
                    throw new DomainException("ambiguous_previous_change", "A previous failed write needs reconciliation.", 409);
                // Revocations remain valid across unrelated changes; never turn expiry into a grant.
                if (operation.SystemRevocation && !tx.Application.Assignments.Any(a => a.Id == operation.Change.TargetId))
                {
                    await tx.SaveOperationAsync(operation with { Status = OperationStatus.Applied }, ct);
                    return true;
                }
                var plan = Policy.Plan(tx.Application, tx.Team, operation.RequestedBy, operation.Change,
                    directory, administrators, now, operation.SystemRevocation);
                if (plan.ApprovalRequired)
                {
                    if (operation.ApprovedBy is null || operation.ApprovedAt is null || now - operation.ApprovedAt > TimeSpan.FromDays(1))
                        throw new DomainException("approval_expired", "A fresh approval is required.", 409);
                    Policy.RequireApproval(operation.ApprovedBy, operation.RequestedBy, tx.Team, administrators);
                }
                if (!operation.SystemRevocation)
                {
                    ValidateOperator(operation.RequestedBy, directory, now);
                    if (plan.ApprovalRequired) ValidateOperator(operation.ApprovedBy!, directory, now);
                }
                using var connectorBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectorBudget.CancelAfter(TimeSpan.FromSeconds(40));
                if (operation.Change.Kind == ChangeKind.Reconcile)
                {
                    if (tx.Application.Status == ApplicationStatus.AwaitingIam
                        && (tx.Application.IamApproval is not { } approval
                            || approval.ClientUuid != tx.Application.KeycloakClientUuid
                            || approval.ClientId != tx.Application.KeycloakClientId))
                        throw new DomainException("iam_approval_required",
                            "Central IAM must attest FGAP, credentials and token audience before activation.", 409);
                    externalStarted = true;
                    var findings = await connector.InspectAsync(tx.Application, directory, connectorBudget.Token);
                    if (tx.Application.Status == ApplicationStatus.AwaitingIam && findings.Count == 0)
                        tx.Application = plan.Next with { Status = ApplicationStatus.Active };
                    if (findings.Count == 0)
                        foreach (var unresolved in await tx.UnresolvedMutationsAsync(ct))
                        {
                            await tx.SaveOperationAsync(unresolved with { Status = OperationStatus.Rejected, ErrorCode = "reconciled_not_applied" }, ct);
                            await tx.AppendAuditAsync(operation.RequestedBy, "change.abandoned_after_reconciliation", unresolved.Id.ToString(), now, ct);
                        }
                    foreach (var finding in findings)
                        await tx.AppendAuditAsync(operation.RequestedBy, "drift.detected", Wire.Serialize(finding), now, ct);
                }
                else
                {
                    externalStarted = true;
                    await connector.ApplyAsync(tx.Application, plan.Next, operation.Change, directory, connectorBudget.Token);
                    tx.Application = plan.Next;
                }
                await tx.SaveOperationAsync(operation with { Status = OperationStatus.Applied, Attempts = operation.Attempts + 1, ErrorCode = null }, ct);
                await tx.AppendAuditAsync(operation.RequestedBy, "change.applied", $"{operation.Id}: {operation.Change.Kind}", now, ct);
            }
            catch (DomainException ex)
            {
                await tx.SaveOperationAsync(operation with { Status = OperationStatus.Conflict, Attempts = operation.Attempts + (externalStarted ? 1 : 0), ErrorCode = ex.Code }, ct);
                await tx.AppendAuditAsync(operation.RequestedBy, "change.blocked", $"{operation.Id}: {ex.Code}", now, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or System.Text.Json.JsonException or InvalidDataException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                var attempts = operation.Attempts + 1;
                var state = attempts >= 6 ? OperationStatus.Failed : OperationStatus.Queued;
                await tx.SaveOperationAsync(operation with
                {
                    Status = state, Attempts = attempts, ErrorCode = "connector_unavailable",
                    NextAttemptAt = now.AddSeconds(Math.Min(300, Math.Pow(2, attempts) * 5))
                }, ct);
                await tx.AppendAuditAsync(operation.RequestedBy, "change.retry", $"{operation.Id}: attempt {attempts}, {state}", now, ct);
            }
            return true;
        }, ct);
    }

    private static void ValidateOperator(Actor actor, IReadOnlyList<DirectoryPrincipal> directory, DateTimeOffset now)
    {
        var principal = directory.SingleOrDefault(p => p.Kind == PrincipalKind.User && p.SubjectKey == actor.Key);
        if (principal is null || !principal.Enabled || principal.Protected)
            throw new DomainException("operator_unverified", "The requester and approver must be enabled, verified identities.", 409);
        Policy.Fresh(principal, now);
    }

    public Task SweepAsync(Guid appId, CancellationToken ct) => store.InApplicationAsync(appId, async tx =>
    {
        var now = clock.GetUtcNow();
        foreach (var assignment in tx.Application.Assignments.Where(a => a.ExpiresAt <= now))
            await ChangeService.QueueRevocationAsync(tx, assignment.Id, "Grant expired", now, ct);
        foreach (var review in (await tx.ReviewsAsync(ct)).Where(r => !r.Closed && r.DueAt <= now))
        {
            foreach (var item in review.Items.Where(i => i.Decision == "Pending"))
                if (tx.Application.Assignments.Any(a => a.Id == item.AssignmentId))
                    await ChangeService.QueueRevocationAsync(tx, item.AssignmentId, "Access review overdue", now, ct);
            await tx.SaveReviewAsync(review with
            {
                Closed = true,
                Items = review.Items.Select(i => i.Decision == "Pending" ? i with { Decision = "RevocationQueued" } : i).ToArray()
            }, ct);
        }
        return true;
    }, ct);
}
