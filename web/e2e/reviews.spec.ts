import { test, expect, type Page } from '@playwright/test';
import type { AppDetail, ManagementRole, Operation, Review, ReviewSchedule, ReviewScheduleInput } from '../src/app/models';

// Browser contract fixtures exercise UI behavior, not server authorization or Keycloak writes.
test.use({ timezoneId: 'Europe/Berlin' });

const appId = '10000000-0000-4000-8000-000000000099';
const assignmentId = '10000000-0000-4000-8000-000000000010';
const afterDays = (days: number) => new Date(Date.now() + days * 86_400_000).toISOString();
const plan = (): ReviewSchedule => ({
 name: 'Regelmäßige Teamprüfung', intervalDays: 30, reviewWindowDays: 7,
 nextStartAt: afterDays(10), enabled: true, revision: 9
});
const campaign = (): Review => ({
 id: 'review-one', applicationId: appId, name: 'Oktoberprüfung', dueAt: afterDays(7),
 scheduledFor: afterDays(-1), closed: false,
 items: [{ assignmentId, decision: 'Pending', reviewedBy: null }]
});

async function fixture(page: Page, options: {
 role?: ManagementRole; schedule?: ReviewSchedule | null; reviews?: Review[];
 operations?: Operation[]; conflict?: boolean; scheduleDue?: boolean; removeOnDecision?: boolean; rejectReviewId?: string;
} = {}) {
 const detail: AppDetail = {
  application: {
   id: appId, teamId: 'team', realmKey: 'enterprise', name: 'Produktionsplanung', slug: 'production',
   description: 'Planung im Team', environment: 'Test', status: 'Active', revision: 42,
   keycloakClientUuid: null, keycloakClientId: 'production-api', loginClientUuid: null, iamApproval: null,
   policy: { requireApproval: true, maxGrantDays: 90, separationRules: [] }, permissions: [],
   roles: [{ id: 'role-one', key: 'invoice-review', name: 'Rechnung prüfen', description: '', permissionIds: [] }],
   assignments: [{ id: assignmentId, roleId: 'role-one', principalId: 'person-one', grantedBy: 'other', grantedAt: afterDays(-20), expiresAt: afterDays(30) }]
  },
  managementRole: options.role ?? 'Owner',
  principals: [{ id: 'person-one', name: 'Prüfungsteam', kind: 'Group', enabled: true, syncedAt: new Date().toISOString() }],
  reviewSchedule: options.schedule === undefined ? plan() : options.schedule
 };
 let reviews = options.reviews ?? [campaign()];
 const writes: { method: string; path: string; body: unknown }[] = [];
 await page.route('**/bff/session', route => route.fulfill({ json: {
  authenticated: true, name: 'Testteam', subjectKey: 'A'.repeat(64),
  administrator: false, development: false, strongAuthentication: true
 } }));
 await page.route('**/api/v1/applications/**', async route => {
  const request = route.request();
  const path = new URL(request.url()).pathname;
  if (request.method() !== 'GET') {
   const body = request.postDataJSON();
   writes.push({ method: request.method(), path, body });
   if (path.endsWith('/review-schedule') && request.method() === 'PUT') {
    if (options.conflict) return route.fulfill({ status: 412, json: { title: 'revision_conflict' } });
    if (options.scheduleDue) return route.fulfill({ status: 409, json: { title: 'review_schedule_due' } });
    const input = body as ReviewScheduleInput;
    detail.reviewSchedule = { name: input.name, intervalDays: input.intervalDays,
     reviewWindowDays: input.reviewWindowDays, nextStartAt: input.nextStartAt,
     enabled: input.enabled, revision: input.expectedRevision + 1 };
    return route.fulfill({ json: detail.reviewSchedule });
   }
   const review = reviews.find(item => path.includes(`/reviews/${item.id}/items/`));
   if (review && request.method() === 'POST') {
    if (review.id === options.rejectReviewId) return route.fulfill({ status: 409, json: { title: 'revocation_pending' } });
    if (options.removeOnDecision) detail.application.assignments = [];
    const decision = options.removeOnDecision ? 'AlreadyRevoked' : body.retain ? 'Retained' : 'RevocationQueued';
    const updated = { ...review, closed: true, items: review.items.map(item => ({ ...item, decision, reviewedBy: 'A'.repeat(64) })) };
    reviews = reviews.map(item => item.id === updated.id ? updated : item);
    return route.fulfill({ json: updated });
   }
   return route.fulfill({ status: 404, json: { detail: 'Unexpected fixture write' } });
  }
  if (path.endsWith('/audit')) return route.fulfill({ json: { items: [], nextSequence: null } });
  if (path.endsWith('/operations')) return route.fulfill({ json: options.operations ?? [] });
  if (path.endsWith('/reviews')) return route.fulfill({ json: reviews });
  if (path === `/api/v1/applications/${appId}`) return route.fulfill({ json: detail });
  return route.fulfill({ status: 404 });
 });
 await page.goto(`/applications/${appId}?tab=reviews`);
 await expect(page.getByRole('heading', { name: 'Rezertifizierung', exact: true })).toBeVisible();
 return { detail, writes };
}

