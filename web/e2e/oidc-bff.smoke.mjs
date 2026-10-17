// Real Keycloak/ASP.NET OIDC BFF browser smoke. Launched only by the isolated CI runner.
// Disposable Keycloak account: first LoA1 password, then genuine configured OTP/LoA2. Not corporate MFA certification.
import { chromium } from '@playwright/test';
import { createHmac } from 'node:crypto';

function decodeBase32(encoded) {
 let bits = 0, value = 0;
 const bytes = [];
 for (const symbol of encoded.toUpperCase().replace(/[^A-Z2-7]/g,'')) {
  const index = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(symbol);
  if (index < 0) throw new Error('Invalid temporary OTP enrollment secret');
  value = (value << 5) | index;
  bits += 5;
  if (bits >= 8) {
   bytes.push((value >>> (bits - 8)) & 0xff);
   bits -= 8;
  }
 }
 if (bytes.length < 10) throw new Error('Ephemeral OTP secret too short');
 return Buffer.from(bytes);
}
function currentTotp(secret) {
 const counter = Math.floor(Date.now() / 30000);
 const bytes = Buffer.alloc(8); bytes.writeBigUInt64BE(BigInt(counter));
 const mac = createHmac('sha1', secret).update(bytes).digest();
 const offset = mac[mac.length - 1] & 0xf;
 const truncated = mac.readUInt32BE(offset) & 0x7fffffff;
 return String(truncated % 1_000_000).padStart(6,'0');
}
async function nextTotpSlot() {
 const msToNextSlot = 30000 - (Date.now() % 30000) + 1000;
 await new Promise(resolve => setTimeout(resolve, msToNextSlot));
}

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
 // New disposable identity configures a real TOTP credential via Keycloak's
 // required action; never log/persist the enrollment secret or OTP.
 await page.locator('#mode-manual').waitFor({timeout: 30000});
 await page.locator('#mode-manual').click();
 const encodedSecret = (await page.locator('#kc-totp-secret-key').textContent())?.trim();
 if (!encodedSecret) throw new Error('Keycloak did not expose a temporary manual OTP enrollment secret');
 const otpSecret = decodeBase32(encodedSecret);
 await page.locator('#totp').fill(currentTotp(otpSecret));
 await page.locator('#userLabel').fill('Permora ephemeral CI OTP');
 await page.locator('#saveTOTPBtn').click();
 await page.waitForURL(url => url.origin === base, {timeout: 45000});
 console.log('PASS: Real Keycloak password login and disposable OTP enrollment completed');

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

 // Force a new authorization challenge for an essential ID-token ACR 2.
 // Keycloak's separate condition requires a genuinely configured OTP factor.
 await page.goto(base + '/bff/step-up', {waitUntil:'domcontentloaded', timeout:45000});
 // prompt=login may require another password challenge before the LoA2 OTP form.
 // Wait for a *visible* form instead of checking #username during the redirect race.
 try {
  await page.locator('#username:visible, #password:visible, #otp:visible').first().waitFor({timeout:30000});
 } catch (error) {
  // Diagnostic metadata only. Do not emit input values, OTP enrollment secrets,
  // cookies, redirect URLs or authentication tokens.
  const visibleInputs = await page.locator('input:visible').evaluateAll(elements =>
   elements.map(e=>({id:e.id,name:e.getAttribute('name'),type:e.type})));
  const titles = await page.locator('h1, h2').allTextContents();
  console.error('Unexpected Keycloak step-up form (metadata only):', JSON.stringify({
   path:new URL(page.url()).pathname,
   headings:titles.map(x=>x.trim().slice(0,80)),
   visibleInputs
  }));
  throw error;
 }
 if (await page.locator('#password:visible').count()) {
  if (await page.locator('#username:visible').count()) await page.locator('#username').fill(username);
  await page.locator('#password').fill(password);
  await page.locator('#kc-login').click();
 }
 // Successful LoA2 must come from a real OTP challenge; never accept a password-only
 // redirect or fallback as evidence of MFA.
 try {
  await page.locator('#otp:visible').waitFor({timeout:45000});
 } catch (error) {
  const errors = await page.locator('#input-error, #kc-error-message, .alert-error').allTextContents();
  const titles = await page.locator('h1, h2').allTextContents();
  console.error('Keycloak challenge (redacted):', JSON.stringify({
   path: new URL(page.url()).pathname,
   titles: titles.map(s=>s.trim().slice(0,120)),
   errorPresent: errors.some(s=>Boolean(s.trim())),
   usernameVisible: await page.locator('#username:visible').count() > 0,
   otpVisible: await page.locator('#otp:visible').count() > 0
  }));
  throw error;
 }
 // Keycloak rejects re-use of a one-time code from the initial enrollment slot.
 await nextTotpSlot();
 await page.locator('#otp').fill(currentTotp(otpSecret));
 await page.locator('#kc-login').click();
 await page.waitForURL(url => url.origin === base, {timeout:45000});
 const verified = await getSession();
 if (verified.status !== 200 || !verified.body.authenticated || !verified.body.strongAuthentication)
  throw new Error('Real OTP step-up did not produce fresh trusted ACR 2');
 console.log('PASS: Real OTP second factor yielded Keycloak LoA2 and trusted BFF strong session');

 const elevated = await page.evaluate(async () => {
  const xsrf = document.cookie.split('; ').find(x => x.startsWith('XSRF-TOKEN='))?.split('=')[1];
  const r = await fetch('/api/v1/applications', {
   method:'POST', credentials:'same-origin',
   headers:{'X-XSRF-TOKEN':decodeURIComponent(xsrf||''),'Content-Type':'application/json'},
   body: JSON.stringify({teamId:'10000000-0000-4000-8000-000000000001',
    realmKey:'unapproved',slug:'not-created',name:'Elevated',
    description:'Still denied by application ownership',environment:'Test'})
  });
  return {status:r.status, title:(await r.json()).title};
 });
 if (elevated.title === 'step_up_required' || elevated.status !== 404)
  throw new Error('Strong session did not pass the assurance gate before scoped app validation');
 console.log('PASS: LoA2 session passes the assurance gate but still respects application authorization');

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
