import { start } from './bridge.mjs';
import { sleep } from './shots_common.mjs';
const s = await start({ width: 1600, height: 1000, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const done = () => page.evaluate(() => new Promise((res) => import('/lib.js').then((l) => { const off = l.on('jobDone', (d) => { off && off(); res(d); }); })));
let st = await rpc('state', {});
const P = st.patches.find((x) => x.name === 'Skyrim') || st.patches[0];
const V = st.voicePacks.find((v) => v.id === P.voicePackId); const orig = JSON.parse(JSON.stringify(V));
await rpc('voice.save', { voice: { ...V, engine: 'openai', openAi: { ...V.openAi, baseUrl: 'http://127.0.0.1:5123', voice: 'default', instructions: 'Calm.' } } });
const patch = await rpc('patch.get', { id: P.id });
const lines = patch.lines.filter((l) => !l.omitted).slice(0, 2);
console.log('lines', lines.map((l) => l.spoken).join(' | '));
// put a tag on the first line
await rpc('line2.update', { id: P.id, lineId: lines[0].lineId, spoken: '[British Extremely Baleful] ' + lines[0].spoken.replace(/^\[[^\]]*\]\s*/, '') }).catch((e) => console.log('upd', e.message));
const w = done();
await rpc('patch.generate', { id: P.id, mode: 'all', lineIds: lines.map((l) => l.lineId) }).catch((e) => console.log('gen', e.message));
const r = await Promise.race([w, sleep(60000).then(() => 'timeout')]);
console.log('jobDone', JSON.stringify(r).slice(0, 300));
const after = await rpc('patch.get', { id: P.id });
for (const l of after.lines.filter((x) => lines.some((y) => y.lineId === x.lineId))) { const t = l.takes.find((x) => x.id === l.selectedTakeId); console.log('take', l.status, t && JSON.stringify(t.settings), l.lastError || ''); }
// DBReV export of WAV + LIP
await rpc('voice.save', { voice: { ...(await rpc('state', {})).voicePacks.find((v) => v.id === V.id), naming: 'Dbrev' } });
const ex = await rpc('patch.exportWavLip', { id: P.id, audioOnly: true }).catch((e) => ({ err: e.message }));
console.log('export', JSON.stringify(ex).slice(0, 300));
const plan = await rpc('patch.staleExports', { id: P.id }).catch((e) => e.message);
console.log('stale', JSON.stringify(plan).slice(0, 200));
await rpc('voice.save', { voice: { ...(await rpc('state', {})).voicePacks.find((v) => v.id === V.id), engine: orig.engine, openAi: orig.openAi, naming: orig.naming } });
process.exit(0);
