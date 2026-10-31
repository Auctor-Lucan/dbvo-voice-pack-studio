// Bridge to the C# host, small DOM helpers, and a virtual grid for thousands of rows.

// Let append()/prepend() take arrays and skip null/false/undefined, so views can pass conditional pieces.
for (const name of ['append', 'prepend']) {
  for (const proto of [Element.prototype, DocumentFragment.prototype]) {
    const native = proto[name];
    proto[name] = function (...nodes) {
      return native.apply(this, nodes.flat(Infinity).filter((n) => n != null && n !== false && n !== true));
    };
  }
}

const pending = new Map();
const listeners = new Map();
let nextId = 1;
const webview = window.chrome && window.chrome.webview;

if (webview) {
  webview.addEventListener('message', (e) => {
    const msg = e.data;
    if (msg && msg.event) {
      (listeners.get(msg.event) || []).slice().forEach((fn) => fn(msg.data));
      return;
    }
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if ('error' in msg && msg.error) { const err = new Error(msg.error); err.fromHost = true; err.method = p.method; p.reject(err); }
    else p.resolve(msg.result);
  });
}

/** Call a host method. */
export function rpc(method, params = {}) {
  if (!webview) return Promise.reject(new Error('Not running inside DBVO Studio.'));
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject, method });
    webview.postMessage({ id, method, params });
  });
}

/** Send dropped File objects to the host; resolves to their real paths. */
export function droppedPaths(fileList) {
  if (!webview || !webview.postMessageWithAdditionalObjects) return Promise.resolve([]);
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject, method: 'dropped' });
    webview.postMessageWithAdditionalObjects({ id, method: 'dropped', params: {} }, fileList);
  });
}

export function on(event, fn) {
  if (!listeners.has(event)) listeners.set(event, []);
  listeners.get(event).push(fn);
  return () => { const l = listeners.get(event); const i = l.indexOf(fn); if (i >= 0) l.splice(i, 1); };
}

/** h('div.cls#id', {attrs/on*}, ...children) */
export function h(tag, attrs, ...children) {
  const m = /^([a-z0-9]+)?((?:[.#][\w-]+)*)$/i.exec(tag) || [];
  const el = document.createElement(m[1] || 'div');
  (m[2] || '').replace(/([.#])([\w-]+)/g, (_, t, v) => {
    if (t === '.') el.classList.add(v);
    else el.id = v;
  });
  // a number (also 0) or string as the second argument is a child, not attributes
  if (typeof attrs === 'number' || (attrs && (typeof attrs !== 'object' || attrs instanceof Node || Array.isArray(attrs)))) {
    children.unshift(attrs);
    attrs = null;
  }
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'title' && v === '') continue; // an empty title would hide the hover text of the element around it
    if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === 'class') el.className += ' ' + v;
    else if (k === 'style' && typeof v === 'object') { for (const [sk, sv] of Object.entries(v)) { if (sk.startsWith('--')) el.style.setProperty(sk, sv); else el.style[sk] = sv; } }
    else if (k === 'value') el.value = v;
    else if (k === 'checked') el.checked = !!v;
    else if (k === 'html') el.innerHTML = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat(Infinity)) {
    if (c == null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

export function clear(el) {
  // Blur first: a focused input inside would fire change/blur while being removed, and a handler that
  // re-renders this element then would break the removal.
  const a = document.activeElement;
  if (a && a !== document.body && el.contains(a)) a.blur();
  el.replaceChildren();
  return el;
}

let toastTimer;
/** A short message at the bottom. Problems (bad = true) stay longer, wrap in full, can be clicked away,
 *  and offer a way to the settings page they mention. */
export function toast(text, bad = false) {
  const t = document.getElementById('toast');
  text = String(text ?? '');
  const parts = [h('span.toast-text', text)];
  if (bad) {
    const m = /Global Settings(?: → ([^→.,;]+?)(?: → ([^.,;]+?))?)?(?=[.,;]|$| first| to | and )/.exec(text);
    if (m) parts.push(h('button.btn.sm.toast-go', { title: 'Open the settings this message mentions', onclick: (e) => { e.stopPropagation(); t.classList.add('hidden'); document.dispatchEvent(new CustomEvent('go-settings', { detail: { tab: /voice pack/i.test(m[1] || '') ? 'voices' : 'general', section: (m[2] || m[1] || '').trim() } })); } }, 'Open Global Settings'));
    parts.push(h('button.toast-x', { title: 'Close', onclick: (e) => { e.stopPropagation(); t.classList.add('hidden'); } }, '✕'));
  }
  t.replaceChildren(...parts);
  t.classList.toggle('bad', bad);
  t.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add('hidden'), bad ? Math.min(20000, 7000 + text.length * 40) : 3200);
}

export function fail(e) {
  console.error(e);
  toast(e && e.message ? e.message : String(e), true);
}

/** Show a modal. content: Node[]; buttons: [{label, primary, value}]. Resolves to the clicked value (null on Esc). */
/** A small ? that opens the user guide at a section (help.js listens), e.g. helpButton('the-toolbar'). */
export function helpButton(anchor, { label = '' } = {}) {
  return h('button.help-btn', { type: 'button', title: 'Help: open the guide for this' + (label ? ` (${label})` : ''), 'data-help': anchor,
    onclick: (e) => { e.preventDefault(); e.stopPropagation(); document.dispatchEvent(new CustomEvent('open-help', { detail: { anchor, toggle: true } })); } }, icon('help', { size: 15 }));
}

// Dialogs stack: one opened from inside another (e.g. "+ New category…" in New mod) goes on top, and the one below comes
// back (with its scroll position) when it closes. Escape closes the top one. Closing a dialog closes the ones on it first.
const dialogs = [];
let modalKeysOn = false;
function scrollState(root) {
  const out = [];
  const walk = (el) => { if (el.scrollTop || el.scrollLeft) out.push([el, el.scrollTop, el.scrollLeft]); for (const c of el.children) walk(c); };
  walk(root);
  return out;
}
export function modal(title, content, buttons = [{ label: 'OK', primary: true, value: true }], { wide = false, help = null, onOpen = null, cls = '', autofocus = true, guard = null } = {}) {
  const m = document.getElementById('modal');
  const card = m.querySelector('.modal-card');
  if (!modalKeysOn) {
    modalKeysOn = true;
    card.tabIndex = -1;
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && dialogs.length && !m.classList.contains('hidden')) dialogs[dialogs.length - 1].ask(null); });
  }
  // A dialog hidden by hand earlier: forget it (its caller gets null).
  if (m.classList.contains('hidden') && dialogs.length) for (const d of dialogs.splice(0)) d.resolve(null);
  // The same dialog twice within a moment (a double-click on the button that opens it): the second one is a no.
  if (dialogs.length && dialogs[dialogs.length - 1].title === title && Date.now() - dialogs[dialogs.length - 1].at < 1500) return Promise.resolve(null);
  document.querySelectorAll('.menu').forEach((x) => x.remove());
  closePopover();
  if (dialogs.length) {
    const top = dialogs[dialogs.length - 1];
    top.scroll = [[card, card.scrollTop, card.scrollLeft], ...scrollState(card)];
    top.nodes = [...card.childNodes];
    if (top.cls) card.classList.remove(top.cls);
  }
  clear(card);
  card.classList.toggle('wide', wide);
  if (cls) card.classList.add(cls);
  return new Promise((resolve) => {
    const me = { title, wide, cls, resolve, nodes: null, scroll: null, close: null, at: Date.now(), guard };
    // Escape and the dialog's own buttons ask first when the dialog has a guard (e.g. unsaved edits); closing by code doesn't.
    me.ask = async (v) => { if (guard && !(await guard(v))) return; me.close(v); };
    me.close = (v) => {
      const i = dialogs.indexOf(me);
      if (i < 0) return;
      while (dialogs.length - 1 > i) dialogs[dialogs.length - 1].close(null);
      dialogs.pop();
      if (cls) card.classList.remove(cls);
      const below = dialogs[dialogs.length - 1];
      if (below) {
        clear(card).append(...below.nodes);
        card.classList.toggle('wide', below.wide);
        if (below.cls) card.classList.add(below.cls);
        for (const [el, t, l] of below.scroll || []) { el.scrollTop = t; el.scrollLeft = l; }
        below.nodes = null;
      } else m.classList.add('hidden');
      resolve(v);
    };
    dialogs.push(me);
    card.append(
      help ? h('div.modal-head', h('h2', title), helpButton(help)) : h('h2', title),
      ...[content].flat(),
      h('div.row', h('div.spacer'), ...buttons.map((b) =>
        h('button.btn' + (b.primary ? '.primary' : '') + (b.danger ? '.danger' : ''), { onclick: () => me.ask(typeof b.value === 'function' ? b.value() : b.value) }, b.label))),
    );
    m.classList.remove('hidden');
    // Focus moves into the dialog, so keys (Space, Enter, arrows) no longer reach the button or grid behind it.
    const first = autofocus && card.querySelector('input,textarea,select');
    if (first) first.focus(); else card.focus({ preventScroll: true });
    if (onOpen) onOpen(me.close);
  });
}

