namespace Permora.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData("orders.read")][InlineData("orders.approve")][InlineData("warehouse-count.read")][InlineData("read2")]
    public void Technical_keys_accept_application_names(string key) => Policy.Key(key);
    [Theory]
    [InlineData("")][InlineData("a")][InlineData("realm-admin")][InlineData("permora.owner")][InlineData("role.admin")]
    [InlineData("perm.read")][InlineData("../other")][InlineData("Orders.Read")][InlineData("orders..read")][InlineData("orders/read")]
    public void Technical_keys_reject_reserved_and_unsafe_values(string key) => Assert.Throws<DomainException>(() => Policy.Key(key));
    [Fact] public void Foreign_team_cannot_read_application()
    { var f = new Fixture(); var error = Assert.Throws<DomainException>(() => Policy.RequireVisible(f.Outsider, f.Team, f.Admins)); Assert.Equal(404, error.Status); }
    [Fact] public void Foreign_team_cannot_write_application()
    { var f = new Fixture(); Assert.Equal("not_found", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), actor: f.Outsider)).Code); }
    [Theory][InlineData(ManagementRole.Auditor)][InlineData(ManagementRole.Approver)]
    public void Read_or_approval_mandates_do_not_allow_definitions(ManagementRole role)
    { var f = new Fixture(); var actor = role == ManagementRole.Auditor ? f.Auditor : f.Approver; Assert.Equal("forbidden", Assert.Throws<DomainException>(() => f.Plan(NewPermission(), actor: actor)).Code); }
    [Fact] public void Maintainer_cannot_assign_access()
    { var f = new Fixture(); Assert.Equal("forbidden", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), actor: f.Maintainer)).Code); }
    [Fact] public void Access_manager_cannot_define_roles()
    { var f = new Fixture(); Assert.Equal("forbidden", Assert.Throws<DomainException>(() => f.Plan(NewPermission(), actor: f.AccessManager)).Code); }
    [Fact] public void New_permission_is_high_risk_even_when_client_requests_standard()
    { var f = new Fixture(); var plan = f.Plan(NewPermission()); Assert.Equal(Risk.High, plan.Next.Permissions[^1].Risk); Assert.True(plan.ApprovalRequired); Assert.Equal(2, plan.Next.Revision); Assert.Equal(2, f.App.Permissions.Count); }
    [Fact] public void Duplicate_permission_is_rejected()
    { var f = new Fixture(); Assert.Equal("already_exists", Assert.Throws<DomainException>(() => f.Plan(NewPermission() with { Key = f.Read.Key })).Code); }
    [Fact] public void Permission_risk_cannot_be_lowered_by_owner()
    { var f = new Fixture(); Assert.Equal("risk_downgrade", Assert.Throws<DomainException>(() => f.Plan(new Change { Kind = ChangeKind.UpdatePermission, TargetId = f.Approve.Id, Name = "Approve", Risk = Risk.Standard, Reason = "Reclassify" })).Code); }
    [Fact] public void Central_iam_may_reclassify_but_still_requires_approval()
    { var f = new Fixture(); var plan = f.Plan(new Change { Kind = ChangeKind.UpdatePermission, TargetId = f.Approve.Id, Name = "Approve", Risk = Risk.Standard, Reason = "Reclassify" }, admins: new HashSet<string> { f.Owner.Key }); Assert.Equal(Risk.Standard, plan.Next.Permissions.Single(p => p.Id == f.Approve.Id).Risk); Assert.True(plan.ApprovalRequired); }
    [Fact] public void Foreign_permission_cannot_be_added_to_role()
    { var f = new Fixture(); Assert.Equal("not_found", Assert.Throws<DomainException>(() => f.Plan(Role(f, Guid.NewGuid()))).Code); }
    [Fact] public void Role_id_cannot_be_used_as_permission_or_cycle()
    { var f = new Fixture(); Assert.Equal("not_found", Assert.Throws<DomainException>(() => f.Plan(Role(f, f.Reader.Id))).Code); }
    [Fact] public void Duplicate_composite_children_are_rejected()
    { var f = new Fixture(); Assert.Equal("invalid_permissions", Assert.Throws<DomainException>(() => f.Plan(Role(f, f.Read.Id) with { PermissionIds = [f.Read.Id, f.Read.Id] })).Code); }
    [Fact] public void Assigned_role_cannot_be_deleted()
    { var f = new Fixture(); var app = f.App with { Assignments = [f.Assignment()] }; Assert.Equal("role_in_use", Assert.Throws<DomainException>(() => f.Plan(new Change { Kind = ChangeKind.DeleteRole, TargetId = f.Reader.Id, Reason = "Remove role" }, app)).Code); }
    [Fact] public void Permission_in_a_role_cannot_be_deleted()
    { var f = new Fixture(); Assert.Equal("permission_in_use", Assert.Throws<DomainException>(() => f.Plan(new Change { Kind = ChangeKind.DeletePermission, TargetId = f.Read.Id, Reason = "Remove permission" })).Code); }
    [Theory][InlineData(ApplicationStatus.AwaitingIam)][InlineData(ApplicationStatus.Suspended)][InlineData(ApplicationStatus.Retired)]
    public void Inactive_applications_reject_new_rights(ApplicationStatus status)
    { var f = new Fixture(); Assert.Equal("inactive_application", Assert.Throws<DomainException>(() => f.Plan(NewPermission(), f.App with { Status = status })).Code); }
    [Fact] public void Revocation_is_allowed_even_when_suspended()
    { var f = new Fixture(); var assignment = f.Assignment(); var app = f.App with { Status = ApplicationStatus.Suspended, Assignments = [assignment] }; var plan = f.Plan(new Change { Kind = ChangeKind.RevokeAccess, TargetId = assignment.Id, Reason = "Revoke access" }, app); Assert.Empty(plan.Next.Assignments); Assert.False(plan.ApprovalRequired); }
    [Fact] public void System_path_can_never_grant()
    { var f = new Fixture(); Assert.Equal("invalid_system_operation", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), system: true)).Code); }
    [Fact] public void Protected_identity_is_rejected()
    { var f = new Fixture(); Assert.Equal("protected_identity", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), directory: [f.Employee with { Protected = true }])).Code); }
    [Fact] public void Disabled_identity_is_rejected()
    { var f = new Fixture(); Assert.Equal("protected_identity", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), directory: [f.Employee with { Enabled = false }])).Code); }
    [Fact] public void Stale_identity_is_rejected()
    { var f = new Fixture(); Assert.Equal("stale_identity", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), directory: [f.Employee with { SyncedAt = f.Clock.Now.AddMinutes(-16) }])).Code); }
    [Fact] public void Future_dated_directory_record_is_rejected()
    { var f = new Fixture(); Assert.Equal("stale_identity", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), directory: [f.Employee with { SyncedAt = f.Clock.Now.AddMinutes(2) }])).Code); }
    [Fact] public void Duplicate_directory_identifiers_are_rejected_before_queueing_a_grant()
    {
        var f = new Fixture();
        var duplicate = f.Employee with { KeycloakId = "different-keycloak-identity", Kind = PrincipalKind.Group };
        var error = Assert.Throws<DomainException>(() => f.Plan(f.Grant(), directory: [f.Employee, duplicate]));
        Assert.Equal("ambiguous_directory_identity", error.Code);
        Assert.Equal(409, error.Status);
    }
    [Fact]
    public void Invisible_existing_group_cannot_hide_a_separation_conflict()
    {
        var f = new Fixture();
        // A group grant may remain effective in Keycloak although the directory
        // snapshot omitted the group. Its members are unknown, not empty.
        var hiddenGroup = f.OwnerPrincipal with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Employee.SubjectKey] };
        var app = f.App with
        {
            Assignments = [f.Assignment(f.Reader.Id, hiddenGroup.Id)],
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Isolation")] }
        };
        var error = Assert.Throws<DomainException>(() =>
            f.Plan(f.Grant(f.Supervisor.Id, f.Employee.Id), app, directory: [f.Employee]));
        Assert.Equal("directory_incomplete", error.Code);
        Assert.Equal(409, error.Status);
    }

    [Fact]
    public void Existing_assignment_in_a_foreign_realm_does_not_count_as_visible()
    {
        var f = new Fixture();
        var foreign = f.Employee with { RealmKey = "external", Kind = PrincipalKind.Group };
        var app = f.App with { Assignments = [f.Assignment(principal: foreign.Id)] };
        var error = Assert.Throws<DomainException>(() =>
            f.Plan(f.Grant(principal: f.OwnerPrincipal.Id), app,
                directory: [foreign, f.OwnerPrincipal]));
        Assert.Equal("directory_incomplete", error.Code);
    }

    [Fact]
    public void Duplicate_existing_principals_are_not_silently_deduplicated()
    {
        var f = new Fixture();
        var app = f.App with { Assignments = [f.Assignment(principal: f.Employee.Id)] };
        var duplicate = f.Employee with { KeycloakId = "ambiguous-other-remote-id", Kind = PrincipalKind.Group };
        var error = Assert.Throws<DomainException>(() =>
            f.Plan(f.Grant(principal: f.OwnerPrincipal.Id), app,
                directory: [f.Employee, duplicate, f.OwnerPrincipal]));
        Assert.Equal("ambiguous_directory_identity", error.Code);
    }

    [Fact]
    public void Stale_existing_group_blocks_new_grants_even_to_a_different_principal()
    {
        var f = new Fixture();
        var staleGroup = f.Employee with { Kind = PrincipalKind.Group, SyncedAt = f.Clock.Now.AddMinutes(-16) };
        var app = f.App with { Assignments = [f.Assignment(principal: staleGroup.Id)] };
        var error = Assert.Throws<DomainException>(() =>
            f.Plan(f.Grant(principal: f.OwnerPrincipal.Id), app,
                directory: [staleGroup, f.OwnerPrincipal]));
        Assert.Equal("stale_identity", error.Code);
    }

    [Fact]
    public void Invalid_or_disabled_existing_assignment_cannot_be_used_to_extend_access()
    {
        var f = new Fixture();
        var app = f.App with { Assignments = [f.Assignment(principal: f.Employee.Id)] };
        var invalid = f.Employee with { Kind = (PrincipalKind)999 };
        Assert.Equal("invalid_directory_identity", Assert.Throws<DomainException>(() =>
            f.Plan(f.Grant(principal: f.OwnerPrincipal.Id), app,
                directory: [invalid, f.OwnerPrincipal])).Code);
        var disabled = f.Employee with { Enabled = false };
        Assert.Equal("ineligible_existing_identity", Assert.Throws<DomainException>(() =>
            f.Plan(f.Grant(principal: f.OwnerPrincipal.Id), app,
                directory: [disabled, f.OwnerPrincipal])).Code);
    }

    [Fact]
    public void Unverified_prior_mapping_does_not_block_an_emergency_revocation()
    {
        var f = new Fixture();
        var assignment = f.Assignment();
        var app = f.App with { Assignments = [assignment] };
        var revoke = f.Plan(new Change { Kind = ChangeKind.RevokeAccess, TargetId = assignment.Id,
            Reason = "Remove access despite incomplete directory" }, app, directory: []);
        Assert.Empty(revoke.Next.Assignments);
    }

    [Fact]
    public void Privileged_role_edit_cannot_ignore_an_unseen_existing_assignment()
    {
        var f = new Fixture();
        var app = f.App with { Assignments = [f.Assignment(principal: f.Employee.Id)] };
        var update = new Change { Kind = ChangeKind.UpdateRole,
            TargetId = f.Reader.Id, Name = "Reader", Description = "Elevated",
            PermissionIds = [f.Read.Id, f.Approve.Id], Reason = "Elevate role" };
        Assert.Equal("directory_incomplete", Assert.Throws<DomainException>(() =>
            f.Plan(update, app, directory: [f.OwnerPrincipal])).Code);
    }

    [Fact]
    public void Complete_existing_directory_allows_nonconflicting_access()
    {
        var f = new Fixture();
        var app = f.App with { Assignments = [f.Assignment(principal: f.Employee.Id)] };
        var plan = f.Plan(f.Grant(principal: f.OwnerPrincipal.Id), app, directory: f.Directory);
        Assert.Equal(2, plan.Next.Assignments.Count);
        Assert.True(plan.ApprovalRequired);
    }

    [Fact] public void Cross_realm_target_is_rejected()
    { var f = new Fixture(); Assert.Equal("not_found", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), directory: [f.Employee with { RealmKey = "foreign" }])).Code); }
    [Fact] public void High_risk_self_assignment_is_rejected()
    { var f = new Fixture(); Assert.Equal("self_assignment", Assert.Throws<DomainException>(() => f.Plan(f.Grant(f.Supervisor.Id, f.OwnerPrincipal.Id))).Code); }
    [Fact] public void Group_self_assignment_is_rejected()
    { var f = new Fixture(); var group = f.Employee with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Owner.Key] }; Assert.Equal("self_assignment", Assert.Throws<DomainException>(() => f.Plan(f.Grant(f.Supervisor.Id), directory: [group])).Code); }
    [Fact] public void Incomplete_group_membership_is_rejected()
    { var f = new Fixture(); var group = f.Employee with { Kind = PrincipalKind.Group, MembershipComplete = false }; Assert.Equal("incomplete_group", Assert.Throws<DomainException>(() => f.Plan(f.Grant(), directory: [group])).Code); }
    [Fact] public void No_expiration_is_rejected()
    { var f = new Fixture(); Assert.Equal("expiry_required", Assert.Throws<DomainException>(() => f.Plan(f.Grant() with { ExpiresAt = null })).Code); }
    [Theory][InlineData(-1)][InlineData(0)][InlineData(91)]
    public void Invalid_expiration_is_rejected(int days)
    { var f = new Fixture(); Assert.Equal("invalid_expiry", Assert.Throws<DomainException>(() => f.Plan(f.Grant() with { ExpiresAt = f.Clock.Now.AddDays(days) })).Code); }
    [Fact] public void Separation_conflict_includes_expired_not_yet_revoked_access()
    { var f = new Fixture(); var assignment = f.Assignment() with { ExpiresAt = f.Clock.Now.AddMinutes(-1) }; var app = f.App with { Assignments = [assignment], Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] } }; Assert.Equal("separation_of_duties", Assert.Throws<DomainException>(() => f.Plan(f.Grant(f.Supervisor.Id), app)).Code); }
    [Fact] public void Cannot_add_privileged_permission_to_own_effective_role()
    { var f = new Fixture(); var app = f.App with { Assignments = [f.Assignment(principal: f.OwnerPrincipal.Id)] }; Assert.Equal("self_escalation", Assert.Throws<DomainException>(() => f.Plan(Role(f, f.Approve.Id) with { Kind = ChangeKind.UpdateRole, TargetId = f.Reader.Id }, app)).Code); }
    [Fact] public void Stale_group_cannot_bypass_role_edit_self_check()
    { var f = new Fixture(); var group = f.OwnerPrincipal with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [], SyncedAt = f.Clock.Now.AddHours(-1) }; var app = f.App with { Assignments = [f.Assignment(principal: group.Id)] }; Assert.Equal("stale_identity", Assert.Throws<DomainException>(() => f.Plan(Role(f, f.Approve.Id) with { Kind = ChangeKind.UpdateRole, TargetId = f.Reader.Id }, app, directory: [group])).Code); }
    [Fact] public void Self_approval_is_rejected_even_for_platform_admin()
    { var f = new Fixture(); Assert.Equal("self_approval", Assert.Throws<DomainException>(() => Policy.RequireApproval(f.Owner, f.Owner, f.Team, new HashSet<string> { f.Owner.Key })).Code); }
    [Fact] public void Second_approver_is_accepted()
    { var f = new Fixture(); Policy.RequireApproval(f.Approver, f.Owner, f.Team, f.Admins); }
    [Fact] public void Empty_group_cannot_accumulate_conflicting_roles_for_future_members()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [] };
        var app = f.App with { Assignments = [f.Assignment()], Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] } };
        Assert.Equal("separation_of_duties", Assert.Throws<DomainException>(() => f.Plan(f.Grant(f.Supervisor.Id), app, directory: [group])).Code);
    }
    private static Change NewPermission() => new() { Kind = ChangeKind.CreatePermission, TargetId = Guid.NewGuid(), Key = "orders.cancel", Name = "Cancel orders", Risk = Risk.Standard, Reason = "Introduce cancellation" };
    private static Change Role(Fixture f, Guid id) => new() { Kind = ChangeKind.CreateRole, TargetId = Guid.NewGuid(), Key = "new-role", Name = "New role", PermissionIds = [id], Reason = "Role change" };
}
