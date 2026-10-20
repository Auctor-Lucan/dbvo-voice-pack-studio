// Phase 2: DBVO Patches (voice pack x mods) — generation grid, takes, export.
import { rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, debounce, diffAdded, switchEl, segmented, popupMenu } from './lib.js';
import { app, go, refreshState } from './app.js';

const STATUS_TEXT = { Pending: 'Not generated', Generating: 'Generating…', Generated: 'Generated', Failed: 'Failed', Omitted: 'Omitted', Stale: 'Text changed since take' };

const LANGS = [['', 'Auto'], ['en', 'en · English'], ['de', 'de · German'], ['fr', 'fr · French'], ['es', 'es · Spanish'], ['it', 'it · Italian'],
  ['pt', 'pt · Portuguese'], ['pl', 'pl · Polish'], ['ru', 'ru · Russian'], ['ja', 'ja · Japanese'], ['zh', 'zh · Chinese'], ['ko', 'ko · Korean'],
  ['nl', 'nl · Dutch'], ['sv', 'sv · Swedish'], ['cs', 'cs · Czech'], ['tr', 'tr · Turkish'], ['uk', 'uk · Ukrainian'], ['hu', 'hu · Hungarian']];

const pct = (p) => { const n = p.total - p.omitted; return n ? Math.round((100 * p.generated) / n) : 0; };

// ------------------------------------------------------------------ list

export async function patchesView() {
  const s = await refreshState();
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'DBVO Patches'),
    h('span.sub', 'Phase 2 · one voice pack × one or more mods. Drag a patch onto another header to change its category.'),
    h('div.spacer'),
    h('button.btn', { onclick: addCategory }, '+ Category'),
    h('button.btn.primary', { onclick: () => newPatch() }, '+ New DBVO Patch')));
  const body = h('div.view-body');
  view.append(body);
  if (!s.patches.length) {
    body.append(h('div.empty', h('h2', 'No DBVO Patches yet'),
      h('div', 'Open a mod in Phase 1 and click "Continue to Phase 2", or create one here.'),
      h('div', { style: { marginTop: '14px' } }, s.voicePacks.length
        ? h('button.btn.primary', { onclick: () => newPatch() }, '+ New DBVO Patch')
        : h('button.btn.primary', { onclick: () => go('settings', { tab: 'voices', id: 'new' }) }, '+ New voice pack'))));
    return view;
  }
  const collapsed = store.get('cats-collapsed', {});
  const voiceFilter = h('select', h('option', { value: '' }, 'All voice packs'), s.voicePacks.map((v) => h('option', { value: v.id }, v.name)));
  voiceFilter.value = store.get('patches-voice', '');
  const q = h('input', { type: 'text', placeholder: 'Filter patches…', style: { width: '240px' } });
  const listEl = h('div');
  body.append(h('div.row', { style: { marginBottom: '12px' } }, q, voiceFilter), listEl);
  let dragId = null;

  const render = () => {
    clear(listEl);
    const f = q.value.toLowerCase();
    const cats = [...s.settings.categories, ...new Set(s.patches.map((p) => p.category).filter((c) => !s.settings.categories.includes(c)))];
    for (const cat of cats) {
      const list = s.patches.filter((p) => p.category === cat && (!voiceFilter.value || p.voicePackId === voiceFilter.value) && (!f || p.name.toLowerCase().includes(f) || p.voicePack.toLowerCase().includes(f)));
      const isOpen = !collapsed[cat];
      const sec = h('div.cat',
        h('div.cat-head', { onclick: () => { collapsed[cat] = isOpen; store.set('cats-collapsed', collapsed); render(); } },
          h('span.caret', isOpen ? '▾' : '▸'), h('h3', cat), h('span.faint', list.length || 'empty · drop a patch here')),
        isOpen ? list.map((p) => {
          const row = h('div.prow', { draggable: true, onclick: () => go('patch', { id: p.id }) },
            h('span.grip', { title: 'Drag to another category' }, '⋮⋮'),
            h('div.ellipsis', h('b', p.name)),
            h('div.dim.ellipsis', p.voicePack),
            h('div.row', h('div.grow', h('div.bar', h('div.bar-fill', { style: { width: pct(p) + '%' } }))), h('span.faint.mono', `${p.generated}/${p.total - p.omitted}`)),
            h('div.row', { style: { justifyContent: 'flex-end' } },
              p.failed ? h('span.badge.b-broken', `${p.failed} failed`) : null,
              p.omitted ? h('span.faint', `${p.omitted} omitted`) : null,
              p.total && p.generated >= p.total - p.omitted ? h('span.badge.b-valid', 'done') : null));
          row.addEventListener('dragstart', (e) => { dragId = p.id; row.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; });
          row.addEventListener('dragend', () => { dragId = null; row.classList.remove('dragging'); });
          return row;
        }) : null,
        null);
      sec.addEventListener('dragover', (e) => { if (dragId) { e.preventDefault(); sec.classList.add('drop-over'); } });
      sec.addEventListener('dragleave', (e) => { if (!sec.contains(e.relatedTarget)) sec.classList.remove('drop-over'); });
      sec.addEventListener('drop', async (e) => {
        e.preventDefault();
        sec.classList.remove('drop-over');
        const p = s.patches.find((x) => x.id === dragId);
        if (!p || p.category === cat) return;
        try {
          for (const mid of p.modIds) await rpc('mod.setCategory', { id: mid, category: cat });
          await refreshState();
          s.patches = app.state.patches;
          render();
          toast(`Moved to ${cat}. The mod's category changed too (it is shared by all its patches).`);
        } catch (err) { fail(err); }
      });
      listEl.append(sec);
    }
  };
  q.addEventListener('input', render);
  voiceFilter.addEventListener('change', () => { store.set('patches-voice', voiceFilter.value); render(); });
  render();
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

