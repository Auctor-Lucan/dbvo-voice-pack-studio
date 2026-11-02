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

// Other Tools text and toggle
await page.click('#tools-btn'); await sleep(300);
ok('Other Tools: new Player Grunts text', await page.locator('.menu .menu-list li:has-text("Smart Player Voice System (SPVS)")').count() === 1);
await page.screenshot({ path: `${OUT}/gr5-tools.png`, clip: { x: 900, y: 0, width: 700, height: 420 } });
await page.click('#tools-btn'); await sleep(300);
ok('Second click closes the menu', await page.locator('.menu').count() === 0);
await page.click('#tools-btn'); await sleep(300);
ok('Third click opens it again', await page.locator('.menu').count() === 1);
await page.keyboard.press('Escape'); await page.mouse.click(5, 900); await sleep(200);

// project with a WAV line
const st = await rpc('state', {});
const c = await rpc('grunts.create', { name: 'Reverb test', target: 'spvs', slot: 'FemaleCustom04', voicePackId: st.voicePacks[0].id });
const wavWin = 'D:\\Packs\\Neutral Grunts\\Sound\\FX\\PC Head Tracking\\Death\\03.wav';
const wav = L(wavWin);
const orig = fs.readFileSync(wav);
let d = await rpc('grunts.get', { id: c.id });
const bl = d.events.find((e) => e.key === 'Book');
await rpc('grunts.addFiles', { id: c.id, eventId: bl.id, paths: [wavWin] });
await go('grunt', { id: c.id });
const imp = page.locator('.filters button:has-text("Import")');
await imp.click(); await sleep(250);
ok('Import menu opens', await page.locator('.menu').count() === 1);
await imp.click(); await sleep(250);
ok('Import menu closes on the second click', await page.locator('.menu').count() === 0);

await page.locator('.gr-tile:has(.gr-label:text-is("Book"))').first().click(); await sleep(500);
ok('Reverb toggle between length and arrows', await page.locator('.gr-line .gr-dur + .gr-rv').count() === 1);
const before = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === bl.id).lines[0].durationSeconds;
await page.click('.gr-line .gr-rv'); await sleep(1500);
let line = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === bl.id).lines[0];
ok('Reverb on: file changed', line.reverb && !fs.readFileSync(wav).equals(orig), JSON.stringify({ r: line.reverb }));
ok('Reverb on: tail added', line.durationSeconds > before + 0.5, `${before} → ${line.durationSeconds}`);
ok('Toggle shows on', await page.locator('.gr-line .gr-rv.on').count() === 1);
await page.screenshot({ path: `${OUT}/gr5-reverb.png` });
await page.click('.gr-line .gr-rv'); await sleep(1500);
line = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === bl.id).lines[0];
ok('Reverb off: original back byte for byte', !line.reverb && fs.readFileSync(wav).equals(orig));

// Apply subtitles
await page.click('.gr-line button:has-text("⋯")'); await sleep(250);
ok('Menu has Apply subtitles', await page.locator('.menu button:has-text("Apply subtitles")').count() === 1);
await page.click('.menu button:has-text("Apply subtitles")'); await sleep(400);
await page.click('#modal button:has-text("Suggest with Whisper")'); await sleep(2500);
const toastTxt = await page.locator('.toast').allInnerTexts().catch(() => []);
await page.screenshot({ path: `${OUT}/gr5-subtitle.png` });
await page.fill('#modal input[type=text]', 'I need to rest.');
await page.click('#modal .btn.primary'); await sleep(800);
line = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === bl.id).lines[0];
ok('Subtitle saved', line.text === 'I need to rest.', line.text + ' / whisper: ' + toastTxt.join('|'));

// subtitles toml
const r = await rpc('grunts.subtitles', { id: c.id, action: 'open' });
const toml = fs.readFileSync(L(r.path), 'utf8');
ok('toml lists the line', toml.includes('I need to rest.'));
await sleep(1100);
fs.writeFileSync(L(r.path), toml.replace('I need to rest.', 'Time to read.'));
await page.evaluate(() => window.dispatchEvent(new Event('focus'))); await sleep(1200);
line = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === bl.id).lines[0];
ok('toml edit read back on focus', line.text === 'Time to read.', line.text);
ok('Subtitles .toml button', await page.locator('.filters button:has-text("Subtitles .toml")').count() === 1);

// recorder: heading, hint, subtitle
await page.click('.inspector button:has-text("Record")'); await sleep(1500);
ok('Recorder: label in the box, description apart', (await page.locator('#modal .rec-line').innerText()).trim() === 'Book');
await page.click('#modal .rec-btn'); await sleep(1500); await page.click('#modal .rec-btn'); await sleep(2500);
await page.fill('#modal input[placeholder^="Subtitle"]', 'Let me see.');
await page.screenshot({ path: `${OUT}/gr5-record.png` });
await page.click('#modal button:has-text("Keep my recording")'); await sleep(2500);
const ev = (await rpc('grunts.get', { id: c.id })).events.find((e) => e.id === bl.id);
ok('Recording kept with its subtitle', ev.lines.some((l) => l.origin === 'recording' && l.text === 'Let me see.'), JSON.stringify(ev.lines.map((l) => [l.origin, l.text])));

// export: report shows (job-done)
const out = 'D:\\Packs\\Out Reverb';
fs.rmSync(L(out), { recursive: true, force: true });
await rpc('grunts.save', { id: c.id, outputFolder: out });
await go('grunt', { id: c.id });
await page.click('.view-head .export-btn'); await sleep(300); await page.click('.menu button:has-text("Export the voice pack")');
for (let i = 0; i < 40 && !(await page.locator('#modal h2:has-text("Export report")').count()); i++) await sleep(500);
ok('Export report shown after the export', await page.locator('#modal h2:has-text("Export report")').count() === 1);
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
