namespace Permora.Tests;

public sealed class DirectoryGovernanceTests
{
    [Fact]
    public void External_group_membership_change_queues_only_the_conflicting_group_grant()
    {
        var f = new Fixture();
        var group = f.Employee with
        {
            Kind = PrincipalKind.Group, MembershipComplete = true,
            EffectiveMemberKeys = [f.Owner.Key]
        };
        var app = f.App with
        {
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Approval conflict")] },
            Assignments =
            [
                f.Assignment(f.Reader.Id, f.OwnerPrincipal.Id),
                f.Assignment(f.Supervisor.Id, group.Id)
            ]
        };
        Assert.Equal([app.Assignments[1].Id], DirectoryGovernance.GroupRevocationsForSeparation(app, [f.OwnerPrincipal, group], f.Clock.Now));
    }

    [Fact]
    public void Two_non_overlapping_groups_do_not_trigger_remediation()
    {
        var f = new Fixture();
        var groupA = f.Employee with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Owner.Key] };
        var groupB = f.OwnerPrincipal with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Approver.Key] };
        var app = f.App with
        {
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] },
            Assignments = [f.Assignment(f.Reader.Id, groupA.Id), f.Assignment(f.Supervisor.Id, groupB.Id)]
        };
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [groupA, groupB], f.Clock.Now));
    }

    [Fact]
    public void Cross_group_conflict_revokes_both_group_assignments()
    {
        var f = new Fixture();
        var groupA = f.Employee with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Owner.Key] };
        var groupB = f.OwnerPrincipal with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Owner.Key] };
        var app = f.App with
        {
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] },
            Assignments = [f.Assignment(f.Reader.Id, groupA.Id), f.Assignment(f.Supervisor.Id, groupB.Id)]
        };
        Assert.Equal(app.Assignments.Select(a => a.Id).Order(), DirectoryGovernance.GroupRevocationsForSeparation(app, [groupA, groupB], f.Clock.Now));
    }

    [Fact]
    public void Incomplete_group_snapshot_does_not_authorize_a_guess_based_revocation()
    {
        var f = new Fixture();
        var group = f.Employee with
        {
            Kind = PrincipalKind.Group, MembershipComplete = false,
            EffectiveMemberKeys = [f.Owner.Key]
        };
        var app = f.App with
        {
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] },
            Assignments = [f.Assignment(f.Reader.Id, f.OwnerPrincipal.Id), f.Assignment(f.Supervisor.Id, group.Id)]
        };
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [f.OwnerPrincipal, group], f.Clock.Now));
    }

    [Fact]
    public void Directory_scope_prevents_cross_realm_group_matching()
    {
        var f = new Fixture();
        var group = f.Employee with
        {
            RealmKey = "other", Kind = PrincipalKind.Group,
            EffectiveMemberKeys = [f.Owner.Key]
        };
        var app = f.App with
        {
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] },
            Assignments = [f.Assignment(f.Reader.Id, f.OwnerPrincipal.Id), f.Assignment(f.Supervisor.Id, group.Id)]
        };
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [f.OwnerPrincipal, group], f.Clock.Now));
    }
    [Fact]
    public void Incomplete_assigned_group_must_be_reported_as_unverifiable()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group, MembershipComplete = false };
        var grant = f.Assignment(principal: group.Id);
        var app = f.App with { Assignments = [grant] };

        Assert.Equal([grant.Id], DirectoryGovernance.UnverifiableGroupAssignments(app, [group], f.Clock.Now));
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [group], f.Clock.Now));
    }

    [Fact]
    public void Stale_or_future_dated_group_snapshot_is_not_certifiable()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group, MembershipComplete = true };
        var grant = f.Assignment(principal: group.Id);
        var app = f.App with { Assignments = [grant] };

        Assert.Empty(DirectoryGovernance.UnverifiableGroupAssignments(app, [group], f.Clock.Now));
        Assert.Equal([grant.Id], DirectoryGovernance.UnverifiableGroupAssignments(app,
            [group with { SyncedAt = f.Clock.Now.AddMinutes(-16) }], f.Clock.Now));
        Assert.Equal([grant.Id], DirectoryGovernance.UnverifiableGroupAssignments(app,
            [group with { SyncedAt = f.Clock.Now.AddMinutes(2) }], f.Clock.Now));
    }

    [Fact]
    public void Unverified_group_detection_does_not_trust_a_different_realm()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group, MembershipComplete = false,
            RealmKey = "external-realm" };
        var app = f.App with { Assignments = [f.Assignment(principal: group.Id)] };
        // Cross-realm principals are never considered valid group snapshots. Connector
        // reconciliation reports the missing in-realm identity independently.
        Assert.Empty(DirectoryGovernance.UnverifiableGroupAssignments(app, [group], f.Clock.Now));
    }


    [Fact]
    public void External_membership_churn_recomputes_revocations_without_sticky_decisions()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Approver.Key] };
        var app = f.App with
        {
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] },
            Assignments = [f.Assignment(f.Reader.Id, f.OwnerPrincipal.Id), f.Assignment(f.Supervisor.Id, group.Id)]
        };
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [f.OwnerPrincipal, group], f.Clock.Now));
        var joined = group with { EffectiveMemberKeys = [f.Approver.Key, f.Owner.Key] };
        Assert.Equal([app.Assignments[1].Id], DirectoryGovernance.GroupRevocationsForSeparation(
            app, [f.OwnerPrincipal, joined], f.Clock.Now));
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [f.OwnerPrincipal, group], f.Clock.Now));
    }

    [Fact]
    public void Duplicate_assigned_group_is_an_incident_not_a_basis_for_automatic_revocation()
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Owner.Key] };
        var divergent = group with { KeycloakId = "other-remote-group", EffectiveMemberKeys = [] };
        var grant = f.Assignment(f.Supervisor.Id, group.Id);
        var app = f.App with
        {
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] },
            Assignments = [f.Assignment(f.Reader.Id, f.OwnerPrincipal.Id), grant]
        };
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(
            app, [f.OwnerPrincipal, group, divergent], f.Clock.Now));
        Assert.Equal([grant.Id], DirectoryGovernance.UnverifiableGroupAssignments(
            app, [f.OwnerPrincipal, group, divergent], f.Clock.Now));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("disabled")]
    [InlineData("protected")]
    [InlineData("missing-remote-id")]
    public void Unsafe_group_snapshot_is_unverifiable_and_does_not_trigger_automatic_revocation(string scenario)
    {
        var f = new Fixture();
        var group = f.Employee with { Kind = PrincipalKind.Group, EffectiveMemberKeys = [f.Owner.Key] };
        group = scenario switch
        {
            "stale" => group with { SyncedAt = f.Clock.Now.AddHours(-1) },
            "disabled" => group with { Enabled = false },
            "protected" => group with { Protected = true },
            "missing-remote-id" => group with { KeycloakId = "" },
            _ => throw new InvalidOperationException()
        };
        var grant = f.Assignment(f.Supervisor.Id, group.Id);
        var app = f.App with
        {
            Policy = f.App.Policy with { SeparationRules = [new(f.Reader.Id, f.Supervisor.Id, "Conflict")] },
            Assignments = [f.Assignment(f.Reader.Id, f.OwnerPrincipal.Id), grant]
        };
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(
            app, [f.OwnerPrincipal, group], f.Clock.Now));
        Assert.Equal([grant.Id], DirectoryGovernance.UnverifiableGroupAssignments(
            app, [f.OwnerPrincipal, group], f.Clock.Now));
    }

}
