// Click test through the grids: one human-speed click right after typing, fast regenerate clicks, spinners.
import { start } from './bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const O = process.env.OUTP;
const s = await start({ width: 1600, height: 950, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
await page.evaluate(() => { localStorage.setItem('dbvo:seen:tags-context', 'true'); });
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
let pass = 0, failN = 0;
const ok = (c, msg) => { if (c) { pass++; console.log('PASS', msg); } else { failN++; console.log('FAIL', msg); } };
// A human click: press, hold ~130 ms, release.
async function human(loc) {
  await loc.scrollIntoViewIfNeeded();
  const b = await loc.boundingBox();
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
  await page.mouse.down(); await sleep(130); await page.mouse.up();
}
const row = (key) => page.locator(`.vrow[data-key="${key}"]`);
const st = await rpc('state', {});

// ---------------- Phase 1
const mods = st.mods;
const M = mods.find((m) => m.name === 'Skyrim');
let mod = await rpc('mod.get', { id: M.id });
const pl = mod.plugins[0];
await page.evaluate((id) => import('/app.js').then((m) => m.go('mod', { id })), M.id); await sleep(1500);
const valid = pl.lines.filter((l) => l.kind === 'Valid' && l.status !== 'Removed' && !l.excluded);
{
  const a = valid[0], b = valid[1];
  const inp = row(a.key).locator('input[type=text]').first();
  await inp.click(); await page.keyboard.press('End'); await page.keyboard.type(' Typed');
  const before = (await rpc('mod.get', { id: M.id })).plugins[0].lines.find((x) => x.key === b.key).voiced;
  await human(row(b.key).locator('button.appr:not(.fix)').first()); await sleep(700);
  const m2 = (await rpc('mod.get', { id: M.id })).plugins[0];
  ok(m2.lines.find((x) => x.key === a.key).final.endsWith(' Typed'), 'P1: typed text saved on leaving the box');
  ok(m2.lines.find((x) => x.key === b.key).voiced !== before, 'P1: one click on ♪ of another row right after typing');
  // same row: type, then click its own ♪
  const inp2 = row(b.key).locator('input[type=text]').first();
  await inp2.click(); await page.keyboard.press('End'); await page.keyboard.type(' X');
  const v2 = m2.lines.find((x) => x.key === b.key).voiced;
  await human(row(b.key).locator('button.appr:not(.fix)').first()); await sleep(700);
  const m3 = (await rpc('mod.get', { id: M.id })).plugins[0];
  ok(m3.lines.find((x) => x.key === b.key).voiced !== v2 && m3.lines.find((x) => x.key === b.key).final.endsWith(' X'), 'P1: one click on ♪ of the same row right after typing');
  // type then click a filter chip
  const inp3 = row(a.key).locator('input[type=text]').first();
  await inp3.click(); await page.keyboard.type('!');
  await human(page.locator('.filter-chips .chip', { hasText: 'In DBVO Fix' }));
  await sleep(600);
  ok(await page.locator('.filter-chips .chip.on', { hasText: 'In DBVO Fix' }).count() === 1, 'P1: one click on a filter chip right after typing');
  await human(page.locator('.filter-chips .chip', { hasText: 'All' }).first()); await sleep(400);
  // checkbox + bulk buttons
  await human(row(a.key).locator('input[type=checkbox]')); await sleep(200);
  ok(await page.locator('.vrow.sel').count() === 1, 'P1: ticking a row');
  await human(page.locator('button', { hasText: 'Mark reviewed' })); await sleep(500);
  ok((await rpc('mod.get', { id: M.id })).plugins[0].lines.find((x) => x.key === a.key).reviewed, 'P1: Mark reviewed on the ticked row');
  if (await page.locator('button', { hasText: 'Clear selection' }).count()) { await human(page.locator('button', { hasText: 'Clear selection' })); await sleep(300); }
  for (const l of [a, b]) { await rpc('line1.update', { modId: M.id, plugin: pl.fileName, key: l.key, action: 'revert' }); await rpc('line1.update', { modId: M.id, plugin: pl.fileName, key: l.key, action: 'voice', value: null }); }
}

// ---------------- Phase 2
const P = st.patches.find((x) => x.name === 'Skyrim') || st.patches[0];
await page.evaluate((id) => import('/app.js').then((m) => m.go('patch', { id })), P.id); await sleep(2500);
let patch = await rpc('patch.get', { id: P.id });
const gen = patch.lines.filter((l) => !l.omitted && !l.excluded).slice(0, 8);
const spinning = () => page.locator('.act-btn.spinning').count();
{
  // fast regenerate clicks on three rows
  for (const l of gen.slice(0, 3)) { await row(l.lineId).locator('.icon-bar .act-btn').first().click(); await sleep(90); }
  await sleep(300);
  console.log('spinning right after clicks:', await spinning());
  await page.screenshot({ path: `${O}/p2_spinning.png` });
  for (let i = 0; i < 40 && (await page.evaluate(() => !!(window.__dbvoState && 0)) || true); i++) { if (!(await spinning())) break; await sleep(500); }
  await sleep(1500);
  ok(await spinning() === 0, 'P2: no ↻ left spinning after fast clicks on 3 rows');
  patch = await rpc('patch.get', { id: P.id });
  const got = gen.slice(0, 3).map((l) => patch.lines.find((x) => x.lineId === l.lineId).takes.length - l.takes.length);
  ok(got.every((n) => n >= 1), 'P2: each of the 3 rows got a new take ' + JSON.stringify(got));
  // double-click regenerate on the same row
  const l3 = gen[3];
  const r3 = row(l3.lineId).locator('.icon-bar .act-btn').first();
  await r3.click(); await sleep(60); await r3.click();
  for (let i = 0; i < 30; i++) { if (!(await spinning())) break; await sleep(500); }
  await sleep(1500);
  ok(await spinning() === 0, 'P2: no ↻ left spinning after a double click on one row');
  // edit spoken text, click regenerate once (existing), then focus the box again while it generates
  const l4 = gen[4];
  const sp = row(l4.lineId).locator('input[type=text]').first();
  await sp.click(); await page.keyboard.press('End'); await page.keyboard.type(' again');
  await human(row(l4.lineId).locator('.icon-bar .act-btn').first());
  await sleep(150);
  await row(l4.lineId).locator('input[type=text]').first().click();
  for (let i = 0; i < 30; i++) { await sleep(500); const p2 = await rpc('patch.get', { id: P.id }); if (p2.lines.find((x) => x.lineId === l4.lineId).takes.length > l4.takes.length) break; }
  await sleep(800);
  ok(await row(l4.lineId).locator('.act-btn.spinning').count() === 0, 'P2: ↻ stops while the spoken text box of that row has the focus');
  ok(await page.evaluate(() => document.activeElement && document.activeElement.matches('input[type=text]')), 'P2: the focus stayed in the box');
  await page.keyboard.press('Escape');
  // type then single click flag / exclude / star / play in same and other rows
  for (const [label, sel, field] of [['flag', '.flag-btn', 'marked'], ['exclude', '.excl-btn', 'excluded']]) {
    for (const same of [true, false]) {
      const la = gen[5], lb = same ? gen[5] : gen[6];
      const before = (await rpc('patch.get', { id: P.id })).lines.find((x) => x.lineId === lb.lineId)[field];
      const box = row(la.lineId).locator('input[type=text]').first();
      await box.click(); await page.keyboard.press('End'); await page.keyboard.type('.');
      await human(row(lb.lineId).locator(sel).first()); await sleep(700);
      const after = (await rpc('patch.get', { id: P.id })).lines.find((x) => x.lineId === lb.lineId)[field];
      ok(after !== before, `P2: one click on ${label} (${same ? 'same' : 'other'} row) right after typing`);
      await rpc('line2.update', { id: P.id, lineId: lb.lineId, action: field === 'marked' ? 'mark' : after ? 'include' : 'exclude', value: field === 'marked' ? before : undefined });
      await sleep(300);
    }
  }
  // star (keep) right after typing
  const lk = (await rpc('patch.get', { id: P.id })).lines.find((x) => x.lineId === gen[0].lineId);
  const keptBefore = (lk.takes.find((t) => t.id === lk.selectedTakeId) || {}).kept;
  await row(gen[1].lineId).locator('input[type=text]').first().click(); await page.keyboard.type(',');
  await human(row(lk.lineId).locator('.icon-bar .act-btn').nth(1)); await sleep(700);
  const lk2 = (await rpc('patch.get', { id: P.id })).lines.find((x) => x.lineId === lk.lineId);
  ok((lk2.takes.find((t) => t.id === lk2.selectedTakeId) || {}).kept !== keptBefore, 'P2: one click on ☆ keep right after typing');
  // inspector: type in the panel's spoken text, then click a panel button
  await human(row(gen[2].lineId).locator('.cell').nth(3)); await sleep(400);
  const ta = page.locator('.inspector textarea, .insp textarea').first();
  if (await ta.count()) {
    await ta.click(); await page.keyboard.press('End'); await page.keyboard.type(' panel');
    const flagB = page.locator('.inspector button[title*="Flag"]').first();
    if (await flagB.count()) {
      const mb = (await rpc('patch.get', { id: P.id })).lines.find((x) => x.lineId === gen[2].lineId).marked;
      await human(flagB); await sleep(700);
      ok((await rpc('patch.get', { id: P.id })).lines.find((x) => x.lineId === gen[2].lineId).marked !== mb, 'P2 panel: one click on a panel button right after typing');
    } else console.log('SKIP no flag button in panel');
  } else console.log('SKIP no panel textarea');
  await page.screenshot({ path: `${O}/p2_after.png` });
}
// ---------------- NPC Revoice
{
  const rv = (await rpc('revoice.list', {}))[0];
  await page.evaluate((id) => import('/app.js').then((m) => m.go('revoice', { id })), rv.id); await sleep(1500);
  await page.click('button:has-text("Lines (")'); await sleep(900);
  const keys = await page.locator('.vrow').evaluateAll((els) => els.slice(0, 4).map((e) => e.dataset.key));
  const regenOf = (k) => row(k).locator('.act-btn').filter({ has: page.locator('svg') }).first();
  let clicked = 0;
  for (const k of keys.slice(0, 3)) { const b = row(k).locator('button.act-btn[title*="Regenerate"], button.act-btn[title*="Making"], button.act-btn[title*="Generate"]').first(); if (await b.count() && await b.isEnabled()) { await b.click(); clicked++; await sleep(90); } }
  console.log('revoice regen clicks:', clicked);
  for (let i = 0; i < 40; i++) { await sleep(500); if (!(await spinning())) break; }
  await sleep(1500);
  ok(await spinning() === 0, `Revoice: no ↻ left spinning after fast clicks (${clicked} rows)`);
  const k0 = keys[0], k1 = keys[1];
  const box = row(k0).locator('input[type=text]').first();
  if (await box.count()) {
    const flag = row(k1).locator('button.act-btn.flag-btn, button[title*="Flag"]').first();
    const before = await flag.evaluate((e) => e.classList.contains('on'));
    await box.click(); await page.keyboard.press('End'); await page.keyboard.type('.');
    await human(flag); await sleep(800);
    ok(await row(k1).locator('button.act-btn.flag-btn, button[title*="Flag"]').first().evaluate((e) => e.classList.contains('on')) !== before, 'Revoice: one click on flag right after typing');
    await row(k1).locator('button.act-btn.flag-btn, button[title*="Flag"]').first().click(); await sleep(500);
    await row(k0).locator('input[type=text]').first().click(); await page.keyboard.press('End'); await page.keyboard.press('Backspace'); await page.keyboard.press('Tab'); await sleep(500);
  } else console.log('SKIP revoice has no text box');
  await page.screenshot({ path: `${O}/rv_after.png` });
}
// restore spoken texts
patch = await rpc('patch.get', { id: P.id });
for (const l of gen) { const n = patch.lines.find((x) => x.lineId === l.lineId); if (n.spoken !== l.spoken) await rpc('line2.update', { id: P.id, lineId: l.lineId, action: 'spoken', value: l.spoken }); }
console.log(`\n${pass} passed, ${failN} failed`);
process.exit(0);
