import { applyTheme } from './themes.js';
import { rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, debounce, popupMenu, pathText, pathRows, pathList, closeAllModals, modalsMayClose, hasGuardedModal } from './lib.js';
import { modsView, modView } from './phase1.js';
import { patchesView, patchView } from './phase2.js';
import { fomodsView, fomodView } from './phase3.js';
import { wavLipView } from './tools.js';
import { revoiceListView, revoiceView } from './revoice.js';
import { settingsView } from './settings.js';
import { booksListView, bookProjectView } from './books.js';
import { gruntsListView, gruntProjectView } from './grunts.js';
import { homeView, noteRecent } from './home.js';
import { helpView, PAGE_HELP } from './help.js';
import { initNav, beforeNavigate } from './nav.js';

export const app = {
  state: null,
  route: store.get('route', { view: 'home' }),
  job: null,
};

const main = document.getElementById('main');
Object.defineProperty(window, '__dbvoState', { get: () => app.state }); // lib.js helpers read the current state

/** The user's background image behind every page (Global Settings → Appearance), or none. */
export function applyBackground(ui) {
  const root = document.documentElement;
  root.classList.toggle('has-bg', !!ui.homeBg);
  root.classList.remove('shade-light', 'shade-normal', 'shade-dark');
  if (ui.homeBg) {
    root.classList.add('shade-' + (ui.homeBgShade || 'normal'));
    root.style.setProperty('--app-bg', `url("https://user.dbvo/${encodeURIComponent(ui.homeBg)}?v=${ui.homeBgV || ''}")`);
  } else root.style.removeProperty('--app-bg');
  root.style.setProperty('--read-zoom', String(READ_ZOOM[ui.readSize] || READ_ZOOM.larger));
}
/** Reading text size for the landing page, the guide, settings pages and dialogs (not the workspaces). */
export const READ_ZOOM = { normal: 1, larger: 1.15, largest: 1.3 };

export async function refreshState() {
  app.state = await rpc('state');
  { const th = (app.state.settings.ui || {}).theme; if (th) applyTheme(th); }
  applyBackground(app.state.settings.ui || {});
  // A job indicator left over from a job that ended without telling the page (e.g. after a crash) must not block buttons.
  if (!app.state.jobRunning && app.job) { app.job = null; document.getElementById('job').classList.add('hidden'); }
  return app.state;
}

// A host error nobody caught (an action's button would otherwise seem to do nothing) is shown, not swallowed.
window.addEventListener('unhandledrejection', (e) => {
  const r = e.reason;
  if (r && r.fromHost) { e.preventDefault(); fail(r); }
});

let navigated = false; // set once the user moves, so the start-up below never overrides a click made while it loads
export function go(view, params = {}, { history = true } = {}) {
  // An open dialog with unsaved edits (Edit take) asks first, whatever started the move (Back button, F1, Ctrl+K…)
  if (hasGuardedModal()) return modalsMayClose().then((ok) => (ok ? goNow(view, params, history) : undefined));
  return goNow(view, params, history);
}
function goNow(view, params, history) {
  navigated = true;
  // Dialogs and the book editor belong to the page being left.
  closeAllModals();
  document.querySelectorAll('.book-editor-overlay').forEach((o) => o.dispatchEvent(new CustomEvent('leave')));
  const next = { view, ...params };
  if (history) beforeNavigate(next);
  app.route = next;
  store.set('route', app.route);
  noteRecent(app.route);
  return render();
}

/** A page that changes what it shows without go() (a settings tab): Back returns to the previous one. */
export function noteNav(route) {
  beforeNavigate(route);
  app.route = route;
  store.set('route', route);
}

const SECTION = { home: 'home', help: 'help', mods: 'mods', mod: 'mods', patches: 'patches', patch: 'patches', fomods: 'fomods', fomod: 'fomods', settings: 'settings', voices: 'settings', wavlip: 'tools', vc: 'patches', revoices: 'tools', revoice: 'tools', books: 'tools', book: 'tools', grunts: 'tools', grunt: 'tools' };

