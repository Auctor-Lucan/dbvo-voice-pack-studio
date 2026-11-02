import { boot, sleep } from './shots_common.mjs';
const s = await boot(); const { page } = s;
await page.waitForSelector('.home-cards');
console.log(await page.evaluate(async () => { const { rpc } = await import('/lib.js'); const st = await rpc('state'); return JSON.stringify({ job: st.jobRunning }); }));
process.exit(0);
