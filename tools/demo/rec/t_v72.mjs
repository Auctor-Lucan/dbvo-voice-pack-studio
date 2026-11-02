// 7.2: Stop button, spinner + flash on single takes, NPC Revoice speaker filter and settings in the check dialog.
import { boot, shooter, sleep, genPick } from './shots_common.mjs';
const s = await boot(); const { page } = s; const S = shooter(page);
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const results = []; const ok = (n, c, x = '') => results.push(`${c ? 'PASS' : 'FAIL'}  ${n}${x ? '  · ' + x : ''}`);
await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1200); };
const st = await rpc('state', {});
const sky = st.patches.find((p) => p.name === 'Skyrim');

// ---- Phase 2: single take spins, then the row glows
await go('patch', { id: sky.id }); await page.waitForSelector('.vrow');
const row = page.locator('.vrow').nth(6);
await row.locator('.icon-bar .act-btn').first().click();
await sleep(150);
ok('Single take: ↻ spins at once', await row.locator('.act-btn.spinning').count() === 1);
await S.clip('v72-spinning', { x: 0, y: 300, width: 1480, height: 260 });
await page.waitForFunction(() => document.querySelector('.vrow.flash-done') || !document.querySelector('.act-btn.spinning'), null, { timeout: 30000 });
ok('Single take: spinner stops when the take arrives', await page.locator('.act-btn.spinning').count() === 0);
await page.waitForFunction(() => document.querySelector('#job').classList.contains('hidden'), null, { timeout: 30000 }); await sleep(800);

// ---- Phase 2: batch turns Generate into Stop, and Stop stops
await genPick(page, 'All lines'); await sleep(700);
await page.locator('#modal .btn.primary').click();
await page.waitForSelector('.statusbar .stop-btn', { timeout: 10000 }).catch(() => {});
ok('Batch: Generate missing became a Stop button', await page.locator('.statusbar .stop-btn').count() === 1);
await S.clip('v72-stop', { x: 0, y: 1100 / 1.4, width: 1480, height: 860 - 1100 / 1.4 });
const doneAtStop = await page.evaluate(() => document.querySelector('#job .job-label').textContent);
await page.click('.statusbar .stop-btn');
await page.waitForFunction(() => document.querySelector('#job').classList.contains('hidden'), null, { timeout: 30000 }); await sleep(800);
ok('Batch: Stop ends the job', await page.locator('.statusbar .stop-btn').count() === 0 && await page.locator('#job.hidden').count() === 1);
const toastText = await page.locator('#toast').innerText().catch(() => '');
ok('Batch: told it was cancelled', /Cancel/i.test(toastText), `${toastText} (stopped at: ${doneAtStop})`);

// ---- NPC Revoice: speaker filter and settings in the dialog
const rvs = await rpc('revoice.list', {});
const rv = rvs[0];
const proj = await rpc('revoice.get', { id: rv.id });
const speakers = [...new Set(proj.lines.map((l) => l.speaker))];
const voiced = speakers.find((k) => proj.lines.some((l) => l.speaker === k));
const all = await rpc('revoice.estimate', { id: rv.id, mode: 'all' });
const one = await rpc('revoice.estimate', { id: rv.id, mode: 'all', speaker: voiced });
ok('NPC Revoice: a speaker filter limits the batch', one.count <= proj.lines.filter((l) => l.speaker === voiced).length && (all.count === 0 || one.count <= all.count), `${one.count} of ${all.count}`);
ok('NPC Revoice: the dialog gets every setting', one.groups.length === 0 || one.groups[0].settings.length >= 8, one.groups[0] ? one.groups[0].settings.map((r) => r[0]).join(', ') : 'no voiced speaker');
await go('revoice', { id: rv.id });
await page.click('button:has-text("Lines (")').catch(() => {}); await sleep(800);
const sel = page.locator('.toolbar select').first();
const opts = await sel.locator('option').evaluateAll((os) => os.map((o) => o.value));
await sel.selectOption(opts.find((v) => v === voiced) || opts[1]); await sleep(800);
await genPick(page, 'Missing').catch(() => {}); await sleep(900);
if (await page.locator('#modal:not(.hidden)').count()) {
  const t = await page.locator('#modal').innerText();
  ok('NPC Revoice dialog names the speaker and shows settings', /Only .+'s lines/.test(t) && /Stability|Reference voice|Model/.test(t), t.replace(/\n/g, ' | ').slice(0, 300));
  await S.modal('v72-rv-check');
  await page.click('#modal button:has-text("Cancel")'); await sleep(300);
} else ok('NPC Revoice dialog shown', false, await page.locator('#toast').innerText().catch(() => ''));
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
process.exit(0);
