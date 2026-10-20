// Phase 2: DBVO Patches (voice pack x mods) and Voice Changer patches — generation grid, takes, export.
import { rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, debounce, diffAdded, switchEl, segmented, popupMenu, columnsMenu, tip, icon } from './lib.js';
import { app, go, refreshState } from './app.js';

const STATUS_TEXT = { Pending: 'Not generated', Generating: 'Generating…', Generated: 'Generated', Failed: 'Failed', Omitted: 'Omitted', Stale: 'Text changed since take' };

const LANGS = [['', 'Auto'], ['en', 'en · English'], ['de', 'de · German'], ['fr', 'fr · French'], ['es', 'es · Spanish'], ['it', 'it · Italian'],
  ['pt', 'pt · Portuguese'], ['pl', 'pl · Polish'], ['ru', 'ru · Russian'], ['ja', 'ja · Japanese'], ['zh', 'zh · Chinese'], ['ko', 'ko · Korean'],
  ['nl', 'nl · Dutch'], ['sv', 'sv · Swedish'], ['cs', 'cs · Czech'], ['tr', 'tr · Turkish'], ['uk', 'uk · Ukrainian'], ['hu', 'hu · Hungarian']];

const ENGINES = [['elevenlabs', 'ElevenLabs'], ['voxcpm', 'VoxCPM (local)'], ['omnivoice', 'OmniVoice (local)']];

const pct = (p) => { const n = p.total - p.omitted; return n ? Math.round((100 * p.generated) / n) : 0; };

/** Ten voice pack colours that sit well on the dark blue-grey theme. */
export const VOICE_COLORS = [
  ['#58a6ff', 'Blue'], ['#56d4dd', 'Sky'], ['#39c5cf', 'Teal'], ['#3fb950', 'Green'], ['#a5d64c', 'Lime'],
  ['#d29922', 'Amber'], ['#f0883e', 'Orange'], ['#f85149', 'Red'], ['#ff7b9c', 'Rose'], ['#8b949e', 'Slate'],
];

let creditsCache = null; // { at, data }
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

