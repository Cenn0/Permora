import type { OperationStatus, AppStatus } from './models';

const labels: Record<string, string> = {
 AwaitingIam: 'IAM-Prüfung ausstehend', Active: 'Aktiv', Suspended: 'Verwaltung gesperrt', Retired: 'Stillgelegt',
 AwaitingApproval: 'Zur Freigabe', Queued: 'In Warteschlange', Applying: 'Wird angewendet', Applied: 'In Keycloak bestätigt',
 Rejected: 'Abgelehnt', Conflict: 'Klärung erforderlich', Failed: 'Technisch fehlgeschlagen',
 CreatePermission: 'Berechtigung erstellen', UpdatePermission: 'Berechtigung ändern', DeletePermission: 'Berechtigung löschen',
 CreateRole: 'Rolle erstellen', UpdateRole: 'Rolle ändern', DeleteRole: 'Rolle löschen', GrantAccess: 'Zugriff vergeben', RevokeAccess: 'Zugriff entziehen', Reconcile: 'Keycloak abgleichen',
 Owner: 'Verantwortlich', Maintainer: 'Rollenpflege', AccessManager: 'Zugriffsverwaltung', Approver: 'Freigabe', Auditor: 'Lesend',
 Pending: 'Offen', Retained: 'Bestätigt', RevocationQueued: 'Entzug angefordert', AlreadyRevoked: 'Nicht mehr zugewiesen', Standard: 'Standard', High: 'Erhöht',
 User: 'Person', Group: 'Gruppe', Production: 'Produktion', Test: 'Test', Development: 'Entwicklung'
};
export function label(value: string): string { return labels[value] ?? value; }
export function statusClass(value: OperationStatus | AppStatus | string): string {
 if (['Applied', 'Active', 'Retained'].includes(value)) return 'good';
 if (['Conflict', 'Failed', 'Suspended'].includes(value)) return 'danger';
 if (['AwaitingApproval', 'AwaitingIam', 'Pending'].includes(value)) return 'warning';
 return 'neutral';
}
export function barWidth(value: number, values: readonly number[]): number {
 const max = Math.max(0, ...values.filter(Number.isFinite));
 return max > 0 && Number.isFinite(value) ? Math.min(100, Math.max(0, value / max * 100)) : 0;
}
export function percentage(part: number, total: number): number {
 return total > 0 && Number.isFinite(part) && Number.isFinite(total) ? Math.min(100, Math.max(0, Math.round(part / total * 100))) : 0;
}
export function applicationColor(index: number): string { return ['blue', 'purple', 'green', 'coral'][Math.abs(index) % 4]!; }
export function safeExpiry(value: string): string | null {
 const time = Date.parse(value); return Number.isFinite(time) ? new Date(time).toISOString() : null;
}
export function problem(error: unknown): string {
 const value = error as { status?: number; error?: { title?: string; detail?: string; code?: string } };
 if (value?.status === 401) return 'Die Sitzung ist abgelaufen. Bitte erneut anmelden.';
 if (value?.status === 403 && value?.error?.title === 'step_up_required')
  return 'Für diese Änderung ist eine aktuelle MFA-Bestätigung erforderlich. Bitte über den Hinweis oben erneut bei Keycloak bestätigen.';
 if (value?.status === 0) return 'Der Server ist nicht erreichbar. Die Änderung wurde nicht bestätigt.';
 if (value?.status === 412) return 'Die Daten wurden zwischenzeitlich geändert. Bitte aktualisieren.';
 const code = value?.error?.title ?? value?.error?.code;
 if (code === 'assignment_expired') return 'Der Zugriff ist bereits abgelaufen. Er kann nicht erneut bestätigt werden.';
 if (code === 'revocation_pending') return 'Für diesen Zugriff ist ein Entzug fällig oder bereits angefordert. Er kann nicht bestätigt werden.';
 if (code === 'review_closed') return 'Diese Prüfung ist abgeschlossen oder ihre Frist ist abgelaufen. Bitte aktualisieren.';
 if (code === 'review_schedule_due') return 'Die fällige Prüfung muss erst vom Worker gestartet werden. Danach den Plan neu laden.';
 const detail = value?.error?.detail;
 return detail && typeof detail === 'string' ? detail : 'Der Vorgang konnte nicht abgeschlossen werden.';
}
// Keep one key for retries of an identical command; never re-use it for an edited command.
export class RequestKeys {
 private fingerprint = '';
 private key = '';
 private readonly generate: () => string;
 constructor(generate: () => string = () => crypto.randomUUID()) { this.generate = generate; }
 for(command: unknown): string {
  const fingerprint = JSON.stringify(command);
  if (fingerprint !== this.fingerprint || !this.key) { this.key = this.generate(); this.fingerprint = fingerprint; }
  return this.key;
 }
 clear(): void { this.fingerprint = ''; this.key = ''; }
}
