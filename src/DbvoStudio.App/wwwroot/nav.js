// Back / Forward between pages (with their scroll positions), Undo of the last 10 edits, the Ctrl+K command palette,
// and the background job queue shown in the top bar.
import { rpc, on, h, clear, toast, fail, popupMenu } from './lib.js';
import { app, go, render, refreshState } from './app.js';

// ------------------------------------------------------------------ back / forward

const backStack = [];
const fwdStack = [];
const MAX = 50;
const main = () => document.getElementById('main');

/** Scroll positions inside the page (the page itself, lists, panels), to put back when coming back to it. */
function snapScroll() {
  const out = [];
  const root = main();
  if (!root) return out;
  const all = [root, ...root.querySelectorAll('*')];
  all.forEach((el, i) => { if (el.scrollTop > 0 || el.scrollLeft > 0) out.push({ i, cls: el.className && typeof el.className === 'string' ? el.className : '', top: el.scrollTop, left: el.scrollLeft }); });
  return out;
}
function restoreScroll(snap) {
  if (!snap || !snap.length) return;
  const apply = () => {
    const root = main();
    const all = [root, ...root.querySelectorAll('*')];
    for (const s of snap) {
      // the same element (same place, same classes), or else the first one with those classes
      let el = all[s.i];
      if (!el || (typeof el.className === 'string' ? el.className : '') !== s.cls) el = s.cls ? all.find((x) => x.className === s.cls) : null;
      if (el) { el.scrollTop = s.top; el.scrollLeft = s.left; }
    }
  };
  requestAnimationFrame(() => { apply(); setTimeout(apply, 150); setTimeout(apply, 500); });
}

const sameRoute = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** Called by go() before it moves to another page. */
export function beforeNavigate(next) {
  if (!app.route || sameRoute(app.route, next)) return;
  backStack.push({ route: { ...app.route }, scroll: snapScroll() });
  if (backStack.length > MAX) backStack.shift();
  fwdStack.length = 0;
  paintNav();
}

async function travel(from, to) {
  const e = from.pop();
  if (!e) return;
  to.push({ route: { ...app.route }, scroll: snapScroll() });
  paintNav();
  await go(e.route.view, (({ view, ...rest }) => rest)(e.route), { history: false });
  restoreScroll(e.scroll);
}
export const back = () => travel(backStack, fwdStack);
export const forward = () => travel(fwdStack, backStack);

function paintNav() {
  const b = document.getElementById('nav-back'), f = document.getElementById('nav-fwd');
  if (!b) return;
  b.disabled = !backStack.length; f.disabled = !fwdStack.length;
  b.title = backStack.length ? `Back to ${label(backStack[backStack.length - 1].route)} (Alt+←)` : 'Back (Alt+←)';
  f.title = fwdStack.length ? `Forward to ${label(fwdStack[fwdStack.length - 1].route)} (Alt+→)` : 'Forward (Alt+→)';
}
function label(r) {
  const s = app.state || {};
  const name = (list, id) => ((list || []).find((x) => x.id === id) || {}).name;
  switch (r.view) {
    case 'home': return 'the landing page';
    case 'mods': return 'Phase 1';
    case 'mod': return name(s.mods, r.id) || 'a mod';
    case 'patches': return 'Phase 2';
    case 'patch': return name(s.patches, r.id) || 'a patch';
    case 'fomods': return 'Phase 3';
    case 'fomod': return name(s.fomods, r.id) || 'a FOMOD';
    case 'settings': return 'Global Settings' + (r.tab ? ' · ' + r.tab : '');
    case 'voices': return 'voice pack ' + (name(s.voicePacks, r.id) || '');
    case 'help': return 'the guide';
    case 'wavlip': return 'WAV to LIP';
    case 'revoices': case 'revoice': return 'NPC Revoice';
    case 'books': case 'book': return 'Voiced Books';
    case 'grunts': case 'grunt': return 'Player Grunts';
    default: return r.view;
  }
}

// ------------------------------------------------------------------ undo

