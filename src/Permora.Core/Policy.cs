using System.Text.RegularExpressions;

namespace Permora.Core;

public static partial class Policy
{
    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static ManagementRole? Role(Actor actor, Team team, IReadOnlySet<string> administrators)
    {
        if (administrators.Contains(actor.Key)) return ManagementRole.Owner;
        return team.Members.FirstOrDefault(x => x.SubjectKey == actor.Key)?.Role;
    }

    public static void RequireVisible(Actor actor, Team team, IReadOnlySet<string> administrators)
    {
        if (Role(actor, team, administrators) is null)
            throw new DomainException("not_found", "Application not found.", 404);
    }

    public static void RequireApproval(Actor actor, Actor requester, Team team, IReadOnlySet<string> administrators)
    {
        RequireVisible(actor, team, administrators);
        if (actor.Key == requester.Key)
            throw new DomainException("self_approval", "A second person must approve this change.", 403);
        if (Role(actor, team, administrators) is not (ManagementRole.Owner or ManagementRole.Approver))
            throw new DomainException("forbidden", "Approval is not permitted.", 403);
    }

    public static void Key(string key)
    {
        if (key.Length is < 2 or > 80 || !KeyPattern().IsMatch(key)
            || key.StartsWith("realm", StringComparison.Ordinal)
            || key.StartsWith("permora", StringComparison.Ordinal)
            || key.StartsWith("role.", StringComparison.Ordinal)
            || key.StartsWith("perm.", StringComparison.Ordinal))
            throw new DomainException("invalid_key", "Use a lowercase application-specific key, without reserved prefixes.");
    }

