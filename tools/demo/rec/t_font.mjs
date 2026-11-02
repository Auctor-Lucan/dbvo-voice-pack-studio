import { boot, sleep } from './shots_common.mjs';
const D = '/tmp/dbvo-work/demo/shots/font';
const s = await boot(); const { page } = s;
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1300); };
const size = async (x) => { await rpc('settings.save', { settings: { ui: { readSize: x } } }).catch(async () => {}); await page.evaluate((x) => import('/app.js').then((m) => m.applyBackground({ ...(m.app.state.settings.ui || {}), readSize: x })), x); };
let n = 0; const shot = async (name) => { await sleep(500); n++; const f = `${D}/${String(n).padStart(2, '0')}-${name}.png`; await page.screenshot({ path: f }); console.log('shot', f); };
const setLS = (k, v) => page.evaluate(([k, v]) => localStorage.setItem('dbvo:' + k, JSON.stringify(v)), [k, v]);
const scrollTo = (sel, text) => page.evaluate(([sel, text]) => { const el = [...document.querySelectorAll(sel)].find((e) => e.textContent.includes(text)); el && el.scrollIntoView({ block: 'start' }); }, [sel, text]);

await size('normal'); await go('home'); await shot('landing-now-normal');
await size('larger'); await go('home'); await shot('landing-larger');
await size('largest'); await go('home'); await shot('landing-largest');
await size('larger');
await go('help', {}); await shot('guide-top');
await page.evaluate(() => document.getElementById('generating').scrollIntoView({ block: 'start' })); await sleep(400); await shot('guide-generating');
await page.evaluate(() => document.getElementById('folders-options').scrollIntoView({ block: 'start' })); await sleep(400); await shot('guide-folders-options');
await page.evaluate(() => document.getElementById('review-the-lines').scrollIntoView({ block: 'start' })); await sleep(400); await shot('guide-phase1-review');
const st = await rpc('state', {});
const sky = st.patches.find((p) => p.name === 'Skyrim') || st.patches[0];
await go('patch', { id: sky.id }); await page.waitForSelector('.vrow');
await page.evaluate(() => import('/help.js').then((m) => m.openHelp('generating'))); await sleep(900); await shot('phase2-workspace-unchanged-with-help-drawer');
await page.keyboard.press('Escape'); await sleep(300);
await page.locator('button:has-text("Folders")').first().click(); await sleep(300);
await page.locator('.pop-panel :text("Folders & options…"), .menu :text("Folders & options…")').first().click().catch(() => {}); await sleep(900);
await shot('dialog-folders-options'); await page.keyboard.press('Escape'); await sleep(400);
for (const [t, label] of [['general', 'settings-general'], ['general', 'settings-appearance'], ['voices', 'settings-voices'], ['pronunciation', 'settings-pronunciation'], ['references', 'settings-references'], ['categories', 'settings-categories'], ['credits', 'settings-credits']]) {
  await setLS('settingsTab', t); await go('settings', {});
  if (label === 'settings-appearance') { await scrollTo('.settings h2, .settings h3, .settings .sec-title, .settings b', 'Reading text size'); }
  await shot(label);
}
const rv = (await rpc('revoice.list', {}))[0];
for (const t of ['setup', 'voicetypes']) { await setLS('rv-tab:' + rv.id, t); await go('home'); await go('revoice', { id: rv.id }); await shot('npc-revoice-' + t); }
const bk = (await rpc('books.list', {}))[0];
for (const t of ['setup', 'voices']) { await setLS('bk-tab:' + bk.id, t); await go('home'); await go('book', { id: bk.id }); await shot('voiced-books-' + t); }
const mod = st.mods[0];
await go('mod', { id: mod.id }); await sleep(800);
await page.locator('.view-head button:has-text("Delete")').first().click().catch(() => console.log('no delete btn')); await sleep(700);
await shot('dialog-delete-mod'); await page.keyboard.press('Escape');
console.log('errors:', errs.join(' / ') || 'none');
await s.close(); process.exit(0);