let undoCount = 0;
function paintUndo(lbl) {
  const b = document.getElementById('undo-btn');
  if (!b) return;
  b.disabled = !undoCount;
  b.title = undoCount ? `Undo${lbl ? ': ' + lbl : ''} (Ctrl+Z) · click for the last ${undoCount} edit(s)` : 'Nothing to undo yet (Ctrl+Z)';
}
async function undoTo(index) {
  try {
    const r = await rpc('undo.apply', { index });
    undoCount = r.left;
    paintUndo();
    toast(`Undone: ${r.undone.join(', ')}.`);
    await refreshState();
    await render();
  } catch (e) { fail(e); }
}
async function undoMenu(anchor) {
  let list = [];
  try { list = await rpc('undo.list'); } catch (e) { fail(e); return; }
  if (!list.length) { toast('Nothing to undo yet.'); return; }
  const ago = (t) => { const s = Math.round((Date.now() - new Date(t)) / 1000); return s < 60 ? `${s} s ago` : s < 3600 ? `${Math.round(s / 60)} min ago` : new Date(t).toLocaleTimeString(); };
  popupMenu(anchor, list.map((e, i) => ({ label: i === 0 ? `Undo: ${e.label}` : `Back to before: ${e.label}`, sub: `${ago(e.at)}${i ? ` · also takes back the ${i} newer edit(s)` : ''}`, onclick: () => undoTo(e.index) })));
}

// ------------------------------------------------------------------ command palette (Ctrl+K)

/** A page can add its own entries (e.g. the events of the open Player Grunts project). */
export function setPaletteExtra(fn) { app.paletteExtra = fn; }

async function paletteItems() {
  const s = app.state || (await refreshState());
  const items = [];
  const add = (kind, label, run, sub = '') => items.push({ kind, label, sub, run });
  add('Page', 'Landing page', () => go('home'));
  add('Page', 'Phase 1: Import Mods', () => go('mods'));
  add('Page', 'Phase 2: DBVO Patching', () => go('patches'));
  add('Page', 'Phase 3: FOMOD Creation', () => go('fomods'));
  add('Page', 'Guide', () => go('help'));
  for (const [tab, name] of [['general', 'General & tools'], ['voices', 'Voice packs'], ['pronunciation', 'Pronunciation'], ['references', 'Omit references'], ['categories', 'Categories'], ['credits', 'Credits']])
    add('Setting', 'Global Settings · ' + name, () => go('settings', { tab }));
  add('Tool', 'WAV to LIP Tool', () => go('wavlip'));
  add('Tool', 'NPC Revoice Tool', () => go('revoices'));
  add('Tool', 'Voiced Books Tool', () => go('books'));
  add('Tool', 'Player Grunts Tool', () => go('grunts'));
  for (const m of s.mods || []) add('Mod', m.name, () => go('mod', { id: m.id }), 'Phase 1');
  for (const p of s.patches || []) add('Patch', p.name, () => go('patch', { id: p.id }), 'Phase 2');
  for (const f of s.fomods || []) add('FOMOD', f.name, () => go('fomod', { id: f.id }), 'Phase 3');
  for (const v of s.voicePacks || []) add('Voice pack', v.name, () => go('voices', { id: v.id }), 'Global Settings');
  const [rv, bk, gr] = await Promise.all([rpc('revoice.list').catch(() => []), rpc('books.list').catch(() => []), rpc('grunts.list').catch(() => [])]);
  for (const x of rv || []) add('NPC Revoice', x.name, () => go('revoice', { id: x.id }));
  for (const x of bk || []) add('Voiced Books', x.name, () => go('book', { id: x.id }));
  for (const x of gr || []) add('Player Grunts', x.name, () => go('grunt', { id: x.id }));
  if (typeof app.paletteExtra === 'function') { try { for (const x of app.paletteExtra() || []) items.unshift(x); } catch { /* ignore */ } }
  return items;
}