test('owner creates a schedule using local time and its independent initial revision', async ({ page }) => {
 const state = await fixture(page, { schedule: null });
 await page.getByRole('button', { name: 'Prüfplan einrichten' }).click();
 const form = page.getByRole('form', { name: 'Prüfplan bearbeiten' });
 await form.getByLabel('Planname', { exact: true }).fill('Monatliche Zugriffsprüfung');
 await form.getByLabel('Intervall in Tagen', { exact: true }).fill('30');
 await form.getByLabel('Bearbeitungsfrist in Tagen', { exact: true }).fill('7');
 const local = `${afterDays(15).slice(0, 10)}T10:15:30`;
 await form.getByLabel('Nächster Start', { exact: true }).fill(local);
 const expectedUtc = await page.evaluate(value => new Date(value).toISOString(), local);
 await form.getByRole('button', { name: 'Prüfplan speichern' }).click();
 await expect(page.getByRole('status')).toContainText('Prüfplan gespeichert');
 expect(state.writes).toEqual([{ method: 'PUT', path: `/api/v1/applications/${appId}/review-schedule`, body: {
  name: 'Monatliche Zugriffsprüfung', intervalDays: 30, reviewWindowDays: 7,
  nextStartAt: expectedUtc, enabled: true, expectedRevision: 0
 } }]);
 await expect(page.getByText('Alle 30 Tage', { exact: true })).toBeVisible();
 await expect(form).toHaveCount(0);
});

test('owner can disable future starts without changing existing campaign deadlines', async ({ page }) => {
 const schedule = plan();
 const review = campaign();
 const state = await fixture(page, { schedule, reviews: [review] });
 await page.getByRole('button', { name: 'Prüfplan bearbeiten' }).click();
 const form = page.getByRole('form', { name: 'Prüfplan bearbeiten' });
 await form.getByLabel('Automatische Prüfungen aktivieren').uncheck();
 await form.getByRole('button', { name: 'Prüfplan speichern' }).click();
 await expect(page.getByRole('status')).toContainText('Bereits gestartete Prüfungen und ihre Fristen bleiben bestehen');
 expect(state.writes[0]?.body).toEqual({ name: schedule.name, intervalDays: 30, reviewWindowDays: 7,
  nextStartAt: schedule.nextStartAt, enabled: false, expectedRevision: 9 });
 await expect(page.getByText('Deaktiviert', { exact: true })).toBeVisible();
 await expect(page.getByRole('article', { name: review.name }).getByRole('button', { name: 'Beibehalten' })).toBeVisible();
});

