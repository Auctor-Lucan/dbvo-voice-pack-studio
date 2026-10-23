// Other Tools → NPC Revoice: replace the voice files of NPC dialogue in any mod (or the base game).
// Standalone from the DBVO pipeline; shares engines, takes and .fuz building with Phase 2.
import { flashRow, TOOL_TIPS, popover, rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, debounce, aiReady, autotagSummary, switchEl, segmented, popupMenu, columnsMenu, droppedPaths, diffAdded, icon, mergeInto, helpButton, generateButton, outputFormats, keepScroll, withBusy } from './lib.js';
import { app, go, refreshState } from './app.js';
import { voiceChip, creditsButton, LANGS } from './phase2.js';
import { bulkTagDialog, insertTagAtCaret, findReplaceDialog } from './tagtools.js';
import { recordTake } from './recorder.js';

const CERT = { Exact: 'Speaker known', Probable: 'Probable speakers', Unknown: 'Speaker unknown' };

// ------------------------------------------------------------------ list

export async function revoiceListView() {
  const s = await refreshState();
  const list = await rpc('revoice.list');
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'NPC Revoice Tool'),
    helpButton('npc-revoice-tool'),
    h('span.sub', 'Other Tools · replace NPC voice files in any mod or the base game, line by line, with voices from your ElevenLabs account or local servers. No plugin needed unless you give an NPC a new voice type.'),
    h('div.spacer'),
    h('button.btn.primary', { title: 'Start a revoice project: drop a mod\'s plugin and its voice archive or folder', onclick: create }, '+ New revoice')));
  const body = h('div.view-body');
  view.append(body);
  if (!s.settings.baseGameFolder) {
    body.append(h('div.banner.warn', h('span', 'Set the base game folder (Skyrim Special Edition\\Data) in Global Settings → General: masters, vanilla voice files and strings are read from there.'),
      h('div.spacer'), h('button.btn.sm', { title: 'Open Global Settings', onclick: () => go('settings', { tab: 'general' }) }, 'Open Global Settings')));
  }
  if (!list.length) {
    body.append(h('div.empty', h('h2', 'No revoice projects yet'),
      h('div', 'Drop a mod\'s plugin (.esp / .esm / .esl) with its .bsa or loose Sound\\Voice folder. The tool finds every voiced NPC line, who says it, and the file the game plays now.'),
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { title: 'Start a revoice project', onclick: create }, '+ New revoice'))));
    return view;
  }
  body.append(h('div.cat', h('div.cat-head', h('h3', 'Revoice projects'), h('span.faint', list.length)),
    h('div.rows3', list.map((x) => h('div.prow', { title: 'Open this project', onclick: () => go('revoice', { id: x.id }) },
      h('div.row', h('span.title.ellipsis.grow', x.name), x.newVoiceTypes ? h('span.badge.b-player', { title: 'Has new voice types (SkyPatcher + FLM)' }, `${x.newVoiceTypes} new voice type(s)`) : null),
      h('div.faint.ellipsis', { title: x.targets.join(', ') }, x.targets.join(', ') || 'no plugins yet'),
      h('div.prow-stats',
        h('div.grow', h('div.bar' + (x.lines && x.generated >= x.lines ? '.ok' : ''), h('div.bar-fill', { style: { width: (x.lines ? (100 * x.generated) / x.lines : 0) + '%' } }))),
        h('span.mono', `${x.generated} / ${x.lines}`),
        h('span.left', h('b', String(x.speakers)), ' speakers')))))));
  return view;

  async function create() {
    const r = await prompt('New revoice project', [{ name: 'n', label: 'Name', placeholder: 'e.g. Example Town revoiced' }]);
    if (!r || !r.n.trim()) return;
    try { const c = await rpc('revoice.create', { name: r.n.trim() }); go('revoice', { id: c.id }); } catch (e) { fail(e); }
  }
}

// ------------------------------------------------------------------ project

