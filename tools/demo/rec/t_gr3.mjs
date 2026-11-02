import { start } from './bridge.mjs';
import { OUT, sleep } from './shots_common.mjs';
const s = await start({ width: 1600, height: 940, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html');
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const results = []; const ok = (n, c, x = '') => results.push(`${c ? 'PASS' : 'FAIL'}  ${n}${x ? '  · ' + x : ''}`);
await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1300); };
await rpc('grunts.paths', { pchtFolder: 'D:\\MO2\\mods\\PC Head Tracking and Voice Type SE', spvsFolder: 'D:\\MO2\\mods\\Smart Player Voice System' });
for (const target of ['spvs', 'pcht']) {
  const c = await rpc('grunts.create', { name: 'Vampira ' + target, target, slot: 'FemaleCustom01' });
  await go('grunt', { id: c.id });
  await page.click('.filters button:has-text("Import")'); await sleep(300);
  s.pick('D:\\MO2\\mods\\FEMALE PCHT - VAMPIRA');
  await page.click('.menu button:has-text("Import a voice pack folder")'); await sleep(6000);
  const toast = await page.locator('#toast').innerText().catch(() => '');
  ok(target + ' import toast', /file\(s\) added/.test(toast), toast);
  ok(target + ' blocks still shown', await page.locator('.gr-tile').count() > 90);
  const d = await rpc('grunts.get', { id: c.id });
  const withAudio = d.events.filter((e) => e.lines.length);
  ok(target + ' events with audio', withAudio.length > 40, `${withAudio.length} events, ${withAudio.reduce((n, e) => n + e.lines.length, 0)} lines`);
  await page.screenshot({ path: `${OUT}/gr-vampira-${target}.png` });
  const chk = await rpc('grunts.check', { id: c.id });
  console.log(target, 'errors', chk.issues.filter((i) => i.severity === 'error').slice(0, 5).map((i) => i.text));
}
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