/** Close every open dialog (their callers get null): before going to another page. */
export function closeAllModals() { while (dialogs.length) dialogs[dialogs.length - 1].close(null); }

/**
 * Before leaving the page: every open dialog with a guard (unsaved edits, e.g. Edit take) is asked, top first.
 * False when one says to stay; the dialogs are then left open as they were.
 */
export async function modalsMayClose() {
  for (const d of [...dialogs].reverse()) if (d.guard && !(await d.guard(null))) return false;
  return true;
}
export const hasGuardedModal = () => dialogs.some((d) => d.guard);

/** Close the dialog that holds el (and any dialog on top of it). */
export function closeDialogOf(el, value = null) {
  const card = document.querySelector('#modal .modal-card');
  const d = dialogs.find((x, i) => (i === dialogs.length - 1 ? card.contains(el) : (x.nodes || []).some((n) => n.contains(el))));
  if (d) d.close(value);
}

/** True while el is in the dialog on top (not covered by another one). */
export function inTopDialog(el) {
  const m = document.getElementById('modal');
  return !m.classList.contains('hidden') && m.querySelector('.modal-card').contains(el);
}

export async function confirmBox(title, text, okLabel = 'OK', danger = false) {
  return modal(title, [h('div', { style: { whiteSpace: 'pre-line' } }, text)], [
    { label: 'Cancel', value: false },
    { label: okLabel, primary: !danger, danger, value: true },
  ]);
}

export async function prompt(title, fields) {
  const inputs = fields.map((f) => {
    const i = f.type === 'textarea'
      ? h('textarea', { rows: f.rows || 5, placeholder: f.placeholder || '' }, f.value || '')
      : h('input', { type: f.type || 'text', value: f.value || '', placeholder: f.placeholder || '' });
    return { f, i };
  });
  const body = inputs.map(({ f, i }) => h('div.col', h('div.field-label', f.label), i, f.help ? h('div.faint', f.help) : null));
  const ok = await modal(title, body, [{ label: 'Cancel', value: false }, { label: 'OK', primary: true, value: true }]);
  if (!ok) return null;
  const out = {};
  inputs.forEach(({ f, i }) => { out[f.name] = i.value; });
  return out;
}

// ------------------------------------------------------------------ dragging things around inside the app
// Pointer-based, not HTML drag and drop: WebView2 shows no drag image for that, so a drag was invisible. Here a label
// follows the pointer, the pointer turns into a grabbing hand, the place it will land lights up and says what happens,
// and the target shows "Moving…" until the work is done. File drops from Explorer still use HTML drag and drop.
const dropTargets = new WeakMap();
let dragNow = null;

/** Lets el be a drop target: accepts(data) → a short text of what a drop does (or null/false: not here); onDrop(data) may be async. */
export function dragTarget(el, { accepts, onDrop }) {
  el.dataset.dragTarget = '1';
  dropTargets.set(el, { accepts, onDrop });
}

/** Makes el draggable: data() gives what is dragged, label() the text that follows the pointer. */
export function dragSource(el, { data, label }) {
  el.addEventListener('mousedown', (e) => {
    if (e.button !== 0 || e.target.closest('input,textarea,select,button,a')) return;
    const x0 = e.clientX, y0 = e.clientY;
    let started = false, over = null, overEl = null, ghost = null, note = null;
    const find = (x, y) => {
      let t = document.elementFromPoint(x, y);
      while (t && t !== document.body) {
        if (t.dataset && t.dataset.dragTarget && dropTargets.has(t) && t !== el) {
          const what = dropTargets.get(t).accepts(dragNow);
          if (what) return [t, what];
        }
        t = t.parentElement;
      }
      return [null, null];
    };
    const move = (ev) => {
      if (!started) {
        if (Math.abs(ev.clientX - x0) + Math.abs(ev.clientY - y0) < 6) return;
        started = true;
        dragNow = data();
        note = h('div.drag-note');
        ghost = h('div.drag-ghost', h('div.drag-what', label()), note);
        document.body.append(ghost);
        document.body.classList.add('drag-active');
        el.classList.add('dragging');
      }
      ev.preventDefault();
      ghost.style.transform = `translate(${ev.clientX + 14}px, ${ev.clientY + 12}px)`;
      const [t, what] = find(ev.clientX, ev.clientY);
      if (t !== overEl) { if (overEl) overEl.classList.remove('drop-over'); overEl = t; if (t) t.classList.add('drop-over'); }
      over = t ? dropTargets.get(t) : null;
      note.textContent = t ? what : 'Not here';
      ghost.classList.toggle('no', !t);
    };
    const up = async () => {
      document.removeEventListener('mousemove', move, true);
      document.removeEventListener('mouseup', up, true);
      if (!started) return;
      // the click that follows a drag must not open the row
      const eat = (ce) => { ce.stopPropagation(); ce.preventDefault(); };
      document.addEventListener('click', eat, { capture: true, once: true });
      setTimeout(() => document.removeEventListener('click', eat, true), 50);
      ghost.remove();
      document.body.classList.remove('drag-active');
      el.classList.remove('dragging');
      const d = dragNow; dragNow = null;
      if (!overEl || !over) return;
      const t = overEl;
      t.classList.remove('drop-over');
      t.classList.add('drop-busy');
      document.body.classList.add('drag-busy');
      try { await over.onDrop(d); } catch (err) { fail(err); } finally { t.classList.remove('drop-busy'); document.body.classList.remove('drag-busy'); }
    };
    document.addEventListener('mousemove', move, true);
    document.addEventListener('mouseup', up, true);
  });
}

/** Finished projects in a list page, as small chips after the cards of those with work left. */
export function pdoneChips(list, open, num, why) {
  if (!list.length) return null;
  return h('div.pchips', { style: { padding: '0 12px 12px' } }, list.map((x) => h('div.pchip', { title: `${x.name} · ${why}. Click to open.`, onclick: () => open(x) },
    h('span.pchip-name', x.name), h('span.mono.faint', num(x)))));
}

/** Wraps a search box with a magnifying glass that lights up while a search is active (lines are hidden), click to clear. */
export function searchLamp(q) {
  if (q.__lamp) return q.__lamp;
  const g = h('button.search-lamp-ico', { type: 'button' }, icon('search', { size: 15 }));
  const w = h('span.search-lamp', g, q);
  const paint = () => {
    const v = q.value.trim();
    w.classList.toggle('on', v.length > 0);
    g.title = v ? `Search is on: "${v}". Only matching rows are shown. Click to clear it.` : 'Search';
  };
  q.addEventListener('input', paint);
  g.addEventListener('click', () => { if (q.value) { q.value = ''; q.dispatchEvent(new Event('input')); } q.focus(); });
  paint();
  q.__lamp = w;
  return w;
}

export function debounce(fn, ms = 400) {
  let t;
  return (...a) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...a), ms);
  };
}

export const store = {
  get(k, d) {
    try { const v = localStorage.getItem('dbvo:' + k); return v == null ? d : JSON.parse(v); } catch { return d; }
  },
  set(k, v) {
    try { localStorage.setItem('dbvo:' + k, JSON.stringify(v)); } catch { /* ignore */ }
  },
};

/**
 * Virtual grid: renders only visible rows, so 10,000+ rows scroll smoothly.
 * columns: [{ id, title, width, min, flex, render(row, index) -> Node|string, cls, resizable }]
 * Column widths can be dragged; they are remembered under `storageKey`.
 */
// ---- clicks and re-rendering
// A row or panel drawn again between mousedown and mouseup swallows the click (the button pressed is gone), which is
// why a click right after typing sometimes needed a second one. Re-renders asked for while a button is down wait
// until the click has been handled.
let _pointerDown = false;
const _afterPointer = new Map();
let _gridUid = 0;
function _flushPointer() {
  _pointerDown = false;
  const fns = [..._afterPointer.values()];
  _afterPointer.clear();
  for (const fn of fns) { try { fn(); } catch (e) { console.error(e); } }
}
document.addEventListener('pointerdown', (e) => { if (e.button === 0) _pointerDown = true; }, true);
// click comes straight after pointerup in the same task, so a timeout runs after it.
document.addEventListener('pointerup', () => { if (_pointerDown) setTimeout(_flushPointer, 0); }, true);
document.addEventListener('pointercancel', () => setTimeout(_flushPointer, 0), true);
window.addEventListener('blur', () => { if (_pointerDown) _flushPointer(); });
/** A take's library path for a media URL: each name escaped, so # and % in Legacy names don't break it. */
export const mediaPath = (p) => String(p || '').split('/').map(encodeURIComponent).join('/');
export function pointerIsDown() { return _pointerDown; }
/** Run fn now, or once the mouse button is released and the click handled (the latest fn per key wins). */
export function afterPointer(key, fn) {
  if (!_pointerDown) { fn(); return; }
  _afterPointer.set(key, fn);
}
/**
 * The spinning ↻ on lines a take is being made for. Every request spins its lines at once; the server's job (started
 * or queued) keeps them spinning until each take arrives or that job ends. Jobs end in the order they were queued,
 * so a quick second click never stops (or leaves spinning) the lines of another job.
 */
