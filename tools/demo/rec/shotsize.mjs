import { start } from './bridge.mjs';
import fs from 'fs';
const s = await start();
const { page } = s;
await page.goto('https://app.dbvo/index.html');
await page.waitForSelector('.pcard');
const cdp = await page.context().newCDPSession(page);
for (const sc of [1, 1.25]) {
  const r = await cdp.send('Page.captureScreenshot', { format: 'jpeg', quality: 92, clip: { x: 0, y: 0, width: 1536, height: 832, scale: sc } });
  fs.writeFileSync(`size_${sc}.jpg`, Buffer.from(r.data, 'base64'));
}
const t0 = Date.now(); let n = 0;
while (Date.now() - t0 < 3000) { await cdp.send('Page.captureScreenshot', { format: 'jpeg', quality: 92, optimizeForSpeed: true, clip: { x: 0, y: 0, width: 1536, height: 832, scale: 1.25 } }); n++; }
console.log('fps', n / 3);
await s.close(); process.exit(0);
