import { sleep, waitJob, drop, genPick } from './shots_common.mjs';
export async function run(s, S) {
  const { page } = s;
  const row = (t) => page.locator(`.vrow:has-text("${t}")`).first();
  // AI auto tag on two ticked rows
  await row('rumors').locator('input[type=checkbox]').check();
  await row('dragons').locator('input[type=checkbox]').check();
  await page.click('button:has-text("AI Auto Tag")');
  await page.waitForFunction(() => document.body.innerText.includes('AI suggestions'), null, { timeout: 20000 }); await sleep(800);
  await row('rumors').locator('span.dim').first().click(); await sleep(500);
  await S.full('phase2-autotag');
  await row('rumors').locator('button[title="Accept tags"]').click(); await sleep(400);
  await row('dragons').locator('button[title="Accept tags"]').click(); await sleep(400);
  await page.click('button:has-text("Ticked (")').catch(() => {}); await sleep(250); await page.click('.menu >> text=Clear ticks').catch(() => {}); await sleep(300);
  // tag bar
  await S.el('phase2-tagbar', '.tagbar');
  await S.el('phase2-toolbar', '.toolbar');
  // generate missing
  await genPick(page, 'Missing'); await sleep(900);
  await S.modal('phase2-confirm');
  await page.click('#modal .btn.primary');
  await waitJob(page);
  await S.full('phase2-generated');
  // a second take of one line, then the takes window
  await row('rumors').locator('.icon-bar .act-btn').first().click();
  await waitJob(page); await sleep(800);
  await row('rumors').locator('.take-lbl').click(); await sleep(600);
  await S.modal('phase2-takes');
  await page.keyboard.press('Escape'); await page.locator('#modal button:has-text("OK"), #modal button:has-text("Close")').first().click().catch(() => {}); await sleep(400);
  // inspector of a generated line
  await row('rumors').locator('span.dim').first().click(); await sleep(500);
  await S.right('phase2-inspector');
  // folders & options
  await page.click('button:has-text("Folders ▾")').then(() => sleep(300)).then(() => page.click('.menu >> text=Folders & options…')); await sleep(700);
  await S.modal('phase2-options');
  await page.click('#modal button:has-text("Cancel")'); await sleep(400);
  // export menu and .fuz build
  await page.click('.statusbar button:has-text("Export")'); await sleep(500);
  await S.full('phase2-export-menu');
  await page.click('text=Build .fuz into the final folder'); await sleep(800);
  if (await page.locator('#modal:not(.hidden)').count()) { /* fix overwrite question */ }
  await page.waitForFunction(() => document.querySelector('#job')?.classList.contains('hidden') && document.querySelector('#modal:not(.hidden)'), null, { timeout: 60000 }); await sleep(600);
  await S.modal('phase2-fuz-report');
  await page.locator('#modal .btn.primary').first().click(); await sleep(500);
  // Phase 1 change → stale export prompt: exclude a generated line and export WAV + LIP twice
  await page.click('.statusbar button:has-text("Export")'); await sleep(400);
  await page.click('text=WAV + LIP to the WAV + LIP folder'); await sleep(1200);
  await page.locator('#modal .btn.primary').first().click().catch(() => {}); await sleep(600);
  await row('rent a room').locator('.excl-btn').click(); await sleep(500);
  await page.click('.statusbar button:has-text("Export")'); await sleep(400);
  await page.click('text=WAV + LIP to the WAV + LIP folder'); await sleep(1200);
  if (await page.locator('text=Files from earlier exports').count()) { await S.modal('phase2-stale'); await page.click('#modal button:has-text("Delete these")'); await sleep(1200); }
  await page.locator('#modal .btn.primary').first().click().catch(() => {}); await sleep(500);
  // second patch for the DLCs, then the patch list
  await page.click('button.crumb[data-section="mods"]'); await page.waitForSelector('.pcard');
  await page.click('.pcard:has-text("Skyrim DLCs")'); await page.waitForSelector('.vrow');
  await page.click('button:has-text("Continue to Phase 2")'); await page.waitForSelector('.statusbar .gen-btn'); await sleep(800);
  await genPick(page, 'Missing'); await sleep(700); await page.click('#modal .btn.primary'); await waitJob(page);
  await page.click('.statusbar button:has-text("Export")'); await sleep(400);
  await page.click('text=Build .fuz into the final folder'); await sleep(800);
  await page.waitForFunction(() => document.querySelector('#job')?.classList.contains('hidden') && document.querySelector('#modal:not(.hidden)'), null, { timeout: 60000 }).catch(async (e) => { await page.screenshot({ path: '/tmp/dbvo-work/demo/shots/FAIL-dlcfuz.png' }); throw e; }); await sleep(500);
  await page.locator('#modal .btn.primary').first().click(); await sleep(400);
  await page.click('button.crumb[data-section="patches"]'); await sleep(1000);
  await S.full('phase2-list');
  await import('./shotsC.mjs').then((m) => m.run(s, S)).catch((e) => console.log('C failed', e.message));
}
