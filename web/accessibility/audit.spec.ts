import { test, expect, type Page } from '@playwright/test';
import { resolve } from 'node:path';

// Synthetic view data is not evidence of server authorization or a Keycloak write.
const id='10000000-0000-4000-8000-000000000099';
const teamId='10000000-0000-4000-8000-000000000001';
const permissionId='20000000-0000-4000-8000-000000000001';
const roleId='30000000-0000-4000-8000-000000000001';
const userId='40000000-0000-4000-8000-000000000001';
const assignmentId='50000000-0000-4000-8000-000000000001';
const permission={id:permissionId,key:'orders.read',name:'Aufträge lesen',description:'Fachliche Leseberechtigung',risk:'Standard'};
const role={id:roleId,key:'reader',name:'Lesende Fachrolle',description:'Freigegebene Auftragsansicht',permissionIds:[permissionId]};
const app={id,teamId,realmKey:'enterprise',name:'Produktionsplanung',slug:'production',
 description:'Anwendungen und Zugriffe für ein getrenntes Testteam verwalten.',environment:'Test',status:'Active',
 revision:1,roleCount:1,permissionCount:1,identityCount:1,assignmentCount:1,
 keycloakClientId:'production-api',keycloakClientUuid:'60000000-0000-4000-8000-000000000001',loginClientUuid:null,
 policy:{requireApproval:true,maxGrantDays:90,separationRules:[]},permissions:[permission],roles:[role],
 assignments:[{id:assignmentId,roleId,principalId:userId,grantedBy:'B'.repeat(64),grantedAt:'2026-10-16T00:00:00Z',expiresAt:'2026-12-01T00:00:00Z'}],iamApproval:null};
const operation={id:'70000000-0000-4000-8000-000000000001',applicationId:id,
 requestedBy:{key:'B'.repeat(64),name:'Andere Testperson'},change:{kind:'GrantAccess',name:'Leserechte',reason:'Testantrag'},
 status:'AwaitingApproval',createdAt:'2026-10-16T12:00:00Z',attempts:0};
const review={id:'80000000-0000-4000-8000-000000000001',applicationId:id,name:'Zugriffsprüfung',
 dueAt:'2026-12-01T00:00:00Z',closed:false,items:[{assignmentId,decision:'Pending'}]};
const team={id:teamId,name:'Testteam',revision:1,members:[{subjectKey:'A'.repeat(64),name:'Testperson',role:'Owner'}]};
const dashboard={applications:[app],counts:{applications:1,roles:1,identities:1,requests:1,failed:0},
 requests:[operation],recentOperations:[operation],activities:[]};

async function fixtures(page:Page, authenticated=true, failure=false) {
 await page.route('**/bff/session',route=>route.fulfill({json:{authenticated,name:'Testperson',
  subjectKey:'A'.repeat(64),administrator:true,development:false,strongAuthentication:true}}));
 await page.route('**/api/v1/**',route=>{
  const path=new URL(route.request().url()).pathname;
  if(path.endsWith('/dashboard')) return failure
   ?route.fulfill({status:503,json:{detail:'Der Testdienst ist vorübergehend nicht erreichbar.'}})
   :route.fulfill({json:dashboard});
  if(path==='/api/v1/applications')return route.fulfill({json:[app]});
  if(path==='/api/v1/teams')return route.fulfill({json:[team]});
  if(path==='/api/v1/realms')return route.fulfill({json:['enterprise']});
  if(path.endsWith('/operations'))return route.fulfill({json:[operation]});
  if(path.endsWith('/reviews'))return route.fulfill({json:[review]});
  if(path.endsWith('/audit'))return route.fulfill({json:{items:[],nextSequence:null}});
  if(path.endsWith('/identities'))return route.fulfill({json:[]});
  if(path==='/api/v1/applications/'+id)return route.fulfill({json:{application:app,managementRole:'Owner',
   principals:[{id:userId,name:'Zugewiesene Testperson',kind:'User'}]}});
  return route.fulfill({status:404,json:{title:'fixture_not_defined'}});
 });
}

