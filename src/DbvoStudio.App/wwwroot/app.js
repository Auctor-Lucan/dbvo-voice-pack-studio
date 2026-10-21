import { applyTheme } from './themes.js';
import { rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, debounce, popupMenu } from './lib.js';
import { modsView, modView } from './phase1.js';
import { patchesView, patchView } from './phase2.js';
import { fomodsView, fomodView } from './phase3.js';
import { wavLipView } from './tools.js';
import { revoiceListView, revoiceView } from './revoice.js';
import { settingsView } from './settings.js';

export const app = {
  state: null,
  route: store.get('route', { view: 'mods' }),
  job: null,
};

const main = document.getElementById('main');

export async function refreshState() {
  app.state = await rpc('state');
  { const th = (app.state.settings.ui || {}).theme; if (th) applyTheme(th); }
  // A job indicator left over from a job that ended without telling the page (e.g. after a crash) must not block buttons.
  if (!app.state.jobRunning && app.job) { app.job = null; document.getElementById('job').classList.add('hidden'); }
  return app.state;
}

export function go(view, params = {}) {
  app.route = { view, ...params };
  store.set('route', app.route);
  render();
}

const SECTION = { mods: 'mods', mod: 'mods', patches: 'patches', patch: 'patches', fomods: 'fomods', fomod: 'fomods', settings: 'settings', voices: 'settings', wavlip: 'tools', vc: 'patches', revoices: 'tools', revoice: 'tools' };

export async function render() {
  const section = SECTION[app.route.view] || 'mods';
  document.querySelectorAll('[data-section]').forEach((b) => b.classList.toggle('active', b.dataset.section === section));
  // An Other Tools page shows its name in the top bar, after the three phases.
  const tool = { wavlip: ['WAV to LIP', 'wavlip'], revoices: ['NPC Revoice', 'revoices'], revoice: ['NPC Revoice', 'revoices'] }[app.route.view];
  const tc = document.getElementById('tool-crumb');
  tc.classList.toggle('hidden', !tool);
  if (tool) { tc.textContent = tool[0]; tc.onclick = () => go(tool[1]); }
  clear(main);
  main.append(h('div.empty', 'Loading…'));
  try {
    if (!app.state) await refreshState();
    let view;
    switch (app.route.view) {
      case 'mod': view = await modView(app.route.id); break;
      case 'patches': view = await patchesView('tts'); break;
      case 'vc': view = await patchesView(); break; // the Voice Changer is now a toggle inside each patch
      case 'patch': view = await patchView(app.route.id); break;
      case 'fomods': view = await fomodsView(); break;
      case 'fomod': view = await fomodView(app.route.id); break;
      case 'wavlip': view = await wavLipView(); break;
      case 'revoices': view = await revoiceListView(); break;
      case 'revoice': view = await revoiceView(app.route.id); break;
      case 'voices': view = await settingsView('voices', app.route.id); break;
      case 'settings': view = await settingsView(app.route.tab, app.route.id); break;
      default: view = await modsView();
    }
    clear(main).append(view);
  } catch (e) {
    console.error(e);
    clear(main).append(h('div.empty', h('h2', 'Something went wrong'), h('div', e.message || String(e)),
      h('div', { style: { marginTop: '12px' } }, h('button.btn', { onclick: () => go('mods') }, 'Back to Mods'))));
    if (app.route.view !== 'mods') store.set('route', { view: 'mods' });
  }
}

document.querySelectorAll('.crumb[data-section]').forEach((b) => b.addEventListener('click', () => go(b.dataset.section)));
document.querySelector('[data-section="settings"]').addEventListener('click', () => go('settings'));
const toolsBtn = document.getElementById('tools-btn');
toolsBtn.addEventListener('click', () => popupMenu(toolsBtn, [
  { label: 'WAV to LIP', sub: 'Whisper listens to WAV files and makes matching .lip files', onclick: () => go('wavlip') },
  { label: 'NPC Revoice', sub: 'Replace NPC voice files in any mod', onclick: () => go('revoices') },
]));

// ---- background jobs (generation, .fuz, FOMOD, WAV to LIP)
const jobEl = document.getElementById('job');
const LABEL = { fuz: 'Building .fuz', fomod: 'Building FOMOD', wavlip: 'WAV to LIP', generate: 'Generating', import: 'Importing .fuz' };
on('progress', (p) => {
  app.job = p;
  jobEl.classList.remove('hidden');
  jobEl.querySelector('.job-label').textContent = `${(p.kind === 'fomod' || p.kind === 'revoice') && p.message ? p.message : LABEL[p.kind] || 'Working'}${p.kind === 'revoice' ? '…' : ` ${p.done}/${p.total}`}${p.failed ? ` · ${p.failed} failed` : ''}`;
  jobEl.title = p.current || '';
  jobEl.querySelector('.bar-fill').style.width = (p.total ? (100 * p.done) / p.total : 0) + '%';
  document.dispatchEvent(new CustomEvent('job-progress', { detail: p }));
});
on('jobDone', async (d) => {
  app.job = null;
  jobEl.classList.add('hidden');
  document.dispatchEvent(new CustomEvent('job-done', { detail: d }));
  if (d.kind === 'fuz' && d.report) {
    const r = d.report;
    const body = [h('div.kv', h('div', 'Written'), h('div', r.written), h('div', 'Skipped (already there)'), h('div', r.skipped), h('div', 'Failed'), h('div', r.failed))];
    if (r.errors && r.errors.length) body.push(h('div.box.mono', { style: { maxHeight: '160px', overflow: 'auto' } }, r.errors.slice(0, 50).join('\n')));
    if (d.files && d.files.length) body.push(h('div', h('div.field-label', 'DBVO Fix plugins and topic files written'), h('div.box.mono', { style: { whiteSpace: 'pre-wrap', maxHeight: '140px', overflow: 'auto' } }, d.files.join('\n'))));
    if (d.reminders && d.reminders.length) body.push(h('div.banner.warn', { style: { margin: 0 } }, h('div', h('b', 'Before you release: '), h('ul', d.reminders.map((x) => h('li', x))))));
    await modal('.fuz files built', body);
  } else if (d.kind === 'import') {
    toast(d.message || 'Done.', !d.ok);
  } else if (d.kind === 'wavlip') {
    if (d.message) toast(d.message, !d.ok);
  } else if (d.kind === 'fomod') {
    if (!d.ok) { toast(d.message || 'FOMOD build failed.', true); return; }
    const r = d.report;
    const v = await modal('FOMOD built', [
      h('div.kv', h('div', 'Files staged'), h('div', r.files), h('div', 'Staging folder'), h('div.mono', r.staging), h('div', 'Archive'), h('div.mono', r.archive || '— (staged only)')),
      r.warnings && r.warnings.length ? h('div.banner.warn', { style: { margin: 0 } }, h('ul', r.warnings.slice(0, 20).map((x) => h('li', x)))) : null,
    ], [{ label: 'Open folder', value: 'open' }, { label: 'OK', primary: true, value: true }]);
    if (v === 'open') rpc('open.path', { path: r.archive || r.staging });
  } else if (!d.ok && d.message) {
    toast(d.message, true);
  } else if (d.kind === 'generate') {
    toast('Generation finished.');
  }
});
document.getElementById('job-cancel').addEventListener('click', () => rpc('job.cancel').catch(fail));

window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => e.preventDefault());

render();

export { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, debounce };
