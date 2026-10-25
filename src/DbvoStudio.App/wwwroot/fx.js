// The effect picker for takes (Phase 2, NPC Revoice): reverbs, filters and voice effects, put on every take of the line(s)
// and on new takes as they are made. The dry originals are kept, so None takes it off again.
import { rpc, h } from './lib.js';

let cache = null;
export async function effectsList() {
  if (!cache) cache = rpc('effects.list').catch(() => { cache = null; return []; });
  return cache;
}

/**
 * A row with the effect select. ticked = how many lines a change applies to (1: this line). onPick(effectId) does the work.
 */
export function effectRow(current, ticked, onPick) {
  const sel = h('select', { title: 'An effect on every take of the line (and new takes): reverb for thoughts and narration, a radio, a robot, a demon… The dry takes are kept, so None takes it off.', style: { maxWidth: '230px' } }, h('option', { value: '' }, 'None'));
  effectsList().then((list) => {
    const groups = {};
    for (const fx of list) (groups[fx.group] = groups[fx.group] || []).push(fx);
    for (const [g, items] of Object.entries(groups)) sel.append(h('optgroup', { label: g }, items.map((fx) => h('option', { value: fx.id, selected: fx.id === current, title: fx.description }, fx.name))));
    sel.value = current || '';
  });
  sel.addEventListener('change', () => onPick(sel.value));
  return h('div.row', { style: { gap: '8px', alignItems: 'center' } }, h('span.field-label', { style: { margin: 0 } }, 'Effect'), sel,
    ticked > 1 ? h('span.faint', `applies to the ${ticked} ticked lines`) : current ? h('span.faint', 'on every take') : null);
}
