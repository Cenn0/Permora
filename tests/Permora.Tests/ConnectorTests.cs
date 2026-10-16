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
    [Fact] public void Stable_directory_identifiers_separate_realm_and_kind()
    { Assert.NotEqual(KeycloakConnector.StableId("one", "user", "123"), KeycloakConnector.StableId("two", "user", "123")); Assert.NotEqual(KeycloakConnector.StableId("one", "user", "123"), KeycloakConnector.StableId("one", "group", "123")); }
    private static HttpResponseMessage SafeClient(Fixture f) => Json(new { id = f.App.KeycloakClientUuid, clientId = f.App.KeycloakClientId, protocol = "openid-connect", enabled = true, fullScopeAllowed = false, serviceAccountsEnabled = false, implicitFlowEnabled = false, directAccessGrantsEnabled = false, attributes = new Dictionary<string,string> { ["permora.application"] = f.App.Id.ToString() } });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class ConnectorFixture : IDisposable
    {
        private readonly string secretPath = Path.GetTempFileName(); private readonly HttpClient client;
        public RecordingHandler Handler { get; }
        public KeycloakConnector Connector { get; }
        public ConnectorFixture(Fixture f, Func<HttpRequestMessage,CancellationToken,HttpResponseMessage> handler)
        {
            File.WriteAllText(secretPath, "test-only-credential-not-a-real-secret"); Handler = new(handler); client = new(Handler);
            var credential = new ServiceCredential("test-service", secretPath);
            var options = new KeycloakOptions { Realms = new() { ["enterprise"] = new("https://keycloak.example.test", "enterprise", Fixture.Issuer, credential, credential, credential, []) } };
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
