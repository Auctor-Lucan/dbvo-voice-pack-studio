import { start } from './bridge.mjs';
const s = await start();
const { page } = s;
await page.goto('https://app.dbvo/index.html');
await page.waitForSelector('.pcard');
const cdp = await page.context().newCDPSession(page);
let t0 = Date.now(), n = 0;
while (Date.now() - t0 < 4000) { const r = await cdp.send('Page.captureScreenshot', { format: 'jpeg', quality: 90, optimizeForSpeed: true }); n++; if (n === 1) console.log('len', r.data.length); }
console.log('fps', n / 4);
await s.close(); process.exit(0);
