// 7.4: Missing vs Edited, books editor buttons, grid stop, scroll kept, busy screen.
import { start } from './bridge.mjs';
import { OUT, sleep } from './shots_common.mjs';
const W = 1920;
const s = await start({ width: W, height: 1040, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html');
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const results = []; const ok = (n, c, x = '') => results.push(`${c ? 'PASS' : 'FAIL'}  ${n}${x ? '  · ' + x : ''}`);
const shot = async (n, clip) => { await sleep(400); await page.screenshot({ path: `${OUT}/${n}.png`, clip }); };
await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1300); };
const genItems = async () => { await page.click('.statusbar .gen-btn'); await sleep(400); const t = await page.locator('.gen-pop .gen-item').allInnerTexts(); await page.keyboard.press('Escape'); await page.mouse.click(300, 200); await sleep(200); return t.map((x) => x.replace(/\s+/g, ' ')); };
const count = (items, label) => { const it = items.find((t) => t.startsWith(label + ' ')); const m = it && it.match(/([\d,]+) (line|book)/); return m ? Number(m[1].replace(/,/g, '')) : -1; };

// ---- Phase 2: a tag added to lines with takes makes them Edited, not Missing
const st = await rpc('state', {});
const sky = st.patches.find((p) => p.name === 'Skyrim');
let P = await rpc('patch.get', { id: sky.id });
const withTake = P.lines.filter((l) => l.takes.length && !l.omitted && !l.excluded);
const before = await (async () => { await go('patch', { id: sky.id }); await page.waitForSelector('.vrow'); return genItems(); })();
await rpc('line2.bulkTags', { id: sky.id, lineIds: withTake.slice(0, 5).map((l) => l.lineId), tags: ['[sighs]'], mode: 'add', placement: '' }).catch((e) => console.log('bulkTags', e.message));
await go('home'); await go('patch', { id: sky.id }); await page.waitForSelector('.vrow');
const after = await genItems();
ok('Phase 2: tagging lines with takes does not make them Missing', count(after, 'Missing') === count(before, 'Missing'), `${count(before, 'Missing')} → ${count(after, 'Missing')}`);
ok('Phase 2: they show under Edited', count(after, 'Edited') >= 5, `Edited ${count(after, 'Edited')}`);
ok('Phase 2: Edited sits right below Missing', after.findIndex((t) => t.startsWith('Edited')) === after.findIndex((t) => t.startsWith('Missing')) + 1, after.map((t) => t.split(' ')[0]).join(','));
const bar = await page.locator('.statusbar .stat').innerText();
ok('Phase 2: status bar says edited since take', /edited since take/.test(bar), bar);
await page.click('.statusbar .gen-btn'); await sleep(400);
await shot('v74-p2-gen', { x: W - 700, y: 300, width: 700, height: 740 });
await page.keyboard.press('Escape'); await page.mouse.click(300, 200); await sleep(200);
await page.click('.chip:has-text("Edited since take")').catch(() => {}); await sleep(500);
ok('Phase 2: Edited since take filter', (await page.locator('.vrow').count()) >= 5);
// undo the tags so other tests keep their data
await rpc('line2.bulkTags', { id: sky.id, lineIds: withTake.slice(0, 5).map((l) => l.lineId), tags: ['[sighs]'], mode: 'remove', placement: '' }).catch(() => {});

// ---- busy screen
await page.evaluate(() => import('/lib.js').then((l) => { l.withBusy(new Promise((r) => setTimeout(r, 2500)), 'Loading voice lines', 'Looking for WAV + LIP files in the folders and matching them to the lines…'); }));
await sleep(1200);
ok('Busy screen shows during a slow step', await page.locator('.busy-overlay').count() === 1);
await shot('v74-busy', { x: 0, y: 0, width: W, height: 1040 });
await sleep(2000);
ok('Busy screen closes', await page.locator('.busy-overlay').count() === 0);

