// Phase 2: DBVO Patches (voice pack x mods) and Voice Changer patches — generation grid, takes, export.
import { flashRow, TOOL_TIPS, popover, rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, debounce, diffAdded, aiReady, autotagSummary, switchEl, segmented, popupMenu, columnsMenu, tip, icon, ringIcon, mergeInto, askFixOverwrite, needInfo, helpButton, generateButton, outputFormats, withBusy } from './lib.js';
import { app, go, refreshState } from './app.js';
import { bulkTagDialog as sharedBulkTagDialog, insertTagAtCaret, findReplaceDialog } from './tagtools.js';
import { recordTake } from './recorder.js';
import { effectRow, effectsButton, effectIcon } from './fx.js';
import { qaDialog } from './qa.js';

const STATUS_TEXT = { Pending: 'Not generated', Generating: 'Generating…', Generated: 'Generated', Failed: 'Failed', Omitted: 'Omitted', Stale: 'Edited since take' };

export const LANGS = [['', 'Auto'], ['en', 'en · English'], ['de', 'de · German'], ['fr', 'fr · French'], ['es', 'es · Spanish'], ['it', 'it · Italian'],
  ['pt', 'pt · Portuguese'], ['pl', 'pl · Polish'], ['ru', 'ru · Russian'], ['ja', 'ja · Japanese'], ['zh', 'zh · Chinese'], ['ko', 'ko · Korean'],
  ['nl', 'nl · Dutch'], ['sv', 'sv · Swedish'], ['cs', 'cs · Czech'], ['tr', 'tr · Turkish'], ['uk', 'uk · Ukrainian'], ['hu', 'hu · Hungarian']];

const ENGINES = [['elevenlabs', 'ElevenLabs'], ['voxcpm', 'VoxCPM (local)'], ['omnivoice', 'OmniVoice (local)']];

const pct = (p) => { const n = p.total - p.omitted - (p.excluded || 0); return n ? Math.round((100 * p.generated) / n) : 0; };

/** Ten voice pack colours that sit well on the dark blue-grey theme. */
export const VOICE_COLORS = [
  ['#58a6ff', 'Blue'], ['#56d4dd', 'Sky'], ['#39c5cf', 'Teal'], ['#3fb950', 'Green'], ['#a5d64c', 'Lime'],
  ['#d29922', 'Amber'], ['#f0883e', 'Orange'], ['#f85149', 'Red'], ['#ff7b9c', 'Rose'], ['#8b949e', 'Slate'],
];

let creditsCache = null; // { at, data }
// Voices and models per engine, kept across pages so the toolbar draws complete at once.
const engineCache = new Map(); // engine -> { voices, models, localVoices }
async function loadCredits(force = false) {
  if (!force && creditsCache && Date.now() - creditsCache.at < 60000) return creditsCache.data;
  const data = await rpc('eleven.subscriptionInfo');
  creditsCache = { at: Date.now(), data };
  return data;
}

export function voiceChip(name, color) {
  const c = h('span.voice-chip', { title: 'Voice pack' }, name || '—');
  if (color) c.style.setProperty('--vc', color);
  return c;
}

// ------------------------------------------------------------------ list (DBVO Patches, or Voice Changer patches)

// ---- ElevenLabs credits (cached for a minute across pages)
/** The ElevenLabs credits button (ring + popover). needs(): characters the current page still needs, label: what needs them. */
export function creditsButton(needs, label = 'This patch') {
  const ring = h('span.row', ringIcon(1, { size: 15 }));
  const btn = h('button.btn.sm.credits-btn', { title: 'ElevenLabs credits left this period (hover for details)' }, ring, h('b', '…'), h('span.faint', 'credits'));
  let data = null, err = null, pop = null, hideT = 0;
  const fill = (c) => {
    data = c;
    const left = Math.max(0, c.characterLimit - c.characterCount);
    btn.children[1].textContent = left.toLocaleString();
    clear(ring).append(ringIcon(c.characterLimit ? left / c.characterLimit : 0, { size: 15 }));
  };
  const body = () => {
    if (err) return [h('div', 'Could not read the credits'), h('div.faint', 'Check the API key in Global Settings.')];
    if (!data) return [h('div.faint', 'Loading…')];
    const c = data;
    const left = Math.max(0, c.characterLimit - c.characterCount);
    const pct = c.characterLimit ? Math.round((100 * left) / c.characterLimit) : 0;
    const pend = needs();
    return [
      h('div.row', ringIcon(c.characterLimit ? left / c.characterLimit : 0, { size: 34 }), h('div', h('div', h('b', left.toLocaleString()), ` credits left (${pct}%)`),
        h('div.faint', `${c.characterCount.toLocaleString()} of ${c.characterLimit.toLocaleString()} used · ${c.tier}`))),
      c.nextReset ? h('div.faint', 'Resets ' + new Date(c.nextReset * 1000).toLocaleString()) : null,
      h('div', `${label} still needs ≈ ${pend.toLocaleString()}`, h('div.faint', pend > left ? 'More than you have left this period' : 'Fits in what you have left')),
      h('div.row', h('div.spacer'), h('button.btn.sm.ghost', { title: 'Ask ElevenLabs again for the credits left', onclick: async () => { creditsCache = null; try { fill(await loadCredits(true)); err = null; } catch (e) { err = e; } if (pop) clear(pop).append(...body()); } }, '⟳ Refresh')),
    ];
  };
  const close = () => { clearTimeout(hideT); hideT = setTimeout(() => { if (pop) { pop.remove(); pop = null; } }, 180); };
  const open = () => {
    clearTimeout(hideT);
    if (pop) return;
    const r = btn.getBoundingClientRect();
    pop = h('div.credits-pop', { style: { top: `${r.bottom + 6}px`, right: `${Math.max(8, window.innerWidth - r.right)}px` } }, ...body());
    pop.addEventListener('mouseenter', () => clearTimeout(hideT));
    pop.addEventListener('mouseleave', close);
    document.body.append(pop);
  };
  btn.addEventListener('mouseenter', open);
  btn.addEventListener('mouseleave', close);
  btn.addEventListener('focus', open);
  btn.addEventListener('blur', close);
  loadCredits().then((c) => { fill(c); if (pop) clear(pop).append(...body()); }).catch((e) => { err = e; btn.children[1].textContent = '—'; });
  return btn;
}

export async function patchesView(kind = 'tts') {
  const s = await refreshState();
  const vcPage = kind === 'vc';
  // The Phase 2 page lists every patch (Voice Changer ones carry a badge); the Voice Changer page only those.
  const patches = s.patches.filter((p) => !vcPage || p.kind === 'vc');
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', vcPage ? 'Voice Changer' : 'DBVO Patches'),
    helpButton(vcPage ? 'voice-changer' : 'the-patch-list'),
    h('span.sub', vcPage
      ? 'Other Tools · revoice WAV or .fuz files another voice pack already has (ElevenLabs speech-to-speech). Works like Phase 2, without DBVO Fix plugins or topic files.'
      : 'One voice pack × one or more mods. Drag a patch onto another header to change its category. Two lists side by side, so you can compare voice packs.'),
    h('div.spacer'),
    vcPage ? null : h('button.btn', { onclick: addCategory, title: 'Add a category header (also used for mods and FOMOD pages)' }, '+ Category'),
    h('button.btn.primary', { onclick: () => newPatch(kind), title: vcPage ? 'Revoice another voice pack\'s files with speech-to-speech' : 'A voice pack × one or more Phase 1 mods' }, vcPage ? '+ New Voice Changer patch' : '+ New DBVO Patch')));
  const body = h('div.view-body');
  view.append(body);
  if (!patches.length && vcPage) {
    body.append(h('div.empty', h('h2', 'No Voice Changer patches yet'),
      h('div', 'Pick a Phase 1 mod, the voice pack to make, and the folder with the WAV or .fuz files of another voice pack for the same mod.'),
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { title: 'A patch that converts another voice pack\'s recordings into this voice', onclick: () => newPatch('vc') }, '+ New Voice Changer patch'))));
    return view;
  }
  if (!s.voicePacks.length) {
    body.append(h('div.empty', h('h2', 'Create a voice pack first'), h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { title: 'Create a voice pack in Global Settings → Voice packs', onclick: () => go('settings', { tab: 'voices', id: 'new' }) }, '+ New voice pack'))));
    return view;
  }
  let dragId = null;

  const card = (p) => {
    const n = p.total - p.omitted - (p.excluded || 0);
    const vc = p.kind === 'vc';
    const warn = p.warnings && p.warnings.length;
    // Two compact lines: name + labels, then progress and what is left.
    const el = h('div.prow', { draggable: true, onclick: () => go('patch', { id: p.id }), title: 'Open this patch' },
      h('div.row',
        h('span.title.ellipsis.grow', p.name),
        warn ? h('span.badge.b-toolong', { title: p.warnings.join('\n') }, '⚠ ' + (p.warnings.length === 1 ? p.warnings[0] : p.warnings.length + ' warnings')) : null,
        vc ? h('span.badge.b-player', { title: 'Voice Changer on: lines are converted from another voice pack\'s files (speech-to-speech)' }, 'Voice Changer') : null,
        voiceChip(p.voicePack, p.voiceColor)),
      h('div.prow-stats',
        (p.unreviewed
          ? h('span.badge.b-broken', { title: 'Phase 1: flagged lines (BROKEN LINE / TOO LONG WILL CTD / stage directions) not yet decided' }, `P1 · ${p.unreviewed} to review`)
          : h('span.badge.b-valid', { title: 'Phase 1: all flagged lines reviewed' }, 'P1 ✓')),
        h('div.grow', { title: 'Phase 2: lines with a take (generated, converted or imported from existing files)' },
          h('div.bar' + (n && p.generated >= n ? '.ok' : ''), h('div.bar-fill', { style: { width: pct(p) + '%' } }))),
        h('span.mono', { title: 'Lines with a take / lines to voice' }, `${p.generated} / ${n}`),
        vc ? h('span.left', { title: 'Voice Changer: ElevenLabs bills speech-to-speech by audio length.' + (p.noSource ? ` ${p.noSource} line(s) have no source file; they can be generated with text-to-speech (see the patch).` : '') }, h('b', String(p.secondsLeft || 0)), ' to convert', p.noSource ? ` · ${p.noSource} no source` : '')
          : h('span.left', { title: 'Characters of spoken text still to generate; ElevenLabs bills about one credit per character' }, h('b', (p.charsLeft || 0).toLocaleString()), ' chars left')));
    {
      el.addEventListener('dragstart', (e) => { dragId = p.id; el.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; });
      el.addEventListener('dragend', () => { dragId = null; el.classList.remove('dragging'); });
    }
    return el;
  };

  function pane(key, defaultVoice) {
    const collapsed = store.get('cats-collapsed:' + key, {});
    const voiceFilter = h('select', { title: 'Show the patches of one voice pack. Default shows the empty categories only.' }, h('option', { value: '-' }, 'Default'), h('option', { value: '' }, 'All voice packs'), s.voicePacks.map((v) => h('option', { value: v.id }, v.name)));
    voiceFilter.value = store.get('patches-voice:' + key, defaultVoice);
    const q = h('input', { type: 'text', placeholder: 'Filter patches…', title: 'Filter by patch or voice pack name', style: { width: '220px' } });
    const listEl = h('div');
    const render = () => {
      clear(listEl);
      const f = q.value.toLowerCase();
      const cats = [...s.settings.categories, ...new Set(patches.map((p) => p.category).filter((c) => !s.settings.categories.includes(c)))];
      for (const cat of cats) {
        const none = voiceFilter.value === '-';
        const list = none ? [] : patches.filter((p) => p.category === cat && (!voiceFilter.value || p.voicePackId === voiceFilter.value) && (!f || p.name.toLowerCase().includes(f) || p.voicePack.toLowerCase().includes(f)));
        if (vcPage && !list.length) continue;
        const isOpen = !collapsed[cat];
        const sec = h('div.cat',
          h('div.cat-head', { title: isOpen ? 'Click to fold' : 'Click to unfold', onclick: () => { collapsed[cat] = isOpen; store.set('cats-collapsed:' + key, collapsed); render(); } },
            h('span.caret', isOpen ? '▾' : '▸'), h('h3', cat), h('span.faint', list.length || (none ? '' : 'empty · drop a patch here'))),
          isOpen && list.length ? h('div.rows1', list.map(card)) : null);
        sec.addEventListener('dragover', (e) => { if (dragId) { e.preventDefault(); sec.classList.add('drop-over'); } });
        sec.addEventListener('dragleave', (e) => { if (!sec.contains(e.relatedTarget)) sec.classList.remove('drop-over'); });
        sec.addEventListener('drop', async (e) => {
          e.preventDefault();
          sec.classList.remove('drop-over');
          const p = patches.find((x) => x.id === dragId);
          if (!p || p.category === cat) return;
          try {
            for (const mid of p.modIds) await rpc('mod.setCategory', { id: mid, category: cat });
            go(vcPage ? 'vc' : 'patches');
            toast(`Moved to ${cat}. The mod's category changed too (it is shared by all its patches).`);
          } catch (err) { fail(err); }
        });
        listEl.append(sec);
      }
    };
    q.addEventListener('input', render);
    voiceFilter.addEventListener('change', () => { store.set('patches-voice:' + key, voiceFilter.value); render(); });
    render();
    return h('div.pane', h('div.row', { style: { marginBottom: '12px' } }, q, voiceFilter), listEl);
  }

  const first = s.settings.lastVoicePackId || s.voicePacks[0].id;
  const second = '-';
  body.append(vcPage ? pane('vc', '') : h('div.panes', pane('left', first), pane('right', second)));
  return view;
}

async function addCategory() {
  const r = await prompt('New category', [{ name: 'n', label: 'Name', placeholder: 'e.g. Armor & Clothing' }]);
  if (!r || !r.n.trim()) return;
  try {
    await rpc('categories.save', { categories: [...app.state.settings.categories, r.n.trim()] });
    go('patches');
  } catch (e) { fail(e); }
}

