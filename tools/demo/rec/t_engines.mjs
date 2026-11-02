import { start } from './bridge.mjs';
import { sleep } from './shots_common.mjs';
const O = process.env.OUTP;
const s = await start({ width: 1600, height: 1000, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards'); await page.evaluate(() => localStorage.setItem('dbvo:seen:tags-context', 'true'));
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
let st = await rpc('state', {});
const P = st.patches.find((x) => x.name === 'Skyrim') || st.patches[0];
const V = st.voicePacks.find((v) => v.id === P.voicePackId);
const orig = JSON.parse(JSON.stringify(V));
console.log('voice keys', Object.keys(V).filter((k) => ['fish', 'inworld', 'cartesia', 'miniMax', 'openAi'].includes(k)).join(','));
// 1. Phase 2 with each cloud engine
for (const eng of ['fish', 'cartesia', 'minimax', 'openai', 'omnivoice', 'voxcpm']) {
  await rpc('voice.save', { voice: { ...V, engine: eng } });
  await page.evaluate((id) => import('/app.js').then((m) => m.go('patch', { id })), P.id); await sleep(2200);
  await page.screenshot({ path: `${O}/p2_${eng}.png`, clip: { x: 0, y: 0, width: 1600, height: 380 } });
  const note = await page.locator('.tag-note').first().textContent().catch(() => '(none)');
  console.log(eng, 'note:', (note || '').slice(0, 80));
}
// 2. key save + estimate
await rpc('voice.save', { voice: { ...V, engine: 'cartesia', cartesia: { ...V.cartesia, voiceId: 'abc-123' } } });
await rpc('settings.setEngineKey', { engine: 'cartesia', key: 'sk-test' });
st = await rpc('state', {});
console.log('keys', JSON.stringify(st.settings.engineKeys));
const est = await rpc('patch.estimate', { id: P.id, mode: 'missing' }).catch((e) => ({ err: e.message }));
console.log('estimate', JSON.stringify(est).slice(0, 400));
// 3. settings: voice engines section + voice pack editor
await page.evaluate(() => import('/app.js').then((m) => m.go('settings', { tab: 'general' }))); await sleep(1500);
const sec = page.locator('section:has-text("Voice engines"), .section:has-text("Voice engines")').first();
await sec.scrollIntoViewIfNeeded().catch(() => {}); await sleep(300);
await page.screenshot({ path: `${O}/settings_keys.png` });
await page.evaluate(() => import('/app.js').then((m) => m.go('settings', { tab: 'voices' }))); await sleep(1500);
await page.screenshot({ path: `${O}/settings_voice.png` });
// restore
await rpc('settings.setEngineKey', { engine: 'cartesia', key: '' });
await rpc('voice.save', { voice: orig });
process.exit(0);
