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
        Assert.Equal([app.Assignments[1].Id], DirectoryGovernance.GroupRevocationsForSeparation(app, [f.OwnerPrincipal, group]));
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
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [groupA, groupB]));
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
        Assert.Equal(app.Assignments.Select(a => a.Id).Order(), DirectoryGovernance.GroupRevocationsForSeparation(app, [groupA, groupB]));
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
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [f.OwnerPrincipal, group]));
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
        Assert.Empty(DirectoryGovernance.GroupRevocationsForSeparation(app, [f.OwnerPrincipal, group]));
    }
}
