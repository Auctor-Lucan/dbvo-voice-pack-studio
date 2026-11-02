import { sleep } from './driver.mjs';
import { genPick } from './shots_common.mjs';
// Fast setup without presentation, to reach a scene for exploring.
export async function fastPhase1(s) {
  const { page } = s;
  await page.waitForSelector('text=No mods yet');
  s.dropNext(['D:\\Modding\\Downloads\\Serana Dialogue Addon\\SeranaDialogAddon.esp', 'D:\\Modding\\Downloads\\Ashe - Fire and Blood\\Ashe - Fire and Blood.esp']);
  await page.evaluate(() => { const dt = new DataTransfer(); dt.items.add(new File(['x'], 'a.esp')); document.querySelector('.view-body').dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: dt })); });
  await page.waitForSelector('table.batch');
  await page.locator('table.batch input[type=text]').nth(0).fill('Serana Dialogue Addon');
  await page.locator('table.batch select').nth(0).selectOption('Followers');
  await page.locator('table.batch select').nth(1).selectOption('Followers');
  await page.click('button:has-text("Create mods and import")');
  await page.waitForSelector('text=Mods added'); await page.click('#modal button:has-text("OK")'); await sleep(500);
  await page.click('.pcard:has-text("Serana Dialogue Addon")'); await page.waitForSelector('.vrow');
}
export async function fastFixAndContinue(s) {
  const { page } = s;
  await page.locator('.vrow:has-text("MeetingPlace") input[type=text]').first().click();
  await page.keyboard.type('Meet me at the Bee and Barb after dark.'); await page.keyboard.press('Enter'); await sleep(500);
  await page.click('button:has-text("Continue to Phase 2")'); await page.waitForSelector('.statusbar .gen-btn');
  await sleep(800);
}
export async function fastGenerate(s) {
  const { page } = s;
  await genPick(page, 'Missing'); await sleep(500);
  await page.click('#modal .btn.primary');
  await page.waitForFunction(() => document.querySelector('#job')?.classList.contains('hidden'), null, { timeout: 60000 }); await sleep(600);
}
export async function fastFuz(s) {
  const { page } = s;
  await page.click('button:has-text("Export")'); await sleep(300);
  await page.click('text=Build .fuz into the final folder'); await sleep(500);
  await page.waitForFunction(() => document.querySelector('#job')?.classList.contains('hidden') && document.querySelector('#modal:not(.hidden)'), null, { timeout: 60000 }); await sleep(400);
  await page.locator('#modal .btn.primary').first().click(); await sleep(400);
}
export async function fastAshe(s) {
  const { page } = s;
  await page.click('button.crumb[data-section="mods"]'); await page.waitForSelector('.pcard');
  await page.click('.pcard:has-text("Ashe - Fire and Blood")'); await page.waitForSelector('.vrow');
  await page.click('button:has-text("Continue to Phase 2")'); await page.waitForSelector('.statusbar .gen-btn'); await sleep(600);
  await fastGenerate(s); await fastFuz(s);
}
