// The effect picker for takes (Phase 2, NPC Revoice): reverbs, filters and voice effects, put on every take of the line(s)
// and on new takes as they are made. The dry originals are kept, so None takes it off again.
import { rpc, h, popupMenu, modal, segmented } from './lib.js';

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
export function effectRow(current, ticked, onPick, title, { compact = false } = {}) {
  const sel = h('select', { title: title || 'An effect on every take of the line (and new takes): reverb for thoughts and narration, a radio, telepathy, a ghost… The dry takes are kept, so No effect takes it off.', style: compact ? {} : { maxWidth: '200px' } }, h('option', { value: '' }, 'No effect'));
  effectsList().then((list) => {
    const groups = {};
    for (const fx of list.filter((x) => !x.isEmpty)) (groups[fx.group] = groups[fx.group] || []).push(fx);
    for (const [g, items] of Object.entries(groups)) sel.append(h('optgroup', { label: g }, items.map((fx) => h('option', { value: fx.id, selected: fx.id === current, title: fx.description }, fx.name))));
    sel.value = current || '';
  });
  sel.addEventListener('change', () => onPick(sel.value));
  if (compact) return sel; // a toolbar that has no room for the label (the options say what it is)
  return h('div.row', { style: { gap: '8px', alignItems: 'center', flexWrap: 'nowrap' } }, h('span.field-label', { style: { margin: 0 } }, 'Effect'), sel,
    ticked > 1 ? h('span.faint', `applies to the ${ticked} ticked lines`) : null);
}

/**
 * "Post-processing ▾": Effects (on the ticked lines) and Soften peaks (on the ticked lines or on all shown).
 * o: { ticked, shown, applyFx(fx), soften(mode, which) } where which is 'ticked' | 'shown' and mode '' | 'standard' | 'strong'.
 */
export function postButton({ ticked: tf, shown: sf, applyFx, soften, noise }) {
  const b = h('button.btn.sm', { title: 'Noise-reduced pauses, effects and Soften peaks for the takes of the ticked lines, or of all lines shown' }, 'Post-processing ▾');
  b.addEventListener('click', () => { const ticked = tf(), shown = sf(); popupMenu(b, [
    { label: 'Effects…', sub: ticked ? `Reverb, filter or voice on the takes of the ${ticked} ticked line(s)` : 'Tick the lines to put an effect on first', disabled: !ticked, onclick: async () => {
      const list = (await effectsList()).filter((x) => !x.isEmpty);
      popupMenu(b, [...list.map((fx) => ({ label: fx.name, sub: `${fx.group} · ${fx.description}`, onclick: () => applyFx(fx.id) })),
        { label: 'No effect', sub: 'Take the effect off: the dry takes come back', onclick: () => applyFx('') }]);
    } },
    { label: 'Soften peaks…', sub: 'Tame hard consonants (t, d, g, k) that distort at a high in-game gain: ticked lines or all shown', onclick: () => softenDialog({ ticked, shown, soften }) },
    noise ? { label: 'Noise-reduced pauses…', sub: 'Remove low rumble and turn down noise in pauses: ticked lines or all shown', onclick: () => noiseDialog({ ticked, shown, noise }) } : null,
  ].filter(Boolean)); });
  return b;
}

/** Which lines and how strong; then soften(mode, which). */
export async function softenDialog({ ticked, shown, soften }) {
  let which = ticked ? 'ticked' : 'shown', mode = 'standard';
  const warn = h('div.banner.warn', { style: { margin: 0 } });
  const renderWarn = () => {
    const n = which === 'ticked' ? ticked : shown;
    warn.textContent = mode === '' ? `Takes Soften peaks off the takes of ${n.toLocaleString()} line(s): their originals come back.`
      : `Changes the WAV of every take of ${n.toLocaleString()} line(s)${n > 200 ? ', which can take a while' : ''}. The originals are kept: Soften peaks → Off puts them back. Runs on your PC, no credits. New takes of these lines get it too; export again afterwards.`;
  };
  renderWarn();
  const ok = await modal('Soften peaks', [
    h('div', 'Turns down only the sharp peaks of hard consonants, over a few milliseconds, then matches the line back to its own loudness. Timing doesn\'t change, so the LIP files stay as they are.'),
    h('div.row', { style: { gap: '10px', alignItems: 'center' } }, h('span.faint', 'Lines'),
      segmented([['ticked', `Ticked (${ticked})`], ['shown', `All shown (${shown.toLocaleString()})`]], which, (v) => { which = v; renderWarn(); }, { disabled: false })),
    h('div.row', { style: { gap: '10px', alignItems: 'center' } }, h('span.faint', 'Strength'),
      segmented([['standard', 'Standard', 'Peaks limited to 8 dB above the speech level (recommended)'], ['strong', 'Strong', 'Peaks limited to 6 dB above the speech level, for lines Standard doesn\'t fix'], ['', 'Off', 'Take Soften peaks off: the original takes come back']], mode, (v) => { mode = v; renderWarn(); })),
    warn,
  ], [{ label: 'Cancel', value: false }, { label: 'Apply', primary: true, value: true }]);
  if (!ok) return;
  if (which === 'ticked' && !ticked) return;
  await soften(mode, which);
}

