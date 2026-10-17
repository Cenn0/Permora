// Real Keycloak/ASP.NET OIDC BFF browser smoke. Launched only by the isolated CI runner.
// The IdP test account is a disposable password-only identity; this is NOT positive MFA proof.
import { chromium } from '@playwright/test';

const base = 'http://127.0.0.1:5082';
const username = process.env.PERMORA_TEST_BROWSER_USERNAME;
const password = process.env.PERMORA_TEST_BROWSER_PASSWORD;
if (!username || !password) throw new Error('Missing disposable OIDC test identity');

const browser = await chromium.launch({headless: true});
try {
 const context = await browser.newContext();
 const page = await context.newPage();
 await page.goto(base + '/bff/login', {waitUntil: 'domcontentloaded', timeout: 45000});
 if (!page.url().startsWith('http://127.0.0.1:18080/realms/'))
  throw new Error('Expected a real Keycloak login page, not a fake identity');
 await page.locator('#username').fill(username);
 await page.locator('#password').fill(password);
 await page.locator('#kc-login').click();
 await page.waitForURL(url => url.origin === base, {timeout: 45000});
 console.log('PASS: Browser completed real Keycloak login and ASP.NET OIDC callback');

 const getSession = async () => page.evaluate(async () => {
  const r = await fetch('/bff/session', {credentials:'same-origin'});
  return {status:r.status, body:await r.json()};
 });
 const s = await getSession();
 if (s.status !== 200 || !s.body.authenticated || s.body.development || s.body.strongAuthentication)
  throw new Error('Password-only Keycloak login unexpectedly satisfied privileged assurance');
 console.log('PASS: Real OIDC BFF session remains low assurance');

 const read = await page.evaluate(async () => (await fetch('/api/v1/applications', {credentials:'same-origin'})).status);
 if (read !== 200) throw new Error('Real login could not read scoped application API');
 console.log('PASS: Read-only API accepts server-side session cookie');

 const write = await page.evaluate(async () => {
  const xsrf = document.cookie.split('; ').find(x => x.startsWith('XSRF-TOKEN='))?.split('=')[1];
  if (!xsrf) throw new Error('Missing XSRF-TOKEN cookie');
  const r = await fetch('/api/v1/applications', {
   method:'POST', credentials:'same-origin',
   headers:{'X-XSRF-TOKEN':decodeURIComponent(xsrf),'Content-Type':'application/json'},
   body: JSON.stringify({teamId:'10000000-0000-4000-8000-000000000001',
    realmKey:'unapproved',slug:'must-not-write',name:'MFA protected',
    description:'Negative test only',environment:'Test'})
  });
  return {status:r.status, title:(await r.json()).title};
 });
 if (write.status !== 403 || write.title !== 'step_up_required')
  throw new Error('Low assurance unexpectedly passed the privileged API gate');
 console.log('PASS: Real password-only OIDC session cannot change privileges');

 const logout = await page.evaluate(async () => {
  const xsrf = document.cookie.split('; ').find(x => x.startsWith('XSRF-TOKEN='))?.split('=')[1];
  return (await fetch('/bff/logout',{
   method:'POST',credentials:'same-origin',
   headers:{'Content-Type':'application/json','X-XSRF-TOKEN':decodeURIComponent(xsrf || '')},
   body:'{}'
  })).status;
 });
 if(logout!==204) throw new Error('Local server-side logout failed');
 const after=await getSession();
 if(after.status!==200||after.body.authenticated)
  throw new Error('Local logout retained authenticated browser session');
 console.log('PASS: BFF local logout invalidates the server-side session');

 // No production or real-user credentials are ever used.
} catch (error) {
 const pagePath = new URL((await browser.contexts()[0]?.pages()[0]?.url()) || base).pathname;
 console.error('OIDC browser smoke failed at route:', pagePath);
 throw error;
} finally {
 await browser.close();
}
