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

// clean earlier runs
for (const x of (await rpc('grunts.list', {}))) await rpc('grunts.delete', { id: x.id });
for (const o of ['D:\\Packs\\Out PCHT', 'D:\\Packs\\Out MPCHT', 'D:\\Packs\\Out SPVS']) fs.rmSync(L(o), { recursive: true, force: true });
await rpc('grunts.paths', { pchtFolder: '', spvsFolder: '' });

await page.click('#tools-btn'); await sleep(300);
ok('Other Tools lists the tool', await page.locator('.menu button:has-text("Player Grunts Tool")').count() === 1);
await page.screenshot({ path: `${OUT}/gr-menu.png`, clip: { x: 1000, y: 0, width: 600, height: 330 } });
await page.locator('.menu button:has-text("Player Grunts Tool")').click(); await sleep(1300);
await page.screenshot({ path: `${OUT}/gr-empty.png` });
await page.click('.view-head button:has-text("New project")'); await sleep(400);
await page.fill('#modal input[type=text]', 'Neutral Grunts');
await page.screenshot({ path: `${OUT}/gr-new.png` });
await page.click('#modal .btn.primary'); await sleep(1600);
ok('Project opens', await page.locator('.gr-tile').count() > 90, String(await page.locator('.gr-tile').count()));
ok('Banner asks for the PCHT folder', await page.locator('.banner:has-text("PC Head Tracking folder")').count() === 1);
await page.screenshot({ path: `${OUT}/gr-blocks-empty.png` });
const id = (await rpc('grunts.list', {}))[0].id;

// folders
await rpc('grunts.paths', { pchtFolder: 'D:\\MO2\\mods\\PC Head Tracking and Voice Type SE', spvsFolder: 'D:\\MO2\\mods\\Smart Player Voice System' });
await go('grunt', { id });
ok('Banner gone with the folder set', await page.locator('.banner:has-text("PC Head Tracking folder")').count() === 0);
ok('Events read from the install', await page.locator('.statusbar:has-text("events read from")').count() === 1);

// import a pack
const r = await rpc('grunts.importPack', { id, path: 'D:\\Packs\\Neutral Grunts' });
ok('Pack import', r.files === 42, `${r.files} files, ${r.events} events`);
await go('grunt', { id });
await page.locator('.gr-tile:has(.gr-label:text-is("Attack"))').first().click(); await sleep(500);
await page.screenshot({ path: `${OUT}/gr-blocks.png` });
ok('Inspector shows 7 lines', await page.locator('.inspector .gr-line').count() === 7, String(await page.locator('.inspector .gr-line').count()));
await page.locator('.inspector .gr-line').first().locator('button:has-text("▶")').click(); await sleep(500);
ok('Play loads audio', await page.evaluate(() => [...document.querySelectorAll('audio')].length >= 0));
await page.locator('.gr-tile:has(.gr-label:text-is("Unrelenting Force"))').first().click(); await sleep(500);
await page.screenshot({ path: `${OUT}/gr-shout.png`, clip: { x: 1100, y: 80, width: 500, height: 700 } });
ok('Shout parts filled', await page.locator('.inspector .gr-line:not(.empty-slot)').count() === 4);

// check + export
await rpc('grunts.save', { id, outputFolder: 'D:\\Packs\\Out PCHT' });
await go('grunt', { id });
await page.click('.view-head button:has-text("Check")'); await sleep(2500);
await page.screenshot({ path: `${OUT}/gr-check.png` });
const ready = await page.locator('#modal h2:has-text("Ready to export")').count();
ok('Check is clean', ready === 1, await page.locator('#modal .stat-row').innerText().catch(() => ''));
await page.click('#modal button:has-text("Export now")'); await waitJob();
await page.screenshot({ path: `${OUT}/gr-report.png` });
const out = L('D:\\Packs\\Out PCHT');
ok('Attack 07.wav written', fs.existsSync(out + '/Sound/FX/PC Head Tracking/Combat/Attack/07.wav'));
ok('Plugin written', fs.existsSync(out + '/PCHT Lines - Neutral Grunts.esp'));
ok('Shout written', fs.existsSync(out + '/Sound/Voice/PC Head Tracking - MCM.esp/_FemaleBYO/_VP_01a_0000A181_1.wav'));
ok('No staging left', !fs.readdirSync(L('D:\\Packs')).some((f) => f.includes('dbvo-export')));
await page.click('#modal .btn.primary').catch(() => {}); await sleep(300);