/** Which lines, on or off; then noise(on, which). */
export async function noiseDialog({ ticked, shown, noise }) {
  let which = ticked ? 'ticked' : 'shown', on = true;
  const warn = h('div.banner.warn', { style: { margin: 0 } });
  const renderWarn = () => {
    const n = which === 'ticked' ? ticked : shown;
    warn.textContent = !on ? `Takes Noise-reduced pauses off the takes of ${n.toLocaleString()} line(s): their originals come back.`
      : `Changes the WAV of every take of ${n.toLocaleString()} line(s)${n > 200 ? ', which can take a while' : ''}. The originals are kept: Off puts them back. Runs on your PC, no credits. New takes of these lines get it too; export again afterwards.`;
  };
  renderWarn();
  const ok = await modal('Noise-reduced pauses', [
    h('div', 'Removes rumble below 80 Hz and turns down noise in pauses (30 dB or more below the voice) by up to 15 dB, never to silence. It opens just before the voice and holds after it, so words, breaths and fading endings stay as they are. Timing doesn\'t change, so the LIP files stay as they are.'),
    h('div.row', { style: { gap: '10px', alignItems: 'center' } }, h('span.faint', 'Lines'),
      segmented([['ticked', `Ticked (${ticked})`], ['shown', `All shown (${shown.toLocaleString()})`]], which, (v) => { which = v; renderWarn(); })),
    h('div.row', { style: { gap: '10px', alignItems: 'center' } }, h('span.faint', 'Noise-reduced pauses'),
      segmented([['on', 'On'], ['off', 'Off', 'Take it off: the original takes come back']], 'on', (v) => { on = v === 'on'; renderWarn(); })),
    warn,
  ], [{ label: 'Cancel', value: false }, { label: 'Apply', primary: true, value: true }]);
  if (!ok) return;
  if (which === 'ticked' && !ticked) return;
  await noise(on, which);
}

/** The small N on a line with Noise-reduced pauses: click takes it off. */
export function noiseIcon(on, onRemove) {
  if (!on) return null;
  return h('button.fx-e.fx-s', { title: 'Noise-reduced pauses on every take. Click to take it off.', onclick: (e) => { e.stopPropagation(); onRemove(); } }, 'N');
}

/** The inspector's Noise-reduced pauses row for a line (or the ticked lines when it is one of them). */
export function noiseRow(on, ticked, onPick) {
  return h('div.row', { style: { gap: '8px', alignItems: 'center', flexWrap: 'nowrap' }, title: 'Noise-reduced pauses on every take of the line (and new takes): low rumble removed and noise in pauses turned down. The originals are kept, so Off puts them back.' },
    h('span.field-label', { style: { margin: 0 } }, 'Noise-reduced pauses'),
    segmented([['off', 'Off'], ['on', 'On']], on ? 'on' : 'off', (v) => onPick(v === 'on')),
    ticked > 1 ? h('span.faint', `applies to the ${ticked} ticked lines`) : null);
}

/** The small S on a line with Soften peaks: click takes it off. */
export function softenIcon(mode, onRemove) {
  if (!mode) return null;
  return h('button.fx-e.fx-s', { title: `Soften peaks${mode === 'strong' ? ' (strong)' : ''} on every take. Click to take it off.`, onclick: (e) => { e.stopPropagation(); onRemove(); } }, 'S');
}

/** The inspector's Soften peaks row for a line (or the ticked lines when it is one of them). */
export function softenRow(current, ticked, onPick) {
  return h('div.row', { style: { gap: '8px', alignItems: 'center', flexWrap: 'nowrap' }, title: 'Soften peaks on every take of the line (and new takes): hard consonants turned down so they don\'t distort at a high in-game gain. The originals are kept, so Off puts them back.' },
    h('span.field-label', { style: { margin: 0 } }, 'Soften peaks'),
    segmented([['', 'Off'], ['standard', 'Standard'], ['strong', 'Strong']], current || '', onPick),
    ticked > 1 ? h('span.faint', `applies to the ${ticked} ticked lines`) : null);
}