let renderTicket = 0;
export async function render() {
  const section = SECTION[app.route.view] || 'mods';
  document.querySelectorAll('[data-section]').forEach((b) => b.classList.toggle('active', b.dataset.section === section));
  document.getElementById('brand').classList.toggle('active', section === 'home');
  // An Other Tools page shows its name in the top bar, after the three phases.
  const tool = { wavlip: ['WAV to LIP Tool', 'wavlip'], revoices: ['NPC Revoice Tool', 'revoices'], revoice: ['NPC Revoice Tool', 'revoices'], books: ['Voiced Books Tool', 'books'], book: ['Voiced Books Tool', 'books'], grunts: ['Player Grunts Tool', 'grunts'], grunt: ['Player Grunts Tool', 'grunts'] }[app.route.view];
  const tc = document.getElementById('tool-crumb');
  tc.classList.toggle('hidden', !tool);
  if (tool) { tc.textContent = tool[0]; tc.onclick = () => go(tool[1]); }
  // Keep the current page until the next one is ready, so switching pages never flashes an empty (or unshaded) frame.
  // "Loading…" only shows when a page takes noticeably long.
  const ticket = ++renderTicket;
  app.paletteExtra = null; // the next page sets its own Ctrl+K entries
  const slow = setTimeout(() => { if (ticket === renderTicket) clear(main).append(h('div.empty.view', 'Loading…')); }, 400);
  try {
    if (!app.state) await refreshState();
    let view;
    switch (app.route.view) {
      case 'home': view = await homeView(); break;
      case 'help': view = await helpView(app.route.anchor); break;
      case 'mod': view = await modView(app.route.id); break;
      case 'patches': view = await patchesView('tts'); break;
      case 'vc': view = await patchesView(); break; // the Voice Changer is now a toggle inside each patch
      case 'patch': view = await patchView(app.route.id); break;
      case 'fomods': view = await fomodsView(); break;
      case 'fomod': view = await fomodView(app.route.id); break;
      case 'wavlip': view = await wavLipView(); break;
      case 'revoices': view = await revoiceListView(); break;
      case 'revoice': view = await revoiceView(app.route.id); break;
      case 'books': view = await booksListView(); break;
      case 'book': view = await bookProjectView(app.route.id); break;
      case 'grunts': view = await gruntsListView(); break;
      case 'grunt': view = await gruntProjectView(app.route.id); break;
      case 'voices': view = await settingsView('voices', app.route.id); break;
      case 'settings': view = await settingsView(app.route.tab, app.route.id); break;
      default: view = await modsView();
    }
    clearTimeout(slow);
    if (ticket !== renderTicket) return; // a newer click already moved on
    clear(main).append(view);
  } catch (e) {
    clearTimeout(slow);
    if (ticket !== renderTicket) return;
    console.error(e);
    clear(main).append(h('div.empty', h('h2', 'Something went wrong'), h('div', e.message || String(e)),
      h('div', { style: { marginTop: '12px' } }, h('button.btn', { title: 'Go to the landing page', onclick: () => go('home') }, 'Back to the landing page'))));
    if (app.route.view !== 'home') store.set('route', { view: 'home' });
  }
}

