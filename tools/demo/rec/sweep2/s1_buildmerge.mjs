import { start } from '../bridge.mjs';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const s = await start({ width: 1600, height: 950, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
await page.evaluate(() => { window.__jd = []; document.addEventListener('job-done', (e) => window.__jd.push(e.detail)); });
const st = await rpc('state', {});
const vp = st.voicePacks[0];
const c = await rpc('fomod.create', { voicePackId: vp.id, name: 'merge ' + Date.now(), rootFolder: 'D:\\fomodroot' });
let proj = c.project; console.log('root', proj.rootFolder, 'rel', proj.releaseFolder);
const patches = st.patches.filter((p) => p.voicePackId === vp.id).map((p) => p.id);
const r = await rpc('fomod.addPatches', { project: proj, patchIds: patches.slice(0, 2), sort: 'az' });
proj = r.project.project;
const opts = proj.steps.flatMap((s) => s.groups.flatMap((g) => g.options));
const lo = opts.find((o) => o.patchId && o.files.length >= 1);
console.log('linked option', lo.name, lo.files.map((f) => f.destination + (f.linked ? '*' : '')));
lo.files.push({ kind: 'file', source: 'hand.txt', destination: 'HandAdded.txt', priority: 0 });
lo.files.push({ kind: 'file', source: 'hand2.txt', destination: 'Hand2.txt', priority: 0 });
await rpc('fomod.save', { project: proj });
// start build with this copy
try { await rpc('fomod.build', { project: proj, package: false }); } catch (e) { console.log('build start err', e.message); }
// edit during build: remove HandAdded.txt, rename Hand2 dest, remove linked Sound with removedLinked
const ed = JSON.parse(JSON.stringify(proj));
const eo = ed.steps.flatMap((s) => s.groups.flatMap((g) => g.options)).find((o) => o.id === lo.id);
eo.files = eo.files.filter((f) => f.destination !== 'HandAdded.txt' && f.destination !== 'Sound');
eo.files.find((f) => f.destination === 'Hand2.txt').destination = 'Hand2-renamed.txt';
eo.removedLinked = ['Sound'];
await rpc('fomod.save', { project: ed });
for (let i = 0; i < 100; i++) { const n = await page.evaluate(() => window.__jd.length); if (n) break; await sleep(200); }
const jd = await page.evaluate(() => window.__jd);
console.log('jobDone', jd.map((d) => [d.kind, d.ok, d.message]));
const after = (await rpc('fomod.get', { id: proj.id })).project;
const ao = after.steps.flatMap((s) => s.groups.flatMap((g) => g.options)).find((o) => o.id === lo.id);
console.log('disk after build:', ao.files.map((f) => f.destination + (f.linked ? '*' : '')), 'removedLinked', ao.removedLinked);
await s.close(); process.exit(0);