export function spinTracker(refresh) {
  const set = new Set();
  const finishing = new Set(); // their job ended; the take may still be on its way (it still flashes)
  const jobs = [];
  const busy = (x) => jobs.some((j) => j.has(x));
  function release(ids, flash) {
    const gone = [];
    for (const x of [...ids]) {
      if (busy(x)) continue;
      if (set.delete(x)) { if (flash) { finishing.add(x); gone.push(x); } refresh(x); }
    }
    if (gone.length) setTimeout(() => { for (const x of gone) finishing.delete(x); }, 5000);
  }
  return {
    has: (x) => set.has(x),
    /** A take arrived: true when the line was spinning (or just finished), so its row flashes. */
    arrived(x) {
      const j = jobs.find((k) => k.has(x));
      if (j) j.delete(x);
      const was = set.has(x) || finishing.has(x);
      finishing.delete(x);
      if (!busy(x)) set.delete(x);
      return was;
    },
    /** Spin these lines now; call accept() once the server took the job, or drop() if it didn't. */
    request(ids) {
      ids = [...new Set(ids || [])];
      for (const x of ids) { set.add(x); refresh(x); }
      let open = true;
      return {
        add(more) { for (const x of more || []) if (!ids.includes(x)) { ids.push(x); set.add(x); refresh(x); } },
        accept() { if (!open) return; open = false; jobs.push(new Set(ids)); for (const x of ids) if (!set.has(x)) { set.add(x); refresh(x); } },
        drop() { if (!open) return; open = false; release(ids, false); },
      };
    },
    /** A job ended: mine = it was one of these generations. The lines still spinning for it stop. */
    jobDone(mine) {
      if (mine) { const j = jobs.shift(); if (j) release(j, true); }
      if (!jobs.length) release([...set], true);
    },
    clear() { jobs.length = 0; release([...set], false); },
  };
}

/** Swap every cell of a row for the fresh row's, except the cell holding the focused input; take the row's classes. */
function patchRowAround(old, fresh) {
  const active = document.activeElement;
  const a = [...old.children], b = [...fresh.children];
  for (let j = 0; j < b.length; j++) {
    if (a[j] && a[j].contains(active)) continue;
    if (a[j]) a[j].replaceWith(b[j]); else old.append(b[j]);
  }
  for (let j = b.length; j < a.length; j++) if (!a[j].contains(active)) a[j].remove();
  old.className = fresh.className;
  old.style.top = fresh.style.top;
  old.dataset.index = fresh.dataset.index;
}

export class VGrid {
  constructor(container, { columns, rowHeight = 34, rowKey, rowClass, onRowClick, onRowDblClick, storageKey }) {
    this._uid = ++_gridUid;
    this.columns = columns.map((c, i) => ({ id: c.id || String(i), min: 40, resizable: true, ...c, _orig: { width: c.width, flex: c.flex } }));
    this.storageKey = storageKey;
    const saved = storageKey ? store.get('cols:' + storageKey, {}) : {};
    for (const c of this.columns) if (saved[c.id]) { c.width = saved[c.id]; c.flex = 0; }
    this.defaultOrder = this.columns.map((c) => c.id);
    if (storageKey) this.applyOrder(store.get('order:' + storageKey, null));
    this.hidden = new Set(storageKey ? store.get('hide:' + storageKey, []) : []);
    // Click a column title to sort: ascending, descending, then back to the usual order.
    const ss = storageKey ? store.get('sort:' + storageKey, null) : null;
    this.sortId = ss && this.columns.some((c) => c.id === ss.id) ? ss.id : null;
    this.sortDir = ss ? ss.dir : 0;
    this.rawRows = [];
    this.rowHeight = rowHeight;
    this.rowKey = rowKey;
    this.rowClass = rowClass;
    this.onRowClick = onRowClick;
    this.onRowDblClick = onRowDblClick;
    this.rows = [];
    this.focusKey = null;
    this.selected = new Set();
    this.el = h('div.vgrid', { tabindex: 0 });
    this.head = h('div.vgrid-head');
    this.body = h('div.vgrid-body');
    this.el.append(this.head, this.body);
    container.append(this.el);
    this.renderHead();
    this.el.addEventListener('scroll', () => this.paint());
    new ResizeObserver(() => this.paint()).observe(this.el);
    this.el.addEventListener('keydown', (e) => this.key(e));
  }

  /** Columns in a saved order (dragged by their titles); columns new since then go after the one they follow by default. */
  applyOrder(ids) {
    if (!Array.isArray(ids) || !ids.length) return;
    const byId = new Map(this.columns.map((c) => [c.id, c]));
    const out = ids.filter((x) => byId.has(x)).map((x) => byId.get(x));
    this.defaultOrder.forEach((cid, i) => {
      if (out.some((c) => c.id === cid)) return;
      const prev = i > 0 ? out.findIndex((c) => c.id === this.defaultOrder[i - 1]) : -1;
      out.splice(prev + 1, 0, byId.get(cid));
    });
    this.columns = out;
  }

  /** Drag a column title sideways to move the column (the tick box column stays first). */
  startMove(e, c, cell) {
    if (e.button !== 0 || c.id === 'sel' || e.target.closest('.resizer')) return;
    const x0 = e.clientX;
    let ghost = null, mark = null, to = null;
    const cells = () => [...this.head.children];
    const move = (ev) => {
      if (!ghost) {
        if (Math.abs(ev.clientX - x0) < 6) return;
        ghost = h('div.col-ghost', c.title || c.tip || 'Column');
        mark = h('div.col-drop');
        document.body.append(ghost, mark);
        cell.classList.add('moving');
        document.body.classList.add('col-dragging');
      }
      ghost.style.left = ev.clientX + 12 + 'px'; ghost.style.top = ev.clientY + 10 + 'px';
      const shown = this.shown();
      let best = null;
      cells().forEach((el, i) => {
        if (shown[i].id === 'sel') return;
        const r = el.getBoundingClientRect();
        const before = ev.clientX < r.left + r.width / 2;
        const x = before ? r.left : r.right;
        if (!best || Math.abs(ev.clientX - x) < Math.abs(ev.clientX - best.x)) best = { x, id: shown[i].id, before, top: r.top, h: r.height };
      });
      to = best;
      if (best) { mark.style.left = best.x - 1 + 'px'; mark.style.top = best.top + 'px'; mark.style.height = this.el.getBoundingClientRect().height + 'px'; }
    };
    const up = () => {
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      if (!ghost) return;
      ghost.remove(); mark.remove(); cell.classList.remove('moving');
      document.body.classList.remove('col-dragging');
      this._moved = Date.now();
      if (!to || to.id === c.id) return;
      const cols = this.columns.filter((x) => x !== c);
      let at = cols.findIndex((x) => x.id === to.id) + (to.before ? 0 : 1);
      if (cols[0] && cols[0].id === 'sel') at = Math.max(1, at);
      cols.splice(at, 0, c);
      this.columns = cols;
      if (this.storageKey) store.set('order:' + this.storageKey, cols.map((x) => x.id));
      this.renderHead();
      this.paint(true);
    };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
  }

  sizeCell(cell, c) {
    if (c.flex) { cell.style.flex = `${c.flex} 1 ${c.width || 120}px`; cell.style.minWidth = (c.min || 80) + 'px'; }
    else { cell.style.flex = '0 0 auto'; cell.style.width = (c.width || 100) + 'px'; }
  }

  /** Columns currently shown (the Columns menu can hide any column with a title). */
  shown() { return this.columns.filter((c) => !this.hidden.has(c.id)); }

  setHidden(id, hide) {
    if (hide) this.hidden.add(id); else this.hidden.delete(id);
    if (this.storageKey) store.set('hide:' + this.storageKey, [...this.hidden]);
    this.renderHead();
    this.paint(true);
  }

  /** Every row gets the same width (the columns' total, or the view if wider), so long text can't push columns out of line. */
  setRowWidth() {
    const total = this.shown().reduce((a, c) => a + (c.flex ? (c.width || 120) : (c.width || 100)), 0);
    this.el.style.setProperty('--roww', total + 'px');
  }

