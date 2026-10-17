import { test, expect, type Page } from '@playwright/test';
// UI contract tests with explicit API fixtures. These do NOT prove Keycloak or server authorization.
const app={id:'10000000-0000-4000-8000-000000000099',teamId:'10000000-0000-4000-8000-000000000001',name:'Produktionsplanung',slug:'production',description:'Planung im Team',environment:'Test',status:'Active',revision:1,roleCount:2,permissionCount:3,identityCount:0,assignmentCount:0};
async function session(page:Page){await page.route('**/bff/session',route=>route.fulfill({json:{authenticated:true,name:'Testteam',subjectKey:'A'.repeat(64),administrator:false,development:false}}));}
test('dashboard uses scoped server counts and handles zero charts',async({page})=>{await session(page);await page.route('**/api/v1/dashboard',route=>route.fulfill({json:{applications:[app],counts:{applications:1,roles:2,identities:0,requests:35,failed:0},requests:[],recentOperations:[],activities:[]}}));await page.goto('/');await expect(page.getByRole('heading',{name:'Willkommen zurück.'})).toBeVisible();await expect(page.getByRole('heading',{name:'Produktionsplanung'})).toBeVisible();await expect(page.getByText('35',{exact:true}).first()).toBeVisible();await expect(page.locator('body')).not.toContainText('NaN');expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth)).toBeTruthy();});
test('low assurance session shows a real Keycloak step-up entry point',async({page})=>{
 await page.route('**/bff/session',route=>route.fulfill({json:{
  authenticated:true,name:'Demo',subjectKey:'A'.repeat(64),administrator:false,development:false,strongAuthentication:false
 }}));
 await page.route('**/api/v1/dashboard',route=>route.fulfill({json:{
  applications:[],counts:{applications:0,roles:0,identities:0,requests:0,failed:0},
  requests:[],recentOperations:[],activities:[]
 }}));
 await page.goto('/');
 await expect(page.getByRole('link',{name:'MFA bei Keycloak bestätigen'})).toHaveAttribute('href','/bff/step-up');
});
test('unauthenticated user sees login instead of app data',async({page})=>{await page.route('**/bff/session',route=>route.fulfill({json:{authenticated:false}}));await page.goto('/');await expect(page.getByRole('link',{name:'Mit Keycloak anmelden'})).toBeVisible();await expect(page.getByRole('navigation',{name:'Hauptnavigation'})).toHaveCount(0);});
test('dashboard failure is visible and retryable',async({page})=>{await session(page);await page.route('**/api/v1/dashboard',route=>route.fulfill({status:503,json:{detail:'Verbindung unterbrochen'}}));await page.goto('/');await expect(page.getByRole('alert')).toContainText('Verbindung unterbrochen');await expect(page.getByRole('button',{name:'Erneut laden'})).toBeVisible();});
test('auditor cannot start writes in the application view',async({page})=>{await session(page);await page.route('**/api/v1/applications/**',route=>{const url=new URL(route.request().url());if(url.pathname.endsWith('/audit'))return route.fulfill({json:{items:[],nextSequence:null}});if(url.pathname.endsWith('/operations')||url.pathname.endsWith('/reviews'))return route.fulfill({json:[]});return route.fulfill({json:{application:{...app,realmKey:'enterprise',keycloakClientId:'production-api',keycloakClientUuid:null,loginClientUuid:null,policy:{requireApproval:true,maxGrantDays:90,separationRules:[]},permissions:[],roles:[],assignments:[]},managementRole:'Auditor',principals:[]}});});await page.goto('/applications/'+app.id);await expect(page.getByRole('heading',{name:'Produktionsplanung'})).toBeVisible();await expect(page.getByRole('button',{name:'Neue Rolle',exact:true})).toHaveCount(0);});


test('keyboard skip link moves focus to the main content landmark',async({page})=>{
 await session(page);
 await page.route('**/api/v1/dashboard',route=>route.fulfill({json:{
  applications:[],counts:{applications:0,roles:0,identities:0,requests:0,failed:0},
  requests:[],recentOperations:[],activities:[]
 }}));
 await page.goto('/');
 const skip=page.getByRole('link',{name:'Zum Inhalt'});
 await skip.focus();
 await expect(skip).toBeFocused();
 await skip.press('Enter');
 await expect(page.locator('main#main')).toBeFocused();
});

test('sidebar identifies the currently active route for assistive technology',async({page})=>{
 await session(page);
 await page.route('**/api/v1/dashboard',route=>route.fulfill({json:{
  applications:[],counts:{applications:0,roles:0,identities:0,requests:0,failed:0},
  requests:[],recentOperations:[],activities:[]
 }}));
 await page.route('**/api/v1/applications',route=>route.fulfill({json:[]}));
 await page.goto('/');
 const nav=page.getByRole('navigation',{name:'Hauptnavigation'});
 await expect(nav.getByRole('link',{name:'Dashboard'})).toHaveAttribute('aria-current','page');
 const mobileToggle = page.getByRole('button',{name:'Navigation öffnen'});
 if (await mobileToggle.isVisible()) await mobileToggle.click();
 await nav.getByRole('link',{name:'Meine Anwendungen'}).click();
 await expect(nav.getByRole('link',{name:'Meine Anwendungen'})).toHaveAttribute('aria-current','page');
 await expect(nav.getByRole('link',{name:'Dashboard'})).not.toHaveAttribute('aria-current','page');
});

test('mobile menu can be dismissed using Escape and updates its expanded state',async({page})=>{
 await page.setViewportSize({width:390,height:844});
 await session(page);
 await page.route('**/api/v1/dashboard',route=>route.fulfill({json:{
  applications:[],counts:{applications:0,roles:0,identities:0,requests:0,failed:0},
  requests:[],recentOperations:[],activities:[]
 }}));
 await page.goto('/');
 const open=page.getByRole('button',{name:'Navigation öffnen'});
 await expect(open).toHaveAttribute('aria-expanded','false');
 await open.click();
 const close=page.getByRole('button',{name:'Navigation schließen'});
 await expect(close).toHaveAttribute('aria-expanded','true');
 await page.keyboard.press('Escape');
 await expect(page.getByRole('button',{name:'Navigation öffnen'})).toHaveAttribute('aria-expanded','false');
});
