// Voice packs and global Settings. Every change is saved immediately.
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, debounce } from './lib.js';
import { app, go, refreshState } from './app.js';

function pathField(value, onSave, { folder = false, filter = '', title = '' } = {}) {
  const i = h('input', { type: 'text', value: value || '' });
  i.addEventListener('change', () => onSave(i.value.trim()));
  const browse = h('button.btn.sm', {
    onclick: async () => {
      const r = folder ? await rpc('pick.folder', { title }) : await rpc('pick.file', { title, filter });
      if (r) { i.value = r; onSave(r); }
    },
  }, 'Browse');
  return h('div.path-input', i, browse);
}

function status(ok, label) {
  return h('span.badge.' + (ok ? 'b-valid' : 'b-broken'), ok ? '✓ ' + label : '✕ ' + label);
}

// ------------------------------------------------------------------ voice packs

export async function voicesView(selectedId) {
  const s = await refreshState();
  const view = h('div.view');
  const list = h('div', { style: { width: '240px', borderRight: '1px solid var(--line)', overflow: 'auto', padding: '10px' } });
  const editor = h('div.view-body');
  view.append(
    h('div.view-head', h('h1', 'Voice packs'), h('span.sub', 'A voice with its settings, tags, pronunciations, naming and folders.'), h('div.spacer'),
      h('button.btn.primary', { onclick: () => edit(null) }, '+ New voice pack')),
    h('div', { style: { display: 'flex', flex: 1, minHeight: 0 } }, list, editor));

  let current = null;

  function renderList() {
    clear(list);
    if (!s.voicePacks.length) list.append(h('div.faint', 'No voice packs yet.'));
    for (const v of s.voicePacks) {
      list.append(h('div.nav-item' + (current && current.id === v.id ? '.active' : ''), { onclick: () => edit(v) },
        h('div', h('div', v.name), h('div.faint', { style: { fontSize: '11px' } }, (v.packId || 'no pack ID') + ' · ' + (v.naming === 'Legacy' ? 'Legacy' : 'DBVO 2.0')))));
    }
  }

  async function persist() {
    if (!current || !current.name.trim()) return;
    try {
      const saved = await rpc('voice.save', { voice: current });
      const isNew = !s.voicePacks.some((v) => v.id === saved.id);
      current = saved;
      if (isNew) s.voicePacks.push(saved);
      else s.voicePacks[s.voicePacks.findIndex((v) => v.id === saved.id)] = saved;
      renderList();
    } catch (e) { fail(e); }
  }
  const persistSoon = debounce(persist, 300);

  function edit(v) {
    current = v ? structuredClone(v) : {
      id: Math.random().toString(36).slice(2, 10), name: '', packId: '', author: '', naming: 'Legacy', engine: 'elevenlabs',
      eleven: { voiceId: '', voiceName: '', modelId: 'eleven_v4', stability: 0.5, similarity: 0.75, style: 0, speakerBoost: false, speed: 1, seed: null, outputFormat: 'mp3_44100_128', languageCode: '', textNormalization: 'auto', concurrency: 5, pronunciationDictionaryIds: [] },
      tags: ['[whispers]', '[sighs]', '[laughs]', '[sarcastic]', '[curious]', '[excited]'],
      pronunciations: [], destinations: { wavLipFolder: '', finalRoot: '', fuzTemplate: '{mod}\\Sound\\DBVO\\{packId}', releaseFolder: '' },
      defaultReferenceIds: [], settingsLocked: false, loudnorm: false, gainDb: 0, aliasParsing: false, overwriteWavLip: false, overwriteFuz: false, trimTailMs: 15, fadeOutMs: 10,
    };
    renderList();
    renderEditor();
  }

  function txt(obj, key, opts = {}) {
    const i = h('input', { type: opts.type || 'text', value: obj[key] ?? '', placeholder: opts.placeholder || '' });
    i.addEventListener('change', () => {
      obj[key] = opts.type === 'number' ? Number(i.value) : i.value;
      if (current.name.trim()) persistSoon();
    });
    return i;
  }

  function toggle(obj, key, label) {
    return h('label.row', h('input', { type: 'checkbox', checked: obj[key], onchange: (e) => { obj[key] = e.target.checked; persistSoon(); } }), label);
  }

  function renderEditor() {
    clear(editor);
    const v = current;
    const d = v.destinations;
    const naming = h('select', { onchange: (e) => { v.naming = e.target.value; persistSoon(); } },
      h('option', { value: 'Legacy', selected: v.naming === 'Legacy' }, 'Legacy (DBVO 1.0) — readable filenames'),
      h('option', { value: 'Dbvo2Hash', selected: v.naming === 'Dbvo2Hash' }, 'DBVO 2.0 — xxHash64 filenames'));

    const tagsBox = h('div.row.wrap');
    const renderTags = () => {
      clear(tagsBox).append(
        v.tags.map((t, i) => h('span.chip.tag-chip', t, h('button.icon-btn', { title: 'Remove', onclick: () => { v.tags.splice(i, 1); renderTags(); persistSoon(); } }, '×'))),
        h('button.btn.sm', { onclick: async () => {
          const r = await prompt('Add tags', [{ name: 't', label: 'Tags', placeholder: '[menacing] [softly]' }]);
          if (!r) return;
          for (const m of r.t.match(/\[[^\]]+\]|[^\s\[\]][^\[\]]*[^\s\[\]]|[^\s\[\]]/g) || []) {
            const t = '[' + m.replace(/^\[|\]$/g, '').trim() + ']';
            if (!v.tags.includes(t)) v.tags.push(t);
          }
          renderTags();
          persistSoon();
        } }, '+ Add'));
    };
    renderTags();

    const prBody = h('tbody');
    const renderPron = () => {
      clear(prBody);
      v.pronunciations.forEach((p, i) => {
        const word = h('input', { type: 'text', value: p.word, placeholder: 'Alduin' });
        const rep = h('input', { type: 'text', value: p.replacement, placeholder: p.kind === 'phoneme' ? 'ˈæl.duː.ɪn' : 'Aldooin' });
        const kind = h('select', h('option', { value: 'alias', selected: p.kind === 'alias' }, 'Say as'), h('option', { value: 'phoneme', selected: p.kind === 'phoneme' }, 'IPA'));
        const ww = h('input', { type: 'checkbox', checked: p.wholeWord });
        const cs = h('input', { type: 'checkbox', checked: p.caseSensitive });
        const upd = () => { Object.assign(p, { word: word.value, replacement: rep.value, kind: kind.value, wholeWord: ww.checked, caseSensitive: cs.checked }); persistSoon(); };
        [word, rep, kind, ww, cs].forEach((x) => x.addEventListener('change', upd));
        prBody.append(h('tr', h('td', word), h('td', kind), h('td', rep), h('td', ww), h('td', cs),
          h('td', h('button.icon-btn', { onclick: () => { v.pronunciations.splice(i, 1); renderPron(); persistSoon(); } }, '×'))));
      });
    };
    renderPron();

    const refs = s.references.map((r) => h('label.row', h('input', {
      type: 'checkbox', checked: v.defaultReferenceIds.includes(r.id),
      onchange: (e) => { v.defaultReferenceIds = e.target.checked ? [...v.defaultReferenceIds, r.id] : v.defaultReferenceIds.filter((x) => x !== r.id); persistSoon(); },
    }), r.name, h('span.faint', `${r.lineCount} lines`)));

    const save = h('button.btn.primary', { onclick: async () => { if (!v.name.trim()) { toast('Give the voice pack a name.', true); return; } await persist(); toast('Saved.'); } }, 'Save');

    editor.append(h('div.form',
      h('h2', 'Voice pack'),
      h('label', 'Name'), txt(v, 'name', { placeholder: 'e.g. Vampira' }),
      h('label', 'DBVO voice pack ID'), txt(v, 'packId', { placeholder: 'e.g. vampira' }),
      h('div.help', 'The folder name under Sound\\DBVO\\ and the voice_pack_id in the DBVO JSON.'),
      h('label', 'Author'), txt(v, 'author'),
      h('label', 'File naming'), naming,
      h('div.help', 'Legacy names are readable (you can see which line each file is). DBVO 2.0 names are hashes of the line, identical to DBVO Voice Pack Maker 2.7.0.'),

      h('h2', 'Folders'),
      h('label', 'WAV + LIP folder'), pathField(d.wavLipFolder, (x) => { d.wavLipFolder = x; persistSoon(); }, { folder: true, title: 'WAV + LIP folder' }),
      h('div.help', 'Generated, correctly named WAV + LIP files are copied here. A copy of each DBVO Fix plugin goes here too.'),
      h('label', 'Final folder (mod / FOMOD root)'), pathField(d.finalRoot, (x) => { d.finalRoot = x; persistSoon(); }, { folder: true, title: 'Final folder' }),
      h('label', '.fuz folder template'), txt(d, 'fuzTemplate', { placeholder: '{mod}\\Sound\\DBVO\\{packId}' }),
      h('div.help', 'Under the final folder. Placeholders: {mod} {packId} {voicePack} {patch}. The .fuz files land in the last folder; the DBVO Fix plugin goes in the folder before "Sound". Example: 02 Large Quests\\Vamp - {mod}\\Sound\\DBVO\\{packId}'),
      h('label', 'Release folder'), pathField(d.releaseFolder, (x) => { d.releaseFolder = x; persistSoon(); }, { folder: true, title: 'Release folder' }),
      h('div.help', 'Where packaged FOMOD archives will go (Phase 3).'),

      h('h2', 'Tags toolbar'),
      h('label', 'Tags for this voice'), tagsBox,
      h('div.help', 'These appear above the Phase 2 grid. Click one to add it at the start of the selected lines.'),

      h('h2', 'Pronunciation dictionary'),
      h('label', 'Entries'), h('div', h('table.tbl', h('thead', h('tr', h('th', 'Word'), h('th', 'Kind'), h('th', 'Replacement'), h('th', 'Whole word'), h('th', 'Case'), h('th', ''))), prBody),
        h('button.btn.sm', { style: { marginTop: '6px' }, onclick: () => { v.pronunciations.push({ word: '', kind: 'alias', replacement: '', wholeWord: true, caseSensitive: false }); renderPron(); } }, '+ Add entry')),
      h('div.help', '"Say as" replaces the word before sending it (works with every model). "IPA" is sent as inline IPA (/…/) to Eleven v3/v4 and Flash v2, which support it; other models get the word unchanged.'),

      h('h2', 'Audio'),
      h('label', 'Tail trim'), h('div.row', txt(v, 'trimTailMs', { type: 'number' }), h('span.dim', 'ms cut, then'), txt(v, 'fadeOutMs', { type: 'number' }), h('span.dim', 'ms fade-out')),
      h('div.help', 'Removes the end-of-clip artefact. Applied before the LIP file is made.'),
      h('label', 'Loudness'), toggle(v, 'loudnorm', 'Normalize loudness (EBU R128, −16 LUFS). Off keeps the original DBVO Voice Pack Maker level.'),
      h('label', 'Gain'), h('div.row', txt(v, 'gainDb', { type: 'number' }), h('span.dim', 'dB')),

      h('h2', 'Behaviour'),
      h('label', 'Overwrite'), h('div.col', toggle(v, 'overwriteWavLip', 'Overwrite existing WAV + LIP files'), toggle(v, 'overwriteFuz', 'Overwrite existing .fuz files')),
      h('label', 'Alias parsing'), toggle(v, 'aliasParsing', 'Enable DBVO 2.0 alias parsing in the voice pack JSON (rarely needed; buggy in game)'),
      h('label', 'Omit by default'), refs.length ? h('div.col', refs) : h('div.faint', 'No reference topic files yet — add them in Settings.'),
      h('div.help', 'New DBVO Patches for this voice start with these references ticked.'),
      h('label', 'Lock'), toggle(v, 'settingsLocked', 'Lock the ElevenLabs settings for this voice pack (prevents accidental changes)'),

      h('div'), h('div.row', save,
        s.voicePacks.some((x) => x.id === v.id) ? h('button.btn.ghost.danger', { onclick: async () => {
          if (!(await confirmBox('Delete voice pack?', `Delete "${v.name}"? DBVO Patches using it stop working.`, 'Delete', true))) return;
          await rpc('voice.delete', { id: v.id });
          go('voices');
        } }, 'Delete') : null),
    ));
  }

  renderList();
  const initial = s.voicePacks.find((v) => v.id === selectedId) || (selectedId === 'new' ? null : s.voicePacks[0]);
  if (initial || selectedId === 'new' || !s.voicePacks.length) edit(initial || null);
  return view;
}

