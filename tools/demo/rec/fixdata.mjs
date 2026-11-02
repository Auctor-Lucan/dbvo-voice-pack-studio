import { start } from './bridge.mjs';
const s = await start({ width: 800, height: 600, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const st = await rpc('state', {}); const M = st.mods.find((m) => m.name === 'Skyrim');
const pl = (await rpc('mod.get', { id: M.id })).plugins[0];
for (const l of pl.lines.filter((l) => l.kind === 'Valid')) { if (l.fixApproved != null) await rpc('line1.update', { modId: M.id, plugin: pl.fileName, key: l.key, action: 'fix', value: null }); if (l.voiceApproved != null) await rpc('line1.update', { modId: M.id, plugin: pl.fileName, key: l.key, action: 'voice', value: null }); }
console.log('reset done'); process.exit(0);
