using Permora.Core;

namespace Permora.Tests;

/// <summary>
/// Contract-level simulation of the most dangerous network failure: Keycloak commits
/// a change, but the connector loses its response before PostgreSQL can record success.
/// These tests prove the portal retry/desired-state behavior with an idempotent simulated
/// remote; they do NOT replace fault injection against an actual Keycloak instance.
/// </summary>
public sealed class RemoteCommitRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Grant_retries_after_remote_commit_without_double_granting()
    {
        var fixture = new Fixture();
        var store = new MemoryStore(fixture);
        var connector = new StatefulRemoteConnector { FailAfterFirstRemoteWrite = true };
        var service = new ChangeService(store, fixture.Admins, fixture.Clock);
        var request = await service.RequestAsync(fixture.App.Id, fixture.Owner, fixture.Grant(), 1,
            "post-commit-grant", Ct);
        await service.DecideAsync(fixture.App.Id, request.Id, fixture.Approver, true, "Verified", Ct);
        var processor = new OperationProcessor(store, connector, fixture.Admins, fixture.Clock);

        await processor.ProcessAsync(fixture.App.Id, Ct);
        Assert.Equal(OperationStatus.Queued, store.Operations[request.Id].Status);
        Assert.Equal(1, store.Operations[request.Id].Attempts);
        Assert.Empty(store.App.Assignments);
        Assert.Single(connector.RemoteAssignments);
        Assert.Equal(1, connector.PhysicalWrites);

        fixture.Clock.Now = store.Operations[request.Id].NextAttemptAt;
        store.Directory = store.Directory.Select(p => p with { SyncedAt = fixture.Clock.Now }).ToArray();
        await processor.ProcessAsync(fixture.App.Id, Ct);

        Assert.Equal(OperationStatus.Applied, store.Operations[request.Id].Status);
        Assert.Single(store.App.Assignments);
        Assert.Equal(1, connector.PhysicalWrites);
        Assert.True(connector.RemoteAssignments.SetEquals(store.App.Assignments.Select(a => a.Id)));
        Assert.True(AuditChain.Verify(store.Audit));
    }

    [Fact]
    public async Task Revocation_retries_after_remote_commit_without_regranting()
    {
        var fixture = new Fixture();
        var assignment = fixture.Assignment() with { ExpiresAt = fixture.Clock.Now.AddMinutes(-1) };
        var store = new MemoryStore(fixture) { App = fixture.App with { Assignments = [assignment] } };
        var connector = new StatefulRemoteConnector { FailAfterFirstRemoteWrite = true };
        connector.RemoteAssignments.Add(assignment.Id);
        var processor = new OperationProcessor(store, connector, fixture.Admins, fixture.Clock);
        await processor.SweepAsync(fixture.App.Id, Ct);
        var queued = Assert.Single(store.Operations.Values);
        Assert.True(queued.SystemRevocation);

        await processor.ProcessAsync(fixture.App.Id, Ct);
        Assert.Equal(OperationStatus.Queued, store.Operations[queued.Id].Status);
        Assert.Single(store.App.Assignments);
        Assert.Empty(connector.RemoteAssignments);
        Assert.Equal(1, connector.PhysicalWrites);

        fixture.Clock.Now = store.Operations[queued.Id].NextAttemptAt;
        await processor.ProcessAsync(fixture.App.Id, Ct);
        Assert.Equal(OperationStatus.Applied, store.Operations[queued.Id].Status);
        Assert.Empty(store.App.Assignments);
        Assert.Empty(connector.RemoteAssignments);
        Assert.Equal(1, connector.PhysicalWrites);
        Assert.True(AuditChain.Verify(store.Audit));
    }

    [Fact]
    public async Task Lost_remote_response_never_marks_grant_applied_early()
    {
        var fixture = new Fixture();
        var store = new MemoryStore(fixture);
        var connector = new StatefulRemoteConnector { FailAfterFirstRemoteWrite = true };
        var service = new ChangeService(store, fixture.Admins, fixture.Clock);
        var request = await service.RequestAsync(fixture.App.Id, fixture.Owner, fixture.Grant(),
            fixture.App.Revision, "post-commit-ambiguous", Ct);
        await service.DecideAsync(fixture.App.Id, request.Id, fixture.Approver, true, "Verified", Ct);

        await new OperationProcessor(store, connector, fixture.Admins, fixture.Clock).ProcessAsync(fixture.App.Id, Ct);

        Assert.Equal(fixture.App.Revision, store.App.Revision);
        Assert.Empty(store.App.Assignments);
        Assert.Equal(OperationStatus.Queued, store.Operations[request.Id].Status);
        Assert.DoesNotContain(store.Audit, e => e.Action == "change.applied");
        Assert.Single(connector.RemoteAssignments);
        Assert.True(AuditChain.Verify(store.Audit));
    }

    private sealed class StatefulRemoteConnector : IAccessConnector
    {
        public HashSet<Guid> RemoteAssignments { get; } = [];
        public bool FailAfterFirstRemoteWrite { get; set; }
        public int PhysicalWrites { get; private set; }
        public Task VerifyBindingAsync(ApplicationDocument app, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<DriftFinding>> InspectAsync(ApplicationDocument app,
            IReadOnlyList<DirectoryPrincipal> directory, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DriftFinding>>([]);
        public Task ApplyAsync(ApplicationDocument before, ApplicationDocument after, Change change,
            IReadOnlyList<DirectoryPrincipal> directory, CancellationToken ct)
        {
            bool changed = change.Kind switch
            {
                ChangeKind.GrantAccess => RemoteAssignments.Add(change.TargetId),
                ChangeKind.RevokeAccess => RemoteAssignments.Remove(change.TargetId),
                _ => throw new InvalidOperationException("Only grant/revoke is simulated.")
            };
            if (changed) PhysicalWrites++;
            if (FailAfterFirstRemoteWrite && changed)
            {
                FailAfterFirstRemoteWrite = false;
                throw new HttpRequestException("Simulated lost response after remote commit");
            }
            return Task.CompletedTask;
        }
    }
}
