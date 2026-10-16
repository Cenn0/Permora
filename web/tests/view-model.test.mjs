import test from 'node:test';
import assert from 'node:assert/strict';
import { applicationColor, barWidth, label, percentage, problem, RequestKeys, safeExpiry, statusClass } from '../src/app/view-model.ts';
for (const [input, expected] of [['Applied','In Keycloak bestätigt'],['Queued','In Warteschlange'],['AwaitingApproval','Zur Freigabe'],['AwaitingIam','IAM-Prüfung ausstehend'],['Unknown','Unknown']]) {
 test(`status label: ${input}`,()=>assert.equal(label(input),expected));
}
for (const [value, values, expected] of [[0,[],0],[0,[0,0],0],[5,[10,5],50],[30,[10],100],[-5,[10],0],[NaN,[10],0],[5,[NaN,10],50]]) {
 test(`chart width ${value} / ${values}`,()=>assert.equal(barWidth(value,values),expected));
}
for(const [part,total,expected] of [[0,0,0],[5,10,50],[20,10,100],[-1,10,0],[NaN,10,0],[5,Infinity,0]]){
 test(`percentage ${part}/${total}`,()=>assert.equal(percentage(part,total),expected));
}
test('color cycle is deterministic',()=>assert.equal(applicationColor(4),applicationColor(0)));
test('negative color index is normalized',()=>assert.equal(applicationColor(-1),applicationColor(1)));
test('invalid expiry is not serialized',()=>assert.equal(safeExpiry('invalid'),null));
test('valid expiry has an explicit timezone',()=>assert.equal(safeExpiry('2026-10-17T12:00:00Z'),'2026-10-17T12:00:00.000Z'));
test('network errors never claim an effective change',()=>assert.match(problem({status:0}),/nicht bestätigt/));
test('stale revision instructs a reload',()=>assert.match(problem({status:412}),/zwischenzeitlich/));
test('expired session requests login',()=>assert.match(problem({status:401}),/erneut anmelden/));
test('low assurance errors request MFA without claiming a write',()=>assert.match(problem({status:403,error:{title:'step_up_required'}}),/MFA-Bestätigung/));
test('server problem detail is displayed as text',()=>assert.equal(problem({error:{detail:'Conflict'}}),'Conflict'));
test('unknown errors have a safe fallback',()=>assert.equal(problem(null),'Der Vorgang konnte nicht abgeschlossen werden.'));
test('success and failure tones are distinct',()=>{assert.equal(statusClass('Applied'),'good');assert.equal(statusClass('Failed'),'danger');assert.notEqual(statusClass('Queued'),'good');});
test('same command reuses the same idempotency key',()=>{let counter=0;const keys=new RequestKeys(()=>`key-${++counter}`);assert.equal(keys.for({reason:'one'}),keys.for({reason:'one'}));assert.equal(counter,1);});
test('edited command gets a fresh idempotency key',()=>{let counter=0;const keys=new RequestKeys(()=>`key-${++counter}`);assert.notEqual(keys.for({reason:'one'}),keys.for({reason:'two'}));});
test('revision change gets a fresh idempotency key',()=>{let counter=0;const keys=new RequestKeys(()=>`key-${++counter}`);assert.notEqual(keys.for({revision:1}),keys.for({revision:2}));});
test('success clears the previous key',()=>{let counter=0;const keys=new RequestKeys(()=>`key-${++counter}`);const first=keys.for({});keys.clear();assert.notEqual(first,keys.for({}));});
