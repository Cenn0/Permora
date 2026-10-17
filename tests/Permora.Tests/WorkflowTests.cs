namespace Permora.Tests;

public sealed class WorkflowTests
{
    [Fact] public async Task Foreign_team_is_not_allowed_to_probe_review_or_decision_field_validation()
    {
        var f = new Fixture(); var service = new ChangeService(new MemoryStore(f), f.Admins, f.Clock);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.StartReviewAsync(f.App.Id, f.Outsider, "x", DateTimeOffset.MinValue, Ct));
        Assert.Equal("not_found", error.Code);
        Assert.Equal(404, error.Status);
        var decision = await Assert.ThrowsAsync<DomainException>(() =>
            service.DecideAsync(f.App.Id, Guid.NewGuid(), f.Outsider, true, "x", Ct));
        Assert.Equal("not_found", decision.Code);
        Assert.Equal(404, decision.Status);
        var request = await Assert.ThrowsAsync<DomainException>(() =>
            service.RequestAsync(f.App.Id, f.Outsider, f.Grant(), 1, "", Ct));
        Assert.Equal("not_found", request.Code);
        Assert.Equal(404, request.Status);
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact] public async Task Disabled_requester_cannot_execute_an_already_approved_grant()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock); var connector = new FakeConnector();
        var operation = await service.RequestAsync(f.App.Id, f.Owner, f.Grant(), 1, "disabled-owner-0001", Ct);
        await service.DecideAsync(f.App.Id, operation.Id, f.Approver, true, "Approved for project", Ct);
        store.Directory = f.Directory.Select(p => p.SubjectKey == f.Owner.Key ? p with { Enabled = false } : p).ToArray();
        await new OperationProcessor(store, connector, f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Equal("operator_unverified", store.Operations[operation.Id].ErrorCode); Assert.Empty(store.App.Assignments); Assert.Equal(0, connector.Writes);
    }
    [Fact] public async Task Requests_are_not_effective_before_approval_and_remote_confirmation()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock); var connector = new FakeConnector();
        var operation = await service.RequestAsync(f.App.Id, f.Owner, f.Grant(), 1, "request-key-0001", Ct);
        Assert.Equal(OperationStatus.AwaitingApproval, operation.Status); Assert.Empty(store.App.Assignments);
        await service.DecideAsync(f.App.Id, operation.Id, f.Approver, true, "Approved for project", Ct);
        Assert.Empty(store.App.Assignments);
        Assert.True(await new OperationProcessor(store, connector, f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct));
        Assert.Single(store.App.Assignments); Assert.Equal(OperationStatus.Applied, store.Operations[operation.Id].Status); Assert.Equal(1, connector.Writes); Assert.True(AuditChain.Verify(store.Audit));
    }
    [Fact] public async Task Same_idempotency_key_and_payload_return_same_operation()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock); var change = f.Grant();
        var first = await service.RequestAsync(f.App.Id, f.Owner, change, 1, "request-key-0002", Ct);
        var second = await service.RequestAsync(f.App.Id, f.Owner, change, 1, "request-key-0002", Ct);
        Assert.Equal(first.Id, second.Id); Assert.Single(store.Operations);
    }
    [Fact] public async Task Edited_payload_cannot_reuse_idempotency_key()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock); var change = f.Grant();
        await service.RequestAsync(f.App.Id, f.Owner, change, 1, "request-key-0003", Ct);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.RequestAsync(f.App.Id, f.Owner, change with { Reason = "Different request" }, 1, "request-key-0003", Ct));
        Assert.Equal("idempotency_conflict", error.Code);
    }
    [Fact] public async Task Old_revision_is_rejected_before_enqueue()
    { var f = new Fixture(); var service = new ChangeService(new MemoryStore(f), f.Admins, f.Clock); Assert.Equal("revision_conflict", (await Assert.ThrowsAsync<DomainException>(() => service.RequestAsync(f.App.Id, f.Owner, f.Grant(), 0, "request-key-0004", Ct))).Code); }
    [Fact] public async Task Revoked_requester_mandate_is_rechecked_by_worker()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock); var connector = new FakeConnector();
        var op = await service.RequestAsync(f.App.Id, f.Owner, f.Grant(), 1, "request-key-0005", Ct);
        await service.DecideAsync(f.App.Id, op.Id, f.Approver, true, "Approved for test", Ct);
        store.Team = store.Team with { Members = store.Team.Members.Where(m => m.SubjectKey != f.Owner.Key).ToArray() };
        await new OperationProcessor(store, connector, f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Equal(OperationStatus.Conflict, store.Operations[op.Id].Status); Assert.Equal(0, connector.Writes); Assert.Empty(store.App.Assignments);
    }
    [Fact] public async Task Revoked_approver_mandate_is_rechecked_by_worker()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock); var connector = new FakeConnector();
        var op = await service.RequestAsync(f.App.Id, f.Owner, f.Grant(), 1, "request-key-0006", Ct);
        await service.DecideAsync(f.App.Id, op.Id, f.Approver, true, "Approved for test", Ct);
        store.Team = store.Team with { Members = store.Team.Members.Where(m => m.SubjectKey != f.Approver.Key).ToArray() };
        await new OperationProcessor(store, connector, f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Equal(OperationStatus.Conflict, store.Operations[op.Id].Status); Assert.Equal(0, connector.Writes);
    }
    [Fact] public async Task Network_failure_does_not_publish_effective_assignment()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock); var connector = new FakeConnector { Failure = new HttpRequestException("Test transport failure") };
        var op = await service.RequestAsync(f.App.Id, f.Owner, f.Grant(), 1, "request-key-0007", Ct);
        await service.DecideAsync(f.App.Id, op.Id, f.Approver, true, "Approved for test", Ct);
        await new OperationProcessor(store, connector, f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Empty(store.App.Assignments); Assert.Equal(1, store.App.Revision); Assert.Equal(OperationStatus.Queued, store.Operations[op.Id].Status); Assert.Equal(1, store.Operations[op.Id].Attempts); Assert.True(store.Operations[op.Id].NextAttemptAt > f.Clock.Now);
    }
    [Fact] public async Task Retry_backoff_does_not_allow_later_mutations_to_overtake()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var connector = new FakeConnector();
        var first = new Operation(Guid.NewGuid(), f.App.Id, f.Owner, f.Grant(), 1, "first-request", "hash", OperationStatus.Queued, f.Clock.Now, f.Clock.Now.AddMinutes(2), 1, f.Approver, f.Clock.Now);
        var second = first with { Id = Guid.NewGuid(), IdempotencyKey = "second-request", CreatedAt = f.Clock.Now.AddSeconds(1), NextAttemptAt = f.Clock.Now, Attempts = 0 };
        store.Operations[first.Id] = first; store.Operations[second.Id] = second;
        Assert.False(await new OperationProcessor(store, connector, f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct)); Assert.Equal(0, connector.Writes);
    }
    [Fact] public async Task Due_revocation_is_not_blocked_by_earlier_revocation_backoff()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var connector = new FakeConnector();
        var grant1 = f.Assignment(); var grant2 = f.Assignment(f.Supervisor.Id);
        store.App = store.App with { Assignments = [grant1, grant2] };
        var older = new Operation(Guid.NewGuid(), f.App.Id, new Actor("system", "Lifecycle"),
            new Change { Kind = ChangeKind.RevokeAccess, TargetId = grant1.Id, Reason = "Expired" },
            1, "revoke:" + grant1.Id, "hash1", OperationStatus.Queued, f.Clock.Now,
            f.Clock.Now.AddMinutes(2), 1, SystemRevocation: true);
        var newer = older with { Id = Guid.NewGuid(), Change = new Change { Kind = ChangeKind.RevokeAccess,
            TargetId = grant2.Id, Reason = "Emergency" }, IdempotencyKey = "revoke:" + grant2.Id,
            CreatedAt = f.Clock.Now.AddSeconds(1), NextAttemptAt = f.Clock.Now, Attempts = 0 };
        store.Operations[older.Id] = older; store.Operations[newer.Id] = newer;
        await new OperationProcessor(store, connector, f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.DoesNotContain(store.App.Assignments, a => a.Id == grant2.Id);
        Assert.Contains(store.App.Assignments, a => a.Id == grant1.Id);
        Assert.Equal(OperationStatus.Queued, store.Operations[older.Id].Status);
        Assert.Equal(OperationStatus.Applied, store.Operations[newer.Id].Status);
    }
    [Fact] public async Task Exhausted_retries_become_failed_without_changing_domain_state()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock); var connector = new FakeConnector { Failure = new HttpRequestException("Offline") };
        var op = await service.RequestAsync(f.App.Id, f.Owner, f.Grant(), 1, "request-key-0008", Ct);
        await service.DecideAsync(f.App.Id, op.Id, f.Approver, true, "Approved for test", Ct);
        var processor = new OperationProcessor(store, connector, f.Admins, f.Clock);
        for (var i = 0; i < 6; i++) { await processor.ProcessAsync(f.App.Id, Ct); f.Clock.Now = store.Operations[op.Id].NextAttemptAt; store.Directory = store.Directory.Select(p => p with { SyncedAt = f.Clock.Now }).ToArray(); }
        Assert.Equal(OperationStatus.Failed, store.Operations[op.Id].Status); Assert.Empty(store.App.Assignments);
        Assert.Equal("ambiguous_previous_change", (await Assert.ThrowsAsync<DomainException>(() => service.RequestAsync(f.App.Id, f.Owner, f.Grant(), 1, "request-key-0009", Ct))).Code);
    }
    [Fact] public async Task Clean_reconciliation_abandons_failed_mutations_instead_of_claiming_success()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var service = new ChangeService(store, f.Admins, f.Clock);
        var failed = new Operation(Guid.NewGuid(), f.App.Id, f.Owner, f.Grant(), 1, "failed-change", "hash", OperationStatus.Failed, f.Clock.Now, f.Clock.Now, 6);
        store.Operations[failed.Id] = failed;
        await service.RequestAsync(f.App.Id, f.Owner, new Change { Kind = ChangeKind.Reconcile, Reason = "Verify recovery" }, 1, "reconcile-request", Ct);
        await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Equal(OperationStatus.Rejected, store.Operations[failed.Id].Status); Assert.Equal("reconciled_not_applied", store.Operations[failed.Id].ErrorCode); Assert.Empty(store.App.Assignments);
    }
    [Fact] public async Task Drift_does_not_clear_ambiguous_write_barrier()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var failed = new Operation(Guid.NewGuid(), f.App.Id, f.Owner, f.Grant(), 1, "failed-change", "hash", OperationStatus.Failed, f.Clock.Now, f.Clock.Now, 6); store.Operations[failed.Id] = failed;
        var service = new ChangeService(store, f.Admins, f.Clock); await service.RequestAsync(f.App.Id, f.Owner, new Change { Kind = ChangeKind.Reconcile, Reason = "Inspect drift" }, 1, "reconcile-request", Ct);
        await new OperationProcessor(store, new FakeConnector { Findings = [new("role.reader", "untracked_direct_assignment", "Unexpected grant")] }, f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Equal(OperationStatus.Failed, store.Operations[failed.Id].Status); Assert.Contains(store.Audit, a => a.Action == "drift.detected");
        var reconcile = store.Operations.Values.Single(o => o.Change.Kind == ChangeKind.Reconcile);
        Assert.Equal(OperationStatus.Conflict, reconcile.Status);
        Assert.Equal("drift_detected", reconcile.ErrorCode);
    }
    [Fact] public async Task Expiration_queues_one_revocation_and_survives_unrelated_revision()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var assignment = f.Assignment() with { ExpiresAt = f.Clock.Now.AddMinutes(-1) }; store.App = store.App with { Assignments = [assignment] };
        var processor = new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock);
        await processor.SweepAsync(f.App.Id, Ct); await processor.SweepAsync(f.App.Id, Ct); Assert.Single(store.Operations);
        store.App = store.App with { Revision = 5 }; await processor.ProcessAsync(f.App.Id, Ct); Assert.Empty(store.App.Assignments);
    }
    [Fact] public async Task Overdue_review_queues_revocation()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var assignment = f.Assignment(); store.App = store.App with { Assignments = [assignment] };
        var service = new ChangeService(store, f.Admins, f.Clock); var review = await service.StartReviewAsync(f.App.Id, f.Owner, "Quarterly review", f.Clock.Now.AddHours(1), Ct);
        f.Clock.Now = f.Clock.Now.AddHours(2); await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock).SweepAsync(f.App.Id, Ct);
        Assert.True(store.Reviews[review.Id].Closed); Assert.Equal("RevocationQueued", store.Reviews[review.Id].Items[0].Decision); Assert.Single(store.Operations);
    }
    [Fact] public async Task Cannot_certify_own_access()
    {
        var f = new Fixture(); var store = new MemoryStore(f); var assignment = f.Assignment(principal: f.OwnerPrincipal.Id); store.App = store.App with { Assignments = [assignment] };
        var service = new ChangeService(store, f.Admins, f.Clock); var review = await service.StartReviewAsync(f.App.Id, f.Owner, "Quarterly review", f.Clock.Now.AddHours(1), Ct);
        Assert.Equal("self_certification", (await Assert.ThrowsAsync<DomainException>(() => service.ReviewAsync(f.App.Id, review.Id, assignment.Id, f.Owner, true, Ct))).Code);
    }
    [Fact] public async Task Pending_binding_never_activates_without_explicit_central_iam_attestation()
    {
        var f = new Fixture(); var store = new MemoryStore(f) { App = f.App with { Status = ApplicationStatus.AwaitingIam } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var operation = await service.RequestAsync(f.App.Id, f.Owner, new Change { Kind = ChangeKind.Reconcile,
            Reason = "IAM verification" }, 1, "binding-no-approval", Ct);
        await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Equal(ApplicationStatus.AwaitingIam, store.App.Status);
        Assert.Equal("iam_approval_required", store.Operations[operation.Id].ErrorCode);
    }
    [Fact] public async Task Attested_binding_activates_only_after_clean_remote_verification()
    {
        var f = new Fixture(); var approval = new IamApproval(f.Owner.Key, f.Clock.Now,
            f.App.KeycloakClientUuid!, f.App.KeycloakClientId!, "IAM-TICKET-42");
        var store = new MemoryStore(f) { App = f.App with { Status = ApplicationStatus.AwaitingIam, IamApproval = approval } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        await service.RequestAsync(f.App.Id, f.Owner, new Change { Kind = ChangeKind.Reconcile,
            Reason = "IAM verification" }, 1, "binding-attested", Ct);
        await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Equal(ApplicationStatus.Active, store.App.Status);
    }
    [Fact] public async Task Approval_for_a_different_client_does_not_activate_the_application()
    {
        var f = new Fixture(); var approval = new IamApproval(f.Owner.Key, f.Clock.Now,
            Guid.NewGuid().ToString(), f.App.KeycloakClientId!, "IAM-TICKET-43");
        var store = new MemoryStore(f) { App = f.App with { Status = ApplicationStatus.AwaitingIam, IamApproval = approval } };
        var service = new ChangeService(store, f.Admins, f.Clock);
        var operation = await service.RequestAsync(f.App.Id, f.Owner, new Change { Kind = ChangeKind.Reconcile,
            Reason = "IAM verification" }, 1, "binding-mismatch", Ct);
        await new OperationProcessor(store, new FakeConnector(), f.Admins, f.Clock).ProcessAsync(f.App.Id, Ct);
        Assert.Equal(ApplicationStatus.AwaitingIam, store.App.Status);
        Assert.Equal("iam_approval_required", store.Operations[operation.Id].ErrorCode);
    }
}
