using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Permora.Infrastructure;

namespace Permora.Tests;

public sealed class ConnectorTests
{
    [Fact] public async Task Disabled_or_foreign_client_is_rejected_without_writes()
    {
        var f = new Fixture(); using var fixture = new ConnectorFixture(f, (request, _) =>
            Json(new { id = f.App.KeycloakClientUuid, clientId = "foreign-api", enabled = true, protocol = "openid-connect", fullScopeAllowed = false, serviceAccountsEnabled = false, attributes = new Dictionary<string,string> { ["permora.application"] = f.App.Id.ToString() } }));
        Assert.Equal("unsafe_client", (await Assert.ThrowsAsync<DomainException>(() => fixture.Connector.VerifyBindingAsync(f.App, TestContext.Current.CancellationToken))).Code);
        Assert.DoesNotContain(fixture.Handler.Paths, p => p.Method != HttpMethod.Get && !p.Path.EndsWith("/token", StringComparison.Ordinal));
    }
    [Fact] public async Task Roles_with_foreign_owner_are_never_overwritten()
    {
        var f = new Fixture(); using var fixture = new ConnectorFixture(f, (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
            ? SafeClient(f) : Json(new { id = Guid.NewGuid().ToString(), name = "perm.orders.read", description = "Old", clientRole = true, containerId = f.App.KeycloakClientUuid, composite = false, attributes = new Dictionary<string,string[]> { ["permora.application"] = [Guid.NewGuid().ToString()], ["permora.entity"] = [f.Read.Id.ToString()] } }));
        var change = new Change { Kind = ChangeKind.UpdatePermission, TargetId = f.Read.Id, Name = "Changed", Reason = "Test" };
        Assert.Equal("role_ownership_conflict", (await Assert.ThrowsAsync<DomainException>(() => fixture.Connector.ApplyAsync(f.App, f.App, change, f.Directory, TestContext.Current.CancellationToken))).Code);
        Assert.DoesNotContain(fixture.Handler.Paths, p => p.Method == HttpMethod.Put);
    }
    [Fact] public async Task Privilege_bearing_composite_permission_is_rejected()
    {
        var f = new Fixture(); using var fixture = new ConnectorFixture(f, (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal) ? SafeClient(f) : Json(new
        { id = Guid.NewGuid().ToString(), name = "perm.orders.read", description = "Old", clientRole = true, containerId = f.App.KeycloakClientUuid, composite = true, attributes = new Dictionary<string,string[]> { ["permora.application"] = [f.App.Id.ToString()], ["permora.entity"] = [f.Read.Id.ToString()] } }));
        var change = new Change { Kind = ChangeKind.UpdatePermission, TargetId = f.Read.Id, Name = "Changed", Reason = "Test" };
        Assert.Equal("unsafe_permission_composite", (await Assert.ThrowsAsync<DomainException>(() => fixture.Connector.ApplyAsync(f.App, f.App, change, f.Directory, TestContext.Current.CancellationToken))).Code);
        Assert.DoesNotContain(fixture.Handler.Paths, p => p.Method == HttpMethod.Put);
    }
    [Fact] public async Task Upstream_forbidden_body_is_not_exposed()
    {
        var f = new Fixture(); using var fixture = new ConnectorFixture(f, (_, _) => new(HttpStatusCode.Forbidden) { Content = new StringContent("private-secret-test-marker") });
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Connector.VerifyBindingAsync(f.App, TestContext.Current.CancellationToken));
        Assert.Equal("keycloak_permission_denied", error.Code); Assert.DoesNotContain("private-secret-test-marker", error.Message);
    }
    [Fact] public async Task Missing_deleted_role_is_an_idempotent_success()
    {
        var f = new Fixture(); using var fixture = new ConnectorFixture(f, (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal) ? SafeClient(f) : new(HttpStatusCode.NotFound));
        await fixture.Connector.ApplyAsync(f.App, f.App with { Roles = [f.Supervisor] }, new Change { Kind = ChangeKind.DeleteRole, TargetId = f.Reader.Id, Reason = "Delete" }, f.Directory, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Handler.Paths, p => p.Method == HttpMethod.Delete);
    }
    [Fact] public async Task Deleting_a_role_with_out_of_band_mappings_is_denied()
    {
        var f = new Fixture(); var roleId = Guid.NewGuid().ToString();
        using var fixture = new ConnectorFixture(f, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)) return SafeClient(f);
            if (path.EndsWith("/role.reader/users", StringComparison.Ordinal))
                return Json(new[] { new { id = "foreign-user", username = "foreign", enabled = true } });
            if (path.EndsWith("/role.reader", StringComparison.Ordinal))
                return Json(new { id = roleId, name = "role.reader", clientRole = true,
                    containerId = f.App.KeycloakClientUuid, composite = false,
                    attributes = new Dictionary<string, string[]> {
                        ["permora.application"] = [f.App.Id.ToString()],
                        ["permora.entity"] = [f.Reader.Id.ToString()] } });
            return Json(Array.Empty<object>());
        });
        var change = new Change { Kind = ChangeKind.DeleteRole, TargetId = f.Reader.Id, Reason = "Retire role" };
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Connector.ApplyAsync(f.App,
            f.App with { Roles = [f.Supervisor] }, change, f.Directory, TestContext.Current.CancellationToken));
        Assert.Equal("role_still_assigned", error.Code);
        Assert.DoesNotContain(fixture.Handler.Paths, p => p.Method == HttpMethod.Delete);
    }
    [Fact] public async Task Delegated_group_grant_is_disabled_by_default_without_remote_mapping_write()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group };
        var assignment = f.Assignment(principal: group.Id);
        var after = f.App with { Assignments = [assignment] };
        using var fixture = new ConnectorFixture(f, (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                ? SafeClient(f) : Json(Array.Empty<object>()));
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.Connector.ApplyAsync(f.App, after,
                new Change { Kind = ChangeKind.GrantAccess, TargetId = assignment.Id, RoleId = assignment.RoleId,
                    PrincipalId = assignment.PrincipalId, Reason = "Denied" },
                [group], TestContext.Current.CancellationToken));
        Assert.Equal("group_mapping_disabled", error.Code);
        Assert.Equal(403, error.Status);
        Assert.DoesNotContain(fixture.Handler.Paths,
            p => p.Method == HttpMethod.Post && p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Realm_opt_in_without_explicit_group_id_does_not_write_to_keycloak()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group };
        var assignment = f.Assignment(principal: group.Id);
        var after = f.App with { Assignments = [assignment] };
        using var fixture = new ConnectorFixture(f,
            (request, _) => request.RequestUri!.AbsolutePath.EndsWith(
                "/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                ? SafeClient(f) : Json(Array.Empty<object>()),
            allowGroupRoleGrants: true, approvedGroupIds: []);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.Connector.ApplyAsync(f.App, after,
                new Change { Kind = ChangeKind.GrantAccess, TargetId = assignment.Id,
                    RoleId = assignment.RoleId, PrincipalId = assignment.PrincipalId,
                    Reason = "Missing group allowlist" },
                [group], TestContext.Current.CancellationToken));
        Assert.Equal("group_mapping_disabled", error.Code);
        Assert.DoesNotContain(fixture.Handler.Paths,
            p => p.Method == HttpMethod.Post && p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unlisted_group_cannot_be_granted_even_when_realm_is_approved()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group };
        var assignment = f.Assignment(principal: group.Id);
        var after = f.App with { Assignments = [assignment] };
        using var fixture = new ConnectorFixture(f,
            (request, _) => request.RequestUri!.AbsolutePath.EndsWith(
                "/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                ? SafeClient(f) : Json(Array.Empty<object>()),
            allowGroupRoleGrants: true, approvedGroupIds: ["some-other-group"]);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.Connector.ApplyAsync(f.App, after,
                new Change { Kind = ChangeKind.GrantAccess, TargetId = assignment.Id,
                    RoleId = assignment.RoleId, PrincipalId = assignment.PrincipalId,
                    Reason = "Unlisted group" },
                [group], TestContext.Current.CancellationToken));
        Assert.Equal("group_mapping_disabled", error.Code);
        Assert.DoesNotContain(fixture.Handler.Paths,
            p => p.Method == HttpMethod.Post && p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exactly_allowlisted_group_can_be_granted_after_full_remote_postcondition_check()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [] };
        var assignment = f.Assignment(principal: group.Id);
        var after = f.App with { Assignments = [assignment] };
        var role = new { id = Guid.NewGuid().ToString(), name = "role.reader",
            clientRole = true, containerId = f.App.KeycloakClientUuid, composite = false,
            attributes = new Dictionary<string, string[]> {
                ["permora.application"] = [f.App.Id.ToString()],
                ["permora.entity"] = [f.Reader.Id.ToString()] } };
        var permission = new { id = Guid.NewGuid().ToString(), name = "perm.orders.read",
            clientRole = true, containerId = f.App.KeycloakClientUuid, composite = false,
            attributes = new Dictionary<string, string[]> {
                ["permora.application"] = [f.App.Id.ToString()],
                ["permora.entity"] = [f.Read.Id.ToString()] } };
        var mapped = false;
        using var fixture = new ConnectorFixture(f, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                && !path.Contains("/role-mappings/", StringComparison.Ordinal))
                return SafeClient(f);
            if (path.EndsWith("/roles/role.reader", StringComparison.Ordinal))
                return Json(role);
            if (path.EndsWith("/roles/perm.orders.read", StringComparison.Ordinal))
                return Json(permission);
            if (path.EndsWith("/roles/role.reader/composites", StringComparison.Ordinal))
                return Json(new[] { permission });
            if (path.EndsWith("/groups/" + group.KeycloakId + "/role-mappings/clients/" +
                f.App.KeycloakClientUuid, StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    mapped = true;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return Json(mapped ? new object[] { role } : Array.Empty<object>());
            }
            if (path.Contains("/groups/", StringComparison.Ordinal) &&
                (path.EndsWith("/members", StringComparison.Ordinal) ||
                 path.EndsWith("/children", StringComparison.Ordinal)))
                return Json(Array.Empty<object>());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, allowGroupRoleGrants: true, approvedGroupIds: [group.KeycloakId]);

        await fixture.Connector.ApplyAsync(f.App, after,
            new Change { Kind = ChangeKind.GrantAccess, TargetId = assignment.Id,
                RoleId = assignment.RoleId, PrincipalId = assignment.PrincipalId,
                Reason = "Approved group" }, [group], TestContext.Current.CancellationToken);
        Assert.True(mapped);
        Assert.Single(fixture.Handler.Paths, p => p.Method == HttpMethod.Post
            && p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
        Assert.Contains(fixture.Handler.Paths, p => p.Method == HttpMethod.Get
            && p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact] public async Task Emergency_group_revocation_remains_available_when_grants_are_disabled()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group };
        var assignment = f.Assignment(principal: group.Id);
        var before = f.App with { Assignments = [assignment] };
        var role = new { id = Guid.NewGuid().ToString(), name = "role.reader",
            clientRole = true, containerId = f.App.KeycloakClientUuid, composite = false,
            attributes = new Dictionary<string, string[]> {
                ["permora.application"] = [f.App.Id.ToString()],
                ["permora.entity"] = [f.Reader.Id.ToString()] } };
        var mapped = true;
        using var fixture = new ConnectorFixture(f, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                && !path.Contains("/role-mappings/", StringComparison.Ordinal))
                return SafeClient(f);
            if (path.EndsWith("/roles/role.reader", StringComparison.Ordinal))
                return Json(role);
            if (path.Contains("/groups/", StringComparison.Ordinal) && path.Contains("/role-mappings/", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Delete)
                {
                    mapped = false;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return Json(mapped ? new object[] { role } : Array.Empty<object>());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await fixture.Connector.ApplyAsync(before, f.App,
            new Change { Kind = ChangeKind.RevokeAccess, TargetId = assignment.Id, Reason = "Emergency" },
            [group], TestContext.Current.CancellationToken);
        Assert.False(mapped);
        Assert.Contains(fixture.Handler.Paths, p => p.Method == HttpMethod.Delete
            && p.Path.Contains("/groups/", StringComparison.Ordinal)
            && p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact] public async Task Unknown_principal_kind_cannot_bypass_group_grant_feature_gate()
    {
        var f = new Fixture();
        var unexpected = f.Employee with { Kind = (PrincipalKind)12345 };
        var assignment = f.Assignment(principal: unexpected.Id);
        var after = f.App with { Assignments = [assignment] };
        using var fixture = new ConnectorFixture(f, (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                ? SafeClient(f) : Json(Array.Empty<object>()));
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.Connector.ApplyAsync(f.App, after,
                new Change { Kind = ChangeKind.GrantAccess, TargetId = assignment.Id, RoleId = assignment.RoleId,
                    PrincipalId = assignment.PrincipalId, Reason = "Must fail closed" },
                [unexpected], TestContext.Current.CancellationToken));
        Assert.Equal("unsupported_principal_kind", error.Code);
        Assert.Equal(409, error.Status);
        Assert.DoesNotContain(fixture.Handler.Paths, p => p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact] public async Task Revocation_after_lost_remote_reply_is_idempotent_with_group_grants_disabled()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group };
        var assignment = f.Assignment(principal: group.Id);
        var before = f.App with { Assignments = [assignment] };
        var role = new { id = Guid.NewGuid().ToString(), name = "role.reader",
            clientRole = true, containerId = f.App.KeycloakClientUuid, composite = false,
            attributes = new Dictionary<string, string[]> {
                ["permora.application"] = [f.App.Id.ToString()],
                ["permora.entity"] = [f.Reader.Id.ToString()] } };
        var mapped = true;
        var deleteAttempts = 0;
        using var fixture = new ConnectorFixture(f, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                && !path.Contains("/role-mappings/", StringComparison.Ordinal))
                return SafeClient(f);
            if (path.EndsWith("/roles/role.reader", StringComparison.Ordinal))
                return Json(role);
            if (path.Contains("/groups/", StringComparison.Ordinal)
                && path.Contains("/role-mappings/", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Delete)
                {
                    ++deleteAttempts;
                    mapped = false; // Remote side committed but the HTTP reply was lost.
                    throw new HttpRequestException("Simulated lost remote reply after commit");
                }
                return Json(mapped ? new object[] { role } : Array.Empty<object>());
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var revoke = new Change { Kind = ChangeKind.RevokeAccess, TargetId = assignment.Id, Reason = "Retry after uncertainty" };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Connector.ApplyAsync(before, f.App, revoke, [group], TestContext.Current.CancellationToken));
        await fixture.Connector.ApplyAsync(before, f.App, revoke, [group], TestContext.Current.CancellationToken);
        Assert.False(mapped);
        Assert.Equal(1, deleteAttempts); // The retry relies on the remote read-back.
        Assert.DoesNotContain(fixture.Handler.Paths,
            p => p.Method == HttpMethod.Post && p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_directory_identity_blocks_remote_role_mapping_even_during_revocation()
    {
        var f = new Fixture();
        var assignment = f.Assignment();
        var before = f.App with { Assignments = [assignment] };
        var duplicate = f.Employee with { KeycloakId = "another-remote-identity", Kind = PrincipalKind.Group };
        using var fixture = new ConnectorFixture(f, (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                ? SafeClient(f) : Json(Array.Empty<object>()));
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            fixture.Connector.ApplyAsync(before, f.App,
                new Change { Kind = ChangeKind.RevokeAccess, TargetId = assignment.Id, Reason = "Ambiguous identity" },
                [f.Employee, duplicate], TestContext.Current.CancellationToken));
        Assert.Equal("ambiguous_directory_identity", error.Code);
        Assert.DoesNotContain(fixture.Handler.Paths,
            p => p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconciliation_reports_conflicting_directory_records_without_guessing_a_remote_resource()
    {
        var f = new Fixture();
        var assignment = f.Assignment();
        var app = f.App with { Assignments = [assignment] };
        var duplicate = f.Employee with { KeycloakId = "another-remote-identity", Kind = PrincipalKind.Group };
        using var fixture = new ConnectorFixture(f, (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                ? SafeClient(f) : Json(Array.Empty<object>()));
        var findings = await fixture.Connector.InspectAsync(app, [f.Employee, duplicate], TestContext.Current.CancellationToken);
        Assert.Contains(findings, finding => finding.Resource == assignment.Id.ToString()
            && finding.Code == "ambiguous_directory_identity");
        Assert.DoesNotContain(fixture.Handler.Paths,
            p => p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reconciliation_reports_invalid_kind_or_empty_remote_identity_without_guessing_a_group(bool emptyKeycloakId)
    {
        var f = new Fixture();
        var invalid = emptyKeycloakId
            ? f.Employee with { KeycloakId = "" }
            : f.Employee with { Kind = (PrincipalKind)32767 };
        var assignment = f.Assignment(principal: invalid.Id);
        var app = f.App with { Assignments = [assignment] };
        using var fixture = new ConnectorFixture(f, (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                ? SafeClient(f) : Json(Array.Empty<object>()));
        var findings = await fixture.Connector.InspectAsync(app, [invalid], TestContext.Current.CancellationToken);
        Assert.Contains(findings, finding => finding.Resource == assignment.Id.ToString()
            && finding.Code == "invalid_directory_identity");
        Assert.DoesNotContain(fixture.Handler.Paths,
            p => p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconciliation_reports_stale_directory_identity_instead_of_clean_access()
    {
        var f = new Fixture();
        var stale = f.Employee with { SyncedAt = f.Clock.Now.AddHours(-1) };
        var assignment = f.Assignment(principal: stale.Id);
        var app = f.App with { Assignments = [assignment] };
        using var fixture = new ConnectorFixture(f, (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal)
                ? SafeClient(f) : Json(Array.Empty<object>()));
        var findings = await fixture.Connector.InspectAsync(app, [stale], TestContext.Current.CancellationToken);
        Assert.Contains(findings, finding => finding.Resource == assignment.Id.ToString()
            && finding.Code == "stale_directory_identity");
        Assert.DoesNotContain(fixture.Handler.Paths,
            p => p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconciliation_reports_disabled_identity_even_if_the_role_is_remote_readable()
    {
        var f = new Fixture();
        var disabled = f.Employee with { Enabled = false };
        var assignment = f.Assignment(principal: disabled.Id);
        var app = f.App with { Assignments = [assignment] };
        var mappedRole = new { id = Guid.NewGuid().ToString(), name = "role.reader",
            clientRole = true, containerId = f.App.KeycloakClientUuid, composite = false,
            attributes = new Dictionary<string, string[]> {
                ["permora.application"] = [f.App.Id.ToString()],
                ["permora.entity"] = [f.Reader.Id.ToString()] } };
        using var fixture = new ConnectorFixture(f, (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            // Role-mapping URLs also end in /clients/{uuid}: dispatch them
            // before the application's client-metadata route.
            if (path.Contains("/role-mappings/", StringComparison.Ordinal))
                return Json(new[] { mappedRole });
            if (path.EndsWith("/clients/" + f.App.KeycloakClientUuid, StringComparison.Ordinal))
                return SafeClient(f);
            return Json(Array.Empty<object>());
        });
        var findings = await fixture.Connector.InspectAsync(app, [disabled], TestContext.Current.CancellationToken);
        Assert.Contains(findings, finding => finding.Resource == assignment.Id.ToString()
            && finding.Code == "ineligible_assigned_identity");
        Assert.Contains(fixture.Handler.Paths, p => p.Method == HttpMethod.Get
            && p.Path.Contains("/role-mappings/", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Handler.Paths, p => p.Method != HttpMethod.Get
            && !p.Path.EndsWith("/token", StringComparison.Ordinal));
    }

    [Fact] public void Stable_directory_identifiers_separate_realm_and_kind()
    { Assert.NotEqual(KeycloakConnector.StableId("one", "user", "123"), KeycloakConnector.StableId("two", "user", "123")); Assert.NotEqual(KeycloakConnector.StableId("one", "user", "123"), KeycloakConnector.StableId("one", "group", "123")); }
    private static HttpResponseMessage SafeClient(Fixture f) => Json(new { id = f.App.KeycloakClientUuid, clientId = f.App.KeycloakClientId, protocol = "openid-connect", enabled = true, fullScopeAllowed = false, serviceAccountsEnabled = false, implicitFlowEnabled = false,
        directAccessGrantsEnabled = false, standardFlowEnabled = false, attributes = new Dictionary<string,string> { ["permora.application"] = f.App.Id.ToString() } });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class ConnectorFixture : IDisposable
    {
        private readonly string secretPath = Path.GetTempFileName(); private readonly HttpClient client;
        public RecordingHandler Handler { get; }
        public KeycloakConnector Connector { get; }
        public ConnectorFixture(Fixture f, Func<HttpRequestMessage,CancellationToken,HttpResponseMessage> handler,
            bool allowGroupRoleGrants = false, IReadOnlyList<string>? approvedGroupIds = null)
        {
            File.WriteAllText(secretPath, "test-only-credential-not-a-real-secret"); Handler = new(handler); client = new(Handler);
            var credential = new ServiceCredential("test-service", secretPath);
            var options = new KeycloakOptions { Realms = new() { ["enterprise"] = new("https://keycloak.example.test", "enterprise", Fixture.Issuer, credential, credential, credential, [], allowGroupRoleGrants, approvedGroupIds) } };
            Connector = new(client, options, f.Clock);
        }
        public void Dispose() { client.Dispose(); File.Delete(secretPath); }
    }
    private sealed class RecordingHandler(Func<HttpRequestMessage,CancellationToken,HttpResponseMessage> handle) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add((request.Method, request.RequestUri!.AbsolutePath));
            if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return Task.FromResult(Json(new { access_token = "test-token-not-valid", expires_in = 60 }));
            return Task.FromResult(handle(request, cancellationToken));
        }
    }
}
