import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { localDateTime, retentionAvailable, reviewOpen, reviewTarget } from '../src/app/review-model.ts';
import { label, problem, statusClass } from '../src/app/view-model.ts';

const now = Date.parse('2026-10-22T12:00:00Z');
const assignment = { id: 'grant', roleId: 'role', principalId: 'principal', expiresAt: '2026-11-01T12:00:00Z' };
const application = { assignments: [assignment], roles: [{ id: 'role', name: 'Rechnung prüfen' }] };
const principal = { id: 'principal', name: 'Prüfungsteam', kind: 'Group' };

test('schedule input retains its UTC instant in winter and summer local time', () => {
 const source = `
  import { localDateTime } from './src/app/review-model.ts';
  import { safeExpiry } from './src/app/view-model.ts';
  const dates = ['2026-01-20T09:15:30Z', '2026-07-20T09:15:30Z'];
  console.log(JSON.stringify(dates.map(value => [localDateTime(value), safeExpiry(localDateTime(value))])));
 `;
 const result = spawnSync(process.execPath, ['--experimental-strip-types', '--input-type=module', '-e', source],
  { cwd: new URL('..', import.meta.url), env: { ...process.env, TZ: 'Europe/Berlin' }, encoding: 'utf8' });
 assert.equal(result.status, 0, result.stderr);
 assert.deepEqual(JSON.parse(result.stdout), [
  ['2026-01-20T10:15:30', '2026-01-20T09:15:30.000Z'],
  ['2026-07-20T11:15:30', '2026-07-20T09:15:30.000Z']
 ]);
});

test('an invalid stored date cannot become a usable schedule input', () => {
 assert.equal(localDateTime('invalid'), '');
});

test('review decisions close at the exact deadline, including malformed and already closed reviews', () => {
 assert.equal(reviewOpen({ closed: false, dueAt: '2026-10-22T12:00:01Z' }, now), true);
 assert.equal(reviewOpen({ closed: false, dueAt: '2026-10-22T12:00:00Z' }, now), false);
 assert.equal(reviewOpen({ closed: false, dueAt: 'invalid' }, now), false);
 assert.equal(reviewOpen({ closed: true, dueAt: '2026-10-23T12:00:00Z' }, now), false);
});

test('review targets identify group and role without treating missing directory data as a missing grant', () => {
 assert.deepEqual(reviewTarget('grant', application, [principal]), {
  name: 'Prüfungsteam', role: 'Rechnung prüfen', kind: 'Group', present: true
 });
 assert.deepEqual(reviewTarget('grant', application, []), {
  name: 'Identität nicht mehr auflösbar', role: 'Rechnung prüfen', kind: null, present: true
 });
 assert.equal(reviewTarget('removed', application, [principal]).present, false);
});

test('expired and missing grants cannot be retained in the UI', () => {
 assert.equal(retentionAvailable('grant', application, [], now), true);
 assert.equal(retentionAvailable('missing', application, [], now), false);
 assert.equal(retentionAvailable('grant', { ...application, assignments: [{ ...assignment, expiresAt: '2026-10-22T12:00:00Z' }] }, [], now), false);
 assert.equal(retentionAvailable('grant', { ...application, assignments: [{ ...assignment, expiresAt: 'invalid' }] }, [], now), false);
});

test('a queued, failed or ambiguous revoke is never shown as eligible for retention', () => {
 for (const status of ['AwaitingApproval', 'Queued', 'Applying', 'Conflict', 'Failed', 'Applied']) {
  assert.equal(retentionAvailable('grant', application, [{ change: { kind: 'RevokeAccess', targetId: 'grant' }, status }], now), false, status);
 }
 assert.equal(retentionAvailable('grant', application, [{ change: { kind: 'RevokeAccess', targetId: 'grant' }, status: 'Rejected' }], now), true);
 assert.equal(retentionAvailable('grant', application, [{ change: { kind: 'RevokeAccess', targetId: 'another' }, status: 'Queued' }], now), true);
});

test('requested and already removed review decisions are distinct from a verified Keycloak write', () => {
 assert.equal(label('AlreadyRevoked'), 'Nicht mehr zugewiesen');
 assert.equal(label('RevocationQueued'), 'Entzug angefordert');
 assert.notEqual(statusClass('RevocationQueued'), statusClass('Applied'));
});

test('retention refusal explains expiry and pending revocation without claiming success', () => {
 assert.match(problem({ status: 409, error: { title: 'assignment_expired' } }), /bereits abgelaufen/);
 assert.match(problem({ status: 409, error: { title: 'revocation_pending' } }), /Entzug fällig oder bereits angefordert/);
 assert.match(problem({ status: 409, error: { title: 'review_closed' } }), /Frist ist abgelaufen/);
 assert.match(problem({ status: 409, error: { title: 'review_schedule_due' } }), /erst vom Worker gestartet/);
});
