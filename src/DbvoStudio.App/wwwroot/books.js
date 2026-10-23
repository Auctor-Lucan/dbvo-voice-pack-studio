// Other Tools → Voiced Books Tool: narrate Skyrim's books, letters and journals (or a mod's) for Voiced Books of Skyrim.
// Books are split into parts the engine can read, each part gets takes, and export joins them, adds the IVBOS
// reverb and loudness chain, encodes xWMA and packs archives, the VBoS .ini and a FOMOD.
import { flashRow, TOOL_TIPS, popover, rpc, on, h, clear, toast, fail, modal, confirmBox, prompt, store, VGrid, debounce, aiReady, switchEl, segmented, popupMenu, columnsMenu, droppedPaths, icon, mergeInto, helpButton, generateButton, outputFormats, keepScroll, withBusy } from './lib.js';
import { app, go, refreshState } from './app.js';
import { insertTagAtCaret } from './tagtools.js';
import { creditsButton, LANGS } from './phase2.js';

const CERT = { Confirmed: 'CONFIRMED', Unverified: 'UNVERIFIED', Inferred: 'INFERRED', None: '' };
const CERT_TIP = {
  Confirmed: 'Confirmed: by you, by the built-in set, or by UESP linking the author to the NPC record',
  Unverified: 'Unverified: the text names the author or sender and an NPC with that name exists, but nothing else proves it is the same person',
  Inferred: 'Inferred: a guess (a name in the title, several NPCs with the name, a template with several voice types). Check before generating',
};
const SRC = { 'by-line': 'a "By …" line', signature: 'the signature', uesp: 'UESP\'s book page', title: 'the title', set: 'the built-in set', user: 'you' };
const DOT = { ready: 'Generated', stale: 'Stale', partial: 'Partial', missing: 'Pending', none: 'Omitted' };
const DOT_TIP = { ready: 'Ready: every part has a current take', stale: 'Edited since take: a part\'s text changed after its take', partial: 'Some parts have takes', missing: 'No takes yet', none: 'Not read by this voice' };
const ENGINE_NAMES = { elevenlabs: 'ElevenLabs', voxcpm: 'VoxCPM (local)', omnivoice: 'OmniVoice (local)' };

// ------------------------------------------------------------------ list

export async function booksListView() {
  const s = await refreshState();
  const [list, sets] = await Promise.all([rpc('books.list'), rpc('books.sets').catch(() => [])]);
  const view = h('div.view');
  view.append(h('div.view-head',
    h('h1', 'Voiced Books Tool'),
    helpButton('voiced-books-tool'),
    h('span.sub', 'Other Tools · narrate books, letters and journals for Voiced Books of Skyrim: the base game from the built-in IVBOS texts, or any mod\'s books read from its plugins.'),
    h('div.spacer'),
    h('button.btn.primary', { title: 'Start a Voiced Books project', onclick: () => create(sets) }, '+ New project')));
  const body = h('div.view-body');
  view.append(body);
  if (!s.settings.baseGameFolder) {
    body.append(h('div.banner.warn', h('span', 'Set the base game folder (Skyrim Special Edition\\Data) in Global Settings → General: masters, NPC records (for authors\' voice types) and the game\'s strings are read from there.'),
      h('div.spacer'), h('button.btn.sm', { title: 'Open Global Settings', onclick: () => go('settings', { tab: 'general' }) }, 'Open Global Settings')));
  }
  if (!list.length) {
    body.append(h('div.empty', h('h2', 'No Voiced Books projects yet'),
      h('div', 'Pick the built-in Skyrim + DLC set to narrate the base game\'s books with your own narrator voice, or start from a mod\'s plugins.'),
      h('div', { style: { marginTop: '14px' } }, h('button.btn.primary', { title: 'Start a Voiced Books project', onclick: () => create(sets) }, '+ New project'))));
    return view;
  }
  body.append(h('div.cat', h('div.cat-head', h('h3', 'Voiced Books projects'), h('span.faint', list.length)),
    h('div.rows3', list.map((x) => h('div.prow', { title: 'Open this project', onclick: () => go('book', { id: x.id }) },
      h('div.row', h('span.title.ellipsis.grow', x.name), x.source !== 'plugins' ? h('span.badge.b-fixed', { title: 'Built-in text set' }, 'built-in set') : null),
      h('div.faint.ellipsis', { title: x.targets.join(', ') }, x.source !== 'plugins' ? 'Skyrim, Dawnguard, Hearthfire, Dragonborn' : (x.targets.join(', ') || 'no plugins yet')),
      h('div.prow-stats',
        h('div.grow', h('div.bar' + (x.lines && x.generated >= x.lines ? '.ok' : ''), h('div.bar-fill', { style: { width: (x.lines ? (100 * x.generated) / x.lines : 0) + '%' } }))),
        h('span.mono', { title: 'Parts with a take / all parts' }, `${x.generated} / ${x.lines}`),
        h('span.left', h('b', String(x.books)), ' books · ', h('b', String(x.narrators)), ' narrator' + (x.narrators === 1 ? '' : 's'))))))));
  return view;
}

async function create(sets) {
  let source = sets.length ? sets[0].id : 'plugins';
  const name = h('input', { type: 'text', placeholder: 'e.g. My narrator - Skyrim books', style: { width: '100%' } });
  const narrator = h('input', { type: 'text', value: 'Narrator', placeholder: 'e.g. Female Narrator - Vampira', style: { width: '100%' }, title: 'Shown in the archive names: "<prefix> <mod> - <narrator>"' });
  const modName = h('input', { type: 'text', placeholder: 'e.g. VIGILANT', style: { width: '100%' }, title: 'Used in archive names and the .ini name' });
  const modRow = h('div.col', h('div.field-label', 'Mod name (for archive and .ini names)'), modName);
  const pick = (v) => { source = v; modRow.classList.toggle('hidden', v !== 'plugins'); };
  const opts = [...sets.map((st) => [st.id, `${st.name} · ${st.books} books`, st.description]), ['plugins', 'From a mod\'s plugins', 'Read the BOOK records of any plugin, with its strings and archives']];
  const choose = h('div.col', opts.map(([v, label, sub]) => h('label.row', { style: { alignItems: 'flex-start', gap: '8px', padding: '6px 0' } },
    h('input', { type: 'radio', name: 'bsrc', checked: v === source, onchange: () => pick(v) }), h('div', h('b', label), h('div.faint', sub)))));
  pick(source);
  const ok = await modal('New Voiced Books project', [
    h('div.col', h('div.field-label', 'Name'), name),
    h('div.col', h('div.field-label', 'Texts'), choose),
    modRow,
    h('div.col', h('div.field-label', 'First narrator'), narrator, h('div.faint', 'You can add more narrators later: each gets its own archive, so players can pick one in the installer.')),
  ], [{ label: 'Cancel', value: false }, { label: 'Create', primary: true, value: true }], { help: 'voiced-books-tool' });
  if (!ok) return;
  try {
    const set = sets.find((x) => x.id === source);
    const c = await rpc('books.create', { name: name.value.trim() || (set ? set.name : 'Voiced books'), source, narrator: narrator.value.trim(), modName: modName.value.trim() });
    go('book', { id: c.id });
    if (source !== 'plugins') rpc('books.scan', { id: c.id }).catch(() => { /* works without the base game */ });
  } catch (e) { fail(e); }
}

// ------------------------------------------------------------------ project

