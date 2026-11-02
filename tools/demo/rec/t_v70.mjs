// Build 7.0 end-to-end: check takes, review, approved-only export, CSV, earlier pack import + lossless build, recording, BSA.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { boot, shooter, sleep } from './shots_common.mjs';
// wait until the job has started (the backup before an import delays its first progress) and then finished
async function waitJob(page, t = 60000) {
  await page.waitForFunction(() => !document.querySelector('#job')?.classList.contains('hidden'), null, { timeout: 15000 }).catch(() => {});
  await page.waitForFunction(() => document.querySelector('#job')?.classList.contains('hidden'), null, { timeout: t });
  await sleep(700);
}
const LIB = '/srv/dbvo/D/DBVO Studio Library';

// Build .fuz: answer any "keep existing" questions until the report shows.
async function buildAndReport(page) {
  await page.click('button:has-text("Export ▾")'); await sleep(300); await page.click('text=Build .fuz into the final folder');
  const end = Date.now() + 90000;
  while (Date.now() < end) {
    await sleep(400);
    if (await page.locator('#modal:not(.hidden) .kv').count() && await page.evaluate(() => document.querySelector('#job').classList.contains('hidden'))) return page.locator('#modal .kv').innerText();
    const keep = page.locator('#modal:not(.hidden) button:has-text("Keep")');
    if (await keep.count()) await keep.first().click();
  }
  await page.screenshot({ path: '/tmp/fail.png' }); console.log('MODAL', await page.locator('#modal').innerText().catch(() => ''), 'JOB', await page.locator('#job').innerText());
  throw new Error('no .fuz report');
}
const results = [];
const ok = (name, cond, extra = '') => { results.push(`${cond ? 'PASS' : 'FAIL'}  ${name}${extra ? '  · ' + extra : ''}`); };
const patchJson = (name) => {
  for (const d of fs.readdirSync(path.join(LIB, 'patches'))) {
    const f = path.join(LIB, 'patches', d, 'patch.json');
    if (!fs.existsSync(f)) continue;
    const j = JSON.parse(fs.readFileSync(f, 'utf8'));
    if (j.Name === name) return { id: d, j, dir: path.join(LIB, 'patches', d) };
  }
};
const lin = (p) => p.replace(/\\/g, '/');
const sel = (l) => l.Takes.find((t) => t.Id === l.SelectedTakeId);

process.on('uncaughtException', (e) => { console.log(results.join('\n')); console.log('CRASH', e.message); process.exit(1); });
process.on('unhandledRejection', (e) => { console.log(results.join('\n')); console.log('CRASH', e && e.message); process.exit(1); });
const s = await boot(); const { page } = s; const S = shooter(page);
const errs = []; page.on('pageerror', (e) => errs.push(e.message)); page.on('console', (m) => { if (m.type() === 'error') console.log('CONSOLE', m.text()); });
await page.waitForSelector('.home-cards');
const openPatch = async (id) => { await page.evaluate((id) => import('/app.js').then((m) => m.go('patch', { id })), id); await page.waitForSelector('.vrow'); await sleep(900); };
const MENU = { 'Check takes': null, 'Import an earlier voice pack': ['Import ▾', 'An earlier voice pack…'], 'Spoken text to a spreadsheet': ['Export ▾', 'Spoken text to a spreadsheet (CSV)…'], 'Spoken text from a spreadsheet': ['Import ▾', 'Spoken text from a spreadsheet (CSV)…'] };
const more = async (label) => { const m = MENU[label]; if (!m) { await page.click(`.statusbar button:has-text("${label}")`); await sleep(600); return; } await page.click(`.statusbar button:has-text("${m[0]}")`); await sleep(300); await page.click(`.menu >> text=${m[1]}`); await sleep(600); };

