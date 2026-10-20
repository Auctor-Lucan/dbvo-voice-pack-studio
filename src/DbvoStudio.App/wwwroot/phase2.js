// Phase 2: DBVO Patches (voice pack x mods) — generation grid, takes, export.
import { rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, debounce, diffAdded } from './lib.js';
import { app, go, refreshState } from './app.js';

const STATUS_TEXT = { Pending: 'Not generated', Generating: 'Generating…', Generated: 'Generated', Failed: 'Failed', Omitted: 'Omitted', Stale: 'Text changed since take' };

// ------------------------------------------------------------------ list

export async function patchesView() {
  const s = await refreshState();
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'DBVO Patches'),
    h('span.sub', 'Phase 2 · one voice pack × one or more mods. Generate WAV + LIP, then .fuz.'),
    h('div.spacer'),
    h('button.btn.primary', { onclick: () => newPatch() }, '+ New DBVO Patch')));
  const body = h('div.view-body');
  view.append(body);
  if (app.route.newFor) setTimeout(() => newPatch(app.route.newFor), 50);
  if (!s.patches.length) {
    body.append(h('div.empty', h('h2', 'No DBVO Patches yet'),
      h('div', s.voicePacks.length ? 'Pick a voice pack and the mods to voice.' : 'Create a voice pack first (Voice packs, in the menu).'),
      h('div', { style: { marginTop: '14px' } }, s.voicePacks.length
        ? h('button.btn.primary', { onclick: () => newPatch() }, '+ New DBVO Patch')
        : h('button.btn.primary', { onclick: () => go('voices', { id: 'new' }) }, '+ New voice pack'))));
    return view;
  }
  const byVoice = {};
  for (const p of s.patches) (byVoice[p.voicePack] ||= []).push(p);
  for (const [voice, list] of Object.entries(byVoice).sort()) {
    body.append(h('h3.dim', { style: { margin: '6px 0 10px', fontWeight: 600 } }, voice));
    body.append(h('div.cards', { style: { marginBottom: '18px' } }, list.map((p) => h('div.card', { onclick: () => go('patch', { id: p.id }) },
      h('h3', p.name),
      h('div.meta', p.modIds.map((mid) => (s.mods.find((m) => m.id === mid) || {}).name || '?').join(', ')),
      h('div.row.meta', h('span', `${p.generated}/${p.total - p.omitted} generated`), p.omitted ? h('span.faint', `${p.omitted} omitted`) : null,
        h('div.grow', h('div.bar', h('div.bar-fill', { style: { width: (p.total - p.omitted ? (100 * p.generated) / (p.total - p.omitted) : 0) + '%' } }))))))));
  }
  return view;
}

