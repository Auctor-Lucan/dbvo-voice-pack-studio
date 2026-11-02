import { start } from '../bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const s = await start({ width: 1400, height: 900, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const st = await rpc('state', {});
const M = st.mods.find((m) => m.name === 'Skyrim');
const mod = await rpc('mod.get', { id: M.id });
const pl = mod.plugins[0];
const vis = pl.lines.filter((l) => l.status !== 'Removed' && l.kind === 'Valid').slice(0, 3);
console.log('before', vis.map((l) => JSON.stringify([l.final, l.original, l.inFix])).join(' | '));
// a line with a non-breaking space (as typed in some mods' text)
await rpc('line1.update', { modId: mod.id, plugin: pl.fileName, key: vis[0].key, action: 'setFinal', value: 'Hello there friend.' });
await page.evaluate((id) => import('/app.js').then((m) => m.go('mod', { id })), M.id); await sleep(1500);
// tick first three rows
await page.getByText('Proofread…').click(); await sleep(500);
await page.getByRole('button', { name: 'Start →' }).click(); await sleep(150);
const chip = async () => page.evaluate(() => { const c = document.querySelector('.busy-chip'); return c ? (c.classList.contains('hidden') ? 'hidden' : c.textContent) : 'none'; });
console.log('chip at start', await chip());
await sleep(500);
console.log('chip after 650ms', await chip(), 'cursor busy:', await page.evaluate(() => document.documentElement.classList.contains('app-busy')));
await sleep(1500);
console.log('line1 editor:', JSON.stringify(await page.locator('.pr-edit').textContent()), 'foot:', await page.locator('.pr-step .btn.primary').last().textContent());
process.exit(0);
