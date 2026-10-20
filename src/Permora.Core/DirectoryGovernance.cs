namespace Permora.Core;

/// <summary>
/// Detects new separation-of-duties violations caused by external directory group
/// membership changes. Detection is based on a complete current directory snapshot;
/// it is not a guarantee about already issued tokens or unobservable external grants.
/// </summary>
public static class DirectoryGovernance
{
    public static IReadOnlyList<Guid> GroupRevocationsForSeparation(ApplicationDocument app,
        IReadOnlyList<DirectoryPrincipal> directory, DateTimeOffset now)
    {
        if (app.Policy.SeparationRules.Count == 0 || app.Assignments.Count == 0)
            return [];

        var principals = directory.Where(p => p.RealmKey == app.RealmKey)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.ToArray());
        var revokedGroups = new HashSet<Guid>();

        // Automatic revocations must be based on an unambiguous, fresh and complete
        // directory view. A first-match lookup could spuriously revoke access when
        // the same ID appears twice with different LDAP membership snapshots.
        bool TryReliable(Guid id, out DirectoryPrincipal principal)
        {
            principal = default!;
            if (!principals.TryGetValue(id, out var matches) || matches.Length != 1)
                return false;
            var current = matches[0];
            if (!Enum.IsDefined(current.Kind) || string.IsNullOrWhiteSpace(current.KeycloakId)
                || !current.Enabled || current.Protected
                || current.SyncedAt > now.AddMinutes(1)
                || now - current.SyncedAt > TimeSpan.FromMinutes(15)
                || (current.Kind == PrincipalKind.Group && !current.MembershipComplete))
                return false;
            principal = current;
            return true;
        }

        foreach (var rule in app.Policy.SeparationRules)
        {
            var left = app.Assignments.Where(a => a.RoleId == rule.FirstRoleId);
            var right = app.Assignments.Where(a => a.RoleId == rule.SecondRoleId);
            foreach (var lhs in left)
            {
                if (!TryReliable(lhs.PrincipalId, out var leftPrincipal))
                    continue;
                var leftKeys = EffectiveKeys(leftPrincipal).ToHashSet(StringComparer.Ordinal);
                foreach (var rhs in right)
                {
                    if (lhs.Id == rhs.Id || !TryReliable(rhs.PrincipalId, out var rightPrincipal))
                        continue;
                    if (!EffectiveKeys(rightPrincipal).Any(leftKeys.Contains)) continue;

                    // A group mapping can affect many members when LDAP membership changes.
                    // Queue targeted *group* revocations, not a mass deletion of individual
                    // rights that were previously separately authorized.
                    if (leftPrincipal.Kind == PrincipalKind.Group) revokedGroups.Add(lhs.Id);
                    if (rightPrincipal.Kind == PrincipalKind.Group) revokedGroups.Add(rhs.Id);
                }
            }
        }
        return revokedGroups.Order().ToArray();
    }

    /// <summary>
    /// Group membership is a prerequisite for a trustworthy effective-access
    /// reconciliation. An incomplete or stale snapshot MUST produce a finding,
    /// not a false declaration of drift-free effective access. We deliberately
    /// do not revoke unknown members solely from incomplete directory data.
    /// </summary>
    public static IReadOnlyList<Guid> UnverifiableGroupAssignments(ApplicationDocument app,
        IReadOnlyList<DirectoryPrincipal> directory, DateTimeOffset now)
    {
        var groups = directory.Where(p => p.RealmKey == app.RealmKey && p.Kind == PrincipalKind.Group)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.ToArray());

        return app.Assignments
            .Where(a => groups.TryGetValue(a.PrincipalId, out var matches)
                && (matches.Length != 1
                    || !matches[0].MembershipComplete
                    || !matches[0].Enabled || matches[0].Protected
                    || string.IsNullOrWhiteSpace(matches[0].KeycloakId)
                    || matches[0].SyncedAt > now.AddMinutes(1)
                    || now - matches[0].SyncedAt > TimeSpan.FromMinutes(15)))
            .Select(a => a.Id).Distinct().Order().ToArray();
    }

    private static IEnumerable<string> EffectiveKeys(DirectoryPrincipal principal)
        => principal.Kind == PrincipalKind.Group
            ? principal.EffectiveMemberKeys.Where(k => !string.IsNullOrWhiteSpace(k))
            : [principal.SubjectKey];
}