export async function bookProjectView(id) {
  await refreshState();
  let proj = await withBusy(rpc('books.get', { id }), 'Loading books', 'Reading the books, parts and takes…');
  let tab = store.get('bk-tab:' + id, 'books');
  let showInsp = store.get('bk-insp', true);
  let filter = store.get('bk-filter:' + id, 'all'), search = '', voiceFilter = '';
  let focused = null;
  const audio = new Audio();
  let index = new Map();
  const reindex = () => { index = new Map(proj.books.map((b) => [b.stem, b])); };
  reindex();

  const view = h('div.view.work');
  const head = h('div.view-head');
  const creditsSlot = h('span.credits-slot');
  const body = h('div', { style: { flex: '1', minHeight: '0', display: 'flex', flexDirection: 'column' } });
  view.append(head, body);

  const allVoices = () => [...proj.narrators.map((n) => ({ key: n.key, name: n.name, kind: 'narrator' })), ...proj.voiceTypes.map((v) => ({ key: v.key, name: v.voiceType, kind: 'vt' }))];

  // ---------------------------------------------------------------- head
  function renderHead() {
    const exp = h('button.btn.accent.export-btn', { title: 'Join the parts, add reverb and loudness, encode xWMA and pack the archives, .ini and installer', disabled: !!app.job }, icon('export', { size: 15 }), ' Export ▾');
    exp.addEventListener('click', () => popupMenu(exp, [
      { label: 'Export everything', sub: `Archives (${proj.packaging === 'loose' ? 'loose files' : 'BSA + plugin'}) per narrator${proj.voiceTypes.length && proj.npcVoices ? ' and for NPC voices' : ''}, ${proj.writeIni ? proj.iniFile + ', ' : ''}topic file, validation report and a FOMOD installer → ${proj.outputFolder || '(choose the output folder first)'}`, onclick: () => doExport(true) },
      { label: 'Export without the installer', sub: 'The same, but no fomod folder', onclick: () => doExport(false) },
      { label: 'Output folder…', sub: proj.outputFolder || 'Not set yet', onclick: pickOutput },
      proj.outputFolder ? { label: 'Open the output folder', sub: proj.outputFolder, onclick: () => rpc('open.path', { path: proj.outputFolder }).catch(fail) } : null,
      proj.lastExportUtc ? { label: 'Open the installer in the FOMOD builder', sub: 'Phase 3: pack it into a .zip / .7z, add pages or images', onclick: openInFomod } : null,
    ].filter(Boolean)));
    const tabs = segmented([
      ['setup', 'Setup', 'Texts, plugins, output and audio settings'],
      ['voices', `Narrators & authors (${proj.narrators.length + proj.voiceTypes.length})`, 'Who reads: your narrators, and the voice types of NPCs who wrote letters and journals'],
      ['books', `Books (${proj.books.filter((b) => !b.excl && !b.gone).length})`, 'Every book: edit the spoken text, generate, listen'],
    ], tab, (v) => { tab = v; store.set('bk-tab:' + id, v); renderAll(); });
    clear(head).append(
      h('button.btn.ghost.sm', { title: 'Back to the project list', onclick: () => go('books') }, '← Voiced Books'),
      h('h1', proj.name),
      tabs,
      helpButton({ setup: 'books-setup', voices: 'narrators-and-authors', books: 'books-and-the-editor' }[tab] || 'voiced-books-tool'),
      h('div.spacer'),
      creditsSlot,
      h('button.btn', { title: proj.source === 'plugins' ? 'Read the plugins again and rebuild the book list (keeps your edits and takes)' : 'Read the game\'s own text for the editor and the checks, and pick up an updated built-in set', disabled: !!app.job, onclick: scan }, proj.lastScan ? '⟳ Rescan' : 'Scan'),
      exp,
      h('button.btn.ghost.danger.sm', { title: 'Delete this project and its takes (exported files stay)', onclick: async () => { if (await confirmBox('Delete Voiced Books project?', 'Deletes its takes and joined audio too. Exported files stay where they are.', 'Delete', true)) { try { await rpc('books.delete', { id }); go('books'); } catch (e) { fail(e); } } } }, 'Delete'));
    const needs = () => proj.narrators.concat(proj.voiceTypes).reduce((a, v) => a + ((v.stats && v.stats.charactersLeft) || 0), 0);
    clear(creditsSlot).append(creditsButton(needs, 'This project'));
  }

  async function save(o) {
    try { proj = await rpc('books.save', { id, ...o }); reindex(); renderAll(); } catch (e) { fail(e); renderHead(); }
  }
  async function scan() {
    try { await rpc('books.scan', { id }); toast('Reading the plugins… the first read of Skyrim.esm takes a few seconds.'); } catch (e) { fail(e); }
  }
  async function pickOutput() {
    const f = await rpc('pick.folder', { title: 'Output folder for the voiced books' });
    if (f) save({ outputFolder: f });
  }
  async function doExport(fomod) {
    if (!proj.outputFolder) { toast('Choose the output folder first (Export ▾ → Output folder…).', true); return; }
    const missing = allVoices().filter((v) => v.kind === 'narrator' && !proj.narrators.find((n) => n.key === v.key).voiceId);
    try { await rpc('books.export', { id, fomod }); } catch (e) { fail(e); }
    if (missing.length) toast(`${missing.map((x) => x.name).join(', ')} has no voice yet: books without takes are listed in the report.`, true);
  }
  async function openInFomod() {
    try { const r = await rpc('fomod.import', { path: proj.outputFolder, books: true }); go('fomod', { id: r.project.project.id }); } catch (e) { fail(e); }
  }

  // ---------------------------------------------------------------- setup
  function renderSetup() {
    const wrap = h('div.view-body.reading');
    const sec = (title, help, extra, ...rows) => h('div.section', h('div.section-head', h('h2', title), help ? helpButton(help) : null, h('div.spacer'), extra), h('div.section-body', rows));
    const srow = (label, help, control) => h('div.srow', h('div.lbl', h('span', label), help ? h('small', help) : null), h('div', { title: typeof help === 'string' ? help : label }, control));
    const text = (val, onSet, o = {}) => { const i = h('input', { type: 'text', value: val || '', placeholder: o.placeholder || '', style: { width: o.width || '100%' }, title: o.title || '' }); i.addEventListener('change', () => onSet(i.value.trim())); return i; };
    const num = (val, onSet, o = {}) => { const i = h('input', { type: 'number', value: val, min: o.min, max: o.max, step: o.step || 'any', style: { width: o.width || '90px' }, title: o.title || '' }); i.addEventListener('change', () => onSet(Number(i.value))); return i; };
    const ls = proj.lastScan;

    // Texts: the built-in set, or the plugins to read.
    let textsBody;
    if (proj.source !== 'plugins') {
      textsBody = h('div.col', { style: { padding: '10px 0', gap: '8px' } },
        h('div', h('b', proj.setName || proj.source), proj.setVersion ? h('span.faint', `  · set version ${proj.setVersion}`) : null),
        proj.setCredit ? h('div.faint', proj.setCredit) : null,
        h('div.faint', 'The spoken texts, which books are read by NPC voices and the USSEP variants come with the set. You only add narrator voices (and voices for the NPC voice types you want). Edits you make are kept; when a newer set changes a book you edited, the book shows "updated text available".'),
        proj.baseGameFolder
          ? (ls ? h('div.stat-row', h('span', h('b', ls.books.toLocaleString()), ' books'), h('span', h('b', ls.npc.toLocaleString()), ' read by NPC voices'), h('span.faint', 'game text read ' + new Date(ls.scannedUtc).toLocaleString()))
            : h('div.banner.info', { style: { margin: 0 } }, h('span', 'Scan once to show the game\'s own text next to each spoken text, and for the checks.'), h('div.spacer'), h('button.btn.sm', { title: 'Read the game text', disabled: !!app.job, onclick: scan }, 'Scan')))
          : h('div.banner.warn', { style: { margin: 0 } }, 'Without the base game folder the editor shows only the spoken text, and the checks against the game text are off. Generating and exporting work.'));
    } else {
      const drop = h('div.dropzone', h('div', h('b', 'Drop plugins (.esp / .esm / .esl), their .bsa archives or a mod folder here')),
        h('div.faint', 'Books defined or changed by the plugins you tick are read. Masters only provide NPC records and voice types. Strings come from a Strings folder or an archive next to the plugin.'),
        h('div.row', { style: { justifyContent: 'center', marginTop: '10px' } },
          h('button.btn.sm', { title: 'Add plugins', onclick: async () => add(await rpc('pick.files', { title: 'Plugins with books', filter: 'Plugins|*.esp;*.esm;*.esl' })) }, 'Add plugins…'),
          h('button.btn.sm', { title: 'Add a mod folder', onclick: async () => { const f = await rpc('pick.folder', { title: 'Mod folder' }); if (f) add([f]); } }, 'Add folder…')));
      drop.addEventListener('dragover', (e) => { e.preventDefault(); drop.classList.add('over'); });
      drop.addEventListener('dragleave', () => drop.classList.remove('over'));
      drop.addEventListener('drop', async (e) => { e.preventDefault(); e.stopPropagation(); drop.classList.remove('over'); try { add(await droppedPaths([...(e.dataTransfer.files || [])])); } catch (err) { fail(err); } });
      textsBody = h('div.col', { style: { padding: '10px 0' } }, drop,
        ls ? h('div.stat-row', h('span', h('b', ls.books.toLocaleString()), ' books'), h('span', h('b', String(ls.records)), ' records'), ls.merged ? h('span', { title: 'Records with exactly the same text share one audio file' }, h('b', String(ls.merged)), ' shared texts') : null,
          h('span', h('b', String(ls.excluded)), ' left out'), h('span', h('b', String(ls.npc)), ' read by NPC voices'), h('span.faint', 'scanned ' + new Date(ls.scannedUtc).toLocaleString())) : h('div.dim', 'Not scanned yet. Add the plugins, then Scan.'),
        ls && ls.missingMasters.length ? h('div.banner.warn', { style: { margin: 0 } }, 'Missing masters: ' + ls.missingMasters.join(', ') + '. NPC records from them are unknown, so authors there can\'t be matched. Drop them in, or set the base game folder.') : null,
        ls && ls.missingStrings.length ? h('div.banner.warn', { style: { margin: 0 } }, 'No strings for localized plugin(s): ' + ls.missingStrings.join(', ') + '. Their books have no text. Add the Strings folder or the archive that holds them.') : null,
        h('table.tbl', h('thead', h('tr', h('th', 'Plugin'), h('th', { title: 'Read this plugin\'s books (otherwise it only provides masters and NPCs)' }, 'Read its books'), h('th', ''))),
          h('tbody', proj.plugins.map((pl) => h('tr',
            h('td', h('div.mono', pl.fileName, pl.baseGame ? h('span.badge.b-skip', { style: { marginLeft: '6px' } }, 'base game') : null, !pl.exists ? h('span.badge.b-broken', { style: { marginLeft: '6px' } }, 'missing') : null), h('div.faint.ellipsis', { title: pl.path }, pl.path)),
            h('td', switchEl(pl.target, (v) => setPlugin(pl, { target: v }), '', { title: 'Read this plugin\'s books' + (pl.baseGame ? ' (the base game has about a thousand)' : '') })),
            h('td', pl.baseGame ? null : h('button.icon-btn', { title: 'Remove from the project', onclick: () => setPlugin(pl, { remove: true }) }, '×')))))));
    }

    const a = proj.audio;
    const setAudio = (k, v) => { a[k] = v; save({ audio: a }); };
    const audioRows = h('div.col', { style: { gap: '0' } },
      srow('Reverb', 'Audacity\'s reverb with the IVBOS settings, on every part. The tail of a part rings on under the next one, so joins sound like one recording.', h('div.row', switchEl(a.reverb, (v) => setAudio('reverb', v), 'On', { title: 'Add the IVBOS reverb' }),
        h('details.inline-details', h('summary', { title: 'Show the reverb settings' }, 'Settings'), h('div.row.wrap', { style: { gap: '10px', marginTop: '6px' } },
          ...[['reverbRoomSize', 'Room size %'], ['reverbPreDelayMs', 'Pre-delay ms'], ['reverbReverberance', 'Reverberance %'], ['reverbHfDamping', 'Damping %'], ['reverbToneLow', 'Tone low %'], ['reverbToneHigh', 'Tone high %'], ['reverbWetGainDb', 'Wet dB'], ['reverbDryGainDb', 'Dry dB']]
            .map(([k, l]) => h('label.col', { style: { gap: '2px' } }, h('span.faint', l), num(a[k], (v) => setAudio(k, v), { width: '80px' }))))))),
      srow('Pauses', 'Silence added where two parts meet: after a paragraph, or after a sentence inside one. The book ends with silence, as the Audacity macros do.', h('div.row',
        h('span.faint', 'Paragraph'), num(a.paragraphPauseMs, (v) => setAudio('paragraphPauseMs', v), { min: 0, max: 2000, width: '76px', title: 'Milliseconds (250–400 sounds natural)' }), h('span.faint', 'ms · Sentence'),
        num(a.sentencePauseMs, (v) => setAudio('sentencePauseMs', v), { min: 0, max: 2000, width: '76px' }), h('span.faint', 'ms · End'), num(a.endSilenceMs, (v) => setAudio('endSilenceMs', v), { min: 0, max: 5000, width: '76px' }), h('span.faint', 'ms'))),
      srow('Loudness', 'One loudness pass over the whole book, then the limiter: the IVBOS "LOUD" chain (−12 LUFS, limiter at −3 dB with output −1 dB). A narrator set to Normal skips the loudness push.', h('div.row',
        num(a.loudnessLufs, (v) => setAudio('loudnessLufs', v), { min: -40, max: 0, width: '76px', title: 'Integrated loudness in LUFS (0 = leave as it is)' }), h('span.faint', 'LUFS · Limiter'),
        switchEl(a.limiter, (v) => setAudio('limiter', v), '', { title: 'Peak limiter' }), num(a.limiterThresholdDb, (v) => setAudio('limiterThresholdDb', v), { width: '70px', title: 'Threshold dB' }), h('span.faint', 'dB → output'),
        num(a.limiterOutputDb, (v) => setAudio('limiterOutputDb', v), { width: '70px', title: 'Output (make-up target) dB' }), h('span.faint', 'dB'))),
      srow('xWMA bit rate', 'The .xwm files VBoS plays. 48 kbps is what IVBOS uses.', h('div.row', h('select', { title: 'Bit rate', onchange: (e) => setAudio('xwmBitrate', Number(e.target.value)) }, [32000, 48000, 64000, 96000, 160000].map((b) => h('option', { value: b, selected: b === a.xwmBitrate }, `${b / 1000} kbps`))),
        proj.xwma ? h('span.faint', 'xWMAEncode.exe found') : h('span', { style: { color: 'var(--warn)' } }, 'xWMAEncode.exe is not set (Global Settings → General & tools → Tools): needed to export'))));

    wrap.append(
      sec(proj.source !== 'plugins' ? 'Texts' : 'Plugins', proj.source !== 'plugins' ? 'the-built-in-skyrim-dlc-set' : 'books-from-a-mod', proj.source === 'plugins' ? h('button.btn.primary.sm', { title: 'Read the plugins and build the book list', disabled: !!app.job, onclick: scan }, ls ? '⟳ Rescan' : 'Scan') : null, textsBody),
      sec('Names and output', 'export-and-the-installer', null,
        srow('Project name', null, text(proj.name, (v) => save({ name: v || proj.name }))),
        srow('Mod name', 'In archive names: "<prefix> <mod name> - <narrator>".', text(proj.modName, (v) => save({ modName: v }), { placeholder: 'e.g. VIGILANT' })),
        srow('Archive prefix', 'The first word of every archive name, e.g. VBOS or IVBOS.', text(proj.archivePrefix, (v) => save({ archivePrefix: v }), { width: '160px' })),
        srow('Archives', 'One per narrator (players pick one in the installer)' + (proj.npcVoices ? ', plus one for the NPC voices.' : '.'), h('div.col', { style: { gap: '2px' } }, proj.archives.length ? proj.archives.map((x) => h('span.mono', x)) : h('span.faint', 'Add a narrator first.'))),
        srow('.ini name', 'Voiced Books of Skyrim reads every *_VBOS.ini in Data. Use the plugin\'s name, e.g. Vigilant → Vigilant_VBOS.ini.', h('div.row', text(proj.iniName, (v) => save({ iniName: v }), { width: '220px', placeholder: 'e.g. Vigilant' }), h('span.mono.faint', proj.iniFile))),
        srow('Output folder', 'Export writes the archives, VBOS INI\\, the topic file, the validation report and fomod\\ here. Use an empty folder.', h('div.path-input', text(proj.outputFolder, (v) => save({ outputFolder: v }), { placeholder: 'e.g. D:\\Releases\\My Voiced Books' }), h('button.btn.sm', { title: 'Choose the folder', onclick: pickOutput }, 'Browse'))),
        srow('Packaging', 'BSA: one archive and a small plugin that loads it, per narrator (like the IVBOS release). Loose: Sound\\fx\\voicedbooksofskyrim folders.', segmented([['bsa', 'BSA + plugin', 'One archive per narrator, loaded by an empty light plugin'], ['loose', 'Loose files', 'Plain .xwm files in Sound folders']], proj.packaging, (v) => save({ packaging: v }))),
        srow('NPC voices', 'On: letters and journals written by NPCs are read in their voice type\'s voice. Off ("narrated books only"): those books are left out and no .ini is needed for them — for players who keep IVBOS\'s NPC voices and install it with its "Only NPC Voices" option.',
          switchEl(proj.npcVoices, (v) => save({ npcVoices: v }), proj.npcVoices ? 'Read NPC-written books in NPC voices' : 'Narrated books only', { title: 'Books written by NPCs get the NPC\'s voice type voice' })),
        srow('Write the .ini', 'Off when another mod\'s .ini already maps these books to the same file names (e.g. IVBOS installed alongside).', switchEl(proj.writeIni, (v) => save({ writeIni: v }), '', { title: 'Write the VBoS .ini' }))),
      sec('Parts', 'splitting-into-parts', null,
        srow('Part length', `Long books are read in parts and joined at export. Parts break at paragraphs first, then at sentence ends — never inside a sentence. The longest part this model accepts: ${proj.ceiling.toLocaleString()} characters.`, h('div.row',
          h('span.faint', 'About'), num(proj.partTarget, (v) => save({ partTarget: v }), { min: 200, step: 100, width: '90px', title: 'Wanted part length in characters' }), h('span.faint', 'characters, at most'), num(proj.partMax, (v) => save({ partMax: v }), { min: 200, step: 100, width: '90px', title: 'Longest part (soft limit)' }))),
        srow('Neighbouring text', 'Sends the end of the previous part and the start of the next with each request, so the voice keeps its flow across joins. ElevenLabs only; not available on eleven_v3.', switchEl(proj.sendContext, (v) => save({ sendContext: v }), '', { title: 'Request stitching (previous_text / next_text)' })),
        srow('Spoken-text rules', 'New books start with the IVBOS rules applied: title and "By" separators, asides as dashes, "Vol. II" as "Volume Two", regnal numbers and dates in words. Ellipses and ALL-CAPS emphasis are yours (or the AI\'s) to add.', switchEl(proj.autoSpokenRules, (v) => save({ autoSpokenRules: v }), '', { title: 'Apply the spoken-text rules to new books' }))),
      sec('Audio', 'the-audio-chain', h('button.btn.sm.ghost', { title: 'Back to the IVBOS release settings', onclick: () => save({ audio: { ...proj.audio, reverb: true, reverbPreDelayMs: 99, reverbRoomSize: 81, reverbReverberance: 23, reverbHfDamping: 62, reverbToneLow: 16, reverbToneHigh: 19, reverbWetGainDb: -4, reverbDryGainDb: 0, paragraphPauseMs: 300, sentencePauseMs: 150, endSilenceMs: 1000, loudnessLufs: -12, limiter: true, limiterThresholdDb: -3, limiterOutputDb: -1, limiterLookaheadMs: 5, limiterReleaseMs: 50, extraGainDb: 0, xwmBitrate: 48000 } }) }, 'IVBOS defaults'), audioRows),
      proj.source === 'plugins' ? sec('Options', null, null,
        srow('Base game', proj.baseGameFolder ? 'Skyrim.esm and the DLCs (masters and NPC records) from ' + proj.baseGameFolder : 'Set the base game folder in Global Settings → General first.',
          h('div.row', switchEl(proj.useBaseGame, (v) => save({ useBaseGame: v }), 'Use the base game', { title: 'Read the vanilla masters and NPCs' }), h('button.btn.sm', { title: 'Look for the base game files again', onclick: () => save({ refreshBaseGame: true }) }, 'Refresh'))),
        srow('Language', 'For localized plugins\' strings (Skyrim_English.dlstrings …).', text(proj.language, (v) => save({ language: v || 'English' }), { width: '160px' }))) : null);
    return wrap;
  }

  async function add(paths) {
    if (!paths || !paths.length) return;
    try { const r = await rpc('books.addFiles', { id, paths }); proj = r.project; reindex(); renderAll(); toast(r.added.length ? `Added ${r.added.length}. Scan when you are ready.` : 'Nothing new to add.'); } catch (e) { fail(e); }
  }
  async function setPlugin(pl, o) { try { proj = await rpc('books.plugin', { id, path: pl.path, ...o }); reindex(); renderAll(); } catch (e) { fail(e); } }

  // ---------------------------------------------------------------- voices (narrators and authors)
  const voiceLists = { elevenlabs: null, voxcpm: null, omnivoice: null };
  let elevenModels = null, loadingVoices = false;
  async function loadVoices(refresh = false) {
    if (loadingVoices) return;
    loadingVoices = true;
    const used = new Set(['elevenlabs', ...proj.narrators.map((n) => n.engine || 'elevenlabs'), ...proj.voiceTypes.map((v) => v.engine || 'elevenlabs')]);
    try {
      await Promise.all([...used].map(async (eng) => {
        if (!refresh && voiceLists[eng]) return;
        try {
          voiceLists[eng] = eng === 'elevenlabs'
            ? (await rpc('eleven.voices', { refresh })).map((v) => ({ id: v.voiceId, name: v.name, sub: v.category }))
            : (await rpc('engine.voices', { engine: eng })).map((v) => ({ id: v, name: v }));
        } catch { voiceLists[eng] = []; }
      }));
      if (!elevenModels || refresh) elevenModels = await rpc('eleven.models', { refresh }).catch(() => []);
    } finally { loadingVoices = false; }
    if (tab === 'voices' || tab === 'books') renderAll();
  }

  /** Engine + voice in one cell. kind: narrator | vt */
  function voicePick(row, kind) {
    const eng = row.engine || 'elevenlabs';
    const list = voiceLists[eng];
    const engSel = h('select', { title: 'Engine for this voice' }, Object.entries(ENGINE_NAMES).map(([k, t]) => h('option', { value: k, selected: k === eng }, t)));
    engSel.addEventListener('change', () => setVoice(row, kind, { engine: engSel.value, voiceId: '' }).then(loadVoices));
    const vSel = h('select', { title: 'The voice that reads these books', style: { maxWidth: '220px' } },
      h('option', { value: '' }, list == null ? 'Loading voices…' : list.length ? '— pick a voice —' : eng === 'elevenlabs' ? 'No voices (API key?)' : 'Server offline?'),
      (list || []).map((v) => h('option', { value: v.id, selected: v.id === row.voiceId }, v.name)));
    if (row.voiceId && list && !list.some((v) => v.id === row.voiceId)) vSel.append(h('option', { value: row.voiceId, selected: true }, `${row.voiceName || row.voiceId} — voice not found`));
    vSel.addEventListener('change', () => { const v = (list || []).find((x) => x.id === vSel.value); setVoice(row, kind, { engine: eng, voiceId: vSel.value, voiceName: v ? v.name : vSel.value }); });
    return h('span.row.voice-cell', { style: { gap: '4px' } }, engSel, vSel);
  }
  async function setVoice(row, kind, o) {
    try {
      proj = await rpc('books.voice', kind === 'narrator' ? { id, kind, narratorId: row.id, ...o } : { id, kind, voiceType: row.voiceType, ...o });
      reindex(); renderAll();
    } catch (e) { fail(e); }
  }

  let voiceEdits = 0;
  const saveVoiceSettings = debounce(async () => {
    const seq = voiceEdits;
    try { const r = await rpc('books.save', { id, voice: proj.voice }); if (seq === voiceEdits) { mergeInto(proj.voice, r.voice); proj.ceiling = r.ceiling; } } catch (e) { fail(e); }
  }, 300);

  /** The shared engine settings as one toolbar row (same controls as Phase 2 and NPC Revoice). */
  function settingsRow() {
    const v = proj.voice;
    const set = (obj, k, val) => { obj[k] = val; voiceEdits++; saveVoiceSettings(); };
    const grp = (label, ...kids) => h('div.tgroup', { title: TOOL_TIPS[label] || '' }, h('label', label), h('div.row', ...kids));
    const slider = (obj, k, min, max, step, label) => {
      const val = h('span.mono.dim', { style: { width: '34px' } }, Number(obj[k]).toFixed(2));
      const r = h('input', { type: 'range', min, max, step, value: obj[k], style: { width: '84px' } });
      r.addEventListener('input', () => { val.textContent = Number(r.value).toFixed(2); });
      r.addEventListener('change', () => set(obj, k, Number(r.value)));
      return grp(label, r, val);
    };
    const num = (obj, k, label, o = {}) => {
      const i = h('input', { type: 'number', value: obj[k] ?? '', placeholder: o.placeholder || '', step: o.step || 'any', min: o.min, title: o.title || '', style: { width: o.width || '72px' } });
      i.addEventListener('change', () => set(obj, k, i.value === '' ? (o.nullable ? null : 0) : Number(i.value)));
      return grp(label, i);
    };
    const used = new Set([...proj.narrators, ...proj.voiceTypes].filter((x) => x.voiceId).map((x) => x.engine || 'elevenlabs'));
    const eng = used.size === 1 ? [...used][0] : 'elevenlabs';
    const kids = [];
    if (eng === 'elevenlabs') {
      const e = v.eleven;
      const tag = /^eleven_v[34]/i.test(e.modelId || '');
      const mi = (elevenModels || []).find((x) => x.modelId === e.modelId) || {};
      const style = mi.canUseStyle != null ? mi.canUseStyle && !tag : !tag;
      const ms = h('select', { onchange: (ev) => { set(e, 'modelId', ev.target.value); setTimeout(() => rpc('books.get', { id }).then((p) => { proj = p; reindex(); renderAll(); }), 400); } },
        (elevenModels && elevenModels.length ? elevenModels : [{ modelId: e.modelId }]).map((x) => h('option', { value: x.modelId, selected: x.modelId === e.modelId }, x.modelId + (/^eleven_v[34]/i.test(x.modelId) ? ' · tags' : ''))));
      const stabVal = e.stability < 0.25 ? 0 : e.stability < 0.75 ? 0.5 : 1;
      const lang = h('select', { title: 'Force the language (ISO 639-1). Auto lets the model detect it.', onchange: (ev) => set(e, 'languageCode', ev.target.value) }, LANGS.map(([k, t]) => h('option', { value: k, selected: k === (e.languageCode || '') }, t)));
      const norm = h('select', { title: 'Whether ElevenLabs writes out numbers, dates and abbreviations before speaking', onchange: (ev) => set(e, 'textNormalization', ev.target.value) }, [['auto', 'Auto'], ['on', 'On'], ['off', 'Off']].map(([k, t]) => h('option', { value: k, selected: k === (e.textNormalization || 'auto') }, t)));
      const fmt = h('select', { onchange: (ev) => set(e, 'outputFormat', ev.target.value) }, outputFormats(e.outputFormat).map(([k, t]) => h('option', { value: k, selected: k === e.outputFormat }, t)));
      const out = h('button.btn.sm', { title: 'Format, seed and how many parts are requested at once' }, `${({ mp3_44100_128: 'MP3 128', mp3_44100_192: 'MP3 192', pcm_44100: 'PCM 44.1k' })[e.outputFormat] || 'Output'} ▾`);
      out.onclick = () => popover(out, () => [grp('Format', fmt), num(e, 'seed', 'Seed', { placeholder: 'random', nullable: true, width: '92px' }), num(e, 'concurrency', 'Concurrent', { min: 1, width: '58px', title: 'Match your ElevenLabs plan\'s concurrency limit' })]);
      kids.push(grp('Model', ms), tag ? grp('Stability', segmented([[0, 'Creative'], [0.5, 'Natural'], [1, 'Robust']], stabVal, (x) => set(e, 'stability', Number(x)))) : slider(e, 'stability', 0, 1, 0.01, 'Stability'),
        slider(e, 'similarity', 0, 1, 0.01, 'Similarity'), style ? slider(e, 'style', 0, 1, 0.01, 'Style') : null, slider(e, 'speed', 0.7, 1.2, 0.01, 'Speed'),
        grp('Language', lang), grp('Read numbers', norm), grp('Output', out));
    } else if (eng === 'voxcpm') {
      const o = v.voxCpm;
      kids.push(num(o, 'cfgValue', 'CFG', { step: 0.1 }), num(o, 'inferenceTimesteps', 'Timesteps', { min: 1 }), num(o, 'concurrency', 'Concurrent', { min: 1, width: '58px' }));
    } else {
      const o = v.omniVoice;
      kids.push(num(o, 'numStep', 'Steps', { min: 1 }), num(o, 'speed', 'Speed', { step: 0.05 }), num(o, 'concurrency', 'Concurrent', { min: 1, width: '58px' }));
    }
    return h('div.toolbar', ...kids.filter(Boolean));
  }

  function progressCell(st) {
    if (!st) return h('span.faint', '—');
    const pct = st.lines ? Math.round((100 * st.generated) / st.lines) : 0;
    return h('div.col', { style: { gap: '3px', minWidth: '150px' } },
      h('div.bar' + (st.lines && st.generated >= st.lines && !st.stale ? '.ok' : ''), h('div.bar-fill', { style: { width: pct + '%' } })),
      h('span.faint', { style: { fontSize: '12px' } }, `${st.done}/${st.books} books · ${st.charactersLeft.toLocaleString()} chars left${st.stale ? ` · ${st.stale} edited` : ''}`));
  }

  function renderVoices() {
    const wrap = h('div.view-body.reading');
    if (Object.values(voiceLists).every((x) => x == null)) loadVoices();
    const level = (row, kind) => segmented([['loud', 'Loud', 'The IVBOS release chain: −12 LUFS and the limiter'], ['normal', 'Normal', 'Limiter only; female voices get +1.8 dB']], row.level || 'loud', (v) => setVoice(row, kind, { level: v }));
    const gender = (row, kind) => segmented([[false, 'Male'], [true, 'Female']], !!row.female, (v) => setVoice(row, kind, { female: v === true || v === 'true' }));
    const state = (st, hasVoice) => !hasVoice ? 1 : st && st.lines && st.generated >= st.lines && !st.stale ? 2 : 0;
    // Narrators
    const narr = proj.narrators.slice().sort((a, b) => state(a.stats, a.voiceId) - state(b.stats, b.voiceId));
    const total = (rows) => rows.reduce((t, r) => ({ books: t.books + ((r.stats && r.stats.books) || 0), left: t.left + ((r.stats && r.stats.charactersLeft) || 0) }), { books: 0, left: 0 });
    const nt = total(proj.narrators), vtt = total(proj.voiceTypes);
    wrap.append(
      h('div.help', { style: { maxWidth: '1200px', marginBottom: '12px' } },
        h('p', h('b', 'Who reads. '), 'Every narrated book is read by each narrator (players pick one in the installer). Letters, notes and journals written by an NPC with a vanilla voice type are read in that voice type\'s voice — one shared archive for all narrators. Authors without an NPC record, long-dead writers and custom-voiced mod NPCs stay with the narrator.')),
      h('details.voice-settings', { open: store.get('bk-vs-open', false), ontoggle: (e) => store.set('bk-vs-open', e.target.open) },
        h('summary', { title: 'Engine settings shared by every voice' }, h('b', 'Voice settings'), h('span.faint', `  ${proj.voice.eleven.modelId} · stability ${Number(proj.voice.eleven.stability).toFixed(2)} · speed ${Number(proj.voice.eleven.speed).toFixed(2)} · shared by every voice`), h('span.spacer'),
          h('button.btn.sm.ghost', { title: 'Reload voices from your accounts and local servers', onclick: (e) => { e.preventDefault(); loadVoices(true); } }, '⟳ Reload voices')),
        settingsRow()),
      h('div.section-head', { style: { marginTop: '10px', maxWidth: '1200px' } }, h('h2', 'Narrators'), helpButton('narrators-and-authors'), h('span.faint', `${proj.narrators.length} · ${nt.books.toLocaleString()} books · ${nt.left.toLocaleString()} characters left`), h('div.spacer'),
        h('button.btn.sm', { title: 'Add another narrator: each gets its own archive and installer option', onclick: async () => { const r = await prompt('Add a narrator', [{ name: 'n', label: 'Name (in the archive name)', value: `Narrator ${proj.narrators.length + 1}`, placeholder: 'e.g. Male Narrator - Vampire' }]); if (r && r.n.trim()) { try { proj = await rpc('books.voice', { id, kind: 'narrator', add: true, name: r.n.trim() }); reindex(); renderAll(); } catch (e) { fail(e); } } } }, '+ Add narrator')),
      h('table.tbl.voices-tbl', h('thead', h('tr', h('th', 'Narrator'), h('th', 'Engine · voice'), h('th', { title: 'Loud: the IVBOS release chain. Normal: limiter only.' }, 'Level'), h('th', { title: 'Female Normal voices get +1.8 dB, as in the IVBOS macros' }, 'Voice'), h('th', 'Progress'), h('th', ''))),
        h('tbody', narr.map((n) => h('tr' + (n.voiceId ? '' : '.needs-voice'),
          h('td', (() => { const i = h('input', { type: 'text', value: n.name, title: 'Shown in the archive name and the installer', style: { width: '200px' } }); i.addEventListener('change', () => setVoice(n, 'narrator', { name: i.value.trim() || n.name })); return i; })()),
          h('td', voicePick(n, 'narrator')), h('td', level(n, 'narrator')), h('td', gender(n, 'narrator')), h('td', progressCell(n.stats)),
          h('td', proj.narrators.length > 1 ? h('button.icon-btn', { title: 'Remove this narrator', onclick: async () => { if (await confirmBox('Remove narrator?', `${n.name}'s takes stay on disk but leave the project.`, 'Remove', true)) { try { proj = await rpc('books.voice', { id, kind: 'narrator', narratorId: n.id, remove: true, force: true }); reindex(); renderAll(); } catch (e) { fail(e); } } } }, '×') : null))))),
      h('div.section-head', { style: { marginTop: '16px', maxWidth: '1200px' } }, h('h2', 'NPC voice types'), h('span.faint', proj.npcVoices ? `${proj.voiceTypes.length} · ${vtt.books.toLocaleString()} books · ${vtt.left.toLocaleString()} characters left` : 'off: narrated books only (Setup)')),
      !proj.npcVoices ? h('div.faint', { style: { padding: '8px 0' } }, 'NPC voices are off, so books written by NPCs are left out. Turn them on in Setup → Names and output.')
        : !proj.voiceTypes.length ? h('div.faint', { style: { padding: '8px 0' } }, 'No book is read by an NPC voice. In the Books tab, set a book\'s reader to an NPC voice type to add one here.')
          : h('table.tbl.voices-tbl', h('thead', h('tr', h('th', 'Voice type'), h('th', { title: 'Books this voice type reads' }, 'Books'), h('th', 'Engine · voice'), h('th', 'Level'), h('th', { title: 'Let the narrator(s) read these books instead' }, 'Narrator instead'), h('th', 'Progress'))),
            h('tbody', proj.voiceTypes.slice().sort((a, b) => (a.useNarrator - b.useNarrator) || (state(a.stats, a.voiceId) - state(b.stats, b.voiceId)) || a.voiceType.localeCompare(b.voiceType)).map((v) => h('tr' + (v.useNarrator ? '.muted' : v.voiceId ? '' : '.needs-voice'),
              h('td', h('b.mono', v.voiceType), h('div.faint.ellipsis', { title: v.titles.join('\n'), style: { maxWidth: '240px', fontSize: '12px' } }, v.titles.slice(0, 3).join(' · '))),
              h('td', h('button.btn.sm.ghost', { title: 'Show these books', onclick: () => { voiceFilter = v.key; tab = 'books'; store.set('bk-tab:' + id, tab); renderAll(); } }, String(v.books))),
              h('td', v.useNarrator ? h('span.faint', 'read by the narrator(s)') : voicePick(v, 'vt')),
              h('td', v.useNarrator ? null : level(v, 'vt')),
              h('td', switchEl(v.useNarrator, (x) => setVoice(v, 'vt', { useNarrator: x }), '', { title: 'The narrator(s) read these books' })),
              h('td', v.useNarrator ? null : progressCell(v.stats)))))));
    return wrap;
  }

  // ---------------------------------------------------------------- books
  const left = h('div.left');
  const insp = h('div.inspector');
  const filters = h('div.filters');
  const vbar = h('div.toolbar');
  const actions = h('div.statusbar');
  const grid = new VGrid(left, {
    storageKey: 'books',
    rowKey: (b) => b.stem,
    rowClass: (b) => [b.gone ? 'muted' : '', b.excl ? 'excluded' : ''].filter(Boolean).join(' '),
    onRowClick: (b) => { focused = b.stem; renderInspector(); },
    onRowDblClick: (b) => openEditor(b.stem),
    columns: [
      { id: 'sel', title: '', width: 32, resizable: false, render: (b) => grid.checkbox(b) },
      { id: 'excl', title: '', width: 30, resizable: false, render: (b) => h('button.act-btn.excl-btn' + (b.excl ? '.on.bad' : ''), { title: b.excl ? `Left out (${b.exReason || 'by you'}) - click to include` : 'Leave this book out', onclick: (e) => { e.stopPropagation(); bookAct(b, b.excl ? 'include' : 'exclude'); } }, icon('ban', { size: 14 })) },
      { id: 'title', title: 'Title', flex: 1, width: 200, render: (b) => h('span.ellipsis', { title: `${b.title}\n${b.edid} · ${b.plugin}${b.records > 1 ? ` (+${b.records - 1} more records share this text)` : ''}` }, b.updated ? h('span.mark.warn', { title: 'An updated text is available from the built-in set' }, '★ ') : null, b.title || h('i.faint', b.stem)) },
      { id: 'stem', title: 'File', width: 130, render: (b) => h('span.mono.faint.ellipsis', { title: `sound\\fx\\voicedbooksofskyrim\\${b.stem}.xwm` }, b.stem) },
      { id: 'reader', title: 'Read by', width: 165, render: (b) => readerCell(b) },
      { id: 'author', title: 'Author / sender', width: 130, render: (b) => b.author ? h('span.ellipsis', { title: `${b.author}${b.npcName ? ' → NPC ' + b.npcName : ''}\nFound in ${SRC[b.authorSrc] || b.authorSrc}\n${CERT_TIP[b.authorCert] || ''}` }, b.author) : h('span.faint', '—') },
      { id: 'kind', title: 'Kind', width: 60, render: (b) => h('span.faint', b.kind) },
      { id: 'chars', title: 'Chars', width: 66, align: 'right', render: (b) => h('span.mono.faint', { title: `${b.chars.toLocaleString()} characters ≈ credits per voice · ${b.parts} part(s)` }, b.chars.toLocaleString()) },
      { id: 'parts', title: 'Parts', width: 46, render: (b) => h('span.mono.faint', String(b.parts || '—')) },
      { id: 'checks', title: 'Checks', width: 58, render: (b) => b.serious ? h('span.badge.b-broken', { title: `${b.serious} serious problem(s): open the editor to see them` }, `⚠ ${b.serious}`) : b.notes ? h('span.faint', { title: `${b.notes} note(s): open the editor to see them` }, `· ${b.notes}`) : h('span.faint', '✓') },
      { id: 'status', title: 'Audio', width: 62, render: (b) => h('span.row', { style: { gap: '4px' } }, b.voices.length ? b.voices.map((v) => h('span.dot.' + DOT[v.status], { title: `${voiceName(v.v)}: ${DOT_TIP[v.status]}` })) : h('span.faint', '—')) },
      { id: 'act', title: '', width: 146, resizable: false, render: (b) => h('span.row.icon-bar', { style: { gap: '2px' } },
        h('button.act-btn' + (inFlight.has(b.stem) ? '.spinning' : ''), { title: genBlock(b) || (b.voices.some((v) => v.miss || v.edited) ? 'Generate this book\'s missing and edited parts (every voice that reads it)' : 'A new take of every part (every voice that reads it)'), disabled: !!genBlock(b), onclick: (e) => { e.stopPropagation(); generate('ticked', [b.stem], { single: true }); } }, icon('regen')),
        h('button.act-btn', { title: 'Open the editor (or double-click the row)', onclick: (e) => { e.stopPropagation(); openEditor(b.stem); } }, icon('edit')),
        h('button.act-btn.flag-btn' + (b.marked ? '.on.warn' : ''), { title: b.marked ? 'Flagged - click to clear' : 'Flag to revisit', onclick: (e) => { e.stopPropagation(); bookAct(b, 'mark'); } }, icon('flag')),
        h('button.act-btn', { title: b.voices.some((v) => v.status === 'ready') ? 'Play the whole book (joined, with reverb and loudness)' : 'No complete take yet', disabled: !b.voices.some((v) => v.status === 'ready'), onclick: (e) => { e.stopPropagation(); playBook(b); } }, icon('play')),
        h('button.act-btn', { title: 'Stop playback', onclick: (e) => { e.stopPropagation(); audio.pause(); } }, h('span', { style: { fontSize: '11px' } }, '■'))) },
    ],
  });
  grid.el.addEventListener('selection', () => renderActions());

  const voiceName = (key) => { const v = allVoices().find((x) => x.key === key); return v ? v.name : key.replace(/^vt:/, ''); };

  function readerCell(b) {
    if (b.reader === 'npc') {
      const cert = b.confirmed ? 'Confirmed' : b.vtCert;
      if (!b.vt) return h('span.ellipsis', { title: 'Written by an NPC, but the voice type isn\'t known yet: the narrator reads it until you pick one (editor or Ticked ▾)' }, h('span.mark.warn', '? '), 'NPC · pick a voice type');
      return h('span.ellipsis', { title: `Read in the ${b.vt} voice\n${b.via || ''}\n${CERT_TIP[cert] || ''}` }, cert !== 'Confirmed' ? h('span.mark.warn', { title: CERT_TIP[cert] }, '? ') : null, h('span.mono', b.vt), cert && cert !== 'Confirmed' ? h('span.faint', ` · ${CERT[cert]}`) : null);
    }
    return h('span.faint', { title: 'Read by the narrator(s)' + (b.cands && b.cands.length ? `\nPossible NPC voice types: ${b.cands.join(', ')}` : '') }, 'Narrator', b.cands && b.cands.length ? h('span.mark.warn', ' ·?') : null);
  }

  function genBlock(b) {
    if (app.job) return 'A job is running (see the top bar)';
    if (b.excl) return 'Left out';
    if (b.gone) return 'No longer in the plugins';
    if (!b.voices.length) return proj.narrators.length ? 'No voice reads this book (NPC voices are off?)' : 'Add a narrator first';
    return '';
  }

  function counts() {
    const B = proj.books.filter((b) => !voiceFilter || b.voices.some((v) => v.v === voiceFilter));
    const live = B.filter((b) => !b.excl && !b.gone);
    return {
      all: B.length,
      todo: live.filter((b) => b.voices.some((v) => v.miss)).length,
      done: live.filter((b) => b.voices.length && b.voices.every((v) => v.status === 'ready')).length,
      stale: live.filter((b) => b.voices.some((v) => v.edited)).length,
      npc: live.filter((b) => b.reader === 'npc').length,
      pickvt: live.filter((b) => b.reader === 'npc' && !b.vt).length,
      unconfirmed: live.filter((b) => b.reader === 'npc' && !b.confirmed).length,
      problems: live.filter((b) => b.serious).length,
      updated: B.filter((b) => b.updated).length,
      suggest: B.filter((b) => b.suggest && b.excl).length,
      marked: B.filter((b) => b.marked).length,
      excl: B.filter((b) => b.excl).length,
    };
  }
  function matches(b, f) {
    switch (f) {
      case 'todo': return !b.excl && !b.gone && b.voices.some((v) => v.miss);
      case 'done': return !b.excl && b.voices.length && b.voices.every((v) => v.status === 'ready');
      case 'stale': return !b.excl && b.voices.some((v) => v.edited);
      case 'npc': return !b.excl && b.reader === 'npc';
      case 'pickvt': return !b.excl && b.reader === 'npc' && !b.vt;
      case 'unconfirmed': return !b.excl && b.reader === 'npc' && !b.confirmed;
      case 'problems': return !b.excl && b.serious > 0;
      case 'updated': return b.updated;
      case 'suggest': return b.suggest && b.excl;
      case 'marked': return b.marked;
      case 'excl': return b.excl;
      default: return true;
    }
  }
  function visible() {
    const q = search.toLowerCase();
    return proj.books.filter((b) => (!voiceFilter || b.voices.some((v) => v.v === voiceFilter)) && matches(b, filter)
      && (!q || b.title.toLowerCase().includes(q) || b.stem.includes(q) || (b.author || '').toLowerCase().includes(q) || (b.vt || '').toLowerCase().includes(q) || (b.edid || '').toLowerCase().includes(q)));
  }
  function renderFilters() {
    const c = counts();
    const chip = (k, label, tip) => (k === 'all' || filter === k || c[k] ? h('button.chip' + (filter === k ? '.on' : ''), { title: tip, onclick: () => { filter = k; store.set('bk-filter:' + id, k); renderFilters(); grid.setRows(visible()); } }, label, ' ', h('span.n', String(c[k]))) : null);
    const q = h('input', { type: 'text', placeholder: 'Search title, file, author, voice type…', value: search, title: 'Filter the list' });
    q.addEventListener('input', debounce(() => { search = q.value; grid.setRows(visible()); }, 200));
    clear(filters).append(
      chip('all', 'All', 'Every book'), chip('todo', 'Missing', 'A voice that reads it has parts with no take yet'), chip('done', 'Ready', 'Every voice has every part'),
      chip('stale', 'Edited since take', 'A part has a take, but its text changed after it (or its last attempt failed)'), chip('npc', 'NPC voices', 'Read in an NPC\'s voice type'),
      chip('pickvt', 'Pick a voice type', 'Written by an NPC whose voice type isn\'t known yet: the narrator reads them until you pick one'),
      chip('unconfirmed', 'Author unconfirmed', 'NPC voice from an automatic match: check, then Confirm'), chip('problems', 'Check found problems', 'Alias markup, leftover markup or short ALL-CAPS words'),
      chip('updated', 'Updated text', 'The built-in set has a newer text for a book you edited'), chip('suggest', 'Suggested', 'Spell tomes or recipes with real text: left out, but worth voicing'),
      chip('marked', 'Flagged', 'Flagged to revisit'), chip('excl', 'Left out', 'Not generated or exported'),
      h('div.spacer'), q, columnsMenu(grid),
      h('button.btn.sm', { title: 'Show or hide the book panel', onclick: () => { showInsp = !showInsp; store.set('bk-insp', showInsp); renderAll(); } }, showInsp ? 'Hide panel ▸' : '◂ Show panel'));
  }

  function renderVbar() {
    const sel = h('select', { title: 'Show the books one voice reads', style: { maxWidth: '260px' } }, h('option', { value: '' }, `All voices (${allVoices().length})`),
      allVoices().map((v) => h('option', { value: v.key, selected: v.key === voiceFilter }, `${v.kind === 'narrator' ? '🎙 ' : ''}${v.name}`)));
    sel.addEventListener('change', () => { voiceFilter = sel.value; renderVbar(); renderFilters(); grid.setRows(visible()); renderActions(); });
    const grp = (label, ...kids) => h('div.tgroup', h('label', label), h('div.row', ...kids));
    const cur = allVoices().find((v) => v.key === voiceFilter);
    const row = cur ? (cur.kind === 'narrator' ? proj.narrators.find((n) => n.key === cur.key) : proj.voiceTypes.find((v) => v.key === cur.key)) : null;
    clear(vbar).append(grp('Voice', sel),
      row ? grp('Engine · voice', voicePick(row, cur.kind)) : grp('Voices', h('button.btn.sm.bordered', { title: 'Every narrator and NPC voice type has its own voice. Pick one on the left to set it here, or set them all in the Narrators & authors tab.', onclick: () => { tab = 'voices'; store.set('bk-tab:' + id, tab); renderAll(); glow(); } }, 'Set in Narrators & authors')),
      helpButton('books-and-the-editor'),
      h('div.spacer'),
      h('span.faint', { title: 'The longest part this model accepts' }, `Parts ≈ ${proj.partTarget.toLocaleString()} chars (max ${Math.min(proj.partMax, proj.ceiling).toLocaleString()})`));
  }
  function glow() { setTimeout(() => { const t = body.querySelector('.voices-tbl'); if (t) { t.classList.add('flash-hl'); setTimeout(() => t.classList.remove('flash-hl'), 2600); } }, 60); }

  function renderInspector() {
    clear(insp);
    const b = focused && index.get(focused);
    if (!b) {
      insp.append(h('h3', 'Book'), h('div.dim', 'Click a row to see the book. Double-click (or ✎) opens the editor.'),
        h('div.help', h('p', h('b', 'Read by'), ' shows who reads the book: the narrator(s), or an NPC voice type when an NPC wrote it. ', h('b', '?'), ' marks automatic matches you haven\'t confirmed.'),
          h('p', h('b', 'Audio'), ' has a dot per voice: green ready, amber changed since take, half partly done, empty not yet.')));
      return;
    }
    const vtSel = h('select', { title: 'The NPC voice type that reads this book', style: { maxWidth: '220px' } }, h('option', { value: '' }, '— voice type —'));
    rpc('books.voiceTypes', { id }).then((list) => {
      const pref = [...new Set([b.vt, ...(b.cands || [])].filter(Boolean))];
      vtSel.append(...pref.map((v) => h('option', { value: v, selected: b.reader === 'npc' && v === b.vt }, v + (b.reader !== 'npc' && v === b.vt ? ' (the author\'s voice type)' : b.cands.includes(v) && v !== b.vt ? ' (candidate)' : ''))),
        h('option', { disabled: true }, '──────'), ...list.filter((v) => !pref.includes(v)).map((v) => h('option', { value: v }, v)));
    }).catch(() => {});
    vtSel.addEventListener('change', () => { if (vtSel.value) bookAct(b, 'reader', 'npc', { voiceType: vtSel.value }); });
    insp.append(
      h('h3', b.title || b.stem),
      h('div.kv',
        h('div', 'File'), h('div.row', h('span.mono.ellipsis', { title: `sound\\fx\\voicedbooksofskyrim\\${b.stem}.xwm` }, b.stem + '.xwm'), h('button.icon-btn', { title: 'Copy the file name', onclick: () => navigator.clipboard.writeText(b.stem + '.xwm').then(() => toast('Copied.')) }, '⧉')),
        h('div', 'Record'), h('div.mono.ellipsis', { title: `${b.plugin} · ${b.edid}${b.records > 1 ? ` · +${b.records - 1} more records with this text` : ''}` }, `${b.edid}${b.records > 1 ? ` +${b.records - 1}` : ''}`),
        h('div', 'Kind'), h('div', b.kind + (b.excl ? ` · left out (${b.exReason || 'by you'})` : '') + (b.suggest ? ' · has real text' : '')),
        b.author ? h('div', 'Author') : null, b.author ? h('div', { title: CERT_TIP[b.authorCert] || '' }, b.author, h('span.faint', ` · ${SRC[b.authorSrc] || b.authorSrc}${b.npcName && b.npcName !== b.author ? ' · NPC ' + b.npcName : b.npcName ? ' · has an NPC record' : ''}`)) : null,
        h('div', 'Characters'), h('div', `${b.chars.toLocaleString()} · ${b.parts || 0} part(s)`)),
      h('div', h('div.field-label', 'Read by'),
        segmented([['narrator', 'Narrator', 'The narrator(s) read it'], ['npc', 'NPC voice', 'Read in the voice type of the NPC who wrote it']], b.reader, (v) => { if (v === 'narrator') bookAct(b, 'reader', 'narrator'); else if (b.vt) bookAct(b, 'reader', 'npc'); else toast('Pick the voice type below.', true); }),
        h('div.row', { style: { marginTop: '6px' } }, vtSel,
          b.reader === 'npc' && !b.confirmed && b.vt ? h('button.btn.sm', { title: 'This attribution is right: mark it CONFIRMED', onclick: () => bookAct(b, 'confirm') }, '✓ Confirm') : null),
        b.via ? h('div.faint', { style: { fontSize: '12px', marginTop: '4px' } }, b.via, b.reader === 'npc' ? ` · ${CERT[b.confirmed ? 'Confirmed' : b.vtCert] || ''}` : '') : null),
      h('div', h('div.field-label', 'Audio'), b.voices.length ? h('div.col', { style: { gap: '4px' } }, b.voices.map((v) => h('div.row', h('span.dot.' + DOT[v.status], { title: DOT_TIP[v.status] }), h('span.grow.ellipsis', voiceName(v.v)), h('span.faint', DOT_TIP[v.status].split(':')[0]),
        h('button.icon-btn', { title: 'Play the whole book in this voice', disabled: v.status !== 'ready', onclick: () => playBook(b, v.v) }, '▶')))) : h('div.faint', genBlock(b))),
      b.serious || b.notes ? h('div', h('div.field-label', 'Checks'), h('div', { style: { color: b.serious ? 'var(--warn)' : 'var(--dim)' } }, `${b.serious ? b.serious + ' problem(s) · ' : ''}${b.notes} note(s): see them in the editor.`)) : null,
      b.updated ? h('div.banner.info', { style: { margin: 0 } }, h('span', 'The built-in set has an updated text for this book.'), h('div.spacer'), h('button.btn.sm', { title: 'Open the editor to compare and choose', onclick: () => openEditor(b.stem) }, 'Compare')) : null,
      h('div.row', h('button.btn.primary', { title: 'Edit the spoken text, the parts and the takes', onclick: () => openEditor(b.stem) }, icon('edit', { size: 14 }), ' Open editor'), h('div.spacer'),
        h('button.btn.sm', { title: b.excl ? 'Include this book' : 'Leave this book out', onclick: () => bookAct(b, b.excl ? 'include' : 'exclude') }, b.excl ? 'Include' : 'Leave out')));
  }

  function renderActions() {
    const c = counts();
    const sel = [...grid.selected];
    const bulkBtn = h('button.btn', { title: 'Actions for the ticked books' }, `Ticked (${sel.length}) ▾`);
    bulkBtn.addEventListener('click', async () => {
      if (!sel.length) { toast('Tick the box of one or more books first.', true); return; }
      popupMenu(bulkBtn, [
        { label: 'Read by the narrator', sub: 'These books are narrated', onclick: () => bulk('narrator', sel) },
        { label: 'Read by an NPC voice type…', sub: 'Pick the voice type for all ticked books', onclick: async () => { const list = await rpc('books.voiceTypes', { id }); const s = h('select', list.map((v) => h('option', { value: v }, v))); const ok = await modal('Voice type for the ticked books', [s], [{ label: 'Cancel', value: false }, { label: 'Set', primary: true, value: true }]); if (ok) bulk('voiceType', sel, s.value); } },
        { label: 'Confirm the attribution', sub: 'Mark their author and voice type CONFIRMED', onclick: () => bulk('confirm', sel) },
        { label: 'Leave out', onclick: () => bulk('exclude', sel) },
        { label: 'Include', onclick: () => bulk('include', sel) },
        { label: 'Flag to revisit', onclick: () => bulk('markOn', sel) },
        { label: 'Clear flags', onclick: () => bulk('markOff', sel) },
        { label: 'Use the updated set text', sub: 'Replaces your edits for those with an update', onclick: () => bulk('useUpdated', sel) },
        { label: 'Restore the game text', sub: 'Back to the game\'s text with the spoken rules (your edits go)', onclick: async () => { if (await confirmBox('Restore the game text?', 'Your edits to the ticked books are replaced by the game text with the spoken-text rules applied.', 'Restore', true)) bulk('restore', sel); } },
      ]);
    });
    const live = proj.books.filter((b) => !b.excl && !b.gone && (!voiceFilter || b.voices.some((v) => v.v === voiceFilter)));
    // Missing: parts with no take at all. Edited: parts with a take whose text changed since. Ticked and Flagged make new takes.
    const vs = (b) => b.voices.filter((v) => !voiceFilter || v.v === voiceFilter);
    const missingParts = (b) => vs(b).filter((v) => v.miss);
    const editedParts = (b) => vs(b).filter((v) => v.edited);
    const est = (books, f = missingParts) => books.reduce((a, b) => a + b.chars * Math.max(1, f(b).length), 0);
    const todo = live.filter((b) => missingParts(b).length);
    const edited = live.filter((b) => editedParts(b).length);
    const ticked = live.filter((b) => sel.includes(b.stem));
    const flagged = live.filter((b) => b.marked);
    const problems = live.filter((b) => b.serious && (missingParts(b).length || editedParts(b).length));
    const forVoice = voiceFilter ? ` for ${voiceName(voiceFilter)}` : '';
    clear(actions).append(
      helpButton('generating-books'),
      h('span.stat', h('b', String(c.done)), ` / ${live.length} books ready${forVoice}`, c.stale ? h('span', { style: { color: 'var(--warn)' } }, ` · ${c.stale} edited since take`) : '', c.pickvt ? h('span', { style: { color: 'var(--warn)' } }, ` · ${c.pickvt} need a voice type`) : ''),
      h('div.spacer'),
      h('button.btn', { title: 'Open the takes or joined-audio folder', onclick: (e) => popupMenu(e.currentTarget, [{ label: 'Takes folder', sub: 'Every part\'s takes', onclick: () => rpc('books.openFolder', { id, which: 'takes' }).catch(fail) }, { label: 'Joined audio', sub: 'Whole books, built for listening and export', onclick: () => rpc('books.openFolder', { id, which: 'joined' }).catch(fail) }, proj.outputFolder ? { label: 'Output folder', sub: proj.outputFolder, onclick: () => rpc('open.path', { path: proj.outputFolder }).catch(fail) } : null].filter(Boolean)) }, icon('folder', { size: 14 }), ' Folders ▾'),
      bulkBtn,
      generateButton({
        running: app.job && app.job.kind === 'generate', unit: 'book',
        items: () => [
          { key: 'missing', label: 'Missing', count: todo.length, chars: est(todo), desc: `Books with parts that have no take yet${forVoice}. A part with a take is never missing.` },
          { key: 'edited', label: 'Edited', count: edited.length, chars: est(edited, editedParts), desc: `A new take for each part whose text changed since its take, or whose last attempt failed${forVoice}.` },
          { key: 'ticked', label: 'Ticked', count: ticked.length, chars: est(ticked, vs), desc: 'A new take for every part of each ticked book.' },
          { key: 'flagged', label: 'Flagged', count: flagged.length, chars: est(flagged, vs), desc: 'A new take for every part of each flagged book.' },
          { key: 'problems', label: 'Check found problems', count: problems.length, chars: est(problems), warn: true, desc: 'Books whose text check found a problem (alias markup, leftover markup, short ALL-CAPS). Usually fix the text first.' },
          { key: 'all', label: 'All parts again', count: live.length, chars: live.reduce((a, b) => a + b.chars * Math.max(1, b.voices.filter((v) => !voiceFilter || v.v === voiceFilter).length), 0), warn: true, desc: `A new take for every part of every book${forVoice}, even ready ones. Older takes stay. Uses the most credits.` },
        ],
        onPick: (k) => generate(k, k === 'ticked' ? sel : null),
      }));
  }

  // ---------------------------------------------------------------- actions
  async function bookAct(b, action, value, extra = {}) {
    try {
      const d = await rpc('books.book', { id, stem: b.stem, action, value, ...extra });
      replaceBook(d.book);
      if (['reader', 'confirm', 'exclude', 'include'].includes(action)) { proj = await rpc('books.get', { id }); reindex(); renderAll(); }
      return d;
    } catch (e) { fail(e); return null; }
  }
  async function bulk(action, stems, value) {
    try { const r = await rpc('books.bulk', { id, stems, action, value }); proj = r.project; reindex(); renderAll(); toast(`${r.changed} book(s) changed.`); } catch (e) { fail(e); }
  }
  function replaceBook(nb) {
    const i = proj.books.findIndex((x) => x.stem === nb.stem);
    if (i >= 0) proj.books[i] = nb;
    index.set(nb.stem, nb);
    const ri = grid.rows.findIndex((x) => x.stem === nb.stem);
    if (ri >= 0) grid.rows[ri] = nb;
    if (tab === 'books' && grid.el.isConnected) { grid.refreshRow(nb.stem); if (focused === nb.stem && !insp.contains(document.activeElement)) renderInspector(); renderActions(); }
  }

  async function playBook(b, voice) {
    const v = voice || (b.voices.find((x) => x.status === 'ready') || {}).v;
    if (!v) return;
    try { toast('Joining the parts…'); const r = await rpc('books.join', { id, stem: b.stem, voice: v }); audio.src = r.url; audio.play().catch(fail); toast(`${voiceName(v)} · ${fmtTime(r.seconds)} · ${r.lufs.toFixed(1)} LUFS · peak ${r.peakDb.toFixed(1)} dB`); } catch (e) { fail(e); }
  }

  const inFlight = new Set();
  function markInFlight(stems) {
    const before = [...inFlight];
    inFlight.clear();
    for (const x of stems || []) inFlight.add(x);
    if (grid.el.isConnected) for (const x of new Set([...before, ...inFlight])) grid.refreshRow(x);
  }

  async function generate(mode, stems, { single = false, lineIds = null } = {}) {
    try {
      if (single && stems) markInFlight(stems);
      // One book (the row's ↻): its parts that need work, or a new take of every part when none do. Ticked and Flagged always make new takes.
      const book = single && stems && stems.length === 1 ? index.get(stems[0]) : null;
      const needsWork = book && book.voices.some((v) => v.miss || v.edited);
      const regenerate = mode === 'all' || mode === 'flagged' || (mode === 'ticked' && (!single || !needsWork));
      const params = { id, mode: lineIds ? 'lines' : mode, stems: stems || [], lineIds: lineIds || [], voice: single || lineIds ? '' : voiceFilter, regenerate };
      const est = await rpc('books.estimate', params);
      if (!est.count) { markInFlight(null); toast(est.unassigned.length ? `Pick a voice for ${est.unassigned.join(', ')} first (Narrators & authors).` : 'Nothing to generate.', !!est.unassigned.length); return; }
      const confirmBatch = (app.state.settings.ui || {}).confirmBatch !== 'false';
      if ((est.count > 1 || !single) && confirmBatch) {
        const ok = await modal('Check before spending credits', estimateBody(est), [{ label: 'Cancel', value: false }, { label: `Generate ${est.count} part(s)`, primary: true, value: true }], { wide: true, help: 'generating-books' });
        if (!ok) { markInFlight(null); return; }
      }
      if (!single) markInFlight(stems || proj.books.filter((b) => mode === 'flagged' ? b.marked : mode === 'all' || b.voices.some((v) => (mode === 'edited' ? v.edited : mode === 'problems' ? v.miss || v.edited : v.miss))).map((b) => b.stem));
      await rpc('books.generate', params);
      renderActions();
    } catch (e) { markInFlight(null); fail(e); }
  }

  function estimateBody(est) {
    const groups = est.groups.map((g) => h('div.est-group' + (g.hasVoice ? '' : '.muted'),
      h('div.row', h('b', g.name), h('span.faint', g.hasVoice ? `${g.voicePack} · ${g.engine}` : 'no voice yet: skipped'), h('div.spacer'), h('b', `${g.books} book(s) · ${g.lines} part(s)`), h('span.faint', `${g.characters.toLocaleString()} characters`)),
      g.hasVoice ? h('div.kv4', g.settings.flatMap(([k, v]) => [h('div', k), h('div', v)])) : null));
    return [
      h('div', { style: { fontSize: '15px' } }, h('b', `${est.books} book(s), ${est.count} part(s), ${est.characters.toLocaleString()} characters ≈ credits.`)),
      est.context ? h('div.faint', 'Each part is sent with the end of the part before and the start of the part after (request stitching), so the reading flows across joins. Only the part itself is billed.') : null,
      est.unconfirmed ? h('div.banner.warn', { style: { margin: 0 } }, `${est.unconfirmed} book(s) are read by an NPC voice from an automatic match you haven't confirmed (filter "Author unconfirmed").`) : null,
      est.problems ? h('div.banner.warn', { style: { margin: 0 } }, `${est.problems} book(s) have text problems the check found (filter "Check found problems"). Fixing the text first saves regenerating.`) : null,
      ...groups,
    ];
  }

  // ---------------------------------------------------------------- the editor
  async function openEditor(stem) {
    let d;
    try { d = await rpc('books.bookGet', { id, stem }); } catch (e) { fail(e); return; }
    let voice = store.get('bk-ed-voice', '');
    if (!d.voices.some((v) => v.key === voice)) voice = d.voices.length ? d.voices[0].key : '';
    let variant = '';
    let dirty = false;
    const overlay = h('div.book-editor-overlay');
    const card = h('div.book-editor');
    overlay.append(card);
    const close = async () => {
      if (dirty) { const v = await modal('Unsaved changes', [h('div', 'Save the spoken text before closing?')], [{ label: 'Discard', value: 'discard' }, { label: 'Cancel', value: null }, { label: 'Save', primary: true, value: 'save' }]); if (v == null) return; if (v === 'save') await saveText(); }
      overlay.remove(); document.removeEventListener('keydown', onKey, true); off(); edAudio.pause();
      try { proj = await rpc('books.get', { id }); reindex(); renderAll(); } catch { /* keep */ }
    };
    const onKey = (e) => {
      // Ask about unsaved text after this key press is done, so the same Esc doesn't also dismiss the question.
      if (e.key === 'Escape' && !document.querySelector('#modal:not(.hidden)')) { e.preventDefault(); e.stopPropagation(); setTimeout(close, 0); }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); saveText(); }
    };
    document.addEventListener('keydown', onKey, true);
    const edAudio = new Audio();
    const ta = h('textarea.spoken-ed', { spellcheck: false, title: 'What the voice reads. Blank lines are paragraphs (parts prefer to break there). [tags] work on v3/v4 models.' });
    const gameBox = h('div.game-text');
    const partsBox = h('div.parts-list');
    const checksBox = h('div.checks-box');
    const headBox = h('div.ed-head');
    const toolsBox = h('div.ed-tools');
    let breaks = [], manual = false;

    const currentText = () => variant ? (d.variantsList.find((x) => x.id === variant) || {}).spoken || '' : d.spoken;
    async function saveText() {
      if (!dirty) return;
      try {
        d = await rpc('books.book', variant ? { id, stem, action: 'variantSpoken', variant, value: ta.value } : { id, stem, action: 'spoken', value: ta.value });
        dirty = false; renderEd(true); replaceBook(d.book);
      } catch (e) { fail(e); }
    }
    ta.addEventListener('input', () => { dirty = true; renderCount(); });
    ta.addEventListener('blur', () => saveText());
    const countEl = h('span.faint');
    function renderCount() { countEl.textContent = `${ta.value.length.toLocaleString()} characters${dirty ? ' · unsaved (Ctrl+S)' : ''}`; }

    async function act(action, value, extra = {}) {
      try { if (dirty) await saveText(); d = await rpc('books.book', { id, stem, action, value, ...extra }); renderEd(true); replaceBook(d.book); } catch (e) { fail(e); }
    }
    async function lineAct(lineId, action, value) {
      try { d = await rpc('books.line', { id, lineId, action, value }); renderEd(false); replaceBook(d.book); } catch (e) { fail(e); }
    }
    const playTake = (t) => { edAudio.src = proj.mediaBase + encodeURI(t.wav) + '?t=' + Date.now(); edAudio.play().catch(fail); };
    const vdata = () => d.voices.find((v) => v.key === voice);
    const linesNow = () => { const v = vdata(); if (!v) return []; return variant ? ((v.variants.find((x) => x.id === variant) || {}).lines || []) : v.lines; };

    function selectRange(s, e) { ta.focus(); ta.setSelectionRange(s, e); const before = ta.value.slice(0, s).split('\n').length; ta.scrollTop = Math.max(0, (before - 3) * 20); }

    async function moveBreak(i, dir) {
      if (dirty) await saveText();
      const bs = await rpc('books.snap', { id, stem, text: ta.value });
      const at = breaks[i];
      const cands = bs.map((x) => x.at).filter((x) => (dir < 0 ? x < at : x > at)).filter((x) => (i === 0 || x > breaks[i - 1]) && (i === breaks.length - 1 || x < breaks[i + 1]));
      if (!cands.length) { toast('No other sentence end between the neighbouring breaks.', true); return; }
      const next = dir < 0 ? Math.max(...cands) : Math.min(...cands);
      const nb = breaks.slice(); nb[i] = next;
      act('breaks', nb);
    }
    async function splitAtCaret() {
      if (dirty) await saveText();
      const at = ta.selectionStart;
      const bs = await rpc('books.snap', { id, stem, text: ta.value });
      if (!bs.length) { toast('This text has no sentence end to break at.', true); return; }
      const near = bs.map((x) => x.at).sort((a, b) => Math.abs(a - at) - Math.abs(b - at))[0];
      if (breaks.includes(near)) { toast('There is already a break there.', true); return; }
      act('breaks', [...breaks, near].sort((a, b) => a - b));
    }

    // [custom tag] + Enter puts the tag at the cursor in the spoken text (v3/v4 models read tags; others ignore them).
    function customTag() {
      const inp = h('input', { type: 'text', placeholder: '[custom tag]', title: 'Type a tag and press Enter: it goes in at the cursor of the spoken text', style: { width: '130px' } });
      inp.addEventListener('keydown', (e) => {
        e.stopPropagation();
        if (e.key !== 'Enter' || !inp.value.trim()) return;
        let t = inp.value.trim();
        if (!t.startsWith('[')) t = `[${t.replace(/[\[\]]/g, '')}]`;
        insertTagAtCaret(ta, t);
        dirty = true; renderCount();
        inp.value = '';
      });
      return inp;
    }

    function renderEd(textToo) {
      const b = d.book;
      const v = vdata();
      breaks = d.breaks; manual = d.manual;
      clear(headBox).append(
        h('div.grow', h('h2', b.title || stem), h('div.faint.mono', `${stem}.xwm · ${d.records.map((r) => `${r.editorId} ${r.formId}~${r.plugin}`).join(' · ')}`)),
        d.voices.length > 1 ? segmented(d.voices.map((x) => [x.key, x.name, x.hasVoice ? 'Show this voice\'s takes' : 'This voice has no voice picked yet']), voice, (k) => { voice = k; store.set('bk-ed-voice', k); renderEd(false); }) : d.voices.length ? h('span.faint', d.voices[0].name) : h('span.faint', 'No voice reads this book'),
        helpButton('books-and-the-editor'),
        h('button.btn', { title: 'Close (Esc). The text is saved when you leave the box.', onclick: close }, 'Close'));
      clear(gameBox).append(
        h('div.ed-col-head', h('b', 'In game'), h('span.faint', d.inGame ? 'as the game shows it' : 'Scan with the base game folder set to see the game\'s text here')),
        h('div.game-text-body', d.inGame || h('i.faint', '—')));
      if (textToo) { ta.value = currentText(); dirty = false; }
      renderCount();
      clear(toolsBox).append(
        b.variants ? segmented([['', 'Main text', 'The text everyone gets'], ...d.variantsList.map((x) => [x.id, `${x.label || x.plugin} ${x.whenInstalled ? 'installed' : 'not installed'}`, `Used when ${x.plugin} is ${x.whenInstalled ? '' : 'not '}installed`])], variant, async (k) => { if (dirty) await saveText(); variant = k; renderEd(true); }) : null,
        h('button.btn.sm', { title: 'Apply the IVBOS spoken-text rules to this text: asides to dashes, "Vol. II" → "Volume Two", regnal numbers, dates in words', onclick: () => act('applyRules') }, 'Spoken rules'),
        customTag(),
        h('button.btn.sm', { title: 'Let your AI provider add pacing (…) and ALL-CAPS emphasis by the IVBOS rules. You review the result before it is used.', onclick: aiFormat }, 'AI Auto Format'),
        h('button.btn.sm.ghost', { title: 'Back to the game\'s text with the spoken rules (your edits go)', disabled: !d.inGame, onclick: async () => { if (await confirmBox('Restore the game text?', 'Your edits to this book are replaced.', 'Restore', true)) act('restore'); } }, '↺ Game text'),
        d.updatedText ? h('button.btn.sm.accent', { title: 'The built-in set has a newer text for this book', onclick: compareUpdated }, '★ Updated text…') : null,
        h('div.spacer'), countEl);
      // parts
      const lines = linesNow();
      clear(partsBox).append(
        h('div.ed-col-head', h('b', `Parts (${d.partRanges.length})`), h('span.faint', manual ? 'breaks moved by you' : 'automatic breaks'), h('div.spacer'),
          h('button.btn.sm', { title: 'Add a break at the sentence end nearest to the cursor in the spoken text', onclick: splitAtCaret }, icon('scissors', { size: 13 }), ' Break at cursor'),
          manual ? h('button.btn.sm.ghost', { title: 'Let the breaks be placed automatically again', onclick: () => act('autoBreaks') }, 'Auto') : null),
        !v ? h('div.faint', 'No voice reads this book.') : !v.hasVoice ? h('div.banner.warn', { style: { margin: 0 } }, `${v.name} has no voice yet (Narrators & authors tab).`) : null,
        ...d.partRanges.map((r, i) => {
          const l = lines.find((x) => x.part === i + 1);
          const t = l && l.takes.find((x) => x.id === l.sel);
          const takeSel = l && l.takes.length > 1 ? (() => { const s = h('select', { title: 'Which take is used' }, l.takes.slice().reverse().map((x) => h('option', { value: x.id, selected: x.id === l.sel }, `#${x.slot} · ${x.durationSeconds.toFixed(1)} s`))); s.addEventListener('change', () => lineAct(l.id, 'selectTake', s.value)); return s; })() : null;
          const card2 = h('div.part-card' + (l && l.status === 'Stale' ? '.stale' : ''),
            h('div.row', h('span.dot.' + (l ? l.status : 'Pending'), { title: l ? (l.status + (l.err ? ': ' + l.err : '')) : 'Not generated' }),
              h('b', { style: { cursor: 'pointer' }, title: 'Select this part in the spoken text', onclick: () => selectRange(r.start, r.end) }, `Part ${i + 1}`),
              h('span.faint', `${(r.end - r.start).toLocaleString()} chars${r.para ? ' · ¶' : ''}`), h('div.spacer'),
              l ? h('button.act-btn' + (inFlight.has(l.id) ? '.spinning' : ''), { title: v && v.hasVoice ? (l.takes.length ? 'A new take of this part' : 'Generate this part') : 'Pick a voice first', disabled: !v || !v.hasVoice || !!app.job, onclick: () => { inFlight.add(l.id); edAutoPlay.add(l.id); renderEd(false); generate('lines', null, { lineIds: [l.id], single: true }); } }, icon('regen')) : null,
              takeSel,
              l && l.takes.length ? h('button.icon-btn', { title: 'Delete the take in use', onclick: async () => { if (await confirmBox('Delete take?', 'Deletes this part\'s take in use and its file.', 'Delete', true)) lineAct(l.id, 'deleteTake', l.sel); } }, '🗑') : null,
              t ? h('button.icon-btn', { title: 'Play this part\'s take', onclick: () => playTake(t) }, '▶') : null),
            h('div.part-excerpt', { title: 'Click to select this part in the spoken text', onclick: () => selectRange(r.start, r.end) }, excerpt(ta.value, r.start, r.end)));
          const brk = i < breaks.length ? h('div.break-row', h('span.faint', '— break —'),
            h('button.icon-btn', { title: 'Move this break one sentence earlier', onclick: () => moveBreak(i, -1) }, '◀'),
            h('button.icon-btn', { title: 'Move this break one sentence later', onclick: () => moveBreak(i, 1) }, '▶'),
            h('button.icon-btn', { title: 'Remove this break (join the two parts)', onclick: () => act('breaks', breaks.filter((_, j) => j !== i)) }, '✕')) : null;
          return [card2, brk];
        }).flat(),
        h('div.row', { style: { marginTop: '6px' } },
          (() => {
            // Parts that need work (no take, or the text changed since): those. Otherwise a new take of every part.
            const need = lines.filter((l) => !l.takes.length || l.status !== 'Generated');
            const ids = (need.length ? need : lines).map((l) => l.id);
            const label = need.length ? (need.length === lines.length ? (lines.length === 1 ? 'Generate' : 'Generate all parts') : `Generate ${need.length} missing part${need.length === 1 ? '' : 's'}`) : (lines.length === 1 ? 'New take' : 'New take of every part');
            return h('button.btn.sm.primary', { title: need.length ? 'Generate the parts that have no take yet or whose text changed since their take, in this voice' : 'Every part has a current take: make a new take of each, in this voice (earlier takes stay)', disabled: !v || !v.hasVoice || !!app.job || !lines.length,
              onclick: () => { ids.forEach((x) => inFlight.add(x)); if (ids.length === 1) edAutoPlay.add(ids[0]); renderEd(false); generate('lines', null, { lineIds: ids, single: ids.length === 1 }); } }, label);
          })(),
          h('div.spacer'),
          h('button.btn.sm', { title: 'Join the parts (with reverb, pauses and loudness) and play the whole book', disabled: !lines.length || lines.some((l) => !l.sel), onclick: async () => { try { const r = await rpc('books.join', { id, stem, voice, variant }); edAudio.src = r.url; edAudio.play().catch(fail); toast(`${fmtTime(r.seconds)} · ${r.lufs.toFixed(1)} LUFS · peak ${r.peakDb.toFixed(1)} dB`); } catch (e) { fail(e); } } }, '▶ Whole book'),
          h('button.btn.sm.ghost', { title: 'Stop playback', onclick: () => edAudio.pause() }, '■')));
      // checks
      clear(checksBox).append(h('div.ed-col-head', h('b', 'Checks'), h('span.faint', 'flags, never blocks')),
        d.issues.length ? h('div.col', { style: { gap: '4px' } }, d.issues.map((x) => {
          // Click a check to select its place in the spoken text (when the check knows it).
          const at = x.find ? ta.value.indexOf(x.find) : -1;
          return h('div.check-item' + (x.serious ? '.bad' : '') + (at >= 0 ? '.click' : ''), { title: at >= 0 ? 'Click to select it in the spoken text' : '', onclick: at >= 0 ? () => { const i = ta.value.indexOf(x.find); if (i >= 0) selectRange(i, i + x.find.length); } : null }, x.serious ? '⚠ ' : '· ', x.message);
        })) : h('div.faint', '✓ Nothing found: aliases intact, no leftover markup, no short ALL-CAPS words, no unconverted dates.'));
    }

    async function aiFormat() {
      if (!aiReady()) return;
      try {
        if (dirty) await saveText();
        toast('Asking the AI… long books take a while.');
        const r = await rpc('books.aiFormat', { id, stem, text: ta.value });
        const box = h('textarea', { rows: 16, style: { width: '100%' } }, r.suggestion);
        const ok = await modal('AI spoken format', [
          h('div.faint', 'Check the result: pacing (…) and ALL-CAPS emphasis should be added, words left as they are. The checks below compare it with the game text.'),
          r.issues.length ? h('div.col', { style: { gap: '3px' } }, r.issues.map((x) => h('div.check-item' + (x.serious ? '.bad' : ''), (x.serious ? '⚠ ' : '· ') + x.message))) : h('div.faint', '✓ The checks found nothing.'),
          box], [{ label: 'Cancel', value: false }, { label: 'Use this text', primary: true, value: true }], { wide: true });
        if (ok) { ta.value = box.value; dirty = true; await saveText(); }
      } catch (e) { fail(e); }
    }
    async function compareUpdated() {
      const ok = await modal('Updated text from the built-in set', [
        h('div.faint', 'Your text (left) and the set\'s newer text (right). Using the new text marks this book\'s takes as changed.'),
        h('div.row', { style: { alignItems: 'stretch' } }, h('div.box.grow', { style: { whiteSpace: 'pre-wrap', maxHeight: '50vh', overflow: 'auto' } }, d.spoken), h('div.box.grow', { style: { whiteSpace: 'pre-wrap', maxHeight: '50vh', overflow: 'auto' } }, d.updatedText)),
      ], [{ label: 'Cancel', value: null }, { label: 'Keep mine', value: 'keep' }, { label: 'Use the updated text', primary: true, value: 'use' }], { wide: true });
      if (ok === 'use') act('useUpdated'); else if (ok === 'keep') act('keepMine');
    }

    const off = on('booksLine', (ev) => {
      if (ev.projectId !== id || ev.stem !== stem) return;
      inFlight.delete(ev.line.id);
      for (const v of d.voices) {
        if (v.key !== ev.line.voice) continue;
        const list = ev.line.variant ? (v.variants.find((x) => x.id === ev.line.variant) || {}).lines : v.lines;
        if (!list) continue;
        const i = list.findIndex((x) => x.id === ev.line.id);
        if (i >= 0) list[i] = ev.line;
      }
      renderEd(false);
      if (ev.line.status === 'Generated' && ev.line.sel && edAutoPlay.has(ev.line.id)) { edAutoPlay.delete(ev.line.id); const t = ev.line.takes.find((x) => x.id === ev.line.sel); if (t) playTake(t); }
    });
    const edAutoPlay = new Set();
    card.append(headBox, h('div.ed-body', h('div.ed-left', gameBox), h('div.ed-mid', h('div.ed-col-head', h('b', 'Spoken text'), h('span.faint', 'what the voice reads')), toolsBox, ta, checksBox), h('div.ed-right', partsBox)));
    renderEd(true);
    document.body.append(overlay);
    ta.focus();
  }

  // ---------------------------------------------------------------- layout
  let shownTab = null;
  function renderAll() {
    const same = shownTab === tab; shownTab = tab;
    keepScroll(body, () => renderAllNow(), same);
  }
  function renderAllNow() {
    const scroll = grid.el.isConnected ? grid.el.scrollTop : null;
    renderHead();
    clear(body);
    if (tab === 'setup') { body.append(renderSetup()); return; }
    if (tab === 'voices') { body.append(renderVoices()); return; }
    if (!proj.books.length) { body.append(h('div.empty', h('h2', 'No books yet'), h('div', proj.source === 'plugins' ? 'Add the plugins in Setup, then Scan.' : 'The set is empty.'))); return; }
    if (!elevenModels) loadVoices();
    const split = h('div.split' + (showInsp ? '' : '.no-insp'));
    split.append(left, insp);
    renderVbar();
    body.append(vbar, filters, split, actions);
    renderFilters();
    grid.setRows(visible());
    if (scroll != null) { grid.el.scrollTop = scroll; grid.paint(true); }
    renderInspector();
    renderActions();
  }

  const offLine = on('booksLine', (d) => {
    if (!document.body.contains(view)) { offLine(); return; }
    if (d.projectId !== id) return;
    // The book row's dots come from the server: refresh that book.
    refreshBook(d.stem);
  });
  const refreshBook = debounce(async (stem) => {
    try { const x = await rpc('books.bookGet', { id, stem }); replaceBook(x.book); inFlight.delete(stem); if (grid.el.isConnected) { grid.refreshRow(stem); flashRow(grid, stem, x.book.voices.some((v) => v.status === 'missing')); } } catch { /* ignore */ }
  }, 250);
  let jobSeen = false;
  document.addEventListener('job-progress', () => { if (!jobSeen && document.body.contains(view)) { jobSeen = true; renderHead(); if (tab === 'books') { grid.paint(true); renderActions(); } } });
  document.addEventListener('job-done', async (e) => {
    jobSeen = false;
    if (!document.body.contains(view)) { offLine && offLine(); return; }
    if (inFlight.size) markInFlight(null);
    const d = e.detail || {};
    if ((d.kind === 'books-scan' && d.id === id) || (d.kind === 'generate' && d.booksId === id) || (d.kind === 'books-export' && d.id === id)) {
      try { proj = await rpc('books.get', { id }); reindex(); } catch { /* keep */ }
      if (d.kind === 'books-scan' && d.ok) toast(`Read: ${d.report.books.toLocaleString()} books${d.report.npc ? `, ${d.report.npc} read by NPC voices` : ''}.`);
      if (d.kind === 'books-export' && d.report) exportReport(d);
      if (!d.ok && d.message) toast(d.message, true);
    }
    renderAll();
  });

  async function exportReport(d) {
    const r = d.report;
    const v = await modal(r.missing.length || r.failed ? 'Exported, with gaps' : 'Exported', [
      h('div.kv',
        h('div', 'Books with audio'), h('div', String(r.books)),
        h('div', 'Audio files'), h('div', `${r.files} (${r.joined} joined now, ${r.reused} unchanged)`),
        h('div', '.ini'), h('div.mono', r.ini ? `${r.ini} · ${r.iniLines} lines` : 'not written'),
        h('div', 'Archives'), h('div.mono', { style: { whiteSpace: 'pre-wrap' } }, r.archives.join('\n') || (proj.packaging === 'loose' ? 'loose files' : '—')),
        h('div', 'Installer'), h('div.mono', r.fomod ? 'fomod\\ModuleConfig.xml' : '—'),
        h('div', 'Report'), h('div.mono', r.report || '—'),
        h('div', 'Folder'), h('div.mono', d.folder)),
      r.missing.length ? h('div', h('div.field-label', `${r.missing.length} book(s) without complete takes were left out`), h('div.box.mono', { style: { maxHeight: '160px', overflow: 'auto', whiteSpace: 'pre-wrap', fontSize: '12px' } }, r.missing.slice(0, 200).join('\n'))) : null,
      r.errors.length ? h('div.box.mono', { style: { maxHeight: '160px', overflow: 'auto' } }, r.errors.slice(0, 50).join('\n')) : null,
    ], [{ label: 'Open the report', value: 'report' }, { label: 'Open folder', value: 'open' }, { label: 'OK', primary: true, value: true }], { help: 'export-and-the-installer' });
    if (v === 'open') rpc('open.path', { path: d.folder });
    if (v === 'report' && r.report) rpc('open.path', { path: d.folder + '\\' + r.report });
  }

  renderAll();
  return view;
}

function excerpt(text, s, e) {
  const t = text.slice(s, e).replace(/\s+/g, ' ').trim();
  return t.length <= 180 ? t : t.slice(0, 110) + ' … ' + t.slice(-60);
}
function fmtTime(sec) { const m = Math.floor(sec / 60); const s = Math.round(sec % 60); return `${m}:${String(s).padStart(2, '0')}`; }
