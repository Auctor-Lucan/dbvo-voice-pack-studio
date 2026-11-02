import { boot, shooter, sleep } from './shots_common.mjs';
import fs from 'node:fs'; fs.mkdirSync('/srv/dbvo/D/Pictures', { recursive: true }); fs.copyFileSync('../assets/Dusk mountains.jpg', '/srv/dbvo/D/Pictures/Dusk mountains.jpg');
const s = await boot(); const { page } = s; const S = shooter(page);
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
await page.waitForSelector('.home-cards');
await page.click('[data-section="settings"]'); await sleep(600);
await page.click('.nav-item:has-text("General & tools")'); await sleep(500);
s.pick('D:\\Pictures\\Dusk mountains.jpg'); await sleep(200);
await page.click('button:has-text("Choose image…")'); await sleep(1500);
await page.locator('.section:has(h2:text-is("Start page"))').scrollIntoViewIfNeeded();
await S.el('bg-setting', '.section:has(h2:text-is("Start page"))');
await page.click('#brand'); await page.waitForSelector('.home-cards'); await sleep(1500);
await S.full('bg-normal');
for (const [sh, lbl] of [['light', 'Lighter'], ['dark', 'Darker']]) {
  await page.click('[data-section="settings"]'); await sleep(600);
  await page.click(`.section:has(h2:text-is("Start page")) button:has-text("${lbl}")`); await sleep(600);
  await page.click('#brand'); await page.waitForSelector('.home-cards'); await sleep(1200);
  await S.full('bg-' + sh);
}
console.log('class:', await page.evaluate(() => document.querySelector('.view.home').className));
await page.click('[data-section="settings"]'); await sleep(600);
await page.click('.section:has(h2:text-is("Start page")) button:has-text("Reset")'); await sleep(1000);
await page.click('#brand'); await page.waitForSelector('.home-cards'); await sleep(800);
console.log('after reset:', await page.evaluate(() => document.querySelector('.view.home').className));
console.log('errors:', errs);
process.exit(0);
