// The effect picker for takes (Phase 2, NPC Revoice): reverbs, filters and voice effects, put on every take of the line(s)
// and on new takes as they are made. The dry originals are kept, so None takes it off again.
import { rpc, h, popupMenu } from './lib.js';

let cache = null;
const names = new Map();
export async function effectsList() {
  if (!cache) cache = rpc('effects.list').then((l) => { for (const x of l) names.set(x.id, x.name); return l; }).catch(() => { cache = null; return []; });
  return cache;
}
effectsList();
/** The effect's name (once the list is loaded), for row icons. */
export const effectName = (id) => names.get(id) || id;

/** The bottom bar's "Effects ▾": an effect on the takes of the ticked lines (or off). */
export function effectsButton(ticked, apply) {
  const b = h('button.btn', { title: ticked ? `Put an effect on the takes of the ${ticked} ticked line(s), and on their new takes` : 'Tick the lines to put an effect on first', disabled: !ticked }, 'Effects ▾');
  b.addEventListener('click', async () => {
    const list = (await effectsList()).filter((x) => !x.isEmpty);
    popupMenu(b, [...list.map((fx) => ({ label: fx.name, sub: `${fx.group} · ${fx.description}`, onclick: () => apply(fx.id) })),
      { label: 'No effect', sub: 'Take the effect off: the dry takes come back', onclick: () => apply('') }]);
  });
  return b;
}

/** The small E on a line with an effect: click takes it off. */
export function effectIcon(id, onRemove) {
  if (!id) return null;
  return h('button.fx-e', { title: `Effect: ${effectName(id)} (on every take). Click to take it off.`, onclick: (e) => { e.stopPropagation(); onRemove(); } }, 'E');
}

/**
 * A row with the effect select. ticked = how many lines a change applies to (1: this line). onPick(effectId) does the work.
 */
export function effectRow(current, ticked, onPick, title) {
  const sel = h('select', { title: title || 'An effect on every take of the line (and new takes): reverb for thoughts and narration, a radio, telepathy, a ghost… The dry takes are kept, so No effect takes it off.', style: { maxWidth: '200px' } }, h('option', { value: '' }, 'No effect'));
  effectsList().then((list) => {
    const groups = {};
    for (const fx of list.filter((x) => !x.isEmpty)) (groups[fx.group] = groups[fx.group] || []).push(fx);
    for (const [g, items] of Object.entries(groups)) sel.append(h('optgroup', { label: g }, items.map((fx) => h('option', { value: fx.id, selected: fx.id === current, title: fx.description }, fx.name))));
    sel.value = current || '';
  });
  sel.addEventListener('change', () => onPick(sel.value));
  return h('div.row', { style: { gap: '8px', alignItems: 'center', flexWrap: 'nowrap' } }, h('span.field-label', { style: { margin: 0 } }, 'Effect'), sel,
    ticked > 1 ? h('span.faint', `applies to the ${ticked} ticked lines`) : null);
}
