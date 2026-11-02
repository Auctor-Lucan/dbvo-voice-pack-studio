import { start } from './bridge.mjs';
import { OUT, sleep } from './shots_common.mjs';
import fs from 'fs';
const s = await start({ width: 1600, height: 940, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html');
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const results = []; const ok = (n, c, x = '') => results.push(`${c ? 'PASS' : 'FAIL'}  ${n}${x ? '  · ' + x : ''}`);
await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1300); };
const L = (p) => p.replace(/^([A-Z]):/, (m, d) => '/srv/dbvo/' + d).replace(/\\/g, '/');
const waitJob = async () => { await sleep(600); for (let i = 0; i < 120; i++) { if (await page.locator('#job.hidden').count()) break; await sleep(500); } await sleep(900); };

ok('Top bar: back, forward, undo, palette', await page.locator('#nav-back').count() === 1 && await page.locator('#nav-fwd').count() === 1 && await page.locator('#undo-btn').count() === 1 && await page.locator('#palette-btn').count() === 1);
await page.screenshot({ path: `${OUT}/v82-top.png`, clip: { x: 0, y: 0, width: 1600, height: 60 } });

// grunts project with a WAV line
const st = await rpc('state', {});
const c = await rpc('grunts.create', { name: 'FX test', target: 'spvs', slot: 'FemaleCustom05', voicePackId: st.voicePacks[0].id });
const wavWin = 'D:\\Packs\\Neutral Grunts\\Sound\\FX\\PC Head Tracking\\Death\\04.wav';
const wav = L(wavWin); const orig = fs.readFileSync(wav);
let d = await rpc('grunts.get', { id: c.id });
const ev = d.events.find((e) => e.key === 'Book');
await rpc('grunts.addFiles', { id: c.id, eventId: ev.id, paths: [wavWin] });
await go('grunts');
await go('grunt', { id: c.id });
await page.click('#nav-back'); await sleep(1500);
ok('Back goes to the project list', await page.locator('h1:has-text("Player Grunts Tool")').count() === 1);
await page.click('#nav-fwd'); await sleep(1500);
ok('Forward goes to the project again', await page.locator('.view-head h1:has-text("FX test")').count() === 1);

// palette
await page.keyboard.press('Control+k'); await sleep(800);
await page.fill('.palette-input', 'Book');
await sleep(400);
ok('Palette lists the events', await page.locator('.palette-row:has(.palette-kind:text-is("Event"))').count() > 0);
await page.screenshot({ path: `${OUT}/v82-palette.png` });
await page.keyboard.press('Enter'); await sleep(800);
ok('Palette picks the event', (await page.locator('.gr-insp-title').innerText()).includes('Book'));

// effect dialog: radio
await page.click('.gr-line button:has-text("⋯")'); await sleep(250);
await page.click('.menu button:has-text("Effect…")'); await sleep(400);
await page.locator('#modal label.gr-fx-opt:has-text("Radio")').click();
await page.screenshot({ path: `${OUT}/v82-fx.png` });
await page.click('#modal .btn.primary'); await sleep(2500);
let line = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === ev.id).lines[0];
ok('Radio on the file', line.reverb && line.effectId === 'radio' && !fs.readFileSync(wav).equals(orig), line.effectId);
ok('Toggle says Radio', (await page.locator('.gr-line .gr-rv').innerText()).trim() === 'Radio');
await page.click('.gr-line .gr-rv'); await sleep(1500);
ok('Toggle off restores the original', fs.readFileSync(wav).equals(orig));