// "Open Global Settings" on a problem message: go there and scroll to the section it names.
document.addEventListener('go-settings', (e) => {
  const { tab, section } = e.detail || {};
  go('settings', { tab: tab || 'general' });
  if (!section) return;
  let tries = 0;
  const find = () => {
    const hd = [...main.querySelectorAll('.section-head h2')].find((x) => x.textContent.trim().toLowerCase().startsWith(section.toLowerCase()));
    if (hd) { hd.scrollIntoView({ block: 'start' }); hd.closest('.section')?.classList.add('flash-hl'); setTimeout(() => hd.closest('.section')?.classList.remove('flash-hl'), 1600); }
    else if (++tries < 20) setTimeout(find, 100);
  };
  setTimeout(find, 50);
});
document.querySelectorAll('.crumb[data-section]').forEach((b) => b.addEventListener('click', () => go(b.dataset.section)));
document.querySelector('[data-section="settings"]').addEventListener('click', () => go('settings'));
document.querySelector('[data-section="help"]').addEventListener('click', () => go('help', { anchor: PAGE_HELP[app.route.view] }));
document.getElementById('brand').addEventListener('click', () => go('home'));
document.getElementById('brand').addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go('home'); } });
// F1: the guide, at the part about the page you are on.
window.addEventListener('keydown', (e) => { if (e.key === 'F1') { e.preventDefault(); if (app.route.view !== 'help') go('help', { anchor: PAGE_HELP[app.route.view] }); } });
const toolsBtn = document.getElementById('tools-btn');
toolsBtn.addEventListener('click', () => popupMenu(toolsBtn, [
  { label: 'WAV to LIP Tool', sub: 'Whisper listens to WAV files and makes matching .lip files', onclick: () => go('wavlip') },
  { label: 'NPC Revoice Tool', sub: 'Replace NPC voice files in any mod', onclick: () => go('revoices') },
  { label: 'Voiced Books Tool', sub: 'Narrate books, letters and journals for Voiced Books of Skyrim', onclick: () => go('books') },
  { label: 'Player Grunts Tool', title: 'Import, edit or create player voice packs for PC Head Tracking (and Multiple Voice Packs) and Smart Player Voice System (SPVS)',
    sub: h('div', 'Import, edit or create voice packs for:', h('ul.menu-list', h('li', 'PC Head Tracking (and Multiple Voice Packs)'), h('li', 'Smart Player Voice System (SPVS)'))), onclick: () => go('grunts') },
]));

// ---- background jobs (generation, .fuz, FOMOD, WAV to LIP)
const jobEl = document.getElementById('job');
const LABEL = { fuz: 'Building .fuz', fomod: 'Building FOMOD', wavlip: 'WAV to LIP', generate: 'Generating', import: 'Importing', qa: 'Checking takes', 'revoice-qa': 'Checking takes', 'grunts-export': 'Exporting voice pack', 'grunts-measure': 'Measuring loudness', 'grunts-whisper': 'Whisper subtitles' };
// Progress reports can arrive a moment after the job has finished (they travel separately); one of those must not
// bring back the job bar and keep buttons disabled.
let lastDone = null;
on('progress', (p) => {
  if (lastDone && lastDone.kind === p.kind && Date.now() - lastDone.t < 1500) return;
  app.job = p;
  jobEl.classList.remove('hidden');
  const qa = p.kind === 'qa' || p.kind === 'revoice-qa';
  // Check takes: the count is always shown once checking starts; where Whisper runs and the speed go in the tooltip.
  const label = qa
    ? (p.done === 0 && p.message ? `${p.message} · 0/${p.total}` : `Checking takes ${p.done.toLocaleString()}/${p.total.toLocaleString()}${p.rate ? ` · ${p.rate}/s` : ''}`)
    : `${(p.kind === 'fomod' || p.kind === 'revoice') && p.message ? p.message : LABEL[p.kind] || 'Working'}${p.kind === 'revoice' ? '…' : ` ${p.done}/${p.total}`}`;
  jobEl.querySelector('.job-label').textContent = label + (p.failed ? ` · ${p.failed} failed` : '');
  jobEl.title = [qa && p.device ? p.device + (p.rate ? ` · ${p.rate} takes per second` : '') : null, p.current || null].filter(Boolean).join('\n');
  jobEl.querySelector('.bar-fill').style.width = (p.total ? (100 * p.done) / p.total : 0) + '%';
  document.dispatchEvent(new CustomEvent('job-progress', { detail: p }));
});
on('contextBilled', (d) => {
  toast(`ElevenLabs may have started billing the context: "${d.line}" cost ${d.billed} credits, while its words alone come to about ${d.expected}. Tags as context may no longer save credits — compare the billed numbers in the takes window.`, true);
});

