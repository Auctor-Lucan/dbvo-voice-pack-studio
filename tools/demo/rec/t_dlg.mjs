import { start } from './bridge.mjs';
import { sleep } from './shots_common.mjs';
const s = await start({ width: 1500, height: 900, scale: 1 }); const { page } = s;
const errs = []; page.on('pageerror', (e) => errs.push(e.message));
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const ev = (f) => page.evaluate(f);
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const go = async (v, p) => { await page.evaluate(([v, p]) => import('/app.js').then((m) => m.go(v, p)), [v, p]); await sleep(1200); };
const title = () => page.locator('#modal:not(.hidden) .modal-card h2').first().innerText().catch(() => '(none)');
// 1. nested: open A, then B on it; Escape closes B only; Escape again closes A
await ev(() => import('/lib.js').then((l) => { window.__a = l.modal('Alpha', [l.h('div', 'a')]); setTimeout(() => { window.__b = l.modal('Beta', [l.h('div', 'b')]); }, 50); }));
await sleep(200); console.log('top', await title());
await page.keyboard.press('Escape'); await sleep(100); console.log('after esc', await title());
await page.keyboard.press('Escape'); await sleep(100); console.log('after esc2', await title());
// 2. closing the lower one closes the upper too and both resolve
await ev(() => import('/lib.js').then((l) => { window.__r = []; l.modal('One', [l.h('div', '1')], undefined, { onOpen: (c) => { window.__c1 = c; } }).then((v) => window.__r.push('one:' + v)); l.modal('Two', [l.h('div', '2')]).then((v) => window.__r.push('two:' + v)); }));
await sleep(100); await ev(() => window.__c1('x')); await sleep(100);
console.log('resolved', await ev(() => window.__r.join(',')), 'visible', await title());
// 3. duplicate title is refused
const dup = await ev(() => import('/lib.js').then(async (l) => { l.modal('Same', [l.h('div', 's')]); const v = await l.modal('Same', [l.h('div', 's2')]); return String(v); }));
console.log('dup ->', dup, 'top', await title());
// 4. navigation closes it
await go('mods', {}); console.log('after go', await title());
// 5. new mod + new category still works, Escape on the prompt returns to New mod
await page.click('button:has-text("+ New mod")'); await sleep(300);
await page.selectOption('#modal select', '__new'); await sleep(300); console.log('prompt', await title());
await page.keyboard.press('Escape'); await sleep(200); console.log('back to', await title(), 'select', await page.inputValue('#modal select'));
await page.keyboard.press('Escape'); await sleep(200);
// 6. takes -> edit take -> cancel -> takes back; focus not in Gain
const st = await rpc('state', {}); const pid = st.patches[0].id;
await go('patch', { id: pid }); await page.waitForSelector('.vrow');
const p0 = await rpc('patch.get', { id: pid }); const l0 = p0.lines.find((l) => l.takes.length > 1) || p0.lines.find((l) => l.selectedTakeId);
await ev(`document.querySelector('.vrow[data-key="${l0.lineId}"] .take-lbl').click()`); await sleep(400);
console.log('takes', await title());
await page.click('#modal button:has-text("Edit take")'); await page.waitForSelector('.te-canvas'); await sleep(500);
console.log('editor', await title(), 'focus', await ev(() => document.activeElement.className + '/' + document.activeElement.tagName));
await page.keyboard.press('Escape'); await sleep(300); console.log('back to', await title());
await page.keyboard.press('Escape'); await sleep(200); console.log('closed', await title());
console.log('errs', errs.join(' | '));
process.exit(0);
