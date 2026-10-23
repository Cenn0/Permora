using Microsoft.Extensions.Configuration;

namespace Permora.Infrastructure;

/// <summary>
/// Explicitly construct connector profiles. ConfigurationBinder may silently
/// discard dictionary values when a record constructor's empty collection cannot
/// be bound. Losing a configured realm must never look like a healthy idle worker.
/// </summary>
public static class KeycloakConfiguration
{
    public static KeycloakOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Keycloak");
        var realms = new Dictionary<string, RealmConnection>(StringComparer.Ordinal);
        foreach (var item in section.GetSection("Realms").GetChildren())
        {
            string Required(IConfiguration value, string key) =>
                !string.IsNullOrWhiteSpace(value[key]) ? value[key]!
                    : throw new InvalidOperationException("A configured Keycloak realm or credential profile is incomplete.");
            ServiceCredential Credential(string name)
            {
                var profile = item.GetSection(name);
                return new ServiceCredential(Required(profile, "ClientId"), Required(profile, "SecretFile"));
            }
            var protectedIds = item.GetSection("ProtectedSubjectIds").Get<string[]>() ?? [];
            if (protectedIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Protected Keycloak identities must contain nonempty identifiers.");
            var approvedGroupIds = item.GetSection("ApprovedGroupIds").Get<string[]>() ?? [];
            if (approvedGroupIds.Any(string.IsNullOrWhiteSpace)
                || approvedGroupIds.Any(id => id != id.Trim())
                || approvedGroupIds.Distinct(StringComparer.Ordinal).Count() != approvedGroupIds.Length)
                throw new InvalidOperationException("Approved Keycloak group IDs must be nonempty, unique and untrimmed.");
            var probeIds = item.GetSection("DirectoryProbeUserIds").Get<string[]>() ?? [];
            if (probeIds.Length > 32 || probeIds.Any(id => string.IsNullOrWhiteSpace(id)
                    || id.Length > 1024 || id != id.Trim())
                || probeIds.Distinct(StringComparer.Ordinal).Count() != probeIds.Length)
                throw new InvalidOperationException("Directory probe IDs must be unique nonempty identifiers, bounded to 32 probes of 1024 characters.");
            realms.Add(item.Key, new RealmConnection(Required(item, "BaseUrl"), Required(item, "Realm"),
                Required(item, "Issuer"), Credential("Roles"), Credential("Assignments"), Credential("Directory"), protectedIds,
                item.GetValue<bool>("AllowGroupRoleGrants"), approvedGroupIds, probeIds));
        }
        return new KeycloakOptions
        {
            AllowHttpInDevelopment = section.GetValue<bool>("AllowHttpInDevelopment"),
            Realms = realms
        };
    }
}
