import { start } from './bridge.mjs';
export const OUT = '/tmp/dbvo-work/demo/shots';
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export async function boot() {
  const s = await start({ width: 1480, height: 860, scale: 1.4 });
  await s.page.goto('https://app.dbvo/index.html');
  await s.page.addStyleTag({ content: '*{caret-color:transparent!important} ::-webkit-scrollbar{width:10px;height:10px}' });
  return s;
}
export function shooter(page) {
  return {
    full: async (n) => { await sleep(350); await page.screenshot({ path: `${OUT}/${n}.png` }); console.log('shot', n); },
    modal: async (n) => { await sleep(350); try { await page.locator('#modal .modal-card').screenshot({ path: `${OUT}/${n}.png`, timeout: 8000 }); } catch (e) { await page.screenshot({ path: `${OUT}/FAIL-${n}.png` }); console.log('FAILSHOT', n, (await page.locator('#toast').innerText().catch(() => ''))); throw e; } console.log('shot', n); },
    el: async (n, sel) => { await sleep(350); await page.locator(sel).first().screenshot({ path: `${OUT}/${n}.png` }); console.log('shot', n); },
    right: async (n, w = 920) => { await sleep(350); const v = page.viewportSize(); await page.screenshot({ path: `${OUT}/${n}.png`, clip: { x: v.width - w, y: 0, width: w, height: v.height } }); console.log('shot', n); },
    clip: async (n, clip) => { await sleep(350); await page.screenshot({ path: `${OUT}/${n}.png`, clip }); console.log('shot', n); },
  };
}
export async function waitJob(page, t = 60000) {
  await sleep(400);
  await page.waitForFunction(() => document.querySelector('#job')?.classList.contains('hidden'), null, { timeout: t });
  await sleep(500);
}
export async function drop(s, sel, paths) {
  s.dropNext(paths);
  await s.page.evaluate((sel) => { const dt = new DataTransfer(); dt.items.add(new File(['x'], 'a.esp')); document.querySelector(sel).dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt })); }, sel);
}
// The Generate options panel (Phase 2, NPC Revoice): open it and pick one choice by its label.
export async function genPick(page, label) {
  await page.click('.statusbar .gen-btn'); await sleep(350);
  await page.click(`.gen-pop .gen-item:has-text("${label}")`); await sleep(500);
}
