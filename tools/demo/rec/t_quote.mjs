import fs from 'node:fs';
import { boot, shooter, sleep } from './shots_common.mjs';
fs.mkdirSync('/srv/dbvo/D/Pictures', { recursive: true }); fs.copyFileSync('../assets/Dusk mountains.jpg', '/srv/dbvo/D/Pictures/Dusk mountains.jpg');
const s = await boot(); const { page } = s; const S = shooter(page);
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
await page.evaluate(() => { localStorage.removeItem('dbvo:greeting'); localStorage.removeItem('dbvo:maiqDeck'); }); await page.reload();
for (let i = 1; i <= 5; i++) {
  await page.waitForSelector('.home-quote'); await sleep(1500);
  if (i === 5) {
    await page.click('[data-section="settings"]'); await sleep(600);
    await page.click('.nav-item:has-text("General & tools")'); await sleep(400);
    s.pick('D:\\Pictures\\Dusk mountains.jpg'); await sleep(200);
    await page.click('button:has-text("Choose image…")'); await sleep(1200);
    await page.click('#brand'); await page.waitForSelector('.home-quote'); await sleep(1500);
  }
  console.log(i, await page.evaluate(() => document.querySelector('.home-hero h1').innerText.replace('\n', ' / ') + ' | ' + document.querySelector('.home-quote').innerText.replace('\n', ' ')));
  await S.full('quote-' + i);
  if (i < 5) await page.reload();
}
await page.click('[data-section="settings"]'); await sleep(500); await page.click('.section:has(h2:text-is("Start page")) button:has-text("Reset")'); await sleep(800);
console.log('deck left:', await page.evaluate(() => JSON.parse(localStorage.getItem('dbvo:maiqDeck')).length), 'errors:', errs);
process.exit(0);