  renderHead() {
    this.setRowWidth();
    clear(this.head);
    for (const c of this.shown()) {
      const sortable = c.title && c.sortable !== false;
      const on = sortable && this.sortId === c.id && this.sortDir;
      const cell = h('div.cell' + (c.align === 'center' ? '.center' : '') + (sortable ? '.sortable' : '') + (on ? '.sorted' : ''),
        { title: (c.tip ? c.tip + '\n' : '') + (sortable ? 'Click to sort (again: reverse, a third time: the usual order)' : '') }, c.title || '', on ? h('span.sort-ind', this.sortDir > 0 ? ' ▲' : ' ▼') : null);
      if (sortable) cell.addEventListener('click', (e) => { if (e.target.closest('.resizer') || Date.now() - (this._moved || 0) < 300) return; this.toggleSort(c.id); });
      if (c.id !== 'sel') { cell.classList.add('movable'); cell.addEventListener('pointerdown', (e) => this.startMove(e, c, cell)); if (!c.title && c.tip) cell.title = c.tip + '\nDrag sideways to move this column'; else cell.title = (cell.title ? cell.title + '\n' : '') + 'Drag sideways to move this column'; }
      this.sizeCell(cell, c);
      if (c.resizable && c.id !== 'sel') {
        const grip = h('div.resizer', { title: 'Drag to resize. Double-click: put every column back (widths and order).' });
        grip.addEventListener('mousedown', (e) => this.startResize(e, c, cell, grip));
        grip.addEventListener('dblclick', () => this.resetColumns());
        cell.append(grip);
      }
      this.head.append(cell);
    }
  }

  startResize(e, c, cell, grip) {
    e.preventDefault();
    e.stopPropagation();
    const startX = e.clientX;
    const startW = cell.getBoundingClientRect().width;
    grip.classList.add('drag');
    const move = (ev) => {
      c.width = Math.max(c.min || 40, Math.round(startW + ev.clientX - startX));
      c.flex = 0;
      this.sizeCell(cell, c);
      this.body.querySelectorAll('.vrow').forEach((r) => this.sizeCell(r.children[this.shown().indexOf(c)], c));
      this.setRowWidth();
    };
    const up = () => {
      grip.classList.remove('drag');
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
      if (this.storageKey) {
        const saved = store.get('cols:' + this.storageKey, {});
        saved[c.id] = c.width;
        store.set('cols:' + this.storageKey, saved);
      }
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  }

  resetColumns() {
    if (this.storageKey) { store.set('cols:' + this.storageKey, {}); store.set('order:' + this.storageKey, null); }
    for (const c of this.columns) Object.assign(c, c._orig);
    const pos = new Map(this.defaultOrder.map((x, i) => [x, i]));
    this.columns.sort((a, b) => pos.get(a.id) - pos.get(b.id));
    this.renderHead();
    this.paint(true);
  }

  toggleSort(id) {
    if (this.sortId !== id) { this.sortId = id; this.sortDir = 1; }
    else if (this.sortDir === 1) this.sortDir = -1;
    else { this.sortId = null; this.sortDir = 0; }
    if (this.storageKey) store.set('sort:' + this.storageKey, this.sortId ? { id: this.sortId, dir: this.sortDir } : null);
    this.renderHead();
    // Rows replaced in place (grid.rows[i] = …) since are picked up by their key.
    const cur = new Map(this.rows.map((r) => [String(this.rowKey(r)), r]));
    this.setRows(this.rawRows.map((r) => cur.get(String(this.rowKey(r))) || r));
  }

  /** The value a column sorts by: its sort(row) when given, else the text it shows (text boxes count by their value). */
  sortKey(c, r) {
    let v;
    if (c.sort) v = c.sort(r);
    else {
      const n = c.render(r, 0);
      if (n instanceof Node) {
        const f = n.matches && n.matches('input,textarea,select') ? n : n.querySelector && n.querySelector('input:not([type=checkbox]),textarea,select');
        v = f ? f.value : n.textContent;
      } else v = n;
    }
    if (typeof v === 'string') { const t = v.trim(); if (/^-?[\d,]+(\.\d+)?$/.test(t)) return Number(t.replace(/,/g, '')); return t.toLowerCase(); }
    return v == null ? '' : v;
  }

  setRows(rows) {
    this.rawRows = rows;
    const c = this.sortId && this.sortDir ? this.columns.find((x) => x.id === this.sortId) : null;
    if (c) {
      const keyed = rows.map((r, i) => ({ r, i, k: this.sortKey(c, r) }));
      const coll = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });
      keyed.sort((a, b) => {
        const x = a.k, y = b.k;
        const d = typeof x === 'number' && typeof y === 'number' ? x - y : coll.compare(String(x), String(y));
        return d ? d * this.sortDir : a.i - b.i;
      });
      rows = keyed.map((x) => x.r);
    }
    this.rows = rows;
    const keys = new Set(rows.map((r) => String(this.rowKey(r))));
    for (const k of [...this.selected]) if (!keys.has(k)) this.selected.delete(k);
    this.body.style.height = rows.length * this.rowHeight + 'px';
    this.paint(true);
  }

  rowEl(key) {
    return this.body.querySelector(`[data-key="${CSS.escape(String(key))}"]`);
  }

  /**
   * Re-render one row. While a mouse button is down the row waits until the click is done, so a click is never
   * lost to a row drawn again under it. While the user types in the row, every cell but the one being typed in
   * is drawn again (icons such as the spinning ↻ stay current, and no keystrokes are lost).
   */
  refreshRow(key, force = false) {
    if (pointerIsDown()) { afterPointer('row:' + this._uid + ':' + key, () => this.refreshRow(key, force)); return; }
    const el = this.rowEl(key);
    if (!el) return;
    const i = Number(el.dataset.index);
    const fresh = this.renderRow(this.rows[i], i);
    if (!force && el.contains(document.activeElement) && document.activeElement.matches('input,textarea,select')) patchRowAround(el, fresh);
    else el.replaceWith(fresh);
  }

  paint(force = false) {
    if (force && pointerIsDown()) { afterPointer('paint:' + this._uid, () => this.paint(true)); return; }
    const top = this.el.scrollTop;
    const height = this.el.clientHeight || 600;
    const first = Math.max(0, Math.floor(top / this.rowHeight) - 10);
    const last = Math.min(this.rows.length, Math.ceil((top + height) / this.rowHeight) + 10);
    if (!force && this._first === first && this._last === last) return;
    const active = document.activeElement;
    const activeRow = active && active.closest && active.closest('.vrow');
    this._first = first;
    this._last = last;
    const frag = document.createDocumentFragment();
    let keep = null;
    for (let i = first; i < last; i++) {
      const r = this.rows[i];
      if (activeRow && activeRow.dataset.key === String(this.rowKey(r)) && active.matches('input,textarea,select')) {
        patchRowAround(activeRow, this.renderRow(r, i));
        keep = activeRow;
        frag.append(activeRow);
        continue;
      }
      frag.append(this.renderRow(r, i));
    }
    clear(this.body).append(frag);
    // Give the focus back without scrolling, or the list jumps back to that row while the user scrolls.
    if (keep && active && document.activeElement !== active) active.focus({ preventScroll: true });
  }

  renderRow(r, i) {
    const key = String(this.rowKey(r));
    const row = h('div.vrow', { 'data-key': key, 'data-index': i });
    row.style.top = i * this.rowHeight + 'px';
    if (this.selected.has(key)) row.classList.add('sel');
    if (this.focusKey === key) row.classList.add('focus');
    // A row that just finished keeps its flash when it is drawn again meanwhile.
    { const f = this._flash && this._flash.get(String(key)); if (f && f.until > Date.now()) row.classList.add(f.cls); }
    const extra = this.rowClass && this.rowClass(r);
    if (extra) row.classList.add(...extra.split(' ').filter(Boolean));
    for (const c of this.shown()) {
      const cell = h('div.cell' + (c.cls ? '.' + c.cls.split(' ').join('.') : '') + (c.align === 'center' ? '.center' : ''));
      this.sizeCell(cell, c);
      const content = c.render(r, i);
      if (content instanceof Node) cell.append(content);
      else if (content != null) cell.textContent = content;
      row.append(cell);
    }
    row.addEventListener('mousedown', (e) => {
      const onControl = e.target.closest('input,button,select,textarea,a');
      if (!onControl) {
        if (e.shiftKey && this.focusKey != null) this.selectRange(this.focusKey, key);
        else if (e.ctrlKey) this.toggle(key);
      }
      this.focus(key);
      if (this.onRowClick) this.onRowClick(r, e);
    });
    if (this.onRowDblClick) row.addEventListener('dblclick', (e) => { if (!e.target.closest('input,button,select')) this.onRowDblClick(r); });
    return row;
  }

  /** Move the focus highlight without re-rendering rows (keeps any input the user clicked into). */
  focus(key, scroll = false) {
    key = String(key);
    if (this.focusKey === key && !scroll) return;
    const prev = this.focusKey != null && this.rowEl(this.focusKey);
    if (prev) prev.classList.remove('focus');
    this.focusKey = key;
    const el = this.rowEl(key);
    if (el) el.classList.add('focus');
    if (scroll) {
      const i = this.rows.findIndex((r) => String(this.rowKey(r)) === key);
      if (i >= 0) {
        const y = i * this.rowHeight;
        if (y < this.el.scrollTop + 30 || y > this.el.scrollTop + this.el.clientHeight - this.rowHeight * 2)
          this.el.scrollTop = y - this.el.clientHeight / 2;
      }
    }
  }

  setSelected(key, on) {
    key = String(key);
    if (on) this.selected.add(key); else this.selected.delete(key);
    const el = this.rowEl(key);
    if (el) {
      el.classList.toggle('sel', on);
      const cb = el.querySelector('input[type=checkbox].rowcheck');
      if (cb) cb.checked = on;
    }
    this.el.dispatchEvent(new CustomEvent('selection'));
  }

  toggle(key) { this.setSelected(key, !this.selected.has(String(key))); }

  selectRange(a, b) {
    const keys = this.rows.map((r) => String(this.rowKey(r)));
    let i = keys.indexOf(a), j = keys.indexOf(b);
    if (i < 0 || j < 0) return;
    if (i > j) [i, j] = [j, i];
    for (let k = i; k <= j; k++) this.selected.add(keys[k]);
    this.paint(true);
    this.el.dispatchEvent(new CustomEvent('selection'));
  }

  selectAll(on) {
    this.selected = on ? new Set(this.rows.map((r) => String(this.rowKey(r)))) : new Set();
    this.paint(true);
    this.el.dispatchEvent(new CustomEvent('selection'));
  }

  /** A checkbox cell bound to the selection. */
  checkbox(r) {
    const key = String(this.rowKey(r));
    const cb = h('input.rowcheck', { type: 'checkbox', checked: this.selected.has(key) });
    cb.addEventListener('change', () => this.setSelected(key, cb.checked));
    return cb;
  }

  key(e) {
    if (e.target.closest('input,textarea,select')) return;
    const keys = this.rows.map((r) => String(this.rowKey(r)));
    let i = keys.indexOf(this.focusKey);
    if (e.key === 'ArrowDown') i = Math.min(keys.length - 1, i + 1);
    else if (e.key === 'ArrowUp') i = Math.max(0, i - 1);
    else if (e.key === ' ' && this.focusKey != null) { e.preventDefault(); this.toggle(this.focusKey); return; }
    else if ((e.key === 'a' || e.key === 'A') && e.ctrlKey) { e.preventDefault(); this.selectAll(true); return; } // also with Caps Lock on
    else return;
    e.preventDefault();
    if (keys[i] != null) {
      this.focus(keys[i], true);
      if (this.onRowClick) this.onRowClick(this.rows[i], e);
    }
  }
}