export async function revoiceView(id) {
  await refreshState();
  let proj = await withBusy(rpc('revoice.get', { id }), 'Loading voice lines', 'Reading the project\'s lines and takes…');
  let showInsp = store.get('rv-insp', true);
  // Reopens where you left off: the tab, the speaker picked in the toolbar and the scroll position of the lines.
  let tab = store.get('rv-tab:' + id, proj.lines.length ? 'speakers' : 'setup');
  let filter = 'all', speakerFilter = store.get('rv-speaker:' + id, ''), search = '';
  if (speakerFilter && !proj.speakers.some((x) => x.key === speakerFilter)) speakerFilter = '';
  let pendingScroll = store.get('rv-scroll:' + id, 0);
  // Missing: no take at all (a line with takes is never missing). Edited: has takes, but the text changed since (or the last attempt failed).
  const isMissing = (l) => !l.takes.length;
  const isEdited = (l) => l.takes.length > 0 && (l.status === 'Stale' || l.status === 'Failed');
  const setTab = (v) => { tab = v; store.set('rv-tab:' + id, v); renderAll(); };
  const setSpeaker = (k) => { speakerFilter = k; store.set('rv-speaker:' + id, k); };
  // Takes that are not in the output folder yet (never exported, or the line has another take since).
  const relKey = (l) => `sound\\voice\\${l.pf}\\${l.vt}\\${l.stem}.fuz`.toLowerCase();
  function unexported() {
    const ex = proj.exportedTakes || {};
    const skip = new Set(proj.speakers.filter((x) => x.skip).map((x) => x.key));
    return proj.lines.filter((l) => l.sel && !l.excl && !l.gone && !skip.has(l.speaker) && ex[relKey(l)] !== l.sel).length;
  }
  const expNew = h('span.exp-new');
  const updateExpNew = () => { const n = unexported(); expNew.textContent = n ? ` · ${n.toLocaleString()} new` : ''; expNew.title = n ? `${n} take(s) not in the output folder yet` : ''; };
  let focusedId = null;
  const audio = new Audio();
  let index = new Map();
  const reindex = () => { index = new Map(proj.lines.map((l) => [l.id, l])); };
  reindex();

  const view = h('div.view.work');
  const head = h('div.view-head');
  const creditsSlot = h('span.credits-slot'); // filled by the toolbar, so the toolbar stays on one line
  const body = h('div', { style: { flex: '1', minHeight: '0', display: 'flex', flexDirection: 'column' } });
  view.append(head, body);

  // ---------------------------------------------------------------- head
  function renderHead() {
    updateExpNew();
    // Credits show on every tab, filled once (the button keeps itself up to date).
    const needs = () => proj.lines.filter((l) => !l.excl && !l.gone && isMissing(l)).reduce((a, l) => a + l.spoken.length, 0);
    clear(creditsSlot).append(creditsEl || (creditsEl = creditsButton(needs, 'This project')));
    const exp = h('button.btn.accent.export-btn', { title: 'Write the chosen takes as .fuz files at the game\'s paths. "new" counts takes that are not in the output folder yet.', disabled: !!app.job }, icon('export', { size: 15 }), 'Export .fuz', expNew, ' ▾');
    exp.addEventListener('click', () => popupMenu(exp, [
      { label: 'Export .fuz files to the output folder', sub: `${proj.outputFolder || '(set the output folder in Setup first)'}\\Sound\\Voice\\<plugin>\\<voice type>\\<file>.fuz, the game's own paths (replaces files already there)` + (proj.plans.length ? '. Also the new voice type plugin, SkyPatcher and FLM files.' : ''), onclick: () => doExport(true) },
      { label: 'Export, but keep files already there', sub: 'The same, but .fuz files that exist in the output folder are left as they are', onclick: () => doExport(false) },
      proj.plans.length ? { label: 'Only the new voice type pack', sub: 'Plugin + SkyPatcher + FLM + install notes', onclick: writePack } : null,
      proj.outputFolder ? { label: 'Open the output folder', sub: proj.outputFolder, onclick: () => rpc('open.path', { path: proj.outputFolder }) } : null,
    ].filter(Boolean)));
    const tabs = segmented([['setup', 'Setup', 'The mod\'s files, output folder and options'], ['speakers', `Speakers (${proj.speakers.length})`, 'Who speaks: pick a voice per NPC or voice type'], ['lines', `Lines (${proj.lines.length})`, 'Every line to revoice: generate, listen and edit'], ['voicetypes', `New voice types (${proj.plans.length})`, 'Give one NPC who shares a vanilla voice type a voice type of its own']], tab, setTab);
    clear(head).append(
      h('button.btn.ghost.sm', { title: 'Back to the project list', onclick: () => go('revoices') }, '← NPC Revoice'),
      h('h1', proj.name),
      tabs,
      helpButton({ setup: 'setup', speakers: 'speakers', lines: 'lines', voicetypes: 'new-voice-types-optional' }[tab] || 'npc-revoice-tool'),
      h('div.spacer'),
      creditsSlot,
      switchEl(proj.voiceChanger, toggleVc, 'Voice Changer', { title: 'Convert the game\'s current recordings into the new voice instead of text-to-speech' }),
      h('button.btn', { title: 'Read the plugins and voice sources again and rebuild the line list (keeps your takes and edits)', disabled: !!app.job, onclick: scan }, proj.lastScan ? '⟳ Rescan' : 'Scan'),
      exp,
      h('span', { style: { width: '18px' } }),
      h('button.btn.ghost.danger.sm', { title: 'Delete this project and its takes (exported files stay)', onclick: async () => { if (await confirmBox('Delete revoice project?', 'Deletes its takes too. Exported files stay where they are.', 'Delete', true)) { try { await rpc('revoice.delete', { id }); go('revoices'); } catch (e) { fail(e); } } } }, 'Delete'));
  }

  async function toggleVc(on) {
    await save({ voiceChanger: on });
  }

  async function save(o) {
    try { proj = await rpc('revoice.save', { id, ...o }); reindex(); renderAll(); } catch (e) { fail(e); renderHead(); }
  }

  async function scan() {
    try { await rpc('revoice.scan', { id }); toast('Scanning… the first scan with Skyrim.esm takes a little while.'); } catch (e) { fail(e); }
  }

  async function doExport(overwrite) {
    if (!proj.outputFolder) { toast('Set the output folder in Setup first.', true); setTab('setup'); return; }
    try {
      // Files an earlier export wrote that no line uses any more (the mod changed, or a line now has another voice type).
      const stale = await rpc('revoice.staleExports', { id }).catch(() => []);
      let removeStale = false;
      if (stale.length) {
        const v = await modal('Files from an earlier export', [
          h('div', `${stale.length} .fuz file(s) in the output folder were written by an earlier export but are no longer exported: their lines are excluded, their speaker is skipped, their take was deleted, or the mod changed so the game looks for them elsewhere. Left in place the game still plays them.`),
          h('div.box.mono', { style: { maxHeight: '180px', overflow: 'auto', whiteSpace: 'pre-wrap', fontSize: '12px' } }, stale.slice(0, 200).join('\n') + (stale.length > 200 ? `\n… ${stale.length - 200} more` : '')),
          h('div.faint', 'Only files this tool wrote are listed; nothing else in the folder is touched.'),
        ], [{ label: 'Cancel', value: null }, { label: 'Keep them', value: 'keep' }, { label: 'Delete them', primary: true, value: 'delete' }]);
        if (v == null) return;
        removeStale = v === 'delete';
      }
      await rpc('revoice.export', { id, overwrite, removeStale });
    } catch (e) { fail(e); }
  }

  async function writePack() {
    try { const files = await rpc('revoice.writePack', { id }); await modal('New voice type pack written', [h('div.box.mono', { style: { whiteSpace: 'pre-wrap' } }, files.join('\n'))]); } catch (e) { fail(e); }
  }

  // ---------------------------------------------------------------- setup
  function renderSetup() {
    const wrap = h('div.view-body.reading');
    const sec = (title, extra, ...rows) => h('div.section', h('div.section-head', h('h2', title), h('div.spacer'), extra), h('div.section-body', rows));
    const srow = (label, help, control) => h('div.srow', h('div.lbl', h('span', label), help ? h('small', help) : null), h('div', { title: typeof help === 'string' ? help : label }, control));
    const drop = h('div.dropzone', h('div', h('b', 'Drop plugins, .bsa archives, .zip / .7z files or folders here')),
      h('div.faint', 'A plugin brings its own archives (Plugin.bsa, Plugin - Voices.bsa) and a Sound\\Voice folder next to it. Folders can be a Data folder, a mod folder (MO2\\mods\\…) or an unpacked download.'),
      h('div.row', { style: { justifyContent: 'center', marginTop: '10px' } },
        h('button.btn.sm', { title: 'Add plugins', onclick: async () => add(await rpc('pick.files', { title: 'Plugins to revoice (and their masters)', filter: 'Plugins|*.esp;*.esm;*.esl' })) }, 'Add plugins…'),
        h('button.btn.sm', { title: 'Add voice archives', onclick: async () => add(await rpc('pick.files', { title: 'Voice archives', filter: 'Archives|*.bsa;*.zip;*.7z' })) }, 'Add .bsa / .zip / .7z…'),
        h('button.btn.sm', { title: 'Add a folder with Sound\\Voice inside', onclick: async () => { const f = await rpc('pick.folder', { title: 'Folder with Sound\\Voice' }); if (f) add([f]); } }, 'Add folder…')));
    drop.addEventListener('dragover', (e) => { e.preventDefault(); drop.classList.add('over'); });
    drop.addEventListener('dragleave', () => drop.classList.remove('over'));
    drop.addEventListener('drop', async (e) => { e.preventDefault(); e.stopPropagation(); drop.classList.remove('over'); try { add(await droppedPaths([...(e.dataTransfer.files || [])])); } catch (err) { fail(err); } });

    const ls = proj.lastScan;
    const scanBox = ls ? h('div.col', { style: { gap: '6px' } },
      h('div.stat-row', h('span', h('b', ls.lines.toLocaleString()), ' voice files to make'), h('span', h('b', ls.withOriginal.toLocaleString()), ' have a current file'),
        h('span', h('b', ls.infos.toLocaleString()), ' lines · ', h('b', ls.responses.toLocaleString()), ' responses'), ls.unknownSpeaker ? h('span', { style: { color: 'var(--warn)' } }, `${ls.unknownSpeaker} response(s) with no known speaker or file`) : null,
        h('span.faint', 'scanned ' + new Date(ls.scannedUtc).toLocaleString())),
      ls.orphans ? h('div.banner.info', { style: { margin: 0 } }, h('span', h('b', `${ls.orphans} voice file(s) in the mod's sources match no line`), ' — usually a merged, compacted (ESL) or renamed plugin changed their FormIDs. Link them to their lines so they play as the current file and can feed the Voice Changer.'),
        h('div.spacer'), h('button.btn.sm', { title: 'Match orphaned files to silent lines by name, then by Whisper transcription', onclick: orphansModal }, 'Match orphaned files…')) : null,
      speakerChangesBanner(),
      ls.textChanged ? h('div.banner.info', { style: { margin: 0 } }, h('span', h('b', `${ls.textChanged} line(s) changed in the mod since their take`), ' — they show as Edited since take: Generate options ▾ → Edited makes new takes.')) : null,
      ls.masterCosts.length ? h('div.banner.warn', { style: { margin: 0 } }, h('div', h('b', 'Missing masters'), h('ul', ls.masterCosts.map((x) => h('li', x))), h('div.faint', 'Drop them in to fix it, or set the base game folder for vanilla masters.'))) : null,
      ls.missingStrings.length ? h('div.banner.warn', { style: { margin: 0 } }, 'No strings for localized plugin(s): ' + ls.missingStrings.join(', ') + '. Their text is empty. Add the plugin\'s Strings folder or the archive that holds its strings.') : null,
      ls.errors.length ? h('div.banner.bad', { style: { margin: 0 } }, h('ul', ls.errors.map((x) => h('li', x)))) : null)
      : h('div.dim', 'Not scanned yet. Add the plugins and voice sources, then Scan.');

    wrap.append(
      sec('Scan', h('button.btn.primary.sm', { title: 'Read everything and build the line list', disabled: !!app.job, onclick: scan }, proj.lastScan ? '⟳ Rescan' : 'Scan'), h('div', { style: { padding: '10px 0' } }, scanBox)),
      sec('Plugins', null,
        h('div', { style: { padding: '10px 0' } }, drop),
        h('table.tbl', h('thead', h('tr', h('th', 'Plugin'), h('th', { title: 'Lines from this plugin are revoiced. Others are only read (masters, NPCs, conditions).' }, 'Revoice its lines'), h('th', ''))),
          h('tbody', proj.plugins.map((pl) => h('tr',
            h('td', h('div.mono', pl.fileName, pl.baseGame ? h('span.badge.b-skip', { style: { marginLeft: '6px' } }, 'base game') : null, !pl.exists ? h('span.badge.b-broken', { style: { marginLeft: '6px' } }, 'missing') : null), h('div.faint.ellipsis', { title: pl.path }, pl.path)),
            h('td', switchEl(pl.target, (v) => setPlugin(pl, { target: v }), '', { title: 'Revoice this plugin\'s lines' + (pl.baseGame ? ' (base game: many thousands of lines)' : '') })),
            h('td', pl.baseGame ? null : h('button.icon-btn', { title: 'Remove from the project', onclick: () => setPlugin(pl, { remove: true }) }, '×'))))))),
      sec('Voice file sources', null,
        h('div.faint', { style: { padding: '8px 0' } }, 'Where the current voice files are (for playback, and to know which voice type folders exist). Higher priority wins; loose files beat archives, as in game.'),
        h('table.tbl', h('thead', h('tr', h('th', 'Source'), h('th', { title: 'Higher wins when two sources have the same file' }, 'Priority'), h('th', ''))),
          h('tbody', proj.sources.map((src) => h('tr',
            h('td', h('div', h('span.badge.b-fixed', src.kind), ' ', h('span.mono', src.path.split('\\').pop()), src.baseGame ? h('span.badge.b-skip', { style: { marginLeft: '6px' } }, 'base game') : null, !src.exists ? h('span.badge.b-broken', { style: { marginLeft: '6px' } }, 'missing') : null), h('div.faint.ellipsis', { title: src.path }, src.path)),
            h('td', src.baseGame ? h('span.faint', String(src.priority)) : h('input', { type: 'number', value: src.priority, style: { width: '70px' }, title: 'Higher wins', onchange: (e) => setSource(src, { priority: Number(e.target.value) }) })),
            h('td', src.baseGame ? null : h('button.icon-btn', { title: 'Remove this source', onclick: () => setSource(src, { remove: true }) }, '×'))))))),
      sec('Options', null,
        srow('Name', null, (() => { const i = h('input', { type: 'text', value: proj.name, style: { width: '100%' } }); i.addEventListener('change', () => save({ name: i.value.trim() || proj.name })); return i; })()),
        srow('Output folder', 'Exports go here as Sound\\Voice\\<plugin>\\<voice type>\\<file>.fuz, ready to zip or pack into a BSA. Use an empty folder or a new MO2 mod folder.', folderField(proj.outputFolder, (f) => save({ outputFolder: f }))),
        srow('Base game', proj.baseGameFolder ? 'Skyrim.esm, the DLCs, the vanilla voice archives and strings from ' + proj.baseGameFolder : 'Set the base game folder in Global Settings → General first.',
          h('div.row', switchEl(proj.useBaseGame, (v) => save({ useBaseGame: v }), 'Use the base game', { title: 'Read the vanilla masters, voices and strings' }),
            h('button.btn.sm', { title: 'Look for the base game files again', onclick: () => save({ refreshBaseGame: true }) }, 'Refresh'))),
        srow('Emotion tags', 'Suggest an audio tag from each line\'s emotion (Anger 80 → [angry]) for you to accept or undo, like AI tagging in Phase 2.', switchEl(proj.emotionTags, (v) => save({ emotionTags: v }), '', { title: 'Suggest tags from the line\'s emotion' })),
        srow('Language', 'For localized plugins\' strings files (Skyrim_English.ilstrings …).', (() => { const i = h('input', { type: 'text', value: proj.language, style: { width: '160px' } }); i.addEventListener('change', () => save({ language: i.value.trim() || 'English' })); return i; })())));
    return wrap;
  }

  function folderField(value, onPick) {
    const i = h('input', { type: 'text', value: value || '', placeholder: 'e.g. D:\\MO2\\mods\\Example Town Revoiced', style: { flex: '1' } });
    i.addEventListener('change', () => onPick(i.value.trim()));
    return h('div.path-input', i, h('button.btn.sm', { title: 'Choose the folder', onclick: async () => { const f = await rpc('pick.folder', { title: 'Output folder' }); if (f) onPick(f); } }, 'Browse'));
  }

  async function add(paths) {
    if (!paths || !paths.length) return;
    try {
      const r = await rpc('revoice.addFiles', { id, paths });
      proj = r.project; reindex(); renderAll();
      toast(r.added.length ? `Added ${r.added.length}: ${r.added.map((x) => x.split('\\').pop()).join(', ')}. Scan when you are ready.` : 'Nothing new to add.');
    } catch (e) { fail(e); }
  }
  async function setPlugin(pl, o) { try { proj = await rpc('revoice.plugin', { id, path: pl.path, ...o }); reindex(); renderAll(); } catch (e) { fail(e); } }
  async function setSource(src, o) { try { proj = await rpc('revoice.source', { id, sourceId: src.id, ...o }); reindex(); renderAll(); } catch (e) { fail(e); } }

  // ---------------------------------------------------------------- orphaned files
  function speakerChangesBanner() {
    const changed = proj.lines.filter((l) => l.newSpeaker);
    if (!changed.length) return null;
    const ex = [...new Set(changed.map((l) => `${l.speakerName} → ${l.newSpeaker}`))];
    const decide = async (decision) => {
      try { const r = await rpc('revoice.speakerChanges', { id, decision }); proj = r.project; reindex(); renderAll(); toast(decision === 'apply' ? `${r.changed} line(s) moved to their new speaker (marked "changed since take").` : `${r.changed} line(s) keep their speaker.`); } catch (e) { fail(e); }
    };
    return h('div.banner.warn', { style: { margin: 0 } },
      h('div', h('b', `This scan found another speaker for ${changed.length} line(s) that already have takes`),
        h('div', { style: { marginTop: '4px' } }, ex.slice(0, 5).join(' · ') + (ex.length > 5 ? ` · +${ex.length - 5} more` : '')),
        h('div.faint', 'They keep their current speaker (and voice) until you choose. Moving them marks their takes as changed, so they can be made again with the new speaker\'s voice.')),
      h('div.spacer'),
      h('button.btn.sm', { title: 'Keep the speaker these lines have now (later scans won\'t ask again for them)', onclick: () => decide('keep') }, 'Keep current'),
      h('button.btn.sm.primary', { title: 'Move these lines to the speaker the scan found', onclick: () => decide('apply') }, 'Use new speakers'));
  }

  async function orphansModal() {
    let data;
    try { data = await rpc('revoice.orphans', { id }); } catch (e) { fail(e); return; }
    const silentByVt = new Map();
    for (const l of data.silent) { const k = l.vt.toLowerCase(); if (!silentByVt.has(k)) silentByVt.set(k, []); silentByVt.get(k).push(l); }
    const choice = new Map(); // key -> lineId
    const keyOf = (m) => m.sourceId + '|' + m.inner;
    for (const m of data.proposals) if (m.lineId) choice.set(keyOf(m), m.lineId);
    const rows = data.proposals.map((m) => {
      const options = (silentByVt.get(m.voiceType.toLowerCase()) || data.silent).slice(0, 400);
      const sel = h('select', { style: { maxWidth: '420px' }, title: 'The line this file belongs to' }, h('option', { value: '' }, '— not linked —'),
        options.map((l) => h('option', { value: l.id, selected: l.id === m.lineId }, `${l.text.slice(0, 80)}  ·  ${l.speakerName}`)));
      sel.addEventListener('change', () => { if (sel.value) choice.set(keyOf(m), sel.value); else choice.delete(keyOf(m)); });
      return h('tr',
        h('td', h('button.icon-btn', { title: 'Play this file', onclick: async () => { try { const u = await rpc('revoice.orphanAudio', { id, sourceId: m.sourceId, inner: m.inner }); audio.src = u; audio.play().catch(fail); } catch (e) { fail(e); } } }, '▶')),
        h('td', h('div.mono.ellipsis', { title: m.display, style: { maxWidth: '300px' } }, m.inner.split(/[\\/]/).pop()), h('div.faint', m.voiceType), m.transcript ? h('div.faint', { title: 'What Whisper heard' }, '“' + m.transcript + '”') : null),
        h('td', sel, h('div.faint', m.method ? m.method : 'no match yet')));
    });
    const ok = await modal(`Orphaned voice files (${data.proposals.length})`, [
      h('div.faint', 'Files are first matched by the quest and topic part of their name. For the rest, Whisper (the WAV to LIP settings) listens to each file and the transcript is matched to the subtitles of silent lines. Check the matches, then Link.'),
      h('div.row', h('button.btn.sm', { title: 'Transcribe the files that have no match yet and match them by text', disabled: !!app.job, onclick: async () => { try { await rpc('revoice.orphansWhisper', { id }); document.getElementById('modal').classList.add('hidden'); toast('Transcribing… the list opens again when it is done.'); } catch (e) { fail(e); } } }, '🎙 Match the rest with Whisper'),
        h('span.faint', `${data.proposals.filter((m) => m.lineId).length} matched`)),
      h('div.box', { style: { maxHeight: '420px', overflow: 'auto' } }, h('table.tbl', h('tbody', rows))),
    ], [{ label: 'Cancel', value: false }, { label: 'Link the matched files', primary: true, value: true }], { wide: true, help: 'setup' });
    if (!ok) return;
    const approved = data.proposals.filter((m) => choice.get(keyOf(m))).map((m) => ({ sourceId: m.sourceId, inner: m.inner, lineId: choice.get(keyOf(m)), method: m.lineId === choice.get(keyOf(m)) ? m.method : 'by hand' }));
    try { const r = await rpc('revoice.orphansApply', { id, approved }); proj = r.project; reindex(); renderAll(); toast(`${r.linked} file(s) linked.`); } catch (e) { fail(e); }
  }

  // ---------------------------------------------------------------- speakers
  // Each speaker picks an engine and a voice from your accounts / servers; the engine settings below are shared.
  const ENGINE_NAMES = { elevenlabs: 'ElevenLabs', voxcpm: 'VoxCPM (local)', omnivoice: 'OmniVoice (local)' };
  const voiceLists = { elevenlabs: null, voxcpm: null, omnivoice: null };
  let elevenModels = null;
  let loadingVoices = false;
  async function loadVoices(refresh = false) {
    if (loadingVoices) return;
    loadingVoices = true;
    const used = new Set(['elevenlabs', ...proj.speakers.map((sp) => sp.engine || 'elevenlabs')]);
    try {
      await Promise.all([...used].map(async (eng) => {
        if (!refresh && voiceLists[eng]) return;
        try {
          voiceLists[eng] = eng === 'elevenlabs'
            ? (await rpc('eleven.voices', { refresh })).map((v) => ({ id: v.voiceId, name: v.name, sub: v.category }))
            : (await rpc('engine.voices', { engine: eng })).map((v) => ({ id: v, name: v }));
        } catch { voiceLists[eng] = []; }
      }));
      if (!elevenModels || refresh) elevenModels = await (proj.voiceChanger ? rpc('eleven.stsModels', {}) : rpc('eleven.models', { refresh })).catch(() => []);
    } finally { loadingVoices = false; }
    if (tab === 'speakers') renderAll();
    else if (tab === 'lines' && vbar.isConnected) renderVbar();
  }

  let voiceEdits = 0;
  const saveVoiceSettings = debounce(async () => {
    // keep the edited object (the controls hold its eleven / voxCpm / … objects); merge the saved copy in
    const seq = voiceEdits;
    try { const r = await rpc('revoice.save', { id, voice: proj.voice }); if (seq === voiceEdits) mergeInto(proj.voice, r.voice); } catch (e) { fail(e); }
  }, 300);

  function voiceSettings() {
    const v = proj.voice;
    const used = new Set(proj.speakers.filter((sp) => sp.voiceId && !sp.skip).map((sp) => sp.engine || 'elevenlabs'));
    const fmtName = { mp3_44100_128: 'MP3 128', mp3_44100_192: 'MP3 192', pcm_44100: 'PCM 44.1k' };
    const parts = [];
    if (proj.voiceChanger) parts.push(`Voice Changer · ${v.sts.modelId} · stability ${Number(v.sts.stability).toFixed(2)} · similarity ${Number(v.sts.similarity).toFixed(2)}`);
    else {
      if (!used.size || used.has('elevenlabs')) parts.push(`ElevenLabs ${v.eleven.modelId} · stability ${Number(v.eleven.stability).toFixed(2)} · similarity ${Number(v.eleven.similarity).toFixed(2)} · speed ${Number(v.eleven.speed).toFixed(2)} · ${fmtName[v.eleven.outputFormat] || v.eleven.outputFormat}`);
      if (used.has('voxcpm')) parts.push(`VoxCPM CFG ${v.voxCpm.cfgValue}`);
      if (used.has('omnivoice')) parts.push(`OmniVoice ${v.omniVoice.numStep} steps`);
    }
    const d = h('details.voice-settings', { style: { maxWidth: '1180px', marginBottom: '12px' } },
      h('summary', h('b', 'Voice settings'), h('span.faint.ellipsis', { style: { minWidth: 0 }, title: 'Shared by every speaker; each speaker picks only its own voice below' }, parts.join('  |  ')), h('div.spacer'),
        h('button.btn.sm.ghost', { title: 'Reload voices from your accounts and local servers', onclick: (e) => { e.preventDefault(); loadVoices(true); } }, '⟳ Reload voices')),
      h('div.faint', { style: { margin: '8px 0 4px' } }, 'Shared by every speaker; each speaker picks only its own voice below.'),
      h('div.col', { style: { gap: '8px' } }, voiceSettingRows()));
    d.open = store.get('rv-vs-open', false);
    d.addEventListener('toggle', () => store.set('rv-vs-open', d.open));
    return d;
  }

  // The engine settings (shared by every speaker), as rows: the Speakers tab and the toolbar above the lines.
  function voiceSettingRows() {
    const v = proj.voice;
    const set = (obj, k, val) => { obj[k] = val; voiceEdits++; saveVoiceSettings(); };
    const grp = (label, ...kids) => h('div.tgroup', { title: TOOL_TIPS[label] || '' }, h('label', label), h('div.row', ...kids));
    const slider = (obj, k, min, max, step, label, title) => {
      const val = h('span.mono.dim', { style: { width: '34px' } }, Number(obj[k]).toFixed(2));
      const r = h('input', { type: 'range', min, max, step, value: obj[k], title: title || '', style: { width: '80px' } });
      r.addEventListener('input', () => { val.textContent = Number(r.value).toFixed(2); });
      r.addEventListener('change', () => set(obj, k, Number(r.value)));
      return grp(label, r, val);
    };
    const num = (obj, k, label, o = {}) => {
      const i = h('input', { type: 'number', value: obj[k] ?? '', placeholder: o.placeholder || '', step: o.step || 'any', min: o.min, title: o.title || '', style: { width: o.width || '72px' } });
      i.addEventListener('change', () => set(obj, k, i.value === '' ? (o.nullable ? null : 0) : Number(i.value)));
      return grp(label, i);
    };
    const used = new Set(proj.speakers.filter((sp) => sp.voiceId && !sp.skip).map((sp) => sp.engine || 'elevenlabs'));
    if (!used.size) used.add('elevenlabs');
    const rows = [];
    if (proj.voiceChanger) {
      const st = v.sts;
      const ms = h('select', { onchange: (e) => set(st, 'modelId', e.target.value) }, (elevenModels && elevenModels.length ? elevenModels : [{ modelId: st.modelId }]).map((m) => h('option', { value: m.modelId, selected: m.modelId === st.modelId }, m.name || m.modelId)));
      rows.push(h('div.tool-row', h('b', 'ElevenLabs Voice Changer'), grp('Model', ms),
        slider(st, 'stability', 0, 1, 0.01, 'Stability'), slider(st, 'similarity', 0, 1, 0.01, 'Similarity'), slider(st, 'style', 0, 1, 0.01, 'Style'),
        grp('Remove noise', switchEl(st.removeBackgroundNoise, (x) => set(st, 'removeBackgroundNoise', x), '', { title: 'Strip background noise from the original before converting' })),
        num(st, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '90px' }), num(st, 'concurrency', 'Concurrent', { min: 1, width: '56px', title: 'Your plan\'s concurrent request limit' })));
    } else {
      if (used.has('elevenlabs')) {
        const e = v.eleven;
        const m = (elevenModels || []).find((x) => x.modelId === e.modelId) || {};
        const ms = h('select', { onchange: (ev) => { set(e, 'modelId', ev.target.value); renderAll(); } }, (elevenModels && elevenModels.length ? elevenModels : [{ modelId: e.modelId }]).map((x) => h('option', { value: x.modelId, selected: x.modelId === e.modelId }, x.name || x.modelId)));
        const fmt = h('select', { onchange: (ev) => set(e, 'outputFormat', ev.target.value) }, outputFormats(e.outputFormat).map(([k, t]) => h('option', { value: k, selected: k === e.outputFormat }, t)));
        const lang = h('input', { type: 'text', value: e.languageCode || '', placeholder: 'auto', title: 'ISO 639-1 language code, e.g. en, de. Empty = auto', style: { width: '56px' } });
        lang.addEventListener('change', () => set(e, 'languageCode', lang.value.trim()));
        rows.push(h('div.tool-row', h('b', 'ElevenLabs'), grp('Model', ms),
          slider(e, 'stability', 0, 1, 0.01, 'Stability'), slider(e, 'similarity', 0, 1, 0.01, 'Similarity'),
          m.canUseStyle === false ? null : slider(e, 'style', 0, 1, 0.01, 'Style'), slider(e, 'speed', 0.7, 1.2, 0.01, 'Speed'),
          m.canUseSpeakerBoost === false ? null : grp('Speaker boost', switchEl(e.speakerBoost, (x) => set(e, 'speakerBoost', x), '', { title: 'Closer to the voice; can clip or sound harsh' })),
          grp('Format', fmt), grp('Language', lang), num(e, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '90px' }),
          num(e, 'concurrency', 'Concurrent', { min: 1, width: '56px', title: 'Your plan\'s concurrent request limit' })));
      }
      if (used.has('voxcpm')) {
        const o = v.voxCpm;
        rows.push(h('div.tool-row', h('b', 'VoxCPM'), num(o, 'cfgValue', 'CFG', { step: 0.1 }), num(o, 'inferenceTimesteps', 'Timesteps', { min: 1 }),
          grp('Normalize', switchEl(o.normalize, (x) => set(o, 'normalize', x), '')), grp('Retry bad cases', switchEl(o.retryBadcase, (x) => set(o, 'retryBadcase', x), '')),
          num(o, 'concurrency', 'Concurrent', { min: 1, width: '56px' })));
      }
      if (used.has('omnivoice')) {
        const o = v.omniVoice;
        rows.push(h('div.tool-row', h('b', 'OmniVoice'), num(o, 'numStep', 'Steps', { min: 1 }), num(o, 'speed', 'Speed', { step: 0.05 }), num(o, 'guidanceScale', 'Guidance', { step: 0.1 }),
          num(o, 'concurrency', 'Concurrent', { min: 1, width: '56px' })));
      }
    }
    return rows;
  }

  function voicePick(sp) {
    const eng = sp.engine || 'elevenlabs';
    const engSel = h('select', { title: proj.voiceChanger ? 'The Voice Changer only works with ElevenLabs voices' : 'Engine for this speaker' },
      Object.entries(ENGINE_NAMES).map(([k, t]) => h('option', { value: k, selected: k === eng, disabled: proj.voiceChanger && k !== 'elevenlabs' }, t)));
    engSel.addEventListener('change', async () => {
      try { proj.speakers = await rpc('revoice.speaker', { id, key: sp.key, engine: engSel.value, voiceId: '' }); renderAll(); loadVoices(); } catch (e) { fail(e); }
    });
    const list = voiceLists[eng];
    const vSel = h('select', { title: 'The voice that speaks this speaker\'s lines', style: { maxWidth: '220px' } },
      h('option', { value: '' }, list == null ? 'Loading voices…' : list.length ? '— pick a voice —' : eng === 'elevenlabs' ? 'No voices (API key?)' : 'Server offline?'),
      (list || []).map((v) => h('option', { value: v.id, selected: v.id === sp.voiceId }, v.name + (v.sub && v.sub !== 'premade' ? '' : v.sub ? ' · premade' : ''))));
    if (sp.voiceId && !(list || []).some((v) => v.id === sp.voiceId)) {
      // An empty list means the voices could not be loaded, so only a loaded list can say the voice is gone.
      const missing = !!(list && list.length);
      vSel.append(h('option', { value: sp.voiceId, selected: true }, (missing ? '⚠ not found: ' : '') + (sp.voiceName || sp.voiceId)));
      if (missing) { vSel.classList.add('bad-sel'); vSel.title = `"${sp.voiceName || sp.voiceId}" is not in your ${eng === 'elevenlabs' ? 'ElevenLabs account' : 'local server\'s voices'} any more (deleted, renamed, or another API key). Pick another voice.`; }
    }
    if (!sp.voiceId && sp.voicePackId) vSel.append(h('option', { value: '', selected: true }, 'Voice pack: ' + sp.voicePack));
    vSel.addEventListener('change', async () => {
      const v = (list || []).find((x) => x.id === vSel.value);
      try { proj.speakers = await rpc('revoice.speaker', { id, key: sp.key, engine: eng, voiceId: vSel.value, voiceName: v ? v.name : vSel.value }); renderAll(); } catch (e) { fail(e); }
    });
    return h('span.row.voice-cell', { style: { gap: '4px', flexWrap: 'nowrap' } }, engSel, vSel);
  }

  // Per speaker: lines still to make (no take, failed, or text changed) and their characters.
  function speakerStats() {
    const m = new Map();
    for (const l of proj.lines) {
      let st = m.get(l.speaker);
      if (!st) m.set(l.speaker, st = { active: 0, left: 0, chars: 0 });
      if (l.excl || l.gone) continue;
      st.active++;
      if (isMissing(l)) { st.left++; st.chars += l.spoken.length; } else if (isEdited(l)) st.edited = (st.edited || 0) + 1;
    }
    return m;
  }
  const hasVoice = (sp) => !!(sp.voiceId || sp.voicePackId);

  function renderSpeakers() {
    const wrap = h('div.view-body');
    if (!proj.speakers.length) { wrap.append(h('div.empty', h('h2', 'No speakers yet'), h('div', 'Scan first (Setup).'))); return wrap; }
    if (Object.values(voiceLists).every((x) => x == null)) loadVoices();
    const stats = speakerStats();
    const stateOf = (sp) => { const st = stats.get(sp.key) || { active: 0, left: 0 }; return sp.skip ? 3 : !hasVoice(sp) ? 1 : st.left ? 0 : 2; };
    // Unfinished speakers with a voice first, then those that still need a voice, then finished, then skipped.
    const list = proj.speakers.map((sp, i) => ({ sp, i, state: stateOf(sp) })).sort((a, b) => a.state - b.state || a.i - b.i);
    const n = [0, 0, 0, 0];
    for (const x of list) n[x.state]++;
    const charsLeft = list.filter((x) => x.state < 2).reduce((a, x) => a + (stats.get(x.sp.key) || { chars: 0 }).chars, 0);
    const kind = (sp) => sp.key.startsWith('vt:') ? 'shared lines of a voice type' : sp.key.startsWith('nvt:') ? 'new voice type' : '';
    const STATE = [['In progress', ''], ['Needs a voice', 'warn'], ['Finished', 'ok'], ['Skipped', '']];
    const tbl = h('table.tbl.speakers-tbl', { style: { maxWidth: '1180px' } },
      h('thead', h('tr', h('th', 'Speaker'), h('th', 'Voice type'), h('th', { title: 'Lines with a current take, out of the lines to make (excluded lines don\'t count)' }, 'Progress'),
        h('th', { title: 'Characters still to generate ≈ ElevenLabs credits' }, 'Left'), h('th', { title: 'Engine and voice for this speaker' }, 'Voice'), h('th', { title: 'Leave this speaker\'s lines alone (not generated or exported)' }, 'Skip'), h('th', ''))),
      h('tbody', list.map(({ sp, state }) => {
        const st = stats.get(sp.key) || { active: 0, left: 0, chars: 0 };
        const done = st.active - st.left;
        const pct = st.active ? Math.round(100 * done / st.active) : 0;
        return h('tr' + (sp.skip ? '.muted' : '') + (state === 1 ? '.needs-voice' : ''),
          h('td', h('div.row', { style: { gap: '6px' } }, h('b', sp.name), sp.plan ? h('span.badge.b-player', 'new voice type') : null),
            kind(sp) ? h('div.faint', { style: { fontSize: '12px' } }, kind(sp)) : null),
          h('td.mono', sp.voiceType),
          h('td', { title: `${done} of ${st.active} line(s) have a take${st.left ? `; ${st.left} missing` : ''}${st.edited ? `; ${st.edited} edited since their take` : ''}` },
            h('div.row', { style: { gap: '8px', flexWrap: 'nowrap' } }, h('div.pbar' + (state === 2 ? '.full' : ''), h('i', { style: { width: pct + '%' } })), h('span.mono.faint', `${done}/${st.active}`))),
          h('td.mono', state === 2 ? h('span', { style: { color: 'var(--ok)' } }, '✓') : st.chars.toLocaleString()),
          h('td', voicePick(sp), state === 1 ? h('div', { style: { color: 'var(--warn)', fontSize: '12px', marginTop: '2px' } }, 'Needs a voice') : null),
          h('td', switchEl(sp.skip, async (v) => { try { proj.speakers = await rpc('revoice.speaker', { id, key: sp.key, skip: v }); renderAll(); } catch (e) { fail(e); } }, '', { title: 'Skip this speaker' })),
          h('td', h('button.btn.sm.ghost', { title: 'Show this speaker\'s lines', onclick: () => { setSpeaker(sp.key); filter = 'all'; setTab('lines'); } }, 'Lines →')));
      })));
    wrap.append(
      h('div.help', { style: { maxWidth: '1180px', marginBottom: '12px' } },
        h('p', h('b', 'Each speaker gets its own voice. '), 'Pick the engine and a voice from your ElevenLabs account or your local VoxCPM / OmniVoice server. A speaker is one NPC when a line belongs to a single character, or a voice type when several NPCs share the line (generic lines). Two NPCs sharing a vanilla voice type can still get different voices for their own lines; only the lines they share have to share a voice.')),
      voiceSettings(),
      h('div.row.speaker-totals', { style: { maxWidth: '1180px', marginBottom: '8px', gap: '14px' } },
        h('b', `${list.length} speaker${list.length === 1 ? '' : 's'}`),
        n[0] ? h('span', `${n[0]} in progress`) : null,
        n[1] ? h('span', { style: { color: 'var(--warn)' } }, `${n[1]} need${n[1] === 1 ? 's' : ''} a voice`) : null,
        n[2] ? h('span', { style: { color: 'var(--ok)' } }, `${n[2]} finished`) : null,
        n[3] ? h('span.faint', `${n[3]} skipped`) : null,
        h('div.spacer'),
        h('span.faint', { title: 'Characters still to generate for speakers that are not skipped ≈ ElevenLabs credits' }, `${charsLeft.toLocaleString()} characters left`)),
      tbl);
    return wrap;
  }

  // ---------------------------------------------------------------- lines
  const left = h('div.left');
  const insp = h('div.inspector');
  const filters = h('div.filters');
  const tagbar = h('div.tagbar');
  const vbar = h('div.toolbar');
  const actions = h('div.statusbar');
  const reviewBar = h('div.review-bar.hidden');
  let review = false, reviewAutoplay = store.get('reviewAutoplay', true);
  let lastEdit = null; // { el, line }: the last spoken-text box the user was in
  const grid = new VGrid(left, {
    storageKey: 'revoice',
    rowKey: (l) => l.id,
    rowClass: (l) => [l.gone ? 'muted' : '', l.excl ? 'excluded' : ''].filter(Boolean).join(' '),
    onRowClick: (l) => { focusedId = l.id; renderInspector(); },
    onRowDblClick: (l) => play(l),
    columns: [
      { id: 'sel', title: '', width: 32, resizable: false, render: (l) => grid.checkbox(l) },
      { id: 'excl', title: '', width: 30, resizable: false, render: (l) => h('button.act-btn.excl-btn' + (l.excl ? '.on.bad' : ''), { title: l.excl ? 'Excluded - click to clear (won\'t generate/export)' : 'Exclude this line (won\'t generate/export)', onclick: (e) => { e.stopPropagation(); upd(l, l.excl ? 'include' : 'exclude'); } }, icon('ban', { size: 14 })) },
      { id: 'speaker', title: 'Speaker', width: 170, render: (l) => h('span.ellipsis', { title: `${l.speakerName}${l.npcs ? '\nNPCs: ' + l.npcs : ''}\n${CERT[l.cert] || l.cert}${l.via ? ' (' + l.via + ')' : ''}` }, l.cert !== 'Exact' ? h('span.mark.warn', { title: CERT[l.cert] }, '? ') : null, l.speakerName) },
      { id: 'vt', title: 'Voice type', width: 140, render: (l) => h('span.mono.faint.ellipsis', { title: `Sound\\Voice\\${l.pf}\\${l.vt}\\${l.stem}.fuz` }, l.vt) },
      { id: 'orig', title: 'Now', width: 54, resizable: false, render: (l) => l.original ? h('button.act-btn', { title: 'Play the file the game uses now: ' + l.original, onclick: (e) => { e.stopPropagation(); playOriginal(l); } }, icon('play')) : h('span.faint', { title: 'No voice file for this line yet (silent in game)' }, '—') },
      { id: 'text', title: 'Subtitle (in game)', flex: 1, width: 260, render: (l) => h('span.ellipsis.dim', { title: l.text }, l.text) },
      { id: 'spoken', title: 'Spoken text (tags allowed)', flex: 1.2, width: 300, render: (l) => spokenCell(l) },
      { id: 'emo', title: 'Emotion', width: 90, render: (l) => l.emo === 'Neutral' ? h('span.faint', '—') : h('span.faint', { title: `${l.emo} ${l.emoV}` }, `${l.emo} ${l.emoV}`) },
      { id: 'take', title: 'Take', width: 64, resizable: false, render: (l) => h('span.row', { style: { gap: '6px' } }, statusDot(l),
        h('span.take-lbl' + (l.takes.length ? '.click' : ''), { title: l.takes.length ? `${l.takes.length} take(s). Click to play, switch or delete takes.` : 'No take yet', onclick: (e) => { if (l.takes.length) { e.stopPropagation(); takesModal(l); } } }, l.takes.length ? '#' + ((l.takes.find((t) => t.id === l.sel) || l.takes[0]).slot) + (l.takes.length > 1 ? '/' + l.takes.length : '') : '—')) },
      { id: 'act', title: '', width: 146, resizable: false, render: (l) => h('span.row.icon-bar', { style: { gap: '2px' } },
        h('button.act-btn' + (inFlight.has(l.id) ? '.spinning' : ''), { title: inFlight.has(l.id) ? 'Making a new take…' : genBlock(l) || 'Regenerate (a new take; older takes stay)', disabled: !!genBlock(l), onclick: (e) => { e.stopPropagation(); generate('selected', [l.id], { autoplay: true }); } }, icon('regen')),
        h('button.act-btn', { title: l.takes.length ? 'Open this line\'s takes folder' : 'No takes yet', disabled: !l.takes.length, onclick: (e) => { e.stopPropagation(); rpc('revoice.openTakeFolder', { id, lineId: l.id }).catch(fail); } }, icon('folder')),
        h('button.act-btn.flag-btn' + (l.marked ? '.on.warn' : ''), { title: l.marked ? 'Flagged to revisit - click to clear' : 'Flag to revisit', onclick: (e) => { e.stopPropagation(); upd(l, 'mark'); } }, icon('flag')),
        h('button.act-btn', { title: l.sel ? 'Play the new take (or double-click the row)' : 'No take yet: generate the line first', disabled: !l.sel, onclick: (e) => { e.stopPropagation(); play(l); } }, icon('play'))) },
    ],
  });
  grid.el.addEventListener('selection', () => renderActions());

  function genBlock(l) {
    if (app.job) return 'A job is running (see the top bar)';
    if (l.excl) return 'Excluded';
    if (l.gone) return 'No longer in the plugins';
    const sp = proj.speakers.find((x) => x.key === l.speaker);
    if (sp && sp.skip) return 'This speaker is skipped (Speakers tab)';
    if (!sp || !(sp.voiceId || sp.voicePackId)) return 'Pick a voice for ' + l.speakerName + ' (Speakers tab)';
    if (proj.voiceChanger && sp.voiceId && sp.engine !== 'elevenlabs') return 'Voice Changer needs an ElevenLabs voice for ' + l.speakerName;
    if (proj.voiceChanger && !l.original) return 'Voice Changer needs a current voice file for this line';
    return '';
  }

  const statusDot = (l) => h('span.dot.' + (l.status || 'Pending'), { title: (l.status || 'Pending') + (l.err ? ': ' + l.err : '') });

  function spokenCell(l) {
    if (l.tag) {
      return h('span.row', { style: { width: '100%' } },
        h('span.grow.ellipsis', { title: l.tag }, diffAdded(l.spoken, l.tag)),
        h('button.icon-btn', { title: 'Accept the suggested tag. To approve all shown [Tags], hold Shift when clicking.', onclick: (e) => { e.stopPropagation(); tagAll(e, l, 'acceptTags'); } }, '✓'),
        h('button.icon-btn', { title: 'Undo the suggested tag. To undo all shown [Tags], hold Shift when clicking.', onclick: (e) => { e.stopPropagation(); tagAll(e, l, 'rejectTags'); } }, '✕'));
    }
    const inp = h('input', { type: 'text', value: l.spoken, disabled: l.excl });
    if (l.spokenEdited) { inp.style.color = 'var(--link)'; inp.title = 'Blue: differs from the subtitle. The arrow on the left restores it.'; }
    inp.addEventListener('change', () => { if (inp.value !== l.spoken) upd(l, 'spoken', inp.value); });
    inp.addEventListener('keydown', (e) => { e.stopPropagation(); if (e.key === 'Enter') inp.blur(); if (e.key === 'Escape') { inp.value = l.spoken; inp.blur(); } });
    inp.addEventListener('focus', () => { lastEdit = { el: inp, line: l }; if (focusedId !== l.id) { focusedId = l.id; grid.focus(l.id); renderInspector(); } });
    if (l.spokenEdited) return h('span.spoken-wrap', h('button.act-btn', { title: 'Restore the subtitle: ' + l.text, onclick: (e) => { e.stopPropagation(); upd(l, 'resetSpoken'); } }, icon('restore', { size: 14 })), inp);
    if (l.undo) return h('span.spoken-wrap', h('button.act-btn', { title: 'Undo the restore (bring your edit back)', onclick: (e) => { e.stopPropagation(); upd(l, 'undoReset'); } }, icon('undo', { size: 14 })), inp);
    return inp;
  }

  function counts() {
    const L = proj.lines.filter((l) => !speakerFilter || l.speaker === speakerFilter);
    return {
      all: L.length,
      todo: L.filter((l) => !l.excl && !l.gone && isMissing(l)).length,
      edited: L.filter((l) => !l.excl && !l.gone && isEdited(l)).length,
      done: L.filter((l) => l.sel).length,
      failed: L.filter((l) => l.status === 'Failed').length,
      orig: L.filter((l) => l.original).length,
      silent: L.filter((l) => !l.original).length,
      unsure: L.filter((l) => l.cert !== 'Exact').length,
      marked: L.filter((l) => l.marked).length,
      approved: L.filter((l) => l.approved).length,
      qa: L.filter((l) => qaFlagged(l)).length,
      excl: L.filter((l) => l.excl).length,
    };
  }
  function visible() {
    const q = search.toLowerCase();
    return proj.lines.filter((l) => (!speakerFilter || l.speaker === speakerFilter)
      && (filter === 'all' || (filter === 'todo' && !l.excl && !l.gone && isMissing(l)) || (filter === 'edited' && !l.excl && !l.gone && isEdited(l)) || (filter === 'done' && l.sel) || (filter === 'failed' && l.status === 'Failed')
        || (filter === 'orig' && l.original) || (filter === 'silent' && !l.original) || (filter === 'unsure' && l.cert !== 'Exact') || (filter === 'marked' && l.marked) || (filter === 'approved' && l.approved) || (filter === 'qa' && qaFlagged(l)) || (filter === 'excl' && l.excl))
      && (!q || l.text.toLowerCase().includes(q) || l.spoken.toLowerCase().includes(q) || (l.stem || '').toLowerCase().includes(q) || l.speakerName.toLowerCase().includes(q) || (l.quest || '').toLowerCase().includes(q)));
  }
  function renderFilters() {
    const c = counts();
    // Like Phase 2: a filter only shows when it has lines (All and the active one always show).
    const chip = (k, label, tip) => (k === 'all' || filter === k || c[k] ? h('button.chip' + (filter === k ? '.on' : ''), { title: tip, onclick: () => { filter = k; renderFilters(); grid.setRows(visible()); } }, label, ' ', h('span.n', String(c[k]))) : null);
    clear(filters).append(
      chip('all', 'All', 'Every voice file to make'), chip('todo', 'Missing', 'No take yet'), chip('edited', 'Edited since take', 'Has a take, but the spoken text changed since (or the last attempt failed)'), chip('done', 'Generated', 'Has a take'),
      chip('failed', 'Failed', 'The last attempt failed'), chip('orig', 'Has current file', 'The game plays a file for this line now (▶ in the Now column)'),
      chip('silent', 'Silent now', 'No voice file yet: subtitles only in game'), chip('unsure', 'Speaker unsure', 'The speaker was worked out as probable or unknown — check before generating'),
      chip('marked', 'Flagged', 'Flagged to revisit'), chip('qa', 'QA flagged', 'Check takes flagged the take in use: words differ, too quiet or clipping'), chip('approved', 'Approved', 'Approved in Review'), chip('excl', 'Excluded', 'Left out of generation and export'),
      h('div.spacer'), folderButton());
  }
  // Search, Columns and Hide panel sit on the tag row (as in Phase 2); Open folder sits on the filter row.
  function viewTools() {
    const q = h('input', { type: 'text', placeholder: 'Search text, file, speaker, quest…', value: search, title: 'Filter the list' });
    q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visible()); }, 200));
    return [q, columnsMenu(grid),
      h('button.btn.sm', { title: 'Show or hide the line panel', onclick: () => { showInsp = !showInsp; store.set('rv-insp', showInsp); renderAll(); } }, showInsp ? 'Hide panel ▸' : '◂ Show panel')];
  }
  function findReplace() {
    findReplaceDialog({ pool: grid.rows.filter((l) => !l.gone), idOf: (l) => l.id, textOf: (l) => l.spoken, tags: proj.voice.tags || [],
      apply: async (items) => {
        try { const r = await rpc('revoice.replaceTexts', { id, items }); for (const nl of r.lines) replaceLine(nl, false); grid.setRows(visible()); renderFilters(); renderActions(); renderInspector(); } catch (e) { fail(e); }
      } });
  }

  function renderInspector() {
    clear(insp);
    const l = focusedId && index.get(focusedId);
    if (!l) {
      insp.append(h('h3', 'Line'), h('div.dim', 'Click a row to see it in full. Double-click plays the new take.'),
        h('div.help', h('p', h('b', 'Now'), ' plays the file the game uses today. ', h('b', 'Spoken text'), ' is what the new voice says; the subtitle stays as it is.'),
          h('p', 'The file name and folder come from the plugin, exactly as the game looks for them, so exported files replace the current ones (loose files beat archives).')));
      return;
    }
    const spoken = h('textarea.autogrow', { rows: 2, disabled: l.excl, title: 'What the voice says. Edit freely and put [tags] anywhere' }, l.spoken);
    const grow = () => { spoken.style.height = 'auto'; spoken.style.height = Math.min(220, spoken.scrollHeight + 2) + 'px'; };
    spoken.addEventListener('input', grow);
    requestAnimationFrame(grow);
    spoken.addEventListener('change', () => upd(l, 'spoken', spoken.value));
    spoken.addEventListener('focus', () => { lastEdit = { el: spoken, line: l }; });
    const path = `Sound\\Voice\\${l.pf}\\${l.vt}\\${l.stem}.fuz`;
    const kv = [];
    // Short values sit two to a row; long ones (how the speaker was found, who, the plugin) take a whole row.
    const add = (k, v, cls = '', wide = false) => { if (v != null && v !== '') kv.push(h('div' + (wide ? '.k-wide' : ''), k), h('div.kv-v' + cls + (wide ? '.v-wide' : ''), { title: String(v) }, v)); };
    const who = l.npcs && l.npcs !== l.speakerName && !(l.npcs.startsWith(l.speakerName + ' (') && !l.npcs.includes(',')) ? l.npcs : '';
    add('Found by', `${CERT[l.cert] || l.cert}${l.via ? ' · ' + l.via : ''}`, '', true);
    add('Who', who, '', true);
    add('Quest', l.quest, '.mono');
    add('Topic', l.topic, '.mono');
    add('Emotion', `${l.emo} ${l.emoV}`);
    add('Voice type', l.vt, '.mono');
    add('Plugin', l.plugin, '.mono', true);
    const meta = h('details.more-meta', h('summary', h('b', l.speakerName), l.cert !== 'Exact' ? h('span', { style: { color: 'var(--warn)' }, title: CERT[l.cert] }, ' ?') : null, h('span.faint', ` · ${l.emo === 'Neutral' ? 'neutral' : l.emo + ' ' + l.emoV}`)),
      h('div.kv2', { style: { marginTop: '6px' } }, kv),
      l.notes ? h('div', { style: { marginTop: '6px', fontSize: '12.5px' } }, h('span.faint', 'Actor notes: '), l.notes) : null);
    meta.open = store.get('rv-meta-open', true);
    meta.addEventListener('toggle', () => store.set('rv-meta-open', meta.open));
    const status = l.status || 'Pending';
    const STATUS_TEXT = { Pending: 'Not generated', Generating: 'Generating…', Generated: 'Generated', Failed: 'Failed', Stale: 'Edited since take' };
    const vcOn = proj.voiceChanger;
    const sp = proj.speakers.find((x) => x.key === l.speaker);
    const eleven = !sp || !sp.voiceId || (sp.engine || 'elevenlabs') === 'elevenlabs';
    insp.append(
      h('div.row', { title: l.err || '' }, statusDot(l), h('span.dim', l.gone ? 'No longer in the plugins' : STATUS_TEXT[status] || status), h('div.spacer'),
        h('button.act-btn' + (l.excl ? '.on.bad' : ''), { title: l.excl ? 'Excluded - click to clear' : 'Exclude from generate and export', onclick: () => upd(l, l.excl ? 'include' : 'exclude') }, icon('ban')),
        h('button.act-btn' + (l.marked ? '.on.warn' : ''), { title: l.marked ? 'Flagged to revisit - click to clear' : 'Flag to revisit', onclick: () => upd(l, 'mark') }, icon('flag')),
        h('button.act-btn' + (l.approved ? '.on' : ''), { title: !l.takes.length ? 'No take to approve yet' : l.approved ? 'Approved - click to clear' : 'Approve: heard and accepted (A in Review)', disabled: !l.takes.length, onclick: () => upd(l, 'approve', !l.approved) }, icon('check'))),
      l.err && status === 'Failed' ? h('div.banner.bad', { style: { margin: 0 } }, l.err) : null,
      meta,
      h('div', h('div.field-label', 'File'), h('div.file-row', h('span.mono.ellipsis', { title: path }, l.stem + '.fuz'),
        h('button.icon-btn', { title: 'Copy the file name (hover the name for the full game path)', onclick: () => navigator.clipboard.writeText(l.stem).then(() => toast('Copied.')) }, '⧉'))),
      h('div.row', { style: { gap: '8px' } },
        l.original ? h('button.btn.sm', { title: 'Play the file the game uses now: ' + l.original, onclick: () => playOriginal(l) }, '▶ Now') : h('span.faint', { title: 'No voice file for this line yet: subtitles only in game' }, 'Silent in game now'),
        h('div.spacer')),
      h('div', h('div.field-label', 'Subtitle (in game)'), h('div.box.ellipsis', { title: l.text }, l.text)),
      h('div', h('div.field-label', 'Spoken text (tags allowed)'), spoken,
        h('div.row', { style: { marginTop: '6px', gap: '6px' } },
          vcOn || !eleven ? null : h('button.btn.sm', { title: 'Let the AI suggest audio tags for this line', disabled: l.excl, onclick: () => autotag([l.id]) }, 'AI tag'),
          l.spokenEdited ? h('button.btn.sm.ghost', { title: 'Use the subtitle as the spoken text again', onclick: () => upd(l, 'resetSpoken') }, '↺ Restore') : null,
          h('div.spacer'),
          h('button.btn.sm', { title: app.job ? 'A job is running' : l.excl ? 'Excluded' : 'Record this line with your microphone, then keep it as it is or convert it into the speaker\'s voice', disabled: l.excl || l.gone || !!app.job, onclick: () => record(l) }, '🎙 Record'),
          h('button.btn.sm.primary', { title: genBlock(l) || 'Make a new take of this line', disabled: !!genBlock(l), onclick: () => generate('selected', [l.id], { autoplay: true }) }, l.takes.length ? 'New take' : 'Generate'))),
      l.takes.length ? h('div', h('div.field-label', `Takes (${l.takes.length})`), h('div.col', { style: { gap: '4px' } }, l.takes.slice().reverse().map((t) => h('div.take.compact' + (t.id === l.sel ? '.sel' : ''),
        h('button.icon-btn', { title: 'Play this take', onclick: () => play(l, t) }, '▶'),
        h('div.grow', { style: { minWidth: 0 } },
          h('div.row', { style: { gap: '6px', flexWrap: 'nowrap' } }, h('b', '#' + t.slot), t.id === l.sel ? h('span.faint', { title: 'This take is exported' }, 'used') : null,
            t.qa ? h('span', { title: qaText(t.qa), style: { color: t.qa.flags.length ? 'var(--warn)' : 'var(--ok)' } }, t.qa.flags.length ? '⚠ ' + t.qa.flags.map((f) => QA_WHY[f] || f).join(', ') : '✓') : null,
            h('span.meta.ellipsis', { title: `${t.engine || ''} ${t.model || ''}` }, `${t.durationSeconds.toFixed(1)} s · ${t.model || t.engine || ''}`)),
          t.spokenText && t.spokenText !== l.spoken ? h('div.meta.ellipsis', { title: t.spokenText }, 'Text then: ' + t.spokenText) : null),
        t.id !== l.sel ? h('button.btn.sm', { title: 'Use this take for export', onclick: () => upd(l, 'selectTake', t.id) }, 'Use') : null,
        h('button.icon-btn', { title: 'Delete this take and its files', onclick: () => upd(l, 'deleteTake', t.id) }, '🗑'))))) : null);
  }

  function renderActions() {
    const c = counts();
    const sel = [...grid.selected];
    const bulkBtn = h('button.btn.sm.ghost', { title: sel.length ? `Actions for the ${sel.length} ticked row(s)` : 'Tick rows first' }, `Ticked (${sel.length}) ▾`);
    bulkBtn.addEventListener('click', () => {
      if (!sel.length) { toast('Tick the box of one or more rows first.', true); return; }
      popupMenu(bulkBtn, [
        { label: 'Accept suggested tags', sub: 'Use the emotion or AI tag suggestion on each ticked line', onclick: () => bulk('acceptTags', sel) },
        { label: 'Undo suggested tags', onclick: () => bulk('rejectTags', sel) },
        { label: 'Exclude', sub: 'Leave out of generation and export', onclick: () => bulk('exclude', sel) },
        { label: 'Include', onclick: () => bulk('include', sel) },
        { label: 'Flag to revisit', onclick: () => bulk('markOn', sel) },
        { label: 'Clear flags', onclick: () => bulk('markOff', sel) },
        { label: 'Keep only the used take', sub: 'Deletes the other takes and their files', onclick: () => bulk('keepUsedTake', sel) },
        { label: 'Untick all', onclick: () => { grid.selected.clear(); grid.paint(true); renderActions(); } },
      ]);
    });
    // The counts follow the speaker picked in the toolbar, exactly as the batch does.
    const L = proj.lines.filter((l) => !speakerFilter || l.speaker === speakerFilter);
    const can = (l) => { const b = genBlock(l); return !b || b.startsWith('A job'); };
    const chars = (a) => a.reduce((n, l) => n + l.spoken.length, 0);
    const miss = L.filter((l) => can(l) && isMissing(l));
    const edl = L.filter((l) => can(l) && isEdited(l));
    const tickedL = sel.map((x) => index.get(x)).filter((l) => l && can(l));
    const flagged = L.filter((l) => l.marked && can(l));
    const unsure = L.filter((l) => l.cert !== 'Exact' && can(l));
    const all = L.filter(can);
    const who = speakerFilter ? (proj.speakers.find((x) => x.key === speakerFilter) || {}).name : '';
    const genBtn = generateButton({
      running: app.job && app.job.kind === 'generate', label: proj.voiceChanger ? 'Convert options' : 'Generate options',
      title: (who ? `Only ${who}'s lines (the speaker picked in the toolbar). ` : '') + 'Choose what to generate. You see the count, characters and every setting before any credits are spent.',
      items: () => [
        { key: 'missing', label: 'Missing', count: miss.length, chars: chars(miss), desc: 'Lines without any take yet. A line with a take is never missing.' },
        { key: 'edited', label: 'Edited', count: edl.length, chars: chars(edl), desc: 'A new take for each line whose spoken text changed since its take (tags added, words edited), or whose last attempt failed.' },
        { key: 'ticked', label: 'Ticked', count: tickedL.length, chars: chars(tickedL), desc: 'A new take for each ticked line.' },
        { key: 'flagged', label: 'Flagged', count: flagged.length, chars: chars(flagged), desc: 'A new take for each line you flagged to revisit.' },
        { key: 'unsure', label: 'Speaker unsure', count: unsure.length, chars: chars(unsure), desc: 'Lines whose speaker was worked out as probable or unknown. Check the speaker first: a wrong speaker means a wrong voice.' },
        { key: 'all', label: who ? `All of ${who}'s lines` : 'All lines', count: all.length, chars: chars(all), warn: true, desc: 'A new take for every line of speakers with a voice (not excluded or skipped). Uses the most credits; earlier takes stay.' },
      ],
      onPick: (k) => k === 'missing' ? generate('missing') : k === 'edited' ? generate('edited') : k === 'flagged' ? generate('marked') : k === 'all' ? generate('all')
        : generate('selected', (k === 'ticked' ? tickedL : unsure).map((l) => l.id)),
    });
    clear(actions).append(
      helpButton('lines'),
      h('span.stat', h('b', String(c.done)), ` / ${c.all} generated`, c.failed ? h('span', { style: { color: 'var(--bad)' } }, ` · ${c.failed} failed`) : '', ` · ${c.silent} silent now`, who ? h('span.faint', ` · ${who} only`) : ''),
      h('div.spacer'),
      sel.length ? bulkBtn : null,
      h('button.btn' + (review ? '.review-on' : ''), { title: review ? 'End Review (Esc)' : 'Listen through the lines with the keyboard: Space plays, A approves and moves on, R makes a new take, F flags', onclick: () => setReview(!review) }, review ? '■ End review' : '▶ Review'),
      h('button.btn', { disabled: !!app.job, title: `Check takes${sel.length ? ` of the ${sel.length} ticked line(s)` : ' of every line shown'}: Whisper compares what is said with the line, plus loudness and clipping. Flags only, deletes nothing.`, onclick: () => qaRun(sel.length ? sel : grid.rows.filter((l) => l.takes.length && !l.excl).map((l) => l.id)) }, 'Check takes'),
      genBtn);
  }

  // ---- Review, Check takes and recording (as in Phase 2)
  function qaText(q) { return q ? [q.score != null ? `Words match ${(q.score * 100).toFixed(0)}%` : null, q.heard ? `Heard: "${q.heard}"` : null,
    q.lufs != null ? `Loudness ${q.lufs.toFixed(1)} LUFS` : null, q.truePeak != null ? `Peak ${q.truePeak.toFixed(1)} dBTP` : null].filter(Boolean).join('\n') : ''; }
  var QA_WHY = { words: 'words differ', quiet: 'too quiet', clipping: 'clipping', error: 'not checked' };
  function qaFlagged(l) { const t = l.takes.find((x) => x.id === l.sel); return !!(t && t.qa && t.qa.flags.length); }
  async function record(l) {
    if (app.job) { toast('Another task is still running (see the bar at the top). Wait for it to finish, or press Cancel there.', true); return; }
    await recordTake({ patchId: id, line: l, kind: 'revoice', onSaved: (nl) => { replaceLine(nl); play(nl); } });
  }
  async function qaRun(ids) {
    const n = ids.length;
    if (!n) { toast('No takes to check yet.', true); return; }
    const ok = await modal('Check takes', [h('div.help',
      h('p', `Whisper listens to the take in use of ${n} line(s) and compares what it hears with the spoken text. Loudness and peak are measured too.`),
      h('p', 'A take is flagged when the words match less than 75 % (wrong line, cut off, extra words), when it is very quiet, or when it clips. Nothing is deleted or regenerated: use the "QA flagged" filter to listen and decide.'),
      h('p.faint', 'Uses the Whisper settings of the WAV to LIP tool. The first run downloads the model.'))],
    [{ label: 'Cancel', value: false }, { label: `Check ${n} take(s)`, primary: true, value: true }], { help: 'checking-takes' });
    if (!ok) return;
    try { await rpc('revoice.qa', { id, lineIds: ids }); renderActions(); } catch (e) { fail(e); }
  }
  function setReview(on) {
    review = on;
    reviewBar.classList.toggle('hidden', !on);
    if (on) {
      if (!focusedId || !grid.rows.some((r) => r.id === focusedId)) { const first = grid.rows.find((r) => r.takes.length); if (first) { focusedId = first.id; grid.focus(first.id, true); renderInspector(); } }
      renderReviewBar();
      grid.el.focus();
    }
    renderActions();
  }
  function renderReviewBar() {
    const k = (x) => h('kbd', x);
    const L = grid.rows;
    clear(reviewBar).append(h('b', 'Review'),
      h('span', k('Space'), ' play / stop'), h('span', k('↑'), k('↓'), ' move'), h('span', k('A'), ' approve + next'), h('span', k('R'), ' new take'), h('span', k('F'), ' flag'), h('span', k('Esc'), ' end'),
      h('div.spacer'),
      h('span.faint', `${L.filter((l) => l.approved).length} of ${L.filter((l) => l.takes.length).length} approved`),
      switchEl(reviewAutoplay, (v) => { reviewAutoplay = v; store.set('reviewAutoplay', v); }, 'Autoplay', { title: 'Play each line as you land on it' }),
      helpButton('review'));
  }
  function moveFocus(step) {
    const i = grid.rows.findIndex((r) => r.id === focusedId);
    const next = grid.rows[Math.max(0, Math.min(grid.rows.length - 1, i + step))];
    if (!next) return;
    focusedId = next.id;
    grid.focus(next.id, true);
    renderInspector();
    if (reviewAutoplay) play(next);
  }
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
    else if (key === 'r') { eat(); if (l && !genBlock(l)) generate('selected', [l.id], { autoplay: true }); }
    else if (key === 'f') { eat(); if (l) upd(l, 'mark'); }
    else if (key === 'Escape') { eat(); setReview(false); }
  };
  window.addEventListener('keydown', reviewKeys, true);

  // ---------------------------------------------------------------- toolbar above the lines (laid out like Phase 2's)
  let creditsEl = null;
  function renderVbar() {
    const v = proj.voice;
    const set = (obj, k, val) => { obj[k] = val; voiceEdits++; saveVoiceSettings(); renderVbar(); };
    const grp = (label, ...kids) => h('div.tgroup', { title: TOOL_TIPS[label] || '' }, h('label', label), h('div.row', ...kids));
    const slider = (obj, k, min, max, step, label) => {
      const val = h('span.mono.dim', { style: { width: '34px' } }, Number(obj[k]).toFixed(2));
      const r = h('input', { type: 'range', min, max, step, value: obj[k], style: { width: '80px' } });
      r.addEventListener('input', () => { val.textContent = Number(r.value).toFixed(2); });
      r.addEventListener('change', () => set(obj, k, Number(r.value)));
      return grp(label, r, val);
    };
    const num = (obj, k, label, o = {}) => {
      const i = h('input', { type: 'number', value: obj[k] ?? '', placeholder: o.placeholder || '', step: o.step || 'any', min: o.min, title: o.title || '', style: { width: o.width || '76px' } });
      i.addEventListener('change', () => set(obj, k, i.value === '' ? (o.nullable ? null : 0) : Number(i.value)));
      return grp(label, i);
    };
    // Speaker: filters the lines; with one speaker picked, its engine and voice can be changed right here.
    const spSel = h('select', { title: 'Show one speaker\'s lines (and pick its voice here)', style: { maxWidth: '200px' } }, h('option', { value: '' }, `All speakers (${proj.speakers.length})`),
      proj.speakers.map((sp) => h('option', { value: sp.key, selected: sp.key === speakerFilter }, `${sp.name} (${sp.lines})`)));
    spSel.addEventListener('change', () => { setSpeaker(spSel.value); renderVbar(); renderFilters(); grid.setRows(visible()); renderActions(); });
    const sp = speakerFilter && proj.speakers.find((x) => x.key === speakerFilter);
    const voiceGrp = sp ? grp('Engine · voice', voicePick(sp), h('button.icon-btn', { title: 'Reload voices and models', onclick: () => loadVoices(true) }, '⟳'))
      : grp('Voice', h('button.btn.sm.bordered', { title: 'Each speaker has their own voice. Pick a speaker in the dropdown to the left to set theirs here, or set them all in the Speakers tab.', onclick: () => {
        setTab('speakers');
        const t = body.querySelector('.speakers-tbl');
        if (t) { t.classList.add('flash-hl'); setTimeout(() => t.classList.remove('flash-hl'), 2600); }
      } }, 'Set in Speakers'));
    const kids = [grp('Speaker', spSel), voiceGrp];
    // Format, seed and concurrency are set once and rarely touched: they live behind one button.
    const outputGroup = (fields, o) => {
      const btn = h('button.btn.sm', { title: `Output: ${o.outputFormat ? o.outputFormat + ' · ' : ''}${o.seed == null || o.seed === '' ? 'random seed' : 'seed ' + o.seed} · ${o.concurrency || 1} at once. Click to change.` }, `${({ mp3_44100_128: 'MP3 128', mp3_44100_192: 'MP3 192', pcm_44100: 'PCM 44.1k' })[o.outputFormat] || 'Seed, concurrent'} ▾`);
      btn.onclick = () => popover(btn, fields);
      return grp('Output', btn);
    };
    if (proj.voiceChanger) {
      const st = v.sts;
      const ms = h('select', { onchange: (e) => set(st, 'modelId', e.target.value) }, (elevenModels && elevenModels.length ? elevenModels : [{ modelId: st.modelId }]).map((m) => h('option', { value: m.modelId, selected: m.modelId === st.modelId }, m.modelId)));
      kids.push(grp('Model', ms), slider(st, 'stability', 0, 1, 0.01, 'Stability'), slider(st, 'similarity', 0, 1, 0.01, 'Similarity'), slider(st, 'style', 0, 1, 0.01, 'Style'),
        grp('Remove background noise', switchEl(st.removeBackgroundNoise, (x) => set(st, 'removeBackgroundNoise', x), '', { title: 'Strip background noise from the original before converting' })),
        outputGroup(() => [num(st, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px' }), num(st, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'Your plan\'s concurrent request limit' })], st));
    } else {
      const used = new Set(proj.speakers.filter((x) => x.voiceId && !x.skip).map((x) => x.engine || 'elevenlabs'));
      const eng = sp ? (sp.engine || 'elevenlabs') : (used.size === 1 ? [...used][0] : 'elevenlabs');
      if (eng === 'elevenlabs') {
        const e = v.eleven;
        const tag = /^eleven_v[34]/i.test(e.modelId || '');
        const mi = (elevenModels || []).find((x) => x.modelId === e.modelId) || {};
        const style = mi.canUseStyle != null ? mi.canUseStyle && !tag : !tag;
        const boost = mi.canUseSpeakerBoost != null ? mi.canUseSpeakerBoost && !tag : !tag;
        const langOk = !/multilingual_v2|monolingual_v1|english_v1/.test(e.modelId || '');
        const ms = h('select', { onchange: (ev) => set(e, 'modelId', ev.target.value) }, (elevenModels && elevenModels.length ? elevenModels : [{ modelId: e.modelId }]).map((x) => h('option', { value: x.modelId, selected: x.modelId === e.modelId }, x.modelId + (/^eleven_v[34]/i.test(x.modelId) ? ' · tags' : ''))));
        const stabVal = e.stability < 0.25 ? 0 : e.stability < 0.75 ? 0.5 : 1;
        const lang = h('select', { disabled: !langOk, title: langOk ? 'Force the language (ISO 639-1). Auto lets the model detect it.' : 'This model does not accept a language code.', onchange: (ev) => set(e, 'languageCode', ev.target.value) },
          LANGS.map(([k, t]) => h('option', { value: k, selected: k === (e.languageCode || '') }, t)));
        const norm = h('select', { title: 'Text normalization: whether ElevenLabs writes out numbers, dates and abbreviations before speaking. Auto lets the model decide; On is safer for numbers; Off keeps the text exactly as written.', onchange: (ev) => set(e, 'textNormalization', ev.target.value) },
          [['auto', 'Auto'], ['on', 'On'], ['off', 'Off']].map(([k, t]) => h('option', { value: k, selected: k === (e.textNormalization || 'auto') }, t)));
        const fmt = h('select', { onchange: (ev) => set(e, 'outputFormat', ev.target.value) }, outputFormats(e.outputFormat).map(([k, t]) => h('option', { value: k, selected: k === e.outputFormat }, t)));
        kids.push(grp('Model', ms),
          tag ? grp('Stability', segmented([[0, 'Creative'], [0.5, 'Natural'], [1, 'Robust']], stabVal, (x) => set(e, 'stability', Number(x)))) : slider(e, 'stability', 0, 1, 0.01, 'Stability'),
          slider(e, 'similarity', 0, 1, 0.01, 'Similarity'),
          style ? slider(e, 'style', 0, 1, 0.01, 'Style') : null,
          boost ? grp('Speaker boost', switchEl(e.speakerBoost, (x) => set(e, 'speakerBoost', x), '', { title: 'Closer to the voice; can clip or sound harsh' })) : null,
          slider(e, 'speed', 0.7, 1.2, 0.01, 'Speed'),
          grp('Language', lang), grp('Read numbers ⓘ', norm),
          outputGroup(() => [grp('Format', fmt),
            num(e, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px', title: 'Same seed + same text + same settings ≈ same take. Empty = random.' }),
            num(e, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'How many lines are requested at once. Match your ElevenLabs plan\'s concurrency limit.' })], e));
      } else if (eng === 'voxcpm') {
        const o = v.voxCpm;
        kids.push(num(o, 'cfgValue', 'CFG', { step: 0.1 }), num(o, 'inferenceTimesteps', 'Timesteps', { min: 1 }),
          grp('Normalize', switchEl(o.normalize, (x) => set(o, 'normalize', x), '')), grp('Retry bad cases', switchEl(o.retryBadcase, (x) => set(o, 'retryBadcase', x), '')),
          num(o, 'concurrency', 'Concurrent', { min: 1, width: '58px' }));
      } else {
        const o = v.omniVoice;
        kids.push(num(o, 'numStep', 'Steps', { min: 1 }), num(o, 'speed', 'Speed', { step: 0.05 }), num(o, 'guidanceScale', 'Guidance', { step: 0.1 }),
          num(o, 'concurrency', 'Concurrent', { min: 1, width: '58px' }));
      }
    }
    clear(vbar).append(...kids);
  }

  // ---------------------------------------------------------------- tags (ElevenLabs audio tags, as in Phase 2)
  const saveSpoken = debounce((l, value) => { if (value !== l.spoken) upd(l, 'spoken', value); }, 600);
  function tagClick(t, remove) {
    const ids = [...grid.selected];
    if (!remove && lastEdit && document.activeElement === lastEdit.el) { saveSpoken(lastEdit.line, insertTagAtCaret(lastEdit.el, t)); return; }
    if (ids.length) { applyTags([t], remove ? 'remove' : 'add', ids); return; }
    toast('Click into a spoken text first (the tag goes in at the cursor), or tick rows to add it to the start of each.', true);
  }
  async function applyTags(tags, mode, ids, placement = '') {
    try {
      const r = await rpc('revoice.bulkTags', { id, lineIds: ids, tags, mode, placement });
      for (const nl of r.lines) replaceLine(nl, false);
      grid.setRows(visible()); renderFilters(); renderActions(); renderInspector();
      toast(`${r.changed} line(s) changed.`);
    } catch (e) { fail(e); }
  }
  async function autotag(ids) {
    if (!aiReady()) return;
    try {
      toast(`Asking the AI for tags on ${ids.length} line(s)…`);
      const r = await rpc('revoice.autotag', { id, lineIds: ids });
      for (const nl of r.lines) replaceLine(nl, false);
      grid.setRows(visible()); renderActions(); renderInspector();
      const bad = r.results.filter((x) => x.error);
      toast(autotagSummary(r.results), r.results.some((x) => x.error));
    } catch (e) { fail(e); }
  }
  function renderTagbar() {
    clear(tagbar);
    if (proj.voiceChanger) {
      tagbar.append(h('span.lbl', 'Voice Changer'), h('span.dim', 'The current recording decides the delivery, so audio tags are not used. The spoken text only feeds the .lip.'),
        h('button.btn.sm.ghost', { title: 'Find text in the spoken text of every line shown and replace it', onclick: findReplace }, 'Find and replace in all shown…'), h('div.spacer'), ...viewTools());
      return;
    }
    const engines = new Set(proj.speakers.filter((sp) => sp.voiceId && !sp.skip).map((sp) => sp.engine || 'elevenlabs'));
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
      h('span.lbl', 'Tags'),
      (proj.voice.tags || []).map(chip),
      custom,
      h('button.btn.sm', { title: 'Let the AI add ElevenLabs audio tags to the ticked lines (you review them)', onclick: () => { const ids = [...grid.selected]; if (!ids.length) { toast('Tick the box of one or more rows first.', true); return; } autotag(ids); } }, 'AI Auto Tag'),
      h('button.btn.sm.ghost', { title: 'Add a [tag] to every line in the current filter…', onclick: () => bulkTagDialog({
        pool: grid.rows.filter((l) => !l.excl && !l.gone), idOf: (l) => l.id,
        original: (l) => `${l.text} ${l.notes || ''} ${l.emo || ''}`,
        originalLabel: 'in SUBTITLE, ACTOR NOTES or EMOTION', originalPlaceholder: 'Anger, whisper',
        originalNote: 'Searches the in-game subtitle, the actor notes from the plugin and the emotion name (Anger, Fear, Happy…). Separate several with commas.',
        apply: applyTags,
      }) }, 'Add a [tag] to all shown…'),
      h('button.btn.sm.ghost', { title: 'Find text in the spoken text of every line shown and replace it, with [tags] if you like', onclick: findReplace }, 'Find and replace in all shown…'),
      engines.size && !engines.has('elevenlabs') ? h('span.dim', 'Your speakers use local engines, which ignore audio tags.') : null,
      h('div.spacer'), ...viewTools());
  }
  // ✓ / ✕ on a suggested tag: this line, or with Shift every line shown that has a suggestion.
  function tagAll(e, l, action) {
    if (!e.shiftKey) { upd(l, action); return; }
    const ids = grid.rows.filter((x) => x.tag && !x.excl).map((x) => x.id);
    if (ids.length) bulk(action, ids);
  }
  function folderButton() {
    const btn = h('button.btn.sm.ghost', { title: 'Open this project\'s folders in Explorer' }, icon('folder', { size: 14 }), ' Open folder ▾');
    btn.addEventListener('click', () => popupMenu(btn, [
      proj.outputFolder ? { label: 'Output folder (export)', sub: proj.outputFolder, onclick: () => rpc('open.path', { path: proj.outputFolder }).catch(fail) } : null,
      { label: 'Takes folder', sub: 'Every take of this project', onclick: () => rpc('revoice.openFolder', { id }).catch(fail) },
    ].filter(Boolean)));
    return btn;
  }

  async function upd(l, action, value) {
    try {
      const nl = await rpc('revoice.line', { id, lineId: l.id, action, value });
      replaceLine(nl);
    } catch (e) { fail(e); }
  }
  async function bulk(action, ids) {
    try {
      const r = await rpc('revoice.bulk', { id, lineIds: ids, action });
      for (const nl of r.lines) replaceLine(nl, false);
      grid.setRows(visible()); renderFilters(); renderActions(); renderInspector();
      toast(`${r.changed} line(s) changed.`);
    } catch (e) { fail(e); }
  }
  function replaceLine(nl, refresh = true) {
    const wasInFlight = inFlight.delete(nl.id);
    const i = proj.lines.findIndex((x) => x.id === nl.id);
    if (i >= 0) proj.lines[i] = nl;
    index.set(nl.id, nl);
    const ri = grid.rows.findIndex((x) => x.id === nl.id);
    if (ri >= 0) grid.rows[ri] = nl;
    if (lastEdit && lastEdit.line.id === nl.id) lastEdit.line = nl;
    updateExpNew();
    if (!refresh) return;
    grid.refreshRow(nl.id);
    if (wasInFlight) flashRow(grid, nl.id, nl.status === 'Failed');
    if (focusedId === nl.id && !insp.contains(document.activeElement)) renderInspector();
    renderActions();
  }

  function play(l, take) {
    const t = take || l.takes.find((x) => x.id === l.sel);
    if (!t) return;
    audio.src = proj.mediaBase + encodeURI(t.wav) + '?t=' + Date.now();
    audio.play().catch(fail);
  }
  async function playOriginal(l) {
    try {
      const url = await rpc('revoice.original', { id, lineId: l.id });
      if (!url) { toast('This line\'s current game file isn\'t there anymore: it was moved, renamed or deleted.', true); return; }
      audio.src = url;
      audio.play().catch(fail);
    } catch (e) { fail(e); }
  }

  async function takesModal(l) {
    const list = h('div.col');
    const render = () => {
      const line = index.get(l.id) || l;
      clear(list).append(line.takes.slice().reverse().map((t) => h('div.take' + (t.id === line.sel ? '.sel' : ''),
        h('button.icon-btn.big', { title: 'Play this take', onclick: () => play(line, t) }, '▶'),
        h('div.grow', h('div', '#' + t.slot + (t.id === line.sel ? ' · used for export' : '')), h('div.meta', `${t.durationSeconds.toFixed(2)} s · ${t.engine || ''} ${t.model || ''} · ${new Date(t.createdUtc).toLocaleString()}`)),
        t.id !== line.sel ? h('button.btn.sm', { title: 'Use this take for export', onclick: async () => { await upd(line, 'selectTake', t.id); render(); } }, 'Use') : null,
        h('button.icon-btn', { title: 'Delete this take and its files', onclick: async () => { await upd(line, 'deleteTake', t.id); render(); } }, '🗑'))));
    };
    render();
    await modal(`Takes · ${l.text}`, [list,
      h('div.row', h('button.btn.sm', { title: 'Delete every take of this line except the one in use', onclick: async () => { await upd(l, 'keepUsedTake'); render(); } }, 'Keep only the used take'),
        h('button.btn.sm', { title: 'Play the file the game uses now', disabled: !l.original, onclick: () => playOriginal(l) }, '▶ Current file'))]);
  }

  let autoPlay = null; // single-line generation plays the new take; batches never do
  const inFlight = new Set(); // lines a take is being made for: their ↻ spins until the take arrives
  function markInFlight(ids) {
    const before = [...inFlight];
    inFlight.clear();
    for (const x of ids || []) inFlight.add(x);
    if (grid) for (const x of new Set([...before, ...inFlight])) grid.refreshRow(x);
  }

  async function generate(mode, lineIds, { autoplay = false } = {}) {
    autoPlay = null;
    if (autoplay && lineIds && lineIds.length === 1) { const l0 = index.get(lineIds[0]); autoPlay = { lineId: lineIds[0], takes: l0 ? l0.takes.length : 0 }; }
    // Batches follow the speaker picked in the toolbar, exactly as the counts on the buttons do.
    const speaker = mode === 'selected' ? '' : speakerFilter;
    try {
      if (lineIds && lineIds.length === 1) markInFlight(lineIds); // spin at once, before the server answers
      const est = await rpc('revoice.estimate', { id, mode, lineIds, speaker });
      if (est.unassigned.length && !est.count) { markInFlight(null); toast('Pick a voice for: ' + est.unassigned.slice(0, 4).join(', ') + (est.unassigned.length > 4 ? '…' : '') + ' (Speakers tab).', true); return; }
      if (!est.count) { markInFlight(null); toast('Nothing to generate.'); return; }
      const confirmBatch = (app.state.settings.ui || {}).confirmBatch !== 'false';
      if (est.count > 1 && confirmBatch) {
        const ok = await modal('Check before spending credits', estimateBody(est), [{ label: 'Cancel', value: false }, { label: `Generate ${est.count} line(s)`, primary: true, value: true }], { wide: true, help: 'lines' });
        if (!ok) { markInFlight(null); return; }
      }
      if (est.count > 1) markInFlight(mode === 'selected' ? lineIds : proj.lines.filter((l) => (!speaker || l.speaker === speaker) && !l.excl && !l.gone && (mode === 'marked' ? l.marked : mode === 'all' ? true : mode === 'edited' ? isEdited(l) : isMissing(l))).map((l) => l.id));
      await rpc('revoice.generate', { id, mode, lineIds, speaker });
      renderActions();
    } catch (e) { markInFlight(null); fail(e); }
  }

  // What a batch will do, with every setting each voice uses, before any credits are spent.
  function estimateBody(est) {
    const who = est.speakerName ? `Only ${est.speakerName}'s lines are generated: ` : '';
    const what = `${est.count} line(s), ${est.characters.toLocaleString()} characters${est.voiceChanger ? ' of text (for the .lip; speech-to-speech is billed by audio length)' : ' ≈ credits'}.`;
    const groups = est.groups.map((g) => h('div.est-group',
      h('div.row', voiceChip(g.voicePack, g.color), h('span.faint', g.engine), h('div.spacer'), h('b', `${g.lines} line(s)`), h('span.faint', `${g.characters.toLocaleString()} characters`)),
      !est.speakerName && g.speakers.length ? h('div.faint', { style: { fontSize: '12px' } }, 'Speakers: ' + g.speakers.slice(0, 8).join(', ') + (g.speakers.length > 8 ? ` and ${g.speakers.length - 8} more` : '')) : null,
      h('div.kv4', g.settings.flatMap(([k, v]) => [h('div', k), h('div', v)]))));
    const skipped = est.unassigned.length
      ? h('div.faint', `${est.unassigned.length} other speaker(s) have no voice yet, so their lines are not generated. To voice them, pick their voices in the Speakers tab.`,
        h('details', h('summary', 'Show their names'), h('div', { style: { fontSize: '12px', marginTop: '4px' } }, est.unassigned.join(', '))))
      : null;
    return [h('div', { style: { fontSize: '15px' } }, who, h('b', what)), ...groups, skipped];
  }

  // ---------------------------------------------------------------- new voice types
  function renderVoiceTypes() {
    const wrap = h('div.view-body.reading');
    wrap.append(
      h('div.help', { style: { maxWidth: '1100px', marginBottom: '12px' } },
        h('p', h('b', 'Only when you need it. '), 'Replacing files in place needs no plugin. A new voice type is for one NPC who shares a vanilla voice type (Aela uses FemaleCommander with Uthgerd, Rayya, Legate Rikke…) and should sound different in all of their lines, including greetings and combat.'),
        h('p', 'The tool writes an ESL plugin with only the voice type record, a SkyPatcher file that gives it to the NPC, and a FormList Manipulator file that adds it to the lists that unlock dialogue — no NPC or list overrides, so other mods\' edits to the NPC keep working. Then every line the NPC can say is generated under the new voice type.')),
      h('div.row', { style: { marginBottom: '12px' } }, h('button.btn.primary', { title: 'Pick the NPC to give a voice type of their own', disabled: !proj.lastScan, onclick: newPlan }, '+ Give an NPC a new voice type…'),
        !proj.lastScan ? h('span.faint', 'Scan first.') : null,
        h('div.spacer'),
        h('span.faint', 'Plugin: ', h('span.mono', proj.voiceTypePlugin)),
        h('button.btn.sm', { title: 'Name of the ESL plugin that holds the voice types', onclick: async () => { const r = await prompt('Voice type plugin', [{ name: 'n', label: 'File name', value: proj.voiceTypePlugin }]); if (r && r.n.trim()) save({ voiceTypePlugin: r.n.trim() }); } }, 'Rename…'),
        switchEl(proj.referenceByEditorId, (v) => save({ referenceByEditorId: v }), 'Refer by EditorID', { title: 'On: the voice type and the form lists are written by EditorID (readable, as Aela Revoiced does). Off: Plugin|FormID. NPCs are always written as Plugin|FormID, which SkyPatcher always finds.' })));
    for (const plan of proj.plans) {
      const n = proj.lines.filter((l) => l.plan === plan.id);
      wrap.append(h('div.section', h('div.section-head', h('h2', plan.editorId), h('span.faint', `${n.length} voice files · ${n.filter((l) => l.sel).length} generated`), h('div.spacer'),
        h('button.btn.sm', { title: 'Lists, NPC copies and the coverage report', onclick: () => planEditor(plan.id) }, 'Edit & coverage…'),
        h('button.btn.sm.ghost.danger', { title: 'Remove this plan (its takes stay until you delete the lines)', onclick: async () => { if (await confirmBox('Remove new voice type?', 'Its lines without takes are removed; lines with takes stay marked as gone.', 'Remove', true)) { try { proj = await rpc('revoice.planDelete', { id, planId: plan.id }); reindex(); renderAll(); } catch (e) { fail(e); } } } }, 'Remove')),
        h('div.section-body', h('div.dim', { style: { padding: '10px 0' } }, `${plan.npcs.length} NPC record(s) · ${plan.lists.length} form list(s) · lines: ${plan.lineSet === 'all' ? 'every line of the original voice type' : 'lines this NPC can say'}`))));
    }
    return wrap;
  }

  async function newPlan() {
    const q = h('input', { type: 'text', placeholder: 'NPC name or EditorID', style: { width: '100%' } });
    const res = h('div.col', { style: { maxHeight: '320px', overflow: 'auto' } });
    let chosen = null;
    const run = debounce(async () => {
      try {
        clear(res).append(h('div.faint', 'Searching… (the first search loads the plugins)'));
        const list = await rpc('revoice.npcs', { id, q: q.value });
        clear(res).append(list.length ? list.map((n) => h('label.row', { style: { padding: '4px 0' } }, h('input', { type: 'radio', name: 'npc', onchange: () => { chosen = n.key; } }), h('b', n.name), h('span.faint.mono', n.editorId), h('span.faint', n.plugin), h('div.spacer'), h('span.mono', n.voiceType))) : h('div.faint', 'No NPC found.'));
      } catch (e) { fail(e); }
    }, 300);
    q.addEventListener('input', run);
    run();
    const ok = await modal('Give an NPC a new voice type', [h('div.faint', 'The NPC keeps everything else; only the voice type changes (through SkyPatcher).'), q, res],
      [{ label: 'Cancel', value: false }, { label: 'Create', primary: true, value: () => chosen }]);
    if (!ok) return;
    try {
      const r = await rpc('revoice.planCreate', { id, npc: ok });
      proj = r.project; reindex(); renderAll();
      await planEditor(r.planId);
    } catch (e) { fail(e); }
  }

  async function planEditor(planId) {
    let info;
    try { info = await rpc('revoice.planInfo', { id, planId }); } catch (e) { fail(e); return; }
    const plan = info.plan;
    const ed = h('input', { type: 'text', value: plan.editorId, style: { width: '260px' }, title: 'Letters, digits and _ only' });
    const npcOn = new Set(plan.npcs);
    const listOn = new Set(plan.lists);
    let lineSet = plan.lineSet;
    let extraBox = null;
    const cov = info.coverage;
    const ok = await modal(`New voice type for ${info.copies[0] ? info.copies[0].name : ''}`, [
      h('div.kv', h('div', 'Original voice type'), h('div.mono', info.baseVoiceType), h('div', 'Shared with'), h('div', { title: info.sharing.join(', ') }, info.sharing.slice(0, 12).join(', ') + (info.sharing.length > 12 ? ` +${info.sharing.length - 12}` : ''))),
      h('div.col', h('div.field-label', 'New voice type EditorID'), ed),
      h('div.col', h('div.field-label', 'NPC records (SkyPatcher)'), h('div.faint', 'Every copy of the character in your plugins, so replacers and mods that copy the character keep the voice. Add more in the generated SkyPatcher file for mods you did not load.'),
        h('div.box', { style: { maxHeight: '150px', overflow: 'auto' } }, info.copies.map((c) => h('label.row', switchEl(npcOn.has(c.key), (v) => { if (v) npcOn.add(c.key); else npcOn.delete(c.key); }, ''), h('b', c.name), h('span.mono.faint', `${c.plugin} | ${c.key.split('|')[1]}`), h('span.faint', c.editorId), h('div.spacer'), h('span.mono', c.voiceType))))),
      (() => { extraBox = h('textarea', { rows: 3, placeholder: 'BadMoon.esp|6603B\nUnslaad.esm|AC23F', title: 'One Plugin|FormID per line' }, (plan.extraNpcs || []).join('\n'));
        return h('div.col', h('div.field-label', 'More NPC records from mods you did not load'), h('div.faint', 'One Plugin|FormID per line (as in xEdit, without the load order prefix). Written to the SkyPatcher file as they are.'), extraBox); })(),
      h('div.col', h('div.field-label', 'Form lists (FormList Manipulator)'), h('div.faint', 'Lists that hold the original voice type, with how many of the NPC\'s lines check each. Tick only the ones that fit the character — e.g. follower or marriage lists for a follower, not lists for a different personality.'),
        h('div.box', { style: { maxHeight: '220px', overflow: 'auto' } }, h('table.tbl', h('tbody', info.lists.map((l) => h('tr',
          h('td', switchEl(listOn.has(l.key), (v) => { if (v) listOn.add(l.key); else listOn.delete(l.key); }, '')), h('td.mono', l.editorId), h('td.faint', l.plugin),
          h('td', l.containsBase ? h('span.badge.b-valid', { title: 'Holds the original voice type directly' }, 'holds it') : h('span.badge.b-skip', { title: 'Holds it through another list' }, 'nested')),
          h('td', { title: 'Lines of this NPC that check this list' }, `${l.gatedLines} line(s)`))))))),
      h('div.col', h('div.field-label', 'Lines to generate'), segmented([['npc', 'Lines the NPC can say'], ['all', 'Every line of the voice type']], lineSet, (v) => { lineSet = v; }),
        h('div.faint', '"Lines the NPC can say" leaves out lines tied to other NPCs. "Every line" is the safe superset (more credits).')),
      h('div.col', h('div.field-label', 'Coverage (with the lists as saved)'),
        h('div', `The NPC can say ${cov.canSay.toLocaleString()} response(s) now; ${cov.covered.toLocaleString()} still work with the new voice type. The rest go silent in game:`),
        cov.gaps.length ? h('div.box', { style: { maxHeight: '180px', overflow: 'auto' } }, h('table.tbl', h('tbody', cov.gaps.map((g) => h('tr', h('td.mono', g.plugin), h('td', g.reason), h('td', String(g.lines)), h('td.faint.ellipsis', { title: g.examples.join('\n'), style: { maxWidth: '260px' } }, g.examples[0] || '')))))) : h('div.faint', 'Nothing goes silent.'),
        h('div.faint', 'Save, then open this again to see the coverage for changed lists. Lines that check the voice type directly need a patch for that mod, or stay silent.')),
    ], [{ label: 'Close', value: false }, { label: 'Save', primary: true, value: true }], { wide: true, help: 'new-voice-types-optional' });
    if (!ok) return;
    try {
      proj = await rpc('revoice.planSave', { id, planId, editorId: ed.value.trim(), npcs: [...npcOn], lists: [...listOn], lineSet, extraNpcs: extraBox ? extraBox.value.split('\n').map((x) => x.trim()).filter(Boolean) : [] });
      reindex(); renderAll();
      toast('Saved. Rescan to update the plan\'s lines.');
    } catch (e) { fail(e); }
  }

  // ---------------------------------------------------------------- layout
  let shownTab = null;
  function renderAll() {
    const same = shownTab === tab; shownTab = tab;
    keepScroll(body, () => renderAllNow(), same);
  }
  function renderAllNow() {
    // Re-attaching the grid resets its scroll position: keep where the user was.
    const scroll = grid.el.isConnected ? grid.el.scrollTop : null;
    renderHead();
    clear(body);
    if (tab === 'setup') { body.append(renderSetup()); return; }
    if (tab === 'speakers') { body.append(renderSpeakers()); return; }
    if (tab === 'voicetypes') { body.append(renderVoiceTypes()); return; }
    if (!proj.lines.length) { body.append(h('div.empty', h('h2', 'No lines yet'), h('div', proj.plugins.some((p) => p.target) ? 'Scan to find the lines.' : 'Add the plugins to revoice in Setup, then Scan.'))); return; }
    const split = h('div.split' + (showInsp ? '' : '.no-insp'));
    split.append(left, insp);
    if (!elevenModels) loadVoices();
    renderVbar();
    body.append(vbar, filters, tagbar, reviewBar, split, actions);
    if (review) renderReviewBar();
    renderFilters();
    renderTagbar();
    grid.setRows(visible());
    if (scroll != null) { grid.el.scrollTop = scroll; grid.paint(true); } else if (pendingScroll) restoreScroll();
    renderInspector();
    renderActions();
  }

  function restoreScroll(tries = 0) {
    if (!grid.el.isConnected || !grid.el.clientHeight) { if (tries < 60) requestAnimationFrame(() => restoreScroll(tries + 1)); return; }
    grid.el.scrollTop = pendingScroll; grid.paint(true); pendingScroll = 0;
  }
  grid.el.addEventListener('scroll', debounce(() => { if (grid.el.isConnected && tab === 'lines') store.set('rv-scroll:' + id, grid.el.scrollTop); }, 300));

  on('revoiceLine', (d) => {
    if (d.projectId !== id || !document.body.contains(view)) return;
    replaceLine(d.line);
    if (autoPlay && d.line.id === autoPlay.lineId && d.line.takes.length > autoPlay.takes && d.line.sel) { autoPlay = null; play(d.line); }
  });
  let jobSeen = false;
  document.addEventListener('job-progress', () => { if (!jobSeen && document.body.contains(view)) { jobSeen = true; renderHead(); if (tab === 'lines') { grid.paint(true); renderActions(); } } });
  document.addEventListener('job-done', async (e) => {
    jobSeen = false;
    if (!document.body.contains(view)) return;
    if (inFlight.size) markInFlight(null);
    const d = e.detail || {};
    if (d.kind === 'revoice-orphans' && d.id === id) { if (d.message) toast(d.message, !d.ok); if (d.ok) orphansModal(); }
    if ((d.kind === 'revoice-scan' && d.id === id) || (d.kind === 'generate' && d.revoiceId === id) || (d.kind === 'revoice-export' && d.id === id) || (d.kind === 'revoice-qa' && d.projectId === id)) {
      if (d.kind === 'revoice-qa' && d.message) toast(d.message, !d.ok);
      try { proj = await rpc('revoice.get', { id }); reindex(); } catch { /* keep */ }
      if (d.kind === 'revoice-scan' && d.ok) { if (proj.lines.length && tab === 'setup') { tab = 'speakers'; store.set('rv-tab:' + id, tab); } toast(`Scan done: ${d.report.lines.toLocaleString()} voice files to make, ${d.report.withOriginal.toLocaleString()} with a current file.`); }
      if (d.kind === 'revoice-export' && d.ok !== undefined && d.report) {
        const folders = Object.entries(d.report.folders || {}).sort((a, b) => a[0].localeCompare(b[0]));
        await modal('Exported', [h('div.kv', h('div', 'Written'), h('div', String(d.report.written)), h('div', 'Kept (already there)'), h('div', String(d.report.skipped || 0)), d.report.removed ? h('div', 'Old files deleted') : null, d.report.removed ? h('div', String(d.report.removed)) : null, h('div', 'Failed'), h('div', String(d.report.failed)), h('div', 'Folder'), h('div.mono', d.folder)),
          folders.length ? h('div', h('div.field-label', 'Written to'), h('div.box.mono', { style: { maxHeight: '180px', overflow: 'auto', whiteSpace: 'pre-wrap', fontSize: '12px' } }, folders.map(([f, n]) => `${f}\\  ${n}`).join('\n'))) : null,
          d.report.files.length ? h('div', h('div.field-label', 'New voice type pack'), h('div.box.mono', { style: { whiteSpace: 'pre-wrap' } }, d.report.files.join('\n'))) : null,
          d.report.errors.length ? h('div.box.mono', { style: { maxHeight: '160px', overflow: 'auto' } }, d.report.errors.slice(0, 50).join('\n')) : null],
        [{ label: 'Open folder', value: 'open' }, { label: 'OK', primary: true, value: true }]).then((v) => { if (v === 'open') rpc('open.path', { path: d.folder }); });
      }
      if (!d.ok && d.message) toast(d.message, true);
    }
    renderAll();
  });

  renderAll();
  return view;
}
