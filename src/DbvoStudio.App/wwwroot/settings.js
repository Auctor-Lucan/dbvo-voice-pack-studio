// Settings: tools, voice packs, pronunciation, omit references, categories, library. Saved as you change them.
import { rpc, h, clear, toast, fail, modal, confirmBox, prompt, store, switchEl, segmented } from './lib.js';
import { app, go, refreshState } from './app.js';

const TABS = [
  ['general', 'General & tools'],
  ['voices', 'Voice packs'],
  ['pronunciation', 'Pronunciation'],
  ['references', 'Omit references'],
  ['categories', 'Categories'],
  ['library', 'Library'],
];

function section(title, extra, ...rows) {
  return h('div.section', h('div.section-head', h('h2', title), extra), h('div.section-body', rows));
}
function srow(label, help, control) {
  return h('div.srow', h('div.lbl', h('span', label), help ? h('small', help) : null), h('div', control));
}
function status(ok, label) {
  return h('span.badge.' + (ok ? 'b-valid' : 'b-broken'), (ok ? '✓ ' : '✕ ') + label);
}
function pathField(value, onSave, { folder = false, filter = '', title = '', placeholder = '' } = {}) {
  const i = h('input', { type: 'text', value: value || '', placeholder });
  i.addEventListener('change', () => onSave(i.value.trim()));
  return h('div.path-input', i, h('button.btn.sm', {
    onclick: async () => {
      const r = folder ? await rpc('pick.folder', { title }) : await rpc('pick.file', { title, filter });
      if (r) { i.value = r; onSave(r); }
    },
  }, 'Browse'));
}

export async function settingsView(tab, selectedId) {
  await refreshState();
  tab = TABS.some(([k]) => k === tab) ? tab : store.get('settingsTab', 'general');
  const view = h('div.view');
  const sub = h('div.subnav');
  const body = h('div.view-body');
  view.append(h('div.view-head', h('h1', 'Settings'), h('span.sub', 'Everything here is saved as you change it.')), h('div.settings', sub, body));

  const show = async (t, id) => {
    tab = t;
    store.set('settingsTab', t);
    clear(sub).append(TABS.map(([k, label]) => h('button.nav-item' + (k === tab ? '.active' : ''), { onclick: () => show(k) }, label)));
    clear(body);
    const render = { general, voices, pronunciation, references, categories, library }[tab];
    try { body.append(await render(id, () => show(tab, id))); } catch (e) { fail(e); }
  };
  await show(tab, selectedId);
  return view;
}

async function saveSettings(patchObj) {
  app.state = await rpc('settings.save', { settings: patchObj });
  return app.state;
}

// ------------------------------------------------------------------ general & tools

