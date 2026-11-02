import { boot, sleep } from './shots_common.mjs';
const s = await boot(); const { page } = s;
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
const where = async () => page.evaluate(() => (document.querySelector('.home-cards') ? 'home' : (document.querySelector('.view-head h1, .view-head h2')||{}).textContent || document.querySelector('main').innerText.slice(0,40)));
await page.waitForSelector('.home-cards'); console.log('1 default open:', await where());
// open a patch, then reload in home mode
await page.click('.home-rc >> nth=2'); await sleep(1200); console.log('2 opened:', await where());
await page.reload(); await sleep(2500); console.log('3 reload (home mode):', await where());
// switch to last
await page.click('[data-section="settings"]'); await sleep(600);
await page.click('.nav-item:has-text("General & tools")'); await sleep(500);
await page.click('.section:has(h2:text-is("Start page")) button:has-text("Where I left off")'); await sleep(600);
await page.click('.home-rc >> nth=0').catch(()=>{});
await page.click('#brand'); await sleep(1200); await page.click('.home-rc >> nth=1'); await sleep(1200);
const before = await where(); console.log('4 opened:', before);
await page.reload(); await sleep(2500); console.log('5 reload (last mode):', await where());
await page.keyboard.press('F1'); await sleep(800); console.log('6 F1:', await where(), await page.evaluate(() => document.querySelector('.help-toc a.on')?.textContent));
await page.keyboard.press('Escape'); await sleep(300); await page.click('.home-rc').catch(()=>{});
await page.focus('#brand'); await page.keyboard.press('Enter'); await sleep(1200); console.log('8 brand Enter:', await where());
// restore
await page.click('[data-section="settings"]'); await sleep(600);
await page.click('.nav-item:has-text("General & tools")'); await sleep(500);
await page.click('.section:has(h2:text-is("Start page")) button:has-text("Landing page")'); await sleep(500);
console.log('errors:', errs);
process.exit(0);
