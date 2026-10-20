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
      (listeners.get(msg.event) || []).forEach((fn) => fn(msg.data));
      return;
    }
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if ('error' in msg && msg.error) p.reject(new Error(msg.error));
    else p.resolve(msg.result);
  });
}

/** Call a host method. */
export function rpc(method, params = {}) {
  if (!webview) return Promise.reject(new Error('Not running inside DBVO Voice Pack Studio.'));
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    webview.postMessage({ id, method, params });
  });
}

/** Send dropped File objects to the host; resolves to their real paths. */
export function droppedPaths(fileList) {
  if (!webview || !webview.postMessageWithAdditionalObjects) return Promise.resolve([]);
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    webview.postMessageWithAdditionalObjects({ id, method: 'dropped', params: {} }, fileList);
  });
}

export function on(event, fn) {
  if (!listeners.has(event)) listeners.set(event, []);
  listeners.get(event).push(fn);
}

/** h('div.cls#id', {attrs/on*}, ...children) */
export function h(tag, attrs, ...children) {
  const m = /^([a-z0-9]+)?((?:[.#][\w-]+)*)$/i.exec(tag) || [];
  const el = document.createElement(m[1] || 'div');
  (m[2] || '').replace(/([.#])([\w-]+)/g, (_, t, v) => {
    if (t === '.') el.classList.add(v);
    else el.id = v;
  });
  if (attrs && (typeof attrs !== 'object' || attrs instanceof Node || Array.isArray(attrs))) {
    children.unshift(attrs);
    attrs = null;
  }
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === 'class') el.className += ' ' + v;
    else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
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
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

let toastTimer;
export function toast(text, bad = false) {
  const t = document.getElementById('toast');
  t.textContent = text;
  t.classList.toggle('bad', bad);
  t.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add('hidden'), bad ? 7000 : 3200);
}

export function fail(e) {
  console.error(e);
  toast(e && e.message ? e.message : String(e), true);
}

/** Show a modal. content: Node[]; buttons: [{label, primary, value}]. Resolves to the clicked value (null on Esc). */
export function modal(title, content, buttons = [{ label: 'OK', primary: true, value: true }], { wide = false } = {}) {
  const m = document.getElementById('modal');
  const card = clear(m.querySelector('.modal-card'));
  card.classList.toggle('wide', wide);
  return new Promise((resolve) => {
    const close = (v) => {
      m.classList.add('hidden');
      document.removeEventListener('keydown', key);
      resolve(v);
    };
    const key = (e) => { if (e.key === 'Escape') close(null); };
    document.addEventListener('keydown', key);
    card.append(
      h('h2', title),
      ...[content].flat(),
      h('div.row', h('div.spacer'), ...buttons.map((b) =>
        h('button.btn' + (b.primary ? '.primary' : '') + (b.danger ? '.danger' : ''), { onclick: () => close(typeof b.value === 'function' ? b.value() : b.value) }, b.label))),
    );
    m.classList.remove('hidden');
    const first = card.querySelector('input,textarea,select');
    if (first) first.focus();
  });
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
export class VGrid {
  constructor(container, { columns, rowHeight = 34, rowKey, rowClass, onRowClick, onRowDblClick, storageKey }) {
    this.columns = columns.map((c, i) => ({ id: c.id || String(i), min: 40, resizable: true, ...c, _orig: { width: c.width, flex: c.flex } }));
    this.storageKey = storageKey;
    const saved = storageKey ? store.get('cols:' + storageKey, {}) : {};
    for (const c of this.columns) if (saved[c.id]) { c.width = saved[c.id]; c.flex = 0; }
    this.hidden = new Set(storageKey ? store.get('hide:' + storageKey, []) : []);
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

  renderHead() {
    clear(this.head);
    for (const c of this.shown()) {
      const cell = h('div.cell', c.title || '');
      this.sizeCell(cell, c);
      if (c.resizable && c.title) {
        const grip = h('div.resizer', { title: 'Drag to resize. Double-click to reset.' });
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
    if (this.storageKey) store.set('cols:' + this.storageKey, {});
    for (const c of this.columns) Object.assign(c, c._orig);
    this.renderHead();
    this.paint(true);
  }

  setRows(rows) {
    this.rows = rows;
    const keys = new Set(rows.map((r) => String(this.rowKey(r))));
    for (const k of [...this.selected]) if (!keys.has(k)) this.selected.delete(k);
    this.body.style.height = rows.length * this.rowHeight + 'px';
    this.paint(true);
  }

  rowEl(key) {
    return this.body.querySelector(`[data-key="${CSS.escape(String(key))}"]`);
  }

  /** Re-render one row (skipped while the user is typing in it, so no keystrokes are lost). */
  refreshRow(key, force = false) {
    const el = this.rowEl(key);
    if (!el) return;
    if (!force && el.contains(document.activeElement) && document.activeElement.matches('input,textarea,select')) return;
    const i = Number(el.dataset.index);
    el.replaceWith(this.renderRow(this.rows[i], i));
  }

  paint(force = false) {
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
        activeRow.style.top = i * this.rowHeight + 'px';
        activeRow.dataset.index = i;
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
    const extra = this.rowClass && this.rowClass(r);
    if (extra) row.classList.add(...extra.split(' ').filter(Boolean));
    for (const c of this.shown()) {
      const cell = h('div.cell' + (c.cls ? '.' + c.cls.split(' ').join('.') : ''));
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
    else if (e.key === 'a' && e.ctrlKey) { e.preventDefault(); this.selectAll(true); return; }
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
  for (const [v, label] of options) {
    el.append(h('button' + (String(v) === String(value) ? '.on' : ''), { disabled, onclick: () => onchange(v) }, label));
  }
  return el;
}

/** Small pop-up menu under an anchor: items [{label, sub, onclick}]. */
export function popupMenu(anchor, items) {
  document.querySelectorAll('.menu').forEach((m) => m.remove());
  const r = anchor.getBoundingClientRect();
  const m = h('div.menu', items.map((it) => h('button', { onclick: () => { m.remove(); it.onclick(); } }, h('div', it.label), it.sub ? h('div.menu-sub', it.sub) : null)));
  document.body.append(m);
  const mh = m.getBoundingClientRect().height;
  m.style.left = Math.min(r.left, window.innerWidth - 260) + 'px';
  m.style.top = (r.bottom + mh + 6 > window.innerHeight ? r.top - mh - 6 : r.bottom + 6) + 'px';
  setTimeout(() => document.addEventListener('mousedown', function close(e) { if (!m.contains(e.target)) { m.remove(); document.removeEventListener('mousedown', close); } }), 0);
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
  btn.addEventListener('click', () => {
    document.querySelectorAll('.menu').forEach((m) => m.remove());
    const r = btn.getBoundingClientRect();
    const m = h('div.menu', grid.columns.filter((c) => c.title).map((c) => {
      const cb = h('input', { type: 'checkbox', checked: !grid.hidden.has(c.id) });
      cb.addEventListener('change', () => grid.setHidden(c.id, !cb.checked));
      return h('label.menu-check', cb, c.title);
    }), h('button', { onclick: () => { m.remove(); grid.resetColumns(); grid.hidden.clear(); grid.setHidden('__none', false); } }, h('div', 'Reset columns')));
    document.body.append(m);
    m.style.left = Math.min(r.left, window.innerWidth - 260) + 'px';
    m.style.top = r.bottom + 6 + 'px';
    setTimeout(() => document.addEventListener('mousedown', function close(e) { if (!m.contains(e.target) && e.target !== btn) { m.remove(); document.removeEventListener('mousedown', close); } }), 0);
  });
  return btn;
}

/** Plain-text help tooltip icon. */
export function tip(text) {
  return h('span.tip', { title: text }, 'ⓘ');
}
