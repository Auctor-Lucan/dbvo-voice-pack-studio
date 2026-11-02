// 7.1 screenshots: Output popover, a problem message, the ticked menu, the .fuz cache row.
import { boot, shooter, sleep } from './shots_common.mjs';
const s = await boot(); const { page } = s; const S = shooter(page);
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
await page.waitForSelector('.home-cards');
const st = await page.evaluate(() => import('/lib.js').then((l) => l.rpc('state')));
const sky = st.patches.find((p) => p.name === 'Skyrim');
await page.evaluate((id) => import('/app.js').then((m) => m.go('patch', { id })), sky.id); await page.waitForSelector('.vrow'); await sleep(1200);
await page.click('.toolbar .tgroup:has(label:text-is("Output")) button'); await sleep(500);
await S.clip('v71-output', { x: 0, y: 60, width: 1480, height: 260 });
await page.keyboard.press('Escape'); await sleep(300);
// AI Auto Tag with an online provider and no key
await page.evaluate(() => { const st = window.__dbvoState; st.settings.aiProvider = { endpoint: 'https://api.openai.com/v1', model: 'gpt-4o-mini', hasKey: false }; });
const boxes = page.locator('.vrow input[type=checkbox]'); await boxes.nth(2).click(); await boxes.nth(3).click(); await sleep(300);
await page.click('.tagbar button:has-text("AI Auto Tag")'); await sleep(700);
await S.full('v71-error');
await page.click('.toast-x'); await sleep(300);
await page.click('button:has-text("Ticked (")'); await sleep(400);
await S.full('v71-ticked');
await page.keyboard.press('Escape'); await sleep(300);
// ? toggles
await page.click('.view-head .help-btn'); await sleep(600); const open1 = await page.locator('.help-drawer').count();
await page.click('.view-head .help-btn'); await sleep(400); const open2 = await page.locator('.help-drawer').count();
console.log('HELP TOGGLE', open1, open2);
await page.evaluate(() => import('/app.js').then((m) => m.go('settings', { tab: 'general' }))); await sleep(1500);
await page.locator('.section:has(h2:text-is("Library"))').scrollIntoViewIfNeeded(); await sleep(400);
await S.el('v71-fuzcache', '.section:has(h2:text-is("Library"))');
console.log('ERRORS', errs);
process.exit(0);