    public static void Text(string value, string field, int min, int max)
    {
        if (value.Trim().Length < min || value.Length > max || value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new DomainException("invalid_text", $"{field} must contain {min} to {max} characters.");
    }

    public static Plan Plan(ApplicationDocument app, Team team, Actor actor, Change change,
        IReadOnlyList<DirectoryPrincipal> directory, IReadOnlySet<string> administrators,
        DateTimeOffset now, bool systemRevocation = false)
    {
        // No public command can opt into the system path. Only expiration/review workers construct it.
        if (systemRevocation && change.Kind != ChangeKind.RevokeAccess)
            throw new DomainException("invalid_system_operation", "Only revocations can use the system path.", 403);
        var role = Role(actor, team, administrators);
        if (!systemRevocation)
        {
            RequireVisible(actor, team, administrators);
            var allowed = change.Kind switch
            {
                ChangeKind.GrantAccess or ChangeKind.RevokeAccess => role is ManagementRole.Owner or ManagementRole.AccessManager,
                ChangeKind.Reconcile => role is ManagementRole.Owner or ManagementRole.Maintainer,
                _ => role is ManagementRole.Owner or ManagementRole.Maintainer
            };
            if (!allowed) throw new DomainException("forbidden", "This management action is not permitted.", 403);
        }
        if (app.Status != ApplicationStatus.Active && change.Kind is not (ChangeKind.RevokeAccess or ChangeKind.Reconcile))
            throw new DomainException("inactive_application", "The application is not active.", 409);
        Text(change.Reason, "Reason", 3, 1000);
        if (!Enum.IsDefined(change.Kind) || !Enum.IsDefined(change.Risk))
            throw new DomainException("invalid_enum", "Invalid command value.");
        var next = app;
        var privileged = false;
        switch (change.Kind)
        {
            case ChangeKind.CreatePermission:
                Key(change.Key); Text(change.Name, "Name", 2, 120); Text(change.Description, "Description", 0, 2000);
                Unique(app.Permissions.Any(p => p.Key == change.Key || p.Id == change.TargetId));
                // New permissions are high risk until the central IAM team classifies them.
                var permission = new Permission(NonEmpty(change.TargetId), change.Key, change.Name.Trim(), change.Description, Risk.High);
                next = app with { Permissions = [.. app.Permissions, permission] };
                privileged = true;
                break;
            case ChangeKind.UpdatePermission:
                var oldPermission = Get(app.Permissions, p => p.Id == change.TargetId);
                Text(change.Name, "Name", 2, 120); Text(change.Description, "Description", 0, 2000);
                if (change.Risk < oldPermission.Risk && !administrators.Contains(actor.Key))
                    throw new DomainException("risk_downgrade", "Only central IAM may reduce a permission's risk.", 403);
                next = app with { Permissions = app.Permissions.Select(p => p.Id == oldPermission.Id
                    ? p with { Name = change.Name.Trim(), Description = change.Description, Risk = change.Risk } : p).ToArray() };
                privileged = true;
                break;
            case ChangeKind.DeletePermission:
                Get(app.Permissions, p => p.Id == change.TargetId);
                if (app.Roles.Any(r => r.PermissionIds.Contains(change.TargetId)))
                    throw new DomainException("permission_in_use", "Remove the permission from all roles first.", 409);
                next = app with { Permissions = app.Permissions.Where(p => p.Id != change.TargetId).ToArray() };
                break;
            case ChangeKind.CreateRole:
            case ChangeKind.UpdateRole:
                Text(change.Name, "Name", 2, 120); Text(change.Description, "Description", 0, 2000);
                if (change.PermissionIds.Count > 500 || change.PermissionIds.Distinct().Count() != change.PermissionIds.Count)
                    throw new DomainException("invalid_permissions", "Choose no more than 500 distinct permissions.");
                var permissions = change.PermissionIds.Select(id => Get(app.Permissions, p => p.Id == id)).ToArray();
                privileged = permissions.Any(p => p.Risk == Risk.High);
                if (change.Kind == ChangeKind.CreateRole)
                {
                    Key(change.Key); Unique(app.Roles.Any(r => r.Key == change.Key || r.Id == change.TargetId));
                    next = app with { Roles = [.. app.Roles, new AppRole(NonEmpty(change.TargetId), change.Key,
                        change.Name.Trim(), change.Description, change.PermissionIds.ToArray())] };
                }
                else
                {
                    var previous = Get(app.Roles, r => r.Id == change.TargetId);
                    foreach (var assignment in app.Assignments.Where(a => a.RoleId == previous.Id))
                    {
                        var principal = Get(directory, p => p.Id == assignment.PrincipalId);
                        Fresh(principal, now);
                        if (principal.Kind == PrincipalKind.Group && !principal.MembershipComplete)
                            throw new DomainException("incomplete_group", "Effective members must be verified before editing an assigned role.", 409);
                    }
                    if (permissions.Any(p => p.Risk == Risk.High && !previous.PermissionIds.Contains(p.Id))
                        && EffectiveRoleIds(app, actor.Key, directory).Contains(previous.Id))
                        throw new DomainException("self_escalation", "You cannot add privileged permissions to your own effective role.", 403);
                    next = app with { Roles = app.Roles.Select(r => r.Id == previous.Id
                        ? r with { Name = change.Name.Trim(), Description = change.Description, PermissionIds = change.PermissionIds.ToArray() } : r).ToArray() };
                }
                break;
            case ChangeKind.DeleteRole:
                Get(app.Roles, r => r.Id == change.TargetId);
                if (app.Assignments.Any(a => a.RoleId == change.TargetId) || app.Policy.SeparationRules.Any(r => r.FirstRoleId == change.TargetId || r.SecondRoleId == change.TargetId))
                    throw new DomainException("role_in_use", "Remove assignments and separation rules first.", 409);
                next = app with { Roles = app.Roles.Where(r => r.Id != change.TargetId).ToArray() };
                break;
            case ChangeKind.GrantAccess:
                var assignedRole = Get(app.Roles, r => r.Id == change.RoleId);
                var target = Get(directory, p => p.Id == change.PrincipalId && p.RealmKey == app.RealmKey);
                if (!target.Enabled || target.Protected)
                    throw new DomainException("protected_identity", "This identity cannot receive delegated access.", 403);
                Fresh(target, now);
                privileged = assignedRole.PermissionIds.Any(id => Get(app.Permissions, p => p.Id == id).Risk == Risk.High);
                var affected = target.Kind == PrincipalKind.Group ? target.EffectiveMemberKeys : new[] { target.SubjectKey };
                if (target.Kind == PrincipalKind.Group && !target.MembershipComplete)
                    throw new DomainException("incomplete_group", "The effective group membership has not been verified.", 409);
                if (privileged && affected.Contains(actor.Key))
                    throw new DomainException("self_assignment", "Privileged self-assignment is not permitted.", 403);
                if (change.ExpiresAt is null && (privileged || app.Policy.MaxGrantDays > 0))
                    throw new DomainException("expiry_required", "An expiration date is required.");
                if (change.ExpiresAt is { } expiry && (expiry <= now || expiry > now.AddDays(app.Policy.MaxGrantDays)))
                    throw new DomainException("invalid_expiry", "Expiration exceeds the application's policy.");
                Unique(app.Assignments.Any(a => a.PrincipalId == target.Id && a.RoleId == assignedRole.Id));
                foreach (var group in directory.Where(p => p.Kind == PrincipalKind.Group
                    && app.Assignments.Any(a => a.PrincipalId == p.Id)))
                {
                    Fresh(group, now);
                    if (!group.MembershipComplete) throw new DomainException("incomplete_group", "An existing group assignment needs verification.", 409);
                }
                // An empty group can acquire members later. Check its own grants as well as today's members.
                var direct = app.Assignments.Where(a => a.PrincipalId == target.Id).Select(a => a.RoleId).Append(assignedRole.Id).ToHashSet();
                if (app.Policy.SeparationRules.Any(rule => direct.Contains(rule.FirstRoleId) && direct.Contains(rule.SecondRoleId)))
                    throw new DomainException("separation_of_duties", "The role conflicts with this identity's direct access.", 409);
                foreach (var subject in affected)
                {
                    var effective = EffectiveRoleIds(app, subject, directory).Append(assignedRole.Id).ToHashSet();
                    if (app.Policy.SeparationRules.Any(rule => effective.Contains(rule.FirstRoleId) && effective.Contains(rule.SecondRoleId)))
                        throw new DomainException("separation_of_duties", "The role conflicts with existing effective access.", 409);
                }
                next = app with { Assignments = [.. app.Assignments, new Assignment(NonEmpty(change.TargetId), assignedRole.Id,
                    target.Id, actor.Key, now, change.ExpiresAt)] };
                break;
            case ChangeKind.RevokeAccess:
                Get(app.Assignments, a => a.Id == change.TargetId);
                next = app with { Assignments = app.Assignments.Where(a => a.Id != change.TargetId).ToArray() };
                break;
            case ChangeKind.Reconcile:
                break;
            default:
                throw new DomainException("unsupported_change", "Unsupported change.");
        }
        if (next.Permissions.Count > 2000 || next.Roles.Count > 2000 || next.Assignments.Count > 10000)
            throw new DomainException("capacity_limit", "Per-application safety limit reached.", 409);
        var requiresApproval = !systemRevocation && change.Kind is not (ChangeKind.RevokeAccess or ChangeKind.Reconcile)
            && (app.Policy.RequireApproval || privileged);
        return new Plan(next with { Revision = app.Revision + 1 }, requiresApproval, $"{change.Kind}: {change.Reason}");
    }

    public static IReadOnlySet<Guid> EffectiveRoleIds(ApplicationDocument app, string subjectKey, IReadOnlyList<DirectoryPrincipal> directory)
    {
        var principals = directory.Where(p => p.SubjectKey == subjectKey
            || (p.Kind == PrincipalKind.Group && p.EffectiveMemberKeys.Contains(subjectKey))).Select(p => p.Id).ToHashSet();
        // Include expired-but-not-yet-revoked assignments: Keycloak may still enforce them.
        return app.Assignments.Where(a => principals.Contains(a.PrincipalId)).Select(a => a.RoleId).ToHashSet();
    }

    public static void Fresh(DirectoryPrincipal principal, DateTimeOffset now)
    {
        if (principal.SyncedAt > now.AddMinutes(1) || now - principal.SyncedAt > TimeSpan.FromMinutes(15))
            throw new DomainException("stale_identity", "Refresh the directory snapshot before changing access.", 409);
    }
    private static T Get<T>(IEnumerable<T> items, Func<T, bool> predicate) where T : class => items.FirstOrDefault(predicate)
        ?? throw new DomainException("not_found", "The application resource was not found.", 404);
    private static void Unique(bool exists)
    {
        if (exists) throw new DomainException("already_exists", "The resource already exists.", 409);
    }
    private static Guid NonEmpty(Guid id) => id != Guid.Empty ? id : throw new DomainException("invalid_id", "A resource identifier is required.");
}
