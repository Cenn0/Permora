export type ManagementRole = 'Owner' | 'Maintainer' | 'AccessManager' | 'Approver' | 'Auditor';
export type AppStatus = 'AwaitingIam' | 'Active' | 'Suspended' | 'Retired';
export type Risk = 'Standard' | 'High';
export type OperationStatus = 'AwaitingApproval' | 'Queued' | 'Applying' | 'Applied' | 'Rejected' | 'Conflict' | 'Failed';
export interface TeamMember { subjectKey: string; name: string; role: ManagementRole }
export interface Team { id: string; name: string; revision: number; members: TeamMember[] }
export interface Session { authenticated: boolean; name?: string; subjectKey?: string; administrator?: boolean; development?: boolean }
export interface AppSummary { id: string; teamId: string; name: string; slug: string; description: string; environment: string; status: AppStatus; revision: number; roleCount: number; permissionCount: number; identityCount: number; assignmentCount: number }
export interface Permission { id: string; key: string; name: string; description: string; risk: Risk }
export interface Role { id: string; key: string; name: string; description: string; permissionIds: string[] }
export interface Assignment { id: string; roleId: string; principalId: string; grantedBy: string; grantedAt: string; expiresAt: string | null }
export interface Principal { id: string; name: string; kind: 'User' | 'Group'; enabled?: boolean; syncedAt: string; membershipComplete?: boolean }
export interface AppPolicy { requireApproval: boolean; maxGrantDays: number; separationRules: { firstRoleId: string; secondRoleId: string; reason: string }[] }
export interface Application { id: string; teamId: string; realmKey: string; slug: string; name: string; description: string; environment: string; status: AppStatus; revision: number; keycloakClientUuid: string | null; keycloakClientId: string | null; loginClientUuid: string | null; policy: AppPolicy; permissions: Permission[]; roles: Role[]; assignments: Assignment[] }
export interface AppDetail { application: Application; managementRole: ManagementRole; principals: Principal[] }
export interface Change { kind: string; targetId?: string; key?: string; name?: string; description?: string; risk?: Risk; permissionIds?: string[]; roleId?: string; principalId?: string; expiresAt?: string | null; reason: string }
export interface Operation { id: string; applicationId: string; requestedBy: { key: string; name: string }; change: Change; expectedRevision: number; status: OperationStatus; createdAt: string; attempts: number; approvedBy: { key: string; name: string } | null; errorCode: string | null; systemRevocation: boolean }
export interface AuditEvent { id: string; applicationId: string; actorKey: string; action: string; detail: string; at: string; sequence: number; hash: string; previousHash: string }
export interface AuditPage { items: AuditEvent[]; nextSequence: number | null }
export interface Review { id: string; applicationId: string; name: string; dueAt: string; closed: boolean; items: { assignmentId: string; decision: string; reviewedBy: string | null }[] }
export interface Dashboard { applications: AppSummary[]; counts: { applications: number; roles: number; identities: number; requests: number; failed: number }; requests: Operation[]; recentOperations: Operation[]; activities: AuditEvent[] }