async function newPatch(preselectModId) {
  const s = await refreshState();
  if (!s.voicePacks.length) { toast('Create a voice pack first.', true); go('voices', { id: 'new' }); return; }
  if (!s.mods.length) { toast('Create a mod in Phase 1 first.', true); go('mods'); return; }
  const name = h('input', { type: 'text', placeholder: 'e.g. Vampira – Legacy of the Dragonborn' });
  const voice = h('select', s.voicePacks.map((v) => h('option', { value: v.id, selected: v.id === s.settings.lastVoicePackId }, v.name)));
  const modBoxes = s.mods.map((m) => ({ m, box: h('input', { type: 'checkbox', checked: m.id === preselectModId }) }));
  const refBoxes = s.references.map((r) => ({ r, box: h('input', { type: 'checkbox' }) }));
  const syncRefs = () => {
    const v = s.voicePacks.find((x) => x.id === voice.value);
    refBoxes.forEach(({ r, box }) => { box.checked = !!(v && v.defaultReferenceIds.includes(r.id)); });
  };
  voice.addEventListener('change', syncRefs);
  syncRefs();
  const ok = await modal('New DBVO Patch', [
    h('div.col', h('div.field-label', 'Voice pack'), voice),
    h('div.col', h('div.field-label', 'Mods to voice'), h('div.box', { style: { maxHeight: '220px', overflow: 'auto' } },
      modBoxes.map(({ m, box }) => h('label.row', box, m.name, h('span.faint', m.plugins.map((p) => p.fileName).join(', ')))))),
    h('div.col', h('div.field-label', 'Omit lines users already have'),
      h('div.faint', 'Lines found in these topic files are left out of this patch, so you never voice a line twice. Use it for the base game pack your users already have. Leave everything unticked when making the base game pack itself, and never tick optional mods.'),
      refBoxes.length ? h('div.box', refBoxes.map(({ r, box }) => h('label.row', box, r.name, h('span.faint', `${r.lineCount} lines`))))
        : h('div.faint', 'No reference topic files yet — add them in Settings.')),
    h('div.col', h('div.field-label', 'Name (optional)'), name),
  ], [{ label: 'Cancel', value: false }, { label: 'Create', primary: true, value: true }]);
  if (!ok) return;
  const modIds = modBoxes.filter((x) => x.box.checked).map((x) => x.m.id);
  if (!modIds.length) { toast('Pick at least one mod.', true); return; }
  try {
    const p = await rpc('patch.create', { name: name.value, voicePackId: voice.value, modIds, referenceIds: refBoxes.filter((x) => x.box.checked).map((x) => x.r.id) });
    go('patch', { id: p.id });
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ workspace

export async function patchView(id) {
  await refreshState();
  let patch = await rpc('patch.get', { id });
  let voice = app.state.voicePacks.find((v) => v.id === patch.voicePackId);
  if (!voice) throw new Error('This patch\'s voice pack was deleted.');
  const ui = store.get('patch:' + id, { filter: 'all' });
  let filter = ui.filter || 'all';
  let search = '';
  let focusedId = null;
  let voices = [], models = [];
  const audio = new Audio();

  const view = h('div.view');
  const head = h('div.view-head');
  const tb = h('div.toolbar');
  const tagbar = h('div.toolbar');
  const banners = h('div');
  const filters = h('div.filters');
  const split = h('div.split');
  const left = h('div.left');
  const insp = h('div.inspector');
  split.append(left, insp);
  const actions = h('div.actionbar');
  view.append(head, tb, tagbar, banners, filters, split, actions);

  const byId = () => new Map(patch.lines.map((l) => [l.lineId, l]));
  let index = byId();

  const grid = new VGrid(left, {
    rowKey: (l) => l.lineId,
    rowClass: (l) => (l.omitted ? 'muted' : ''),
    onRowClick: (l) => { focusedId = l.lineId; renderInspector(); },
    onRowDblClick: (l) => play(l),
    columns: [
      { title: '', width: 26, render: (l) => h('input', { type: 'checkbox', checked: grid.selected.has(l.lineId), onchange: () => grid.toggle(l.lineId) }) },
      { title: '', width: 22, render: (l) => h('span.dot.' + (l.omitted ? 'Omitted' : l.stale && l.status === 'Generated' ? 'Stale' : l.status), { title: l.omitted ? `Omitted — already in ${l.omittedBy}` : (STATUS_TEXT[l.status] || l.status) + (l.lastError ? ': ' + l.lastError : '') }) },
      { title: 'In-game line (filename)', flex: 1, render: (l) => h('span', { title: `${l.finalLine}\nFile: ${l.stem}` }, l.finalLine) },
      { title: 'Spoken text', flex: 1.3, render: (l) => spokenCell(l) },
      { title: '', width: 70, render: (l) => h('span',
        l.editedInPhase1 ? h('span.mark.info', { title: 'Edited in Phase 1. Original: ' + l.originalLine }, 'P1') : null,
        l.differs ? h('span.mark', { title: 'Spoken text differs from the in-game line' }, '≠') : null,
        l.tagSuggestion ? h('span.mark.info', { title: 'AI tag suggestion waiting' }, '✨') : null,
        l.omitted ? h('span.mark', { title: 'Omitted: ' + l.omittedBy }, 'omit') : null) },
      { title: 'Take', width: 64, render: (l) => l.takes.length > 1
        ? h('select', { style: { padding: '1px 2px' }, onchange: (e) => upd(l, 'selectTake', e.target.value) }, l.takes.map((t) => h('option', { value: t.id, selected: t.id === l.selectedTakeId }, '#' + t.slot)))
        : h('span.faint', l.takes.length ? '#' + l.takes[0].slot : '—') },
      { title: '', width: 66, render: (l) => h('span',
        h('button.icon-btn', { title: 'Play (double-click a row)', disabled: !l.selectedTakeId, onclick: () => play(l) }, '▶'),
        h('button.icon-btn', { title: 'Generate a new take', disabled: l.omitted || !!app.job, onclick: () => generate('selected', [l.lineId]) }, '↻')) },
    ],
  });
  grid.el.addEventListener('selection', () => renderActions());

  function spokenCell(l) {
    if (l.tagSuggestion) {
      return h('span.row', { style: { width: '100%' } },
        h('span.grow', { title: l.tagSuggestion, style: { overflow: 'hidden', textOverflow: 'ellipsis' } }, diffAdded(l.spoken, l.tagSuggestion)),
        h('button.icon-btn', { title: 'Accept tags', onclick: () => upd(l, 'acceptTags') }, '✓'),
        h('button.icon-btn', { title: 'Undo', onclick: () => upd(l, 'rejectTags') }, '✕'));
    }
    const inp = h('input', { type: 'text', value: l.spoken, disabled: l.omitted });
    inp.addEventListener('change', () => upd(l, 'spoken', inp.value));
    inp.addEventListener('focus', () => { focusedId = l.lineId; grid.focus(l.lineId); renderInspector(); });
    inp.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { inp.blur(); }
      if (e.key === 'Escape') { inp.value = l.spoken; inp.blur(); }
    });
    return h('span.row', { style: { width: '100%' } }, inp,
      h('button.icon-btn', { title: 'Auto-tag with AI', disabled: l.omitted, onclick: () => autotag([l.lineId]) }, '✨'));
  }

  function play(l) {
    const t = l.takes.find((x) => x.id === l.selectedTakeId);
    if (!t) return;
    audio.src = patch.mediaBase + encodeURI(t.wav) + '?t=' + Date.now();
    audio.play().catch(fail);
  }

  async function upd(l, action, value) {
    try {
      const r = await rpc('line2.update', { id, lineId: l.lineId, action, value });
      replaceLine(r);
    } catch (e) { fail(e); }
  }

  function replaceLine(nl) {
    const i = patch.lines.findIndex((x) => x.lineId === nl.lineId);
    if (i >= 0) patch.lines[i] = nl;
    index = byId();
    const ri = grid.rows.findIndex((x) => x.lineId === nl.lineId);
    if (ri >= 0) grid.rows[ri] = nl;
    grid.refreshRow(nl.lineId);
    if (focusedId === nl.lineId) renderInspector();
    renderCounts();
  }

  // ---- toolbar: ElevenLabs settings, saved on every change
  const saveVoice = debounce(async () => {
    try {
      voice = await rpc('voice.save', { voice });
      const i = app.state.voicePacks.findIndex((v) => v.id === voice.id);
      if (i >= 0) app.state.voicePacks[i] = voice;
    } catch (e) { fail(e); }
  }, 300);

  function setE(key, value) {
    if (voice.settingsLocked) { toast('This voice pack\'s settings are locked. Unlock them in Voice packs.', true); renderToolbar(); return; }
    voice.eleven[key] = value;
    saveVoice();
    renderToolbar();
  }

  const isTagModel = (m) => /^eleven_v[34]/i.test(m || '');
  const modelInfo = (m) => models.find((x) => x.modelId === m) || {};

  function renderToolbar() {
    const e = voice.eleven;
    const locked = voice.settingsLocked;
    const tag = isTagModel(e.modelId);
    const mi = modelInfo(e.modelId);
    const supportsStyle = mi.canUseStyle != null ? mi.canUseStyle && !tag : !tag;
    const supportsBoost = mi.canUseSpeakerBoost != null ? mi.canUseSpeakerBoost && !tag : !tag;
    const slider = (key, min, max, step, label) => {
      const val = h('span.mono.dim', { style: { width: '32px' } }, Number(e[key]).toFixed(2));
      const r = h('input', { type: 'range', min, max, step, value: e[key], disabled: locked, style: { width: '90px' } });
      r.addEventListener('input', () => { val.textContent = Number(r.value).toFixed(2); });
      r.addEventListener('change', () => setE(key, Number(r.value)));
      return h('div.grp', h('label', label), r, val);
    };
    const voiceSel = h('select', { disabled: locked, style: { maxWidth: '180px' }, onchange: (ev) => { const v = voices.find((x) => x.voiceId === ev.target.value); voice.eleven.voiceName = v ? v.name : ''; setE('voiceId', ev.target.value); } },
      h('option', { value: '' }, voices.length ? '— pick a voice —' : (e.voiceName || 'Loading voices…')),
      voices.map((v) => h('option', { value: v.voiceId, selected: v.voiceId === e.voiceId }, v.name)));
    if (e.voiceId && !voices.some((v) => v.voiceId === e.voiceId)) voiceSel.append(h('option', { value: e.voiceId, selected: true }, e.voiceName || e.voiceId));
    const modelSel = h('select', { disabled: locked, onchange: (ev) => setE('modelId', ev.target.value) },
      (models.length ? models : [{ modelId: e.modelId, name: e.modelId }]).map((m) => h('option', { value: m.modelId, selected: m.modelId === e.modelId }, m.modelId + (isTagModel(m.modelId) ? ' · tags' : ''))));
    const stability = tag
      ? h('div.grp', h('label', 'Stability'), h('select', { disabled: locked, onchange: (ev) => setE('stability', Number(ev.target.value)) },
        [['0', 'Creative'], ['0.5', 'Natural'], ['1', 'Robust']].map(([v, t]) => h('option', { value: v, selected: Math.abs(Number(v) - (e.stability < 0.25 ? 0 : e.stability < 0.75 ? 0.5 : 1)) < 0.01 }, t))))
      : slider('stability', 0, 1, 0.01, 'Stability');
    const fmt = h('select', { disabled: locked, onchange: (ev) => setE('outputFormat', ev.target.value) },
      [['mp3_44100_128', 'MP3 44.1k 128'], ['mp3_44100_192', 'MP3 44.1k 192'], ['pcm_44100', 'PCM 44.1k (lossless)'], ['pcm_48000', 'PCM 48k']].map(([v, t]) => h('option', { value: v, selected: v === e.outputFormat }, t)));
    const seed = h('input', { type: 'number', value: e.seed ?? '', placeholder: 'random', disabled: locked, style: { width: '90px' } });
    seed.addEventListener('change', () => setE('seed', seed.value === '' ? null : Number(seed.value)));
    const conc = h('input', { type: 'number', min: 1, max: 50, value: e.concurrency, disabled: locked, style: { width: '56px' }, title: 'Your ElevenLabs plan\'s concurrent request limit' });
    conc.addEventListener('change', () => setE('concurrency', Math.max(1, Number(conc.value) || 1)));
    const lang = h('input', { type: 'text', value: e.languageCode, placeholder: 'auto', disabled: locked, style: { width: '56px' }, title: 'Language code, e.g. en' });
    lang.addEventListener('change', () => setE('languageCode', lang.value.trim()));
    const norm = h('select', { disabled: locked, onchange: (ev) => setE('textNormalization', ev.target.value), title: 'Text normalization (numbers, abbreviations)' },
      ['auto', 'on', 'off'].map((v) => h('option', { value: v, selected: v === e.textNormalization }, v)));

    clear(tb).append(
      h('div.grp', h('label', 'Voice'), voiceSel, h('button.icon-btn', { title: 'Reload voices and models', onclick: () => loadEleven(true) }, '⟳')),
      h('div.grp', h('label', 'Model'), modelSel),
      stability,
      slider('similarity', 0, 1, 0.01, 'Similarity'),
      supportsStyle ? slider('style', 0, 1, 0.01, 'Style') : null,
      supportsBoost ? h('div.grp', h('label.row', h('input', { type: 'checkbox', checked: e.speakerBoost, disabled: locked, onchange: (ev) => setE('speakerBoost', ev.target.checked) }), 'Speaker boost')) : null,
      slider('speed', 0.7, 1.2, 0.01, 'Speed'),
      h('div.grp', h('label', 'Seed'), seed),
      h('div.grp', h('label', 'Format'), fmt),
      h('div.grp', h('label', 'Lang'), lang, h('label', 'Normalize'), norm),
      h('div.grp', h('label', 'Concurrent'), conc),
      locked ? h('span.badge.b-toolong', { title: 'Unlock in Voice packs' }, '🔒 locked') : null);
  }

  function renderTagbar() {
    const sel = () => [...grid.selected];
    const apply = async (tags, mode) => {
      const ids = sel();
      const scope = ids.length ? `${ids.length} selected line(s)` : 'every line shown';
      if (!ids.length && !(await confirmBox(mode === 'remove' ? 'Remove tag' : 'Add prefix tag', `${mode === 'remove' ? 'Remove' : 'Add'} ${tags.join(' ')} ${mode === 'remove' ? 'from' : 'to'} ${scope}?`, 'Apply'))) return;
      try {
        const r = await rpc('line2.bulkTags', { id, lineIds: ids.length ? ids : grid.rows.map((x) => x.lineId), tags, mode });
        patch = r.patch;
        index = byId();
        grid.setRows(visible());
        toast(`${r.changed} line(s) changed.`);
      } catch (e) { fail(e); }
    };
    clear(tagbar).append(
      h('label', 'Prefix tags'),
      voice.tags.map((t) => h('span.chip.tag-chip', { title: 'Click: add to selected (or all shown). Shift-click: remove.', onclick: (e) => apply([t], e.shiftKey ? 'remove' : 'add') }, t)),
      h('button.btn.sm.ghost', { onclick: async () => {
        const r = await prompt('Add tags', [{ name: 't', label: 'Tags', placeholder: '[nervous] [quietly]', help: 'Added at the start of the selected lines (or all lines shown).' }]);
        if (r && r.t.trim()) apply(r.t.match(/\[[^\]]+\]|[^\s\[\]]+/g), 'add');
      } }, '+ custom'),
      h('div.spacer'),
      h('button.btn.sm', { onclick: () => { const ids = sel(); if (!ids.length) { toast('Select lines first.', true); return; } autotag(ids); } }, '✨ Auto-tag selected'),
      h('button.btn.sm.ghost', { onclick: () => go('voices', { id: voice.id }) }, 'Edit tags…'));
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
      index = byId();
      grid.setRows(visible());
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
    const chip = (key, label) => h('span.chip' + (filter === key ? '.on' : ''), { onclick: () => { filter = key; store.set('patch:' + id, { filter }); renderCounts(); grid.setRows(visible()); } }, label, h('span.n', c[key]));
    const q = h('input', { type: 'text', placeholder: 'Search…', value: search });
    q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visible()); }, 150));
    clear(filters).append(chip('all', 'All'), chip('pending', 'To generate'), chip('generated', 'Generated'), chip('stale', 'Changed since take'),
      chip('failed', 'Failed'), chip('omitted', 'Omitted'), chip('differs', 'Spoken ≠ in-game'), chip('p1', 'Edited in Phase 1'),
      c.suggestions ? chip('suggestions', '✨ Suggestions') : null, h('div.spacer'), q);
  }

  // ---- inspector
  function renderInspector() {
    clear(insp);
    const l = focusedId && index.get(focusedId);
    if (!l) {
      insp.append(h('h3', 'Line'), h('div.dim', 'Click a row to see it in full. Double-click plays the selected take.'),
        h('div.faint', { style: { marginTop: '10px' } },
          h('div', 'The in-game line decides the filename and never changes here. The spoken text is what the voice says: edit it freely and add [tags] anywhere.'),
          h('div', { style: { marginTop: '6px' } }, 'Keys: ↑/↓ move, Space selects, Ctrl+A selects all shown.')));
      return;
    }
    const spoken = h('textarea', { rows: 3, disabled: l.omitted }, l.spoken);
    spoken.addEventListener('change', () => upd(l, 'spoken', spoken.value));
    insp.append(
      h('div.row', h('span.dot.' + (l.omitted ? 'Omitted' : l.status)), h('span.dim', l.omitted ? 'Omitted — already in ' + l.omittedBy : STATUS_TEXT[l.status] || l.status)),
      l.lastError ? h('div.banner.bad', { style: { margin: 0 } }, l.lastError) : null,
      h('div', h('div.field-label', 'Original line (before Phase 1)'), h('div.box', l.originalLine)),
      h('div', h('div.field-label', 'In-game line'), h('div.box', l.finalLine)),
      h('div', h('div.field-label', 'File'), h('div.box.mono', l.stem + '.wav / .lip / .fuz')),
      h('div', h('div.field-label', 'Spoken text (tags allowed)'), spoken,
        h('div.row', { style: { marginTop: '6px' } },
          h('button.btn.sm', { disabled: l.omitted, onclick: () => autotag([l.lineId]) }, '✨ Auto-tag'),
          l.spokenEdited ? h('button.btn.sm.ghost', { onclick: () => upd(l, 'resetSpoken') }, 'Reset to in-game line') : null,
          h('div.spacer'),
          h('button.btn.sm.primary', { disabled: l.omitted || !!app.job, onclick: () => generate('selected', [l.lineId]) }, l.takes.length ? 'New take' : 'Generate'))),
      l.tagSuggestion ? h('div.box', h('div.field-label', 'AI suggestion'), h('div', diffAdded(l.spoken, l.tagSuggestion)),
        h('div.row', { style: { marginTop: '6px' } }, h('button.btn.sm.primary', { onclick: () => upd(l, 'acceptTags') }, 'Accept'), h('button.btn.sm', { onclick: () => upd(l, 'rejectTags') }, 'Undo'))) : null,
      l.omittedBy ? h('label.row', h('input', { type: 'checkbox', checked: l.overrideOmit, onchange: (e) => upd(l, 'overrideOmit', e.target.checked) }), `Generate anyway (it is in "${l.omittedBy}")`) : null,
      h('h3', `Takes (${l.takes.length})`),
      l.takes.length ? h('div.col', l.takes.slice().reverse().map((t) => h('div.take' + (t.id === l.selectedTakeId ? '.sel' : ''),
        h('button.icon-btn', { title: 'Play', onclick: () => { audio.src = patch.mediaBase + encodeURI(t.wav) + '?t=' + Date.now(); audio.play().catch(fail); } }, '▶'),
        h('div.grow', h('div', '#' + t.slot + (t.id === l.selectedTakeId ? ' · selected' : '')),
          h('div.meta', `${t.durationSeconds.toFixed(2)} s · ${t.settings.modelId} · stab ${t.settings.stability} · sim ${t.settings.similarity}${t.settings.speakerBoost ? ' · boost' : ''}${t.settings.seed != null ? ' · seed ' + t.settings.seed : ''}`),
          t.spokenText !== l.spoken ? h('div.meta', { title: t.spokenText }, 'Spoken: ' + t.spokenText) : null),
        t.id !== l.selectedTakeId ? h('button.btn.sm', { onclick: () => upd(l, 'selectTake', t.id) }, 'Use') : null,
        h('button.icon-btn', { title: 'Discard take', onclick: async () => { if (await confirmBox('Discard take?', `Delete take #${t.slot} and its files?`, 'Discard', true)) upd(l, 'deleteTake', t.id); } }, '🗑'))))
        : h('div.faint', 'No takes yet.'),
      h('h3', 'Used by'),
      h('div.col', l.sources.map((s) => h('div.faint.mono', `${patch.modNames[s.modId] || '?'} · ${s.plugin} · ${s.formId}`))),
    );
  }

  // ---- actions
  function renderActions() {
    const sel = [...grid.selected];
    const busy = !!app.job;
    clear(actions).append(
      h('button.btn', { onclick: sync, disabled: busy, title: 'Pull in Phase 1 changes from the mods' }, '⟳ Sync from mods'),
      h('div.spacer'),
      sel.length ? h('span.dim', `${sel.length} selected`) : null,
      sel.length ? h('button.btn', { disabled: busy, onclick: () => generate('selected', sel) }, `Generate selected (${sel.length})`) : null,
      h('button.btn.primary', { disabled: busy, onclick: () => generate('missing') }, 'Generate missing'),
      h('button.btn', { disabled: busy, onclick: exportWavLip }, 'Copy WAV + LIP to folder'),
      h('button.btn', { disabled: busy, onclick: () => buildFuz(false) }, 'Build .fuz'),
      h('button.btn.ghost', { disabled: busy, onclick: () => buildFuz(true) }, '.fuz to other folder…'),
      h('button.btn.ghost', { disabled: busy, onclick: writeFix }, 'Write DBVO Fix plugins'));
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
        ['Language', est.languageCode || 'auto'], ['Text normalization', est.textNormalization],
        ['Loudness normalization', est.loudnorm ? 'ON' : 'off'], ['Gain', est.gainDb + ' dB'],
        ['Tail trim / fade', `${est.trimTailMs} ms / ${est.fadeOutMs} ms`], ['Naming', est.naming],
        ['Pronunciation entries', est.pronunciations], ['ElevenLabs dictionaries', est.dictionaries],
      ];
      const ok = await modal('Check before spending credits', [
        h('div.kv', rows.flatMap(([k, v]) => [h('div', k), h('div', String(v))])),
        !est.voiceId ? h('div.banner.bad', { style: { margin: 0 } }, 'Pick a voice in the toolbar first.') : null,
      ], [{ label: 'Cancel', value: false }, { label: `Generate ${est.count} line(s)`, primary: true, value: !!est.voiceId }]);
      if (!ok) return;
      await rpc('patch.generate', { id, mode, lineIds });
      renderActions();
    } catch (e) { fail(e); }
  }

  async function sync() {
    try {
      const r = await rpc('patch.sync', { id });
      patch = r.patch;
      index = byId();
      grid.setRows(visible());
      renderCounts();
      const rep = r.report;
      toast(`${rep.total} lines · ${rep.added} new · ${rep.removed} removed · ${rep.omitted} omitted${rep.stale ? ` · ${rep.stale} changed since take` : ''}`);
    } catch (e) { fail(e); }
  }

  async function exportWavLip() {
    try {
      const r = await rpc('patch.exportWavLip', { id });
      toast(`WAV + LIP: ${r.written} written, ${r.skipped} skipped (already there)${r.failed ? `, ${r.failed} failed` : ''}.`, r.failed > 0);
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
      await modal('Files written', [h('div.box.mono', { style: { whiteSpace: 'pre-wrap' } }, written.join('\n'))]);
    } catch (e) { fail(e); }
  }

  async function renderBanners() {
    clear(banners);
    try {
      const c = await rpc('patch.checks', { id });
      const t = c.tools;
      const missing = [!t.apiKey && 'ElevenLabs API key', !t.ffmpeg && 'ffmpeg.exe', !t.faceFx && 'FaceFXWrapper.exe', !t.fonix && 'FonixData.cdf', !t.xwma && 'xWMAEncode.exe (for .fuz)'].filter(Boolean);
      if (missing.length) banners.append(h('div.banner.bad', h('span', 'Missing: ' + missing.join(', ')), h('div.spacer'), h('button.btn.sm', { onclick: () => go('settings') }, 'Open Settings')));
      if (!c.wavLipFolder) banners.append(h('div.banner.warn', h('span', 'No WAV + LIP folder set for this voice pack.'), h('div.spacer'), h('button.btn.sm', { onclick: () => go('voices', { id: voice.id }) }, 'Set folders')));
      for (const w of c.pathWarnings) banners.append(h('div.banner.warn', h('b', 'Path length: '), h('span', `${w.where} reaches ${w.length} of ${w.limit} characters with the longest line — tools like Yakitori fail past 260.`), h('span.mono.faint', { title: w.example }, '…' + w.example.slice(-60))));
      if (c.unreviewed.length) banners.append(h('div.banner.warn', h('span', 'Unreviewed flagged lines in Phase 1: ' + c.unreviewed.map((u) => `${u.plugin} (${u.count})`).join(', '))));
    } catch (e) { /* checks are advisory */ }
  }

  function renderHead() {
    clear(head).append(
      h('button.btn.ghost.sm', { onclick: () => go('patches') }, '← DBVO Patches'),
      h('h1', patch.name),
      h('span.sub', voice.name + ' · ' + (voice.naming === 'Legacy' ? 'Legacy names' : 'DBVO 2.0 hash names')),
      h('div.spacer'),
      h('button.btn.sm', { onclick: editPatch }, 'Mods, references & folders…'),
      h('button.btn.sm.ghost.danger', { onclick: async () => { if (await confirmBox('Delete DBVO Patch?', 'Deletes its takes too. Files already exported stay where they are. A backup of the project is made first.', 'Delete', true)) { await rpc('patch.delete', { id }); go('patches'); } } }, 'Delete'));
  }

  async function editPatch() {
    await refreshState();
    const s = app.state;
    const name = h('input', { type: 'text', value: patch.name });
    const modBoxes = s.mods.map((m) => ({ m, box: h('input', { type: 'checkbox', checked: patch.modIds.includes(m.id) }) }));
    const refBoxes = s.references.map((r) => ({ r, box: h('input', { type: 'checkbox', checked: patch.referenceIds.includes(r.id) }) }));
    const folder = (val, title) => {
      const i = h('input', { type: 'text', value: val, placeholder: 'use the voice pack\'s setting' });
      return { i, el: h('div.path-input', i, h('button.btn.sm', { onclick: async () => { const f = await rpc('pick.folder', { title }); if (f) i.value = f; } }, 'Browse')) };
    };
    const wl = folder(patch.wavLipFolder, 'WAV + LIP folder');
    const fr = folder(patch.finalRoot, 'Final folder');
    const tpl = h('input', { type: 'text', value: patch.fuzTemplate, placeholder: voice.destinations.fuzTemplate });
    const ok = await modal('DBVO Patch settings', [
      h('div.col', h('div.field-label', 'Name'), name),
      h('div.col', h('div.field-label', 'Mods'), h('div.box', { style: { maxHeight: '180px', overflow: 'auto' } }, modBoxes.map(({ m, box }) => h('label.row', box, m.name)))),
      h('div.col', h('div.field-label', 'Omit lines users already have'), h('div.faint', 'Only tick topic files every user of this patch has (such as the base game pack). Never tick optional mods.'),
        refBoxes.length ? h('div.box', refBoxes.map(({ r, box }) => h('label.row', box, r.name, h('span.faint', `${r.lineCount} lines`)))) : h('div.faint', 'None yet — add them in Settings.')),
      h('div.col', h('div.field-label', 'WAV + LIP folder (this patch only)'), wl.el),
      h('div.col', h('div.field-label', 'Final folder (this patch only)'), fr.el),
      h('div.col', h('div.field-label', '.fuz folder template (this patch only)'), tpl, h('div.faint', 'Placeholders: {mod} {packId} {voicePack} {patch}. The .fuz files land in the last folder.')),
    ], [{ label: 'Cancel', value: false }, { label: 'Save', primary: true, value: true }]);
    if (!ok) return;
    try {
      patch = await rpc('patch.saveMeta', {
        id, name: name.value, modIds: modBoxes.filter((x) => x.box.checked).map((x) => x.m.id), referenceIds: refBoxes.filter((x) => x.box.checked).map((x) => x.r.id),
        wavLipFolder: wl.i.value, finalRoot: fr.i.value, fuzTemplate: tpl.value,
      });
      index = byId();
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
  const onLine = (d) => { if (d.patchId === id && document.body.contains(view)) replaceLine(d.line); };
  const onDone = () => { if (document.body.contains(view)) { renderActions(); renderBanners(); } };
  on('line2', onLine);
  document.addEventListener('job-done', onDone);
  document.addEventListener('job-progress', () => { if (document.body.contains(view) && actions.querySelector('.btn.primary:not([disabled])')) renderActions(); });

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
