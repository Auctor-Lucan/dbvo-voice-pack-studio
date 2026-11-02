import { boot, sleep } from './shots_common.mjs';
const OUT = '/tmp/dbvo-work/demo/shots9';
const s = await boot(); const { page } = s;
await page.waitForSelector('.home-cards');
await page.evaluate(() => { for (const k of Object.keys(localStorage)) if (/^(order:|cols:|hide:)/.test(k)) localStorage.removeItem(k); localStorage.setItem('seen:tags-context', 'true'); });
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1500); };
const st = await rpc('state', {}); const P = st.patches.find((x) => x.name === 'Skyrim');
let pa = await rpc('patch.get', { id: P.id });
for (const l of pa.lines) if (l.spoken.includes('Hello there')) await rpc('line2.update', { id: P.id, lineId: l.lineId, action: 'resetSpoken' });
for (const l of pa.lines) if (l.excluded) await rpc('line2.update', { id: P.id, lineId: l.lineId, action: 'include' });
await rpc('patch.saveMeta', { id: P.id, tagsAsContext: true });
await go('patch', { id: P.id }); await sleep(1500);
if (await page.locator('#modal:not(.hidden) h2:has-text("leading tags")').count()) { await page.locator('#modal .modal-card').screenshot({ path: `${OUT}/tags-context-dialog.png` }); await page.click('#modal button:has-text("Keep it on")'); await sleep(500); }
await sleep(800);
await page.locator('.vrow').nth(2).click(); await sleep(500);
await page.screenshot({ path: `${OUT}/phase2-generated.png` });
// output popover
await page.click('.toolbar button:has-text("PCM"), button:has-text("PCM 44.1k")').catch(() => {}); await sleep(500);
await page.locator('.pop-panel').screenshot({ path: `${OUT}/output-popover.png` }).catch((e) => console.log('nopop', e.message));
await page.keyboard.press('Escape'); await sleep(300);
// takes dialog
const l = (await rpc('patch.get', { id: P.id })).lines.find((x) => x.takes.length >= 2) || pa.lines[1];
await page.evaluate((k) => document.querySelector(`.vrow[data-key="${k}"] .take-lbl`)?.click(), l.lineId); await sleep(700);
await page.locator('#modal .modal-card').screenshot({ path: `${OUT}/phase2-takes.png` });
await page.click('#modal button:has-text("Edit take")'); await page.waitForSelector('.te-canvas'); await sleep(900);
const c = await page.locator('.te-canvas').boundingBox();
await page.mouse.move(c.x + c.width * 0.42, c.y + 100); await page.mouse.down(); await page.mouse.move(c.x + c.width * 0.6, c.y + 100, { steps: 6 }); await page.mouse.up(); await sleep(400);
await page.locator('#modal .modal-card').screenshot({ path: `${OUT}/edit-take.png` });
await page.keyboard.press('Escape'); await sleep(300); await page.keyboard.press('Escape'); await sleep(300);
process.exit(0);
