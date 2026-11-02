import { start } from './bridge.mjs';
import { sleep } from './shots_common.mjs';
const s = await start({ width: 1400, height: 900, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const st = await rpc('state', {}); const id = st.patches[0].id;
let pa = await rpc('patch.get', { id }); const l = pa.lines.find((x) => !x.excluded && !x.omitted);
await rpc('line2.update', { id, lineId: l.lineId, action: 'spoken', value: '[British Baleful] [sighs] Hello there, [laughs] friend.' });
for (const on of [true, false]) {
  await rpc('patch.saveMeta', { id, tagsAsContext: on });
  await rpc('patch.generate', { id, mode: 'selected', lineIds: [l.lineId] }); await sleep(7000);
}
pa = await rpc('patch.get', { id }); const ll = pa.lines.find((x) => x.lineId === l.lineId);
console.log(ll.takes.slice(-2).map((t) => JSON.stringify({ billed: t.settings.billedCharacters, ctx: t.settings.tagsAsContext })).join(' '));
process.exit(0);