async function newPatch(kind = 'tts') {
  const s = await refreshState();
  const vc = kind === 'vc';
  if (!s.voicePacks.length) { toast('Create a voice pack first, in Global Settings → Voice packs.', true); go('settings', { tab: 'voices', id: 'new' }); return; }
  if (!s.mods.length) { toast('Create a mod in Phase 1 first.', true); go('mods'); return; }
  const name = h('input', { type: 'text', placeholder: vc ? 'leave empty for "<mod> (Voice Changer)"' : 'leave empty for the mod\'s name' });
  const voice = h('select', s.voicePacks.map((v) => h('option', { value: v.id, selected: v.id === s.settings.lastVoicePackId }, v.name)));
  const modBoxes = s.mods.map((m) => ({ m, box: h('input', { type: 'checkbox' }) }));
  const sources = [];
  const srcList = h('div.col');
  const renderSrc = () => clear(srcList).append(sources.length ? sources.map((f, i) => h('div.row', h('span.mono.grow.ellipsis', f), h('button.icon-btn', { title: 'Remove this source folder', onclick: () => { sources.splice(i, 1); renderSrc(); } }, '×'))) : h('div.faint', 'No folder yet.'));
  renderSrc();
  const ok = await modal(vc ? 'New Voice Changer patch' : 'New DBVO Patch', [
    h('div.col', h('div.field-label', vc ? 'Voice pack to make (the target voice)' : 'Voice pack'), voice),
    h('div.col', h('div.field-label', vc ? 'Phase 1 mod(s) the source WAV files belong to' : 'Mods to voice (several = an all-in-one patch)'), h('div.box', { style: { maxHeight: '240px', overflow: 'auto' } },
      modBoxes.map(({ m, box }) => h('label.row', box, m.name, h('span.faint', m.category))))),
    vc ? h('div.col', h('div.field-label', 'Source WAV folder(s) from another voice pack'), srcList,
      h('div.row', h('button.btn.sm', { onclick: async () => { const f = await rpc('pick.folder', { title: 'Folder with the source voice pack\'s WAV files' }); if (f) { sources.push(f); renderSrc(); } } }, '+ Add folder')),
      h('div.faint', 'Searched with sub-folders. Files are matched to lines by name, in both Legacy and DBVO 2.0 naming.')) : null,
    h('div.col', h('div.field-label', 'Name (optional)'), name),
    h('div.faint', 'Folders come from the voice pack\'s defaults; change them per patch under "Folders & options".'),
  ], [{ label: 'Cancel', value: false }, { label: 'Create', primary: true, value: true }]);
  if (!ok) return;
  const modIds = modBoxes.filter((x) => x.box.checked).map((x) => x.m.id);
  if (!modIds.length) { toast('Pick at least one mod.', true); return; }
  try {
    const p = await rpc('patch.create', { name: name.value, voicePackId: voice.value, modIds, kind, sourceFolders: sources });
    go('patch', { id: p.id });
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ workspace

export async function patchView(id) {
  await refreshState();
  let patch;
  try {
    const r = await withBusy(rpc('patch.sync', { id }), 'Loading voice lines', 'Reading the lines, takes and WAV + LIP files…'); // pull in Phase 1 edits automatically
    patch = r.patch;
    if (r.report.adopted) setTimeout(() => toast(`${r.report.adopted} line(s) already had WAV + LIP files in the WAV + LIP folder: added as take #1.`), 300);
  } catch {
    patch = await rpc('patch.get', { id });
  }
  const vc = patch.kind === 'vc';
  let voice = app.state.voicePacks.find((v) => v.id === patch.voicePackId);
  if (!voice) throw new Error('This patch\'s voice pack was deleted.');
  const ui = store.get('patch:' + id, { filter: 'all' });
  let filter = ['differs', 'generated'].includes(ui.filter) ? 'all' : (ui.filter || 'all');
  let search = '';
  let focusedId = null;
  let voices = [], models = [], localVoices = [];
  let creditsEl = null; // one credits button per page, updated in place
  const inFlight = new Set(); // lines a take is being made for: their ↻ spins until the take arrives
  const finishing = new Set(); // in flight when the job ended; their take may still be on its way
  const creditsSlot = h('span.credits-slot');
  { const c = engineCache.get(vc ? 'sts' : voice.engine || 'elevenlabs'); if (c) ({ voices, models, localVoices } = c); }
  let showInsp = store.get('insp2', true);
  let lastEdit = null; // { el, line } — the last spoken-text box the user was in
  const audio = new Audio();
  let review = false; // listen-through review mode
  let reviewAutoplay = store.get('reviewAutoplay', true);
  const engine = () => (vc ? 'sts' : voice.engine || 'elevenlabs');
  // With the Voice Changer on, a line needs a source file, unless text-to-speech for such lines is turned on.
  const canGen = (l) => !vc || l.hasSource || !!patch.ttsForMissing;

  const view = h('div.view.work');
  const head = h('div.view-head');
  const tb = h('div.toolbar');
  const tagbar = h('div.tagbar');
  const banners = h('div');
  const filters = h('div.filters');
  const reviewBar = h('div.review-bar.hidden');
  const split = h('div.split' + (showInsp ? '' : '.no-insp'));
  const left = h('div.left');
  const insp = h('div.inspector');
  split.append(left, insp);
  const actions = h('div.statusbar');
  view.append(head, tb, tagbar, banners, filters, reviewBar, split, actions);

  let index = new Map();
  const reindex = () => { index = new Map(patch.lines.map((l) => [l.lineId, l])); };
  reindex();

  const statusDot = (l) => h('span.dot.' + (l.omitted ? 'Omitted' : l.stale && l.status === 'Generated' ? 'Stale' : l.status),
    { title: l.omitted ? `Omitted — already in ${l.omittedBy}` : (STATUS_TEXT[l.status] || l.status) + (l.lastError ? ': ' + l.lastError : '') });

  const selTake = (l) => l.takes.find((t) => t.id === l.selectedTakeId);
  const qaText = (q) => q ? [q.score != null ? `Words match ${(q.score * 100).toFixed(0)}%` : null, q.heard ? `Heard: "${q.heard}"` : null,
    q.lufs != null ? `Loudness ${q.lufs.toFixed(1)} LUFS` : null, q.truePeak != null ? `Peak ${q.truePeak.toFixed(1)} dBTP` : null, q.onsetMs != null ? `Start: a ${Math.round(q.onsetMs)} ms burst, then ${Math.round(q.onsetGapMs)} ms of silence before the line` : null].filter(Boolean).join('\n') : '';
  const QA_WHY = { words: 'words differ', quiet: 'too quiet', clipping: 'clipping', onset: 'burst at the start', error: 'not checked' };
  // A line is flagged when its take in use, or a kept ★ variant, was flagged by Check takes.
  const flaggedTake = (l) => { const s = selTake(l); return [s, ...l.takes.filter((t) => t.kept && t !== s)].find((t) => t && t.qa && t.qa.flags.length); };
  const qaFlagged = (l) => !!flaggedTake(l);
  function qaBadge(l) {
    const t = flaggedTake(l) || selTake(l);
    if (!t || !t.qa) return null;
    const q = t.qa;
    return q.flags.length
      ? h('span.qa-badge.bad', { title: 'Check takes flagged this take: ' + q.flags.map((f) => QA_WHY[f] || f).join(', ') + '\n' + qaText(q) }, '⚠')
      : h('span.qa-badge.ok', { title: 'Check takes: passed\n' + qaText(q) }, '✓');
  }

  const grid = new VGrid(left, {
    storageKey: vc ? 'vc' : 'phase2',
    rowKey: (l) => l.lineId,
    rowClass: (l) => [l.omitted || !canGen(l) ? 'muted' : '', l.excluded ? 'excluded' : '', l.approved ? 'approved' : ''].filter(Boolean).join(' '),
    onRowClick: (l) => { focusedId = l.lineId; renderInspector(); if (review && reviewAutoplay) play(l); },
    onRowDblClick: (l) => play(l),
    columns: [
      { id: 'sel', title: '', width: 32, resizable: false, render: (l) => grid.checkbox(l) },
      { id: 'excl', title: '', width: 30, resizable: false, render: (l) => h('button.act-btn.excl-btn' + (l.excluded ? '.on.bad' : ''), { title: l.excluded ? 'Excluded - click to clear (won\'t generate/export)' : 'Exclude this line (won\'t generate/export, not counted as missing)', onclick: (e) => { e.stopPropagation(); upd(l, l.excluded ? 'include' : 'exclude'); } }, icon('ban', { size: 14 })) },
      { id: 'orig', title: 'Original mod line', width: 240, render: (l) => h('span.dim', { title: l.originalLine }, l.originalLine) },
      { id: 'file', title: 'Filename', flex: 1, width: 240, render: (l) => h('span.faint.mono', { title: `In-game line: ${l.finalLine}\nFile: ${l.stem}.wav / .lip / .fuz` }, l.editedInPhase1 ? h('span.mark.info', { title: 'Edited in Phase 1' }, 'P1 ') : null, l.stem + '.wav') },
      vc ? { id: 'src', title: 'Source', width: 80, render: (l) => l.hasSource ? h('span.row', h('button.icon-btn', { title: 'Play the source: ' + l.sourceWav, onclick: () => playSource(l) }, '▶'), h('span.badge.b-valid', { title: l.sourceWav }, l.sourceWav.toLowerCase().endsWith('.fuz') ? '.fuz' : '✓')) : h('span.badge.b-broken', { title: `No ${l.stem}.wav or .fuz (Legacy or DBVO 2.0 name) in the source folders. New lines from a mod update have no source until the source pack has them.` }, patch.ttsForMissing ? 'TTS' : 'missing') } : null,
      { id: 'spoken', title: vc ? 'Text for the LIP (and TTS without a source)' : 'Spoken text (tags anywhere)', flex: 1.3, width: 320, render: (l) => spokenCell(l) },
      { id: 'qa', title: 'QA', tip: 'Check takes: ⚠ flagged, ✓ passed, empty = not checked. Click to sort the flagged ones to the top.', width: 44, resizable: false, align: 'center',
        sort: (l) => { const t = flaggedTake(l) || selTake(l); return !t || !t.qa ? 2 : t.qa.flags.length ? 0 : 1; }, render: (l) => qaBadge(l) },
      { id: 'take', title: 'Take', width: 84, resizable: false, render: (l) => h('span.row', { style: { gap: '5px' } }, statusDot(l),
        h('span.take-lbl' + (l.takes.length ? '.click' : ''), { title: l.takes.length ? `${l.takes.length} take(s); #${(l.takes.find((t) => t.id === l.selectedTakeId) || l.takes[0]).slot} is used. Click to play, switch, keep or delete takes.` : 'No take yet', onclick: (e) => { if (l.takes.length) { e.stopPropagation(); takesModal(l); } } },
          l.takes.length ? '#' + ((l.takes.find((t) => t.id === l.selectedTakeId) || l.takes[0]).slot) + (l.takes.length > 1 ? '/' + l.takes.length : '') : '—'),
        effectIcon(l.effect, () => applyFx([l.lineId], ''))) },
      { id: 'act', title: '', width: 176, resizable: false, render: (l) => rowIcons(l) },
    ].filter(Boolean),
  });
  grid.el.addEventListener('selection', () => renderActions());

  function trackCaret(el, l) {
    el.addEventListener('focus', () => { lastEdit = { el, line: l }; });
  }

  function rowIcons(l) {
    const sel = l.takes.find((t) => t.id === l.selectedTakeId);
    const kept = l.takes.filter((t) => t.kept).length;
    const regenTitle = l.excluded ? 'Excluded: clear the 🚫 to generate it' : l.omitted ? 'Omitted: a reference topic file already has this line' : !canGen(l) ? 'No source WAV or .fuz with this line\'s name. Turn on "Generate lines without a source with text-to-speech" in the Voice Changer banner to voice it.' : vc && !l.hasSource ? 'Generate with text-to-speech (no source file)' : 'Regenerate the draft variant (a new take; kept takes stay)';
    const keepTitle = !sel ? 'No take yet' : voice && voice.naming === 'Legacy'
      ? (sel.kept ? 'Kept - click to un-keep. Legacy naming exports one take per line (the one in use).' : 'Keep this take. Legacy naming exports one take per line, so variants need DBVO 2.0 naming.')
      : (sel.kept ? `Kept (${kept} kept) - click to un-keep. Kept takes are exported as DBVO 2.0 variants (2+ → name\\0, 1, …).` : `Keep this take as a variant (${kept} kept). DBVO 2.0 exports every kept take; with none kept, the take in use.`);
    return h('span.row.icon-bar', { style: { gap: '2px' } },
      h('button.act-btn' + (inFlight.has(l.lineId) ? '.spinning' : ''), { title: inFlight.has(l.lineId) ? 'Making a new take…' : regenTitle, disabled: l.excluded || l.omitted || !canGen(l), onmousedown: keepEditing, onclick: (e) => { e.stopPropagation(); regen(l); } }, icon('regen')),
      h('button.act-btn' + (sel && sel.kept ? '.on' : ''), { title: keepTitle, disabled: !sel, onclick: (e) => { e.stopPropagation(); keep(l, sel); } }, icon('star', { filled: !!(sel && sel.kept) })),
      h('button.act-btn', { title: sel ? 'Open this line\'s takes folder' : 'No takes yet', disabled: !l.takes.length, onclick: (e) => { e.stopPropagation(); rpc('patch.openTakeFolder', { id, lineId: l.lineId }).catch(fail); } }, icon('folder')),
      h('button.act-btn.flag-btn' + (l.marked ? '.on.warn' : ''), { title: l.marked ? 'Flagged to revisit - click to clear' : 'Flag to revisit (stays until you clear it; "Flagged lines" generates them)', onclick: (e) => { e.stopPropagation(); upd(l, 'mark', !l.marked); } }, icon('flag')),
      h('button.act-btn', { title: sel ? 'Play (or double-click the row)' : vc ? 'No take yet: convert the line first' : 'No take yet: generate the line first', disabled: !sel, onclick: (e) => { e.stopPropagation(); play(l); } }, icon('play')));
  }

  async function keep(l, t, value) {
    try { replaceLine(await rpc('line2.update', { id, lineId: l.lineId, action: 'keep', value: t.id, kept: value ?? !t.kept })); } catch (e) { fail(e); }
  }

  function spokenCell(l) {
    if (l.tagSuggestion) {
      return h('span.row', { style: { width: '100%' } },
        h('span.grow.ellipsis', { title: l.tagSuggestion }, diffAdded(l.spoken, l.tagSuggestion)),
        h('button.icon-btn', { title: 'Accept tags', onclick: () => upd(l, 'acceptTags') }, '✓'),
        h('button.icon-btn', { title: 'Undo', onclick: () => upd(l, 'rejectTags') }, '✕'));
    }
    const inp = h('input', { type: 'text', value: l.spoken, disabled: l.omitted });
    if (l.differs) { inp.style.color = 'var(--link)'; inp.title = 'Blue: the spoken text differs from the in-game line (your edit). The arrow on the left restores it.'; }
    inp.addEventListener('change', () => { if (inp.value !== (index.get(l.lineId) || l).spoken) upd(l, 'spoken', inp.value); });
    inp.addEventListener('focus', () => { editing = { lineId: l.lineId, el: inp }; if (focusedId !== l.lineId) { focusedId = l.lineId; grid.focus(l.lineId); renderInspector(); } });
    inp.addEventListener('keydown', (e) => {
      e.stopPropagation();
      if (e.key === 'Enter') inp.blur();
      if (e.key === 'Escape') { inp.value = l.spoken; inp.blur(); }
    });
    trackCaret(inp, l);
    if (l.undo && !l.spokenEdited) return h('span.spoken-wrap', h('button.act-btn', { title: 'Undo the restore (bring your edit back)', onclick: () => upd(l, 'undoReset') }, icon('undo', { size: 14 })), inp);
    if (!l.spokenEdited) return inp;
    return h('span.spoken-wrap', h('button.act-btn', { title: 'Restore the in-game line: ' + l.finalLine, onclick: () => upd(l, 'resetSpoken') }, icon('restore', { size: 14 })), inp);
  }

  function play(l, take) {
    const t = take || l.takes.find((x) => x.id === l.selectedTakeId);
    if (!t) return;
    audio.src = patch.mediaBase + encodeURI(t.wav) + '?t=' + Date.now();
    audio.play().catch(fail);
  }

  async function playSource(l) {
    try {
      const url = await rpc('audio.preview', { path: l.sourceWav });
      if (!url) { toast('This line\'s source file isn\'t there anymore: it was moved, renamed or deleted.', true); return; }
      audio.src = url;
      audio.play().catch(fail);
    } catch (e) { fail(e); }
  }

  async function takesModal(l) {
    const list = h('div.col');
    const render = () => {
      const line = index.get(l.lineId) || l;
      clear(list).append(line.takes.slice().reverse().map((t) => h('div.take' + (t.id === line.selectedTakeId ? '.sel' : ''),
        h('button.icon-btn.big', { title: 'Play this take', onclick: () => play(line, t) }, '▶'),
        h('div.grow', { style: { minWidth: 0 } }, h('div', '#' + t.slot + (t.id === line.selectedTakeId ? ' · used for export and .fuz' : '')),
          h('div.meta', `${t.durationSeconds.toFixed(2)} s · ${t.settings.engine || ''} · ${t.settings.modelId || ''} · ${new Date(t.createdUtc).toLocaleString()}`),
          t.spokenText !== line.spoken ? h('div.meta.ellipsis', { title: t.spokenText }, 'Text then: ' + t.spokenText) : null),
        h('button.act-btn' + (t.kept ? '.on' : ''), { title: t.kept ? 'Kept as a variant - click to un-keep' : 'Keep as a variant (DBVO 2.0 exports every kept take)', onclick: async () => { await keep(line, t); render(); } }, icon('star', { filled: t.kept })),
        t.id !== line.selectedTakeId ? h('button.btn.sm', { title: 'Use this take for export and .fuz', onclick: async () => { await upd(line, 'selectTake', t.id); render(); } }, 'Use') : null,
        h('button.icon-btn', { title: 'Delete this take and its files', onclick: async () => { await upd(line, 'deleteTake', t.id); render(); } }, '🗑'))));
    };
    render();
    await modal(`Takes · ${l.finalLine}`, [list,
      h('div.row', h('button.btn.sm', { title: 'Delete every take of this line except the one in use and kept ones', onclick: async () => {
        try { const r = await rpc('patch.purgeTakes', { id, lineIds: [l.lineId] }); patch = r.patch; reindex(); grid.setRows(visible()); render(); toast(`${r.removed} take(s) deleted.`); } catch (e) { fail(e); }
      } }, 'Keep only the used take'),
      h('button.btn.sm', { title: 'Make another take of this line', onclick: () => { document.getElementById('modal').classList.add('hidden'); generate('selected', [l.lineId], { autoplay: true }); } }, vc ? 'Convert again' : 'New take'))], undefined, { help: 'takes' });
  }

  async function purge(ids) {
    const n = ids ? ids.length : patch.lines.length;
    if (!(await confirmBox('Delete unused takes?', `For ${n} line(s), every take except the one in use is deleted with its files. This can't be undone.`, 'Delete', true))) return;
    try {
      const r = await rpc('patch.purgeTakes', { id, lineIds: ids });
      patch = r.patch; reindex(); grid.setRows(visible()); renderInspector();
      toast(`${r.removed} take(s) deleted.`);
    } catch (e) { fail(e); }
  }

  // A click on Generate while the spoken text is being edited: keep the focus (no blur, so the row isn't re-drawn under
  // the click), save the typed text, then generate it. One click is enough.
  let editing = null;
  const keepEditing = (e) => { if (editing && document.activeElement === editing.el) e.preventDefault(); };
  async function regen(l) {
    const ed = editing && editing.lineId === l.lineId && document.activeElement === editing.el ? editing.el : null;
    if (ed) {
      const cur = index.get(l.lineId) || l;
      if (ed.value !== cur.spoken) await upd(cur, 'spoken', ed.value);
      editing = null;
      if (document.activeElement === ed) ed.blur();
    }
    generate('selected', [l.lineId], { autoplay: true });
  }
  // Effect on the takes of the focused line, or of every ticked line when it is one of them.
  async function applyFx(ids, fx) {
    try {
      const r = await withBusy(rpc('takes.effect', { kind: 'patch', id, lineIds: ids, effect: fx }), fx ? 'Adding the effect' : 'Taking the effect off', `${ids.length} line(s)…`);
      patch = r.patch; reindex(); grid.setRows(visible()); renderInspector(); renderActions();
      toast(r.problems.length ? r.problems.slice(0, 2).join(' · ') : `${r.takes} take(s) changed.`, !!r.problems.length);
    } catch (e) { fail(e); renderInspector(); }
  }
  function fxRow(l) {
    const ids = grid.selected.has(l.lineId) && grid.selected.size > 1 ? [...grid.selected] : [l.lineId];
    return effectRow(l.effect || '', ids.length, (fx) => applyFx(ids, fx));
  }
  async function upd(l, action, value) {
    try {
      replaceLine(await rpc('line2.update', { id, lineId: l.lineId, action, value }));
    } catch (e) { fail(e); }
  }

  // done: the line came back from generation (only then does its ↻ stop and the row flash).
  function replaceLine(nl, done = false) {
    const wasInFlight = done && (inFlight.delete(nl.lineId) | finishing.delete(nl.lineId));
    const i = patch.lines.findIndex((x) => x.lineId === nl.lineId);
    if (i >= 0) patch.lines[i] = nl;
    index.set(nl.lineId, nl);
    const ri = grid.rows.findIndex((x) => x.lineId === nl.lineId);
    if (ri >= 0) grid.rows[ri] = nl;
    if (lastEdit && lastEdit.line.lineId === nl.lineId) lastEdit.line = nl;
    grid.refreshRow(nl.lineId);
    if (wasInFlight) flashRow(grid, nl.lineId, nl.status === 'Failed');
    if (focusedId === nl.lineId && !insp.contains(document.activeElement)) renderInspector();
    renderCounts();
    renderActions();
  }

  // ---- toolbar: the voice pack's engine settings, saved on every change
  // The toolbar's controls edit this voice object (and its eleven / sts / … objects) directly, so it is never
  // replaced: the saved copy is merged into it, and only when nothing changed while it was saving.
  let voiceEdits = 0;
  const saveVoice = debounce(async () => {
    const seq = voiceEdits;
    try {
      const saved = await rpc('voice.save', { voice });
      if (seq === voiceEdits) mergeInto(voice, saved);
      const i = app.state.voicePacks.findIndex((v) => v.id === voice.id);
      if (i >= 0) app.state.voicePacks[i] = voice;
    } catch (e) { fail(e); }
  }, 300);

  function setIn(obj, key, value) {
    if (voice.settingsLocked) { toast('This voice pack\'s settings are locked. Unlock them in Global Settings → Voice packs.', true); renderToolbar(); return; }
    obj[key] = value;
    voiceEdits++;
    saveVoice();
    renderToolbar();
  }
  const setE = (k, v) => setIn(voice.eleven, k, v);

  const isTagModel = (m) => /^eleven_v[34]/i.test(m || '');
  const modelInfo = (m) => models.find((x) => x.modelId === m) || {};
  const grp = (label, ...kids) => h('div.tgroup', { title: TOOL_TIPS[label] || '' }, h('label', label), h('div.row', ...kids));
  const slider = (obj, key, min, max, step, label, locked) => {
    const val = h('span.mono.dim', { style: { width: '34px' } }, Number(obj[key]).toFixed(2));
    const r = h('input', { type: 'range', min, max, step, value: obj[key], disabled: locked, style: { width: '80px' } });
    r.addEventListener('input', () => { val.textContent = Number(r.value).toFixed(2); });
    r.addEventListener('change', () => setIn(obj, key, Number(r.value)));
    return grp(label, r, val);
  };
  const numIn = (obj, key, label, opts = {}) => {
    const i = h('input', { type: 'number', value: obj[key] ?? '', placeholder: opts.placeholder || '', step: opts.step || 'any', min: opts.min, disabled: voice.settingsLocked, style: { width: opts.width || '76px' }, title: opts.title || '' });
    i.addEventListener('change', () => setIn(obj, key, i.value === '' ? (opts.nullable ? null : 0) : Number(i.value)));
    return grp(label, i);
  };
  const voicePicker = (obj, idKey, nameKey, list, label = 'Voice') => {
    const sel = h('select', { disabled: voice.settingsLocked, style: { maxWidth: '170px' }, onchange: (ev) => { const v = list.find((x) => x.voiceId === ev.target.value); if (nameKey) obj[nameKey] = v ? v.name : ''; setIn(obj, idKey, ev.target.value); } },
      h('option', { value: '' }, list.length ? '— pick a voice —' : ((nameKey && obj[nameKey]) || 'Loading voices…')),
      list.map((v) => h('option', { value: v.voiceId, selected: v.voiceId === obj[idKey] }, v.name)));
    if (obj[idKey] && !list.some((v) => v.voiceId === obj[idKey])) sel.append(h('option', { value: obj[idKey], selected: true }, (nameKey && obj[nameKey]) || obj[idKey]));
    return grp(label, sel, h('button.icon-btn', { title: 'Reload voices and models', onclick: () => loadEngine(true) }, '⟳'));
  };

  // Format, seed and concurrency are set once and rarely touched: they live behind one button.
  const FORMAT_NAME = { mp3_44100_128: 'MP3 128', mp3_44100_192: 'MP3 192', pcm_44100: 'PCM 44.1k', pcm_24000: 'PCM 24k', pcm_22050: 'PCM 22k', pcm_16000: 'PCM 16k' };
  function outputGroup(fields, o) {
    const sum = `${FORMAT_NAME[o.outputFormat] || o.outputFormat || '—'} · ${o.seed == null || o.seed === '' ? 'random seed' : 'seed ' + o.seed} · ${o.concurrency || 1} at once`;
    const btn = h('button.btn.sm', { title: `Output: ${sum}. Click to change the audio format, seed and how many lines are requested at once.` }, `${FORMAT_NAME[o.outputFormat] || 'Format'} ▾`);
    btn.onclick = () => popover(btn, fields);
    return grp('Output', btn);
  }

  function renderToolbar() {
    const locked = voice.settingsLocked;
    const eng = engine();
    const engineSel = vc ? null : grp('Engine', h('select', { disabled: locked, onchange: (ev) => { setIn(voice, 'engine', ev.target.value); loadEngine(); } },
      ENGINES.map(([v, t]) => h('option', { value: v, selected: v === (voice.engine || 'elevenlabs') }, t))));
    clear(tb);
    if (eng === 'sts') {
      const st = voice.sts;
      const fmt = h('select', { disabled: locked, onchange: (ev) => setIn(st, 'outputFormat', ev.target.value) },
        outputFormats(st.outputFormat).map(([v, t]) => h('option', { value: v, selected: v === st.outputFormat }, t)));
      const modelSel = h('select', { disabled: locked, onchange: (ev) => setIn(st, 'modelId', ev.target.value) },
        (models.length ? models : [{ modelId: st.modelId }]).map((m) => h('option', { value: m.modelId, selected: m.modelId === st.modelId }, m.modelId)));
      tb.append(voicePicker(st, 'voiceId', 'voiceName', voices, 'Target voice'), grp('Model', modelSel),
        slider(st, 'stability', 0, 1, 0.01, 'Stability', locked), slider(st, 'similarity', 0, 1, 0.01, 'Similarity', locked), slider(st, 'style', 0, 1, 0.01, 'Style', locked),
        grp('Speaker boost', switchEl(st.speakerBoost, (v) => setIn(st, 'speakerBoost', v), '', { disabled: locked, title: 'Boosts similarity to the target voice; slightly slower' })),
        grp('Remove background noise', switchEl(st.removeBackgroundNoise, (v) => setIn(st, 'removeBackgroundNoise', v), '', { disabled: locked, title: 'Strip background noise from the source before converting' })),
        outputGroup(() => [grp('Format', fmt), numIn(st, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px' }),
          numIn(st, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'Your ElevenLabs plan\'s concurrency limit' })], st));
    } else if (eng === 'voxcpm' || eng === 'omnivoice') {
      const o = eng === 'voxcpm' ? voice.voxCpm : voice.omniVoice;
      const vsel = h('select', { disabled: locked, onchange: (ev) => setIn(o, 'voice', ev.target.value) },
        h('option', { value: '' }, localVoices.length ? '— pick a reference voice —' : (o.voice || 'Server offline?')),
        localVoices.map((v) => h('option', { value: v, selected: v === o.voice }, v)));
      if (o.voice && !localVoices.includes(o.voice)) vsel.append(h('option', { value: o.voice, selected: true }, o.voice));
      const health = h('span.badge.b-skip', 'checking…');
      rpc('engine.health', { engine: eng }).then((r) => { health.className = 'badge ' + (r.ok ? 'b-valid' : 'b-broken'); health.textContent = r.ok ? `ready${r.device ? ' · ' + r.device : ''}` : r.phase; }).catch(() => { health.textContent = 'offline'; health.className = 'badge b-broken'; });
      tb.append(engineSel, grp('Reference voice', vsel, h('button.icon-btn', { title: 'Reload', onclick: () => loadEngine(true) }, '⟳')), grp('Server', health));
      if (eng === 'voxcpm') {
        tb.append(numIn(o, 'cfgValue', 'CFG', { step: 0.1 }), numIn(o, 'inferenceTimesteps', 'Timesteps', { min: 1 }),
          grp('Normalize', switchEl(o.normalize, (v) => setIn(o, 'normalize', v), '', { disabled: locked, title: 'Normalize numbers and abbreviations in the text before speaking' })),
          grp('Retry bad cases', switchEl(o.retryBadcase, (v) => setIn(o, 'retryBadcase', v), '', { disabled: locked, title: 'Regenerate automatically when the result is too long or garbled' })),
          grp('Reference mode', switchEl(o.referenceMode, (v) => setIn(o, 'referenceMode', v), '', { disabled: locked, title: 'Use the reference audio as a voice prompt (closer timbre)' })),
          o.referenceMode ? numIn(o, 'controlCfgValue', 'Control CFG', { step: 0.1 }) : null,
          numIn(o, 'concurrency', 'Concurrent', { min: 1, width: '58px' }));
      } else {
        tb.append(numIn(o, 'numStep', 'Steps', { min: 1 }), numIn(o, 'speed', 'Speed', { step: 0.05 }), numIn(o, 'guidanceScale', 'Guidance', { step: 0.1 }),
          numIn(o, 'concurrency', 'Concurrent', { min: 1, width: '58px' }));
      }
    } else {
      const e = voice.eleven;
      const tag = isTagModel(e.modelId);
      const mi = modelInfo(e.modelId);
      const supportsStyle = mi.canUseStyle != null ? mi.canUseStyle && !tag : !tag;
      const supportsBoost = mi.canUseSpeakerBoost != null ? mi.canUseSpeakerBoost && !tag : !tag;
      const langOk = !/multilingual_v2|monolingual_v1|english_v1/.test(e.modelId);
      const modelSel = h('select', { disabled: locked, onchange: (ev) => setE('modelId', ev.target.value) },
        (models.length ? models : [{ modelId: e.modelId }]).map((m) => h('option', { value: m.modelId, selected: m.modelId === e.modelId }, m.modelId + (isTagModel(m.modelId) ? ' · tags' : ''))));
      const stabVal = e.stability < 0.25 ? 0 : e.stability < 0.75 ? 0.5 : 1;
      const stability = tag
        ? grp('Stability', segmented([[0, 'Creative'], [0.5, 'Natural'], [1, 'Robust']], stabVal, (v) => setE('stability', Number(v)), { disabled: locked }))
        : slider(e, 'stability', 0, 1, 0.01, 'Stability', locked);
      const fmt = h('select', { disabled: locked, onchange: (ev) => setE('outputFormat', ev.target.value) },
        outputFormats(e.outputFormat).map(([v, t]) => h('option', { value: v, selected: v === e.outputFormat }, t)));
      const lang = h('select', { disabled: locked || !langOk, title: langOk ? 'Force the language (ISO 639-1). Auto lets the model detect it.' : 'This model does not accept a language code.', onchange: (ev) => setE('languageCode', ev.target.value) },
        LANGS.map(([v, t]) => h('option', { value: v, selected: v === (e.languageCode || '') }, t)));
      const norm = h('select', { disabled: locked, title: 'Text normalization: whether ElevenLabs writes out numbers, dates and abbreviations before speaking ("1st" → "first", "Dr." → "Doctor"). Auto lets the model decide; On is safer for numbers; Off keeps the text exactly as written.', onchange: (ev) => setE('textNormalization', ev.target.value) },
        [['auto', 'Auto'], ['on', 'On'], ['off', 'Off']].map(([v, t]) => h('option', { value: v, selected: v === e.textNormalization }, t)));
      tb.append(engineSel, voicePicker(e, 'voiceId', 'voiceName', voices), grp('Model', modelSel), stability,
        slider(e, 'similarity', 0, 1, 0.01, 'Similarity', locked),
        supportsStyle ? slider(e, 'style', 0, 1, 0.01, 'Style', locked) : null,
        supportsBoost ? grp('Speaker boost', switchEl(e.speakerBoost, (v) => setE('speakerBoost', v), '', { disabled: locked, title: 'Boosts similarity to the voice; slightly slower' })) : null,
        slider(e, 'speed', 0.7, 1.2, 0.01, 'Speed', locked),
        grp('Language', lang), grp('Read numbers ⓘ', norm),
        outputGroup(() => [grp('Format', fmt),
          numIn(e, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px', title: 'Same seed + same text + same settings ≈ same take. Empty = random.' }),
          numIn(e, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'How many lines are requested at once. Match your ElevenLabs plan\'s concurrency limit; the app slows down automatically on "too many requests".' })], e));
    }
    if (locked) tb.append(h('span.badge.b-toolong', { title: 'Unlock in Global Settings → Voice packs' }, '🔒 locked'));
    tb.append(helpButton('the-toolbar'));
    // The credits sit in the page head, so the toolbar stays on one line.
    clear(creditsSlot);
    if (eng === 'elevenlabs' || eng === 'sts') creditsSlot.append(creditsEl || (creditsEl = creditsButton(() => patch.lines.filter(pendingLine).reduce((a, l) => a + l.spoken.length, 0))));
  }

  // Folders ▾ in the page head: the settings dialog and every folder of this patch, with its path.
  function foldersMenu() {
    const btn = h('button.btn.sm', { title: 'Folders & options, and opening this patch\'s folders in Explorer' }, icon('folder', { size: 14 }), ' Folders ▾');
    btn.addEventListener('click', () => popupMenu(btn, [
      { label: 'Folders & options…', sub: 'Output folders, DBVO Fix folder, reference topic files and overwrite options', onclick: openOptions },
      patch.wavLipFolder ? { label: 'Open the WAV + LIP folder', sub: patch.wavLipFolder, onclick: () => rpc('open.path', { path: patch.wavLipFolder }).catch(fail) } : { label: 'Open the WAV + LIP folder', sub: 'Not set yet: Folders & options…', onclick: openOptions },
      patch.finalRoot ? { label: 'Open the final folder (.fuz)', sub: patch.finalRoot, onclick: () => rpc('open.path', { path: patch.finalRoot }).catch(fail) } : { label: 'Open the final folder (.fuz)', sub: 'Not set yet: Folders & options…', onclick: openOptions },
      patch.fixFolder ? { label: 'Open the DBVO Fix folder', sub: patch.fixFolder, onclick: () => rpc('open.path', { path: patch.fixFolder }).catch(fail) } : null,
    ].filter(Boolean)));
    return btn;
  }

  // ---- tags: insert at the caret, or prefix the ticked rows
  function insertAtCaret(t) {
    const { el, line } = lastEdit;
    saveSpoken(line, insertTagAtCaret(el, t));
  }
  const saveSpoken = debounce((l, value) => { if (value !== l.spoken) upd(l, 'spoken', value); }, 600);

  // "Add a [tag] to all shown…" (shared with NPC Revoice)
  function bulkTagDialog() {
    const pool = grid.rows.filter((l) => !l.omitted && !l.excluded);
    const plugins = [...new Set(pool.flatMap((l) => l.sources.map((x) => x.plugin)))];
    return sharedBulkTagDialog({
      pool, idOf: (l) => l.lineId, original: (l) => l.originalLine,
      originalLabel: `in ORIGINAL MOD LINE (${plugins.join(', ') || 'plugin'})`,
      originalNote: '(Parenthesis) cues such as (Intimidate) are removed from topic files, so look for them here. Separate several with commas.',
      apply: applyTags,
    });
  }

  async function applyTags(tags, mode, ids, placement = '') {
    try {
      const r = await rpc('line2.bulkTags', { id, lineIds: ids, tags, mode, placement });
      patch = r.patch;
      reindex();
      grid.setRows(visible());
      renderInspector();
      renderCounts();
      toast(`${r.changed} line(s) changed.`);
    } catch (e) { fail(e); }
  }

  function tagClick(t, remove) {
    const ids = [...grid.selected];
    if (!remove && lastEdit && document.activeElement === lastEdit.el) { insertAtCaret(t); return; }
    if (ids.length) { applyTags([t], remove ? 'remove' : 'add', ids); return; }
    toast('Click into a spoken text first (the tag goes in at the cursor), or tick rows to add it to the start of each.', true);
  }

  function renderTagbar() {
    clear(tagbar);
    if (vc) {
      tagbar.append(h('span.lbl', 'Voice Changer'),
        h('span.dim', 'The source recording decides the delivery. For converted lines the text column is only used for the .lip, so edit it if the source says something different. Lines without a source use it as text-to-speech when that is turned on.'),
        null);
      return;
    }
    if (engine() !== 'elevenlabs') {
      tagbar.append(h('span.lbl', 'Tags'), h('span.dim', 'Audio tags are an ElevenLabs v3/v4 feature; local engines ignore them (they are removed before sending).'), null);
      return;
    }
    const chip = (t) => {
      const c = h('span.chip.tag-chip', { title: 'Click: insert at the cursor, or add to the start of the ticked rows. Shift-click: remove from the ticked rows.' }, t);
      c.addEventListener('mousedown', (e) => e.preventDefault()); // keep the caret in the text box
      c.addEventListener('click', (e) => tagClick(t, e.shiftKey));
      return c;
    };
    const custom = h('input', { type: 'text', placeholder: '[custom tag]', title: 'Type any [tag] and press Enter to add it to the ticked rows', style: { width: '130px' } });
    custom.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' || !custom.value.trim()) return;
      let t = custom.value.trim();
      if (!t.startsWith('[')) t = `[${t.replace(/[\[\]]/g, '')}]`;
      const ids = [...grid.selected];
      if (!ids.length) { toast('Tick the box of one or more rows first.', true); return; }
      applyTags([t], 'add', ids);
      custom.value = '';
    });
    tagbar.append(
      h('span.lbl', 'Tags'), helpButton('spoken-text-and-tags'),
      voice.tags.map(chip),
      custom,
      h('button.btn.sm', { title: 'Let the AI add ElevenLabs audio tags to the ticked lines (you review them)', onclick: () => { const ids = [...grid.selected]; if (!ids.length) { toast('Tick the box of one or more rows first.', true); return; } autotag(ids); } }, 'AI Auto Tag'),
      h('button.btn.sm.ghost', { title: 'Add a [tag] to every line in the current filter…', onclick: () => bulkTagDialog() }, 'Add a [tag] to all shown…'),
      h('button.btn.sm.ghost', { title: 'Find text in the spoken text of every line shown and replace it, with [tags] if you like', onclick: () => findReplaceDialog({
        pool: grid.rows.filter((l) => !l.omitted), idOf: (l) => l.lineId, textOf: (l) => l.spoken, tags: voice.tags || [],
        apply: async (items) => { try { patch = await rpc('line2.replaceTexts', { id, items }); reindex(); grid.setRows(visible()); renderCounts(); renderActions(); renderInspector(); } catch (e) { fail(e); } } }) }, 'Find and replace in all shown…'),
      h('div.spacer'),
      null);
  }

  async function autotag(ids) {
    if (!aiReady()) return;
    try {
      toast(`Asking the AI for tags on ${ids.length} line(s)…`);
      const r = await rpc('patch.autotag', { id, lineIds: ids });
      patch = r.patch;
      reindex();
      grid.setRows(visible());
      renderCounts();
      const bad = r.results.filter((x) => x.error);
      toast(autotagSummary(r.results), r.results.some((x) => x.error));
    } catch (e) { fail(e); }
  }

  // ---- filters
  // Like the backend: "missing" = no take at all (a line with one or more takes is never missing);
  // "edited" = has takes, but the spoken text changed since the take in use (or the last attempt failed).
  const pendingLine = (l) => !l.omitted && !l.excluded && !l.takes.length && canGen(l);
  const isEdited = (l) => l.takes.length > 0 && (l.stale || l.status === 'Stale' || l.status === 'Failed');
  const editedLine = (l) => !l.omitted && !l.excluded && isEdited(l) && canGen(l);
  function counts() {
    const L = patch.lines;
    return {
      all: L.length,
      pending: L.filter(pendingLine).length,
      generated: L.filter((l) => l.takes.length && !l.omitted && !l.excluded).length,
      current: L.filter((l) => l.takes.length && !isEdited(l) && !l.omitted && !l.excluded).length,
      failed: L.filter((l) => l.status === 'Failed' && !l.omitted && !l.excluded).length,
      stale: L.filter((l) => isEdited(l) && !l.omitted && !l.excluded).length,
      omitted: L.filter((l) => l.omitted).length,
      marked: L.filter((l) => l.marked).length,
      excluded: L.filter((l) => l.excluded).length,
      active: L.filter((l) => !l.omitted && !l.excluded).length,
      differs: L.filter((l) => l.differs).length,
      p1: L.filter((l) => l.editedInPhase1).length,
      suggestions: L.filter((l) => l.tagSuggestion).length,
      nosource: L.filter((l) => vc && !l.hasSource).length,
      qa: L.filter(qaFlagged).length,
      approved: L.filter((l) => l.approved).length,
      unapproved: L.filter((l) => l.takes.length && !l.approved && !l.omitted && !l.excluded).length,
    };
  }

  function visible() {
    const q = search.toLowerCase();
    return patch.lines.filter((l) => {
      switch (filter) {
        case 'pending': if (!pendingLine(l)) return false; break;
        case 'current': if (!(l.takes.length && !isEdited(l))) return false; break;
        case 'stale': if (!(isEdited(l) && !l.omitted && !l.excluded)) return false; break;
        case 'failed': if (l.status !== 'Failed') return false; break;
        case 'omitted': if (!l.omitted) return false; break;
        case 'marked': if (!l.marked) return false; break;
        case 'excluded': if (!l.excluded) return false; break;
        case 'p1': if (!l.editedInPhase1) return false; break;
        case 'suggestions': if (!l.tagSuggestion) return false; break;
        case 'nosource': if (l.hasSource) return false; break;
        case 'qa': if (!qaFlagged(l)) return false; break;
        case 'approved': if (!l.approved) return false; break;
        case 'unapproved': if (!(l.takes.length && !l.approved && !l.omitted && !l.excluded)) return false; break;
      }
      return !q || l.finalLine.toLowerCase().includes(q) || l.spoken.toLowerCase().includes(q) || l.originalLine.toLowerCase().includes(q);
    });
  }

  function renderCounts() {
    const c = counts();
    const chip = (key, label, title) => h('span.chip' + (filter === key ? '.on' : ''), { title, onclick: () => { filter = key; store.set('patch:' + id, { filter }); renderCounts(); grid.setRows(visible()); } }, label, h('span.n', c[key]));
    const had = filters.querySelector('input[type=text]');
    const q = had || h('input', { type: 'text', placeholder: 'Search…', value: search, title: 'Search the original line, spoken text and file name' });
    if (!had) q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visible()); }, 150));
    // Only filters that match at least one line (the open one always stays).
    const opt = (key, label, title) => (key === 'all' || filter === key || c[key] ? chip(key, label, title) : null);
    clear(filters).append(opt('all', 'All', 'Every line of this patch'), opt('pending', 'Missing', 'Lines without any take yet'), opt('current', vc ? 'Converted' : 'Generated', 'Lines with a take that matches their spoken text'),
      opt('stale', 'Edited since take', 'Lines with a take whose spoken text changed since (tags added, words edited), or whose last attempt failed'),
      opt('failed', 'Failed', 'The last attempt made no usable take (engine error, credits, offline local engine, LIP or WAV step). The error is shown at the top of the line\'s panel; Generate options ▾ → Missing (no take yet) or Edited (has a take) retries these.'),
      opt('marked', 'Flagged', 'Flagged to revisit'), opt('excluded', 'Excluded', 'Won\'t generate or export'),
      opt('omitted', 'Omitted', 'Left out because a ticked reference topic file (e.g. the base game pack) already has the line'),
      vc ? opt('nosource', 'No source file', 'No file of the other voice pack was found for these lines') : null, opt('p1', 'Edited in Phase 1', 'The in-game line was edited in Phase 1'),
      opt('suggestions', 'AI suggestions', 'AI tag suggestions waiting for Accept or Undo'),
      opt('qa', 'QA flagged', 'Check takes flagged the take in use: the words differ from the line, it is too quiet, or it clips'),
      opt('approved', 'Approved', 'Approved in Review'), review || filter === 'unapproved' ? opt('unapproved', 'Not approved', 'Lines with a take that are not approved yet') : null,
      h('div.spacer'), q, columnsMenu(grid),
      h('button.btn.sm', { title: 'Show or hide the editing panel', onclick: () => { showInsp = !showInsp; store.set('insp2', showInsp); split.classList.toggle('no-insp', !showInsp); renderCounts(); } }, showInsp ? 'Hide panel ▸' : '◂ Show panel'));
  }

  // ---- inspector
  function renderInspector() {
    clear(insp);
    const l = focusedId && index.get(focusedId);
    if (!l) {
      insp.append(h('h3', 'Line'), h('div.dim', 'Click a row to see it in full. Double-click plays the selected take.'),
        h('div.help',
          vc ? h('p', 'Voice Changer: each line is converted from the source WAV or .fuz with the same name (Legacy or DBVO 2.0). A new .lip is always made from the text, so it matches any edits. Lines without a source are muted unless text-to-speech for them is turned on.')
            : h('p', h('b', 'Filename'), ' comes from the in-game line set in Phase 1. ', h('b', 'Spoken text'), ' is what the voice says: edit it in the list or here, and put [tags] anywhere. The back arrow at the start of an edited box restores the in-game line.'),
          h('p', 'Tick rows for one-off selections; flags stay until you clear them, so you can collect lines to redo and generate them together.'),
          vc ? null : h('p', 'Tag buttons insert at the cursor of the box you are typing in. With rows ticked instead, they go at the start of each ticked line.'),
          h('p', 'Keys: ↑/↓ move, Space ticks, Ctrl+A ticks all shown.')));
      return;
    }
    const spoken = h('textarea.autogrow', { rows: 2, disabled: l.omitted, title: 'What the voice says. Edit freely and put [tags] anywhere; the file name doesn\'t change' }, l.spoken);
    const grow = () => { spoken.style.height = 'auto'; spoken.style.height = Math.min(220, spoken.scrollHeight + 2) + 'px'; };
    spoken.addEventListener('input', grow);
    requestAnimationFrame(grow);
    spoken.addEventListener('change', () => { if (spoken.value !== (index.get(l.lineId) || l).spoken) upd(l, 'spoken', spoken.value); });
    spoken.addEventListener('focus', () => { editing = { lineId: l.lineId, el: spoken }; });
    trackCaret(spoken, l);
    insp.append(
      h('div.row', statusDot(l), h('span.dim', l.omitted ? 'Omitted — already in ' + l.omittedBy : STATUS_TEXT[l.status] || l.status), h('div.spacer'),
        h('button.act-btn' + (l.excluded ? '.on.bad' : ''), { title: l.excluded ? 'Excluded - click to clear' : 'Exclude from generate and export', onclick: () => upd(l, l.excluded ? 'include' : 'exclude') }, icon('ban')),
        h('button.act-btn' + (l.marked ? '.on.warn' : ''), { title: l.marked ? 'Flagged to revisit - click to clear' : 'Flag to revisit', onclick: () => upd(l, 'mark', !l.marked) }, icon('flag')),
        h('button.act-btn' + (l.approved ? '.on' : ''), { title: !l.takes.length ? 'No take to approve yet' : l.approved ? 'Approved - click to clear' : 'Approve: heard and accepted (A in Review)', disabled: !l.takes.length, onclick: () => upd(l, 'approve', !l.approved) }, icon('check'))),
      l.lastError ? h('div.banner.bad', { style: { margin: 0 } }, l.lastError) : null,
      l.originalLine === l.finalLine
        ? h('div', h('div.field-label', 'Line (same in game)'), h('div.box', l.finalLine))
        : [h('div', h('div.field-label', 'Original line (from the mod)'), h('div.box', l.originalLine)), h('div', h('div.field-label', 'In-game line (edited in Phase 1)'), h('div.box', l.finalLine))],
      h('div', h('div.field-label', 'File'), h('div.file-row', h('span.mono.ellipsis', { title: `${l.stem}.wav / .lip / .fuz` }, l.stem), h('span.ext-tags', h('span.badge.b-skip', '.wav'), h('span.badge.b-skip', '.lip'), h('span.badge.b-skip', '.fuz')),
        h('button.icon-btn', { title: 'Copy the file name', onclick: () => navigator.clipboard.writeText(l.stem).then(() => toast('Copied.')) }, '⧉'))),
      vc ? h('div', h('div.field-label', 'Source WAV'), l.hasSource ? h('div.row', h('button.btn.sm', { title: 'Play the original recording this line is converted from', onclick: () => playSource(l) }, '▶ Play source'), h('span.mono.faint.ellipsis', { title: l.sourceWav }, l.sourceWav)) : h('div.banner.bad', { style: { margin: 0 } }, 'No source WAV found with this name.')) : null,
      fxRow(l),
      h('div', h('div.field-label', vc ? 'Text for the LIP' : 'Spoken text (tags allowed)'), spoken,
        h('div.row', { style: { marginTop: '6px', gap: '6px' } },
          vc || engine() !== 'elevenlabs' ? null : h('button.btn.sm', { title: 'Let the AI suggest audio tags for this line', disabled: l.omitted, onclick: () => autotag([l.lineId]) }, 'AI tag'),
          l.spokenEdited ? h('button.btn.sm.ghost', { title: 'Put the in-game line back as the spoken text', onclick: () => upd(l, 'resetSpoken') }, '↺ Restore') : null,
          h('div.spacer'),
          h('button.btn.sm', { title: l.omitted ? 'Omitted: a reference topic file already has this line' : app.job ? 'A job is running' : 'Record this line with your microphone, then keep it as it is or convert it into the voice pack voice', disabled: l.omitted || !!app.job, onclick: () => record(l) }, '🎙 Record'),
          h('button.btn.sm.primary', { title: l.omitted ? 'Omitted: a reference topic file already has this line' : !canGen(l) ? 'No source file for this line (text-to-speech for such lines is off)' : 'Make a new take of this line', disabled: l.omitted || !canGen(l), onmousedown: keepEditing, onclick: () => regen(l) }, l.takes.length ? 'New take' : vc ? 'Convert' : 'Generate'))),
      l.tagSuggestion ? h('div.box', h('div.field-label', 'AI suggestion'), h('div', diffAdded(l.spoken, l.tagSuggestion)),
        h('div.row', { style: { marginTop: '6px' } }, h('button.btn.sm.primary', { title: 'Keep the suggested tags', onclick: () => upd(l, 'acceptTags') }, 'Accept'), h('button.btn.sm', { title: 'Go back to the text before tagging', onclick: () => upd(l, 'rejectTags') }, 'Undo'))) : null,
      l.omittedBy ? switchEl(l.overrideOmit, (v) => upd(l, 'overrideOmit', v), `Generate anyway (it is in "${l.omittedBy}")`) : null,
      h('h3', `Takes (${l.takes.length})`),
      l.takes.length ? h('div.col', { style: { gap: '4px' } }, l.takes.slice().reverse().map((t) => h('div.take.compact' + (t.id === l.selectedTakeId ? '.sel' : ''),
        h('button.icon-btn', { title: 'Play', onclick: () => play(l, t) }, '▶'),
        h('div.grow', { style: { minWidth: 0 } },
          h('div.row', { style: { gap: '6px' } }, h('b', '#' + t.slot), t.id === l.selectedTakeId ? h('span.faint', 'used') : null,
            t.qa ? h('span', { title: qaText(t.qa) + (t.qa.heard ? `\nHeard: "${t.qa.heard}"` : ''), style: { color: t.qa.flags.length ? 'var(--warn)' : 'var(--ok)' } }, t.qa.flags.length ? '⚠ ' + t.qa.flags.map((f) => QA_WHY[f] || f).join(', ') : '✓') : null,
            h('span.meta.ellipsis', { title: `${t.settings.engine} · ${t.settings.modelId}${t.settings.engine && t.settings.engine.startsWith('elevenlabs') ? ` · stability ${t.settings.stability} · similarity ${t.settings.similarity}${t.settings.speakerBoost ? ' · boost' : ''}` : ''}${t.settings.seed != null ? ' · seed ' + t.settings.seed : ''}${t.original ? '\nImported .fuz, kept as it is' : ''}` }, `${t.durationSeconds.toFixed(1)} s · ${t.settings.modelId || t.settings.engine}${t.original ? ' · imported' : ''}`)),
          t.spokenText !== l.spoken ? h('div.meta.ellipsis', { title: t.spokenText }, 'Text then: ' + t.spokenText) : null),
        t.id !== l.selectedTakeId ? h('button.btn.sm', { title: 'Use this take for export and .fuz', onclick: () => upd(l, 'selectTake', t.id) }, 'Use') : null,
        h('button.icon-btn', { title: 'Discard take', onclick: async () => { if (await confirmBox('Discard take?', `Delete take #${t.slot} and its files?`, 'Discard', true)) upd(l, 'deleteTake', t.id); } }, '🗑'))))
        : h('div.faint', 'No takes yet.'),
      h('h3', 'Used by'),
      h('div.col', l.sources.map((s) => h('div.faint.mono.ellipsis', `${patch.modNames[s.modId] || '?'} · ${s.plugin} · ${s.formId}`))),
    );
  }

  // ---- status bar: Export, then one button per generate choice
  function renderActions() {
    const sel = [...grid.selected];
    const busy = !!app.job;
    const c = counts();
    actions.removeAttribute('title'); // job starts wait in the queue while another task runs
    const verb = vc ? 'Convert' : 'Generate';
    const exp = h('button.btn', { title: busy ? 'Write WAV + LIP files and build .fuz files (waits in the queue while the running task finishes)' : 'Write WAV + LIP files and build .fuz files' }, 'Export ▾');
    exp.addEventListener('click', () => popupMenu(exp, [
      { label: 'WAV + LIP, DBVO Fix plugins and topic file', sub: 'WAV + LIP to the WAV + LIP folder; the DBVO Fix plugins and topic file to their folders (replacing the ones there)', onclick: () => exportWavLip(false) },
      { label: 'WAV + LIP only', sub: 'Leaves the DBVO Fix plugins and topic file in those folders as they are: for when you will fix lines in Phase 1 first, even with Overwrite on for those files', onclick: () => exportWavLip(true) },
      { label: 'Build .fuz into the final folder', sub: 'Also writes the DBVO Fix plugins and topic file', onclick: () => buildFuz(false) },
      { label: 'Build .fuz to another folder…', sub: 'Only the .fuz files', onclick: () => buildFuz(true) },
      { label: 'Only DBVO Fix plugins + topic file', sub: 'No audio: just the plugin with the edited lines and the topic file', onclick: writeFix },
      { label: 'Spoken text to a spreadsheet (CSV)…', sub: 'Edit it in Excel or any editor, then Import ▾ it back', onclick: exportCsv },
    ].filter(Boolean)));
    const takesBtn = h('button.btn', { disabled: busy, title: 'Clean up takes you no longer need' }, 'Takes ▾');
    takesBtn.addEventListener('click', () => popupMenu(takesBtn, [
      { label: `Keep only the used take · ticked lines (${sel.length})`, sub: 'Deletes the other takes and their files (kept ★ takes stay)', onclick: () => sel.length ? purge(sel) : toast('Tick the box of one or more rows first.', true) },
      { label: 'Keep only the used take · all lines', sub: 'Deletes every take that is not in use or kept ★', onclick: () => purge(null) },
    ]));
    const imp = h('button.btn', { title: 'Bring in takes or text from elsewhere' }, 'Import ▾');
    imp.addEventListener('click', () => popupMenu(imp, [
      { label: 'An earlier voice pack…', sub: 'DBVO 1 (Legacy names) or DBVO 2.0 folder: fills lines without takes, .fuz kept as it is', onclick: legacyImport },
      { label: 'Takes from another patch…', sub: 'Its takes for the same lines are added; yours are never replaced', onclick: mergeTakes },
      { label: 'Spoken text from a spreadsheet (CSV)…', sub: 'Shows every change first; you pick which to apply', onclick: importCsv },
    ]));
    const checkBtn = h('button.btn', { title: `Check takes${sel.length ? ` of the ${sel.length} ticked line(s)` : ' of every line'}: Whisper compares what is said with the line, plus loudness and clipping. Flags only, deletes nothing.`, onclick: () => qaRun(sel.length ? sel : null) }, 'Check takes');
    const reviewBtn = h('button.btn' + (review ? '.review-on' : ''), { title: review ? 'End Review (Esc)' : 'Listen through the lines with the keyboard: Space plays, A approves and moves on, R makes a new take, F flags', onclick: () => setReview(!review) }, review ? '■ End review' : '▶ Review');
    const chars = (ls) => ls.reduce((a, l) => a + (l.spoken || '').length, 0);
    const pend = patch.lines.filter(pendingLine);
    const edited = patch.lines.filter(editedLine);
    const tickedL = sel.map((x) => index.get(x)).filter((l) => l && !l.omitted && canGen(l));
    const flagged = patch.lines.filter((l) => l.marked && !l.omitted && !l.excluded && canGen(l));
    const qaL = patch.lines.filter((l) => qaFlagged(l) && !l.omitted && !l.excluded && canGen(l));
    const unapp = patch.lines.filter((l) => l.takes.length && !l.approved && !l.omitted && !l.excluded && canGen(l));
    const all = patch.lines.filter((l) => !l.omitted && !l.excluded && canGen(l));
    const genBtn = generateButton({
      running: app.job && app.job.kind === 'generate', label: vc ? 'Convert options' : 'Generate options',
      items: () => [
        { key: 'missing', label: 'Missing', count: pend.length, chars: chars(pend), desc: 'Lines without any take yet. A line with a take is never missing.' },
        { key: 'edited', label: 'Edited', count: edited.length, chars: chars(edited), desc: 'A new take for each line whose spoken text changed since its take (tags added, words edited), or whose last attempt failed.' },
        { key: 'ticked', label: 'Ticked', count: tickedL.length, chars: chars(tickedL), desc: 'A new take for each ticked line.' },
        { key: 'flagged', label: 'Flagged', count: flagged.length, chars: chars(flagged), desc: 'A new take for each line you flagged to revisit.' },
        { key: 'qa', label: 'Check found problems', count: qaL.length, chars: chars(qaL), desc: 'A new take for each line Check takes flagged (the words differ, too quiet, or clipping).' },
        { key: 'unapproved', label: 'Not approved yet', count: unapp.length, chars: chars(unapp), desc: 'A new take for each line that has a take you haven\'t approved in Review.' },
        { key: 'all', label: 'All lines', count: all.length, chars: chars(all), warn: true, desc: 'A new take for every line that is not omitted or excluded. Uses the most credits; earlier takes stay.' },
      ],
      onPick: (k) => k === 'missing' ? generate('missing') : k === 'edited' ? generate('edited') : k === 'all' ? generate('all') : k === 'flagged' ? generate('marked')
        : generate('selected', (k === 'ticked' ? tickedL : k === 'qa' ? qaL : unapp).map((l) => l.lineId)),
    });
    clear(actions).append(
      helpButton('generating'),
      h('span.stat', h('b', c.generated), ` / ${c.active} ${vc ? 'converted' : 'generated'}`, c.approved ? ` · ${c.approved} approved` : '', patch.exportApprovedOnly ? h('span', { title: 'Folders & options → Export: only approved lines are exported' }, ' · exporting approved only') : '', c.omitted ? ` · ${c.omitted} omitted` : '', c.excluded ? ` · ${c.excluded} excluded` : '', c.failed ? h('span', { style: { color: 'var(--bad)' } }, ` · ${c.failed} failed`) : '', c.stale ? ` · ${c.stale} edited since take` : '', vc && c.nosource ? ` · ${c.nosource} without source` : ''),
      h('div.spacer'),
      sel.length || c.marked ? tickedMenu(sel, c) : null,
      effectsButton(sel.length, (fx) => applyFx(sel, fx)),
      reviewBtn,
      checkBtn,
      imp,
      takesBtn,
      exp,
      genBtn);
  }

  // One menu for what to do with the ticked rows (and flags), so the status bar stays on one line.
  function tickedMenu(sel, c) {
    const btn = h('button.btn.sm.ghost', { title: sel.length ? `Exclude, include, flag or untick the ${sel.length} ticked line(s)` : 'Clear every flag' }, sel.length ? `Ticked (${sel.length}) ▾` : 'Flags ▾');
    btn.onclick = () => popupMenu(btn, [
      sel.length ? { label: '🚫 Exclude ticked', sub: 'Not generated or exported; they stop counting as missing', onclick: () => bulkExclude(sel, true) } : null,
      sel.length && sel.some((x) => (index.get(x) || {}).excluded) ? { label: 'Include ticked', sub: 'Generate and export them again', onclick: () => bulkExclude(sel, false) } : null,
      sel.length ? { label: '⚑ Flag ticked', sub: 'Mark them to revisit; Flagged lines makes new takes of them', onclick: () => bulkMark(sel, true) } : null,
      sel.length ? { label: 'Clear ticks', sub: 'Untick all rows', onclick: () => grid.selectAll(false) } : null,
      c.marked ? { label: `Clear all flags (${c.marked})`, sub: 'Unflag every flagged line', onclick: () => bulkMark(patch.lines.filter((l) => l.marked).map((l) => l.lineId), false) } : null,
    ].filter(Boolean));
    return btn;
  }

  // Spinning ↻ on the lines being made, cleared as each take arrives (or all at once when the job ends).
  function markInFlight(ids) {
    const before = [...inFlight];
    inFlight.clear();
    for (const x of ids || []) inFlight.add(x);
    for (const x of new Set([...before, ...inFlight])) grid.refreshRow(x);
  }

  // ---- 7.0: record, review, check takes, earlier packs, merge, spreadsheet, cache
  async function record(l) {
    if (app.job) { toast('Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.', true); return; }
    await recordTake({ patchId: id, line: l, onSaved: (nl) => { replaceLine(nl); play(nl); } });
  }

  function setReview(on) {
    review = on;
    reviewBar.classList.toggle('hidden', !on);
    if (on) {
      if (!focusedId || !grid.rows.some((r) => r.lineId === focusedId)) { const first = grid.rows.find((r) => r.takes.length); if (first) { focusedId = first.lineId; grid.focus(first.lineId, true); renderInspector(); } }
      renderReviewBar();
      grid.el.focus();
    }
    renderCounts();
    renderActions();
  }

  function renderReviewBar() {
    const c = counts();
    const k = (x) => h('kbd', x);
    clear(reviewBar).append(h('b', 'Review'),
      h('span', k('Space'), ' play / stop'), h('span', k('↑'), k('↓'), ' move'), h('span', k('A'), ' approve + next'), h('span', k('R'), ' new take'), h('span', k('F'), ' flag'), h('span', k('Esc'), ' end'),
      h('div.spacer'),
      h('span.faint', `${c.approved} of ${c.generated} approved`),
      switchEl(reviewAutoplay, (v) => { reviewAutoplay = v; store.set('reviewAutoplay', v); }, 'Autoplay', { title: 'Play each line as you land on it' }),
      helpButton('review'));
  }

  function moveFocus(step) {
    const i = grid.rows.findIndex((r) => r.lineId === focusedId);
    const next = grid.rows[Math.max(0, Math.min(grid.rows.length - 1, i + step))];
    if (!next) return;
    focusedId = next.lineId;
    grid.focus(next.lineId, true);
    renderInspector();
    if (reviewAutoplay) play(next);
  }

  // Review keys work wherever the focus is on this page, except while typing and while a dialog is open.
  const reviewKeys = (e) => {
    if (!review) return;
    if (!document.body.contains(view)) { window.removeEventListener('keydown', reviewKeys, true); return; }
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    if (e.target.closest && e.target.closest('input,textarea,select,[contenteditable]')) return;
    if (!document.getElementById('modal').classList.contains('hidden')) return;
    const l = focusedId && index.get(focusedId);
    const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    const eat = () => { e.preventDefault(); e.stopImmediatePropagation(); };
    if (key === ' ') { eat(); if (l) { if (!audio.paused) audio.pause(); else play(l); } }
    else if (key === 'ArrowDown' || key === 'ArrowUp') { eat(); moveFocus(key === 'ArrowDown' ? 1 : -1); }
    else if (key === 'a') { eat(); if (l && l.takes.length) { upd(l, 'approve', true).then(() => { renderReviewBar(); moveFocus(1); }); } else toast('This line has no take to approve.', true); }
    else if (key === 'r') { eat(); if (l && canGen(l) && !l.omitted && !l.excluded) regen(l); }
    else if (key === 'f') { eat(); if (l) upd(l, 'mark', !l.marked); }
    else if (key === 'Escape') { eat(); setReview(false); }
  };
  window.addEventListener('keydown', reviewKeys, true);

  async function qaRun(ids) {
    const pool = (ids ? ids.map((x) => index.get(x)).filter(Boolean) : patch.lines).filter((l) => !l.omitted && !l.excluded && l.takes.length);
    if (!pool.length) { toast('No takes to check yet.', true); return; }
    const unchecked = pool.filter((l) => { const t = l.takes.find((x) => x.id === l.selectedTakeId); return !(t && t.qa); }).length;
    const o = await qaDialog({ total: pool.length, unchecked, ticked: !!ids });
    if (!o) return;
    try { await rpc('patch.qa', { id, lineIds: ids, ...o }); renderActions(); } catch (e) { fail(e); }
  }

  async function clearQa() {
    try { patch = await rpc('patch.clearQa', { id }); reindex(); if (filter === 'qa') filter = 'all'; renderAll(); toast('Check results cleared.'); } catch (e) { fail(e); }
  }

  async function legacyImport() {
    try {
      const folder = await rpc('pick.folder', { title: 'The earlier voice pack: its folder (sub-folders are searched)' });
      if (!folder) return;
      const s = await rpc('patch.legacyScan', { id, folder });
      const found = s.fromFuz + s.fromWavLip;
      const ok = await modal('Import an earlier voice pack', [
        h('div.mono.faint', { style: { wordBreak: 'break-all' } }, s.folder),
        h('div.kv',
          h('div', 'Lines found in the pack'), h('div', h('b', found), found ? ` (${s.fromFuz} .fuz, ${s.fromWavLip} WAV + LIP · ${s.legacyNamed} Legacy names, ${s.hashNamed} DBVO 2.0 names)` : ''),
          h('div', 'Still to generate'), h('div', s.missing),
          h('div', 'Already have takes (left as they are)'), h('div', s.haveTakes),
          h('div', 'Files in the pack that match no line'), h('div', s.unmatched ? h('span', { title: s.unmatchedExamples.join('\n') }, `${s.unmatched} (another mod's lines, or another version of this one)`) : '0')),
        h('div.help', h('p', 'Each found line gets a take. A .fuz is unpacked so it can be played and checked, and kept as it is: building .fuz files later uses the original, so the audio is never encoded twice. Export writes the names of this voice pack (', voice.naming === 'Legacy' ? 'Legacy' : 'DBVO 2.0', ').'),
          h('p', 'No credits are used. A backup is made first.')),
        !found && s.unmatched ? h('div.banner.warn', { style: { margin: 0 } }, 'Nothing in this folder matches this patch\'s lines. Is it the pack for this mod, and this version of it?') : null,
      ], [{ label: 'Cancel', value: false }, { label: `Import ${found} line(s)`, primary: true, value: found > 0 }], { help: 'importing-an-earlier-pack' });
      if (!ok) return;
      await rpc('patch.importLegacy', { id, folder });
      renderActions();
    } catch (e) { fail(e); }
  }

  async function mergeTakes() {
    const others = app.state.patches.filter((p) => p.id !== id && p.modIds && p.modIds.some((m) => patch.modIds.includes(m)));
    if (!others.length) { toast('No other patch of the same mod(s) to merge from.', true); return; }
    const sel = h('select', others.map((p) => h('option', { value: p.id }, `${p.name} · ${p.voicePack}${p.kind === 'vc' ? ' · Voice Changer' : ''} · ${p.generated} generated`)));
    const ok = await modal('Merge takes from another patch', [
      h('div.help', h('p', 'Every take the other patch has for a line of this patch is added here as an extra take. Your takes, and the take each line uses, stay as they are; lines without a take start using the first one added.'),
        h('p', 'Handy after working on the same mod in two patches, or to bring another voice pack\'s takes in as variants. A backup is made first.')),
      h('div.col', h('div.field-label', 'Take the takes from'), sel)],
    [{ label: 'Cancel', value: false }, { label: 'Merge', primary: true, value: true }], { help: 'merging-takes' });
    if (!ok) return;
    try {
      const r = await rpc('patch.mergeTakes', { id, sourceId: sel.value });
      patch = r.patch; reindex(); renderAll();
      toast(`${r.takes} take(s) added to ${r.lines} line(s).` + (r.onlyThere ? ` ${r.onlyThere} line(s) of the other patch are not in this one.` : ''));
    } catch (e) { fail(e); }
  }

  async function exportCsv() {
    try {
      const path = await rpc('pick.save', { title: 'Save the spoken text', filter: 'CSV (Excel)|*.csv', name: `${patch.name} - spoken text.csv` });
      if (!path) return;
      const r = await rpc('patch.exportCsv', { id, path });
      const v = await modal('Spoken text saved', [h('div', `${r.lines} line(s) saved. Edit the "Spoken text" column (tags allowed) and keep the other columns, then use Import ▾ → Spoken text from a spreadsheet.`), h('div.mono.faint', { style: { wordBreak: 'break-all' } }, r.path)],
        [{ label: 'Open it', value: 'open' }, { label: 'OK', primary: true, value: true }]);
      if (v === 'open') rpc('open.path', { path: r.path });
    } catch (e) { fail(e); }
  }

  async function importCsv() {
    try {
      const path = await rpc('pick.file', { title: 'The edited spreadsheet', filter: 'CSV|*.csv;*.txt' });
      if (!path) return;
      const r = await rpc('patch.readCsv', { id, path });
      if (!r.changes.length) { toast(`No changes: ${r.same} line(s) are the same${r.unmatched ? `, ${r.unmatched} row(s) match no line` : ''}.`); return; }
      const ticks = r.changes.map(() => true);
      const list = h('div.csv-list', r.changes.map((c, i) => h('label.csv-row', h('input', { type: 'checkbox', checked: true, onchange: (e) => { ticks[i] = e.target.checked; } }),
        h('div.col', { style: { minWidth: 0, gap: '2px' } }, h('div.faint.ellipsis', { title: c.finalLine }, c.finalLine), h('div', diffAdded(c.current, c.incoming))))));
      const ok = await modal(`${r.changes.length} change(s) in the spreadsheet`, [
        h('div.faint', `${r.same} line(s) unchanged${r.unmatched ? ` · ${r.unmatched} row(s) match no line of this patch` : ''}. Untick any change you don't want.`),
        list], [{ label: 'Cancel', value: false }, { label: 'Apply ticked changes', primary: true, value: true }], { wide: true, help: 'editing-in-a-spreadsheet' });
      if (!ok) return;
      const changes = r.changes.filter((_, i) => ticks[i]).map((c) => ({ lineId: c.lineId, spoken: c.incoming }));
      const res = await rpc('patch.applyCsv', { id, changes });
      patch = res.patch; reindex(); renderAll();
      toast(`${res.applied} line(s) changed. They show as edited since their take: Generate options ▾ → Edited makes new takes.`);
    } catch (e) { fail(e); }
  }

  async function bulkExclude(ids, value) {
    try {
      patch = await rpc('line2.bulkExclude', { id, lineIds: ids, value });
      reindex(); grid.setRows(visible()); renderCounts(); renderActions(); renderInspector();
      toast(`${ids.length} line(s) ${value ? 'excluded' : 'included'}.`);
    } catch (e) { fail(e); }
  }

  async function bulkMark(ids, value) {
    try {
      patch = await rpc('line2.bulkMark', { id, lineIds: ids, value });
      reindex();
      grid.setRows(visible());
      renderCounts();
      renderActions();
      renderInspector();
    } catch (e) { fail(e); }
  }

  // Single-line generation (row ↻, panel, takes window) plays the new take when it is ready; batches never do.
  let autoPlay = null; // { lineId, takes }
  async function generate(mode, lineIds, { autoplay = false } = {}) {
    autoPlay = null;
    try {
      if (lineIds && lineIds.length === 1) markInFlight(lineIds); // spin at once, before the server answers
      const est = await rpc('patch.estimate', { id, mode, lineIds });
      if (!est.count) { markInFlight(null); toast(vc ? 'Nothing to convert.' : 'Nothing to generate.'); return; }
      // One line never asks; batches ask unless that is turned off in Global Settings.
      const confirmBatch = (app.state.settings.ui || {}).confirmBatch !== 'false';
      if (est.count === 1 || !confirmBatch) {
        if (!est.voiceId) { markInFlight(null); toast('Pick a voice in the toolbar first.', true); return; }
        if (autoplay && lineIds && lineIds.length === 1) { const l0 = index.get(lineIds[0]); autoPlay = { lineId: lineIds[0], takes: l0 ? l0.takes.length : 0 }; }
        markInFlight(lineIds);
        await rpc('patch.generate', { id, mode, lineIds }).catch((e) => { markInFlight(null); throw e; });
        renderActions();
        return;
      }
      let rows;
      let missingVoice = !est.voiceId;
      if (est.kind === 'vc') {
        rows = [['Converted (speech-to-speech)', est.srcCount],
          ...(est.ttsCount ? [['Text-to-speech (no source file)', `${est.ttsCount} line(s) · ${est.ttsCharacters.toLocaleString()} characters ≈ credits · ${est.ttsEngine}${est.ttsVoice ? ' · ' + est.ttsVoice : ''}`]] : []),
          ['Source audio (billed by length)', `${est.seconds.toFixed(0)} s ≈ ${(est.seconds / 60).toFixed(1)} min`],
          ['Target voice', est.voice || est.voiceId || '— none —'], ['Model', est.model], ['Stability', est.stability], ['Similarity', est.similarity], ['Style', est.style],
          ['Speaker boost', est.speakerBoost ? 'ON' : 'off'], ['Remove background noise', est.removeBackgroundNoise ? 'ON' : 'off'], ['Seed', est.seed ?? 'random'],
          ['Output format', est.outputFormat], ['Concurrent requests', est.concurrency], ['Loudness normalization', est.loudnorm ? 'ON' : 'off'], ['Gain', est.gainDb + ' dB'],
          ['Tail trim / fade', `${est.trimTailMs} ms / ${est.fadeOutMs} ms`], ['LIP', 'new, from the text column'], ['Naming', est.naming]];
      } else if (est.kind === 'voxcpm' || est.kind === 'omnivoice') {
        rows = [['Lines', est.count], ['Characters', est.characters.toLocaleString()], ['Engine', est.kind], ['Reference voice', est.voiceId || '— none —'],
          ...Object.entries(est.engineSettings).filter(([k]) => k !== 'voice').map(([k, v]) => [k, String(v)]),
          ['Loudness normalization', est.loudnorm ? 'ON' : 'off'], ['Tail trim / fade', `${est.trimTailMs} ms / ${est.fadeOutMs} ms`], ['Naming', est.naming], ['Pronunciation entries (global + voice)', est.pronunciations]];
      } else {
        const s = est.settings;
        rows = [
          ['Lines', est.count], ['Characters (≈ credits)', est.characters.toLocaleString()],
          ['Voice', est.voice || est.voiceId || '— none —'], ['Model', est.model],
          ['Stability', s.stability], ['Similarity', s.similarityBoost],
          ...(s.style != null ? [['Style', s.style]] : []),
          ...(s.useSpeakerBoost != null ? [['Speaker boost', s.useSpeakerBoost ? 'ON' : 'off']] : []),
          ['Speed', s.speed], ['Seed', est.seed ?? 'random'], ['Output format', est.outputFormat], ['Concurrent requests', est.concurrency],
          ['Language', est.languageCode || 'auto'], ['Read numbers (normalization)', est.textNormalization],
          ['Loudness normalization', est.loudnorm ? 'ON' : 'off'], ['Gain', est.gainDb + ' dB'],
          ['Tail trim / fade', `${est.trimTailMs} ms / ${est.fadeOutMs} ms`], ['Naming', est.naming],
          ['Pronunciation entries (global + voice)', est.pronunciations], ['ElevenLabs dictionaries', est.dictionaries],
        ];
      }
      const ok = await modal(mode === 'all' ? `${vc ? 'Convert' : 'Generate'} every line again?` : 'Check before spending credits', [
        h('div', voiceChip(voice.name, voice.color)),
        h('div.kv', rows.flatMap(([k, v]) => [h('div', k), h('div', String(v))])),
        mode === 'all' ? h('div.banner.warn', { style: { margin: 0 } }, 'Every line gets a new take, which becomes the one used. Earlier takes stay in the take list.') : null,
        missingVoice ? h('div.banner.bad', { style: { margin: 0 } }, 'Pick a voice in the toolbar first.') : null,
      ], [{ label: 'Cancel', value: false }, { label: `${vc ? 'Convert' : 'Generate'} ${est.count} line(s)`, primary: true, value: !missingVoice }], { help: 'generating' });
      if (!ok) { markInFlight(null); return; }
      markInFlight(mode === 'selected' ? lineIds : patch.lines.filter(mode === 'marked' ? (l) => l.marked && canGen(l) : mode === 'all' ? (l) => !l.omitted && !l.excluded && canGen(l) : mode === 'edited' ? editedLine : pendingLine).map((l) => l.lineId));
      await rpc('patch.generate', { id, mode, lineIds }).catch((e) => { markInFlight(null); throw e; });
      renderActions();
    } catch (e) { markInFlight(null); fail(e); }
  }

  const filesModal = (title, summary, files) => modal(title, [summary, files && files.length ? h('div', h('div.field-label', 'DBVO Fix plugins and topic files written'), h('div.box.mono', { style: { whiteSpace: 'pre-wrap', maxHeight: '180px', overflow: 'auto' } }, files.join('\n'))) : null]);

  // DBVO Fix plugins in the final folder: ask first when the patch says so (Folders & options → Overwrite).
  async function fixChoice() {
    const ow = await askFixOverwrite(await rpc('patch.fixConflicts', { id }).catch(() => []));
    return ow == null ? null : { keepFix: !ow };
  }

  // Files from earlier exports whose lines are no longer exported (excluded, removed or renamed in Phase 1):
  // left in place they still play in game and end up in the FOMOD. Ask once per export. null = cancelled.
  async function staleChoice() {
    const stale = await rpc('patch.staleExports', { id }).catch(() => []);
    if (!stale.length) return { removeStale: false };
    const groups = new Map();
    for (const f of stale) { if (!groups.has(f.why)) groups.set(f.why, []); groups.get(f.why).push(f.path); }
    const v = await modal('Files from earlier exports', [
      h('div', `This patch exported ${stale.length} file(s) earlier for lines it no longer exports. Left in place they still play in game and go into the FOMOD.`),
      h('div', h('b', 'Delete these files'), ' removes exactly the files listed below, and nothing else: no takes, no other files in those folders. ', h('b', 'Keep them'), ' leaves them where they are; this export still writes everything else.'),
      patch.exportApprovedOnly ? h('div.banner.warn', { style: { margin: 0 } }, 'Export is set to approved lines only (Folders & options), so lines you haven\'t approved count as not exported. Turn that off to export every line; approving is optional.') : null,
      h('div.stale-groups', [...groups].map(([why, paths]) => h('details', { open: groups.size === 1 },
        h('summary', h('b', `${paths.length} file(s)`), `: ${why}`),
        h('div.box.mono', { style: { whiteSpace: 'pre', maxHeight: '180px', overflow: 'auto', fontSize: '12px' } }, paths.slice(0, 300).join('\n') + (paths.length > 300 ? `\n… +${paths.length - 300} more` : ''))))),
    ], [{ label: 'Cancel', value: null }, { label: 'Keep them', value: 'keep' }, { label: `Delete these ${stale.length} file(s)`, primary: true, value: 'delete' }], { wide: true, help: 'exporting' });
    return v == null ? null : { removeStale: v === 'delete' };
  }

  async function exportWavLip(audioOnly) {
    try {
      const fc = audioOnly ? {} : await fixChoice(); if (!fc) return;
      const sc = await staleChoice(); if (!sc) return;
      const r = await rpc('patch.exportWavLip', { id, audioOnly, ...fc, ...sc });
      await filesModal(audioOnly ? 'WAV + LIP exported (DBVO Fix plugins and topic file left as they were)' : 'WAV + LIP exported', h('div.kv',
        h('div', 'Written'), h('div', r.written), r.removed ? [h('div', 'Removed (no longer exported)'), h('div', r.removed)] : null, h('div', 'Skipped (up to date, or not ours and Overwrite is off)'), h('div', r.skipped), h('div', 'Failed'), h('div', r.failed),
        r.errors && r.errors.length ? [h('div', 'Errors'), h('div.mono', r.errors.slice(0, 10).join('\n'))] : null), r.files);
    } catch (e) { fail(e); }
  }

  async function buildFuz(other) {
    let customFolder = '';
    if (other) {
      customFolder = await rpc('pick.folder', { title: 'Folder for the .fuz files' });
      if (!customFolder) return;
    }
    try {
      const fc = other ? {} : await fixChoice(); if (!fc) return;
      const sc = other ? {} : await staleChoice(); if (!sc) return;
      await rpc('patch.buildFuz', { id, customFolder, ...fc, ...sc }); renderActions();
    } catch (e) { fail(e); }
  }

  async function writeFix() {
    try {
      const fc = await fixChoice(); if (!fc) return;
      const written = await rpc('patch.writeFixPlugins', { id, ...fc });
      await modal('Files written', [h('div.box.mono', { style: { whiteSpace: 'pre-wrap' } }, written.length ? written.join('\n') : 'Nothing to write: no plugin needs a DBVO Fix.')]);
    } catch (e) { fail(e); }
  }

  async function renderBanners() {
    clear(banners);
    try {
      const c = await rpc('patch.checks', { id });
      const t = c.tools;
      const eng = engine();
      const missing = [(eng === 'elevenlabs' || eng === 'sts') && !t.apiKey && ['ElevenLabs API key', 'elevenlabs'], !t.ffmpeg && ['ffmpeg.exe', null], !t.faceFx && ['FaceFXWrapper.exe', 'facefx'], !t.fonix && ['FonixData.cdf', 'fonix'], !t.xwma && ['xWMAEncode.exe (for .fuz)', 'xwma']].filter(Boolean);
      if (missing.length) banners.append(h('div.banner.bad', h('span.row', { style: { gap: '6px', flexWrap: 'wrap' } }, 'Missing:', ...missing.map(([n, k], i) => h('span.row', { style: { gap: '2px' } }, n + (i < missing.length - 1 ? ',' : ''), k ? needInfo(k) : null))), h('div.spacer'), h('button.btn.sm', { title: 'Set the tool paths', onclick: () => go('settings', { tab: 'general' }) }, 'Open Global Settings')));
      const unset = [!patch.wavLipFolder && 'WAV + LIP folder', !patch.finalRoot && 'final folder', !patch.fixFolder && 'DBVO Fix folder', vc && !patch.sourceFolders.length && 'source WAV folder'].filter(Boolean);
      if (unset.length) banners.append(h('div.banner.warn', h('span', 'Not set for this patch: ' + unset.join(', ') + '.'), h('div.spacer'), h('button.btn.sm', { title: 'Choose where files go for this patch', onclick: openOptions }, 'Set folders')));
      for (const w of c.pathWarnings) banners.append(h('div.banner.warn', h('b', 'Path length: '), h('span', `${w.where} reaches ${w.length} of ${w.limit} characters with the longest line — tools like Yakitori fail past 260.`), h('div.spacer'), h('button.btn.sm', { title: 'Open Folders & options to pick shorter folders', onclick: openOptions }, 'Shorten folders')));
      if (vc) {
        const noSrc = patch.lines.filter((l) => !l.omitted && !l.hasSource).length;
        banners.append(h('div.banner.info', h('div.col', { style: { gap: '4px' } },
          h('span', h('b', 'Voice Changer is on. '), 'Lines are converted from another voice pack\'s recordings of this mod (the source folders in Folders & options), so delivery and timing come from the source. Only worth it when the mod is already DBVO-patched with another voice.'),
          noSrc ? h('span', h('b', `${noSrc} line(s) have no source file`), ' — usually lines added by a mod update after the source pack was made, or lines it never voiced. Generate them with text-to-speech (the voice pack\'s ', h('b', voice.engine === 'elevenlabs' ? 'ElevenLabs' : voice.engine), ' settings; turn the Voice Changer off to adjust those), or leave them unvoiced.') : h('span.dim', 'Every line has a source file.')),
          h('div.spacer'),
          noSrc ? switchEl(!!patch.ttsForMissing, async (on) => { try { patch = await rpc('patch.saveMeta', { id, ttsForMissing: on }); reindex(); renderAll(); } catch (e) { fail(e); } }, 'Generate lines without a source with text-to-speech', { title: 'Off: lines without a source stay unvoiced. On: Generate options ▾ → Missing also voices them with text-to-speech.' }) : null));
      }
      if (c.fuzOnly) banners.append(h('div.banner.info', h('span', `${c.fuzOnly} line(s) have no WAV + LIP here but a .fuz in the final folder. Import them as takes so they count as done and can be played.`),
        h('div.spacer'), h('button.btn.sm', { title: 'Unpack the .fuz files into WAV + LIP takes so they show as generated', onclick: async () => { try { await rpc('patch.importFuz', { id }); } catch (e) { fail(e); } } }, 'Import from .fuz')));
      if (c.unreviewed.length) banners.append(h('div.banner.warn', h('span', 'Flagged lines not reviewed in Phase 1: ' + c.unreviewed.map((u) => `${u.plugin} (${u.count})`).join(', ')),
        h('div.spacer'), patch.modIds.length === 1 ? h('button.btn.sm', { title: 'Open the mod this patch reads its lines from', onclick: () => go('mod', { id: patch.modIds[0] }) }, 'Open in Phase 1') : null));
    } catch { /* checks are advisory */ }
  }

  function renderHead() {
    const vs = app.state.voicePacks;
    const voiceSel = h('select', { title: 'Switch voice pack: opens the same mods with another voice' }, vs.map((v) => h('option', { value: v.id, selected: v.id === voice.id }, v.name)));
    voiceSel.addEventListener('change', async () => {
      if (app.job) { toast('Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.', true); voiceSel.value = voice.id; return; }
      try { const r = await rpc('patch.switchVoice', { id, voicePackId: voiceSel.value }); go('patch', { id: r.id }); } catch (e) { fail(e); }
    });
    clear(head).append(
      h('button.btn.ghost.sm', { title: 'Back to the list of DBVO Patches', onclick: () => go('patches') }, '← DBVO Patches'),
      h('h1', patch.name),
      helpButton('the-patch-page'),
      voiceChip(voice.name, voice.color),
      h('span.badge.b-skip', patch.category),
      switchEl(vc, (on) => toggleVc(on), 'Voice Changer', { title: 'Convert another voice pack\'s WAV / .fuz files of this mod into this voice (ElevenLabs speech-to-speech) instead of text-to-speech. Only useful when the mod is already DBVO-patched with another voice.' }),
      h('div.spacer'),
      creditsSlot,
      voiceSel,
      h('button.icon-btn', { title: 'Edit this voice pack (Global Settings)', onclick: () => go('settings', { tab: 'voices', id: voice.id }) }, '✎'),
      h('span.badge.b-skip', { title: voice.naming === 'Legacy' ? 'File names: Legacy (DBVO 1.0.1), from the line text. Set per voice pack.' : 'File names: DBVO 2.0 hashes. Set per voice pack.' }, voice.naming === 'Legacy' ? 'Legacy names' : 'DBVO 2.0 names'),
      foldersMenu(),
      h('button.btn.sm.ghost', { title: 'Re-read Phase 1 now (also happens every time you open this patch)', onclick: sync }, '⟳ Refresh'),
      h('button.btn.sm.ghost.danger', { title: 'Delete this patch and its takes', onclick: async () => { if (await confirmBox('Delete patch?', 'Deletes the patch and every take it made (their WAV and LIP files in the Library). Stays: the Phase 1 mods, the voice pack, and every WAV + LIP, .fuz and DBVO Fix file already exported to your folders; a new patch with the same WAV + LIP folder picks those WAV + LIP files up again as takes. A backup of the project files is made first (backups hold no audio).', 'Delete', true)) { await rpc('patch.delete', { id }); go('patches'); } } }, 'Delete'));
  }

  async function sync() {
    try {
      const r = await rpc('patch.sync', { id });
      patch = r.patch;
      reindex();
      grid.setRows(visible());
      renderCounts();
      renderActions();
      const rep = r.report;
      toast(`${rep.total} lines · ${rep.added} new · ${rep.removed} removed${rep.parked ? ` (takes of ${rep.parked} kept in case they come back)` : ''} · ${rep.omitted} omitted${rep.stale ? ` · ${rep.stale} edited since take` : ''}`);
    } catch (e) { fail(e); }
  }

  async function toggleVc(on) {
    if (app.job) { toast('Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.', true); renderHead(); return; }
    try {
      if (!on) {
        if (!(await confirmBox('Turn off the Voice Changer?', 'Lines are generated with text-to-speech again. Converted takes stay as they are, and the source folders are remembered for later.', 'Turn off'))) { renderHead(); return; }
        await rpc('patch.saveMeta', { id, kind: 'tts' });
        go('patch', { id });
        return;
      }
      const sources = [...(patch.sourceFolders || [])];
      const srcList = h('div.col');
      const renderSrc = () => clear(srcList).append(sources.length ? sources.map((f, i) => h('div.row', h('span.mono.grow.ellipsis', f), h('button.icon-btn', { title: 'Remove this folder', onclick: () => { sources.splice(i, 1); renderSrc(); } }, '×'))) : h('div.faint', 'No folder yet.'));
      renderSrc();
      const ok = await modal('Turn on the Voice Changer?', [
        h('div.help',
          h('p', 'The Voice Changer converts recordings that ', h('b', 'another voice pack'), ' already has for this mod into this voice (ElevenLabs speech-to-speech). Delivery, emotion and timing come from the source; only the voice changes. A new .lip is made from the text.'),
          h('p', h('b', 'Use it only when this mod is already DBVO-patched with another voice. '), 'Lines that pack never recorded have nothing to convert, so they would stay missing. Typical gaps are lines added by a mod update after the source pack was made. You can generate those with text-to-speech, or leave them unvoiced.'),
          h('p', 'Billed by audio length instead of characters. Existing takes stay; the DBVO Fix plugin and topic file are written as usual.')),
        h('div.col', h('div.field-label', 'Source folders: the other voice pack\'s WAV or .fuz files for this mod'), srcList,
          h('button.btn.sm', { style: { alignSelf: 'flex-start' }, title: 'Searched with sub-folders; files are matched by Legacy and DBVO 2.0 names', onclick: async () => { const f = await rpc('pick.folder', { title: 'Folder with the other voice pack\'s WAV or .fuz files' }); if (f) { sources.push(f); renderSrc(); } } }, '+ Add folder')),
      ], [{ label: 'Cancel', value: false }, { label: 'Turn on', primary: true, value: true }], { help: 'voice-changer' });
      if (!ok) { renderHead(); return; }
      if (!sources.length) { toast('Add at least one source folder.', true); renderHead(); return; }
      await rpc('patch.saveMeta', { id, kind: 'vc', sourceFolders: sources });
      const r = await rpc('patch.matchSources', { id });
      const miss = r.total - r.found;
      toast(`${r.found} of ${r.total} lines have a source file.` + (miss ? ` ${miss} without one can be generated with text-to-speech (see the banner).` : ''), miss > 0);
      go('patch', { id });
    } catch (e) { fail(e); renderHead(); }
  }

  async function openOptions() {
    await refreshState();
    const s = app.state;
    const name = h('input', { type: 'text', value: patch.name });
    const refOn = new Set(patch.referenceIds);
    // The Phase 1 mods this patch reads. A mod deleted and imported again is a new mod: tick it here and the takes the
    // patch kept aside for its lines come back.
    const modOn = new Set(patch.modIds.filter((m) => s.mods.some((x) => x.id === m)));
    const goneMods = patch.modIds.length - modOn.size;
    const sources = [...(patch.sourceFolders || [])];
    let owWav = patch.overwriteWavLip, owFuz = patch.overwriteFuz, owFix = patch.overwriteFix !== false;
    let apprOnly = !!patch.exportApprovedOnly;
    const modName = Object.values(patch.modNames || {})[0] || patch.name;
    const expand = (t) => (t || '').replace(/\{mod\}/gi, modName).replace(/\{packId\}/gi, voice.packId || 'packId').replace(/\{voicePack\}/gi, voice.name).replace(/\{patch\}/gi, name.value || patch.name);
    const preview = h('div.opt-preview.mono');
    const folder = (val, title, ph) => {
      const i = h('input', { type: 'text', value: val || '', placeholder: ph });
      i.addEventListener('input', renderPreview);
      return { i, el: h('div.path-input', i, h('button.btn.sm', { onclick: async () => { const f = await rpc('pick.folder', { title }); if (f) { i.value = f; renderPreview(); } } }, 'Browse')) };
    };
    const wl = folder(patch.wavLipFolder, 'WAV + LIP folder', 'C:\\DBVO Dev\\MyVoice - Example Mod\\RawWav');
    const fx = folder(patch.fixFolder, 'DBVO Fix folder', 'C:\\DBVO Dev\\MyVoice - Example Mod\\Fix');
    const fr = folder(patch.finalRoot, 'Final folder', 'C:\\DBVO MyVoice FOMOD\\04 Followers');
    // Same rules as the app uses when writing: relative to the final folder, ending in Sound\DBVO\<voice pack ID>.
    const normTpl = (t) => {
      let v = (t || '').trim().replace(/\//g, '\\');
      if (/^[a-z]:|^\\\\/i.test(v)) {
        const root = fr.i.value.trim().replace(/\//g, '\\').replace(/\\+$/, '');
        v = root && (v.toLowerCase() === root.toLowerCase() || v.toLowerCase().startsWith(root.toLowerCase() + '\\')) ? v.slice(root.length) : '';
      }
      v = v.replace(/^[\\\s]+|[\\\s]+$/g, '');
      if (!v) return 'Sound\\DBVO\\{packId}';
      if (!v.split('\\').some((x) => x.toLowerCase() === 'sound')) v += '\\Sound\\DBVO\\{packId}';
      return v;
    };
    const tpl = h('input', { type: 'text', value: normTpl(patch.fuzTemplate), placeholder: 'Sound\\DBVO\\{packId}' });
    tpl.addEventListener('input', renderPreview);
    tpl.addEventListener('change', () => { const n = normTpl(tpl.value); if (n !== tpl.value.trim()) { tpl.value = n; toast('The .fuz sub-folder is relative to the final folder and ends in Sound\\DBVO\\<voice pack ID>: corrected.'); } renderPreview(); });
    function renderPreview() {
      const root = fr.i.value.trim() || '<final folder>';
      const sub = expand(normTpl(tpl.value));
      const i = sub.toLowerCase().indexOf('sound\\');
      const modFolder = i > 0 ? root + '\\' + sub.slice(0, i).replace(/\\$/, '') : root;
      clear(preview).append(
        h('div', h('span.faint', 'WAV + LIP → '), `${wl.i.value.trim() || '<WAV + LIP folder>'}\\`),
        h('div', h('span.faint', 'Phase 2 DBVO Fix + Topic File → '), `${fx.i.value.trim() || '<DBVO Fix folder>'}\\`),
        h('div', h('span.faint', 'Final DBVO Fix + Topic File → '), `${modFolder}\\`),
        h('div', h('span.faint', '.fuz files → '), `${root}\\${sub}\\`));
    }
    const srcList = h('div.col');
    const renderSrc = () => clear(srcList).append(sources.length ? sources.map((f, i) => h('div.row', h('span.mono.grow.ellipsis', f), h('button.icon-btn', { title: 'Remove this source folder', onclick: () => { sources.splice(i, 1); renderSrc(); } }, '×'))) : h('div.faint', 'No folder yet.'));
    renderSrc();
    renderPreview();
    const row = (label, hint, ...field) => h('div.opt-row', h('div.opt-label', label), h('div.col', { style: { gap: '5px', minWidth: 0 }, title: typeof hint === 'string' ? hint : label }, ...field, hint ? h('div.opt-hint', hint) : null));
    const ok = await modal(h('span.row', 'Folders & options — ', voiceChip(voice.name + ' voice pack', voice.color)), [
      h('div.opt-grid',
        row('Patch name', null, name),
        vc ? row('Voice Changer sources', 'Another voice pack\'s WAV / .fuz for this mod', srcList,
          h('button.btn.sm', { style: { alignSelf: 'flex-start' }, onclick: async () => { const f = await rpc('pick.folder', { title: 'Folder with the source WAV files' }); if (f) { sources.push(f); renderSrc(); } } }, '+ Add folder')) : null,
        row('WAV + LIP folder', 'Your copy of the takes as .wav + .lip', wl.el),
        row('DBVO Fix folder', 'Your copy of the Fix plugin + topic file', fx.el),
        row('Final folder', 'The FOMOD root (or a page of it). The final DBVO Fix plugin and its topic file go here too.', fr.el),
        row('.fuz sub-folder', 'Inside the final folder, e.g. Sound\\DBVO\\{packId}. {packId} = the voice pack ID (' + (voice.packId || 'not set') + ')', tpl),
        row('Result', null, preview),
        row('Overwrite', 'Files this patch exported are always updated when their line has a new take. Off: other files already there (e.g. from another tool) are left alone; for DBVO Fix you are asked',
          h('div.row', { style: { gap: '18px' } }, switchEl(owWav, (v) => { owWav = v; }, 'WAV + LIP'), switchEl(owFuz, (v) => { owFuz = v; }, '.fuz'),
            switchEl(owFix, (v) => { owFix = v; }, 'DBVO Fix', { title: 'DBVO Fix plugins in the final folder. Off: you are asked before one with the same name is replaced. The working copy in the DBVO Fix folder is always replaced.' }))),
        row('Export', 'Off (the usual): every line with a take is exported; approving is optional. On: only lines approved in Review (▶ Review, then A). Files of lines not approved that earlier exports wrote are listed before anything is deleted.',
          switchEl(apprOnly, (v) => { apprOnly = v; }, 'Only lines approved in Review', { title: 'Export and build .fuz for approved lines only. Approving never changes anything else.' })),
        row('Mods', 'The Phase 1 mods this patch voices. Lines of a mod you untick leave the patch, but their takes are kept and come back if you tick it again.',
          h('div.col', { style: { gap: '4px' } },
            goneMods ? h('div', { style: { color: 'var(--warn)', fontSize: '12.5px' } }, `${goneMods} mod(s) of this patch were deleted in Phase 1. Imported again, they are new mods: tick them here to bring their lines (and the kept takes) back.`) : null,
            h('div.box', { style: { maxHeight: '170px', overflow: 'auto', padding: '6px 8px', display: 'flex', flexDirection: 'column', gap: '6px', alignItems: 'flex-start' } }, s.mods.map((m) => switchEl(modOn.has(m.id), (v) => { if (v) modOn.add(m.id); else modOn.delete(m.id); }, `${m.name}  ·  ${m.category || ''}`))))),
        row('Omit lines users have', 'Only packs every user installs',
          s.references.length ? h('div.col', { style: { gap: '4px' } }, s.references.map((r) => switchEl(refOn.has(r.id), (v) => { if (v) refOn.add(r.id); else refOn.delete(r.id); }, `${r.name}  ·  ${r.lineCount} lines`)))
            : h('div.faint', 'None yet: Global Settings → Omit references.'))),
    ], [{ label: 'Cancel', value: false }, { label: 'Save', primary: true, value: true }], { wide: true, help: 'folders-options' });
    if (!ok) return;
    if (!modOn.size) { toast('A patch needs at least one mod: nothing was saved.', true); return; }
    const modIds = [...modOn];
    try {
      patch = await withBusy(rpc('patch.saveMeta', {
        id, name: name.value, modIds, referenceIds: [...refOn],
        wavLipFolder: wl.i.value.trim(), finalRoot: fr.i.value.trim(), fuzTemplate: normTpl(tpl.value), fixFolder: fx.i.value.trim(),
        overwriteWavLip: owWav, overwriteFuz: owFuz, overwriteFix: owFix, sourceFolders: sources, exportApprovedOnly: apprOnly,
      }), 'Loading voice lines', 'Looking for WAV + LIP files in the folders and matching them to the lines…');
      if (vc) {
        const r = await withBusy(rpc('patch.matchSources', { id }), 'Matching source files', 'Looking for the other voice pack\'s file for every line…');
        patch = r.patch;
        toast(`${r.found} of ${r.total} lines have a source WAV.`);
      }
      reindex();
      renderAll();
    } catch (e) { fail(e); }
  }

  async function loadEngine(refresh = false) {
    const eng = engine();
    const before = JSON.stringify([voices, models, localVoices]);
    try {
      if (eng === 'voxcpm' || eng === 'omnivoice') {
        localVoices = await rpc('engine.voices', { engine: eng }).catch(() => []);
      } else if (eng === 'sts') {
        [voices, models] = await Promise.all([rpc('eleven.voices', { refresh }), rpc('eleven.stsModels', {})]);
      } else {
        [voices, models] = await Promise.all([rpc('eleven.voices', { refresh }), rpc('eleven.models', { refresh })]);
      }
    } catch (e) {
      if (app.state.settings.hasApiKey) fail(e);
    }
    engineCache.set(eng, { voices, models, localVoices });
    // Only redraw when something changed, so the toolbar doesn't flicker.
    if (refresh || JSON.stringify([voices, models, localVoices]) !== before) { renderToolbar(); renderTagbar(); }
  }

  // live updates from generation
  on('line2', (d) => {
    if (d.patchId !== id || !document.body.contains(view)) return;
    replaceLine(d.line, true);
    if (autoPlay && d.line.lineId === autoPlay.lineId && d.line.takes.length > autoPlay.takes && d.line.selectedTakeId) { autoPlay = null; play(d.line); }
  });
  let jobSeen = false;
  document.addEventListener('job-progress', () => { if (!jobSeen && document.body.contains(view)) { jobSeen = true; grid.paint(true); renderActions(); } });
  document.addEventListener('job-done', async (e) => {
    jobSeen = false;
    if (!document.body.contains(view)) return;
    // The last take's line can arrive just after the job ends: it still flashes.
    if (inFlight.size) { for (const x of inFlight) finishing.add(x); setTimeout(() => finishing.clear(), 5000); markInFlight(null); }
    if (e.detail && (e.detail.kind === 'import' || e.detail.kind === 'qa') && e.detail.patchId === id) {
      patch = await rpc('patch.get', { id });
      reindex();
      grid.setRows(visible());
      renderCounts();
      renderInspector();
    }
    // Rows drawn while the job ran have their buttons disabled: redraw them.
    grid.paint(true);
    if (!insp.contains(document.activeElement)) renderInspector();
    renderActions();
    renderBanners();
  });

  function renderAll() {
    renderHead();
    renderToolbar();
    renderTagbar();
    renderBanners();
    renderCounts();
    grid.setRows(visible());
    renderInspector();
    renderActions();
  }

  renderAll();
  loadEngine();
  return view;
}
