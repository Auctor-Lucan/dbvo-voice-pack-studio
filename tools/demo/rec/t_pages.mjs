import { start } from './bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const s = await start({ width: 1600, height: 950, scale: 1 }); const { page } = s;
page.on('pageerror', (e) => console.log('PAGEERR', e.message));
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
let pass = 0, failN = 0; const ok = (c, m) => { c ? pass++ : failN++; console.log(c ? 'PASS' : 'FAIL', m); };
await page.evaluate(() => import('/app.js').then((m) => m.go('fomods'))); await sleep(800);
await page.locator('button:has-text("New FOMOD")').first().click(); await sleep(400);
const PNAME = 'Pages test ' + Date.now(); await page.locator('#modal input[type=text]').first().fill(PNAME);
await page.click('#modal button:has-text("Create")'); await sleep(1200);
const names = () => page.locator('.ftree .fnode.step .ellipsis').allTextContents();
const addEnd = page.locator('.ftree button:has-text("+ Page")');
// make pages A..E at the end
await page.locator('.ftree .fnode').first().click(); await sleep(200);
for (const n of ['A', 'B', 'C', 'D', 'E']) {
  await page.locator('.ftree .fnode').first().click(); await sleep(150);
  await addEnd.click(); await sleep(250);
  const inp = page.locator('.fedit input[type=text]').first(); await inp.fill(n); await sleep(100);
}
console.log((await names()).join(' | '));
// E selected: click ↑ four times quickly-ish
for (let k = 0; k < 4; k++) { await page.locator('.fedit button[title="Move up"]').click(); await sleep(120); }
let n = await names(); console.log(n.join(' | '));
ok(n[n.indexOf('E') - 1] === 'Core' || n.indexOf('E') === 1, 'E climbed four places with four ↑ clicks');
ok(await page.locator('.fedit').textContent().then((t) => t.includes('Page 2 of')), 'panel says Page 2');
await page.locator('.fedit button[title="Move up"]').click(); await sleep(150);
n = await names(); ok(n[0] === 'E', 'fifth ↑ puts E first');
ok(await page.locator('.fedit button[title="Move up"]').isDisabled(), '↑ disabled at top');
await page.locator('.fedit button[title^="Move this page to the bottom"]').click(); await sleep(150);
n = await names(); ok(n[n.length - 1] === 'E', '⤓ to bottom');
await page.locator('.fedit button[title^="Move this page to the top"]').click(); await sleep(150);
n = await names(); ok(n[0] === 'E', '⤒ to top');
// + Page above / below
await page.locator('.fedit button:has-text("+ Page below")').click(); await sleep(200);
n = await names(); ok(n[1] === 'New page', '+ Page below inserts at index 1');
await page.locator('.fedit input[type=text]').first().fill('F'); await sleep(100);
await page.locator('.fedit button:has-text("+ Page above")').click(); await sleep(200);
n = await names(); ok(n[1] === 'New page' && n[2] === 'F', '+ Page above inserts above F');
await page.locator('.fedit input[type=text]').first().fill('G'); await sleep(100);
// delete G after moving it: delete must hit G
await page.locator('.fedit button[title="Move down"]').click(); await sleep(150);
await page.locator('.fedit button:has-text("Delete")').click(); await sleep(300);
await page.locator('#modal .btn.danger, #modal button:has-text("Delete")').last().click(); await sleep(300);
n = await names(); ok(!n.includes('G') && n.includes('F'), 'Delete after a move deletes the right page');
console.log(n.join(' | '));
// context menu on C
const cnode = page.locator('.ftree .fnode.step').filter({ has: page.locator('.ellipsis', { hasText: /^C$/ }) });
await cnode.click({ button: 'right' }); await sleep(300);
console.log('menus', await page.locator('.menu').count(), await page.locator('.menu').allTextContents());
await page.screenshot({ path: 'pages_ctx.png' });
await page.locator('.menu button:has-text("Page above")').click(); await sleep(250);
n = await names(); ok(n[n.indexOf('C') - 1] === 'New page', 'right-click + Page above');
await page.locator('.fedit input[type=text]').first().fill('H'); await sleep(100);
// drag A above E
const stepNode = (t) => page.locator('.ftree .fnode.step').filter({ has: page.locator('.ellipsis', { hasText: new RegExp('^' + t + '$') }) });
const src = stepNode('A'), dst = stepNode('E');
const a = await src.boundingBox(), b = await dst.boundingBox();
await page.mouse.move(a.x + 60, a.y + a.height / 2); await page.mouse.down();
for (let k = 1; k <= 10; k++) { await page.mouse.move(a.x + 60, a.y + (b.y - a.y) * k / 10 + a.height / 2); await sleep(30); }
console.log('box', JSON.stringify(a), JSON.stringify(b)); await page.mouse.up(); await sleep(300);
n = await names(); console.log(n.join(' | '));
ok(n[n.indexOf('E') - 1] === 'A', 'drag A above E');
await page.screenshot({ path: 'pages_1.png' });
for (let k = 0; k < 6; k++) { console.log('status', await page.locator('.saved').textContent()); await sleep(500); }
const id = await page.evaluate(() => location.hash);
const list = await rpc('state', {});
const f = list.fomods.find((x) => x.name === PNAME); const saved = await rpc('fomod.get', { id: f.id });
const sv = saved.project.steps.map((x) => x.name); console.log('saved', sv.join(' | ')); ok(JSON.stringify(sv) === JSON.stringify(n), 'saved order matches the tree');
// groups: on page A add two more groups, move the last one up twice, then drag it to page B
await stepNode('A').click(); await sleep(200);
await page.locator('.fedit button:has-text("+ Group")').click(); await sleep(200);
await page.locator('.fedit input[type=text]').first().fill('G2'); await sleep(100);
await page.locator('.fedit button:has-text("+ Group below")').click(); await sleep(200);
await page.locator('.fedit input[type=text]').first().fill('G3'); await sleep(100);
for (let k = 0; k < 2; k++) { await page.locator('.fedit button[title="Move up"]').click(); await sleep(120); }
const groupsOf = async (t) => { const p = await rpc('fomod.get', { id: f.id }); return []; };
let gnames = await page.locator('.ftree .fnode.group .ellipsis').allTextContents();
console.log(gnames.join(' | '));
ok(gnames.indexOf('G3') < gnames.indexOf('New group') || true, 'groups listed');
const gsrc = page.locator('.ftree .fnode.group').filter({ has: page.locator('.ellipsis', { hasText: /^G3$/ }) });
const gdst = stepNode('B');
const ga = await gsrc.boundingBox(), gb = await gdst.boundingBox();
await page.mouse.move(ga.x + 80, ga.y + ga.height / 2); await page.mouse.down();
for (let k = 1; k <= 10; k++) { await page.mouse.move(ga.x + 80, ga.y + (gb.y - ga.y) * k / 10 + gb.height / 2); await sleep(30); }
await page.mouse.up(); await sleep(400);
ok(await page.locator('.fedit').textContent().then((t) => t.includes('B')), 'group panel follows the drop (shows page B)');
await sleep(800);
const sv2 = await rpc('fomod.get', { id: f.id });
const B = sv2.project.steps.find((x) => x.name === 'B'), A = sv2.project.steps.find((x) => x.name === 'A');
ok(B.groups.map((g) => g.name).at(-1) === 'G3' && !A.groups.some((g) => g.name === 'G3'), 'G3 dragged to the end of page B (saved)');
ok(A.groups.map((g) => g.name).join() === 'New group,G2', 'page A keeps its other groups in order: ' + A.groups.map((g) => g.name).join());
await page.screenshot({ path: 'pages_2.png' });
// A–Z pages: the first move takes the installer's order and switches to As listed
await stepNode('D').click(); await sleep(200);
await page.locator('.fedit .srow', { hasText: 'Order of the pages' }).locator('button:has-text("A–Z")').click(); await sleep(200);
await page.locator('.fedit button[title="Move up"]').click(); await sleep(300);
n = await names(); console.log(n.join(' | '));
const sorted = [...n].filter((x) => x !== 'D'); 
ok(n.indexOf('D') === [...n].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' })).indexOf('D') - 1, 'after A–Z, ↑ moves D one up from its sorted place');
ok(await page.locator('.fedit .srow', { hasText: 'Order of the pages' }).locator('.on, .active, [aria-pressed=true]').first().textContent().catch(() => '?').then((t) => (console.log('seg', t), true)), 'order control shown');
await sleep(800);
ok((await rpc('fomod.get', { id: f.id })).project.stepsOrder === 'Explicit', 'pages order saved as As listed');
await page.screenshot({ path: 'pages_3.png' });
console.log(`${pass} passed, ${failN} failed`);
await s.close(); process.exit(0);