type Scan={violations:{id:string;impact:string;nodes:{target:string[];html:string;failureSummary:string}[]}[];incomplete:unknown[]};
async function audit(page:Page) {
 await page.evaluate(()=>document.fonts.ready);
 await page.addScriptTag({path:resolve('../tools/accessibility/node_modules/axe-core/axe.min.js')});
 const result=await page.evaluate(async()=>{
  const axe=(globalThis as unknown as {axe:{run:(node:Document,options:unknown)=>Promise<Scan>}}).axe;
  return await axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']},
   resultTypes:['violations','incomplete']});
 });
 const issues=result.violations.map(v=>({rule:v.id,impact:v.impact,nodes:v.nodes.map(n=>({
  target:n.target,html:n.html.slice(0,180),reason:n.failureSummary?.slice(0,280)
 }))}));
 const layout=await page.evaluate(()=>({width:window.innerWidth,documentWidth:document.documentElement.scrollWidth,
  scrollContainers:[...document.querySelectorAll('*')].filter(e=>e.scrollWidth>e.clientWidth+1).slice(0,24).map(e=>({tag:e.tagName,className:String(e.className),clientWidth:e.clientWidth,scrollWidth:e.scrollWidth})),
  overflow:[...document.querySelectorAll('*')].filter(e=>{
   const r=e.getBoundingClientRect();return r.width>0&&r.right>window.innerWidth+1;
  }).slice(0,12).map(e=>({tag:e.tagName,className:e.className,width:Math.round(e.getBoundingClientRect().width)}))}));
 console.log('A11Y',JSON.stringify({route:new URL(page.url()).pathname,issues,layout,manualReviewChecks:result.incomplete.length}));
 expect(issues,'All automated WCAG violations must be fixed, not excluded').toEqual([]);
 expect(layout.documentWidth,'Document must reflow; internal table scrolling is permitted').toBeLessThanOrEqual(layout.width+1);
}

for(const view of [
 {name:'dashboard',path:'/'},{name:'application-list',path:'/applications'},
 {name:'registration',path:'/applications/new'},{name:'team-directory',path:'/teams'},
 {name:'approvals',path:'/requests'},{name:'help',path:'/help'}
])test(view.name,async({page})=>{
 await fixtures(page);await page.goto(view.path);await expect(page.locator('main h1')).toBeVisible();await audit(page);
});
for(const tab of ['Rollen','Berechtigungen','Zugriffe','Aufträge','Rezertifizierung','Änderungsprotokoll','Anbindung'])
 test('application-'+tab,async({page})=>{
  await fixtures(page);await page.goto('/applications/'+id);
  const button=page.getByRole('navigation',{name:'Anwendungsbereiche'}).getByRole('button',{name:tab,exact:true});
  await button.click();await expect(button).toHaveAttribute('aria-current','page');
  await expect(page.locator('main section.panel').first().getByRole('heading').first()).toBeVisible();
  await audit(page);
 });
test('role-editor',async({page})=>{
 await fixtures(page);await page.goto('/applications/'+id);
 await page.getByRole('button',{name:'Neue Rolle',exact:true}).click();
 await expect(page.locator('form').last()).toBeVisible();await audit(page);
});
test('team-editor',async({page})=>{
 await fixtures(page);await page.goto('/teams');
 await page.getByRole('button',{name:'Neues Team',exact:true}).click();
 await page.getByRole('button',{name:'Mitglied hinzufügen',exact:true}).click();
 await expect(page.locator('fieldset')).toBeVisible();await audit(page);
});
test('unauthenticated',async({page})=>{
 await fixtures(page,false);await page.goto('/');
 await expect(page.getByRole('link',{name:'Mit Keycloak anmelden'})).toBeVisible();await audit(page);
});
test('error-state',async({page})=>{
 await fixtures(page,true,true);await page.goto('/');
 await expect(page.getByRole('alert')).toBeVisible();await audit(page);
});
test('keyboard-and-enlarged-text',async({page})=>{
 await fixtures(page);await page.goto('/');await expect(page.locator('main h1')).toBeVisible();
 const skip=page.getByRole('link',{name:'Zum Inhalt'});await skip.focus();await skip.press('Enter');
 await expect(page.locator('main')).toBeFocused();
 await page.addStyleTag({content:'html { font-size: 200% !important; }'});await audit(page);
});
