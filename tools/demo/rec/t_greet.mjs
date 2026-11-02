import { boot, shooter, sleep } from './shots_common.mjs';
const s = await boot(); const { page } = s; const S = shooter(page);
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
await page.evaluate(() => localStorage.removeItem('dbvo:greeting')); await page.reload();
for (let i = 0; i < 7; i++) {
  await page.waitForSelector('.home-hero h1'); await sleep(400);
  console.log(i, await page.evaluate(() => document.querySelector('.home-hero h1').innerText.replace('\n', ' / ')));
  if (i === 2) await S.full('greet-long');
  await page.click('[data-section="settings"]'); await sleep(300); await page.click('#brand'); await sleep(400);
  console.log('  same after brand:', await page.evaluate(() => document.querySelector('.home-hero h1').innerText.replace('\n', ' / ')));
  await page.reload();
}
console.log('errors:', errs); process.exit(0);
