// The "Check takes" dialog shared by Phase 2 and NPC Revoice: what to check, which Whisper model, which takes.
import { h, modal, store, segmented, switchEl } from './lib.js';

const MODELS = [
  ['large-v3-turbo', 'large-v3 turbo', 'Fast and nearly as accurate as large-v3 (recommended)'],
  ['small.en', 'small.en', 'English only, faster still; fine for spotting wrong or cut-off lines'],
  ['base.en', 'base.en', 'English only, fastest; rougher transcripts'],
  ['large-v3', 'large-v3', 'The WAV to LIP model: most accurate, slowest'],
];

/** Shows the options; resolves to the request fields for patch.qa / revoice.qa, or null when cancelled. */
export async function qaDialog({ total, unchecked, ticked }) {
  const o = { model: 'large-v3-turbo', language: 'en', quick: true, words: true, levels: true, ...store.get('qaOpts', {}) };
  o.onlyUnchecked = unchecked < total && unchecked > 0;
  const count = h('b');
  const upd = () => { const n = o.onlyUnchecked ? unchecked : total; count.textContent = `${n.toLocaleString()} line(s)`; okBtn && (okBtn.textContent = `Check ${n.toLocaleString()} line(s)`, okBtn.disabled = !n || (!o.words && !o.levels)); };
  const row = (label, help, control) => h('div.srow', h('div.lbl', h('span', label), help ? h('small', help) : null), h('div', control));
  const modelSel = h('select', MODELS.map(([v, l, t]) => h('option', { value: v, selected: v === o.model, title: t }, l)));
  modelSel.addEventListener('change', () => { o.model = modelSel.value; });
  let okBtn = null;
  const body = [
    h('div.help', h('p', `Whisper listens to the take in use of each line (${ticked ? 'the ticked lines' : 'every line shown'}) and compares what it hears with the text. Loudness and peak are measured in the same pass. A take is flagged when the words match less than 75 %, when it is very quiet, or when it clips. Nothing is deleted or regenerated: use the "QA flagged" filter to listen and decide.`)),
    h('div', 'Lines to check: ', count),
    unchecked < total ? row('Which takes', `${(total - unchecked).toLocaleString()} were checked before.`,
      segmented([['new', `Only unchecked (${unchecked.toLocaleString()})`], ['all', `All (${total.toLocaleString()})`]], o.onlyUnchecked ? 'new' : 'all', (v) => { o.onlyUnchecked = v === 'new'; upd(); })) : null,
    row('Check the words', 'Whisper transcribes each take. Off: only loudness and clipping, which is very fast.', switchEl(o.words, (v) => { o.words = v; upd(); })),
    row('Whisper model', 'Checking only needs a rough transcript, so a lighter model than WAV to LIP\'s is much faster. The first use downloads it once.', modelSel),
    row('Language', 'English skips language detection on every take.', segmented([['en', 'English'], ['auto', 'Detect']], o.language, (v) => { o.language = v; })),
    row('Quick decoding', 'One pass per take, no timestamps: several times faster on short lines. Off: the careful decoding WAV to LIP uses.', switchEl(o.quick, (v) => { o.quick = v; })),
    row('Check loudness and clipping', 'Flags takes quieter than −30 LUFS or peaking above −0.5 dBTP.', switchEl(o.levels, (v) => { o.levels = v; upd(); })),
    h('p.faint', 'Uses the device and engine set in the WAV to LIP tool (GPU or CPU). The progress bar shows where Whisper runs and how many takes per second it checks.'),
  ];
  const p = modal('Check takes', body, [{ label: 'Cancel', value: false }, { label: 'Check', primary: true, value: true }], { help: 'checking-takes' });
  okBtn = document.querySelector('#modal .modal-card .btn.primary');
  upd();
  if (!(await p)) return null;
  store.set('qaOpts', { model: o.model, language: o.language, quick: o.quick, words: o.words, levels: o.levels });
  return { model: o.model, language: o.language, quick: o.quick, words: o.words, levels: o.levels, onlyUnchecked: o.onlyUnchecked };
}
