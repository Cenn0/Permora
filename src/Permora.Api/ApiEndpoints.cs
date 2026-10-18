using System.Security.Claims;
using Npgsql;
using Permora.Core;
using Permora.Infrastructure;

namespace Permora.Api;

public static class ApiEndpoints
{
    public static Actor Actor(ClaimsPrincipal principal, PortalOptions options)
    {
        var subject = principal.FindFirstValue("sub") ?? throw new DomainException("unauthorized", "A subject is required.", 401);
        var issuer = principal.FindFirstValue("iss") ?? options.Authority;
        if (issuer.TrimEnd('/') != options.Authority.TrimEnd('/')) throw new DomainException("invalid_issuer", "Unexpected identity issuer.", 401);
        return new Actor(Wire.Subject(issuer, subject), principal.FindFirstValue("name") ?? "Teammitglied");
    }

    public static void Map(WebApplication app, PortalOptions options, IReadOnlySet<string> administrators)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        async Task<(ApplicationDocument App, Team Team)> Visible(Guid id, ClaimsPrincipal user, IPortalStore store, CancellationToken ct)
        {
            var entity = await store.ApplicationAsync(id, ct) ?? throw new DomainException("not_found", "Application not found.", 404);
            var team = (await store.TeamsAsync(ct)).Single(t => t.Id == entity.TeamId);
            Policy.RequireVisible(Actor(user, options), team, administrators);
            return (entity, team);
        }
        async Task<IReadOnlyList<ApplicationDocument>> Applications(ClaimsPrincipal user, IPortalStore store, CancellationToken ct)
        {
            var actor = Actor(user, options);
            var teams = (await store.TeamsAsync(ct)).Where(t => Policy.Role(actor, t, administrators) is not null).Select(t => t.Id).ToArray();
            return await store.ApplicationsAsync(teams, administrators.Contains(actor.Key), ct);
        }