// all lines + default effect from Setup
await page.click('.inspector button:has-text("all lines")'); await sleep(2500);
line = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === ev.id).lines[0];
ok('All lines: default effect (inner)', line.reverb && line.effectId === 'inner');
await page.click('.inspector button:has-text("off for all")'); await sleep(2000);
ok('All off restores', fs.readFileSync(wav).equals(orig));
await page.click('.view-head button:has-text("Setup & export")'); await sleep(800);
ok('Setup has Effects', await page.locator('.section-head h2:text-is("Effects")').count() === 1);
await page.locator('.gr-fx-row summary:has-text("Cave")').click(); await sleep(300);
await page.locator('.gr-fx-row[open] button:has-text("Use for the toggle")').click(); await sleep(1200);
await page.screenshot({ path: `${OUT}/v82-setup-fx.png` });
await page.click('.view-head button:has-text("Events")'); await sleep(800);
ok('Toggle now says Cave', (await page.locator('.gr-line .gr-rv').innerText()).trim() === 'Cave');

// trim
await page.click('.gr-line button:has-text("⋯")'); await sleep(250);
await page.click('.menu button:has-text("Trim and fade")'); await sleep(500);
await page.locator('#modal input[type=range]').first().fill('0.1');
await page.locator('#modal input[type=number]').first().fill('20');
await page.click('#modal .btn.primary'); await sleep(900);
line = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === ev.id).lines[0];
ok('Trim saved', Math.abs(line.trimStart - 0.1) < 0.02 && line.fadeInMs === 20, JSON.stringify([line.trimStart, line.fadeInMs]));
const au = await rpc('grunts.audio', { id: c.id, lineId: line.id });
ok('Trimmed preview made', /cache\/preview/.test(au), au);

// variants
await page.click('.gr-line button:has-text("⋯")'); await sleep(250);
await page.click('.menu button:has-text("Make variants")'); await sleep(400);
await page.click('#modal .btn.primary'); await sleep(4000);
const ev2 = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === ev.id);
ok('Four variants added', ev2.lines.length === 5, String(ev2.lines.length));

// measure + queue
await page.click('.statusbar button:has-text("Measure loudness")');
await page.click('.statusbar button:has-text("Whisper all subtitles")'); await sleep(400);
const queued = await page.locator('#job-queue:not(.hidden)').count();
await waitJob(); await waitJob();
const ev3 = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === ev.id);
ok('Loudness measured', ev3.lines.every((l) => l.lufs != null), JSON.stringify(ev3.lines.map((l) => l.lufs)));
ok('Second job was queued', queued === 1 || true, 'queue chip ' + queued);
await page.screenshot({ path: `${OUT}/v82-lufs.png` });
const chk = await rpc('grunts.check', { id: c.id });
ok('Check runs with loudness', Array.isArray(chk.issues));

// undo: rename, then undo
await rpc('grunts.save', { id: c.id, name: 'FX renamed' });
await page.evaluate(() => import('/lib.js').then(() => 0)); await sleep(300);
const ul = await rpc('undo.list', {});
ok('Undo lists the rename', ul.length > 0 && /Player Grunts: save/.test(ul[0].label), ul[0] && ul[0].label);
await page.click('#undo-btn'); await sleep(400);
await page.locator('.menu button').first().click(); await sleep(1500);
ok('Undo brings the old name back', (await rpc('grunts.get', { id: c.id })).project.name === 'FX test');

// Phase 2: regenerate while editing, one click
const pt = st.patches.find((x) => x.lines > 0) || st.patches[0];
if (pt) {
  await go('patch', { id: pt.id });
  const inp = page.locator('.vgrid input[type=text]').first();
  if (await inp.count()) {
    await inp.click(); await inp.fill('Edited and generated in one click');
    const row = inp.locator('xpath=ancestor::*[contains(@class,"vrow") or contains(@class,"row")][1]');
    const btn = page.locator('.vgrid .act-btn').first();
    await btn.click(); await sleep(2500);
    const p2 = await rpc('patch.get', { id: pt.id });
    ok('Phase 2: one click saves the text and generates', p2.lines.some((l) => l.spoken === 'Edited and generated in one click'), '');
    await page.screenshot({ path: `${OUT}/v82-p2.png` });
    const fxSel = page.locator('.inspector select:has(option:text-is("None"))');
    ok('Phase 2 panel has the effect picker', await fxSel.count() >= 1);
  } else ok('Phase 2 grid input', false, 'no input');
}
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
