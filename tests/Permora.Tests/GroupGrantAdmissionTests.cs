namespace Permora.Tests;

public sealed class GroupGrantAdmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryStore StoreWithGroup(Fixture fixture)
    {
        var group = fixture.Employee with
        {
            Kind = PrincipalKind.Group,
            MembershipComplete = true,
            EffectiveMemberKeys = []
        };
        return new MemoryStore(fixture)
        {
            Directory = fixture.Directory.Select(p => p.Id == group.Id ? group : p).ToArray()
        };
    }

    [Fact]
    public async Task Unapproved_group_grant_is_rejected_before_approval_queue_or_audit()
    {
        var fixture = new Fixture();
        var store = StoreWithGroup(fixture);
        var service = new ChangeService(store, fixture.Admins, fixture.Clock);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.RequestAsync(fixture.App.Id, fixture.Owner, fixture.Grant(), 1,
                "group-preflight-denied-01", Ct));

        Assert.Equal("group_mapping_disabled", error.Code);
        Assert.Equal(403, error.Status);
        Assert.Empty(store.Operations);
        Assert.Empty(store.Audit);
        Assert.Empty(store.App.Assignments);
    }

    [Fact]
    public async Task Approval_for_another_realm_does_not_authorize_group_access()
    {
        var fixture = new Fixture();
        var store = StoreWithGroup(fixture);
        var policy = new GroupGrantPolicy(new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal) { ["other-realm"] = new HashSet<string>(StringComparer.Ordinal) { fixture.Employee.KeycloakId } });
        var service = new ChangeService(store, fixture.Admins, fixture.Clock, policy);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.RequestAsync(fixture.App.Id, fixture.Owner, fixture.Grant(), 1,
                "group-preflight-foreign", Ct));

        Assert.Equal("group_mapping_disabled", error.Code);
        Assert.Empty(store.Operations);
    }

    [Fact]
    public async Task Explicit_same_realm_approval_only_queues_the_request()
    {
        var fixture = new Fixture();
        var store = StoreWithGroup(fixture);
        var policy = new GroupGrantPolicy(new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal) { ["enterprise"] = new HashSet<string>(StringComparer.Ordinal) { fixture.Employee.KeycloakId } });
        var service = new ChangeService(store, fixture.Admins, fixture.Clock, policy);

        var operation = await service.RequestAsync(fixture.App.Id, fixture.Owner, fixture.Grant(), 1,
            "group-preflight-allowed", Ct);

        Assert.Equal(OperationStatus.AwaitingApproval, operation.Status);
        Assert.Single(store.Operations);
        Assert.Empty(store.App.Assignments); // Never equate queued/approved with a remote grant.
    }

    [Fact]
    public async Task Realm_wide_opt_in_without_group_allowlist_does_not_admit_a_grant()
    {
        var fixture = new Fixture();
        var store = StoreWithGroup(fixture);
        var policy = new GroupGrantPolicy(new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["enterprise"] = new HashSet<string>(StringComparer.Ordinal)
        });
        var service = new ChangeService(store, fixture.Admins, fixture.Clock, policy);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.RequestAsync(fixture.App.Id, fixture.Owner, fixture.Grant(), 1,
                "group-realm-without-id", Ct));
        Assert.Equal("group_mapping_disabled", error.Code);
        Assert.Empty(store.Operations);
    }

    [Fact]
    public async Task Another_allowed_group_id_does_not_authorize_this_group()
    {
        var fixture = new Fixture();
        var store = StoreWithGroup(fixture);
        var policy = new GroupGrantPolicy(new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["enterprise"] = new HashSet<string>(StringComparer.Ordinal) { "other-keycloak-group-id" }
        });
        var service = new ChangeService(store, fixture.Admins, fixture.Clock, policy);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.RequestAsync(fixture.App.Id, fixture.Owner, fixture.Grant(), 1,
                "group-foreign-id-denied", Ct));
        Assert.Equal("group_mapping_disabled", error.Code);
        Assert.Empty(store.Operations);
    }

    [Fact]
    public async Task Default_deny_does_not_block_urgent_group_revocation()
    {
        var fixture = new Fixture();
        var store = StoreWithGroup(fixture);
        var assignment = fixture.Assignment(principal: fixture.Employee.Id);
        store.App = fixture.App with { Assignments = [assignment] };
        var service = new ChangeService(store, fixture.Admins, fixture.Clock);

        var operation = await service.RequestAsync(fixture.App.Id, fixture.Owner,
            new Change { Kind = ChangeKind.RevokeAccess, TargetId = assignment.Id,
                Reason = "Emergency group revocation" }, 1, "group-revoke-allowed", Ct);

        Assert.Equal(OperationStatus.Queued, operation.Status);
        Assert.Single(store.Operations);
        Assert.Single(store.App.Assignments); // Applied only after the worker's Keycloak readback.
    }

    [Fact]
    public async Task Foreign_actor_cannot_probe_the_group_grant_policy()
    {
        var fixture = new Fixture();
        var store = StoreWithGroup(fixture);
        var service = new ChangeService(store, fixture.Admins, fixture.Clock);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            service.RequestAsync(fixture.App.Id, fixture.Outsider, fixture.Grant(), 1,
                "foreign-probe-group-gate", Ct));

        Assert.Equal("not_found", error.Code);
        Assert.Equal(404, error.Status);
        Assert.Empty(store.Operations);
    }

    [Fact]
    public void Unknown_directory_principal_kind_fails_before_group_policy()
    {
        var fixture = new Fixture();
        var malformed = fixture.Employee with { Kind = (PrincipalKind)1024 };
        var error = Assert.Throws<DomainException>(() =>
            fixture.Plan(fixture.Grant(), directory: [malformed]));

        Assert.Equal("unsupported_principal_kind", error.Code);
        Assert.Equal(409, error.Status);
    }
}
