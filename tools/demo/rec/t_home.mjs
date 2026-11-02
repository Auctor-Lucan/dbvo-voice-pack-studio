import { boot, sleep } from './shots_common.mjs';
const s = await boot(); const { page } = s;
await page.waitForSelector('.home-cards'); await sleep(800);
await page.screenshot({ path: '/tmp/dbvo-work/demo/shots/v772-home.png' });
await s.close(); process.exit(0);
