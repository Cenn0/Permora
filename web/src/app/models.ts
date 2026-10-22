export type ManagementRole = 'Owner' | 'Maintainer' | 'AccessManager' | 'Approver' | 'Auditor';
export type AppStatus = 'AwaitingIam' | 'Active' | 'Suspended' | 'Retired';
export type Risk = 'Standard' | 'High';
export type OperationStatus = 'AwaitingApproval' | 'Queued' | 'Applying' | 'Applied' | 'Rejected' | 'Conflict' | 'Failed';
export interface TeamMember { subjectKey: string; name: string; role: ManagementRole }
export interface Team { id: string; name: string; revision: number; members: TeamMember[] }
export interface Session { authenticated: boolean; name?: string; subjectKey?: string; administrator?: boolean; development?: boolean; strongAuthentication?: boolean }
export interface AppSummary { id: string; teamId: string; name: string; slug: string; description: string; environment: string; status: AppStatus; revision: number; roleCount: number; permissionCount: number; identityCount: number; assignmentCount: number }
export interface Permission { id: string; key: string; name: string; description: string; risk: Risk }
export interface Role { id: string; key: string; name: string; description: string; permissionIds: string[] }
export interface Assignment { id: string; roleId: string; principalId: string; grantedBy: string; grantedAt: string; expiresAt: string | null }
export interface Principal { id: string; name: string; kind: 'User' | 'Group'; enabled?: boolean; syncedAt: string; membershipComplete?: boolean }
export interface AppPolicy { requireApproval: boolean; maxGrantDays: number; separationRules: { firstRoleId: string; secondRoleId: string; reason: string }[] }
export interface IamApproval { approvedBy: string; approvedAt: string; clientUuid: string; clientId: string; evidenceReference: string }
export interface Application { id: string; teamId: string; realmKey: string; slug: string; name: string; description: string; environment: string; status: AppStatus; revision: number; keycloakClientUuid: string | null; keycloakClientId: string | null; loginClientUuid: string | null; policy: AppPolicy; permissions: Permission[]; roles: Role[]; assignments: Assignment[]; iamApproval: IamApproval | null }
export interface ReviewSchedule { name: string; intervalDays: number; reviewWindowDays: number; nextStartAt: string; enabled: boolean; revision: number }
export interface ReviewScheduleInput { name: string; intervalDays: number; reviewWindowDays: number; nextStartAt: string; enabled: boolean; expectedRevision: number }
export interface AppDetail { application: Application; managementRole: ManagementRole; principals: Principal[]; reviewSchedule?: ReviewSchedule | null }
export interface Change { kind: string; targetId?: string; key?: string; name?: string; description?: string; risk?: Risk; permissionIds?: string[]; roleId?: string; principalId?: string; expiresAt?: string | null; reason: string }
export interface Operation { id: string; applicationId: string; requestedBy: { key: string; name: string }; change: Change; expectedRevision: number; status: OperationStatus; createdAt: string; attempts: number; approvedBy: { key: string; name: string } | null; errorCode: string | null; systemRevocation: boolean }
export interface AuditEvent { id: string; applicationId: string; actorKey: string; action: string; detail: string; at: string; sequence: number; hash: string; previousHash: string }
export interface AuditPage { items: AuditEvent[]; nextSequence: number | null }
export interface Review { id: string; applicationId: string; name: string; dueAt: string; closed: boolean; scheduledFor?: string | null; items: { assignmentId: string; decision: string; reviewedBy: string | null }[] }
export interface Dashboard { applications: AppSummary[]; counts: { applications: number; roles: number; identities: number; requests: number; failed: number }; requests: Operation[]; recentOperations: Operation[]; activities: AuditEvent[] }
export interface GovernanceCounts {
 assignments: number; expiredAssignments: number; unverifiableAssignments: number;
 groupSeparationRevocations: number; openReviews: number; pendingDecisions: number; overdueDecisions: number;
}
export interface GovernanceNotice {
 key: string; code: string; severity: 'critical' | 'warning' | 'info';
 count: number; dueAt: string | null; reviewId: string | null;
}
export interface GovernanceReport {
 applicationId: string; applicationRevision: number; asOf: string; counts: GovernanceCounts;
 totalNotices: number; notices: GovernanceNotice[]; nextOffset: number | null;
}
