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
export function modal(title, content, buttons = [{ label: 'OK', primary: true, value: true }]) {
  const m = document.getElementById('modal');
  const card = clear(m.querySelector('.modal-card'));
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
  return modal(title, [h('div', text)], [
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
 * columns: [{ title, width | flex, render(row, index) -> Node|string, cls }]
 */
export class VGrid {
  constructor(container, { columns, rowHeight = 30, rowKey, rowClass, onRowClick, onRowDblClick }) {
    this.columns = columns;
    this.rowHeight = rowHeight;
    this.rowKey = rowKey;
    this.rowClass = rowClass;
    this.onRowClick = onRowClick;
    this.onRowDblClick = onRowDblClick;
    this.rows = [];
    this.focusKey = null;
    this.selected = new Set();
    this.el = h('div.vgrid', { tabindex: 0 });
    this.head = h('div.vgrid-head', columns.map((c) => this.cellFor(c, c.title || '')));
    this.body = h('div.vgrid-body');
    this.el.append(this.head, this.body);
    container.append(this.el);
    this.el.addEventListener('scroll', () => this.paint());
    new ResizeObserver(() => this.paint()).observe(this.el);
    this.el.addEventListener('keydown', (e) => this.key(e));
  }

  cellFor(c, content) {
    const cell = h('div.cell' + (c.flex ? '.flex' : '') + (c.cls ? '.' + c.cls : ''));
    if (c.width) cell.style.width = c.width + 'px';
    if (c.flex) cell.style.flex = c.flex;
    if (content instanceof Node) cell.append(content);
    else cell.textContent = content;
    return cell;
  }

  setRows(rows) {
    this.rows = rows;
    const keys = new Set(rows.map(this.rowKey));
    for (const k of [...this.selected]) if (!keys.has(k)) this.selected.delete(k);
    this.body.style.height = rows.length * this.rowHeight + 'px';
    this.paint(true);
  }

  refreshRow(key) {
    const el = this.body.querySelector(`[data-key="${CSS.escape(key)}"]`);
    if (!el) return;
    const i = Number(el.dataset.index);
    el.replaceWith(this.renderRow(this.rows[i], i));
  }

  paint(force = false) {
    const top = this.el.scrollTop;
    const height = this.el.clientHeight || 600;
    const first = Math.max(0, Math.floor(top / this.rowHeight) - 10);
    const last = Math.min(this.rows.length, Math.ceil((top + height) / this.rowHeight) + 10);
    if (!force && this._first === first && this._last === last) return;
    // Keep a focused input alive while the user types.
    const active = document.activeElement;
    const activeRow = active && active.closest && active.closest('.vrow');
    this._first = first;
    this._last = last;
    const frag = document.createDocumentFragment();
    let keep = null;
    for (let i = first; i < last; i++) {
      const r = this.rows[i];
      if (activeRow && activeRow.dataset.key === String(this.rowKey(r)) && !force) {
        activeRow.style.top = i * this.rowHeight + 'px';
        keep = activeRow;
        frag.append(activeRow);
        continue;
      }
      frag.append(this.renderRow(r, i));
    }
    clear(this.body).append(frag);
    if (keep && active) active.focus();
  }

  renderRow(r, i) {
    const key = String(this.rowKey(r));
    const row = h('div.vrow', { 'data-key': key, 'data-index': i });
    row.style.top = i * this.rowHeight + 'px';
    if (this.selected.has(key)) row.classList.add('sel');
    if (this.focusKey === key) row.classList.add('focus');
    const extra = this.rowClass && this.rowClass(r);
    if (extra) row.classList.add(...extra.split(' ').filter(Boolean));
    for (const c of this.columns) row.append(this.cellFor(c, c.render(r, i)));
    row.addEventListener('mousedown', (e) => {
      if (e.target.closest('input,button,select,textarea,a')) return;
      if (e.shiftKey && this.focusKey != null) this.selectRange(this.focusKey, key);
      else if (e.ctrlKey) this.toggle(key);
      this.focus(key);
      if (this.onRowClick) this.onRowClick(r, e);
    });
    if (this.onRowDblClick) row.addEventListener('dblclick', () => this.onRowDblClick(r));
    return row;
  }

  focus(key, scroll = false) {
    const prev = this.focusKey;
    this.focusKey = key;
    if (prev != null) this.refreshRow(prev);
    this.refreshRow(key);
    if (scroll) {
      const i = this.rows.findIndex((r) => String(this.rowKey(r)) === key);
      if (i >= 0) {
        const y = i * this.rowHeight;
        if (y < this.el.scrollTop + 28 || y > this.el.scrollTop + this.el.clientHeight - this.rowHeight * 2)
          this.el.scrollTop = y - this.el.clientHeight / 2;
      }
    }
  }

  toggle(key) {
    if (this.selected.has(key)) this.selected.delete(key);
    else this.selected.add(key);
    this.refreshRow(key);
    this.el.dispatchEvent(new CustomEvent('selection'));
  }

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

  key(e) {
    if (e.target.closest('input,textarea,select')) return;
    const keys = this.rows.map((r) => String(this.rowKey(r)));
    let i = keys.indexOf(this.focusKey);
    if (e.key === 'ArrowDown' || e.key === 'j') i = Math.min(keys.length - 1, i + 1);
    else if (e.key === 'ArrowUp' || e.key === 'k') i = Math.max(0, i - 1);
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
