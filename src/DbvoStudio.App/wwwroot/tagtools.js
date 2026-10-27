// Shared ElevenLabs tag tools for Phase 2 and NPC Revoice: the "Add a [tag] to all shown…" dialog and caret insertion.
import { h, clear, toast, modal, segmented } from './lib.js';

/** Insert a tag at the caret of a text box (spaces added as needed); returns the new value. */
export function insertTagAtCaret(el, t) {
  const v = el.value;
  const a = el.selectionStart ?? v.length, b = el.selectionEnd ?? a;
  const before = v.slice(0, a), after = v.slice(b);
  const ins = (before && !/\s$/.test(before) ? ' ' : '') + t + (after && !/^\s/.test(after) ? ' ' : (after ? '' : ' '));
  el.value = before + ins + after;
  const pos = (before + ins).length;
  el.focus();
  el.setSelectionRange(pos, pos);
  return el.value;
}

/**
 * "Add a [tag] to all shown…": add or remove tags on the lines in the current filter, optionally only where conditions match.
 * pool: the lines shown (each with .spoken); idOf(l); original(l): the text searched by the second condition;
 * originalLabel / originalNote: how that condition is described; apply(tags, mode, ids, placement).
 */
export async function bulkTagDialog({ pool, idOf, original, originalLabel, originalNote, originalPlaceholder = '(Intimidate), (Brawl)', tags: voiceTags = [], apply }) {
  let mode = 'add';
  const tagsIn = h('input', { type: 'text', placeholder: '[whispers]' });
  const list = (v) => v.split(',').map((x) => x.trim().toLowerCase()).filter(Boolean);
  const strip = (t) => t.replace(/\[[^\]]*\]/g, ' ').replace(/\s+/g, ' ').trim();
  const opt = (label, input, after, note) => {
    const cb = h('input', { type: 'checkbox' });
    if (input) input.addEventListener('input', () => { cb.checked = input.value.trim().length > 0; update(); });
    cb.addEventListener('change', update);
    return { cb, el: h('label.tag-opt', cb, h('span.tag-opt-label', label), input || null, after ? h('span.tag-opt-after', after) : null, note ? h('div.tag-opt-note', note) : null) };
  };
  const spokenIn = h('input', { type: 'text', placeholder: 'I will kill you.' });
  const origIn = h('input', { type: 'text', placeholder: originalPlaceholder });
  const longIn = h('input', { type: 'number', min: 0, style: { width: '70px' } });
  const shortIn = h('input', { type: 'number', min: 0, style: { width: '70px' } });
  const o1 = opt('', spokenIn, 'in SPOKEN TEXT');
  const o2 = opt('', origIn, originalLabel, originalNote);
  const o3 = opt('', longIn, 'characters. Shorter lines will not be tagged.');
  const o4 = opt('', shortIn, 'characters. Longer lines will not be tagged.');
  const o5 = opt('Replace current initial tag. If unchecked, the tag will be placed after existing tag.', null, null);
  const count = h('div.faint');
  const verb = () => (mode === 'add' ? 'Add' : 'Remove');
  const labels = () => {
    o1.el.querySelector('.tag-opt-label').textContent = `${verb()} tag if detected:`;
    o2.el.querySelector('.tag-opt-label').textContent = `${verb()} tag if detected:`;
    o3.el.querySelector('.tag-opt-label').textContent = `${verb()} tag if longer than:`;
    o4.el.querySelector('.tag-opt-label').textContent = `${verb()} tag if shorter than:`;
    o5.el.style.display = mode === 'add' ? '' : 'none';
  };
  const matches = () => pool.filter((l) => {
    const sp = strip(l.spoken).toLowerCase();
    if (o1.cb.checked && list(spokenIn.value).length && !list(spokenIn.value).some((x) => sp.includes(strip(x)))) return false;
    if (o2.cb.checked && list(origIn.value).length && !list(origIn.value).some((x) => (original(l) || '').toLowerCase().includes(x))) return false;
    if (o3.cb.checked && longIn.value !== '' && !(strip(l.spoken).length > Number(longIn.value))) return false;
    if (o4.cb.checked && shortIn.value !== '' && !(strip(l.spoken).length < Number(shortIn.value))) return false;
    return true;
  });
  function update() {
    const n = matches().length;
    count.textContent = mode === 'add' ? `Added to ${n} of ${pool.length} line(s) in the current filter.` : `Removed from ${n} of ${pool.length} line(s) in the current filter (where they have it).`;
  }
  const modeSel = segmented([['add', 'Add'], ['remove', 'Remove']], mode, (v) => { mode = v; labels(); update(); });
  modeSel.classList.add('accent');
  labels(); update();
  // the voice pack's tags, as in the grid's tags row: click one to put it in the box
  let tagRow = null;
  if (voiceTags.length) {
    const put = (t) => insertTagAtCaret(tagsIn, t);
    const custom = h('input', { type: 'text', placeholder: '[custom tag]', title: 'Type any [tag] and press Enter to put it in the box', style: { width: '140px' } });
    custom.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' || !custom.value.trim()) return;
      e.preventDefault();
      let t = custom.value.trim();
      if (!t.startsWith('[')) t = `[${t.replace(/[\[\]]/g, '')}]`;
      custom.value = ''; put(t);
    });
    tagRow = h('div.row.wrap', { style: { gap: '6px' } },
      voiceTags.map((t) => { const c = h('span.chip.tag-chip', { title: 'Put this tag in the box' }, t); c.addEventListener('mousedown', (e) => e.preventDefault()); c.addEventListener('click', () => put(t)); return c; }),
      custom);
  }
  const ok = await modal(h('span.row', modeSel, h('span', 'a tag to every line shown')), [
    tagRow,
    h('div.col', h('div.field-label', 'Tags'), tagsIn), count,
    h('div.tag-opts', o1.el, o2.el, o3.el, o4.el, o5.el),
  ], [{ label: 'Cancel', value: false }, { label: 'OK', primary: true, value: true }], { wide: true });
  if (!ok) return;
  const tags = (tagsIn.value.match(/\[[^\]]+\]|[^\s\[\],]+/g) || []);
  if (!tags.length) { toast('Type a tag first, e.g. [whispers].', true); return; }
  const ids = matches().map(idOf);
  if (!ids.length) { toast('No line in the current filter matches.', true); return; }
  apply(tags, mode, ids, mode === 'add' ? (o5.cb.checked ? 'replace' : 'after') : '');
}