let paletteOpen = false;
export async function openPalette() {
  if (paletteOpen) return;
  paletteOpen = true;
  const input = h('input.palette-input', { type: 'text', placeholder: 'Go to a mod, patch, project, event, setting or tool…', spellcheck: false });
  const list = h('div.palette-list');
  const box = h('div.palette', h('div.palette-card', input, list, h('div.palette-foot.faint', '↑ ↓ to choose · Enter to go · Esc to close')));
  document.body.append(box);
  input.focus();
  let items = [], shown = [], at = 0;
  const close = () => { paletteOpen = false; box.remove(); };
  const paint = () => {
    const q = input.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
    shown = items.filter((x) => { const hay = (x.label + ' ' + x.kind + ' ' + x.sub).toLowerCase(); return q.every((w) => hay.includes(w)); }).slice(0, 60);
    at = Math.min(at, Math.max(0, shown.length - 1));
    clear(list);
    if (!shown.length) list.append(h('div.dim', { style: { padding: '10px 12px' } }, items.length ? 'Nothing matches.' : 'Loading…'));
    shown.forEach((x, i) => {
      const row = h('div.palette-row' + (i === at ? '.on' : ''), { onmousedown: (e) => { e.preventDefault(); close(); x.run(); } },
        h('span.palette-kind', x.kind), h('span.grow.ellipsis', x.label), x.sub ? h('span.faint.ellipsis', x.sub) : null);
      list.append(row);
    });
    const on = list.querySelector('.palette-row.on');
    if (on) on.scrollIntoView({ block: 'nearest' });
  };
  input.addEventListener('input', () => { at = 0; paint(); });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { e.preventDefault(); close(); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); at = Math.min(shown.length - 1, at + 1); paint(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); at = Math.max(0, at - 1); paint(); }
    else if (e.key === 'Enter') { e.preventDefault(); const x = shown[at]; if (x) { close(); x.run(); } }
  });
  box.addEventListener('mousedown', (e) => { if (e.target === box) close(); });
  paint();
  try { items = await paletteItems(); } catch (e) { fail(e); }
  if (paletteOpen) paint();
}

// ------------------------------------------------------------------ job queue

let queue = [];
export function paintQueue() {
  const job = document.getElementById('job');
  let q = document.getElementById('job-queue');
  if (!q) {
    q = h('button.btn.sm.ghost#job-queue', { title: 'Tasks waiting to start after this one' });
    q.addEventListener('click', () => popupMenu(q, queue.map((x, i) => ({ label: `${i + 1}. ${x.label}`, sub: 'Waiting · click to take it out of the queue', onclick: () => rpc('queue.remove', { queueId: x.id }).catch(fail) }))));
    job.insertBefore(q, document.getElementById('job-cancel'));
  }
  q.textContent = `+${queue.length} queued`;
  q.classList.toggle('hidden', !queue.length);
}

// ------------------------------------------------------------------ start

export function initNav() {
  const brand = document.getElementById('brand');
  const arrows = h('div.nav-arrows',
    h('button.nav-btn#nav-back', { title: 'Back (Alt+←)', disabled: true, onclick: back }, '‹'),
    h('button.nav-btn#nav-fwd', { title: 'Forward (Alt+→)', disabled: true, onclick: forward }, '›'));
  brand.after(arrows);
  const tools = document.getElementById('tools-btn');
  const undoBtn = h('button.top-btn.icon-top#undo-btn', { title: 'Nothing to undo yet (Ctrl+Z)', disabled: true, onclick: (e) => undoMenu(e.currentTarget) }, '↶');
  const palBtn = h('button.top-btn.icon-top#palette-btn', { title: 'Go to… a mod, patch, project, event or setting (Ctrl+K)', onclick: openPalette }, '⌕');
  tools.before(undoBtn, palBtn);
  on('undo', (d) => { undoCount = d.count; paintUndo(d.label); });
  on('queue', (d) => { queue = d.items || []; paintQueue(); });
  rpc('undo.list').then((l) => { undoCount = l.length; paintUndo(l[0] && l[0].label); }).catch(() => {});
  const editable = (t) => t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));
  window.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'k') { e.preventDefault(); openPalette(); return; }
    if (e.altKey && e.key === 'ArrowLeft') { e.preventDefault(); back(); return; }
    if (e.altKey && e.key === 'ArrowRight') { e.preventDefault(); forward(); return; }
    // Ctrl+Z in a text field undoes typing there; elsewhere it undoes the last edit.
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 'z' && !editable(e.target) && !document.querySelector('#modal:not(.hidden)')) {
      if (undoCount) { e.preventDefault(); undoTo(0); }
    }
  });
  // The mouse's own back and forward buttons.
  window.addEventListener('mouseup', (e) => { if (e.button === 3) { e.preventDefault(); back(); } else if (e.button === 4) { e.preventDefault(); forward(); } });
}
