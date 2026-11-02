import { start } from './bridge.mjs';
import { OUT, sleep } from './shots_common.mjs';
const s = await start({ width: 1600, height: 940, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html');
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const results = []; const ok = (n, c, x = '') => results.push(`${c ? 'PASS' : 'FAIL'}  ${n}${x ? '  · ' + x : ''}`);
await page.waitForSelector('.home-cards');
ok('Landing text', await page.locator('.home-card b:text-is("I want to voice player events and shouts.")').count() === 1);
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1500); };
// effect preview
const gl = await rpc('grunts.list', {}); const g = gl.find((x) => x.lines > 0);
await go('grunt', { id: g.id });
await page.locator('.gr-tile.st-full').first().click(); await sleep(500);
await page.click('.gr-line button:has-text("⋯")'); await sleep(250);
await page.click('.menu button:has-text("Effect…")'); await sleep(500);
ok('Normal tile', await page.locator('#modal .fx-tile.normal').count() === 1);
await page.locator('#modal .fx-tile:has-text("Hall")').click(); await sleep(2000);
ok('Click plays the effect', (await page.locator('#modal .fx-play').innerText()).includes('Hall'), await page.locator('#modal .fx-play').innerText());
await page.locator('#modal .fx-tile.normal').click(); await sleep(1500);
ok('Normal plays', (await page.locator('#modal .fx-play').innerText()).includes('Normal'));
await page.screenshot({ path: `${OUT}/v85-fx.png` });
await page.keyboard.press('Escape'); await sleep(300);
// import spoken
const st = await rpc('state', {});
const [a, b] = st.patches;
if (a && b) {
  const pa = await rpc('patch.get', { id: a.id });
  const l = pa.lines.find((x) => x.takes.length === 0) || pa.lines[0];
  await rpc('line2.update', { id: a.id, lineId: l.lineId, action: 'spoken', value: '[sighs] ' + l.finalLine });
  await go('patch', { id: b.id });
  await page.click('button:has-text("Import ▾")'); await sleep(300);
  await page.click('.menu button:has-text("Spoken text from another patch")'); await sleep(1500);
  await page.screenshot({ path: `${OUT}/v85-import.png` });
  const txt = await page.locator('#modal .box').innerText();
  ok('Preview counts', /line\(s\) get/.test(txt), txt.slice(0, 120));
  await page.click('#modal .btn.primary'); await sleep(1500);
  const pb = await rpc('patch.get', { id: b.id });
  const m = pb.lines.find((x) => x.finalLine === l.finalLine);
  ok('Spoken text brought over', !m || m.spoken === '[sighs] ' + l.finalLine || m.spokenEdited, m ? m.spoken : 'not in b');
}
// books reverb switch
const bl = await rpc('books.list', {});
const pj = await rpc('books.get', { id: bl[0].id });
const stem = pj.books[0].stem;
const r1 = await rpc('books.book', { id: bl[0].id, stem, action: 'overrides', reverb: false });
ok('Book reverb off', r1.bookReverb === false, String(r1.bookReverb));
const r2 = await rpc('books.book', { id: bl[0].id, stem, action: 'overrides', reverb: true });
ok('Book reverb on', r2.bookReverb === true);
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
