import { start } from './bridge.mjs';
import { sleep } from './shots_common.mjs';
const s = await start({ width: 1400, height: 900, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const st = await rpc('state', {}); const P = st.patches.find((x) => x.name === 'Skyrim');
const np = await rpc('patch.create', { name: 'Skyrim copy', voicePackId: P.voicePackId, modIds: P.modIds, kind: 'tts', sourceFolders: [] });
const pa = await rpc('patch.get', { id: P.id });
console.log('sharedWith', JSON.stringify(pa.sharedWith));
await rpc('patch.delete', { id: np.id });
// cancel keeps audio already paid for
const lines = pa.lines.filter((l) => !l.excluded && !l.omitted).slice(0, 6).map((l) => l.lineId);
const before = Object.fromEntries(pa.lines.map((l) => [l.lineId, l.takes.length]));
await rpc('patch.generate', { id: P.id, mode: 'selected', lineIds: lines });
await sleep(1300); await rpc('job.cancel', {}); await sleep(6000);
const after = await rpc('patch.get', { id: P.id });
console.log('new takes after cancel', after.lines.filter((l) => lines.includes(l.lineId)).map((l) => l.takes.length - before[l.lineId]).join(','), 'statuses', after.lines.filter((l) => lines.includes(l.lineId)).map((l) => l.status).join(','));
process.exit(0);
