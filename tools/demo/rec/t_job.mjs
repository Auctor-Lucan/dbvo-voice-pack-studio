import { start } from './bridge.mjs';
import { sleep } from './driver.mjs';
const s = await start(); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-checks');
await page.evaluate(() => { const j = document.getElementById('job'); j.classList.remove('hidden'); j.querySelector('.job-label').textContent = 'Generating 3 of 18 lines'; const q = document.createElement('button'); q.className = 'btn sm ghost'; q.id = 'job-queue'; q.textContent = '+1 queued'; j.insertBefore(q, document.getElementById('job-cancel')); });
for (const w of [1920, 1400, 1280, 1060, 900]) {
  await page.setViewportSize({ width: w, height: 700 }); await sleep(400);
  const r = await page.evaluate(() => { const hd = document.querySelector('header'); const j = document.getElementById('job'); const c = document.getElementById('job-cancel').getBoundingClientRect(); const u = document.getElementById('undo-btn').getBoundingClientRect(); return { over: hd.scrollWidth - hd.clientWidth, job: Math.round(j.getBoundingClientRect().width), cancelInside: c.right <= j.getBoundingClientRect().right, undoClear: u.left >= j.getBoundingClientRect().right }; });
  console.log(w, JSON.stringify(r));
  await page.screenshot({ path: `job_${w}.png`, clip: { x: 0, y: 0, width: w, height: 64 } });
}
await s.close(); process.exit(0);
