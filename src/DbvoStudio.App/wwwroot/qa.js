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
  const o = { model: 'large-v3-turbo', language: 'en', quick: true, words: true, levels: true, onset: true, onsetSensitivity: 'normal', peaks: true, peaksDb: 14, ...store.get('qaOpts', {}) };
  o.onlyUnchecked = unchecked < total && unchecked > 0;
  const count = h('b');
  const upd = () => { const n = o.onlyUnchecked ? unchecked : total; count.textContent = `${n.toLocaleString()} line(s)`; okBtn && (okBtn.textContent = `Check ${n.toLocaleString()} line(s)`, okBtn.disabled = !n || (!o.words && !o.levels && !o.onset && !o.peaks)); };
  // Compact: two columns of small cards, the explanations in shorter words (hover a card for the full one).
  const card = (label, help, tip, control) => h('div.qa-opt', { title: tip || '' }, h('div.qa-opt-head', h('b', label), control), help ? h('small', help) : null);
  const modelSel = h('select', MODELS.map(([v, l, t]) => h('option', { value: v, selected: v === o.model, title: t }, l)));
  modelSel.addEventListener('change', () => { o.model = modelSel.value; });
  let okBtn = null;
  const body = [
    h('div.faint', `Flags takes whose words differ from the text (under 75 % match), that are very quiet or clip, whose hard consonants spike far above the voice, or that open with a stray burst. Nothing is changed: use the "QA flagged" filter or sort the QA column to listen and decide.`),
    h('div.row', { style: { gap: '12px', alignItems: 'center', flexWrap: 'wrap' } }, h('span', 'Lines: ', count, ticked ? h('span.faint', ' (ticked)') : null),
      unchecked < total ? segmented([['new', `Only unchecked (${unchecked.toLocaleString()})`], ['all', `All (${total.toLocaleString()})`]], o.onlyUnchecked ? 'new' : 'all', (v) => { o.onlyUnchecked = v === 'new'; upd(); }) : null),
    h('div.qa-grid',
      card('Words', 'Whisper transcribes each take', 'Off: only loudness, clipping and the start of each take, which is very fast.', switchEl(o.words, (v) => { o.words = v; upd(); })),
      card('Model', 'Lighter = faster', 'Checking only needs a rough transcript, so a lighter model than WAV to LIP\'s is much faster. The first use downloads it once.', modelSel),
      card('Language', 'English skips detection', 'English skips language detection on every take.', segmented([['en', 'English'], ['auto', 'Detect']], o.language, (v) => { o.language = v; })),
      card('Quick decoding', 'One pass per take', 'One pass per take, no timestamps: several times faster on short lines. Off: the careful decoding WAV to LIP uses.', switchEl(o.quick, (v) => { o.quick = v; })),
      card('Loudness and clipping', 'Under −30 LUFS or over −0.5 dBTP', 'Flags takes quieter than −30 LUFS or peaking above −0.5 dBTP.', switchEl(o.levels, (v) => { o.levels = v; upd(); })),
      card('Sharp peaks', 'Consonants far above the voice', 'Flags takes whose loudest peak (often a hard t, d, g or k) stands far above the speech level. They don\'t clip in the file, but distort at a high in-game gain. Fix them with Post-processing ▾ → Soften peaks. Very fast.',
        switchEl(o.peaks, (v) => { o.peaks = v; upd(); })),
      card('Start of each take', 'The v4 burst before the line', 'Flags the stray burst some ElevenLabs v4 takes open with: a short loud sound right at the start, then silence, then the line. Only flags it; the audio is never changed. Very fast.',
        switchEl(o.onset, (v) => { o.onset = v; upd(); }))),
    h('div.row', { style: { gap: '10px', alignItems: 'center' } }, h('span.faint', 'Start-of-take sensitivity'),
      segmented([['strict', 'Strict', 'Shorter pauses and quieter bursts count too (more takes flagged)'], ['normal', 'Normal', 'A burst up to 4 dB quieter than the line, then at least 80 ms of silence'], ['relaxed', 'Relaxed', 'Only a burst at least as loud as the line, then 120 ms of silence']], o.onsetSensitivity, (v) => { o.onsetSensitivity = v; })),
    h('div.row', { style: { gap: '10px', alignItems: 'center' } }, h('span.faint', 'Sharp peaks: flag a peak more than'),
      segmented([[12, '12 dB', 'Strict: more takes flagged'], [14, '14 dB', 'Normal'], [16, '16 dB', 'Relaxed: only the worst']], o.peaksDb, (v) => { o.peaksDb = Number(v); }), h('span.faint', 'above the speech level')),
    h('div.faint', { style: { fontSize: '12px' } }, 'Whisper runs on the device set in the WAV to LIP tool. The top bar counts the lines checked; hover it to see the device and takes per second.'),
  ];
  const p = modal('Check takes', body, [{ label: 'Cancel', value: false }, { label: 'Check', primary: true, value: true }], { help: 'checking-takes' });
  okBtn = document.querySelector('#modal .modal-card .btn.primary');
  upd();
  if (!(await p)) return null;
  const out = { model: o.model, language: o.language, quick: o.quick, words: o.words, levels: o.levels, onset: o.onset, onsetSensitivity: o.onsetSensitivity, peaks: o.peaks, peaksDb: o.peaksDb };
  store.set('qaOpts', out);
  return { ...out, onlyUnchecked: o.onlyUnchecked };
}