// ---------------- check takes
console.log('STEP check takes');
let P = patchJson('Skyrim');
const lines = P.j.Lines.filter((l) => sel(l) && !l.Excluded);
const wrong = lines[0], quiet = lines[1];
fs.writeFileSync(path.join(P.dir, lin(sel(wrong).WavPath)) + '.heard', 'Where can I find a good inn around here?');
const qwav = path.join(P.dir, lin(sel(quiet).WavPath));
execFileSync('ffmpeg', ['-v', 'error', '-y', '-i', qwav, '-af', 'volume=-35dB', qwav + '.q.wav']); fs.renameSync(qwav + '.q.wav', qwav);
await openPatch(P.id);
await more('Check takes');
await S.modal('v70-qa-confirm');
await page.locator('#modal .btn.primary').click();
await waitJob(page, 120000);
P = patchJson('Skyrim');
const qaW = sel(P.j.Lines.find((l) => l.LineId === wrong.LineId)).Qa, qaQ = sel(P.j.Lines.find((l) => l.LineId === quiet.LineId)).Qa;
const others = P.j.Lines.filter((l) => sel(l) && !l.Excluded && l.LineId !== wrong.LineId && l.LineId !== quiet.LineId).map((l) => sel(l).Qa);
ok('QA flags the wrong words', qaW && qaW.Flags.includes('words'), `score ${qaW && qaW.Score}`);
ok('QA flags the quiet take', qaQ && qaQ.Flags.includes('quiet'), `${qaQ && qaQ.Lufs} LUFS`);
ok('QA passes the other takes', others.every((q) => q && q.Flags.length === 0), `${others.filter((q) => q && q.Flags.length).length} of ${others.length} flagged: ${[...new Set(others.flatMap((q) => q ? q.Flags : ['none']))].join(',')}`);
await S.full('v70-qa');
await page.click('.chip:has-text("QA flagged")'); await sleep(500);
await page.locator('.vrow span.dim').first().click(); await sleep(500);
await S.full('v70-qa-flagged');
ok('QA flagged filter shows 2 rows', (await page.locator('.vrow').count()) === 2);
await page.click('.chip:has-text("All")'); await sleep(400);

// ---------------- review
console.log('STEP review');
await page.click('button:has-text("▶ Review")'); await sleep(600);
for (const k of ['a', 'a', 'f']) { await page.keyboard.press(k); await sleep(700); }
await page.keyboard.press(' '); await sleep(300);
await page.evaluate(() => document.getElementById('toast').classList.add('hidden')); await S.full('v70-review');
await page.keyboard.press('Escape'); await sleep(400);
P = patchJson('Skyrim');
ok('Review: A approved 2 lines', P.j.Lines.filter((l) => l.Approved).length === 2);
ok('Review: F flagged a line', P.j.Lines.some((l) => l.Marked));
ok('Review: Space did not tick rows', (await page.locator('input.rowcheck:checked').count()) === 0);

