import { start } from '../bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const s = await start({ width: 1600, height: 950, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
// delay responses of slow methods by 2.5 s (backend has already done the work and saved)
await page.evaluate(() => {
  const slowIds = new Set(); const SLOW = /^fomod\.(addPatches|refresh)$/;
  const post = window.chrome.webview.postMessage;
  window.chrome.webview.postMessage = (m) => { if (SLOW.test(m.method)) slowIds.add(m.id); return post(m); };
  const del = window.__deliver;
  window.__deliver = (str) => { const d = JSON.parse(str); if (slowIds.has(d.id)) { setTimeout(() => del(str), 2500); } else del(str); };
});
const st = await rpc('state'); const vp = st.voicePacks[0];
const c = await rpc('fomod.create', { voicePackId: vp.id, name: 'race', rootFolder: 'D:\\fomodroot' });
const id = c.project.id;
await page.evaluate((id) => import('/app.js').then((m) => m.go('fomod', { id })), id); await sleep(1200);
await page.locator('.ftree .fnode').first().click(); await sleep(300);
await page.click('button:has-text("Add DBVO Patches")'); await sleep(500);
await page.locator('#modal input[type=checkbox]').first().check();
await page.click('#modal .btn.primary'); await sleep(200);
// user edits while the add is in flight
const nameIn = page.locator('.fedit input[type=text]').first();
await nameIn.fill('EDITED NAME');
await sleep(4000);
const ui = await page.evaluate(() => [document.querySelector('.fedit input[type=text]').value, document.querySelectorAll('.ftree .fnode.opt').length, document.querySelector('.saved').textContent]);
const disk = (await rpc('fomod.get', { id })).project;
console.log('UI name/options/saved:', ui);
console.log('DISK name/options:', disk.name, disk.steps.flatMap((s) => s.groups.flatMap((g) => g.options)).length);
await s.close(); process.exit(0);