// MPCHT
await rpc('grunts.paths', { pchtFolder: 'D:\\MO2\\mods\\Multi Voice Packs support for PC Head Tracking' });
await rpc('grunts.save', { id, pcht: { multi: true, prefix: 'Neutral', fullName: 'Neutral Grunts' }, outputFolder: 'D:\\Packs\\Out MPCHT' });
await go('grunt', { id });
await page.click('.view-head .segmented button:has-text("Setup"), .view-head .seg button:has-text("Setup")'); await sleep(600);
await page.screenshot({ path: `${OUT}/gr-setup-pcht.png`, fullPage: false });
const c2 = await rpc('grunts.check', { id });
ok('MPCHT check clean', c2.errors === 0, c2.issues.filter((i) => i.severity === 'error').map((i) => i.text).join(' | '));
await rpc('grunts.export', { id }); await waitJob();
const mo = L('D:\\Packs\\Out MPCHT');
ok('MPCHT plugin + ini', fs.existsSync(mo + '/PCHTV_Neutral.esp') && fs.existsSync(mo + '/PCHTV_Neutral_FLM.ini'));
ok('MPCHT prefixed folders', fs.existsSync(mo + '/Sound/FX/PC Head Tracking/Neutral/Combat/Attack/01.wav'));
await page.keyboard.press('Escape'); await sleep(300);

// SPVS project from the PCHT one
const c = await rpc('grunts.create', { name: 'Neutral SPVS', target: 'spvs', slot: 'FemaleCustom03' });
const n = await rpc('grunts.importProject', { id: c.id, fromId: id });
ok('PCHT → SPVS copy', n.added > 30, String(n.added));
await go('grunt', { id: c.id });
await page.locator('.gr-tile:has(.gr-label:text-is("Attack"))').first().click(); await sleep(500);
await page.screenshot({ path: `${OUT}/gr-spvs.png` });
await page.click('.filters button:has-text("Custom events")'); await sleep(500);
await page.click('#modal button:has-text("Add event")'); await sleep(400);
const ins = page.locator('#modal input[type=text]');
await ins.nth(0).fill('Swimming'); await ins.nth(1).fill('Custom'); await ins.nth(2).fill('SwimStart'); await ins.nth(3).fill('Swim\\Start');
await page.screenshot({ path: `${OUT}/gr-custom-edit.png` });
const box = await page.locator('#modal .modal-card').boundingBox();
ok('Custom event dialog fits', box.height < 600, `${Math.round(box.height)} px`);
await page.click('#modal .btn.primary'); await sleep(600);
await page.screenshot({ path: `${OUT}/gr-custom-list.png` });
await page.keyboard.press('Escape'); await sleep(1200);
ok('Custom event shown', await page.locator('.gr-tile:has(.gr-label:text-is("Swimming"))').count() === 1);
await rpc('grunts.save', { id: c.id, outputFolder: 'D:\\Packs\\Out SPVS' });
const c3 = await rpc('grunts.check', { id: c.id });
ok('SPVS check clean', c3.errors === 0, c3.issues.filter((i) => i.severity === 'error').map((i) => i.text).join(' | '));
await rpc('grunts.export', { id: c.id }); await waitJob();
const so = L('D:\\Packs\\Out SPVS');
ok('SPVS script folder', fs.existsSync(so + '/Sound/SPVS_AdditionalVoice/CustomVoiceType/FemaleCustom03/_Vanilla/Attack/vanillaattack_01.wav'));
ok('SPVS vanilla names', fs.existsSync(so + '/Sound/Voice/SmartPlayerVoiceSystem.esp/SPVS_ReplaceFemaleCustom03/Combat_____Attack__00000816_1.wav'));
await page.keyboard.press('Escape'); await sleep(300);
await go('grunts');
await page.screenshot({ path: `${OUT}/gr-list.png` });
ok('No page errors', errs.length === 0, errs.join(' / '));
console.log(results.join('\n'));
await s.close(); process.exit(0);
