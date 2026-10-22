import type { Application, Operation, Principal, Review } from './models';

/** datetime-local expects local wall time, while the API exchanges UTC instants. */
export function localDateTime(value: string): string {
 const date = new Date(value);
 if (!Number.isFinite(date.getTime())) return '';
 const part = (number: number) => String(number).padStart(2, '0');
 return `${date.getFullYear()}-${part(date.getMonth() + 1)}-${part(date.getDate())}T${part(date.getHours())}:${part(date.getMinutes())}:${part(date.getSeconds())}`;
}

export function reviewOpen(review: Review, now = Date.now()): boolean {
 return !review.closed && Date.parse(review.dueAt) > now;
}

export function reviewTarget(assignmentId: string, application: Application, principals: readonly Principal[]): {
 name: string; role: string; kind: Principal['kind'] | null; present: boolean;
} {
 const assignment = application.assignments.find(item => item.id === assignmentId);
 if (!assignment) return { name: 'Zuweisung nicht mehr vorhanden', role: 'Bereits aus dem verwalteten Bestand entfernt', kind: null, present: false };
 const principal = principals.find(item => item.id === assignment.principalId);
 const role = application.roles.find(item => item.id === assignment.roleId);
 return {
  name: principal?.name ?? 'Identität nicht mehr auflösbar',
  role: role?.name ?? 'Rolle nicht mehr auflösbar',
  kind: principal?.kind ?? null,
  present: true
 };
}

/** UI hint only. The API independently validates eligibility and pending revocations. */
export function retentionAvailable(assignmentId: string, application: Application,
 operations: readonly Operation[], now = Date.now()): boolean {
 const assignment = application.assignments.find(item => item.id === assignmentId);
 if (!assignment || (assignment.expiresAt !== null && !(Date.parse(assignment.expiresAt) > now))) return false;
 return !operations.some(operation => operation.change.kind === 'RevokeAccess'
  && operation.change.targetId === assignmentId && operation.status !== 'Rejected');
}