/** Toggle switch. */
export function switchEl(checked, onchange, label, { disabled = false, title = '' } = {}) {
  const input = h('input', { type: 'checkbox', checked, disabled });
  input.addEventListener('change', () => onchange(input.checked));
  return h('label.switch' + (disabled ? '.disabled' : ''), { title }, input, h('span.track'), label ? h('span', label) : null);
}

/** Segmented control: options [[value, label]]. */
export function segmented(options, value, onchange, { disabled = false } = {}) {
  const el = h('div.seg');
  for (const [v, label, tip] of options) {
    const b = h('button' + (String(v) === String(value) ? '.on' : ''), { disabled, title: tip || '', onclick: () => {
      // show the choice right away, also where the caller doesn't redraw
      el.querySelectorAll('button').forEach((x) => x.classList.toggle('on', x === b));
      onchange(v);
    } }, label);
    el.append(b);
  }
  return el;
}

/**
 * Copy a saved object's values into the object the page is editing, keeping every nested object the same
 * (controls hold references to them, so replacing them would make later edits go nowhere).
 */
export function mergeInto(target, src) {
  for (const [k, v] of Object.entries(src || {})) {
    const t = target[k];
    if (v && typeof v === 'object' && !Array.isArray(v) && t && typeof t === 'object' && !Array.isArray(t)) mergeInto(t, v);
    else target[k] = v;
  }
  return target;
}

/** A file path in path boxes: the folders dimmed, the last part bright, line breaks only after a backslash or a space. */
export function pathText(p) {
  const s = String(p ?? '');
  const trail = /[\\/]$/.test(s) ? s.slice(-1) : '';
  const body = trail ? s.slice(0, -1) : s;
  const i = Math.max(body.lastIndexOf('\\'), body.lastIndexOf('/'));
  const head = i >= 0 ? body.slice(0, i + 1) : '';
  const last = i >= 0 ? body.slice(i + 1) : body;
  return h('span.path', { title: s }, head ? h('span.path-head', head.split(/(?<=[\\/])/).map((x) => [x, h('wbr')])) : null, h('b.path-last', last), trail);
}
/** Labelled paths in a box: [[label, path], …]; an empty path shows as "(not set)". */
export function pathRows(rows, attrs = {}) {
  return h('div.path-box.path-rows', attrs, rows.filter(Boolean).map(([k, v]) => [h('div.path-key', k), h('div.path-val', v == null || v === '' ? h('span.faint', '(not set)') : pathText(v))]));
}
/** A list of paths in a box (one per row), scrolling past maxHeight; "more" adds a last faint row. */
export function pathList(paths, { maxHeight = '180px', more = 0, suffix = null } = {}) {
  return h('div.path-box', { style: { maxHeight, overflow: 'auto' } }, paths.map((p, i) => h('div', pathText(p), suffix ? suffix(p, i) : null)), more > 0 ? h('div.faint', `… ${more} more`) : null);
}

/**
 * Ask before replacing DBVO Fix plugins that are already in a folder. Resolves to null (cancel),
 * false (keep the existing ones) or true (overwrite). With no conflicts it resolves to true at once.
 */
export async function askFixOverwrite(paths) {
  if (!paths || !paths.length) return true;
  const v = await modal('Overwrite DBVO Fix plugins?', [
    h('div', `${paths.length} DBVO Fix plugin(s) with the same name are already in the destination:`),
    pathList(paths),
    h('div.faint', 'Keep existing writes everything else and leaves these files as they are.'),
  ], [{ label: 'Cancel', value: null }, { label: 'Keep existing', value: 'keep' }, { label: 'Overwrite', primary: true, value: 'overwrite' }]);
  return v == null ? null : v === 'overwrite';
}

/** Small pop-up menu under an anchor: items [{label, sub, onclick}]. */
export function popupMenu(anchor, items) {
  // A click on the button whose menu is open closes it (the mousedown already closed it; don't reopen on the click).
  if (anchor && anchor.__menuClosed && Date.now() - anchor.__menuClosed < 600) { anchor.__menuClosed = 0; return null; }
  document.querySelectorAll('.menu').forEach((m) => m.remove());
  const r = anchor.getBoundingClientRect();
  const m = h('div.menu', items.map((it) => h('button', { title: it.title || it.sub || '', disabled: !!it.disabled, onclick: () => { m.remove(); if (m.__close) document.removeEventListener('mousedown', m.__close); it.onclick(); } }, h('div', it.label), it.sub ? h('div.menu-sub', it.sub) : null)));
  document.body.append(m);
  const { height: mh, width: mw } = m.getBoundingClientRect();
  // keep the whole menu on screen (a button at the right edge opens it leftwards)
  m.style.left = Math.max(8, Math.min(r.left, window.innerWidth - mw - 8)) + 'px';
  m.style.top = (r.bottom + mh + 6 > window.innerHeight ? r.top - mh - 6 : r.bottom + 6) + 'px';
  m.__close = function close(e) {
    if (m.contains(e.target)) return;
    if (anchor && anchor.contains && anchor.contains(e.target) && m.isConnected) anchor.__menuClosed = Date.now();
    m.remove(); document.removeEventListener('mousedown', close);
  };
  setTimeout(() => document.addEventListener('mousedown', m.__close), 0);
  return m;
}

/** Highlight what b adds to a (word-level), for tag suggestions. */
export function diffAdded(a, b) {
  const aw = a.split(/(\s+)/), bw = b.split(/(\s+)/);
  const out = [];
  let i = 0;
  for (const w of bw) {
    if (i < aw.length && aw[i] === w) { out.push(document.createTextNode(w)); i++; continue; }
    out.push(/^\s+$/.test(w) ? document.createTextNode(w) : h('span.diff-add', w));
  }
  return out;
}

