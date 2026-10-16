using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Permora.Authorization;

namespace Permora.Tests;

public sealed class AuditAndAuthorizationTests
{
    [Fact] public void Audit_chain_detects_changed_content()
    { var f = new Fixture(); var first = AuditChain.Create(f.App.Id, f.Owner.Key, "created", "Original", f.Clock.Now, null); var second = AuditChain.Create(f.App.Id, f.Owner.Key, "changed", "Original", f.Clock.Now, first); Assert.True(AuditChain.Verify([first, second])); Assert.False(AuditChain.Verify([first with { Detail = "Forged" }, second])); }
    [Fact] public void Audit_chain_detects_removed_or_reordered_events()
    { var f = new Fixture(); var a = AuditChain.Create(f.App.Id, "actor", "created", "A", f.Clock.Now, null); var b = AuditChain.Create(f.App.Id, "actor", "updated", "B", f.Clock.Now, a); Assert.False(AuditChain.Verify([b])); Assert.False(AuditChain.Verify([b, a])); Assert.True(AuditChain.Verify([b], a.Hash, a.Sequence)); }
    [Fact] public void Audit_chain_cannot_mix_applications()
    { var f = new Fixture(); var a = AuditChain.Create(f.App.Id, "actor", "create", "A", f.Clock.Now, null); var b = AuditChain.Create(Guid.NewGuid(), "actor", "create", "B", f.Clock.Now, a); Assert.False(AuditChain.Verify([a, b])); }
    [Fact] public void Subject_identifier_is_issuer_bound_and_unambiguous()
    { Assert.NotEqual(Wire.Subject("https://a", "user"), Wire.Subject("https://b", "user")); Assert.Equal(Wire.Subject("https://a/", "user"), Wire.Subject("https://a", "user")); Assert.NotEqual(Wire.Subject("https://a|b", "c"), Wire.Subject("https://a", "b|c")); }
    [Theory]
    [InlineData("{\"kind\":\"CreateRole\",\"reason\":\"Testing\",\"systemRevocation\":true}")]
    [InlineData("{\"kind\":999}")]
    [InlineData("{\"kind\":\"GrantAccess\",\"permissionIds\":null}")]
    public void Unknown_admin_fields_invalid_enums_and_null_collections_are_rejected(string value)
    { Assert.Throws<JsonException>(() => Wire.Deserialize<Change>(value)); }
    [Fact] public void Serialized_application_roundtrips_without_loss()
    { var f = new Fixture(); var json = Wire.Serialize(f.App); Assert.Equal(json, Wire.Serialize(Wire.Deserialize<ApplicationDocument>(json))); }
    [Theory]
    [InlineData("production-api", "{\"production-api\":{\"roles\":[\"perm.orders.read\"]}}", true)]
    [InlineData("foreign-api", "{\"production-api\":{\"roles\":[\"perm.orders.read\"]}}", false)]
    [InlineData("production-api", "{\"production-api\":{\"roles\":[\"role.reader\"]}}", false)]
    [InlineData("production-api", "{\"production-api\":{\"roles\":[\"perm.orders.read.all\"]}}", false)]
    [InlineData("production-api", "{\"production-api\":{\"roles\":\"perm.orders.read\"}}", false)]
    [InlineData("production-api", "not-json", false)]
    public async Task Sdk_requires_the_exact_client_and_atomic_permission(string client, string claim, bool expected)
    {
        var requirement = new PermissionRequirement(client, "orders.read");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new("resource_access", claim)], "validated-jwt"));
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await new PermissionHandler().HandleAsync(context); Assert.Equal(expected, context.HasSucceeded);
    }
    [Fact] public async Task Unauthenticated_identity_is_denied_even_with_role_claim()
    { var requirement = new PermissionRequirement("production-api", "orders.read"); var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity([new("resource_access", "{\"production-api\":{\"roles\":[\"perm.orders.read\"]}}")])), null); await new PermissionHandler().HandleAsync(context); Assert.False(context.HasSucceeded); }
}
