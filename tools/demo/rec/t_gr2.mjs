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
const waitJob = async () => { await sleep(500); for (let i = 0; i < 120; i++) { if (await page.locator('#job.hidden').count()) break; await sleep(500); } await sleep(800); };
const L = (p) => p.replace(/^([A-Z]):/, (m, d) => '/srv/dbvo/' + d).replace(/\\/g, '/');
const st = await rpc('state', {});
const vp = st.voicePacks[0];
const list = await rpc('grunts.list', {});
const spvs = list.find((x) => x.target === 'spvs');
await rpc('grunts.save', { id: spvs.id, voicePackId: vp.id });

// text-to-speech on a spoken event
await go('grunt', { id: spvs.id });
await page.locator('.gr-tile:has(.gr-label:text-is("Book"))').first().click(); await sleep(400);
await page.click('.inspector button:has-text("Text-to-speech")'); await sleep(300);
await page.fill('#modal textarea', 'Hmm, let me read this.');
await page.screenshot({ path: `${OUT}/gr-tts.png` });
await page.click('#modal .btn.primary'); await sleep(4000);
const ev = (await rpc('grunts.get', { id: spvs.id })).events.find((e) => e.key === 'Book');
ok('TTS line added', ev.lines.length === 1 && ev.lines[0].origin === 'generated', JSON.stringify(ev.lines.map((l) => [l.origin, l.text])));
ok('TTS text kept as subtitle', ev.lines[0].text === 'Hmm, let me read this.');

// drop files on a tile
s.dropNext(['D:\\Packs\\Neutral Grunts\\Sound\\FX\\PC Head Tracking\\Death\\01.wav', 'D:\\Packs\\Neutral Grunts\\Sound\\FX\\PC Head Tracking\\Death\\02.wav']);
const tile = page.locator('.gr-tile:has(.gr-label:text-is("Bleedout"))').first();
await tile.dispatchEvent('drop', { dataTransfer: await page.evaluateHandle(() => new DataTransfer()) }); await sleep(1500);
const bl = (await rpc('grunts.get', { id: spvs.id })).events.find((e) => e.key === 'Bleedout');
ok('Drop on a tile adds lines', bl.lines.length === 2, String(bl.lines.length));

// record (fake microphone)
await page.locator('.gr-tile:has(.gr-label:text-is("Jump"))').first().click(); await sleep(400);
await page.click('.inspector button:has-text("Record")'); await sleep(1500);
await page.click('#modal .rec-btn'); await sleep(1800); await page.click('#modal .rec-btn'); await sleep(1200);
await page.screenshot({ path: `${OUT}/gr-record.png` });
await page.click('#modal button:has-text("Keep my recording")'); await sleep(2500);
const jp = (await rpc('grunts.get', { id: spvs.id })).events.find((e) => e.key === 'Jump');
ok('Recording added', jp.lines.length === 1 && jp.lines[0].origin === 'recording', JSON.stringify(jp.lines.map((l) => l.origin)));

// overwrite confirmation: a foreign file at an export path
const so = L('D:\\Packs\\Out SPVS') + '/Sound/SPVS_AdditionalVoice/CustomVoiceType/FemaleCustom03/Book';
fs.mkdirSync(so, { recursive: true }); fs.writeFileSync(so + '/book_01.wav', 'x');
await go('grunt', { id: spvs.id });
await page.click('.view-head .export-btn'); await sleep(300); await page.click('.menu button:has-text("Export the voice pack")'); await sleep(2500);
ok('Asks before replacing a foreign file', await page.locator('#modal h2:has-text("Replace files")').count() === 1);
await page.screenshot({ path: `${OUT}/gr-overwrite.png` });
await page.click('#modal button:has-text("Replace them")'); await waitJob();
ok('Export after confirm', fs.statSync(so + '/book_01.wav').size > 100);
ok('Subtitle toml', fs.readFileSync(so + '/book_01.toml', 'utf8').includes('let me read this'));
await page.keyboard.press('Escape'); await sleep(300);

// missing file and relink
const pc = list.find((x) => x.target === 'pcht');
const tmp = L('D:\\Packs\\Moved'); fs.rmSync(tmp, { recursive: true, force: true }); fs.mkdirSync(tmp + '/x', { recursive: true });
const src = L('D:\\Packs\\Neutral Grunts\\Sound\\FX\\PC Head Tracking\\Breath\\Sprint');
fs.renameSync(src, tmp + '/x/Sprint');
await go('grunt', { id: pc.id });
ok('Missing files chip', await page.locator('.filters .chip:has-text("Missing files")').count() === 1);
await page.screenshot({ path: `${OUT}/gr-missing.png` });
const c = await rpc('grunts.check', { id: pc.id });
ok('Check blocks missing files', c.errors >= 5, String(c.errors));
const r = await rpc('grunts.relink', { id: pc.id, path: 'D:\\Packs\\Moved' });
ok('Relink finds them', r.found === 5, String(r.found));
fs.renameSync(tmp + '/x/Sprint', src);
await rpc('grunts.relink', { id: pc.id, path: 'D:\\Packs\\Neutral Grunts' });
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
