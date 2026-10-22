import { test, expect, type Page } from '@playwright/test';
import type { GovernanceReport } from '../src/app/models';

const id='10000000-0000-4000-8000-000000000099';
const report=():GovernanceReport=>({applicationId:id,applicationRevision:42,asOf:'2026-10-22T12:00:00Z',
 counts:{assignments:11,expiredAssignments:2,unverifiableAssignments:3,groupSeparationRevocations:1,
 openReviews:210,pendingDecisions:400,overdueDecisions:211},totalNotices:30,nextOffset:25,
 notices:[{key:'review:one',code:'review_overdue',severity:'critical',count:211,dueAt:'2026-10-21T12:00:00Z',reviewId:'one'}]});

async function session(page:Page){await page.route('**/bff/session',route=>route.fulfill({json:{
 authenticated:true,name:'Audit team',subjectKey:'A'.repeat(64),administrator:false,development:false,strongAuthentication:true
}}));}

test('governance uses complete server counts and pages read-only notices on desktop and mobile',async({page})=>{
 await session(page);
 const offsets:string[]=[];
 await page.route('**/api/v1/applications/*/governance?*',route=>{
  expect(route.request().method()).toBe('GET');
  const offset=new URL(route.request().url()).searchParams.get('offset')!;offsets.push(offset);
  return route.fulfill({json:offset==='0'?report():{...report(),nextOffset:null,notices:[
   {key:'directory',code:'directory_unverifiable',severity:'critical',count:3,dueAt:null,reviewId:null}
  ]}});
 });
 await page.goto(`/applications/${id}/governance`);
 await expect(page.getByRole('heading',{name:'Prüfstand'})).toBeVisible();
 await expect(page.locator('dl')).toContainText('400');
 await expect(page.getByText('Eskalation erforderlich · Anzahl: 211')).toBeVisible();
 await expect(page.getByRole('link',{name:'Reviews öffnen'})).toHaveAttribute('href',`/applications/${id}?tab=reviews`);
 await page.getByRole('button',{name:'Nächste Seite'}).click();
 await expect(page.getByText('Seite 2',{exact:true})).toBeVisible();
 await expect(page.getByRole('button',{name:'Nächste Seite'})).toBeDisabled();
 await expect(page.getByText('Zuweisungen mit nicht verifizierbarem Directory-Stand',{exact:true})).toBeVisible();
 await page.getByRole('button',{name:'Vorherige Seite'}).click();
 await expect(page.getByText('Seite 1',{exact:true})).toBeVisible();
 expect(offsets).toEqual(['0','25','0']);
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBeTruthy();
});

test('failed refresh removes old privileged data and permits retry',async({page})=>{
 await session(page);let reads=0;
 await page.route('**/api/v1/applications/*/governance?*',route=>{
  reads++;
  return reads===2?route.fulfill({status:404,json:{detail:'Anwendung nicht gefunden.'}}):route.fulfill({json:report()});
 });
 await page.goto(`/applications/${id}/governance`);
 await expect(page.getByRole('heading',{name:'Prüfstand'})).toBeVisible();
 await page.getByRole('button',{name:'Bericht aktualisieren'}).click();
 await expect(page.getByRole('alert')).toContainText('Anwendung nicht gefunden.');
 await expect(page.getByRole('heading',{name:'Prüfstand'})).toHaveCount(0);
 await page.getByRole('button',{name:'Bericht aktualisieren'}).click();
 await expect(page.getByRole('heading',{name:'Prüfstand'})).toBeVisible();
});

test('empty report states its limits and has no enabled pagination',async({page})=>{
 await session(page);
 await page.route('**/api/v1/applications/*/governance?*',route=>route.fulfill({json:{...report(),totalNotices:0,nextOffset:null,notices:[]}}));
 await page.goto(`/applications/${id}/governance`);
 await expect(page.getByText('Keine Hinweise auf dieser Seite. Das ist keine Compliance-Freigabe.')).toBeVisible();
 await expect(page.getByRole('button',{name:'Nächste Seite'})).toBeDisabled();
 await expect(page.getByRole('button',{name:'Vorherige Seite'})).toBeDisabled();
});