// ---------------- approved-only export
console.log('STEP approved-only export');
await page.click('button:has-text("Folders ▾")').then(() => sleep(300)).then(() => page.click('.menu >> text=Folders & options…')); await sleep(600);
await page.click('#modal label.switch:has-text("Only lines approved in Review")'); await sleep(200);
await page.click('#modal button:has-text("Save")'); await sleep(900);
P = patchJson('Skyrim');
ok('Approved-only export saved', P.j.ExportApprovedOnly === true);
const wl = '/srv/dbvo/D/DBVO/Sample Voice/WAV + LIP/Skyrim-approved';
fs.rmSync(wl, { recursive: true, force: true });
await page.evaluate(async ({ id, wl }) => { const { rpc } = await import('/lib.js'); await rpc('patch.saveMeta', { id, wavLipFolder: wl.replace('/srv/dbvo/D', 'D:').replace(/\//g, '\\') }); }, { id: P.id, wl });
await page.click('button:has-text("Export ▾")'); await sleep(300); await page.click('text=WAV + LIP to the WAV + LIP folder'); await sleep(1200);
for (let i = 0; i < 3 && await page.locator('#modal:not(.hidden)').count(); i++) { await page.locator('#modal .btn.primary').first().click().catch(() => {}); await sleep(700); }
const exported = fs.existsSync(wl) ? fs.readdirSync(wl).filter((f) => f.endsWith('.wav')).length : -1;
ok('Approved-only export wrote only the 2 approved lines', exported === 2, `${exported} WAV`);
await page.evaluate(async (id) => { const { rpc } = await import('/lib.js'); await rpc('patch.saveMeta', { id, exportApprovedOnly: false }); }, P.id);

// ---------------- CSV round trip (saved, edited like Swedish Excel with ;)
console.log('STEP CSV round trip (saved, edited like Swedish Excel with ;)');
await openPatch(P.id);
s.pick('D:\\DBVO\\Skyrim spoken.csv');
await more('Spoken text to a spreadsheet');
await page.locator('#modal .btn.primary').click(); await sleep(400);
const csvPath = '/srv/dbvo/D/DBVO/Skyrim spoken.csv';
let csv = fs.readFileSync(csvPath, 'utf8');
ok('CSV saved with BOM and a header', csv.charCodeAt(0) === 0xFEFF && csv.includes('"Spoken text"'));
const rowsCsv = csv.split('\r\n');
const target1 = P.j.Lines[2], target2 = P.j.Lines[3];
csv = csv.replace(`"${target1.Spoken}"\r\n`, `"[whispers] ${target1.Spoken}"\r\n`).replace(`"${target2.Spoken}"\r\n`, `"${target2.Spoken} Quickly, now."\r\n`).replace(/","/g, '";"');
fs.writeFileSync(csvPath, csv);
s.pick('D:\\DBVO\\Skyrim spoken.csv');
await more('Spoken text from a spreadsheet');
await S.modal('v70-csv');
const changeCount = await page.locator('#modal .csv-row').count();
ok('CSV import found exactly the 2 edits (semicolons)', changeCount === 2, `${changeCount} changes of ${rowsCsv.length - 2} rows`);
await page.locator('#modal .btn.primary').click(); await sleep(900);
P = patchJson('Skyrim');
ok('CSV edits applied', P.j.Lines[2].Spoken.startsWith('[whispers]') && P.j.Lines[3].Spoken.endsWith('Quickly, now.'));

// ---------------- earlier pack import + lossless .fuz build
console.log('STEP earlier pack import + lossless .fuz build');
let D2 = patchJson('Skyrim DLCs');
const final = '/srv/dbvo/D/DBVO/Sample Voice/FOMOD/Skyrim DLCs/Sound/DBVO/SampleVoice';
const pick4 = D2.j.Lines.filter((l) => sel(l)).slice(0, 4);
const old = '/srv/dbvo/D/Old pack/Sound/DBVO/OldVoice/sub';
fs.rmSync('/srv/dbvo/D/Old pack', { recursive: true, force: true }); fs.mkdirSync(old, { recursive: true });
const legacyName = (line) => { let t = line.trim(); const c = t.indexOf(' ('); if (c >= 0) t = t.slice(0, c); return t.replace(/[\s\\/:*?"<>|]/g, '_'); };
const origBytes = {};
for (const l of pick4) {
  const stem = path.basename(lin(sel(l).WavPath), '.wav');
  const src = path.join(final, stem + '.fuz');
  const dst = path.join(old, legacyName(l.FinalLine) + '.fuz');
  fs.copyFileSync(src, dst); origBytes[stem] = fs.readFileSync(dst);
}
for (let i = 0; i < 3; i++) fs.copyFileSync(path.join(old, fs.readdirSync(old)[0]), path.join(old, `Another_mods_line_${i}.fuz`));
await openPatch(D2.id);
await page.evaluate(async ({ id, ids }) => { const { rpc } = await import('/lib.js'); for (const lineId of ids) { const p = await rpc('patch.get', { id }); const l = p.lines.find((x) => x.lineId === lineId); for (const t of l.takes) await rpc('line2.update', { id, lineId, action: 'deleteTake', value: t.id }); } }, { id: D2.id, ids: pick4.map((l) => l.LineId) });
for (const l of pick4) { const stem = path.basename(lin(sel(l).WavPath), '.wav'); fs.rmSync(path.join(final, stem + '.fuz'), { force: true }); }
await openPatch(D2.id);
s.pick('D:\\Old pack');
await more('Import an earlier voice pack');
await S.modal('v70-legacy');
console.log('JOB BEFORE IMPORT', await page.evaluate(async () => { const { rpc } = await import('/lib.js'); const st = await rpc('state'); return st.jobRunning + ' | ' + document.querySelector('#job .job-label').textContent + ' | hidden=' + document.querySelector('#job').classList.contains('hidden'); }));
const kv = await page.locator('#modal .kv').innerText();
ok('Pack scan: 4 found, 3 unmatched', /Lines found in the pack\s+4/.test(kv) && /match no line\s+3/.test(kv), kv.replace(/\n/g, ' | '));
await page.locator('#modal .btn.primary').click();
await waitJob(page, 60000); await sleep(600);
D2 = patchJson('Skyrim DLCs');
const imported = D2.j.Lines.filter((l) => pick4.some((p) => p.LineId === l.LineId) && sel(l) && sel(l).OriginalFuz);
ok('Import added 4 takes with the original .fuz kept', imported.length === 4);
const rep = await buildAndReport(page);
await S.modal('v70-fuz-report');
ok('Build reused the 4 imported .fuz', /kept as they were\s+4/.test(rep), rep.replace(/\n/g, ' | '));
let same = 0; for (const [stem, b] of Object.entries(origBytes)) { const f = path.join(final, stem + '.fuz'); if (fs.existsSync(f) && Buffer.compare(fs.readFileSync(f), b) === 0) same++; }
ok('Rebuilt .fuz are byte-identical to the old pack', same === 4, `${same}/4`);
await page.locator('#modal .btn.primary').first().click(); await sleep(400);
// second build: everything from the cache, nothing encoded
await page.evaluate(async (id) => { const { rpc } = await import('/lib.js'); await rpc('patch.saveMeta', { id, overwriteFuz: true }); }, D2.id);
const fuzFolder = final;
for (const f of fs.readdirSync(fuzFolder)) fs.rmSync(path.join(fuzFolder, f));
const rep2 = await buildAndReport(page);
ok('Rebuild comes from the cache', /From the .fuz cache[^\n]*\s+(\d+)/.test(rep2) && +rep2.match(/From the .fuz cache[^\n]*\s+(\d+)/)[1] >= 10, rep2.replace(/\n/g, ' | '));
await page.locator('#modal .btn.primary').first().click(); await sleep(400);

// ---------------- recording (fake microphone) + Voice Changer
console.log('STEP recording (fake microphone) + Voice Changer');
await page.locator('.vrow span.dim').nth(5).click(); await sleep(500);
const takesBefore = (await page.locator('.inspector .take').count());
await page.click('.inspector button:has-text("Record")'); await sleep(800);
await page.click('#modal button:has-text("Record")'); await sleep(1800);
await page.click('#modal button:has-text("Stop")'); await sleep(900);
await S.modal('v70-recorder');
await page.click('#modal button:has-text("Keep my recording")'); await sleep(3500);
const t1 = await page.locator('.inspector .take').count();
ok('Recording kept as a take', t1 === takesBefore + 1, `${takesBefore} -> ${t1}`);
await page.click('.inspector button:has-text("Record")'); await sleep(800);
await page.click('#modal button:has-text("Record")'); await sleep(1500);
await page.click('#modal button:has-text("Stop")'); await sleep(900);
await page.click('#modal button:has-text("Convert to")'); await sleep(5000);
const t2 = await page.locator('.inspector .take').count();
ok('Recording converted with the Voice Changer and kept', t2 === t1 + 1, `${t1} -> ${t2}`);
await S.full('v70-recorded');
D2 = patchJson('Skyrim DLCs');
const engines = D2.j.Lines.flatMap((l) => l.Takes.map((t) => t.Settings.Engine)).filter((e) => e.startsWith('microphone'));
ok('Takes record their origin', engines.includes('microphone') && engines.includes('microphone+vc'), engines.join(','));

// ---------------- Pack as BSA (Phase 3)
console.log('STEP Pack as BSA (Phase 3)');
await page.click('button.crumb[data-section="fomods"]'); await sleep(800);
await page.locator('.pcard').first().click(); await sleep(1000);
await page.locator('.fnode:has-text("Installer & info.xml")').click(); await sleep(500);
await page.click('button:has-text("Pack as BSA")'); await sleep(500);
await S.el('v70-bsa-setting', '.section:has(h2:text-is("Installer"))');
await page.click('button:has-text("Build ▾")'); await sleep(400);
await page.click('text=Stage only').catch(async () => { await page.click('text=Build and pack'); }); await sleep(800);
await page.waitForFunction(() => document.querySelector('#job')?.classList.contains('hidden') && document.querySelector('#modal:not(.hidden)'), null, { timeout: 90000 }); await sleep(600);
await S.modal('v70-bsa-built');
const built = await page.locator('#modal .kv').innerText();
ok('FOMOD build packed BSAs', /BSA archives packed\s+2/.test(built), built.replace(/\n/g, ' | '));
const stg = fs.readdirSync('/srv/dbvo/D/DBVO/Releases/_staging').map((d) => path.join('/srv/dbvo/D/DBVO/Releases/_staging', d, '_bsa')).find((d) => fs.existsSync(d));
const bsas = stg ? fs.readdirSync(stg) : [];
ok('Staged BSA + loader plugins', bsas.filter((f) => f.endsWith('.bsa')).length === 2, bsas.join(', '));
fs.writeFileSync('/tmp/bsa_dir.txt', stg || '');
await page.locator('#modal .btn.primary').first().click().catch(() => {}); await sleep(300);

ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
process.exit(0);
