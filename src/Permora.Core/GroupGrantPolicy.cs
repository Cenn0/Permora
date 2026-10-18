namespace Permora.Core;

/// <summary>
/// API-side admission policy. This does not confer Keycloak privileges or replace
/// the worker connector's independent authorization gate.
/// </summary>
public interface IGroupGrantPolicy
{
    bool Allows(string realmKey, string keycloakGroupId);
}

/// <summary>
/// Snapshot of explicit per-realm Keycloak group IDs admitted for grants.
/// Neither a blanket realm approval nor a missing allowlist authorizes a group.
/// This guard does not reduce the Keycloak service token's actual privileges.
/// </summary>
public sealed class GroupGrantPolicy(
    IReadOnlyDictionary<string, IReadOnlySet<string>> allowedGroupIds) : IGroupGrantPolicy
{
    public bool Allows(string realmKey, string keycloakGroupId) =>
        allowedGroupIds.TryGetValue(realmKey, out var allowed)
        && !string.IsNullOrWhiteSpace(keycloakGroupId)
        && allowed.Contains(keycloakGroupId);
}