async function general(_, rerender) {
  const st = app.state.settings;
  const t = st.tools;
  const save = async (o) => { try { await saveSettings(o); toast('Saved.'); rerender(); } catch (e) { fail(e); } };

  const apiStatus = h('span.dim');
  const apiKey = h('input', { type: 'password', placeholder: st.hasApiKey ? '•••••••• (saved, encrypted)' : 'Paste your ElevenLabs API key', style: { width: '320px' } });
  const test = async () => {
    apiStatus.textContent = 'Checking…';
    try {
      const s = await rpc('eleven.subscriptionInfo');
      apiStatus.textContent = `✓ ${s.tier || 'plan'} · ${s.characterCount.toLocaleString()} / ${s.characterLimit.toLocaleString()} characters used`;
    } catch (e) { apiStatus.textContent = '✕ ' + e.message; }
  };
  if (st.hasApiKey) test();
  const aiKey = h('input', { type: 'password', placeholder: st.aiProvider.hasKey ? '•••••••• (saved)' : 'not needed for local Ollama', style: { width: '260px' } });
  const textInput = (value, onSave, placeholder = '') => { const i = h('input', { type: 'text', value: value || '', placeholder, style: { width: '100%' } }); i.addEventListener('change', () => onSave(i.value.trim())); return i; };

  return h('div',
    section('ElevenLabs', null,
      srow('API key', 'Stored encrypted for your Windows user.', h('div.col',
        h('div.row', apiKey,
          h('button.btn.sm.primary', { onclick: async () => { try { await rpc('settings.setApiKey', { key: apiKey.value.trim() }); apiKey.value = ''; await refreshState(); rerender(); } catch (e) { fail(e); } } }, 'Save key'),
          h('button.btn.sm', { onclick: test }, 'Test')),
        apiStatus))),
    section('Tools', h('div.row', status(t.fonix, 'FonixData'), status(t.xwma, 'xWMAEncode'), status(t.ffmpeg, 'ffmpeg'), status(t.faceFx, 'FaceFXWrapper')),
      srow('FonixData.cdf', 'Needed for LIP files.', pathField(st.fonixDataPath, (x) => save({ fonixDataPath: x }), { filter: 'FonixData|*.cdf', title: 'FonixData.cdf' })),
      srow('xWMAEncode.exe', 'Comes with Yakitori Audio Converter. Needed for .fuz files.', pathField(st.xwmaEncodePath, (x) => save({ xwmaEncodePath: x }), { filter: 'xWMAEncode|*.exe', title: 'xWMAEncode.exe' })),
      srow('ffmpeg.exe', 'Found automatically if DBVO Voice Pack Maker is installed.', pathField(st.ffmpegPath, (x) => save({ ffmpegPath: x }), { filter: 'ffmpeg|*.exe', title: 'ffmpeg.exe' })),
      srow('FaceFXWrapper.exe', 'Found automatically if DBVO Voice Pack Maker is installed.', pathField(st.faceFxWrapperPath, (x) => save({ faceFxWrapperPath: x }), { filter: 'FaceFXWrapper|*.exe', title: 'FaceFXWrapper.exe' }))),
    section('Plugins', null,
      srow('Base game plugins folder', 'A folder with Skyrim.esm, Update.esm and the DLC masters (your game\'s Data folder works). Used as masters when reading plugins.',
        h('div.col', pathField(st.baseGameFolder, (x) => save({ baseGameFolder: x }), { folder: true, title: 'Base game plugins' }), status(t.baseGame, 'found'))),
      srow('Strings folder', 'For localized plugins such as Skyrim.esm: a folder with Skyrim_English.STRINGS and so on. Use the strings your players have.',
        h('div.col', pathField(st.stringsFolder, (x) => save({ stringsFolder: x }), { folder: true, title: 'Strings folder' }), status(t.strings, 'found'))),
      srow('Strings language', 'The language part of the strings file names.', textInput(st.stringsLanguage, (x) => save({ stringsLanguage: x || 'English' })))),
    section('AI auto-tagging', null,
      srow('Endpoint', 'OpenAI-compatible: https://api.openai.com/v1, or http://localhost:11434/v1 for Ollama.', textInput(st.aiProvider.endpoint, (x) => save({ aiEndpoint: x }), 'https://api.openai.com/v1')),
      srow('Model', null, textInput(st.aiProvider.model, (x) => save({ aiModel: x }), 'gpt-4o-mini, llama3.1, …')),
      srow('API key', 'ElevenLabs has no API for its Enhance button, so auto-tagging uses a model you choose. Suggestions that change any words are discarded.',
        h('div.row', aiKey, h('button.btn.sm', { onclick: async () => { await rpc('settings.setAiKey', { key: aiKey.value.trim() }); aiKey.value = ''; await refreshState(); rerender(); toast('Saved.'); } }, 'Save key')))),
  );
}

// ------------------------------------------------------------------ voice packs

const NEW_VOICE = () => ({
  id: Math.random().toString(36).slice(2, 10), name: '', packId: '', author: '', naming: 'Legacy', engine: 'elevenlabs',
  eleven: { voiceId: '', voiceName: '', modelId: 'eleven_v4', stability: 0.5, similarity: 0.75, style: 0, speakerBoost: false, speed: 1, seed: null, outputFormat: 'mp3_44100_128', languageCode: '', textNormalization: 'auto', concurrency: 5, pronunciationDictionaryIds: [] },
  tags: ['[whispers]', '[sighs]', '[laughs]', '[sarcastic]', '[curious]', '[excited]'],
  pronunciations: [], destinations: { wavLipFolder: '', finalRoot: '', fuzTemplate: 'Sound\\DBVO\\{packId}', releaseFolder: '' },
  defaultReferenceIds: [], settingsLocked: false, loudnorm: false, gainDb: 0, aliasParsing: false, overwriteWavLip: false, overwriteFuz: false, trimTailMs: 15, fadeOutMs: 10,
});