/** A "Columns ▾" button: tick which grid columns are shown. */
export function columnsMenu(grid) {
  const btn = h('button.btn.sm', { title: 'Show or hide columns' }, 'Columns ▾');
  let open = null;
  btn.addEventListener('click', () => {
    // a second click on Columns ▾ closes it
    if (open && open.isConnected) { open.remove(); open = null; return; }
    document.querySelectorAll('.menu').forEach((m) => m.remove());
    const r = btn.getBoundingClientRect();
    const m = open = h('div.menu', grid.columns.filter((c) => c.title || c.menuTitle).map((c) => {
      const cb = h('input', { type: 'checkbox', checked: !grid.hidden.has(c.id) });
      cb.addEventListener('change', () => grid.setHidden(c.id, !cb.checked));
      return h('label.menu-check', cb, c.menuTitle || c.title);
    }), h('button', { title: 'Show every column again, at its standard width and order', onclick: () => { m.remove(); grid.resetColumns(); grid.hidden.clear(); grid.setHidden('__none', false); } }, h('div', 'Reset columns')));
    document.body.append(m);
    m.style.left = Math.min(r.left, window.innerWidth - 260) + 'px';
    m.style.top = r.bottom + 6 + 'px';
    setTimeout(() => document.addEventListener('mousedown', function close(e) {
      if (!m.isConnected) { document.removeEventListener('mousedown', close); return; }
      if (!m.contains(e.target) && !btn.contains(e.target)) { m.remove(); document.removeEventListener('mousedown', close); }
    }), 0);
  });
  return btn;
}

/** Plain-text help tooltip icon. */
export function tip(text) {
  return h('span.tip', { title: text }, 'ⓘ');
}

// ------------------------------------------------------------------ icons
// Line icons (Lucide shapes, MIT), drawn with currentColor so they follow the theme.
const ICON_PATHS = {
  play: '<polygon points="6 3 20 12 6 21 6 3"/>',
  regen: '<path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/>',
  takes: '<path d="M3 7V5a2 2 0 0 1 2-2h2"/><path d="M17 3h2a2 2 0 0 1 2 2v2"/><path d="M21 17v2a2 2 0 0 1-2 2h-2"/><path d="M7 21H5a2 2 0 0 1-2-2v-2"/>',
  restore: '<path d="M9 14 4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11"/>',
  star: '<polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/>',
  folder: '<path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/>',
  flag: '<path class="corner" d="M3 7V5a2 2 0 0 1 2-2h2"/><path class="corner" d="M17 3h2a2 2 0 0 1 2 2v2"/><path class="corner" d="M21 17v2a2 2 0 0 1-2 2h-2"/><path class="corner" d="M7 21H5a2 2 0 0 1-2-2v-2"/><circle cx="12" cy="12" r="1"/><path d="M18.94 12.33a1 1 0 0 0 0-.66 7.5 7.5 0 0 0-13.88 0 1 1 0 0 0 0 .66 7.5 7.5 0 0 0 13.88 0"/>',
  ban: '<circle cx="12" cy="12" r="10"/><path d="m4.9 4.9 14.2 14.2"/>',
  undo: '<path d="M3 7v6h6"/><path d="M21 17a9 9 0 0 0-9-9 9 9 0 0 0-6 2.3L3 13"/>',
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/>',
  wave: '<path d="M3 12h2M7 8v8M11 5v14M15 9v6M19 11v2"/>',
  mic: '<rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5 11a7 7 0 0 0 14 0M12 18v3M8 21h8"/>',
  box: '<path d="M3 7l9-4 9 4-9 4-9-4z"/><path d="M3 7v10l9 4 9-4V7M12 11v10"/>',
  swords: '<path d="M14.5 17.5 3 6V3h3l11.5 11.5"/><path d="m13 19 6-6"/><path d="m16 16 4 4"/><path d="m19 21 2-2"/><path d="M14.5 6.5 18 3h3v3l-3.5 3.5"/><path d="m5 14 4 4"/><path d="m7 17-3 3"/><path d="m3 19 2 2"/>',
  lips: '<path d="M2 12c3-4 6-5 10-2 4-3 7-2 10 2-3 4-6 6-10 6s-7-2-10-6z"/><path d="M2 12h20"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  gear: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
  book: '<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20V3H6.5A2.5 2.5 0 0 0 4 5.5v14z"/><path d="M4 19.5A2.5 2.5 0 0 0 6.5 22H20v-5"/>',
  check: '<path d="M5 12l5 5L20 7"/>',
  help: '<circle cx="12" cy="12" r="10"/><path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3"/><path d="M12 17h.01"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/>',
  x: '<path d="M18 6 6 18M6 6l12 12"/>',
  edit: '<path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/>',
  export: '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M7 10l5 5 5-5"/><path d="M12 15V3"/>',
  wrench: '<path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/>',
  scissors: '<circle cx="6" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M20 4 8.12 15.88M14.47 14.48 20 20M8.12 8.12 12 12"/>',
  coins: '<circle cx="8" cy="8" r="6"/><path d="M18.09 10.37A6 6 0 1 1 10.34 18"/><path d="M7 6h1v4"/><path d="m16.71 13.88.7.71-2.82 2.82"/>',
};

/** A ring that empties as credits are used: fraction = share left (0..1). */
export function ringIcon(fraction, { size = 16 } = {}) {
  const f = Math.max(0, Math.min(1, Number.isFinite(fraction) ? fraction : 1));
  const r = 9, c = 2 * Math.PI * r;
  const col = f < 0.1 ? 'var(--bad)' : f < 0.25 ? 'var(--warn)' : 'var(--accent)';
  const span = document.createElement('span');
  span.className = 'ico';
  span.innerHTML = `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke-width="3"><circle cx="12" cy="12" r="${r}" stroke="var(--line)"/><circle cx="12" cy="12" r="${r}" stroke="${col}" stroke-linecap="round" stroke-dasharray="${(c * f).toFixed(2)} ${c.toFixed(2)}" transform="rotate(-90 12 12)"/></svg>`;
  return span;
}

/** An inline SVG icon: icon('play'), icon('star', { filled: true }). */
export function icon(name, { size = 16, filled = false } = {}) {
  const span = document.createElement('span');
  span.className = 'ico';
  span.innerHTML = `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 24 24" fill="${filled ? 'currentColor' : 'none'}" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${ICON_PATHS[name] || ''}</svg>`;
  return span;
}

