// Other Tools → NPC Revoice: replace the voice files of NPC dialogue in any mod (or the base game).
// Standalone from the DBVO pipeline; shares engines, takes and .fuz building with Phase 2.
import { rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, debounce, switchEl, segmented, popupMenu, columnsMenu, droppedPaths, diffAdded, icon, mergeInto } from './lib.js';
import { app, go, refreshState } from './app.js';
import { voiceChip, creditsButton, LANGS } from './phase2.js';
import { bulkTagDialog, insertTagAtCaret } from './tagtools.js';

const CERT = { Exact: 'Speaker known', Probable: 'Probable speakers', Unknown: 'Speaker unknown' };

// ------------------------------------------------------------------ list

export async function revoiceListView() {
  const s = await refreshState();
  const list = await rpc('revoice.list');
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'NPC Revoice'),
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
    h('div.rows1', list.map((x) => h('div.prow', { title: 'Open this project', onclick: () => go('revoice', { id: x.id }) },
      h('div.row', h('span.title.ellipsis.grow', x.name), x.newVoiceTypes ? h('span.badge.b-player', { title: 'Has new voice types (SkyPatcher + FLM)' }, `${x.newVoiceTypes} new voice type(s)`) : null,
        h('span.faint.ellipsis', { style: { maxWidth: '45%' } }, x.targets.join(', ') || 'no plugins yet')),
      h('div.prow-stats',
        h('div.grow', h('div.bar' + (x.lines && x.generated >= x.lines ? '.ok' : ''), h('div.bar-fill', { style: { width: (x.lines ? (100 * x.generated) / x.lines : 0) + '%' } }))),
        h('span.mono', `${x.generated} / ${x.lines}`),
        h('span.left', h('b', String(x.speakers)), ' speakers')))))));
  return view;

  async function create() {
    const r = await prompt('New revoice project', [{ name: 'n', label: 'Name', placeholder: 'e.g. Vigilant revoiced' }]);
    if (!r || !r.n.trim()) return;
    try { const c = await rpc('revoice.create', { name: r.n.trim() }); go('revoice', { id: c.id }); } catch (e) { fail(e); }
  }
}

// ------------------------------------------------------------------ project