async function voices(selectedId, rerender) {
  const packs = app.state.voicePacks;
  const wrap = h('div', { style: { display: 'grid', gridTemplateColumns: '240px 1fr', gap: '16px', alignItems: 'start' } });
  const list = h('div.section', { style: { padding: '8px' } });
  const editor = h('div');
  wrap.append(list, editor);

  let current = null;
  let timer = null;
  const savedEl = h('span.saved');

  // Save the exact object that was edited, even if the user switches to another pack before the timer fires.
  async function persistNow(obj = current) {
    clearTimeout(timer);
    timer = null;
    if (!obj || !obj.name.trim()) { savedEl.textContent = 'Give the voice pack a name to save it'; savedEl.className = 'saved bad'; return false; }
    try {
      const saved = await rpc('voice.save', { voice: obj });
      const i = packs.findIndex((v) => v.id === saved.id);
      if (i >= 0) packs[i] = saved; else packs.push(saved);
      if (current && current.id === saved.id) savedEl.textContent = 'Saved ✓', savedEl.className = 'saved ok';
      renderList();
      return true;
    } catch (e) {
      savedEl.textContent = 'Not saved: ' + e.message;
      savedEl.className = 'saved bad';
      fail(e);
      return false;
    }
  }
  function persistSoon() {
    const obj = current;
    savedEl.textContent = 'Saving…';
    savedEl.className = 'saved';
    clearTimeout(timer);
    timer = setTimeout(() => persistNow(obj), 400);
  }

  function renderList() {
    clear(list).append(
      h('button.btn.primary', { style: { width: '100%', justifyContent: 'center', marginBottom: '8px' }, onclick: () => select(null) }, '+ New voice pack'),
      packs.length ? packs.map((v) => h('button.nav-item' + (current && current.id === v.id ? '.active' : ''), { onclick: () => select(v) },
        h('div', h('div', v.name), h('div.faint', { style: { fontSize: '11px' } }, (v.packId || 'no pack ID') + ' · ' + (v.naming === 'Legacy' ? 'Legacy' : 'DBVO 2.0')))))
        : h('div.faint', { style: { padding: '8px' } }, 'No voice packs yet.'));
  }

  async function select(v) {
    if (timer) await persistNow();
    current = v ? structuredClone(v) : NEW_VOICE();
    savedEl.textContent = v ? 'Saved ✓' : 'New — give it a name';
    savedEl.className = 'saved' + (v ? ' ok' : '');
    renderList();
    renderEditor();
  }

  function txt(obj, key, opts = {}) {
    const i = h('input', { type: opts.type || 'text', value: obj[key] ?? '', placeholder: opts.placeholder || '', style: { width: opts.width || '100%' } });
    i.addEventListener('input', () => { obj[key] = opts.type === 'number' ? Number(i.value) : i.value; persistSoon(); });
    return i;
  }

  function renderEditor() {
    const v = current;
    const d = v.destinations;
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

    clear(editor).append(
      section('Voice pack', h('div.row', h('div.spacer'), savedEl, h('button.btn.primary.sm', { onclick: async () => { if (await persistNow()) toast('Saved.'); } }, 'Save')),
        srow('Name', null, txt(v, 'name', { placeholder: 'e.g. Vampira' })),
        srow('DBVO voice pack ID', 'The folder name under Sound\\DBVO\\ and the voice_pack_id in the DBVO JSON.', txt(v, 'packId', { placeholder: 'e.g. vampira' })),
        srow('Author', null, txt(v, 'author')),
        srow('File naming', 'Legacy names are readable, so you can see which line each file is. DBVO 2.0 names are hashes of the line, identical to DBVO Voice Pack Maker 2.7.0.',
          segmented([['Legacy', 'Legacy (DBVO 1.0)'], ['Dbvo2Hash', 'DBVO 2.0 hash']], v.naming, (x) => { v.naming = x; persistSoon(); renderEditor(); })),
        srow('Voice and model', 'Picked in the Phase 2 toolbar, and remembered here.', h('span.dim', v.eleven.voiceName || '— not picked yet —', ' · ', v.eleven.modelId))),
      section('Folder templates', null,
        h('div.help', { style: { margin: '10px 0' } }, 'New DBVO Patches get their folders from these templates, with ', h('b', '{mod}'), ' replaced by the mod name. Each patch can then change its own folders in Phase 2.'),
        srow('WAV + LIP folder', 'e.g. C:\\DBVO Dev\\Vampira - {mod}\\RawWav', pathField(d.wavLipFolder, (x) => { d.wavLipFolder = x; persistSoon(); }, { folder: true, title: 'WAV + LIP folder (then add {mod} where the mod name goes)' })),
        srow('Final mod folder', 'The mod\'s folder in your FOMOD, e.g. C:\\DBVO Vampira FOMOD\\Vamp - {mod}. The DBVO Fix plugin goes here.', pathField(d.finalRoot, (x) => { d.finalRoot = x; persistSoon(); }, { folder: true, title: 'Final mod folder (then add {mod})' })),
        srow('.fuz sub-folder', 'Under the final mod folder. {packId} is this voice pack\'s ID.', txt(d, 'fuzTemplate', { placeholder: 'Sound\\DBVO\\{packId}' }))),
      section('Tags toolbar', null, srow('Tags for this voice', 'Shown above the Phase 2 grid.', tagsBox)),
      section('Voice pack pronunciation', h('span.pill.gold', 'THIS VOICE'),
        h('div.help', { style: { margin: '10px 0' } }, 'Words this voice should say differently. The global DBVO dictionary (Settings → Pronunciation) applies to every voice pack; when both have the same word, ', h('b', 'this voice pack\'s entry wins'), '.'),
        pronTable(v.pronunciations, persistSoon)),
      section('Audio', null,
        srow('Tail trim', 'Removes the end-of-clip artefact before the LIP is made.', h('div.row', txt(v, 'trimTailMs', { type: 'number', width: '80px' }), h('span.dim', 'ms cut, then'), txt(v, 'fadeOutMs', { type: 'number', width: '80px' }), h('span.dim', 'ms fade-out'))),
        srow('Loudness normalization', 'EBU R128, −16 LUFS. Off keeps the original DBVO Voice Pack Maker level.', switchEl(v.loudnorm, (x) => { v.loudnorm = x; persistSoon(); })),
        srow('Gain', null, h('div.row', txt(v, 'gainDb', { type: 'number', width: '80px' }), h('span.dim', 'dB')))),
      section('Behaviour', null,
        srow('Alias parsing', 'DBVO 2.0 voice pack JSON setting. Rarely needed and buggy in game.', switchEl(v.aliasParsing, (x) => { v.aliasParsing = x; persistSoon(); })),
        srow('Lock ElevenLabs settings', 'Prevents accidental changes in the Phase 2 toolbar.', switchEl(v.settingsLocked, (x) => { v.settingsLocked = x; persistSoon(); })),
        packs.some((x) => x.id === v.id) ? srow('Delete', null, h('button.btn.danger.sm', { onclick: async () => {
          if (!(await confirmBox('Delete voice pack?', `Delete "${v.name}"? DBVO Patches using it stop working.`, 'Delete', true))) return;
          await rpc('voice.delete', { id: v.id });
          await refreshState();
          go('settings', { tab: 'voices' });
        } }, 'Delete voice pack')) : null),
    );
  }

  renderList();
  const initial = packs.find((v) => v.id === selectedId) || (selectedId === 'new' ? null : packs[0]);
  await select(initial || null);
  return wrap;
}