on('jobDone', async (d) => {
  // A queued task that was removed or couldn't start: pages stop waiting for it. A removal leaves the running task alone.
  if (d.kind === 'queue' && d.queueId) {
    document.dispatchEvent(new CustomEvent('queue-dropped', { detail: d }));
    if (d.removed) return;
  }
  lastDone = { kind: d.kind, t: Date.now() };
  app.job = null;
  jobEl.classList.add('hidden');
  document.dispatchEvent(new CustomEvent('job-done', { detail: d }));
  if (d.kind === 'fuz' && d.report) {
    const r = d.report;
    const body = [h('div.kv', h('div', 'Written'), h('div', r.written),
      r.reused ? [h('div', 'Imported .fuz kept as they were'), h('div', r.reused)] : null,
      r.cached ? [h('div', 'From the .fuz cache (unchanged since an earlier build)'), h('div', r.cached)] : null,
      h('div', 'Skipped (already there)'), h('div', r.skipped), h('div', 'Failed'), h('div', r.failed))];
    if (r.errors && r.errors.length) body.push(h('div.box.mono', { style: { maxHeight: '160px', overflow: 'auto' } }, r.errors.slice(0, 50).join('\n')));
    if (d.files && d.files.length) body.push(h('div', h('div.field-label', 'DBVO Fix plugins and topic files written'), pathList(d.files, { maxHeight: '160px' })));
    if (d.reminders && d.reminders.length) body.push(h('div.banner.warn', { style: { margin: 0 } }, h('div', h('b', 'Before you release: '), h('ul', d.reminders.map((x) => h('li', x))))));
    await modal('.fuz files built', body);
  } else if (d.kind === 'import' || d.kind === 'qa' || d.kind === 'revoice-qa') {
    toast(d.message || 'Done.', !d.ok || (d.kind === 'qa' && d.flagged > 0));
  } else if (d.kind === 'wavlip') {
    if (d.message) toast(d.message, !d.ok);
  } else if (d.kind === 'fomod') {
    if (!d.ok) { toast(d.message || 'FOMOD build failed.', true); return; }
    const r = d.report;
    const v = await modal('FOMOD built', [
      h('div.kv', h('div', 'Files staged'), h('div', r.files), r.bsas ? [h('div', 'BSA archives packed'), h('div', r.bsas)] : null, h('div', 'Staging folder'), h('div', pathText(r.staging)), h('div', 'Archive'), h('div', r.archive && pathText(r.archive) || '— (staged only)')),
      r.warnings && r.warnings.length ? h('div.banner.warn', { style: { margin: 0 } }, h('ul', r.warnings.slice(0, 20).map((x) => h('li', x)))) : null,
    ], [{ label: 'Open folder', value: 'open' }, { label: 'OK', primary: true, value: true }]);
    if (v === 'open') rpc('open.path', { path: r.archive || r.staging });
  } else if (d.kind === 'grunts-measure' || d.kind === 'grunts-whisper') {
    if (d.message) toast(d.message, !d.ok);
  } else if (!d.ok && d.message) {
    toast(d.message, true);
  } else if (d.kind === 'generate') {
    toast('Generation finished.');
  }
});
document.getElementById('job-cancel').addEventListener('click', () => rpc('job.cancel').catch(fail));
initNav();

window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => e.preventDefault());

// Start on the landing page, or where you left off (Global Settings → General & tools → Start page).
(async () => {
  clear(main).append(h('div.empty.view', 'Loading…'));
  let home = true;
  try { await refreshState(); home = ((app.state.settings.ui || {}).startPage || 'home') !== 'last'; } catch { /* render shows the error */ }
  if (navigated) { rpc('window.ready').catch(() => {}); return; }
  if (home) app.route = { view: 'home' };
  await render();
  // The first screen is drawn: the splash can go.
  rpc('window.ready').catch(() => { /* not in the app */ });
})();

export { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, debounce };

// Text cut short with "…" shows in full when hovered.
document.addEventListener('mouseover', (e) => {
  const el = e.target.closest && e.target.closest('.stat, .ellipsis');
  if (el && !el.hasAttribute('title') && el.scrollWidth > el.clientWidth + 1) el.title = el.textContent.trim();
});
