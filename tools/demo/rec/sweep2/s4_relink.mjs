import { start } from '../bridge.mjs';
const s = await start({ width: 1600, height: 950, scale: 1 }); const { page } = s;
await page.goto('https://app.dbvo/index.html'); await page.waitForSelector('.home-cards');
const rpc = (m, p) => page.evaluate(([m, p]) => import('/lib.js').then((l) => l.rpc(m, p)), [m, p]);
const st = await rpc('state'); const vp = st.voicePacks[0];
let proj = (await rpc('fomod.create', { voicePackId: vp.id, name: 'relink', rootFolder: 'D:\\fomodroot' })).project;
proj = (await rpc('fomod.addPatches', { project: proj, patchIds: [st.patches.find((p) => p.voicePackId === vp.id).id], sort: '' })).project.project;
const o = proj.steps.flatMap((s) => s.groups.flatMap((g) => g.options)).find((o) => o.patchId);
console.log('before', JSON.stringify(o.files.map((f) => [f.destination, f.source, !!f.linked])));
// user: remove linked Sound, add own Sound folder by hand
o.files = o.files.filter((f) => f.destination !== 'Sound'); o.removedLinked = ['Sound'];
o.files.push({ kind: 'folder', source: 'MySound', destination: 'Sound', priority: 0 });
await rpc('fomod.save', { project: proj });
const r = await rpc('fomod.refresh', { project: proj });
const o2 = r.project.project.steps.flatMap((s) => s.groups.flatMap((g) => g.options)).find((x) => x.id === o.id);
console.log('after refresh', JSON.stringify(o2.files.map((f) => [f.destination, f.source, !!f.linked])));
await s.close(); process.exit(0);