export async function patchesView(kind = 'tts') {
  const s = await refreshState();
  const vcPage = kind === 'vc';
  // The Phase 2 page lists every patch (Voice Changer ones carry a badge); the Voice Changer page only those.
  const patches = s.patches.filter((p) => !vcPage || p.kind === 'vc');
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', vcPage ? 'Voice Changer' : 'DBVO Patches'),
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
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { onclick: () => newPatch('vc') }, '+ New Voice Changer patch'))));
    return view;
  }
  if (!s.voicePacks.length) {
    body.append(h('div.empty', h('h2', 'Create a voice pack first'), h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { onclick: () => go('settings', { tab: 'voices', id: 'new' }) }, '+ New voice pack'))));
    return view;
  }
  let dragId = null;

  const card = (p) => {
    const n = p.total - p.omitted;
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
  if (!s.voicePacks.length) { toast('Create a voice pack first.', true); go('settings', { tab: 'voices', id: 'new' }); return; }
  if (!s.mods.length) { toast('Create a mod in Phase 1 first.', true); go('mods'); return; }
  const name = h('input', { type: 'text', placeholder: vc ? 'leave empty for "<mod> (Voice Changer)"' : 'leave empty for the mod\'s name' });
  const voice = h('select', s.voicePacks.map((v) => h('option', { value: v.id, selected: v.id === s.settings.lastVoicePackId }, v.name)));
  const modBoxes = s.mods.map((m) => ({ m, box: h('input', { type: 'checkbox' }) }));
  const sources = [];
  const srcList = h('div.col');
  const renderSrc = () => clear(srcList).append(sources.length ? sources.map((f, i) => h('div.row', h('span.mono.grow.ellipsis', f), h('button.icon-btn', { onclick: () => { sources.splice(i, 1); renderSrc(); } }, '×'))) : h('div.faint', 'No folder yet.'));
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
    const r = await rpc('patch.sync', { id }); // pull in Phase 1 edits automatically
    patch = r.patch;
    if (r.report.adopted) setTimeout(() => toast(`${r.report.adopted} line(s) already had WAV + LIP files in the WAV + LIP folder: added as take #1.`), 300);
  } catch {
    patch = await rpc('patch.get', { id });
  }
  const vc = patch.kind === 'vc';
  let voice = app.state.voicePacks.find((v) => v.id === patch.voicePackId);
  if (!voice) throw new Error('This patch\'s voice pack was deleted.');
  const ui = store.get('patch:' + id, { filter: 'all' });
  let filter = ui.filter || 'all';
  let search = '';
  let focusedId = null;
  let voices = [], models = [], localVoices = [];
  let showInsp = store.get('insp2', true);
  let lastEdit = null; // { el, line } — the last spoken-text box the user was in
  const audio = new Audio();
  const engine = () => (vc ? 'sts' : voice.engine || 'elevenlabs');
  // With the Voice Changer on, a line needs a source file, unless text-to-speech for such lines is turned on.
  const canGen = (l) => !vc || l.hasSource || !!patch.ttsForMissing;

  const view = h('div.view');
  const head = h('div.view-head');
  const tb = h('div.toolbar');
  const tagbar = h('div.tagbar');
  const banners = h('div');
  const filters = h('div.filters');
  const split = h('div.split' + (showInsp ? '' : '.no-insp'));
  const left = h('div.left');
  const insp = h('div.inspector');
  split.append(left, insp);
  const actions = h('div.statusbar');
  view.append(head, tb, tagbar, banners, filters, split, actions);

  let index = new Map();
  const reindex = () => { index = new Map(patch.lines.map((l) => [l.lineId, l])); };
  reindex();

  const statusDot = (l) => h('span.dot.' + (l.omitted ? 'Omitted' : l.stale && l.status === 'Generated' ? 'Stale' : l.status),
    { title: l.omitted ? `Omitted — already in ${l.omittedBy}` : (STATUS_TEXT[l.status] || l.status) + (l.lastError ? ': ' + l.lastError : '') });

  const grid = new VGrid(left, {
    storageKey: vc ? 'vc' : 'phase2',
    rowKey: (l) => l.lineId,
    rowClass: (l) => (l.omitted || !canGen(l) ? 'muted' : ''),
    onRowClick: (l) => { focusedId = l.lineId; renderInspector(); },
    onRowDblClick: (l) => play(l),
    columns: [
      { id: 'sel', title: '', width: 32, resizable: false, render: (l) => grid.checkbox(l) },
      { id: 'orig', title: 'Original mod line', width: 240, render: (l) => h('span.dim', { title: l.originalLine }, l.originalLine) },
      { id: 'file', title: 'Filename', flex: 1, width: 240, render: (l) => h('span.faint.mono', { title: `In-game line: ${l.finalLine}\nFile: ${l.stem}.wav / .lip / .fuz` }, l.editedInPhase1 ? h('span.mark.info', { title: 'Edited in Phase 1' }, 'P1 ') : null, l.stem + '.wav') },
      vc ? { id: 'src', title: 'Source', width: 80, render: (l) => l.hasSource ? h('span.row', h('button.icon-btn', { title: 'Play the source: ' + l.sourceWav, onclick: () => playSource(l) }, '▶'), h('span.badge.b-valid', { title: l.sourceWav }, l.sourceWav.toLowerCase().endsWith('.fuz') ? '.fuz' : '✓')) : h('span.badge.b-broken', { title: `No ${l.stem}.wav or .fuz (Legacy or DBVO 2.0 name) in the source folders. New lines from a mod update have no source until the source pack has them.` }, patch.ttsForMissing ? 'TTS' : 'missing') } : null,
      { id: 'spoken', title: vc ? 'Text for the LIP (and TTS without a source)' : 'Spoken text (tags anywhere)', flex: 1.3, width: 320, render: (l) => spokenCell(l) },
      { id: 'take', title: 'Take', width: 64, resizable: false, render: (l) => h('span.row', { style: { gap: '6px' } }, statusDot(l),
        h('span.take-lbl', { title: l.takes.length ? `${l.takes.length} take(s); #${(l.takes.find((t) => t.id === l.selectedTakeId) || l.takes[0]).slot} is used` : 'No take yet' },
          l.takes.length ? '#' + ((l.takes.find((t) => t.id === l.selectedTakeId) || l.takes[0]).slot) + (l.takes.length > 1 ? '/' + l.takes.length : '') : '—')) },
      { id: 'act', title: '', width: 98, resizable: false, render: (l) => h('span.row', { style: { gap: '2px' } },
        h('button.act-btn', { title: l.omitted ? 'Omitted: a reference topic file already has this line' : app.job ? 'A job is running (see the top bar)' : !canGen(l) ? 'No source WAV or .fuz with this line\'s name. Turn on "Generate lines without a source with text-to-speech" in the Voice Changer banner to voice it.' : vc && !l.hasSource ? 'Generate with text-to-speech (no source file)' : vc ? 'Convert again (new take)' : 'Generate a new take (no confirmation for one line)',
          disabled: l.omitted || !!app.job || !canGen(l), onclick: () => generate('selected', [l.lineId]) }, icon('regen')),
        h('button.act-btn', { title: l.takes.length ? `Takes (${l.takes.length}): play, switch, delete` : 'No takes yet', disabled: !l.takes.length, onclick: () => takesModal(l) }, icon('takes')),
        h('button.act-btn', { title: l.selectedTakeId ? 'Play (or double-click the row)' : vc ? 'No take yet: convert the line first' : 'No take yet: generate the line first', disabled: !l.selectedTakeId, onclick: () => play(l) }, icon('play'))) },
      { id: 'mark', title: '', width: 34, resizable: false, render: (l) => h('button.act-btn' + (l.marked ? '.on' : ''), { title: l.marked ? 'Marked: click to unmark' : 'Mark this line (kept until you unmark it; "Marked lines" generates them)', onclick: () => upd(l, 'mark', !l.marked) }, icon('star', { filled: l.marked })) },
    ].filter(Boolean),
  });
  grid.el.addEventListener('selection', () => renderActions());

  function trackCaret(el, l) {
    el.addEventListener('focus', () => { lastEdit = { el, line: l }; });
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
    inp.addEventListener('change', () => { if (inp.value !== l.spoken) upd(l, 'spoken', inp.value); });
    inp.addEventListener('focus', () => { if (focusedId !== l.lineId) { focusedId = l.lineId; grid.focus(l.lineId); renderInspector(); } });
    inp.addEventListener('keydown', (e) => {
      e.stopPropagation();
      if (e.key === 'Enter') inp.blur();
      if (e.key === 'Escape') { inp.value = l.spoken; inp.blur(); }
    });
    trackCaret(inp, l);
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
      if (!url) { toast('The source file is gone.', true); return; }
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
        t.id !== line.selectedTakeId ? h('button.btn.sm', { title: 'Use this take for export and .fuz', onclick: async () => { await upd(line, 'selectTake', t.id); render(); } }, 'Use') : null,
        h('button.icon-btn', { title: 'Delete this take and its files', onclick: async () => { await upd(line, 'deleteTake', t.id); render(); } }, '🗑'))));
    };
    render();
    await modal(`Takes · ${l.finalLine}`, [list,
      h('div.row', h('button.btn.sm', { title: 'Delete every take of this line except the one in use', onclick: async () => {
        try { const r = await rpc('patch.purgeTakes', { id, lineIds: [l.lineId] }); patch = r.patch; reindex(); grid.setRows(visible()); render(); toast(`${r.removed} take(s) deleted.`); } catch (e) { fail(e); }
      } }, 'Keep only the used take'),
      h('button.btn.sm', { title: 'Make another take of this line', onclick: () => { document.getElementById('modal').classList.add('hidden'); generate('selected', [l.lineId]); } }, vc ? 'Convert again' : 'New take'))]);
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

  async function upd(l, action, value) {
    try {
      replaceLine(await rpc('line2.update', { id, lineId: l.lineId, action, value }));
    } catch (e) { fail(e); }
  }

  function replaceLine(nl) {
    const i = patch.lines.findIndex((x) => x.lineId === nl.lineId);
    if (i >= 0) patch.lines[i] = nl;
    index.set(nl.lineId, nl);
    const ri = grid.rows.findIndex((x) => x.lineId === nl.lineId);
    if (ri >= 0) grid.rows[ri] = nl;
    if (lastEdit && lastEdit.line.lineId === nl.lineId) lastEdit.line = nl;
    grid.refreshRow(nl.lineId);
    if (focusedId === nl.lineId && !insp.contains(document.activeElement)) renderInspector();
    renderCounts();
    renderActions();
  }

  // ---- toolbar: the voice pack's engine settings, saved on every change
  const saveVoice = debounce(async () => {
    try {
      voice = await rpc('voice.save', { voice });
      const i = app.state.voicePacks.findIndex((v) => v.id === voice.id);
      if (i >= 0) app.state.voicePacks[i] = voice;
    } catch (e) { fail(e); }
  }, 300);

  function setIn(obj, key, value) {
    if (voice.settingsLocked) { toast('This voice pack\'s settings are locked. Unlock them in Global Settings → Voice packs.', true); renderToolbar(); return; }
    obj[key] = value;
    saveVoice();
    renderToolbar();
  }
  const setE = (k, v) => setIn(voice.eleven, k, v);

  const isTagModel = (m) => /^eleven_v[34]/i.test(m || '');
  const modelInfo = (m) => models.find((x) => x.modelId === m) || {};
  const grp = (label, ...kids) => h('div.tgroup', h('label', label), h('div.row', ...kids));
  const slider = (obj, key, min, max, step, label, locked) => {
    const val = h('span.mono.dim', { style: { width: '34px' } }, Number(obj[key]).toFixed(2));
    const r = h('input', { type: 'range', min, max, step, value: obj[key], disabled: locked, style: { width: '96px' } });
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
    const sel = h('select', { disabled: voice.settingsLocked, style: { maxWidth: '200px' }, onchange: (ev) => { const v = list.find((x) => x.voiceId === ev.target.value); if (nameKey) obj[nameKey] = v ? v.name : ''; setIn(obj, idKey, ev.target.value); } },
      h('option', { value: '' }, list.length ? '— pick a voice —' : ((nameKey && obj[nameKey]) || 'Loading voices…')),
      list.map((v) => h('option', { value: v.voiceId, selected: v.voiceId === obj[idKey] }, v.name)));
    if (obj[idKey] && !list.some((v) => v.voiceId === obj[idKey])) sel.append(h('option', { value: obj[idKey], selected: true }, (nameKey && obj[nameKey]) || obj[idKey]));
    return grp(label, sel, h('button.icon-btn', { title: 'Reload voices and models', onclick: () => loadEngine(true) }, '⟳'));
  };

  function renderToolbar() {
    const locked = voice.settingsLocked;
    const eng = engine();
    const engineSel = vc ? null : grp('Engine', h('select', { disabled: locked, onchange: (ev) => { setIn(voice, 'engine', ev.target.value); loadEngine(); } },
      ENGINES.map(([v, t]) => h('option', { value: v, selected: v === (voice.engine || 'elevenlabs') }, t))));
    clear(tb);
    if (eng === 'sts') {
      const st = voice.sts;
      const fmt = h('select', { disabled: locked, onchange: (ev) => setIn(st, 'outputFormat', ev.target.value) },
        [['mp3_44100_128', 'MP3 44.1k 128'], ['mp3_44100_192', 'MP3 44.1k 192'], ['pcm_44100', 'PCM 44.1k (Pro plan)'], ['pcm_24000', 'PCM 24k'], ['pcm_22050', 'PCM 22k'], ['pcm_16000', 'PCM 16k']].map(([v, t]) => h('option', { value: v, selected: v === st.outputFormat }, t)));
      const modelSel = h('select', { disabled: locked, onchange: (ev) => setIn(st, 'modelId', ev.target.value) },
        (models.length ? models : [{ modelId: st.modelId }]).map((m) => h('option', { value: m.modelId, selected: m.modelId === st.modelId }, m.modelId)));
      tb.append(voicePicker(st, 'voiceId', 'voiceName', voices, 'Target voice'), grp('Model', modelSel),
        slider(st, 'stability', 0, 1, 0.01, 'Stability', locked), slider(st, 'similarity', 0, 1, 0.01, 'Similarity', locked), slider(st, 'style', 0, 1, 0.01, 'Style', locked),
        grp('Speaker boost', switchEl(st.speakerBoost, (v) => setIn(st, 'speakerBoost', v), '', { disabled: locked, title: 'Boosts similarity to the target voice; slightly slower' })),
        grp('Remove background noise', switchEl(st.removeBackgroundNoise, (v) => setIn(st, 'removeBackgroundNoise', v), '', { disabled: locked, title: 'Strip background noise from the source before converting' })),
        grp('Format', fmt), numIn(st, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px' }),
        numIn(st, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'Your ElevenLabs plan\'s concurrency limit' }));
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
        [['mp3_44100_128', 'MP3 44.1k 128'], ['mp3_44100_192', 'MP3 44.1k 192'], ['pcm_44100', 'PCM 44.1k (lossless)']].map(([v, t]) => h('option', { value: v, selected: v === e.outputFormat }, t)));
      const lang = h('select', { disabled: locked || !langOk, title: langOk ? 'Force the language (ISO 639-1). Auto lets the model detect it.' : 'This model does not accept a language code.', onchange: (ev) => setE('languageCode', ev.target.value) },
        LANGS.map(([v, t]) => h('option', { value: v, selected: v === (e.languageCode || '') }, t)));
      const norm = h('select', { disabled: locked, title: 'Text normalization: whether ElevenLabs writes out numbers, dates and abbreviations before speaking ("1st" → "first", "Dr." → "Doctor"). Auto lets the model decide; On is safer for numbers; Off keeps the text exactly as written.', onchange: (ev) => setE('textNormalization', ev.target.value) },
        [['auto', 'Auto'], ['on', 'On'], ['off', 'Off']].map(([v, t]) => h('option', { value: v, selected: v === e.textNormalization }, t)));
      tb.append(engineSel, voicePicker(e, 'voiceId', 'voiceName', voices), grp('Model', modelSel), stability,
        slider(e, 'similarity', 0, 1, 0.01, 'Similarity', locked),
        supportsStyle ? slider(e, 'style', 0, 1, 0.01, 'Style', locked) : null,
        supportsBoost ? grp('Speaker boost', switchEl(e.speakerBoost, (v) => setE('speakerBoost', v), '', { disabled: locked, title: 'Boosts similarity to the voice; slightly slower' })) : null,
        slider(e, 'speed', 0.7, 1.2, 0.01, 'Speed', locked),
        grp('Language', lang), grp('Read numbers ⓘ', norm), grp('Format', fmt),
        numIn(e, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px', title: 'Same seed + same text + same settings ≈ same take. Empty = random.' }),
        numIn(e, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'How many lines are requested at once. Match your ElevenLabs plan\'s concurrency limit; the app slows down automatically on "too many requests".' }));
    }
    if (locked) tb.append(h('span.badge.b-toolong', { title: 'Unlock in Global Settings → Voice packs' }, '🔒 locked'));
    if (eng === 'elevenlabs' || eng === 'sts') tb.append(h('div.spacer'), creditsButton());
  }

  // ---- ElevenLabs credits (cached for a minute across pages)
  function creditsButton() {
    const btn = h('button.btn.sm.credits-btn', { title: 'ElevenLabs credits left this period' }, icon('coins', { size: 14 }), h('b', '…'), h('span.faint', 'credits'));
    const fill = (c) => {
      const left = Math.max(0, c.characterLimit - c.characterCount);
      btn.children[1].textContent = left.toLocaleString();
      btn.title = `${left.toLocaleString()} of ${c.characterLimit.toLocaleString()} credits left` + (c.nextReset ? ` · resets ${new Date(c.nextReset * 1000).toLocaleDateString()}` : '');
    };
    loadCredits().then(fill).catch(() => { btn.children[1].textContent = '—'; btn.title = 'Could not read the credits (check the API key in Global Settings)'; });
    btn.addEventListener('click', async () => {
      let c;
      try { c = await loadCredits(true); fill(c); } catch (e) { fail(e); return; }
      const left = Math.max(0, c.characterLimit - c.characterCount);
      const pct = c.characterLimit ? Math.round((100 * left) / c.characterLimit) : 0;
      const pend = patch.lines.filter(pendingLine).reduce((a, l) => a + l.spoken.length, 0);
      popupMenu(btn, [
        { label: `${left.toLocaleString()} credits left (${pct}%)`, sub: `${c.characterCount.toLocaleString()} of ${c.characterLimit.toLocaleString()} used · plan: ${c.tier}${c.nextReset ? ' · resets ' + new Date(c.nextReset * 1000).toLocaleDateString() : ''}`, onclick: () => {} },
        { label: `This patch still needs ≈ ${pend.toLocaleString()}`, sub: pend > left ? 'More than you have left this period' : 'Fits in what you have left', onclick: () => {} },
        { label: 'Refresh', sub: 'Read the credits from ElevenLabs again', onclick: () => { creditsCache = null; renderToolbar(); } },
      ]);
    });
    return btn;
  }

  function folderButton() {
    const btn = h('button.btn.sm.ghost', { title: 'Open this patch\'s folders in Explorer' }, icon('folder', { size: 14 }), ' Open folder ▾');
    btn.addEventListener('click', () => {
      const items = [
        patch.wavLipFolder ? { label: 'WAV + LIP folder', sub: patch.wavLipFolder, onclick: () => rpc('open.path', { path: patch.wavLipFolder }).catch(fail) } : null,
        patch.finalRoot ? { label: 'Final folder (.fuz)', sub: patch.finalRoot, onclick: () => rpc('open.path', { path: patch.finalRoot }).catch(fail) } : null,
        patch.fixFolder ? { label: 'DBVO Fix folder', sub: patch.fixFolder, onclick: () => rpc('open.path', { path: patch.fixFolder }).catch(fail) } : null,
        { label: 'Takes (this app\'s library)', sub: 'Every generated take of this patch', onclick: () => rpc('patch.openTakes', { id }).catch(fail) },
      ].filter(Boolean);
      if (items.length === 1) toast('Set the folders in Folders & options first.', true);
      popupMenu(btn, items);
    });
    return btn;
  }

  // ---- tags: insert at the caret, or prefix the ticked rows
  function insertAtCaret(t) {
    const { el, line } = lastEdit;
    const v = el.value;
    const a = el.selectionStart ?? v.length, b = el.selectionEnd ?? a;
    const before = v.slice(0, a), after = v.slice(b);
    const ins = (before && !/\s$/.test(before) ? ' ' : '') + t + (after && !/^\s/.test(after) ? ' ' : (after ? '' : ' '));
    el.value = before + ins + after;
    const pos = (before + ins).length;
    el.focus();
    el.setSelectionRange(pos, pos);
    saveSpoken(line, el.value);
  }
  const saveSpoken = debounce((l, value) => { if (value !== l.spoken) upd(l, 'spoken', value); }, 600);

  async function applyTags(tags, mode, ids) {
    try {
      const r = await rpc('line2.bulkTags', { id, lineIds: ids, tags, mode });
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
        h('div.spacer'), columnsMenu(grid), folderButton());
      return;
    }
    if (engine() !== 'elevenlabs') {
      tagbar.append(h('span.lbl', 'Tags'), h('span.dim', 'Audio tags are an ElevenLabs v3/v4 feature; local engines ignore them (they are removed before sending).'), h('div.spacer'), columnsMenu(grid), folderButton());
      return;
    }
    const chip = (t) => {
      const c = h('span.chip.tag-chip', { title: 'Click: insert at the cursor, or add to the start of the ticked rows. Shift-click: remove from the ticked rows.' }, t);
      c.addEventListener('mousedown', (e) => e.preventDefault()); // keep the caret in the text box
      c.addEventListener('click', (e) => tagClick(t, e.shiftKey));
      return c;
    };
    const custom = h('input', { type: 'text', placeholder: '[custom tag]', style: { width: '130px' } });
    custom.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' || !custom.value.trim()) return;
      let t = custom.value.trim();
      if (!t.startsWith('[')) t = `[${t.replace(/[\[\]]/g, '')}]`;
      const ids = [...grid.selected];
      if (!ids.length) { toast('Tick rows first.', true); return; }
      applyTags([t], 'add', ids);
      custom.value = '';
    });
    tagbar.append(
      h('span.lbl', 'Tags'),
      voice.tags.map(chip),
      custom,
      h('button.btn.sm.ghost', { title: 'Add a tag to every line in the current filter', onclick: async () => {
        const r = await prompt('Add a tag to every line shown', [{ name: 't', label: 'Tags', placeholder: '[whispers]', help: `Added to the start of ${grid.rows.filter((l) => !l.omitted).length} line(s) in the current filter.` }]);
        if (r && r.t.trim()) applyTags(r.t.match(/\[[^\]]+\]|[^\s\[\]]+/g), 'add', grid.rows.filter((l) => !l.omitted).map((l) => l.lineId));
      } }, 'Add to all shown…'),
      h('div.spacer'),
      columnsMenu(grid),
      h('button.btn.sm', { title: 'Let the AI add ElevenLabs audio tags to the ticked lines (you review them)', onclick: () => { const ids = [...grid.selected]; if (!ids.length) { toast('Tick rows first.', true); return; } autotag(ids); } }, '✨ Auto-tag ticked'),
      h('button.btn.sm.ghost', { title: 'Edit the tag list in the voice pack settings', onclick: () => go('settings', { tab: 'voices', id: voice.id }) }, 'Edit tags…'),
      folderButton());
  }

  async function autotag(ids) {
    if (!app.state.settings.aiProvider.endpoint) {
      toast('Set up AI auto-tagging in Global Settings → General & tools first.', true);
      return;
    }
    try {
      toast(`Asking the AI for tags on ${ids.length} line(s)…`);
      const r = await rpc('patch.autotag', { id, lineIds: ids });
      patch = r.patch;
      reindex();
      grid.setRows(visible());
      renderCounts();
      const bad = r.results.filter((x) => x.error);
      toast(bad.length ? `${r.results.length - bad.length} suggestion(s). ${bad.length} skipped: ${bad[0].error}` : `${r.results.length} suggestion(s) ready — accept or undo in each row.`, bad.length > 0);
    } catch (e) { fail(e); }
  }

  // ---- filters
  const pendingLine = (l) => !l.omitted && (l.status === 'Pending' || l.status === 'Failed') && canGen(l);
  function counts() {
    const L = patch.lines;
    return {
      all: L.length,
      pending: L.filter(pendingLine).length,
      generated: L.filter((l) => l.status === 'Generated' && !l.stale).length,
      failed: L.filter((l) => l.status === 'Failed').length,
      stale: L.filter((l) => l.stale || l.status === 'Stale').length,
      omitted: L.filter((l) => l.omitted).length,
      marked: L.filter((l) => l.marked).length,
      differs: L.filter((l) => l.differs).length,
      p1: L.filter((l) => l.editedInPhase1).length,
      suggestions: L.filter((l) => l.tagSuggestion).length,
      nosource: L.filter((l) => vc && !l.hasSource).length,
    };
  }

  function visible() {
    const q = search.toLowerCase();
    return patch.lines.filter((l) => {
      switch (filter) {
        case 'pending': if (!pendingLine(l)) return false; break;
        case 'generated': if (!(l.status === 'Generated' && !l.stale)) return false; break;
        case 'failed': if (l.status !== 'Failed') return false; break;
        case 'stale': if (!(l.stale || l.status === 'Stale')) return false; break;
        case 'omitted': if (!l.omitted) return false; break;
        case 'marked': if (!l.marked) return false; break;
        case 'differs': if (!l.differs) return false; break;
        case 'p1': if (!l.editedInPhase1) return false; break;
        case 'suggestions': if (!l.tagSuggestion) return false; break;
        case 'nosource': if (l.hasSource) return false; break;
      }
      return !q || l.finalLine.toLowerCase().includes(q) || l.spoken.toLowerCase().includes(q) || l.originalLine.toLowerCase().includes(q);
    });
  }

  function renderCounts() {
    const c = counts();
    const chip = (key, label, title) => h('span.chip' + (filter === key ? '.on' : ''), { title, onclick: () => { filter = key; store.set('patch:' + id, { filter }); renderCounts(); grid.setRows(visible()); } }, label, h('span.n', c[key]));
    const had = filters.querySelector('input[type=text]');
    const q = had || h('input', { type: 'text', placeholder: 'Search…', value: search });
    if (!had) q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visible()); }, 150));
    clear(filters).append(chip('all', 'All'), chip('pending', vc ? 'To convert' : 'To generate'), chip('generated', vc ? 'Converted' : 'Generated'), chip('stale', 'Changed since take'),
      chip('failed', 'Failed'), chip('marked', '★ Marked'),
      chip('omitted', 'Omitted', 'Left out because a ticked reference topic file (e.g. the base game pack) already has the line'),
      vc ? chip('nosource', 'No source file') : chip('differs', 'Spoken ≠ in-game'), chip('p1', 'Edited in Phase 1'),
      c.suggestions ? chip('suggestions', '✨ Suggestions') : null, h('div.spacer'), q,
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
          h('p', 'Tick rows for one-off selections; ★ marks stay until you remove them, so you can collect lines to redo and generate them together.'),
          vc ? null : h('p', 'Tag buttons insert at the cursor of the box you are typing in. With rows ticked instead, they go at the start of each ticked line.'),
          h('p', 'Keys: ↑/↓ move, Space ticks, Ctrl+A ticks all shown.')));
      return;
    }
    const spoken = h('textarea', { rows: 3, disabled: l.omitted }, l.spoken);
    spoken.addEventListener('change', () => { if (spoken.value !== l.spoken) upd(l, 'spoken', spoken.value); });
    trackCaret(spoken, l);
    insp.append(
      h('div.row', statusDot(l), h('span.dim', l.omitted ? 'Omitted — already in ' + l.omittedBy : STATUS_TEXT[l.status] || l.status), h('div.spacer'),
        h('button.mark-btn' + (l.marked ? '.on' : ''), { title: 'Mark', onclick: () => upd(l, 'mark', !l.marked) }, '★')),
      l.lastError ? h('div.banner.bad', { style: { margin: 0 } }, l.lastError) : null,
      h('div', h('div.field-label', 'Original line (from the mod)'), h('div.box', l.originalLine)),
      h('div', h('div.field-label', 'In-game line'), h('div.box', l.finalLine)),
      h('div', h('div.field-label', 'File'), h('div.box.mono', { style: { wordBreak: 'break-all' } }, l.stem + '.wav / .lip / .fuz')),
      vc ? h('div', h('div.field-label', 'Source WAV'), l.hasSource ? h('div.row', h('button.btn.sm', { onclick: () => playSource(l) }, '▶ Play source'), h('span.mono.faint.ellipsis', { title: l.sourceWav }, l.sourceWav)) : h('div.banner.bad', { style: { margin: 0 } }, 'No source WAV found with this name.')) : null,
      h('div', h('div.field-label', vc ? 'Text for the LIP' : 'Spoken text (tags allowed)'), spoken,
        h('div.row', { style: { marginTop: '6px' } },
          vc || engine() !== 'elevenlabs' ? null : h('button.btn.sm', { title: 'Let the AI suggest audio tags for this line', disabled: l.omitted, onclick: () => autotag([l.lineId]) }, '✨ Auto-tag'),
          l.spokenEdited ? h('button.btn.sm.ghost', { title: 'Put the in-game line back as the spoken text', onclick: () => upd(l, 'resetSpoken') }, '↺ Restore in-game line') : null,
          h('div.spacer'),
          h('button.btn.sm.primary', { title: l.omitted ? 'Omitted: a reference topic file already has this line' : app.job ? 'A job is running' : !canGen(l) ? 'No source file for this line (text-to-speech for such lines is off)' : 'Make a new take of this line', disabled: l.omitted || !!app.job || !canGen(l), onclick: () => generate('selected', [l.lineId]) }, l.takes.length ? 'New take' : vc ? 'Convert' : 'Generate'))),
      l.tagSuggestion ? h('div.box', h('div.field-label', 'AI suggestion'), h('div', diffAdded(l.spoken, l.tagSuggestion)),
        h('div.row', { style: { marginTop: '6px' } }, h('button.btn.sm.primary', { title: 'Keep the suggested tags', onclick: () => upd(l, 'acceptTags') }, 'Accept'), h('button.btn.sm', { title: 'Go back to the text before tagging', onclick: () => upd(l, 'rejectTags') }, 'Undo'))) : null,
      l.omittedBy ? switchEl(l.overrideOmit, (v) => upd(l, 'overrideOmit', v), `Generate anyway (it is in "${l.omittedBy}")`) : null,
      h('h3', `Takes (${l.takes.length})`),
      l.takes.length ? h('div.col', l.takes.slice().reverse().map((t) => h('div.take' + (t.id === l.selectedTakeId ? '.sel' : ''),
        h('button.icon-btn.big', { title: 'Play', onclick: () => play(l, t) }, '▶'),
        h('div.grow', { style: { minWidth: 0 } }, h('div', '#' + t.slot + (t.id === l.selectedTakeId ? ' · used' : '')),
          h('div.meta', `${t.durationSeconds.toFixed(2)} s · ${t.settings.engine} · ${t.settings.modelId}${t.settings.engine && t.settings.engine.startsWith('elevenlabs') ? ` · stab ${t.settings.stability} · sim ${t.settings.similarity}${t.settings.speakerBoost ? ' · boost' : ''}` : ''}${t.settings.seed != null ? ' · seed ' + t.settings.seed : ''}`),
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
    if (busy) actions.title = 'A job is running (see the top bar). Wait for it, or Cancel it.'; else actions.removeAttribute('title');
    const verb = vc ? 'Convert' : 'Generate';
    const exp = h('button.btn', { disabled: busy, title: 'Write WAV + LIP files and build .fuz files' }, 'Export ▾');
    exp.addEventListener('click', () => popupMenu(exp, [
      { label: 'WAV + LIP to the WAV + LIP folder', sub: 'Also writes the DBVO Fix plugins and topic file to their folders', onclick: exportWavLip },
      { label: 'Build .fuz into the final folder', sub: 'Also writes the DBVO Fix plugins and topic file', onclick: () => buildFuz(false) },
      { label: 'Build .fuz to another folder…', sub: 'Only the .fuz files', onclick: () => buildFuz(true) },
      { label: 'Only DBVO Fix plugins + topic file', onclick: writeFix },
    ].filter(Boolean)));
    const takesBtn = h('button.btn', { disabled: busy, title: 'Clean up takes you no longer need' }, 'Takes ▾');
    takesBtn.addEventListener('click', () => popupMenu(takesBtn, [
      { label: `Keep only the used take · ticked lines (${sel.length})`, sub: 'Deletes the other takes and their files', onclick: () => sel.length ? purge(sel) : toast('Tick rows first.', true) },
      { label: 'Keep only the used take · all lines', sub: 'Deletes every take that is not in use', onclick: () => purge(null) },
    ]));
    clear(actions).append(
      h('span.stat', h('b', c.generated), ` / ${c.all - c.omitted} ${vc ? 'converted' : 'generated'}`, c.omitted ? ` · ${c.omitted} omitted` : '', c.failed ? h('span', { style: { color: 'var(--bad)' } }, ` · ${c.failed} failed`) : '', c.stale ? ` · ${c.stale} changed since take` : '', vc && c.nosource ? ` · ${c.nosource} without source` : ''),
      h('div.spacer'),
      sel.length ? h('button.btn.sm.ghost', { title: 'Untick all rows', onclick: () => grid.selectAll(false) }, 'Clear ticks') : null,
      sel.length ? h('button.btn.sm.ghost', { title: 'Mark the ticked lines', onclick: () => bulkMark(sel, true) }, '★ Mark ticked') : null,
      c.marked ? h('button.btn.sm.ghost', { title: 'Remove every mark', onclick: () => bulkMark(patch.lines.filter((l) => l.marked).map((l) => l.lineId), false) }, 'Clear marks') : null,
      takesBtn,
      exp,
      h('button.btn', { disabled: busy || !(c.all - c.omitted), title: 'A new take for every line (the new take becomes the one used)', onclick: () => generate('all') }, `All lines (${c.all - c.omitted})`),
      h('button.btn', { disabled: busy || !sel.length, title: 'A new take for each ticked line', onclick: () => generate('selected', sel) }, `Ticked lines (${sel.length})`),
      h('button.btn', { disabled: busy || !c.marked, title: 'A new take for each ★ marked line', onclick: () => generate('marked') }, `Marked lines (${c.marked})`),
      h('button.btn.primary', { disabled: busy || !c.pending, title: 'Lines without a take yet, and failed ones', onclick: () => generate('missing') }, `${verb} missing (${c.pending})`));
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

  async function generate(mode, lineIds) {
    try {
      const est = await rpc('patch.estimate', { id, mode, lineIds });
      if (!est.count) { toast(vc ? 'Nothing to convert.' : 'Nothing to generate.'); return; }
      // One line never asks; batches ask unless that is turned off in Global Settings.
      const confirmBatch = (app.state.settings.ui || {}).confirmBatch !== 'false';
      if (est.count === 1 || !confirmBatch) {
        if (!est.voiceId) { toast('Pick a voice in the toolbar first.', true); return; }
        await rpc('patch.generate', { id, mode, lineIds });
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
      ], [{ label: 'Cancel', value: false }, { label: `${vc ? 'Convert' : 'Generate'} ${est.count} line(s)`, primary: true, value: !missingVoice }]);
      if (!ok) return;
      await rpc('patch.generate', { id, mode, lineIds });
      renderActions();
    } catch (e) { fail(e); }
  }

  const filesModal = (title, summary, files) => modal(title, [summary, files && files.length ? h('div', h('div.field-label', 'DBVO Fix plugins and topic files written'), h('div.box.mono', { style: { whiteSpace: 'pre-wrap', maxHeight: '180px', overflow: 'auto' } }, files.join('\n'))) : null]);

  async function exportWavLip() {
    try {
      const r = await rpc('patch.exportWavLip', { id });
      await filesModal('WAV + LIP exported', h('div.kv',
        h('div', 'Written'), h('div', r.written), h('div', 'Skipped (already there)'), h('div', r.skipped), h('div', 'Failed'), h('div', r.failed),
        r.errors && r.errors.length ? [h('div', 'Errors'), h('div.mono', r.errors.slice(0, 10).join('\n'))] : null), r.files);
    } catch (e) { fail(e); }
  }

  async function buildFuz(other) {
    let customFolder = '';
    if (other) {
      customFolder = await rpc('pick.folder', { title: 'Folder for the .fuz files' });
      if (!customFolder) return;
    }
    try { await rpc('patch.buildFuz', { id, customFolder }); renderActions(); } catch (e) { fail(e); }
  }

  async function writeFix() {
    try {
      const written = await rpc('patch.writeFixPlugins', { id });
      await modal('Files written', [h('div.box.mono', { style: { whiteSpace: 'pre-wrap' } }, written.length ? written.join('\n') : 'Nothing to write: no plugin needs a DBVO Fix.')]);
    } catch (e) { fail(e); }
  }

  async function renderBanners() {
    clear(banners);
    try {
      const c = await rpc('patch.checks', { id });
      const t = c.tools;
      const eng = engine();
      const missing = [(eng === 'elevenlabs' || eng === 'sts') && !t.apiKey && 'ElevenLabs API key', !t.ffmpeg && 'ffmpeg.exe', !t.faceFx && 'FaceFXWrapper.exe', !t.fonix && 'FonixData.cdf', !t.xwma && 'xWMAEncode.exe (for .fuz)'].filter(Boolean);
      if (missing.length) banners.append(h('div.banner.bad', h('span', 'Missing: ' + missing.join(', ')), h('div.spacer'), h('button.btn.sm', { title: 'Set the tool paths', onclick: () => go('settings', { tab: 'general' }) }, 'Open Global Settings')));
      const unset = [!patch.wavLipFolder && 'WAV + LIP folder', !patch.finalRoot && 'final folder', !patch.fixFolder && 'DBVO Fix folder', vc && !patch.sourceFolders.length && 'source WAV folder'].filter(Boolean);
      if (unset.length) banners.append(h('div.banner.warn', h('span', 'Not set for this patch: ' + unset.join(', ') + '.'), h('div.spacer'), h('button.btn.sm', { title: 'Choose where files go for this patch', onclick: openOptions }, 'Set folders')));
      for (const w of c.pathWarnings) banners.append(h('div.banner.warn', h('b', 'Path length: '), h('span', `${w.where} reaches ${w.length} of ${w.limit} characters with the longest line — tools like Yakitori fail past 260.`), h('div.spacer'), h('button.btn.sm', { onclick: openOptions }, 'Shorten folders')));
      if (vc) {
        const noSrc = patch.lines.filter((l) => !l.omitted && !l.hasSource).length;
        banners.append(h('div.banner.info', h('div.col', { style: { gap: '4px' } },
          h('span', h('b', 'Voice Changer is on. '), 'Lines are converted from another voice pack\'s recordings of this mod (the source folders in Folders & options), so delivery and timing come from the source. Only worth it when the mod is already DBVO-patched with another voice.'),
          noSrc ? h('span', h('b', `${noSrc} line(s) have no source file`), ' — usually lines added by a mod update after the source pack was made, or lines it never voiced. Generate them with text-to-speech (the voice pack\'s ', h('b', voice.engine === 'elevenlabs' ? 'ElevenLabs' : voice.engine), ' settings; turn the Voice Changer off to adjust those), or leave them unvoiced.') : h('span.dim', 'Every line has a source file.')),
          h('div.spacer'),
          noSrc ? switchEl(!!patch.ttsForMissing, async (on) => { try { patch = await rpc('patch.saveMeta', { id, ttsForMissing: on }); reindex(); renderAll(); } catch (e) { fail(e); } }, 'Generate lines without a source with text-to-speech', { title: 'Off: lines without a source stay unvoiced. On: Generate missing also voices them with text-to-speech.' }) : null));
      }
      if (c.fuzOnly) banners.append(h('div.banner.info', h('span', `${c.fuzOnly} line(s) have no WAV + LIP here but a .fuz in the final folder. Import them as takes so they count as done and can be played.`),
        h('div.spacer'), h('button.btn.sm', { title: 'Unpack the .fuz files into WAV + LIP takes so they show as generated', disabled: !!app.job, onclick: async () => { try { await rpc('patch.importFuz', { id }); } catch (e) { fail(e); } } }, 'Import from .fuz')));
      if (c.unreviewed.length) banners.append(h('div.banner.warn', h('span', 'Flagged lines not reviewed in Phase 1: ' + c.unreviewed.map((u) => `${u.plugin} (${u.count})`).join(', ')),
        h('div.spacer'), patch.modIds.length === 1 ? h('button.btn.sm', { title: 'Open the mod this patch reads its lines from', onclick: () => go('mod', { id: patch.modIds[0] }) }, 'Open in Phase 1') : null));
    } catch { /* checks are advisory */ }
  }

  function renderHead() {
    const vs = app.state.voicePacks;
    const voiceSel = h('select', { title: 'Switch voice pack: opens the same mods with another voice' }, vs.map((v) => h('option', { value: v.id, selected: v.id === voice.id }, v.name)));
    voiceSel.addEventListener('change', async () => {
      if (app.job) { toast('Wait for the running job, or cancel it.', true); voiceSel.value = voice.id; return; }
      try { const r = await rpc('patch.switchVoice', { id, voicePackId: voiceSel.value }); go('patch', { id: r.id }); } catch (e) { fail(e); }
    });
    clear(head).append(
      h('button.btn.ghost.sm', { onclick: () => go('patches') }, '← DBVO Patches'),
      h('h1', patch.name),
      voiceChip(voice.name, voice.color),
      h('span.badge.b-skip', patch.category),
      switchEl(vc, (on) => toggleVc(on), 'Voice Changer', { title: 'Convert another voice pack\'s WAV / .fuz files of this mod into this voice (ElevenLabs speech-to-speech) instead of text-to-speech. Only useful when the mod is already DBVO-patched with another voice.' }),
      h('div.spacer'),
      h('span.faint', 'Voice pack'),
      voiceSel,
      h('button.icon-btn', { title: 'Edit this voice pack (Global Settings)', onclick: () => go('settings', { tab: 'voices', id: voice.id }) }, '✎'),
      h('span.faint', voice.naming === 'Legacy' ? 'Legacy names' : 'DBVO 2.0 hash names'),
      h('button.btn.sm', { title: 'Output folders, DBVO Fix folder, reference topic files and overwrite options', onclick: openOptions }, 'Folders & options…'),
      h('button.btn.sm.ghost', { title: 'Re-read Phase 1 now (also happens every time you open this patch)', onclick: sync }, '⟳ Refresh from Phase 1'),
      h('button.btn.sm.ghost.danger', { title: 'Delete this patch and its takes', onclick: async () => { if (await confirmBox('Delete patch?', 'Deletes its takes too. Files already exported stay where they are. A backup of the project is made first.', 'Delete', true)) { await rpc('patch.delete', { id }); go('patches'); } } }, 'Delete'));
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
      toast(`${rep.total} lines · ${rep.added} new · ${rep.removed} removed · ${rep.omitted} omitted${rep.stale ? ` · ${rep.stale} changed since take` : ''}`);
    } catch (e) { fail(e); }
  }

  async function toggleVc(on) {
    if (app.job) { toast('Wait for the running job, or cancel it.', true); renderHead(); return; }
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
      ], [{ label: 'Cancel', value: false }, { label: 'Turn on', primary: true, value: true }]);
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
    const modBoxes = s.mods.map((m) => ({ m, box: h('input', { type: 'checkbox', checked: patch.modIds.includes(m.id) }) }));
    const refOn = new Set(patch.referenceIds);
    const sources = [...(patch.sourceFolders || [])];
    let owWav = patch.overwriteWavLip, owFuz = patch.overwriteFuz;
    const folder = (val, title, ph) => {
      const i = h('input', { type: 'text', value: val || '', placeholder: ph });
      return { i, el: h('div.path-input', i, h('button.btn.sm', { onclick: async () => { const f = await rpc('pick.folder', { title }); if (f) i.value = f; } }, 'Browse')) };
    };
    const wl = folder(patch.wavLipFolder, 'WAV + LIP folder', 'e.g. D:\\DBVO\\Vampira\\Lucien\\WAV');
    const fx = folder(patch.fixFolder, 'DBVO Fix plugin + topic file folder', 'e.g. D:\\DBVO\\Vampira\\Lucien\\Fix');
    const fr = folder(patch.finalRoot, 'Final folder', 'e.g. D:\\DBVO Vampira FOMOD');
    const tpl = h('input', { type: 'text', value: patch.fuzTemplate, placeholder: voice.destinations.fuzTemplate || 'Sound\\DBVO\\{packId}' });
    const srcList = h('div.col');
    const renderSrc = () => clear(srcList).append(sources.length ? sources.map((f, i) => h('div.row', h('span.mono.grow.ellipsis', f), h('button.icon-btn', { onclick: () => { sources.splice(i, 1); renderSrc(); } }, '×'))) : h('div.faint', 'No folder yet.'));
    renderSrc();
    const ok = await modal(h('span.row', 'Folders & options — ', voiceChip(voice.name + ' voice pack', voice.color)), [
      h('div.col', h('div.field-label', 'Name'), name),
      vc ? h('div.col', h('div.field-label', 'Voice Changer source folders (another voice pack\'s WAV / .fuz files for this mod)'), srcList,
        h('button.btn.sm', { style: { alignSelf: 'flex-start' }, onclick: async () => { const f = await rpc('pick.folder', { title: 'Folder with the source WAV files' }); if (f) { sources.push(f); renderSrc(); } } }, '+ Add folder'),
        h('div.faint', 'Searched with sub-folders; matched by Legacy and DBVO 2.0 names.')) : null,
      h('div.col', h('div.field-label', 'WAV + LIP folder'), wl.el, h('div.faint', 'Export copies the chosen takes here as .wav + .lip.')),
      h('div.col', h('div.field-label', 'DBVO Fix plugin + topic file folder'), fx.el, h('div.faint', 'Your working copy of the DBVO Fix plugins and the topic file, kept apart from the WAV + LIP files. Written by Export and Build .fuz.')),
      h('div.col', h('div.field-label', 'Final folder'), fr.el, h('div.faint', 'The release (FOMOD) folder. .fuz files go into the sub-folder below; the DBVO Fix plugins go next to each mod\'s Sound folder, ready for Phase 3.')),
      h('div.col', h('div.field-label', '.fuz sub-folder inside the final folder'), tpl, h('div.faint', 'Placeholders: {packId} {voicePack} {mod} {patch}, e.g. Vamp - {mod}\\Sound\\DBVO\\{packId}.')),
      h('div.col', h('div.field-label', 'Overwrite'),
        switchEl(owWav, (v) => { owWav = v; }, 'Overwrite WAV + LIP files already in the folder'),
        switchEl(owFuz, (v) => { owFuz = v; }, 'Overwrite .fuz files already in the final folder'),
        h('div.faint', 'Off: existing files are skipped ("Skipped" in the report).')),
      h('div.col', h('div.field-label', 'Omit lines users already have'),
        h('div.faint', 'Lines found in these topic files are left out ("Omitted"). Tick only packs every user of this patch has, such as the base game pack. Never tick optional mods.'),
        s.references.length ? h('div.col', s.references.map((r) => switchEl(refOn.has(r.id), (v) => { if (v) refOn.add(r.id); else refOn.delete(r.id); }, `${r.name}  ·  ${r.lineCount} lines`)))
          : h('div.faint', 'No reference topic files yet — add them in Global Settings → Omit references.')),
      h('div.col', h('div.field-label', 'Mods in this patch'), h('div.box', { style: { maxHeight: '160px', overflow: 'auto' } }, modBoxes.map(({ m, box }) => h('label.row', box, m.name, h('span.faint', m.category))))),
    ], [{ label: 'Cancel', value: false }, { label: 'Save', primary: true, value: true }]);
    if (!ok) return;
    const modIds = modBoxes.filter((x) => x.box.checked).map((x) => x.m.id);
    if (!modIds.length) { toast('A patch needs at least one mod.', true); return; }
    try {
      patch = await rpc('patch.saveMeta', {
        id, name: name.value, modIds, referenceIds: [...refOn],
        wavLipFolder: wl.i.value.trim(), finalRoot: fr.i.value.trim(), fuzTemplate: tpl.value.trim(), fixFolder: fx.i.value.trim(),
        overwriteWavLip: owWav, overwriteFuz: owFuz, sourceFolders: sources,
      });
      if (vc) {
        const r = await rpc('patch.matchSources', { id });
        patch = r.patch;
        toast(`${r.found} of ${r.total} lines have a source WAV.`);
      }
      reindex();
      renderAll();
    } catch (e) { fail(e); }
  }

  async function loadEngine(refresh = false) {
    const eng = engine();
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
    renderToolbar();
    renderTagbar();
  }

  // live updates from generation
  on('line2', (d) => { if (d.patchId === id && document.body.contains(view)) replaceLine(d.line); });
  let jobSeen = false;
  document.addEventListener('job-progress', () => { if (!jobSeen && document.body.contains(view)) { jobSeen = true; grid.paint(true); renderActions(); } });
  document.addEventListener('job-done', async (e) => {
    jobSeen = false;
    if (!document.body.contains(view)) return;
    if (e.detail && e.detail.kind === 'import' && e.detail.patchId === id) {
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