export async function revoiceView(id) {
  await refreshState();
  let proj = await rpc('revoice.get', { id });
  let showInsp = store.get('rv-insp', true);
  let tab = store.get('rv-tab:' + id, proj.lines.length ? 'lines' : 'setup');
  let filter = 'all', speakerFilter = '', search = '';
  let focusedId = null;
  const audio = new Audio();
  let index = new Map();
  const reindex = () => { index = new Map(proj.lines.map((l) => [l.id, l])); };
  reindex();

  const view = h('div.view');
  const head = h('div.view-head');
  const body = h('div', { style: { flex: '1', minHeight: '0', display: 'flex', flexDirection: 'column' } });
  view.append(head, body);

  // ---------------------------------------------------------------- head
  function renderHead() {
    const exp = h('button.btn', { title: 'Write the chosen takes as .fuz files at the game\'s paths', disabled: !!app.job }, 'Export ▾');
    exp.addEventListener('click', () => popupMenu(exp, [
      { label: 'Export .fuz files to the output folder', sub: `${proj.outputFolder || '(set the output folder in Setup first)'}\\Sound\\Voice\\<plugin>\\<voice type>\\<file>.fuz, the game's own paths (replaces files already there)` + (proj.plans.length ? '. Also the new voice type plugin, SkyPatcher and FLM files.' : ''), onclick: () => doExport(true) },
      { label: 'Export, but keep files already there', sub: 'The same, but .fuz files that exist in the output folder are left as they are', onclick: () => doExport(false) },
      proj.plans.length ? { label: 'Only the new voice type pack', sub: 'Plugin + SkyPatcher + FLM + install notes', onclick: writePack } : null,
      proj.outputFolder ? { label: 'Open the output folder', sub: proj.outputFolder, onclick: () => rpc('open.path', { path: proj.outputFolder }) } : null,
    ].filter(Boolean)));
    const tabs = segmented([['setup', 'Setup'], ['speakers', `Speakers (${proj.speakers.length})`], ['lines', `Lines (${proj.lines.length})`], ['voicetypes', `New voice types (${proj.plans.length})`]], tab, (v) => { tab = v; store.set('rv-tab:' + id, v); renderAll(); });
    clear(head).append(
      h('button.btn.ghost.sm', { title: 'Back to the project list', onclick: () => go('revoices') }, '← NPC Revoice'),
      h('h1', proj.name),
      tabs,
      h('div.spacer'),
      switchEl(proj.voiceChanger, toggleVc, 'Voice Changer', { title: 'Convert the game\'s current recordings instead of text-to-speech. Not for uploading: see the warning.' }),
      h('button.btn', { title: 'Read the plugins and voice sources again and rebuild the line list (keeps your takes and edits)', disabled: !!app.job, onclick: scan }, proj.lastScan ? '⟳ Rescan' : 'Scan'),
      exp,
      h('button.btn.ghost.danger.sm', { title: 'Delete this project and its takes (exported files stay)', onclick: async () => { if (await confirmBox('Delete revoice project?', 'Deletes its takes too. Exported files stay where they are.', 'Delete', true)) { try { await rpc('revoice.delete', { id }); go('revoices'); } catch (e) { fail(e); } } } }, 'Delete'));
  }

  async function toggleVc(on) {
    if (!on) { await save({ voiceChanger: false }); return; }
    if (!proj.voiceChangerWarningAccepted) {
      const tick = h('input', { type: 'checkbox' });
      const ok = await modal('Voice Changer on other people\'s recordings', [
        h('div.help.bad',
          h('p', h('b', 'These lines were performed by voice actors. '), 'Converting their performances without their consent must not be uploaded to Nexus Mods or used commercially.'),
          h('p', 'Even for private use in your own modlist, check ElevenLabs\' terms and your local law; that responsibility is yours.'),
          h('p', 'To publish, generate from text with a voice you have the rights to (turn the Voice Changer off).')),
        h('label.row', tick, 'I have read this'),
      ], [{ label: 'Cancel', value: false }, { label: 'Turn on', primary: true, value: () => tick.checked }]);
      if (!ok) { toast(ok === false ? 'Voice Changer stays off.' : 'Tick "I have read this" to continue.', ok !== false); renderHead(); return; }
      await save({ voiceChangerWarningAccepted: true });
    }
    await save({ voiceChanger: true });
  }

  async function save(o) {
    try { proj = await rpc('revoice.save', { id, ...o }); reindex(); renderAll(); } catch (e) { fail(e); renderHead(); }
  }

  async function scan() {
    try { await rpc('revoice.scan', { id }); toast('Scanning… the first scan with Skyrim.esm takes a little while.'); } catch (e) { fail(e); }
  }

  async function doExport(overwrite) {
    if (!proj.outputFolder) { toast('Set the output folder in Setup first.', true); tab = 'setup'; renderAll(); return; }
    try {
      // Files an earlier export wrote that no line uses any more (the mod changed, or a line now has another voice type).
      const stale = await rpc('revoice.staleExports', { id }).catch(() => []);
      let removeStale = false;
      if (stale.length) {
        const v = await modal('Files from an earlier export', [
          h('div', `${stale.length} .fuz file(s) in the output folder were written by an earlier export but no line uses them now. The mod or the plugin list changed since, or these lines now belong to another voice type, so the game looks for them in another folder.`),
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
    const wrap = h('div.view-body');
    const sec = (title, extra, ...rows) => h('div.section', h('div.section-head', h('h2', title), h('div.spacer'), extra), h('div.section-body', rows));
    const srow = (label, help, control) => h('div.srow', h('div.lbl', h('span', label), help ? h('small', help) : null), h('div', control));
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
      ls.textChanged ? h('div.banner.info', { style: { margin: 0 } }, h('span', h('b', `${ls.textChanged} line(s) changed in the mod since their take`), ' — they are marked "changed since take" and come up again under To do.')) : null,
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
    const i = h('input', { type: 'text', value: value || '', placeholder: 'e.g. D:\\MO2\\mods\\Vigilant Revoiced', style: { flex: '1' } });
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
    ], [{ label: 'Cancel', value: false }, { label: 'Link the matched files', primary: true, value: true }], { wide: true });
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
    return h('div.box', { style: { maxWidth: '1100px', marginBottom: '12px' } },
      h('div.row', h('b', 'Voice settings'), h('span.faint', 'Shared by every speaker; each speaker picks its own voice below.'), h('div.spacer'),
        h('button.btn.sm.ghost', { title: 'Reload voices from your accounts and local servers', onclick: () => loadVoices(true) }, '⟳ Reload voices')),
      h('div.col', { style: { gap: '8px', marginTop: '8px' } }, voiceSettingRows()));
  }

  // The engine settings (shared by every speaker), as rows: the Speakers tab and the toolbar above the lines.
  function voiceSettingRows() {
    const v = proj.voice;
    const set = (obj, k, val) => { obj[k] = val; voiceEdits++; saveVoiceSettings(); };
    const grp = (label, ...kids) => h('div.tgroup', h('label', label), h('div.row', ...kids));
    const slider = (obj, k, min, max, step, label, title) => {
      const val = h('span.mono.dim', { style: { width: '34px' } }, Number(obj[k]).toFixed(2));
      const r = h('input', { type: 'range', min, max, step, value: obj[k], title: title || '', style: { width: '96px' } });
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
        const fmt = h('select', { onchange: (ev) => set(e, 'outputFormat', ev.target.value) }, [['mp3_44100_128', 'MP3 44.1k 128'], ['mp3_44100_192', 'MP3 44.1k 192'], ['pcm_44100', 'PCM 44.1k (Pro plan)'], ['pcm_24000', 'PCM 24k']].map(([k, t]) => h('option', { value: k, selected: k === e.outputFormat }, t)));
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
    if (sp.voiceId && !(list || []).some((v) => v.id === sp.voiceId)) vSel.append(h('option', { value: sp.voiceId, selected: true }, sp.voiceName || sp.voiceId));
    if (!sp.voiceId && sp.voicePackId) vSel.append(h('option', { value: '', selected: true }, 'Voice pack: ' + sp.voicePack));
    vSel.addEventListener('change', async () => {
      const v = (list || []).find((x) => x.id === vSel.value);
      try { proj.speakers = await rpc('revoice.speaker', { id, key: sp.key, engine: eng, voiceId: vSel.value, voiceName: v ? v.name : vSel.value }); renderAll(); } catch (e) { fail(e); }
    });
    return h('span.row', { style: { gap: '4px' } }, engSel, vSel);
  }

  function renderSpeakers() {
    const wrap = h('div.view-body');
    if (!proj.speakers.length) { wrap.append(h('div.empty', h('h2', 'No speakers yet'), h('div', 'Scan first (Setup).'))); return wrap; }
    if (Object.values(voiceLists).every((x) => x == null)) loadVoices();
    const kind = (sp) => sp.key.startsWith('npc:') ? 'one NPC' : sp.key.startsWith('vt:') ? 'shared lines (voice type)' : sp.key.startsWith('nvt:') ? 'new voice type' : '';
    wrap.append(
      h('div.help', { style: { maxWidth: '1100px', marginBottom: '12px' } },
        h('p', h('b', 'Each speaker gets its own voice. '), 'Pick the engine and a voice from your ElevenLabs account or your local VoxCPM / OmniVoice server. A speaker is one NPC when a line belongs to a single character, or a voice type when several NPCs share the line (generic lines). Two NPCs sharing a vanilla voice type can still get different voices for their own lines; only the lines they share have to share a voice.')),
      voiceSettings(),
      h('table.tbl', { style: { maxWidth: '1100px' } }, h('thead', h('tr', h('th', 'Speaker'), h('th', 'Voice type'), h('th', { title: 'Voice files to make' }, 'Lines'), h('th', 'Done'), h('th', { title: 'Characters of text to generate ≈ ElevenLabs credits' }, 'Characters'), h('th', 'Voice'), h('th', { title: 'Leave these lines alone' }, 'Skip'), h('th', ''))),
        h('tbody', proj.speakers.map((sp) => h('tr' + (sp.skip ? '.muted' : ''),
          h('td', h('b', sp.name), sp.plan ? h('span.badge.b-player', { style: { marginLeft: '6px' } }, 'new voice type') : null, h('div.faint', kind(sp))),
          h('td.mono', sp.voiceType),
          h('td', String(sp.lines)), h('td', `${sp.generated}`), h('td', sp.characters.toLocaleString()),
          h('td', voicePick(sp)),
          h('td', switchEl(sp.skip, async (v) => { try { proj.speakers = await rpc('revoice.speaker', { id, key: sp.key, skip: v }); renderAll(); } catch (e) { fail(e); } }, '', { title: 'Skip this speaker' })),
          h('td', h('button.btn.sm.ghost', { title: 'Show this speaker\'s lines', onclick: () => { speakerFilter = sp.key; tab = 'lines'; renderAll(); } }, 'Lines →')))))));
    return wrap;
  }

  // ---------------------------------------------------------------- lines
  const left = h('div.left');
  const insp = h('div.inspector');
  const filters = h('div.filters');
  const tagbar = h('div.tagbar');
  const vbar = h('div.toolbar');
  const actions = h('div.statusbar');
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
        h('button.act-btn', { title: genBlock(l) || 'Regenerate (a new take; older takes stay)', disabled: !!genBlock(l), onclick: (e) => { e.stopPropagation(); generate('selected', [l.id], { autoplay: true }); } }, icon('regen')),
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
        h('button.icon-btn', { title: 'Accept the suggested tag', onclick: (e) => { e.stopPropagation(); upd(l, 'acceptTags'); } }, '✓'),
        h('button.icon-btn', { title: 'Undo', onclick: (e) => { e.stopPropagation(); upd(l, 'rejectTags'); } }, '✕'));
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
      todo: L.filter((l) => !l.excl && !l.gone && ['Pending', 'Failed', 'Stale'].includes(l.status)).length,
      done: L.filter((l) => l.sel).length,
      failed: L.filter((l) => l.status === 'Failed').length,
      orig: L.filter((l) => l.original).length,
      silent: L.filter((l) => !l.original).length,
      unsure: L.filter((l) => l.cert !== 'Exact').length,
      marked: L.filter((l) => l.marked).length,
      excl: L.filter((l) => l.excl).length,
    };
  }
  function visible() {
    const q = search.toLowerCase();
    return proj.lines.filter((l) => (!speakerFilter || l.speaker === speakerFilter)
      && (filter === 'all' || (filter === 'todo' && !l.excl && !l.gone && ['Pending', 'Failed', 'Stale'].includes(l.status)) || (filter === 'done' && l.sel) || (filter === 'failed' && l.status === 'Failed')
        || (filter === 'orig' && l.original) || (filter === 'silent' && !l.original) || (filter === 'unsure' && l.cert !== 'Exact') || (filter === 'marked' && l.marked) || (filter === 'excl' && l.excl))
      && (!q || l.text.toLowerCase().includes(q) || l.spoken.toLowerCase().includes(q) || (l.stem || '').toLowerCase().includes(q) || l.speakerName.toLowerCase().includes(q) || (l.quest || '').toLowerCase().includes(q)));
  }
  function renderFilters() {
    const c = counts();
    const chip = (k, label, tip) => h('button.chip' + (filter === k ? '.on' : ''), { title: tip, onclick: () => { filter = k; renderFilters(); grid.setRows(visible()); } }, label, ' ', h('span.n', String(c[k])));
    const q = h('input', { type: 'text', placeholder: 'Search text, file, speaker, quest…', value: search, title: 'Filter the list' });
    q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visible()); }, 200));
    clear(filters).append(
      chip('all', 'All', 'Every voice file to make'), chip('todo', 'To generate', 'No take yet, failed, or text changed since the take'), chip('done', 'Generated', 'Has a take'),
      chip('failed', 'Failed', 'The last attempt failed'), chip('orig', 'Has current file', 'The game plays a file for this line now (▶ in the Now column)'),
      chip('silent', 'Silent now', 'No voice file yet: subtitles only in game'), chip('unsure', 'Speaker unsure', 'The speaker was worked out as probable or unknown — check before generating'),
      chip('marked', 'Flagged', 'Flagged to revisit'), chip('excl', 'Excluded', 'Left out of generation and export'),
      h('div.spacer'), q, columnsMenu(grid),
      h('button.btn.sm', { title: 'Show or hide the line panel', onclick: () => { showInsp = !showInsp; store.set('rv-insp', showInsp); renderAll(); } }, showInsp ? 'Hide panel ▸' : '◂ Show panel'));
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
    const spoken = h('textarea', { rows: 3, disabled: l.excl }, l.spoken);
    spoken.addEventListener('change', () => upd(l, 'spoken', spoken.value));
    spoken.addEventListener('focus', () => { lastEdit = { el: spoken, line: l }; });
    insp.append(
      h('h3', 'Line'),
      h('div.kv', h('div', 'Speaker'), h('div', l.speakerName), h('div', 'Who'), h('div', l.npcs || '—'), h('div', 'Found by'), h('div', `${CERT[l.cert] || l.cert}${l.via ? ' · ' + l.via : ''}`),
        h('div', 'Quest'), h('div.mono', l.quest || '—'), h('div', 'Topic'), h('div.mono', l.topic || '—'), h('div', 'Plugin'), h('div.mono', l.plugin),
        h('div', 'Emotion'), h('div', `${l.emo} ${l.emoV}`), l.notes ? h('div', 'Actor notes') : null, l.notes ? h('div', l.notes) : null),
      h('div', h('div.field-label', 'File'), h('div.box.mono', { style: { wordBreak: 'break-all' } }, `Sound\\Voice\\${l.pf}\\${l.vt}\\${l.stem}.fuz`)),
      h('div', h('div.field-label', 'Now'), l.original ? h('div.row', h('button.btn.sm', { title: 'Play the current file', onclick: () => playOriginal(l) }, '▶ Play current'), h('span.mono.faint.ellipsis', { title: l.original }, l.original)) : h('div.faint', 'Silent in game: no voice file yet.')),
      h('div', h('div.field-label', 'Subtitle (in game)'), h('div.box', l.text)),
      h('div', h('div.field-label', 'Spoken text (tags allowed)'), spoken,
        h('div.row', { style: { marginTop: '6px' } },
          l.spokenEdited ? h('button.btn.sm.ghost', { title: 'Use the subtitle as the spoken text again', onclick: () => upd(l, 'resetSpoken') }, '↺ Restore subtitle') : null,
          h('div.spacer'),
          h('button.btn.sm', { title: l.excl ? 'Bring the line back' : 'Leave this line out of generation and export', onclick: () => upd(l, l.excl ? 'include' : 'exclude') }, l.excl ? 'Include' : 'Exclude'),
          h('button.btn.sm.primary', { title: genBlock(l) || 'Make a new take of this line', disabled: !!genBlock(l), onclick: () => generate('selected', [l.id], { autoplay: true }) }, l.takes.length ? 'New take' : 'Generate'))),
      l.takes.length ? h('div', h('div.field-label', 'Takes'), h('div.col', l.takes.slice().reverse().map((t) => h('div.take' + (t.id === l.sel ? '.sel' : ''),
        h('button.icon-btn.big', { title: 'Play this take', onclick: () => play(l, t) }, '▶'),
        h('div.grow', h('div', '#' + t.slot + (t.id === l.sel ? ' · used for export' : '')), h('div.meta', `${t.durationSeconds.toFixed(2)} s · ${t.engine || ''} ${t.model || ''}`)),
        t.id !== l.sel ? h('button.btn.sm', { title: 'Use this take for export', onclick: () => upd(l, 'selectTake', t.id) }, 'Use') : null,
        h('button.icon-btn', { title: 'Delete this take and its files', onclick: () => upd(l, 'deleteTake', t.id) }, '🗑'))))) : null);
  }

  function renderActions() {
    const c = counts();
    const sel = [...grid.selected];
    const bulkBtn = h('button.btn', { title: 'Actions for the ticked rows' }, `Ticked (${sel.length}) ▾`);
    bulkBtn.addEventListener('click', () => {
      if (!sel.length) { toast('Tick rows first.', true); return; }
      popupMenu(bulkBtn, [
        { label: 'Accept suggested tags', sub: 'Use the emotion or AI tag suggestion on each ticked line', onclick: () => bulk('acceptTags', sel) },
        { label: 'Undo suggested tags', onclick: () => bulk('rejectTags', sel) },
        { label: 'Exclude', sub: 'Leave out of generation and export', onclick: () => bulk('exclude', sel) },
        { label: 'Include', onclick: () => bulk('include', sel) },
        { label: 'Flag to revisit', onclick: () => bulk('markOn', sel) },
        { label: 'Clear flags', onclick: () => bulk('markOff', sel) },
        { label: 'Keep only the used take', sub: 'Deletes the other takes and their files', onclick: () => bulk('keepUsedTake', sel) },
      ]);
    });
    clear(actions).append(
      h('span.stat', h('b', String(c.done)), ` / ${c.all} generated`, c.failed ? h('span', { style: { color: 'var(--bad)' } }, ` · ${c.failed} failed`) : '', ` · ${c.silent} silent now`),
      h('div.spacer'),
      bulkBtn,
      h('button.btn', { disabled: !!app.job || !sel.length, title: 'Generate the ticked rows', onclick: () => generate('selected', sel) }, `Generate ticked (${sel.length})`),
      h('button.btn', { disabled: !!app.job || !c.marked, title: 'Generate the flagged lines', onclick: () => generate('marked') }, `Flagged (${c.marked})`),
      h('button.btn.primary', { disabled: !!app.job || !c.todo, title: 'Generate every line without a take (for speakers with a voice)', onclick: () => generate('missing') }, `Generate missing (${c.todo})`));
  }

  // ---------------------------------------------------------------- toolbar above the lines (laid out like Phase 2's)
  let creditsEl = null;
  function renderVbar() {
    const v = proj.voice;
    const set = (obj, k, val) => { obj[k] = val; voiceEdits++; saveVoiceSettings(); renderVbar(); };
    const grp = (label, ...kids) => h('div.tgroup', h('label', label), h('div.row', ...kids));
    const slider = (obj, k, min, max, step, label) => {
      const val = h('span.mono.dim', { style: { width: '34px' } }, Number(obj[k]).toFixed(2));
      const r = h('input', { type: 'range', min, max, step, value: obj[k], style: { width: '96px' } });
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
    const spSel = h('select', { title: 'Show one speaker\'s lines (and pick its voice here)', style: { maxWidth: '240px' } }, h('option', { value: '' }, `All speakers (${proj.speakers.length})`),
      proj.speakers.map((sp) => h('option', { value: sp.key, selected: sp.key === speakerFilter }, `${sp.name} (${sp.lines})`)));
    spSel.addEventListener('change', () => { speakerFilter = spSel.value; renderVbar(); renderFilters(); grid.setRows(visible()); renderActions(); });
    const sp = speakerFilter && proj.speakers.find((x) => x.key === speakerFilter);
    const voiceGrp = sp ? grp('Engine · voice', voicePick(sp), h('button.icon-btn', { title: 'Reload voices and models', onclick: () => loadVoices(true) }, '⟳'))
      : grp('Voice', h('button.btn.sm.ghost', { title: 'Each speaker has its own voice: pick a speaker on the left, or set them all in the Speakers tab', onclick: () => { tab = 'speakers'; renderAll(); } }, 'Per speaker · Speakers tab →'));
    const kids = [grp('Speaker', spSel), voiceGrp];
    const fmts = [['mp3_44100_128', 'MP3 44.1k 128'], ['mp3_44100_192', 'MP3 44.1k 192'], ['pcm_44100', 'PCM 44.1k (lossless)']];
    if (proj.voiceChanger) {
      const st = v.sts;
      const ms = h('select', { onchange: (e) => set(st, 'modelId', e.target.value) }, (elevenModels && elevenModels.length ? elevenModels : [{ modelId: st.modelId }]).map((m) => h('option', { value: m.modelId, selected: m.modelId === st.modelId }, m.modelId)));
      kids.push(grp('Model', ms), slider(st, 'stability', 0, 1, 0.01, 'Stability'), slider(st, 'similarity', 0, 1, 0.01, 'Similarity'), slider(st, 'style', 0, 1, 0.01, 'Style'),
        grp('Remove background noise', switchEl(st.removeBackgroundNoise, (x) => set(st, 'removeBackgroundNoise', x), '', { title: 'Strip background noise from the original before converting' })),
        num(st, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px' }), num(st, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'Your plan\'s concurrent request limit' }));
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
        const fmt = h('select', { onchange: (ev) => set(e, 'outputFormat', ev.target.value) }, fmts.map(([k, t]) => h('option', { value: k, selected: k === e.outputFormat }, t)));
        kids.push(grp('Model', ms),
          tag ? grp('Stability', segmented([[0, 'Creative'], [0.5, 'Natural'], [1, 'Robust']], stabVal, (x) => set(e, 'stability', Number(x)))) : slider(e, 'stability', 0, 1, 0.01, 'Stability'),
          slider(e, 'similarity', 0, 1, 0.01, 'Similarity'),
          style ? slider(e, 'style', 0, 1, 0.01, 'Style') : null,
          boost ? grp('Speaker boost', switchEl(e.speakerBoost, (x) => set(e, 'speakerBoost', x), '', { title: 'Closer to the voice; can clip or sound harsh' })) : null,
          slider(e, 'speed', 0.7, 1.2, 0.01, 'Speed'),
          grp('Language', lang), grp('Read numbers ⓘ', norm), grp('Format', fmt),
          num(e, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px', title: 'Same seed + same text + same settings ≈ same take. Empty = random.' }),
          num(e, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'How many lines are requested at once. Match your ElevenLabs plan\'s concurrency limit.' }));
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
    const needs = () => proj.lines.filter((l) => !l.excl && !l.gone && ['Pending', 'Failed', 'Stale'].includes(l.status)).reduce((a, l) => a + l.spoken.length, 0);
    clear(vbar).append(...kids, h('div.spacer'), creditsEl || (creditsEl = creditsButton(needs, 'This project')));
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
    if (!app.state.settings.aiProvider.endpoint) { toast('Set up AI auto-tagging in Global Settings → General & tools first.', true); return; }
    try {
      toast(`Asking the AI for tags on ${ids.length} line(s)…`);
      const r = await rpc('revoice.autotag', { id, lineIds: ids });
      for (const nl of r.lines) replaceLine(nl, false);
      grid.setRows(visible()); renderActions(); renderInspector();
      const bad = r.results.filter((x) => x.error);
      toast(bad.length ? `${r.results.length - bad.length} suggestion(s). ${bad.length} skipped: ${bad[0].error}` : `${r.results.length} suggestion(s) ready — accept (✓) or undo (✕) in each row.`, bad.length > 0);
    } catch (e) { fail(e); }
  }
  function renderTagbar() {
    clear(tagbar);
    if (proj.voiceChanger) {
      tagbar.append(h('span.lbl', 'Voice Changer'), h('span.dim', 'The current recording decides the delivery, so audio tags are not used. The spoken text only feeds the .lip.'), h('div.spacer'), folderButton());
      return;
    }
    const engines = new Set(proj.speakers.filter((sp) => sp.voiceId && !sp.skip).map((sp) => sp.engine || 'elevenlabs'));
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
      (proj.voice.tags || []).map(chip),
      custom,
      h('button.btn.sm', { title: 'Let the AI add ElevenLabs audio tags to the ticked lines (you review them)', onclick: () => { const ids = [...grid.selected]; if (!ids.length) { toast('Tick rows first.', true); return; } autotag(ids); } }, 'AI Auto Tag'),
      h('button.btn.sm.ghost', { title: 'Add a [tag] to every line in the current filter…', onclick: () => bulkTagDialog({
        pool: grid.rows.filter((l) => !l.excl && !l.gone), idOf: (l) => l.id,
        original: (l) => `${l.text} ${l.notes || ''} ${l.emo || ''}`,
        originalLabel: 'in SUBTITLE, ACTOR NOTES or EMOTION', originalPlaceholder: 'Anger, whisper',
        originalNote: 'Searches the in-game subtitle, the actor notes from the plugin and the emotion name (Anger, Fear, Happy…). Separate several with commas.',
        apply: applyTags,
      }) }, 'Add a [tag] to all shown…'),
      engines.size && !engines.has('elevenlabs') ? h('span.dim', 'Your speakers use local engines, which ignore audio tags.') : null,
      h('div.spacer'), folderButton());
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
    const i = proj.lines.findIndex((x) => x.id === nl.id);
    if (i >= 0) proj.lines[i] = nl;
    index.set(nl.id, nl);
    const ri = grid.rows.findIndex((x) => x.id === nl.id);
    if (ri >= 0) grid.rows[ri] = nl;
    if (lastEdit && lastEdit.line.id === nl.id) lastEdit.line = nl;
    if (!refresh) return;
    grid.refreshRow(nl.id);
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
      if (!url) { toast('The current file could not be found.', true); return; }
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
  async function generate(mode, lineIds, { autoplay = false } = {}) {
    autoPlay = null;
    if (autoplay && lineIds && lineIds.length === 1) { const l0 = index.get(lineIds[0]); autoPlay = { lineId: lineIds[0], takes: l0 ? l0.takes.length : 0 }; }
    try {
      const est = await rpc('revoice.estimate', { id, mode, lineIds });
      if (est.unassigned.length && !est.count) { toast('Pick a voice for: ' + est.unassigned.slice(0, 4).join(', ') + (est.unassigned.length > 4 ? '…' : '') + ' (Speakers tab).', true); return; }
      if (!est.count) { toast('Nothing to generate.'); return; }
      const confirmBatch = (app.state.settings.ui || {}).confirmBatch !== 'false';
      if (est.count > 1 && confirmBatch) {
        const ok = await modal('Check before spending credits', [
          h('div.kv', h('div', 'Lines'), h('div', String(est.count)), h('div', est.voiceChanger ? 'Text (for the .lip)' : 'Characters (≈ credits)'), h('div', est.characters.toLocaleString())),
          h('table.tbl', h('thead', h('tr', h('th', 'Voice pack'), h('th', 'Engine'), h('th', 'Lines'), h('th', 'Characters'))),
            h('tbody', est.groups.map((g) => h('tr', h('td', voiceChip(g.voicePack, g.color)), h('td', g.engine), h('td', String(g.lines)), h('td', g.characters.toLocaleString()))))),
          est.unassigned.length ? h('div.banner.warn', { style: { margin: 0 } }, 'Skipped (no voice yet): ' + est.unassigned.join(', ')) : null,
        ], [{ label: 'Cancel', value: false }, { label: `Generate ${est.count} line(s)`, primary: true, value: true }]);
        if (!ok) return;
      }
      await rpc('revoice.generate', { id, mode, lineIds });
      renderActions();
    } catch (e) { fail(e); }
  }

  // ---------------------------------------------------------------- new voice types
  function renderVoiceTypes() {
    const wrap = h('div.view-body');
    wrap.append(
      h('div.help', { style: { maxWidth: '1100px', marginBottom: '12px' } },
        h('p', h('b', 'Only when you need it. '), 'Replacing files in place needs no plugin. A new voice type is for one NPC who shares a vanilla voice type (Aela uses FemaleCommander with Uthgerd, Rayya, Legate Rikke…) and should sound different in all of their lines, including greetings and combat.'),
        h('p', 'The tool writes an ESL plugin with only the voice type record, a SkyPatcher file that gives it to the NPC, and a FormList Manipulator file that adds it to the lists that unlock dialogue — no NPC or list overrides, so other mods\' edits to the NPC keep working. Then every line the NPC can say is generated under the new voice type.')),
      h('div.row', { style: { marginBottom: '12px' } }, h('button.btn.primary', { title: 'Pick the NPC to give a voice type of their own', disabled: !proj.lastScan, onclick: newPlan }, '+ Give an NPC a new voice type…'),
        !proj.lastScan ? h('span.faint', 'Scan first.') : null,
        h('div.spacer'),
        h('span.faint', 'Plugin: ', h('span.mono', proj.voiceTypePlugin)),
        h('button.btn.sm', { title: 'Name of the ESL plugin that holds the voice types', onclick: async () => { const r = await prompt('Voice type plugin', [{ name: 'n', label: 'File name', value: proj.voiceTypePlugin }]); if (r && r.n.trim()) save({ voiceTypePlugin: r.n.trim() }); } }, 'Rename…'),
        switchEl(proj.referenceByEditorId, (v) => save({ referenceByEditorId: v }), 'Refer by EditorID', { title: 'On: SkyPatcher / FLM lines use EditorIDs (readable, as Aela Revoiced does). Off: Plugin|FormID.' })));
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
    ], [{ label: 'Close', value: false }, { label: 'Save', primary: true, value: true }], { wide: true });
    if (!ok) return;
    try {
      proj = await rpc('revoice.planSave', { id, planId, editorId: ed.value.trim(), npcs: [...npcOn], lists: [...listOn], lineSet, extraNpcs: extraBox ? extraBox.value.split('\n').map((x) => x.trim()).filter(Boolean) : [] });
      reindex(); renderAll();
      toast('Saved. Rescan to update the plan\'s lines.');
    } catch (e) { fail(e); }
  }

  // ---------------------------------------------------------------- layout
  function renderAll() {
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
    body.append(vbar, filters, tagbar, split, actions);
    renderFilters();
    renderTagbar();
    grid.setRows(visible());
    if (scroll != null) { grid.el.scrollTop = scroll; grid.paint(true); }
    renderInspector();
    renderActions();
  }

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
    const d = e.detail || {};
    if (d.kind === 'revoice-orphans' && d.id === id) { if (d.message) toast(d.message, !d.ok); if (d.ok) orphansModal(); }
    if ((d.kind === 'revoice-scan' && d.id === id) || (d.kind === 'generate' && d.revoiceId === id) || (d.kind === 'revoice-export' && d.id === id)) {
      try { proj = await rpc('revoice.get', { id }); reindex(); } catch { /* keep */ }
      if (d.kind === 'revoice-scan' && d.ok) { if (proj.lines.length && tab === 'setup') tab = 'speakers'; toast(`Scan done: ${d.report.lines.toLocaleString()} voice files to make, ${d.report.withOriginal.toLocaleString()} with a current file.`); }
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