// ---- Voiced Books: scroll kept, grid stop, editor buttons
const bk = (await rpc('books.list', {}))[0];
await page.evaluate((id) => localStorage.setItem('dbvo:bk-tab:' + id, JSON.stringify('setup')), bk.id);
await go('book', { id: bk.id }); await sleep(600);
await page.evaluate(() => { const v = document.querySelector('.view-body'); v.scrollTop = 600; });
await sleep(200);
const top0 = await page.evaluate(() => document.querySelector('.view-body').scrollTop);
const sw = page.locator('.srow:has-text("Write the .ini") .switch, .srow:has-text("Write the .ini") input[type=checkbox]').first();
await sw.click().catch(async () => { await page.locator('.srow:has-text("Write the .ini") label').first().click(); });
await sleep(1200);
const top1 = await page.evaluate(() => document.querySelector('.view-body').scrollTop);
ok('Books setup: a change keeps the scroll position', top0 > 100 && Math.abs(top1 - top0) < 5, `${top0} → ${top1}`);
await sw.click().catch(() => {}); await sleep(800);
await page.click('button:has-text("Books (")'); await sleep(900);
ok('Books grid: stop button next to play', await page.locator('.vrow').first().locator('button[title="Stop playback"]').count() === 1);
await page.locator('.vrow:has-text("Roads of the Old North")').first().dblclick(); await sleep(1200);
const toolsTxt = await page.locator('.ed-tools').innerText();
ok('Editor: AI Auto Format looks like Phase 2 (no sparkle)', /AI Auto Format/.test(toolsTxt) && !/✨/.test(toolsTxt), toolsTxt.replace(/\n/g, ' | '));
ok('Editor: [custom tag] input', await page.locator('.ed-tools input[placeholder="[custom tag]"]').count() === 1);
const firstCard = page.locator('.part-card').first();
const order = await firstCard.locator('.row button').evaluateAll((bs) => bs.map((b) => b.title.split(' ')[0]));
ok('Editor: ↻ first, ▶ last on a part', order.length >= 2 && /^A|^Generate/.test(order[0]) && /^Play/.test(order[order.length - 1]), order.join(','));
const genLbl = await page.locator('.parts-list .btn.primary').innerText();
ok('Editor: the button makes takes when nothing is missing', /New take|Generate/.test(genLbl) && !(await page.locator('.parts-list .btn.primary').isDisabled()), genLbl);
await page.locator('.ed-tools input[placeholder="[custom tag]"]').fill('whispers');
await page.locator('textarea.spoken-ed').evaluate((ta) => { ta.focus(); ta.setSelectionRange(0, 0); });
await page.locator('.ed-tools input[placeholder="[custom tag]"]').press('Enter'); await sleep(300);
const txt = await page.locator('textarea.spoken-ed').inputValue();
ok('Editor: custom tag goes in at the cursor', txt.startsWith('[whispers] '), txt.slice(0, 40));
await shot('v74-editor', { x: 0, y: 0, width: W, height: 1040 });
// put the text back without saving
await page.locator('textarea.spoken-ed').evaluate((ta, t) => { ta.value = t; }, txt.replace(/^\[whispers\] /, ''));
await page.keyboard.press('Escape'); await sleep(700);
const modalTxt = await page.locator('#modal:not(.hidden)').innerText().catch(() => '');
if (await page.locator('#modal:not(.hidden) button:has-text("Discard")').count()) await page.click('#modal button:has-text("Discard")');
await sleep(700);
ok('Editor closes after Discard', await page.locator('.book-editor-overlay').count() === 0, modalTxt.replace(/\n/g, ' | ').slice(0, 120));
if (await page.locator('.book-editor-overlay').count()) { console.log(results.join('\n')); process.exit(0); }

// ---- NPC Revoice: Missing vs Edited choices
const rv = (await rpc('revoice.list', {}))[0];
await page.evaluate((id) => localStorage.setItem('dbvo:rv-tab:' + id, JSON.stringify('lines')), rv.id);
await go('revoice', { id: rv.id });
const ri = await genItems();
ok('NPC Revoice: Missing then Edited', ri.findIndex((t) => t.startsWith('Edited')) === ri.findIndex((t) => t.startsWith('Missing')) + 1, ri.map((t) => t.split(' ')[0]).join(','));
// Revoice setup keeps the scroll
await page.click('button:has-text("Setup")'); await sleep(800);
await page.evaluate(() => { document.querySelector('.view-body').scrollTop = 400; }); await sleep(200);
const r0 = await page.evaluate(() => document.querySelector('.view-body').scrollTop);
const emo = page.locator('.srow:has-text("Emotion tags") label, .srow:has-text("Emotion tags") .switch').first();
await emo.click().catch(() => {}); await sleep(1200);
const r1 = await page.evaluate(() => document.querySelector('.view-body').scrollTop);
ok('NPC Revoice setup: a change keeps the scroll position', r0 > 50 && Math.abs(r1 - r0) < 5, `${r0} → ${r1}`);
await emo.click().catch(() => {}); await sleep(800);

ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