function pronTable(list, onChange) {
  const tbody = h('tbody');
  const render = () => {
    clear(tbody);
    list.forEach((p, i) => {
      const word = h('input', { type: 'text', value: p.word, placeholder: 'Alduin' });
      const rep = h('input', { type: 'text', value: p.replacement, placeholder: p.kind === 'phoneme' ? 'ˈæl.duː.ɪn' : 'Aldooin' });
      const kind = h('select', h('option', { value: 'alias', selected: p.kind === 'alias' }, 'Sounds like'), h('option', { value: 'phoneme', selected: p.kind === 'phoneme' }, 'IPA'));
      const upd = () => { Object.assign(p, { word: word.value, replacement: rep.value, kind: kind.value }); onChange(); };
      [word, rep].forEach((x) => x.addEventListener('input', upd));
      kind.addEventListener('change', upd);
      tbody.append(h('tr', h('td', word), h('td', { style: { width: '130px' } }, kind), h('td', rep),
        h('td', { style: { width: '110px' } }, switchEl(p.wholeWord, (x) => { p.wholeWord = x; onChange(); }, 'whole')),
        h('td', { style: { width: '100px' } }, switchEl(p.caseSensitive, (x) => { p.caseSensitive = x; onChange(); }, 'case')),
        h('td', { style: { width: '40px' } }, h('button.icon-btn', { title: 'Remove', onclick: () => { list.splice(i, 1); render(); onChange(); } }, '×'))));
    });
  };
  render();
  return h('div', { style: { padding: '6px 0 12px' } },
    h('table.tbl', h('thead', h('tr', h('th', 'Word in the line'), h('th', 'Kind'), h('th', 'How it\'s spoken'), h('th', ''), h('th', ''), h('th', ''))), tbody),
    h('div.row', { style: { marginTop: '8px' } },
      h('button.btn.sm', { onclick: () => { list.push({ word: '', kind: 'alias', replacement: '', wholeWord: true, caseSensitive: false }); render(); } }, '+ Add word'),
      h('span.faint', '"Sounds like" works with every model. IPA is sent as inline /…/ to Eleven v3/v4 and Flash v2. Never changes filenames. Longer words are applied first.')));
}