/**
 * "Find and replace in all shown…": the classic find / replace over the texts in the current filter.
 * pool: the rows shown; idOf(row); textOf(row): the text that is searched and changed; what: how that text is called;
 * tags: [tag] chips that can be put into either box (omit where tags aren't allowed); apply(items [{k, v}]) saves the changes.
 */
export async function findReplaceDialog({ pool, idOf, textOf, what = 'spoken text', tags = null, apply }) {
  const find = h('textarea.fr-box', { rows: 2, placeholder: 'Text to find', spellcheck: false });
  const repl = h('textarea.fr-box', { rows: 2, placeholder: 'Replace with (leave empty to delete what is found)', spellcheck: false });
  let last = find;
  find.addEventListener('focus', () => { last = find; });
  repl.addEventListener('focus', () => { last = repl; });
  let matchCase = false, whole = false;
  const opt = (label, tip, get, set) => { const c = h('input', { type: 'checkbox' }); c.checked = get(); c.addEventListener('change', () => { set(c.checked); update(); }); return h('label.row', { title: tip, style: { gap: '6px' } }, c, label); };
  const summary = h('div.fr-sum');
  const preview = h('div.fr-preview');
  const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const rx = () => {
    if (!find.value) return null;
    const src = esc(find.value);
    return new RegExp(whole ? `(?<![\\p{L}\\p{N}_])${src}(?![\\p{L}\\p{N}_])` : src, 'gu' + (matchCase ? '' : 'i'));
  };
  let changes = [];
  const mark = (txt, re, cls) => {
    const out = []; let i = 0;
    for (const m of txt.matchAll(re)) { out.push(txt.slice(i, m.index), h('mark.' + cls, m[0] || '·')); i = m.index + m[0].length; }
    out.push(txt.slice(i)); return out;
  };
  function update() {
    const re = rx();
    changes = []; let hits = 0;
    clear(preview);
    if (re) for (const r of pool) {
      const t = textOf(r) || '';
      const n = [...t.matchAll(re)].length;
      if (!n) continue;
      hits += n;
      const v = t.replace(re, () => repl.value);
      if (v !== t) changes.push({ k: idOf(r), v, before: t });
    }
    summary.textContent = !re ? `Searches the ${what} of the ${pool.length.toLocaleString()} row(s) shown.`
      : hits ? `${hits.toLocaleString()} match(es) in ${changes.length.toLocaleString()} of ${pool.length.toLocaleString()} row(s) shown.` : 'No matches in the rows shown.';
    summary.className = 'fr-sum' + (re && !hits ? ' dim' : '');
    if (re) {
      const reR = repl.value ? new RegExp(esc(repl.value), 'g') : null;
      for (const c of changes.slice(0, 6)) {
        preview.append(h('div.fr-item', h('div.fr-before', mark(c.before, rx(), 'del')), h('div.fr-after', reR ? mark(c.v, reR, 'ins') : c.v)));
      }
      if (changes.length > 6) preview.append(h('div.faint', `…and ${changes.length - 6} more.`));
    }
    okBtn && (okBtn.disabled = !changes.length, okBtn.textContent = changes.length ? `Replace in ${changes.length.toLocaleString()} row(s)` : 'Replace');
  }
  find.addEventListener('input', update); repl.addEventListener('input', update);
  const insert = (t) => { insertTagAtCaret(last, t); update(); };
  let tagRow = null;
  if (tags) {
    const custom = h('input', { type: 'text', placeholder: '[custom tag]', title: 'Type any [tag] and press Enter to put it in the box you were typing in', style: { width: '140px' } });
    custom.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' || !custom.value.trim()) return;
      e.preventDefault();
      let t = custom.value.trim();
      if (!t.startsWith('[')) t = `[${t.replace(/[\[\]]/g, '')}]`;
      custom.value = ''; insert(t);
    });
    tagRow = h('div.row.wrap', { style: { gap: '6px' } }, h('span.faint', 'Tags:'),
      tags.map((t) => { const c = h('span.chip.tag-chip', { title: 'Put this tag in the box you were typing in, at the cursor' }, t); c.addEventListener('mousedown', (e) => e.preventDefault()); c.addEventListener('click', () => insert(t)); return c; }),
      custom);
  }
  let okBtn = null;
  const body = [
    h('div.col', { style: { gap: '4px' } }, h('div.field-label', 'Find'), find),
    h('div.col', { style: { gap: '4px' } }, h('div.field-label', 'Replace with'), repl),
    tagRow,
    h('div.row', { style: { gap: '18px' } }, opt('Match case', 'Only find text with the same capital letters', () => matchCase, (v) => { matchCase = v; }),
      opt('Whole words only', 'Skip matches inside a longer word', () => whole, (v) => { whole = v; })),
    summary, preview,
  ];
  const p = modal('Find and replace in all shown', body, [{ label: 'Cancel', value: false }, { label: 'Replace', primary: true, value: true }]);
  okBtn = document.querySelector('#modal .modal-card .btn.primary');
  update();
  if (!(await p) || !changes.length) return;
  await apply(changes.map(({ k, v }) => ({ k, v })));
  toast(`Replaced in ${changes.length.toLocaleString()} row(s).`);
}
