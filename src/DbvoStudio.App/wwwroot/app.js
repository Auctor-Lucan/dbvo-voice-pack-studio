import { rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, debounce } from './lib.js';
import { modsView, modView } from './phase1.js';
import { patchesView, patchView } from './phase2.js';
import { voicesView, settingsView } from './settings.js';

export const app = {
  state: null,
  route: store.get('route', { view: 'mods' }),
  job: null,
};

const main = document.getElementById('main');

export async function refreshState() {
  app.state = await rpc('state');
  return app.state;
}

export function go(view, params = {}) {
  app.route = { view, ...params };
  store.set('route', app.route);
  render();
}

export async function render() {
  document.querySelectorAll('.nav-item').forEach((b) => {
    const v = app.route.view;
    const section = v === 'mod' ? 'mods' : v === 'patch' ? 'patches' : v;
    b.classList.toggle('active', b.dataset.view === section);
  });
  clear(main);
  const loading = h('div.empty', 'Loading…');
  main.append(loading);
  try {
    if (!app.state) await refreshState();
    let view;
    switch (app.route.view) {
      case 'mod': view = await modView(app.route.id); break;
      case 'patches': view = await patchesView(); break;
      case 'patch': view = await patchView(app.route.id); break;
      case 'voices': view = await voicesView(app.route.id); break;
      case 'settings': view = await settingsView(); break;
      default: view = await modsView();
    }
    clear(main).append(view);
  } catch (e) {
    clear(main).append(h('div.empty', h('h2', 'Something went wrong'), h('div', e.message || String(e)),
      h('div', { style: { marginTop: '12px' } }, h('button.btn', { onclick: () => go('mods') }, 'Back to Mods'))));
    if (app.route.view !== 'mods') store.set('route', { view: 'mods' });
  }
}

document.querySelectorAll('.nav-item').forEach((b) => b.addEventListener('click', () => go(b.dataset.view)));

// ---- background jobs (generation, .fuz)
const jobEl = document.getElementById('job');
on('progress', (p) => {
  app.job = p;
  jobEl.classList.remove('hidden');
  const label = p.kind === 'fuz' ? 'Building .fuz' : 'Generating';
  jobEl.querySelector('.job-label').textContent = `${label} ${p.done}/${p.total}${p.failed ? ` · ${p.failed} failed` : ''}`;
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
    if (d.reminders && d.reminders.length) body.push(h('div.banner.warn', { style: { margin: 0 } }, h('div', h('b', 'Before you release: '), h('ul', d.reminders.map((x) => h('li', x))))));
    await modal('.fuz files built', body);
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
