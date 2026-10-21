// Shared ElevenLabs tag tools for Phase 2 and NPC Revoice: the "Add a [tag] to all shown…" dialog and caret insertion.
import { h, toast, modal, segmented } from './lib.js';

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
export async function bulkTagDialog({ pool, idOf, original, originalLabel, originalNote, originalPlaceholder = '(Intimidate), (Brawl)', apply }) {
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
  const ok = await modal(h('span.row', modeSel, h('span', 'a tag to every line shown')), [
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