async function newPatch() {
  const s = await refreshState();
  if (!s.voicePacks.length) { toast('Create a voice pack first.', true); go('settings', { tab: 'voices', id: 'new' }); return; }
  if (!s.mods.length) { toast('Create a mod in Phase 1 first.', true); go('mods'); return; }
  const name = h('input', { type: 'text', placeholder: 'leave empty for "<voice pack> – <mod>"' });
  const voice = h('select', s.voicePacks.map((v) => h('option', { value: v.id, selected: v.id === s.settings.lastVoicePackId }, v.name)));
  const modBoxes = s.mods.map((m) => ({ m, box: h('input', { type: 'checkbox' }) }));
  const ok = await modal('New DBVO Patch', [
    h('div.col', h('div.field-label', 'Voice pack'), voice),
    h('div.col', h('div.field-label', 'Mods to voice (several = an all-in-one patch)'), h('div.box', { style: { maxHeight: '240px', overflow: 'auto' } },
      modBoxes.map(({ m, box }) => h('label.row', box, m.name, h('span.faint', m.category))))),
    h('div.col', h('div.field-label', 'Name (optional)'), name),
    h('div.faint', 'Folders and omit lists come from the voice pack and Settings; change them per patch under "Folders & options".'),
  ], [{ label: 'Cancel', value: false }, { label: 'Create', primary: true, value: true }]);
  if (!ok) return;
  const modIds = modBoxes.filter((x) => x.box.checked).map((x) => x.m.id);
  if (!modIds.length) { toast('Pick at least one mod.', true); return; }
  try {
    const p = await rpc('patch.create', { name: name.value, voicePackId: voice.value, modIds });
    go('patch', { id: p.id });
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ workspace

export async function patchView(id) {
  await refreshState();
  let patch;
  try {
    patch = (await rpc('patch.sync', { id })).patch; // pull in Phase 1 edits automatically
  } catch {
    patch = await rpc('patch.get', { id });
  }
  let voice = app.state.voicePacks.find((v) => v.id === patch.voicePackId);
  if (!voice) throw new Error('This patch\'s voice pack was deleted.');
  const ui = store.get('patch:' + id, { filter: 'all' });
  let filter = ui.filter || 'all';
  let search = '';
  let focusedId = null;
  let voices = [], models = [];
  let showInsp = store.get('insp2', true);
  let lastEdit = null; // { el, line } — the last spoken-text box the user was in
  const audio = new Audio();

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

  const grid = new VGrid(left, {
    storageKey: 'phase2',
    rowKey: (l) => l.lineId,
    rowClass: (l) => (l.omitted ? 'muted' : ''),
    onRowClick: (l) => { focusedId = l.lineId; renderInspector(); },
    onRowDblClick: (l) => play(l),
    columns: [
      { id: 'sel', title: '', width: 32, resizable: false, render: (l) => grid.checkbox(l) },
      { id: 'st', title: '', width: 26, resizable: false, render: (l) => h('span.dot.' + (l.omitted ? 'Omitted' : l.stale && l.status === 'Generated' ? 'Stale' : l.status), { title: l.omitted ? `Omitted — already in ${l.omittedBy}` : (STATUS_TEXT[l.status] || l.status) + (l.lastError ? ': ' + l.lastError : '') }) },
      { id: 'orig', title: 'Original line', width: 220, render: (l) => h('span.dim', { title: l.originalLine }, l.originalLine) },
      { id: 'final', title: 'In-game line (filename)', flex: 1, width: 260, render: (l) => h('span', { title: `${l.finalLine}\nFile: ${l.stem}` },
        l.editedInPhase1 ? h('span.mark.info', { title: 'Edited in Phase 1' }, 'P1 ') : null, l.finalLine) },
      { id: 'spoken', title: 'Spoken text (tags anywhere)', flex: 1.3, width: 320, render: (l) => spokenCell(l) },
      { id: 'take', title: 'Take', width: 70, render: (l) => l.takes.length > 1
        ? h('select', { style: { padding: '1px 2px' }, onchange: (e) => upd(l, 'selectTake', e.target.value) }, l.takes.map((t) => h('option', { value: t.id, selected: t.id === l.selectedTakeId }, '#' + t.slot)))
        : h('span.faint', l.takes.length ? '#' + l.takes[0].slot : '—') },
      { id: 'act', title: '', width: 70, resizable: false, render: (l) => h('span',
        h('button.icon-btn', { title: 'Play (or double-click the row)', disabled: !l.selectedTakeId, onclick: () => play(l) }, '▶'),
        h('button.icon-btn', { title: 'Generate a new take', disabled: l.omitted || !!app.job, onclick: () => generate('selected', [l.lineId]) }, '↻')) },
    ],
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
    if (l.differs) inp.style.color = 'var(--link)';
    inp.addEventListener('change', () => { if (inp.value !== l.spoken) upd(l, 'spoken', inp.value); });
    inp.addEventListener('focus', () => { if (focusedId !== l.lineId) { focusedId = l.lineId; grid.focus(l.lineId); renderInspector(); } });
    inp.addEventListener('keydown', (e) => {
      e.stopPropagation();
      if (e.key === 'Enter') inp.blur();
      if (e.key === 'Escape') { inp.value = l.spoken; inp.blur(); }
    });
    trackCaret(inp, l);
    return inp;
  }

  function play(l, take) {
    const t = take || l.takes.find((x) => x.id === l.selectedTakeId);
    if (!t) return;
    audio.src = patch.mediaBase + encodeURI(t.wav) + '?t=' + Date.now();
    audio.play().catch(fail);
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

  // ---- toolbar: ElevenLabs settings of the voice pack, saved on every change
  const saveVoice = debounce(async () => {
    try {
      voice = await rpc('voice.save', { voice });
      const i = app.state.voicePacks.findIndex((v) => v.id === voice.id);
      if (i >= 0) app.state.voicePacks[i] = voice;
    } catch (e) { fail(e); }
  }, 300);

  function setE(key, value) {
    if (voice.settingsLocked) { toast('This voice pack\'s settings are locked. Unlock them in Settings → Voice packs.', true); renderToolbar(); return; }
    voice.eleven[key] = value;
    saveVoice();
    renderToolbar();
  }

  const isTagModel = (m) => /^eleven_v[34]/i.test(m || '');
  const modelInfo = (m) => models.find((x) => x.modelId === m) || {};
  const grp = (label, ...kids) => h('div.tgroup', h('label', label), h('div.row', ...kids));

  function renderToolbar() {
    const e = voice.eleven;
    const locked = voice.settingsLocked;
    const tag = isTagModel(e.modelId);
    const mi = modelInfo(e.modelId);
    const supportsStyle = mi.canUseStyle != null ? mi.canUseStyle && !tag : !tag;
    const supportsBoost = mi.canUseSpeakerBoost != null ? mi.canUseSpeakerBoost && !tag : !tag;
    const langOk = !/multilingual_v2|monolingual_v1|english_v1/.test(e.modelId);
    const slider = (key, min, max, step, label) => {
      const val = h('span.mono.dim', { style: { width: '34px' } }, Number(e[key]).toFixed(2));
      const r = h('input', { type: 'range', min, max, step, value: e[key], disabled: locked, style: { width: '96px' } });
      r.addEventListener('input', () => { val.textContent = Number(r.value).toFixed(2); });
      r.addEventListener('change', () => setE(key, Number(r.value)));
      return grp(label, r, val);
    };
    const voiceSel = h('select', { disabled: locked, style: { maxWidth: '190px' }, onchange: (ev) => { const v = voices.find((x) => x.voiceId === ev.target.value); voice.eleven.voiceName = v ? v.name : ''; setE('voiceId', ev.target.value); } },
      h('option', { value: '' }, voices.length ? '— pick a voice —' : (e.voiceName || 'Loading voices…')),
      voices.map((v) => h('option', { value: v.voiceId, selected: v.voiceId === e.voiceId }, v.name)));
    if (e.voiceId && !voices.some((v) => v.voiceId === e.voiceId)) voiceSel.append(h('option', { value: e.voiceId, selected: true }, e.voiceName || e.voiceId));
    const modelSel = h('select', { disabled: locked, onchange: (ev) => setE('modelId', ev.target.value) },
      (models.length ? models : [{ modelId: e.modelId }]).map((m) => h('option', { value: m.modelId, selected: m.modelId === e.modelId }, m.modelId + (isTagModel(m.modelId) ? ' · tags' : ''))));
    const stabVal = e.stability < 0.25 ? 0 : e.stability < 0.75 ? 0.5 : 1;
    const stability = tag
      ? grp('Stability', segmented([[0, 'Creative'], [0.5, 'Natural'], [1, 'Robust']], stabVal, (v) => setE('stability', Number(v)), { disabled: locked }))
      : slider('stability', 0, 1, 0.01, 'Stability');
    const fmt = h('select', { disabled: locked, onchange: (ev) => setE('outputFormat', ev.target.value) },
      [['mp3_44100_128', 'MP3 44.1k 128'], ['mp3_44100_192', 'MP3 44.1k 192'], ['pcm_44100', 'PCM 44.1k (lossless)']].map(([v, t]) => h('option', { value: v, selected: v === e.outputFormat }, t)));
    const seed = h('input', { type: 'number', value: e.seed ?? '', placeholder: 'random', disabled: locked, style: { width: '92px' }, title: 'Same seed + same text + same settings ≈ same take. Empty = random.' });
    seed.addEventListener('change', () => setE('seed', seed.value === '' ? null : Number(seed.value)));
    const conc = h('input', { type: 'number', min: 1, max: 50, value: e.concurrency, disabled: locked, style: { width: '58px' }, title: 'How many lines are requested at once. Match your ElevenLabs plan\'s concurrency limit; the app slows down automatically on "too many requests".' });
    conc.addEventListener('change', () => setE('concurrency', Math.max(1, Number(conc.value) || 1)));
    const lang = h('select', { disabled: locked || !langOk, title: langOk ? 'Force the language (ISO 639-1). Auto lets the model detect it.' : 'This model does not accept a language code.', onchange: (ev) => setE('languageCode', ev.target.value) },
      LANGS.map(([v, t]) => h('option', { value: v, selected: v === (e.languageCode || '') }, t)));
    const norm = h('select', { disabled: locked, title: 'Text normalization: whether ElevenLabs writes out numbers, dates and abbreviations before speaking ("1st" → "first", "Dr." → "Doctor"). Auto lets the model decide; On is safer for numbers; Off keeps the text exactly as written.', onchange: (ev) => setE('textNormalization', ev.target.value) },
      [['auto', 'Auto'], ['on', 'On'], ['off', 'Off']].map(([v, t]) => h('option', { value: v, selected: v === e.textNormalization }, t)));

    clear(tb).append(
      grp('Voice', voiceSel, h('button.icon-btn', { title: 'Reload voices and models', onclick: () => loadEleven(true) }, '⟳')),
      grp('Model', modelSel),
      stability,
      slider('similarity', 0, 1, 0.01, 'Similarity'),
      supportsStyle ? slider('style', 0, 1, 0.01, 'Style') : null,
      supportsBoost ? grp('Speaker boost', switchEl(e.speakerBoost, (v) => setE('speakerBoost', v), '', { disabled: locked })) : null,
      slider('speed', 0.7, 1.2, 0.01, 'Speed'),
      grp('Language', lang),
      grp('Read numbers ⓘ', norm),
      grp('Format', fmt),
      grp('Seed', seed),
      grp('Concurrent', conc),
      locked ? h('span.badge.b-toolong', { title: 'Unlock in Settings → Voice packs' }, '🔒 locked') : null);
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
    clear(tagbar).append(
      h('span.lbl', 'Tags'),
      voice.tags.map(chip),
      custom,
      h('button.btn.sm.ghost', { title: 'Add a tag to every line in the current filter', onclick: async () => {
        const r = await prompt('Add a tag to every line shown', [{ name: 't', label: 'Tags', placeholder: '[whispers]', help: `Added to the start of ${grid.rows.filter((l) => !l.omitted).length} line(s) in the current filter.` }]);
        if (r && r.t.trim()) applyTags(r.t.match(/\[[^\]]+\]|[^\s\[\]]+/g), 'add', grid.rows.filter((l) => !l.omitted).map((l) => l.lineId));
      } }, 'Add to all shown…'),
      h('div.spacer'),
      h('button.btn.sm', { onclick: () => { const ids = [...grid.selected]; if (!ids.length) { toast('Tick rows first.', true); return; } autotag(ids); } }, '✨ Auto-tag ticked'),
      h('button.btn.sm.ghost', { onclick: () => go('settings', { tab: 'voices', id: voice.id }) }, 'Edit tags…'));
  }

  async function autotag(ids) {
    if (!app.state.settings.aiProvider.endpoint) {
      toast('Set an AI provider (OpenAI-compatible or Ollama) in Settings to use auto-tagging.', true);
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
  function counts() {
    const L = patch.lines;
    return {
      all: L.length,
      pending: L.filter((l) => !l.omitted && (l.status === 'Pending' || l.status === 'Failed')).length,
      generated: L.filter((l) => l.status === 'Generated' && !l.stale).length,
      failed: L.filter((l) => l.status === 'Failed').length,
      stale: L.filter((l) => l.stale || l.status === 'Stale').length,
      omitted: L.filter((l) => l.omitted).length,
      differs: L.filter((l) => l.differs).length,
      p1: L.filter((l) => l.editedInPhase1).length,
      suggestions: L.filter((l) => l.tagSuggestion).length,
    };
  }

  function visible() {
    const q = search.toLowerCase();
    return patch.lines.filter((l) => {
      switch (filter) {
        case 'pending': if (!(!l.omitted && (l.status === 'Pending' || l.status === 'Failed'))) return false; break;
        case 'generated': if (!(l.status === 'Generated' && !l.stale)) return false; break;
        case 'failed': if (l.status !== 'Failed') return false; break;
        case 'stale': if (!(l.stale || l.status === 'Stale')) return false; break;
        case 'omitted': if (!l.omitted) return false; break;
        case 'differs': if (!l.differs) return false; break;
        case 'p1': if (!l.editedInPhase1) return false; break;
        case 'suggestions': if (!l.tagSuggestion) return false; break;
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
    clear(filters).append(chip('all', 'All'), chip('pending', 'To generate'), chip('generated', 'Generated'), chip('stale', 'Changed since take'),
      chip('failed', 'Failed'),
      chip('omitted', 'Omitted', 'Left out because a ticked reference topic file (e.g. the base game pack) already has the line'),
      chip('differs', 'Spoken ≠ in-game'), chip('p1', 'Edited in Phase 1'),
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
          h('p', h('b', 'In-game line'), ' decides the filename and comes from Phase 1. ', h('b', 'Spoken text'), ' is what the voice says: edit it in the list or here, and put [tags] anywhere.'),
          h('p', 'Tag buttons insert at the cursor of the box you are typing in. With rows ticked instead, they go at the start of each ticked line.'),
          h('p', 'Keys: ↑/↓ move, Space ticks, Ctrl+A ticks all shown.')));
      return;
    }
    const spoken = h('textarea', { rows: 3, disabled: l.omitted }, l.spoken);
    spoken.addEventListener('change', () => { if (spoken.value !== l.spoken) upd(l, 'spoken', spoken.value); });
    trackCaret(spoken, l);
    insp.append(
      h('div.row', h('span.dot.' + (l.omitted ? 'Omitted' : l.status)), h('span.dim', l.omitted ? 'Omitted — already in ' + l.omittedBy : STATUS_TEXT[l.status] || l.status)),
      l.lastError ? h('div.banner.bad', { style: { margin: 0 } }, l.lastError) : null,
      h('div', h('div.field-label', 'Original line (from the mod)'), h('div.box', l.originalLine)),
      h('div', h('div.field-label', 'In-game line'), h('div.box', l.finalLine)),
      h('div', h('div.field-label', 'File'), h('div.box.mono', { style: { wordBreak: 'break-all' } }, l.stem + '.wav / .lip / .fuz')),
      h('div', h('div.field-label', 'Spoken text (tags allowed)'), spoken,
        h('div.row', { style: { marginTop: '6px' } },
          h('button.btn.sm', { disabled: l.omitted, onclick: () => autotag([l.lineId]) }, '✨ Auto-tag'),
          l.spokenEdited ? h('button.btn.sm.ghost', { onclick: () => upd(l, 'resetSpoken') }, 'Reset') : null,
          h('div.spacer'),
          h('button.btn.sm.primary', { disabled: l.omitted || !!app.job, onclick: () => generate('selected', [l.lineId]) }, l.takes.length ? 'New take' : 'Generate'))),
      l.tagSuggestion ? h('div.box', h('div.field-label', 'AI suggestion'), h('div', diffAdded(l.spoken, l.tagSuggestion)),
        h('div.row', { style: { marginTop: '6px' } }, h('button.btn.sm.primary', { onclick: () => upd(l, 'acceptTags') }, 'Accept'), h('button.btn.sm', { onclick: () => upd(l, 'rejectTags') }, 'Undo'))) : null,
      l.omittedBy ? switchEl(l.overrideOmit, (v) => upd(l, 'overrideOmit', v), `Generate anyway (it is in "${l.omittedBy}")`) : null,
      h('h3', `Takes (${l.takes.length})`),
      l.takes.length ? h('div.col', l.takes.slice().reverse().map((t) => h('div.take' + (t.id === l.selectedTakeId ? '.sel' : ''),
        h('button.icon-btn', { title: 'Play', onclick: () => play(l, t) }, '▶'),
        h('div.grow', { style: { minWidth: 0 } }, h('div', '#' + t.slot + (t.id === l.selectedTakeId ? ' · used' : '')),
          h('div.meta', `${t.durationSeconds.toFixed(2)} s · ${t.settings.modelId} · stab ${t.settings.stability} · sim ${t.settings.similarity}${t.settings.speakerBoost ? ' · boost' : ''}${t.settings.seed != null ? ' · seed ' + t.settings.seed : ''}`),
          t.spokenText !== l.spoken ? h('div.meta.ellipsis', { title: t.spokenText }, 'Said: ' + t.spokenText) : null),
        t.id !== l.selectedTakeId ? h('button.btn.sm', { onclick: () => upd(l, 'selectTake', t.id) }, 'Use') : null,
        h('button.icon-btn', { title: 'Discard take', onclick: async () => { if (await confirmBox('Discard take?', `Delete take #${t.slot} and its files?`, 'Discard', true)) upd(l, 'deleteTake', t.id); } }, '🗑'))))
        : h('div.faint', 'No takes yet.'),
      h('h3', 'Used by'),
      h('div.col', l.sources.map((s) => h('div.faint.mono.ellipsis', `${patch.modNames[s.modId] || '?'} · ${s.plugin} · ${s.formId}`))),
    );
  }

  // ---- status bar and actions
  function renderActions() {
    const sel = [...grid.selected];
    const busy = !!app.job;
    const c = counts();
    const genBtn = h('button.btn.primary', { disabled: busy }, `Generate missing · ${c.pending}`);
    genBtn.addEventListener('click', () => generate('missing'));
    const more = h('button.btn.primary', { disabled: busy, title: 'More generate options' }, '▾');
    more.addEventListener('click', () => popupMenu(more, [
      { label: `Missing lines (${c.pending})`, sub: 'Not generated yet, or failed', onclick: () => generate('missing') },
      { label: `Ticked lines (${sel.length})`, sub: 'A new take each; earlier takes stay selectable', onclick: () => sel.length ? generate('selected', sel) : toast('Tick rows first.', true) },
      { label: `All lines (${c.all - c.omitted})`, sub: 'A new take for every line (overwrites which take is used)', onclick: () => generate('all') },
    ]));
    const exp = h('button.btn', { disabled: busy }, 'Export ▾');
    exp.addEventListener('click', () => popupMenu(exp, [
      { label: 'WAV + LIP to the WAV + LIP folder', sub: 'Also writes the DBVO Fix plugins and topic file', onclick: exportWavLip },
      { label: 'Build .fuz into the final folder', sub: 'Also writes the DBVO Fix plugins and topic file', onclick: () => buildFuz(false) },
      { label: 'Build .fuz to another folder…', sub: 'Only the .fuz files', onclick: () => buildFuz(true) },
      { label: 'Only DBVO Fix plugins + topic file', onclick: writeFix },
    ]));
    clear(actions).append(
      h('span.stat', h('b', c.generated), ` / ${c.all - c.omitted} generated`, c.omitted ? ` · ${c.omitted} omitted` : '', c.failed ? h('span', { style: { color: 'var(--bad)' } }, ` · ${c.failed} failed`) : '', c.stale ? ` · ${c.stale} changed since take` : ''),
      h('div.spacer'),
      sel.length ? h('span.dim', `${sel.length} ticked`) : null,
      sel.length ? h('button.btn.sm.ghost', { onclick: () => grid.selectAll(false) }, 'Clear') : null,
      exp,
      h('div.btn-group', genBtn, more));
  }

  async function generate(mode, lineIds) {
    try {
      const est = await rpc('patch.estimate', { id, mode, lineIds });
      if (!est.count) { toast('Nothing to generate.'); return; }
      const s = est.settings;
      const rows = [
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
      const ok = await modal(mode === 'all' ? 'Generate every line again?' : 'Check before spending credits', [
        h('div.kv', rows.flatMap(([k, v]) => [h('div', k), h('div', String(v))])),
        mode === 'all' ? h('div.banner.warn', { style: { margin: 0 } }, 'Every line gets a new take, which becomes the one used. Earlier takes stay in the take list.') : null,
        !est.voiceId ? h('div.banner.bad', { style: { margin: 0 } }, 'Pick a voice in the toolbar first.') : null,
      ], [{ label: 'Cancel', value: false }, { label: `Generate ${est.count} line(s)`, primary: true, value: !!est.voiceId }]);
      if (!ok) return;
      await rpc('patch.generate', { id, mode, lineIds });
      renderActions();
    } catch (e) { fail(e); }
  }

  const filesModal = (title, summary, files) => modal(title, [summary, files && files.length ? h('div', h('div.field-label', 'Also written'), h('div.box.mono', { style: { whiteSpace: 'pre-wrap', maxHeight: '180px', overflow: 'auto' } }, files.join('\n'))) : null]);

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
      const missing = [!t.apiKey && 'ElevenLabs API key', !t.ffmpeg && 'ffmpeg.exe', !t.faceFx && 'FaceFXWrapper.exe', !t.fonix && 'FonixData.cdf', !t.xwma && 'xWMAEncode.exe (for .fuz)'].filter(Boolean);
      if (missing.length) banners.append(h('div.banner.bad', h('span', 'Missing: ' + missing.join(', ')), h('div.spacer'), h('button.btn.sm', { onclick: () => go('settings', { tab: 'general' }) }, 'Open Settings')));
      if (!patch.wavLipFolder || !patch.finalRoot) banners.append(h('div.banner.warn', h('span', !patch.wavLipFolder ? 'No WAV + LIP folder set for this patch.' : 'No final folder set for this patch.'), h('div.spacer'), h('button.btn.sm', { onclick: openOptions }, 'Set folders')));
      for (const w of c.pathWarnings) banners.append(h('div.banner.warn', h('b', 'Path length: '), h('span', `${w.where} reaches ${w.length} of ${w.limit} characters with the longest line — tools like Yakitori fail past 260.`), h('span.mono.faint', { title: w.example }, '…' + w.example.slice(-60)), h('div.spacer'), h('button.btn.sm', { onclick: openOptions }, 'Shorten folders')));
      if (c.unreviewed.length) banners.append(h('div.banner.warn', h('span', 'Flagged lines not reviewed in Phase 1: ' + c.unreviewed.map((u) => `${u.plugin} (${u.count})`).join(', ')),
        h('div.spacer'), patch.modIds.length === 1 ? h('button.btn.sm', { onclick: () => go('mod', { id: patch.modIds[0] }) }, 'Open in Phase 1') : null));
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
      h('span.badge.b-skip', patch.category),
      h('div.spacer'),
      h('span.faint', 'Voice pack'),
      voiceSel,
      h('button.icon-btn', { title: 'Edit this voice pack (Settings)', onclick: () => go('settings', { tab: 'voices', id: voice.id }) }, '✎'),
      h('span.faint', voice.naming === 'Legacy' ? 'Legacy names' : 'DBVO 2.0 hash names'),
      h('button.btn.sm', { onclick: openOptions }, 'Folders & options…'),
      h('button.btn.sm.ghost', { title: 'Re-read Phase 1 now (also happens every time you open this patch)', onclick: sync }, '⟳ Refresh from Phase 1'),
      h('button.btn.sm.ghost.danger', { onclick: async () => { if (await confirmBox('Delete DBVO Patch?', 'Deletes its takes too. Files already exported stay where they are. A backup of the project is made first.', 'Delete', true)) { await rpc('patch.delete', { id }); go('patches'); } } }, 'Delete'));
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

  async function openOptions() {
    await refreshState();
    const s = app.state;
    const name = h('input', { type: 'text', value: patch.name });
    const modBoxes = s.mods.map((m) => ({ m, box: h('input', { type: 'checkbox', checked: patch.modIds.includes(m.id) }) }));
    const refOn = new Set(patch.referenceIds);
    let owWav = patch.overwriteWavLip, owFuz = patch.overwriteFuz;
    const folder = (val, title, ph) => {
      const i = h('input', { type: 'text', value: val, placeholder: ph });
      return { i, el: h('div.path-input', i, h('button.btn.sm', { onclick: async () => { const f = await rpc('pick.folder', { title }); if (f) i.value = f; } }, 'Browse')) };
    };
    const wl = folder(patch.wavLipFolder, 'WAV + LIP folder', 'e.g. D:\\DBVO\\Vampira\\Lucien\\WAV');
    const fr = folder(patch.finalRoot, 'Final folder', 'e.g. D:\\DBVO\\Vampira\\Lucien\\Release');
    const tpl = h('input', { type: 'text', value: patch.fuzTemplate, placeholder: voice.destinations.fuzTemplate || 'Sound\\DBVO\\{packId}' });
    const ok = await modal('Folders & options', [
      h('div.col', h('div.field-label', 'Name'), name),
      h('div.col', h('div.field-label', 'WAV + LIP folder'), wl.el, h('div.faint', 'Generated WAV + LIP files are copied here (Export). The DBVO Fix plugins and topic file go here too.')),
      h('div.col', h('div.field-label', 'Final folder'), fr.el, h('div.faint', 'The release folder. .fuz files, DBVO Fix plugins and the topic file are built into it.')),
      h('div.col', h('div.field-label', '.fuz sub-folder inside the final folder'), tpl, h('div.faint', 'Placeholders: {packId} {voicePack} {mod} {patch}.')),
      h('div.col', h('div.field-label', 'Overwrite'),
        switchEl(owWav, (v) => { owWav = v; }, 'Overwrite WAV + LIP files already in the folder'),
        switchEl(owFuz, (v) => { owFuz = v; }, 'Overwrite .fuz files already in the final folder'),
        h('div.faint', 'Off: existing files are skipped ("Skipped" in the report).')),
      h('div.col', h('div.field-label', 'Omit lines users already have'),
        h('div.faint', 'Lines found in these topic files are left out ("Omitted"). Tick only packs every user of this patch has, such as the base game pack. Never tick optional mods.'),
        s.references.length ? h('div.col', s.references.map((r) => switchEl(refOn.has(r.id), (v) => { if (v) refOn.add(r.id); else refOn.delete(r.id); }, `${r.name}  ·  ${r.lineCount} lines`)))
          : h('div.faint', 'No reference topic files yet — add them in Settings → References.')),
      h('div.col', h('div.field-label', 'Mods in this patch'), h('div.box', { style: { maxHeight: '160px', overflow: 'auto' } }, modBoxes.map(({ m, box }) => h('label.row', box, m.name, h('span.faint', m.category))))),
    ], [{ label: 'Cancel', value: false }, { label: 'Save', primary: true, value: true }]);
    if (!ok) return;
    const modIds = modBoxes.filter((x) => x.box.checked).map((x) => x.m.id);
    if (!modIds.length) { toast('A patch needs at least one mod.', true); return; }
    try {
      patch = await rpc('patch.saveMeta', {
        id, name: name.value, modIds, referenceIds: [...refOn],
        wavLipFolder: wl.i.value.trim(), finalRoot: fr.i.value.trim(), fuzTemplate: tpl.value.trim(),
        overwriteWavLip: owWav, overwriteFuz: owFuz,
      });
      reindex();
      renderAll();
    } catch (e) { fail(e); }
  }

  async function loadEleven(refresh = false) {
    try {
      [voices, models] = await Promise.all([rpc('eleven.voices', { refresh }), rpc('eleven.models', { refresh })]);
      renderToolbar();
    } catch (e) {
      if (app.state.settings.hasApiKey) fail(e);
    }
  }

  // live updates from generation
  on('line2', (d) => { if (d.patchId === id && document.body.contains(view)) replaceLine(d.line); });
  document.addEventListener('job-done', () => { if (document.body.contains(view)) { renderActions(); renderBanners(); } });

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
  loadEleven();
  return view;
}
