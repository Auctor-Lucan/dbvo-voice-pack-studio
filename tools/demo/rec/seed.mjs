// Seeds the demo library after `DemoHost --make-data` (and v1_setup.mjs + the start of v2_phases.mjs, which add the
// voice packs and the Skyrim mod): a Skyrim DBVO Patch for the first voice pack, generated with the fake TTS server.
import { start } from './bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const s = await start({ width: 1400, height: 900, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
let st = await rpc('state', {});
const vp = st.voicePacks[0], sky = st.mods.find((m) => m.name === 'Skyrim');
if (!vp || !sky) { console.log('Run v1_setup.mjs and v2_phases.mjs first (voice packs:', st.voicePacks.length, 'mods:', st.mods.map((m) => m.name), ')'); process.exit(1); }
let patch = st.patches.find((p) => p.name === 'Skyrim');
if (!patch) patch = await rpc('patch.create', { name: 'Skyrim', voicePackId: vp.id, modIds: [sky.id], kind: 'tts', sourceFolders: [] });
await page.evaluate(() => { window.__done = false; document.addEventListener('job-done', () => { window.__done = true; }); });
await rpc('patch.generate', { id: patch.id, mode: 'pending', lineIds: [] });
for (let i = 0; i < 240 && !(await page.evaluate(() => window.__done)); i++) await sleep(500);
st = await rpc('state', {});
console.log('patches', st.patches.map((p) => `${p.name} (${p.voicePackId})`).join(', '));
await s.close(); process.exit(0);
