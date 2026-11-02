// Voiced Books Tool end to end on the demo data: create, scan a mod, voices, editor, generate, join, export, built-in set.
import fs from 'fs';
import { boot, shooter, sleep, waitJob } from './shots_common.mjs';
const s = await boot(); const { page } = s; const S = shooter(page);
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const results = []; const ok = (n, c, x = '') => { results.push(`${c ? 'PASS' : 'FAIL'}  ${n}${x ? '  · ' + x : ''}`); console.log(results.at(-1)); };
await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1200); };
const W = (p) => p; // paths are already Windows-style in the UI

// landing card
const card = page.locator('.home-card', { hasText: 'narrate Skyrim' });
ok('Landing: book card present', await card.count() === 1);
const cards = await page.locator('.home-card b').allInnerTexts();
ok('Landing: book card bottom-left, setup bottom-right, no guide card', cards[3].includes('narrate') && cards[5].includes('set up') && !cards.some((c) => /guide/i.test(c)), cards.join(' | '));
await S.full('vb-landing');
await card.click(); await sleep(1200);
await S.full('vb-list-empty');

// new project from a mod
await page.click('.view-head button:has-text("New project")'); await sleep(500);
await page.locator('#modal label', { hasText: 'From a mod' }).locator('input').check();
await page.locator('#modal input[type=text]').nth(0).fill('Example Library');
await page.locator('#modal input[type=text]').nth(1).fill('EXAMPLE LIBRARY');
await page.locator('#modal input[type=text]').nth(2).fill('Female Narrator');
await S.modal('vb-new');
await page.click('#modal .btn.primary'); await sleep(1500);
let list = await rpc('books.list', {});
const id = list[0].id;
ok('Project created', list.length === 1 && list[0].name === 'Example Library');
await page.click('.view-head .seg button:has-text("Setup")').catch(() => {}); await sleep(600);
s.pick('D:\\Modding\\Downloads\\Example Library\\Example Library.esp');
await page.click('button:has-text("Add plugins…")'); await sleep(1200);
await page.click('.section-head button:has-text("Scan")'); await waitJob(page);
let proj = await rpc('books.get', { id });
await S.full('vb-setup');
const by = (st) => proj.books.find((b) => b.stem === st);
ok('Scan: books found', proj.books.length >= 8, `${proj.books.length} books`);
ok('Scan: skill copy shares the text', by('exroadsoldnorth2') && by('exroadsoldnorth2').records === 2);
ok('Scan: spell tome without text left out', by('exspelltomespark').excl);
ok('Scan: spell tome with text suggested', by('exspelltomelantern').suggest);
ok('Scan: recipe left out', by('exrecipestew').excl);
ok('Scan: letter by Brannoc read as MaleNord', by('exletterbrannoc').reader === 'npc' && by('exletterbrannoc').vt === 'MaleNord');
ok('Scan: notice stays with the narrator', by('exinnnotice').reader === 'narrator');

// voices
await page.click('.view-head .seg button:has-text("Narrators")'); await sleep(1500);
await page.waitForFunction(() => [...document.querySelectorAll('.voice-cell select')].some((s) => s.options.length > 2), null, { timeout: 15000 });
const vsel = page.locator('.voices-tbl .voice-cell select:nth-child(2)');
await vsel.nth(0).selectOption({ index: 2 }); await sleep(900);
await page.locator('.voices-tbl tr', { hasText: 'MaleNord' }).locator('.voice-cell select:nth-child(2)').selectOption({ index: 3 }); await sleep(900);
proj = await rpc('books.get', { id });
ok('Voices: narrator and MaleNord have voices', proj.narrators[0].voiceId && proj.voiceTypes.find((v) => v.voiceType === 'MaleNord').voiceId);
await S.full('vb-voices');

// books + editor (shorter parts for the demo, so the example book splits)
await rpc('books.save', { id, partTarget: 900, partMax: 1200 });
await page.click('.view-head .seg button:has-text("Books")'); await sleep(1200);
await page.locator('.vrow', { hasText: 'Roads of the Old North' }).first().click(); await sleep(500);
await S.full('vb-books');
await page.locator('.vrow', { hasText: 'Roads of the Old North' }).first().dblclick(); await sleep(1500);
const parts = await page.locator('.part-card').count();
ok('Editor: long book is in parts', parts >= 2, `${parts} parts`);
const spoken = await page.locator('textarea.spoken-ed').inputValue();
ok('Editor: spoken text has the structure and rules', spoken.includes('---- By Orvin the Archivist') && spoken.includes('Volume Two') && spoken.includes('Seventeenth of Last Seed') && spoken.includes('- and a hungry one.'), spoken.slice(0, 120).replace(/\n/g, ' '));
ok('Editor: drop cap restored', spoken.includes('The old roads'));
await S.full('vb-editor');
// move a break later, then back to automatic
const before = await rpc('books.bookGet', { id, stem: 'exroadsoldnorth2' });
await page.locator('.break-row button[title*="later"]').first().click(); await sleep(1200);
const after = await rpc('books.bookGet', { id, stem: 'exroadsoldnorth2' });
ok('Editor: a break moves one sentence later', after.manual && after.breaks[0] > before.breaks[0], `${before.breaks[0]} → ${after.breaks[0]}`);
await page.click('.parts-list button:has-text("Auto")'); await sleep(1000);
// generate this book's parts from the editor
await page.click('.parts-list .btn.primary'); await sleep(800);
if (await page.locator('#modal:not(.hidden)').count()) { await S.modal('vb-check'); await page.click('#modal .btn.primary'); }
await waitJob(page, 60000);
const gen = await rpc('books.bookGet', { id, stem: 'exroadsoldnorth2' });
ok('Editor: parts generated', gen.voices[0].lines.every((l) => l.sel), gen.voices[0].lines.map((l) => l.status).join(','));
await S.full('vb-editor-done');
const j = await rpc('books.join', { id, stem: 'exroadsoldnorth2', voice: gen.voices[0].key });
ok('Join: whole book built', j.seconds > 1 && j.lufs < -8 && j.lufs > -16 && j.peakDb <= -0.9, `${j.seconds.toFixed(1)} s ${j.lufs} LUFS ${j.peakDb} dB`);
await page.keyboard.press('Escape'); await sleep(1000);

