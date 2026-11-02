import { start } from './bridge.mjs';
import { sleep } from './shots_common.mjs';
const O = process.env.OUTP;
const s = await start({ width: 1600, height: 1000, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
// NPC Revoice
const rl = await rpc('revoice.list', {}); const rp = rl.find((x) => x.lines > 0) || rl[0];
if (rp) {
  const proj = await rpc('revoice.get', { id: rp.id });
  const sp = proj.speakers[0];
  const before = { engine: sp.engine, voiceId: sp.voiceId, voiceName: sp.voiceName };
  await rpc('revoice.speaker', { id: rp.id, key: sp.key, engine: 'fish', voiceId: 'fishvoice123', voiceName: 'fishvoice123' });
  await page.evaluate((id) => { localStorage.setItem('rv-tab:' + id, JSON.stringify('speakers')); return import('/app.js').then((m) => m.go('revoice', { id })); }, rp.id); await sleep(2500);
  await page.locator('details.voice-settings').first().evaluate((d) => { d.open = true; }).catch(() => {});
  await sleep(400); await page.screenshot({ path: `${O}/rv_speakers_fish.png` });
  await page.evaluate((id) => { localStorage.setItem('rv-tab:' + id, JSON.stringify('lines')); return import('/app.js').then((m) => m.go('revoice', { id })); }, rp.id); await sleep(2500);
  await page.screenshot({ path: `${O}/rv_lines_fish.png`, clip: { x: 0, y: 0, width: 1600, height: 330 } });
  await rpc('revoice.speaker', { id: rp.id, key: sp.key, engine: before.engine || '', voiceId: before.voiceId || '', voiceName: before.voiceName || '' });
} else console.log('no revoice project');
// Voiced Books
const bl = await rpc('books.list', {}).catch(() => []);
const bp = bl[0];
if (bp) {
  const proj = await rpc('books.get', { id: bp.id });
  const n = proj.narrators[0];
  if (n) {
    const before = { engine: n.engine, voiceId: n.voiceId, voiceName: n.voiceName };
    await rpc('books.voice', { id: bp.id, kind: 'narrator', narratorId: n.id, engine: 'cartesia', voiceId: 'cart-1', voiceName: 'cart-1' });
    await page.evaluate((id) => import('/app.js').then((m) => m.go('books', { id })), bp.id); await sleep(2500);
    await page.click('text=/^Voices/').catch(() => {}); await sleep(1500);
    await page.screenshot({ path: `${O}/books_cartesia.png` });
    await rpc('books.voice', { id: bp.id, kind: 'narrator', narratorId: n.id, engine: before.engine || 'elevenlabs', voiceId: before.voiceId || '', voiceName: before.voiceName || '' });
  } else console.log('no narrator');
} else console.log('no books project');
process.exit(0);
