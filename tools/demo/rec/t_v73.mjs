// 7.3: Generate options panels, trimmed line panels, NPC Revoice head / Speakers tab / reopen state, FOMOD cards + summary.
import { start } from './bridge.mjs';
import { OUT, sleep } from './shots_common.mjs';
const W = Number(process.env.W || 1920);
const s = await start({ width: W, height: 1040, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html');
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const results = []; const ok = (n, c, x = '') => results.push(`${c ? 'PASS' : 'FAIL'}  ${n}${x ? '  · ' + x : ''}`);
const shot = async (n, clip) => { await sleep(400); await page.screenshot({ path: `${OUT}/${n}.png`, clip }); };
await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1300); };
const oneLine = async (sel) => page.evaluate((sel) => {
  const el = document.querySelector(sel); if (!el) return 'missing';
  const kids = [...el.children].filter((k) => k.offsetParent);
  const tops = new Set(kids.map((k) => Math.round(k.getBoundingClientRect().top / 14)));
  const over = el.scrollWidth > el.clientWidth + 2 ? ' (overflows)' : '';
  return (tops.size === 1 ? 'one' : `${tops.size} rows`) + over;
}, sel);
const st = await rpc('state', {});
const sky = st.patches.find((p) => p.name === 'Skyrim') || st.patches[0];

// ---- Phase 2
await go('patch', { id: sky.id }); await page.waitForSelector('.vrow');
ok('Phase 2 toolbar on one line', (await oneLine('.toolbar')) === 'one', await oneLine('.toolbar'));
ok('Phase 2 status bar on one line', (await oneLine('.statusbar')) === 'one', await oneLine('.statusbar'));
await page.locator('.vrow').nth(2).click(); await sleep(600);
const insp = await page.locator('.inspector').innerText();
ok('Line panel: merged line box when identical', /Line \(same in game\)|Original line/i.test(insp));
ok('Line panel: file tags', (await page.locator('.inspector .ext-tags .badge').count()) === 3);
ok('Line panel: short buttons', /New take|Generate/.test(insp) && !/Restore in-game line/.test(insp));
await shot('v73-p2', { x: 0, y: 0, width: W, height: 1040 });
await page.click('.statusbar .gen-btn'); await sleep(400);
const items = await page.locator('.gen-pop .gen-item').allInnerTexts();
ok('Phase 2 Generate options lists 7 choices', items.length === 7, items.map((t) => t.split('\n')[0]).join(' | '));
ok('Zero-count choices are greyed', (await page.locator('.gen-pop .gen-item.off').count()) >= 1);
ok('No Approved choice', !items.some((t) => /^Approved/.test(t)));
await shot('v73-p2-gen', { x: W - 700, y: 400, width: 700, height: 640 });
await page.keyboard.press('Escape'); await page.mouse.click(400, 200); await sleep(300);

// ---- NPC Revoice
const rv = (await rpc('revoice.list', {}))[0];
await page.evaluate((id) => { for (const k of ['rv-tab:', 'rv-speaker:', 'rv-scroll:']) localStorage.removeItem('dbvo:' + k + id); }, rv.id);
await go('revoice', { id: rv.id });
ok('NPC Revoice opens on Speakers after a scan', await page.locator('.speakers-tbl').count() === 1);
ok('Head: Export .fuz is an accent button', await page.locator('.view-head .btn.accent.export-btn').count() === 1, await page.locator('.view-head .export-btn').innerText().catch(() => ''));
const headOrder = await page.evaluate(() => [...document.querySelector('.view-head').children].map((c) => c.textContent.trim().slice(0, 14)));
ok('Delete is right of Export', headOrder.findIndex((t) => /Export/.test(t)) < headOrder.findIndex((t) => /Delete/.test(t)), headOrder.join(' | '));
ok('Speakers: progress bars', (await page.locator('.speakers-tbl .pbar').count()) >= 1);
ok('Speakers: totals line', /speaker/.test(await page.locator('.speaker-totals').innerText()));
await shot('v73-rv-speakers', { x: 0, y: 0, width: W, height: 1040 });
await page.click('.voice-settings > summary'); await sleep(400);
await shot('v73-rv-speakers-open', { x: 0, y: 0, width: W, height: 1040 });
await page.click('.voice-settings > summary'); await sleep(300);
await page.locator('.speakers-tbl button:has-text("Lines →")').first().click(); await sleep(900);
ok('Lines → picks that speaker', await page.evaluate(() => document.querySelector('.toolbar select').value !== ''));
ok('NPC Revoice toolbar on one line', (await oneLine('.toolbar')) === 'one', await oneLine('.toolbar'));
await page.locator('.toolbar select').first().selectOption(''); await sleep(600);
ok('Set in Speakers button', await page.locator('.toolbar button.bordered:has-text("Set in Speakers")').count() === 1);
await page.locator('.vrow').nth(1).click(); await sleep(500);
await shot('v73-rv-lines', { x: 0, y: 0, width: W, height: 1040 });
const rinsp = await page.locator('.inspector').innerText();
ok('Revoice line panel: file name + Now on the status row', /\.fuz/.test(rinsp) && /(▶ Now|Silent in game now)/.test(rinsp), rinsp.slice(0, 200).replace(/\n/g, ' | '));
await page.click('.statusbar .gen-btn'); await sleep(400);
const ritems = await page.locator('.gen-pop .gen-item').allInnerTexts();
ok('Revoice Generate options lists 6 choices', ritems.length === 6, ritems.map((t) => t.split('\n')[0]).join(' | '));
await shot('v73-rv-gen', { x: W - 700, y: 400, width: 700, height: 640 });
await page.keyboard.press('Escape'); await page.mouse.click(400, 200); await sleep(300);
await page.click('.toolbar button:has-text("Set in Speakers")'); await sleep(500);
ok('Set in Speakers switches tab and glows', await page.locator('.speakers-tbl.flash-hl').count() === 1);
// reopen where left off
await page.click('button:has-text("Lines (")'); await sleep(700);
await page.locator('.toolbar select').first().selectOption({ index: 1 }); await sleep(500);
const picked = await page.locator('.toolbar select').first().inputValue();
await go('home'); await go('revoice', { id: rv.id });
ok('Reopens on Lines with the same speaker', await page.locator('.toolbar select').count() > 0 && (await page.locator('.toolbar select').first().inputValue()) === picked);

// ---- FOMOD builder
const fm = (await rpc('state', {})).fomods[0];
if (fm) {
  await go('fomod', { id: fm.id });
  await page.locator('.fnode:has-text("Installer & info.xml")').click(); await sleep(1500);
  ok('FOMOD: setting cards', (await page.locator('.scard').count()) >= 10);
  ok('FOMOD: summary panel', /Voice files/.test(await page.locator('.fomod-summary').innerText()), (await page.locator('.fomod-summary').innerText()).replace(/\n/g, ' | '));
  await page.locator('.fomod-summary button:has-text("Validate")').click(); await sleep(900);
  await page.locator('#modal .btn').last().click().catch(() => {}); await sleep(500);
  ok('FOMOD: last validate shown inline', !/Not checked yet/.test(await page.locator('.fomod-summary').innerText()));
  await shot('v73-fomod', { x: 0, y: 0, width: W, height: 1040 });
} else ok('FOMOD project exists', false);
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
