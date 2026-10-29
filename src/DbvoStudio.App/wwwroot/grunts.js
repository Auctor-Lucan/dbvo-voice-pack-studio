// Other Tools → Player Grunts Tool: player voice packs for PC Head Tracking (with or without Multi Voice Packs) and
// Smart Player Voice System. Events are shown as blocks by category; the panel on the right holds the selected event's
// lines. Event definitions are data (built in, read from the installed mods, or the user's own custom events).
import { rpc, h, clear, toast, fail, modal, confirmBox, store, debounce, switchEl, segmented, popupMenu, droppedPaths, icon, helpButton, withBusy, pathText, pathRows, pathList, searchLamp, pdoneChips, closeDialogOf } from './lib.js';
import { app, go, refreshState } from './app.js';
import { setPaletteExtra } from './nav.js';
import { recordTake } from './recorder.js';
import { insertTagAtCaret } from './tagtools.js';

const TARGET = { pcht: 'PCHT', spvs: 'SPVS' };
const TARGET_LONG = { pcht: 'PC Head Tracking', spvs: 'Smart Player Voice System' };
const SLOTS = ['FemaleCustom01', 'FemaleCustom02', 'FemaleCustom03', 'FemaleCustom04', 'FemaleCustom05', 'MaleCustom01', 'MaleCustom02', 'MaleCustom03', 'MaleCustom04', 'MaleCustom05'];
const SUB_LANGS = [['en', 'English'], ['ja', 'Japanese'], ['zh', 'Chinese'], ['fr', 'French'], ['de', 'German'], ['it', 'Italian'], ['pl', 'Polish'], ['es', 'Spanish'], ['ru', 'Russian'], ['ko', 'Korean']];
const STATUS_TIP = { empty: 'No audio yet', full: 'Has audio', partial: 'Some parts have audio', broken: 'An audio file is missing on disk', skipped: 'Left out on purpose' };
const fmtSec = (s) => (s ? s.toFixed(s < 10 ? 2 : 1) + ' s' : '–');
const kindBadge = (x) => x.target === 'pcht' ? (x.multi ? 'MPCHT' : 'PCHT') : 'SPVS';

// ------------------------------------------------------------------ paths (shared with the list and the project)

async function pathsDialog(paths, onSaved) {
  const pi = h('input', { type: 'text', value: paths.pchtFolder || '', placeholder: 'e.g. D:\\MO2\\mods\\PC Head Tracking and Voice Type SE', style: { width: '100%' } });
  const si = h('input', { type: 'text', value: paths.spvsFolder || '', placeholder: 'e.g. D:\\MO2\\mods\\Smart Player Voice System', style: { width: '100%' } });
  const pick = (input, title) => h('button.btn.sm', { title, onclick: async () => { const f = await rpc('pick.folder', { title }); if (f) input.value = f; } }, 'Choose…');
  const ok = await modal('Installed mod folders', [
    h('div.faint', 'The tool reads these folders (it never changes them): the event list, the shout and vanilla-based file names, and the plugin a PCHT pack builds on. Without them the built-in event list is used.'),
    h('div.col', h('div.field-label', 'PC Head Tracking folder'), h('div.row', pi, pick(pi, 'The folder with PC Head Tracking - MCM.esp')),
      h('div.faint', 'The one with the active PC Head Tracking - MCM.esp. With Multi Voice Packs support installed over PCHT (MO2), pick its folder: its version of the plugin is the one a voice set needs.')),
    h('div.col', h('div.field-label', 'Smart Player Voice System folder'), h('div.row', si, pick(si, 'The folder with SmartPlayerVoiceSystem.esp')),
      h('div.faint', 'Its SKSE\\Plugins\\AudioUtil config lists the events SPVS plays, so new SPVS events show up without a DBVO Studio update.')),
  ], [{ label: 'Cancel', value: false }, { label: 'Save', primary: true, value: true }], { help: 'player-grunts-tool' });
  if (!ok) return;
  try { const r = await rpc('grunts.paths', { pchtFolder: pi.value.trim(), spvsFolder: si.value.trim() }); onSaved && onSaved(r); } catch (e) { fail(e); }
}

function pathsBanner(paths, target, onSaved) {
  const ok = target === 'spvs' ? paths.spvsOk : paths.pchtOk;
  const folder = target === 'spvs' ? paths.spvsFolder : paths.pchtFolder;
  if (ok) return null;
  const why = folder ? (target === 'spvs' ? paths.spvsState : paths.pchtState) : `The ${TARGET_LONG[target]} folder isn't set.`;
  return h('div.banner.' + (folder ? 'bad' : 'warn'), h('span', why + (target === 'pcht' ? ' A PCHT export with more or fewer lines than PCHT\'s own (and every Multi Voice Packs export) needs its plugin.' : ' The built-in event list (SPVS 1.26) is used meanwhile.')),
    h('div.spacer'), h('button.btn.sm', { title: 'Set the installed mod folders', onclick: () => pathsDialog(paths, onSaved) }, 'Set folders…'));
}

// ------------------------------------------------------------------ list

export async function gruntsListView() {
  const s = await refreshState();
  const [list, paths] = await Promise.all([rpc('grunts.list'), rpc('grunts.paths', {})]);
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'Player Grunts Tool'),
    helpButton('player-grunts-tool'),
    h('span.sub', 'Other Tools · player voice packs for PC Head Tracking (and Multi Voice Packs) and Smart Player Voice System: attacks, hits, breathing, shouts, greetings and every other event.'),
    h('div.spacer'),
    h('button.btn', { title: 'The installed PCHT and SPVS folders the tool reads', onclick: () => pathsDialog(paths, () => go('grunts')) }, 'Mod folders…'),
    h('button.btn.primary', { title: 'Start a player voice pack', onclick: () => create(s, paths) }, '+ New project')));
  const body = h('div.view-body');
  view.append(body);
  const state = (ok, folder, text) => h('span.badge.' + (ok ? 'b-valid' : folder ? 'b-broken' : 'b-skip'), { title: folder || 'Not set' }, ok ? text : folder ? 'not readable' : 'not set');
  body.append(h('div.stat-row', { style: { margin: '0 4px 12px' } },
    h('span', 'PC Head Tracking: ', state(paths.pchtOk, paths.pchtFolder, paths.pchtMulti ? 'with Multi Voice Packs' : 'found')),
    h('span', 'Smart Player Voice System: ', state(paths.spvsOk, paths.spvsFolder, paths.spvsState || 'found'))));
  if (!list.length) {
    body.append(h('div.empty', h('h2', 'No player voice packs yet'),
      h('div', 'Pick PCHT or SPVS, then drop audio on each event: attacks, power attacks, hits, breathing, death, shouts and (with SPVS) dozens of spoken comments. Import an existing pack folder to start from it.'),
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { title: 'Start a player voice pack', onclick: () => create(s, paths) }, '+ New project'))));
    return view;
  }
  body.append(h('div.cat', h('div.cat-head', h('h3', 'Player voice packs'), h('span.faint', list.length)),
    h('div.rows3', list.filter((x) => !(x.total && x.events >= x.total)).map((x) => h('div.prow', { title: 'Open this project', onclick: () => go('grunt', { id: x.id }) },
      h('div.row', h('span.title.ellipsis.grow', x.name), h('span.badge.b-fixed', { title: TARGET_LONG[x.target] + (x.multi ? ' · Multi Voice Packs' : '') }, kindBadge(x))),
      h('div.faint.ellipsis', x.target === 'spvs' ? `Voice slot ${x.slot}` : x.multi ? `Voice set ${x.prefix || '(no prefix yet)'} · ${x.gender}` : `Default PCHT voice · ${x.gender}`),
      h('div.prow-stats',
        h('div.grow', h('div.bar' + (x.total && x.events >= x.total ? '.ok' : ''), h('div.bar-fill', { style: { width: (x.total ? (100 * x.events) / x.total : 0) + '%' } }))),
        h('span.mono', { title: 'Events with audio / all events' }, `${x.events} / ${x.total}`),
        h('span.left', h('b', String(x.lines)), ' lines'))))),
    pdoneChips(list.filter((x) => x.total && x.events >= x.total), (x) => go('grunt', { id: x.id }), (x) => `${x.events}/${x.total}`, 'every event has audio')));
  return view;
}