// ------------------------------------------------------------------ global pronunciation

async function pronunciation(_, rerender) {
  const list = structuredClone(app.state.settings.pronunciations || []);
  const savedEl = h('span.saved.ok', 'Saved ✓');
  let timer;
  const onChange = () => {
    savedEl.textContent = 'Saving…'; savedEl.className = 'saved';
    clearTimeout(timer);
    timer = setTimeout(async () => {
      try { await saveSettings({ pronunciations: list }); savedEl.textContent = 'Saved ✓'; savedEl.className = 'saved ok'; }
      catch (e) { savedEl.textContent = 'Not saved'; savedEl.className = 'saved bad'; fail(e); }
    }, 400);
  };
  return section('DBVO pronunciation dictionary', h('div.row', h('span.pill.blue', 'GLOBAL'), h('div.spacer'), savedEl),
    h('div.help', { style: { margin: '10px 0' } }, 'Used by every voice pack: Skyrim names and places once, for all voices. A voice pack can have its own dictionary (Settings → Voice packs); ', h('b', 'its entries win'), ' for the same word.'),
    pronTable(list, onChange));
}

// ------------------------------------------------------------------ references

async function references(_, rerender) {
  const st = app.state;
  const defaults = new Set(st.settings.defaultReferenceIds || []);
  return section('Omit lines users already have', null,
    h('div.help', { style: { margin: '10px 0' } },
      h('p', 'Topic .txt files of lines your users already have, such as the base game voice pack. In a DBVO Patch, lines found in a ticked reference are ', h('b', 'omitted'), ' so you never voice the same line twice.'),
      h('p', 'Only add packs every user installs. Never add optional mods: users pick different mods, so a line found only in an optional mod may be missing for them.')),
    st.references.length ? h('table.tbl', h('thead', h('tr', h('th', 'Name'), h('th', 'Lines'), h('th', 'Tick in new patches'), h('th', ''))),
      h('tbody', st.references.map((r) => h('tr',
        h('td', r.name), h('td.faint', r.lineCount),
        h('td', switchEl(defaults.has(r.id), async (on) => { on ? defaults.add(r.id) : defaults.delete(r.id); await saveSettings({ defaultReferenceIds: [...defaults] }); })),
        h('td', h('button.btn.sm.danger', { onclick: async () => { if (await confirmBox('Remove reference?', `Remove "${r.name}"?`, 'Remove', true)) { app.state = await rpc('references.remove', { id: r.id }); rerender(); } } }, 'Remove'))))))
      : h('div.faint', { style: { padding: '8px 0' } }, 'None yet.'),
    h('div', { style: { padding: '12px 0' } }, h('button.btn', { onclick: async () => {
      const file = await rpc('pick.file', { title: 'Choose a topic .txt', filter: 'Topic files|*.txt' });
      if (!file) return;
      const r = await prompt('Name this reference', [{ name: 'name', label: 'Name', value: 'Skyrim Base Game + DLC', help: 'Shown wherever you choose which lines to omit.' }]);
      if (!r || !r.name.trim()) return;
      try { app.state = await rpc('references.add', { name: r.name, path: file }); rerender(); } catch (e) { fail(e); }
    } }, '+ Add topic file')));
}