test('a due schedule cannot be disabled before its pending campaign starts', async ({ page }) => {
 const state = await fixture(page, { schedule: { ...plan(), nextStartAt: afterDays(-2) }, scheduleDue: true });
 await page.getByRole('button', { name: 'Prüfplan bearbeiten' }).click();
 const form = page.getByRole('form', { name: 'Prüfplan bearbeiten' });
 await form.getByLabel('Automatische Prüfungen aktivieren').uncheck();
 await form.getByRole('button', { name: 'Prüfplan speichern' }).click();
 await expect(page.getByRole('alert')).toContainText('erst vom Worker gestartet');
 await expect(page.getByText('Aktiviert', { exact: true })).toBeVisible();
 await expect(form).toBeVisible();
 await expect(page.getByRole('status')).toHaveCount(0);
 expect((state.writes[0]?.body as ReviewScheduleInput).enabled).toBe(false);
});

test('stale schedule edit keeps entered values and reports conflict without a success claim', async ({ page }) => {
 const state = await fixture(page, { conflict: true });
 await page.getByRole('button', { name: 'Prüfplan bearbeiten' }).click();
 const form = page.getByRole('form', { name: 'Prüfplan bearbeiten' });
 await form.getByLabel('Planname', { exact: true }).fill('Geänderte Teamprüfung');
 await form.getByRole('button', { name: 'Prüfplan speichern' }).click();
 await expect(page.getByRole('alert')).toContainText('zwischenzeitlich geändert');
 await expect(form.getByLabel('Planname', { exact: true })).toHaveValue('Geänderte Teamprüfung');
 await expect(page.getByRole('status')).toHaveCount(0);
 expect((state.writes[0]?.body as ReviewScheduleInput).expectedRevision).toBe(9);
});

test('review window cannot exceed its interval and an active overdue start cannot be saved', async ({ page }) => {
 const state = await fixture(page);
 await page.getByRole('button', { name: 'Prüfplan bearbeiten' }).click();
 const form = page.getByRole('form', { name: 'Prüfplan bearbeiten' });
 await form.getByLabel('Intervall in Tagen', { exact: true }).fill('3');
 await form.getByLabel('Bearbeitungsfrist in Tagen', { exact: true }).fill('4');
 await expect(form.getByRole('button', { name: 'Prüfplan speichern' })).toBeDisabled();
 await form.getByLabel('Bearbeitungsfrist in Tagen', { exact: true }).fill('2');
 await form.getByLabel('Nächster Start', { exact: true }).fill('2020-01-01T12:00');
 await form.getByRole('button', { name: 'Prüfplan speichern' }).click();
 await expect(page.getByRole('alert')).toContainText('innerhalb der kommenden 365 Tage');
 expect(state.writes).toHaveLength(0);
});

test('approver sees meaningful review targets and can decide but cannot configure schedules', async ({ page }) => {
 const state = await fixture(page, { role: 'Approver' });
 await expect(page.getByRole('heading', { name: 'Automatischer Prüfplan' })).toBeVisible();
 await expect(page.getByRole('button', { name: 'Prüfplan bearbeiten' })).toHaveCount(0);
 await expect(page.getByRole('button', { name: 'Prüfung starten' })).toHaveCount(0);
 const card = page.getByRole('article', { name: 'Oktoberprüfung' });
 await expect(card.getByText('Prüfungsteam', { exact: true })).toBeVisible();
 await expect(card.getByText('Rechnung prüfen · Gruppe', { exact: true })).toBeVisible();
 await expect(card).toContainText('Automatisch · geplanter Start');
 await card.getByRole('button', { name: 'Beibehalten' }).click();
 await expect(page.getByRole('status')).toContainText('Berechtigung bestätigt');
 expect(state.writes).toEqual([{ method: 'POST', path: `/api/v1/applications/${appId}/reviews/review-one/items/${assignmentId}`, body: { retain: true } }]);
});

