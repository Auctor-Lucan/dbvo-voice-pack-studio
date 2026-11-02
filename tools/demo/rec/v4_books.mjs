// Video 4: the Voiced Books Tool, for Voiced Books of Skyrim.
import { start } from './bridge.mjs';
import { overlay, makeDriver, startCapture, sleep } from './driver.mjs';

const OUT = process.env.OUT || 'frames_v4';
const s = await start();
const { page } = s;
await page.goto('https://app.dbvo/index.html');
await page.waitForSelector('.home-checks');
await overlay(page);
const d = makeDriver(page);
await d.fade(true);
const cap = process.env.NOCAP ? { pause() {}, resume() {}, async stop() { return 0; } } : await startCapture(page, OUT);
let k = 0;
const shot = () => (process.env.SHOTS ? page.screenshot({ path: `v4_${k++}.png` }) : null);
let capAt = 0, capHold = 0;
const say = async (kick, text) => { const w = capAt + capHold - Date.now(); if (w > 0) await sleep(w); await d.caption(kick, text); capAt = Date.now(); capHold = Math.max(3200, text.length * 62); };
const settle = async () => { const w = capAt + capHold - Date.now(); if (w > 0) await sleep(w); };
const until = (fn, t = 180000) => page.waitForFunction(fn, null, { timeout: t, polling: 100 });
const jobDone = () => until(() => document.querySelector('#job')?.classList.contains('hidden'));
const mid = async (sel) => { await page.locator(sel).first().evaluate((e) => e.scrollIntoView({ block: 'center', behavior: 'smooth' })); await sleep(700); };
const row = (label) => `.srow:has(.lbl > span:text-is("${label}"))`;

await sleep(400);
await d.fade(false);
await say('Voiced Books Tool', 'The Voiced Books Tool narrates books, letters and journals for Voiced Books of Skyrim, in a narrator voice of your choosing.');
await d.click('#tools-btn', { pause: 700 });
await d.click('.menu >> text=Voiced Books Tool', { pause: 1100 });
await d.click('button:has-text("+ New project")', { pause: 900 });
await say('Voiced Books Tool', 'The base game comes as a built-in text set. Books from any mod are read straight out of its plugins.');
await d.hover('#modal label >> nth=0', 1800);
await d.click('#modal label:has-text("From a mod")', { pause: 500 });
const ins = page.locator('#modal input[type=text]');
await d.clickType(ins.nth(0), 'Example Library voiced', { delay: 40 });
await d.clickType(ins.nth(1), 'Example Library', { delay: 40 });
await d.clickType(ins.nth(2), 'Sample Narrator', { delay: 40, clear: true });
await shot();
await d.click('#modal button:has-text("Create")', { pause: 1000 });

// ---------------------------------------------------------------- setup
await d.click('button:text-is("Setup")', { pause: 800 });
await say('Setup', 'Add the mod\'s plugin and scan. Strings, archives, masters and NPC records are all read for you.');
s.pick('D:\\Modding\\Downloads\\Example Library\\Example Library.esp');
await d.click('button:has-text("Add plugins…")', { pause: 1000 });
await d.click('.view-head button:has-text("Scan")', { pause: 600 });
await page.waitForSelector('text=read by NPC voices', { timeout: 30000 });
await sleep(500);
await d.hover('text=read by NPC voices', 2200);
await shot();
await mid('.section:has(h2:text-is("Names and output"))');
await say('Setup', 'You get one archive per narrator, and one for NPC voices. Voiced Books of Skyrim finds them through the VBOS ini file.');
await d.hover(`${row('Archives')} .lbl`, 1800);
await d.hover(`${row('.ini name')} .lbl`, 1600);
await say('Setup', 'They\'re packed the way Voiced Books of Skyrim expects: a BSA, with a small dummy plugin that loads it.');
await mid(row('Packaging'));
await d.hover(`${row('Packaging')} .seg button >> nth=0`, 2200);
s.pick('D:\\Modding\\Mods\\Example Library Voiced');
await d.click(`${row('Output folder')} button:has-text("Browse")`, { pause: 700 });
await shot();