        api.MapGet("/teams", async (ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
            (await store.TeamsAsync(ct)).Where(t => Policy.Role(Actor(user, options), t, administrators) is not null));
        api.MapGet("/realms", async (ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            var actor = Actor(user, options);
            var teams = (await store.TeamsAsync(ct)).Where(t => Policy.Role(actor, t, administrators) == ManagementRole.Owner).Select(t => t.Id.ToString()).ToHashSet();
            return options.RealmTeams.Where(r => administrators.Contains(actor.Key) || r.Value.Any(teams.Contains)).Select(r => r.Key);
        });
        api.MapGet("/applications", async (ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
            (await Applications(user, store, ct)).Select(Summary));
        api.MapGet("/dashboard", async (ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            var visible = await Applications(user, store, ct);
            var feed = await store.DashboardAsync(visible.Select(a => a.Id).ToArray(), ct);
            return Results.Ok(new
            {
                applications = visible.Select(Summary),
                counts = new { applications = visible.Count, roles = visible.Sum(a => a.Roles.Count),
                    identities = visible.SelectMany(a => a.Assignments).Select(a => a.PrincipalId).Distinct().Count(),
                    requests = feed.AwaitingApproval, failed = feed.Failed },
                requests = feed.Requests, recentOperations = feed.RecentOperations, activities = feed.Activities
            });
        });
        api.MapPost("/applications", async (RegisterApplication input, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            var actor = Actor(user, options);
            var team = (await store.TeamsAsync(ct)).SingleOrDefault(t => t.Id == input.TeamId)
                ?? throw new DomainException("not_found", "Team not found.", 404);
            if (!team.Members.Any(m => m.SubjectKey == actor.Key && m.Role == ManagementRole.Owner))
                throw new DomainException("forbidden", "Only explicit team owners may register applications.", 403);
            if (!options.RealmTeams.TryGetValue(input.RealmKey, out var allowed)
                || (!administrators.Contains(actor.Key) && !allowed.Contains(team.Id.ToString())))
                throw new DomainException("realm_not_allowed", "The team cannot register applications in this realm.", 403);
            Policy.Key(input.Slug); Policy.Text(input.Name, "Name", 2, 120); Policy.Text(input.Description, "Description", 0, 2000);
            if (input.Environment is not ("Development" or "Test" or "Production")) throw new DomainException("invalid_environment", "Choose Development, Test or Production.");
            var entity = new ApplicationDocument(Guid.NewGuid(), team.Id, input.RealmKey, input.Slug, input.Name, input.Description,
                input.Environment, ApplicationStatus.AwaitingIam, 1, null, null, null, AppPolicy.Default, [], [], []);
            await store.CreateApplicationAsync(entity, actor, ct);
            return Results.Created($"/api/v1/applications/{entity.Id}", entity);
        });
        api.MapGet("/applications/{id:guid}", async (Guid id, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            var (entity, team) = await Visible(id, user, store, ct);
            var assignedIds = entity.Assignments.Select(a => a.PrincipalId).ToHashSet();
            var principals = (await store.DirectoryAsync(entity.RealmKey, ct)).Where(p => assignedIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Kind, p.Enabled, p.SyncedAt });
            return Results.Ok(new { application = entity, managementRole = Policy.Role(Actor(user, options), team, administrators), principals });
        });
        api.MapGet("/applications/{id:guid}/identities", async (Guid id, string q, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            var (entity, _) = await Visible(id, user, store, ct);
            Policy.Text(q, "Search", 3, 100);
            return Results.Ok((await store.DirectoryAsync(entity.RealmKey, ct)).Where(p => p.Enabled && !p.Protected && p.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(25).Select(p => new { p.Id, p.Name, p.Kind, p.SyncedAt, p.MembershipComplete }));
        });
        api.MapPost("/applications/{id:guid}/changes", async (Guid id, SubmitChange input, HttpContext context, ChangeService changes, CancellationToken ct) =>
        {
            var operation = await changes.RequestAsync(id, Actor(context.User, options), input.Change, input.ExpectedRevision,
                context.Request.Headers["Idempotency-Key"].ToString(), ct);
            return Results.Accepted($"/api/v1/applications/{id}/operations/{operation.Id}", operation);
        });
        api.MapGet("/applications/{id:guid}/operations", async (Guid id, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        { await Visible(id, user, store, ct); return await store.OperationsAsync(id, 200, ct); });
        api.MapGet("/applications/{id:guid}/operations/{operationId:guid}", async (Guid id, Guid operationId, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            await Visible(id, user, store, ct);
            return await store.InApplicationAsync(id, async tx =>
                await tx.OperationAsync(operationId, ct) ?? throw new DomainException("not_found", "Operation not found.", 404), ct);
        });
        api.MapPost("/applications/{id:guid}/operations/{operationId:guid}/decision", async (Guid id, Guid operationId, Decision input,
            ClaimsPrincipal user, ChangeService changes, CancellationToken ct) => await changes.DecideAsync(id, operationId, Actor(user, options), input.Approve, input.Reason, ct));
        api.MapPost("/applications/{id:guid}/operations/{operationId:guid}/retry", async (Guid id, Guid operationId, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            await Visible(id, user, store, ct);
            return await store.InApplicationAsync(id, async tx =>
            {
                var actor = Actor(user, options);
                if (Policy.Role(actor, tx.Team, administrators) != ManagementRole.Owner) throw new DomainException("forbidden", "Only owners may retry.", 403);
                var operation = await tx.OperationAsync(operationId, ct) ?? throw new DomainException("not_found", "Operation not found.", 404);
                if (operation.Status is not (OperationStatus.Failed or OperationStatus.Conflict)
                    || operation.Attempts == 0 || (!operation.SystemRevocation && operation.ExpectedRevision != tx.Application.Revision))
                    throw new DomainException("not_retryable", "Reconcile and resubmit this request.", 409);
                var updated = operation with { Status = OperationStatus.Queued, Attempts = 0, NextAttemptAt = DateTimeOffset.UtcNow, ErrorCode = null };
                await tx.SaveOperationAsync(updated, ct);
                await tx.AppendAuditAsync(actor, "change.retry_requested", operationId.ToString(), DateTimeOffset.UtcNow, ct);
                return updated;
            }, ct);
        });
        api.MapGet("/applications/{id:guid}/audit", async (Guid id, long? after, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            await Visible(id, user, store, ct);
            if (after < 0) throw new DomainException("invalid_cursor", "Cursor must not be negative.");
            var items = await store.AuditAsync(id, after ?? 0, 200, ct);
            return Results.Ok(new { items, nextSequence = items.Count == 200 ? (long?)items[^1].Sequence : null });
        });
        api.MapGet("/applications/{id:guid}/reviews", async (Guid id, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        { await Visible(id, user, store, ct); return await store.ReviewsAsync(id, ct); });
        api.MapPost("/applications/{id:guid}/reviews", async (Guid id, StartReview input, ClaimsPrincipal user, ChangeService changes, CancellationToken ct) =>
            await changes.StartReviewAsync(id, Actor(user, options), input.Name, input.DueAt, ct));
        api.MapPost("/applications/{id:guid}/reviews/{reviewId:guid}/items/{assignmentId:guid}", async (Guid id, Guid reviewId, Guid assignmentId,
            ReviewDecision input, ClaimsPrincipal user, ChangeService changes, CancellationToken ct) =>
            await changes.ReviewAsync(id, reviewId, assignmentId, Actor(user, options), input.Retain, ct));

        var admin = api.MapGroup("/admin").RequireAuthorization("PlatformAdmin");
        // Aggregate health only: never expose per-user grants, client secrets, or realm identifiers.
        admin.MapGet("/operations-health", async (NpgsqlDataSource database, CancellationToken ct) =>
            await OperationsHealth.ReadAsync(database, DateTimeOffset.UtcNow, ct));
        admin.MapPost("/teams", async (SaveTeam input, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            ValidateTeam(input);
            var entity = new Team(Guid.NewGuid(), input.Name.Trim(), 1, NormalizeMembers(input.Members));
            await store.SaveTeamAsync(entity, null, Actor(user, options), ct);
            return Results.Created($"/api/v1/teams/{entity.Id}", entity);
        });
        admin.MapPut("/teams/{id:guid}", async (Guid id, SaveTeam input, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
        {
            ValidateTeam(input);
            var entity = new Team(id, input.Name.Trim(), input.ExpectedRevision + 1, NormalizeMembers(input.Members));
            await store.SaveTeamAsync(entity, input.ExpectedRevision, Actor(user, options), ct);
            return entity;
        });
        admin.MapPut("/applications/{id:guid}/binding", async (Guid id, Binding input, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
            await store.InApplicationAsync(id, async tx =>
            {
                if (tx.Application.Revision != input.ExpectedRevision) throw new DomainException("revision_conflict", "Reload before binding.", 412);
                if (tx.Application.Permissions.Count > 0 || tx.Application.Roles.Count > 0 || tx.Application.Assignments.Count > 0)
                    throw new DomainException("binding_in_use", "Binding changes require an explicit migration for a populated application.", 409);
                if (!Guid.TryParse(input.ClientUuid, out _) || (input.LoginClientUuid is not null && !Guid.TryParse(input.LoginClientUuid, out _)))
                    throw new DomainException("invalid_client_uuid", "Use Keycloak internal UUIDs, not display client IDs.");
                Policy.Text(input.ClientId, "Client ID", 2, 120);
                tx.Application = tx.Application with { KeycloakClientUuid = input.ClientUuid, KeycloakClientId = input.ClientId,
                    LoginClientUuid = input.LoginClientUuid, IamApproval = null,
                    Status = ApplicationStatus.AwaitingIam, Revision = tx.Application.Revision + 1 };
                var actor = Actor(user, options); var now = DateTimeOffset.UtcNow;
                var command = new Change { Kind = ChangeKind.Reconcile, Reason = "Central IAM approved the client binding" };
                var op = new Operation(Guid.NewGuid(), id, actor, command, tx.Application.Revision,
                    $"binding:{Guid.NewGuid()}", Wire.Hash(Wire.Serialize(command)), OperationStatus.Queued, now, now, 0);
                await tx.SaveOperationAsync(op, ct);
                await tx.AppendAuditAsync(actor, "binding.approved", AuditDetails.ClientBinding(input.ClientId), now, ct);
                return tx.Application;
            }, ct));
        admin.MapPost("/applications/{id:guid}/iam-approval", async (Guid id, ConfirmIamApproval input,
            ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
            await store.InApplicationAsync(id, async tx =>
            {
                if (tx.Application.Revision != input.ExpectedRevision)
                    throw new DomainException("revision_conflict", "Reload before confirming IAM controls.", 412);
                if (tx.Application.Status != ApplicationStatus.AwaitingIam
                    || string.IsNullOrWhiteSpace(tx.Application.KeycloakClientUuid)
                    || string.IsNullOrWhiteSpace(tx.Application.KeycloakClientId))
                    throw new DomainException("invalid_state", "Only a bound, pending application can be approved.", 409);
                if (!input.FgapVerified || !input.RolePermissionsVerified || !input.AssignmentPermissionsVerified
                    || !input.TokenAudienceVerified)
                    throw new DomainException("incomplete_iam_review", "All four IAM security checks must be completed.", 409);
                Policy.Text(input.EvidenceReference, "IAM evidence reference", 6, 400);
                var actor = Actor(user, options);
                var now = DateTimeOffset.UtcNow;
                tx.Application = tx.Application with
                {
                    IamApproval = new(actor.Key, now, tx.Application.KeycloakClientUuid, tx.Application.KeycloakClientId,
                        input.EvidenceReference.Trim()),
                    Revision = tx.Application.Revision + 1
                };
                var change = new Change { Kind = ChangeKind.Reconcile, Reason = "Central IAM attestation: verify binding" };
                var operation = new Operation(Guid.NewGuid(), id, actor, change, tx.Application.Revision,
                    $"iam-approval:{Guid.NewGuid()}", Wire.Hash(Wire.Serialize(change)), OperationStatus.Queued, now, now, 0);
                await tx.SaveOperationAsync(operation, ct);
                await tx.AppendAuditAsync(actor, "iam.approved", AuditDetails.IamApproval(tx.Application.KeycloakClientId!, input.EvidenceReference), now, ct);
                return tx.Application;
            }, ct));
        admin.MapPut("/applications/{id:guid}/policy", async (Guid id, UpdatePolicy input, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
            await store.InApplicationAsync(id, async tx =>
            {
                if (input.ExpectedRevision != tx.Application.Revision) throw new DomainException("revision_conflict", "Reload before editing.", 412);
                if (input.Policy.MaxGrantDays is < 1 or > 365 || input.Policy.SeparationRules.Count > 100)
                    throw new DomainException("invalid_policy", "Policy limits exceeded.");
                if (tx.Application.Assignments.Count > 0 && Wire.Serialize(input.Policy) != Wire.Serialize(tx.Application.Policy))
                    throw new DomainException("policy_in_use", "Review and revoke existing grants before changing the policy in this release.", 409);
                foreach (var rule in input.Policy.SeparationRules)
                    if (rule.FirstRoleId == rule.SecondRoleId || !tx.Application.Roles.Any(r => r.Id == rule.FirstRoleId)
                        || !tx.Application.Roles.Any(r => r.Id == rule.SecondRoleId))
                        throw new DomainException("invalid_separation_rule", "Separation rules must reference different roles of this application.");
                tx.Application = tx.Application with { Policy = input.Policy, Revision = tx.Application.Revision + 1 };
                await tx.AppendAuditAsync(Actor(user, options), "policy.updated", Wire.Serialize(input.Policy), DateTimeOffset.UtcNow, ct);
                return tx.Application;
            }, ct));
        admin.MapPut("/applications/{id:guid}/state", async (Guid id, UpdateState input, ClaimsPrincipal user, IPortalStore store, CancellationToken ct) =>
            await store.InApplicationAsync(id, async tx =>
            {
                if (input.ExpectedRevision != tx.Application.Revision) throw new DomainException("revision_conflict", "Reload before editing.", 412);
                if (input.Status is not (ApplicationStatus.Suspended or ApplicationStatus.AwaitingIam))
                    throw new DomainException("invalid_state", "Only suspend or request re-verification here.");
                tx.Application = tx.Application with { Status = input.Status,
                    IamApproval = input.Status == ApplicationStatus.AwaitingIam ? null : tx.Application.IamApproval,
                    Revision = tx.Application.Revision + 1 };
                await tx.AppendAuditAsync(Actor(user, options), "application.state_changed", input.Status.ToString(), DateTimeOffset.UtcNow, ct);
                return tx.Application;
            }, ct));
    }

    private static object Summary(ApplicationDocument app) => new
    {
        app.Id, app.TeamId, app.Name, app.Slug, app.Description, app.Environment, app.Status, app.Revision,
        roleCount = app.Roles.Count, permissionCount = app.Permissions.Count,
        identityCount = app.Assignments.Select(a => a.PrincipalId).Distinct().Count(), assignmentCount = app.Assignments.Count
    };
    private static TeamMember[] NormalizeMembers(IReadOnlyList<TeamMember> members) =>
        members.Select(m => m with { SubjectKey = m.SubjectKey.ToUpperInvariant(), Name = m.Name.Trim() }).ToArray();
    private static void ValidateTeam(SaveTeam team)
    {
        Policy.Text(team.Name, "Name", 2, 120);
        foreach (var member in team.Members) Policy.Text(member.Name, "Member name", 2, 120);
        if (team.Members.Count is < 1 or > 200 || !team.Members.Any(m => m.Role == ManagementRole.Owner)
            || team.Members.Select(m => m.SubjectKey.ToUpperInvariant()).Distinct().Count() != team.Members.Count
            || team.Members.Any(m => m.SubjectKey.Length != 64 || m.SubjectKey.Any(c => !Uri.IsHexDigit(c)) || !Enum.IsDefined(m.Role)))
            throw new DomainException("invalid_team", "A team needs unique subject keys and at least one owner.");
    }
    public sealed record RegisterApplication(Guid TeamId, string RealmKey, string Slug, string Name, string Description, string Environment);
    public sealed record SubmitChange(Change Change, long ExpectedRevision);
    public sealed record Decision(bool Approve, string Reason);
    public sealed record StartReview(string Name, DateTimeOffset DueAt);
    public sealed record ReviewDecision(bool Retain);
    public sealed record SaveTeam(string Name, IReadOnlyList<TeamMember> Members, long ExpectedRevision);
    public sealed record Binding(string ClientUuid, string ClientId, string? LoginClientUuid, long ExpectedRevision);
    public sealed record ConfirmIamApproval(long ExpectedRevision, bool FgapVerified,
        bool RolePermissionsVerified, bool AssignmentPermissionsVerified, bool TokenAudienceVerified,
        string EvidenceReference);
    public sealed record UpdatePolicy(AppPolicy Policy, long ExpectedRevision);
    public sealed record UpdateState(ApplicationStatus Status, long ExpectedRevision);
}