test('auditor reads the plan and removed assignment history without mutation controls', async ({ page }) => {
 const review = { ...campaign(), closed: true, items: [{ assignmentId: 'removed', decision: 'AlreadyRevoked', reviewedBy: null }] };
 const state = await fixture(page, { role: 'Auditor', reviews: [review] });
 await expect(page.getByText('Regelmäßige Teamprüfung', { exact: true })).toBeVisible();
 await expect(page.getByText('Zuweisung nicht mehr vorhanden', { exact: true })).toBeVisible();
 await expect(page.getByText('Nicht mehr zugewiesen', { exact: true })).toBeVisible();
 for (const name of ['Prüfplan bearbeiten', 'Prüfung starten', 'Beibehalten', 'Entziehen', 'Als entfernt bestätigen'])
  await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0);
 expect(state.writes).toHaveLength(0);
});

test('expired review denies decision controls before the worker closes it', async ({ page }) => {
 const state = await fixture(page, { role: 'Approver', reviews: [{ ...campaign(), dueAt: afterDays(-1) }] });
 const card = page.getByRole('article', { name: 'Oktoberprüfung' });
 await expect(card).toContainText('Frist abgelaufen');
 await expect(card).toContainText('Entscheidungen sind gesperrt');
 await expect(card.getByRole('button', { name: 'Beibehalten' })).toHaveCount(0);
 await expect(card.getByRole('button', { name: 'Entziehen', exact: true })).toHaveCount(0);
 expect(state.writes).toHaveLength(0);
});

test('pending revoke prevents retention and an accepted revoke remains visibly unconfirmed', async ({ page }) => {
 const operation: Operation = { id: 'revoke-one', applicationId: appId,
  requestedBy: { key: 'system', name: 'Lifecycle worker' }, change: { kind: 'RevokeAccess', targetId: assignmentId, reason: 'Review expired' },
  expectedRevision: 42, status: 'Queued', createdAt: afterDays(-1), attempts: 0, approvedBy: null, errorCode: null, systemRevocation: true };
 await fixture(page, { role: 'Approver', operations: [operation] });
 const card = page.getByRole('article', { name: 'Oktoberprüfung' });
 await expect(card.getByRole('button', { name: 'Beibehalten' })).toBeDisabled();
 await card.getByRole('button', { name: 'Entziehen', exact: true }).click();
 await expect(page.getByRole('status')).toContainText('noch nicht wirksam bestätigt');
 await expect(card.getByText('Entzug angefordert', { exact: true })).toBeVisible();
 await expect(card).not.toContainText('In Keycloak bestätigt');
 await card.getByRole('button', { name: 'Aufträge', exact: true }).click();
 await expect(page.getByRole('heading', { name: 'Änderungsaufträge' })).toBeVisible();
});

test('concurrently removed grant is never announced as retained', async ({ page }) => {
 await fixture(page, { role: 'Approver', removeOnDecision: true });
 await page.getByRole('button', { name: 'Beibehalten' }).click();
 await expect(page.getByRole('status')).toContainText('Die Zuweisung ist bereits entfernt');
 await expect(page.getByRole('status')).not.toContainText('Berechtigung bestätigt');
 await expect(page.getByText('Nicht mehr zugewiesen', { exact: true })).toBeVisible();
});

test('a rejected later decision clears the earlier success notice', async ({ page }) => {
 const first = campaign();
 const second = { ...campaign(), id: 'review-two', name: 'Zusätzliche Prüfung' };
 await fixture(page, { role: 'Approver', reviews: [first, second], rejectReviewId: second.id });
 await page.getByRole('article', { name: first.name }).getByRole('button', { name: 'Beibehalten' }).click();
 await expect(page.getByRole('status')).toContainText('Berechtigung bestätigt');
 await page.getByRole('article', { name: second.name }).getByRole('button', { name: 'Beibehalten' }).click();
 await expect(page.getByRole('alert')).toContainText('Entzug fällig oder bereits angefordert');
 await expect(page.getByRole('status')).toHaveCount(0);
});

test('plan editor and campaign targets reflow at 320 pixels', async ({ page }) => {
 await page.setViewportSize({ width: 320, height: 900 });
 await fixture(page);
 await page.getByRole('button', { name: 'Prüfplan bearbeiten' }).click();
 await expect(page.getByRole('form', { name: 'Prüfplan bearbeiten' })).toBeVisible();
 expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
});
