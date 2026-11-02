import { start } from './bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const s = await start({ width: 1600, height: 950, scale: 1 }); const { page } = s;
page.on('pageerror', (e) => console.log('PAGEERR', e.message));
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
await page.evaluate(() => { const w = window.chrome.webview; const orig = w.postMessage.bind(w); w.postMessage = (m) => { if (m.method === 'mod.create' || m.method === 'pick.folder') setTimeout(() => orig(m), 4200); else orig(m); }; });
const chip = () => page.evaluate(() => { const b = document.querySelector('.busy-chip'); return b && !b.classList.contains('hidden') ? b.textContent + ' / cursor ' + document.documentElement.classList.contains('app-busy') : 'none'; });
page.evaluate(() => import('/lib.js').then((l) => l.rpc('mod.create', { name: 'Chip test ' + Date.now(), category: 'Misc' })));
for (const t of [200, 600, 3500, 5000]) { await sleep(t - (t > 200 ? [200, 600, 3500, 5000][[200, 600, 3500, 5000].indexOf(t) - 1] : 0)); console.log(t, 'ms:', await chip()); }
await page.screenshot({ path: 'chip.png', clip: { x: 0, y: 0, width: 1600, height: 60 } });
page.evaluate(() => import('/lib.js').then((l) => l.rpc('pick.folder', {}))).catch(() => {});
await sleep(1000); console.log('picker:', await chip());
await s.close(); process.exit(0);