// ---------------------------------------------------------------- narrators
await d.click('button:has-text("Narrators & authors")', { pause: 900 });
await say('Narrators & authors', 'The narrator reads the books. Add more narrators, and players choose one in the installer.');
const vsel = (i) => page.locator('select:has(option:has-text("Old Nord Narrator"))').nth(i);
const pick = async (i, name) => d.select(vsel(i), await vsel(i).locator(`option:has-text("${name}")`).first().getAttribute('value'), { pause: 300 });
await pick(0, 'Old Nord Narrator');
await say('Narrators & authors', 'Letters and journals written by an NPC are read in that NPC\'s own voice type. Nobody wants a stranger reading their diary.');
await d.hover('text=Lyra\'s Journal', 1200);
await pick(1, 'Sample Voice');
await d.hover('text=Letter to Lyra', 1000);
await pick(2, 'Gruff Captain');
await sleep(600);
await shot();

// ---------------------------------------------------------------- books
await d.click('button:has-text("Books (")', { pause: 900 });
await say('Books', 'Every book, and who reads it. Spell tomes without text, recipes and blank notes are left out on their own.');
await d.hover('.vrow:has-text("Spell Tome: Spark")', 1500);
await d.hover('.vrow:has-text("Letter to Lyra") >> text=MaleNord', 1500).catch(() => sleep(1500));
await d.click('.vrow:has-text("Roads of the Old North")', { pause: 600 });
await d.click('button:has-text("Open editor")', { pause: 1400 });
await say('The editor', 'What the game shows sits next to what the voice reads: markup removed, dates and numbers written out in words.');
await sleep(2600);
await d.hover('text=Seventeenth of Last Seed', 1800).catch(() => sleep(1800));
await shot();
await say('The editor', 'Long books are split into parts, each with its own takes. Effects and reverb are set per book.');
await d.hover('text=Part 1', 1500).catch(() => sleep(1500));
await d.hover('.bk-gen-row .switch, .bk-gen-row', 1800).catch(() => sleep(1800));
await d.click('#modal button:has-text("Close")', { pause: 700 }).catch(async () => { await page.keyboard.press('Escape'); await sleep(500); });

await say('Takes', 'Generate every book in one go. The reading flows smoothly across the joins between parts.');
await d.click('button:has-text("Generate options")', { pause: 700 });
await d.click('.gen-pop .gen-item:has-text("Missing")', { pause: 900 });
await d.hover('#modal .kv, #modal', 2000).catch(() => {});
await d.click('#modal .btn.primary', { pause: 200 });
await d.move({ x: 1000, y: 560 });
await jobDone();
await sleep(800);
await say('Takes', 'Green means ready. Listen to a whole book, or redo it with one click. Earlier takes are kept.');
await d.hover('.vrow:has-text("Roads of the Old North") button[title*="lay"] >> nth=0', 1600).catch(() => sleep(1600));
await d.hover('.vrow:has-text("Notice") button >> nth=0', 1400).catch(() => sleep(1400));
await shot();

// ---------------------------------------------------------------- export
await say('Final compilation', 'Export joins the parts, adds reverb and evens out loudness, then packs the archives, the ini file and an installer.');
await d.click('.export-btn', { pause: 700 });
await d.click('.menu >> text=Export everything', { pause: 300 });
console.log('after export click', await page.locator('.menu').count(), await page.locator('#modal:not(.hidden)').count(), await page.locator('.toast').allInnerTexts());
await sleep(2000);
if (!(await page.evaluate(() => !!document.querySelector('#modal:not(.hidden)') || !document.querySelector('#job')?.classList.contains('hidden')))) { console.log('FALLBACK'); await page.click('.export-btn'); await sleep(300); await page.click('.menu >> text=Export everything'); }
await until(() => document.querySelector('#job')?.classList.contains('hidden') && document.querySelector('#modal:not(.hidden)'));
await sleep(900);
await say('Final compilation', 'Out comes a BSA and dummy plugin per narrator and for NPC voices, the VBOS ini file, a validation report and a FOMOD.');
await d.hover('#modal >> text=VBOS Example Library - Sample Narrator.bsa', 2200).catch(() => sleep(2200));
await d.hover('#modal >> text=Example Library_VBOS.ini', 1600).catch(() => sleep(1600));
await shot();
await d.click('#modal .btn.primary', { pause: 500 });
await say('Voiced Books Tool', 'Install it next to Voiced Books of Skyrim, open a book in the game, and it reads itself to you.');
await sleep(2600);
await settle();
await d.uncaption();
await d.fade(true);
await sleep(300);
const n = await cap.stop();
console.log('frames', n);
await s.close();
process.exit(0);
