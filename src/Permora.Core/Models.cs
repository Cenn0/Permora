using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Permora.Core;

public enum ManagementRole { Owner, Maintainer, AccessManager, Approver, Auditor }
public enum Risk { Standard, High }
public enum ApplicationStatus { AwaitingIam, Active, Suspended, Retired }
public enum PrincipalKind { User, Group }
public enum OperationStatus { AwaitingApproval, Queued, Applying, Applied, Rejected, Conflict, Failed }
public enum ChangeKind
{
    CreatePermission, UpdatePermission, DeletePermission,
    CreateRole, UpdateRole, DeleteRole,
    GrantAccess, RevokeAccess, Reconcile
}

public sealed record Actor(string Key, string Name);
public sealed record TeamMember(string SubjectKey, string Name, ManagementRole Role);
public sealed record Team(Guid Id, string Name, long Revision, IReadOnlyList<TeamMember> Members);
public sealed record Permission(Guid Id, string Key, string Name, string Description, Risk Risk);
public sealed record AppRole(Guid Id, string Key, string Name, string Description, IReadOnlyList<Guid> PermissionIds);
public sealed record Assignment(Guid Id, Guid RoleId, Guid PrincipalId, string GrantedBy,
    DateTimeOffset GrantedAt, DateTimeOffset? ExpiresAt);
public sealed record SeparationRule(Guid FirstRoleId, Guid SecondRoleId, string Reason);
public sealed record AppPolicy(bool RequireApproval, int MaxGrantDays, IReadOnlyList<SeparationRule> SeparationRules)
{
    public static AppPolicy Default => new(true, 90, []);
}
public sealed record ApplicationDocument(
    Guid Id, Guid TeamId, string RealmKey, string Slug, string Name, string Description,
    string Environment, ApplicationStatus Status, long Revision,
    string? KeycloakClientUuid, string? KeycloakClientId, string? LoginClientUuid,
    AppPolicy Policy, IReadOnlyList<Permission> Permissions, IReadOnlyList<AppRole> Roles,
    IReadOnlyList<Assignment> Assignments);

public sealed record DirectoryPrincipal(
    Guid Id, string RealmKey, string SubjectKey, string KeycloakId, string Name,
    PrincipalKind Kind, bool Enabled, bool Protected, bool MembershipComplete,
    IReadOnlyList<string> EffectiveMemberKeys, DateTimeOffset SyncedAt);

// This is a typed domain command, never a Keycloak REST proxy or a client configuration object.
public sealed record Change
{
    public required ChangeKind Kind { get; init; }
    public Guid TargetId { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public Risk Risk { get; init; } = Risk.High;
    public IReadOnlyList<Guid> PermissionIds { get; init; } = [];
    public Guid RoleId { get; init; }
    public Guid PrincipalId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Reason { get; init; } = "";
}
public sealed record Operation(
    Guid Id, Guid ApplicationId, Actor RequestedBy, Change Change, long ExpectedRevision,
    string IdempotencyKey, string RequestHash, OperationStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset NextAttemptAt, int Attempts,
    Actor? ApprovedBy = null, DateTimeOffset? ApprovedAt = null, string? ErrorCode = null,
    bool SystemRevocation = false);
public sealed record AuditEvent(Guid Id, Guid ApplicationId, string ActorKey, string Action,
    string Detail, DateTimeOffset At, long Sequence, string PreviousHash, string Hash);
public sealed record ReviewItem(Guid AssignmentId, string Decision, string? ReviewedBy = null);
public sealed record AccessReview(Guid Id, Guid ApplicationId, string Name, DateTimeOffset DueAt,
    IReadOnlyList<ReviewItem> Items, bool Closed = false);
public sealed record DriftFinding(string Resource, string Code, string Description);
public sealed record Plan(ApplicationDocument Next, bool ApprovalRequired, string Summary);

public sealed class DomainException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("Null document.");
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Subject(string issuer, string subject)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            throw new DomainException("invalid_subject", "Issuer and subject are required.");
        return Hash(Serialize(new[] { issuer.TrimEnd('/'), subject }));
    }
}

public sealed record DashboardFeed(int AwaitingApproval, int Failed, IReadOnlyList<Operation> Requests,
    IReadOnlyList<Operation> RecentOperations, IReadOnlyList<AuditEvent> Activities);