// ------------------------------------------------------------------ external requirements
// Things DBVO Studio cannot ship (licences), explained plainly: what it is, where to get it, where to put it.
const extLink = (url, text) => h('a', { href: '#', onclick: (e) => { e.preventDefault(); rpc('open.url', { url }); } }, text || url);
const REQUIREMENTS = {
  facefx: {
    title: 'FaceFXWrapper.exe: makes the .lip (lip-sync) files',
    why: 'Not included: it contains Creation Kit code and Bethesda files, so each user downloads it.',
    steps: () => [
      ['Download FaceFXWrapper from Nukem\'s page: ', extLink('https://www.nexusmods.com/skyrimspecialedition/mods/20061', 'Nexus Mods'), ' or ', extLink('https://github.com/Nukem9/FaceFXWrapper/releases', 'GitHub releases'), '.'],
      ['Unzip it into any folder you keep, e.g. C:\\DBVO Tools\\.'],
      ['Global Settings → Tools → FaceFXWrapper.exe → Browse, and pick FaceFXWrapper.exe.'],
      ['Already have DBVO Voice Pack Maker installed? Then it is found automatically.'],
    ],
  },
  fonix: {
    title: 'FonixData.cdf: the speech data FaceFXWrapper needs for .lip files',
    why: 'Not included: it is Bethesda\'s file from the Creation Kit.',
    steps: () => [
      ['Install the free Skyrim Special Edition Creation Kit from Steam: ', extLink('https://store.steampowered.com/app/1946180/', 'Steam store page'), ' (you need to own Skyrim Special Edition).'],
      ['After installing, the file is here: <Skyrim Special Edition>\\Data\\Sound\\Voice\\Processing\\FonixData.cdf.'],
      ['Set Global Settings → Plugins → Base game plugins folder to your game\'s Data folder and DBVO Studio finds it by itself. Or use Browse next to FonixData.cdf.'],
      ['Not there after installing the Creation Kit? Some installs miss it: ', extLink('https://www.nexusmods.com/skyrimspecialedition/mods/40971', 'SSE Creation Kit Fonixdata Lip Sync Fix'), ' on Nexus Mods provides it.'],
    ],
  },
  xwma: {
    title: 'xWMAEncode.exe: converts the audio for .fuz files',
    why: 'Not included: it is a Microsoft tool (DirectX SDK) that comes with the Creation Kit.',
    steps: () => [
      ['Install the free Skyrim Special Edition Creation Kit from Steam: ', extLink('https://store.steampowered.com/app/1946180/', 'Steam store page'), '.'],
      ['It is then here: <Skyrim Special Edition>\\Tools\\Audio\\xWMAEncode.exe. DBVO Studio looks there by itself (set the Base game plugins folder if your game is not in the default Steam folder).'],
      ['Or use Browse next to xWMAEncode.exe. It also comes with Yakitori Audio Converter and the Microsoft DirectX SDK (June 2010).'],
      ['Only needed for .fuz files. WAV + LIP export works without it.'],
    ],
  },
  elevenlabs: {
    title: 'ElevenLabs API key: lets DBVO Studio generate voices with your ElevenLabs account',
    why: 'Each user uses their own account and credits.',
    steps: () => [
      ['Sign in at ', extLink('https://elevenlabs.io/app/settings/api-keys', 'elevenlabs.io → API keys'), ' (make an account first if you have none).'],
      ['Create API key. Give it access to Text to Speech, Speech to Speech, Voices and Models (or full access), then copy it.'],
      ['Global Settings → ElevenLabs → API key: paste it and Save key. It is stored encrypted for your Windows user.'],
      ['Characters you generate use your plan\'s credits; the credits button in Phase 2 shows what is left.'],
    ],
  },
  'engine-fish': {
    title: 'Fish Audio API key: generate with your Fish Audio voices',
    why: 'Each user uses their own account and credits. Fish Audio S2 performs free-form [tags] like ElevenLabs v3/v4.',
    steps: () => [
      ['Sign in at ', extLink('https://fish.audio/app/api-keys/', 'fish.audio → API Keys'), ' and create a key.'],
      ['Make your voice model on fish.audio from recordings you have the right to use, and copy its ID (the model page\'s address ends in it).'],
      ['Global Settings → Voice engines → Fish Audio: paste the key and Save key.'],
      ['Pick Fish Audio as the voice pack\'s engine (Phase 2 toolbar) and paste the model ID into Voice ID.'],
    ],
  },
  'engine-inworld': {
    title: 'Inworld API key: generate with Inworld TTS-2',
    why: 'Each user uses their own account. inworld-tts-2 follows free-form [tags] as instructions and performs sounds such as [laugh] [sigh].',
    steps: () => [
      ['Sign in at ', extLink('https://platform.inworld.ai/', 'platform.inworld.ai'), ' → API Keys, create a key and copy the ', h('b', 'Base64'), ' credential (not the key ID).'],
      ['Pick a voice in the TTS playground, or clone one from your own recordings, and copy its voice ID.'],
      ['Global Settings → Voice engines → Inworld: paste the credential and Save key.'],
      ['Pick Inworld as the voice pack\'s engine (Phase 2 toolbar) and type the voice ID.'],
    ],
  },
  'engine-cartesia': {
    title: 'Cartesia API key: generate with Cartesia Sonic',
    why: 'Each user uses their own account. Cartesia takes an emotion per line instead of [tags]: a leading tag picks it.',
    steps: () => [
      ['Sign in at ', extLink('https://play.cartesia.ai/keys', 'play.cartesia.ai → API Keys'), ' and create a key.'],
      ['Pick or clone a voice under Voices and copy its ID (the long ID, not the name).'],
      ['Global Settings → Voice engines → Cartesia: paste the key and Save key.'],
      ['Pick Cartesia as the voice pack\'s engine (Phase 2 toolbar) and paste the voice ID.'],
    ],
  },
  'engine-minimax': {
    title: 'MiniMax API key: generate with MiniMax Speech',
    why: 'Each user uses their own account. MiniMax takes one of 9 emotions per line instead of [tags]: a leading tag picks it.',
    steps: () => [
      ['Sign in at ', extLink('https://platform.minimax.io/', 'platform.minimax.io'), ' → API Keys and create a key (the international site; keys from the China site don\'t work here).'],
      ['Use a system voice ID, or clone a voice there from your own recordings and copy its ID.'],
      ['Global Settings → Voice engines → MiniMax: paste the key and Save key.'],
      ['Pick MiniMax as the voice pack\'s engine (Phase 2 toolbar) and type the voice ID.'],
    ],
  },
  'engine-openai': {
    title: 'OpenAI-compatible: OpenAI, or a free speech server on your PC',
    why: 'OpenAI needs a key. A local OpenAI-compatible server (Chatterbox TTS API, Kokoro-FastAPI, AllTalk, openedai-speech) needs none.',
    steps: () => [
      ['OpenAI: create a key at ', extLink('https://platform.openai.com/api-keys', 'platform.openai.com → API keys'), ' and save it under Global Settings → Voice engines → OpenAI-compatible.'],
      ['A local server: start it, then put its address in the Phase 2 toolbar\'s Server field, e.g. http://127.0.0.1:5123. No key needed.'],
      ['Voice: OpenAI\'s built-in voices (marin, cedar, alloy…), or the voice name your server lists. Extra fields pass settings only your server knows, e.g. {"exaggeration": 0.6}.'],
      ['[tags] are sent as delivery instructions. gpt-4o-mini-tts follows them; most local servers ignore them.'],
    ],
  },
  python: {
    title: 'Python with faster-whisper (optional)',
    why: 'Only for the "My faster-whisper (Python)" engine. The built-in Whisper engine needs nothing.',
    steps: () => [
      ['Install Python 3.10 or newer from ', extLink('https://www.python.org/downloads/windows/', 'python.org'), '.'],
      ['Open a terminal and run: pip install faster-whisper'],
      ['Global Settings → WAV to LIP → Python: Browse to python.exe.'],
    ],
  },
};

/** An ⓘ button that explains, with links, how to get an external requirement. */
export function needInfo(key) {
  const r = REQUIREMENTS[key];
  if (!r) return null;
  const btn = h('button.icon-btn.need-info', { title: 'How to get ' + r.title.split(':')[0], onclick: (e) => { e.preventDefault(); e.stopPropagation(); showNeed(key); } }, icon('info', { size: 15 }));
  return btn;
}

export function showNeed(key) {
  const r = REQUIREMENTS[key];
  if (!r) return;
  modal(r.title, [
    h('div.faint', r.why),
    h('ol.need-steps', r.steps().map((parts) => h('li', ...parts))),
  ]);
}

/** Before asking the AI for tags: is a provider set up? Explains what is missing if not. */
export function aiReady() {
  const ai = (window.__dbvoState && window.__dbvoState.settings.aiProvider) || {};
  const where = 'Global Settings → General & tools → AI auto-tagging';
  if (!ai.endpoint) { toast(`AI Auto Tag needs an AI provider, and none is set up yet. Choose one (free ones run on your own PC) in ${where}.`, true); return false; }
  const local = /^https?:\/\/(localhost|127\.0\.0\.1|\[::1\])[:/]/i.test(ai.endpoint);
  if (!local && !ai.hasKey) { toast(`AI Auto Tag uses an online AI provider (${ai.endpoint.replace(/^https?:\/\//, '').split('/')[0]}), which needs an API key, and none is saved. Add it in ${where}.`, true); return false; }
  return true;
}

/** One readable sentence about an AI Auto Tag run: how many suggestions, and why the rest were skipped. */
export function autotagSummary(results) {
  const ok = results.filter((x) => !x.error).length;
  const bad = results.filter((x) => x.error);
  if (!bad.length) return `${ok} suggestion(s) ready. Accept (✓) or undo (✕) them in each row.`;
  const reasons = [...new Set(bad.map((x) => x.error))];
  if (!ok) return reasons.length === 1 ? `No tags were added. ${reasons[0]}` : `No tags were added. ${reasons[0]} (and ${reasons.length - 1} other problem(s))`;
  return `${ok} suggestion(s) ready; ${bad.length} line(s) got none. ${reasons[0]}`;
}

/** A small panel under a button, for settings that don't need to be on screen all the time. Closes on a click
 *  outside or Esc. content: a function returning the panel's elements (called each time it opens). */
let openPop = null;
export function popover(anchor, content) {
  if (openPop) { const same = openPop.anchor === anchor; openPop.close(); if (same) return; }
  const r = anchor.getBoundingClientRect();
  const el = h('div.pop-panel', { style: { top: `${r.bottom + 6}px`, left: `${Math.max(8, Math.min(r.left, window.innerWidth - 360))}px` } }, ...content());
  const onDown = (e) => { if (!el.contains(e.target) && !anchor.contains(e.target)) close(); };
  const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
  function close() { el.remove(); document.removeEventListener('mousedown', onDown, true); window.removeEventListener('keydown', onKey, true); if (openPop && openPop.el === el) openPop = null; }
  document.addEventListener('mousedown', onDown, true);
  window.addEventListener('keydown', onKey, true);
  document.body.append(el);
  // Near the bottom of the window (a status bar), open upwards instead.
  const pr = el.getBoundingClientRect();
  if (pr.bottom > window.innerHeight - 8) el.style.top = `${Math.max(8, r.top - 6 - pr.height)}px`;
  if (pr.right > window.innerWidth - 8) el.style.left = `${Math.max(8, window.innerWidth - 8 - pr.width)}px`;
  openPop = { el, anchor, close };
}

