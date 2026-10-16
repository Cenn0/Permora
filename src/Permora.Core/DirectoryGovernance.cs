namespace Permora.Core;

/// <summary>
/// Detects new separation-of-duties violations caused by external directory group
/// membership changes. Detection is based on a complete current directory snapshot;
/// it is not a guarantee about already issued tokens or unobservable external grants.
/// </summary>
public static class DirectoryGovernance
{
    public static IReadOnlyList<Guid> GroupRevocationsForSeparation(ApplicationDocument app,
        IReadOnlyList<DirectoryPrincipal> directory)
    {
        if (app.Policy.SeparationRules.Count == 0 || app.Assignments.Count == 0)
            return [];

        var principals = directory.Where(p => p.RealmKey == app.RealmKey)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var revokedGroups = new HashSet<Guid>();

        foreach (var rule in app.Policy.SeparationRules)
        {
            var left = app.Assignments.Where(a => a.RoleId == rule.FirstRoleId);
            var right = app.Assignments.Where(a => a.RoleId == rule.SecondRoleId);
            foreach (var lhs in left)
            {
                if (!principals.TryGetValue(lhs.PrincipalId, out var leftPrincipal)
                    || (leftPrincipal.Kind == PrincipalKind.Group && !leftPrincipal.MembershipComplete))
                    continue;
                var leftKeys = EffectiveKeys(leftPrincipal).ToHashSet(StringComparer.Ordinal);
                foreach (var rhs in right)
                {
                    if (lhs.Id == rhs.Id || !principals.TryGetValue(rhs.PrincipalId, out var rightPrincipal)
                        || (rightPrincipal.Kind == PrincipalKind.Group && !rightPrincipal.MembershipComplete))
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

    private static IEnumerable<string> EffectiveKeys(DirectoryPrincipal principal)
        => principal.Kind == PrincipalKind.Group
            ? principal.EffectiveMemberKeys.Where(k => !string.IsNullOrWhiteSpace(k))
            : [principal.SubjectKey];
}
