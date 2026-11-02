import { start } from './bridge.mjs';
import { OUT, sleep } from './shots_common.mjs';
for (const W of [1600, 1280]) {
const s = await start({ width: W, height: 900, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1500); };
const bl = await rpc('books.list', {});
await go('book', { id: bl[0].id });
await page.locator('.vgrid .vrow').first().dblclick(); await sleep(1500);
const row = page.locator('.bk-gen-row');
const bb = await row.boundingBox();
const kids = await row.evaluate((r) => [...r.children].map((c) => { const b = c.getBoundingClientRect(); return [c.tagName, Math.round(b.left), Math.round(b.width), Math.round(b.top)]; }));
console.log(W, JSON.stringify(kids));
await page.screenshot({ path: `${OUT}/bkrow-${W}.png`, clip: { x: bb.x - 20, y: bb.y - 60, width: bb.width + 40, height: bb.height + 80 } });
await s.close();
}
process.exit(0);