export function closePopover() { if (openPop) openPop.close(); }

/**
 * The one "Generate options ▾" button of a page: a panel lists what can be generated, each with its count, characters
 * and a plain sentence; empty choices are greyed out. Every choice goes through the page's "Check before spending
 * credits" step. While a generation runs, the button is the Stop button.
 * items(): [{ key, label, count, chars, desc, warn }]
 */
export function generateButton({ running, items, onPick, unit = 'line', label = 'Generate options', title = 'Choose what to generate. You see the count, characters and every setting before any credits are spent.' }) {
  if (running) {
    return h('button.btn.primary.stop-btn', { title: 'Stop now: what is already made keeps its takes; nothing more is sent to the engine', onclick: (e) => { e.currentTarget.disabled = true; e.currentTarget.textContent = 'Stopping…'; rpc('job.cancel').catch(fail); } }, '■ Stop generating');
  }
  const btn = h('button.btn.primary.gen-btn', { title }, label + ' ▾');
  btn.addEventListener('click', () => popover(btn, () => [h('div.gen-pop', items().filter(Boolean).map((it) => {
    const off = !it.count;
    const n = `${it.count.toLocaleString()} ${it.unit || unit}${it.count === 1 ? '' : 's'}`;
    return h('button.gen-item' + (off ? '.off' : '') + (it.warn ? '.warn' : ''), {
      disabled: off, title: off ? 'Nothing to generate here right now' : it.desc,
      onclick: () => { closePopover(); onPick(it.key); },
    }, h('div.gen-top', h('b', it.label), h('span.spacer'), h('span.mono', n), it.chars != null ? h('span.faint', `${it.chars.toLocaleString()} chars`) : null), h('div.gen-desc', it.desc));
  }))]));
  return btn;
}

/** Hover texts for the engine settings in the Phase 2 and NPC Revoice toolbars, by their label. */
export const TOOL_TIPS = {
  'Engine': 'Which voice engine makes the takes: ElevenLabs, Fish Audio, Inworld, Cartesia, MiniMax or OpenAI-compatible online (keys in Global Settings → Voice engines), or VoxCPM / OmniVoice running on this PC. Each handles [tags] its own way: see the line under the tag bar',
  'Voice ID': 'The voice to speak with, as the engine names it. Hover the box for where to find it',
  'Temperature': 'Fish Audio: how much takes vary; 0 = the engine\'s default',
  'Emotion': 'The emotion of lines whose leading [tags] name none; "from the tags" leaves those lines to the model',
  'Instructions': 'Delivery instructions sent with every line; the words of each line\'s [tags] are added',
  'Extra fields': 'JSON sent as it is, for settings only your server knows (e.g. Chatterbox: exaggeration, cfg_weight)',
  'Server': 'https://api.openai.com, or the address of an OpenAI-compatible server on your PC',
  'API key': 'Whether this engine\'s key is saved (Global Settings → General & tools → Voice engines)',
  'Volume': 'Loudness the engine renders at (1 = normal)',
  'Pitch': 'MiniMax: semitones up or down (−12 to 12)',
  'Voice': 'The voice that speaks the lines',
  'Target voice': 'The voice the recordings are converted into',
  'Engine · voice': 'This speaker\'s engine and voice',
  'Speaker': 'Show one speaker\'s lines; pick its voice next to it',
  'Model': 'The ElevenLabs model. "· tags" models (v3 / v4) understand [audio tags]',
  'Stability': 'Creative varies the delivery more; Robust keeps it steady but flatter',
  'Similarity': 'How closely the take sticks to the voice. Too high can add artefacts',
  'Style': 'Exaggerates the voice\'s style. Costs speed and stability; 0 is safest',
  'Speed': 'Speaking speed. 1.00 is normal',
  'Speaker boost': 'Boosts similarity to the voice; slightly slower',
  'Remove background noise': 'Strip background noise from the source before converting',
  'Language': 'Force the language, or Auto to let the model detect it',
  'Read numbers ⓘ': 'Whether numbers, dates and abbreviations are written out before speaking ("1st" → "first")',
  'Output': 'Audio format, seed and how many lines are requested at once',
  'Format': 'The audio ElevenLabs sends back. PCM 44.1k is lossless (needs a Pro plan or higher)',
  'Seed': 'Same seed + same text + same settings ≈ same take. Empty = random',
  'Concurrent': 'How many lines are requested at once. Match your plan\'s limit',
  'Reference voice': 'The reference recording the local engine copies the voice from',
  'Server': 'Whether the local engine\'s server answers',
  'CFG': 'How strongly VoxCPM follows the text and voice (higher = stricter)',
  'Timesteps': 'VoxCPM quality steps: more is slower and usually has fewer artefacts',
  'Steps': 'OmniVoice quality steps: more is slower and usually has fewer artefacts',
  'Guidance': 'How strongly OmniVoice follows the text and voice',
  'Normalize': 'Write out numbers and abbreviations before speaking',
  'Retry bad cases': 'Regenerate automatically when the result is too long or garbled',
  'Reference mode': 'Use the reference audio as a voice prompt (closer timbre)',
  'Control CFG': 'How strongly the reference prompt is followed',
};

/** A short glow on a row whose new take just arrived (red when it failed). */
export function flashRow(grid, key, bad = false) {
  const cls = bad ? 'flash-bad' : 'flash-done';
  grid._flash = grid._flash || new Map();
  grid._flash.set(String(key), { cls, until: Date.now() + 2600 });
  setTimeout(() => { const f = grid._flash.get(String(key)); if (f && f.until <= Date.now()) grid._flash.delete(String(key)); const e2 = grid.rowEl(key); if (e2) e2.classList.remove('flash-done', 'flash-bad'); }, 2650);
  const el = grid.rowEl(key);
  if (el) el.classList.add(cls);
}

/** ElevenLabs output formats offered in the toolbars. A format saved by an older version stays listed, so the select shows what is really used. */
export function outputFormats(current) {
  const list = [['mp3_44100_128', 'MP3 44.1k 128'], ['mp3_44100_192', 'MP3 44.1k 192'], ['pcm_44100', 'PCM 44.1k (lossless)']];
  if (current && !list.some(([k]) => k === current)) list.push([current, current.replace(/^pcm_(\d+)$/, (m, hz) => `PCM ${Number(hz) / 1000}k`).replace(/^mp3_(\d+)_(\d+)$/, (m, hz, kb) => `MP3 ${Number(hz) / 1000}k ${kb}`) + ' (earlier setting)']);
  return list;
}

/**
 * Rebuild a page part without jumping to the top: the scroll positions of its scrolling panels (.view-body, .fedit)
 * are put back after rebuild(). Pass keep = false when the user switched to another tab (that one starts at the top).
 */
export function keepScroll(root, rebuild, keep = true) {
  const sel = '.view-body, .fedit';
  const before = keep ? [...root.querySelectorAll(sel)].map((el) => el.scrollTop) : [];
  const rootTop = keep && root.matches && root.matches(sel) ? root.scrollTop : null;
  rebuild();
  if (!keep) return;
  const restore = () => {
    [...root.querySelectorAll(sel)].forEach((el, i) => { if (before[i] && el.scrollTop !== before[i]) el.scrollTop = before[i]; });
    if (rootTop) root.scrollTop = rootTop;
  };
  restore();
  requestAnimationFrame(restore);
}

/**
 * A full-window "please wait" screen with a large glowing progress bar, for slow steps (reading thousands of voice lines).
 * It only appears when the step takes longer than delayMs, so quick steps don't flash. Returns close().
 */
export function busyOverlay(title = 'Loading voice lines', sub = 'Reading the takes and files from disk…', delayMs = 350) {
  let el = null;
  const timer = setTimeout(() => {
    el = h('div.busy-overlay', { role: 'status', 'aria-live': 'polite' },
      h('div.busy-card', h('div.busy-title', title), h('div.busy-bar', h('i')), h('div.busy-sub', sub)));
    document.body.append(el);
  }, delayMs);
  return () => { clearTimeout(timer); if (el) { el.classList.add('out'); const e = el; setTimeout(() => e.remove(), 220); } };
}

/** Run a slow promise behind the busy screen. */
export async function withBusy(promise, title, sub) {
  const close = busyOverlay(title, sub);
  try { return await promise; } finally { close(); }
}

/** A voice pack's pack format, in words. */
export function namingLabel(n) { return n === 'Legacy' ? 'DBVO 1, Legacy names' : n === 'Dbrev' ? 'DBReV (BETA TESTING)' : 'DBVO 2.0'; }
