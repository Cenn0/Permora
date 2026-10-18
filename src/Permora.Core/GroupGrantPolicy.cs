namespace Permora.Core;

/// <summary>
/// API-side admission policy. This does not confer Keycloak privileges or replace
/// the worker connector's independent authorization gate.
/// </summary>
public interface IGroupGrantPolicy
{
    bool Allows(string realmKey);
}

/// <summary>
/// Snapshot of realm approvals loaded from the API's non-secret configuration.
/// Missing realms are denied. Keep the worker's separate connector gate enabled.
/// </summary>
public sealed class GroupGrantPolicy(IReadOnlySet<string> approvedRealms) : IGroupGrantPolicy
{
    public bool Allows(string realmKey) => approvedRealms.Contains(realmKey);
}
