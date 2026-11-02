import { start } from './bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const s = await start({ width: 1600, height: 950, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards'); await page.evaluate(() => localStorage.setItem('dbvo:seen:tags-context', 'true'));
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const st = await rpc('state', {});
const P = st.patches.find((x) => x.name === 'Skyrim');
await page.evaluate((id) => import('/app.js').then((m) => m.go('patch', { id })), P.id); await sleep(2500);
const patch = await rpc('patch.get', { id: P.id });
const ls = patch.lines.filter((l) => !l.omitted && !l.excluded);
const row = (k) => page.locator(`.vrow[data-key="${k}"]`);
await rpc('patch.generate', { id: P.id, mode: 'selected', lineIds: ls.slice(0, 4).map((l) => l.lineId) }); // a running job
await sleep(300);
await row(ls[6].lineId).locator('.icon-bar .act-btn').first().click(); // queued
await sleep(800);
const q = await rpc('queue.list', {});
console.log('queued:', q.length, 'spinning row:', await row(ls[6].lineId).locator('.act-btn.spinning').count());
await rpc('queue.remove', { queueId: q[0].id || q[0].Id });
await sleep(600);
console.log('after remove spinning:', await row(ls[6].lineId).locator('.act-btn.spinning').count(), 'job bar hidden:', await page.locator('.job').first().evaluate((e) => e.classList.contains('hidden')).catch(() => 'n/a'));
for (let i = 0; i < 30; i++) { await sleep(500); if (!(await page.locator('.act-btn.spinning').count())) break; }
console.log('all spinners stopped:', await page.locator('.act-btn.spinning').count() === 0);
process.exit(0);