// generate the rest with the options panel
await page.click('.statusbar .gen-btn'); await sleep(500);
await S.el('vb-genpanel', '.pop-panel');
const offItems = await page.locator('.gen-item.off').count();
ok('Generate options: empty choices greyed out', offItems >= 1);
await page.click('.gen-item:has-text("Missing")'); await sleep(900);
await S.modal('vb-estimate');
await page.click('#modal .btn.primary'); await sleep(400);
ok('Generate: button became Stop', await page.locator('.statusbar .stop-btn').count() === 1);
await waitJob(page, 90000);
proj = await rpc('books.get', { id });
const live = proj.books.filter((b) => !b.excl);
ok('Generate: every included book ready', live.every((b) => b.voices.every((v) => v.status === 'ready')), live.filter((b) => b.voices.some((v) => v.status !== 'ready')).map((b) => b.stem).join(','));

// export
await rpc('books.save', { id, outputFolder: 'D:\\Modding\\Mods\\Example Library Voiced Books' });
await go('book', { id });
await page.click('.export-btn'); await sleep(400);
await page.click('.popup-menu >> text=Export everything').catch(async () => { await page.locator('text=Export everything').first().click(); });
await waitJob(page, 90000); await sleep(800);
await S.modal('vb-exported');
const out = '/srv/dbvo/D/Modding/Mods/Example Library Voiced Books';
const has = (p) => fs.existsSync(out + '/' + p);
ok('Export: narrator BSA + 49-byte plugin', has('VBOS EXAMPLE LIBRARY - Female Narrator/VBOS EXAMPLE LIBRARY - Female Narrator.bsa') && fs.statSync(out + '/VBOS EXAMPLE LIBRARY - Female Narrator/VBOS EXAMPLE LIBRARY - Female Narrator.esp').size === 49);
ok('Export: NPC voices BSA', has('VBOS EXAMPLE LIBRARY - NPC Voices/VBOS EXAMPLE LIBRARY - NPC Voices.bsa'));
const iniName = fs.readdirSync(out + '/VBOS INI')[0];
const ini = fs.readFileSync(out + '/VBOS INI/' + iniName, 'utf8');
ok('Export: .ini with file-local IDs', /^;Example Library\r\n0x80[0-9A-F]~Example Library\.esp\|sound\\fx\\voicedbooksofskyrim\\/m.test(ini), iniName + ' ' + ini.split('\r\n')[1]);
ok('Export: FOMOD written', has('fomod/ModuleConfig.xml'));
const rep = fs.readdirSync(out).find((f) => f.endsWith('_FULL_VALIDATION.txt'));
const repText = rep ? fs.readFileSync(out + '/' + rep, 'utf8') : '';
ok('Export: validation report', rep && /RESULT: (PASS|CHECK)/.test(repText), (repText.match(/RESULT:.*/) || [''])[0]);
await page.keyboard.press('Escape'); await sleep(500);

// the FOMOD opens in the builder
await page.click('.export-btn'); await sleep(400);
await page.locator('text=Open the installer in the FOMOD builder').first().click(); await sleep(2000);
ok('FOMOD builder opened with the narrator option', await page.locator('.fomod .fnode:has-text("Female Narrator")').count() > 0);
ok('Books installer: no DBVO voice pack JSON', await page.locator('.fnode:has-text("DBVO voice pack JSON") .n').innerText().catch(() => '') === 'off');
await S.full('vb-fomod');

// built-in set
const c = await rpc('books.create', { name: 'Skyrim books', source: 'skyrim-dlc', narrator: 'Narrator' });
await go('book', { id: c.id });
const sp = await rpc('books.get', { id: c.id });
ok('Built-in set: 793 books, 333 by NPC voices', sp.books.length === 793 && sp.books.filter((b) => b.reader === 'npc').length === 333);
ok('Built-in set: USSEP variants', sp.books.filter((b) => b.variants > 0).length === 5);
await S.full('vb-set');
await page.locator('.vrow').nth(2).dblclick(); await sleep(1200);
await S.full('vb-set-editor');
await page.keyboard.press('Escape'); await sleep(600);
await page.click('.view-head .seg button:has-text("Narrators")'); await sleep(1500);
await S.full('vb-set-voices');

console.log('\n' + results.join('\n'));
console.log('PAGE ERRORS', errs);
process.exit(0);