// ------------------------------------------------------------------ settings

export async function settingsView() {
  const s = await refreshState();
  const st = s.settings;
  const view = h('div.view');
  const body = h('div.view-body');
  view.append(h('div.view-head', h('h1', 'Settings'), h('span.sub', 'Saved as you change them.')), body);

  const save = async (patchObj) => {
    try { app.state = await rpc('settings.save', { settings: patchObj }); toast('Saved.'); } catch (e) { fail(e); }
  };

  const apiStatus = h('span.dim');
  const apiKey = h('input', { type: 'password', placeholder: st.hasApiKey ? '•••••••• (saved, encrypted)' : 'Paste your ElevenLabs API key' });
  const setKey = h('button.btn.sm', { onclick: async () => {
    try { await rpc('settings.setApiKey', { key: apiKey.value.trim() }); apiKey.value = ''; apiKey.placeholder = '•••••••• (saved, encrypted)'; await test(); } catch (e) { fail(e); }
  } }, 'Save key');
  const test = async () => {
    apiStatus.textContent = 'Checking…';
    try {
      const sub = await rpc('eleven.subscriptionInfo');
      apiStatus.textContent = `✓ ${sub.tier || 'plan'} · ${sub.characterCount.toLocaleString()} / ${sub.characterLimit.toLocaleString()} characters used`;
    } catch (e) { apiStatus.textContent = '✕ ' + e.message; }
  };
  if (st.hasApiKey) test();

  const aiKey = h('input', { type: 'password', placeholder: st.aiProvider.hasKey ? '•••••••• (saved)' : 'API key (not needed for local Ollama)' });

  const refsBox = h('div.col');
  const renderRefs = () => {
    clear(refsBox).append(
      app.state.references.length ? h('table.tbl', h('tbody', app.state.references.map((r) => h('tr',
        h('td', r.name), h('td.faint', `${r.lineCount} lines`),
        h('td', h('button.btn.sm.ghost.danger', { onclick: async () => { if (await confirmBox('Remove reference?', `Remove "${r.name}"? DBVO Patches using it will stop omitting its lines after their next sync.`, 'Remove', true)) { app.state = await rpc('references.remove', { id: r.id }); renderRefs(); } } }, 'Remove')))))) : h('div.faint', 'None yet.'),
      h('div', h('button.btn.sm', { onclick: async () => {
        const file = await rpc('pick.file', { title: 'Choose a topic .txt', filter: 'Topic files|*.txt' });
        if (!file) return;
        const r = await prompt('Name this reference', [{ name: 'name', label: 'Name', value: 'Skyrim Base Game + DLC', help: 'Shown wherever you choose which lines to omit.' }]);
        if (!r || !r.name.trim()) return;
        try { app.state = await rpc('references.add', { name: r.name, path: file }); renderRefs(); } catch (e) { fail(e); }
      } }, '+ Add topic file')));
  };
  renderRefs();

  body.append(h('div.form',
    h('h2', 'ElevenLabs'),
    h('label', 'API key'), h('div.row', apiKey, setKey, h('button.btn.sm.ghost', { onclick: test }, 'Test')),
    h('div'), apiStatus,
    h('div.help', 'Stored encrypted for your Windows user (DPAPI).'),

    h('h2', 'Tools'),
    h('label', 'FonixData.cdf'), pathField(st.fonixDataPath, (x) => save({ fonixDataPath: x }), { filter: 'FonixData|*.cdf', title: 'FonixData.cdf' }),
    h('div.help', 'Needed for LIP files. Usually in Skyrim Special Edition\\Data\\Sound\\Voice\\Processing.'),
    h('label', 'xWMAEncode.exe'), pathField(st.xwmaEncodePath, (x) => save({ xwmaEncodePath: x }), { filter: 'xWMAEncode|xWMAEncode.exe;*.exe', title: 'xWMAEncode.exe' }),
    h('div.help', 'Comes with Yakitori Audio Converter. Needed for .fuz files.'),
    h('label', 'ffmpeg.exe'), pathField(st.ffmpegPath, (x) => save({ ffmpegPath: x }), { filter: 'ffmpeg|*.exe', title: 'ffmpeg.exe' }),
    h('label', 'FaceFXWrapper.exe'), pathField(st.faceFxWrapperPath, (x) => save({ faceFxWrapperPath: x }), { filter: 'FaceFXWrapper|*.exe', title: 'FaceFXWrapper.exe' }),
    h('div.help', 'Found automatically if DBVO Voice Pack Maker is installed.'),
    h('label', 'Status'), h('div.row', status(!!st.fonixDataPath, 'FonixData'), status(!!st.xwmaEncodePath, 'xWMAEncode'), status(!!st.ffmpegPath, 'ffmpeg'), status(!!st.faceFxWrapperPath, 'FaceFXWrapper')),

    h('h2', 'Plugins'),
    h('label', 'Base game plugins folder'), pathField(st.baseGameFolder, (x) => save({ baseGameFolder: x }), { folder: true, title: 'Folder with Skyrim.esm, Update.esm and the DLC masters' }),
    h('div.help', 'A folder holding Skyrim.esm, Update.esm, Dawnguard.esm, HearthFires.esm and Dragonborn.esm (your game\'s Data folder works). Used as masters when parsing.'),
    h('label', 'Strings folder'), pathField(st.stringsFolder, (x) => save({ stringsFolder: x }), { folder: true, title: 'Folder with .STRINGS files' }),
    h('div.help', 'For localized plugins such as Skyrim.esm: a folder with Skyrim_English.STRINGS etc. Use the strings your players have (vanilla, unless you target a strings mod).'),
    h('label', 'Strings language'), (() => { const i = h('input', { type: 'text', value: st.stringsLanguage }); i.addEventListener('change', () => save({ stringsLanguage: i.value.trim() || 'English' })); return i; })(),

    h('h2', 'Omit lines users already have'),
    h('label', 'Reference topic files'), refsBox,
    h('div.help', 'Topic .txt files of lines your users already have, such as the base game voice pack. In a DBVO Patch, lines found here are omitted so you never voice a line twice. Only add packs every user installs; never optional mods.'),

    h('h2', 'AI auto-tagging'),
    h('label', 'Endpoint'), (() => { const i = h('input', { type: 'text', value: st.aiProvider.endpoint, placeholder: 'https://api.openai.com/v1  or  http://localhost:11434/v1' }); i.addEventListener('change', () => save({ aiEndpoint: i.value.trim() })); return i; })(),
    h('label', 'Model'), (() => { const i = h('input', { type: 'text', value: st.aiProvider.model, placeholder: 'gpt-4o-mini, llama3.1, …' }); i.addEventListener('change', () => save({ aiModel: i.value.trim() })); return i; })(),
    h('label', 'API key'), h('div.row', aiKey, h('button.btn.sm', { onclick: async () => { app.state = await rpc('settings.setAiKey', { key: aiKey.value.trim() }); aiKey.value = ''; aiKey.placeholder = '•••••••• (saved)'; toast('Saved.'); } }, 'Save key')),
    h('div.help', 'ElevenLabs has no API for its Enhance button, so auto-tagging uses an AI model you choose, with rules that forbid changing any words. Results that change words are discarded.'),

    h('h2', 'Library'),
    h('label', 'Location'), h('div.row', h('span.mono', s.libraryRoot), h('button.btn.sm', { onclick: () => rpc('library.open') }, 'Open')),
    h('label', 'Backups'), h('div.row', (() => { const i = h('input', { type: 'number', value: st.backupsToKeep, style: { width: '70px' } }); i.addEventListener('change', () => save({ backupsToKeep: Number(i.value) || 30 })); return i; })(),
      h('span.dim', 'kept · made automatically before imports, generation and deletes'),
      h('button.btn.sm', { onclick: async () => { const p = await rpc('backup.now', { reason: 'manual' }); toast('Backup: ' + p); } }, 'Back up now')),
  ));
  return view;
}