// ------------------------------------------------------------------ categories

async function categories(_, rerender) {
  const cats = [...app.state.settings.categories];
  const list = h('div.col');
  const save = async () => { app.state = await rpc('categories.save', { categories: cats }); rerender(); };
  cats.forEach((c, i) => {
    const inp = h('input', { type: 'text', value: c, style: { width: '320px' } });
    inp.addEventListener('change', () => { cats[i] = inp.value.trim(); save(); });
    list.append(h('div.row', inp,
      h('button.icon-btn', { title: 'Move up', disabled: i === 0, onclick: () => { [cats[i - 1], cats[i]] = [cats[i], cats[i - 1]]; save(); } }, '↑'),
      h('button.icon-btn', { title: 'Move down', disabled: i === cats.length - 1, onclick: () => { [cats[i + 1], cats[i]] = [cats[i], cats[i + 1]]; save(); } }, '↓'),
      h('button.icon-btn', { title: 'Remove', onclick: () => { cats.splice(i, 1); save(); } }, '×')));
  });
  return section('Mod categories', null,
    h('div.help', { style: { margin: '10px 0' } }, 'Used to sort the DBVO Patches list. You pick a mod\'s category in Phase 1, or drag a patch to another category. Categories never change folder paths.'),
    list,
    h('div', { style: { padding: '12px 0' } }, h('button.btn', { onclick: async () => { const r = await prompt('New category', [{ name: 'n', label: 'Name' }]); if (r && r.n.trim()) { cats.push(r.n.trim()); save(); } } }, '+ Add category')));
}

// ------------------------------------------------------------------ library

async function library(_, rerender) {
  const st = app.state;
  const keep = h('input', { type: 'number', value: st.settings.backupsToKeep, style: { width: '80px' } });
  keep.addEventListener('change', async () => { await saveSettings({ backupsToKeep: Number(keep.value) || 30 }); toast('Saved.'); });
  return h('div',
    section('Library', null,
      srow('Location', 'Holds your projects, takes and backups. A short path such as D:\\DBVO leaves the most room under Windows\' 260-character path limit.',
        h('div.row', h('span.mono', st.libraryRoot),
          h('button.btn.sm', { onclick: () => rpc('library.open') }, 'Open'),
          h('button.btn.sm', { onclick: async () => {
            const folder = await rpc('pick.folder', { title: 'New library location (an empty folder, or an existing DBVO Studio library)' });
            if (!folder) return;
            if (!(await confirmBox('Change library location?', `An empty folder gets a copy of your current library; a folder that already holds a library is used as it is. The app restarts afterwards.\n\n${folder}`, 'Change and restart'))) return;
            try { await rpc('library.change', { path: folder }); } catch (e) { fail(e); }
          } }, 'Change…'))),
      srow('Backups to keep', 'Made automatically before imports, generation and deletes.', h('div.row', keep,
        h('button.btn.sm', { onclick: async () => { const p = await rpc('backup.now', { reason: 'manual' }); toast('Backup: ' + p); } }, 'Back up now')))));
}
