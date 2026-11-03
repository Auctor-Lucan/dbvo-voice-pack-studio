import { start } from '../bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const s = await start({ width: 1600, height: 950, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
await page.evaluate(() => { const add = document.addEventListener.bind(document); window.__jdCount = 0; document.addEventListener = (t, f, o) => { if (t === 'job-done') window.__jdCount++; return add(t, f, o); }; });
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const st = await rpc('state', {}); const vp = st.voicePacks[0];
const c = await rpc('fomod.create', { voicePackId: vp.id, name: 'perf', rootFolder: 'D:\\fomodroot' });
const proj = c.project;
const nid = () => Math.random().toString(36).slice(2, 10);
for (let p = 0; p < 4; p++) {
  const g = { id: nid(), name: 'G' + p, type: 'SelectAny', order: 'Explicit', options: [] };
  for (let i = 0; i < 50; i++) g.options.push({ id: nid(), name: `Opt ${p}-${i}`, description: 'hello world', image: '', files: [{ kind: 'file', source: 'hand.txt', destination: 'x' + i + '.txt', priority: 0 }], mode: 'Optional', plugins: [], requireAll: false, notUsableWhenMissing: true, patchId: null, modId: null, carriesJson: false });
  proj.steps.push({ id: nid(), name: 'Page ' + p, groupsOrder: 'Explicit', groups: [g] });
}
await rpc('fomod.save', { project: proj });
for (let k = 0; k < 5; k++) { await page.evaluate((id) => import('/app.js').then((m) => m.go('fomod', { id })), proj.id); await sleep(400); await page.evaluate(() => import('/app.js').then((m) => m.go('fomods'))); await sleep(300); }
await page.evaluate((id) => import('/app.js').then((m) => m.go('fomod', { id })), proj.id); await sleep(1000);
console.log('job-done listeners added after 6 opens:', await page.evaluate(() => window.__jdCount));
await page.locator('.ftree .fnode.opt').nth(120).click(); await sleep(300);
const nameIn = page.locator('.fedit input[type=text]').first();
await nameIn.click();
const t0 = Date.now(); await page.keyboard.type('abcdefghijklmnopqrst', { delay: 0 }); const t1 = Date.now();
console.log('20 keystrokes in option name (tree re-render each):', t1 - t0, 'ms; nodes', await page.locator('.ftree .fnode').count());
const tsel = await page.evaluate(async () => { const n = document.querySelectorAll('.ftree .fnode.opt'); const t = performance.now(); for (let i = 0; i < 20; i++) n[i] && document.querySelectorAll('.ftree .fnode.opt')[i].click(); return performance.now() - t; });
console.log('20 selects:', Math.round(tsel), 'ms');
// descArea: icon into a never-focused description
await page.locator('.ftree .fnode.opt').nth(3).click(); await sleep(300);
await page.locator('.fedit .icon-bar button').first().click(); await sleep(100);
console.log('description after icon click w/o focusing:', JSON.stringify(await page.locator('.fedit textarea').first().inputValue()));
await s.close(); process.exit(0);
