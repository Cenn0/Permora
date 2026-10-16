using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Permora.Core;

namespace Permora.Infrastructure;

public sealed record ServiceCredential(string ClientId, string SecretFile);
public sealed record RealmConnection(string BaseUrl, string Realm, string Issuer,
    ServiceCredential Roles, ServiceCredential Assignments, ServiceCredential Directory,
    IReadOnlyList<string> ProtectedSubjectIds);
public sealed class KeycloakOptions
{
    public Dictionary<string, RealmConnection> Realms { get; init; } = [];
    public bool AllowHttpInDevelopment { get; init; }
}

// All URLs come from trusted deployment configuration. No user-controlled host, path or credential profile is forwarded.
public sealed class KeycloakConnector(HttpClient http, KeycloakOptions options, TimeProvider clock) : IAccessConnector, IIdentityConnector
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> tokens = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> tokenLocks = new();
    private const string OwnerAttribute = "permora.application";
    private const string EntityAttribute = "permora.entity";

    public async Task VerifyBindingAsync(ApplicationDocument app, CancellationToken ct)
    {
        Bound(app);
        var client = await GetAsync<Client>(app.RealmKey, "roles", $"clients/{E(app.KeycloakClientUuid!)}", ct)
            ?? throw new DomainException("client_missing", "The configured Keycloak client does not exist.", 409);
        if (client.ClientId != app.KeycloakClientId || client.Attributes?.GetValueOrDefault(OwnerAttribute) != app.Id.ToString()
            || client.Enabled != true || client.Protocol != "openid-connect"
            || client.FullScopeAllowed != false || client.ServiceAccountsEnabled != false
            || client.DirectAccessGrantsEnabled != false || client.ImplicitFlowEnabled != false
            || client.StandardFlowEnabled != false)
            throw new DomainException("unsafe_client", "Client identity, ownership or security configuration is invalid.", 409);
        if (app.LoginClientUuid is { } loginId)
        {
            var login = await GetAsync<Client>(app.RealmKey, "roles", $"clients/{E(loginId)}", ct)
                ?? throw new DomainException("login_client_missing", "The login client does not exist.", 409);
            if (login.Attributes?.GetValueOrDefault(OwnerAttribute) != app.Id.ToString() || login.FullScopeAllowed != false
                || login.Enabled != true || login.Protocol != "openid-connect" || login.ServiceAccountsEnabled != false
                || login.DirectAccessGrantsEnabled != false || login.ImplicitFlowEnabled != false
                || login.StandardFlowEnabled != true
                || login.Attributes?.GetValueOrDefault("pkce.code.challenge.method") != "S256")
                throw new DomainException("unsafe_login_client", "The login client does not meet the application's security template.", 409);
        }
    }

    public async Task ApplyAsync(ApplicationDocument before, ApplicationDocument after, Change change,
        IReadOnlyList<DirectoryPrincipal> directory, CancellationToken ct)
    {
        await VerifyBindingAsync(before, ct);
        switch (change.Kind)
        {
            case ChangeKind.CreatePermission:
            case ChangeKind.UpdatePermission:
                var permission = after.Permissions.Single(p => p.Id == change.TargetId);
                await UpsertRoleAsync(after, permission.Id, "perm." + permission.Key, permission.Description, ct);
                break;
            case ChangeKind.DeletePermission:
                var removed = before.Permissions.Single(p => p.Id == change.TargetId);
                await DeleteRoleAsync(before, removed.Id, "perm." + removed.Key, ct);
                break;
            case ChangeKind.CreateRole:
            case ChangeKind.UpdateRole:
                var role = after.Roles.Single(r => r.Id == change.TargetId);
                var keycloakRole = await UpsertRoleAsync(after, role.Id, "role." + role.Key, role.Description, ct);
                var expected = new List<Role>();
                foreach (var id in role.PermissionIds)
                {
                    var leaf = after.Permissions.Single(p => p.Id == id);
                    expected.Add(await RequiredRoleAsync(after, leaf.Id, "perm." + leaf.Key, ct));
                }
                var compositePath = $"clients/{E(after.KeycloakClientUuid!)}/roles/{E(keycloakRole.Name)}/composites";
                var existing = await GetAsync<Role[]>(after.RealmKey, "roles", compositePath, ct) ?? [];
                foreach (var leaf in existing)
                    if (!leaf.Name.StartsWith("perm.", StringComparison.Ordinal) || !Owned(after, leaf))
                        throw new DomainException("unsafe_composite", "An unmanaged or foreign role was found in a composite.", 409);
                var remove = existing.Where(r => expected.All(e => e.Id != r.Id)).ToArray();
                var add = expected.Where(r => existing.All(e => e.Id != r.Id)).ToArray();
                if (remove.Length > 0) await SendAsync(after.RealmKey, "roles", HttpMethod.Delete, compositePath, remove, ct);
                if (add.Length > 0) await SendAsync(after.RealmKey, "roles", HttpMethod.Post, compositePath, add, ct);
                var verified = await GetAsync<Role[]>(after.RealmKey, "roles", compositePath, ct) ?? [];
                if (!verified.Select(r => r.Id).ToHashSet().SetEquals(expected.Select(r => r.Id)))
                    throw new HttpRequestException("Composite postcondition was not met.");
                break;
            case ChangeKind.DeleteRole:
                var deletedRole = before.Roles.Single(r => r.Id == change.TargetId);
                await DeleteRoleAsync(before, deletedRole.Id, "role." + deletedRole.Key, ct);
                break;
            case ChangeKind.GrantAccess:
                var assignment = after.Assignments.Single(a => a.Id == change.TargetId);
                await MapRoleAsync(after, assignment, directory, true, ct);
                break;
            case ChangeKind.RevokeAccess:
                var revoked = before.Assignments.Single(a => a.Id == change.TargetId);
                await MapRoleAsync(before, revoked, directory, false, ct);
                break;
            default:
                throw new DomainException("unsupported_connector_operation", "The connector does not support this operation.");
        }
    }

    private async Task<Role> UpsertRoleAsync(ApplicationDocument app, Guid entity, string name, string description, CancellationToken ct)
    {
        var path = $"clients/{E(app.KeycloakClientUuid!)}/roles/{E(name)}";
        var existing = await GetAsync<Role>(app.RealmKey, "roles", path, ct);
        if (existing is null)
        {
            var create = new Role(null, name, description, true, null, false, new()
            {
                [OwnerAttribute] = [app.Id.ToString()], [EntityAttribute] = [entity.ToString()]
            });
            await SendAsync(app.RealmKey, "roles", HttpMethod.Post, $"clients/{E(app.KeycloakClientUuid!)}/roles", create, ct);
        }
        var role = await RequiredRoleAsync(app, entity, name, ct);
        if (role.Description != description)
            await SendAsync(app.RealmKey, "roles", HttpMethod.Put, path, role with { Description = description }, ct);
        // A client with Full Scope Allowed disabled must receive explicit role scope mappings.
        if (app.LoginClientUuid is { } login)
            await SendAsync(app.RealmKey, "roles", HttpMethod.Post,
                $"clients/{E(login)}/scope-mappings/clients/{E(app.KeycloakClientUuid!)}", new[] { role }, ct);
        var verified = await RequiredRoleAsync(app, entity, name, ct);
        if (verified.Description != description) throw new HttpRequestException("Role postcondition was not met.");
        return verified;
    }

    private async Task DeleteRoleAsync(ApplicationDocument app, Guid entity, string name, CancellationToken ct)
    {
        var path = $"clients/{E(app.KeycloakClientUuid!)}/roles/{E(name)}";
        var role = await GetAsync<Role>(app.RealmKey, "roles", path, ct);
        if (role is null) return; // A previous attempt may already have completed remotely.
        VerifyRole(app, role, entity);
        // Never delete a role that Keycloak still reports as assigned, including out-of-band grants.
        // A local empty assignment catalog is NOT proof that no user or group is affected.
        var users = await PagesAsync<User>(app.RealmKey, "assignments", path + "/users", ct);
        var groups = await PagesAsync<Group>(app.RealmKey, "assignments", path + "/groups", ct);
        if (users.Count > 0 || groups.Count > 0)
            throw new DomainException("role_still_assigned",
                "Keycloak reports role assignments outside the verified empty catalog; reconcile first.", 409);
        await SendAsync(app.RealmKey, "roles", HttpMethod.Delete, path, null, ct);
        if (await GetAsync<Role>(app.RealmKey, "roles", path, ct) is not null)
            throw new HttpRequestException("Role deletion was not confirmed.");
    }

    private async Task<Role> RequiredRoleAsync(ApplicationDocument app, Guid entity, string name, CancellationToken ct)
    {
        var role = await GetAsync<Role>(app.RealmKey, "roles", $"clients/{E(app.KeycloakClientUuid!)}/roles/{E(name)}", ct)
            ?? throw new DomainException("role_missing", "A managed Keycloak role is missing.", 409);
        VerifyRole(app, role, entity);
        if (name.StartsWith("perm.", StringComparison.Ordinal) && role.Composite)
            throw new DomainException("unsafe_permission_composite", "An atomic permission must not contain other roles.", 409);
        return role;
    }
    private static bool Owned(ApplicationDocument app, Role role) => role.ClientRole && role.ContainerId == app.KeycloakClientUuid
        && Attribute(role, OwnerAttribute) == app.Id.ToString();
    private static string? Attribute(Role role, string name) => role.Attributes?.GetValueOrDefault(name) is { Length: 1 } values ? values[0] : null;
    private static void VerifyRole(ApplicationDocument app, Role role, Guid entity)
    {
        if (string.IsNullOrWhiteSpace(role.Id) || !Owned(app, role)
            || Attribute(role, EntityAttribute) != entity.ToString())
            throw new DomainException("role_ownership_conflict", "Role ownership or client identity is invalid.", 409);
    }

    private async Task MapRoleAsync(ApplicationDocument app, Assignment assignment, IReadOnlyList<DirectoryPrincipal> directory, bool grant, CancellationToken ct)
    {
        var principal = directory.SingleOrDefault(p => p.Id == assignment.PrincipalId && p.RealmKey == app.RealmKey)
            ?? throw new DomainException("identity_missing", "The target identity is missing from the directory snapshot.", 409);
        var role = app.Roles.Single(r => r.Id == assignment.RoleId);
        var mapped = await RequiredRoleAsync(app, role.Id, "role." + role.Key, ct);
        var type = principal.Kind == PrincipalKind.User ? "users" : "groups";
        if (grant)
        {
            await VerifyCompositionAsync(app, role, ct);
            if (principal.Kind == PrincipalKind.User)
            {
                var user = await GetAsync<User>(app.RealmKey, "directory", $"users/{E(principal.KeycloakId)}", ct);
                if (user is null || !user.Enabled || user.ServiceAccountClientId is not null || Connection(app.RealmKey).ProtectedSubjectIds.Contains(user.Id))
                    throw new DomainException("ineligible_identity", "The target is disabled, protected or missing.", 403);
                await CheckLiveSeparationAsync(app, principal.KeycloakId, role.Id, ct);
            }
            else
            {
                var members = await EffectiveMembersAsync(app.RealmKey, principal.KeycloakId, new HashSet<string>(), ct);
                if (members.Count > 200) throw new DomainException("group_size_limit", "Large group grants require a separately reviewed integration.", 409);
                var keys = members.Select(u => Wire.Subject(Connection(app.RealmKey).Issuer, u.Id)).ToHashSet();
                if (!keys.SetEquals(principal.EffectiveMemberKeys))
                    throw new DomainException("group_changed", "Group membership changed. Refresh and resubmit.", 409);
                foreach (var member in members)
                {
                    if (!member.Enabled || member.ServiceAccountClientId is not null || Connection(app.RealmKey).ProtectedSubjectIds.Contains(member.Id))
                        throw new DomainException("ineligible_group_member", "The group contains an ineligible identity.", 403);
                    await CheckLiveSeparationAsync(app, member.Id, role.Id, ct);
                }
            }
        }
        var path = $"{type}/{E(principal.KeycloakId)}/role-mappings/clients/{E(app.KeycloakClientUuid!)}";
        var current = await GetAsync<Role[]>(app.RealmKey, "assignments", path, ct);
        if (current is null)
        {
            if (!grant) return;
            throw new DomainException("identity_missing", "The target identity no longer exists.", 409);
        }
        if (grant != current.Any(r => r.Id == mapped.Id))
            await SendAsync(app.RealmKey, "assignments", grant ? HttpMethod.Post : HttpMethod.Delete, path, new[] { mapped }, ct);
        var verified = await GetAsync<Role[]>(app.RealmKey, "assignments", path, ct) ?? [];
        if (grant != verified.Any(r => r.Id == mapped.Id)) throw new HttpRequestException("Role mapping postcondition was not met.");
    }

    private async Task VerifyCompositionAsync(ApplicationDocument app, AppRole role, CancellationToken ct)
    {
        var expected = new List<Role>();
        foreach (var id in role.PermissionIds)
        {
            var permission = app.Permissions.Single(p => p.Id == id);
            expected.Add(await RequiredRoleAsync(app, permission.Id, "perm." + permission.Key, ct));
        }
        var actual = await GetAsync<Role[]>(app.RealmKey, "roles",
            $"clients/{E(app.KeycloakClientUuid!)}/roles/{E("role." + role.Key)}/composites", ct) ?? [];
        if (actual.Any(r => !Owned(app, r) || r.Composite) || !actual.Select(r => r.Id).ToHashSet().SetEquals(expected.Select(r => r.Id)))
            throw new DomainException("composite_drift", "The role's effective permissions differ from its approved catalog.", 409);
    }

    private async Task CheckLiveSeparationAsync(ApplicationDocument app, string userId, Guid additionalRole, CancellationToken ct)
    {
        var effective = await GetAsync<Role[]>(app.RealmKey, "assignments",
            $"users/{E(userId)}/role-mappings/clients/{E(app.KeycloakClientUuid!)}/composite", ct)
            ?? throw new DomainException("identity_missing", "The target identity no longer exists.", 409);
        var names = effective.Select(r => r.Name).Append("role." + app.Roles.Single(r => r.Id == additionalRole).Key).ToHashSet();
        foreach (var rule in app.Policy.SeparationRules)
        {
            var left = "role." + app.Roles.Single(r => r.Id == rule.FirstRoleId).Key;
            var right = "role." + app.Roles.Single(r => r.Id == rule.SecondRoleId).Key;
            if (names.Contains(left) && names.Contains(right))
                throw new DomainException("live_separation_conflict", "Keycloak has conflicting effective access.", 409);
        }
    }

    public async Task<IReadOnlyList<DriftFinding>> InspectAsync(ApplicationDocument app, IReadOnlyList<DirectoryPrincipal> directory, CancellationToken ct)
    {
        await VerifyBindingAsync(app, ct);
        var findings = new List<DriftFinding>();
        var roles = await PagesAsync<Role>(app.RealmKey, "roles", $"clients/{E(app.KeycloakClientUuid!)}/roles", ct);
        var expected = app.Permissions.Select(p => (p.Id, Name: "perm." + p.Key))
            .Concat(app.Roles.Select(r => (r.Id, Name: "role." + r.Key))).ToArray();
        foreach (var item in expected)
        {
            var actual = roles.SingleOrDefault(r => r.Name == item.Name);
            if (actual is null) findings.Add(new(item.Name, "missing_role", "The managed role is missing."));
            else if (!Owned(app, actual) || Attribute(actual, EntityAttribute) != item.Id.ToString())
                findings.Add(new(item.Name, "ownership_changed", "Role ownership or client identity changed."));
            else if (item.Name.StartsWith("perm.", StringComparison.Ordinal) && actual.Composite)
                findings.Add(new(item.Name, "unsafe_permission_composite", "An atomic permission has become a composite role."));
        }
        foreach (var role in roles.Where(r => expected.All(e => e.Name != r.Name)))
            findings.Add(new(role.Name, "unexpected_role", "A role exists outside the portal catalog; it was not adopted or deleted."));
        foreach (var role in app.Roles)
        {
            if (roles.All(r => r.Name != "role." + role.Key)) continue;
            var children = await GetAsync<Role[]>(app.RealmKey, "roles",
                $"clients/{E(app.KeycloakClientUuid!)}/roles/{E("role." + role.Key)}/composites", ct) ?? [];
            var expectedNames = role.PermissionIds.Select(id => "perm." + app.Permissions.Single(p => p.Id == id).Key).ToHashSet();
            if (children.Any(r => !Owned(app, r)) || !children.Select(r => r.Name).ToHashSet().SetEquals(expectedNames))
                findings.Add(new(role.Key, "composite_drift", "The effective permission composition differs from the catalog."));
        }
        foreach (var assignment in app.Assignments)
        {
            var principal = directory.SingleOrDefault(p => p.Id == assignment.PrincipalId);
            if (principal is null) { findings.Add(new(assignment.Id.ToString(), "identity_missing", "The assigned identity is missing.")); continue; }
            var kind = principal.Kind == PrincipalKind.User ? "users" : "groups";
            var actual = await GetAsync<Role[]>(app.RealmKey, "assignments",
                $"{kind}/{E(principal.KeycloakId)}/role-mappings/clients/{E(app.KeycloakClientUuid!)}", ct) ?? [];
            var name = "role." + app.Roles.Single(r => r.Id == assignment.RoleId).Key;
            if (actual.All(r => r.Name != name)) findings.Add(new(assignment.Id.ToString(), "missing_assignment", "The tracked direct mapping is missing."));
        }
        foreach (var item in expected)
        {
            if (!roles.Any(r => r.Name == item.Name && Owned(app, r))) continue;
            var path = $"clients/{E(app.KeycloakClientUuid!)}/roles/{E(item.Name)}";
            var users = await PagesAsync<User>(app.RealmKey, "assignments", path + "/users", ct);
            var groups = await PagesAsync<Group>(app.RealmKey, "assignments", path + "/groups", ct);
            foreach (var principal in users.Select(u => (Kind: "users", Id: u.Id, Principal: StableId(app.RealmKey, "user", u.Id)))
                .Concat(groups.Select(g => (Kind: "groups", Id: g.Id, Principal: StableId(app.RealmKey, "group", g.Id)))))
            {
                var direct = await GetAsync<Role[]>(app.RealmKey, "assignments",
                    $"{principal.Kind}/{E(principal.Id)}/role-mappings/clients/{E(app.KeycloakClientUuid!)}", ct);
                if (direct?.Any(role => role.Name == item.Name) == true
                    && !app.Assignments.Any(a => a.PrincipalId == principal.Principal && a.RoleId == item.Id))
                    findings.Add(new(item.Name, "untracked_direct_assignment", "A direct role or permission grant exists outside the portal registry."));
            }
        }
        return findings;
    }

    public async Task<IReadOnlyList<DirectoryPrincipal>> ReadDirectoryAsync(string realmKey, CancellationToken ct)
    {
        var realm = Connection(realmKey);
        var users = await PagesAsync<User>(realmKey, "directory", "users", ct);
        var groups = await PagesAsync<Group>(realmKey, "directory", "groups", ct);
        var now = clock.GetUtcNow();
        var result = users.Select(u => new DirectoryPrincipal(StableId(realmKey, "user", u.Id), realmKey,
            Wire.Subject(realm.Issuer, u.Id), u.Id,
            string.Join(" ", new[] { u.FirstName, u.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } name ? name : u.Username,
            PrincipalKind.User, u.Enabled, u.ServiceAccountClientId is not null || realm.ProtectedSubjectIds.Contains(u.Id),
            true, [], now)).ToList();
        var visited = new HashSet<string>();
        async Task AddGroup(Group group)
        {
            if (!visited.Add(group.Id) || visited.Count > 5000) throw new DomainException("directory_cycle", "Group hierarchy is cyclic or too large.", 409);
            var members = await EffectiveMembersAsync(realmKey, group.Id, new HashSet<string>(), ct);
            result.Add(new DirectoryPrincipal(StableId(realmKey, "group", group.Id), realmKey,
                Wire.Subject(realm.Issuer, "group:" + group.Id), group.Id, group.Name, PrincipalKind.Group,
                true, false, true, members.Select(u => Wire.Subject(realm.Issuer, u.Id)).Distinct().ToArray(), now));
            foreach (var child in await PagesAsync<Group>(realmKey, "directory", $"groups/{E(group.Id)}/children", ct)) await AddGroup(child);
        }
        foreach (var group in groups) await AddGroup(group);
        return result;
    }

    private async Task<IReadOnlyList<User>> EffectiveMembersAsync(string realm, string groupId, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(groupId) || visited.Count > 100) throw new DomainException("group_depth_limit", "Group expansion exceeds the safety limit.", 409);
        var members = (await PagesAsync<User>(realm, "directory", $"groups/{E(groupId)}/members", ct)).ToList();
        foreach (var child in await PagesAsync<Group>(realm, "directory", $"groups/{E(groupId)}/children", ct))
            members.AddRange(await EffectiveMembersAsync(realm, child.Id, visited, ct));
        return members.DistinctBy(u => u.Id).ToArray();
    }

    private async Task<IReadOnlyList<T>> PagesAsync<T>(string realm, string profile, string path, CancellationToken ct)
    {
        var results = new List<T>();
        for (var first = 0; first < 100000; first += 100)
        {
            var page = await GetAsync<T[]>(realm, profile, $"{path}?first={first}&max=100&briefRepresentation=false", ct)
                ?? throw new DomainException("directory_incomplete", "A paginated resource was not available.", 409);
            results.AddRange(page);
            if (page.Length < 100) return results;
        }
        throw new DomainException("page_limit", "The configured pagination safety limit was reached.", 409);
    }

    private async Task<T?> GetAsync<T>(string realm, string profile, string path, CancellationToken ct) where T : class
    {
        using var response = await RequestAsync(realm, profile, HttpMethod.Get, path, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Ensure(response);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new HttpRequestException("A successful Keycloak response must not contain JSON null.");
    }
    private async Task SendAsync(string realm, string profile, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await RequestAsync(realm, profile, method, path, body, ct);
        Ensure(response);
    }
    private async Task<HttpResponseMessage> RequestAsync(string realmKey, string profile, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var realm = Connection(realmKey);
        using var request = new HttpRequestMessage(method, $"{realm.BaseUrl.TrimEnd('/')}/admin/realms/{E(realm.Realm)}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(realmKey, profile, ct));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) tokens.TryRemove($"{realmKey}/{profile}", out _);
        return response;
    }
    private async Task<string> TokenAsync(string realmKey, string profile, CancellationToken ct)
    {
        var key = $"{realmKey}/{profile}";
        var gate = tokenLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (tokens.TryGetValue(key, out var cached) && cached.Until > clock.GetUtcNow()) return cached.Token;
            var realm = Connection(realmKey);
            var credential = profile switch { "roles" => realm.Roles, "assignments" => realm.Assignments, "directory" => realm.Directory,
                _ => throw new InvalidOperationException("Unknown credential profile.") };
            var secret = (await File.ReadAllTextAsync(credential.SecretFile, ct)).Trim();
            if (secret.Length < 16) throw new InvalidOperationException("The service credential is missing or too short.");
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = credential.ClientId, ["client_secret"] = secret
            });
            using var response = await http.PostAsync($"{realm.BaseUrl.TrimEnd('/')}/realms/{E(realm.Realm)}/protocol/openid-connect/token", body, ct);
            Ensure(response);
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct) ?? throw new HttpRequestException("Empty token response.");
            if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0) throw new HttpRequestException("Invalid token response.");
            tokens[key] = (token.AccessToken, clock.GetUtcNow().AddSeconds(Math.Max(0, token.ExpiresIn - 30)));
            return token.AccessToken;
        }
        finally { gate.Release(); }
    }
    private RealmConnection Connection(string key) => options.Realms.TryGetValue(key, out var realm) ? realm
        : throw new DomainException("unknown_realm", "The realm is not configured.", 409);
    private static void Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new DomainException("keycloak_permission_denied", "The Keycloak service account is not permitted to perform this operation.", 409);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new DomainException("keycloak_conflict", "Keycloak reports a resource conflict. Reconcile before retrying.", 409);
        // Never copy upstream response bodies, tokens or personal data into public errors.
        throw new HttpRequestException($"Keycloak returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }
    private static string E(string value) => Uri.EscapeDataString(value);
    private static void Bound(ApplicationDocument app)
    {
        if (string.IsNullOrWhiteSpace(app.KeycloakClientUuid) || string.IsNullOrWhiteSpace(app.KeycloakClientId)
            || new[] { "realm-management", "account", "account-console", "admin-cli", "broker", "security-admin-console" }.Contains(app.KeycloakClientId)
            || app.KeycloakClientId.StartsWith("permora", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("unbound_application", "The application is not bound to an approved resource client.", 409);
    }
    public static Guid StableId(string realm, string kind, string id) => new(Convert.FromHexString(Wire.Hash(Wire.Serialize(new[] { realm, kind, id })))[..16]);
    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
    private sealed record Role(string? Id, string Name, string? Description, bool ClientRole, string? ContainerId,
        bool Composite, Dictionary<string, string[]>? Attributes);
    private sealed record Client(string Id, string ClientId, bool? Enabled, string? Protocol, bool? FullScopeAllowed, bool? ServiceAccountsEnabled,
        bool? DirectAccessGrantsEnabled, bool? ImplicitFlowEnabled, bool? StandardFlowEnabled, Dictionary<string, string>? Attributes);
    private sealed record User(string Id, string Username, string? FirstName, string? LastName, bool Enabled, string? ServiceAccountClientId);
    private sealed record Group(string Id, string Name);
}