async function create(s, paths) {
  let target = 'pcht', multi = !!paths.pchtMulti, gender = 'female', slot = 'FemaleCustom01';
  const name = h('input', { type: 'text', placeholder: 'e.g. Vampira', style: { width: '100%' } });
  const vp = h('select', { style: { width: '100%' } }, h('option', { value: '' }, '— none (import or record your own audio) —'), (s.voicePacks || []).map((v) => h('option', { value: v.id }, v.name)));
  const extra = h('div.col');
  const renderExtra = () => {
    clear(extra);
    if (target === 'pcht') {
      extra.append(
        switchEl(multi, (v) => { multi = v; renderExtra(); }, 'Multi Voice Packs support (MPCHT)', { title: 'A voice set players pick per character in the PCHT MCM; needs "Multi Voice Packs support for PC Head Tracking" and FormList Manipulator' }),
        h('div.faint', multi ? 'Exports PCHTV_<prefix>.esp, its FormList Manipulator .ini and the sounds under a prefix folder, so it installs next to other voice sets.' : 'Exports the default PCHT voice: the sound folders, and a small plugin when an event has more or fewer lines than PCHT\'s own.'),
        h('div.col', h('div.field-label', 'Voice'), segmented(multi ? [['female', 'Female'], ['male', 'Male'], ['both', 'Both']] : [['female', 'Female'], ['male', 'Male'], ['both', 'Both']], gender, (v) => { gender = v; }), h('div.faint', 'Which BYO voice type the shouts go to (both: the same files for each).')));
    } else {
      const sel = h('select', SLOTS.map((x) => h('option', { value: x, selected: x === slot }, x)));
      sel.addEventListener('change', () => { slot = sel.value; gender = slot.startsWith('Male') ? 'male' : 'female'; });
      extra.append(h('div.col', h('div.field-label', 'SPVS voice slot'), sel, h('div.faint', 'SPVS has five custom female and five custom male slots. Pick one no other installed pack uses.')));
    }
  };
  const tsel = segmented([['pcht', 'PC Head Tracking'], ['spvs', 'Smart Player Voice System']], target, (v) => { target = v; renderExtra(); });
  renderExtra();
  const ok = await modal('New player voice pack', [
    h('div.col', h('div.field-label', 'Name'), name),
    h('div.col', h('div.field-label', 'For'), tsel),
    extra,
    h('div.col', h('div.field-label', 'DBVO voice pack (optional)'), vp, h('div.faint', 'Its voice converts your recordings (Voice Changer) and reads spoken lines (text-to-speech).')),
  ], [{ label: 'Cancel', value: false }, { label: 'Create', primary: true, value: true }], { help: 'player-grunts-tool' });
  if (!ok) return;
  try {
    const c = await rpc('grunts.create', { name: name.value.trim(), target, multi, gender: target === 'spvs' ? (slot.startsWith('Male') ? 'male' : 'female') : gender, slot, voicePackId: vp.value });
    go('grunt', { id: c.id });
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ project

export async function gruntProjectView(id) {
  await refreshState();
  let d = await withBusy(rpc('grunts.get', { id }), 'Loading the voice pack', 'Reading the events and audio…');
  let tab = store.get('gr-tab:' + id, 'events');
  let filter = store.get('gr-filter:' + id, 'all'), search = '', catFilter = '';
  let selId = store.get('gr-sel:' + id, null);
  let showInsp = store.get('gr-insp', true);
  let collapsed = new Set(store.get('gr-collapsed:' + id, []));
  const audio = new Audio();
  let playing = null;
  let saving = 0;
  const P = () => d.project;
  const evById = () => new Map(d.events.map((e) => [e.id, e]));
  let byId = evById();

  const view = h('div.view.work.grunts');
  const head = h('div.view-head');
  const body = h('div', { style: { flex: '1', minHeight: '0', display: 'flex', flexDirection: 'column' } });
  view.append(head, body);
  const saveState = h('span.save-state', 'All changes saved');
  const setSaving = (on) => { saving += on ? 1 : -1; saveState.textContent = saving > 0 ? 'Saving…' : 'All changes saved'; saveState.classList.toggle('busy', saving > 0); };

  async function call(method, params, { quiet = false } = {}) {
    setSaving(true);
    try {
      const r = await rpc(method, { id, ...params });
      // A full project has an events list; some replies carry it as .project (and use .events for a count).
      const proj = r && Array.isArray(r.events) ? r : r && r.project && Array.isArray(r.project.events) ? r.project : null;
      if (proj) { d = proj; byId = evById(); }
      return r;
    } catch (e) { if (!quiet) fail(e); throw e; } finally { setSaving(false); }
  }
  async function saveProject(o) { try { await call('grunts.save', o); renderAll(); } catch { renderAll(); } }

  // ---------------------------------------------------------------- head
  function renderHead() {
    const p = P();
    const withAudio = d.events.filter((e) => e.lines.length).length;
    const exp = h('button.btn.accent.export-btn', { title: 'Check the project, then write the voice pack to the output folder' }, icon('export', { size: 15 }), ' Export ▾');
    exp.addEventListener('click', () => popupMenu(exp, [
      { label: 'Export the voice pack', sub: p.outputFolder ? `→ ${p.outputFolder}` : 'Choose the output folder first', onclick: doExport },
      { label: 'Check only', sub: 'List problems and what the export would write; nothing is written', onclick: () => check(false) },
      { label: 'Output folder…', sub: p.outputFolder || 'Not set', onclick: pickOutput },
      p.outputFolder ? { label: 'Open the output folder', sub: p.outputFolder, onclick: () => rpc('grunts.openFolder', { id, which: 'output' }).catch(fail) } : null,
      p.lastReport ? { label: 'Last export report', sub: p.lastExportUtc ? new Date(p.lastExportUtc).toLocaleString() : '', onclick: () => showReport(p.lastReport) } : null,
    ].filter(Boolean)));
    const tabs = segmented([
      ['events', `Events (${withAudio}/${d.events.length})`, 'Every event as a block, by category: drop audio on it'],
      ['setup', 'Setup & export', 'Mod folders, PCHT / SPVS options, loudness, output and custom events'],
    ], tab, (v) => { tab = v; store.set('gr-tab:' + id, v); renderAll(); });
    clear(head).append(
      h('button.btn.ghost.sm', { title: 'Back to the project list', onclick: () => go('grunts') }, '← Player Grunts'),
      h('h1', p.name),
      h('span.badge.b-fixed', { title: TARGET_LONG[p.target] + (p.target === 'pcht' && p.pcht.multi ? ' with Multi Voice Packs: voice set ' + (p.pcht.prefix || '?') : p.target === 'spvs' ? ' · voice slot ' + p.spvs.slot : ' · the default PCHT voice') }, p.target === 'pcht' ? (p.pcht.multi ? 'MPCHT · ' + (p.pcht.prefix || '?') : 'PCHT') : 'SPVS · ' + p.spvs.slot),
      tabs,
      helpButton(tab === 'setup' ? 'grunts-setup' : 'grunt-events'),
      h('div.spacer'),
      saveState,
      h('button.btn', { title: 'List every problem and what the export would write (nothing is written)', onclick: () => check(false) }, 'Check'),
      exp,
      h('span', { style: { width: '18px' } }),
      h('button.btn.ghost.danger.sm', { title: 'Delete this project, its recordings and generated lines (your imported files and exported packs stay)', onclick: async () => { if (await confirmBox('Delete player voice pack project?', 'Deletes the project with its own recordings and generated lines. Audio files you imported stay where they are, and so does anything you exported.', 'Delete', true)) { try { await rpc('grunts.delete', { id }); go('grunts'); } catch (e) { fail(e); } } } }, 'Delete'));
  }

  // ---------------------------------------------------------------- events tab
  const left = h('div.left');
  const insp = h('div.inspector');
  const filters = h('div.filters');
  const blocks = h('div.gr-blocks');
  const statusbar = h('div.statusbar');
  left.append(blocks);
  blocks.addEventListener('scroll', debounce(() => store.set('gr-scroll:' + id, blocks.scrollTop), 300));

  const isDone = (e) => e.status === 'full' || e.status === 'partial';
  const counts = () => {
    const c = { all: d.events.length, empty: 0, has: 0, partial: 0, broken: 0, skipped: 0, custom: 0, extra: 0 };
    for (const e of d.events) {
      if (e.status === 'empty') c.empty++;
      if (isDone(e)) c.has++;
      if (e.status === 'partial') c.partial++;
      if (e.status === 'broken') c.broken++;
      if (e.status === 'skipped') c.skipped++;
      if (e.origin === 'custom') c.custom++;
      if (e.kind === 'pool' && P().target === 'pcht' && e.lines.length > e.baseSlots) c.extra++;
    }
    return c;
  };
  function matches(e) {
    if (catFilter && e.category !== catFilter) return false;
    if (filter === 'empty' && e.status !== 'empty') return false;
    if (filter === 'has' && !isDone(e)) return false;
    if (filter === 'partial' && e.status !== 'partial') return false;
    if (filter === 'broken' && e.status !== 'broken') return false;
    if (filter === 'skipped' && e.status !== 'skipped') return false;
    if (filter === 'custom' && e.origin !== 'custom') return false;
    if (filter === 'extra' && !(e.kind === 'pool' && e.lines.length > e.baseSlots)) return false;
    if (search) {
      const q = search.toLowerCase();
      const hay = [e.label, e.category, e.description, e.folder, e.key, e.sndr, ...e.lines.map((l) => l.file + ' ' + l.text), ...e.slots.map((s) => s.text)].join(' ').toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  }
  function renderFilters() {
    const c = counts();
    const chip = (k, label, tip) => (k === 'all' || filter === k || c[k] ? h('button.chip' + (filter === k ? '.on' : ''), { title: tip, onclick: () => { filter = k; store.set('gr-filter:' + id, k); renderFilters(); renderBlocks(); } }, label, ' ', h('span.n', String(c[k]))) : null);
    const q = h('input', { type: 'text', placeholder: 'Search events, files, text…', value: search, title: 'Filter the events' });
    q.addEventListener('input', debounce(() => { search = q.value.trim(); renderBlocks(); }, 200));
    const cats = [...new Set(d.events.map((e) => e.category))];
    const cs = h('select', { title: 'Show one category' }, h('option', { value: '' }, 'All categories'), cats.map((x) => h('option', { value: x, selected: x === catFilter }, x)));
    cs.addEventListener('change', () => { catFilter = cs.value; renderBlocks(); });
    const imp = h('button.btn.sm', { title: 'Bring in audio from elsewhere' }, 'Import ▾');
    imp.addEventListener('click', () => popupMenu(imp, [
      { label: 'Import a voice pack folder…', sub: `A PCHT or SPVS pack's mod folder (the one with Sound). Files are matched to events${P().target === 'spvs' ? '; a PCHT pack is mapped the way SPVS\'s rename tool does it' : '; an SPVS pack is mapped back to PCHT\'s events'}.`, onclick: importPack },
      d.others.length ? { label: 'Copy from another project…', sub: 'Its lines go to the matching events (PCHT ↔ SPVS works too)', onclick: importProject } : null,
      { label: 'Find missing files…', sub: 'Pick a folder: files that were moved are found again by name', onclick: relink },
    ].filter(Boolean)));
    clear(filters).append(
      chip('all', 'All', 'Every event'), chip('empty', 'No audio', 'Events without any line'), chip('has', 'Has audio', 'Events with lines'),
      chip('partial', 'Shouts in part', 'Shouts with some parts still missing'), chip('broken', 'Missing files', 'A line\'s audio file isn\'t on disk any more'),
      chip('extra', 'More than PCHT\'s own', 'PCHT events with more lines than the base plugin lists (the export adds them)'),
      chip('skipped', 'Skipped', 'Left out on purpose'), chip('custom', 'Custom', 'Events you added'),
      h('div.spacer'), searchLamp(q), cs, imp,
      h('button.btn.sm', { title: 'Open every line\'s subtitle in one .toml file in your text editor; save it and come back, and the changes are read in', onclick: openSubtitles }, 'Subtitles .toml'),
      h('button.btn.sm', { title: 'Add, edit, import or export your own event definitions', onclick: customEventsDialog }, 'Custom events…'),
      h('button.btn.sm', { title: 'Show or hide the event panel', onclick: () => { showInsp = !showInsp; store.set('gr-insp', showInsp); renderAll(); } }, showInsp ? 'Hide panel ▸' : '◂ Show panel'));
  }

  function tileMeta(e) {
    if (e.kind === 'slots') return `${e.lines.length}/${e.slots.length} parts`;
    const n = e.lines.length;
    if (!n) return e.status === 'skipped' ? 'skipped' : 'no audio';
    const base = P().target === 'pcht' ? (n === e.baseSlots ? ' · as PCHT' : ` · PCHT lists ${e.baseSlots}`) : e.vanilla ? ` · ${e.vanilla} fixed` : '';
    return `${n} line${n === 1 ? '' : 's'}${base}`;
  }
  function renderBlocks() {
    const keep = blocks.scrollTop;
    clear(blocks);
    const shown = d.events.filter(matches);
    if (!shown.length) { blocks.append(h('div.empty', 'No events match.')); return; }
    const groups = new Map();
    for (const e of shown) { if (!groups.has(e.category)) groups.set(e.category, []); groups.get(e.category).push(e); }
    // Small categories (one to three events) sit side by side in one row instead of a whole row each.
    let smallRow = null;
    for (const [cat, list] of groups) {
      const small = list.length <= 3;
      const done = list.filter(isDone).length;
      const closed = collapsed.has(cat) && !search;
      const headEl = h('div.gr-cat-head', { title: closed ? 'Show this category' : 'Fold this category', onclick: () => { if (collapsed.has(cat)) collapsed.delete(cat); else collapsed.add(cat); store.set('gr-collapsed:' + id, [...collapsed]); renderBlocks(); } },
        h('span.caret', closed ? '▸' : '▾'), h('h3', cat), h('span.faint', `${done}/${list.length}`),
        h('div.gr-cat-bar', h('i', { style: { width: (100 * done / list.length) + '%' } })));
      const grid = h('div.gr-grid' + (closed ? '.hidden' : ''), small ? { style: { gridTemplateColumns: `repeat(${list.length}, 176px)` } } : {});
      for (const e of list) grid.append(tile(e));
      const catEl = h('div.gr-cat' + (small ? '.small' : ''), headEl, grid);
      if (small) {
        if (!smallRow) { smallRow = h('div.gr-smallrow'); blocks.append(smallRow); }
        smallRow.append(catEl);
      } else { smallRow = null; blocks.append(catEl); }
    }
    blocks.scrollTop = keep;
  }
  function tile(e) {
    const t = h('div.gr-tile.st-' + e.status + (e.id === selId ? '.sel' : ''), {
      'data-id': e.id,
      title: `${e.label}${e.description ? '\n' + e.description : ''}\n${STATUS_TIP[e.status] || ''} · drop audio files here`,
      onclick: () => select(e.id),
    },
    h('div.gr-tile-top', h('span.gr-dot'), h('span.gr-label.ellipsis', e.label),
      e.origin === 'custom' ? h('span.badge.b-player', { title: 'Your own event' }, 'custom') : null,
      e.lines.length ? h('button.icon-btn.gr-play', { title: 'Play a random line', onclick: (ev) => { ev.stopPropagation(); playLine(e.lines[Math.floor(Math.random() * e.lines.length)]); } }, '▶') : null),
    h('div.gr-tile-meta', tileMeta(e), e.missing ? h('span', { style: { color: 'var(--bad)' } }, ` · ${e.missing} missing`) : null),
    e.kind === 'slots' ? h('div.gr-parts', e.slots.map((s) => h('i' + (e.lines.some((l) => l.slot === s.id) ? '.on' : ''), { title: `${s.label}: ${s.text}` }))) : null);
    dropTarget(t, (paths) => addFiles(e, paths));
    return t;
  }
  function dropTarget(el, onPaths) {
    el.addEventListener('dragover', (ev) => { ev.preventDefault(); el.classList.add('over'); });
    el.addEventListener('dragleave', () => el.classList.remove('over'));
    el.addEventListener('drop', async (ev) => {
      ev.preventDefault(); ev.stopPropagation(); el.classList.remove('over');
      try { const paths = await droppedPaths([...(ev.dataTransfer.files || [])]); if (paths.length) onPaths(paths); } catch (err) { fail(err); }
    });
  }
  function select(eid) {
    selId = eid; store.set('gr-sel:' + id, eid);
    // Only the highlight moves (re-drawing every block on each click was slow with many events).
    blocks.querySelectorAll('.gr-tile.sel').forEach((x) => x.classList.remove('sel'));
    const t = blocks.querySelector(`.gr-tile[data-id="${CSS.escape(eid)}"]`);
    if (t) t.classList.add('sel'); else renderBlocks();
    renderInspector();
    if (!showInsp) { showInsp = true; store.set('gr-insp', true); renderAll(); }
  }

  // ---------------------------------------------------------------- audio
  async function playLine(l) {
    try {
      if (playing === l.id && !audio.paused) { audio.pause(); playing = null; return; }
      audio.src = await rpc('grunts.audio', { id, lineId: l.id });
      playing = l.id;
      await audio.play();
    } catch (e) { fail(e); }
  }

  async function addFiles(e, paths, slot) {
    try {
      const r = await call('grunts.addFiles', { eventId: e.id, paths, slot: slot || '' });
      toast(r.added ? `${e.label}: ${r.added} file${r.added === 1 ? '' : 's'} added${r.skipped ? ` (${r.skipped} already there)` : ''}.` : 'Those files are already in this event.');
      selId = e.id; store.set('gr-sel:' + id, e.id);
      renderAll();
    } catch { /* shown */ }
  }
  async function pickFiles(e, slot) {
    const f = await rpc('pick.files', { title: `Audio for ${e.label}`, filter: 'Audio|*.wav;*.xwm;*.fuz;*.mp3;*.ogg;*.flac;*.m4a;*.opus' });
    if (f && f.length) addFiles(e, f, slot);
  }
  async function line(e, l, o) {
    try { await call('grunts.line', { eventId: e.id, lineId: l.id, ...o }); renderAll(); } catch { renderAll(); }
  }
  async function record(e, slot) {
    const s = slot ? e.slots.find((x) => x.id === slot) : null;
    const saved = await recordTake({ patchId: id, kind: 'grunts', subtitles: true,
      line: { lineId: e.id + '|' + (slot || ''), spoken: s ? `“${s.text}”` : e.label, heading: s ? `${e.label} · ${s.label}` : e.category !== e.label ? e.category : '', hint: s ? '' : e.description, subtitle: s ? s.text : '' } });
    if (saved) { d = await rpc('grunts.get', { id }); byId = evById(); renderAll(); }
  }
  // Text-to-speech workspace: write, tag, try as often as needed, listen and compare; only Save puts a try on the event.
  // The texts and every try are kept with the project (closing keeps them; Discard throws them away).
  async function tts(e, slot = '', line = null) {
    const key = { eventId: e.id, slot: slot || '', lineId: line ? line.id : '' };
    let dr;
    try { dr = await rpc('grunts.draft', { id, ...key }); } catch (err) { fail(err); return; }
    const V = dr.voice;
    const sub = h('input', { type: 'text', value: dr.subtitle, placeholder: P().target === 'spvs' ? 'What the subtitle shows, e.g. Got you!' : 'Optional: kept with the line (PCHT shows no subtitles)', style: { width: '100%' } });
    const spoken = h('textarea.gr-spoken', { rows: 3, placeholder: V.engine === 'elevenlabs' ? 'What the voice gets, e.g. [effortful grunt] Hhh-AH! or [breathless] Not... yet.' : 'What the voice gets, with punctuation and formatting for delivery' }, dr.spoken);
    const saveText = debounce(() => rpc('grunts.draftText', { id, ...key, subtitle: sub.value, spoken: spoken.value }).catch(() => {}), 600);
    sub.addEventListener('input', saveText);
    spoken.addEventListener('input', saveText);
    let sendTags = V.engine !== 'voxcpm';
    let stability = V.stability, speed = V.speed, keepSeed = false;
    const chip = (t) => { const c = h('span.chip.tag-chip', { title: 'Insert at the cursor in the spoken text' }, t); c.addEventListener('mousedown', (ev) => ev.preventDefault()); c.addEventListener('click', () => { insertTagAtCaret(spoken, t); saveText(); }); return c; };
    const custom = h('input', { type: 'text', placeholder: '[any tag]', title: 'Type a tag and press Enter to insert it at the cursor', style: { width: '120px' } });
    custom.addEventListener('keydown', (ev) => { if (ev.key !== 'Enter' || !custom.value.trim()) return; ev.preventDefault(); let t = custom.value.trim(); if (!t.startsWith('[')) t = `[${t.replace(/[[\]]/g, '')}]`; insertTagAtCaret(spoken, t); custom.value = ''; saveText(); });
    const tagNote = V.engine === 'voxcpm' ? h('span.faint', 'VoxCPM reads [tags] aloud, so they are left out unless you turn this on; punctuation, … and CAPS still shape the delivery.')
      : V.engine === 'omnivoice' ? h('span.faint', 'OmniVoice reads inline tags such as [laughter], [sigh] and [gasp].')
      : V.tagModel ? h('span.faint', 'v3 / v4 models perform [tags]: emotions, breaths, grunts, laughs, shouts…') : h('span', { style: { color: 'var(--warn)' } }, `${V.model} ignores [tags]; pick a v3 or v4 model in the voice pack to use them.`);
    const tagbar = h('div.row.wrap.gr-tagbar', h('span.field-label', { style: { margin: 0 } }, 'Tags'), V.tags.map(chip), custom,
      V.engine === 'elevenlabs' ? null : switchEl(sendTags, (v) => { sendTags = v; }, 'Send [tags]', { title: 'Leave [tags] in the text sent to the local engine' }),
      h('div.spacer'), h('button.btn.sm.ghost', { title: 'Copy the subtitle into the spoken text', onclick: () => { spoken.value = sub.value; saveText(); spoken.focus(); } }, 'Subtitle → spoken'));
    // Per-try voice settings (the voice pack stays as it is).
    const settings = h('div.row.wrap', { style: { gap: '14px' } });
    if (V.engine === 'elevenlabs') {
      const stab = V.discrete
        ? segmented([[0, 'Creative'], [0.5, 'Natural'], [1, 'Robust']], [0, 0.5, 1].reduce((a, b) => (Math.abs(b - stability) < Math.abs(a - stability) ? b : a)), (v) => { stability = Number(v); })
        : (() => { const r = h('input', { type: 'range', min: 0, max: 1, step: 0.05, value: stability }); const lbl = h('span.mono', stability.toFixed(2)); r.addEventListener('input', () => { stability = Number(r.value); lbl.textContent = stability.toFixed(2); }); return h('span.row', r, lbl); })();
      const sp = h('input', { type: 'number', min: 0.7, max: 1.2, step: 0.05, value: speed, style: { width: '70px' } });
      sp.addEventListener('change', () => { speed = Number(sp.value) || 1; });
      settings.append(h('div.tgroup', h('label', 'Stability'), stab), h('div.tgroup', h('label', 'Speed'), sp),
        V.seed != null ? h('div.tgroup', h('label', 'Seed'), switchEl(false, (v) => { keepSeed = v; }, `Keep ${V.seed}`, { title: 'Off: a new seed every try (different takes). On: the voice pack\'s seed (the same take for the same text).' })) : null);
    } else if (V.engine === 'omnivoice') {
      const sp = h('input', { type: 'number', min: 0.5, max: 2, step: 0.05, value: speed, style: { width: '70px' } });
      sp.addEventListener('change', () => { speed = Number(sp.value) || 1; });
      settings.append(h('div.tgroup', h('label', 'Speed'), sp));
    }
    const status = h('span.faint', '');
    const genBtn = h('button.btn.primary', { title: 'Make a new try with this text and these settings (Ctrl+Enter)', disabled: !V.ready }, '▶ Generate try');
    const tries = h('div.gr-tries');
    const triesLbl = h('div.field-label');
    const saveBtn = h('button.btn.primary', {}, '');
    const audioT = new Audio();
    const paintTries = () => {
      clear(tries);
      triesLbl.textContent = dr.tries.length ? `Tries (${dr.tries.length}) · pick the one to save` : 'Tries';
      if (!dr.tries.length) tries.append(h('div.dim', { style: { padding: '10px 4px' } }, 'No tries yet. Write the spoken text, add tags, Generate, listen; change and try again. Nothing reaches the event until you save a try.'));
      dr.tries.forEach((t, n) => {
        const sel = t.id === dr.selectedId;
        const row = h('div.gr-try' + (sel ? '.sel' : ''), { title: 'Click to pick this try', onclick: () => { dr.selectedId = t.id; rpc('grunts.draftText', { id, ...key, selectedId: t.id }).catch(() => {}); paintTries(); } },
          h('input', { type: 'radio', name: 'gr-try', checked: sel }),
          h('button.icon-btn', { title: 'Play this try', onclick: (ev) => { ev.stopPropagation(); audioT.src = t.url + '?t=' + n; audioT.play().catch(fail); } }, '▶'),
          h('b.mono', '#' + (n + 1)), h('span.mono.faint', fmtSec(t.seconds)),
          h('span.ellipsis.grow', { title: t.spoken }, t.spoken),
          h('span.faint.ellipsis', { style: { maxWidth: '240px' }, title: t.settings }, t.settings),
          h('button.icon-btn', { title: 'Throw this try away', onclick: async (ev) => { ev.stopPropagation(); try { dr = await rpc('grunts.tryDelete', { id, ...key, tryId: t.id }); paintTries(); } catch (err) { fail(err); } } }, '🗑'));
        tries.append(row);
      });
      tries.scrollTop = tries.scrollHeight;
      const target = line ? `Replace ${line.file}` : slot ? `Save to ${dr.slotLabel || 'the part'}` : `Save to ${e.label}`;
      saveBtn.textContent = dr.selectedId ? `${target} (try #${dr.tries.findIndex((t) => t.id === dr.selectedId) + 1})` : target;
      saveBtn.disabled = !dr.selectedId;
    };
    async function generate() {
      if (!spoken.value.trim()) { toast('Write the spoken text first.', true); spoken.focus(); return; }
      genBtn.disabled = true; status.textContent = 'Generating…';
      try {
        const r = await rpc('grunts.try', { id, ...key, spoken: spoken.value, subtitle: sub.value, stability, speed, keepSeed, sendTags });
        dr = r.draft; paintTries(); status.textContent = '';
        const t = dr.tries.find((x) => x.id === r.tryId);
        if (t) { audioT.src = t.url; audioT.play().catch(() => {}); }
      } catch (err) { status.textContent = ''; fail(err); } finally { genBtn.disabled = !V.ready; }
    }
    genBtn.addEventListener('click', generate);
    spoken.addEventListener('keydown', (ev) => { if (ev.key === 'Enter' && (ev.ctrlKey || ev.metaKey)) { ev.preventDefault(); generate(); } });
    let committed = null;
    saveBtn.addEventListener('click', async () => {
      try {
        const r = await rpc('grunts.commit', { id, ...key, tryId: dr.selectedId, subtitle: sub.value });
        committed = r; d = r.project; byId = evById();
        closeDialogOf(saveBtn);
      } catch (err) { fail(err); }
    });
    const discard = h('button.btn.ghost.danger', { title: 'Throw away every try and the texts of this workspace' }, 'Discard all');
    let armed = false;
    discard.addEventListener('click', async () => {
      // A second click confirms (a dialog on top would close this workspace).
      if (dr.tries.length && !armed) { armed = true; discard.textContent = `Delete ${dr.tries.length} tr${dr.tries.length === 1 ? 'y' : 'ies'}? Click again`; setTimeout(() => { armed = false; discard.textContent = 'Discard all'; }, 4000); return; }
      try { d = await rpc('grunts.discard', { id, ...key }); byId = evById(); closeDialogOf(discard); } catch (err) { fail(err); }
    });
    paintTries();
    const voiceLine = V.ready ? [V.voicePack, V.engine === 'elevenlabs' ? 'ElevenLabs' : V.engine === 'voxcpm' ? 'VoxCPM' : 'OmniVoice', V.voiceName !== V.voicePack ? V.voiceName : null, V.model].filter(Boolean).join(' · ') : (V.voicePack ? `${V.voicePack} has no ${V.engine} voice yet.` : 'Pick a DBVO voice pack in Setup & export first.');
    await modal(`Text-to-speech · ${e.label}${dr.slotLabel ? ' · ' + dr.slotLabel : ''}`, [
      h('div.row', { style: { gap: '10px' } }, h('span.dim', 'Voice: ', h('b', voiceLine)), dr.words ? h('span.faint', `Words: “${dr.words}”`) : null),
      h('div.col', h('div.field-label', 'Subtitle'), sub),
      h('div.col', h('div.field-label', 'Spoken text · [tags] and formatting for the voice'), spoken, tagbar, tagNote),
      settings.childNodes.length ? settings : null,
      h('div.row', genBtn, status, h('div.spacer'), h('span.faint', 'Ctrl+Enter generates')),
      h('div.col', triesLbl, tries),
      h('div.row', discard, h('div.spacer'), saveBtn),
    ], [{ label: 'Close (keeps the tries)', value: null }], { wide: true, help: 'text-to-speech-workspace' });
    audioT.pause();
    if (committed) { toast(line ? 'Line replaced.' : 'Saved to the event.'); selId = e.id; }
    else { try { d = await rpc('grunts.get', { id }); byId = evById(); } catch { /* keep */ } }
    renderAll();
  }

  // ---------------------------------------------------------------- inspector
  function renderInspector() {
    clear(insp);
    const e = selId && byId.get(selId);
    if (!e) {
      insp.append(h('h3', 'Event'), h('div.dim', 'Click a block to see its lines. Drop audio files on a block to add them.'),
        h('div.help',
          h('p', P().target === 'pcht' ? 'PCHT plays a random file of the event. Its own plugin lists 5 per event (greetings 10); the export lists exactly your lines, so any number works.' : 'SPVS plays a random file of the event\'s folder, any names and any number. Text becomes the subtitle (.toml).'),
          h('p', 'Shouts have fixed parts: one file each, matched to the words.')));
      return;
    }
    const p = P();
    const st = h('span.badge.' + ({ full: 'b-valid', partial: 'b-toolong', broken: 'b-broken', skipped: 'b-skip', empty: 'b-skip' }[e.status]), STATUS_TIP[e.status]);
    const gamePath = p.target === 'pcht'
      ? (e.kind === 'pool' ? `Sound\\FX\\PC Head Tracking\\${p.pcht.multi ? (p.pcht.prefix || '<prefix>') + '\\' : ''}${e.folder}\\01.wav …` : 'Sound\\Voice\\PC Head Tracking - …esp\\<voice type>\\<fixed name>')
      : (e.kind === 'pool' ? `Sound\\SPVS_AdditionalVoice\\CustomVoiceType\\${p.spvs.slot}\\${e.folder}` : `Sound\\Voice\\SPVS_VanillaShout.esp\\SPVS_Replace${p.spvs.slot}\\…`);
    const drop = h('div.dropzone.gr-drop', h('b', 'Drop audio here'), h('div.faint', e.kind === 'slots' ? 'Fills the empty parts in order' : 'WAV, XWM, FUZ, MP3, OGG, FLAC…'));
    dropTarget(drop, (paths) => addFiles(e, paths));
    const canTts = !!p.voicePackId;
    insp.append(
      h('div.row', h('h3.grow', e.category), st),
      h('div.gr-insp-title', e.label, e.origin === 'custom' ? h('span.badge.b-player', { style: { marginLeft: '8px' } }, 'custom') : null),
      e.description ? h('div.dim', { style: { fontSize: '12.5px' } }, e.description) : null,
      e.notes ? h('div.banner.info', { style: { margin: 0 } }, e.notes) : null,
      h('div.kv2',
        h('div', 'Folder'), h('div.kv-v.mono', { title: gamePath }, gamePath),
        p.target === 'pcht' && e.kind === 'pool' ? [h('div', 'PCHT lists'), h('div.kv-v', { title: 'How many files PCHT\'s own plugin lists for this event' }, `${e.baseSlots} (${e.sndr})`)] : null,
        p.target === 'spvs' && e.key ? [h('div', 'Category'), h('div.kv-v.mono', { title: 'The Audio Util category SPVS asks for' }, e.key)] : null,
        p.target === 'spvs' && e.vanilla ? [h('div', 'Vanilla-based'), h('div.kv-v', { title: 'The vanilla-based method uses this many fixed file names; your lines are cycled into them' }, `${e.vanilla} fixed files`)] : null),
      e.kind === 'pool' ? drop : null,
      h('div.row.wrap', { style: { gap: '6px' } },
        h('button.btn.sm', { title: 'Choose audio files', onclick: () => pickFiles(e) }, 'Add files…'),
        h('button.btn.sm', { title: 'Record with your microphone (optionally through the Voice Changer)', disabled: !!app.job, onclick: () => record(e) }, '🎙 Record'),
        h('button.btn.sm', { title: canTts ? 'Open the text-to-speech workspace: write, tag and try until a take is right, then save it to the event' : 'Pick a DBVO voice pack in Setup & export first', disabled: !canTts, onclick: () => tts(e, e.kind === 'slots' ? (e.slots.find((x) => !e.lines.some((l) => l.slot === x.id)) || e.slots[0]).id : '') }, 'Text-to-speech'),
        e.lines.some((l) => l.exists && l.wav) ? (() => {
          const all = e.lines.filter((l) => l.exists && l.wav);
          const on = all.every((l) => l.reverb);
          return h('button.btn.sm', { title: on ? 'Take the effect off every line of this event (the originals come back)' : `${fxName(P().defaultEffect)} on every line of this event (Setup → Effects picks which effect)`, onclick: () => effectLines(on ? all : all.filter((l) => !l.reverb), on ? '' : P().defaultEffect) }, on ? `${fxShort(P().defaultEffect)} off for all` : `${fxShort(P().defaultEffect)} all lines`);
        })() : null,
        e.lines.some((l) => !l.text) ? h('button.btn.sm', { title: 'Let Whisper fill in the empty subtitles of this event', onclick: () => whisperAll(e.id) }, 'Whisper subtitles') : null,
        h('div.spacer'),
        h('button.act-btn' + (e.skipped ? '.on.bad' : ''), { title: e.skipped ? 'Skipped (left out of the export) - click to include' : 'Skip: leave this event out of the export', onclick: () => evAction(e, { skipped: !e.skipped }) }, icon('ban')),
        e.lines.length ? h('button.act-btn', { title: 'Remove every line of this event (your files stay on disk)', onclick: async () => { if (await confirmBox('Remove all lines?', `Removes the ${e.lines.length} line(s) of ${e.label} from the project. Your audio files stay where they are; the project's own recordings are deleted.`, 'Remove', true)) evAction(e, { clear: true }); } }, icon('x')) : null),
      (e.drafts || []).length ? h('div.col', { style: { gap: '4px' } }, e.drafts.map((dd) => h('button.btn.sm.gr-draft', { title: 'Open the text-to-speech workspace you left', onclick: () => tts(e, dd.slot, dd.lineId ? e.lines.find((l) => l.id === dd.lineId) || null : null) },
        `▶ ${dd.tries} unsaved tr${dd.tries === 1 ? 'y' : 'ies'}${dd.slot ? ' · ' + ((e.slots.find((x) => x.id === dd.slot) || {}).label || dd.slot) : ''}${dd.lineId ? ' · redoing a line' : ''}`))) : null,
      e.kind === 'slots' ? renderSlots(e) : renderPool(e));
  }
  async function evAction(e, o) { try { await call('grunts.event', { eventId: e.id, ...o }); renderAll(); } catch { renderAll(); } }

  function lineRow(e, l, i, slot) {
    const p = P();
    const showName = p.target === 'spvs' && e.kind === 'pool';
    const showText = (p.target === 'spvs' && e.kind === 'pool' && e.folder.indexOf('_Vanilla') !== 0) || l.origin === 'generated';
    const outName = e.kind === 'slots' ? '' : p.target === 'pcht' ? String(i + 1).padStart(e.lines.length >= 100 ? 3 : 2, '0') + '.wav' : (l.fileName || `${(e.key || e.folder.replace(/[^A-Za-z0-9]/g, '')).toLowerCase()}_${String(i + 1).padStart(e.lines.length >= 100 ? 3 : 2, '0')}`);
    const name = showName ? h('input.gr-fname', { type: 'text', value: l.fileName, placeholder: outName, title: 'File name to export as (no extension). Empty: numbered.' }) : null;
    if (name) name.addEventListener('change', () => line(e, l, { fileName: name.value.trim() }));
    const text = showText ? h('input.gr-text', { type: 'text', value: l.text, placeholder: 'Subtitle (optional)', title: 'Written to a .toml subtitle file next to the audio' }) : null;
    if (text) text.addEventListener('change', () => line(e, l, { text: text.value }));
    const meta = `${fmtSec(l.durationSeconds)}${l.channels > 1 ? ' · stereo' : ''}${l.sampleRate && l.sampleRate !== 44100 ? ' · ' + (l.sampleRate / 1000) + ' kHz' : ''}${l.origin === 'import' ? '' : ' · ' + l.origin}`;
    return h('div.gr-line' + (l.exists ? '' : '.missing'),
      h('div.row', { style: { gap: '6px', flexWrap: 'nowrap' } },
        h('button.icon-btn', { title: l.exists ? 'Play' : 'The file is missing: ' + l.path, disabled: !l.exists, onclick: () => playLine(l) }, '▶'),
        slot ? h('span.gr-slot', { title: slot.text }, slot.label) : h('span.gr-num.mono', { title: outName ? 'Exported as ' + outName : '' }, p.target === 'pcht' && e.kind === 'pool' ? outName.replace('.wav', '') : String(i + 1)),
        h('span.ellipsis.grow', { title: l.path + (slot ? `\n“${slot.text}”` : '') }, slot ? h('span.dim', `“${slot.text}” `) : null, l.file, l.exists ? null : h('span.badge.b-broken', { style: { marginLeft: '6px' } }, 'missing')),
        h('span.gr-dur.faint', { title: meta }, fmtSec(l.durationSeconds)),
        h('button.gr-rv' + (l.reverb ? '.on' : ''), {
          title: !l.exists ? 'The file is missing' : !l.wav ? 'Effects work on WAV files (replace this line with a WAV first)'
            : l.reverb ? `${fxName(l.effectId)} is on: the file itself has it (the original is kept in the project). Click to take it off; ⋯ → Effect… picks another.`
            : `${fxName(P().defaultEffect)}: ${fxDesc(P().defaultEffect)} Changes the file itself; the original is kept so it can be taken off. ⋯ → Effect… for another one; Setup → Effects to change them.`,
          disabled: !l.exists || !l.wav, onclick: () => reverb(e, l, !l.reverb),
        }, h('i'), fxShort(l.reverb ? l.effectId : P().defaultEffect)),
        e.kind === 'pool' ? h('button.icon-btn', { title: 'Move up', disabled: i === 0, onclick: () => line(e, l, { to: i - 1 }) }, '↑') : null,
        e.kind === 'pool' ? h('button.icon-btn', { title: 'Move down', disabled: i === e.lines.length - 1, onclick: () => line(e, l, { to: i + 1 }) }, '↓') : null,
        h('button.icon-btn', { title: 'More: replace the file, move to another event, open its folder' , onclick: (ev) => lineMenu(ev.currentTarget, e, l) }, '⋯'),
        h('button.icon-btn', { title: 'Remove this line (the file stays on disk unless it is the project\'s own recording)', onclick: () => line(e, l, { remove: true }) }, '×')),
      name || text || l.lufs != null || hasTrim(l) ? h('div.row', { style: { gap: '6px', paddingLeft: '26px', flexWrap: 'nowrap' } }, name, text, text ? null : h('div.spacer'),
        hasTrim(l) ? h('span.badge.b-skip', { title: `Trimmed ${l.trimStart.toFixed(2)} s – ${l.trimEnd ? l.trimEnd.toFixed(2) + ' s' : 'end'}${l.fadeInMs ? `, fade in ${l.fadeInMs} ms` : ''}${l.fadeOutMs ? `, fade out ${l.fadeOutMs} ms` : ''} (when listening and exporting; the file isn't changed)` }, '✂') : null,
        lufsTag(l)) : null);
  }
  // ---- effects (reverb, filters, voices), loudness, trim, variants
  const fxList = () => P().effects || [];
  const fxOf = (fid) => fxList().find((x) => x.id === fid) || (P().builtInEffects || []).find((x) => x.id === fid) || { id: fid, name: fid, description: '' };
  const fxName = (fid) => fxOf(fid).name;
  const fxDesc = (fid) => fxOf(fid).description || '';
  const fxShort = (fid) => (fid === 'inner' ? 'Reverb' : fxName(fid).split(' (')[0]);
  const hasTrim = (l) => l.trimStart > 0.0005 || l.trimEnd > 0.0005 || l.fadeInMs > 0.5 || l.fadeOutMs > 0.5;
  function lufsTag(l) {
    if (l.lufs == null) return null;
    if (l.lufs <= -70) return h('span.gr-lufs.bad', { title: 'Silent or too short to measure' }, 'silent');
    const mid = d.project.packLufs;
    const off = mid == null ? 0 : l.lufs - mid;
    const cls = Math.abs(off) > 4 ? '.bad' : l.peakDb != null && l.peakDb > -0.1 ? '.warn' : '';
    const w = Math.max(4, Math.min(100, ((l.lufs + 40) / 34) * 100));
    return h('span.gr-lufs' + cls, { title: `Loudness ${l.lufs.toFixed(1)} LUFS${l.peakDb != null ? ` · peak ${l.peakDb.toFixed(1)} dBTP` : ''}${mid != null ? ` · the pack's middle is ${mid.toFixed(1)} LUFS (${off >= 0 ? '+' : ''}${off.toFixed(1)} LU)` : ''}${Math.abs(off) > 4 ? (off > 0 ? ' · too loud compared with the pack' : ' · too quiet compared with the pack') : ''}` },
      h('i', h('b', { style: { width: w + '%' } })), `${l.lufs.toFixed(1)} LUFS`);
  }
  async function effectLines(lines, fid, force = false, soften = undefined) {
    try {
      const r = await withBusy(call('grunts.effect', { lineIds: lines.map((x) => x.id), effect: fid, force, ...(soften === undefined ? {} : { soften }) }), fid || soften ? 'Post-processing' : 'Taking the effect off', `${lines.length} line(s)…`);
      if (r.changed) { if (await confirmBox('Put the original back?', r.message, 'Put the original back', true)) return effectLines(lines, fid, true, soften); return; }
      if (r.problems && r.problems.length) toast(r.problems.slice(0, 3).join(' · '), true);
      audio.pause(); playing = null;
      renderAll();
    } catch { renderAll(); }
  }
  async function effectDialog(e, l) {
    // A click picks an effect and plays the line with it (the file isn't changed until Apply); Normal is the line as it is.
    let pick = l.reverb ? l.effectId : '';
    let soft = !!l.soften;
    const groups = {};
    for (const fx of fxList()) (groups[fx.group] = groups[fx.group] || []).push(fx);
    const tiles = [];
    const pa = new Audio();
    let playTicket = 0;
    const status = h('span.fx-play', '');
    const paint = () => tiles.forEach(([t, fid]) => t.classList.toggle('on', fid === pick));
    async function listen(fid, name) {
      const ticket = ++playTicket;
      status.textContent = `Making ${name}…`;
      try {
        const r = await rpc('grunts.fxPreview', { id, lineId: l.id, effect: fid, soften: soft });
        if (ticket !== playTicket) return;
        pa.src = r.url; await pa.play();
        status.textContent = `▶ ${name}`;
      } catch (err) { if (ticket === playTicket) status.textContent = ''; fail(err); }
    }
    const tile = (fid, name, desc, o = {}) => {
      const t = h('button.fx-tile' + (o.empty ? '.empty' : '') + (o.normal ? '.normal' : ''), {
        title: o.empty ? 'An empty slot: give it a filter or a reverb in Setup & export → Effects' : `${desc} Click to hear the line with it; double-click to apply.`, disabled: !!o.empty,
        onclick: () => { pick = fid; paint(); listen(fid, name); }, ondblclick: () => { pick = fid; document.querySelector('#modal .btn.primary')?.click(); },
      }, h('div.fx-name', name, o.badge || null), h('div.fx-desc', o.empty ? 'Empty · set it up in Setup & export → Effects' : desc));
      tiles.push([t, fid]);
      return t;
    };
    const cols = h('div.fx-cols', Object.entries(groups).map(([g, list], gi) => h('div.fx-col',
      h('div.fx-col-head', g),
      gi === 0 ? tile('', 'Normal', 'No effect: the line as it is.', { normal: true, badge: !l.reverb ? h('span.fx-badge', 'now') : null }) : null,
      list.map((fx) => tile(fx.id, fx.name, fx.description, {
        empty: fx.isEmpty,
        badge: l.reverb && fx.id === l.effectId ? h('span.fx-badge', 'now') : fx.id === P().defaultEffect ? h('span.fx-badge.dim', 'toggle') : null,
      })))));
    paint();
    const v = await modal(`Post-processing · ${l.file}`, [cols,
      h('div.row.fx-foot', status, h('div.spacer'),
        h('div.fx-soften', { title: 'Soften peaks after the effect: sharp peaks (hard consonants, clicks) turned down to 8 dB above the voice, so they don\'t distort at a high in-game gain. Works with Normal too.' },
          switchEl(soft, (x) => { soft = x; const t = tiles.find(([, fid]) => fid === pick); listen(pick, (t && t[0].querySelector('.fx-name').firstChild.textContent) || 'Normal'); }, 'Soften peaks'))),
      h('div.row.fx-foot', h('span.fx-note', 'Click an effect to hear the line with it. Apply changes the WAV file itself; the original is kept, so Normal (with Soften peaks off) puts it back.'))],
      [{ label: 'Cancel', value: false }, { label: 'Apply', primary: true, value: 'on' }], { wide: true, help: 'grunt-events' });
    pa.pause(); playTicket++;
    if (v !== 'on') return;
    const sameFx = pick ? l.reverb && pick === l.effectId : !l.reverb;
    if (!sameFx || soft !== !!l.soften) effectLines([l], pick, false, soft);
  }
  async function trimDialog(e, l) {
    const dur = l.durationSeconds || 0;
    if (!dur) { toast('The length of this line isn\'t known yet.', true); return; }
    const startIn = h('input', { type: 'range', min: 0, max: dur, step: 0.01, value: l.trimStart || 0 });
    const endIn = h('input', { type: 'range', min: 0, max: dur, step: 0.01, value: l.trimEnd || dur });
    const sl = h('span.mono'), el = h('span.mono');
    const fin = h('input', { type: 'number', min: 0, max: 2000, step: 5, value: l.fadeInMs || 0, style: { width: '80px' } });
    const fout = h('input', { type: 'number', min: 0, max: 2000, step: 5, value: l.fadeOutMs || 0, style: { width: '80px' } });
    const sync = () => { if (+startIn.value > +endIn.value - 0.05) startIn.value = Math.max(0, +endIn.value - 0.05); sl.textContent = (+startIn.value).toFixed(2) + ' s'; el.textContent = (+endIn.value).toFixed(2) + ' s'; };
    startIn.addEventListener('input', sync); endIn.addEventListener('input', sync); sync();
    const a = new Audio();
    const playSel = async () => {
      try {
        // the whole file, played from Start to End (the fades are heard after saving)
        a.src = await rpc('grunts.audio', { id, lineId: l.id, raw: true });
        a.currentTime = +startIn.value;
        const stop = +endIn.value;
        const tick = () => { if (a.currentTime >= stop) { a.pause(); a.removeEventListener('timeupdate', tick); } };
        a.addEventListener('timeupdate', tick);
        await a.play();
      } catch (err) { fail(err); }
    };
    const v = await modal(`Trim and fade · ${l.file}`, [
      h('div.rec-trim', h('div.row', h('span.faint', 'Start'), startIn, sl), h('div.row', h('span.faint', 'End'), endIn, el)),
      h('div.row', { style: { gap: '10px' } }, h('span.faint', 'Fade in'), fin, h('span.faint', 'ms · Fade out'), fout, h('span.faint', 'ms'), h('div.spacer'), h('button.btn.sm', { title: 'Play from Start to End', onclick: playSel }, '▶ Play selection')),
      h('div.faint', 'The file isn\'t changed: the cut and the fades are used when listening and exporting, and can be changed or reset any time.'),
    ], [{ label: 'Cancel', value: false }, { label: 'Reset', value: 'reset' }, { label: 'Save', primary: true, value: true }], { help: 'grunt-events' });
    a.pause();
    if (!v) return;
    const o = v === 'reset' ? { start: 0, end: 0, fadeIn: 0, fadeOut: 0 } : { start: +startIn.value, end: +endIn.value >= dur - 0.005 ? 0 : +endIn.value, fadeIn: +fin.value || 0, fadeOut: +fout.value || 0 };
    try { await call('grunts.trim', { lineId: l.id, ...o }); renderAll(); } catch { /* shown */ }
  }
  async function variantsDialog(e, l) {
    const count = h('input', { type: 'number', min: 1, max: 12, value: 4, style: { width: '70px' } });
    const pitch = h('input', { type: 'number', min: 0, max: 25, step: 0.5, value: 3, style: { width: '70px' } });
    const speed = h('input', { type: 'number', min: 0, max: 25, step: 0.5, value: 3, style: { width: '70px' } });
    const ok = await modal(`Variants of ${l.file}`, [
      h('div.dim', 'New lines made from this one, each a little higher or lower and faster or slower, so a pool doesn\'t repeat the same grunt.'),
      h('div.row', { style: { gap: '10px' } }, h('span.faint', 'How many'), count, h('span.faint', 'Pitch ±'), pitch, h('span.faint', '% · Speed ±'), speed, h('span.faint', '%')),
      h('div.faint', 'They are spread evenly over the range (never the original itself) and added right after this line. Its trim, fades and subtitle carry over.'),
    ], [{ label: 'Cancel', value: false }, { label: 'Make them', primary: true, value: true }], { help: 'grunt-events' });
    if (!ok) return;
    try { const r = await withBusy(call('grunts.variants', { eventId: e.id, lineId: l.id, count: +count.value || 4, pitch: +pitch.value || 0, speed: +speed.value || 0 }), 'Making variants', '…'); toast(`${r.added} variant(s) added.`); renderAll(); } catch { /* shown */ }
  }
  async function whisperAll(eventId) {
    try { const r = await call('grunts.whisperAll', { eventId: eventId || '' }); toast(r.queued ? 'Whisper subtitles: queued after the running task.' : `Whisper is filling in ${r.lines} empty subtitle(s)…`); } catch { /* shown */ }
  }
  async function measure(all) {
    try { const r = await call('grunts.measure', { all: !!all }); if (r.queued) toast('Loudness measuring: queued after the running task.'); } catch { /* shown */ }
  }
  async function reverb(e, l, on, force = false) {
    try {
      const r = await call('grunts.reverb', { lineId: l.id, on, force });
      if (r.changed) {
        if (await confirmBox('Take the reverb off?', r.message, 'Put the original back', true)) return reverb(e, l, on, true);
        return;
      }
      if (playing === l.id) { audio.pause(); playing = null; }
      renderAll();
    } catch { renderAll(); }
  }
  // Subtitle for any line (not only text-to-speech ones), with Whisper's suggestion from the audio.
  async function subtitleDialog(e, l) {
    const inp = h('input', { type: 'text', value: l.text || '', placeholder: P().target === 'spvs' ? 'What the subtitle shows' : 'The words (PCHT shows no subtitles; .fuz files get lip sync from it)', style: { width: '100%' } });
    const note = h('span.faint', '');
    const sug = h('button.btn.sm', { title: 'Let Whisper (the WAV to LIP Tool\'s settings) listen to the line and suggest the words' }, 'Suggest with Whisper');
    sug.addEventListener('click', async () => {
      sug.disabled = true; note.textContent = 'Whisper is listening…';
      try { const r = await rpc('grunts.transcribe', { id, lineId: l.id }); if (r.text) { inp.value = r.text; inp.focus(); inp.select(); note.textContent = 'Suggested: check it before saving.'; } else note.textContent = 'Whisper heard no words.'; }
      catch (err) { note.textContent = ''; fail(err); } finally { sug.disabled = false; }
    });
    inp.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') { ev.preventDefault(); document.querySelector('#modal .btn.primary')?.click(); } });
    const ok = await modal(`Subtitle · ${l.file}`, [
      h('div.dim', `${e.category} · ${e.label}`),
      inp, h('div.row', sug, note, h('div.spacer'), h('button.btn.sm.ghost', { title: 'Play the line', onclick: () => playLine(l) }, '▶ Play')),
    ], [{ label: 'Cancel', value: false }, { label: 'Save', primary: true, value: true }], { help: 'grunt-events' });
    if (ok) line(e, l, { text: inp.value.trim() });
  }
  function lineMenu(anchor, e, l) {
    popupMenu(anchor, [
      { label: 'Apply subtitles…', sub: l.text ? `“${l.text}”` : 'Write the subtitle, or let Whisper suggest it from the audio', onclick: () => subtitleDialog(e, l) },
      l.exists && l.wav ? { label: 'Post-processing…', sub: [l.reverb ? `On: ${fxName(l.effectId)}` : null, l.soften ? 'Soften peaks' : null].filter(Boolean).join(' · ') || 'Effects (reverb, cave, radio, telepathy…) and Soften peaks', onclick: () => effectDialog(e, l) }
        : l.exists ? { label: 'Post-processing…', sub: `Effects and Soften peaks change the WAV file itself, and this line is a .${(l.file.split('.').pop() || '').toLowerCase()}. Replace the file with a WAV first.`, disabled: true, onclick: () => {} } : null,
      l.exists ? { label: 'Trim and fade…', sub: hasTrim(l) ? 'Trimmed (click to change or reset)' : 'Cut the start or end, fade in or out (the file isn\'t changed)', onclick: () => trimDialog(e, l) } : null,
      l.exists && e.kind === 'pool' ? { label: 'Make variants…', sub: 'Pitch and speed variants of this line to fill the pool', onclick: () => variantsDialog(e, l) } : null,
      { label: 'Replace the file…', sub: 'Keep the line (its place, name and text), use another audio file', onclick: async () => { const f = await rpc('pick.file', { title: 'Replacement audio', filter: 'Audio|*.wav;*.xwm;*.fuz;*.mp3;*.ogg;*.flac;*.m4a;*.opus' }); if (f) line(e, l, { replace: f }); } },
      P().voicePackId ? { label: 'New tries with text-to-speech…', sub: 'Make takes for this line in the workspace; saving one replaces this line\'s audio', onclick: () => tts(e, l.slot || '', l) } : null,
      e.kind === 'pool' ? { label: 'Move to another event…', onclick: () => moveDialog(e, l) } : null,
      l.exists ? { label: 'Show in its folder', sub: l.path, onclick: () => rpc('open.path', { path: l.path.replace(/[\\/][^\\/]*$/, '') }).catch(fail) } : null,
    ].filter(Boolean));
  }
  async function moveDialog(e, l) {
    const sel = h('select', { style: { width: '100%' } }, d.events.filter((x) => x.kind === 'pool' && x.id !== e.id).map((x) => h('option', { value: x.id }, `${x.category} · ${x.label}`)));
    const ok = await modal('Move the line', [h('div', l.file), sel], [{ label: 'Cancel', value: false }, { label: 'Move', primary: true, value: true }]);
    if (ok) line(e, l, { moveTo: sel.value });
  }
  function renderPool(e) {
    if (!e.lines.length) return h('div.faint', { style: { padding: '6px 0' } }, P().target === 'pcht' ? 'No lines: the event stays silent (as with PCHT\'s own empty folders).' : 'No lines: SPVS skips this event.');
    return h('div', h('div.field-label', `Lines (${e.lines.length})`), h('div.gr-lines', e.lines.map((l, i) => lineRow(e, l, i))));
  }
  function renderSlots(e) {
    return h('div', h('div.field-label', `Parts (${e.lines.length}/${e.slots.length})`),
      h('div.gr-lines', e.slots.map((s) => {
        const l = e.lines.find((x) => x.slot === s.id);
        if (l) return lineRow(e, l, 0, s);
        const row = h('div.gr-line.empty-slot', h('div.row', { style: { gap: '6px', flexWrap: 'nowrap' } },
          h('span.gr-slot', { title: s.text }, s.label), h('span.grow.dim', `“${s.text}”`, s.patch ? h('span.faint', { title: 'Dawnguard / Dragonborn shout (PC Head Tracking - Patch.esp)' }, ' · DLC') : null),
          h('button.btn.sm', { title: 'Choose a file for this part', onclick: () => pickFiles(e, s.id) }, 'Add…'),
          h('button.icon-btn', { title: 'Record this part', onclick: () => record(e, s.id) }, '🎙'),
          P().voicePackId ? h('button.icon-btn', { title: 'Text-to-speech for this part', onclick: () => tts(e, s.id) }, 'T') : null));
        dropTarget(row, (paths) => addFiles(e, paths.slice(0, 1), s.id));
        return row;
      })));
  }

  function renderStatus() {
    const c = counts();
    const lines = d.events.reduce((n, e) => n + e.lines.length, 0);
    const missing = d.events.reduce((n, e) => n + e.missing, 0);
    clear(statusbar).append(
      h('span.stat', h('b', String(c.has)), ` of ${c.all} events have audio · `, h('b', String(lines)), ' lines', missing ? h('span', { style: { color: 'var(--bad)' } }, ` · ${missing} file(s) missing`) : null,
        d.catalog.source === 'installed' ? h('span.faint', { title: d.catalog.sourceInfo }, ` · events read from the installed ${TARGET[P().target]}`) : h('span.faint', ' · built-in event list')),
      h('div.spacer'),
      d.orphans.length ? h('button.btn.sm.ghost', { title: 'Lines of events the current event list doesn\'t have', onclick: orphansDialog }, `${d.orphans.length} unknown event(s)`) : null,
      lines ? h('button.btn.sm.ghost', { title: 'Measure every line\'s loudness (LUFS) and peak; lines far from the pack\'s middle are flagged in Check', onclick: () => measure(true) }, 'Measure loudness') : null,
      lines ? h('button.btn.sm.ghost', { title: 'Let Whisper fill in every empty subtitle of the project (one model load for all)', onclick: () => whisperAll('') }, 'Whisper all subtitles') : null,
      h('button.btn.sm', { title: 'List every problem before exporting', onclick: () => check(false) }, 'Check'));
  }
  async function orphansDialog() {
    const v = await modal('Lines of unknown events', [
      h('div', 'These events have lines in the project but aren\'t in the event list any more (a custom event was deleted, or the installed mod no longer has them). They are not exported.'),
      h('div.box.mono', { style: { maxHeight: '200px', overflow: 'auto' } }, d.orphans.map((o) => `${o.id}  (${o.lines} lines)`).join('\n')),
    ], [{ label: 'Keep them', value: false }, { label: 'Forget them', danger: true, value: true }]);
    if (v) { try { await call('grunts.event', { eventIds: d.orphans.map((o) => o.id), forget: true }); renderAll(); } catch { /* shown */ } }
  }

  async function openSubtitles() {
    try { const r = await call('grunts.subtitles', { action: 'open' }); toast(`Opened ${r.path}. Save it and come back here: the changes are read in.`); } catch { /* shown */ }
  }
  // Coming back from the editor: read the subtitles .toml if it was saved.
  async function syncSubtitles() {
    if (!document.body.contains(view)) { window.removeEventListener('focus', syncSubtitles); return; }
    try { const r = await call('grunts.subtitles', { action: 'sync' }, { quiet: true }); if (r.changed) { toast(`${r.changed} subtitle(s) read from the .toml.`); renderAll(); } } catch { /* ignore */ }
  }
  window.addEventListener('focus', syncSubtitles);

  // ---------------------------------------------------------------- import
  async function importPack() {
    const f = await rpc('pick.folder', { title: 'The voice pack\'s mod folder (the one holding Sound)' });
    if (!f) return;
    try {
      const r = await withBusy(call('grunts.importPack', { path: f }), 'Importing', 'Matching the pack\'s files to the events…');
      toast(r.files ? `${r.files} file(s) added to ${r.events} event(s).${r.notes.length ? ' ' + r.notes.join(' ') : ''}` : (r.notes.join(' ') || 'Nothing new found.'), !r.files);
      renderAll();
    } catch { /* shown */ }
  }
  async function importProject() {
    const sel = h('select', { style: { width: '100%' } }, d.others.map((o) => h('option', { value: o.id }, `${o.name} · ${TARGET[o.target]} · ${o.lines} lines`)));
    const ok = await modal('Copy from another project', [sel, h('div.faint', 'Each of its lines is added to the matching event here (between PCHT and SPVS, with SPVS\'s own event mapping). Lines already here are skipped; its recordings are copied.')],
      [{ label: 'Cancel', value: false }, { label: 'Copy', primary: true, value: true }]);
    if (!ok) return;
    try { const r = await call('grunts.importProject', { fromId: sel.value }); toast(r.added ? `${r.added} line(s) copied.` : 'Nothing to copy.'); renderAll(); } catch { /* shown */ }
  }
  async function relink() {
    const f = await rpc('pick.folder', { title: 'A folder holding the moved audio files (searched with its subfolders)' });
    if (!f) return;
    try { const r = await call('grunts.relink', { path: f }); toast(r.found ? `${r.found} missing file(s) found again.` : 'None of the missing files are in that folder.', !r.found); renderAll(); } catch { /* shown */ }
  }

  // ---------------------------------------------------------------- check and export
  async function pickOutput() {
    const f = await rpc('pick.folder', { title: 'Output folder for the voice pack (e.g. a new MO2 mod folder)' });
    if (f) await saveProject({ outputFolder: f });
  }
  function issueList(c) {
    const sev = { error: ['b-broken', 'Error'], warning: ['b-toolong', 'Warning'], info: ['b-skip', 'Note'] };
    const row = (i) => h('div.gr-issue' + (i.eventId ? '.link' : ''), { title: i.eventId ? 'Show this event' : '', onclick: i.eventId ? (e) => { closeDialogOf(e.currentTarget); tab = 'events'; selId = i.eventId; renderAll(); } : null },
      h('span.badge.' + sev[i.severity][0], sev[i.severity][1]), h('span', i.text));
    const main = c.issues.filter((i) => i.severity !== 'info').slice(0, 200);
    const notes = c.issues.filter((i) => i.severity === 'info');
    return h('div.gr-issues', main.map(row),
      notes.length ? h('details', { open: !main.length && notes.length < 12 }, h('summary.faint', `${notes.length} note(s): events without audio and other details`), notes.slice(0, 300).map(row)) : null,
      !main.length && !notes.length ? h('div.dim', 'No problems found.') : null);
  }
  async function check(thenExport) {
    let c;
    try { c = await withBusy(call('grunts.check', {}), 'Checking', 'Planning the export and building the plugin in memory…'); } catch { return; }
    const infos = c.issues.filter((i) => i.severity === 'info').length;
    const v = await modal(c.errors ? 'Fix these before exporting' : 'Ready to export', [
      h('div.stat-row', h('span', h('b', String(c.audio)), ' audio files'), h('span', h('b', String(c.files - c.audio)), ' other files'), h('span', h('b', String(c.errors)), ' errors'), h('span', h('b', String(c.warnings)), ' warnings'), infos ? h('span', h('b', String(infos)), ' notes') : null),
      c.plugins.length ? h('div.box', c.plugins.map((pl) => h('div', h('b.mono', pl.fileName), ` · ${pl.overrides ? pl.overrides + ' sound descriptor override(s)' : pl.newRecords + ' new records'} · masters ${pl.masters.join(', ')} · ESL-flagged · checked`))) : null,
      c.notes.length ? h('ul.gr-notes', c.notes.map((n) => h('li', n))) : null,
      issueList(c),
    ], c.errors ? [{ label: 'Close', primary: true, value: false }] : [{ label: 'Close', value: false }, { label: 'Export now', primary: true, value: true }], { wide: true, help: 'checking-and-exporting-packs' });
    if (v) doExport();
  }
  async function doExport(overwrite = false) {
    if (!P().outputFolder) { toast('Choose the output folder first (Export ▾ → Output folder…).', true); return; }
    try {
      const r = await call('grunts.export', { overwrite });
      if (r.started) return; // the bar at the top shows progress
      if (r.confirm) {
        const c = r.check;
        const ok = await modal('Replace files in the output folder?', [
          h('div', `${c.overwriteCount} file(s) in ${c.output} weren't written by this project and would be replaced:`),
          pathList(c.overwrite, { more: c.overwriteCount - c.overwrite.length }),
          h('div.faint', 'Pick an empty folder (a new mod folder) to keep them, or replace them.'),
        ], [{ label: 'Cancel', value: false }, { label: 'Replace them', danger: true, value: true }]);
        if (ok) doExport(true);
        return;
      }
      check(false); // errors: show them
    } catch { /* shown */ }
  }
  async function showReport(text) {
    const v = await modal('Export report', [h('pre.box.gr-report', text)], [{ label: 'Open folder', value: 'open' }, { label: 'OK', primary: true, value: true }], { wide: true, help: 'checking-and-exporting-packs' });
    if (v === 'open') rpc('grunts.openFolder', { id, which: 'output' }).catch(fail);
  }

  // ---------------------------------------------------------------- custom events
  async function customEventsDialog() {
    const target = P().target;
    let list = (await rpc('grunts.custom', { target, action: 'list' }).catch(fail)) || { events: [] };
    const box = h('div.gr-custom-list');
    const paint = () => {
      clear(box);
      if (!list.events.length) box.append(h('div.dim', { style: { padding: '8px 0' } }, 'No custom events yet.'));
      for (const e of list.events) box.append(h('div.gr-custom-row',
        h('div.grow', h('b', e.label), h('span.faint', ` · ${e.category}`), h('div.mono.faint', target === 'spvs' ? `${e.key} → ${e.folder}` : `${e.sndr} → ${e.folder}`)),
        h('button.btn.sm', { title: 'Edit this event', onclick: async () => { const r = await editCustom(e); if (r) { list = r; paint(); } } }, 'Edit'),
        h('button.icon-btn', { title: 'Delete this event (projects keep its lines; they show as an unknown event)', onclick: async () => { list = await rpc('grunts.custom', { target, action: 'delete', eventId: e.id }).catch(fail) || list; paint(); } }, '×')));
    };
    paint();
    const add = h('button.btn.sm.primary', { title: 'Define a new event', onclick: async () => { const r = await editCustom(null); if (r) { list = r; paint(); } } }, '+ Add event');
    const imp = h('button.btn.sm', { title: target === 'spvs' ? 'Import DBVO Studio event definitions (.json) or an SPVS Audio Util config (.toml): its categories become events' : 'Import DBVO Studio event definitions (.json)', onclick: async () => {
      const f = await rpc('pick.file', { title: 'Event definitions', filter: target === 'spvs' ? 'Event definitions|*.json;*.toml' : 'Event definitions|*.json' });
      if (!f) return;
      try {
        const r = await rpc('grunts.custom', { target, action: 'import', path: f });
        list = r.list; paint();
        await modal('Imported', [h('div', `${r.added} event(s) added.`), r.problems.length ? h('ul', r.problems.slice(0, 30).map((x) => h('li', x))) : null]);
      } catch (e) { fail(e); }
    } }, 'Import…');
    const exp = h('button.btn.sm', { title: 'Save your custom events to share them or use them on another PC', onclick: async () => {
      const f = await rpc('pick.folder', { title: 'Folder to save the event definitions in' });
      if (!f) return;
      try { const r = await rpc('grunts.custom', { target, action: 'export', path: f.replace(/[\\/]$/, '') + '\\DBVO Studio ' + TARGET[target] + ' events.json' }); toast(`${r.written} event(s) saved to ${r.path}.`); } catch (e) { fail(e); }
    } }, 'Export…');
    await modal(`Custom ${TARGET[target]} events`, [
      h('div.faint', target === 'spvs'
        ? 'Add an event SPVS plays that this list doesn\'t have yet (e.g. from a newer SPVS version): its Audio Util category key and folder. Setting the SPVS folder reads its own event list, which covers most new events by itself.'
        : 'Only for sound descriptors (SNDR) another plugin adds to PCHT\'s voice events. PCHT\'s own events are all listed already.'),
      box,
      h('div.row', add, imp, exp, h('div.spacer'), h('span.faint.mono', { title: list.path || '' }, 'Saved in the library')),
    ], [{ label: 'Close', primary: true, value: true }], { help: 'custom-events' });
    d = await rpc('grunts.get', { id }); byId = evById(); renderAll();
  }
  async function editCustom(e) {
    const target = P().target;
    const v = e || { label: '', category: 'Custom', key: '', folder: '', sndr: '', description: '', spoken: target === 'spvs' };
    const f = (val, ph, title) => h('input', { type: 'text', value: val || '', placeholder: ph, title, style: { width: '100%' } });
    const label = f(v.label, 'e.g. Swimming', 'The name shown on the block');
    const cat = f(v.category, 'e.g. Custom', 'The category it is listed under');
    const key = f(v.key, 'e.g. SwimStart', 'The Audio Util category key SPVS\'s scripts ask for (letters, digits, _)');
    const folder = f(v.folder, target === 'spvs' ? 'e.g. Swim\\Start' : 'e.g. Combat\\Taunt', target === 'spvs' ? 'Folder under the voice slot, as in SPVS\'s config' : 'Folder under Sound\\FX\\PC Head Tracking');
    const sndr = f(v.sndr, 'e.g. _ComTaunt', 'Editor ID of the sound descriptor to override');
    const desc = f(v.description, 'When it plays (optional)', 'Shown in the event panel');
    let spoken = !!v.spoken;
    if (target === 'spvs') key.addEventListener('input', () => { if (!folder.value.trim() && !e) folder.placeholder = key.value.replace(/([a-z])([A-Z])/, '$1\\$2'); });
    const grid = h('div.gr-form',
      h('div.col', h('div.field-label', 'Name'), label), h('div.col', h('div.field-label', 'Category'), cat),
      target === 'spvs' ? h('div.col', h('div.field-label', 'Audio Util category key'), key) : h('div.col', h('div.field-label', 'Sound descriptor (SNDR)'), sndr),
      h('div.col', h('div.field-label', 'Folder'), folder),
      h('div.col.span2', h('div.field-label', 'Description'), desc),
      target === 'spvs' ? h('div.span2', switchEl(spoken, (x) => { spoken = x; }, 'Spoken lines (subtitles, text-to-speech)')) : null);
    const ok = await modal(e ? 'Edit custom event' : 'New custom event', [grid,
      h('div.faint', target === 'spvs' ? 'SPVS only plays an event its scripts ask for. A key the installed SPVS doesn\'t know is exported, with a warning, and plays once an SPVS version with that event is installed.' : 'The export overrides this descriptor like PCHT\'s own ones.')],
    [{ label: 'Cancel', value: false }, { label: 'Save', primary: true, value: true }], { help: 'custom-events' });
    if (!ok) return null;
    try {
      return await rpc('grunts.custom', { target, action: 'save', oldId: e ? e.id : '', event: { label: label.value.trim(), category: cat.value.trim(), key: key.value.trim(), folder: folder.value.trim() || folder.placeholder.replace(/^e\.g\. .*$/, ''), sndr: sndr.value.trim(), description: desc.value.trim(), spoken, kind: 'pool' } });
    } catch (err) { fail(err); return editCustom({ ...v, id: e ? e.id : undefined, label: label.value, category: cat.value, key: key.value, folder: folder.value, sndr: sndr.value, description: desc.value, spoken }); }
  }

  // ---------------------------------------------------------------- setup tab
  function renderSetup() {
    const p = P();
    const wrap = h('div.view-body.reading');
    const sec = (title, help, extra, ...rows) => h('div.section', h('div.section-head', h('h2', title), help ? helpButton(help) : null, h('div.spacer'), extra), h('div.section-body', rows));
    const srow = (label, help, control) => h('div.srow', h('div.lbl', h('span', label), help ? h('small', help) : null), h('div', control));
    const text = (val, onSet, o = {}) => { const i = h('input', { type: 'text', value: val || '', placeholder: o.placeholder || '', style: { width: o.width || '100%' }, title: o.title || '' }); i.addEventListener('change', () => onSet(i.value.trim())); return i; };
    const num = (val, onSet, o = {}) => { const i = h('input', { type: 'number', value: val, min: o.min, max: o.max, step: o.step || 'any', style: { width: o.width || '90px' }, title: o.title || '' }); i.addEventListener('change', () => onSet(Number(i.value))); return i; };
    const banner = pathsBanner(d.paths, p.target, async () => { d = await rpc('grunts.get', { id }); byId = evById(); renderAll(); });
    const vsel = h('select', { style: { width: '320px' } }, h('option', { value: '' }, '— none —'), d.voices.map((v) => h('option', { value: v.id, selected: v.id === p.voicePackId }, v.name)));
    vsel.addEventListener('change', () => saveProject({ voicePackId: vsel.value }));

    wrap.append(banner || '',
      sec('Project', 'grunts-setup', null,
        srow('Name', null, text(p.name, (v) => saveProject({ name: v }))),
        p.target === 'pcht' ? srow('Voice', 'Which BYO voice type the shouts go to', segmented([['female', 'Female'], ['male', 'Male'], ['both', 'Both']], p.gender, (v) => saveProject({ gender: v }))) : null,
        srow('DBVO voice pack', 'Its voice converts recordings (Voice Changer) and reads text-to-speech lines', vsel)),
      sec('Installed mod folders', 'player-grunts-tool', h('button.btn.sm', { title: 'Change the folders', onclick: () => pathsDialog(d.paths, async () => { d = await rpc('grunts.get', { id }); byId = evById(); renderAll(); }) }, 'Change…'),
        srow('PC Head Tracking', d.paths.pchtState || 'Not set', h('div.row', h('span.mono.ellipsis.grow', d.paths.pchtFolder || '— not set —'), h('span.badge.' + (d.paths.pchtOk ? 'b-valid' : 'b-broken'), d.paths.pchtOk ? (d.paths.pchtMulti ? 'MPCHT ✓' : '✓') : '✕'))),
        srow('Smart Player Voice System', d.paths.spvsState || 'Not set', h('div.row', h('span.mono.ellipsis.grow', d.paths.spvsFolder || '— not set —'), h('span.badge.' + (d.paths.spvsOk ? 'b-valid' : 'b-broken'), d.paths.spvsOk ? '✓' : '✕'))),
        srow('Event list', d.catalog.notes.join(' ') || null, h('span', d.catalog.source === 'installed' ? 'Read from the installed mod' : 'Built in (the mod folder isn\'t set)', d.catalog.customs ? h('span.faint', ` · ${d.catalog.customs} custom`) : null))));

    if (p.target === 'pcht') {
      const m = p.pcht;
      wrap.append(sec('PC Head Tracking', 'pcht-packs', null,
        srow('Multi Voice Packs (MPCHT)', 'A voice set players pick per character. Needs "Multi Voice Packs support for PC Head Tracking" and FormList Manipulator.', switchEl(m.multi, (v) => saveProject({ pcht: { multi: v } }), m.multi ? 'On: PCHTV_<prefix>.esp voice set' : 'Off: the default PCHT voice')),
        m.multi ? srow('Prefix', 'Letters and digits: the plugin, folder and voice type names', text(m.prefix, (v) => saveProject({ pcht: { prefix: v } }), { width: '220px', placeholder: 'e.g. Vampira' })) : null,
        m.multi ? srow('Name in the MCM', null, text(m.fullName, (v) => saveProject({ pcht: { fullName: v } }), { width: '320px' })) : null,
        m.multi ? srow('FormList Manipulator .ini', 'Registers the voice set in the MCM lists (needed unless you add the lines by hand)', switchEl(m.writeFlmIni, (v) => saveProject({ pcht: { writeFlmIni: v } }), `PCHTV_${m.prefix || '<prefix>'}_FLM.ini`)) : null,
        !m.multi ? srow('Plugin name', 'Only written when an event has more or fewer lines than PCHT\'s own', text(m.pluginName, (v) => saveProject({ pcht: { pluginName: v } }), { width: '320px', placeholder: p.defaultPlugin })) : null,
        srow('Dawnguard and Dragonborn shouts', 'PC Head Tracking - Patch.esp\'s shouts (Drain Vitality, Soul Tear, Durnehviir, Dragon Aspect, Cyclone, Bend Will, Battle Fury)', switchEl(m.patchShouts, (v) => saveProject({ pcht: { patchShouts: v } }), 'Write them'))));
    } else {
      const s = p.spvs;
      const slotSel = h('select', SLOTS.map((x) => h('option', { value: x, selected: x === s.slot }, x)));
      slotSel.addEventListener('change', () => saveProject({ spvs: { slot: slotSel.value } }));
      const langSel = h('select', SUB_LANGS.map(([v, n]) => h('option', { value: v, selected: v === s.subtitleLanguage }, n)));
      langSel.addEventListener('change', () => saveProject({ spvs: { subtitleLanguage: langSel.value } }));
      wrap.append(sec('Smart Player Voice System', 'spvs-packs', null,
        srow('Voice slot', 'Pick it in the SPVS MCM as Custom 1–5 (female or male)', slotSel),
        srow('Script-based method', 'Every event\'s folder: any file names and counts, lip sync with WAV and FUZ, subtitles', switchEl(s.scriptBased, (v) => saveProject({ spvs: { scriptBased: v } }), 'Write the folders')),
        srow('Vanilla-based method', 'Attacks, blocks, hits, breathing and death into the game\'s fixed file names (no delay, fixed count). SPVS recommends shipping both.', switchEl(s.vanillaBased, (v) => saveProject({ spvs: { vanillaBased: v } }), 'Write the fixed names')),
        srow('Subtitles', 'A .toml next to each line that has text', h('div.row', switchEl(s.subtitles, (v) => saveProject({ spvs: { subtitles: v } }), ''), langSel))));
    }
    const a = p.audio;
    wrap.append(sec('Audio', 'loudness-and-format', null,
      srow('Format', a.format === 'wav' ? 'WAV works everywhere (PCHT, SPVS lip sync without .lip files)' : a.format === 'xwm' ? 'Smaller; needs xWMAEncode. SPVS plays XWM without lip sync.' : 'FUZ for dialogue files (shouts, vanilla-based); pool folders get XWM. Needs xWMAEncode.',
        segmented([['wav', 'WAV'], ['xwm', 'XWM'], ['fuz', 'FUZ']], a.format, (v) => saveProject({ audio: { format: v } }))),
      srow('Soften peaks', 'On every file at export, before the loudness: sharp peaks (hard consonants, clicks) turned down so they don\'t distort at a high in-game gain. Your files aren\'t changed. Standard limits peaks to 8 dB above the voice, Strong to 6 dB. Lines with Soften peaks on in their effect are left as they are.',
        segmented([['', 'Off'], ['standard', 'Standard'], ['strong', 'Strong']], a.softenPeaks || '', (v) => saveProject({ audio: { softenPeaks: v } }))),
      srow('Loudness', 'Every file brought to the same loudness, without peaks above the ceiling', h('div.row', { style: { gap: '10px' } },
        switchEl(a.normalize, (v) => saveProject({ audio: { normalize: v } }), ''),
        h('span.faint', 'Target'), num(a.targetLufs, (v) => saveProject({ audio: { targetLufs: v } }), { min: -40, max: -6, step: 0.5, title: 'Integrated loudness (LUFS). Vanilla grunts sit around -18 to -16.' }), h('span.faint', 'LUFS'),
        h('span.faint', 'Peak ceiling'), num(a.peakDb, (v) => saveProject({ audio: { peakDb: v } }), { min: -12, max: 0, step: 0.5 }), h('span.faint', 'dBTP'))),
      srow('Extra gain', 'After normalizing, e.g. to sit the grunts under dialogue', h('div.row', num(a.gainDb, (v) => saveProject({ audio: { gainDb: v } }), { min: -24, max: 12, step: 0.5 }), h('span.faint', 'dB'))),
      srow('Sample rate', 'Mono, 16-bit', segmented([[22050, '22.05 kHz'], [44100, '44.1 kHz'], [48000, '48 kHz']], a.sampleRate, (v) => saveProject({ audio: { sampleRate: Number(v) } })))));
    wrap.append(renderEffects());
    wrap.append(sec('Output', 'checking-and-exporting-packs', null,
      srow('Output folder', 'A new mod folder is best. Exports only replace their own earlier files; others are asked about first.', h('div.row', h('span.mono.ellipsis.grow', p.outputFolder || '— not set —'), h('button.btn.sm', { title: 'Choose the output folder', onclick: pickOutput }, 'Choose…'),
        p.outputFolder ? h('button.btn.sm', { title: 'Open it', onclick: () => rpc('grunts.openFolder', { id, which: 'output' }).catch(fail) }, 'Open') : null)),
      srow('Last export', p.lastExportUtc ? new Date(p.lastExportUtc).toLocaleString() + ` · ${p.exported} files` : 'Not exported yet', h('div.row', h('button.btn.sm', { title: 'List problems and what would be written', onclick: () => check(false) }, 'Check'), p.lastReport ? h('button.btn.sm', { title: 'Show the report', onclick: () => showReport(p.lastReport) }, 'Report') : null, h('div.spacer'), h('button.btn.accent', { title: 'Write the voice pack', onclick: () => doExport() }, icon('export', { size: 15 }), ' Export')))),
      sec('Custom events', 'custom-events', h('button.btn.sm', { title: 'Add, edit, import or export your own event definitions', onclick: customEventsDialog }, 'Manage…'),
        h('div.faint', { style: { padding: '10px 0' } }, `${d.catalog.customs} custom event(s) for ${TARGET[p.target]}. They are kept in the library, shared by every ${TARGET[p.target]} project, and can be exported to share.`)));
    return wrap;
  }

  // Setup → Effects: the project's presets (the line toggle uses the default one).
  function renderEffects() {
    const list = fxList().map((x) => JSON.parse(JSON.stringify(x)));
    const builtIns = P().builtInEffects || [];
    const saveFx = debounce(() => saveProject({ effects: list }), 500);
    const num = (obj, k, o) => { const i = h('input', { type: 'number', value: obj[k], min: o.min, max: o.max, step: o.step || 1, style: { width: '68px' }, title: o.title || '' }); i.addEventListener('change', () => { obj[k] = Number(i.value); saveFx(); }); return h('span.gr-fx-num', h('span.faint', o.label), i); };
    const rows = list.map((fx) => {
      const isDefault = fx.id === P().defaultEffect;
      const builtIn = builtIns.find((b) => b.id === fx.id);
      const det = h('details.gr-fx-row',
        h('summary', h('b', fx.name), h('span.faint', ` · ${fx.group}`), isDefault ? h('span.badge.b-valid', { style: { marginLeft: '8px' } }, 'toggle') : null, h('span.faint.ellipsis', { style: { marginLeft: '10px' } }, fx.description)),
        h('div.col', { style: { gap: '8px', padding: '8px 0 4px 14px' } },
          h('div.row', { style: { gap: '8px' } },
            (() => { const n = h('input', { type: 'text', value: fx.name, style: { width: '220px' } }); n.addEventListener('change', () => { fx.name = n.value.trim() || fx.name; saveFx(); }); return n; })(),
            (() => { const t = h('input', { type: 'text', value: fx.description, placeholder: 'What it sounds like', style: { flex: '1' } }); t.addEventListener('change', () => { fx.description = t.value; saveFx(); }); return t; })()),
          fx.reverb ? h('div.row.wrap', { style: { gap: '10px' } },
            num(fx.reverb, 'roomSize', { label: 'Room', min: 0, max: 100 }), num(fx.reverb, 'reverberance', { label: 'Reverberance', min: 0, max: 100 }),
            num(fx.reverb, 'hfDamping', { label: 'Damping', min: 0, max: 100 }), num(fx.reverb, 'preDelayMs', { label: 'Delay ms', min: 0, max: 200 }),
            num(fx.reverb, 'toneLow', { label: 'Tone low', min: 0, max: 100 }), num(fx.reverb, 'toneHigh', { label: 'Tone high', min: 0, max: 100 }),
            num(fx.reverb, 'wetGainDb', { label: 'Wet dB', min: -20, max: 10, step: 0.5 }), num(fx.reverb, 'dryGainDb', { label: 'Dry dB', min: -20, max: 10, step: 0.5 }),
            num(fx.reverb, 'tailSeconds', { label: 'Tail s', min: 0, max: 5, step: 0.1, title: 'Silence added at the end first, so the reverb rings out (the line\'s own length when shorter)' })) : null,
          h('div.row', { style: { gap: '8px' } }, h('span.faint', 'ffmpeg filter'), (() => { const t = h('input.mono', { type: 'text', value: fx.filter, placeholder: '(none)', style: { flex: '1', fontSize: '12px' }, title: 'An ffmpeg -af chain applied before the reverb; {rate} is the file\'s sample rate' }); t.addEventListener('change', () => { fx.filter = t.value.trim(); saveFx(); }); return t; })(),
            num(fx, 'gainDb', { label: 'Gain dB', min: -20, max: 20, step: 0.5 })),
          h('div.row', { style: { gap: '6px' } },
            fx.reverb ? h('button.btn.sm.ghost', { title: 'Only the filter, no reverb', onclick: () => { fx.reverb = null; saveProject({ effects: list }); } }, 'No reverb')
              : h('button.btn.sm.ghost', { title: 'Add a reverb after the filter (starts as Inner thoughts)', onclick: () => { fx.reverb = JSON.parse(JSON.stringify((builtIns.find((b) => b.id === 'inner') || {}).reverb || {})); saveProject({ effects: list }); } }, 'Add a reverb'),
            isDefault ? h('span.faint', 'The line toggle and "all lines" use this one.') : h('button.btn.sm', { title: 'The line toggle and "all lines of this event" use this effect', disabled: !!fx.isEmpty, onclick: () => saveProject({ effects: list, defaultEffect: fx.id }) }, 'Use for the toggle'),
            h('button.btn.sm.ghost', { title: 'A copy to change without losing this one', onclick: () => { const c = JSON.parse(JSON.stringify(fx)); c.id = fx.id + '-' + Math.random().toString(36).slice(2, 6); c.name = fx.name + ' (copy)'; list.push(c); saveProject({ effects: list }); } }, 'Duplicate'),
            builtIn ? h('button.btn.sm.ghost', { title: 'Back to the built-in settings of this effect', onclick: () => { Object.assign(fx, JSON.parse(JSON.stringify(builtIn))); saveProject({ effects: list }); } }, 'Reset') : h('button.btn.sm.ghost.danger', { title: 'Delete this effect (lines that have it keep it until you take it off)', disabled: isDefault, onclick: () => { saveProject({ effects: list.filter((x) => x !== fx) }); } }, 'Delete'))));
      return det;
    });
    return h('div.section', h('div.section-head', h('h2', 'Effects'), helpButton('grunt-events'), h('div.spacer'),
      h('button.btn.sm.ghost', { title: 'Every built-in effect back as it came (your copies go)', onclick: async () => { if (await confirmBox('Reset the effects?', 'Every effect goes back to its built-in settings and your own copies are removed. Lines keep the effect they have.', 'Reset', true)) saveProject({ effects: builtIns, defaultEffect: 'inner' }); } }, 'Reset all')),
      h('div.section-body', h('div.faint', { style: { padding: '6px 0 8px' } }, 'Reverbs, filters and voice effects for lines: the switch on each line (and "all lines of this event") uses the one marked toggle; ⋯ → Effect… picks any. The reverb is Audacity\'s; the rest are ffmpeg filters.'), rows));
  }

  // ---------------------------------------------------------------- layout
  function renderAll() {
    renderHead();
    clear(body);
    if (tab === 'setup') { body.append(renderSetup()); return; }
    const banner = pathsBanner(d.paths, P().target, async () => { d = await rpc('grunts.get', { id }); byId = evById(); renderAll(); });
    const split = h('div.split' + (showInsp ? '' : '.no-insp'));
    split.append(left, insp);
    if (banner) body.append(banner);
    body.append(filters, split, statusbar);
    renderFilters();
    renderBlocks();
    renderInspector();
    renderStatus();
  }

  document.addEventListener('job-done', async function onDone(e) {
    if (!document.body.contains(view)) { document.removeEventListener('job-done', onDone); audio.pause(); return; }
    const r = e.detail || {};
    if ((r.kind === 'grunts-measure' || r.kind === 'grunts-whisper') && r.id === id) { try { d = await rpc('grunts.get', { id }); byId = evById(); } catch { /* keep */ } renderAll(); return; }
    if (r.kind !== 'grunts-export' || r.id !== id) return;
    try { d = await rpc('grunts.get', { id }); byId = evById(); } catch { /* keep */ }
    renderAll();
    if (r.ok) showReport(r.report);
    else toast(r.message || 'Export failed.', true);
  });

  renderAll();
  requestAnimationFrame(() => { blocks.scrollTop = store.get('gr-scroll:' + id, 0); });
  // Ctrl+K: every event of this project.
  setPaletteExtra(() => d.events.map((e) => ({ kind: 'Event', label: `${e.category} · ${e.label}`, sub: `${e.lines.length} line(s) · ${P().name}`, run: () => { tab = 'events'; store.set('gr-tab:' + id, 'events'); selId = e.id; catFilter = ''; search = ''; filter = 'all'; renderAll(); requestAnimationFrame(() => blocks.querySelector(`.gr-tile[data-id="${CSS.escape(e.id)}"]`)?.scrollIntoView({ block: 'center' })); } })));
  return view;
}
