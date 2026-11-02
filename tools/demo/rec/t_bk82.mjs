import { start } from './bridge.mjs';
import { OUT, sleep } from './shots_common.mjs';
const s = await start({ width: 1600, height: 940, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html');
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const results = []; const ok = (n, c, x = '') => results.push(`${c ? 'PASS' : 'FAIL'}  ${n}${x ? '  · ' + x : ''}`);
await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1500); };
const bl = await rpc('books.list', {});
ok('Books projects in demo', bl.length > 0, String(bl.length));
if (bl.length) {
  const b = bl[0];
  const pj = await rpc('books.get', { id: b.id });
  const stem = (pj.books || [])[0]?.stem;
  const r = await rpc('books.book', { id: b.id, stem, action: 'overrides', voice: '', speed: 0.9, pause: 500 });
  ok('Book overrides saved', r.speed === 0.9 && r.pause === 500, JSON.stringify([r.speed, r.pause]));
  await go('book', { id: b.id });
  await page.locator('.vgrid .vrow, .vgrid [data-key]').first().dblclick().catch(() => {});
  await sleep(1500);
  const btn = page.locator('.book-editor button:has-text("This book")');
  ok('Editor shows This book', await btn.count() === 1);
  if (await btn.count()) { await btn.click(); await sleep(500); await page.screenshot({ path: `${OUT}/v82-book.png` }); await page.keyboard.press('Escape'); await sleep(500); await page.keyboard.press('Escape'); await sleep(1200); }
}
// settings back
await go('settings', { tab: 'general' });
await page.locator('.nav-item:has-text("Pronunciation")').click(); await sleep(800);
await page.click('#nav-back'); await sleep(1200);
ok('Back returns to the previous settings tab', await page.locator('.nav-item.active:has-text("General")').count() === 1);
const mods = (await rpc('state', {})).mods;
if (mods.length) { const m = await rpc('mod.get', { id: mods[0].id }); const pl = m.plugins[0]; if (pl) { const r = await rpc('mod.stringsDiff', { id: m.id, plugin: pl.fileName }); ok('stringsDiff answers', Array.isArray(r.diffs)); } }
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
