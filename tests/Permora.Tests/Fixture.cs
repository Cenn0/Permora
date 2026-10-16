namespace Permora.Tests;

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class Fixture
{
    public const string Issuer = "https://sso.example.test/realms/enterprise";
    public ManualClock Clock { get; } = new();
    public Actor Owner { get; } = new(Wire.Subject(Issuer, "owner"), "Owner");
    public Actor Approver { get; } = new(Wire.Subject(Issuer, "approver"), "Approver");
    public Actor Maintainer { get; } = new(Wire.Subject(Issuer, "maintainer"), "Maintainer");
    public Actor AccessManager { get; } = new(Wire.Subject(Issuer, "access"), "Access manager");
    public Actor Auditor { get; } = new(Wire.Subject(Issuer, "auditor"), "Auditor");
    public Actor Outsider { get; } = new(Wire.Subject(Issuer, "foreign"), "Foreign");
    public IReadOnlySet<string> Admins { get; } = new HashSet<string>();
    public Team Team { get; }
    public Permission Read { get; } = new(Guid.NewGuid(), "orders.read", "Read orders", "Read orders", Risk.Standard);
    public Permission Approve { get; } = new(Guid.NewGuid(), "orders.approve", "Approve orders", "Approve orders", Risk.High);
    public AppRole Reader { get; }
    public AppRole Supervisor { get; }
    public DirectoryPrincipal Employee { get; }
    public DirectoryPrincipal OwnerPrincipal { get; }
    public IReadOnlyList<DirectoryPrincipal> Directory { get; }
    public ApplicationDocument App { get; }
    public Fixture()
    {
        Team = new(Guid.NewGuid(), "Example team", 1, [new(Owner.Key, Owner.Name, ManagementRole.Owner),
            new(Approver.Key, Approver.Name, ManagementRole.Approver), new(Maintainer.Key, Maintainer.Name, ManagementRole.Maintainer),
            new(AccessManager.Key, AccessManager.Name, ManagementRole.AccessManager), new(Auditor.Key, Auditor.Name, ManagementRole.Auditor)]);
        Reader = new(Guid.NewGuid(), "reader", "Reader", "Read only", [Read.Id]);
        Supervisor = new(Guid.NewGuid(), "supervisor", "Supervisor", "Approval", [Read.Id, Approve.Id]);
        Employee = Person("employee"); OwnerPrincipal = Person("owner");
        Directory = [Employee, OwnerPrincipal, Person("approver")];
        App = new(Guid.NewGuid(), Team.Id, "enterprise", "production", "Production", "Test application", "Test", ApplicationStatus.Active, 1,
            Guid.NewGuid().ToString(), "production-api", null, AppPolicy.Default, [Read, Approve], [Reader, Supervisor], []);
    }
    private DirectoryPrincipal Person(string id) => new(Guid.NewGuid(), "enterprise", Wire.Subject(Issuer, id), id, id,
        PrincipalKind.User, true, false, true, [], Clock.Now);
    public Change Grant(Guid? role = null, Guid? principal = null) => new() { Kind = ChangeKind.GrantAccess,
        TargetId = Guid.NewGuid(), RoleId = role ?? Reader.Id, PrincipalId = principal ?? Employee.Id, ExpiresAt = Clock.Now.AddDays(7), Reason = "Project assignment" };
    public Assignment Assignment(Guid? role = null, Guid? principal = null) => new(Guid.NewGuid(), role ?? Reader.Id, principal ?? Employee.Id,
        Owner.Key, Clock.Now, Clock.Now.AddDays(7));
    public Plan Plan(Change change, ApplicationDocument? app = null, Actor? actor = null, IReadOnlyList<DirectoryPrincipal>? directory = null,
        IReadOnlySet<string>? admins = null, bool system = false) => Policy.Plan(app ?? App, Team, actor ?? Owner, change,
            directory ?? Directory, admins ?? Admins, Clock.Now, system);
}
