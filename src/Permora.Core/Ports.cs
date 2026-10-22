namespace Permora.Core;

public interface IPortalStore
{
    Task<DashboardFeed> DashboardAsync(IReadOnlyCollection<Guid> appIds, CancellationToken ct);
    Task<IReadOnlyList<Team>> TeamsAsync(CancellationToken ct);
    Task SaveTeamAsync(Team team, long? expectedRevision, Actor actor, CancellationToken ct);
    Task<IReadOnlyList<ApplicationDocument>> ApplicationsAsync(IReadOnlyCollection<Guid> teams, bool all, CancellationToken ct);
    Task<ApplicationDocument?> ApplicationAsync(Guid id, CancellationToken ct);
    Task CreateApplicationAsync(ApplicationDocument app, Actor actor, CancellationToken ct);
    Task<T> InApplicationAsync<T>(Guid id, Func<IApplicationTransaction, Task<T>> action, CancellationToken ct);
    Task<IReadOnlyList<DirectoryPrincipal>> DirectoryAsync(string realm, CancellationToken ct);
    Task ReplaceDirectoryAsync(string realm, IReadOnlyList<DirectoryPrincipal> principals, CancellationToken ct);
    Task<IReadOnlyList<Guid>> DueApplicationsAsync(DateTimeOffset now, int limit, Guid afterId, CancellationToken ct);
    Task<IReadOnlyList<AuditEvent>> AuditAsync(Guid appId, long after, int limit, CancellationToken ct);
    Task<IReadOnlyList<Operation>> OperationsAsync(Guid appId, int limit, CancellationToken ct);
    Task<IReadOnlyList<AccessReview>> ReviewsAsync(Guid appId, CancellationToken ct);
    Task<AccessReviewSchedule?> ReviewScheduleAsync(Guid appId, CancellationToken ct);
}

public interface IApplicationTransaction
{
    ApplicationDocument Application { get; set; }
    Team Team { get; }
    Task<Operation?> OperationAsync(Guid id, CancellationToken ct);
    Task<Operation?> IdempotentAsync(string actorKey, string idempotencyKey, CancellationToken ct);
    Task<IReadOnlyList<Operation>> UnresolvedMutationsAsync(CancellationToken ct);
    Task<Operation?> NextOperationAsync(DateTimeOffset now, CancellationToken ct);
    Task SaveOperationAsync(Operation operation, CancellationToken ct);
    Task AppendAuditAsync(Actor actor, string action, string detail, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<AccessReview>> ReviewsAsync(CancellationToken ct);
    Task SaveReviewAsync(AccessReview review, CancellationToken ct);
    Task<AccessReviewSchedule?> ReviewScheduleAsync(CancellationToken ct);
    Task SaveReviewScheduleAsync(AccessReviewSchedule schedule, CancellationToken ct);
    Task<bool> HasUnresolvedRevocationAsync(Guid assignmentId, CancellationToken ct);
}

public interface IIdentityConnector
{
    Task<IReadOnlyList<DirectoryPrincipal>> ReadDirectoryAsync(string realmKey, CancellationToken ct);
}
public interface IAccessConnector
{
    Task ApplyAsync(ApplicationDocument before, ApplicationDocument after, Change change,
        IReadOnlyList<DirectoryPrincipal> directory, CancellationToken ct);
    Task<IReadOnlyList<DriftFinding>> InspectAsync(ApplicationDocument app,
        IReadOnlyList<DirectoryPrincipal> directory, CancellationToken ct);
    Task VerifyBindingAsync(ApplicationDocument app, CancellationToken ct);
}
