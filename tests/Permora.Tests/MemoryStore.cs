namespace Permora.Tests;

// A transactional fake for workflow tests, not an alternate production persistence provider.
public sealed class MemoryStore(Fixture fixture) : IPortalStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Team Team { get; set; } = fixture.Team;
    public ApplicationDocument App { get; set; } = fixture.App;
    public IReadOnlyList<DirectoryPrincipal> Directory { get; set; } = fixture.Directory;
    public Dictionary<Guid, Operation> Operations { get; private set; } = [];
    public Dictionary<Guid, AccessReview> Reviews { get; private set; } = [];
    public AccessReviewSchedule? ReviewSchedule { get; set; }
    public List<AuditEvent> Audit { get; private set; } = [];
    public Task<IReadOnlyList<Team>> TeamsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Team>>([Team]);
    public Task SaveTeamAsync(Team team, long? expectedRevision, Actor actor, CancellationToken ct) { Team = team; return Task.CompletedTask; }
    public Task<IReadOnlyList<ApplicationDocument>> ApplicationsAsync(IReadOnlyCollection<Guid> teams, bool all, CancellationToken ct) => Task.FromResult<IReadOnlyList<ApplicationDocument>>(all || teams.Contains(Team.Id) ? [App] : []);
    public Task<ApplicationDocument?> ApplicationAsync(Guid id, CancellationToken ct) => Task.FromResult<ApplicationDocument?>(id == App.Id ? App : null);
    public Task CreateApplicationAsync(ApplicationDocument app, Actor actor, CancellationToken ct) { App = app; return Task.CompletedTask; }
    public Task<IReadOnlyList<DirectoryPrincipal>> DirectoryAsync(string realm, CancellationToken ct) => Task.FromResult<IReadOnlyList<DirectoryPrincipal>>(Directory.Where(d => d.RealmKey == realm).ToArray());
    public Task ReplaceDirectoryAsync(string realm, IReadOnlyList<DirectoryPrincipal> principals, CancellationToken ct) { Directory = principals; return Task.CompletedTask; }
    public Task<IReadOnlyList<Guid>> DueApplicationsAsync(DateTimeOffset now, int limit, Guid afterId, CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([App.Id]);
    public Task<IReadOnlyList<AuditEvent>> AuditAsync(Guid appId, long after, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditEvent>>(Audit.Where(e => e.Sequence > after && e.ApplicationId == appId).Take(limit).ToArray());
    public Task<IReadOnlyList<Operation>> OperationsAsync(Guid appId, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Operation>>(Operations.Values.Where(o => o.ApplicationId == appId).Take(limit).ToArray());
    public Task<IReadOnlyList<AccessReview>> ReviewsAsync(Guid appId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AccessReview>>(Reviews.Values.Where(r => r.ApplicationId == appId).ToArray());
    public Task<AccessReviewSchedule?> ReviewScheduleAsync(Guid appId, CancellationToken ct) => Task.FromResult(appId == App.Id ? ReviewSchedule : null);
    public Task<DashboardFeed> DashboardAsync(IReadOnlyCollection<Guid> appIds, CancellationToken ct)
    {
        var visible = Operations.Values.Where(o => appIds.Contains(o.ApplicationId)).ToArray();
        return Task.FromResult(new DashboardFeed(visible.Count(o => o.Status == OperationStatus.AwaitingApproval),
            visible.Count(o => o.Status is OperationStatus.Failed or OperationStatus.Conflict), visible.Where(o => o.Status == OperationStatus.AwaitingApproval).Take(20).ToArray(), visible.Take(10).ToArray(), Audit.Where(a => appIds.Contains(a.ApplicationId)).Take(12).ToArray()));
    }
    public async Task<T> InApplicationAsync<T>(Guid id, Func<IApplicationTransaction, Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (App.Id != id) throw new DomainException("not_found", "Not found", 404);
            var session = new Session(App, Team, new(Operations), new(Reviews), new(Audit), ReviewSchedule);
            var result = await action(session);
            App = session.Application; Operations = session.Operations; Reviews = session.Reviews; Audit = session.Audit;
            ReviewSchedule = session.ReviewSchedule;
            return result;
        }
        finally { gate.Release(); }
    }
    private sealed class Session(ApplicationDocument app, Team team, Dictionary<Guid, Operation> operations,
        Dictionary<Guid, AccessReview> reviews, List<AuditEvent> audit, AccessReviewSchedule? reviewSchedule) : IApplicationTransaction
    {
        public ApplicationDocument Application { get; set; } = app;
        public Team Team { get; } = team;
        public Dictionary<Guid, Operation> Operations { get; } = operations;
        public Dictionary<Guid, AccessReview> Reviews { get; } = reviews;
        public List<AuditEvent> Audit { get; } = audit;
        public AccessReviewSchedule? ReviewSchedule { get; private set; } = reviewSchedule;
        public Task<Operation?> OperationAsync(Guid id, CancellationToken ct) => Task.FromResult(Operations.GetValueOrDefault(id));
        public Task<Operation?> IdempotentAsync(string actor, string key, CancellationToken ct) => Task.FromResult(Operations.Values.SingleOrDefault(o => o.RequestedBy.Key == actor && o.IdempotencyKey == key));
        public Task<IReadOnlyList<Operation>> UnresolvedMutationsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Operation>>(Operations.Values.Where(o => o.Status is OperationStatus.Conflict or OperationStatus.Failed).Where(o => o.Attempts > 0 && o.Change.Kind != ChangeKind.Reconcile).ToArray());
        public Task<Operation?> NextOperationAsync(DateTimeOffset now, CancellationToken ct)
        {
            var pending = Operations.Values.Where(o => o.Status == OperationStatus.Queued).ToArray();
            var urgent = pending.Where(o => o.SystemRevocation && o.NextAttemptAt <= now)
                .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).FirstOrDefault();
            if (urgent is not null) return Task.FromResult<Operation?>(urgent);
            var revocations = pending.Where(o => o.Change.Kind == ChangeKind.RevokeAccess).ToArray();
            var manualRevoke = revocations.Where(o => !o.SystemRevocation && o.NextAttemptAt <= now)
                .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).FirstOrDefault();
            if (manualRevoke is not null) return Task.FromResult<Operation?>(manualRevoke);
            // An unconfirmed revoke in backoff must never allow fresh grants past it.
            if (revocations.Length > 0) return Task.FromResult<Operation?>(null);
            var firstMutation = pending.Where(o => !o.SystemRevocation)
                .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).FirstOrDefault();
            return Task.FromResult(firstMutation?.NextAttemptAt <= now ? firstMutation : null);
        }
        public Task SaveOperationAsync(Operation operation, CancellationToken ct) { Operations[operation.Id] = operation; return Task.CompletedTask; }
        public Task AppendAuditAsync(Actor actor, string action, string detail, DateTimeOffset now, CancellationToken ct) { Audit.Add(AuditChain.Create(Application.Id, actor.Key, action, detail, now, Audit.LastOrDefault())); return Task.CompletedTask; }
        public Task<IReadOnlyList<AccessReview>> ReviewsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AccessReview>>(Reviews.Values.Where(r => !r.Closed).ToArray());
        public Task SaveReviewAsync(AccessReview review, CancellationToken ct) { Reviews[review.Id] = review; return Task.CompletedTask; }
        public Task<AccessReviewSchedule?> ReviewScheduleAsync(CancellationToken ct) => Task.FromResult(ReviewSchedule);
        public Task SaveReviewScheduleAsync(AccessReviewSchedule schedule, CancellationToken ct) { ReviewSchedule = schedule; return Task.CompletedTask; }
        public Task<bool> HasUnresolvedRevocationAsync(Guid assignmentId, CancellationToken ct) => Task.FromResult(
            Operations.Values.Any(o => o.ApplicationId == Application.Id && o.Status != OperationStatus.Rejected
                && o.Change.Kind == ChangeKind.RevokeAccess && o.Change.TargetId == assignmentId));
    }
}
public sealed class FakeConnector : IAccessConnector
{
    public int Writes { get; private set; }
    public Exception? Failure { get; set; }
    public IReadOnlyList<DriftFinding> Findings { get; set; } = [];
    public Task VerifyBindingAsync(ApplicationDocument app, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyAsync(ApplicationDocument before, ApplicationDocument after, Change change, IReadOnlyList<DirectoryPrincipal> directory, CancellationToken ct)
    { Writes++; if (Failure is not null) throw Failure; return Task.CompletedTask; }
    public Task<IReadOnlyList<DriftFinding>> InspectAsync(ApplicationDocument app, IReadOnlyList<DirectoryPrincipal> directory, CancellationToken ct) => Task.FromResult(Findings);
}
